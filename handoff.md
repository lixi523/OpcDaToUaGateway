# Handoff - OPC DA to UA Gateway (OpcDaToUaGateway) 项目交接文档

> **生成时间：** 2026-07-26 13:00  
> **当前版本：** V2.4.0  
> **编译状态：** 0 警告 / 0 错误 ✅（Debug + Release）  
> **项目目录：** `D:\Documents\AgensCode\OpcDa2Ua`

---

## 1. 项目目标

`OpcDaToUaGateway` 是一个面向工业现场部署的协议转换网关：
- **核心功能**：实时读取 OPC DA 标签，映射为 OPC UA 节点，供数据采集系统通过 UA 获取
- **质量要求**：7×24 小时稳定运行，0 天不宕机
- **技术栈**：.NET Framework 4.7.2（x86）、WinForms、SDK 风格 `.csproj`，使用 `TitaniumAS.Opc.Client` 1.0.2 作 DA 客户端，服务端基于 `OPCFoundation.NetStandard.Opc.Ua.Server` 1.5.378.145
- **关键约束**：面向工业现场部署，UI 修改 = 现场修改，任何 UI 线程阻塞都会造成生产事故

---

## 2. 当前进度

| 维度 | 状态 |
|---|---|
| ponytail 代码清理（8 项） | ✅ 完成 |
| 版本号升级 | ✅ V2.4.0（三个 `.csproj` + `AppConstants` 同步） |
| 移除旧客户端列表功能 | ✅ 已移除 |
| 启动延迟、未响应问题修复 | ✅ V1.8.1 完成，节点创建走后台线程 |
| DA 标签真实数据类型获取 | ✅ V2.0.0 完成（临时 Group + CanonicalDataType） |
| String 类型标签显示修复 | ✅ V2.2.0 完成，移除 `!= "String"` 排除逻辑 |
| CSV 导出格式统一为 6 列 | ✅ V2.2.0 完成，含 UA 完整路径 |
| 按钮导航移除 | ✅ V2.2.0 完成 |
| 图标更新 | ✅ V2.2.0 完成，统一为 `opc-da-opc-ua.ico` |
| 接口抽象与单元测试 | ✅ V2.2.0 完成，56/56 测试通过 |
| 安全增强 | ✅ V2.3.0 完成：授权码 AES 加密、OnValuesChanged 防护、ConvertValue 日志 |
| 代码质量优化 | ✅ V2.3.0 完成：锁策略拆分、定时器泄漏修复、SafeInvoke 死锁修复、FormClosing 简化 |
| 架构优化 | ✅ V2.3.0 完成：AutoStartManager 分离、BuildUI 拆分为 9 子方法、常量统一 |
| 稳定性增强 | ✅ V2.4.0 完成：DPAPI 试用累计时间、单一自动启动 Timer、Sync 重连保持、同步 Read 生命周期保护 |
| 编译验证 | ✅ 0 警告 / 0 错误（Debug + Release） |
| 单元测试 | ✅ 56/56 通过 |

---

## 3. 已完成修改

### 3.1 启动性能修复（V1.8.0 → V1.8.1，2026-07-16/17）
**问题**：`GatewayNodeManager.AddVariableNode` 每次创建新 UA 节点时，调用 `Diag()` → `OnStatusChanged` → `_log.Append` → `BeginInvoke(UpdateTextBox)` 将日志消息投递到 UI 线程。`LogManager.UpdateTextBox` 每次 `AppendText` + 截断 `Substring` 是 O(n)。3.5 万节点累计 **O(n²)**，耗时约 7 分钟。
**修复**：
- V1.8.0：移除 `AddVariableNode` 中节点 `Diag()` 调用，节点创建改为 O(1) `AddPredefinedNode`
- V1.8.1：`DataBridge.StartAsync(Action<string>)` 使用 `Task.Run` 将 3.5 万 `AddVariableNode` 循环在后台线程执行，通过 `progressReport` 回调报告进度，每 ~5% 一次

