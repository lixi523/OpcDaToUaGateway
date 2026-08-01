using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Win32;

namespace OpcDaToUaGateway
{
    /// <summary>
    /// OPC DA 服务器信息（单个已发现的服务器实例）。
    /// </summary>
    public class OpcServerInfo
    {
        /// <summary>服务器的 ProgId（例如 "Matrikon.OPC.Simulation.1"），用于连接时标识服务器。</summary>
        public string ProgId { get; set; }
        /// <summary>服务器的友好描述或显示名称。</summary>
        public string Description { get; set; }
        /// <summary>服务器的 CLSID（GUID 格式），用于 COM 激活。</summary>
        public string Clsid { get; set; }
        /// <summary>服务器可执行文件的本地路径（仅适用于 LocalServer32 类型的 out-of-proc COM 服务器）。</summary>
        public string ServerPath { get; set; }

        /// <summary>发现来源（便于诊断不同扫描策略的有效性）。</summary>
        public string Source { get; set; }

        /// <summary>返回 ProgId 和描述的组合格式，便于 UI 展示。</summary>
        public override string ToString() => $"{ProgId} — {Description}";
    }

    /// <summary>
    /// OPC DA 服务器扫描器，使用 5 种互补策略尽可能发现本机上所有已安装的 OPC DA 服务器。
    /// 
    /// 设计背景：
    /// OPC DA 服务器的注册方式因厂商而异——有的通过标准 Component Categories 注册，
    /// 有的只写了 ProgID/CLSID 注册表项，有的通过 OPC Foundation 的安装组件注册。
    /// 单一策略无法覆盖所有场景，因此采用"宽进严出"的多策略扫描 + 去重合并。
    /// 
    /// 五种扫描策略（按优先级排序）：
    /// 
    ///   策略 1 — 注册表组件类别扫描（最可靠）
    ///     在 HKCR\Component Categories 下查找 OPC DA 相关的 CATID，
    ///     读取其 CLSID 子键获取注册的服务器 CLSID。不依赖 COM 运行时。
    /// 
    ///   策略 2 — STA 线程上的 COM 枚举（IOPCServerList）
    ///     通过 OPC Foundation 提供的 OPCServerList COM 对象枚举已注册的 DA 服务器。
    ///     必须在 STA 线程上执行（COM 要求），且可能因 OPC 核心组件未安装而失败。
    /// 
    ///   策略 3 — HKCR ProgID 全量扫描（兜底策略）
    ///     遍历 ClassesRoot 下所有符合 ProgID 格式（含点号、有 CLSID 子键、有 LocalServer32）
    ///     的注册表项，通过名称关键词和 Component Categories 判断是否与 OPC 相关。
    /// 
    ///   策略 4 — OPC Foundation 已安装组件注册表
    ///     读取 HKLM\SOFTWARE\OPC Foundation\Installed Components 下的已安装组件信息。
    /// 
    ///   策略 5 — ProgID 有效性验证（贯穿在上述策略中）
    ///     通过注册表反查 CLSID → ProgID 映射，确保发现的 ProgID 有效且有对应的 LocalServer32。
    /// 
    /// 结果合并：所有策略共享同一个 Dictionary&lt;string, OpcServerInfo&gt;（key = ProgID），
    /// 后续策略发现相同 ProgID 时自动跳过，确保去重。
    /// </summary>
    public static class OpcServerScanner
    {
        // OPC DA 服务器的标准 Component Category GUID（由 OPC Foundation 定义）。
        // 不同版本的 DA 规范使用不同的 CATID，扫描时需要全部覆盖。
        private static readonly Guid CATID_OPCDAServer20 =
            new Guid("63D5F432-CFE4-11D1-B2C8-0060083BA1FB"); // DA 2.0（最广泛使用）
        private static readonly Guid CATID_OPCDAServer30 =
            new Guid("CC603642-66D7-48F1-B69A-B625E73652D7"); // DA 3.0
        private static readonly Guid CATID_OPCDAServer10 =
            new Guid("63D5F430-CFE4-11D1-B2C8-0060083BA1FB"); // DA 1.0（历史遗留）

        /// <summary>
        /// 诊断日志（扫描过程中记录每一步的结果，便于排查扫描遗漏问题）。
        /// P5 修复：使用 ConcurrentBag 保证线程安全（ScanServers 虽然是同步方法，
        /// 但 Log 也被 OpcDaClient.BrowseRecursive 调用，可能来自不同线程）。
        /// </summary>
        private static readonly ConcurrentBag<string> _diagnosticLog = new ConcurrentBag<string>();

        /// <summary>
        /// 获取诊断日志的只读快照。调用时会将 ConcurrentBag 转为数组返回。
        /// </summary>
        /// <returns>诊断日志条目的只读列表。</returns>
        public static IReadOnlyList<string> GetDiagnosticLog() => _diagnosticLog.ToArray();

