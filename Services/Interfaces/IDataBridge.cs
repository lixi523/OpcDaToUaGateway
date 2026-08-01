using System;
using System.Collections.Generic;
using OpcDaToUaGateway.Models;

namespace OpcDaToUaGateway.Services.Interfaces
{
    /// <summary>
    /// 数据桥接器接口 —— 抽象出 DA → UA 数据转发的核心契约。
    ///
    /// 设计目的（PLAN 3.1 接口抽象）：
    /// 1. 解耦 GatewayManager 与桥接器实现，使数据流可以独立验证。
    /// 2. 暴露统计计数器（TotalUpdates / ErrorCount / LastUpdateTime）便于健康监控。
    ///
    /// 实现约束：
    /// - Start() 必须幂等，重复调用不得抛异常。
    /// - GetSnapshots() 返回的列表顺序必须与构造时传入的 TagKey 顺序一致（N-7 修复）。
    /// - OnLog 事件可在异步线程上触发，订阅方需保证线程安全。
    ///
    /// 实现类：<see cref="DataBridge"/>
    /// </summary>
    public interface IDataBridge : IDisposable
    {
        /// <summary>累计成功更新次数（线程安全读取）。</summary>
        long TotalUpdates { get; }

        /// <summary>累计错误次数（线程安全读取）。</summary>
        int ErrorCount { get; }

        /// <summary>最后一次成功更新的本地时间。</summary>
        DateTime LastUpdateTime { get; }

        /// <summary>桥接器日志事件（数据转换错误、异常等）。</summary>
        event Action<string> OnLog;

        /// <summary>启动数据桥接：订阅 DA 客户端的 OnDataChanged 事件并向 UA 服务器转发。</summary>
        void Start();

        /// <summary>获取所有标签的当前值快照（按 TagKey 构造顺序排列）。</summary>
        IReadOnlyList<TagSnapshot> GetSnapshots();
    }
}
