using System;
using System.Collections.Generic;
using System.Threading;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using OpcDaToUaGateway.Models;
using OpcDaToUaGateway.Services.Interfaces;

namespace OpcDaToUaGateway.Services
{
    /// <summary>
    /// 健康快照采集器 — 定时采集进程内存/CPU/UA节点等指标，写入 JSON 文件。
    /// 
    /// 采集频率：每 5 分钟一次（由 MainForm 的 _healthTimer 触发）。
    /// 存储路径：bin\Release\net472\health\snapshots_YYYY-MM-DD.jsonl
    /// 每日聚合：每天凌晨 00:05 将前一天的快照聚合成 DailySummary，写入 health_daily.jsonl。
    /// </summary>
    public class HealthSnapshot : IHealthSnapshot
    {
        private readonly LogManager _log;
        private readonly string _healthDir;
        private readonly string _dailyFile;
        private readonly List<DailySummary> _dailyCache = new List<DailySummary>();
        private readonly object _captureLock = new object();

        private int _disposedInt;
        private string _lastAggregatedDate;
        private DateTime? _lastCaptureTime;
        private double? _lastWorkingSetMB;
        private System.Diagnostics.Process _cachedProcess;

        /// <summary>IHealthSnapshot 接口实现：最近一次成功采集的本地时间。</summary>
        public DateTime? LastCaptureTime => _lastCaptureTime;

        /// <summary>IHealthSnapshot 接口实现：最近一次采集的工作集内存（MB）。</summary>
        public double? LastWorkingSetMB => _lastWorkingSetMB;

        /// <summary>内存增长率告警事件（severity: 0=正常 1=黄色 2=红色）。</summary>
        public event Action<string, int> OnAlert;

        /// <summary>
        /// 创建健康快照采集器。
        /// </summary>
        /// <param name="gatewayMgr">网关管理器（用于获取 DA 连接状态和更新计数）。</param>
        /// <param name="log">日志管理器。</param>
        public HealthSnapshot(GatewayManager gatewayMgr, LogManager log)
        {
            _gatewayMgr = gatewayMgr;
            _log = log;
            _healthDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "health");
            Directory.CreateDirectory(_healthDir);
            _dailyFile = Path.Combine(_healthDir, "health_daily.jsonl");

            // 加载历史每日缓存
            LoadDailyCache();
        }

        private readonly GatewayManager _gatewayMgr;

        /// <summary>
        /// 从 health_daily.jsonl 加载历史每日聚合数据到内存缓存。
        /// </summary>
        private void LoadDailyCache()
        {
            try
            {
                if (!File.Exists(_dailyFile)) return;

                var lines = File.ReadAllLines(_dailyFile)
                    .Where(l => !string.IsNullOrWhiteSpace(l))
                    .Select(l => { try { return JsonConvert.DeserializeObject<DailySummary>(l); } catch { return null; } })
                    .Where(x => x != null)
                    .OrderBy(x => x.Date)
                    .ToList();
                _dailyCache.AddRange(lines);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[健康快照] 加载缓存失败: " + ex.Message);
            }
        }

        /// <summary>
        /// 定时采集进程内存/CPU/UA节点等指标。
        /// </summary>
        private void Capture()
        {
            if (Volatile.Read(ref _disposedInt) == 1) return;
            if (!Monitor.TryEnter(_captureLock)) return;

            try
            {
                // 进入 Monitor 后二次检查，防止 Dispose 在另一线程执行后访问已释放的 _gatewayMgr
                if (Volatile.Read(ref _disposedInt) == 1) return;

                var snapshot = new SnapshotData
                {
                    TimestampUtc = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
                    DaConnected = false,
                    TotalUpdates = 0,
                    UaVariableCount = 0,
                    WorkingSetMB = 0,
                    PrivateMemoryMB = 0,
                    GcTotalMemoryMB = 0,
                    UpdateRatePerSec = 0,
                    ErrorCount = 0
                };

                // 进程内存指标
                try
                {
                    var proc = System.Diagnostics.Process.GetCurrentProcess();
                    snapshot.WorkingSetMB = proc.WorkingSet64 / (1024 * 1024);
                    snapshot.PrivateMemoryMB = proc.PrivateMemorySize64 / (1024 * 1024);
                    snapshot.GcTotalMemoryMB = GC.GetTotalMemory(false) / (1024 * 1024);
                    _lastWorkingSetMB = snapshot.WorkingSetMB;
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine("[健康快照] 获取进程内存失败: " + ex.Message);
                }

                _lastCaptureTime = DateTime.Now;

                // 写入当日快照文件
                string today = DateTime.Now.ToString("yyyy-MM-dd");
                string snapshotFile = Path.Combine(_healthDir, $"snapshots_{today}.jsonl");
                File.AppendAllText(snapshotFile, JsonConvert.SerializeObject(snapshot) + Environment.NewLine);

                RotateOldSnapshots();
            }
            catch (Exception ex)
            {
                try { _log?.Append("[健康快照] 采集失败: " + ex.Message); } catch { }
            }
            finally
            {
                Monitor.Exit(_captureLock);
            }
        }

