using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Windows.Forms;

namespace OpcDaToUaGateway.Services
{
    /// <summary>
    /// 看门狗管理器 — 主进程侧的看门狗生命周期管理组件
    /// </summary>
    /// <remarks>
    /// <para><b>职责概述:</b></para>
    /// <para>
    /// 本类负责从主进程侧管理外部看门狗进程 (OpcDaToUaGateway.Watchdog.exe) 的完整生命周期，
    /// 包括启动、停止、心跳维持以及优雅退出协调。它是主进程与看门狗进程之间的桥梁。
    /// </para>
    ///
    /// <para><b>生命周期:</b></para>
    /// <para>
    /// 1. <see cref="Start"/> — 启动看门狗进程，创建心跳事件和定时器，开始周期性地向看门狗报告存活状态。
    ///    启动前会先清理残留的看门狗实例，确保不会有两个看门狗同时运行。
    ///    同时清除上次残留的优雅退出信号，使看门狗在新周期中能正确区分崩溃与正常退出。
    /// </para>
    /// <para>
    /// 2. 运行期 — 心跳定时器每 10 秒 Set 一次 HeartbeatEvent，
    ///    看门狗进程检测到后 Reset，形成"乒乓"协议。若看门狗在 30 秒内
    ///    未收到心跳，判定主进程挂起并强制重启。
    /// </para>
    /// <para>
    /// 3. <see cref="SignalGracefulExit"/> — 用户从托盘菜单主动退出时调用。
    ///    停止心跳定时器，Set 优雅退出事件，通知看门狗"主进程是正常退出，不要重启"。
    ///    看门狗自身保持运行，继续监控，等待下一次主进程被手动启动。
    /// </para>
    /// <para>
    /// 4. <see cref="Stop"/> — 完全停止看门狗进程。通过 Set StopEvent 让看门狗
    ///    退出监控循环，等待其自行退出，超时则强制 Kill。用于程序卸载、服务重启等场景。
    /// </para>
    ///
    /// <para><b>命名事件协议（主进程视角）:</b></para>
    /// <para>
    /// 主进程（本类）与看门狗进程通过以下命名事件协作：
    /// <list type="bullet">
    ///   <item>
    ///     <b>StopEvent</b> ("OpcDaToUaGateway_Watchdog_Stop"):
    ///     由本类 <see cref="Stop"/> 方法 Set，通知看门狗退出。
    ///   </item>
    ///   <item>
    ///     <b>HeartbeatEvent</b> ("OpcDaToUaGateway_Heartbeat"):
    ///     由本类的心跳定时器定期 Set（每 10 秒），看门狗检测并 Reset。
    ///   </item>
    ///   <item>
    ///     <b>GracefulExitEvent</b> ("OpcDaToUaGateway_GracefulExit"):
    ///     由 <see cref="SignalGracefulExit"/> Set，看门狗检测并 Reset（消费）。
    ///   </item>
    /// </list>
    /// </para>
    ///
    /// <para><b>线程安全:</b></para>
    /// <para>
    /// 所有公开方法内部使用 <c>_lock</c> 对象加锁，防止并发调用
    /// （如 UI 线程与定时器回调同时触发）导致内核句柄和定时器泄漏。
    /// </para>
    /// </remarks>
    public class WatchdogManager
    {
        // ─── 命名事件与进程名称常量 ─────────────────────────────────────────

        /// <summary>
        /// 停止事件名称 — <see cref="Stop"/> 方法通过 Set 此事件通知看门狗进程退出监控循环。
        /// 看门狗在每次循环迭代中检测此事件，收到信号后优雅退出。
        /// </summary>
        private const string StopEventName = "OpcDaToUaGateway_Watchdog_Stop";

        /// <summary>
        /// 看门狗进程名（不含 .exe 扩展名），用于按名称查找和清理残留实例。
        /// </summary>
        private const string ProcessName = "OpcDaToUaGateway.Watchdog";

