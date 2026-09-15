using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using TitaniumAS.Opc.Client;

namespace OpcDaToUaGateway
{
    /// <summary>
    /// 应用程序入口类，负责程序启动流程、单实例检测和全局异常处理。
    /// </summary>
    static class Program
    {
        // =====================================================================
        // Win32 API 导入：用于查找和激活已有实例的窗口
        // =====================================================================

        /// <summary>通过窗口类名和标题查找窗口句柄。</summary>
        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr FindWindow(string lpClassName, string lpWindowName);

        /// <summary>将指定窗口带到前台并获得焦点。</summary>
        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        /// <summary>设置窗口的显示状态（还原、最小化、最大化等）。</summary>
        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        /// <summary>判断窗口是否处于最小化状态。</summary>
        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool IsIconic(IntPtr hWnd);

        /// <summary>SW_RESTORE：还原并激活最小化的窗口。</summary>
        private const int SW_RESTORE = 9;

        // =====================================================================
        // 单实例互斥体（Named Mutex）
        // =====================================================================
        // 使用命名 Mutex 实现操作系统级别的单实例限制：
        // - 命名 Mutex 在内核对象命名空间中全局可见（跨进程、跨会话）；
        // - 第一个实例创建 Mutex 成功（createdNew = true）；
        // - 后续实例尝试创建同名 Mutex 时会获得已有句柄（createdNew = false），
        //   由此判断已有实例在运行。
        // - using 块确保程序退出时 Mutex 被释放，允许再次启动。
        //
        // 命名约定与看门狗（Watchdog）共享，确保看门狗和主程序也互斥。
        // =====================================================================
        private const string SingleInstanceMutexName = "OpcDaToUaGateway_SingleInstance";

