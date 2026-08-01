using System;
using System.Threading.Tasks;
using Opc.Ua;

namespace OpcDaToUaGateway.Services.Interfaces
{
    /// <summary>
    /// OPC UA 服务器接口 —— 抽象出 UA 服务器启动、停止、变量节点管理的核心契约。
    ///
    /// 设计目的（PLAN 3.1 接口抽象）：
    /// 1. 解耦 GatewayManager 与 UA 服务器实现，允许在测试中注入 Mock UA 服务器。
    /// 2. 明确变量节点创建、状态报告、配置回写（NamespaceIndex）等关键操作。
    ///
    /// 实现约束：
    /// - AddVariableNode 必须在 UA 服务器启动后调用，tagKey 在同一实例内必须唯一。
    /// - ParseDataType / ComputeUaBrowsePath 是实现类静态方法，接口不约束（C# 7.3 限制）。
    /// - StartAsync / StopAsync 必须实现为可重入安全（H-12 修复语义）。
    ///
    /// 实现类：<see cref="GatewayOpcUaServer"/>
    /// </summary>
    public interface IGatewayOpcUaServer : IDisposable
    {
        /// <summary>服务器当前是否处于运行状态（监听端口中）。</summary>
        bool IsRunning { get; }

        /// <summary>UA 命名空间索引，由服务器在 StartAsync 后分配并通过 OnConfigChanged 回调回写。</summary>
        ushort NamespaceIndex { get; }

        /// <summary>UA 命名空间 URI（用于节点 NodeId 构造）。</summary>
        string NamespaceUri { get; }

        /// <summary>已注册的变量节点总数（含 DaTags 与 FlatTags）。</summary>
        int VariableCount { get; }

        /// <summary>DaTags 文件夹的子节点数（未创建时返回 -1）。</summary>
        int DaTagsChildrenCount { get; }

        /// <summary>FlatTags 主文件夹的子节点数（未创建时返回 -1）。</summary>
        int FlatTagsChildrenCount { get; }

        /// <summary>FlatTags 含批量子文件夹在内的全部可浏览节点数（未创建时返回 -1）。</summary>
        int FlatTagsBrowseCount { get; }

        /// <summary>状态变化事件，用于向 UI 层报告启动/停止/错误等信息。</summary>
        event Action<string> OnStatusChanged;

        /// <summary>配置变更回调 — 当 NamespaceIndex 等关键参数被回写后立即触发，供上层调用 ConfigManager.Save()。</summary>
        Action OnConfigChanged { get; set; }

        /// <summary>异步启动 UA 服务器，包括配置构建、证书检查/生成、启动监听。</summary>
        Task StartAsync();

        /// <summary>异步停止 UA 服务器。Dispose 内部使用 Task.Run 包装避免 UI 线程 SynchronizationContext 死锁（C-06 修复）。</summary>
        Task StopAsync();


        /// <summary>注册一个变量节点到 UA 地址空间。tagKey 在同一实例内必须唯一。</summary>
        void AddVariableNode(string tagKey, string itemId, string displayName, BuiltInType dataType, string nodeId = null);

        /// <summary>
        /// 更新变量节点的值。由 DataBridge 在 DA 数据变化回调中调用。
        /// isGood=false 时值可为 null，表示 Bad Quality。
        /// </summary>
        void UpdateValue(string tagKey, object value, bool isGood, DateTime sourceTimestamp);
    }
}
