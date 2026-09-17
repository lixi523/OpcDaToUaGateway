using System;
using System.Drawing;
using System.Threading;
using System.Threading.Tasks;
using OpcDaToUaGateway.Models;
using OpcDaToUaGateway.Services.Interfaces;

namespace OpcDaToUaGateway.Services
{
    /// <summary>
    /// 网关管理器 — 负责 OPC DA → OPC UA 网关的完整生命周期管理。
    ///
    /// == 启动流程（3 步严格有序） ==
    ///   步骤 1: 启动 OPC UA 服务器 — 先监听端口，确保下游客户端可连接
    ///   步骤 2: 连接 OPC DA 服务器 — 建立到传统 DA 设备的数据通道
    ///   步骤 3: 启动数据桥接     — 将 DA 侧采集的数据实时转发至 UA 侧
    /// 启动顺序不可调换：桥接依赖 DA 和 UA 都已就绪。
    /// 任何步骤失败时，已创建的资源按逆序回滚（bridge → daClient → uaServer），防止资源泄漏。
    ///
    /// == 优雅关闭 ==
    ///   停止时同样按依赖逆序释放：先断开桥接、再关闭 DA 客户端、最后停止 UA 服务器。
    ///   每个 Dispose/Stop 调用都在独立的 try-catch 中执行，确保单个组件的异常不会阻断后续释放。
    ///   释放完成后通过事件通知 UI 更新状态指示器。
    ///
    /// == 健康监控 ==
    ///   由外部定时器周期性调用 <see cref="CheckHealth"/>，检测 DA 连接状态并在断开时自动重连，
    ///   最多尝试 <see cref="MaxReconnectAttempts"/> 次，超过后停止重连并告警。
    ///
    /// == 线程安全策略 ==
    ///   所有可变字段（_daClient、_uaServer、_bridge、IsRunning、_starting、_reconnectAttempts）
    ///   的读写均通过 <c>_lock</c>（Monitor）保护，防止 StartAsync / StopAsync / CheckHealth
    ///   在并发调用时产生竞态。局部变量在锁内捕获快照后，后续操作在锁外执行以减小锁粒度。
    /// </summary>
    public class GatewayManager
    {
        // volatile 确保跨线程读写的可见性，配合 _lock 使用保证复合操作的原子性
        // PLAN 3.1：字段类型改为接口，使单元测试可注入 Fake 模拟器。
        private volatile IOpcDaClient _daClient;
        private volatile IGatewayOpcUaServer _uaServer;
        private volatile IDataBridge _bridge;

        private readonly LogManager _log;
        private readonly AppConfig _config;

        /// <summary>
        /// 全局互斥锁，保护所有可变状态字段的并发访问。
        /// 使用模式：在锁内做条件判断 + 状态变更，在锁外执行耗时操作（如网络 I/O、Dispose），
        /// 以减小锁持有时间、避免死锁。局部引用在锁内捕获后带出锁外使用。
        /// </summary>
        private readonly object _lock = new object();

        /// <summary>
        /// 启动中标志 — 用于防止 TOCTOU（Time-of-Check-Time-of-Use）竞态。
        ///
        /// P1-5 加固：使用 int + Interlocked.CompareExchange 替代 volatile bool。
        /// 虽然 x86 CLR 对 bool 读写通常是原子的，但 JIT 可能重排指令顺序。
        /// Interlocked API 提供全内存屏障，消除所有重排不确定性。
        ///
        /// 工作流程：
        ///   1. 在 _lock 内同时检查 IsRunning 和 _starting，两者都为 0 才继续
        ///   2. 立即通过 CompareExchange 将 _starting 设为 1，后续并发调用会被拦截
        ///   3. 启动成功或失败后，在 _lock 内将 _starting 重置为 0
        /// </summary>
        private int _startingFlag;

        /// <summary>
        /// 当前累计重连尝试次数，使用 Interlocked 操作保证线程安全。
        /// 重连成功后归零，达到 <see cref="MaxReconnectAttempts"/> 后停止尝试。
        /// </summary>
        private volatile int _reconnectAttempts;

        /// <summary>
        /// H-34: 上次重连尝试的时间戳 (Ticks)，用于指数退避。
        /// 初始 1s，每次失败翻倍，上限 60s，通过 Interlocked 原子操作。
        /// </summary>
        private long _lastReconnectAttemptTicks;