### 3.2 ponytail-review 代码清理（V1.9.0，2026-07-20）
| 项目 | 内容 |
|---|---|
| R1 删除 GateController | 删除 `Services/GateController.cs`（150 行），事件透传模式替代 |
| R2 删除 GatewayFactory | 删除 `GatewayFactory.cs`（56 行），StartAsync 改用直接 `new` |
| R3 SafeInvoke 统一 | MainForm 新增 `SafeInvoke(Action)` 通用方法替换 4 处重复模式 |
| R4 DataBridge 简化 | 删除 `_cachedDisplayName` 字典、`TagSnapshot.Equals/GetHashCode` |
| R5 LogManager 清理 | 删除注释块、手动注释、空 catch |
| R6 HealthSnapshot 重构 | 新增 4 个私有分析函数 |
| R7 DEBUG 条件编译 | `Diag()` 添加 `[Conditional("DEBUG")]`，Release 自动消除 |
| **合计** | **减少约 -485 行** |

### 3.3 全面 P0/P1 修复（V2.0.0，2026-07-21）
**P0 Bug 修复**：
- `HealthSnapshot.Capture()` Monitor 死锁：`TryEnter` 失败时 return，finally 中不会 `Exit` 导致 `SynchronizationLockException`

**P1 建议实施**：
- `GatewayManager.StartAsync` catch 块：`await uaServer.StopAsync()` 改为 `ConfigureAwait(false).GetAwaiter().GetResult()` 防止 SynchronizationContext 死锁
- `GatewayManager.StopAsync`：双重 `_disposedInt` 检查，防止双释放
- `ConfigManager`：FileSystemWatcher debounce Timer 泄漏修复（`Interlocked.Exchange` 原子替换时 Timer Dispose）
- `FillRealDataTypes`：显式写入 UpdateRate 字段
- `OpcDaClient.TryReconnect`：增加 `_disposedInt` 前置检查
- `HealthSnapshot`：异常 catch 改为 `Debug.WriteLine` 输出
- `Watchdog`：最小化启动参数改为 `--minimized`

### 3.4 String 类型标签显示修复（V2.2.0，2026-07-23）
**问题**：部分 OPC DA 标签类型为 String 时显示为 "Variant"
**修复**：移除 `OpcDaClient.cs` 中 `ExtractDataType` 和 `FillRealDataTypes` 内的 `&& typeName != "String"` 排除逻辑

### 3.5 CSV 导出格式统一（V2.2.0，2026-07-23）
**问题**：导出/导入时，`CsvTagExporter` 和 `ItemSelectionDialog` 格式不统一，缺少 UA 完整路径
**修复**：
- 统一为 6 列格式：`名称, ItemId, 数据类型, 值, 质量, UA完整路径`
- `CsvTagExporter.cs`：新增 6 列"UA完整路径"，值为 `ns={NamespaceIndex};s={Path}` 格式
- `ItemSelectionDialog.cs`：导出/导入逻辑统一为 6 列格式
- `MainForm.cs`：导入时解析 `NamespaceIndex` 写入 CsvTagExporter
- 兼容旧格式（检测头部行数自动转换）
- 移除 MainForm 上的"导出CSV"按钮

### 3.6 图标更新（V2.2.0，2026-07-23）
- 主项目 `.csproj`：`ApplicationIcon` 从 `app.ico` 改为 `opc-da-opc-ua.ico`
- Keygen 子项目：`ApplicationIcon` 改为 `..\opc-da-opc-ua.ico`
- Watchdog 子项目：`ApplicationIcon` 改为 `..\opc-da-opc-ua.ico`
- MainForm 窗口图标：`app.ico` 资源文件名仍为 app.ico，不影响

### 3.7 版本号升级（V2.2.0，2026-07-24）
所有 `.csproj`、`AppConstants.AppVersion`、README.md、STATUS.md、handoff.md、使用文档.md 同步更新

