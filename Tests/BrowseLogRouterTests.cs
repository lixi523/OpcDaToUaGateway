using Xunit;

namespace OpcDaToUaGateway.Tests
{
    /// <summary>
    /// 点位浏览诊断消息路由测试。
    ///
    /// 背景：大规模 OPC DA 服务器（&gt;1 万点位、分支多）浏览时会产生数千至上万条诊断日志。
    /// 若逐条封送到 UI 线程刷新运行日志文本框（TextBox.AppendText 累计 O(n²)），
    /// 会把「OPC DA 点位浏览」对话框拖成「未响应」（V1.7.0 / V1.8.0 同型根因）。
    ///
    /// 因此只有低频「里程碑」消息允许进入 UI，高频诊断（分支摘要 / 进度 / 数据类型批次）
    /// 必须走仅文件通道。本测试锁定该分流契约，防止后续重构把高频消息重新灌回 UI。
    /// </summary>
    public class BrowseLogRouterTests
    {
        // ---------------- 进 UI 的里程碑 ----------------

        [Theory]
        [InlineData("[Browse] 尝试连接 OPC DA 服务器")]
        [InlineData("[Browse] 连接成功，开始递归浏览...")]
        [InlineData("[Browse] 浏览完成，共获取 48 个点位")]
        [InlineData("[Browse] 已达最大点位上限 50000，停止浏览")]
        [InlineData("[Browse] 连接 OPC DA 服务器失败")]
        [InlineData("[DCOM] 正在诊断 ProgId: Matrikon.OPC.Simulation.1")]
        public void IsUiMilestone_Milestones_ReturnTrue(string msg)
        {
            Assert.True(BrowseLogRouter.IsUiMilestone(msg));
        }

        // ---------------- 不进 UI 的高频诊断 ----------------

        [Theory]
        [InlineData("[Browse] 分支 [b.c] (深度=2): 共 8 元素, 8 叶子, 0 分支, 0 其他")]
        [InlineData("[Browse] 分支 [(root)] (深度=0): 共 2 元素, 0 叶子, 2 分支, 0 其他")]
        [InlineData("[Browse] 已扫描 2000 个点位... (当前分支: b, 本层: 8叶/0枝/0其他)")]
        [InlineData("[DataType] 创建临时浏览 Group，准备填充 50000 个点位的数据类型")]
        [InlineData("[DataType] 批次 1 完成 (500 个点位)")]
        [InlineData("[DataType] 所有 50000 个点位的数据类型填充完成")]
        // 子树级失败也是高频诊断，必须只落文件，不得因含「失败」二字而灌回 UI
        [InlineData("[Browse] 浏览分支 [a.b] 失败: 0x80004005")]
        public void IsUiMilestone_HighFrequencyDiagnostics_ReturnFalse(string msg)
        {
            Assert.False(BrowseLogRouter.IsUiMilestone(msg));
        }

        [Theory]
        [InlineData("")]
        [InlineData(null)]
        public void IsUiMilestone_NullOrEmpty_ReturnFalse(string msg)
        {
            Assert.False(BrowseLogRouter.IsUiMilestone(msg));
        }

        // ---------------- 状态栏里程碑 ----------------

        [Theory]
        [InlineData("[Browse] 尝试连接 OPC DA 服务器")]
        [InlineData("[Browse] 连接成功，开始递归浏览...")]
        [InlineData("[Browse] 浏览完成，共获取 48 个点位")]
        [InlineData("[Browse] 连接 OPC DA 服务器失败")]
        public void IsLabelMilestone_PhaseMilestones_ReturnTrue(string msg)
        {
            Assert.True(BrowseLogRouter.IsLabelMilestone(msg));
        }

        [Theory]
        // [DCOM] 明细只进 UI 日志，不覆盖状态栏
        [InlineData("[DCOM] 正在诊断 ProgId: Matrikon.OPC.Simulation.1")]
        [InlineData("[Browse] 分支 [b.c] (深度=2): 共 8 元素, 8 叶子, 0 分支, 0 其他")]
        [InlineData("[Browse] 浏览分支 [a.b] 失败: 0x80004005")]
        public void IsLabelMilestone_NonPhaseMessages_ReturnFalse(string msg)
        {
            Assert.False(BrowseLogRouter.IsLabelMilestone(msg));
        }
    }
}