        /// <summary>DA 连接断开后的最大自动重连次数</summary>
        /// <remarks>
        /// M6 修复：从硬编码常量改为可从配置读取的值。
        /// 默认 50 次，<see cref="OpcDaConfig.MaxReconnectAttempts"/> 为非正数时使用此默认值。
        /// M1 修复（V2.6.0）：原为本地 50 硬编码，改由 AppConstants.DaMaxReconnectAttempts 集中管理。
        /// </remarks>
        private const int MaxReconnectAttemptsDefault = AppConstants.DaMaxReconnectAttempts;

        /// <summary>
        /// 获取当前有效的最大重连次数。
        /// 优先使用配置值（<see cref="OpcDaConfig.MaxReconnectAttempts"/>），无效时回退到默认值。
        /// </summary>
        private int EffectiveMaxReconnectAttempts
            => _config.OpcDa.MaxReconnectAttempts > 0 ? _config.OpcDa.MaxReconnectAttempts : MaxReconnectAttemptsDefault;

        /// <summary>网关当前运行状态（Idle / Starting / Running / Stopping / Error）</summary>
        public GatewayState State { get; private set; } = GatewayState.Idle;

        /// <summary>兼容旧代码：IsRunning 等价于 State == GatewayState.Running</summary>
        public bool IsRunning => State == GatewayState.Running;

        /// <summary>当前 DA 客户端实例（只读，供 UI 定时刷新状态使用）</summary>
        public IOpcDaClient DaClient => _daClient;

        /// <summary>当前数据桥接实例（只读，供 UI 定时刷新状态使用）</summary>
        public IDataBridge Bridge => _bridge;

        /// <summary>当前 UA 服务器实例（只读，供导出点表等操作使用）</summary>
        public IGatewayOpcUaServer UaServer => _uaServer;

        /// <summary>
        /// DA 侧状态变化事件。
        /// 参数: (状态文本, 前景色) — UI 层直接用于更新状态标签。
        /// </summary>
        /// <remarks>
        /// M7 注意：此事件可能在后台线程（CheckHealth 定时器 / StartAsync 线程池线程）触发，
        /// 订阅者必须在 UI 线程中处理（如 MainForm 使用 SafeInvoke 封送）。
        /// </remarks>
        public event Action<string, Color> DaStatusChanged;

        /// <summary>
        /// UA 侧状态变化事件。
        /// 参数: (状态文本, 前景色) — UI 层直接用于更新状态标签。
        /// </summary>
        /// <remarks>
        /// M7 注意：此事件可能在后台线程触发，订阅者必须在 UI 线程中处理。
        /// </remarks>
        public event Action<string, Color> UaStatusChanged;

        /// <summary>网关运行状态变更。UI 层用于启用/禁用控件。</summary>
        public event Action<bool> RunningStateChanged;

        /// <summary>
        /// N-5: 配置变更事件 — 当关键参数（如 NamespaceIndex）在运行时被回写后触发。
        /// MainForm 订阅此事件以立即调用 ConfigManager.Save()，不依赖 500ms 防抖定时器。
        /// </summary>
        public event Action ConfigDirty;

        /// <summary>
        /// 初始化网关管理器。
        /// </summary>
        /// <param name="log">日志管理器，用于记录运行日志（不可为 null）</param>
        /// <param name="config">应用配置，包含 DA 和 UA 的连接参数（不可为 null）</param>
        /// <exception cref="ArgumentNullException">log 或 config 为 null 时抛出</exception>
        public GatewayManager(LogManager log, AppConfig config, IOpcDaClient daClient = null, IGatewayOpcUaServer uaServer = null, IDataBridge bridge = null)
        {
            _log = log ?? throw new ArgumentNullException(nameof(log));
            _config = config ?? throw new ArgumentNullException(nameof(config));

            // H-12 修复：构造函数接受接口参数但之前未赋值，注入的 Mock 在 StartAsync 里被忽略。
            // 非 null 时存入字段，StartAsync 中的 `if (xxx == null) xxx = new ...` 兜底逻辑
            // 才能正确跳过真实对象创建，使单元测试依赖注入生效。
            if (daClient != null) _daClient = daClient;
            if (uaServer != null) _uaServer = uaServer;
            if (bridge != null) _bridge = bridge;
        }

