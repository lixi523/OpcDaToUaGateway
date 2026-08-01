using System;
using System.Collections.Generic;
using System.Linq;
using Opc.Ua;

namespace OpcDaToUaGateway.Models
{
    // =====================================================================
    // 配置模型类总览
    // =====================================================================
    //
    // 本文件定义了网关的全部配置模型，对应 config.json 的结构：
    //
    //   AppConfig              —— 顶层配置，包含 DA/UA 子配置和全局选项
    //   ├── OpcDaConfig        —— OPC DA 连接配置（服务器地址、刷新频率、标签列表）
    //   │   └── TagConfig      —— 单个 DA 标签的配置（ItemId、显示名、数据类型）
    //   └── OpcUaConfig        —— OPC UA 服务器配置（端口、安全模式、会话限制等）
    //
    // 配置通过 System.Text.Json 反序列化，属性名与 JSON key 一一对应。
    // GetEffective* 方法为每个可选配置项提供带默认值的安全访问入口，
    // 避免各处散落 null/0 值判断逻辑。
    // =====================================================================

    /// <summary>
    /// 单个 OPC DA 标签的配置，对应 config.json 中 OpcDa.Tags 数组的每一项。
    /// 
    /// <para>一个 TagConfig 描述了从 OPC DA 服务器读取的一个数据点，
    /// 以及它在 OPC UA 网关服务器中对应的变量节点。</para>
    /// </summary>
    public class TagConfig
    {
        /// <summary>
        /// OPC DA 标签的 ItemId（如 "Random.Int32"），
        /// 是 DA 服务器内部用于标识数据点的唯一字符串。
        /// </summary>
        public string ItemId { get; set; }

        /// <summary>在 OPC UA 地址空间中显示的名称，为空时回退到 <see cref="ItemId"/>。</summary>
        public string DisplayName { get; set; }

        /// <summary>
        /// 数据类型，可选值：Boolean, Int16, Int32, UInt16, UInt32, Float, Double, String, DateTime。
        /// 决定了 <see cref="DataBridge"/> 在转发数据时如何做类型转换。
        /// </summary>
        public string DataType { get; set; }

        /// <summary>
        /// 运行时内部唯一标识，用于在 <see cref="DataBridge"/> 和 <see cref="GatewayOpcUaServer"/>
        /// 中索引标签。持久化到配置文件，加载后不再重新分配。
        /// 
        /// <para>由 <see cref="AssignTagKeys"/> 在加载/选择标签后统一分配：
        /// - 若所有 ItemId 唯一，TagKey = ItemId；
        /// - 若存在重复 ItemId，所有 TagKey 统一使用 "索引_ItemId" 格式，
        ///   确保同一 ItemId 出现在多个 UA 节点时仍可区分。</para>
        /// 
        /// <para>N-8 持久化：AssignTagKeys 现在仅对 TagKey 为 null/empty 的标签分配，
        /// 已有 TagKey 的标签保持不变。新分配的 Key 会通过 ConfigManager 立即写入磁盘。</para>
        /// </summary>
        public string TagKey { get; set; }

        /// <summary>
        /// 预分配的 OPC UA NodeId 标识符（如 "DaTag_Channel1_Device0_Status"）。
        /// 
        /// <para>在点位浏览窗口"确定导入"时与 TagKey 一同生成并持久化到配置文件，
        /// 启动网关时 <see cref="GatewayNodeManager.AddVariableNode"/> 直接使用此值
        /// 构建完整的 NodeId（命名空间索引 + 此标识符），无需启动时重复计算。</para>
        /// 
        /// <para>空值兼容：旧配置文件或手动创建的标签可能没有此字段，
        /// GatewayNodeManager 在 nodeId 参数为空时回退到 "DaTag_{TagKey}" 自动生成。</para>
        /// </summary>
        public string UaNodeId { get; set; }

