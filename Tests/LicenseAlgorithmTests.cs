using System;
using System.Security.Cryptography;
using System.Text;
using OpcDaToUaGateway;
using Xunit;

namespace OpcDaToUaGateway.Tests
{
    /// <summary>
    /// LicenseAlgorithm 单元测试。
    /// 测试 DeriveKey 确定性、GenerateAuthCode 格式与一致性、
    /// VerifyAuthCode 常数时间比较逻辑、PCID 生成格式。
    /// </summary>
    public class LicenseAlgorithmTests
    {
        // =====================================================================
        // DeriveKey 确定性测试 — 通过反射调用私有方法
        // =====================================================================

        [Fact]
        public void DeriveKey_ReturnsConsistentKey()
        {
            // 通过反射调用私有静态方法 DeriveKey
            var method = typeof(LicenseAlgorithm).GetMethod(
                "DeriveKey",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);

            Assert.NotNull(method);

            var key1 = (byte[])method.Invoke(null, null);
            var key2 = (byte[])method.Invoke(null, null);

            Assert.NotNull(key1);
            Assert.NotNull(key2);
            Assert.Equal(32, key1.Length);
            Assert.Equal(32, key2.Length);
            Assert.Equal(key1, key2);
        }

        [Fact]
        public void DeriveKey_KeyIsNotAllZeros()
        {
            var method = typeof(LicenseAlgorithm).GetMethod(
                "DeriveKey",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);

            var key = (byte[])method.Invoke(null, null);

            foreach (var b in key)
            {
                Assert.NotEqual(0, b);
            }
        }

        // =====================================================================
        // GenerateAuthCode 格式与一致性测试
        // =====================================================================

        [Fact]
        public void GenerateAuthCode_ReturnsCorrectFormat()
        {
            const string testPcid = "A1B2C3D4E5F67890";
            var authCode = LicenseAlgorithm.GenerateAuthCode(testPcid);

            // 取前 20 字节 = 40 hex 字符 + 4 个连字符 = 44 字符
            // 格式: XXXX-XXXX-...-XXXX (每4个hex加一个-)
            Assert.Equal(44, authCode.Length);
            Assert.Matches(@"^[0-9A-F]{40}$", authCode.Replace("-", ""));
        }

        [Fact]
        public void GenerateAuthCode_DeterministicForSamePcid()
        {
            const string testPcid = "TESTPCID12345678";
            var code1 = LicenseAlgorithm.GenerateAuthCode(testPcid);
            var code2 = LicenseAlgorithm.GenerateAuthCode(testPcid);

            Assert.Equal(code1, code2);
        }

        [Fact]
        public void GenerateAuthCode_DifferentPcidProducesDifferentCode()
        {
            var code1 = LicenseAlgorithm.GenerateAuthCode("PCID123456780001");
            var code2 = LicenseAlgorithm.GenerateAuthCode("PCID123456780002");

            Assert.NotEqual(code1, code2);
        }

        [Fact]
        public void GenerateAuthCode_CaseInsensitivePcid()
        {
            // PCID 统一转大写，不同大小写应产生相同授权码
            var codeUpper = LicenseAlgorithm.GenerateAuthCode("A1B2C3D4E5F67890");
            var codeLower = LicenseAlgorithm.GenerateAuthCode("a1b2c3d4e5f67890");
            var codeMixed = LicenseAlgorithm.GenerateAuthCode("A1b2C3d4E5f67890");

            Assert.Equal(codeUpper, codeLower);
            Assert.Equal(codeUpper, codeMixed);
        }

        [Fact]
        public void GenerateAuthCode_ThrowsForNullOrEmptyPcid()
        {
            Assert.ThrowsAny<ArgumentException>(() => LicenseAlgorithm.GenerateAuthCode(null));
            Assert.ThrowsAny<ArgumentException>(() => LicenseAlgorithm.GenerateAuthCode(""));
        }

        // =====================================================================
        // VerifyAuthCode 常数时间比较测试
        // =====================================================================

