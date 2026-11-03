using System;
using System.IO;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using OpcDaToUaGateway;
using OpcDaToUaGateway.Models;
using Xunit;

namespace OpcDaToUaGateway.Tests
{
    /// <summary>
    /// GatewayOpcUaServer 应用证书自愈能力测试。
    /// 
    /// 背景：UA SDK 仅在"找不到证书"时才创建新证书。若 Certificates\Own 下已存在
    /// 无效证书（过期，或整目录从别的机器复制而来导致私钥不可用），SDK 会走
    /// "校验失败"分支抛 ServiceResultException；又因本项目构造 ApplicationInstance
    /// 时未提供 MessageDlg，SDK 无法询问用户、只能静默拒绝 —— 于是每次启动必然失败，
    /// 只能人工删目录。
    /// 
    /// 这些用例直接跑真实的 StartAsync，验证过期证书/异机证书能自动重建。
    /// 证书目录取自 AppDomain.CurrentDomain.BaseDirectory（全局唯一），因此每个用例
    /// 在开始前重建该目录；xunit 同一测试类内的方法串行执行，不会互相干扰。
    /// 端口使用非默认值，避免与运行中的网关（4840）冲突。
    /// </summary>
    public class ApplicationCertificateRecoveryTests : IDisposable
    {
        private const string RecoveryMarker = "自动重建";
        private const int TestPort = 24841;

        private readonly string _certRoot;

        public ApplicationCertificateRecoveryTests()
        {
            _certRoot = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Certificates");
            ResetCertificateStore();
        }

        public void Dispose()
        {
            ResetCertificateStore();
        }

        private void ResetCertificateStore()
        {
            try
            {
                if (Directory.Exists(_certRoot))
                    Directory.Delete(_certRoot, true);
            }
            catch (IOException) { /* 上个用例的残留文件锁，交由用例自身断言暴露 */ }
        }

        private string OwnPath => Path.Combine(_certRoot, "Own");

        /// <summary>过期证书：应自动清理并重建，网关正常启动。</summary>
        [Fact]
        public void ExpiredCertificate_IsRebuiltAndServerStarts()
        {
            Plant("CN=GwCertTest, C=CN, O=Gateway, DC=" + Environment.MachineName, expired: true);

            var r = StartServer();
            Assert.True(r.Started, "网关应正常启动。日志：\n" + r.Log);
            Assert.True(r.SawRecovery, "应输出证书自动重建的诊断信息。日志：\n" + r.Log);
        }

        /// <summary>
        /// 异机证书（目录被整体复制，主题机器名与当前环境不符）：
        /// 应自动清理并重建，网关正常启动。
        /// </summary>
        [Fact]
        public void ForeignMachineCertificate_IsRebuiltAndServerStarts()
        {
            Plant("CN=GwCertTest, C=CN, O=Gateway, DC=desktop-p9jb20m", expired: false);

            var r = StartServer();
            Assert.True(r.Started, "网关应正常启动。日志：\n" + r.Log);
            Assert.True(r.SawRecovery, "应输出证书自动重建的诊断信息。日志：\n" + r.Log);
        }

        /// <summary>
        /// 证书文件被其他进程占用时，自动清理无法完成：必须如实报告失败并给出
        /// 人工处理指引，绝不能谎报"已清理"后继续重试。
        /// 
        /// 回归用例：早期实现里 per-file 的 <c>catch (IOException)</c> 吞掉了删除失败，
        /// 却仍返回 true，导致日志显示"已清理旧证书"、重试再抛同样的原始异常，
        /// 用户既看不到哪个文件删不掉，也看不到手动处理的提示。
        /// </summary>
        [Fact]
        public void LockedCertificateFile_ReportsFailureInsteadOfFalselyClaimingCleanup()
        {
            Plant("CN=GwCertTest, C=CN, O=Gateway, DC=" + Environment.MachineName, expired: true);

            // 独占打开一个证书文件，使其在测试期间无法被删除
            var locked = LockFirstCertificateFile();
            try
            {
                var r = StartServer();
                Assert.False(r.Started, "证书文件被占用时不应启动成功。日志：\n" + r.Log);
                Assert.False(r.ClaimedCleanup,
                    "清理未完成时不得输出\"已清理旧证书\"。日志：\n" + r.Log);
                Assert.True(r.SawManualHint,
                    "应输出手动删除目录的指引。日志：\n" + r.Log);
                Assert.True(r.SawLockedFileDetail,
                    "应指出具体哪个文件无法删除。日志：\n" + r.Log);
            }
            finally
            {
                locked?.Dispose();
            }
        }

