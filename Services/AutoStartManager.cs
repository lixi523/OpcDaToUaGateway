using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace OpcDaToUaGateway.Services
{
    /// <summary>
    /// 开机自启动管理器 — 负责在 Windows "启动"文件夹中创建/删除 .lnk 快捷方式。
    /// 
    /// 实现方式：通过 COM 互操作调用 WScript.Shell 的 CreateShortcut 方法。
    /// 从 ConfigManager 分离而来，遵循单一职责原则。
    /// </summary>
    public class AutoStartManager
    {
        private readonly LogManager _log;

        public AutoStartManager(LogManager log)
        {
            _log = log ?? throw new ArgumentNullException(nameof(log));
        }

        /// <summary>
        /// 设置或取消 Windows 开机自动启动。
        /// </summary>
        /// <param name="enable">true 添加开机启动；false 移除</param>
        public void SetAutoStart(bool enable)
        {
            try
            {
                if (enable)
                {
                    CreateStartupShortcut();
                    _log.Append("[开机启动] 已添加开机启动快捷方式");
                }
                else
                {
                    RemoveAutoStartShortcut();
                    _log.Append("[开机启动] 已移除开机启动快捷方式");
                }
            }
            catch (Exception ex)
            {
                _log.Append($"[开机启动] 设置失败: {ex.Message}");
            }
        }

        private void CreateStartupShortcut()
        {
            string shortcutPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.Startup),
                "OpcDaToUaGateway.lnk");

            string exePath = Application.ExecutablePath;

            Type shellType = Type.GetTypeFromProgID("WScript.Shell");
            object shell = Activator.CreateInstance(shellType);
            object shortcut = null;
            try
            {
                shortcut = shellType.InvokeMember("CreateShortcut",
                    System.Reflection.BindingFlags.InvokeMethod, null, shell,
                    new object[] { shortcutPath });

                Type scType = shortcut.GetType();
                scType.InvokeMember("TargetPath",
                    System.Reflection.BindingFlags.SetProperty, null, shortcut, new object[] { exePath });
                scType.InvokeMember("Arguments",
                    System.Reflection.BindingFlags.SetProperty, null, shortcut, new object[] { "--minimized" });
                scType.InvokeMember("WorkingDirectory",
                    System.Reflection.BindingFlags.SetProperty, null, shortcut,
                    new object[] { Path.GetDirectoryName(exePath) });
                scType.InvokeMember("WindowStyle",
                    System.Reflection.BindingFlags.SetProperty, null, shortcut, new object[] { 1 });
                scType.InvokeMember("Description",
                    System.Reflection.BindingFlags.SetProperty, null, shortcut,
                    new object[] { "OPC DA to OPC UA Gateway" });
                scType.InvokeMember("Save",
                    System.Reflection.BindingFlags.InvokeMethod, null, shortcut, null);
            }
            finally
            {
                if (shortcut != null) Marshal.ReleaseComObject(shortcut);
                if (shell != null) Marshal.ReleaseComObject(shell);
            }
        }

        private void RemoveAutoStartShortcut()
        {
            string shortcutPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.Startup),
                "OpcDaToUaGateway.lnk");

            if (File.Exists(shortcutPath))
                File.Delete(shortcutPath);
        }
    }
}
