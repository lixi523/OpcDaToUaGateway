using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Opc.Ua;
using Opc.Ua.Configuration;
using Opc.Ua.Server;
using OpcDaToUaGateway.Models;
using OpcDaToUaGateway.Services.Interfaces;

namespace OpcDaToUaGateway
{
    // ================================================================
    //  自定义节点管理器 —— 管理 OPC DA 桥接过来的变量节点
    // ================================================================

    /// <summary>
    /// 自定义 OPC UA 节点管理器，负责在 UA 地址空间中创建和维护从 OPC DA 桥接过来的变量节点。
    /// 
    /// 锁策略说明（Lock 属性继承自 CustomNodeManager2）：
    /// - 所有对地址空间的修改（创建文件夹、添加变量节点、更新变量值）都必须在 lock(Lock) 内执行。
    /// - 这把锁由 UA SDK 的 MasterNodeManager 在进行节点读取/浏览时也会持有，
    ///   因此它本质上是"地址空间的读写互斥锁"。
    /// - 不要在持锁期间执行任何可能阻塞的操作（如网络 I/O），否则会导致 UA SDK 的
    ///   请求处理线程被阻塞，影响所有客户端的读写响应延迟。
    /// - UpdateValue 被高频调用（与 DA 刷新率同频），锁持有时间应尽量短。
    /// </summary>
    public class GatewayNodeManager : CustomNodeManager2
    {
        private readonly string _namespaceUri;

        /// <summary>诊断日志事件，用于报告地址空间创建、节点注册等关键操作的状态。</summary>
        public event Action<string> OnDiagnostics;

        /// <summary>
        /// 早期诊断消息缓冲 — CreateAddressSpace 在服务器启动过程中被调用，
        /// 此时 OnDiagnostics 事件尚未被外部订阅。将早期消息暂存，
        /// 待 SubscribeDiagnostics 被调用时一次性回放，确保不丢失任何诊断信息。
        /// </summary>
        private readonly List<string> _earlyDiagnostics = new List<string>();

        // P1 优化：缓存 BaseDataVariableState 引用，UpdateValue 查找从 O(n) → O(1)。
        // key = 业务 TagKey，value = UA 变量节点对象引用。
        private readonly Dictionary<string, BaseDataVariableState> _variableCache = new Dictionary<string, BaseDataVariableState>();

        // 文件夹缓存，key = 路径（如 "Device1.Group1"），value = FolderState 对象引用。
        // 使用 OrdinalIgnoreCase 比较器，因为 OPC DA 的 ItemId 大小写不敏感。
        private readonly Dictionary<string, FolderState> _folderCache = new Dictionary<string, FolderState>(StringComparer.OrdinalIgnoreCase);

        private FolderState _daTagsFolder;
        private ushort _namespaceIndex;

        // H-24-1 Flat 变量批量分组：无分支时不在单个文件夹下放过多变量，
        // 而是按 BatchSize 分组到 FlatTags/Batch_000、Batch_001... 子文件夹中。
        // 避免单节点引用数过多触发 SDK 内部的 Browse 限制或性能问题。
        private int _flatBatchCount;        // 当前批次数（已创建的批量子文件夹数）
        private int _flatCurrentBatchCount; // 当前批次的变量计数

        /// <summary>
        /// 创建节点管理器实例。
        /// </summary>
        /// <param name="server">UA 服务器内部接口。</param>
        /// <param name="configuration">应用配置。</param>
        /// <param name="namespaceUri">自定义命名空间 URI。</param>
        public GatewayNodeManager(IServerInternal server, ApplicationConfiguration configuration, string namespaceUri)
            : base(server, configuration, namespaceUri)
        {
            _namespaceUri = namespaceUri;
        }

        /// <summary>
        /// 创建初始地址空间：在 ObjectsFolder 下创建 "DaTags" 根文件夹，作为所有桥接变量的父节点。
        /// 由 UA SDK 在服务器启动时自动调用。
        /// </summary>
        /// <param name="externalReferences">外部引用集合，用于将 DaTags 文件夹挂接到 ObjectsFolder。</param>
        public override void CreateAddressSpace(IDictionary<NodeId, IList<IReference>> externalReferences)
        {
            lock (Lock)
            {
                _namespaceIndex = NamespaceIndexes[0];

                var folderId = new NodeId("DaTags", _namespaceIndex);
                _daTagsFolder = new FolderState(null)
                {
                    NodeId = folderId,
                    BrowseName = new QualifiedName("DaTags", _namespaceIndex),
                    DisplayName = new LocalizedText("OPC DA Tags"),
                    Description = new LocalizedText("Data bridged from OPC DA server"),
                    WriteMask = AttributeWriteMask.None,
                    UserWriteMask = AttributeWriteMask.None,
                    ReferenceTypeId = ReferenceTypes.Organizes,
                    TypeDefinitionId = ObjectTypeIds.FolderType,
                    EventNotifier = EventNotifiers.None
                };

                if (!externalReferences.TryGetValue(ObjectIds.ObjectsFolder, out var references))
                {
                    references = new List<IReference>();
                    externalReferences[ObjectIds.ObjectsFolder] = references;
                }

                // 建立双向引用：DaTags → ObjectsFolder（反向）+ ObjectsFolder → DaTags（正向）。
                // OPC UA 规范要求 Organizes 引用必须在父子双方都注册。
                _daTagsFolder.AddReference(ReferenceTypes.Organizes, true, ObjectIds.ObjectsFolder);
                references.Add(new NodeStateReference(ReferenceTypes.Organizes, false, _daTagsFolder.NodeId));
                AddPredefinedNode(SystemContext, _daTagsFolder);
                _folderCache[""] = _daTagsFolder;

                // 诊断日志：报告地址空间初始化状态
                Diag($"[诊断] 地址空间已创建 — 命名空间索引: {_namespaceIndex}, " +
                    $"命名空间URI: {_namespaceUri}, DaTags NodeId: {_daTagsFolder.NodeId}, " +
                    $"外部引用数: {references.Count}");
            }
        }