        /// <summary>
        /// 锁定 private/ 下的私钥文件，使 SDK 能找到证书、却无法加载私钥
        /// （对应"目录从其他机器复制而来"或"文件被占用"的真实场景）。
        /// 
        /// 注意不能锁 certs/ 下的 .der：那样 SDK 连证书都读不到，会认为证书不存在
        /// 而直接新建一张，压根走不到清理分支，测不到本用例想验证的行为。
        /// </summary>
        private FileStream LockFirstCertificateFile()
        {
            string priv = Path.Combine(OwnPath, "private");
            if (Directory.Exists(priv))
            {
                foreach (var f in Directory.EnumerateFiles(priv))
                    return new FileStream(f, FileMode.Open, FileAccess.Read, FileShare.None);
            }
            return null;
        }

        /// <summary>
        /// 全新安装（Own 为空）：必须正常启动，且不得触发重建流程
        /// （防止把正常路径也走一遍删除逻辑）。
        /// </summary>
        [Fact]
        public void CleanInstall_StartsWithoutTriggeringRecovery()
        {
            var r = StartServer();
            Assert.True(r.Started, "网关应正常启动。日志：\n" + r.Log);
            Assert.False(r.SawRecovery, "全新安装不应触发证书重建。日志：\n" + r.Log);
        }

        private sealed class StartResult
        {
            public bool Started;
            public bool SawRecovery;
            public bool ClaimedCleanup;
            public bool SawManualHint;
            public bool SawLockedFileDetail;
            public string Log = "";
        }

        /// <summary>展开异常链，便于定位 BadInternalError 的真实来源。</summary>
        private static string Describe(Exception ex)
        {
            var sb = new System.Text.StringBuilder();
            int depth = 0;
            while (ex != null && depth++ < 6)
            {
                sb.Append(ex.GetType().Name).Append(": ")
                  .Append(ex.Message.Replace("\r", " ").Replace("\n", " "));
                ex = ex.InnerException;
                if (ex != null) sb.Append(" --> ");
            }
            return sb.ToString();
        }

        private StartResult StartServer()
        {
            var cfg = new OpcUaConfig
            {
                ServerName = "GwCertTest",
                // NamespaceUri 必须非空：GatewayNodeManager 构造时会用它注册命名空间
                NamespaceUri = "http://gateway.example.com/CertTest/",
                Port = TestPort,
                ListenAddress = "localhost",
                SecurityMode = "None",
                SecurityPolicy = "None",
                AutoAcceptCertificates = true,
                MaxSessionCount = 5,
                SessionTimeout = 60000
            };

            var server = new GatewayOpcUaServer(cfg);
            var log = new System.Text.StringBuilder();
            server.OnStatusChanged += m => { lock (log) log.AppendLine(m); };

            var result = new StartResult();
            try
            {
                server.StartAsync().GetAwaiter().GetResult();
                result.Started = server.IsRunning;
            }
            catch (Exception ex)
            {
                // StartAsync 失败会重新抛出；这属于被测行为，记入日志即可
                lock (log) log.AppendLine("EX: " + Describe(ex));
            }
            finally
            {
                try { server.Dispose(); } catch { }
            }

            lock (log) result.Log = log.ToString();
            result.SawRecovery = result.Log.Contains(RecoveryMarker);
            result.ClaimedCleanup = result.Log.Contains("已清理旧证书");
            result.SawManualHint = result.Log.Contains("手动删除");
            result.SawLockedFileDetail = result.Log.Contains("无法删除");
            return result;
        }

        private void Plant(string subject, bool expired)
        {
            string own = OwnPath;
            Directory.CreateDirectory(own);

            using (var rsa = RSA.Create(2048))
            {
                var req = new CertificateRequest(subject, rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
                req.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
                req.CertificateExtensions.Add(new X509KeyUsageExtension(
                    X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment
                    | X509KeyUsageFlags.NonRepudiation, false));

                var nb = expired ? DateTimeOffset.UtcNow.AddYears(-3) : DateTimeOffset.UtcNow.AddDays(-1);
                var na = expired ? DateTimeOffset.UtcNow.AddDays(-1) : DateTimeOffset.UtcNow.AddYears(1);
                var created = req.CreateSelfSigned(nb, na);
                var cert = new X509Certificate2(created.Export(X509ContentType.Pfx, ""), "",
                    X509KeyStorageFlags.Exportable);

                string certs = Path.Combine(own, "certs");
                string priv = Path.Combine(own, "private");
                Directory.CreateDirectory(certs);
                Directory.CreateDirectory(priv);
                // 与 SDK 的 Directory store 命名一致："<subject> [<thumbprint>].der/.pfx"
                string b = cert.Subject + " [" + cert.Thumbprint + "]";
                File.WriteAllBytes(Path.Combine(certs, b + ".der"), cert.RawData);
                File.WriteAllBytes(Path.Combine(priv, b + ".pfx"), cert.Export(X509ContentType.Pfx, ""));
            }
        }
    }
}