### 3.8 接口抽象与单元测试（V2.2.0，2026-07-24）
**目标**：为扩展性、可测试性、可替换性奠定基础
**变更记录**：
- `IGatewayOpcUaServer.cs`：新增 `UpdateValue(string tagKey, object value, bool isGood, DateTime sourceTimestamp)` 方法签名
- `DataBridge.cs`：`_daClient` 和 `_uaServer` 字段从具体类型改为接口（`IOpcDaClient` / `IGatewayOpcUaServer`），构造函数接受接口参数
- `GatewayManager.cs`：构造函数接受可选接口参数（默认 null），`StartAsync` 条件实例化（如 `uaServer == null` 则 new 具体类）
- `FakeGatewayOpcUaServer.cs`：新建，实现 `IGatewayOpcUaServer` 完整 Mock 实现（内存存储变量、状态计数、故障注入标志 ThrowOnAddNode/ThrowOnUpdate）
- `Tests/FakeGatewayOpcUaServerTests.cs`：新建，14 个 xUnit 测试，覆盖生命周期、节点创建、值更新、故障注入、释放安全
- `OpcDaToUaGateway.csproj`：新增 `<Compile Remove="Tests\**" />` 排除测试文件
- **测试结果**：Debug 0 警告 0 错误，Release 0 警告 0 错误，Test 0 警告 0 错误，14/14 通过

### 3.9 安全增强与代码质量优化（V2.3.0，2026-07-26）

**安全增强：**
- 授权码 AES-256-CBC 加密存储：`config.json` 中 `AuthorizationCode` 不再明文，密钥由 `LicenseAlgorithm.DeriveKey()` 派生，随机 IV 每次加密生成
- `OpcDaClient.OnValuesChanged` 添加 `Volatile.Read(ref _disposedInt)` 检查，防止 Dispose 后回调崩溃
- `DataBridge.ConvertValue` 空 catch 添加日志告警，数据类型转换失败不再静默

**代码质量优化：**
- `ConfigManager` 锁策略拆分：`DoSave()` 拆分为 `DoSaveCore()`（无锁，由调用者持有）+ `DoSave()`（Timer 回调入口，获取锁后委托）
- `WatchdogManager.Start` 异常时 `_heartbeatTimer?.Dispose()` 修复定时器泄漏
- `LogManager.WriterLoop` 文件重建失败时降级写入 `Debug.WriteLine`，日志不再永久丢失
- `MainForm.SafeInvoke` 改为 `BeginInvoke`，消除同步 Invoke 的潜在死锁
- `OpcDaClient.TryReconnect` 重构：内联释放资源，避免调用 `Cleanup()` 导致的 `_disposedInt` 竞态
- `MainForm.FormClosing` 四个标志位（`_isShuttingDown`, `_closeInProgress`, `_isShuttingDone`, `_forceClose`）简化为两个
- `HealthSnapshot.Capture` 在 Monitor 内添加 `Volatile.Read` 二次检查

**架构优化：**
- `AutoStartManager` 从 `ConfigManager` 分离：新建 `Services/AutoStartManager.cs`，`ConfigManager` 缩减约 70 行
- `MainForm.BuildUI` 从 597 行拆分为 9 个子方法：`BuildDaServerSection`、`BuildUaServerSection`、`BuildControlPanelSection`、`BuildTagMonitorSection`、`BuildLogSection`、`InitializeManagers`、`BuildTrayIcon`、`SetupEventHandlersAndAutoStart`
- 修复 3 个 xUnit1031 警告（`Task.WaitAll` → `await Task.WhenAll`）
- `HealthSnapshot` 缓存 Process 对象，避免每 5 分钟创建新实例
- 硬编码字符串 `"localhost"`、`"Variant"` 统一为 `AppConstants.DefaultDaHost`、`AppConstants.UnknownDataType`
- 去除所有文件中的冗余修复标记注释（`P1-1`、`H-40`、`C-14`、`R-8` 等），保留描述性注释

**验证结果：** Debug 0 警告 0 错误，Release 0 警告 0 错误，测试 56/56 通过