        /// <summary>
        /// 在地址空间中创建一个变量节点，并自动创建其父文件夹路径。
        /// 变量节点以 "DaTag_{tagKey}" 作为 NodeId，挂载在按 ItemId 层级生成的文件夹下。
        /// </summary>
        /// <param name="tagKey">业务标签键（用于后续 UpdateValue 查找）。</param>
        /// <param name="itemId">OPC DA 的完整 ItemId（用于生成文件夹层级和 BrowseName）。</param>
        /// <param name="displayName">在 UA 客户端中展示的名称。</param>
        /// <param name="dataType">UA 内置数据类型。</param>
        /// <param name="nodeId">
        /// 预分配的 NodeId 标识符（如 "DaTag_Channel1_Device0_Status"）。
        /// 由点位导入时预计算并持久化到配置文件。为空时回退到 "DaTag_{tagKey}" 自动生成。
        /// </param>
        public void AddVariableNode(string tagKey, string itemId, string displayName, BuiltInType dataType, string nodeId = null)
        {
            lock (Lock)
            {
                // H-33: 防止配置错误或异常导入导致缓存无限增长 → OOM
                if (_variableCache.Count >= AppConstants.MaxVariableNodes)
                {
                    Diag($"[警告] 变量节点已达上限 {AppConstants.MaxVariableNodes}，拒绝添加: {tagKey}");
                    return;
                }

                // 根据 ItemId 的 "." 分隔符提取文件夹路径。
                // 例如 "Device1.Group1.Temperature" → branchPath = "Device1.Group1"。
                string branchPath = "";
                int lastDot = itemId.LastIndexOf('.');
                if (lastDot > 0)
                    branchPath = itemId.Substring(0, lastDot);

                FolderState parentFolder = GetOrCreateFolder(branchPath);

                var variable = new BaseDataVariableState(null)
                {
                    NodeId = new NodeId(nodeId ?? $"DaTag_{tagKey}", _namespaceIndex),
                    BrowseName = new QualifiedName(itemId, _namespaceIndex),
                    DisplayName = new LocalizedText(displayName),
                    DataType = GetDataTypeId(dataType),
                    ValueRank = ValueRanks.Scalar,
                    AccessLevel = AccessLevels.CurrentRead,
                    UserAccessLevel = AccessLevels.CurrentRead,
                    Historizing = false,
                    StatusCode = StatusCodes.UncertainInitialValue,
                    Timestamp = DateTime.UtcNow,
                    Value = GetDefaultValue(dataType)
                };

                // H-23 修复：手动分两步创建运行时节点，替代 CreateNode。
                // SDK 1.5.378.145 的 CreateNode 内部 instance.Create(..., assignNodeIds=true)
                // 在大批量创建场景下对 BaseDataVariableState 触发 NRE。
                // CreateNode 的核心逻辑就是这两步：
                //   Step 1) parentFolder.AddChild(variable) → 建立父子 Organizes 引用
                //   Step 2) AddPredefinedNode → 注册到 PredefinedNodes 字典
                // Organizes 引用是 UA 客户端 Browse 可见性的基础，
                // MasterNodeManager 通过遍历 Organizes 引用来构建浏览树。
                parentFolder.AddChild(variable);
                AddPredefinedNode(SystemContext, variable);

                // P1 优化：缓存变量引用，避免每次 UpdateValue 时做 O(n) 的 Find 查找。
                _variableCache[tagKey] = variable;
            }
        }