        /// <summary>
        /// 为标签列表中尚未分配 TagKey 的标签分配唯一标识。
        /// 
        /// <para>N-8 修改：现在仅对 TagKey 为 null 或空字符串的标签进行分配，
        /// 已持久化 TagKey 的标签保持不变。返回值指示是否进行了新的分配。
        /// 分配策略与之前一致：
        /// - 如果所有 ItemId 都唯一，TagKey 直接等于 ItemId（最简洁）；
        /// - 如果存在重复的 ItemId（同一 DA 标签映射到多个 UA 节点），
        ///   所有标签统一使用 "列表索引_ItemId" 格式（如 "3_Random.Int32"），
        ///   确保 TagKey 全局唯一。</para>
        /// 
        /// <para>此方法必须在标签列表确定后、创建 <see cref="DataBridge"/> 之前调用。</para>
        /// </summary>
        /// <param name="tags">需要检查并分配 TagKey 的标签列表。</param>
        /// <returns>如果有任何新 TagKey 被分配则返回 true，调用方应触发持久化。</returns>
        public static bool AssignTagKeys(List<TagConfig> tags)
        {
            if (tags == null || tags.Count == 0) return false;

            // N-8: 先收集需要分配 TagKey 的标签（仅当 TagKey 为空时）
            var unassigned = new List<(int index, TagConfig tag)>();
            for (int i = 0; i < tags.Count; i++)
            {
                if (string.IsNullOrEmpty(tags[i].TagKey))
                    unassigned.Add((i, tags[i]));
            }

            if (unassigned.Count == 0) return false;

            // 统计每个 ItemId 出现的次数，用于判断是否存在重复
            var counts = tags.GroupBy(t => t.ItemId ?? "").ToDictionary(g => g.Key, g => g.Count());

            // 使用原始列表索引（i）而非 unassigned 的局部索引，
            // 确保索引含义一致：TagKey 中的数字 = 列表中的位置
            foreach (var (i, tag) in unassigned)
            {
                string itemId = tag.ItemId ?? "";
                if (counts[itemId] == 1)
                    tag.TagKey = itemId;
                else
                    tag.TagKey = $"{i}_{itemId}";
            }

            return true;
        }
    }

    /// <summary>
    /// OPC DA 数据获取方式。
    /// Async = 异步订阅（服务器主动推送，经 ValuesChanged 回调）；
    /// Sync  = 同步轮询（网关按刷新频率定时 group.Read 主动拉取）。
    /// 默认 Async，与历史行为一致。
    /// </summary>
    public enum DaAcquisitionMode
    {
        /// <summary>异步订阅：依赖 OPC DA 服务器的数据变化回调推送。</summary>
        Async,
        /// <summary>同步轮询：网关定时主动读取，不依赖服务器回调。</summary>
        Sync
    }

    /// <summary>
    /// OPC DA 连接配置，对应 config.json 中的 "OpcDa" 节点。
    /// 包含 DA 服务器的连接信息和要读取的标签列表。
    /// </summary>
    public class OpcDaConfig
    {
        /// <summary>OPC DA 服务器的 ProgId（如 "Matrikon.OPC.Simulation.1"），用于 COM 注册表查找。</summary>
        public string ServerProgId { get; set; }

        /// <summary>
        /// OPC DA 服务器所在主机名。
        /// 默认 localhost 表示本机服务器；设为远程主机名时通过 DCOM 连接。
        /// </summary>
        public string ServerHost { get; set; }

        /// <summary>数据刷新频率（毫秒），决定 DA 客户端轮询或订阅的时间间隔。</summary>
        public int UpdateRateMs { get; set; }

        /// <summary>
        /// 数据获取方式：Async(异步订阅推送) / Sync(同步轮询拉取)。
        /// 仅作字符串存储，实际解析经 <see cref="GetEffectiveMode"/>，
        /// 容错笔误/缺失（未知值统一回退 Async）。
        /// </summary>
        public string Mode { get; set; }

        /// <summary>
        /// 获取有效的数据获取模式。
        /// 当 <see cref="Mode"/> 为 "Sync"(不区分大小写) 时返回 <see cref="DaAcquisitionMode.Sync"/>，
        /// 其余（空值、无法识别）一律回退 <see cref="DaAcquisitionMode.Async"/>，
        /// 确保配置文件缺失该字段或笔误时不会中断网关启动。
        /// </summary>
        public DaAcquisitionMode GetEffectiveMode()
        {
            if (string.Equals(Mode, "Sync", StringComparison.OrdinalIgnoreCase))
                return DaAcquisitionMode.Sync;
            return DaAcquisitionMode.Async;
        }

        /// <summary>
        /// 要读取的标签列表。
        /// P5 修复：默认初始化为空列表而非 null，避免在序列化、UI 绑定和遍历时的各处空值检查。
        /// </summary>
        public List<TagConfig> Tags { get; set; } = new List<TagConfig>();

