namespace OpcDaToUaGateway.Services
{
    /// <summary>
    /// 网关运行状态枚举 — 替代原来的 bool IsRunning，
    /// 使调用方能区分 Idle / Starting / Running / Stopping / Error 五种状态，
    /// 避免启动异常后 UI 无法感知"正在启动"这一中间态。
    /// </summary>
    public enum GatewayState
    {
        /// <summary>网关未启动，所有资源已释放</summary>
        Idle,

        /// <summary>正在执行启动流程（UA 服务器 / DA 客户端 / 数据桥接 初始化中）</summary>
        Starting,

        /// <summary>网关正常运行，DA ↔ UA 数据桥接已建立</summary>
        Running,

        /// <summary>正在执行停止流程（按逆序释放资源）</summary>
        Stopping,

        /// <summary>启动失败或运行时发生不可恢复错误，需人工介入</summary>
        Error,
    }

    /// <summary>
    /// G-2 修复：为 GatewayState 枚举提供友好的中文显示文本。
    /// 供 UI 层直接获取状态描述，无需额外映射。
    /// </summary>
    public static class GatewayStateExtensions
    {
        /// <summary>将 GatewayState 转换为中文显示文本。</summary>
        public static string ToDisplayName(this GatewayState state)
        {
            return state switch
            {
                GatewayState.Idle       => "空闲",
                GatewayState.Starting   => "正在启动",
                GatewayState.Running    => "运行中",
                GatewayState.Stopping   => "正在停止",
                GatewayState.Error      => "错误",
                _                       => "未知",
            };
        }
    }
}