        /// <summary>
        /// 根据路径字符串获取或递归创建文件夹层级。
        /// 例如 "Device1.Group1" 会在 DaTags 下创建 Device1，再在 Device1 下创建 Group1。
        /// 
        /// H-24 修复：当 OPC DA 服务器没有层级分支（ItemId 不含 "."）时，
        /// 不直接返回 _daTagsFolder，而是创建一个名为 "FlatTags" 的中间子文件夹。
        /// 原因：MasterNodeManager 在初始化时缓存了 _daTagsFolder 的路由信息（当时为空），
        /// 之后通过 AddChild 动态添加的引用不会被路由表识别，导致 UA 客户端 Browse 时
        /// 返回空列表。创建运行时子文件夹可以绕开此缓存问题。
        /// </summary>
        private FolderState GetOrCreateFolder(string path)
        {
            // H-24-1：无分支时按 BatchSize 分批创建子文件夹，
            // 避免单个文件夹下引用数过多（35010个）触发 Browse 不可见的 SDK 边界条件。
            // 例如：FlatTags/Batch_000(0-999), Batch_001(1000-1999), ...
            if (string.IsNullOrEmpty(path))
            {
                // 检查当前批次是否已满，需要创建新批次文件夹
                if (_flatCurrentBatchCount >= AppConstants.UaFlatBatchSize)
                {
                    _flatBatchCount++;
                    _flatCurrentBatchCount = 0;
                }

                // 第一个批次且尚未创建任何批次文件夹时，创建根 FlatTags + Batch_000
                if (_flatBatchCount == 0 && _flatCurrentBatchCount == 0)
                {
                    const string flatRootName = "FlatTags";
                    if (!_folderCache.TryGetValue(flatRootName, out FolderState flatRoot))
                    {
                        flatRoot = CreateSimpleFolder("Folder_FlatTags", "FlatTags", "Flat Tags",
                            "Variables without DA branch hierarchy (batched)");
                        _daTagsFolder.AddChild(flatRoot);
                        AddPredefinedNode(SystemContext, flatRoot);
                        _folderCache[flatRootName] = flatRoot;
                        Diag($"[诊断] 创建 FlatTags 根文件夹 (无分支点位容器)");
                    }
                }

                // 构建当前批次文件夹名和缓存键
                string batchName = $"Batch_{_flatBatchCount:D3}";
                string batchCacheKey = $"FlatBatch:{_flatBatchCount}";

                if (!_folderCache.TryGetValue(batchCacheKey, out FolderState batchFolder))
                {
                    // 找到 FlatTags 根文件夹作为父节点
                    if (!_folderCache.TryGetValue("FlatTags", out FolderState flatRoot))
                        throw new InvalidOperationException("FlatTags root folder not found");

                    batchFolder = CreateSimpleFolder(
                        $"Folder_Batch_{_flatBatchCount:D3}",
                        batchName,
                        $"Batch {_flatBatchCount:D3}",
                        $"Flat variable batch {_flatBatchCount} (batch size {AppConstants.UaFlatBatchSize})");

                    flatRoot.AddChild(batchFolder);
                    AddPredefinedNode(SystemContext, batchFolder);
                    _folderCache[batchCacheKey] = batchFolder;
                }

                _flatCurrentBatchCount++;
                return batchFolder;
            }

            if (_folderCache.TryGetValue(path, out FolderState cached))
                return cached;

            // 逐级创建文件夹：先找到已缓存的最深祖先，然后从该层开始向下创建。
            string[] parts = path.Split('.');
            FolderState parent = _daTagsFolder;
            string currentPath = "";

            for (int i = 0; i < parts.Length; i++)
            {
                currentPath = i == 0 ? parts[0] : currentPath + "." + parts[i];
                if (_folderCache.TryGetValue(currentPath, out FolderState existing))
                {
                    parent = existing;
                    continue;
                }
                parent = CreateFolder(parts[i], currentPath, parent);
            }

            return parent;
        }

        /// <summary>
        /// 创建单个文件夹节点并注册到父节点下，同时加入缓存。
        /// </summary>
        private FolderState CreateFolder(string folderName, string fullPath, FolderState parent)
        {
            var folderId = new NodeId($"Folder_{fullPath}", _namespaceIndex);
            var folder = new FolderState(null)
            {
                NodeId = folderId,
                BrowseName = new QualifiedName(folderName, _namespaceIndex),
                DisplayName = new LocalizedText(folderName),
                WriteMask = AttributeWriteMask.None,
                UserWriteMask = AttributeWriteMask.None,
                ReferenceTypeId = ReferenceTypes.Organizes,
                TypeDefinitionId = ObjectTypeIds.FolderType,
                EventNotifier = EventNotifiers.None
            };

            // H-23 修复：手动创建文件夹节点（与 AddVariableNode 同理）
            parent.AddChild(folder);
            AddPredefinedNode(SystemContext, folder);

            _folderCache[fullPath] = folder;

            return folder;
        }

        /// <summary>
        /// H-24-1 新增：创建简单文件夹（不自动加入缓存，由调用方决定缓存策略）。
        /// 用于批量子文件夹等场景。
        /// </summary>
        private FolderState CreateSimpleFolder(string nodeId, string browseName, string displayName, string description)
        {
            return new FolderState(null)
            {
                NodeId = new NodeId(nodeId, _namespaceIndex),
                BrowseName = new QualifiedName(browseName, _namespaceIndex),
                DisplayName = new LocalizedText(displayName),
                Description = new LocalizedText(description),
                WriteMask = AttributeWriteMask.None,
                UserWriteMask = AttributeWriteMask.None,
                ReferenceTypeId = ReferenceTypes.Organizes,
                TypeDefinitionId = ObjectTypeIds.FolderType,
                EventNotifier = EventNotifiers.None
            };
        }

        /// <summary>
        /// 更新变量节点的值、时间戳和质量状态。
        /// 由 OpcDaClient 的 DataChanged 回调通过 GatewayManager 调用，频率与 DA 订阅刷新率一致。
        /// 使用 O(1) 的字典查找获取缓存的变量引用，然后直接修改其属性。
        /// </summary>
        /// <param name="tagKey">业务标签键。</param>
        /// <param name="value">新的数据值。</param>
        /// <param name="isGood">OPC DA 质量位是否为 Good。</param>
        /// <param name="sourceTimestamp">数据源时间戳。</param>
        public void UpdateValue(string tagKey, object value, bool isGood, DateTime sourceTimestamp)
        {
            // H-38: 锁内只做字典查找+值更新，ClearChangeMasks 在锁外执行。
            //       减少与 UA SDK Browse/Read 请求的锁竞争。变量引用永不删除，锁外操作安全。
            BaseDataVariableState variable = null;
            lock (Lock)
            {
                if (!_variableCache.TryGetValue(tagKey, out variable))
                    return;

                variable.Value = value;

                // V1.6.3 修复（延续）：bool 等快速翻转标签，OPC DA 服务器送来的源时间戳
                // 常长期冻结（实测约 8-9 分钟才变化），而数值本身在快速变化。故 UA SourceTimestamp
                // 必须以网关自身时钟为基准，而非 DA 源戳。DataBridge 现已统一以网关接收时刻(UTC)
                // 作为 sourceTimestamp 传入（见 DataBridge.OnDaDataChanged 注释），此处以其为基准
                // 并强制单调递增：
                //   1) 不低于传入的网关接收 UTC（实时推进，杜绝冻结）；
                //   2) 不低于上一轮戳 +1 tick（绝对单调，杜绝回退/重复，驱动 UA 变化检测）。
                // 二者取最大，既符合 OPC UA 对 SourceTimestamp 为 UTC 的规范，
                // 又保证每次送达都被识别为新数据，bool 翻转标签即可正常上送。
                // 注：该 recvUtc 与 MainForm 监控快照所用时间戳为同一值，故两侧时间戳严格一致。
                DateTime srcUtc = sourceTimestamp.ToUniversalTime();
                DateTime lastTs = variable.Timestamp;
                DateTime ts = srcUtc;
                if (lastTs.AddTicks(1) > ts) ts = lastTs.AddTicks(1);
                variable.Timestamp = ts;

                // P2 修复：使用 Bad 而非 Uncertain 表示坏质量数据。
                variable.StatusCode = isGood ? StatusCodes.Good : StatusCodes.Bad;
            }
            variable.ClearChangeMasks(SystemContext, false);
        }

