using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Forms;
using OpcDaToUaGateway.Models;

namespace OpcDaToUaGateway
{
    /// <summary>
    /// OPC DA 点位选择对话框
    /// 异步浏览服务器上的所有点位，以列表形式展示，用户可筛选、勾选后导入
    /// </summary>
    public class ItemSelectionDialog : Form
    {
        private ListView _listView;
        private TextBox _txtFilter;
        private Button _btnSelectAll;
        private Button _btnDeselectAll;
        private Button _btnExportCsv;
        private Button _btnImportCsv;
        private Button _btnOK;
        private Button _btnCancel;
        private Label _lblStatus;
        private ProgressBar _progressBar;
        private Label _lblElapsed;   // 新增：显示经过的秒数
        private Timer _timer;         // 新增：计时器

        private readonly string _serverProgId;
        private readonly Action<string> _logger;  // 日志委托，用于显示诊断信息
        private readonly ushort _namespaceIndex;   // OPC UA 命名空间索引，用于预分配 NodeId
        private List<OpcDaItemInfo> _allItems = new List<OpcDaItemInfo>();
        private List<OpcDaItemInfo> _displayItems = new List<OpcDaItemInfo>();  // 虚拟模式：当前显示的项（过滤后）

        // P1 修复：过滤防抖 Timer，避免每次击键都重建 ListView
        private Timer _filterDebounce;
        // P5 修复：缓存已勾选项的 ItemId 集合，过滤时保留勾选状态
        private readonly HashSet<string> _checkedItemIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // 计时器相关字段
        private DateTime _startTime;  // 浏览开始时间
        private int _elapsedSeconds;    // 经过的秒数

        /// <summary>
        /// 用户确认选择后的标签列表
        /// </summary>
        public List<TagConfig> SelectedTags { get; private set; }

        public ItemSelectionDialog(string serverProgId)
        {
            _serverProgId = serverProgId;
            BuildUI();
        }

        /// <summary>
        /// 创建浏览对话框，并传入日志委托用于显示诊断信息，以及命名空间索引用于预分配 NodeId。
        /// </summary>
        public ItemSelectionDialog(string serverProgId, Action<string> logger, ushort namespaceIndex = 2)
        {
            _serverProgId = serverProgId;
            _logger = logger;
            _namespaceIndex = namespaceIndex;
            BuildUI();
        }

