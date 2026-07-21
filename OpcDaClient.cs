using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using TitaniumAS.Opc.Client;
using TitaniumAS.Opc.Client.Common;
using TitaniumAS.Opc.Client.Da;
using TitaniumAS.Opc.Client.Da.Browsing;
using OpcDaToUaGateway.Models;
using OpcDaToUaGateway.Services.Interfaces;

namespace OpcDaToUaGateway
{
    /// <summary>
    /// OPC DA 客户端封装，负责与 OPC DA 服务器建立 COM 连接、创建订阅并接收异步数据回调。
    /// 
    /// H-26（2026-06-23）：从 Technosoftware DaAeHdaClient（商业库，30 天试用到期）
    /// 迁移至 TitaniumAS.Opc.Client（MIT 开源免费库）。
    /// 
    /// API 映射对照表：
    /// ┌───────────────────────────────┬─────────────────────────────────┐
    /// │ Technosoftware (旧)           │ TitaniumAS (新)                 │
    /// ├───────────────────────────────┼─────────────────────────────────┤
    /// │ new Factory()                 │ 不需要，Bootstrap.Initialize()  │
    /// │ new OpcUrl(urlStr)            │ UrlBuilder.Build(progId, host)  │
    /// │ new TsCDaServer(factory,url)  │ new OpcDaServer(uri)            │
    /// │ server.Connect(url, null)     │ server.Connect()                │
    /// │ TsCDaSubscriptionState        │ group.UpdateRate / .IsActive    │
    /// │ server.CreateSubscription()   │ server.AddGroup(name)           │
    /// │ subscription.AddItems(...)    │ group.AddItems(definitions)     │
    /// │ subscription.DataChangedEvent │ group.ValuesChanged             │
    /// │ subscription.Read(items)      │ group.Read(items, source)       │
    /// │ server.Browse(id,filters,pos) │ browser.GetElements(itemId)     │
    /// │ server.Disconnect / Dispose   │ server.Dispose()                │
    /// │ factory.Dispose()             │ 不需要                          │
    /// └───────────────────────────────┴─────────────────────────────────┘
    /// 
    /// 关键差异：
    /// 1. 不再需要 Factory 概念；OpcDaServer 直接构造连接。
    /// 2. 订阅生命周期：OpcDaGroup 由 server.AddGroup() 创建，Dispose 时自动释放。
    /// 3. Browse 无显式分页 —— GetElements() 一次性返回该层级全部子元素。
    ///    大量点位场景下依赖 COM 内部的枚举分页（如果配置了 BatchSize）。
    /// 4. group.Read() 同步读取会触发 ValuesChanged 事件，但 DataBridge
    ///    的快照式更新机制天然消重（同值覆盖），不影响数据正确性。
    /// </summary>
    public class OpcDaClient : IOpcDaClient
    {
        private OpcDaServer _server;
        private OpcDaGroup _group;

        private readonly string _serverProgId;
        private readonly List<TagConfig> _tags;
        private readonly string _host;

        // 字典锁：AddAllItems 中批量重建（Clear + 批量写入）需要原子性。
        // 日常 DataChanged 回调读取依赖 ConcurrentDictionary 的线程安全性，无需持锁。
        private readonly object _dictLock = new object();
        private readonly ConcurrentDictionary<string, OpcDaItem> _tagKeyToItem =
            new ConcurrentDictionary<string, OpcDaItem>();
        private readonly ConcurrentDictionary<string, List<string>> _itemIdToTagKeys =
            new ConcurrentDictionary<string, List<string>>();

        private Timer _readTimer;

        // 生命周期锁 + 原子 Disposed 守护
        private readonly object _lifecycleLock = new object();
        private int _disposedInt;

        /// <summary>
        /// 数据变化事件：参数 (标签TagKey, 值, 质量是否Good, 时间戳)。
        /// 由 OPC DA 服务器的异步订阅回调触发，回调线程为 COM 线程池线程。
        /// </summary>
        public event Action<string, object, bool, DateTime> OnDataChanged;

        /// <summary>
        /// 连接状态变化事件，用于向 UI 层报告连接/断开/错误等状态信息。
        /// </summary>
        public event Action<string> OnStatusChanged;

