using System;
using System.Collections.Generic;
using System.Net.Sockets;

namespace OpcDaToUaGateway.Services
{
    /// <summary>
    /// UA 服务器启动失败时，把 SDK 抛出的英文异常翻译成可操作的中文诊断提示。
    /// 
    /// 仅识别 TCP 监听端口绑定失败这一类问题 —— 它是现场最常见、也最容易被误判的：
    /// 报 WSAEACCES(10013) 时并非"端口被占用"，而是该端口落在 Windows 系统保留区间内
    /// （Hyper-V / WSL2 / Docker 会预留大批端口供容器动态分配），杀进程无用，只能换端口。
    /// 不属于此类问题时返回 null，由调用方保持原样，不影响其他错误信息。
    /// </summary>
    internal static class PortBindDiagnostic
    {
        private const int WSAEACCES = 10013;      // 访问权限不允许 —— 端口被系统保留
        private const int WSAEADDRINUSE = 10048;  // 地址已在使用 —— 被其他进程占用

        // UA SDK 在绑定失败时统一使用的消息前缀（TcpTransportListener）
        private const string ListenerFailureMarker = "Failed to establish tcp listener sockets";

        /// <summary>
        /// 若异常源于 TCP 端口绑定失败，返回中文诊断提示（多行）；否则返回 null。
        /// </summary>
        /// <param name="ex">启动失败抛出的异常（会自动遍历内部异常与 AggregateException）。</param>
        /// <param name="port">配置中的 OPC UA 端口，用于给出具体建议。</param>
        public static string Describe(Exception ex, int port)
        {
            if (ex == null) return null;

            var socketErrors = CollectSocketErrors(ex);

            // 只有在确认为监听器绑定失败时才给出提示，避免与其他异常混淆
            if (!MentionsListenerFailure(ex))
                return null;

            if (socketErrors.Contains(WSAEACCES))
            {
                return
                    $"端口诊断: 端口 {port} 无法绑定 —— Windows 系统保留了该端口（非端口被占用）。\r\n" +
                    "  常见原因: Hyper-V / WSL2 / Docker 会预留大批端口供容器动态分配，OPC UA 默认端口易落入其中。\r\n" +
                    $"  处理办法: 将 OPC UA 端口改为未被保留的端口（例如 1111）后重启网关。\r\n" +
                    "  查看本机保留区间: netsh interface ipv4 show excludedportrange protocol=tcp";
            }

            if (socketErrors.Contains(WSAEADDRINUSE))
            {
                return
                    $"端口诊断: 端口 {port} 已被其他进程占用。\r\n" +
                    "  处理办法: 关闭占用该端口的程序，或改用其他端口。\r\n" +
                    $"  查看占用进程: netstat -ano | findstr :{port}";
            }

            // 绑定失败但未取到具体 Socket 错误码（如仅拿到 AggregateException）
            return
                $"端口诊断: 端口 {port} 绑定失败，UA 服务器无法监听。\r\n" +
                "  处理办法: 确认该端口未被占用且未被系统保留；否则改用其他端口。";
        }

        /// <summary>异常链（含 AggregateException 的所有子异常）中是否出现监听器绑定失败。</summary>
        private static bool MentionsListenerFailure(Exception ex)
        {
            foreach (var e in Enumerate(ex))
            {
                if (e.Message != null &&
                    e.Message.IndexOf(ListenerFailureMarker, StringComparison.OrdinalIgnoreCase) >= 0)
                    return true;
            }
            return false;
        }

        private static HashSet<int> CollectSocketErrors(Exception ex)
        {
            var codes = new HashSet<int>();
            foreach (var e in Enumerate(ex))
            {
                if (e is SocketException se)
                    codes.Add(se.NativeErrorCode);
            }
            return codes;
        }

        /// <summary>展开异常树：普通 InnerException 链 + AggregateException 的所有子异常。</summary>
        private static IEnumerable<Exception> Enumerate(Exception ex)
        {
            // 显式栈，避免 AggregateException / InnerException 互相嵌套时重复访问
            var pending = new Stack<Exception>();
            pending.Push(ex);
            var seen = new HashSet<Exception>();

            while (pending.Count > 0)
            {
                var cur = pending.Pop();
                if (cur == null || !seen.Add(cur)) continue;

                yield return cur;

                if (cur is AggregateException agg)
                {
                    foreach (var inner in agg.InnerExceptions)
                        if (inner != null) pending.Push(inner);
                }
                else if (cur.InnerException != null)
                {
                    pending.Push(cur.InnerException);
                }
            }
        }
    }
}