        /// <summary>
        /// 清理超过 30 天的旧快照文件。
        /// </summary>
        private void RotateOldSnapshots()
        {
            try
            {
                string today = DateTime.Now.ToString("yyyy-MM-dd");
                var toDelete = new List<string>();

                foreach (string f in Directory.GetFiles(_healthDir, "snapshots_*.jsonl"))
                {
                    try
                    {
                        string datePart = Path.GetFileName(f).Replace("snapshots_", "").Replace(".jsonl", "");
                        if (datePart != today && DateTime.TryParse(datePart, out DateTime fileDate) && fileDate < DateTime.Now.AddDays(-30))
                            toDelete.Add(f);
                    }
                    catch { }
                }

                foreach (string f in toDelete)
                    try { File.Delete(f); } catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[健康快照] 删除旧快照失败: " + ex.Message); }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[健康快照] 旋转快照失败: " + ex.Message);
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposedInt, 1) == 1) return;
            if (_cachedProcess != null) { _cachedProcess.Dispose(); _cachedProcess = null; }

        }

        /// <summary>
        /// 手动触发一次"昨日"日聚合。
        /// </summary>
        public DailySummary GenerateDailySummary()
        {
            if (!Monitor.TryEnter(_captureLock)) return null;
            try
            {
                string targetDate = DateTime.Now.Date.AddDays(-1).ToString("yyyy-MM-dd");
                return AppendDailySummary(targetDate);
            }
            catch (Exception ex)
            {
                _log?.Append("[健康快照] 手动聚合失败: " + ex.Message);
                return null;
            }
            finally
            {
                Monitor.Exit(_captureLock);
            }
        }

        private DailySummary AppendDailySummary(string targetDate)
        {
            if (targetDate == _lastAggregatedDate) return null;

            string sourceFile = Path.Combine(_healthDir, $"snapshots_{targetDate}.jsonl");
            if (!File.Exists(sourceFile)) return null;

            var dataPoints = new List<SnapshotData>();
            foreach (string f in File.ReadAllLines(sourceFile))
            {
                try
                {
                    var d = JsonConvert.DeserializeObject<SnapshotData>(f);
                    if (d != null) dataPoints.Add(d);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine("[健康快照] 读取快照失败: " + ex.Message);
                }
            }

            if (dataPoints.Count == 0) return null;

            // Count DA disconnects (status flips)
            int daDisconnects = 0;
            bool? prevDaConnected = null;
            foreach (var dp in dataPoints)
            {
                if (prevDaConnected.HasValue && prevDaConnected.Value != dp.DaConnected)
                    daDisconnects++;
                prevDaConnected = dp.DaConnected;
            }

            var summary = new DailySummary
            {
                Date = targetDate,
                SampleCount = dataPoints.Count,
                WorkingSetAvgMB = Math.Round(dataPoints.Average(d => d.WorkingSetMB), 1),
                WorkingSetMinMB = Math.Round(dataPoints.Min(d => d.WorkingSetMB), 1),
                WorkingSetMaxMB = Math.Round(dataPoints.Max(d => d.WorkingSetMB), 1),
                PrivateMemoryAvgMB = Math.Round(dataPoints.Average(d => d.PrivateMemoryMB), 1),
                GcTotalMemoryAvgMB = Math.Round(dataPoints.Average(d => d.GcTotalMemoryMB), 1),
                UpdateRateAvgPerSec = Math.Round(dataPoints.Average(d => d.UpdateRatePerSec), 1),
                TotalUpdatesEndOfDay = dataPoints[dataPoints.Count - 1].TotalUpdates,
                ErrorCountEndOfDay = dataPoints[dataPoints.Count - 1].ErrorCount,
                DaDisconnectedCount = daDisconnects,
                GrowthRate = CalculateGrowthRate(targetDate)
            };

            // 内存增长率告警
            if (summary.GrowthRate > 0.2)
            {
                OnAlert?.Invoke($"内存持续增长: {summary.GrowthRate:P1}", 2);
            }
            else if (summary.GrowthRate > 0.1)
            {
                OnAlert?.Invoke($"内存缓慢增长: {summary.GrowthRate:P1}", 1);
            }

            // 追加到每日聚合文件
            try
            {
                File.AppendAllText(_dailyFile, JsonConvert.SerializeObject(summary) + Environment.NewLine);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[健康快照] 保存每日缓存失败: " + ex.Message);
            }

            _lastAggregatedDate = targetDate;
            _log?.Append($"[健康快照] 已聚合 {targetDate}（{dataPoints.Count} 个样本，平均工作集 {summary.WorkingSetAvgMB:F1}MB，告警={summary.GrowthAlert}）");

            return summary;
        }

        private double CalculateGrowthRate(string targetDate)
        {
            // 计算过去 7 天与过去 30 天的内存增长率
            try
            {
                DateTime cutoff7 = DateTime.Now.AddDays(-7);
                DateTime cutoff30 = DateTime.Now.AddDays(-30);
                var recent = _dailyCache.Where(d => DateTime.TryParse(d.Date, out DateTime dt) && dt >= cutoff7).ToList();
                var older = _dailyCache.Where(d => DateTime.TryParse(d.Date, out DateTime dt) && dt >= cutoff30 && dt < cutoff7).ToList();

                if (recent.Count == 0 || older.Count == 0) return 0;

                double recentAvg = recent.Average(d => d.WorkingSetAvgMB);
                double olderAvg = older.Average(d => d.WorkingSetAvgMB);

                if (olderAvg <= 0) return 0;
                return (recentAvg - olderAvg) / olderAvg;
            }
            catch
            {
                return 0;
            }
        }
    }
}
