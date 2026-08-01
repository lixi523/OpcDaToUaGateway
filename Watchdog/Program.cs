// ============================================================================
// OpcDaToUaGateway.Watchdog — 看门狗进程 (Program.cs)
// ============================================================================
//
// 架构角色:
//   本进程是 OPC DA→UA 网关的外部守护者，以独立进程形式运行。
//   它的唯一职责是：持续监控主进程 (OpcDaToUaGateway.exe) 是否存活，
//   当主进程因崩溃或挂起而异常退出时，自动将其重启，从而保证网关服务的高可用。
//
// 进程间通信协议 — 命名事件 (Named Events):
//   看门狗与主进程之间通过三个 Windows 命名事件进行协调：
//
//   1. StopEvent ("OpcDaToUaGateway_Watchdog_Stop")
//      - 方向: 外部 → 看门狗
//      - 作用: 通知看门狗自身退出。当管理员或部署脚本需要完全停止网关时，
//        先 Set 此事件让看门狗退出监控循环，再由主进程停止服务。
//      - 模式: ManualReset（设置后保持信号状态，直到被手动 Reset）
//
//   2. HeartbeatEvent ("OpcDaToUaGateway_Heartbeat")
//      - 方向: 主进程 → 看门狗
//      - 作用: 主进程每隔 10 秒 Set 一次此事件，表明自身仍在工作。
//        看门狗若在 30 秒内未检测到心跳信号，则判定主进程已挂起（死锁/无响应），
//        主动 Kill 主进程并重启。这解决了进程存在但实际已卡死的场景。
//      - 模式: ManualReset（主进程 Set，看门狗 Reset，形成"乒乓"协议）
//
//   3. GracefulExitEvent ("OpcDaToUaGateway_GracefulExit")
//      - 方向: 主进程 → 看门狗
//      - 作用: 用户通过托盘菜单主动退出网关时，主进程在退出前 Set 此事件。
//        看门狗检测到该信号后，知道主进程是正常退出而非崩溃，因此不会重启主进程，
//        但看门狗自身继续保持监控状态，等待下一次主进程手动启动。
//      - 模式: ManualReset（主进程 Set，看门狗 Reset 消费信号）
//
// 监控循环逻辑:
//   看门狗进入 while(true) 循环，每 5 秒执行一次检查：
//   a) 检查 StopEvent → 若有信号则退出循环
//   b) 按进程名查找主进程 → 判断是否存活
//   c) 若存活且心跳事件存在 → 检测心跳是否超时，超时则 Kill 挂起进程
//   d) 若不存活 → 检查 GracefulExitEvent：
//      - 有信号 = 用户主动退出 → 不重启，继续监控
//      - 无信号 = 崩溃退出 → 执行重启（带防抖保护）
//   e) 等待 CheckIntervalMs 后进入下一轮（同时监听 StopEvent）
//
// 防抖保护:
//   若主进程在 60 秒内连续崩溃重启超过 3 次，看门狗会暂停 60 秒再尝试，
//   避免陷入"启动即崩溃、崩溃即重启"的无限循环，浪费系统资源。
//
// 用法: OpcDaToUaGateway.Watchdog.exe [主程序EXE路径]
// ============================================================================
using System;
using System.Diagnostics;
using System.IO;
using System.Threading;

namespace OpcDaToUaGateway.Watchdog
{
    static class Program
    {
        // ─── 命名事件与互斥锁名称 ───────────────────────────────────────────

        /// <summary>
        /// 全局互斥锁名称 — 确保系统内只有一个看门狗实例运行。
        /// 若第二个看门狗进程启动时检测到此互斥锁已存在，则立即退出。
        /// </summary>
        private const string MutexName = "OpcDaToUaGateway_Watchdog_Mutex";

        /// <summary>
        /// 停止事件名称 — 外部通过 Set 此事件通知看门狗退出监控循环。
        /// 典型场景: 管理员执行 Stop() 或部署脚本需要完全停止网关。
        /// </summary>
        private const string StopEventName = "OpcDaToUaGateway_Watchdog_Stop";

        /// <summary>
        /// 优雅退出事件名称 — 主进程正常退出时 Set 此事件，
        /// 看门狗检测到后知道是用户主动退出（而非崩溃），不会重启主进程。
        /// </summary>
        private const string ExitOkEventName = "OpcDaToUaGateway_GracefulExit";

