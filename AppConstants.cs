using System;

namespace OpcDaToUaGateway
{
    /// <summary>
    /// 应用全局常量 — 集中管理所有可调参数、Magic Numbers 和默认值。
    /// 
    /// 使用原则：
    ///   - 仅在需要跨文件共享或需要统一调优入口时引用
    ///   - 组件内部的实现细节常量（如 Win32 消息常量、COM 参数）保持在各文件内
    ///   - 所有值附带注释说明其业务含义和调优方向
    /// </summary>
    public static class AppConstants
    {
        // ══════════════════════════════════════════════════
        //  OPC DA 相关
        // ══════════════════════════════════════════════════

        /// <summary>单次 DA AddItems 的批量大小（减少 COM 往返次数）</summary>
        public const int DaAddItemBatchSize = 2000;

        /// <summary>浏览分页大小（避免 DCOM 20s 超时，与 OpcDaClient.BrowsePageSize 保持一致）</summary>
        public const int DaBrowsePageSize = 500;

        /// <summary>定时同步读取间隔 (ms) — 作为 DA 订阅的兜底补充</summary>
        public const int DaSyncIntervalMs = 300000; // 5 分钟

        /// <summary>浏览日志进度输出间隔（每收集 N 个点位输出一次）</summary>
        public const int DaBrowseProgressInterval = 2000;

        /// <summary>DA 浏览最大条目数上限</summary>
        public const int DaMaxBrowseItems = 50000;

        // H-33: 变量节点缓存上限 — 防止配置错误导致 OOM
        // 100000 个 BaseDataVariableState 约 500MB，作为安全阈值
        public const int MaxVariableNodes = 100000;

        // ══════════════════════════════════════════════════
        //  OPC UA 相关
        // ══════════════════════════════════════════════════

        /// <summary>扁平标签批量分组大小 — 每批最多 N 个变量，超过则创建新的 Batch 子文件夹</summary>
        public const int UaFlatBatchSize = 1000;

        /// <summary>命名空间索引默认值 — NS0=OPC UA base, NS1=server URI, NS2=custom</summary>
        public const ushort UaDefaultNamespaceIndex = 2;

        /// <summary>默认 UA 服务器端口</summary>
        public const int UaDefaultPort = 4840;

        // ══════════════════════════════════════════════════
        //  日志管理
        // ══════════════════════════════════════════════════

        /// <summary>文件日志队列容量 — 超出后丢弃（防止慢磁盘拖垮网关）</summary>
        public const int LogQueueCapacity = 50000;

        /// <summary>每次批量 Flush 的行数 — 平衡磁盘 I/O 与崩溃数据丢失风险</summary>
        public const int LogFlushInterval = 10;

        /// <summary>触发过期日志清理的写入次数间隔</summary>
        public const int LogCleanupInterval = 500;

        /// <summary>UI TextBox 最大字符数（超出截断）— H-35 提升至 500K 减少频繁截断</summary>
        public const int LogUiMaxChars = 500000;

        /// <summary>UI TextBox 截断时保留的末尾字符数</summary>
        public const int LogUiTrimChars = 50000;

        /// <summary>日志文件保留天数</summary>
        public const int LogRetentionDays = 30;

        // ══════════════════════════════════════════════════
        //  看门狗 & 健康监控
        // ══════════════════════════════════════════════════

        /// <summary>心跳发送间隔 (ms)</summary>
        public const int WatchdogHeartbeatMs = 10000;

        /// <summary>DA 连接断开后最大自动重连次数</summary>
        public const int DaMaxReconnectAttempts = 50;

        /// <summary>健康检查间隔 (ms)</summary>
        public const int HealthCheckIntervalMs = 10000;

        /// <summary>健康快照采集间隔（分钟）— 默认每 5 分钟采集一次进程健康指标</summary>
        public const int HealthSnapshotIntervalMinutes = 5;

        // ══════════════════════════════════════════════════
        //  配置
        // ══════════════════════════════════════════════════

        /// <summary>配置保存防抖延迟 (ms) — 多次快速修改合并为一次写入</summary>
        public const int ConfigDebounceMs = 500;

        /// <summary>默认会话超时 (ms)</summary>
        public const int DefaultSessionTimeoutMs = 120000;

        /// <summary>默认 OPC DA 主机名</summary>
        public const string DefaultDaHost = "localhost";

        /// <summary>未知数据类型占位符</summary>
        public const string UnknownDataType = "Variant";

        // ══════════════════════════════════════════════════
        //  UI
        // ══════════════════════════════════════════════════

        /// <summary>数据网格默认刷新间隔 (ms)</summary>
        public const int UiDefaultRefreshMs = 1000;

        /// <summary>数据网格自适应慢刷新间隔 (ms) — 无数据变化时使用</summary>
        public const int UiSlowRefreshMs = 3000;

        /// <summary>试用期 (分钟)</summary>
        public const int TrialPeriodMinutes = 30;

        // ══════════════════════════════════════════════════
        //  版本
        // ══════════════════════════════════════════════════

        /// <summary>当前软件版本</summary>
        public const string AppVersion = "2.6.0";

        /// <summary>窗口标题（不含版本号）</summary>
        public const string WindowTitle = "OPC DA → OPC UA 网关";
    }
}
