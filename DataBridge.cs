using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Opc.Ua;
using OpcDaToUaGateway.Models;
using OpcDaToUaGateway.Services.Interfaces;

namespace OpcDaToUaGateway
{
    /// <summary>
    /// OPC DA 到 OPC UA 的数据桥接器，实现桥接（Bridge）设计模式。
    /// 
    /// <para>核心职责：将 OPC DA 客户端的数据变化事件实时转发到 OPC UA 服务器，
    /// 使两个异构 OPC 协议之间实现透明的数据流通。</para>
    /// 
    /// <para>数据流向：
    /// OPC DA 服务器 → DA 客户端回调（线程池线程） → 类型转换 → UA 变量节点更新 + 快照缓存</para>
    /// 
    /// <para>线程安全模型：
    /// - DA 回调（<see cref="OnDaDataChanged"/>）在线程池线程上执行，可能多个回调并发触发；
    /// - UI 定时读取（<see cref="GetSnapshots"/>）在 WinForms 定时器线程上执行；
    /// - 所有共享状态均通过 <see cref="ConcurrentDictionary{TKey,TValue}"/>、
    ///   <see cref="Interlocked"/> 和 volatile 保证安全，无需显式加锁。</para>
    /// 
    /// <para>所有权语义：
    /// DataBridge 不拥有 <paramref name="daClient"/> 和 <paramref name="uaServer"/> 的生命周期，
    /// 它们由外部（MainForm）创建和销毁。DataBridge 仅持有引用用于数据转发，
    /// Dispose 时只取消事件订阅，不释放 DA 客户端或 UA 服务器。</para>
    /// </summary>
    public class DataBridge : IDataBridge
    {
        // DA 客户端引用（所有权归 MainForm，此处仅用于订阅事件和读取数据）
        private readonly IOpcDaClient _daClient;
        // UA 服务器引用（所有权归 MainForm，此处仅用于更新变量节点）
        private readonly IGatewayOpcUaServer _uaServer;

        // 按 TagKey 索引的标签配置映射，用于在数据变化时快速查找标签元数据
        private readonly ConcurrentDictionary<string, TagConfig> _tagMap;
        // TagKey 的有序列表（构造时确定，保证 GetSnapshots 返回顺序与 UI 表格行顺序一致）
        private readonly List<string> _orderedKeys;

        // 缓存每个标签的 BuiltInType（避免每次数据变化时对 DataType 字符串做重复解析）
        private readonly ConcurrentDictionary<string, BuiltInType> _cachedTypes = new ConcurrentDictionary<string, BuiltInType>();

        // 运行时统计计数器，使用 Interlocked 保证跨线程原子性
        // 使用 long 防溢出。35K 标签每秒更新时，int 约 17 小时溢出。
        private long _totalUpdates;
        private int _errorCount;
        // 最后更新时间的 Ticks 值（long），通过 Interlocked.Exchange 原子写入
        private long _lastUpdateTicks;
        // 使用 Interlocked.Exchange 原子操作替代 volatile bool 的 check-then-set。
        // 原因：volatile bool + if (_disposed) return; _disposed = true; 不是原子操作，
        // 两个并发 Dispose() 可能同时通过检查。Interlocked.Exchange 保证只有一个线程拿到旧值 0。
        // 与 OpcDaClient._disposedInt 和 GatewayOpcUaServer._disposedInt 保持一致。
        private int _disposedInt;

        // 预编译类型转换委托缓存，避免每次数据回调时 switch-case 分支预测开销。
        // 使用静态数组而非字典——BuiltInType 枚举值范围小（0~25），数组索引 O(1) 无哈希冲突。
        // 每个委托内联快速路径类型检查（is pattern），匹配时零分配返回，不匹配时调用 Convert.To*。
        private static readonly Func<object, object>[] TypeConverters;