        /// <summary>
        /// 将 BuiltInType 枚举映射为对应的 UA 标准 DataType NodeId。
        /// P0-2 重构：委托给 DataTypeConverter 单一真源。
        /// </summary>
        private static NodeId GetDataTypeId(BuiltInType type)
            => Models.DataTypeConverter.GetDataTypeNodeId(type);

        /// <summary>
        /// 为指定数据类型生成安全的默认值，用于变量节点创建时的初始赋值。
        /// P0-2 重构：委托给 DataTypeConverter 单一真源。
        /// </summary>
        private static object GetDefaultValue(BuiltInType type)
            => Models.DataTypeConverter.GetDefaultValue(type);

        /// <summary>当前已注册的变量节点数量（供诊断和 UI 查询）。</summary>
        public int VariableCount => _variableCache.Count;

        /// <summary>当前已注册的文件夹数量（供诊断查询）。</summary>
        public int FolderCount => _folderCache.Count;

        /// <summary>当前命名空间 URI（如 http://yourcompany.com/UA/Gateway）。</summary>
        public string NamespaceUri => _namespaceUri;

        /// <summary>
        /// DaTags 根文件夹的直接子对象数量（通过 SDK GetChildren 实时计算，用于验证浏览可见性）。
        /// 返回 -1 表示 DaTags 文件夹未创建。
        /// </summary>
        public int DaTagsChildrenCount => _daTagsFolder != null ? GetFolderChildrenCount(_daTagsFolder) : -1;

        /// <summary>
        /// FlatTags/Batch_000 批量子文件夹的直接子对象数量
        /// H-24-1: 检测 Batch_000 子文件夹的 GetChildren 数量。
        /// </summary>
        public int FlatTagsChildrenCount
        {
            get
            {
                lock (Lock)
                {
                    if (!_folderCache.TryGetValue("FlatBatch:0", out var batchFolder))
                    {
                        if (!_folderCache.TryGetValue("FlatTags", out batchFolder))
                            return -1;
                    }
                    return GetFolderChildrenCount(batchFolder);
                }
            }
        }

        /// <summary>
        /// FlatTags 的 INodeBrowser 引用计数（模拟 UA 客户端 Browse 行为）。
        /// H-24-1: 检测第一个 Batch 文件夹的 Browse 引用数，验证分组方案是否修复可见性。
        /// </summary>
        public int FlatTagsBrowseCount
        {
            get
            {
                lock (Lock)
                {
                    // 优先检查 Batch_000 文件夹
                    if (!_folderCache.TryGetValue("FlatBatch:0", out var batchFolder))
                    {
                        // 回退到旧的 FlatTags 键
                        if (!_folderCache.TryGetValue("FlatTags", out batchFolder))
                            return -1;
                    }
                    return GetFolderBrowseCount(batchFolder);
                }
            }
        }

        /// <summary>
        /// 辅助方法：用 INodeBrowser 统计文件夹的浏览引用数（模拟 UA Client Browse）。
        /// </summary>
        private int GetFolderBrowseCount(FolderState folder)
        {
            try
            {
                var browser = folder.CreateBrowser(
                    SystemContext,
                    null,                   // view
                    null,                   // referenceTypeId (all)
                    true,                   // includeSubtypes
                    BrowseDirection.Forward,
                    null,                   // nodeClassMask (all)
                    null,                   // additional references
                    false);                 // no external references
                int count = 0;
                for (IReference r = browser.Next(); r != null; r = browser.Next())
                    count++;
                return count;
            }
            catch
            {
                return -1;
            }
        }

        /// <summary>
        /// 辅助方法：获取给定文件夹的 GetChildren 子对象数量。
        /// 内部使用，已持有 Lock。
        /// </summary>
        private int GetFolderChildrenCount(FolderState folder)
        {
            try
            {
                var children = new List<BaseInstanceState>();
                folder.GetChildren(SystemContext, children);
                return children.Count;
            }
            catch
            {
                return -1;
            }
        }

        /// <summary>
        /// 诊断日志辅助方法：如果 OnDiagnostics 已有订阅者，直接发送；
        /// 否则将消息存入早期缓冲队列，待 SubscribeDiagnostics 被调用时回放。
        /// 这样无论是 CreateAddressSpace（事件订阅前）还是 AddVariableNode（事件订阅后）
        /// 的诊断消息都不会丢失。
        /// M2 修复（V2.6.0）：原方法标记 [Conditional("DEBUG")]，导致 Release 下节点
        /// 超限被拒绝（数据保护逻辑）静默无日志。现去掉 Conditional，诊断消息统一
        /// 走 OnDiagnostics → SubscribeDiagnostics → OnStatusChanged 通道，Release 下
        /// 也可见。纯性能追踪类消息可改用其他机制。
        /// </summary>
        private void Diag(string message)
        {
            if (OnDiagnostics != null)
                OnDiagnostics.Invoke(message);
            else
                _earlyDiagnostics.Add(message);
        }