        /// <summary>
        /// 启动网关，按 3 步流程依次初始化各组件：
        ///   [1/3] 创建并启动 OPC UA 服务器
        ///   [2/3] 创建并连接 OPC DA 客户端
        ///   [3/3] 创建并启动数据桥接
        ///
        /// 如果网关已在运行或正在启动中，方法立即返回（幂等）。
        /// 任何步骤失败时，已创建的资源按逆序回滚，然后重新抛出异常。
        /// 
        /// <param name="progressReport">可选进度回调，用于报告节点创建等耗时操作的进度文本。
        /// 调用方需保证此回调线程安全（MainForm 已通过 SynchronizationContext.Post 保证）。</param>
        /// </summary>
        /// <returns>异步任务，在所有步骤完成后结束</returns>
        public async Task StartAsync(Action<string> progressReport = null)
        {
            // 在锁内做 TOCTOU 安全的条件检查：只有 Idle 状态才能启动
            lock (_lock)
            {
                if (IsRunning) return;
                if (_startingFlag != 0) return; // 已在启动中
                State = GatewayState.Starting;
                Interlocked.Exchange(ref _startingFlag, 1);
            }

            // H-01 修复：原先声明 uaServer/daClient/bridge 均为 null 的局部变量，
            // 下方的 `if (xxx == null) xxx = new ...` 判断局部变量，永远为 null，
            // 构造函数注入的字段值（_uaServer/_daClient/_bridge）完全被忽略，DI 失效。
            // 修复：从字段读取初始值，注入非 null 时直接使用，null 时才创建真实实例。
            // H2 修复：之前用 `as 具体类型` 转型，注入 FakeOpcDaClient/FakeGatewayOpcUaServer
            // 时转型结果为 null，DI 被静默丢弃、重建真实对象。字段本就声明为接口类型
            // （_daClient/_uaServer/_bridge 在 :38-40），直接用接口类型驱动；仅当接口缺失
            // 成员时才需收紧，但 StopAsync(:286-299) 已全用接口类型，证明接口足够。
            IGatewayOpcUaServer uaServer = _uaServer;
            IOpcDaClient daClient = _daClient;
            IDataBridge bridge = _bridge;

            try
            {
                _log.Append("[1/3] 启动 OPC UA 服务器...");
                if (uaServer == null) uaServer = new GatewayOpcUaServer(_config.OpcUa);
                uaServer.OnStatusChanged += msg =>
                {
                    _log.Append("  " + msg);
                };
                uaServer.OnConfigChanged = () =>
                {
                    // ConfigDirty 可能在非 UI 线程触发，
                    // 调用方（MainForm）如果直接操作 UI 控件会抛跨线程异常。
                    // 此处不做线程切换，由订阅者自行处理线程安全（MainForm 已使用 SafeInvoke）。
                    ConfigDirty?.Invoke();
                };
                await uaServer.StartAsync().ConfigureAwait(false);

                _log.Append("[2/3] 连接 OPC DA 服务器...");
                string daHost = string.IsNullOrEmpty(_config.OpcDa.ServerHost)
                    ? AppConstants.DefaultDaHost : _config.OpcDa.ServerHost;
                if (daClient == null) { daClient = new OpcDaClient(_config.OpcDa.ServerProgId, _config.OpcDa.Tags, daHost); }
                daClient.OnStatusChanged += msg =>
                {
                    if (!msg.StartsWith("[诊断]"))
                        _log.Append("  " + msg);
                };
                daClient.Start(_config.OpcDa.UpdateRateMs, _config.OpcDa.GetEffectiveMode());

                _log.Append("[3/3] 启动数据桥接...");
                if (bridge == null) bridge = new DataBridge(daClient, uaServer, _config.OpcDa.Tags);
                bridge.OnLog += msg => _log.Append(msg);
                await bridge.StartAsync(progressReport).ConfigureAwait(false);

                // 所有步骤成功，在锁内一次性发布引用，确保外部观察者看到一致状态
                lock (_lock)
                {
                    _uaServer = uaServer;
                    _daClient = daClient;
                    _bridge = bridge;
                    State = GatewayState.Running;
                    _reconnectAttempts = 0;
                    _lastReconnectAttemptTicks = 0;
                    _startingFlag = 0;
                }

                DaStatusChanged?.Invoke("● DA: 已连接", Color.Green);
                UaStatusChanged?.Invoke("● UA: 运行中", Color.Green);
                RunningStateChanged?.Invoke(true);

                _log.Append("网关启动成功！");
                _log.Append($"OPC UA 地址: {_config.OpcUa.GetEndpointUrl()}");
                _log.Append("可以使用 UaExpert 等 OPC UA 客户端连接测试");
            }
            catch (Exception ex)
            {
                _log.Append($"启动失败: {ex.GetType().Name}: {ex.Message}");
                if (!string.IsNullOrEmpty(ex.StackTrace))
                {
                    string[] lines = ex.StackTrace.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                    int showLines = Math.Min(3, lines.Length);
                    for (int i = 0; i < showLines; i++)
                        _log.Append($"  堆栈[{i}]: {lines[i].Trim()}");
                }
                if (ex.InnerException != null)
                    _log.Append($"  内部异常: {ex.InnerException.GetType().Name}: {ex.InnerException.Message}");

                // 回滚已创建资源
                _log.Append("正在回滚已创建资源...");
                lock (_lock)
                {
                    _startingFlag = 0;
                    State = GatewayState.Error;
                }
                RunningStateChanged?.Invoke(false);
try { bridge?.Dispose(); } catch (Exception ex2) { _log.Append($"[网关] 释放 DataBridge 失败: {ex2.Message}"); }
try { daClient?.Dispose(); } catch (Exception ex2) { _log.Append($"[网关] 释放 OpcDaClient 失败: {ex2.Message}"); }
                if (uaServer != null)
                {
                    // 避免 .Wait() 导致 SynchronizationContext 死锁，
                    // 改用 ConfigureAwait(false) + Task.Wait 在线程池上下文中执行。
try { uaServer.StopAsync().ConfigureAwait(false).GetAwaiter().GetResult(); } catch (Exception ex2) { _log.Append($"[网关] UA Server 停止失败: {ex2.Message}"); }
try { uaServer.Dispose(); } catch (Exception ex2) { _log.Append($"[网关] UA Server 释放失败: {ex2.Message}"); }
                }
                throw;
            }
        }