        /// <summary>
        /// 程序入口点。
        /// 
        /// <para>[STAThread] 是必需的：OPC DA 基于 COM 技术，COM 的套间模型要求
        /// 调用线程处于单线程套间（STA）模式，否则 DA 客户端的 COM 调用会失败。</para>
        /// 
        /// <para>启动流程：
        /// 1. 注册全局异常处理器（UI 线程异常 + AppDomain 未处理异常）；
        /// 2. 初始化 WinForms 视觉样式；
        /// 3. 单实例检测（命名 Mutex）——已有实例则激活其窗口后退出；
        /// 4. 解析命令行参数（--minimized 用于开机自启时最小化到托盘）；
        /// 5. 启动主窗口的消息循环。</para>
        /// 
        /// <para>优雅退出流程：
        /// 当用户关闭或最小化到托盘后退出时，Application.Run 返回，
        /// using 块释放 Mutex，进程正常终止。MainForm 的 FormClosing 事件
        /// 负责清理 DA 客户端和 UA 服务器资源。</para>
        /// </summary>
        /// <param name="args">命令行参数，支持 --minimized 参数。</param>
        [STAThread]
        static void Main(string[] args)
        {
            // H-04 修复：全局异常处理器必须在 Bootstrap.Initialize() 之前注册，
            // 否则 Bootstrap 内部若抛出异常（CoInitializeSecurity 失败等），
            // 处理器尚未注册，崩溃无法被 crash.log 记录。
            Application.ThreadException += (s, e) =>
            {
                LogUnhandledException("UI线程异常", e.Exception);
                // H-04 修复：MessageBox.Show 在 UI 控件不一致状态下可能引发二次异常，
                // 用 try-catch 包裹，确保日志写入不被中断。
                try
                {
                    MessageBox.Show(
                        $"程序遇到未处理异常:\n{e.Exception.Message}\n\n已记录到日志文件。",
                        "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
                catch { }
            };
            AppDomain.CurrentDomain.UnhandledException += (s, e) =>
            {
                var ex = e.ExceptionObject as Exception;
                LogUnhandledException("全局未处理异常", ex);
            };
            // 强制 WinForms 将所有异常路由到 ThreadException 处理器，
            // 而非由操作系统默认处理（后者可能直接终止进程而不记录日志）
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);

            // H-26: TitaniumAS.Opc.Client 初始化（异常处理器注册后执行，确保初始化异常可被记录）。
            // Bootstrap.Initialize() 内部调用 CoInitializeSecurity 配置 COM 安全，
            // 必须在主线程 STA 初始化之后、任何 COM 对象创建之前执行。
            Bootstrap.Initialize();

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            // ---- 单实例检测 ----
            // 使用命名 Mutex 实现操作系统级别的互斥：
            // createdNew = true  → 当前是第一个实例，正常启动
            // createdNew = false → 已有实例持有该 Mutex，激活其窗口后退出
            using (var mutex = new Mutex(true, SingleInstanceMutexName, out bool createdNew))
            {
                if (!createdNew)
                {
                    // 已有实例在运行，尝试将其窗口带到前台（方便用户找到）
                    ActivateExistingInstance();
                    MessageBox.Show(
                        "OPC DA → OPC UA 网关已在运行中。\n请检查系统托盘或任务栏。",
                        "单实例限制", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }

                // 解析 --minimized 参数：开机自启动时以最小化状态运行（静默驻留系统托盘）
                bool startMinimized = false;
                foreach (string arg in args)
                {
                    if (arg.Equals("--minimized", StringComparison.OrdinalIgnoreCase))
                    {
                        startMinimized = true;
                        break;
                    }
                }

                // 启动主窗口消息循环，程序在 Application.Run 返回时开始退出流程：
                // MainForm 的 Dispose 会清理 DA 客户端、UA 服务器和 DataBridge 资源，
                // 随后 using 块释放 Mutex，进程终止。
                Application.Run(new MainForm(startMinimized));
            }
        }

        // =====================================================================
        // C-16 修复：crash.log 写入锁
        // =====================================================================
        // 问题背景：
        // 当程序崩溃时，ThreadException 和 UnhandledException 可能同时触发
        // （例如 UI 线程异常会同时触发两者），两个处理器并发调用 LogUnhandledException
        // 会导致两条日志条目在文件中交错——一行 A 的内容被一行 B 的内容截断。
        //
        // 解决方案：
        // 使用静态锁对象确保同一时刻只有一个线程在执行 File.AppendAllText，
        // 保证每条日志条目都是完整、连续的。
        // =====================================================================
        private static readonly object _crashLogLock = new object();

        /// <summary>
        /// 将未处理异常追加写入 crash.log 文件，确保无人值守时也能追踪崩溃原因。
        /// 
        /// <para>C-16 修复：使用 <see cref="_crashLogLock"/> 加锁，防止
        /// ThreadException 和 UnhandledException 两个处理器并发写入导致日志条目交错。
        /// 在崩溃场景下，两个异常事件可能在不同的线程上同时触发，
        /// 不加锁会导致两条日志的内容在文件内互相截断。</para>
        /// 
        /// <para>日志路径：程序目录下的 crash.log。
        /// 使用 File.AppendAllText 保证每次写入是原子的追加操作。</para>
        /// </summary>
        /// <param name="context">异常来源的上下文描述（如 "UI线程异常"）。</param>
        /// <param name="ex">异常对象，可能为 null。</param>
        private static void LogUnhandledException(string context, Exception ex)
        {
            try
            {
                lock (_crashLogLock)
                {
                    string logPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "crash.log");
                    string line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [{context}] {ex}\r\n\r\n";
                    File.AppendAllText(logPath, line);
                }
            }
            catch
            {
                // 日志写入本身失败时静默忽略——崩溃处理器不应再抛异常
            }
        }

        /// <summary>
        /// 尝试查找并激活已有实例的窗口，将其从最小化状态还原并带到前台。
        /// 
        /// <para>通过 Win32 FindWindow API 按窗口标题查找（标题与 MainForm.Text 保持一致）。
        /// 如果窗口处于最小化状态，先用 ShowWindow(SW_RESTORE) 还原，
        /// 再调用 SetForegroundWindow 将其带到前台。</para>
        /// 
        /// <para>H-17 修复：异常时记录到 crash.log，便于诊断窗口激活失败的原因
        /// （如权限不足、目标进程无响应等）。</para>
        /// </summary>
        private static void ActivateExistingInstance()
        {
            try
            {
                // 通过窗口标题查找（与 MainForm.WindowTitle 常量一致）
                IntPtr hWnd = FindWindow(null, MainForm.WindowTitle);
                if (hWnd != IntPtr.Zero)
                {
                    // 如果窗口处于最小化状态（系统托盘模式），先还原到正常显示
                    if (IsIconic(hWnd))
                        ShowWindow(hWnd, SW_RESTORE);

                    // 将窗口带到前台，让用户能够看到已有实例
                    SetForegroundWindow(hWnd);
                }
            }
            catch (Exception ex)
            {
                LogUnhandledException("激活已有实例失败", ex);
            }
        }
    }
}