        private void BuildUI()
        {
            Text = "OPC DA 点位浏览";
            Size = new Size(800, 560);
            StartPosition = FormStartPosition.CenterParent;
            MinimumSize = new Size(650, 420);
            Font = new Font("Microsoft YaHei UI", 9f);
            BackColor = Theme.FormBg;

            int y = 10;

            // ---- 顶部工具栏 ----
            var lblFilter = new Label
            {
                Text = "搜索筛选:",
                Location = new Point(10, y + 3),
                AutoSize = true
            };

            _txtFilter = new TextBox
            {
                Location = new Point(75, y),
                Size = new Size(300, 25)
            };
            _txtFilter.TextChanged += TxtFilter_TextChanged;

            _btnSelectAll = new Button
            {
                Text = "全选",
                Location = new Point(390, y - 1),
                Size = new Size(65, 27),
                FlatStyle = FlatStyle.Flat,
                Cursor = Cursors.Hand
            };
            _btnSelectAll.FlatAppearance.BorderColor = Theme.Border;
            _btnSelectAll.Click += (s, e) => SetAllChecked(true);

            _btnDeselectAll = new Button
            {
                Text = "取消全选",
                Location = new Point(460, y - 1),
                Size = new Size(80, 27),
                FlatStyle = FlatStyle.Flat,
                Cursor = Cursors.Hand
            };
            _btnDeselectAll.FlatAppearance.BorderColor = Theme.Border;
            _btnDeselectAll.Click += (s, e) => SetAllChecked(false);

            _btnExportCsv = new Button
            {
                Text = "导出 CSV",
                Location = new Point(555, y - 1),
                Size = new Size(85, 27),
                FlatStyle = FlatStyle.Flat,
                BackColor = Theme.Primary,
                ForeColor = Color.White,
                Cursor = Cursors.Hand
            };
            _btnExportCsv.FlatAppearance.BorderSize = 0;
            _btnExportCsv.Click += BtnExportCsv_Click;

            _btnImportCsv = new Button
            {
                Text = "导入 CSV",
                Location = new Point(650, y - 1),
                Size = new Size(85, 27),
                FlatStyle = FlatStyle.Flat,
                BackColor = Theme.Warning,
                ForeColor = Color.White,
                Cursor = Cursors.Hand
            };
            _btnImportCsv.FlatAppearance.BorderSize = 0;
            _btnImportCsv.Click += BtnImportCsv_Click;

            y += 35;

            // ---- 列表视图 ----
            _listView = new ListView
            {
                Location = new Point(10, y),
                Size = new Size(765, 400),
                View = View.Details,
                CheckBoxes = true,
                FullRowSelect = true,
                GridLines = true,
                MultiSelect = false,
                HideSelection = false,
                VirtualMode = true,  // 启用虚拟模式，避免 1万+ 项时 UI 卡死
                Font = new Font("Consolas", 9.5f),
                Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right
            };
            
            // 虚拟模式：只在需要显示时才创建 ListViewItem
            // 使用 _displayItems（过滤后的列表）而非 _allItems，以支持筛选
            _listView.RetrieveVirtualItem += (s, e) =>
            {
                if (e.ItemIndex < 0 || e.ItemIndex >= _displayItems.Count) return;
                
                var item = _displayItems[e.ItemIndex];
                var lvi = new ListViewItem(item.ItemId);
                lvi.SubItems.Add(item.Name ?? "");
                lvi.SubItems.Add(item.DataTypeName ?? AppConstants.UnknownDataType);
                lvi.SubItems.Add(item.Description ?? "");
                lvi.Checked = _checkedItemIds.Contains(item.ItemId);
                e.Item = lvi;
            };
            
            // 虚拟模式下处理勾选状态变化
            _listView.ItemCheck += (s, e) =>
            {
                if (e.Index < 0 || e.Index >= _displayItems.Count) return;
                
                var item = _displayItems[e.Index];
                if (e.NewValue == CheckState.Checked)
                    _checkedItemIds.Add(item.ItemId);
                else
                    _checkedItemIds.Remove(item.ItemId);
            };

            _listView.Columns.Add("ItemId", "ItemId", 320);
            _listView.Columns.Add("Name", "名称", 200);
            _listView.Columns.Add("Type", "数据类型", 80);
            _listView.Columns.Add("Desc", "描述", 140);

            y += 410;

            // ---- 底部状态栏 ----
            _lblStatus = new Label
            {
                Text = "准备浏览...",
                Location = new Point(10, y),
                Size = new Size(380, 20),
                Anchor = AnchorStyles.Bottom | AnchorStyles.Left
            };

            _lblElapsed = new Label
            {
                Text = "用时: 0秒",
                Location = new Point(400, y),
                Size = new Size(90, 20),
                TextAlign = ContentAlignment.MiddleLeft,
                ForeColor = Theme.Primary,
                Font = new Font("Microsoft YaHei UI", 9f, FontStyle.Bold),
                Anchor = AnchorStyles.Bottom | AnchorStyles.Left
            };

            _progressBar = new ProgressBar
            {
                Location = new Point(500, y),
                Size = new Size(80, 18),
                Style = ProgressBarStyle.Marquee,
                Visible = false,
                Anchor = AnchorStyles.Bottom | AnchorStyles.Right
            };

            // 初始化计时器（每秒触发一次）
            _timer = new Timer
            {
                Interval = 1000  // 1秒
            };
            _timer.Tick += Timer_Tick;

            _btnOK = new Button
            {
                Text = "确定导入",
                Location = new Point(590, y - 4),
                Size = new Size(85, 30),
                BackColor = Theme.Success,
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat,
                Enabled = false,
                Cursor = Cursors.Hand,
                Anchor = AnchorStyles.Bottom | AnchorStyles.Right
            };
            _btnOK.FlatAppearance.BorderSize = 0;
            _btnOK.Click += BtnOK_Click;

            _btnCancel = new Button
            {
                Text = "取消",
                Location = new Point(685, y - 4),
                Size = new Size(85, 30),
                FlatStyle = FlatStyle.Flat,
                Cursor = Cursors.Hand,
                Anchor = AnchorStyles.Bottom | AnchorStyles.Right
            };
            _btnCancel.FlatAppearance.BorderColor = Theme.Border;
            _btnCancel.Click += (s, e) => { DialogResult = DialogResult.Cancel; Close(); };
            _btnCancel.DialogResult = DialogResult.Cancel;

            AcceptButton = _btnOK;
            CancelButton = _btnCancel;

            Controls.AddRange(new Control[]
            {
                lblFilter, _txtFilter, _btnSelectAll, _btnDeselectAll, _btnExportCsv, _btnImportCsv,
                _listView,
                _lblStatus, _lblElapsed, _progressBar, _btnOK, _btnCancel
            });

            Load += (s, e) => BeginBrowse();
            
            // 注册窗体关闭事件，确保计时器资源被正确释放
            FormClosed += (s, e) =>
            {
                if (_timer != null)
                {
                    _timer.Stop();
                    _timer.Tick -= Timer_Tick;
                    _timer.Dispose();
                    _timer = null;
                }
            };
        }

