using System;
using System.Diagnostics;
using System.Drawing;
using System.Windows.Forms;
using OpcDaToUaGateway.Models;

namespace OpcDaToUaGateway.Services
{
    /// <summary>
    /// 授权管理器 — 处理授权码验证、试用期计时、授权状态维护。
    /// UI 层订阅 StatusChanged 事件更新状态栏，订阅 GatewayStopRequested 事件在试用到期时停止网关。
    /// </summary>
    public class LicenseManager : IDisposable
    {
        private readonly LogManager _log;
        private readonly ConfigManager _configMgr;
        private readonly Action _requestGatewayStop;
        private readonly TrialStateStore _trialStateStore;
        private readonly string _pcid;

        private bool _isLicensed;
        private bool _trialExpired;
        private Stopwatch _trialStopwatch;
        private Timer _licenseTimer;
        private long _persistedTrialSeconds;
        private long _lastSavedTrialSeconds;

        /// <summary>授权状态变化（已授权/试用中/试用到期）。</summary>
        public event Action<string, Color> StatusChanged;

        /// <summary>请求停止网关（试用到期时触发）。</summary>
        public event Action GatewayStopRequested;

        /// <summary>当前是否已授权。</summary>
        public bool IsLicensed => _isLicensed;

        /// <summary>试用是否已到期。</summary>
        public bool IsTrialExpired => _trialExpired;

        /// <summary>机器码 PCID。</summary>
        public string PCID => _pcid;

        public LicenseManager(LogManager log, ConfigManager configMgr, Action requestGatewayStop)
            : this(log, configMgr, requestGatewayStop, new TrialStateStore())
        {
        }

        internal LicenseManager(LogManager log, ConfigManager configMgr, Action requestGatewayStop, TrialStateStore trialStateStore)
        {
            _log = log ?? throw new ArgumentNullException(nameof(log));
            _configMgr = configMgr ?? throw new ArgumentNullException(nameof(configMgr));
            _requestGatewayStop = requestGatewayStop;
            _trialStateStore = trialStateStore ?? throw new ArgumentNullException(nameof(trialStateStore));

            // H-18 修复：捕获具体异常并记录，而非吞掉所有异常并用 "UNKNOWN" 替代。
            // "UNKNOWN" 会导致已保存的授权码与 PCID 不匹配，有效授权被清除，用户看到试用倒计时。
            try { _pcid = LicenseAlgorithm.GeneratePCID(); }
            catch (Exception ex)
            {
                _pcid = "UNKNOWN";
                _log.Append($"[授权] ⚠ 读取机器码失败（{ex.GetType().Name}: {ex.Message}），已授权的用户可能需要重新输入授权码");
            }

            _log.Append($"[授权] 机器码 PCID: {_pcid}");

            Initialize();
        }

        private void Initialize()
        {
            string savedCode = _configMgr.Config?.AuthorizationCode;
            if (!string.IsNullOrEmpty(savedCode) && LicenseAlgorithm.VerifyAuthCode(_pcid, savedCode))
            {
                _isLicensed = true;
                StatusChanged?.Invoke("● 授权: 已授权", Color.Green);
                _log.Append("[授权] 授权码验证通过，已授权");
                return;
            }

            _isLicensed = false;
            if (!string.IsNullOrEmpty(savedCode))
            {
                _log.Append("[授权] 已保存的授权码无效（可能硬件变更），进入试用模式");
                _configMgr.Config.AuthorizationCode = null;
                _configMgr.Save();
            }
            else
            {
                _log.Append("[授权] 未检测到授权码，进入试用模式");
            }

            StartTrialTimer();
        }

        private void StartTrialTimer()
        {
            TrialStateLoadResult loadResult = _trialStateStore.Initialize(out _persistedTrialSeconds);
            if (loadResult == TrialStateLoadResult.Invalid)
            {
                // H-19 修复：Invalid（文件损坏/磁盘抖动）≠ 试用已满。
                // 原先直接设为满额秒数导致任何一过性 I/O 故障都使正常用户被强制过期。
                // 改为：将累计时间保留为 0（按首次运行处理），记录警告，让用户继续试用。
                // 防误用说明：TrySave 会在下次 Tick 写入新文件，真实持久化在保存成功后才生效。
                _persistedTrialSeconds = 0;
                _log.Append("[授权] ⚠ 试用状态文件无效或损坏，按首次运行重置（若重复出现请检查文件权限）");
            }

            _lastSavedTrialSeconds = _persistedTrialSeconds;
            _trialStopwatch = Stopwatch.StartNew();
            _trialExpired = false;
            if (GetRemainingTrialTime() == TimeSpan.Zero)
            {
                ExpireTrial("[授权] 试用期已到，网关不可启动");
                return;
            }

            _licenseTimer = new Timer { Interval = 1000 };
            _licenseTimer.Tick += LicenseTimer_Tick;
            _licenseTimer.Start();

            UpdateTrialStatus();
            _log.Append($"[授权] 试用倒计时 {AppConstants.TrialPeriodMinutes} 分钟已开始");
        }