        /// <summary>
        /// 指示当前是否已成功连接到 OPC DA 服务器。
        /// 使用 volatile 字段 + 属性包装，确保多线程间的可见性（读操作总能获取最新写入值）。
        /// 改为属性形式以满足 IOpcDaClient.IsConnected { get; } 接口契约。
        /// </summary>
        private volatile bool _isConnected;
        public bool IsConnected => _isConnected;

        /// <summary>
        /// 创建 DA 客户端实例。
        /// </summary>
        /// <param name="serverProgId">DA 服务器的 ProgId（例如 "Matrikon.OPC.Simulation.1"）。</param>
        /// <param name="tags">需要订阅的标签配置列表。</param>
        /// <param name="host">DA 服务器所在主机名，默认 "localhost" 表示本机。</param>
        public OpcDaClient(string serverProgId, List<TagConfig> tags, string host = "localhost")
        {
            _serverProgId = serverProgId ?? throw new ArgumentNullException(nameof(serverProgId));
            _tags = tags ?? throw new ArgumentNullException(nameof(tags));
            _host = string.IsNullOrWhiteSpace(host) ? "localhost" : host;
        }

        /// <summary>
        /// 启动 OPC DA 客户端：连接到服务器、创建订阅组、添加标签点位、注册异步回调。
        /// 如果连接过程中任何步骤失败，会自动调用 Cleanup() 释放已创建的资源并重新抛出异常。
        /// </summary>
        /// <param name="updateRateMs">订阅组的刷新率（毫秒），OPC DA 服务器按此周期推送数据变化。</param>
        /// <summary>
        /// 启动 OPC DA 客户端：连接服务器、创建订阅、按指定数据获取方式注册回调或启动轮询。
        /// 如果连接过程中任何步骤失败，会自动调用 Cleanup() 释放已创建的资源并重新抛出异常。
        /// </summary>
        /// <param name="updateRateMs">刷新频率（毫秒），异步模式作为订阅推送周期、同步模式作为轮询周期。</param>
        /// <param name="mode">数据获取方式（异步订阅 / 同步轮询）。</param>
        public void Start(int updateRateMs, DaAcquisitionMode mode)
        {
            try
            {
                OnStatusChanged?.Invoke("正在创建 OPC DA 客户端 (TitaniumAS)...");

                var uri = UrlBuilder.Build(_serverProgId, _host);
                OnStatusChanged?.Invoke($"  URL: {uri}");

                // 创建 COM 服务器代理并建立连接。
                // Connect() 内部调用 COM 的 CoCreateInstance 创建远程/本地 OPC DA 服务器实例
                _server = new OpcDaServer(uri);
                _server.Connect();
                OnStatusChanged?.Invoke($"  已连接到 OPC DA 服务器: {_serverProgId}");

                // 创建订阅组（OPC DA Group），设置刷新率。
                _group = _server.AddGroup("OpcDaToUaGroup");
                _group.UpdateRate = TimeSpan.FromMilliseconds(updateRateMs);
                _group.IsActive = true;
                OnStatusChanged?.Invoke($"  订阅已创建, 刷新率: {updateRateMs}ms");

                // 将配置的标签点位添加到订阅组中。
                AddAllItems();

                if (mode == DaAcquisitionMode.Async)
                {
                    // 异步订阅：注册数据变化回调，由 OPC DA 服务器主动推送。
                    _group.ValuesChanged += OnValuesChanged;
                    _group.IsSubscribed = true;

                    // 5 分钟定时同步读取作为异步订阅的兜底保障（防止回调丢失）。
                    _readTimer = new Timer(_ => DoSyncRead(), null, AppConstants.DaSyncIntervalMs, AppConstants.DaSyncIntervalMs);
                    OnStatusChanged?.Invoke($"  异步订阅已启用（回调推送 + {AppConstants.DaSyncIntervalMs / 60000} 分钟同步兜底）");
                }
                else
                {
                    // 同步轮询：不依赖服务器回调，由网关按刷新频率定时主动读取。
                    _group.IsSubscribed = false;
                    _readTimer = new Timer(_ => DoSyncRead(), null, updateRateMs, updateRateMs);
                    OnStatusChanged?.Invoke($"  同步轮询已启用, 轮询间隔: {updateRateMs}ms");
                }

                _isConnected = true;
                OnStatusChanged?.Invoke($"已连接 OPC DA 服务器: {_serverProgId}");
            }
            catch (Exception ex)
            {
                _isConnected = false;
                Cleanup();
                OnStatusChanged?.Invoke($"连接失败: {ex.Message}");
                throw;
            }
        }

