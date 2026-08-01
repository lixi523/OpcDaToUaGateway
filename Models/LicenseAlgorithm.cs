using System;
using System.Management;
using System.Security.Cryptography;
using System.Text;

namespace OpcDaToUaGateway
{
    /// <summary>
    /// 授权码算法，主程序和授权码生成工具（Keygen）共享此类。
    /// 
    /// <para>算法原理：
    /// 1. PCID = SHA256(CPU序列号 + 主板序列号 + BIOS序列号) 取前 16 个十六进制字符；
    /// 2. 授权码 = HMAC-SHA256(派生密钥, PCID) 取前 20 字节，格式化为 XXXX-XXXX-XXXX-XXXX-XXXX；
    /// 3. 验证时只需重新计算 HMAC 并做常数时间比较，无需反向解密。</para>
    /// 
    /// <para>C-11 修复：密钥不再以明文常量嵌入 IL 元数据中，而是通过三层异或混淆
    /// 存储于三个独立的字节数组中，运行时按位异或后再经 SHA256 哈希派生真实密钥。
    /// 这使得通过简单二进制字符串搜索（如 strings.exe）无法直接找到密钥，
    /// 显著提高了静态逆向分析的门槛。</para>
    /// 
    /// <para>TODO: 当前方案本质上仍是共享密钥（对称 HMAC），攻击者通过反编译提取三层数据
    /// 后仍可还原密钥并自行生成合法授权码。长期方案应迁移到非对称签名体系（如 ECDSA P-256）：
    /// - 授权端持有私钥签名生成授权码；
    /// - 客户端仅持有公钥验证签名；
    /// - 即使客户端被完全反编译，也无法从公钥反推私钥来伪造授权码。
    /// 迁移时需考虑：旧授权码的向后兼容、密钥轮换策略、离线激活流程。</para>
    /// </summary>
    public static class LicenseAlgorithm
    {
        // =====================================================================
        // 三层异或混淆密钥存储
        // =====================================================================
        // 真实密钥 = SHA256(_layer1 XOR _layer2 XOR _layer3)
        //
        // 设计意图：
        // - 任何单层数组中的字节都不是有效密钥的一部分；
        // - 必须同时获取全部三个数组并按位异或，才能得到异或中间值；
        // - 异或中间值还需再经过 SHA256 哈希才得到最终 HMAC 密钥；
        // - 这防止了简单的二进制字符串搜索（如 strings.exe 或 hex 编辑器搜索），
        //   迫使攻击者必须理解 DeriveKey() 的运算逻辑才能还原密钥。
        //
        // 安全性边界：
        // - 此方案可抵御浅层静态分析（字符串扫描、简单反编译）；
        // - 但无法抵御深度逆向（攻击者调试 DeriveKey 方法即可获取运行时密钥）；
        // - 彻底解决需迁移到非对称签名（见类级 TODO）。
        // =====================================================================

        /// <summary>混淆层 1：密钥异或的第一层数据，单独无意义。</summary>
        private static readonly byte[] _layer1 = new byte[]
        {
            0xA3, 0xC2, 0x0E, 0xD5, 0x17, 0xF8, 0x46, 0x91,
            0x7B, 0xE4, 0x6A, 0x33, 0x8D, 0x15, 0xCF, 0x7E,
            0x56, 0xAA, 0xF1, 0x2C, 0x93, 0xD7, 0x4B, 0x68,
            0xE0, 0x19, 0x5F, 0xB4, 0x72, 0xC6, 0x3D, 0x85
        };

        /// <summary>混淆层 2：密钥异或的第二层数据，单独无意义。</summary>
        private static readonly byte[] _layer2 = new byte[]
        {
            0x6D, 0x37, 0xB1, 0x4E, 0x9A, 0x02, 0xCC, 0x58,
            0x2F, 0x73, 0xE8, 0xD6, 0x14, 0xAB, 0x60, 0x39,
            0x81, 0x47, 0x2D, 0xF5, 0xBC, 0x63, 0x9E, 0x0A,
            0x7C, 0xA5, 0x18, 0x4F, 0xD3, 0x5B, 0x92, 0xE6
        };

        /// <summary>混淆层 3：密钥异或的第三层数据，单独无意义。</summary>
        private static readonly byte[] _layer3 = new byte[]
        {
            0x1F, 0x88, 0x74, 0x62, 0xC0, 0xA6, 0x53, 0xEB,
            0x38, 0x97, 0x49, 0x0D, 0xFE, 0x76, 0xD1, 0x24,
            0xB5, 0x3C, 0x8A, 0x1E, 0x50, 0xAF, 0x67, 0x9D,
            0x0B, 0xCC, 0x43, 0x86, 0x3E, 0xF9, 0x21, 0x57
        };