        /// <summary>
        /// 心跳事件名称 — 主进程定期 Set 此事件证明自身存活且响应正常。
        /// 看门狗若在 HeartbeatTimeoutMs 内未收到信号，判定主进程挂起并强制重启。
        /// 使用 ManualReset 模式：主进程 Set，看门狗 Reset，形成"乒乓"协议。
        /// </summary>
        private const string HeartbeatEventName = "OpcDaToUaGateway_Heartbeat";

        // ─── 监控参数常量 ─────────────────────────────────────────────────

        /// <summary>
        /// 监控循环的检查间隔 (5 秒)。
        /// 选择 5 秒是在"检测及时性"和"CPU/系统开销"之间的平衡：
        /// 间隔太短会浪费 CPU，间隔太长会导致崩溃后恢复时间过长。
        /// </summary>
        private const int CheckIntervalMs = 5000;

        /// <summary>
        /// 重启前的延迟等待 (3 秒)。
        /// 给操作系统和底层资源（如 OPC COM 连接、端口）留出释放时间，
        /// 避免主进程刚退出就立即启动导致资源冲突。
        /// </summary>
        private const int RestartDelayMs = 3000;

        /// <summary>
        /// 短时间窗口内允许的最大重启次数 (3 次)。
        /// 超过此阈值说明主进程可能存在"启动即崩溃"的致命问题，
        /// 此时应暂停重启而非无限循环，避免日志暴涨和系统资源浪费。
        /// </summary>
        private const int MaxRapidRestarts = 3;

        /// <summary>
        /// "短时间"窗口定义 (60 秒)。
        /// 若 MaxRapidRestarts 次重启都发生在此窗口内，视为异常频繁重启，
        /// 看门狗将暂停一个窗口时长后再尝试。
        /// </summary>
        private const int RapidRestartWindowMs = 60000;

        /// <summary>
        /// 心跳超时阈值 (30 秒)。
        /// 主进程每 10 秒发一次心跳，30 秒 = 3 个心跳周期。
        /// 允许偶尔一次心跳丢失（如 GC 暂停），连续 3 次未收到才判定挂起，
        /// 避免误杀正常运行中的主进程。
        /// </summary>
        private const int HeartbeatTimeoutMs = 30000;

        /// <summary>
        /// 日志最大保留天数 (7 天)。
        /// 看门狗日志为低频输出，7 天的日志量很小，但足以覆盖大部分故障排查窗口。
        /// </summary>
        private const int LogMaxDays = 7;

        /// <summary>
        /// 日志清理触发频率 — 每写 N 条日志后执行一次过期清理。
        /// 选择 100 是因为看门狗日志写入频率很低（正常时几乎不写），
        /// 每 100 条清理一次既能控制日志文件大小，又不会频繁执行 I/O。
        /// </summary>
        private const int LogCleanupInterval = 100;

        /// <summary>
        /// 日志写入计数器，用于控制过期日志清理的触发频率。
        /// </summary>
        private static int _logWriteCount = 0;