        /// <summary>
        /// 静态构造器一次性填充所有类型转换委托。
        /// 线程安全保证：CLR 保证静态构造器在类型首次使用前执行且仅执行一次。
        /// 35K 标签回调场景下，避免每次回调都经过 switch-case 分支预测开销。
        /// </summary>
        static DataBridge()
        {
            TypeConverters = new Func<object, object>[32]; // BuiltInType 枚举值范围足够

            TypeConverters[(int)BuiltInType.Boolean]  = v =>
            {
                if (v is bool b) return b;
                if (v is string s)
                {
                    if (string.Equals(s, "true", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(s, "1", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(s, "yes", StringComparison.OrdinalIgnoreCase)) return true;
                    if (string.Equals(s, "false", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(s, "0", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(s, "no", StringComparison.OrdinalIgnoreCase)) return false;
                }
                try { return Convert.ToBoolean(v); }
                catch { return v; }
            };
            TypeConverters[(int)BuiltInType.SByte]    = v => v is sbyte sb ? sb : Convert.ToSByte(v);
            TypeConverters[(int)BuiltInType.Byte]     = v => v is byte by ? by : Convert.ToByte(v);
            TypeConverters[(int)BuiltInType.Int16]    = v => v is short s ? s : Convert.ToInt16(v);
            TypeConverters[(int)BuiltInType.Int32]    = v => v is int i ? i : Convert.ToInt32(v);
            TypeConverters[(int)BuiltInType.Int64]    = v => v is long l ? l : Convert.ToInt64(v);
            TypeConverters[(int)BuiltInType.UInt16]   = v => v is ushort us ? us : Convert.ToUInt16(v);
            TypeConverters[(int)BuiltInType.UInt32]   = v => v is uint ui ? ui : Convert.ToUInt32(v);
            TypeConverters[(int)BuiltInType.UInt64]   = v => v is ulong ul ? ul : Convert.ToUInt64(v);
            TypeConverters[(int)BuiltInType.Float]    = v => v is float f ? f : Convert.ToSingle(v);
            TypeConverters[(int)BuiltInType.Double]   = v => v is double d ? d : Convert.ToDouble(v);
            TypeConverters[(int)BuiltInType.String]   = v => Convert.ToString(v);
            TypeConverters[(int)BuiltInType.DateTime] = v => v is DateTime dt ? dt : Convert.ToDateTime(v);
        }

        /// <summary>获取累计成功更新次数（线程安全读取）。</summary>
        public long TotalUpdates => Volatile.Read(ref _totalUpdates);

        /// <summary>获取累计错误次数（线程安全读取）。</summary>
        public int ErrorCount => Volatile.Read(ref _errorCount);

        /// <summary>获取最后一次成功更新的时间（线程安全读取）。</summary>
        public DateTime LastUpdateTime => new DateTime(Volatile.Read(ref _lastUpdateTicks), DateTimeKind.Local);

        /// <summary>
        /// 当前值缓存，采用不可变快照（Immutable Snapshot）模式：
        /// 每次数据变化时构造一个全新的 <see cref="TagSnapshot"/> 对象替换旧对象，
        /// 而非原地修改属性。这样读取端（UI）获取到的快照始终是一致的完整状态，
        /// 无需加锁即可避免读到半更新的中间状态。
        /// </summary>
        private readonly ConcurrentDictionary<string, TagSnapshot> _snapshots
            = new ConcurrentDictionary<string, TagSnapshot>();

        /// <summary>
        /// 日志事件，由 MainForm 订阅后输出到界面日志区域。
        /// </summary>
        public event Action<string> OnLog;

        /// <summary>
        /// 初始化数据桥接器，建立标签映射和初始快照。
        /// </summary>
        /// <param name="daClient">OPC DA 客户端实例（由调用方管理生命周期）。</param>
        /// <param name="uaServer">OPC UA 网关服务器实例（由调用方管理生命周期）。</param>
        /// <param name="tags">要桥接的标签配置列表，顺序决定了 UI 显示顺序。</param>
        public DataBridge(IOpcDaClient daClient, IGatewayOpcUaServer uaServer, List<TagConfig> tags)
        {
            _daClient = daClient;
            _uaServer = uaServer;
            _tagMap = new ConcurrentDictionary<string, TagConfig>();
            _orderedKeys = new List<string>(tags.Count);

            foreach (var tag in tags)
            {
                _tagMap[tag.TagKey] = tag;
                _orderedKeys.Add(tag.TagKey);
                _cachedTypes[tag.TagKey] = GatewayOpcUaServer.ParseDataType(tag.DataType);
                _snapshots[tag.TagKey] = new TagSnapshot(
                    tag.ItemId, tag.DisplayName, "-", "Unknown", DateTime.MinValue);
            }
        }

        /// <summary>
        /// 启动桥接：在 OPC UA 服务器中注册所有变量节点，并订阅 DA 数据变化事件。
        /// 
        /// <para>V1.8.1 优化：此方法为同步版本，适用于测试/快速场景。
        /// 生产环境推荐使用 <see cref="StartAsync"/>，它将节点创建移至后台线程，
        /// 避免 3.5 万节点在 UI 线程同步创建导致窗口"未响应"。</para>
        /// </summary>
        public void Start()
        {
            int tagCount = _tagMap.Count;
            Log($"正在创建 OPC UA 变量节点... (共 {tagCount} 个标签)");

            if (tagCount == 0)
            {
                Log("[警告] 标签列表为空，没有可创建的 UA 变量节点。请检查 config.json 中的 Tags 配置。");
            }

            int addedCount = 0;
            int failedCount = 0;
            var failedTags = new List<string>();
            int total = _orderedKeys.Count;
            int progressStep = Math.Max(1, total / 20);
            int i = 0;
            foreach (string key in _orderedKeys)
            {
                if (!_tagMap.TryGetValue(key, out TagConfig tag)) continue;
                _cachedTypes.TryGetValue(tag.TagKey, out BuiltInType builtInType);

                try
                {
                    _uaServer.AddVariableNode(tag.TagKey, tag.ItemId, tag.DisplayName, builtInType, tag.UaNodeId);
                    addedCount++;
                }
                catch (Exception ex)
                {
                    failedCount++;
                    if (failedTags.Count < 10)
                        failedTags.Add($"{tag.DisplayName} ({tag.TagKey}): {ex.Message}");
                }

                i++;
                if (i % progressStep == 0 || i == total)
                    Log($"  已创建 UA 变量节点: {addedCount}/{total}");
            }

            Log($"已创建 {addedCount}/{tagCount} 个 UA 变量节点");
            if (failedCount > 0)
            {
                Log($"[警告] {failedCount} 个节点创建失败:");
                foreach (string detail in failedTags)
                    Log($"  - {detail}");
                if (failedCount > 10)
                    Log($"  ...及其他 {failedCount - 10} 个失败");
            }

            int actualVarCount = _uaServer.VariableCount;
            ushort nsIndex = _uaServer.NamespaceIndex;
            Log($"  命名空间索引: {nsIndex}, 实际变量数: {actualVarCount}");

            // L1 修复：原代码中 `-= OnDaDataChanged` 连写两遍（合并残留）。
            // C-01 修复：订阅前先 -=，保证 Start() 重复调用时不累积重复订阅。
            _daClient.OnDataChanged -= OnDaDataChanged;
            _daClient.OnDataChanged += OnDaDataChanged;
            Log("数据桥接已启动，等待数据...");
        }

        /// <summary>
        /// 异步启动桥接：将变量节点创建移至后台线程，避免阻塞 UI 线程。
        ///
        /// <para>V1.8.1 新增：针对 3.5 万+ 节点场景，在后台线程执行 AddVariableNode 循环，
        /// 通过 progressReport 回调报告进度。节点创建完成后订阅 DA 事件。
        /// 此方法确保 UI 线程在启动过程中始终保持响应。</para>
        ///
        /// <para>V1.9.0 修复：快照 _uaServer 引用，防止 Stop() 在创建过程中将其置 null。</para>
        ///
        /// <para>V2.5.0 修复：订阅前先 -=，保证 Start()/StartAsync() 重复调用时
        /// 不会累积重复订阅，避免单次 DA 回调触发 N 次数据投递。</para>
        ///
        /// <param name="progressReport">进度回调，报告节点创建进度文本。可为 null。</param>
        /// </summary>
        public async Task StartAsync(Action<string> progressReport = null)
        {
            int tagCount = _tagMap.Count;
            string logPrefix = $"[{DateTime.Now:HH:mm:ss}]";

            if (tagCount == 0)
            {
                Log("[警告] 标签列表为空，没有可创建的 UA 变量节点。请检查 config.json 中的 Tags 配置。");
                // L2 修复（V2.6.0）：与 Start() 行为统一，空标签时也补 UA 节点统计日志，
                // 避免 StartAsync 路径跳过统计导致运维无法区分"无标签"与"节点创建失败"。
                // C-03 修复：uaServer 为 null（Stop() 并发置空）时禁止 NRE。
                Log($"  命名空间索引: {_uaServer?.NamespaceIndex ?? 0}, 实际变量数: {_uaServer?.VariableCount ?? 0}");
                _daClient.OnDataChanged -= OnDaDataChanged;
                _daClient.OnDataChanged += OnDaDataChanged;
                Log("数据桥接已启动，等待数据...");
                return;
            }

            progressReport?.Invoke($"{logPrefix} 正在创建 OPC UA 变量节点... (共 {tagCount} 个标签)");

            int total = _orderedKeys.Count;
            int progressStep = Math.Max(1, total / 20);

            // V1.9.0 修复：快照 _uaServer 引用，防止 Stop() 在创建过程中将其置 null
            var uaServer = _uaServer;
            var createResult = await Task.Run(() =>
            {
                int i = 0;
                int localAdded = 0;
                int localFailed = 0;
                var localFailedTags = new List<string>();

                foreach (string key in _orderedKeys)
                {
                    if (uaServer == null) break;
                    if (!_tagMap.TryGetValue(key, out TagConfig tag)) continue;
                    _cachedTypes.TryGetValue(tag.TagKey, out BuiltInType builtInType);

                    try
                    {
                        uaServer.AddVariableNode(tag.TagKey, tag.ItemId, tag.DisplayName, builtInType, tag.UaNodeId);
                        localAdded++;
                    }
                    catch (Exception ex)
                    {
                        localFailed++;
                        lock (localFailedTags)
                        {
                            if (localFailedTags.Count < 10)
                                localFailedTags.Add($"{tag.DisplayName} ({tag.TagKey}): {ex.Message}");
                        }
                    }

                    i++;
                    if (i % progressStep == 0 || i == total)
                    {
                        int pct = (int)((double)i / total * 100);
                        progressReport?.Invoke($"{logPrefix} 已创建 UA 变量节点: {localAdded}/{total} ({pct}%)");
                    }
                }

                return (Added: localAdded, Failed: localFailed, FailedTags: localFailedTags);
            });

            int addedCount = createResult.Added;
            int failedCount = createResult.Failed;
            var failedTags = createResult.FailedTags;

            Log($"已创建 {addedCount}/{tagCount} 个 UA 变量节点");
            if (failedCount > 0)
            {
                Log($"[警告] {failedCount} 个节点创建失败:");
                foreach (string detail in failedTags)
                    Log($"  - {detail}");
                if (failedCount > 10)
                    Log($"  ...及其他 {failedCount - 10} 个失败");
            }

            // C-03 修复：uaServer 为 null（Stop() 并发置空）时禁止 NRE，
            // 使用空合并运算符安全取值。
            int actualVarCount = uaServer?.VariableCount ?? 0;
            ushort nsIndex = uaServer?.NamespaceIndex ?? 0;
            Log($"  命名空间索引: {nsIndex}, 实际变量数: {actualVarCount}");

            // H5 修复：与 Start() 及 V2.5.0 注释一致，订阅前先 -= 防重复订阅。
            // 生产路径走 StartAsync，原先只有 +=，异常回滚后重试会累积订阅，
            // 单次数据变化触发 N 次投递。
            _daClient.OnDataChanged -= OnDaDataChanged;
            _daClient.OnDataChanged += OnDaDataChanged;
            progressReport?.Invoke($"{logPrefix} 数据桥接已启动，等待数据...");
        }

        /// <summary>
        /// 停止桥接并释放资源：取消 DA 数据变化事件订阅。
        /// <para>P2 修复：设置 <see cref="_disposed"/> 标志，确保 Dispose 后
        /// 仍在排队的线程池回调不会继续处理数据或更新 UA 节点。</para>
        /// <para>注意：此方法不释放 <see cref="_daClient"/> 和 <see cref="_uaServer"/>，
        /// 因为它们的生命周期由外部所有者（MainForm）管理。</para>
        /// </summary>
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposedInt, 1) == 1) return;
            _daClient.OnDataChanged -= OnDaDataChanged;
        }

        /// <summary>
        /// OPC DA 数据变化回调，是桥接模式的核心转发逻辑。
        /// </summary>
        private void OnDaDataChanged(string tagKey, object value, bool isGood, DateTime timestamp)
        {
            if (Volatile.Read(ref _disposedInt) == 1) return;

            try
            {
                object convertedValue = ConvertValue(tagKey, value);
                DateTime recvUtc = DateTime.UtcNow;

                _uaServer.UpdateValue(tagKey, convertedValue, isGood, recvUtc);

                _snapshots[tagKey] = new TagSnapshot(
                    _tagMap.TryGetValue(tagKey, out var tc) ? tc.ItemId : tagKey,
                    _tagMap.TryGetValue(tagKey, out var dn) ? dn.DisplayName ?? dn.ItemId : tagKey,
                    convertedValue?.ToString() ?? "null",
                    isGood ? "Good" : "Bad",
                    recvUtc);

                Interlocked.Increment(ref _totalUpdates);
                Interlocked.Exchange(ref _lastUpdateTicks, DateTime.Now.Ticks);
            }
            catch (Exception ex)
            {
                Interlocked.Increment(ref _errorCount);
                Log($"[错误] 更新 {tagKey}: {ex.Message}");
            }
        }

        /// <summary>
        /// 获取当前所有标签的数据快照，供 UI 定时刷新显示。
        /// </summary>
        public IReadOnlyList<TagSnapshot> GetSnapshots()
        {
            var result = new List<TagSnapshot>(_orderedKeys.Count);
            foreach (string key in _orderedKeys)
            {
                if (_snapshots.TryGetValue(key, out var snap))
                    result.Add(snap);
            }
            return result;
        }

        /// <summary>
        /// 使用预编译委托缓存替代 switch-case，O(1) 数组索引无分支预测开销。
        /// 增加 DBNull、COM decimal、未知类型等边缘情况的兼容处理。
        /// </summary>
        private object ConvertValue(string tagKey, object value)
        {
            if (value == null || value is DBNull) return null;

            if (!_cachedTypes.TryGetValue(tagKey, out BuiltInType targetType))
                return value;

            try
            {
                int typeIndex = (int)targetType;
                if (typeIndex >= 0 && typeIndex < TypeConverters.Length)
                {
                    var converter = TypeConverters[typeIndex];
                    if (converter != null)
                        return converter(value);
                }
                return value;
            }
            catch (Exception ex)
            {
                Log($"标签 [{tagKey}] 类型转换失败 ({targetType}): {ex.Message}，使用原始值");
                return value;
            }
        }

        /// <summary>
        /// 输出带时间戳的日志消息到 <see cref="OnLog"/> 事件订阅者。
        /// </summary>
        private void Log(string message)
        {
            OnLog?.Invoke($"[{DateTime.Now:HH:mm:ss}] {message}");
        }
    }

    /// <summary>
    /// 标签数据快照，采用不可变对象（Immutable Object）模式。
    /// </summary>
    public class TagSnapshot
    {
        public string ItemId { get; }
        public string DisplayName { get; }
        public string Value { get; }
        public string Quality { get; }
        public DateTime Timestamp { get; }

        public TagSnapshot(string itemId, string displayName, string value, string quality, DateTime timestamp)
        {
            ItemId = itemId;
            DisplayName = displayName;
            Value = value;
            Quality = quality;
            Timestamp = timestamp;
        }
    }
}