        /// <summary>
        /// 订阅诊断事件并回放早期缓冲的消息。
        /// 必须在服务器启动后（CreateAddressSpace 已执行）调用，
        /// 确保 CreateAddressSpace 期间产生的诊断信息不会丢失。
        /// </summary>
        /// <param name="handler">诊断消息处理程序。</param>
        public void SubscribeDiagnostics(Action<string> handler)
        {
            OnDiagnostics += handler;

            // 回放早期缓冲的诊断消息
            foreach (string msg in _earlyDiagnostics)
                handler(msg);
            _earlyDiagnostics.Clear();
        }
    }

    // ================================================================
    //  自定义 StandardServer —— 注入自定义节点管理器
    // ================================================================

    /// <summary>
    /// 自定义 UA StandardServer，通过重写 CreateMasterNodeManager 注入 GatewayNodeManager。
    /// 这样 UA SDK 在初始化地址空间时会自动使用我们的节点管理器来管理桥接变量。
    /// </summary>
    public class GatewayServer : StandardServer
    {
        private GatewayNodeManager _gatewayNodeManager;
        private readonly string _namespaceUri;

        /// <summary>
        /// 创建自定义 UA 服务器实例。
        /// </summary>
        /// <param name="namespaceUri">桥接变量的命名空间 URI。</param>
        public GatewayServer(string namespaceUri)
        {
            _namespaceUri = namespaceUri;
        }

        /// <summary>
        /// 重写 MasterNodeManager 创建逻辑，注册自定义的 GatewayNodeManager。
        /// 由 UA SDK 在 StartAsync 时自动调用。
        /// </summary>
        protected override MasterNodeManager CreateMasterNodeManager(IServerInternal server, ApplicationConfiguration configuration)
        {
            _gatewayNodeManager = new GatewayNodeManager(server, configuration, _namespaceUri);
            var nodeManagers = new List<INodeManager> { _gatewayNodeManager };
            // H-20 修复：将自定义命名空间 URI 作为 dynamicNamespaceUri 传入，
            // 确保 MasterNodeManager 的命名空间路由表能正确将 Browse 请求路由到 GatewayNodeManager。
            // 之前传 null 可能导致路由表初始化不完整，UA 客户端无法浏览到自定义节点。
            return new MasterNodeManager(server, configuration, _namespaceUri, nodeManagers.ToArray());
        }

        /// <summary>
        /// 获取自定义节点管理器的引用，用于外部添加变量节点和更新值。
        /// </summary>
        public GatewayNodeManager GatewayNodeManager => _gatewayNodeManager;
    }

    // ================================================================
    //  OPC UA 服务器封装（对外接口）
    // ================================================================

    /// <summary>
    /// OPC UA 服务器封装类，对外提供启动、停止、添加变量、更新值等接口。
    /// 内部封装了 ApplicationInstance（UA SDK 配置/证书管理）和 GatewayServer（自定义节点管理器）。
    /// 
    /// Dispose 模式说明：
    /// - 使用 Interlocked&lt;int&gt; 实现幂等释放（与 OpcDaClient 的 _disposedInt 同理）。
    /// - Dispose 内部通过 Task.Run 在线程池线程上执行异步停止操作，避免在 UI 线程上
    ///   同步等待异步方法导致 SynchronizationContext 死锁（C-06 修复）。
    /// - 之所以不能直接调用 StopAsync().GetAwaiter().GetResult()，是因为 UI 线程的
    ///   SynchronizationContext 会将 continuation 调度回 UI 线程，而 UI 线程正在阻塞等待，
    ///   形成死锁。Task.Run 绕过了 SynchronizationContext 的捕获。
    /// </summary>
    public class GatewayOpcUaServer : IGatewayOpcUaServer
    {
        private ApplicationInstance _appInstance;
        private GatewayServer _server;
        private readonly OpcUaConfig _uaConfig;
        private readonly string _baseDirectory;

        // H-05 修复：原子启动标志，防止并发调用 StartAsync 时两个线程都能通过
        // volatile bool 检查然后各自创建 GatewayServer 实例（端口冲突+旧实例泄漏）。
        private int _startingFlag;

        // H1 修复：volatile 确保多线程可见性（与 GatewayManager.IsRunning 保持一致）。
        // 用于 StartAsync/StopAsync/Dispose 之间的状态协调。
        private volatile bool _isRunning;
        public bool IsRunning => _isRunning;

        /// <summary>状态变化事件，用于向 UI 层报告启动/停止/错误等信息。</summary>
        public event Action<string> OnStatusChanged;

        /// <summary>配置变更回调 — 当 NamespaceIndex 等关键参数被回写后立即触发，供上层调用 ConfigManager.Save()。</summary>
        public Action OnConfigChanged { get; set; }

        /// <summary>
        /// 创建 UA 服务器实例（尚未启动，需调用 StartAsync）。
        /// </summary>
        /// <param name="uaConfig">UA 服务器配置（端口、安全策略、命名空间等）。</param>
        public GatewayOpcUaServer(OpcUaConfig uaConfig)
        {
            _uaConfig = uaConfig ?? throw new ArgumentNullException(nameof(uaConfig));
            _baseDirectory = AppDomain.CurrentDomain.BaseDirectory;
        }

