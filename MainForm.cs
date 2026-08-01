using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;
using OpcDaToUaGateway.Models;
using OpcDaToUaGateway.Services;
using OpcDaToUaGateway.Services.Interfaces;

namespace OpcDaToUaGateway
{
    /// <summary>
    /// 主窗体 - OPC DA 到 OPC UA 网关的控制界面
    /// 职责：UI 构建、用户交互、协调各 Manager
    /// </summary>
    public class MainForm : Form
    {
        // P3 修复 + R-6：统一使用 AppConstants.WindowTitle，消除硬编码重复
        internal const string WindowTitle = AppConstants.WindowTitle;

        // ---- UI 控件 ----
        private Label _lblCurrentServer;
        private TextBox _txtProgId;
        private ComboBox _cmbDaMode;
        private Button _btnBrowse;
        private Button _btnFetchTags;
        private ComboBox _cmbListenAddress;
        private NumericUpDown _nudUaPort;
        private ComboBox _cmbSecurityMode;
        private CheckBox _chkAutoAcceptCerts;
        private NumericUpDown _nudMaxSessions;
        private Label _lblEndpointUrl;
        private Button _btnStart;
        private Button _btnStop;
        private Button _btnExportTags;
        private CheckBox _chkAutoConnectDa;
        private CheckBox _chkAutoStartUa;
        private CheckBox _chkAutoStartWin;
        private CheckBox _chkEnableWatchdog;
        private Label _lblDaStatus;
        private Label _lblUaStatus;
        private Label _lblStats;
        private Label _lblWatchdogStatus;
        private DataGridView _dgvTags;
        private IReadOnlyList<TagSnapshot> _cachedSnapshots; // 缓存的快照引用，避免重复读取（接口 IDataBridge.GetSnapshots 返回类型）
        private TextBox _txtLog;
        private Timer _refreshTimer;

        /// <summary>P1-4: 自适应刷新 — 缓存上次快照的特征哈希，无变化时降低刷新频率</summary>
        private int _lastSnapshotHash;
        private Timer _healthTimer;
        private Timer _autoStartTimer;  // 存为字段以便 Dispose

        // ---- 管理器 ----
        private LogManager _log;
        private ConfigManager _configMgr;
        private AutoStartManager _autoStartMgr;
        private WatchdogManager _watchdogMgr;
        private GatewayManager _gatewayMgr;
        private IHealthSnapshot _healthSnapshot;
        private LicenseManager _licenseMgr;

        // ---- 授权 ----
        private Label _lblLicenseStatus;

        // ---- 系统托盘 ----
        private NotifyIcon _notifyIcon;
        private readonly bool _startMinimized;
        private bool _forceClose;
        private bool _isShuttingDown;
        // 合并 _closeInProgress、_isShuttingDone 为单一标志
        // _isShuttingDown 表示关闭流程已启动
        // _forceClose 表示第二次 Close 调用应执行资源释放

        /// <summary>当前配置（便捷属性，代理到 ConfigManager）</summary>
        private AppConfig Config => _configMgr?.Config;

        /// <summary>是否正在加载配置（防止触发自动保存）</summary>
        private bool _isLoadingConfig;

        public MainForm(bool startMinimized = false)
        {
            _startMinimized = startMinimized;
            BuildUI();
            LoadConfiguration();
        }

        // ================================================================
        //  UI 构建
        // ================================================================