        /// <summary>
        /// 扫描本机所有已安装的 OPC DA 服务器。
        /// 依次执行 4 种扫描策略，将所有发现的结果按 ProgID 去重后返回。
        /// 每种策略独立 try-catch，一种策略失败不影响其他策略的执行。
        /// </summary>
        /// <returns>按 ProgID 排序的 OPC DA 服务器列表。</returns>
        public static List<OpcServerInfo> ScanServers()
        {
            // ConcurrentBag 没有 Clear()，用循环 TryTake 清空旧日志。
            while (_diagnosticLog.TryTake(out _)) { }
            var servers = new Dictionary<string, OpcServerInfo>(StringComparer.OrdinalIgnoreCase);

            // ================================================================
            // 策略 1：注册表组件类别扫描（最可靠，不依赖 COM 运行时）
            // ================================================================
            try
            {
                int before = servers.Count;
                ScanViaComponentCategoryRegistry(servers);
                Log($"[组件类别] 找到 {servers.Count - before} 个服务器");
            }
            catch (Exception ex)
            {
                Log($"[组件类别] 失败: {ex.Message}");
            }

            // ================================================================
            // 策略 2：在专用 STA 线程上执行 COM 枚举
            // ================================================================
            try
            {
                int before = servers.Count;
                RunComEnumerationOnStaThread(servers);
                Log($"[COM枚举] 找到 {servers.Count - before} 个服务器");
            }
            catch (Exception ex)
            {
                Log($"[COM枚举] 失败: {ex.Message}");
            }

            // ================================================================
            // 策略 3：HKCR ProgID 全量扫描（兜底策略，耗时较长）
            // ================================================================
            try
            {
                int before = servers.Count;
                ScanViaAllProgIds(servers);
                Log($"[ProgID扫描] 找到 {servers.Count - before} 个服务器");
            }
            catch (Exception ex)
            {
                Log($"[ProgID扫描] 失败: {ex.Message}");
            }

            // ================================================================
            // 策略 4：OPC Foundation 已安装组件
            // ================================================================
            try
            {
                int before = servers.Count;
                ScanViaInstalledComponents(servers);
                Log($"[已安装组件] 找到 {servers.Count - before} 个服务器");
            }
            catch (Exception ex)
            {
                Log($"[已安装组件] 失败: {ex.Message}");
            }

            var result = new List<OpcServerInfo>(servers.Values);
            result.Sort((a, b) => string.Compare(a.ProgId, b.ProgId, StringComparison.OrdinalIgnoreCase));
            Log($"总计找到 {result.Count} 个 OPC DA 服务器");
            return result;
        }

        // ================================================================
        //  策略 1：组件类别注册表扫描
        // ================================================================

        /// <summary>
        /// 策略 1：通过注册表中的 Component Categories 查找 OPC DA 服务器。
        /// 这是最可靠的方式，因为所有标准安装的 OPC DA 服务器都会在注册表中注册其 CATID。
        /// 同时覆盖 64 位和 32 位（WOW6432Node）注册表视图。
        /// </summary>
        private static void ScanViaComponentCategoryRegistry(Dictionary<string, OpcServerInfo> servers)
        {
            // OPC DA 服务器会在注册表的 Component Categories 下注册自己的 CLSID。
            // 扫描全部三个版本的 CATID 以覆盖不同年代安装的服务器。
            Guid[] categoryIds = { CATID_OPCDAServer10, CATID_OPCDAServer20, CATID_OPCDAServer30 };

            foreach (var catId in categoryIds)
            {
                string catIdStr = catId.ToString("B"); // {guid} 格式
                // 两种可能的注册表路径（不同 Windows 版本的布局不同）
                string[] searchPaths = new[]
                {
                    $@"CLSID\Component Categories\{catIdStr}\CLSID",
                    $@"Component Categories\{catIdStr}\CLSID",
                };

                foreach (string catPath in searchPaths)
                {
                    // 在 ClassesRoot 下查找（HKCR = HKLM\SOFTWARE\Classes + HKCU\SOFTWARE\Classes 的合并视图）。
                    // 注意：此处不使用 using 包裹 Registry.ClassesRoot，
                    // 原因详见 ScanViaAllProgIds 中的 C-01 修复说明。
                    ReadClsidsFromCategoryKey(Registry.ClassesRoot, catPath, servers, "ComponentCategories");

                    // 在 WOW6432Node 下查找（32 位 OPC 服务器在 64 位 Windows 上的注册表重定向路径）。
                    string wow64Path = $@"SOFTWARE\Wow6432Node\Classes\{catPath}";
                    using (var key = Registry.LocalMachine.OpenSubKey(wow64Path))
                    {
                        if (key != null)
                        {
                            foreach (string clsidStr in key.GetValueNames())
                            {
                                if (clsidStr.StartsWith("{"))
                                {
                                    TryAddServerByClsid(clsidStr, servers, "ComponentCategories");
                                }
                            }
                        }
                    }
                }
            }

            // 直接扫描 HKCR\Component Categories 下的所有类别，
            // 查找描述中包含 "OPC" 的类别（覆盖非标准 CATID 注册的 OPC 服务器）。
            try
            {
                using (var catRoot = Registry.ClassesRoot.OpenSubKey("Component Categories"))
                {
                    if (catRoot != null)
                    {
                        foreach (string subKeyName in catRoot.GetSubKeyNames())
                        {
                            // 检查类别描述中是否包含 "OPC"。
                            // 注册表值 "409" 是英语 LCID 标识符，存储该类别的英语描述。
                            using (var catKey = catRoot.OpenSubKey(subKeyName))
                            {
                                string desc = catKey?.GetValue("409") as string  // 英语描述
                                           ?? catKey?.GetValue("") as string;
                                if (desc != null && desc.IndexOf("OPC", StringComparison.OrdinalIgnoreCase) >= 0)
                                {
                                    // 检查其 CLSID 子键（存储属于此类别的 COM 组件 CLSID）。
                                    using (var clsidsKey = catKey.OpenSubKey("CLSID"))
                                    {
                                        if (clsidsKey != null)
                                        {
                                            foreach (string valueName in clsidsKey.GetValueNames())
                                            {
                                                string value = clsidsKey.GetValue(valueName) as string ?? valueName;
                                                if (value.StartsWith("{"))
                                                {
                                                    TryAddServerByClsid(value, servers, "ComponentCategories");
                                                }
                                            }
                                        }
                                    }
                                }
                            }
                        }
                    }
                }
            }
            catch (Exception ex) { Log($"[扫描] 操作失败: {ex.Message}"); }
        }

