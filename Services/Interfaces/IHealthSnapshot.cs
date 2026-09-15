using System;
using OpcDaToUaGateway.Models;

namespace OpcDaToUaGateway.Services.Interfaces
{
    /// <summary>
    /// 运行状态快照采集器契约。
    ///
    /// 纯后台运行（5 分钟一次定时采集），无外部交互；接口暴露
    /// 轻量级调试属性与 3.5 长期内存监控所需的方法。
    ///
    /// PLAN 3.1 收尾 — DI 改造：HealthSnapshot 类实现此接口，
    /// 测试中可注入 FakeHealthSnapshot 模拟采集行为。
    /// PLAN 3.5 — 新增 OnAlert 事件与 GenerateDailySummary 方法，
    /// 供长期内存增长告警与手动触发聚合。
    /// </summary>
    public interface IHealthSnapshot : IDisposable
    {
        /// <summary>最近一次成功采集的本地时间；从未采集或采集失败时为 null。</summary>
        DateTime? LastCaptureTime { get; }

        /// <summary>最近一次采集的工作集内存（MB）；从未采集或采集失败时为 null。</summary>
        double? LastWorkingSetMB { get; }

        /// <summary>
        /// 内存增长率告警事件。3.5 长期监控每日聚合时若发现 7 天/30 天
        /// 工作集增长超过阈值，触发此事件，订阅者可更新 UI 状态栏。
        /// </summary>
        event Action<string, int> OnAlert;

        /// <summary>
        /// 触发一次健康快照采集（进程内存、DA 连接状态、UA 变量数等）。
        /// H-14 修复：Capture 原为私有方法且从未被调用，健康文件永远不会产生数据。
        /// 现在暴露为公共接口方法，由 MainForm._healthTimer.Tick 每 5 分钟调用一次。
        /// </summary>
        void Capture();

        /// <summary>
        /// 手动触发一次"昨日"日聚合。3.5 扩展点：测试 / 菜单可调用，
        /// 立即读取现有 health_*.json 计算并追加到 health_daily.jsonl。
        /// </summary>
        /// <returns>写入的聚合行（包含 GrowthRate/GrowthAlert），异常时返回 null。</returns>
        DailySummary GenerateDailySummary();
    }
}