        /// <summary>
        /// 心跳事件名称 — 主进程通过定时器每 <see cref="HeartbeatIntervalMs"/> 毫秒 Set 一次，
        /// 看门狗检测到后 Reset，形成"乒乓"协议。
        /// 看门狗若在超时阈值（30 秒 = 3 个心跳周期）内未收到信号，判定主进程挂起。
        /// </summary>
        private const string HeartbeatEventName = "OpcDaToUaGateway_Heartbeat";

        /// <summary>
        /// 优雅退出事件名称 — 当用户主动从托盘退出时，<see cref="SignalGracefulExit"/> 方法
        /// Set 此事件，告知看门狗"主进程是正常退出而非崩溃，请勿重启"。
        /// 使用 internal 可见性是因为主程序其他组件（如托盘退出逻辑）可能也需要引用此常量。
        /// </summary>
        internal const string ExitOkEventName = "OpcDaToUaGateway_GracefulExit";

        /// <summary>
        /// 心跳发送间隔 (10 秒)。
        /// 选择 10 秒是因为看门狗超时阈值为 30 秒（3 个周期），
        /// 10 秒间隔允许偶发一次心跳丢失（如 GC 暂停、系统繁忙）而不会触发误杀。
        /// </summary>
        private const int HeartbeatIntervalMs = 10000;

        // ─── 实例字段 ────────────────────────────────────────────────────

        /// <summary>看门狗进程对象，为 null 表示尚未启动或已停止。</summary>
        private Process _process;

        /// <summary>日志管理器引用，用于记录看门狗管理器的运行状态。</summary>
        private readonly LogManager _log;

        /// <summary>
        /// 并发保护锁 — 防止 Start/Stop/SignalGracefulExit 被多线程并发调用
        /// 导致内核句柄泄漏（如重复创建心跳事件）或定时器泄漏。
        /// </summary>
        private readonly object _lock = new object();

        /// <summary>心跳定时器 — 定期 Set 心跳事件，向看门狗报告主进程存活。</summary>
        private System.Threading.Timer _heartbeatTimer;

        /// <summary>
        /// 心跳事件的本地句柄 — 指向与看门狗共享的命名事件。
        /// 主进程 Set，看门狗 Reset。
        /// </summary>
        private EventWaitHandle _heartbeatEvent;

        /// <summary>
        /// 获取看门狗是否正在运行。
        /// 通过检查进程对象是否存在且未退出来判断。
        /// </summary>
        public bool IsRunning => _process != null && !_process.HasExited;

        /// <summary>
        /// 状态变化事件 — 看门狗状态发生变化时触发。
        /// 参数: (状态文本, 前景颜色)，供 UI 层直接绑定显示。
        /// </summary>
        public event Action<string, System.Drawing.Color> StatusChanged;

        /// <summary>
        /// 初始化看门狗管理器实例。
        /// </summary>
        /// <param name="log">日志管理器，用于记录看门狗相关操作日志。不可为 null。</param>
        /// <exception cref="ArgumentNullException"><paramref name="log"/> 为 null 时抛出。</exception>
        public WatchdogManager(LogManager log)
        {
            _log = log ?? throw new ArgumentNullException(nameof(log));
        }