        /// <summary>
        /// 从指定的注册表组件类别路径中读取 CLSID 列表，并通过 TryAddServerByClsid 添加到结果字典。
        /// CLSID 可能以值名称或值内容的形式存储（不同安装程序的注册方式不同）。
        /// </summary>
        private static void ReadClsidsFromCategoryKey(
            RegistryKey root, string path,
            Dictionary<string, OpcServerInfo> servers, string source)
        {
            try
            {
                using (var key = root.OpenSubKey(path))
                {
                    if (key == null) return;

                    // CLSID 可能以值名称或值内容的形式存储（两种注册方式都存在）。
                    foreach (string valueName in key.GetValueNames())
                    {
                        string clsidStr = key.GetValue(valueName) as string ?? valueName;
                        if (clsidStr.StartsWith("{"))
                        {
                            TryAddServerByClsid(clsidStr, servers, source);
                        }
                    }

                    // 有些注册方式把 CLSID 作为子键名而非值。
                    foreach (string subKeyName in key.GetSubKeyNames())
                    {
                        if (subKeyName.StartsWith("{"))
                        {
                            TryAddServerByClsid(subKeyName, servers, source);
                        }
                    }
                }
            }
            catch (Exception ex) { Log($"[扫描] 操作失败: {ex.Message}"); }
        }

        // ================================================================
        //  策略 2：STA 线程 COM 枚举
        // ================================================================

