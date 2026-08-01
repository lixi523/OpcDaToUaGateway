using System;

namespace OpcDaToUaGateway.Models
{
    /// <summary>
    /// 运行状态快照数据结构（序列化到 health_*.json）。
    /// </summary>
    public class SnapshotData
    {
        public string Timestamp { get; set; }
        public string TimestampUtc { get; set; }

        // 内存
        public double WorkingSetMB { get; set; }
        public double PrivateMemoryMB { get; set; }
        public double GcTotalMemoryMB { get; set; }
        public int ThreadCount { get; set; }
        public int HandleCount { get; set; }

        // 网关
        public bool IsRunning { get; set; }
        public bool DaConnected { get; set; }
        public long TotalUpdates { get; set; }
        public double UpdateRatePerSec { get; set; }
        public int ErrorCount { get; set; }
        public string LastUpdateTime { get; set; }
        public int UaVariableCount { get; set; }
        public int UaNamespaceIndex { get; set; }
        public int DaTagsChildren { get; set; }

        // 线程池
        public int ThreadPoolWorkerBusy { get; set; }
        public int ThreadPoolWorkerMax { get; set; }
    }
}