        // ================================================================
        //  计时器事件：每秒更新显示
        // ================================================================
        private void Timer_Tick(object sender, EventArgs e)
        {
            _elapsedSeconds++;
            _lblElapsed.Text = $"用时: {_elapsedSeconds}秒";
        }

        // ================================================================
        //  异步浏览
        // ================================================================

        private void BeginBrowse()
        {
            _btnOK.Enabled = false;
            _progressBar.Visible = true;
            _lblStatus.Text = $"正在浏览 [{_serverProgId}] 的点位，请稍候...";
            
            // 虚拟模式：通过设置 VirtualListSize = 0 清空显示
            _listView.VirtualListSize = 0;

            // 启动计时器
            _elapsedSeconds = 0;
            _startTime = DateTime.Now;
            _lblElapsed.Text = "用时: 0秒";
            _timer.Start();

            // 使用 Task.Run 在后台线程执行递归浏览（遍历所有分支）
            Task.Run(() =>
            {
                try
                {
                    // 组合 logger：同时写入文件日志 + 更新 UI 状态标签
                    var items = OpcDaClient.BrowseAllItems(_serverProgId, "localhost", msg =>
                    {
                        // 所有诊断消息写入文件日志，方便离线排查
                        _logger?.Invoke($"[Browse] {msg}");
                        // UI 线程上更新状态标签（只显示关键信息，避免刷屏）
                        SafeBeginInvoke(() =>
                        {
                            if (msg.Contains("尝试连接") || msg.Contains("连接成功") ||
                                msg.Contains("浏览完成") || msg.Contains("失败"))
                            {
                                _lblStatus.Text = msg.Replace("[Browse] ", "");
                            }
                        });
                    });

                    SafeBeginInvoke(() => OnBrowseComplete(items));
                }
                catch (Exception ex)
                {
                    _logger?.Invoke($"[Browse] 异常: {ex.Message}");
                    SafeBeginInvoke(() => OnBrowseFailed(ex));
                }
            });
        }

        /// <summary>
        /// 线程安全的异步 UI 封送。后台线程通过 Task.Run 调用 BrowseAllItems 时，
        /// 首条日志可能在对话框窗口句柄创建前触发，此时裸调 BeginInvoke 会抛
        /// "在创建窗口句柄之前，不能在控件上调用 Invoke 或 BeginInvoke"。
        /// 此处与 MainForm.SafeInvoke 保持一致：句柄未就绪时订阅 HandleCreated，
        /// 待 UI 线程创建句柄后再执行，确保回调不丢且不崩溃。
        /// </summary>
        private void SafeBeginInvoke(Action a)
        {
            if (IsDisposed) return;
            if (IsHandleCreated)
            {
                BeginInvoke(a);
                return;
            }
            HandleCreated += OnHandleReady;
            void OnHandleReady(object sender, EventArgs e)
            {
                HandleCreated -= OnHandleReady;
                if (!IsDisposed) BeginInvoke(a);
            }
        }