        /// <summary>
        /// 异步启动 OPC UA 服务器，包括：配置构建 → 证书目录创建 → 证书检查/生成 → 启动监听。
        /// 
        /// 重复启动守卫（H-12 修复）：
        /// 如果服务器已在运行中，直接返回而不会创建新的 Server 实例。
        /// 这是因为重复启动会导致旧的 Server 实例泄漏（旧实例仍在监听端口但引用已丢失），
        /// 新实例会因为端口冲突而启动失败。
        /// </summary>
        public async Task StartAsync()
        {
            // H-05 修复："检查-然后-设置"不是原子操作，两个并发调用者都能通过 _isRunning 检查
            // 然后各自创建 GatewayServer 实例，导致端口冲突或旧实例泄漏。
            // 使用 Interlocked.CompareExchange 原子标志保护启动入口（与 GatewayManager._startingFlag 同模式）。
            if (Interlocked.CompareExchange(ref _startingFlag, 1, 0) != 0) return;
            if (_isRunning)
            {
                Interlocked.Exchange(ref _startingFlag, 0);
                return;
            }

            try
            {
                string serverName = _uaConfig.ServerName ?? "OpcDaToUaGateway";
                int port = _uaConfig.Port;
                string listenAddress = _uaConfig.GetEffectiveListenAddress();
                MessageSecurityMode secMode = _uaConfig.GetEffectiveSecurityMode();
                string secPolicy = _uaConfig.GetEffectiveSecurityPolicy();
                bool autoAccept = _uaConfig.GetEffectiveAutoAcceptCertificates();
                int maxSessions = _uaConfig.GetEffectiveMaxSessionCount();
                int sessionTimeout = _uaConfig.GetEffectiveSessionTimeout();

                OnStatusChanged?.Invoke("正在配置 OPC UA 服务器...");

                // 创建证书目录结构（UA SDK 要求目录预先存在）。
                string certStorePath = Path.Combine(_baseDirectory, "Certificates");
                string ownStorePath = Path.Combine(certStorePath, "Own");
                string trustedStorePath = Path.Combine(certStorePath, "Trusted");
                string rejectedStorePath = Path.Combine(certStorePath, "Rejected");
                string issuerStorePath = Path.Combine(certStorePath, "Issuers");

                Directory.CreateDirectory(ownStorePath);
                Directory.CreateDirectory(trustedStorePath);
                Directory.CreateDirectory(rejectedStorePath);
                Directory.CreateDirectory(issuerStorePath);

                var securityPolicies = new ServerSecurityPolicyCollection();

                // 仅当用户选择 None 时才添加无加密策略。
                // 非 None 模式下添加对应的安全策略（如 Sign / SignAndEncrypt），
                // 避免无意中暴露无加密端口。
                if (secMode == MessageSecurityMode.None)
                {
                    securityPolicies.Add(new ServerSecurityPolicy
                    {
                        SecurityMode = MessageSecurityMode.None,
                        SecurityPolicyUri = SecurityPolicies.None
                    });
                }
                else
                {
                    securityPolicies.Add(new ServerSecurityPolicy
                    {
                        SecurityMode = secMode,
                        SecurityPolicyUri = secPolicy
                    });
                    OnStatusChanged?.Invoke($"  安全策略: {secMode} / {secPolicy}");
                }

                string endpointUrl = $"opc.tcp://{listenAddress}:{port}/{serverName}";

                // 证书 SubjectName 使用实际监听地址而非硬编码 localhost。
                // 当监听 0.0.0.0 时使用机器名，否则使用实际绑定地址。
                string certHost = listenAddress == "0.0.0.0"
                    ? Environment.MachineName
                    : listenAddress;

                // ApplicationUri/ProductUri 使用实际主机名，而非硬编码 localhost。
                // UA 客户端在验证服务器证书时会比对 ApplicationUri 中的主机名。
                string uriHost = listenAddress == "0.0.0.0"
                    ? Environment.MachineName
                    : listenAddress;

                var config = new ApplicationConfiguration
                {
                    ApplicationName = serverName,
                    ApplicationType = ApplicationType.Server,
                    ApplicationUri = $"urn:{uriHost}:{serverName}",
                    ProductUri = $"http://{uriHost}/{serverName}",

                    SecurityConfiguration = new SecurityConfiguration
                    {
                        ApplicationCertificate = new CertificateIdentifier
                        {
                            StoreType = CertificateStoreType.Directory,
                            StorePath = ownStorePath,
                            SubjectName = $"CN={serverName}, C=CN, O=Gateway, DC={certHost}"
                        },
                        TrustedIssuerCertificates = new CertificateTrustList
                        {
                            StoreType = CertificateStoreType.Directory,
                            StorePath = issuerStorePath
                        },
                        TrustedPeerCertificates = new CertificateTrustList
                        {
                            StoreType = CertificateStoreType.Directory,
                            StorePath = trustedStorePath
                        },
                        RejectedCertificateStore = new CertificateTrustList
                        {
                            StoreType = CertificateStoreType.Directory,
                            StorePath = rejectedStorePath
                        },
                        AutoAcceptUntrustedCertificates = autoAccept
                    },

                    TransportConfigurations = new TransportConfigurationCollection(),
                    TransportQuotas = new TransportQuotas { OperationTimeout = 30000 },

                    ServerConfiguration = new ServerConfiguration
                    {
                        BaseAddresses = new StringCollection { endpointUrl },
                        SecurityPolicies = securityPolicies,
                        MinRequestThreadCount = 5,
                        MaxRequestThreadCount = 200,
                        MaxQueuedRequestCount = 500,
                        MaxSessionCount = maxSessions,
                        MinSessionTimeout = 10000,
                        MaxSessionTimeout = sessionTimeout
                    },

                    TraceConfiguration = new TraceConfiguration()
                };

                OnStatusChanged?.Invoke($"  监听地址: {endpointUrl}");
                OnStatusChanged?.Invoke($"  自动接受证书: {(autoAccept ? "是" : "否")}");
                OnStatusChanged?.Invoke($"  最大会话数: {maxSessions}");

                await config.ValidateAsync(ApplicationType.Server);

                _appInstance = new ApplicationInstance(config, null);

                OnStatusChanged?.Invoke("正在检查/创建应用证书...");

                // 检查应用证书是否存在且有效，如果不存在则自动生成自签名证书。
                bool haveCert = await _appInstance.CheckApplicationInstanceCertificatesAsync(false);
                if (!haveCert)
                    throw new Exception("无法创建或加载 OPC UA 应用证书，请检查 Certificates 目录权限");

                OnStatusChanged?.Invoke("正在启动 OPC UA 服务器...");

                _server = new GatewayServer(_uaConfig.NamespaceUri);
                await _appInstance.StartAsync(_server);

                // 将节点管理器的诊断日志转发到 OnStatusChanged，使所有诊断信息可见。
                // 使用 SubscribeDiagnostics 回放 CreateAddressSpace 期间的早期消息。
                if (_server.GatewayNodeManager != null)
                {
                    _server.GatewayNodeManager.SubscribeDiagnostics(msg => OnStatusChanged?.Invoke(msg));
                    OnStatusChanged?.Invoke($"[诊断] 节点管理器已就绪 — 命名空间数: {_server.GatewayNodeManager.NamespaceIndexes?.Count ?? 0}, " +
                        $"变量节点: {_server.GatewayNodeManager.VariableCount}, 文件夹: {_server.GatewayNodeManager.FolderCount}");
                }

                // H-21 验证：输出命名空间索引，确认地址空间路由是否正确
                // 如果 NamespaceIndex 为 0，说明自定义命名空间未正确注册（bug）
                // UA 客户端将无法浏览到所有自定义节点
                ushort nsIdx = _server.GatewayNodeManager?.NamespaceIndex ?? 0;
                int varCount = _server.GatewayNodeManager?.VariableCount ?? 0;
                int folderCount = _server.GatewayNodeManager?.FolderCount ?? 0;
                OnStatusChanged?.Invoke($"  地址空间: 命名空间索引={nsIdx}, 文件夹={folderCount}, 变量={varCount} (待桥接注册)");
                if (nsIdx == 0)
                {
                    OnStatusChanged?.Invoke("  ⚠ 命名空间索引为 0，自定义节点可能不可见！");
                }

                _isRunning = true;

                // 将命名空间索引回写配置，确保 UA 服务器未运行时也能导出正确的 NodeId
                _uaConfig.NamespaceIndex = nsIdx;
                // N-5: 立即触发持久化，不依赖 500ms 防抖定时器，防止进程崩溃导致索引丢失
                OnConfigChanged?.Invoke();

                OnStatusChanged?.Invoke($"OPC UA 服务器已启动: {endpointUrl}");
            }
            catch (Exception ex)
            {
                _isRunning = false;
                OnStatusChanged?.Invoke($"启动 UA 服务器失败: {ex.Message}");
                throw;
            }
            finally
            {
                // H-05 修复：无论成功或失败都重置启动标志，允许下次重试
                Interlocked.Exchange(ref _startingFlag, 0);
            }
        }