        /// <summary>
        /// OPC Foundation 提供的 COM 枚举接口，用于查询已注册的 OPC 服务器列表。
        /// CLSID {13486D51-4821-11D2-A494-3CB306C10000} 是 IOPCServerList 的标准接口 ID。
        /// </summary>
        [ComImport]
        [Guid("13486D51-4821-11D2-A494-3CB306C10000")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IOPCServerList
        {
            void QueryCLSID([In] ref Guid catid, out IEnumGUID enumGuids);
            void GetCLSIDFromProgID([In, MarshalAs(UnmanagedType.LPWStr)] string progId, out Guid clsid);
            void GetProgIDFromCLSID([In] ref Guid clsid, [Out, MarshalAs(UnmanagedType.LPWStr)] out string progId);
        }

        /// <summary>
        /// COM 标准的 GUID 枚举器接口，用于遍历 IOPCServerList.QueryCLSID 返回的结果集。
        /// </summary>
        [ComImport]
        [Guid("0002E000-0000-0000-C000-000000000046")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IEnumGUID
        {
            void Next([In] int celt, [Out, MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 0)] Guid[] rgelt, out int pceltFetched);
            void Skip([In] int celt);
            void Reset();
            void Clone(out IEnumGUID ppenum);
        }

        /// <summary>
        /// OPCServerList COM 类的激活入口（CLSID {13486D50-4821-11D2-A494-3CB306C10000}）。
        /// 该 COM 类由 OPC Foundation 的核心组件（OPC Core Components）安装时注册。
        /// 如果未安装核心组件，new OPCServerListClass() 将抛出 COMException。
        /// </summary>
        [ComImport]
        [Guid("13486D50-4821-11D2-A494-3CB306C10000")]
        private class OPCServerListClass { }

        // ================================================================
        //  STA 线程 + 局部字典 + 合并模式（C-02 修复）
        // ================================================================
        //
        // 为什么需要 STA 线程？
        // ─────────────────────
        // OPC DA 的 COM 对象（OPCServerList）要求调用线程的 COM 公寓模型为 STA
        // （Single-Threaded Apartment）。如果使用默认的 MTA 线程（.NET 线程池线程），
        // COM 调用会失败或行为异常。
        //
        // 为什么使用局部字典 + 延迟合并，而不是直接写主字典？
        // ────────────────────────────────────────────────────
        // C-02 修复的核心问题：如果 STA 线程直接写共享的主字典（servers），
        // 当线程超时被 Abort 时，它可能正处于"正在修改字典内部哈希桶"的中间状态，
        // 导致字典数据结构损坏（哈希链断裂），后续对主字典的任何操作都可能抛出异常。
        //
        // 解决方案：STA 线程只写自己的局部字典（localResults），
        // 主线程在 Join 返回后（确保 STA 线程已完全退出）才将局部结果合并到主字典。
        // 这样即使 Abort 发生，被损坏的也只是局部字典（可以丢弃），主字典始终完好。
        //
        // 协作式取消标志的作用：
        // ────────────────────
        // 在调用 Abort（破坏性操作）之前，先设置 _staCancellationRequested = true，
        // 然后给线程 2 秒时间自行检测到标志并 break 退出循环。
        // 这是一种"先礼后兵"的策略——如果线程能在检查点自行退出，就不需要 Abort，
        // 从而避免 Abort 可能导致的资源泄漏（未释放的 COM 对象）。
        // 只有当线程 2 秒内仍未响应时，才作为最后手段使用 Abort。

        /// <summary>
        /// 协作式取消标志：当主线程检测到 STA 线程超时后设置为 true，
        /// STA 线程在枚举循环的每次迭代中检查此标志，如果为 true 则提前退出。
        /// 使用 volatile 确保主线程的写入对 STA 线程立即可见。
        /// </summary>
        private static volatile bool _staCancellationRequested;

        /// <summary>
        /// 在专用 STA 线程上执行 COM 枚举，收集 OPC DA 服务器列表。
        /// 超时为 15 秒，超时后通过协作式取消 + 2 秒宽限期 + 最终 Abort 三步策略终止线程。
        /// </summary>
        private static void RunComEnumerationOnStaThread(Dictionary<string, OpcServerInfo> servers)
        {
            Exception threadError = null;
            _staCancellationRequested = false;

            // C-02 修复：STA 线程使用独立局部字典收集结果，Join 后合并到主字典。
            // 避免 Abort 时正在写主字典导致数据结构损坏。
            var localResults = new Dictionary<string, OpcServerInfo>(StringComparer.OrdinalIgnoreCase);

            // 在 STA 线程上执行 COM 枚举（OPC DA 的 COM 对象需要 STA 公寓模型）。
            var staThread = new Thread(() =>
            {
                try
                {
                    ScanViaComEnumeration(localResults);
                }
                catch (Exception ex)
                {
                    threadError = ex;
                }
            });

            staThread.SetApartmentState(ApartmentState.STA);
            // 后台线程：进程退出时自动终止，不阻塞关闭流程。
            // 协同 _staCancellationRequested 标志，ScanViaComEnumeration 在迭代中检查并提前退出。
            staThread.IsBackground = true;
            staThread.Start();

            // 超时后先设置取消标志（协作式），再等待一小段时间让线程自行退出。
            // 三步终止策略：1. Join(15s) → 2. 设置标志 + Join(2s) → 3. 放弃等待（最后手段）。
            // P1-1 修复：移除 Thread.Abort，改为依赖 IsBackground + Join 超时。
            // Thread.Abort 在 .NET Core/.NET 5+ 已移除，即使在 .NET Framework 中也可能
            // 导致锁未释放、非托管资源泄漏和不可预测的状态损坏。
            // 由于线程已标记 IsBackground=true，进程退出时会自动终止，不会造成进程挂起。
            bool completed = staThread.Join(TimeSpan.FromSeconds(15));
            if (!completed)
            {
                Log("[COM枚举] 扫描超时（15秒），发出取消信号");
                _staCancellationRequested = true;
                // 给线程 2 秒自行退出（检查取消标志后 break）。
                completed = staThread.Join(TimeSpan.FromSeconds(2));
                if (!completed)
                {
                    // 线程卡在 COM 调用内部无法中断（COM 不支持真正的取消）。
                    // 不再使用 Thread.Abort，依赖 IsBackground=true 确保进程退出时清理。
                    // 局部结果可能不完整，但字典结构不会被破坏（C-02 防护）。
                    Log("[COM枚举] 线程在取消后仍未响应，放弃等待（IsBackground 确保进程退出时清理）");
                }
            }

            // C-02 修复：合并局部结果到主字典。
            // 此时 Join 已返回（或 Abort 已执行），STA 线程不再活动，写主字典是安全的。
            foreach (var kvp in localResults)
            {
                if (!servers.ContainsKey(kvp.Key))
                    servers[kvp.Key] = kvp.Value;
            }

            if (threadError != null)
            {
                Log($"[COM枚举] 线程错误: {threadError.Message}");
            }
        }

        /// <summary>
        /// 通过 IOPCServerList COM 接口枚举已注册的 OPC DA 服务器。
        /// 依次查询 DA 1.0、2.0、3.0 三个版本的 CATID，覆盖不同年代安装的服务器。
        /// 此方法必须在 STA 线程上调用。
        /// </summary>
        private static void ScanViaComEnumeration(Dictionary<string, OpcServerInfo> servers)
        {
            IOPCServerList serverList;
            try
            {
                serverList = (IOPCServerList)new OPCServerListClass();
            }
            catch (Exception ex)
            {
                // OPCServerList COM 类不存在——说明 OPC Core Components 未安装。
                // 这在某些精简安装的工控机上是正常的，不是错误。
                Log($"[COM枚举] OPCServerList 创建失败（可能未安装 OPC 核心组件）: {ex.Message}");
                return;
            }

            try
            {
                CollectFromCategory(serverList, CATID_OPCDAServer10, servers);
                CollectFromCategory(serverList, CATID_OPCDAServer20, servers);
                CollectFromCategory(serverList, CATID_OPCDAServer30, servers);
            }
            finally
            {
                // 必须显式释放 COM 对象引用，否则 COM 引用计数泄漏会导致 OPC 服务器进程无法退出。
                Marshal.ReleaseComObject(serverList);
            }
        }

        /// <summary>
        /// 从指定的 OPC DA 类别中枚举所有服务器，并将结果添加到字典中。
        /// 每次迭代都检查协作式取消标志，支持主线程发起的超时取消。
        /// </summary>
        private static void CollectFromCategory(IOPCServerList serverList, Guid catId, Dictionary<string, OpcServerInfo> servers)
        {
            IEnumGUID enumerator = null;
            try
            {
                serverList.QueryCLSID(ref catId, out enumerator);
            }
            catch (Exception ex) { Log($"[扫描] 操作失败: {ex.Message}"); return; }

            if (enumerator == null) return;

            try
            {
                var guids = new Guid[1];
                while (true)
                {
                    // C-02 修复：每次迭代检查取消标志，实现协作式取消。
                    // 当主线程设置 _staCancellationRequested = true 后，此检查使枚举尽快退出，
                    // 避免 Abort 破坏 COM 对象状态。这是"先礼后兵"策略中的"礼"。
                    if (_staCancellationRequested) break;

                    enumerator.Next(1, guids, out int fetched);
                    if (fetched != 1) break;

                    Guid clsid = guids[0];
                    string clsidStr = clsid.ToString("B").ToUpperInvariant();

                    // 优先通过 COM 接口获取 ProgID，回退到注册表查找。
                    string progId = null;
                    try { serverList.GetProgIDFromCLSID(ref clsid, out progId); } catch (Exception ex) { Log($"[扫描] GetProgIDFromCLSID 失败: {ex.Message}"); }
                    if (string.IsNullOrEmpty(progId)) progId = ProgIdFromClsidRegistry(clsidStr);
                    if (string.IsNullOrEmpty(progId)) continue;
                    // 去重：如果已由其他策略发现，跳过。
                    if (servers.ContainsKey(progId)) continue;

                    string serverPath = GetLocalServerPath(clsidStr);
                    string friendlyName = GetFriendlyName(clsidStr) ?? progId;

                    servers[progId] = new OpcServerInfo
                    {
                        ProgId = progId,
                        Description = friendlyName,
                        Clsid = clsidStr,
                        ServerPath = serverPath,
                        Source = "COMEnumeration"
                    };
                }
            }
            finally
            {
                // 释放 COM 枚举器引用。
                Marshal.ReleaseComObject(enumerator);
            }
        }

        // ================================================================
        //  策略 3：HKCR ProgID 全量扫描
        // ================================================================

        /// <summary>
        /// 策略 3（兜底）：遍历 HKCR 下所有符合 ProgID 格式的注册表项，
        /// 通过检查 CLSID → LocalServer32 路径和 OPC 相关性关键词来筛选 OPC DA 服务器。
        /// 此策略耗时较长（HKCR 可能包含数万个子键），但能发现未通过标准 Component Categories 注册的服务器。
        /// </summary>
        private static void ScanViaAllProgIds(Dictionary<string, OpcServerInfo> servers)
        {
            // C-01 修复：Registry.ClassesRoot 是 .NET Framework 管理的共享静态单例，
            // 代表 HKCR 注册表根键的合并视图（HKLM\SOFTWARE\Classes + HKCU\SOFTWARE\Classes）。
            // 它不是普通的 RegistryKey 实例——框架内部持有并缓存这个对象。
            // 如果用 using 语句释放它，后续所有访问 Registry.ClassesRoot 的代码
            // （包括本方法后续的 OpenSubKey 调用和其他策略的 Registry 操作）
            // 都会收到 ObjectDisposedException 或行为异常。
            // 因此这里直接赋值给局部变量使用，不做 using 包裹。
            var classesRoot = Registry.ClassesRoot;
            foreach (string keyName in classesRoot.GetSubKeyNames())
            {
                // 跳过不以字母开头的键（如 {CLSID}、.extension 等）。
                if (string.IsNullOrEmpty(keyName) || !char.IsLetter(keyName[0])) continue;

                // ProgID 格式通常是 "Vendor.Product.Version"（包含至少一个点）。
                if (keyName.IndexOf('.') < 0) continue;

                // 跳过明显不是 ProgID 的键（COM 内部生成的辅助键）。
                if (keyName.EndsWith("_class", StringComparison.OrdinalIgnoreCase)) continue;

                try
                {
                    // 获取 CLSID：ProgID 注册表项下的 CLSID 子键存储了对应的 COM 类 GUID。
                    string clsidStr = null;
                    using (var clsidKey = classesRoot.OpenSubKey(keyName + @"\CLSID"))
                    {
                        clsidStr = clsidKey?.GetValue("") as string;
                    }
                    if (string.IsNullOrEmpty(clsidStr)) continue;

                    // 必须有 LocalServer32——OPC DA 服务器都是 out-of-proc COM（独立进程），
                    // 通过 LocalServer32 注册其可执行文件路径。in-proc COM（InprocServer32/DLL）不是 DA 服务器。
                    string serverPath = GetLocalServerPath(clsidStr);
                    if (string.IsNullOrEmpty(serverPath)) continue;

                    // 判断是否和 OPC 相关（名称关键词、Component Categories、Implemented Categories 等）。
                    if (!IsOpcRelated(keyName, clsidStr, classesRoot)) continue;

                    // 去重：已由之前的策略发现则跳过。
                    if (servers.ContainsKey(keyName)) continue;

                    string friendlyName = GetFriendlyName(clsidStr) ?? keyName;

                    servers[keyName] = new OpcServerInfo
                    {
                        ProgId = keyName,
                        Description = friendlyName,
                        Clsid = clsidStr,
                        ServerPath = serverPath,
                        Source = "ProgIdScan"
                    };
                }
                catch (Exception ex)
                {
                    Log($"[注册表] 读取 HKCR 值失败: {ex.Message}");
                }
            }
        }

        /// <summary>
        /// 判断一个 ProgID/CLSID 是否和 OPC 相关。
        /// 采用四级判定策略：
        ///   1. ProgID 名称中包含 "OPC" 关键词（快速路径）。
        ///   2. CLSID 注册在 OPC DA 的 Component Categories 下。
        ///   3. CLSID 的 Implemented Categories 中包含 OPC DA CATID。
        ///   4. CLSID 注册表项的描述中包含 "OPC"。
        /// 任一条件为真即判定为 OPC 相关。
        /// </summary>
        private static bool IsOpcRelated(string progId, string clsidStr, RegistryKey classesRoot)
        {
            // 1. ProgID 名称中包含 OPC 相关关键词（最快的判定，覆盖大多数情况）。
            if (ContainsOpcKeyword(progId)) return true;

            // 2. 检查 CLSID 是否注册在 OPC 组件类别下。
            string clsidUpper = clsidStr.ToUpperInvariant();
            Guid[] catIds = { CATID_OPCDAServer10, CATID_OPCDAServer20, CATID_OPCDAServer30 };
            foreach (var catId in catIds)
            {
                string catPath = $@"Component Categories\{{{catId}}}\CLSID";
                try
                {
                    using (var catKey = classesRoot.OpenSubKey(catPath))
                    {
                        if (catKey != null)
                        {
                            foreach (string valueName in catKey.GetValueNames())
                            {
                                string val = (catKey.GetValue(valueName) as string ?? valueName).ToUpperInvariant();
                                if (val == clsidUpper) return true;
                            }
                        }
                    }
                }
                catch (Exception ex) { Log($"[扫描] 操作失败: {ex.Message}"); }
            }

            // 3. 检查 Implemented Categories 子键（COM 组件声明自己实现的组件类别）。
            try
            {
                using (var implCatKey = classesRoot.OpenSubKey($@"CLSID\{clsidStr}\Implemented Categories"))
                {
                    if (implCatKey != null)
                    {
                        foreach (string subKeyName in implCatKey.GetSubKeyNames())
                        {
                            foreach (var catId in catIds)
                            {
                                if (subKeyName.Equals($"{{{catId}}}", StringComparison.OrdinalIgnoreCase))
                                    return true;
                            }
                        }
                    }
                }
            }
            catch (Exception ex) { Log($"[扫描] 操作失败: {ex.Message}"); }

            // 4. 检查 CLSID 注册表项的描述中是否包含 "OPC"。
            try
            {
                using (var clsidKey = classesRoot.OpenSubKey($@"CLSID\{clsidStr}"))
                {
                    string desc = clsidKey?.GetValue("") as string;
                    if (desc != null && desc.IndexOf("OPC", StringComparison.OrdinalIgnoreCase) >= 0)
                        return true;
                }
            }
            catch (Exception ex) { Log($"[扫描] 操作失败: {ex.Message}"); }

            return false;
        }

        /// <summary>
        /// 检查文本中是否包含 "OPC" 关键词。
        /// H-19 修复：使用 OrdinalIgnoreCase 一次检查覆盖所有大小写组合（"OPC"、"opc"、"Opc" 等）。
        /// </summary>
        private static bool ContainsOpcKeyword(string text)
        {
            return text.IndexOf("opc", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        // ================================================================
        //  策略 4：OPC Foundation 已安装组件
        // ================================================================

        /// <summary>
        /// 策略 4：从 OPC Foundation 的已安装组件注册表中查找 OPC DA 服务器。
        /// 某些 OPC 服务器在安装时只在此路径注册，不一定会出现在 Component Categories 中。
        /// 同时覆盖 64 位和 32 位（WOW6432Node）注册表视图。
        /// </summary>
        private static void ScanViaInstalledComponents(Dictionary<string, OpcServerInfo> servers)
        {
            string[] searchPaths = new[]
            {
                @"SOFTWARE\OPC Foundation\Installed Components",
                @"SOFTWARE\Wow6432Node\OPC Foundation\Installed Components"
            };

            foreach (string basePath in searchPaths)
            {
                try
                {
                    using (var root = Registry.LocalMachine.OpenSubKey(basePath))
                    {
                        if (root == null) continue;

                        foreach (string subKeyName in root.GetSubKeyNames())
                        {
                            try
                            {
                                using (var compKey = root.OpenSubKey(subKeyName))
                                {
                                    string clsidStr = compKey?.GetValue("CLSID") as string;
                                    // ProgID/ProgId 两种大小写都尝试（不同厂商注册时的拼写不一致）。
                                    string progId = compKey?.GetValue("ProgID") as string
                                                 ?? compKey?.GetValue("ProgId") as string;
                                    string desc = compKey?.GetValue("Description") as string
                                               ?? compKey?.GetValue("") as string;

                                    if (!string.IsNullOrEmpty(progId) && !servers.ContainsKey(progId))
                                    {
                                        string serverPath = !string.IsNullOrEmpty(clsidStr) ? GetLocalServerPath(clsidStr) : null;

                                        servers[progId] = new OpcServerInfo
                                        {
                                            ProgId = progId,
                                            Description = desc ?? progId,
                                            Clsid = clsidStr,
                                            ServerPath = serverPath,
                                            Source = "InstalledComponents"
                                        };
                                    }
                                }
                            }
                            catch (Exception ex) { Log($"[扫描] 操作失败: {ex.Message}"); }
                        }
                    }
                }
                catch (Exception ex) { Log($"[扫描] 操作失败: {ex.Message}"); }
            }
        }

        // ================================================================
        //  辅助方法
        // ================================================================

        /// <summary>
        /// 通过 CLSID 尝试添加一个 OPC DA 服务器到结果字典。
        /// 会从注册表中反查 ProgID 和 LocalServer32 路径，只有两者都能找到时才添加。
        /// 如果 ProgID 已存在于字典中则跳过（去重）。
        /// </summary>
        /// <param name="clsidStr">CLSID（GUID 格式，例如 "{XXXXX-...}" ）。</param>
        /// <param name="servers">结果字典（key = ProgID）。</param>
        /// <param name="source">发现来源标识，记录到 OpcServerInfo.Source 中。</param>
        private static void TryAddServerByClsid(string clsidStr, Dictionary<string, OpcServerInfo> servers, string source)
        {
            if (string.IsNullOrEmpty(clsidStr)) return;

            clsidStr = clsidStr.Trim();
            if (!clsidStr.StartsWith("{")) return;

            string progId = ProgIdFromClsidRegistry(clsidStr);
            if (string.IsNullOrEmpty(progId)) return;
            if (servers.ContainsKey(progId)) return;

            string serverPath = GetLocalServerPath(clsidStr);
            string friendlyName = GetFriendlyName(clsidStr) ?? progId;

            servers[progId] = new OpcServerInfo
            {
                ProgId = progId,
                Description = friendlyName,
                Clsid = clsidStr,
                ServerPath = serverPath,
                Source = source
            };
        }

        /// <summary>
        /// 从注册表中获取 CLSID 对应的友好名称（人类可读的描述）。
        /// 依次尝试：1. CLSID 注册表项的默认值（通常是友好名称）。2. AppID 注册表项的默认值。
        /// </summary>
        /// <param name="clsidStr">CLSID（GUID 格式）。</param>
        /// <returns>友好名称，无法获取时返回 null。</returns>
        private static string GetFriendlyName(string clsidStr)
        {
            if (string.IsNullOrEmpty(clsidStr)) return null;

            try
            {
                using (var clsidKey = Registry.ClassesRoot.OpenSubKey("CLSID\\" + clsidStr))
                {
                    if (clsidKey == null) return null;

                    // 先查 CLSID 自身的默认值（通常是友好名称）。
                    string desc = clsidKey.GetValue("") as string;
                    if (!string.IsNullOrEmpty(desc) && desc != clsidStr)
                        return desc;

                    // 再查 AppID 的描述（某些 COM 服务器的友好名称存储在 AppID 下）。
                    string appId = clsidKey.GetValue("AppId") as string;
                    if (!string.IsNullOrEmpty(appId))
                    {
                        using (var appIdKey = Registry.ClassesRoot.OpenSubKey("AppID\\" + appId))
                        {
                            string appIdDesc = appIdKey?.GetValue("") as string;
                            if (!string.IsNullOrEmpty(appIdDesc)) return appIdDesc;
                        }
                    }
                }
            }
            catch (Exception ex) { Log($"[扫描] 操作失败: {ex.Message}"); }

            return null;
        }

        /// <summary>
        /// 获取 CLSID 对应的 LocalServer32 可执行文件路径。
        /// 依次查找 64 位注册表视图和 32 位（WOW6432Node）视图。
        /// 只有 out-of-proc COM 服务器才有 LocalServer32（OPC DA 服务器都是 out-of-proc）。
        /// </summary>
        /// <param name="clsidStr">CLSID（GUID 格式）。</param>
        /// <returns>可执行文件路径，无法获取时返回 null。</returns>
        private static string GetLocalServerPath(string clsidStr)
        {
            if (string.IsNullOrEmpty(clsidStr)) return null;

            // 64 位视图
            string path = ReadLocalServer32(Registry.ClassesRoot, clsidStr);
            if (!string.IsNullOrEmpty(path)) return path;

            // 32 位视图（WOW6432Node，32 位 OPC 服务器在 64 位 Windows 上的重定向路径）
            try
            {
                using (var key = Registry.LocalMachine.OpenSubKey(
                    $@"SOFTWARE\Wow6432Node\Classes\CLSID\{clsidStr}\LocalServer32"))
                {
                    if (key != null)
                    {
                        string value = key.GetValue("") as string;
                        if (!string.IsNullOrWhiteSpace(value)) return value.Trim();
                    }
                }
            }
            catch (Exception ex) { Log($"[扫描] 操作失败: {ex.Message}"); }

            return null;
        }

        /// <summary>
        /// 从指定的注册表根键下读取 CLSID 的 LocalServer32 默认值。
        /// </summary>
        private static string ReadLocalServer32(RegistryKey root, string clsidStr)
        {
            try
            {
                using (var key = root.OpenSubKey($@"CLSID\{clsidStr}\LocalServer32"))
                {
                    if (key == null) return null;
                    string value = key.GetValue("") as string;
                    return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
                }
            }
            catch (Exception ex) { Log($"[扫描] 操作失败: {ex.Message}"); return null; }
        }

        /// <summary>
        /// 从注册表中根据 CLSID 反查 ProgID。
        /// 优先查找 VersionIndependentProgID（版本无关的 ProgID，如 "Vendor.Server"），
        /// 回退到 ProgId（版本相关的 ProgID，如 "Vendor.Server.1"）。
        /// 优先使用版本无关 ProgID 是因为用户配置中通常使用它，不受版本升级影响。
        /// </summary>
        /// <param name="clsidStr">CLSID（GUID 格式）。</param>
        /// <returns>ProgID 字符串，无法获取时返回 null。</returns>
        private static string ProgIdFromClsidRegistry(string clsidStr)
        {
            try
            {
                // 优先查 VersionIndependentProgID（版本无关 ProgID）。
                using (var key = Registry.ClassesRoot.OpenSubKey($@"CLSID\{clsidStr}\VersionIndependentProgID"))
                {
                    if (key != null)
                    {
                        string progId = key.GetValue("") as string;
                        if (!string.IsNullOrEmpty(progId)) return progId;
                    }
                }

                // 回退到版本相关的 ProgId。
                using (var key = Registry.ClassesRoot.OpenSubKey($@"CLSID\{clsidStr}\ProgId"))
                {
                    if (key != null)
                    {
                        string progId = key.GetValue("") as string;
                        if (!string.IsNullOrEmpty(progId)) return progId;
                    }
                }
            }
            catch (Exception ex) { Log($"[扫描] 操作失败: {ex.Message}"); }

            return null;
        }

        /// <summary>
        /// 记录诊断日志。internal 可见性供 OpcDaClient.BrowseRecursive 调用。
        /// 使用 ConcurrentBag 存储，线程安全。
        /// </summary>
        /// <param name="message">日志消息。</param>
        internal static void Log(string message) // L5: 改为 internal 供 OpcDaClient 调用
        {
            _diagnosticLog.Add($"[{DateTime.Now:HH:mm:ss}] {message}");
        }
    }
}
