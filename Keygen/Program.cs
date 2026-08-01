// =====================================================================
// OPC DA to UA Gateway — 授权码计算工具（Keygen）
// =====================================================================
//
// 本文件是独立的控制台应用程序，供管理员为终端用户生成授权码。
// 与网关主程序共享 LicenseAlgorithm 类，确保生成的授权码与验证逻辑一致。
//
// 运行模式：
//   1. 交互模式（无参数启动）：
//      提供菜单驱动的控制台界面，支持以下操作：
//      - 查看本机的 PCID（硬件指纹生成的机器唯一标识）
//      - 根据任意 PCID 生成授权码
//      适用于管理员在现场或远程协助用户时逐步操作。
//
//   2. 命令行模式（带参数启动）：
//      直接将 PCID 作为第一个命令行参数传入，输出授权码后立即退出。
//      适用于批量生成或脚本集成场景（如：Keygen.exe ABCD1234EF567890）。
//
// 部署说明：
//   此工具应仅部署在管理员控制的机器上，不应分发给终端用户。
//   工具与主程序共享相同的密钥派生逻辑，因此持有此工具等同于持有授权能力。
// =====================================================================

using System;

namespace OpcDaToUaGateway.Keygen
{
    /// <summary>
    /// 授权码计算工具的控制台入口。
    /// 
    /// <para>支持两种运行模式：
    /// - 交互模式（无参数启动）：菜单驱动，支持查看本机 PCID 和为任意 PCID 生成授权码；
    /// - 命令行模式（带参数启动）：第一个参数为 PCID，直接输出授权码后退出。</para>
    /// </summary>
    class Program
    {
        /// <summary>
        /// 工具入口点。根据是否传入命令行参数决定运行模式。
        /// </summary>
        /// <param name="args">
        /// 可选参数：args[0] 为目标机器的 PCID。
        /// 传入时进入命令行模式（直接生成并退出）；
        /// 不传入时进入交互模式（菜单循环）。
        /// </param>
        static void Main(string[] args)
        {
            // 设置控制台输出编码为 UTF-8，确保中文正确显示
            Console.OutputEncoding = System.Text.Encoding.UTF8;
            Console.Title = "OPC DA to UA Gateway - 授权码计算工具";

            Console.WriteLine("================================================");
            Console.WriteLine("  OPC DA → OPC UA 网关 授权码计算工具 v2.4.0");
            Console.WriteLine("================================================");
            Console.WriteLine();

            if (args.Length > 0)
            {
                // ---- 命令行模式 ----
                // 直接将第一个参数视为 PCID，生成授权码后退出。
                // 适用于自动化脚本调用或批量生成场景。
                string pcid = args[0].Trim();
                GenerateForPcid(pcid);
                return;
            }

            // ---- 交互模式 ----
            // 菜单驱动的循环，用户可反复操作直到选择退出。
            while (true)
            {
                Console.WriteLine("请选择操作:");
                Console.WriteLine("  [1] 查看本机 PCID");
                Console.WriteLine("  [2] 根据 PCID 生成授权码");
                Console.WriteLine("  [q] 退出");
                Console.WriteLine();
                Console.Write("请输入选项: ");

                string choice = Console.ReadLine()?.Trim();
                Console.WriteLine();

                // P5 修复：处理 stdin EOF（ReadLine 返回 null）
                // 当标准输入被重定向并到达 EOF 时（如管道关闭），
                // ReadLine 返回 null，此时应优雅退出而非 NullReferenceException
                if (choice == null)
                    return;

                switch (choice.ToLowerInvariant())
                {
                    case "1":
                        ShowLocalPcid();
                        break;
                    case "2":
                        Console.Write("请输入目标机器的 PCID: ");
                        string pcidInput = Console.ReadLine()?.Trim();
                        if (!string.IsNullOrEmpty(pcidInput))
                        {
                            GenerateForPcid(pcidInput);
                        }
                        else
                        {
                            Console.WriteLine("[错误] PCID 不能为空");
                        }
                        break;
                    case "q":
                    case "quit":
                    case "exit":
                        return;
                    default:
                        Console.WriteLine("[提示] 无效选项，请重新选择");
                        break;
                }
                Console.WriteLine();
                Console.WriteLine(new string('-', 48));
                Console.WriteLine();
            }
        }

        /// <summary>
        /// 查询并显示本机的 PCID（基于当前机器的 CPU、主板、BIOS 硬件指纹）。
        /// 用于管理员在现场快速获取本机标识以生成授权码。
        /// </summary>
        private static void ShowLocalPcid()
        {
            try
            {
                string pcid = LicenseAlgorithm.GeneratePCID();
                Console.WriteLine($"本机 PCID: {pcid}");
                Console.WriteLine();
                Console.WriteLine("请将此 PCID 提供给管理员以获取授权码。");
            }
            catch (Exception ex)
            {
                // WMI 服务异常或硬件标识全部获取失败时，给出可读的错误信息
                Console.WriteLine($"[错误] 无法获取本机 PCID: {ex.Message}");
            }
        }

        /// <summary>
        /// 根据指定的 PCID 生成授权码并显示。
        /// PCID 会自动转为大写，确保与验证端的一致性。
        /// </summary>
        /// <param name="pcid">目标机器的 PCID 字符串。</param>
        private static void GenerateForPcid(string pcid)
        {
            try
            {
                Console.WriteLine($"输入 PCID:  {pcid.ToUpperInvariant()}");
                string authCode = LicenseAlgorithm.GenerateAuthCode(pcid);
                Console.WriteLine($"生成授权码: {authCode}");
                Console.WriteLine();
                Console.WriteLine("请将此授权码填入目标机器上网关程序的\"关于\"对话框中。");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[错误] 生成授权码失败: {ex.Message}");
            }
        }
    }
}