        /// <summary>
        /// 在 UA 地址空间中创建一个变量节点。服务器必须已启动。
        /// </summary>
        /// <param name="tagKey">业务标签键。</param>
        /// <param name="itemId">OPC DA ItemId（用于生成文件夹层级）。</param>
        /// <param name="displayName">UA 客户端可见的显示名称。</param>
        /// <param name="dataType">UA 内置数据类型。</param>
        /// <param name="nodeId">预分配的 NodeId 标识符（空值时自动生成）。</param>
        /// <exception cref="InvalidOperationException">服务器未启动时抛出。</exception>
        public void AddVariableNode(string tagKey, string itemId, string displayName, BuiltInType dataType, string nodeId = null)
        {
            if (_server?.GatewayNodeManager == null)
                throw new InvalidOperationException("服务器未启动");

            _server.GatewayNodeManager.AddVariableNode(tagKey, itemId, displayName, dataType, nodeId);
        }

        /// <summary>
        /// 更新 UA 地址空间中某个变量节点的值。服务器未启动时静默返回。
        /// </summary>
        /// <param name="tagKey">业务标签键（必须与 AddVariableNode 时传入的一致）。</param>
        /// <param name="value">新的数据值。</param>
        /// <param name="isGood">OPC DA 质量位是否为 Good。</param>
        /// <param name="sourceTimestamp">数据源时间戳。</param>
        public void UpdateValue(string tagKey, object value, bool isGood, DateTime sourceTimestamp)
        {
            if (_server?.GatewayNodeManager == null)
                return;

            _server.GatewayNodeManager.UpdateValue(tagKey, value, isGood, sourceTimestamp);
        }

        /// <summary>当前已注册的 UA 变量节点数量（供 DataBridge 验证）。</summary>
        public int VariableCount => _server?.GatewayNodeManager?.VariableCount ?? 0;