        static void Main(string[] args)
        {
            // ── 步骤 1: 解析主程序路径 ──────────────────────────────────────
            // 优先使用命令行参数指定的路径，若未提供则默认与看门狗同目录。
            // 这种设计允许看门狗监控任意位置的主程序，同时也支持最简单的部署方式
            // （将看门狗 EXE 放在主程序同目录下即可，无需额外配置）。
            string mainExePath;
            if (args.Length > 0 && File.Exists(args[0]))
            {
                mainExePath = args[0];
            }
            else
            {
                string watchdogDir = AppDomain.CurrentDomain.BaseDirectory;
                mainExePath = Path.Combine(watchdogDir, "OpcDaToUaGateway.exe");
                if (!File.Exists(mainExePath))
                {
                    WriteLog("找不到主程序: " + mainExePath);
                    return;
                }
            }

            string processName = Path.GetFileNameWithoutExtension(mainExePath);
            WriteLog($"看门狗启动，监控进程: {processName}");
            WriteLog($"主程序路径: {mainExePath}");

            // 启动时立即清理过期日志，确保日志目录不会因长期运行而无限增长
            CleanupOldLogs();

            // ── 步骤 2: 互斥锁 — 全局唯一实例保护 ──────────────────────────
            // 使用命名互斥锁 (Named Mutex) 在操作系统层面保证只有一个看门狗实例。
            // 若 Mutex 已存在（createdNew=false），说明有另一个看门狗正在运行，直接退出。
            using (var mutex = new Mutex(true, MutexName, out bool createdNew))
            {
                if (!createdNew)
                {
                    WriteLog("另一个看门狗实例已在运行，退出");
                    return;
                }

                // ── 步骤 3: 初始化停止事件 ────────────────────────────────
                // 尝试打开已有的命名事件（可能由主进程预创建），
                // 若不存在则自行创建。无论哪种情况，启动时都先 Reset，
                // 清除上次残留的停止信号，确保新一轮监控从干净状态开始。
                EventWaitHandle stopEvent;
                try
                {
                    stopEvent = EventWaitHandle.OpenExisting(StopEventName);
                }
                catch (WaitHandleCannotBeOpenedException)
                {
                    stopEvent = new EventWaitHandle(false, EventResetMode.ManualReset, StopEventName);
                }
                stopEvent.Reset();

                // ── 步骤 4: 初始化心跳事件 ────────────────────────────────
                // 心跳事件由主进程创建，看门狗尝试打开它。
                // 若主进程尚未启动（首次启动场景），心跳事件不存在是正常的，
                // heartbeatEvent 保持 null，后续循环中跳过心跳检测。
                EventWaitHandle heartbeatEvent = null;
                try
                {
                    heartbeatEvent = EventWaitHandle.OpenExisting(HeartbeatEventName);
                    WriteLog("已连接到主进程心跳事件");
                }
                catch (WaitHandleCannotBeOpenedException)
                {
                    WriteLog("主进程心跳事件不存在（主进程尚未启动或版本不支持心跳）");
                }

                // ── 步骤 5: 进入监控循环 ──────────────────────────────────
                // rapidRestartCount 和 lastRestartTime 配合实现"防抖"逻辑：
                // 在 RapidRestartWindowMs 窗口内若重启次数超过 MaxRapidRestarts，
                // 看门狗会暂停一个窗口时长，避免"启动即崩溃"的无限循环。
                int rapidRestartCount = 0;
                DateTime lastRestartTime = DateTime.MinValue;

                while (true)
                {
                    // 每次循环首先检查停止信号，确保看门狗能被及时关闭。
                    // WaitOne(0) 是非阻塞检测：有信号立即返回 true，无信号立即返回 false。
                    if (stopEvent.WaitOne(0))
                    {
                        WriteLog("收到停止信号，看门狗退出");
                        break;
                    }

                    // 按进程名查找主进程。注意：进程名不含扩展名，
                    // 且可能匹配到同名但不同路径的进程。在当前部署场景下
                    // （单实例网关）这种风险可接受。
                    var processes = Process.GetProcessesByName(processName);
                    bool isRunning = processes.Length > 0;

                    // ── 心跳检测（仅在进程存活且心跳事件可用时执行） ──────
                    // 采用两阶段检测策略：
                    //   第一次 WaitOne(0) — 非阻塞快速检查是否有心跳信号。
                    //     有信号 → Reset 并继续（主进程工作正常）
                    //     无信号 → 进入第二次 WaitOne(HeartbeatTimeoutMs) 阻塞等待。
                    //       在超时时间内收到信号 → Reset 并继续
                    //       超时仍无信号 → 判定主进程挂起 (isHung=true)
                    //
                    // 使用 ManualReset 模式的原因：
                    //   AutoReset 的 WaitOne 会自动消费信号，若看门狗恰好在主进程
                    //   Set 之后、Reset 之前检查，可能丢失一次心跳。ManualReset
                    //   保证信号在被手动 Reset 前一直可见，不会丢失。
                    bool isHung = false;
                    if (isRunning && heartbeatEvent != null)
                    {
                        if (heartbeatEvent.WaitOne(0))
                        {
                            // 快速路径：主进程心跳正常，Reset 事件等待下一轮
                            heartbeatEvent.Reset();
                        }
                        else
                        {
                            // 慢速路径：暂无心跳，阻塞等待超时时间
                            if (!heartbeatEvent.WaitOne(HeartbeatTimeoutMs))
                            {
                                // 超时仍无心跳 → 主进程可能死锁或无响应
                                isHung = true;
                                WriteLog($"主进程 [{processName}] 心跳超时 ({HeartbeatTimeoutMs / 1000}秒)，判定为挂起");
                            }
                            else
                            {
                                // 在超时窗口内收到了心跳，主进程仍正常
                                heartbeatEvent.Reset();
                            }
                        }
                    }

                    // 及时释放 Process 对象，避免操作系统句柄泄漏
                    foreach (var p in processes) p.Dispose();

                    // ── 处理挂起的主进程 ──────────────────────────────────
                    // 心跳超时意味着主进程虽然在运行但已无法正常工作。
                    // 必须先强制终止它，然后按崩溃流程重启。
                    // 外层 try-catch 防止 GetProcessesByName 本身抛出异常（如权限不足）。
                    if (isHung)
                    {
                        try
                        {
                            var hungProcs = Process.GetProcessesByName(processName);
                            foreach (var p in hungProcs)
                            {
                                try
                                {
                                    WriteLog($"正在终止挂起的主进程 PID={p.Id}");
                                    p.Kill();
                                    // 最多等待 5 秒让进程退出，避免无限阻塞看门狗
                                    p.WaitForExit(5000);
                                }
                                catch (Exception ex) { WriteLog($"终止进程失败: {ex.Message}"); }
                                finally { p.Dispose(); }
                            }
                        }
                        catch { }
                        // 强制标记为不存活，触发下方的重启逻辑
                        isRunning = false;
                    }

                    if (!isRunning)
                    {
                        // ── 区分"优雅退出"与"崩溃退出" ────────────────────
                        // 主进程不存活有两种可能：
                        //   (a) 用户主动从托盘退出 → GracefulExitEvent 已被 Set
                        //   (b) 主进程崩溃/被终止 → GracefulExitEvent 未被 Set
                        // 只有 (b) 才需要重启主进程。
                        bool gracefulExit = false;
                        try
                        {
                            using (var exitOk = EventWaitHandle.OpenExisting(ExitOkEventName))
                            {
                                if (exitOk.WaitOne(0))
                                {
                                    gracefulExit = true;
                                    // 消费信号并 Reset，为下一次启动周期做准备。
                                    // 若不 Reset，下次主进程崩溃时看门狗会误判为优雅退出。
                                    exitOk.Reset();
                                    WriteLog("检测到主进程优雅退出信号（用户主动退出），不重启，继续监控");
                                }
                            }
                        }
                        catch (WaitHandleCannotBeOpenedException)
                        {
                            // 事件不存在 = 主进程从未创建过该事件 = 不可能是优雅退出
                        }

                        if (!gracefulExit)
                        {
                            // ── 防抖检查：避免崩溃→重启→崩溃的无限循环 ────
                            // 如果在一个时间窗口内重启次数已超过阈值，说明问题不是偶发的，
                            // 暂停一个完整窗口时长让系统/环境恢复（如等待 OPC 服务器重启）。
                            if (rapidRestartCount >= MaxRapidRestarts &&
                                (DateTime.Now - lastRestartTime).TotalMilliseconds < RapidRestartWindowMs)
                            {
                                WriteLog($"短时间内已重启 {rapidRestartCount} 次，暂停 60 秒...");
                                // 使用 WaitOne 而非 Thread.Sleep，这样在暂停期间
                                // 仍能响应停止信号，保证看门狗可被及时关闭。
                                if (stopEvent.WaitOne(RapidRestartWindowMs))
                                {
                                    WriteLog("收到停止信号，看门狗退出");
                                    break;
                                }
                                // 冷却期结束，重置计数器重新开始
                                rapidRestartCount = 0;
                            }

                            WriteLog($"主进程 [{processName}] 异常退出，{RestartDelayMs / 1000} 秒后重启...");

                            // 重启前短暂延迟，给操作系统释放资源的时间。
                            // 同样使用 WaitOne 而非 Sleep 以响应停止信号。
                            if (stopEvent.WaitOne(RestartDelayMs))
                            {
                                WriteLog("收到停止信号，看门狗退出");
                                break;
                            }

                            // ── 启动主进程 ────────────────────────────────
                            try
                            {
                                var startInfo = new ProcessStartInfo
                                {
                                    FileName = mainExePath,
                                    WorkingDirectory = Path.GetDirectoryName(mainExePath),
                                    Arguments = "--minimized", // I-08 修复：与开机启动快捷方式保持一致，最小化到托盘
                                    UseShellExecute = true
                                };
                                // Process.Start 返回的 Process 对象持有 OS 句柄，
                                // 必须 Dispose 释放。此处不需要持有该对象（不监控其退出），
                                // 所以用 using 立即释放。
                                using (Process.Start(startInfo)) { }
                                WriteLog($"已重启主进程: {mainExePath}");

                                rapidRestartCount++;
                                lastRestartTime = DateTime.Now;
                            }
                            catch (Exception ex)
                            {
                                WriteLog($"重启主进程失败: {ex.Message}");
                            }
                        }
                    }

                    // 等待下一次检查。使用 WaitOne(CheckIntervalMs) 而非 Thread.Sleep，
                    // 这样在等待间隔期间能立即响应停止信号，保证看门狗退出延迟
                    // 不超过 CheckIntervalMs（最坏情况 5 秒）。
                    if (stopEvent.WaitOne(CheckIntervalMs))
                    {
                        WriteLog("收到停止信号，看门狗退出");
                        break;
                    }
                }

                // 清理命名事件句柄，避免资源泄漏
                stopEvent.Dispose();
                heartbeatEvent?.Dispose();
            }

            WriteLog("看门狗已退出");
        }

