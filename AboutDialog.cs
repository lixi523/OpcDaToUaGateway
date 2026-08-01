using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Reflection;
using System.Windows.Forms;

namespace OpcDaToUaGateway
{
    /// <summary>
    /// 关于对话框 - 显示软件版本、版权信息、PCID 和授权状态
    /// </summary>
    public class AboutDialog : Form
    {
        // P5 修改：版本号统一从资源集合获取，不硬编码版本
        private static readonly string AppVersion =
            System.Reflection.Assembly.GetExecutingAssembly().GetName().Version.ToString(3);

        private readonly string _pcid;
        private readonly bool _isLicensed;

        // H6 修改：追加并记录所有创建的 Font 对象，在关闭时统一释放 GDI 资源
        private readonly List<Font> _ownedFonts = new List<Font>();

        /// <summary>手动创建并注册到资源列表的 Font，关闭时自动释放</summary>
        private Font OwnedFont(string family, float size, FontStyle style = FontStyle.Regular)
        {
            var f = new Font(family, size, style);
            _ownedFonts.Add(f);
            return f;
        }

        /// <summary>
        /// 用户输入授权码（DialogResult.OK 时返回）；为空白表示未提交授权
        /// </summary>
        public string AuthorizationCode { get; private set; }

        public AboutDialog(string pcid, bool isLicensed)
        {
            _pcid = pcid ?? "UNKNOWN";
            _isLicensed = isLicensed;
            BuildUI();
        }

        private void BuildUI()
        {
            Text = "OPC DA 转 OPC UA 网关";
            Size = new Size(520, 780);
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            Font = OwnedFont("Microsoft YaHei UI", 9f);
            BackColor = Color.White;

            // ==================== 顶部蓝色标题栏 ====================
            var headerPanel = new Panel
            {
                Location = new Point(0, 0),
                Size = new Size(ClientSize.Width, 90),
                BackColor = Color.FromArgb(33, 150, 243)
            };

            // 程序图标（P2 修改：Icon 使用后 Dispose）
            Image iconImage = null;
            string iconPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "app.ico");
            if (File.Exists(iconPath))
            {
                try
                {
                    using (var ico = new Icon(iconPath, 48, 48))
                        iconImage = ico.ToBitmap();
                }
                catch { }
            }

            if (iconImage != null)
            {
                var picIcon = new PictureBox
                {
                    Image = iconImage,
                    Location = new Point(16, 20),
                    Size = new Size(48, 48),
                    SizeMode = PictureBoxSizeMode.Zoom,
                    BackColor = Color.Transparent
                };
                headerPanel.Controls.Add(picIcon);
            }

            var lblTitle = new Label
            {
                Text = "OPC DA 转 OPC UA 网关",
                Location = new Point(iconImage != null ? 76 : 16, 18),
                AutoSize = true,
                Font = OwnedFont("Microsoft YaHei UI", 14f, FontStyle.Bold),
                ForeColor = Color.White,
                BackColor = Color.Transparent
            };

            var lblVersion = new Label
            {
                Text = $"版本 {AppVersion}",
                Location = new Point(iconImage != null ? 76 : 16, 50),
                AutoSize = true,
                Font = OwnedFont("Microsoft YaHei UI", 10f),
                ForeColor = Color.FromArgb(220, 255, 255, 255),
                BackColor = Color.Transparent
            };

            headerPanel.Controls.AddRange(new Control[] { lblTitle, lblVersion });
            Controls.Add(headerPanel);

            // ==================== 软件基本信息 ====================
            int infoY = 105;

            var infoPanel = new Panel
            {
                Location = new Point(16, infoY),
                Size = new Size(480, 195)
            };

            string[] infoLines = new string[]
            {
                "OPC DA 转 OPC UA 协议转换网关",
                "支持通过 OPC DA(COM 协议)实时连接并映射到 OPC UA(TCP 协议)。",
                "遇到bug请联系18510086469,408738480@qq.com",
                "运行环境:",
                "  .NET Framework 4.7.2 (x86)",
                "  OPC Foundation UA SDK 1.5.378.145",
                "  TitaniumAS.Opc.Client 1.0.2",
                "系统信息:",
                $"  运行版本: {Environment.Version}",
                $"  操作系统: {Environment.OSVersion.VersionString}",
                $"  CLR 位数: {IntPtr.Size * 8} 位",
            };