        /// <summary>
        /// 启动看门狗进程，建立心跳通道，进入守护状态。
        /// </summary>
        /// <remarks>
        /// <para>执行流程:</para>
        /// <list type="number">
        ///   <item>检查是否已在运行，若是则直接返回（幂等保护）。</item>
        ///   <item>调用 <see cref="KillAll"/> 清理所有残留的看门狗进程实例。</item>
        ///   <item>创建并启动看门狗子进程，传入主程序 EXE 路径作为命令行参数。</item>
        ///   <item>创建命名心跳事件 (ManualReset 模式)，启动定时器每 10 秒 Set 一次。</item>
        ///   <item>清除上次残留的优雅退出信号，确保看门狗在新周期中能正确检测崩溃。</item>
        /// </list>
        /// <para>
        /// 整个方法在 <c>_lock</c> 保护下执行，防止并发调用导致句柄泄漏。
        /// 看门狗 EXE 必须与主程序在同一目录下，否则将记录错误并返回。
        /// </para>
        /// </remarks>
        public void Start()
        {
            lock (_lock)
            {
                // 幂等保护：若看门狗已在运行，直接返回，避免创建重复的进程和事件句柄
                if (IsRunning) return;

                try
                {
                    // 先清理残留实例（如上次异常退出遗留的看门狗进程），
                    // 保证系统中只有一个看门狗在运行。
                    KillAll();

                    // 看门狗 EXE 固定与主程序在同一部署目录下
                    string watchdogPath = Path.Combine(
                        AppDomain.CurrentDomain.BaseDirectory,
                        "OpcDaToUaGateway.Watchdog.exe");

                    if (!File.Exists(watchdogPath))
                    {
                        _log.Append("[守护] 看门狗程序不存在: " + watchdogPath);
                        StatusChanged?.Invoke("● 守护: 未找到", System.Drawing.Color.Gray);
                        return;
                    }

                    var startInfo = new ProcessStartInfo
                    {
                        FileName = watchdogPath,
                        // 将主程序自身的路径传给看门狗，使其知道要监控哪个进程
                        Arguments = $"\"{Application.ExecutablePath}\"",
                        WorkingDirectory = AppDomain.CurrentDomain.BaseDirectory,
                        UseShellExecute = true,
                        // 最小化窗口 — 看门狗以普通控制台程序运行，
                        // 不需要用户交互，最小化减少对桌面环境的干扰
                        WindowStyle = ProcessWindowStyle.Minimized
                    };

                    _process = Process.Start(startInfo);

                    // 创建心跳事件。选择 ManualReset 模式的原因：
                    // AutoReset 的 WaitOne 会自动消费信号，存在时序窗口导致信号丢失；
                    // ManualReset 下主进程 Set、看门狗手动 Reset，信号在被消费前一直可见。
                    try
                    {
                        _heartbeatEvent = new EventWaitHandle(false, EventResetMode.ManualReset, HeartbeatEventName);
                    }
                    catch (WaitHandleCannotBeOpenedException)
                    {
                        // 名称冲突（极端罕见，如操作系统级命名空间冲突），
                        // 不重试以避免无限循环，仅记录日志。心跳功能降级但看门狗仍可通过进程检测工作。
                        _heartbeatTimer?.Dispose();
                        _heartbeatTimer = null;
                        _log.Append("[守护] 心跳事件名称冲突，心跳监控不可用");
                    }

                    // 启动心跳定时器。
                    // dueTime=0 表示立即触发第一次心跳，period=HeartbeatIntervalMs 为后续间隔。
                    // 回调中的异常被吞掉，因为定时器回调崩溃会导致整个进程终止。
                    _heartbeatTimer = new System.Threading.Timer(_ =>
                    {
                        // 心跳异常记录到独立文件，确保主日志系统崩溃时也能留下痕迹
                        try { _heartbeatEvent?.Set(); }
                        catch (Exception ex)
                        {
                            try
                            {
                                string errPath = System.IO.Path.Combine(
                                    AppDomain.CurrentDomain.BaseDirectory, "watchdog_errors.log");
                                System.IO.File.AppendAllText(errPath,
                                    $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] 心跳 Set 失败: {ex.GetType().Name}: {ex.Message}\r\n");
                            }
                catch (Exception ex2) { _log.Append($"[看门狗] 注册事件失败: {ex2.Message}"); }
                        }
                    }, null, 0, HeartbeatIntervalMs);

                    // 清除上次残留的优雅退出信号。
                    // 如果上一次是用户主动退出（设了 GracefulExitEvent），而看门狗还活着，
                    // 新启动的主进程必须 Reset 该信号，否则下次主进程崩溃时
                    // 看门狗会误判为"优雅退出"而不去重启。
                    try
                    {
                        using (var exitOk = EventWaitHandle.OpenExisting(ExitOkEventName))
                        {
                            exitOk.Reset();
                        }
                    }
catch (Exception ex) { _log.Append($"[看门狗] 进程监控失败: {ex.Message}"); }

                    StatusChanged?.Invoke("● 守护: 运行中", System.Drawing.Color.Green);
                    _log.Append("[守护] 看门狗进程已启动");
                }
                catch (Exception ex)
                {
                    _log.Append($"[守护] 启动看门狗失败: {ex.Message}");
                    StatusChanged?.Invoke("● 守护: 启动失败", System.Drawing.Color.Red);
                }
            }
        }