        /// <summary>
        /// 启动 OPC DA 客户端（默认异步订阅模式）。
        /// </summary>
        /// <param name="updateRateMs">刷新频率（毫秒），OPC DA 服务器推送数据变化的频率。</param>
        public void Start(int updateRateMs)
        {
            Start(updateRateMs, DaAcquisitionMode.Async);
        }

        /// <summary>
        /// 停止 OPC DA 客户端并释放所有 COM 资源。调用后 IsConnected 将被设为 false。
        /// </summary>
        public void Stop()
        {
            Cleanup();
            _isConnected = false;
        }

        /// <summary>
        /// 释放 OPC DA 客户端占用的所有资源。
        /// 与 TryReconnect 互斥：通过 _lifecycleLock 确保 Dispose 和重连操作不会并发执行。
        /// </summary>
        public void Dispose()
        {
            lock (_lifecycleLock)
            {
                Stop();
            }
        }

        // ================================================================
        //  私有方法：添加标签（批量提交到订阅组）
        // ================================================================

        private void AddAllItems()
        {
            if (_tags == null || _tags.Count == 0) return;

            int count = _tags.Count;
            OnStatusChanged?.Invoke($"正在添加 {count} 个标签...");
            OnStatusChanged?.Invoke(
                $"前3个标签: {string.Join(", ", _tags.Take(3).Select(t => $"\"{t.ItemId}\""))}");

            // 分批向 OPC DA 服务器提交点位，避免大量标签时单次 COM 调用超时。
            var allResults = new OpcDaItemResult[count];
            int batchCount = (count + AppConstants.DaAddItemBatchSize - 1) / AppConstants.DaAddItemBatchSize;

            for (int batch = 0; batch < batchCount; batch++)
            {
                int start = batch * AppConstants.DaAddItemBatchSize;
                int batchSize = Math.Min(AppConstants.DaAddItemBatchSize, count - start);

                var batchDefs = new OpcDaItemDefinition[batchSize];
                for (int i = 0; i < batchSize; i++)
                {
                    batchDefs[i] = new OpcDaItemDefinition
                    {
                        ItemId = _tags[start + i].ItemId,
                        IsActive = true
                    };
                }

                if (batchCount > 1)
                    OnStatusChanged?.Invoke($"  批次 {batch + 1}/{batchCount}: 提交 {batchSize} 个标签...");

                OpcDaItemResult[] batchResults = _group.AddItems(batchDefs);
                Array.Copy(batchResults, 0, allResults, start, batchResults.Length);
            }

            // 收集锁内产生的状态消息，在锁外触发事件，防止死锁。
            var statusMessages = new List<string>();
            int successCount = 0;
            int failCount = 0;

            lock (_dictLock)
            {
                _tagKeyToItem.Clear();
                _itemIdToTagKeys.Clear();

                for (int i = 0; i < allResults.Length; i++)
                {
                    string tagKey = _tags[i].TagKey;

                    if (!allResults[i].Error.Succeeded)
                    {
                        failCount++;
                        if (failCount <= 5)
                            statusMessages.Add(
                                $"  标签 [{_tags[i].ItemId}] 失败: 0x{(int)allResults[i].Error:X8}");
                        continue;
                    }

                    var addedItem = allResults[i].Item;
                    _tagKeyToItem[tagKey] = addedItem;

                    string itemId = _tags[i].ItemId;
                    _itemIdToTagKeys.AddOrUpdate(itemId,
                        _ => { var list = new List<string>(); list.Add(tagKey); return list; },
                        (_, list) => { lock (list) { list.Add(tagKey); } return list; });

                    successCount++;
                }
            } // end lock

            // 事件回调在锁外执行。
            foreach (string msg in statusMessages)
                OnStatusChanged?.Invoke(msg);

            if (failCount > 5)
                OnStatusChanged?.Invoke($"  ...及其他 {failCount - 5} 个标签也失败");

