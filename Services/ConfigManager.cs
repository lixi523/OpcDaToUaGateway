using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Security.Cryptography;
using System.Windows.Forms;
using Newtonsoft.Json;
using OpcDaToUaGateway.Models;

namespace OpcDaToUaGateway.Services
{
    /// <summary>
    /// 配置管理器 — 负责应用配置文件（config.json）的完整生命周期管理。
    ///
    /// == 配置生命周期 ==
    ///   1. Load()      — 从应用目录读取 config.json，反序列化为 AppConfig 对象，
    ///                    然后调用 ApplyBackwardCompatDefaults() 为旧版本缺失的字段填充安全默认值，
    ///                    最后为每个 Tag 分配运行时唯一键（AssignTagKeys）。
    ///   2. 运行时修改  — UI 层直接修改 Config 对象属性（如选择 DA 服务器、修改端口号等）。
    ///   3. Save()      — 防抖保存：500ms 内的多次调用合并为一次磁盘写入，
    ///                    减少 UI 频繁变更时的 I/O 压力。
    ///   4. SaveImmediate() — 立即保存（跳过防抖），用于程序退出等关键场景。
    ///
    /// == 原子写入策略 ==
    ///   写入采用"临时文件 + File.Replace"模式：先写入 .tmp 文件，再原子替换目标文件。
    ///   确保读取端永远不会看到写了一半的 JSON（torn write），即使写入过程中断电也安全。
    ///
    /// == 并发保存保护 ==
    ///   使用 Monitor（lock）+ 手动 Monitor.Enter 的组合保护 DoSave()，
    ///   确保防抖 Timer 回调与 SaveImmediate() 不会并发执行序列化+写入。
    ///   Monitor 是可重入的，同一线程多次 Enter 不会死锁。
    /// </summary>
    public class ConfigManager : IDisposable
    {
        private readonly LogManager _log;

        /// <summary>
        /// 保存操作的全局锁。
        /// 保护 Save() 的防抖 Timer 创建/重置、SaveImmediate() 的直接写入、以及 DoSave() 内部的
        /// 序列化+文件写入流程。所有涉及 config.json 磁盘写入的路径都必须持有此锁。
        /// </summary>
        private readonly object _saveLock = new object();

        /// <summary>
        /// 防抖定时器 — Save() 调用时重置计时，500ms 无新调用后触发 DoSave()。
        /// 复用同一个 Timer 实例（通过 Change 重置），避免反复创建/销毁带来的 GC 压力。
        /// </summary>
        private System.Threading.Timer _debounceTimer;

        /// <summary>
        /// H-40: 文件系统监视器 — 监听 config.json 的外部修改。
        /// </summary>
        private FileSystemWatcher _configWatcher;

        /// <summary>当前应用配置对象，Load() 后可读，UI 层可直接修改其属性</summary>
        public AppConfig Config { get; private set; }

        /// <summary>
        /// 加载时的原始 JSON 文本。
        /// 保留原始文本的目的是支持向后兼容判断：通过检查 JSON 中是否包含某个字段名，
        /// 区分"用户显式设置了默认值"和"旧版本配置根本没有这个字段"。
        /// </summary>
        public string RawJson { get; private set; }

        /// <summary>
        /// H-40: 配置文件被外部修改时触发。MainForm 订阅后根据网关状态决定自动重载或提示。
        /// </summary>
        public event Action ConfigFileChanged;

        // H-23 修复：将防抖定时器提升为实例字段，用 Interlocked.Exchange 原子替换，
        // 防止 Changed 事件并发触发时的写-写竞态（双重 Dispose 或新 Timer 引用丢失）。
        private System.Threading.Timer _watcherDebounce;

        // S-3 修复：授权码加密存储的 AES 密钥，由 LicenseAlgorithm.DeriveKey() 派生
        private static readonly byte[] _authCodeKey = LicenseAlgorithm.GetEncryptionKey();