        /// <summary>当前命名空间索引（供诊断验证）。</summary>
        public ushort NamespaceIndex => _server?.GatewayNodeManager?.NamespaceIndex ?? 0;

        /// <summary>当前命名空间 URI（如 http://yourcompany.com/UA/Gateway）。</summary>
        public string NamespaceUri => _server?.GatewayNodeManager?.NamespaceUri ?? _uaConfig?.NamespaceUri ?? "";

        /// <summary>DaTags 根文件夹的直接子对象数（通过引用表计算，用于验证 UA 客户端浏览可见性）。</summary>
        public int DaTagsChildrenCount => _server?.GatewayNodeManager?.DaTagsChildrenCount ?? -1;

        /// <summary>FlatTags 中间文件夹的直接子对象数（用于诊断无分支场景下变量节点引用是否建立）。</summary>
        public int FlatTagsChildrenCount => _server?.GatewayNodeManager?.FlatTagsChildrenCount ?? -1;

        /// <summary>FlatTags 的 INodeBrowser 引用计数（模拟 UA Browse，H-24-1 诊断新增）。</summary>
        public int FlatTagsBrowseCount => _server?.GatewayNodeManager?.FlatTagsBrowseCount ?? -1;

        /// <summary>
        /// 异步停止 OPC UA 服务器。停止失败时也会将 _isRunning 设为 false 并记录错误。
        /// </summary>
        public async Task StopAsync()
        {
            // H-06 修复：Dispose 过程中 _server 可能已被置 null，直接跳过。
            // 使用 Volatile.Read 确保读取到其他线程写入的最新值，避免 JIT 寄存器缓存。
            if (Volatile.Read(ref _disposedInt) == 1) return;

            try
            {
                if (_server != null)
                    await _server.StopAsync();
                _isRunning = false;
                OnStatusChanged?.Invoke("OPC UA 服务器已停止");
            }
            catch (Exception ex)
            {
                _isRunning = false;
                OnStatusChanged?.Invoke($"停止 UA 服务器时出错: {ex.Message}");
            }
        }

        // H-03 修复：原子 Disposed 守护，确保幂等释放。
        // 使用 Interlocked<int> 而非 volatile bool，原因与 OpcDaClient._disposedInt 相同：
        // 需要 compare-and-swap 原子语义来防止并发 Dispose 导致双重释放。
        private int _disposedInt;

        /// <summary>
        /// 释放 UA 服务器资源，实现 IDisposable。
        /// 
        /// P2 修复：实现 IDisposable，释放服务器资源。自动停止运行中的服务器。
        /// C-06 修复：避免在 UI 线程同步阻塞异步调用导致死锁。
        ///   - Dispose 通常是同步方法（由 using 语句或 GC 调用），
        ///     而 _server.StopAsync() 是异步方法。
        ///   - 如果在 UI 线程上直接 .GetAwaiter().GetResult()，异步方法的 continuation
        ///     会被 SynchronizationContext.Post 调度回 UI 线程，但 UI 线程正在阻塞等待，
        ///     形成经典死锁。
        ///   - 解决方案：通过 Task.Run 将异步调用分派到线程池线程执行，
        ///     线程池没有 SynchronizationContext，continuation 直接在线程池线程上完成。
        /// H-03 修复：幂等释放，防止并发 Dispose 双重释放。
        /// </summary>
        public void Dispose()
        {
            // H-03 修复：确保只有一个线程执行释放。
            if (Interlocked.Exchange(ref _disposedInt, 1) == 1) return;

            if (_server != null)
            {
                // C-06 修复：通过 Task.Run 在线程池线程执行异步停止，
                // 避免 SynchronizationContext 捕获导致 UI 线程死锁。
                if (_isRunning)
                {
                    _isRunning = false;
                    // H-31 修复：添加 10 秒超时 + ConfigureAwait(false)，防止 StopAsync 永久挂起时
                    //        Dispose() 无限阻塞，导致进程无法正常退出。
                    try
                    {
                        // H-04 修复：Task.Run(() => asyncMethod()) 对 ValueTask 不适用 Unwrap。
                        // 改用 async lambda 确保 await 完整执行异步停止链。
                        var stopTask = Task.Run(async () => await _server.StopAsync());
                        if (!stopTask.Wait(10000))
                        {
                            // 超时后不抛异常，直接进入 finally 块的 Dispose 清理
                        }
                    }
                    catch { }
                }
                try { _server.Dispose(); } catch { }
                _server = null;
            }
            _appInstance = null;
        }

        /// <summary>
        /// 将配置文件中指定的数据类型名称解析为 UA BuiltInType 枚举。
        /// P0-2 重构：委托给 DataTypeConverter 单一真源。
        /// </summary>
        /// <param name="typeName">数据类型名称（大小写不敏感）。</param>
        /// <returns>对应的 BuiltInType 枚举值。</returns>
        public static BuiltInType ParseDataType(string typeName)
            => Models.DataTypeConverter.ParseDataType(typeName);

        /// <summary>
        /// 根据 OPC DA ItemId 计算对应的 UA 浏览路径。
        /// N-3 修复：从 MainForm.cs 移至业务层，去耦合 UI 依赖。
        /// </summary>
        public static string ComputeUaBrowsePath(string itemId, string displayName)
        {
            if (string.IsNullOrEmpty(itemId)) return "Objects/OPC DA Tags/" + displayName;
            int lastDot = itemId.LastIndexOf('.');
            if (lastDot > 0)
                return $"Objects/OPC DA Tags/{itemId.Substring(0, lastDot).Replace('.', '/')}/{itemId}";
            return $"Objects/OPC DA Tags/Flat Tags/{itemId}";
        }
    }
}