            OnStatusChanged?.Invoke(
                $"添加结果: {successCount} 成功, {failCount} 失败 (共 {count})");
        }

        // ================================================================
        //  异步回调（OPC DA 订阅数据变化事件）
        // ================================================================

        /// <summary>
        /// OPC DA 订阅的异步数据变化回调处理程序（TitaniumAS ValuesChanged 事件）。
        /// 当 OPC DA 服务器检测到已订阅点位的数据变化时触发。
        /// 
        /// 数据流路径：OPC DA 服务器 → COM 回调线程 → 本方法 → OnDataChanged 事件 → GatewayManager。
        /// </summary>
        private void OnValuesChanged(object sender, OpcDaItemValuesChangedEventArgs args)
        {
            foreach (OpcDaItemValue value in args.Values)
            {
                try
                {
                    if (!value.Error.Succeeded) continue;
                    if (value.Value == null) continue;

                    string itemId = value.Item?.ItemId;
                    if (string.IsNullOrEmpty(itemId)) continue;

                    bool isGood = value.Error.Succeeded;
                    DateTime timestamp = value.Timestamp.LocalDateTime;

                    if (_itemIdToTagKeys.TryGetValue(itemId, out var tagKeys))
                    {
                        string[] keysSnapshot;
                        lock (tagKeys) { keysSnapshot = tagKeys.ToArray(); }
                        foreach (string tagKey in keysSnapshot)
                            OnDataChanged?.Invoke(tagKey, value.Value, isGood, timestamp);
                    }
                }
                catch (Exception ex)
                {
                    OnStatusChanged?.Invoke($"[数据回调异常] {ex.Message}");
                }
            }
        }

        // ================================================================
        //  定时同步读取（异步订阅的兜底保障）
        // ================================================================

        /// <summary>
        /// 由 5 分钟定时器触发的同步全量读取。
        /// 对订阅组中的所有点位执行一次同步 Read，结果通过 OnDataChanged 事件投递。
        /// 
        /// 注意：TitaniumAS 的 group.Read() 也会触发 ValuesChanged 事件，
        /// 但由于 DataBridge 使用快照式更新（同值覆盖），不会产生副作用。
        /// </summary>
        private void DoSyncRead()
        {
            OpcDaGroup group;
            ConcurrentDictionary<string, OpcDaItem> tagKeyToItem;
            bool connected;

            // H-30 修复：检查 _disposedInt，防止 Cleanup() 释放资源后仍在执行的定时器回调
            //       访问已 Dispose 的 _group/_server，导致 ObjectDisposedException 或竞态崩溃。
            if (Volatile.Read(ref _disposedInt) == 1) return;

            group = _group;
            tagKeyToItem = _tagKeyToItem;
            connected = IsConnected;

            if (group == null || !connected || tagKeyToItem == null || tagKeyToItem.Count == 0)
                return;

            try
            {
                var allItems = tagKeyToItem.Values.ToList();
                if (allItems.Count == 0) return;

                // 同步读取所有点位（从 Device 获取最新值）
                OpcDaItemValue[] results = group.Read(allItems, OpcDaDataSource.Device);

                foreach (OpcDaItemValue value in results)
                {
                    try
                    {
                        if (!value.Error.Succeeded) continue;
                        if (value.Value == null) continue;

                        string itemId = value.Item?.ItemId;
                        if (string.IsNullOrEmpty(itemId)) continue;

                        bool isGood = value.Error.Succeeded;
                        DateTime timestamp = value.Timestamp.LocalDateTime;

                        if (_itemIdToTagKeys.TryGetValue(itemId, out var tagKeys))
                        {
                            string[] keysSnapshot;
                            lock (tagKeys) { keysSnapshot = tagKeys.ToArray(); }
                            foreach (string tagKey in keysSnapshot)
                                OnDataChanged?.Invoke(tagKey, value.Value, isGood, timestamp);
                        }
                    }
                    catch { }
                }
            }
            catch (Exception ex)
            {
                OnStatusChanged?.Invoke($"[定时同步] 读取失败: {ex.Message}");
            }
        }

        // ================================================================
        //  清理与释放
        // ================================================================

        /// <summary>
        /// 释放所有 COM 资源，确保幂等性（多次调用只有第一次生效）。
        /// 
        /// COM 资源释放顺序：停止定时器 → 移除回调 → 移除组 → 断开服务器 → 清空字典。
        /// 每一步都用独立的 try-catch 包裹，因为 COM 对象可能已被服务器端释放。
        /// </summary>
        private void Cleanup()
        {
            if (Interlocked.Exchange(ref _disposedInt, 1) == 1) return;

            try
            {
                StopReadTimer();

                if (_group != null)
                {
                    try { _group.ValuesChanged -= OnValuesChanged; } catch { }
                    try { _group.IsSubscribed = false; } catch { }
                    try { _server?.RemoveGroup(_group); } catch { }
                    try { ((IDisposable)_group).Dispose(); } catch { }
                    _group = null;
                }

                if (_server != null)
                {
                    try { _server.Dispose(); } catch { }
                    _server = null;
                }

                lock (_dictLock)
                {
                    _tagKeyToItem.Clear();
                    _itemIdToTagKeys.Clear();
                }

                _isConnected = false;
            }
            catch (Exception ex)
            {
                OnStatusChanged?.Invoke($"清理 OPC DA 资源时出错: {ex.Message}");
            }

            OnStatusChanged?.Invoke("OPC DA 已断开");
        }

        private void StopReadTimer()
        {
            if (_readTimer != null)
            {
                try { _readTimer.Dispose(); } catch { }
                _readTimer = null;
            }
        }

        // ================================================================
        //  重连
        // ================================================================

        /// <summary>
        /// 尝试重新连接到 OPC DA 服务器。由看门狗定时器在检测到连接中断时调用。
        /// 与 Dispose 互斥：通过 _lifecycleLock 确保 TryReconnect 和 Dispose 不会并发执行。
        /// </summary>
        /// <param name="updateRateMs">重连后订阅组的刷新率（毫秒）。</param>
        /// <returns>重连是否成功。</returns>
        public bool TryReconnect(int updateRateMs)
        {
            lock (_lifecycleLock)
            {
                // V1.9.0 修复：在重连前检查 _disposedInt，防止 Dispose 后仍在排队的回调
                //       访问已释放的 _group/_server，导致 ObjectDisposedException 或竞态崩溃。
                if (Volatile.Read(ref _disposedInt) == 1) return false;

                try
                {
                    OnStatusChanged?.Invoke("[看门狗] 正在尝试重新连接 OPC DA...");
                    Cleanup();
                    Interlocked.Exchange(ref _disposedInt, 0);
                    Start(updateRateMs);
                    OnStatusChanged?.Invoke("[看门狗] OPC DA 重连成功");
                    return true;
                }
                catch (Exception ex)
                {
                    OnStatusChanged?.Invoke($"[看门狗] OPC DA 重连失败: {ex.Message}");
                    return false;
                }
            }
        }

        // ================================================================
        //  静态方法：浏览 OPC DA 服务器的全部点位
        // ================================================================

        /// <summary>浏览地址空间时的最大递归深度。</summary>
        public const int MaxBrowseDepth = 10;

        // R-6 修复：AppConstants.DaMaxBrowseItems/AppConstants.DaAddItemBatchSize/AppConstants.DaSyncIntervalMs 迁移至 AppConstants，统一调优入口

        /// <summary>
        /// 浏览 OPC DA 服务器的所有叶子点位（支持远程服务器）。
        /// 使用递归深度优先遍历，最大深度 MaxBrowseDepth 层，
        /// 最多返回 AppConstants.DaMaxBrowseItems 个点位以防止内存溢出。
        /// 
        /// H-26 迁移：使用 OpcDaBrowserAuto.GetElements() 替代
        /// Technosoftware 的 server.Browse() + BrowseNext() 分页模式。
        /// TitaniumAS 没有显式分页 API —— GetElements() 一次性返回当前层级
        /// 全部子元素，其内部通过 OPC DA 3.0 的 dwMaxElementsReturned 控制单次数量。
        /// </summary>
        public static List<OpcDaItemInfo> BrowseAllItems(
            string serverProgId, string host = "localhost", Action<string> logger = null)
        {
            if (string.IsNullOrWhiteSpace(host)) host = "localhost";

            void Log(string message)
            {
                OpcServerScanner.Log(message);
                logger?.Invoke(message);
            }

            try
            {
                var uri = UrlBuilder.Build(serverProgId, host);
                Log($"[Browse] 尝试连接 OPC DA 服务器");
                Log($"[Browse]   主机: {host}");
                Log($"[Browse]   ProgId: {serverProgId}");
                Log($"[Browse]   URL: {uri}");
                Log($"[Browse]   最大递归深度: {MaxBrowseDepth}");
                Log($"[Browse]   最大点位数量: {AppConstants.DaMaxBrowseItems}");

                using (var server = new OpcDaServer(uri))
                {
                    server.Connect();
                    Log($"[Browse] 连接成功，开始递归浏览...");

                    var items = new List<OpcDaItemInfo>();
                    var browser = new OpcDaBrowserAuto(server);
                    Log($"[Browse] 浏览器类型: {browser.GetType().Name}");
                    BrowseRecursive(browser, items, MaxBrowseDepth,
                        currentDepth: 0, parentItemId: null, logger: Log);

                    Log($"[Browse] 浏览完成，共获取 {items.Count} 个点位");
                    if (items.Count == 0)
                    {
                        Log($"[Browse] ⚠️ 警告: 浏览返回 0 个点位！请检查:");
                        Log($"[Browse]   1. OPC DA 服务器是否正常运行且有已配置的标签");
                        Log($"[Browse]   2. 服务器地址空间结构是否为分支嵌套（非扁平根节点）");
                        Log($"[Browse]   3. 可使用 OPC 客户端工具（如 Matrikon OPC Explorer）验证");
                    }
                    return items;
                }
            }
            catch (Exception ex)
            {
                // 连接失败时自动运行 COM 注册表诊断
                var dcomDiag = DiagnoseComRegistration(serverProgId);
                foreach (var line in dcomDiag)
                    Log(line);

                string errorMsg = $"连接 OPC DA 服务器失败\n" +
                                  $"  服务器: {serverProgId}\n" +
                                  $"  主机: {host}\n" +
                                  $"  错误: {ex.Message}\n" +
                                  $"  错误代码: 0x{ex.HResult:X8}\n\n" +
                                  $"可能的原因:\n" +
                                  $"  1. OPC DA 服务器未运行 - 请检查服务是否启动\n" +
                                  $"  2. DCOM 权限配置错误 - 请运行 dcomcnfg 配置权限\n" +
                                  $"  3. 32位/64位不匹配 - 确认程序与 OPC 服务器位数一致\n" +
                                  $"  4. 服务器 ProgId 不正确 - 请确认 ProgId 拼写正确\n" +
                                  $"  5. 防火墙阻止连接 - 请检查防火墙设置";

                Log($"[Browse] {errorMsg}");
                Log($"[Browse] 异常堆栈: {ex.StackTrace}");

                throw new InvalidOperationException(errorMsg, ex);
            }
        }

        /// <summary>
        /// 诊断 COM 类注册状态：查找 ProgId 对应的 CLSID、LocalServer32 路径、文件是否存在。
        /// 在 BrowseAllItems 连接失败时调用，帮助快速定位注册表层面的问题。
        /// </summary>
        private static List<string> DiagnoseComRegistration(string progId)
        {
            var results = new List<string>();
            if (string.IsNullOrEmpty(progId))
            {
                results.Add("[DCOM] ProgId 为空，跳过诊断");
                return results;
            }

            try
            {
                results.Add($"[DCOM] 正在诊断 ProgId: {progId}");

                // Step 1: ProgId → CLSID
                using (var progIdKey = Microsoft.Win32.Registry.ClassesRoot.OpenSubKey(progId))
                {
                    if (progIdKey == null)
                    {
                        results.Add($"[DCOM] ✗ 在 HKCR\\{progId} 未找到 ProgId 注册");
                        results.Add("[DCOM]   可能原因: 32位/64位注册表不匹配，或 OPC 服务器未正确安装");
                        return results;
                    }
                    results.Add($"[DCOM] ✓ 找到 ProgId 注册: HKCR\\{progId}");
                }

                using (var clsidKey = Microsoft.Win32.Registry.ClassesRoot.OpenSubKey($"{progId}\\CLSID"))
                {
                    if (clsidKey == null)
                    {
                        results.Add($"[DCOM] ✗ 在 HKCR\\{progId}\\CLSID 未找到 CLSID 子键");
                        return results;
                    }
                    string clsid = clsidKey.GetValue(null) as string;
                    if (string.IsNullOrEmpty(clsid))
                    {
                        results.Add("[DCOM] ✗ CLSID 值为空");
                        return results;
                    }
                    results.Add($"[DCOM] ✓ CLSID: {clsid}");

                    // Step 2: CLSID → LocalServer32
                    using (var clsKey = Microsoft.Win32.Registry.ClassesRoot.OpenSubKey($"CLSID\\{clsid}"))
                    {
                        if (clsKey == null)
                        {
                            results.Add($"[DCOM] ✗ 在 HKCR\\CLSID\\{clsid} 未找到 CLSID 注册");
                            return results;
                        }

                        using (var lsKey = clsKey.OpenSubKey("LocalServer32"))
                        {
                            if (lsKey != null)
                            {
                                string serverPath = lsKey.GetValue(null) as string;
                                results.Add($"[DCOM] ✓ LocalServer32: {serverPath}");
                                bool exists = System.IO.File.Exists(serverPath);
                                results.Add(exists
                                    ? $"[DCOM] ✓ 文件存在"
                                    : $"[DCOM] ✗ 文件不存在！请检查 OPC 服务器安装");
                            }
                            else
                            {
                                // 某些 OPC 服务器注册为 InprocServer32（DLL 代理）
                                using (var isKey = clsKey.OpenSubKey("InprocServer32"))
                                {
                                    if (isKey != null)
                                    {
                                        string dllPath = isKey.GetValue(null) as string;
                                        results.Add($"[DCOM] ✓ InprocServer32 (DLL): {dllPath}");
                                        bool exists = System.IO.File.Exists(dllPath);
                                        results.Add(exists
                                            ? $"[DCOM] ✓ DLL 文件存在"
                                            : $"[DCOM] ✗ DLL 文件不存在！");
                                    }
                                    else
                                    {
                                        results.Add("[DCOM] ✗ 未找到 LocalServer32 或 InprocServer32 子键");
                                    }
                                }
                            }
                        }

                        // Step 3: AppID → DCOM 配置
                        string appId = clsKey.GetValue("AppID") as string;
                        if (!string.IsNullOrEmpty(appId))
                        {
                            results.Add($"[DCOM] ✓ AppID: {appId}");
                            using (var appIdKey = Microsoft.Win32.Registry.ClassesRoot.OpenSubKey($"AppID\\{appId}"))
                            {
                                if (appIdKey != null)
                                {
                                    string runAs = appIdKey.GetValue("RunAs") as string;
                                    if (!string.IsNullOrEmpty(runAs))
                                        results.Add($"[DCOM]   RunAs (启动身份): {runAs}");
                                    else
                                        results.Add("[DCOM]   RunAs 未设置（使用交互式用户或启动用户）");

                                    string dllSurrogate = appIdKey.GetValue("DllSurrogate") as string;
                                    if (!string.IsNullOrEmpty(dllSurrogate))
                                        results.Add($"[DCOM]   DllSurrogate: {dllSurrogate}");
                                }
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                results.Add($"[DCOM] 诊断异常: {ex.Message}");
            }

            return results;
        }

        /// <summary>
        /// 递归浏览 OPC DA 服务器的节点树，收集所有叶子点位。
        /// </summary>
        private static void BrowseRecursive(
            IOpcDaBrowser browser,
            List<OpcDaItemInfo> items,
            int maxDepth,
            int currentDepth,
            string parentItemId,
            Action<string> logger = null)
        {
            if (currentDepth > maxDepth) return;
            if (items.Count >= AppConstants.DaMaxBrowseItems)
            {
                logger?.Invoke($"[Browse] 已达最大点位上限 {AppConstants.DaMaxBrowseItems}，停止浏览");
                return;
            }

            string branchLabel = parentItemId ?? "(root)";

            try
            {
                OpcDaBrowseElement[] elements = browser.GetElements(parentItemId);

                // 新增：记录每个分支的元素数量和节点类型分布，辅助排查"扫描不到点位"问题
                int branchCount = 0;
                int leafCount = 0;
                int otherCount = 0;

                if (elements == null || elements.Length == 0)
                {
                    logger?.Invoke($"[Browse] 分支 [{branchLabel}] 返回 0 个元素 (深度={currentDepth})");
                    return;
                }

                foreach (var element in elements)
                {
                    if (items.Count >= AppConstants.DaMaxBrowseItems) break;

                    if (element.IsItem)
                    {
                        leafCount++;
                        string itemName = element.ItemId;
                        if (!string.IsNullOrEmpty(itemName))
                        {
                            string dataTypeName = ExtractDataType(element);

                            items.Add(new OpcDaItemInfo
                            {
                                ItemId = itemName,
                                Name = element.Name ?? itemName,
                                DataTypeName = dataTypeName,
                                Description = parentItemId ?? ""
                            });
                        }
                    }
                    else if (element.HasChildren)
                    {
                        branchCount++;
                    }
                    else
                    {
                        otherCount++;
                    }

                    // 进度报告：每扫描 ~2000 个点位输出一次
                    int progress = items.Count;
                    if (progress > 0 && progress % 2000 == 0)
                    {
                        logger?.Invoke(
                            $"[Browse] 已扫描 {progress} 个点位... " +
                            $"(当前分支: {branchLabel}, 本层: {leafCount}叶/{branchCount}枝/{otherCount}其他)");
                    }

                    // 递归探索子节点
                    if (element.HasChildren && currentDepth < maxDepth)
                        BrowseRecursive(browser, items, maxDepth,
                            currentDepth + 1, element.ItemId, logger);
                }

                // 分支摘要日志：每层统计叶子和子分支数量
                logger?.Invoke(
                    $"[Browse] 分支 [{branchLabel}] (深度={currentDepth}): " +
                    $"共 {elements.Length} 元素, {leafCount} 叶子, {branchCount} 分支, {otherCount} 其他");
            }
            catch (Exception ex)
            {
                string errMsg = $"[Browse] 浏览分支 [{branchLabel}] 失败: {ex.Message} (0x{ex.HResult:X8})";
                OpcServerScanner.Log(errMsg);
                logger?.Invoke(errMsg);
            }
        }

        /// <summary>
                /// <summary>
        /// 从浏览元素的 ItemProperties 中提取实际数据类型名称。
        /// TitaniumAS 的 OpcDaBrowseElement.ItemProperties.Properties 数组中包含
        /// 每个标签的属性（Value、DataType、Quality、Timestamp 等）。
        /// 优先使用 DataType 属性的 Type 名称（如 Int32、Double、Boolean），
        /// 回退到 Value 属性的实际类型，最后回退到 "Variant"。
        /// </summary>
        private static string ExtractDataType(OpcDaBrowseElement element)
        {
            try
            {
                var itemProps = element.ItemProperties;
                if (itemProps?.Properties == null)
                    return "Variant";

                foreach (var prop in itemProps.Properties)
                {
                    if (prop.DataType != null)
                    {
                        string typeName = prop.DataType.Name;
                        if (!string.IsNullOrEmpty(typeName) && typeName != "Object" && typeName != "String")
                            return MapBclToDataType(typeName);
                    }
                    if (prop.Value != null)
                    {
                        string typeName = prop.Value.GetType().Name;
                        if (!string.IsNullOrEmpty(typeName) && typeName != "Object" && typeName != "String")
                            return MapBclToDataType(typeName);
                    }
                }
            }
            catch { }
            return "Variant";
        }

        /// <summary>
        /// 将 .NET BCL 类型名称映射为 DataTypeConverter 支持的数据类型名。
        /// </summary>
        private static string MapBclToDataType(string bclTypeName)
        {
            switch (bclTypeName)
            {
                case "SByte":
                case "Int16":
                case "Int32":
                case "Int64":
                case "Byte":
                case "UInt16":
                case "UInt32":
                case "UInt64":
                    return bclTypeName;
                case "Single": return "Float";
                case "Double": return "Double";
                case "Boolean": return "Boolean";
                case "String": return "String";
                case "DateTime": return "DateTime";
                default: return bclTypeName;
            }
        }
    }

    /// <summary>
    /// OPC DA 服务器的一个点位信息（浏览结果），用于 UI 展示和标签配置。
    /// </summary>
    public class OpcDaItemInfo
    {
        /// <summary>OPC DA 点位的完整 ItemId（例如 "Device1.Group1.Temperature"）。</summary>
        public string ItemId { get; set; }
        /// <summary>点位的显示名称。</summary>
        public string Name { get; set; }
        /// <summary>数据类型名称（例如 "Int32"、"Float"），无法获取时为 "Variant"。</summary>
        public string DataTypeName { get; set; }
        /// <summary>点位的描述信息（通常为父节点路径）。</summary>
        public string Description { get; set; }
        /// <summary>返回 ItemId 用于调试和日志输出。</summary>
        public override string ToString() => ItemId;
    }
}