            int lineY = 0;
            foreach (string line in infoLines)
            {
                bool isEmpty = string.IsNullOrWhiteSpace(line);
                bool isHeader = !isEmpty && line.EndsWith(":") && !line.StartsWith("  ");
                var lbl = new Label
                {
                    Text = line,
                    Location = new Point(0, lineY),
                    Size = new Size(480, isEmpty ? 4 : 18),
                    Font = isHeader
                        ? OwnedFont("Microsoft YaHei UI", 9f, FontStyle.Bold)
                        : OwnedFont("Consolas", 9f),
                    ForeColor = isHeader ? Color.FromArgb(33, 33, 33) : Color.FromArgb(80, 80, 80),
                    BackColor = Color.Transparent
                };
                infoPanel.Controls.Add(lbl);
                lineY += lbl.Height;
            }

            Controls.Add(infoPanel);

            // ==================== 授权信息区域 ====================
            int authY = 315;

            var pnlAuth = new Panel
            {
                Location = new Point(16, authY),
                Size = new Size(480, 140)
            };

            // 授权状态
            var lblAuthState = new Label
            {
                Text = "当前状态:",
                Location = new Point(15, 10),
                AutoSize = true,
                Font = OwnedFont("Microsoft YaHei UI", 9f, FontStyle.Bold)
            };

            var lblAuthValue = new Label
            {
                Text = _isLicensed ? "已授权" : "未授权（试用中）",
                Location = new Point(85, 10),
                AutoSize = true,
                ForeColor = _isLicensed ? Color.Green : Color.OrangeRed
            };

            // PCID 显示
            var lblPcidLabel = new Label
            {
                Text = "机器码 (PCID):",
                Location = new Point(15, 38),
                AutoSize = true,
                Font = OwnedFont("Microsoft YaHei UI", 9f, FontStyle.Bold)
            };

            var txtPcid = new TextBox
            {
                Text = _pcid,
                Location = new Point(120, 35),
                Size = new Size(200, 25),
                ReadOnly = true,
                BackColor = Color.FromArgb(245, 245, 245),
                Font = OwnedFont("Consolas", 10f),
                BorderStyle = BorderStyle.FixedSingle
            };

