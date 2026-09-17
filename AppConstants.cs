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

        /// <summary>浏览分页大小（避免 DCOM 20s 超时）</summary>
        public const int DaBrowsePageSize = 500;

        /// <summary>定时同步读取间隔 (ms) — 作为 DA 订阅的兜底补充</summary>
        public const int DaSyncIntervalMs = 300000; // 5 分钟

        /// <summary>DA 浏览最大条目数上限</summary>
        public const int DaMaxBrowseItems = 50000;

        // H-33: 变量节点缓存上限 — 防止配置错误导致 OOM
        // 100000 个 BaseDataVariableState 约 500MB，作为安全阈值
        public const int MaxVariableNodes = 100000;

        // M1 清理（V2.6.0）：以下常量此前在代码中无引用，实际值在各处硬编码。
        // 为避免“集中管理常量”变成最大的 Magic Number 来源、调参时改了不生效的陷阱，
        // 将硬编码处改为引用本类后，这些常量即为唯一来源：
        //   WatchdogHeartbeatMs      ← Services/WatchdogManager.cs HeartbeatIntervalMs
        //   DaMaxReconnectAttempts   ← Services/GatewayManager.cs MaxReconnectAttemptsDefault
        //   DefaultSessionTimeoutMs   ← Services/ConfigManager.cs ApplyBackwardCompatDefaults
        //   UaDefaultPort            ← Services/ConfigManager.cs ApplyBackwardCompatDefaults
        // 纯死常量（DaBrowseProgressInterval / LogQueueCapacity / LogFlushInterval /
        // HealthSnapshotIntervalMinutes）已删除。

        // ══════════════════════════════════════════════════
        //  OPC UA 相关
        // ══════════════════════════════════════════════════

        /// <summary>扁平标签批量分组大小 — 每批最多 N 个变量，超过则创建新的 Batch 子文件夹</summary>
        public const int UaFlatBatchSize = 1000;

        /// <summary>默认 UA 服务器端口（OPC UA 标准端口）— M1：ConfigManager 默认值引用此常量</summary>
        public const int UaDefaultPort = 4840;

        // ══════════════════════════════════════════════════
        //  日志管理
        // ══════════════════════════════════════════════════

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

        /// <summary>心跳发送间隔 (ms) — M1：WatchdogManager 心跳定时器引用此常量</summary>
        public const int WatchdogHeartbeatMs = 10000;

        /// <summary>DA 连接断开后最大自动重连次数 — M1：GatewayManager 默认值引用此常量</summary>
        public const int DaMaxReconnectAttempts = 50;

        /// <summary>健康检查间隔 (ms)</summary>
        public const int HealthCheckIntervalMs = 10000;

        // ══════════════════════════════════════════════════
        //  配置
        // ══════════════════════════════════════════════════

        /// <summary>配置保存防抖延迟 (ms) — 多次快速修改合并为一次写入</summary>
        public const int ConfigDebounceMs = 500;

        /// <summary>默认会话超时 (ms) — M1：ConfigManager 向后兼容默认值引用此常量</summary>
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

        // L6 修复（V2.6.0）：删除硬编码 AppVersion 常量，改为单一来源 ——
        // OpcDaClient.AppVersion 从 AssemblyInformationalVersion 反射读取（csproj <Version>2.7.0</Version>），
        // 与 Watchdog csproj 保持同一来源，避免"改了常量不生效"的陷阱。
        // 如需在 UI 显示版本号，请引用 OpcDaClient.AppVersion 或 AboutDialog 的 AppVersion。

        /// <summary>窗口标题（不含版本号）</summary>
        public const string WindowTitle = "OPC DA → OPC UA 网关";
    }
}