        private void OnBrowseComplete(List<OpcDaItemInfo> items)
        {
            _timer.Stop();
            _progressBar.Visible = false;
            _lblElapsed.Text = $"总用时: {_elapsedSeconds}秒";
            _allItems.Clear();
            _displayItems.Clear();  // 清空显示列表
            _checkedItemIds.Clear();  // 清空勾选状态

            if (items != null && items.Count > 0)
            {
                // 虚拟模式：只存储数据，不创建 ListViewItem
                _allItems.AddRange(items);
                _displayItems.AddRange(items);  // 初始显示所有项
                foreach (var item in items)
                    _checkedItemIds.Add(item.ItemId);  // 新浏览的项默认勾选
                
                // 设置虚拟列表大小，触发 RetrieveVirtualItem 事件
                _listView.VirtualListSize = _displayItems.Count;
            }

            _lblStatus.Text = $"共找到 {_allItems.Count} 个点位";
            _btnOK.Enabled = _allItems.Count > 0;
            _logger?.Invoke($"[Browse] 浏览完成: 共 {_allItems.Count} 个点位, 用时 {_elapsedSeconds} 秒");

            if (_allItems.Count == 0)
            {
                _logger?.Invoke($"[Browse] ⚠️ 浏览返回 0 个点位！");
                MessageBox.Show(
                    "未在服务器上找到任何点位（标签）。\n" +
                    "可能该服务器没有公开点位，或者服务器未正常运行。",
                    "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
        }

        private void OnBrowseFailed(Exception ex)
        {
            _timer.Stop();
            _progressBar.Visible = false;
            _lblElapsed.Text = $"失败用时: {_elapsedSeconds}秒";
            _lblStatus.Text = "浏览失败";
            _logger?.Invoke($"[Browse] 浏览失败: {ex.Message}");

            MessageBox.Show(
                $"浏览 OPC DA 服务器点位失败:\n{ex.Message}\n\n" +
                "请确认:\n" +
                "  1. OPC DA 服务器正在运行\n" +
                "  2. 服务器 ProgId 正确\n" +
                "  3. 已安装 OPC Core Components" +
                "\n\n详细信息请查看运行日志",
                "浏览失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }

        // ================================================================
        //  筛选
        // ================================================================

        private void TxtFilter_TextChanged(object sender, EventArgs e)
        {
            // P1 修复：防抖 200ms，避免每次击键都重建 ListView
            _filterDebounce?.Dispose();
            _filterDebounce = new Timer { Interval = 200 };
            _filterDebounce.Tick += (s2, e2) =>
            {
                _filterDebounce?.Dispose();
                _filterDebounce = null;
                ApplyFilter();
            };
            _filterDebounce.Start();
        }

        private void ApplyFilter()
        {
            string filter = _txtFilter.Text.Trim();

            // 虚拟模式：过滤 _allItems，将匹配的项存入 _displayItems
            _displayItems.Clear();
            
            foreach (var item in _allItems)
            {
                bool match = string.IsNullOrEmpty(filter)
                    || item.ItemId.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0
                    || (item.Name != null && item.Name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0);

                if (match)
                {
                    _displayItems.Add(item);
                }
            }

            // 更新虚拟列表大小，触发 RetrieveVirtualItem 事件
            _listView.VirtualListSize = _displayItems.Count;

            // 大数据量时在状态栏显示过滤结果数量，帮助用户了解匹配比例
            if (!string.IsNullOrEmpty(filter))
                _lblStatus.Text = $"过滤结果: {_displayItems.Count} / {_allItems.Count} 个点位";
            else
                _lblStatus.Text = $"共 {_allItems.Count} 个点位";
        }

        // ================================================================
        //  全选 / 取消全选（虚拟模式版本）
        // ================================================================

        private void SetAllChecked(bool check)
        {
            if (check)
            {
                // 虚拟模式下，将当前显示的所有项添加到勾选集合
                foreach (var item in _displayItems)
                    _checkedItemIds.Add(item.ItemId);
            }
            else
            {
                // 虚拟模式下，从勾选集合中移除当前显示的所有项
                foreach (var item in _displayItems)
                    _checkedItemIds.Remove(item.ItemId);
            }
            
            // 刷新显示以更新勾选状态
            _listView.Refresh();
        }

        // ================================================================
        //  导出 CSV：将当前勾选的点位导出为表格（虚拟模式版本）
        // ================================================================

        private void BtnExportCsv_Click(object sender, EventArgs e)
        {
            // 虚拟模式：遍历 _displayItems，通过 _checkedItemIds 判断勾选状态
            var checkedItems = new List<OpcDaItemInfo>();
            foreach (var item in _displayItems)
            {
                if (_checkedItemIds.Contains(item.ItemId))
                {
                    checkedItems.Add(item);
                }
            }

            if (checkedItems.Count == 0)
            {
                MessageBox.Show("没有勾选任何点位，无法导出。", "提示",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            using (var sfd = new SaveFileDialog())
            {
                sfd.Title = "导出已选点位";
                sfd.Filter = "CSV 文件 (*.csv)|*.csv";
                sfd.FileName = $"点位_{_serverProgId}_{DateTime.Now:yyyyMMdd_HHmmss}.csv";
                sfd.DefaultExt = "csv";

                if (sfd.ShowDialog(this) == DialogResult.OK)
                {
                    try
                    {
                        var sb = new System.Text.StringBuilder();
                        // 头部行 1~3（5 列格式，与 CsvTagExporter 一致）
                        sb.AppendLine(MakeRow("#不用改", "#服务器:", _serverProgId ?? "", "", ""));
                        sb.AppendLine(MakeRow("#不用改", "#导出时间:", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"), "", ""));
                        sb.AppendLine(MakeRow("#修改C3", "#已选点位:", checkedItems.Count.ToString(), "", ""));
                        // 第 4~5 行：空行
                        sb.AppendLine(",,,,");
                        sb.AppendLine(",,,,");
                        // 表头行
                        sb.AppendLine(MakeRow("序号", "ItemId", "名称", "数据类型", "描述"));

                        int index = 1;
                        foreach (var item in checkedItems)
                        {
                            sb.AppendLine(MakeRow(index.ToString(), EscapeCsv(item.ItemId), EscapeCsv(item.Name), EscapeCsv(item.DataTypeName), EscapeCsv(item.Description)));
                            index++;
                        }

                        File.WriteAllText(sfd.FileName, sb.ToString(), new System.Text.UTF8Encoding(true));

                        _lblStatus.Text = $"已导出 {checkedItems.Count} 个点位到: {Path.GetFileName(sfd.FileName)}";
                        MessageBox.Show(
                            $"导出成功！\n文件: {sfd.FileName}\n共 {checkedItems.Count} 个点位",
                            "导出成功", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show($"导出失败:\n{ex.Message}", "错误",
                            MessageBoxButtons.OK, MessageBoxIcon.Error);
                    }
                }
            }
        }

        // ================================================================
        //  导入 CSV：从 CSV 读取点位列表，在列表中勾选对应项
        // ================================================================

        private void BtnImportCsv_Click(object sender, EventArgs e)
        {
            using (var ofd = new OpenFileDialog())
            {
                ofd.Title = "导入点位 CSV";
                ofd.Filter = "CSV 文件 (*.csv)|*.csv|所有文件 (*.*)|*.*";
                ofd.DefaultExt = "csv";

                if (ofd.ShowDialog(this) == DialogResult.OK)
                {
                    try
                    {
                        string[] lines = File.ReadAllLines(ofd.FileName, DetectEncoding(ofd.FileName));

                        // 解析 CSV：跳过固定头部行（#不用改、#导出时间、#已选点位）
                        // 以及空行和表头行，从第7行开始读取数据
                        var importItemIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        int expectedCount = 0;
                        bool headerPassed = false;
                        int dataRowCount = 0;

                        for (int i = 0; i < lines.Length; i++)
                        {
                            string line = lines[i].Trim();
                            if (string.IsNullOrEmpty(line)) continue;

                            // 提取 #已选点位: 后面的数字（第3行）
                            if (line.Contains("#已选点位:"))
                            {
                                string countStr = ParseHeaderLine(line, "#已选点位:");
                                int.TryParse(countStr, out expectedCount);
                                continue;
                            }

                            // 跳过所有头部注释行（#不用改、#修改C3 等）
                            if (line.StartsWith("#")) continue;

                            // 跳过空行（,,,,）
                            if (line.TrimEnd(',', ' ') == "") continue;

                            // 跳过表头行
                            if (!headerPassed)
                            {
                                if (line.StartsWith("序号") && line.Contains("ItemId"))
                                {
                                    headerPassed = true;
                                    continue;
                                }
                                // 如果不是表头行，说明还没到数据区，跳过
                                continue;
                            }

                            // 数据行：解析 CSV，取第二列（B列）作为 ItemId
                            string[] cols = ParseCsvLine(line);
                            if (cols.Length >= 2)
                            {
                                string itemId = cols[1].Trim(); // B 列：ItemId
                                if (!string.IsNullOrEmpty(itemId))
                                {
                                    importItemIds.Add(itemId);
                                    dataRowCount++;
                                }
                            }
                        }

                        if (importItemIds.Count == 0)
                        {
                            MessageBox.Show("CSV 文件中未找到有效的点位数据。",
                                "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
                            return;
                        }

                        // 校验已选点位数量
                        if (expectedCount > 0 && dataRowCount != expectedCount)
                        {
                            var result = MessageBox.Show(
                                $"⚠️ 点位数量不一致！\n\n" +
                                $"表格中填写的已选点位: {expectedCount}\n" +
                                $"实际数据行数: {dataRowCount}\n\n" +
                                $"将以实际数据为准继续导入。\n" +
                                $"请修改表格中的已选点位数量后重试。\n\n" +
                                "是否继续导入？",
                                "数量校验警告",
                                MessageBoxButtons.YesNo,
                                MessageBoxIcon.Warning);
                            if (result != DialogResult.Yes)
                                return;
                        }

                        // 在列表中勾选匹配的点位，取消不匹配的
                        int matchCount = 0;
                        
                        // 虚拟模式：更新 _checkedItemIds，然后刷新显示
                        // 先清空当前显示项的勾选状态
                        foreach (var item in _displayItems)
                            _checkedItemIds.Remove(item.ItemId);
                        
                        // 勾选匹配的项
                        foreach (var item in _allItems)
                        {
                            if (importItemIds.Contains(item.ItemId))
                            {
                                _checkedItemIds.Add(item.ItemId);
                                matchCount++;
                            }
                        }
                        
                        // 刷新显示以更新勾选状态
                        _listView.Refresh();

                        _lblStatus.Text = $"从 CSV 导入: 匹配并勾选了 {matchCount} 个点位 (CSV 共 {importItemIds.Count} 条)";
                        _btnOK.Enabled = _listView.Items.Count > 0;

                        if (matchCount < importItemIds.Count)
                        {
                            MessageBox.Show(
                                $"CSV 中有 {importItemIds.Count} 个点位，在当前列表中匹配到 {matchCount} 个并已勾选。\n" +
                                $"未匹配的 {importItemIds.Count - matchCount} 个点位可能不存在于该服务器。",
                                "导入结果", MessageBoxButtons.OK, MessageBoxIcon.Information);
                        }
                        else
                        {
                            MessageBox.Show(
                                $"导入成功！已勾选 {matchCount} 个点位。\n点击\"确定导入\"完成操作。",
                                "导入结果", MessageBoxButtons.OK, MessageBoxIcon.Information);
                        }
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show($"导入失败:\n{ex.Message}", "错误",
                            MessageBoxButtons.OK, MessageBoxIcon.Error);
                    }
                }
            }
        }

        // ================================================================
        //  CSV 辅助方法
        // ================================================================

        /// <summary>将 5 个字段组合成一行 CSV。</summary>
        private static string MakeRow(string colA, string colB, string colC, string colD, string colE)
        {
            return $"{EscapeCsv(colA)},{EscapeCsv(colB)},{EscapeCsv(colC)},{EscapeCsv(colD)},{EscapeCsv(colE)}";
        }

        /// <summary>从头部行中提取指定标签后的值。</summary>
        private static string ParseHeaderLine(string line, string label)
        {
            int idx = line.IndexOf(label, StringComparison.Ordinal);
            if (idx < 0) return "";
            return line.Substring(idx + label.Length).Trim();
        }

        /// <summary>检测文件编码，支持 UTF-8 BOM / UTF-16 LE BOM / ANSI。</summary>
        private static System.Text.Encoding DetectEncoding(string filePath)
        {
            byte[] bytes = System.IO.File.ReadAllBytes(filePath);
            if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
                return new System.Text.UTF8Encoding(true);
            if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
                return System.Text.Encoding.Unicode; // UTF-16 LE
            if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
                return System.Text.Encoding.BigEndianUnicode; // UTF-16 BE
            return System.Text.Encoding.Default;
        }

        /// <summary>CSV 字段转义：处理逗号、双引号和换行符。</summary>
        private static string EscapeCsv(string field)
        {
            if (string.IsNullOrEmpty(field)) return "";
            if (field.Contains(",") || field.Contains("\"") || field.Contains("\n"))
            {
                return "\"" + field.Replace("\"", "\"\"") + "\"";
            }
            return field;
        }

        /// <summary>
        /// 简易 CSV 行解析，支持双引号包裹的字段
        /// </summary>
        private static string[] ParseCsvLine(string line)
        {
            var fields = new List<string>();
            int i = 0;
            while (i < line.Length)
            {
                if (line[i] == '"')
                {
                    // 引号包裹的字段
                    i++; // 跳过开头引号
                    var sb = new System.Text.StringBuilder();
                    while (i < line.Length)
                    {
                        if (line[i] == '"')
                        {
                            if (i + 1 < line.Length && line[i + 1] == '"')
                            {
                                sb.Append('"');
                                i += 2;
                            }
                            else
                            {
                                i++; // 跳过结尾引号
                                break;
                            }
                        }
                        else
                        {
                            sb.Append(line[i]);
                            i++;
                        }
                    }
                    fields.Add(sb.ToString());
                    // 跳过逗号
                    if (i < line.Length && line[i] == ',') i++;
                }
                else
                {
                    // 普通字段
                    int start = i;
                    while (i < line.Length && line[i] != ',') i++;
                    fields.Add(line.Substring(start, i - start));
                    if (i < line.Length) i++; // 跳过逗号
                }
            }
            return fields.ToArray();
        }

        // ================================================================
        //  确认选择
        // ================================================================

        private void BtnOK_Click(object sender, EventArgs e)
        {
            SelectedTags = new List<TagConfig>();

            // 虚拟模式：遍历 _allItems，通过 _checkedItemIds 判断勾选状态
            foreach (var item in _allItems)
            {
                if (_checkedItemIds.Contains(item.ItemId))
                {
                    // H3 修复：传递浏览获取到的实际数据类型，而非硬编码 "Variant"
                    SelectedTags.Add(new TagConfig
                    {
                        ItemId = item.ItemId,
                        DisplayName = item.Name,
                        DataType = item.DataTypeName ?? AppConstants.UnknownDataType
                    });
                }
            }

            if (SelectedTags.Count == 0)
            {
                MessageBox.Show(
                    "请至少勾选一个点位！",
                    "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            // 确定导入后立即分配所有点位的运行时唯一索引（TagKey）
            TagConfig.AssignTagKeys(SelectedTags);

            // 预分配 OPC UA NodeId — 在导入时即确定每个点位的 NodeId 并持久化到配置文件，
            // 启动网关时 DataBridge 直接使用预分配的 NodeId 创建 UA 节点，无需启动时重复计算。
            foreach (var tag in SelectedTags)
            {
                tag.UaNodeId = $"DaTag_{tag.TagKey}";
            }

            _logger?.Invoke($"[Browse] 已预分配 {SelectedTags.Count} 个 UA NodeId (ns={_namespaceIndex})");

            DialogResult = DialogResult.OK;
            Close();
        }
    }
}