        [Fact]
        public void VerifyAuthCode_ValidCode_ReturnsTrue()
        {
            const string pcid = "A1B2C3D4E5F67890";
            var authCode = LicenseAlgorithm.GenerateAuthCode(pcid);

            Assert.True(LicenseAlgorithm.VerifyAuthCode(pcid, authCode));
        }

        [Fact]
        public void VerifyAuthCode_CorrectCodeWithSpaces_ReturnsTrue()
        {
            const string pcid = "A1B2C3D4E5F67890";
            var authCode = LicenseAlgorithm.GenerateAuthCode(pcid);

            // Trim 应该被去除
            Assert.True(LicenseAlgorithm.VerifyAuthCode(pcid, "  " + authCode + "  "));
        }

        [Fact]
        public void VerifyAuthCode_LowercaseCode_ReturnsTrue()
        {
            const string pcid = "A1B2C3D4E5F67890";
            var authCode = LicenseAlgorithm.GenerateAuthCode(pcid).ToLowerInvariant();

            Assert.True(LicenseAlgorithm.VerifyAuthCode(pcid, authCode));
        }

        [Fact]
        public void VerifyAuthCode_WrongCode_ReturnsFalse()
        {
            const string pcid = "A1B2C3D4E5F67890";
            var correctCode = LicenseAlgorithm.GenerateAuthCode(pcid);

            // 修改最后一个字符
            var wrongCode = correctCode.Substring(0, correctCode.Length - 1) +
                            (correctCode[correctCode.Length - 1] == 'F' ? '0' : 'F');

            Assert.False(LicenseAlgorithm.VerifyAuthCode(pcid, wrongCode));
        }

        [Fact]
        public void VerifyAuthCode_TooShortCode_ReturnsFalse()
        {
            const string pcid = "A1B2C3D4E5F67890";
            Assert.False(LicenseAlgorithm.VerifyAuthCode(pcid, "AAAA"));
        }

        [Fact]
        public void VerifyAuthCode_TooLongCode_ReturnsFalse()
        {
            const string pcid = "A1B2C3D4E5F67890";
            var correctCode = LicenseAlgorithm.GenerateAuthCode(pcid);
            var tooLong = correctCode + "-FFFF";

            Assert.False(LicenseAlgorithm.VerifyAuthCode(pcid, tooLong));
        }

        [Fact]
        public void VerifyAuthCode_NullPcid_ReturnsFalse()
        {
            Assert.False(LicenseAlgorithm.VerifyAuthCode(null, "AAAA-BBBB-CCCC-DDDD-EEEE"));
        }

        [Fact]
        public void VerifyAuthCode_NullAuthCode_ReturnsFalse()
        {
            Assert.False(LicenseAlgorithm.VerifyAuthCode("A1B2C3D4E5F67890", null));
        }

        [Fact]
        public void VerifyAuthCode_EmptyAuthCode_ReturnsFalse()
        {
            Assert.False(LicenseAlgorithm.VerifyAuthCode("A1B2C3D4E5F67890", ""));
        }

        // =====================================================================
        // 端到端测试：生成后验证
        // =====================================================================

        [Fact]
        public void EndToEnd_GenerateAndVerify()
        {
            for (int i = 0; i < 10; i++)
            {
                string pcid = $"PCID{i:D16}";
                var authCode = LicenseAlgorithm.GenerateAuthCode(pcid);
                Assert.True(LicenseAlgorithm.VerifyAuthCode(pcid, authCode),
                    $"Failed for PCID={pcid}, Code={authCode}");
            }
        }

        [Fact]
        public void EndToEnd_VerifyAgainstWrongPcid_Fails()
        {
            const string pcid1 = "PCID123456780001";
            const string pcid2 = "PCID123456780002";

            var authCode = LicenseAlgorithm.GenerateAuthCode(pcid1);

            // 用 pcid1 生成的授权码验证 pcid2 应该失败
            Assert.False(LicenseAlgorithm.VerifyAuthCode(pcid2, authCode));
        }
    }
}