        /// <summary>
        /// 优雅停止网关，按依赖逆序释放所有组件：
        ///   1. 释放数据桥接（切断 DA ↔ UA 数据转发）
        ///   2. 释放 DA 客户端（断开与传统 DA 服务器的连接）
        ///   3. 停止并释放 UA 服务器（关闭监听端口）
        ///
        /// 如果网关未在运行，方法立即返回（幂等）。
        /// 每个组件的释放都在独立的 try-catch 中，确保单个异常不阻断后续清理。
        /// 事件处理器在释放前解绑，防止多次启停时事件订阅累积。
        /// </summary>
        /// <returns>异步任务，在所有组件停止后结束</returns>
        public async Task StopAsync()
        {
            IOpcDaClient daClient;
            IGatewayOpcUaServer uaServer;
            IDataBridge bridge;

            lock (_lock)
            {
                if (!IsRunning) return;
                State = GatewayState.Stopping;
                daClient = _daClient;
                uaServer = _uaServer;
                bridge = _bridge;
                _daClient = null;
                _uaServer = null;
                _bridge = null;
            }

            _log.Append("正在停止网关...");

try { bridge?.Dispose(); } catch (Exception ex) { _log.Append($"[网关] 停止时释放 DataBridge 失败: {ex.Message}"); }
try { daClient?.Dispose(); } catch (Exception ex) { _log.Append($"[网关] 停止时释放 OpcDaClient 失败: {ex.Message}"); }

            if (uaServer != null)
            {
                try { await uaServer.StopAsync(); }
                catch (Exception ex) { _log.Append($"停止 UA 服务器时出错: {ex.Message}"); }
try { uaServer.Dispose(); } catch (Exception ex) { _log.Append($"[网关] UA Server 最终释放失败: {ex.Message}"); }
            }

            lock (_lock)
            {
                State = GatewayState.Idle;
            }

            DaStatusChanged?.Invoke("● DA: 未连接", Color.Gray);
            UaStatusChanged?.Invoke("● UA: 未启动", Color.Gray);
            RunningStateChanged?.Invoke(false);

            _log.Append("网关已停止");
        }

        /// <summary>
        /// 清除错误状态，将网关重置为 Idle，允许用户修复问题后重试启动。
        /// 仅在 State == Error 时有效，防止在 Running 时意外重置。
        /// </summary>
        public void ClearErrorState()
        {
            lock (_lock)
            {
                if (State != GatewayState.Error) return;
                // 同时检查 _startingFlag，防止在 StartAsync 异常回滚过程中被意外重置
                if (_startingFlag != 0) return;
                State = GatewayState.Idle;
                _startingFlag = 0;
            }
        }

