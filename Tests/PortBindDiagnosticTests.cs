using System;
using System.Net.Sockets;
using Opc.Ua;
using OpcDaToUaGateway.Services;
using Xunit;

namespace OpcDaToUaGateway.Tests
{
    /// <summary>
    /// 端口绑定失败诊断提示测试。
    /// 
    /// 关注点：只有真正的"端口绑定失败"才附加提示，且必须能区分两种成因 ——
    /// WSAEACCES(10013) 系统保留端口 vs WSAEADDRINUSE(10048) 被进程占用。
    /// 二者处置方式完全相反（前者换端口、后者关进程），混淆会误导用户。
    /// </summary>
    public class PortBindDiagnosticTests
    {
        private static string Describe(Exception ex, int port = 4840)
            => PortBindDiagnostic.Describe(ex, port);

        /// <summary>
        /// 复现现场真实异常链：
        /// ServiceResultException → AggregateException → SocketException(WSAEACCES)
        /// 这是 Hyper-V/WSL/Docker 机器上启动失败的实际形态。
        /// </summary>
        [Fact]
        public void ReservedPort_WSAEACCES_SuggestsChangingPort()
        {
            var ex = BuildListenerFailure(new SocketException(10013));

            string hint = Describe(ex);

            Assert.False(string.IsNullOrEmpty(hint), "应给出诊断提示");
            Assert.Contains("系统保留", hint);
            Assert.Contains("4840", hint);
            // 必须明确否定"端口被占用"这一误解，并给出换端口的可操作建议
            Assert.DoesNotContain("已被其他进程占用", hint);
            Assert.Contains("1111", hint);
            // 应提供自助排查命令
            Assert.Contains("excludedportrange", hint);
        }

        /// <summary>端口确实被别的进程占用：应提示查占用进程，而非让用户去改保留区。</summary>
        [Fact]
        public void PortInUse_WSAEADDRINUSE_SuggestsFindingOwnerProcess()
        {
            var ex = BuildListenerFailure(new SocketException(10048));

            string hint = Describe(ex);

            Assert.False(string.IsNullOrEmpty(hint));
            Assert.Contains("已被其他进程占用", hint);
            Assert.Contains("netstat", hint);
            Assert.DoesNotContain("excludedportrange", hint);
        }

        /// <summary>取不到具体错误码时也应给出兜底提示，而非静默无输出。</summary>
        [Fact]
        public void ListenerFailureWithoutSocketError_StillGivesFallbackHint()
        {
            var ex = new ServiceResultException(
                "Failed to establish tcp listener sockets on port 4840 for Ipv4 and IPv6.");

            string hint = Describe(ex);

            Assert.False(string.IsNullOrEmpty(hint));
            Assert.Contains("4840", hint);
        }

        /// <summary>非端口问题的启动失败不得附加提示，避免噪声与误导。</summary>
        [Fact]
        public void UnrelatedFailure_GivesNoHint()
        {
            Assert.Null(Describe(new InvalidOperationException("服务器未启动")));
            Assert.Null(Describe(new ServiceResultException(
                "The certificate with subject CN=X in the configuration is invalid.")));
        }

        [Fact]
        public void NullException_GivesNoHint()
        {
            Assert.Null(Describe(null));
        }

        /// <summary>
        /// 证书/配置类错误常带有 SocketException 内部异常时，也不能误判为端口问题 ——
        /// 必须同时满足"消息含监听器失败标记"才提示。
        /// </summary>
        [Fact]
        public void SocketErrorWithoutListenerMarker_IsNotTreatedAsPortProblem()
        {
            var inner = new AggregateException(new SocketException(10013));
            var ex = new ServiceResultException(
                StatusCodes.BadInternalError, "some unrelated failure", inner);

            Assert.Null(Describe(ex));
        }

        /// <summary>提示中的端口号应跟随实际配置，而非写死。</summary>
        [Fact]
        public void Hint_UsesConfiguredPort()
        {
            string hint = Describe(BuildListenerFailure(new SocketException(10013)), 1111);
            Assert.Contains("1111", hint);
        }

        /// <summary>构造与现场一致的异常链。</summary>
        private static Exception BuildListenerFailure(Exception socketError)
        {
            var aggregate = new AggregateException(socketError);
            return new ServiceResultException(
                StatusCodes.BadInternalError,
                "Failed to establish tcp listener sockets on port 4840 for Ipv4 and IPv6.",
                aggregate);
        }
    }
}