        /// <summary>
        /// 获取日志目录路径。
        /// 看门狗与主程序共用同一 logs 子目录（位于看门狗 EXE 所在目录下），
        /// 便于运维人员集中查看所有日志。若目录不存在则自动创建。
        /// </summary>
        /// <returns>日志目录的绝对路径（如 C:\Program Files\Gateway\logs）</returns>
        private static string GetLogDir()
        {
            string logDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "logs");
            if (!Directory.Exists(logDir))
                Directory.CreateDirectory(logDir);
            return logDir;
        }

        /// <summary>
        /// 向日志文件 (logs/watchdog.log) 追加一条带时间戳的记录。
        /// 每写入 <see cref="LogCleanupInterval"/> 条记录后自动触发过期日志清理，
        /// 防止长期运行时日志文件无限增长。
        /// </summary>
        /// <param name="message">日志内容（不含时间戳前缀，方法会自动添加）</param>
        /// <remarks>
        /// 写入失败时静默吞掉异常，因为看门狗的日志系统不应导致自身崩溃。
        /// 这是"基础设施组件"的常见做法：日志本身不能再依赖日志来报错。
        /// </remarks>
        private static void WriteLog(string message)
        {
            try
            {
                string logPath = Path.Combine(GetLogDir(), "watchdog.log");
                string line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}{Environment.NewLine}";
                File.AppendAllText(logPath, line);

                _logWriteCount++;
                if (_logWriteCount >= LogCleanupInterval)
                {
                    _logWriteCount = 0;
                    CleanupOldLogs();
                }
            }
            catch { }
        }

        /// <summary>
        /// 清理日志文件中超过 <see cref="LogMaxDays"/> 天的旧条目。
        /// </summary>
        /// <remarks>
        /// 实现逻辑：
        ///   1. 若日志文件小于 10KB 则跳过（避免对小文件执行无意义的读取和重写）
        ///   2. 逐行解析时间戳 [yyyy-MM-dd HH:mm:ss]，与截止时间比较
        ///   3. 非标准格式行（续行/空行）跟随前一条目的保留/丢弃状态
        ///   4. 仅当确实有内容被移除时才重写文件，避免无变更的写入
        ///
        /// 选择"行内清理"而非"文件轮转"是因为看门狗日志量极小，
        /// 单文件方案对运维人员更友好（直接用记事本打开即可查看全部历史）。
        /// </remarks>
        private static void CleanupOldLogs()
        {
            try
            {
                string logPath = Path.Combine(GetLogDir(), "watchdog.log");

                if (!File.Exists(logPath)) return;

                // 文件太小时不值得执行清理操作（读取+解析+可能重写的开销大于收益）
                var fileInfo = new FileInfo(logPath);
                if (fileInfo.Length < 10240) return;

                string[] lines = File.ReadAllLines(logPath);
                DateTime cutoff = DateTime.Now.AddDays(-LogMaxDays);
                var keptLines = new System.Collections.Generic.List<string>();
                // 标记当前是否处于"旧条目"区域，用于处理多行日志（如异常堆栈）
                bool inOldEntry = false;

                foreach (string line in lines)
                {
                    // 日志行格式固定为 [yyyy-MM-dd HH:mm:ss]，总长度至少 22 字符。
                    // 通过位置索引解析比正则更快，适合在低频清理任务中使用。
                    if (line.Length >= 22 && line[0] == '[' && line[20] == ']')
                    {
                        string ts = line.Substring(1, 19);
                        if (DateTime.TryParse(ts, out DateTime entryTime))
                        {
                            inOldEntry = entryTime < cutoff;
                            if (!inOldEntry)
                            {
                                keptLines.Add(line);
                            }
                            continue;
                        }
                    }

                    // 非标准行（如异常的续行、空行等）继承前一条目的时间状态。
                    // 这样一条旧日志及其完整堆栈信息会被一起删除。
                    if (!inOldEntry)
                    {
                        keptLines.Add(line);
                    }
                }

                // 仅当有行被过滤掉时才重写文件，避免无意义的磁盘写入
                if (keptLines.Count < lines.Length)
                {
                    File.WriteAllLines(logPath, keptLines.ToArray());
                }
            }
            catch { }
        }
    }
}