        /// <summary>
        /// 运行时派生 HMAC 密钥：三层异或 + SHA256 哈希。
        /// 
        /// <para>计算步骤：
        /// 1. 对三个混淆层的对应字节做异或运算，得到 32 字节的中间值；
        /// 2. 对中间值做 SHA256 哈希，得到 32 字节的最终 HMAC-SHA256 密钥。</para>
        /// 
        /// <para>SHA256 哈希的作用：即使攻击者通过某种方式知道了异或中间值，
        /// 也无法直接用于 HMAC 计算，必须额外识别出此哈希步骤。</para>
        /// </summary>
        /// <returns>32 字节的 HMAC-SHA256 密钥。</returns>
        private static byte[] DeriveKey()
        {
            // 第一步：三层逐字节异或，还原中间值
            byte[] xored = new byte[32];
            for (int i = 0; i < 32; i++)
                xored[i] = (byte)(_layer1[i] ^ _layer2[i] ^ _layer3[i]);

            // 第二步：SHA256 哈希得到最终密钥
            using (var sha = SHA256.Create())
                return sha.ComputeHash(xored);
        }

        /// <summary>
        /// 获取 AES 加密密钥，用于加密配置文件中存储的授权码。
        /// 授权码不再明文存储，使用 AES-256-CBC 加密后写入 config.json。
        /// </summary>
        public static byte[] GetEncryptionKey() => DeriveKey();

        /// <summary>
        /// 生成机器唯一标识 PCID（Personal Computer ID），基于硬件指纹。
        /// 
        /// <para>PCID 由以下 WMI 硬件属性组合后经 SHA256 哈希生成：
        /// - CPU 序列号（Win32_Processor.ProcessorId）
        /// - 主板序列号（Win32_BaseBoard.SerialNumber）
        /// - BIOS 序列号（Win32_BIOS.SerialNumber）</para>
        /// 
        /// <para>组合多个硬件标识的原因：单一硬件标识的唯一性不足
        /// （如相同型号 CPU 的 ProcessorId 可能相同），多标识组合可显著降低碰撞概率。</para>
        /// 
        /// <para>P4 修复：当所有 WMI 查询均失败时抛出异常，而非回退到 "UNKNOWN" 占位值。
        /// 回退方案会导致所有 WMI 异常的机器共享同一个 PCID，从而共享同一个授权码，
        /// 违反一机一码的授权模型。</para>
        /// </summary>
        /// <returns>16 个十六进制字符组成的 PCID 字符串。</returns>
        /// <exception cref="InvalidOperationException">
        /// 当所有 WMI 查询（CPU、主板、BIOS）均无法获取有效值时抛出。
        /// </exception>
        public static string GeneratePCID()
        {
            string cpuId = GetWmiValue("Win32_Processor", "ProcessorId");
            string boardSerial = GetWmiValue("Win32_BaseBoard", "SerialNumber");
            string biosSerial = GetWmiValue("Win32_BIOS", "SerialNumber");

            // 所有硬件标识获取失败时拒绝生成，防止共享 PCID 导致授权绕过
            if (cpuId == "UNKNOWN" && boardSerial == "UNKNOWN" && biosSerial == "UNKNOWN")
                throw new InvalidOperationException(
                    "无法获取硬件标识（WMI 查询全部失败），请检查 WMI 服务是否正常运行。");

            // 以管道分隔符组合多个硬件标识，保证各标识边界清晰（避免拼接歧义）
            string rawId = $"{cpuId}|{boardSerial}|{biosSerial}";

            using (var sha256 = SHA256.Create())
            {
                byte[] hash = sha256.ComputeHash(Encoding.UTF8.GetBytes(rawId));
                // 取哈希前 8 字节（16 个十六进制字符）作为 PCID，
                // 64 bit 空间对于机器标识已足够，且方便用户手动抄录
                var sb = new StringBuilder(16);
                for (int i = 0; i < 8; i++)
                    sb.Append(hash[i].ToString("X2"));
                return sb.ToString();
            }
        }

        /// <summary>
        /// 根据 PCID 计算授权码。
        /// 
        /// <para>算法：HMAC-SHA256(派生密钥, PCID大写) → 取前 20 字节 → 格式化为 5 组 4 字符。</para>
        /// 
        /// <para>输出格式示例：A1B2-C3D4-E5F6-7890-ABCD（共 29 字符，含 4 个连字符）。</para>
        /// </summary>
        /// <param name="pcid">目标机器的 PCID。</param>
        /// <returns>格式化的授权码字符串。</returns>
        /// <exception cref="ArgumentException">当 <paramref name="pcid"/> 为空时抛出。</exception>
        public static string GenerateAuthCode(string pcid)
        {
            if (string.IsNullOrEmpty(pcid))
                throw new ArgumentException("PCID 不能为空", nameof(pcid));

            byte[] key = DeriveKey();
            using (var hmac = new HMACSHA256(key))
            {
                // PCID 统一转大写，确保不同大小写输入产生相同授权码
                byte[] hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(pcid.ToUpperInvariant()));

                // 取前 20 字节（160 bit），格式化为 XXXX-XXXX-XXXX-XXXX-XXXX
                // 160 bit 对于授权码暴力破解已足够（2^160 种可能）
                var sb = new StringBuilder(29);
                for (int i = 0; i < 20; i++)
                {
                    if (i > 0 && i % 4 == 0)
                        sb.Append('-');
                    sb.Append(hash[i].ToString("X2"));
                }
                return sb.ToString();
            }
        }