        /// <summary>
        /// 停止看门狗进程，清理所有关联资源（心跳定时器、事件句柄）。
        /// </summary>
        /// <remarks>
        /// <para>执行流程:</para>
        /// <list type="number">
        ///   <item>停止心跳定时器并释放事件句柄（主进程即将关闭，不再发送心跳）。</item>
        ///   <item>Set StopEvent 通知看门狗退出监控循环（优雅方式）。</item>
        ///   <item>等待看门狗进程退出，最多 3 秒。</item>
        ///   <item>若看门狗仍在运行，调用 <see cref="KillAll"/> 强制终止（兜底保护）。</item>
        ///   <item>释放本地进程对象。</item>
        /// </list>
        /// <para>
        /// 整个方法在 <c>_lock</c> 保护下执行，防止与 <see cref="Start"/> 竞态。
        /// 此方法用于"完全关闭"场景（如程序卸载、服务重启）。
        /// 若仅需通知看门狗不重启主进程（但看门狗自身保持运行），请使用 <see cref="SignalGracefulExit"/>。
        /// </para>
        /// </remarks>
        public void Stop()
        {
            lock (_lock)
            {
                // 第一步：先停止心跳，因为主进程即将退出，继续发送心跳没有意义，
                // 且可能在事件句柄被释放后导致定时器回调异常。
try { _heartbeatTimer?.Dispose(); } catch (Exception ex) { _log.Append($"[看门狗] 释放心跳定时器失败: {ex.Message}"); }
                _heartbeatTimer = null;
try { _heartbeatEvent?.Dispose(); } catch (Exception ex) { _log.Append($"[看门狗] 释放心跳事件失败: {ex.Message}"); }
                _heartbeatEvent = null;

                // 第二步：通过命名事件通知看门狗退出（优雅方式）。
                // OpenExisting 可能失败（如看门狗从未启动过），静默忽略。
                try
                {
                    using (var stopEvent = System.Threading.EventWaitHandle.OpenExisting(StopEventName))
                    {
                        stopEvent.Set();
                    }
                }
catch (Exception ex) { _log.Append($"[看门狗] 启动看门狗失败: {ex.Message}"); }

                // 第三步：等待看门狗自行退出。
                // 看门狗检测到 StopEvent 后会退出循环，通常需要几十毫秒。
                // 3 秒的超时足够覆盖正常退出路径，避免无限阻塞调用方。
                if (_process != null)
                {
                    try
                    {
                        if (!_process.HasExited)
                            _process.WaitForExit(3000);
                    }
catch (Exception ex) { _log.Append($"[看门狗] 重启进程失败: {ex.Message}"); }
                }

                // 第四步：兜底 — 强制终止所有残留实例。
                // 如果优雅退出失败（如看门狗卡在某个 I/O 操作中），必须强制清理，
                // 否则下次 Start() 时会因互斥锁冲突而无法启动新实例。
                KillAll();

                _process?.Dispose();
                _process = null;

                StatusChanged?.Invoke("● 守护: 未启动", System.Drawing.Color.Gray);
                _log.Append("[守护] 看门狗进程已停止");
            }
        }

