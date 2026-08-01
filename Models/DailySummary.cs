using System;

namespace OpcDaToUaGateway.Models
{
    /// <summary>
    /// 日聚合数据结构（持久化到 health_daily.jsonl）。
    /// </summary>
    public class DailySummary
    {
        public string Date { get; set; }              // "2026-07-02"
        public int SampleCount { get; set; }          // 当日有效快照数
        public double WorkingSetAvgMB { get; set; }
        public double WorkingSetMaxMB { get; set; }
        public double WorkingSetMinMB { get; set; }
        public double PrivateMemoryAvgMB { get; set; }
        public double GcTotalMemoryAvgMB { get; set; }
        public double UpdateRateAvgPerSec { get; set; }
        public long TotalUpdatesEndOfDay { get; set; }
        public int ErrorCountEndOfDay { get; set; }
        public int DaDisconnectedCount { get; set; }
        public string GrowthAlert { get; set; }       // "none"/"warning"/"critical"
        public double GrowthRate { get; set; }        // 7day/30day - 1
    }
}