        /// <summary>
        /// 验证授权码是否与指定 PCID 匹配。
        /// 
        /// <para>H-12 修复：采用常数时间比较（Constant-Time Comparison），
        /// 将长度差异和字符差异统一纳入同一个 diff 累加器，确保比较耗时
        /// 与输入内容无关，防止攻击者通过精确测量响应时间来逐字符推断正确授权码
        /// （即计时攻击，Timing Attack）。</para>
        /// 
        /// <para>具体实现细节：
        /// - diff 初始值 = a.Length XOR trimmed.Length：长度不同时 diff 不为零；
        /// - 遍历公共长度部分：diff |= a[i] XOR trimmed[i]：任何字符不同都会置位；
        /// - 遍历 trimmed 超出部分：diff |= trimmed[i]：额外字符也纳入 diff；
        /// - 最终 diff == 0 当且仅当长度相等且所有字符完全匹配。</para>
        /// </summary>
        /// <param name="pcid">机器的 PCID。</param>
        /// <param name="authCode">用户输入的授权码。</param>
        /// <returns>授权码有效返回 true，否则返回 false。</returns>
        public static bool VerifyAuthCode(string pcid, string authCode)
        {
            if (string.IsNullOrEmpty(pcid) || string.IsNullOrEmpty(authCode))
                return false;

            string expected = GenerateAuthCode(pcid);
            string trimmed = authCode.Trim().ToUpperInvariant();
            string a = expected.ToUpperInvariant();

            // 常数时间比较
            // 将长度差异编码到 diff 中：如果两个字符串长度不同，XOR 结果非零，
            // diff 从一开始就不为零，后续遍历不会改变这个事实。
            // 这避免了先比较长度再比较内容的分支差异（短字符串提前返回的耗时差异）。
            int diff = a.Length ^ trimmed.Length;
            int minLen = Math.Min(a.Length, trimmed.Length);
            // 遍历公共长度部分，按位异或累加到 diff
            for (int i = 0; i < minLen; i++)
                diff |= a[i] ^ trimmed[i];
            // 如果用户输入比期望值更长，额外字符也纳入 diff 累加，
            // 确保超长输入不会绕过长度检查
            for (int i = minLen; i < trimmed.Length; i++)
                diff |= trimmed[i];

            return diff == 0;
        }

        /// <summary>
        /// 通过 WMI 查询指定硬件类的属性值。
        /// 
        /// <para>H-13 修复：ManagementObject 在 finally 块中显式 Dispose，
        /// 防止 WMI COM 对象泄漏（长时间运行的服务场景下尤其重要）。</para>
        /// 
        /// <para>查询失败时返回 "UNKNOWN" 而非抛异常，允许调用方根据
        /// 多个标识的获取结果综合判断（见 <see cref="GeneratePCID"/> 中的 P4 修复逻辑）。</para>
        /// </summary>
        /// <param name="wmiClass">WMI 类名（如 "Win32_Processor"）。</param>
        /// <param name="propertyName">属性名（如 "ProcessorId"）。</param>
        /// <returns>属性值字符串，查询失败或值为空时返回 "UNKNOWN"。</returns>
        private static string GetWmiValue(string wmiClass, string propertyName)
        {
            try
            {
                using (var searcher = new ManagementObjectSearcher($"SELECT {propertyName} FROM {wmiClass}"))
                using (var results = searcher.Get())
                {
                    foreach (ManagementObject obj in results)
                    {
                        try
                        {
                            object val = obj[propertyName];
                            if (val != null)
                            {
                                string str = val.ToString().Trim();
                                if (!string.IsNullOrEmpty(str))
                                    return str;
                            }
                        }
                        finally
                        {
                            // H-13 修复：无论是否成功读取属性，都确保释放 COM 对象
                            obj.Dispose();
                        }
                    }
                }
            }
            catch
            {
                // WMI 查询可能因权限不足、服务未运行等原因失败，
                // 返回 "UNKNOWN" 由调用方（GeneratePCID）统一处理
            }
            return "UNKNOWN";
        }
    }
}