        private void BuildUI()
        {
            // 窗体初始化
            Text = WindowTitle;
            Size = new Size(960, 900);
            StartPosition = FormStartPosition.CenterScreen;
            MaximizeBox = false;
            MaximumSize = new Size(0, 0);
            MinimumSize = new Size(800, 720);
            Font = new Font("Microsoft YaHei UI", 9f);
            BackColor = Theme.FormBg;

            // 加载图标
            string iconPath = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "app.ico");
            if (System.IO.File.Exists(iconPath))
            {
                try { Icon = new Icon(iconPath); }
                catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"加载图标失败: {ex.Message}"); }
            }

            int y = 10;
            y = BuildDaServerSection(y);
            y = BuildUaServerSection(y);
            y = BuildControlPanelSection(y);
            y = BuildTagMonitorSection(y);
            BuildLogSection(y);
            InitializeManagers();
            BuildTrayIcon();
            SetupEventHandlersAndAutoStart();
        }
        private int BuildDaServerSection(int y)
        {
            var grpServer = new GroupBox
            {
                Text = "OPC DA 服务器设置",
                Location = new Point(10, y),
                Size = new Size(920, 130),
                BackColor = Theme.Surface
            };

            var lblPrompt = new Label { Text = "服务器 ProgId:", Location = new Point(15, 30), AutoSize = true };
            _txtProgId = new TextBox { Location = new Point(110, 27), Size = new Size(420, 25) };
            _txtProgId.TextChanged += (s, ev) => UpdateDaButtonsState();

            _btnBrowse = new Button
            {
                Text = "浏览...", Location = new Point(540, 25), Size = new Size(80, 28),
                FlatStyle = FlatStyle.Flat, Cursor = Cursors.Hand
            };
            _btnBrowse.FlatAppearance.BorderColor = Theme.Border;
            _btnBrowse.Click += BtnBrowse_Click;

            _lblCurrentServer = new Label { Text = "", Location = new Point(640, 30), AutoSize = true, ForeColor = Color.Gray };

            _btnFetchTags = new Button
            {
                Text = "获取点位...", Location = new Point(110, 65), Size = new Size(110, 30),
                FlatStyle = FlatStyle.Flat, BackColor = Theme.Primary, ForeColor = Color.White,
                Cursor = Cursors.Hand, Enabled = false
            };
            _btnFetchTags.FlatAppearance.BorderSize = 0;
            _btnFetchTags.Click += BtnFetchTags_Click;

            var lblFetchHint = new Label
            {
                Text = "点击「获取点位」浏览服务器地址空间，选择需要桥接的标签",
                Location = new Point(230, 70), AutoSize = true, ForeColor = Theme.TextSecondary
            };

            var lblDaMode = new Label { Text = "获取方式:", Location = new Point(15, 100), AutoSize = true };
            _cmbDaMode = new ComboBox
            {
                Location = new Point(110, 97), Size = new Size(120, 25), DropDownStyle = ComboBoxStyle.DropDownList
            };
            _cmbDaMode.Items.AddRange(new object[] { "异步订阅", "同步轮询" });
            _cmbDaMode.SelectedIndex = 0;
            _cmbDaMode.SelectedIndexChanged += (s, ev) =>
            {
                if (!_isLoadingConfig && Config != null)
                {
                    Config.OpcDa.Mode = _cmbDaMode.SelectedIndex == 1 ? "Sync" : "Async";
                    _configMgr.Save();
                }
            };

            grpServer.Controls.AddRange(new Control[] { lblPrompt, _txtProgId, _btnBrowse, _lblCurrentServer, _btnFetchTags, lblFetchHint, lblDaMode, _cmbDaMode });
            Controls.Add(grpServer);
            return y + 140;
        }
        private int BuildUaServerSection(int y)
        {
            var grpUaSettings = new GroupBox
            {
                Text = "OPC UA 服务器设置",
                Location = new Point(10, y),
                Size = new Size(920, 150),
                BackColor = Theme.Surface
            };

            var lblListenAddr = new Label { Text = "监听地址:", Location = new Point(15, 30), AutoSize = true };
            _cmbListenAddress = new ComboBox
            {
                Location = new Point(110, 27), Size = new Size(150, 25),
                DropDownStyle = ComboBoxStyle.DropDownList
            };
            _cmbListenAddress.Items.AddRange(new object[] { "localhost", "0.0.0.0" });
            _cmbListenAddress.SelectedIndexChanged += (s, ev) =>
            {
                if (!_isLoadingConfig && Config != null)
                {
                    Config.OpcUa.ListenAddress = _cmbListenAddress.SelectedItem?.ToString();
                    UpdateEndpointUrlLabel();
                    _configMgr.Save();
                }
            };

            var lblPort = new Label { Text = "端口:", Location = new Point(280, 30), AutoSize = true };
            _nudUaPort = new NumericUpDown { Location = new Point(320, 27), Size = new Size(80, 25), Minimum = 1024, Maximum = 65535 };
            _nudUaPort.ValueChanged += (s, ev) =>
            {
                if (!_isLoadingConfig && Config != null)
                {
                    Config.OpcUa.Port = (int)_nudUaPort.Value;
                    UpdateEndpointUrlLabel();
                    _configMgr.Save();
                }
            };

            var lblSecurity = new Label { Text = "安全模式:", Location = new Point(15, 65), AutoSize = true };
            _cmbSecurityMode = new ComboBox
            {
                Location = new Point(110, 62), Size = new Size(150, 25),
                DropDownStyle = ComboBoxStyle.DropDownList
            };
            _cmbSecurityMode.Items.AddRange(new object[] { "None", "Sign", "SignAndEncrypt" });
            _cmbSecurityMode.SelectedIndexChanged += (s, ev) =>
            {
                if (!_isLoadingConfig && Config != null)
                {
                    Config.OpcUa.SecurityMode = _cmbSecurityMode.SelectedItem?.ToString();
                    _configMgr.Save();
                }
            };

            _chkAutoAcceptCerts = new CheckBox
            {
                Text = "自动接受客户端证书", Location = new Point(280, 63), AutoSize = true
            };
            _chkAutoAcceptCerts.CheckedChanged += (s, ev) =>
            {
                if (!_isLoadingConfig && Config != null)
                {
                    Config.OpcUa.AutoAcceptCertificates = _chkAutoAcceptCerts.Checked;
                    _configMgr.Save();
                }
            };

            var lblMaxSessions = new Label { Text = "最大会话数:", Location = new Point(15, 100), AutoSize = true };
            _nudMaxSessions = new NumericUpDown { Location = new Point(110, 97), Size = new Size(80, 25), Minimum = 1, Maximum = 200 };
            _nudMaxSessions.ValueChanged += (s, ev) =>
            {
                if (!_isLoadingConfig && Config != null)
                {
                    Config.OpcUa.MaxSessionCount = (int)_nudMaxSessions.Value;
                    _configMgr.Save();
                }
            };

            _lblEndpointUrl = new Label
            {
                Text = "端点 URL: 尚未配置", Location = new Point(15, 125), AutoSize = true,
                ForeColor = Theme.TextSecondary
            };

            grpUaSettings.Controls.AddRange(new Control[] {
                lblListenAddr, _cmbListenAddress, lblPort, _nudUaPort,
                lblSecurity, _cmbSecurityMode, _chkAutoAcceptCerts,
                lblMaxSessions, _nudMaxSessions, _lblEndpointUrl
            });
            Controls.Add(grpUaSettings);
            return y + 160;
        }
        private int BuildControlPanelSection(int y)
        {
            var grpControl = new GroupBox
            {
                Text = "控制面板",
                Location = new Point(10, y),
                Size = new Size(920, 130),
                BackColor = Theme.Surface
            };

            _btnStart = CreateButton("启动网关", Color.FromArgb(76, 175, 80), new Point(15, 25));
            _btnStart.Click += BtnStart_Click;

            _btnStop = CreateButton("停止网关", Color.FromArgb(244, 67, 54), new Point(15, 55));
            _btnStop.Enabled = false;
            _btnStop.Click += BtnStop_Click;

            _btnExportTags = CreateButton("导出标签", Color.FromArgb(33, 150, 243), new Point(15, 85));
            _btnExportTags.Click += BtnExportTags_Click;

            var lblAutoOptions = new Label { Text = "自动选项:", Location = new Point(140, 13), AutoSize = true, ForeColor = Theme.TextSecondary };
            _chkAutoConnectDa = new CheckBox { Text = "启动时连接 DA", Location = new Point(140, 33), AutoSize = true };
            _chkAutoConnectDa.CheckedChanged += (s, ev) =>
            {
                if (!_isLoadingConfig && Config != null) { Config.AutoConnectDa = _chkAutoConnectDa.Checked; _configMgr.Save(); }
            };

            _chkAutoStartUa = new CheckBox { Text = "启动时启动 UA", Location = new Point(140, 53), AutoSize = true };
            _chkAutoStartUa.CheckedChanged += (s, ev) =>
            {
                if (!_isLoadingConfig && Config != null) { Config.AutoStartUa = _chkAutoStartUa.Checked; _configMgr.Save(); }
            };

            _chkAutoStartWin = new CheckBox { Text = "开机启动", Location = new Point(140, 73), AutoSize = true };
            _chkAutoStartWin.CheckedChanged += (s, ev) =>
            {
                if (!_isLoadingConfig && Config != null)
                {
                    Config.AutoStartWithWindows = _chkAutoStartWin.Checked;
                    _autoStartMgr.SetAutoStart(_chkAutoStartWin.Checked);
                    _configMgr.Save();
                }
            };

            _chkEnableWatchdog = new CheckBox { Text = "看门狗守护", Location = new Point(140, 93), AutoSize = true };
            _chkEnableWatchdog.CheckedChanged += (s, ev) =>
            {
                if (!_isLoadingConfig && Config != null)
                {
                    Config.EnableWatchdog = _chkEnableWatchdog.Checked;
                    _configMgr.Save();
                    if (_watchdogMgr != null)
                    {
                        if (Config.EnableWatchdog) _watchdogMgr.Start();
                        else _watchdogMgr.Stop();
                    }
                }
            };

            _lblDaStatus = new Label { Text = "● DA: 未连接", Location = new Point(340, 15), AutoSize = true, ForeColor = Color.Gray };
            _lblUaStatus = new Label { Text = "● UA: 未启动", Location = new Point(340, 35), AutoSize = true, ForeColor = Color.Gray };
            _lblWatchdogStatus = new Label { Text = "● 守护: 未启动", Location = new Point(340, 55), AutoSize = true, ForeColor = Color.Gray };
            _lblLicenseStatus = new Label { Text = "", Location = new Point(340, 75), AutoSize = true, ForeColor = Color.Gray };
            _lblStats = new Label { Text = "", Location = new Point(340, 95), AutoSize = true, ForeColor = Color.Gray };

            var btnAbout = new Button
            {
                Text = "授权管理...", Location = new Point(780, 15), Size = new Size(120, 28),
                FlatStyle = FlatStyle.Flat, BackColor = Theme.Primary, ForeColor = Color.White, Cursor = Cursors.Hand
            };
            btnAbout.FlatAppearance.BorderSize = 0;
            btnAbout.Click += BtnAbout_Click;

            grpControl.Controls.AddRange(new Control[] {
                _btnStart, _btnStop, _btnExportTags,
                lblAutoOptions, _chkAutoConnectDa, _chkAutoStartUa,
                _chkAutoStartWin, _chkEnableWatchdog, _lblDaStatus, _lblUaStatus, _lblStats,
                _lblWatchdogStatus, _lblLicenseStatus, btnAbout
            });
            Controls.Add(grpControl);
            return y + 140;
        }
        private int BuildTagMonitorSection(int y)
        {
            var grpMonitor = new GroupBox
            {
                Text = "标签数据监控（实时）",
                Location = new Point(10, y),
                Size = new Size(920, 220),
                BackColor = Theme.Surface
            };

            _dgvTags = new DataGridView
            {
                Location = new Point(10, 20), Size = new Size(900, 195),
                AllowUserToAddRows = false, AllowUserToDeleteRows = false,
                ReadOnly = true, RowHeadersVisible = false,
                SelectionMode = DataGridViewSelectionMode.FullRowSelect,
                AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
                BackgroundColor = Theme.Surface, ForeColor = Theme.TextPrimary,
                BorderStyle = BorderStyle.None
            };
            _dgvTags.Columns.Add("TagKey", "标识");
            _dgvTags.Columns.Add("DisplayName", "显示名称");
            _dgvTags.Columns.Add("Value", "值");
            _dgvTags.Columns.Add("Quality", "质量");
            _dgvTags.Columns.Add("Timestamp", "时间戳");

            grpMonitor.Controls.Add(_dgvTags);
            Controls.Add(grpMonitor);
            return y + 230;
        }
        private void BuildLogSection(int y)
        {
            var grpLog = new GroupBox
            {
                Text = "运行日志",
                Location = new Point(10, y),
                Size = new Size(920, 200),
                BackColor = Theme.TerminalBg,
                ForeColor = Theme.TextOnDark,
                Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right
            };

            _txtLog = new TextBox
            {
                Location = new Point(10, 20), Size = new Size(900, 175),
                Multiline = true, ReadOnly = true, BackColor = Theme.TerminalBg,
                ForeColor = Theme.TextOnDark, Font = new Font("Consolas", 9f),
                BorderStyle = BorderStyle.None, ScrollBars = ScrollBars.Vertical
            };

            grpLog.Controls.Add(_txtLog);
            Controls.Add(grpLog);
        }
        private void InitializeManagers()
        {
            _log = new LogManager(_txtLog);
            _configMgr = new ConfigManager(_log);
            _autoStartMgr = new AutoStartManager(_log);
            _watchdogMgr = new WatchdogManager(_log);

            _refreshTimer = new Timer { Interval = AppConstants.UiDefaultRefreshMs };
            _refreshTimer.Tick += (s, e) => RefreshStats();
        }
        private void BuildTrayIcon()
        {
            var trayMenu = new ContextMenuStrip();
            trayMenu.Items.Add("显示主窗口", null, (s, e) => ShowMainWindow());
            trayMenu.Items.Add(new ToolStripSeparator());
            trayMenu.Items.Add("退出", null, (s, e) => ExitApplication());

            _notifyIcon = new NotifyIcon
            {
                Text = WindowTitle,
                Icon = Icon,
                ContextMenuStrip = trayMenu,
                Visible = true
            };
            _notifyIcon.DoubleClick += (s, e) => ShowMainWindow();
        }
        private void SetupEventHandlersAndAutoStart()
        {
            _healthTimer = new Timer { Interval = AppConstants.HealthCheckIntervalMs };
            _healthTimer.Tick += (s, e) => HealthCheck();
            _healthTimer.Start();

            // 窗口关闭事件
            FormClosing += MainForm_FormClosing;
            Resize += (s, e) =>
            {
                if (WindowState == FormWindowState.Minimized)
                    HideToTray();
            };

            // 导出标签
            _btnExportTags.Enabled = Config?.OpcDa?.Tags != null && Config.OpcDa.Tags.Count > 0;

            // 启动自动连接/启动
            if (Config != null)
            {
                if (Config.AutoConnectDa || Config.AutoStartUa)
                    _log.Append("检测到自动选项，将在 1 秒后自动启动网关...");
            }
        }


        // ================================================================
        //  服务器选择 & 点位获取
        // ================================================================

        private void BtnBrowse_Click(object sender, EventArgs e)
        {
            using (var dialog = new ServerSelectionDialog(_txtProgId.Text.Trim()))
            {
                var result = dialog.ShowDialog(this);
                if (result == DialogResult.OK && !string.IsNullOrEmpty(dialog.SelectedProgId))
                {
                    _txtProgId.Text = dialog.SelectedProgId;
                    if (dialog.SelectedServer != null)
                    {
                        _lblCurrentServer.Text = dialog.SelectedServer.Description ?? "";
                        _lblCurrentServer.ForeColor = Color.DarkGreen;
                    }
                    _configMgr.SaveProgId(dialog.SelectedProgId);
                    UpdateDaButtonsState();
                }
            }
        }

        private async void BtnFetchTags_Click(object sender, EventArgs e)
        {
            string progId = _txtProgId.Text.Trim();
            if (string.IsNullOrEmpty(progId))
            {
                MessageBox.Show("请先选择 OPC DA 服务器！\n点击\"浏览...\"按钮扫描本机服务器。",
                    "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            _btnFetchTags.Enabled = false;
            _lblCurrentServer.Text = "正在获取点位...";
            _lblCurrentServer.ForeColor = Color.Blue;
            _log.Append($"正在从 [{progId}] 获取所有点位...");

            try
            {
                using (var dialog = new ItemSelectionDialog(progId, _log.Append, Config.OpcUa.NamespaceIndex))
                {
                    _btnFetchTags.Enabled = true;

                    var result = dialog.ShowDialog(this);

                    if (result == DialogResult.OK && dialog.SelectedTags != null && dialog.SelectedTags.Count > 0)
                    {
                        foreach (var tag in dialog.SelectedTags)
                            tag.DisplayName = $"{progId}_{tag.ItemId}";

                        Config.OpcDa.Tags = dialog.SelectedTags;
                        UpdateTagGrid(dialog.SelectedTags);
                        _configMgr.SaveTagsImmediate();
                        _configMgr.Save();

                        _lblCurrentServer.Text = $"已获取 {dialog.SelectedTags.Count} 个点位";
                        _lblCurrentServer.ForeColor = Color.DarkGreen;
                        _log.Append($"已保存 {dialog.SelectedTags.Count} 个点位到配置");
                    }
                    else
                    {
                        _lblCurrentServer.Text = "获取点位已取消";
                        _lblCurrentServer.ForeColor = Color.Gray;
                    }
                }
            }
            catch (Exception ex)
            {
                _log.Append($"获取点位失败: {ex.Message}");
                MessageBox.Show($"获取点位失败:\n{ex.Message}", "错误",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
                _lblCurrentServer.Text = "获取点位失败";
                _lblCurrentServer.ForeColor = Color.Red;
            }
            finally
            {
                _btnFetchTags.Enabled = true;
            }
        }

        private void UpdateDaButtonsState()
        {
            bool hasProgId = !string.IsNullOrEmpty(_txtProgId?.Text?.Trim());
            _btnFetchTags.Enabled = hasProgId;
        }

        private void UpdateEndpointUrlLabel()
        {
            if (_lblEndpointUrl != null && Config?.OpcUa != null)
                _lblEndpointUrl.Text = $"端点 URL: {Config.OpcUa.GetEndpointUrl()}";
        }

        private void UpdateTagGrid(List<TagConfig> tags)
        {
            if (_dgvTags == null) return;
            _dgvTags.Rows.Clear();
            if (tags == null) return;
            foreach (var tag in tags)
                _dgvTags.Rows.Add(tag.TagKey, tag.DisplayName, "", "", "");
        }

        private static Button CreateButton(string text, Color color, Point location)
        {
            return new Button
            {
                Text = text,
                Location = location,
                Size = new Size(110, 28),
                FlatStyle = FlatStyle.Flat,
                BackColor = color,
                ForeColor = Color.White,
                Cursor = Cursors.Hand,
                FlatAppearance = { BorderSize = 0 }
            };
        }

                private void SetUiRunningState(bool isRunning)
        {
            _btnStart.Enabled = !isRunning;
            _btnBrowse.Enabled = !isRunning;
            _btnFetchTags.Enabled = !isRunning;
            _btnExportTags.Enabled = true;
            _cmbDaMode.Enabled = !isRunning;
            _cmbListenAddress.Enabled = !isRunning;
            _nudUaPort.Enabled = !isRunning;
            _cmbSecurityMode.Enabled = !isRunning;
            _chkAutoAcceptCerts.Enabled = !isRunning;
            _nudMaxSessions.Enabled = !isRunning;
            _chkAutoConnectDa.Enabled = !isRunning;
            _chkAutoStartUa.Enabled = !isRunning;
            _chkAutoStartWin.Enabled = !isRunning;
            _chkEnableWatchdog.Enabled = !isRunning;
        }

        private void BtnAbout_Click(object sender, EventArgs e)
        {
            using (var about = new AboutDialog(LicenseAlgorithm.GeneratePCID(), _licenseMgr?.IsLicensed ?? false))
            {
                if (about.ShowDialog(this) == DialogResult.OK && !string.IsNullOrEmpty(about.AuthorizationCode))
                    _licenseMgr.ApplyAuthorizationCode(about.AuthorizationCode);
            }
        }

        // ================================================================
        //  健康检查
        // ================================================================

        private void HealthCheck()
        {
            _gatewayMgr?.CheckHealth();
        }

        private void ScheduleAutoStartIfNeeded()
        {
            if (Config?.AutoStartUa != true || Config.OpcDa?.Tags?.Count <= 0)
                return;

            _log.Append("[自动启动] 检测到自动启动选项已启用，1 秒后启动网关...");
            var timer = new Timer { Interval = 1000 };
            EventHandler tick = null;
            tick = (sender, e) =>
            {
                timer.Stop();
                timer.Tick -= tick;
                timer.Dispose();
                if (ReferenceEquals(_autoStartTimer, timer))
                    _autoStartTimer = null;

                HideToTray();
                BtnStart_Click(null, EventArgs.Empty);
            };

            _autoStartTimer = timer;
            timer.Tick += tick;
            timer.Start();
        }

        private void MainForm_FormClosing(object sender, FormClosingEventArgs e)
        {
            if (!_forceClose && e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                HideToTray();
            }
        }

        private void ShowMemoryAlert(string msg, int severity)
        {
            if (severity >= 2)
                _log.Append($"[内存告警] {msg}");
        }
        private void LoadConfiguration()
        {
            if (!_configMgr.Load())
            {
                // 配置加载失败时禁用所有交互控件，防止 null 引用
                _btnStart.Enabled = false;
                _btnStop.Enabled = false;
                _btnExportTags.Enabled = false;

                _chkAutoConnectDa.Enabled = false;
                _chkAutoStartUa.Enabled = false;
                _chkEnableWatchdog.Enabled = false;
                _chkAutoStartWin.Enabled = false;
                return;
            }

            // 显示当前服务器 ProgId
            _txtProgId.Text = Config.OpcDa.ServerProgId ?? "";

            _log.Append("配置加载成功");
            _log.CleanupOldFiles();
            _log.Append($"  OPC DA 服务器: {Config.OpcDa.ServerProgId}");
            _log.Append($"  刷新频率: {Config.OpcDa.UpdateRateMs} ms");
            _log.Append($"  数据获取: {Config.OpcDa.GetEffectiveMode()}");
            _log.Append($"  标签数量: {Config.OpcDa.Tags?.Count ?? 0}");
            _log.Append($"  OPC UA 端口: {Config.OpcUa.Port}");
            _log.Append($"  OPC UA 监听: {Config.OpcUa.GetEffectiveListenAddress()}");
            _log.Append($"  OPC UA 安全: {Config.OpcUa.SecurityMode ?? "None"}");
            _log.Append($"  OPC UA 端点: {Config.OpcUa.GetEndpointUrl()}");
            _log.Append($"  上次连接: {Config.LastConnectedProgId ?? "无"}");
            _log.Append($"  自动连接 DA: {Config.AutoConnectDa}");
            _log.Append($"  自动启动网关: {Config.AutoStartUa}");
            _log.Append($"  开机启动: {Config.AutoStartWithWindows}");
            _log.Append($"  进程守护: {Config.EnableWatchdog}");

            // 加载自动选项的复选框状态（用标志位防止触发保存）
            _isLoadingConfig = true;
            _cmbDaMode.SelectedItem = Config.OpcDa.GetEffectiveMode() == DaAcquisitionMode.Sync ? "同步轮询" : "异步订阅";
            _chkAutoConnectDa.Checked = Config.AutoConnectDa;
            _chkAutoStartUa.Checked = Config.AutoStartUa;
            _chkAutoStartWin.Checked = Config.AutoStartWithWindows;
            _chkEnableWatchdog.Checked = Config.EnableWatchdog;

            string listenAddr = Config.OpcUa.GetEffectiveListenAddress();
            _cmbListenAddress.SelectedItem = _cmbListenAddress.Items.Contains(listenAddr) ? (object)listenAddr : "localhost";
            _nudUaPort.Value = Config.OpcUa.Port > 0 ? Config.OpcUa.Port : 4840;

            string secMode = Config.OpcUa.SecurityMode ?? "None";
            _cmbSecurityMode.SelectedItem = _cmbSecurityMode.Items.Contains(secMode) ? (object)secMode : "None";
            _chkAutoAcceptCerts.Checked = Config.OpcUa.AutoAcceptCertificates;
            _nudMaxSessions.Value = Config.OpcUa.GetEffectiveMaxSessionCount();
            UpdateEndpointUrlLabel();

            _isLoadingConfig = false;

            // 初始化网关管理器
            _gatewayMgr = new GatewayManager(_log, Config);
            _healthSnapshot = new HealthSnapshot(_gatewayMgr, _log);
            _healthSnapshot.OnAlert += (msg, severity) =>
            {
                SafeInvoke(() => ShowMemoryAlert(msg, severity));
            };

            _gatewayMgr.DaStatusChanged += (text, color) =>
            {
                SafeInvoke(() => { _lblDaStatus.Text = text; _lblDaStatus.ForeColor = color; });
            };
            _gatewayMgr.UaStatusChanged += (text, color) =>
            {
                SafeInvoke(() => { _lblUaStatus.Text = text; _lblUaStatus.ForeColor = color; });
            };
            _watchdogMgr.StatusChanged += (text, color) =>
            {
                SafeInvoke(() => { _lblWatchdogStatus.Text = text; _lblWatchdogStatus.ForeColor = color; });
            };

            _gatewayMgr.ConfigDirty += () => _configMgr?.Save();

            // H-40: 订阅配置文件外部修改事件
            _configMgr.ConfigFileChanged += () =>
            {
                if (_gatewayMgr?.IsRunning == true)
                {
                    _log.Append("[配置] 检测到外部修改，请重启网关以应用新配置");
                    SafeInvoke(() =>
                    {
                        _lblCurrentServer.Text = "(配置已变更，需重启)";
                        _lblCurrentServer.ForeColor = Color.Orange;
                    });
                }
                else
                {
                    _log.Append("[配置] 网关未运行，自动应用外部修改");
                    SafeInvoke(() =>
                    {
                        _isLoadingConfig = true;
                        if (_configMgr.Load())
                        {
                            string listenAddr = Config.OpcUa?.ListenAddress ?? "localhost";
                            _cmbListenAddress.SelectedItem = _cmbListenAddress.Items.Contains(listenAddr) ? (object)listenAddr : "localhost";
                            _nudUaPort.Value = (Config.OpcUa?.Port > 0) ? Config.OpcUa.Port : 4840;
                            UpdateEndpointUrlLabel();
                            if (Config.OpcDa.Tags != null)
                                UpdateTagGrid(Config.OpcDa.Tags);
                        }
                        _isLoadingConfig = false;
                    });
                }
            };

            _gatewayMgr.RunningStateChanged += (isRunning) =>
            {
                // 该事件由 GatewayManager 后台线程（StartAsync 经 ConfigureAwait(false) 后的延续）
                // 触发，直接操作控件会抛 Cross-thread 异常。统一经 SafeInvoke 封送回 UI 线程。
                SafeInvoke(() => SetUiRunningState(isRunning));
            };

            // 恢复上次连接的 ProgId
            if (string.IsNullOrEmpty(_txtProgId.Text) && !string.IsNullOrEmpty(Config.LastConnectedProgId))
            {
                _txtProgId.Text = Config.LastConnectedProgId;
                _lblCurrentServer.Text = "(上次连接)";
                _lblCurrentServer.ForeColor = Color.DarkGreen;
            }

            // 配置加载后，根据 ProgId 是否非空启用按钮
            UpdateDaButtonsState();

            if (Config.OpcDa.Tags != null)
                UpdateTagGrid(Config.OpcDa.Tags);

            if (Config.EnableWatchdog)
            {
                _watchdogMgr.Start();
                _log.Append("[守护] 已开启进程守护");
            }

            if (Config.AutoConnectDa && !Config.AutoStartUa && !string.IsNullOrEmpty(Config.OpcDa.ServerProgId))
            {
                _log.Append($"[自动连接] 已恢复到上次连接的服务器: {Config.LastConnectedProgId}");
            }

            // 初始化授权管理器
            _licenseMgr = new LicenseManager(_log, _configMgr, () => { });
            _licenseMgr.StatusChanged += (text, color) =>
            {
                SafeInvoke(() => { _lblLicenseStatus.Text = text; _lblLicenseStatus.ForeColor = color; });
            };
            Func<Task> handleTrialExpiredAsync = async () =>
            {
                // 试用到期，停止网关 (在 UI 线程上执行)
                _log.Append("[授权] 正在停止网关...");
                _btnStop.Enabled = false;
                try
                {
                    if (_gatewayMgr?.IsRunning == true)
                        await _gatewayMgr.StopAsync();
                }
                catch (Exception ex)
                {
                    _log.Append($"[授权] 停止网关失败: {ex.Message}");
                }

                _btnStart.Enabled = false;
                _btnBrowse.Enabled = false;
                _btnFetchTags.Enabled = false;


                MessageBox.Show(
                    "软件试用期（30 分钟）已到，网关已自动停止。\n\n" +
                    "请获取授权码后在\"关于\"窗口中输入以继续使用。\n" +
                    "关闭此提示后程序将退出。",
                    "试用到期", MessageBoxButtons.OK, MessageBoxIcon.Warning);

                _forceClose = true;
                _notifyIcon.Visible = false;
                Close();
            };
            _licenseMgr.GatewayStopRequested += async () => await handleTrialExpiredAsync();
            if (_licenseMgr.IsTrialExpired)
                _ = handleTrialExpiredAsync();
            else
                ScheduleAutoStartIfNeeded();
        }

        // ================================================================
        //  启动 / 停止网关
        // ================================================================

        private async void BtnStart_Click(object sender, EventArgs e)
        {
            if (Config == null) return;

            if (_licenseMgr.IsTrialExpired)
            {
                MessageBox.Show(
                    "软件试用期已到（30 分钟），网关无法继续运行。\n\n请获取授权码后在\"关于\"窗口中输入以继续使用。",
                    "授权限制", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            string progId = _txtProgId.Text.Trim();

            try
            {
                if (string.IsNullOrEmpty(progId))
                    throw new InvalidOperationException("请先选择 OPC DA 服务器！\n点击\"浏览...\"按钮扫描本机已安装的 OPC DA 服务器。");

                Config.OpcDa.ServerProgId = progId;

                if (Config.OpcDa?.Tags == null || Config.OpcDa.Tags.Count == 0)
                    throw new InvalidOperationException("尚未获取 OPC DA 服务器的点位！\n请先点击\"获取点位...\"按钮浏览并选择要桥接的标签。");

                _log.Append("========================================");
                _log.Append("正在启动网关...");

                // V1.8.1: 进度回调 — 通过 SynchronizationContext.Post 将进度报告安全地投递到 UI 线程，
                // 确保日志框实时更新而不会阻塞 UI。
                var syncCtx = System.Threading.SynchronizationContext.Current;
                Action<string> report = null;
                report = msg =>
                {
                    if (syncCtx != null)
                        syncCtx.Post(_ => _log.Append(msg), null);
                    else
                        _log.Append(msg);
                };

                // 启动网关
                await _gatewayMgr.StartAsync(report);

                Config.LastConnectedProgId = progId;
                _configMgr.Save();
                _log.Append("========================================");

                _refreshTimer.Start();
                _healthTimer.Start();

                _btnStop.Enabled = true;
                _btnExportTags.Enabled = true;
            }
            catch (InvalidOperationException ex)
            {
                // 预条件验证失败（如无 ProgId、无标签）
                MessageBox.Show(ex.Message, "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                // M1 修复：启动异常已由 GatewayManager 回滚资源并设置 Error 状态，
                // 此处需要提示用户调用 ClearErrorState() 后才能重试。
                _log.Append($"启动失败: {ex.Message}");
                MessageBox.Show($"启动失败:\n\n{ex.Message}\n\n请检查日志后点击\"清除错误状态\"按钮重试。", "错误",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
                _refreshTimer?.Stop();
            }
        }

        private async void BtnStop_Click(object sender, EventArgs e)
        {
            _btnStop.Enabled = false;
            _refreshTimer.Stop();
            _healthTimer?.Stop();

            try
            {
                await _gatewayMgr.StopAsync();
            }
            catch (Exception ex)
            {
                _log.Append($"停止网关失败: {ex.Message}");
            }
        }

        // ================================================================
        //  导出 OPC UA 点表完整信息
        // ================================================================

        private async void BtnExportTags_Click(object sender, EventArgs e)
        {
            if (Config?.OpcDa?.Tags == null || Config.OpcDa.Tags.Count == 0)
            {
                MessageBox.Show("当前没有点位可导出。\n请先获取点位。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            using (var sfd = new SaveFileDialog())
            {
                sfd.Title = "导出点位配置 CSV";
                sfd.Filter = "CSV 文件 (*.csv)|*.csv";
                sfd.FileName = $"Tags_{Config.OpcDa.ServerProgId}_{DateTime.Now:yyyyMMdd_HHmmss}.csv";
                sfd.DefaultExt = "csv";
                if (sfd.ShowDialog(this) == DialogResult.OK)
                {
                    _btnExportTags.Enabled = false;
                    try
                    {
                        await Task.Run(() =>
                        {
                            ushort nsIndex = _gatewayMgr?.UaServer != null && _gatewayMgr.UaServer.NamespaceIndex > 0 ? _gatewayMgr.UaServer.NamespaceIndex : (ushort)(Config?.OpcUa?.NamespaceIndex ?? 2);
                            bool ok = CsvTagExporter.ExportToFile(Config.OpcDa.Tags, Config.OpcDa.ServerProgId, sfd.FileName, nsIndex, _log);
                            if (!ok) throw new InvalidOperationException("导出失败，请查看日志。");
                        });
                        MessageBox.Show($"CSV 导出成功！\n文件: {sfd.FileName}\n共 {Config.OpcDa.Tags.Count} 个标签", "导出成功", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show($"导出失败:\n{ex.Message}", "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    }
                    finally
                    {
                        _btnExportTags.Enabled = true;
                    }
                }
            }
        }
        /// <summary>
        /// 定时刷新：从 DataBridge 获取快照缓存，更新 UI 统计标签。
        /// </summary>
        private void RefreshStats()
        {
            if (_gatewayMgr?.Bridge == null) return;

            _cachedSnapshots = _gatewayMgr.Bridge.GetSnapshots();
            _dgvTags.RowCount = _cachedSnapshots.Count;
            _dgvTags.Invalidate();

            // 自适应刷新间隔
            int currentHash = _cachedSnapshots != null ? GetSnapshotHash(_cachedSnapshots) : 0;
            bool hasChange = currentHash != _lastSnapshotHash;
            _lastSnapshotHash = currentHash;

            // 更新统计标签
            var bridge = _gatewayMgr.Bridge;
            long totalUpdates = bridge.TotalUpdates;
            int errorCount = bridge.ErrorCount;
            _lblStats.Text = $"更新: {totalUpdates} | 错误: {errorCount}";

            // 无变化时降低刷新频率
            if (!hasChange && _refreshTimer.Interval < AppConstants.UiSlowRefreshMs)
                _refreshTimer.Interval = AppConstants.UiSlowRefreshMs;
            else if (hasChange && _refreshTimer.Interval >= AppConstants.UiSlowRefreshMs)
                _refreshTimer.Interval = AppConstants.UiDefaultRefreshMs;
        }

        /// <summary>计算快照特征哈希，用于检测数据是否变化。</summary>
        private static int GetSnapshotHash(IReadOnlyList<TagSnapshot> snapshots)
        {
            if (snapshots == null || snapshots.Count == 0) return 0;
            int hash = 17;
            foreach (var s in snapshots)
            {
                hash = hash * 31 + (s.Value?.GetHashCode() ?? 0);
            }
            return hash;
        }
        /// <summary>
        /// 虚拟模式回调：设置单元格显示样式（质量列颜色）。
        /// </summary>
        private void DgvTags_CellFormatting(object sender, DataGridViewCellFormattingEventArgs e)
        {
            if (e.ColumnIndex == 3 && e.Value is string quality) // 质量列
            {
                e.CellStyle.ForeColor = quality == "Good" ? Color.Green : Color.Red;
            }
        }

        // ================================================================
        //  系统托盘
        // ================================================================

        private void ShowMainWindow()
        {
            Show();
            ShowInTaskbar = true;
            WindowState = FormWindowState.Normal;
            BringToFront();
            Activate();
        }

        private void HideToTray()
        {
            Hide();
            ShowInTaskbar = false;
            _notifyIcon.ShowBalloonTip(2000, "OPC DA → OPC UA 网关",
                "程序已最小化到系统托盘，双击图标可打开主窗口。", ToolTipIcon.Info);
        }

        private void ExitApplication()
        {
            _forceClose = true;
            _notifyIcon.Visible = false;
            Close();
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            if (WindowState == FormWindowState.Minimized)
            {
                Hide();
                ShowInTaskbar = false;
            }
        }

        protected override async void OnFormClosing(FormClosingEventArgs e)
        {
            // 单一标志位控制关闭流程
            // 第一次 Close：用户点击关闭 → 最小化到托盘
            // 第二次 Close：异步关闭完成后的资源释放
            if (!_forceClose && e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                HideToTray();
                return;
            }

            if (!_isShuttingDown)
            {
                e.Cancel = true;
                _isShuttingDown = true;

                // 捕获局部引用后置 null，防止重复释放
                var refreshTimer = _refreshTimer;
                var healthTimer = _healthTimer;
                var autoStartTimer = _autoStartTimer;
                _refreshTimer = null;
                _healthTimer = null;
                _autoStartTimer = null;

                refreshTimer?.Stop();
                refreshTimer?.Dispose();
                healthTimer?.Stop();
                healthTimer?.Dispose();
                autoStartTimer?.Stop();
                autoStartTimer?.Dispose();

                // 发送优雅退出信号
                try { _watchdogMgr?.SignalGracefulExit(); }
                catch (Exception ex) { _log?.Append($"关闭时发送退出信号异常: {ex.Message}"); }

                // 异步关闭网关
                try
                {
                    if (_gatewayMgr != null)
                        await _gatewayMgr.StopAsync();
                }
                catch (Exception ex)
                {
                    _log?.Append($"关闭时停止网关异常: {ex.Message}");
                }

                // 标记强制关闭，触发第二次 Close 执行资源释放
                _forceClose = true;
                Close();
                return;
            }

            // 第二次 Close：执行资源释放
            _log?.Dispose();
            _notifyIcon?.Dispose();
            _licenseMgr?.Dispose();
            try { _healthSnapshot?.Dispose(); } catch { } // H-36
            try { _configMgr?.Dispose(); } catch { } // H-40 + V1.9.0: ConfigManager 现实现 IDisposable
            base.OnFormClosing(e);
        }

        // ================================================================
        //  辅助方法
        // ================================================================

        /// <summary>
        /// 线程安全地执行 UI 操作。
        /// 增加句柄防护：窗体未创建句柄或已释放时直接跳过，避免关闭/初始化期间
        /// 后台事件（如 HealthSnapshot 定时器、LicenseManager 试用到期）触发 Invoke 抛
        /// ObjectDisposedException 或跨线程异常。
        /// </summary>
        private void SafeInvoke(Action a)
        {
            if (IsDisposed || !IsHandleCreated) return;
            try
            {
                if (InvokeRequired) BeginInvoke(a); else a();
            }
            catch (ObjectDisposedException)
            {
                // 在检查 IsHandleCreated 和执行 a() 之间，窗体可能已被释放，
                // 安全忽略即可。
            }
            catch (InvalidOperationException)
            {
                // 跨线程调用目标已销毁时也忽略。
            }
        }

        /// <summary>
        /// CSV 字段转义：处理逗号、双引号和换行符。
        /// </summary>
        private static string EscapeCsv(string field)
        {
            if (string.IsNullOrEmpty(field)) return "";
            if (field.Contains(",") || field.Contains("\"") || field.Contains("\n"))
                return "\"" + field.Replace("\"", "\"\"") + "\"";
            return field;
        }
    }
}