        /// <summary>
        /// 发送优雅退出信号 — 通知看门狗"主进程即将正常退出，请勿重启"。
        /// </summary>
        /// <remarks>
        /// <para><b>何时使用此方法（而非 <see cref="Stop"/>）:</b></para>
        /// <para>
        /// 当用户从托盘菜单选择"退出"时，主进程是正常关闭而非崩溃。
        /// 此时我们希望：
        /// <list type="bullet">
        ///   <item>看门狗知道这是正常退出 → 不重启主进程（通过 Set GracefulExitEvent 实现）</item>
        ///   <item>看门狗自身保持运行 → 继续监控，等待管理员下次手动启动主程序</item>
        /// </list>
        /// 而 <see cref="Stop"/> 是完全停止看门狗进程，适用于程序卸载或服务重启场景。
        /// </para>
        ///
        /// <para><b>执行逻辑:</b></para>
        /// <list type="number">
        ///   <item>
        ///     停止心跳定时器 — 主进程即将退出，不再发送心跳。
        ///     如果不停止，定时器回调可能在进程退出期间触发，访问已释放的事件句柄。
        ///   </item>
        ///   <item>
        ///     Set 优雅退出事件 (GracefulExitEvent) — 看门狗在下次检测到主进程不存活时，
        ///     会先检查此事件。发现有信号 = 正常退出，不执行重启。
        ///   </item>
        /// </list>
        ///
        /// <para><b>事件创建的兜底处理:</b></para>
        /// <para>
        /// 先尝试 OpenExisting 打开看门狗预创建的事件；若不存在（看门狗尚未启动），
        /// 则自行创建。这确保了即使在看门狗未运行的极端场景下，调用也不会失败。
        /// </para>
        /// </remarks>
        public void SignalGracefulExit()
        {
            // 先停止心跳。原因：主进程即将退出，继续发送心跳会让看门狗
            // 误以为主进程仍然健康，干扰退出后的状态判断。
try { _heartbeatTimer?.Dispose(); } catch (Exception ex) { _log.Append($"[看门狗] 停止时释放定时器失败: {ex.Message}"); }
            _heartbeatTimer = null;
try { _heartbeatEvent?.Dispose(); } catch (Exception ex) { _log.Append($"[看门狗] 停止时释放事件失败: {ex.Message}"); }
            _heartbeatEvent = null;

            // Set 优雅退出事件，让看门狗在主进程退出后不执行重启
            try
            {
                EventWaitHandle exitOk;
                try
                {
                    exitOk = EventWaitHandle.OpenExisting(ExitOkEventName);
                }
                catch (WaitHandleCannotBeOpenedException)
                {
                    // 看门狗可能尚未启动（未创建该事件），由主进程自行创建
                    exitOk = new EventWaitHandle(false, EventResetMode.ManualReset, ExitOkEventName);
                }
                exitOk.Set();
                exitOk.Dispose();
                _log.Append("[守护] 已发送优雅退出信号，看门狗将保持运行但不重启主进程");
            }
            catch (Exception ex)
            {
                _log.Append($"[守护] 发送优雅退出信号失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 按进程名查找并强制终止所有看门狗进程实例（排除自身 PID）。
        /// </summary>
        /// <remarks>
        /// 用于启动前清理残留实例和停止时的兜底保护。
        /// 排除当前进程 PID 是为了防止在某些异常命名场景下误杀自身。
        /// 每个进程的 Kill + WaitForExit 独立包裹在 try-catch 中，
        /// 确保一个进程的清理失败不影响其他进程。
        /// </remarks>
        private void KillAll()
        {
            try
            {
                var procs = Process.GetProcessesByName(ProcessName);
                int currentPid = Process.GetCurrentProcess().Id;

                foreach (var p in procs)
                {
                    try
                    {
                        // 排除当前进程（防御性检查，正常情况下当前进程不会是看门狗）
                        if (p.Id != currentPid && !p.HasExited)
                        {
                            p.Kill();
                            // 最多等待 3 秒让进程退出，超时则继续（进程可能在终止中）
                            p.WaitForExit(3000);
                        }
                    }
catch (Exception ex) { _log.Append($"[看门狗] Kill + WaitForExit 失败: {ex.Message}"); }
                    finally
                    {
                        // 无论成功失败都必须 Dispose，释放操作系统进程句柄
                        p.Dispose();
                    }
                }
            }
catch (Exception ex) { _log.Append($"[看门狗] 强制终止进程失败: {ex.Message}"); }
        }
    }
}