        /// <summary>
        /// DA 连接断开后的最大自动重连次数（默认 50，非正数时使用默认值）。
        /// M6 修复：从 GatewayManager 硬编码移至配置项，允许不同部署环境自定义。
        /// </summary>
        public int MaxReconnectAttempts { get; set; }
    }

    /// <summary>
    /// OPC UA 服务器配置，对应 config.json 中的 "OpcUa" 节点。
    /// 
    /// <para>包含 UA 服务器的网络监听、安全策略、会话管理等方面的配置。
    /// 每个可选配置项都提供了 GetEffective* 方法，返回经过默认值填充后的安全值，
    /// 避免使用者需要自行处理 null 或 0 值的情况。</para>
    /// </summary>
    public class OpcUaConfig
    {
        /// <summary>OPC UA 服务器名称，用于端点 URL 和服务器描述。</summary>
        public string ServerName { get; set; }

        /// <summary>监听端口（默认 4840，OPC UA 标准端口）。</summary>
        public int Port { get; set; }

        /// <summary>自定义命名空间 URI，用于 UA 地址空间中的变量节点。</summary>
        public string NamespaceUri { get; set; }

        /// <summary>
        /// 监听地址：localhost 仅允许本机访问，0.0.0.0 允许远程访问。
        /// 默认 localhost（安全优先）。
        /// </summary>
        public string ListenAddress { get; set; }

        /// <summary>
        /// 安全模式：None / Sign / SignAndEncrypt。
        /// 默认 None（无安全）。生产环境建议使用 SignAndEncrypt。
        /// </summary>
        public string SecurityMode { get; set; }

        /// <summary>
        /// 安全策略 URI：None / Basic256Sha256 / Basic128Rsa15 / Basic256。
        /// 仅在 <see cref="SecurityMode"/> 不为 None 时生效。
        /// </summary>
        public string SecurityPolicy { get; set; }

        /// <summary>
        /// 是否自动接受不受信任的客户端证书。
        /// 默认 false（C-10 修复：安全优先），开发/测试环境可设为 true 简化连接。
        /// </summary>
        public bool AutoAcceptCertificates { get; set; }

        /// <summary>
        /// 最大并发会话数（默认 50），限制同时连接的 UA 客户端数量。
        /// </summary>
        public int MaxSessionCount { get; set; }

        /// <summary>
        /// 会话超时时间（毫秒，默认 120000 = 2 分钟），
        /// 超过此时间无活动的会话将被服务器自动关闭。
        /// </summary>
        public int SessionTimeout { get; set; }

        // =====================================================================
        // GetEffective* 方法：为可选配置项提供带默认值的安全访问入口
        // =====================================================================
        // 设计意图：将"配置值是否有效"的判断集中在此处，
        // 而非分散在使用配置的各个位置，减少重复的空值/零值检查代码。
        // =====================================================================

        /// <summary>
        /// 获取有效的监听地址。
        /// 当 <see cref="ListenAddress"/> 为空或仅包含空白时，返回默认值 "localhost"。
        /// </summary>
        /// <returns>经过 trim 处理的监听地址字符串。</returns>
        public string GetEffectiveListenAddress()
            => string.IsNullOrWhiteSpace(ListenAddress) ? "localhost" : ListenAddress.Trim();

        /// <summary>
        /// 获取完整的 OPC UA 端点 URL（如 opc.tcp://localhost:4840/MyServer）。
        /// 
        /// <para>H4 修复：当 <see cref="Port"/> 为 0 或负数时，使用 OPC UA 标准端口 4840，
        /// 避免拼出无效的端口号。</para>
        /// </summary>
        /// <returns>完整的 opc.tcp:// 端点 URL。</returns>
        public string GetEndpointUrl()
        {
            string host = GetEffectiveListenAddress();
            // H4 修复：Port 为 0 或负数时使用默认值 4840（OPC UA 标准端口）
            int port = Port > 0 ? Port : 4840;
            return $"opc.tcp://{host}:{port}/{ServerName}";
        }

        /// <summary>
        /// 获取有效的安全模式枚举值。
        /// 当 <see cref="SecurityMode"/> 为空或无法识别时，返回 <see cref="MessageSecurityMode.None"/>。
        /// </summary>
        /// <returns>解析后的 <see cref="MessageSecurityMode"/> 枚举值。</returns>
        public Opc.Ua.MessageSecurityMode GetEffectiveSecurityMode()
        {
            if (string.IsNullOrEmpty(SecurityMode)) return Opc.Ua.MessageSecurityMode.None;
            switch (SecurityMode.Trim().ToLowerInvariant())
            {
                case "sign": return Opc.Ua.MessageSecurityMode.Sign;
                case "signandencrypt": return Opc.Ua.MessageSecurityMode.SignAndEncrypt;
                default: return Opc.Ua.MessageSecurityMode.None;
            }
        }