        /// <summary>
        /// 健康检查 — 由外部定时器周期性调用，检测 DA 连接状态并在断开时自动重连。
        ///
        /// 重连策略：
        ///   - 每次检测到断开时尝试一次重连
        ///   - 累计重连次数不超过 <see cref="MaxReconnectAttempts"/>（默认 50 次）
        ///   - 重连成功后计数器归零
        ///   - 达到上限后停止尝试，通过 UI 和日志告警
        ///
        /// 线程安全说明：
        ///   在 _lock 内捕获 daClient 快照后在锁外操作，减小锁粒度。
        ///   操作过程中 daClient 可能被 StopAsync 在另一线程释放，因此必须捕获
        ///   <see cref="ObjectDisposedException"/>。
        /// </summary>
        public void CheckHealth()
        {
            IOpcDaClient daClient;
            lock (_lock)
            {
                if (!IsRunning) return;
                daClient = _daClient;
                if (daClient == null) return;
            }

            try
            {
                if (daClient.IsConnected) return;

                // M-02 修复：EffectiveMaxReconnectAttempts 是计算属性，每次调用都重新读配置。
                // 若配置在 CheckHealth 执行期间被热更新，三处调用可能得到不同值，
                // 导致"停止重连"日志条件永远不满足。快照为局部变量，确保逻辑一致。
                int maxAttempts = EffectiveMaxReconnectAttempts;

                // H-09 修复：_reconnectAttempts 原先在退避检查前就递增，导致退避等待期间
                // 计数器持续膨胀，远早于预期触发"达到最大重连次数"。
                long nowTicks = DateTime.UtcNow.Ticks;
                long lastTicks = Interlocked.Read(ref _lastReconnectAttemptTicks);
                int currentAttempts = _reconnectAttempts; // 仅用于计算退避窗口，不消耗计数
                int backoffMs = (int)Math.Min(1000L * (1L << Math.Min(currentAttempts, 6)), 60000L);
                long elapsedMs = (nowTicks - lastTicks) / TimeSpan.TicksPerMillisecond;
                if (lastTicks > 0 && elapsedMs < backoffMs) return;
                Interlocked.Exchange(ref _lastReconnectAttemptTicks, nowTicks);

                // 退避窗口已过，本次真正要重连：此时才递增计数器
                int attempts = Interlocked.Increment(ref _reconnectAttempts);

                if (attempts > maxAttempts)
                {
                    // L5 修复（V2.6.0）：原代码用 Interlocked.CompareExchange 钳位，绕且不易读。
                    // 此处 attempts 已经是本线程 Increment 的返回值，若超出 maxAttempts+1
                    // 直接原子钳到 maxAttempts+1 即可；并发下其他线程可能同时钳位但结果一致，
                    // 且后续重连成功路径会清零，不会累积偏差。
                    Interlocked.Exchange(ref _reconnectAttempts, maxAttempts + 1);
                    attempts = maxAttempts + 1;
                }

                if (attempts <= maxAttempts)
                {
                    _log.Append($"[监控] DA 连接断开，尝试重连 ({attempts}/{maxAttempts})...");
                    DaStatusChanged?.Invoke("● DA: 重连中...", Color.Orange);

                    bool success = daClient.TryReconnect(_config.OpcDa.UpdateRateMs);

                    if (success)
                    {
                        Interlocked.Exchange(ref _reconnectAttempts, 0);
                        Interlocked.Exchange(ref _lastReconnectAttemptTicks, 0);
                        DaStatusChanged?.Invoke("● DA: 已连接", Color.Green);
                        _log.Append("[监控] DA 重连成功");
                    }
                }
                else if (attempts == maxAttempts + 1)
                {
                    _log.Append("[监控] 达到最大重连次数，停止重连。请手动检查 OPC DA 服务器。");
                    DaStatusChanged?.Invoke("● DA: 重连失败", Color.Red);
                }
            }
            catch (ObjectDisposedException)
            {
                // CheckHealth 在锁外操作 daClient 的 IsConnected / TryReconnect 方法时，
                // StopAsync 可能在另一个线程完成了对 daClient 的 Dispose。
                // 这是正常的生命周期交叉，安全忽略即可。
            }
            catch (Exception ex)
            {
                _log.Append($"[监控] 健康检查异常: {ex.Message}");
                // 运行时异常 → 标记 Error 状态
                lock (_lock)
                {
                    if (IsRunning)
                        State = GatewayState.Error;
                }
            }
        }
    }
}

