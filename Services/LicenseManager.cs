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

            try { _pcid = LicenseAlgorithm.GeneratePCID(); }
            catch { _pcid = "UNKNOWN"; }

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
                _persistedTrialSeconds = (long)TimeSpan.FromMinutes(AppConstants.TrialPeriodMinutes).TotalSeconds;
                _log.Append("[授权] 试用状态文件无效，按试用到期处理");
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
            if (!_isLicensed && _trialStopwatch != null && !TrySaveTrialState(GetCurrentElapsedSeconds()))
                _log.Append("[授权] 退出时保存试用累计时间失败");

            _licenseTimer?.Stop();
            _licenseTimer?.Dispose();
            _licenseTimer = null;
        }
    }
}