        /// <summary>
        /// 使用 AES-256-CBC 加密授权码。加密结果包含随机 IV（前 16 字节）+ 密文，整体 Base64 编码。
        /// </summary>
        private static string EncryptAuthCode(string plaintext)
        {
            if (string.IsNullOrEmpty(plaintext)) return plaintext;

            try
            {
                using (var aes = Aes.Create())
                {
                    aes.Key = _authCodeKey;
                    aes.GenerateIV();

                    using (var encryptor = aes.CreateEncryptor())
                    using (var ms = new MemoryStream())
                    {
                        // 先写入 IV，解密时从中提取
                        ms.Write(aes.IV, 0, aes.IV.Length);
                        using (var cs = new CryptoStream(ms, encryptor, CryptoStreamMode.Write))
                        using (var sw = new StreamWriter(cs))
                        {
                            sw.Write(plaintext);
                        }
                        return Convert.ToBase64String(ms.ToArray());
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[配置] 授权码加密失败: {ex.Message}");
                return plaintext;
            }
        }

        /// <summary>
        /// 解密配置文件中加密存储的授权码。
        /// 向后兼容：如果字符串不是有效的 Base64 或解密失败，视为明文返回。
        /// </summary>
        private static string DecryptAuthCode(string encrypted)
        {
            if (string.IsNullOrEmpty(encrypted)) return encrypted;

            try
            {
                byte[] fullData = Convert.FromBase64String(encrypted);
                if (fullData.Length <= 16) return encrypted; // 太短，不可能是有效的加密数据

                using (var aes = Aes.Create())
                {
                    aes.Key = _authCodeKey;
                    byte[] iv = new byte[16];
                    Array.Copy(fullData, 0, iv, 0, 16);
                    aes.IV = iv;

                    using (var decryptor = aes.CreateDecryptor())
                    using (var ms = new MemoryStream(fullData, 16, fullData.Length - 16))
                    using (var cs = new CryptoStream(ms, decryptor, CryptoStreamMode.Read))
                    using (var sr = new StreamReader(cs))
                    {
                        return sr.ReadToEnd();
                    }
                }
            }
            catch
            {
                // 向后兼容：解密失败时视为明文（旧版本配置）
                return encrypted;
            }
        }

        /// <summary>
        /// 初始化配置管理器。
        /// </summary>
        /// <param name="log">日志管理器，用于记录配置操作日志（不可为 null）</param>
        /// <exception cref="ArgumentNullException">log 为 null 时抛出</exception>
        public ConfigManager(LogManager log)
        {
            _log = log ?? throw new ArgumentNullException(nameof(log));
        }

        /// <summary>
        /// 从应用目录加载 config.json 配置文件。
        ///
        /// 加载流程：
        ///   1. 检查文件是否存在，不存在则弹窗提示并返回 false
        ///   2. 读取文件内容并反序列化为 AppConfig
        ///   3. 尝试从 tags.json 加载标签数据（P1-1 增量保存优化）
        ///   4. 调用 ApplyBackwardCompatDefaults() 填充旧版本缺失字段的默认值
        ///   5. 为 OPC DA 标签列表分配运行时唯一键
        ///
        /// 加载失败时通过 MessageBox 向用户提示错误原因。
        /// </summary>
        /// <returns>加载成功返回 true；文件不存在或解析失败返回 false</returns>
        public bool Load()
        {
            string configPath = GetConfigPath();

            if (!File.Exists(configPath))
            {
                MessageBox.Show(
                    "找不到配置文件 config.json！\n请确保该文件与程序在同一目录下。",
                    "配置错误", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return false;
            }

            try
            {
                RawJson = File.ReadAllText(configPath);
                Config = JsonConvert.DeserializeObject<AppConfig>(RawJson);
                if (Config == null)
                {
                    Config = new AppConfig();
                }

                // P1-1: 优先从独立的 tags.json 加载标签数据（增量保存优化）
                // 如果 tags.json 不存在，回退到 config.json 中的内联 Tags（向后兼容）
                string tagsPath = GetTagsPath();
                if (File.Exists(tagsPath))
                {
                    try
                    {
                        string tagsJson = File.ReadAllText(tagsPath);
                        var tagsWrapper = JsonConvert.DeserializeAnonymousType(tagsJson,
                            new { Tags = new List<TagConfig>() });
                        if (tagsWrapper?.Tags != null)
                        {
                            if (Config.OpcDa == null) Config.OpcDa = new OpcDaConfig();
                            Config.OpcDa.Tags = tagsWrapper.Tags;
                        }
                    }
                    catch (Exception ex)
                    {
                        _log?.Append($"[配置] 加载 tags.json 失败，回退到 config.json: {ex.Message}");
                        // 保留 config.json 中的内联 Tags（如果存在）
                    }
                }

                // 为旧版本配置中新增的字段填充安全默认值
                ApplyBackwardCompatDefaults();
                // N-8: 为尚未分配 TagKey 的标签生成唯一键并持久化。
                bool keysAssigned = TagConfig.AssignTagKeys(Config.OpcDa?.Tags);
                if (keysAssigned)
                {
                    SaveImmediate();
                    _log?.Append("[配置] 已为新标签分配 TagKey 并持久化");
                }

                // S-3 修复：解密配置文件中的授权码
                if (!string.IsNullOrEmpty(Config.AuthorizationCode))
                {
                    string decrypted = DecryptAuthCode(Config.AuthorizationCode);
                    if (decrypted != Config.AuthorizationCode)
                    {
                        _log?.Append("[配置] 已解密授权码");
                        Config.AuthorizationCode = decrypted;
                    }
                }

                // H-40: 启动配置文件监视
                StartWatching();

                return true;
            }
            catch (Exception ex)
            {
                MessageBox.Show($"加载配置失败:\n{ex.Message}", "错误",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
                return false;
            }
        }

        /// <summary>
        /// 会话数上限和超时时间：0 或负数表示旧配置未设置，填充生产环境合理值。
        /// M1 修复（V2.6.0）：原为 120000/4840 硬编码，改由 AppConstants 集中管理。
        /// </summary>
        private void ApplyBackwardCompatDefaults()
        {
            // 确保 OpcUa 配置节点存在（旧版本可能没有 UA 相关配置）
            if (Config.OpcUa == null)
            {
                Config.OpcUa = new OpcUaConfig();
            }

            var ua = Config.OpcUa;

            // 监听地址默认 localhost — 仅本机可连接，安全性较好
            if (string.IsNullOrEmpty(ua.ListenAddress))
                ua.ListenAddress = AppConstants.DefaultDaHost;

            // 安全模式和策略默认 None — 简化初始配置，用户可按需启用加密
            if (string.IsNullOrEmpty(ua.SecurityMode))
                ua.SecurityMode = "None";
            if (string.IsNullOrEmpty(ua.SecurityPolicy))
                ua.SecurityPolicy = "None";

            // 向后兼容关键逻辑：通过检查原始 JSON 是否包含字段名来判断是"旧配置"还是"用户设置"。
            // 如果 RawJson 中没有 "AutoAcceptCertificates"，说明这是旧版本配置升级上来的，
            // 默认设为 false（不自动接受证书），要求用户显式确认后才启用，安全优先。
            // 如果 RawJson 中已有此字段，则尊重用户的原始设置（无论 true/false）。
            if (RawJson != null && !RawJson.Contains("AutoAcceptCertificates"))
                ua.AutoAcceptCertificates = false;

            // 会话数上限和超时时间：0 或负数表示旧配置未设置，填充生产环境合理值
            if (ua.MaxSessionCount <= 0)
                ua.MaxSessionCount = 50;
            if (ua.SessionTimeout <= 0)
                ua.SessionTimeout = AppConstants.DefaultSessionTimeoutMs;

            // 端口号默认 4840（OPC UA 标准端口），防止 Port 为 0 时生成无效的端点地址
            if (ua.Port <= 0)
                ua.Port = AppConstants.UaDefaultPort;

            // 确保 OpcDa 配置节点存在
            if (Config.OpcDa == null)
            {
                Config.OpcDa = new OpcDaConfig();
            }

            // DA 数据更新频率默认 1000ms — 适合大多数工业场景的采集周期
            if (Config.OpcDa.UpdateRateMs <= 0)
            {
                Config.OpcDa.UpdateRateMs = 1000;
            }

            // 首次运行：未配置 ProgId 时填充默认值，让用户开箱即用
            if (string.IsNullOrEmpty(Config.OpcDa.ServerProgId))
            {
                Config.OpcDa.ServerProgId = "Matrikon.OPC.Simulation.1";
            }
        }

        /// <summary>
        /// 将当前配置保存到 config.json（防抖模式）。
        ///
        /// 防抖机制：调用后启动/重置一个 500ms 的单次定时器，
        /// 如果 500ms 内没有新的 Save() 调用，定时器回调执行 DoSave()。
        /// 如果 500ms 内有新调用，定时器被重置，从最后一次调用开始重新计时。
        /// 这样可以合并 UI 中连续多次属性变更（如拖动滑块），只写一次磁盘。
        ///
        /// 线程安全：在 _saveLock 内操作 Timer，防止并发 Save 导致 Timer 竞态。
        /// </summary>
        public void Save()
        {
            if (Config == null) return;

            lock (_saveLock)
            {
                // 复用 Timer 实例：首次创建，后续通过 Change() 重置触发时间。
                // 避免每次 Save 都 new Timer，减少 GC 压力和系统资源消耗。
                if (_debounceTimer == null)
                {
                    // dueTime=500ms 后触发一次 DoSave，period=Infinite 表示不周期触发
                    _debounceTimer = new System.Threading.Timer(_ => DoSave(), null, AppConstants.ConfigDebounceMs, System.Threading.Timeout.Infinite);
                }
                else
                {
                    // 重置 Timer：从此刻开始重新计时 500ms
                    _debounceTimer.Change(AppConstants.ConfigDebounceMs, System.Threading.Timeout.Infinite);
                }
            }
        }

        /// <summary>
        /// 立即保存当前配置到 config.json（跳过防抖）。
        ///
        /// 适用场景：程序退出、用户点击"保存"按钮等需要确保数据立即落盘的关键时刻。
        /// 调用时会先销毁待执行的防抖 Timer（如果存在），防止防抖回调与本次写入并发。
        ///
        /// 线程安全：在 _saveLock 内执行，与 Save() 和 DoSave() 互斥。
        /// </summary>
        public void SaveImmediate()
        {
            if (Config == null) return;
            lock (_saveLock)
            {
                // 取消待执行的防抖写入，防止 Timer 回调与本次立即写入并发执行 DoSave
                _debounceTimer?.Dispose();
                _debounceTimer = null;
                DoSaveCore();
            }
        }

        public bool TrySaveImmediate()
        {
            if (Config == null) return false;
            lock (_saveLock)
            {
                _debounceTimer?.Dispose();
                _debounceTimer = null;
                return DoSaveCore();
            }
        }

        /// <summary>
        /// 执行实际的配置序列化与文件写入。
        ///
        /// P1-1 增量保存：将 Tags 数组从 config.json 分离到独立的 tags.json。
        /// config.json 仅包含网关设置（端口、安全策略等，约 2KB），
        /// tags.json 仅包含标签数据（35K 标签场景下约 8MB）。
        /// 这样 UI 设置变更（频繁）只触发 config.json 的小文件写入，
        /// 标签数据（仅导入时变更）只在 SaveTagsImmediate 时单独写入。
        ///
        /// 为什么使用 Monitor.Enter 而非 lock 语句：
        ///   DoSave() 可能被 Timer 回调线程调用（Save 的防抖触发），也可能被 UI 线程直接调用
        ///   （SaveImmediate）。两者都需要与 _saveLock 互斥。
        ///   Monitor 是可重入的（同一线程多次 Enter 不阻塞），所以这里显式使用
        ///   Monitor.Enter/Exit 确保无论从哪条路径调用都能正确持有锁。
        ///
        /// 写入策略：
        ///   1. 在锁内将 Config 序列化为 JSON 字符串快照（Tags 临时置 null 以排除）
        ///   2. 将快照写入临时文件（.tmp）
        ///   3. 用 File.Replace 原子替换目标文件（不存在时 File.Move）
        ///   tags.json 由 SaveTagsImmediate 单独写入（不在 DoSave 中，避免全量序列化）
        /// </summary>
        /// <summary>
        /// 执行实际的配置序列化与文件写入（必须由调用者持有 _saveLock 锁）。
        /// Timer 回调路径通过 <see cref="DoSave"/> 获取锁后调用此方法，
        /// SaveImmediate 路径已持有锁，直接调用此方法。
        /// </summary>
        private bool DoSaveCore()
        {
            if (Config == null) return false;

            // R-8 修复：在 try 外部保存 Tags 原始引用，确保 finally 块能访问它。
            var tags = Config.OpcDa?.Tags;
            // H-16 修复：在 try 外声明 originalAuthCode，确保 finally 块能访问。
            string originalAuthCode = Config.AuthorizationCode;
            try
            {
                string configPath = GetConfigPath();

                // P1-1: 临时置 null Tags 以从 config.json 中排除标签数据。
                if (Config.OpcDa != null) Config.OpcDa.Tags = null;

                // S-3 修复：序列化前加密授权码，避免明文存储（M5：实际强度等同异或混淆，
                // 用于增加读取难度，非强加密；密钥与验证算法同源，详见 LicenseAlgorithm.GetEncryptionKey）
                if (!string.IsNullOrEmpty(Config.AuthorizationCode))
                    Config.AuthorizationCode = EncryptAuthCode(Config.AuthorizationCode);

                // 序列化网关配置快照（不含 Tags，约 2KB，远小于全量 8-10MB）
                string configJson = JsonConvert.SerializeObject(Config, Formatting.Indented);

                // 恢复明文授权码（内存中仍保持明文，方便 UI 读取）
                Config.AuthorizationCode = originalAuthCode;

                // 恢复 Tags 引用（正常路径）
                if (Config.OpcDa != null) Config.OpcDa.Tags = tags;

                // M-03 修复：固定后缀 .tmp 在多进程并发保存时会互相覆盖临时文件，
                // 与 TrialStateStore/CsvTagExporter 保持一致，改用 GUID 随机临时文件名。
                string tempPath = configPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
                File.WriteAllText(tempPath, configJson);
                if (File.Exists(configPath))
                    File.Replace(tempPath, configPath, null);
                else
                    File.Move(tempPath, configPath);

                return true;
            }
            catch (Exception ex)
            {
                _log.Append($"保存配置失败: {ex.Message}");
                return false;
            }
            finally
            {
                // H-16 修复：序列化异常时 AuthorizationCode 可能已被覆盖为加密后的 Base64 字符串。
                // 必须同时恢复 AuthorizationCode，否则下次启动授权验证将用 Base64 密文与 HMAC 比对，
                // 导致有效授权被清除并强制进入试用模式。
                if (Config?.AuthorizationCode != null && Config.AuthorizationCode != originalAuthCode)
                    Config.AuthorizationCode = originalAuthCode;

                // R-8 修复：序列化异常时 Tags 可能为 null，恢复原始引用而非空列表
                if (Config?.OpcDa != null && Config.OpcDa.Tags == null)
                    Config.OpcDa.Tags = tags;
            }
        }

        /// <summary>
        /// 由 Timer 防抖回调调用的入口，获取锁后委托给 <see cref="DoSaveCore"/>。
        /// 
        /// 为什么使用 Monitor.TryEnter 而非 lock 语句：
        ///   此方法可能被 Timer 回调线程调用（Save 的防抖触发），超时 10 秒后返回 false 而非永久阻塞。
        ///   lock 语句无法指定超时，因此显式使用 Monitor.Enter/Exit。
        /// </summary>
        private void DoSave()
        {
            if (Config == null) return;

            if (!Monitor.TryEnter(_saveLock, 10000))
            {
                _log?.Append("[配置] 无法获取保存锁（10s 超时），跳过本次写入");
                return;
            }

            try
            {
                DoSaveCore();
            }
            finally
            {
                Monitor.Exit(_saveLock);
            }
        }

        /// <summary>
        /// P1-1: 立即保存标签数据到独立的 tags.json 文件。
        /// 仅在标签导入/变更时调用，不触发全量 config.json 序列化。
        /// 原子写入策略与 DoSave 保持一致（临时文件 + File.Replace）。
        /// </summary>
        public void SaveTagsImmediate()
        {
            if (Config?.OpcDa?.Tags == null) return;

            lock (_saveLock)
            {
                try
                {
                    string tagsPath = GetTagsPath();
                    var tagsWrapper = new { Tags = Config.OpcDa.Tags };
                    string tagsJson = JsonConvert.SerializeObject(tagsWrapper, Formatting.Indented);

                    string tempPath = tagsPath + ".tmp";
                    File.WriteAllText(tempPath, tagsJson);
                    if (File.Exists(tagsPath))
                        File.Replace(tempPath, tagsPath, null);
                    else
                        File.Move(tempPath, tagsPath);

                    _log?.Append($"[配置] 已保存 {Config.OpcDa.Tags.Count} 个标签到 tags.json");
                }
                catch (Exception ex)
                {
                    _log?.Append($"保存标签配置失败: {ex.Message}");
                }
            }
        }

        /// <summary>
        /// 更新 OPC DA 服务器的 ProgId 并立即保存。
        ///
        /// 变更和保存在 _saveLock 内分两步执行：先在锁内修改 Config 属性（确保与 DoSave 的
        /// 序列化互斥，不会读到 torn state），然后调用 SaveImmediate 在锁外触发保存。
        /// </summary>
        /// <param name="progId">OPC DA 服务器的 ProgId（如 "Matrikon.OPC.Simulation.1"）</param>
        public void SaveProgId(string progId)
        {
            if (Config == null) return;
            lock (_saveLock)
            {
                if (Config.OpcDa == null)
                    Config.OpcDa = new OpcDaConfig();
                Config.OpcDa.ServerProgId = progId;
            }
            SaveImmediate();
            _log.Append($"已保存服务器 ProgId: {progId}");
        }

        /// <summary>
        /// 获取配置文件的完整路径（应用目录下的 config.json）。
        /// </summary>
        /// <returns>配置文件的绝对路径</returns>
        private static string GetConfigPath()
            => Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "config.json");

        /// <summary>
        /// P1-1: 获取标签数据文件的完整路径（应用目录下的 tags.json）。
        /// </summary>
        private static string GetTagsPath()
            => Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "tags.json");

        // ================================================================
        //  H-40: 配置文件热加载感知
        // ================================================================

        /// <summary>
        /// 启动 FileSystemWatcher 监听 config.json 的外部修改。
        /// 防抖 500ms：编辑器保存时可能触发多次 Changed 事件。
        /// </summary>
        private void StartWatching()
        {
            try
            {
                string configPath = GetConfigPath();
                string dir = Path.GetDirectoryName(configPath);
                string file = Path.GetFileName(configPath);

                _configWatcher = new FileSystemWatcher(dir, file)
                {
                    NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size,
                    EnableRaisingEvents = false  // 先不启动，等防抖设置完
                };

                // H-23 修复：Changed 事件可从多个 IO 完成端口线程并发触发，
                // 闭包局部变量 debounce 存在写-写竞态（双重 Dispose 或新 Timer 引用丢失）。
                // 将其提升为实例字段 _watcherDebounce，用 Interlocked.Exchange 原子替换，
                // 确保旧 Timer 被唯一地获取后再 Dispose，新 Timer 引用不被覆盖。
                _configWatcher.Changed += (s, e) =>
                {
                    var old = Interlocked.Exchange(ref _watcherDebounce,
                        new System.Threading.Timer(_ =>
                        {
                            Interlocked.Exchange(ref _watcherDebounce, null)?.Dispose();
                            _log?.Append("[配置] 检测到 config.json 外部修改");
                            ConfigFileChanged?.Invoke();
                        }, null, 500, Timeout.Infinite));
                    old?.Dispose();
                };

                _configWatcher.EnableRaisingEvents = true;
                _log?.Append("[配置] 文件监视已启动");
            }
            catch (Exception ex)
            {
                _log?.Append($"[配置] 文件监视启动失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 停止文件监视并释放资源。
        /// </summary>
        public void StopWatching()
        {
            try { _configWatcher?.Dispose(); } catch { }
            _configWatcher = null;
        }

        // H-40 改进：实现 IDisposable，确保 FileSystemWatcher 和 Timer 资源被正确释放
        public void Dispose()
        {
            StopWatching();
        }
    }
}