            var btnCopyPcid = new Button
            {
                Text = "复制",
                Location = new Point(330, 34),
                Size = new Size(55, 27),
                FlatStyle = FlatStyle.Flat,
                BackColor = Theme.Neutral,
                ForeColor = Color.White,
                Cursor = Cursors.Hand
            };
            btnCopyPcid.FlatAppearance.BorderSize = 0;
            btnCopyPcid.Click += (s, ev) =>
            {
                try
                {
                    Clipboard.SetText(_pcid);
                    MessageBox.Show("PCID 已复制到剪贴板。", "提示",
                        MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
                catch { }
            };

            // 授权码输入
            var lblAuthCode = new Label
            {
                Text = "授权码:",
                Location = new Point(15, 68),
                AutoSize = true,
                Font = OwnedFont("Microsoft YaHei UI", 9f, FontStyle.Bold)
            };

            var txtAuthCode = new TextBox
            {
                Location = new Point(120, 65),
                Size = new Size(200, 25),
                Font = OwnedFont("Consolas", 9.5f),
                BorderStyle = BorderStyle.FixedSingle
            };
            txtAuthCode.PlaceholderText("XXXX-XXXX-XXXX-XXXX-XXXX");

            var btnVerify = new Button
            {
                Text = "验证授权",
                Location = new Point(330, 64),
                Size = new Size(80, 28),
                FlatStyle = FlatStyle.Flat,
                BackColor = Theme.Success,
                ForeColor = Color.White,
                Cursor = Cursors.Hand
            };
            btnVerify.FlatAppearance.BorderSize = 0;
            btnVerify.Click += (s, ev) =>
            {
                string code = txtAuthCode.Text.Trim();
                if (string.IsNullOrEmpty(code))
                {
                    MessageBox.Show("请输入授权码。", "提示",
                        MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }

                AuthorizationCode = code;
                DialogResult = DialogResult.OK;
                Close();
            };

            // 授权码格式说明
            var lblFormatHint = new Label
            {
                Text = "格式: XXXX-XXXX-XXXX-XXXX-XXXX（仅数字大小写均可）",
                Location = new Point(120, 95),
                AutoSize = true,
                ForeColor = Color.Gray,
                Font = OwnedFont("Microsoft YaHei UI", 8f)
            };

            pnlAuth.Controls.AddRange(new Control[] {
                lblAuthState, lblAuthValue,
                lblPcidLabel, txtPcid, btnCopyPcid,
                lblAuthCode, txtAuthCode, btnVerify,
                lblFormatHint
            });
            Controls.Add(pnlAuth);

            // ==================== 第三方许可证声明 ====================
            int licenseY = 470;

            var grpLicense = new GroupBox
            {
                Text = "第三方软件许可证声明",
                Location = new Point(16, licenseY),
                Size = new Size(480, 140)
            };

            var rtbLicense = new RichTextBox
            {
                Location = new Point(10, 20),
                Size = new Size(460, 115),
                ReadOnly = true,
                WordWrap = true,
                ScrollBars = RichTextBoxScrollBars.Vertical,
                Font = OwnedFont("Consolas", 7.5f),
                ForeColor = Color.FromArgb(60, 60, 60),
                BackColor = Color.FromArgb(250, 250, 250),
                BorderStyle = BorderStyle.None
            };

            rtbLicense.Text = @"本软件使用了以下第三方开源库，其许可证均为 MIT License：

1. TitaniumAS.Opc.Client v1.0.2
   Copyright (c) 2016 titanium-as
   https://github.com/titanium-as/TitaniumAS.Opc.Client

2. OPC Foundation UA .NET Standard Stack v1.5.378.145
   Copyright (c) 2005-2025 OPC Foundation, Inc.
   https://github.com/OPCFoundation/UA-.NETStandard

3. Newtonsoft.Json v13.0.4
   Copyright (c) James Newton-King
   https://github.com/JamesNK/Newtonsoft.Json

4. BouncyCastle.Cryptography v2.6.2
   Copyright (c) 2000-2026 The Legion of the Bouncy Castle Inc.
   https://www.bouncycastle.org/csharp

5. Costura.Fody v5.7.0
   Copyright (c) Hans Holmberg & contributors
   https://github.com/Fody/Costura

MIT License 摘要：
Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the ""Software""), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell copies
of the Software, subject to the condition that the above copyright notice and
permission notice appear in all copies.

详细许可证文本可访问各项目 GitHub 仓库查看。";

            grpLicense.Controls.Add(rtbLicense);
            Controls.Add(grpLicense);

            // ==================== 版权底部 ====================
            var lblCopyright = new Label
            {
                Text = "Copyright \u00A9 2026 OPC DA to UA Gateway",
                Location = new Point(16, 625),
                AutoSize = true,
                ForeColor = Color.Gray,
                Font = OwnedFont("Microsoft YaHei UI", 8.5f)
            };
            Controls.Add(lblCopyright);

            // 联系信息
            var lblContact = new Label
            {
                Text = "问题反馈: 18510086469 | 408738480@qq.com",
                Location = new Point(16, 643),
                AutoSize = true,
                ForeColor = Color.FromArgb(150, 150, 150),
                Font = OwnedFont("Microsoft YaHei UI", 8f)
            };
            Controls.Add(lblContact);

            // ==================== 底部按钮 ====================
            var btnOk = new Button
            {
                Text = "关闭",
                Location = new Point(ClientSize.Width - 95, ClientSize.Height - 40),
                Size = new Size(80, 30),
                FlatStyle = FlatStyle.Flat,
                BackColor = Theme.Neutral,
                ForeColor = Color.White,
                Cursor = Cursors.Hand
            };
            btnOk.FlatAppearance.BorderSize = 0;
            btnOk.Click += (s, e) =>
            {
                AuthorizationCode = null; // 未提交授权码
                DialogResult = DialogResult.Cancel;
                Close();
            };
            Controls.Add(btnOk);

            CancelButton = btnOk;
        }

        /// <summary>
        /// H6 修改：表单关闭时统一释放所有创建的 Font GDI 资源
        /// </summary>
        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            base.OnFormClosed(e);
            foreach (var font in _ownedFonts)
            {
                try { font.Dispose(); } catch { }
            }
            _ownedFonts.Clear();
        }
    }
}