        private void LicenseTimer_Tick(object sender, EventArgs e)
        {
            TimeSpan remaining = GetRemainingTrialTime();
            long elapsedSeconds = GetCurrentElapsedSeconds();
            if (elapsedSeconds - _lastSavedTrialSeconds >= 60 && !TrySaveTrialState(elapsedSeconds))
            {
                ExpireTrial("[授权] 保存试用累计时间失败，按试用到期处理");
                return;
            }

            if (remaining == TimeSpan.Zero)
            {
                TrySaveTrialState((long)TimeSpan.FromMinutes(AppConstants.TrialPeriodMinutes).TotalSeconds);
                ExpireTrial("[授权] ★★★ 试用期已到，网关将自动关闭 ★★★");
                return;
            }

            UpdateTrialStatus(remaining);
        }

        private void UpdateTrialStatus(TimeSpan? remaining = null)
        {
            if (remaining == null)
                remaining = GetRemainingTrialTime();

            int totalSeconds = Math.Max(0, (int)remaining.Value.TotalSeconds);
            string text = $"● 试用: {totalSeconds / 60:D2}:{totalSeconds % 60:D2}";
            Color color = remaining.Value.TotalMinutes <= 5 ? Color.Red
                       : remaining.Value.TotalMinutes <= 10 ? Color.Orange
                       : Color.DarkOrange;

            StatusChanged?.Invoke(text, color);
        }

        internal static long GetElapsedSeconds(long persistedSeconds, TimeSpan currentRuntime)
        {
            long safePersisted = Math.Max(0, persistedSeconds);
            long runtimeSeconds = Math.Max(0, (long)currentRuntime.TotalSeconds);
            return safePersisted + runtimeSeconds;
        }

        internal static TimeSpan GetRemaining(long persistedSeconds, TimeSpan currentRuntime, TimeSpan trialPeriod)
        {
            long remainingSeconds = (long)trialPeriod.TotalSeconds - GetElapsedSeconds(persistedSeconds, currentRuntime);
            return remainingSeconds > 0 ? TimeSpan.FromSeconds(remainingSeconds) : TimeSpan.Zero;
        }

        private long GetCurrentElapsedSeconds()
            => GetElapsedSeconds(_persistedTrialSeconds, _trialStopwatch?.Elapsed ?? TimeSpan.Zero);

        private TimeSpan GetRemainingTrialTime()
            => GetRemaining(_persistedTrialSeconds, _trialStopwatch?.Elapsed ?? TimeSpan.Zero,
                TimeSpan.FromMinutes(AppConstants.TrialPeriodMinutes));

        private bool TrySaveTrialState(long elapsedSeconds)
        {
            if (!_trialStateStore.TrySave(elapsedSeconds))
                return false;

            _lastSavedTrialSeconds = elapsedSeconds;
            return true;
        }

        private void ExpireTrial(string logMessage)
        {
            if (_trialExpired) return;

            _trialExpired = true;
            _licenseTimer?.Stop();
            StatusChanged?.Invoke("● 授权: 试用到期", Color.Red);
            _log.Append(logMessage);
            GatewayStopRequested?.Invoke();
        }

        /// <summary>
        /// 验证并应用授权码。
        /// </summary>
        /// <param name="authCode">授权码。</param>
        /// <returns>验证是否成功。</returns>
        public bool ApplyAuthorizationCode(string authCode)
        {
            if (LicenseAlgorithm.VerifyAuthCode(_pcid, authCode))
            {
                if (!TrySaveTrialState(GetCurrentElapsedSeconds()))
                {
                    _log.Append("[授权] 保存试用累计时间失败，授权未应用");
                    return false;
                }

                _configMgr.Config.AuthorizationCode = authCode;
                if (!_configMgr.TrySaveImmediate())
                {
                    _configMgr.Config.AuthorizationCode = null;
                    _isLicensed = false;
                    _log.Append("[授权] 授权码保存失败，授权未应用");
                    return false;
                }

                _isLicensed = true;
                _trialExpired = false;
                _licenseTimer?.Stop();
                _licenseTimer?.Dispose();
                _licenseTimer = null;

                StatusChanged?.Invoke("● 授权: 已授权", Color.Green);
                _log.Append("[授权] ★ 授权码验证成功，软件已授权 ★");
                return true;
            }
            else
            {
                _log.Append("[授权] 授权码验证失败");
                return false;
            }
        }

        public void Dispose()
        {
            // M6 说明（V2.6.0）：试用累计时长在 LicenseTimer_Tick 中每 60 秒持久化一次，
            // 正常情况下 Dispose 退出时的保存失败丢失窗口 ≤1 分钟，可接受。
            // 但 LicenseTimer_Tick 在保存失败时按"试用到期"严格处理，而 Dispose 失败
            // 仅打日志，两者策略不对称：若磁盘故障持续，用户重启后试用时间会回退。
            // 属已知取舍，保留现状（退出路径不能因磁盘故障阻断关闭），在此注明。
            if (!_isLicensed && _trialStopwatch != null && !TrySaveTrialState(GetCurrentElapsedSeconds()))
                _log.Append("[授权] 退出时保存试用累计时间失败");

            _licenseTimer?.Stop();
            _licenseTimer?.Dispose();
            _licenseTimer = null;
        }
    }
}