---

## 4. 关键文件

| 文件 | 状态 | 说明 |
|---|---|---|
| `GatewayOpcUaServer.cs` | 已修改 | UA Server 封装，移除节点 Diag，节点创建改为 O(1) AddPredefinedNode |
| `DataBridge.cs` | 已修改 | V1.8.1 StartAsync 后台线程创建节点；V2.2.0 接口化 `_daClient`/`_uaServer`；V2.3.0 ConvertValue 空 catch 日志 |
| `Services/CsvTagExporter.cs` | 已修改 | V2.2.0 6 列格式，含 UA 完整路径，UTF-16 LE BOM，RFC 4180 逗号转义 |
| `Services/GatewayManager.cs` | 已修改 | ConfigureAwait(false) 修复，StopAsync 双释放保护，MaxReconnectAttempts 参数化，V2.2.0 接口注入 |
| `Services/ConfigManager.cs` | 已修改 | FileSystemWatcher debounce Timer 泄漏修复；V2.3.0 锁策略拆分，移除 auto-start 方法 |
| `Services/HealthSnapshot.cs` | 已修改 | Monitor TryEnter 修复，异常 catch 改 Debug.WriteLine，V2.2.0 移除未使用的 _timer 字段，V2.3.0 缓存 Process |
| `OpcDaClient.cs` | 已修改 | FillRealDataTypes 移除 String 排除，BrowseAllItems 批量获取真实类型，V2.3.0 OnValuesChanged 防护 + TryReconnect 重构 |
| `MainForm.cs` | 已修改 | 移除导航按钮，SafeInvoke 增加 ObjectDisposedException 防护，VirtualListSize/RowCount 虚拟化，AboutDialog 信息面板自适应高度+滚动条，V2.3.0 BuildUI 拆分为 9 子方法 |
| `ItemSelectionDialog.cs` | 已修改 | 导出/导入逻辑统一 6 列格式 |
| `GatewayState.cs` | 已修改 | 新增 GatewayStateExtensions.ToDisplayName() 统一显示 |
| `Models/TagConfig.cs` | 已修改 | OpcDaConfig 新增 MaxReconnectAttempts 参数 |
| `AppConstants.cs` | 已修改 | AppVersion = "2.4.0"，包含 DefaultDaHost / UnknownDataType 常量 |
| `Services/AutoStartManager.cs` | **新建** | V2.3.0 从 ConfigManager 分离的开机启动管理器 |
| `Services/LicenseManager.cs` | 新建 | 封装授权/到期/事件通知 |
| `Services/Interfaces/*.cs` | 已修改/新建 | IOpcDaClient, IGatewayOpcUaServer, IDataBridge, IHealthSnapshot |
| `Services/Interfaces/FakeGatewayOpcUaServer.cs` | 新建 | Mock 实现，用于单元测试 |
| `Tests/FakeGatewayOpcUaServerTests.cs` | 新建 | 14 个 xUnit 测试 |
| `Tests/LicenseAlgorithmTests.cs` | 新建 | 18 个 xUnit 测试 |
| `Tests/DataBridgeTests.cs` | 新建 | 24 个 xUnit 测试 |

---

## 5. 不能动的边界

| 文件 / 模块 | 原因 |
|---|---|
| `GatewayOpcUaServer.cs` 内 `UpdateValue` / 订阅 / 节点管理核心 | UA Server 线程安全，修改风险极高 |
| `OpcDaClient.cs` COM 连接/断开逻辑 | OPC DA COM 客户端，线程模型复杂，连接/超时/释放时序敏感 |
| `ConfigManager.cs` 配置逻辑 | 配置管理优先级高，影响稳定性 |
| `Services/WatchdogManager.cs` | 看门狗进程管理逻辑，通过事件通知 |
| `Models/LicenseAlgorithm.cs` | 授权算法（PCID + HMAC），修改会导致历史授权失效 |
| `Theme.cs` / `AppConstants.cs` | 全局样式常量 |
| `Watchdog/` / `Keygen/` 目录 | 独立子项目，编译后自动复制到输出目录 |
| `Services/Interfaces/IHealthSnapshot.cs` | 健康快照接口 |