        /// <summary>
        /// 获取有效的安全策略 URI 字符串。
        /// 当 <see cref="SecurityPolicy"/> 为空或无法识别时，返回 <see cref="SecurityPolicies.None"/>。
        /// 
        /// <para>支持映射的策略名：basic256sha256、basic128rsa15、basic256。</para>
        /// </summary>
        /// <returns>OPC UA 安全策略 URI 字符串。</returns>
        public string GetEffectiveSecurityPolicy()
        {
            if (string.IsNullOrEmpty(SecurityPolicy)) return Opc.Ua.SecurityPolicies.None;
            switch (SecurityPolicy.Trim().ToLowerInvariant())
            {
                case "basic256sha256": return Opc.Ua.SecurityPolicies.Basic256Sha256;
                case "basic128rsa15": return Opc.Ua.SecurityPolicies.Basic128Rsa15;
                case "basic256": return Opc.Ua.SecurityPolicies.Basic256;
                default: return Opc.Ua.SecurityPolicies.None;
            }
        }

        /// <summary>
        /// 获取有效的最大会话数。
        /// 当 <see cref="MaxSessionCount"/> 小于等于 0 时，返回默认值 50。
        /// </summary>
        /// <returns>正整数的最大会话数。</returns>
        public int GetEffectiveMaxSessionCount()
            => MaxSessionCount > 0 ? MaxSessionCount : 50;

        /// <summary>
        /// 获取有效的会话超时时间（毫秒）。
        /// 当 <see cref="SessionTimeout"/> 小于等于 0 时，返回默认值 120000（2 分钟）。
        /// </summary>
        /// <returns>正整数的超时毫秒数。</returns>
        public int GetEffectiveSessionTimeout()
            => SessionTimeout > 0 ? SessionTimeout : 120000;

        /// <summary>
        /// 上次成功启动时的 OPC UA 命名空间索引。
        /// 运行时由网关记录，用于 UA 服务器未运行时仍能导出正确的 NodeId。
        /// 默认值 2（NS 0=OPC UA base, 1=server URI, 2=custom namespace）。
        /// </summary>
        public ushort NamespaceIndex { get; set; } = 2;

        /// <summary>
        /// 获取是否自动接受不受信任的客户端证书。
        /// 
        /// <para>C-10 修复：默认值为 false（安全优先），
        /// 仅在配置文件中显式设为 true 时才自动接受。</para>
        /// </summary>
        /// <returns>AutoAcceptCertificates 的原始布尔值。</returns>
        public bool GetEffectiveAutoAcceptCertificates()
        {
            return AutoAcceptCertificates;
        }
    }

    /// <summary>
    /// 整体应用配置，对应 config.json 的根对象。
    /// 
    /// <para>包含 OPC DA 连接配置、OPC UA 服务器配置以及全局行为选项
    /// （自动连接、自动启动、看门狗守护、授权码等）。</para>
    /// </summary>
    public class AppConfig
    {
        /// <summary>OPC DA 连接配置。</summary>
        public OpcDaConfig OpcDa { get; set; }

        /// <summary>OPC UA 服务器配置。</summary>
        public OpcUaConfig OpcUa { get; set; }

        /// <summary>最后一次成功连接的 OPC DA 服务器 ProgId，用于下次启动时自动填充。</summary>
        public string LastConnectedProgId { get; set; }

        /// <summary>程序启动时是否自动连接 OPC DA 服务器。</summary>
        public bool AutoConnectDa { get; set; }

        /// <summary>程序启动时是否自动启动 OPC UA 网关服务器。</summary>
        public bool AutoStartUa { get; set; }

        /// <summary>是否随 Windows 开机自动启动（通过启动文件夹快捷方式实现）。</summary>
        public bool AutoStartWithWindows { get; set; }

        /// <summary>是否启用看门狗进程守护（主进程崩溃时由看门狗自动重启）。</summary>
        public bool EnableWatchdog { get; set; }

        /// <summary>授权码，验证通过后保存到配置文件，下次启动时自动验证。</summary>
        public string AuthorizationCode { get; set; }
    }
}