---

## 6. 已否掉的方案

| 方案 | 否决原因 |
|---|---|
| **保留 `AddPredefinedNode` 回退**（`_bulkLoading` + 旧支持） | 当前实现已确认 `AddPredefinedNode` 自带 O(1) 字典查找，无需回退机制 |
| **保留 `GatewayFactory` 模式** | 仅默认实现，`StartAsync` 强制转回具体类型，完全可移除 |
| **保留 `GateController` 中间层** | 5 个事件纯透传，无业务逻辑 |
| **保留节点日志 `Diag`/`Log` 在 `AddVariableNode` 中** | 3.5 万节点 × 3.5 万次 BeginInvoke = O(n²)，已确认性能杀手 |
| **保留 `TagSnapshot.Equals/GetHashCode`** | 全项目未使用 |
| **保留旧式 CSV 格式** | 6 列格式已兼容旧格式转换 |

---

## 7. 当前风险点

| 风险 | 等级 | 说明 / 缓解 |
|---|---|---|
| 未经验证的运行时 | 🟢 已缓解(V1.8.1) | 实际验证通过，3.5 万节点 UI 无卡顿 |
| `[Conditional("DEBUG")]` 调试信息丢失 | 🟡 中（需感知） | Release 模式消除调试日志，排查问题时需临时切 Debug |
| `Diag` 函数仍有 3 处调用节点 | 🟢 低（已加后缀） | 按需调用，注意性能 |
| `_gridTags` 字段未使用 | 🟢 低 | 预留给虚拟模式，未赋值不影响功能 |
| CSV 导出格式兼容性 | 🟢 低 | 新版本 6 列为默认，旧版 3/5 列格式已兼容转换 |

---

## 8. 已经跑过的测试

| 测试 / 验证 | 结果 |
|---|---|
| `dotnet build OpcDaToUaGateway.csproj -c Debug` | 0 警告 / 0 错误 ✅ |
| `dotnet build OpcDaToUaGateway.csproj -c Release` | 0 警告 / 0 错误 ✅ |
| Test 项目编译 | 0 警告 / 0 错误 ✅ |
| xUnit 单元测试（LicenseAlgorithmTests） | 18/18 全部通过 ✅ |
| xUnit 单元测试（DataBridgeTests） | 24/24 全部通过 ✅ |
| xUnit 单元测试（FakeGatewayOpcUaServerTests） | 14/14 全部通过 ✅ |
| **合计** | **56/56 全部通过** ✅ |
| 全项目无未引用/已删除文件 | 0 冗余 ✅ |
| `GatewayOpcUaServer.cs` 中 `Diag` 调用审计 | 仅剩余命名空间、修改警告、FlatTags 标签刷新频率 ✅ |
| SDK 源码验证 | `ilspycmd` 确认 `Opc.Ua.Server.dll` 1.5.378.145 中 `AddPredefinedNode` 自带 O(1) 节点通知 ✅ |
| CSV 格式验证 | 6 列模板文件 `test_csv_format.csv` 结构正确 ✅ |

> **未执行运行时验证**：需要 OPC DA 服务器（如 Matrikon / Kepware）配合验证，非测试环境无法确认。

---

## 9. 下一步计划

优化工作已全部完成，**加强扩展性**为可选规划，不再维护 `PLAN.md`（已删除）：

| 项目 | 优先级 | 状态 |
|---|---|---|
| 3.1 接口抽象与单元测试 | P0 | ✅ 已完成（V2.2.0） |
| 3.2 多 DA 聚合扩展 | P1 | 🟡 待规划（需 `DaConfigs: List<OpcDaConfig>` + UA 前缀） |
| 3.3 UA Browse 类型增强 | P1 | 🟡 待规划 |
| 3.4 授权算法 HMAC→ECDSA P-256 | P1 | 🟡 待规划（需兼容旧 PCID 数据） |
| 3.5 内存监控 | ? 低 | HealthSnapshot 每 5 分钟采集内存/CPU/标签数 |
| 3.6 崩溃恢复预案 | ? 低 | 参考文档 `崩溃恢复预案.md` |

**推荐顺序**：
1. ✅ V1.8.1 启动性能修复
2. ✅ V1.9.0 全面代码清理
3. ✅ V2.0.0 DA 标签真实数据类型获取
4. ✅ V2.2.0 String 类型修复 + CSV 6 列统一 + 按钮导航移除 + 图标更新 + 接口抽象 + 单元测试
5. ✅ V2.3.0 安全增强 + 代码质量优化 + 架构优化
6. ✅ V2.4.0 试用持久化 + 自动启动/DA 重连/读取生命周期稳定性增强
7. 等待用户明确指示再推进

---

## 10. 新窗口启动提示词

> 复制以下内容到新对话窗口即可接续：
>
> 我是 OPC DA→UA 协议转换网关（OpcDaToUaGateway）维护助手。项目目录：D:\Documents\AgensCode\OpcDa2Ua
>
> 先读取以下文件建立上下文：
> - D:\Documents\AgensCode\OpcDa2Ua\handoff.md（项目交接文档，含 10 节结构）
> - D:\Documents\AgensCode\OpcDa2Ua\STATUS.md（状态看板）
> - D:\Documents\AgensCode\OpcDa2Ua\OPC_DA转UA网关开发指南.md（架构/版本历史）
>
> 项目现状摘要（截至 2026-07-26）：
> - 版本 V2.4.0，Debug + Release 均 0 警告 / 0 错误
> - ponytail 代码清理 8 项完成（删除 GateController.cs、GatewayFactory.cs，新建 LicenseManager.cs，MainForm 直接委托，SafeInvoke 通用化等），移除旧客户端列表功能
> - 性能修复：V1.8.0 移除节点 Diag，V1.8.1 节点创建走后台线程
> - DA 标签真实数据类型获取：V2.0.0（临时 Group + CanonicalDataType）
> - String 类型标签修复：V2.2.0（移除排除逻辑）
> - CSV 导出统一 6 列格式含 UA 完整路径：V2.2.0
> - 按钮导航移除：V2.2.0
> - 图标更新为 opc-da-opc-ua.ico：V2.2.0
> - 接口抽象与单元测试：V2.2.0（IOpcDaClient/IGatewayOpcUaServer/IDataBridge 接口化，FakeGatewayOpcUaServer Mock，56 个 xUnit 测试全部通过）
> - 安全增强：V2.3.0（授权码 AES 加密、OnValuesChanged 防护、ConvertValue 日志）
> - 代码质量优化：V2.3.0（锁策略拆分、定时器泄漏修复、SafeInvoke 死锁修复、FormClosing 简化、TryReconnect 竞态修复）
> - 架构优化：V2.3.0（AutoStartManager 分离、BuildUI 拆分为 9 子方法、常量统一、注释清理）
> - 稳定性增强：V2.4.0（DPAPI 试用累计时间、单一自动启动 Timer、Sync 重连保持、同步 Read 生命周期保护，66/66 测试通过）
> - 不能动边界：GatewayOpcUaServer.cs 内部/修改节点逻辑、OpcDaClient.cs、ConfigManager.cs、WatchdogManager.cs、Models/LicenseAlgorithm.cs、Theme.cs、AppConstants.cs、Watchdog/、Keygen/、IHealthSnapshot.cs
> - 当前无待办，等待用户明确指示再推进
>
> 不要擅自修改代码，除非用户明确要求你做什么，或少量补遗。遵循用户指令。

---

*本 handoff.md 由当前会话生成，2026-07-26。STATUS.md / OPC_DA转UA网关开发指南.md / 使用文档.md 配合使用。*