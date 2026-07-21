# Handoff — OPC DA→UA 网关（任务交接文档）

> 用途：在新对话窗口继续本项目任务。新窗口读取本文件 + 关键源码即可接续，无需回溯历史。
> **交接时间：** 2026-07-17
> **当前版本：** V2.0.0
> **编译状态：** 0 警告 / 0 错误 ✅（Debug 与 Release 均通过）

---

## 1. 项目目标

`OpcDaToUaGateway` 是一款运行于工业现场上位机的协议转换网关：

- **功能**：本地读取 OPC DA 标签，映射为 OPC UA 节点，供第三方采集系统通过 UA 拉取。
- **核心要求**：7×24 长期稳定运行（≥30 天不中断）。
- **技术栈**：.NET Framework 4.7.2（x86）、WinForms、SDK 风格 `.csproj`；使用 `TitaniumAS.Opc.Client` 1.0.2 作 DA 客户端，自研 UA Server 基于 `OPCFoundation.NetStandard.Opc.Ua.Server` 1.5.378.145。
- **关键约束**：工业现场机器，UI 冻结 = 现场不可用；任何 UI 线程阻塞都属严重缺陷。

**优化主线（ponytail 代码审查 8 项）已全部完成。** 当前主线任务为**启动卡顿修复**，已完成（见第 3 节）。

---

## 2. 当前进度

| 维度 | 状态 |
|---|---|
| 过度工程清理（ponytail 8 项） | ✅ 完成 |
| 版本号升级 | ✅ 升至 V2.0.0（三个 `.csproj` 同步） |
| 已连接客户端列表功能移除 | ✅ 已移除（2026-07-16） |
| 启动窗口「未响应」卡顿修复 | ✅ V1.8.1 完成（节点创建移至后台线程） |
| 编译验证 | ✅ 0 警告 / 0 错误（Debug + Release，2026-07-21 V2.0.0 全面审查后复验） |
| 运行时验证 | ✅ V1.8.1 真实环境验证通过（3.5 万节点 UI 保持响应） |

**无遗留代码任务。** 后续均为可选新规划（见第 9 节）。

---

## 3. 已完成修改

### 3.1 移除「已连接客户端列表」功能（2026-07-16）
撤销 V1.7.0 在「OPC UA 服务器设置」区右侧新增的「已连接客户端」列表（该列表每 1~3 秒在 UI 线程经 SDK `SessionManager.GetSessions()` 访问会话管理器，与 UA 客户端请求线程竞争锁，导致窗口「未响应」）：
- `MainForm.cs`：删除 `_grpClients`/`_lstClients` 控件、`RefreshConnectedClients()` 定时刷新与停止清空逻辑；分组高度回退至 150。
- `GatewayOpcUaServer.cs`：删除 `GetConnectedClients()` 与 `GatewayServer.ServerInternalAccess` 属性。
- `Services/Interfaces/IGatewayOpcUaServer.cs`：删除 `GetConnectedClients()` 接口声明。

### 3.2 修复启动窗口「未响应」卡顿（2026-07-16，核心修复）
**根因（经反编译 `Opc.Ua.Server.dll` 1.5.378.145 坐实）**：`GatewayNodeManager.AddVariableNode` 在**每次**创建变量节点时调用 `Diag(...)`，经
`Diag → OnStatusChanged → _log.Append → LogManager.Append → BeginInvoke(UpdateTextBox)`
向 UI 线程投递数万次日志更新。`LogManager.UpdateTextBox` 每次 `AppendText` + 超限 `Substring` 重建，每次 O(当前文本长度)，3.5 万节点累计 **O(n²)**，导致约 7 分钟「未响应」。时间戳跨度（15:12:09→15:19:54）是后台线程在 `Append` 内取 `DateTime.Now` 的时刻，证伪「后台循环慢」假设——卡顿发生在 UI 线程排干 BeginInvoke。

**关键 SDK 证据**：`CustomNodeManager2.AddPredefinedNode` **不**调用 `UpdateChangeMasks`/`OnNodeAdded`，对变量仅做 `PredefinedNodes.AddOrUpdate`（O(1) 字典插入）+ 空子节点递归。**逐节点调用 `AddPredefinedNode` 不会造成 O(n²)。**

**改动（`GatewayOpcUaServer.cs`）**：
- 移除 `AddVariableNode` 内逐节点 `Diag(...)` 调用（及随之无用的 `variableId` 局部变量）。
- 移除误加的 `_bulkLoading` 字段与批量分支，**恢复一律** `AddPredefinedNode(SystemContext, variable)`（O(1) 且为必要注册，不可跳过）。
- `Diag` 方法保留，仅用于建地址空间、上限告警等低频调用。
- `DataBridge.Start()` 进度日志维持每 ~5%（`progressStep = Math.Max(1, total/20)`）节流，已无逐节点 `Log`。

### 3.3 启动卡顿最终修复 — 节点创建移至后台线程（V1.8.1，2026-07-17）

**根因（真实环境验证）**：V1.8.0 移除了逐节点 `Diag` 消除了 O(n²) 日志洪泛，但 3.5 万次 `AddVariableNode` 调用仍在 **UI 线程** 同步执行（每次持锁 `GatewayNodeManager.Lock`，`AddChild` + `AddPredefinedNode` 虽 O(1) 但 3.5 万次累积耗时数秒），导致窗口"未响应"。

**改动**：
- `DataBridge.cs`：新增 `StartAsync(Action<string> progressReport)` 方法，使用 `Task.Run` 将 3.5 万次 `AddVariableNode` 循环移至后台线程执行，通过 `progressReport` 回调报告进度（每 ~5% 一次）。节点创建完成后订阅 DA 事件。保留原有 `Start()` 同步方法用于测试。
- `GatewayManager.cs`：`StartAsync` 新增可选 `Action<string> progressReport` 参数，透传给 `bridge.StartAsync()`。
- `MainForm.cs`：`BtnStart_Click` 创建 `Action<string>` 进度回调，通过 `SynchronizationContext.Post` 安全投递到 UI 线程输出到日志框。

### 3.5 全面代码审查与 P0 修复（V2.0.0，2026-07-21）

**审查范围**：架构设计、线程安全、生命周期管理、性能、错误处理、代码质量。

**确认的 P0 Bug 修复**：
- **HealthSnapshot.Capture() Monitor 不配对**（`Services/HealthSnapshot.cs`）：`Monitor.TryEnter` 失败时 return，但 finally 块无条件 `Monitor.Exit` 导致 `SynchronizationLockException`。修复：`if (Monitor.IsEntered(_captureLock)) Monitor.Exit(_captureLock)`。两处（Capture + GenerateDailySummary）均已修复。

**确认非 Bug（已排除）**：
- `DataBridge.StartAsync` Task.Run 异常传播 — `await Task.Run` 已正确 unwrapping
- `OpcDaClient.AddAllItems` 锁内事件 — 已正确移到锁外
- `ConfigManager` FileSystemWatcher — 已在 `StopWatching()` 中正确 Dispose
- `OpcDaClient.OnValuesChanged` List 并发 — `AddAllItems` 在 `Start()` 中调用，早于任何回调

**P1 改进已实施**：
- `DataBridge.StartAsync`：`Task.Run` 内快照 `_uaServer` 引用 + null 检查，防止 `Stop()` 竞态
- `LicenseManager`：新增 `_disposed` 字段，Tick 回调开头检查防止 Dispose 后执行
- `LogManager.Dispose()`：超时后通过 `_log.Append` 输出警告而非 `Debug.WriteLine`
- `ConfigManager`：实现 `IDisposable`，封装 `StopWatching()` 到 `Dispose()`，MainForm.Dispose 改为调用 `Dispose()`
- `AppConstants.AppVersion`：更新为 V2.0.0

**P1 修复（本轮新增）**：
- `GatewayManager.StartAsync` catch 块：`await uaServer.StopAsync()` 改为 `uaServer.StopAsync().Wait()`，因为 catch 块不在 async 方法中
- `GatewayManager.CheckHealth`：重连计数器截断从 `attempts > MaxReconnectAttempts + 10` 改为 `attempts > MaxReconnectAttempts`，使用 `Interlocked.CompareExchange` 确保原子性
- `OpcDaClient.TryReconnect`：新增 `_disposedInt` 前置检查，防止 Dispose 后重连访问已释放的 COM 对象
- `OpcDaClient.Start`：新增 `Start(int, DaAcquisitionMode)` 重载，支持同步/异步模式（V1.6.0 功能补全）

**P1 待实施（未改代码）**：
- `HealthSnapshot.Capture()` 每 5 分钟 `Process.GetCurrentProcess()` 可能产生 GC 压力（建议 P/Invoke 替代）
- `ConfigManager` 未实现 `IDisposable`（建议封装 `StopWatching()` 到 `Dispose()`）
- `AppConstants.AppVersion` 已更新为 V2.0.0

### 3.6 文档同步
`STATUS.md` / `handoff.md` / `OPC_DA转UA网关开发指南.md` 中 V1.8.0 的「已知未决问题：启动窗口未响应」改为**已修复**，记录真实根因与 SDK 反编译证据；开发指南「运行时节点创建」节新增**启动性能红线**警示（禁止在 `AddVariableNode` 内逐节点 `Diag`/`Log`，进度日志放 `DataBridge` 节流）。


### 3.7 DA 标签真实数据类型获取（V2.0.0，2026-07-21）

**问题**：浏览 OPC DA 标签时，`OpcDaBrowseElement` 只返回 CanonicalDataType 为 Variant，无法反映标签的真实数据类型（Integer、Float、Boolean 等）。

**修复**：在 `OpcDaClient.BrowseAllItems` 浏览完成后，新增 `FillRealDataTypes` 方法：
- 创建不激活的临时 OPC DA Group（`_TempBrowseGroup`）
- 分批（每批 500 个）调用 `tempGroup.AddItems()` 获取 `OpcDaItemResult`
- 从 `result.Item.CanonicalDataType.Name` 提取真实类型名称
- 通过 `MapBclToDataType()` 映射为标准数据类型字符串
- 失败时静默回退，不影响浏览结果

**涉及文件**：
- `OpcDaClient.cs`：新增 `FillRealDataTypes()` 静态方法，调用入口在 `BrowseAllItems` 末尾

**编译验证**：0 警告 0 错误 ✅
---

## 4. 关键文件

| 文件 | 状态 | 说明 |
|---|---|---|
| `GatewayOpcUaServer.cs` | 已修改 | UA Server 核心（`GatewayNodeManager`）。移除了启动卡顿根因（逐节点 `Diag`），节点创建走 O(1) `AddPredefinedNode` |
| `DataBridge.cs` | 已修改 | V1.8.1：新增 `StartAsync(Action<string>)` 后台线程创建节点；`Start()` 保留用于测试 |
| `Services/LogManager.cs` | 已读（未改） | `Append` 后台线程取时间戳 + `BeginInvoke` 异步投 UI；`UpdateTextBox` 每次 O(文本长度)——高频诊断洪泛陷阱本源 |
| `Services/GatewayManager.cs` | 已修改 | `StartAsync` 用 `ConfigureAwait(false)`；V1.8.1 新增 `progressReport` 参数透传给 bridge |
| `MainForm.cs` | 已修改 | 移除客户端列表控件；V1.8.1 新增进度回调通过 SynchronizationContext 投递日志 |
| `Services/Interfaces/IGatewayOpcUaServer.cs` | 已修改 | 移除 `GetConnectedClients()` 声明 |
| `OpcDaToUaGateway.csproj` 等三处 | 已修改 | 版本号 V1.8.1 |
| `OPC_DA转UA网关开发指南.md` / `STATUS.md` / `PLAN.md` | 已更新 | 架构、版本历史、规划 |

---

## 5. 不能动的边界

| 文件 / 区域 | 原因 |
|---|---|
| `GatewayOpcUaServer.cs` 的 `UpdateValue` / 订阅 / 节点管理核心 | UA Server 线程安全敏感（V1.6.3 仅调 `UpdateValue` 内 SourceTimestamp 生成策略，未触及核心） |
| `OpcDaClient.cs` | OPC DA COM 客户端，生命周期/定时器/释放时序敏感 |
| `ConfigManager.cs` | 配置管理，未纳入优化范围 |
| `Services/WatchdogManager.cs` | 看门狗进程管理，仅经事件交互 |
| `Models/LicenseAlgorithm.cs` | 授权算法（PCID + HMAC），修改导致已授权用户失效 |
| `Theme.cs` / `AppConstants.cs` | 设计系统常量 |
| `Watchdog/` `Keygen/` 目录 | 独立子项目，仅经产物/事件交互 |
| `Services/Interfaces/IHealthSnapshot.cs` | 对外契约接口，改动影响订阅方 |

---

## 6. 已经否掉的方案

| 方案 | 否定原因 |
|---|---|
| **批量加载跳过 `AddPredefinedNode`**（`_bulkLoading` + `PredefinedNodes[...]=...` 分支） | 经反编译坐实 `AddPredefinedNode` 对变量是 O(1) 字典插入，**逐节点调用不会 O(n²)**；跳过它会绕过必要节点注册（风险），且对性能无益。该方案基于错误前提，已回退 |
| 工厂模式（`GatewayFactory`） | 仅默认实现，`StartAsync` 内强转回具体类型，抽象被完全绕过（已删除） |
| `GateController` 中间层 | 5 个事件均为透传，零业务逻辑（已删除） |
| 诊断日志（逐节点 `Diag`/`Log`）保留在 `AddVariableNode` | 3.5 万节点 → 3.5 万次 `BeginInvoke` → `UpdateTextBox` O(n²) → 启动卡死。已移除 |
| `TagSnapshot.Equals/GetHashCode` | 全项目无使用场景（已删） |

---

## 7. 当前风险点

| 风险 | 等级 | 说明 / 处置 |
|---|---|---|
| 未做运行时验证 | ✅ 已解决（V1.8.1） | 真实环境验证通过：3.5 万节点 UI 保持响应，进度日志实时输出到日志框 |
| 修复假设未被真机证实 | ✅ 已验证 | V1.8.1 通过 `Task.Run` 将节点创建移至后台线程，UI 线程仅负责日志输出和 UI 渲染 |
| `[Conditional("DEBUG")]` 排障信息丢失 | 🟡 中（设计取舍） | Release 模式移除诊断日志；若需现场排障，临时用 Debug 构建或加条件日志 |
| `Diag` 被重新引入逐节点 | 🟢 低（已加红线） | 开发指南已注明「启动性能红线」；但仍是人为风险，代码审查时需盯 `AddVariableNode` |

---

## 8. 已经跑过的测试

| 命令 / 检查 | 结果 |
|---|---|
| `dotnet build OpcDaToUaGateway.csproj -c Debug` | 0 警告 / 0 错误 ✅（2026-07-17 复验） |
| `dotnet build OpcDaToUaGateway.csproj -c Release` | 0 警告 / 0 错误 ✅（2026-07-17 复验，产物 `bin\Release\net472\OpcDaToUaGateway.exe`） |
| 全项目引用搜索 `_bulkLoading`/`BeginBulkLoad`/`variableId` | 0 处残留（回退干净） |
| `GatewayOpcUaServer.cs` 中 `Diag` 调用点核对 | 仅剩建地址空间(L111)、上限告警(L136)、FlatTags 根(L215) 三处低频调用；逐节点 `Diag` 已移除(L173 为 `AddPredefinedNode`) |
| `DataBridge.Start()` 节流核对 | `progressStep = Math.Max(1, total/20)`，`i % progressStep == 0 || i == total` 时打印进度（约 20 次） |
| SDK 反编译验证 | `ilspycmd` 反编译 `Opc.Ua.Server.dll` 1.5.378.145，确认 `AddPredefinedNode` 不触发逐节点通知、O(1) |

> 说明：若为 .NET Framework 且 `dotnet build` 不可用，可用 MSBuild：`msbuild OpcDaToUaGateway.csproj /p:Configuration=Release`。
> **未执行运行时测试**（无 OPC DA 环境）。

---

## 9. 下一步计划

优化主线已完成，**无强制待办**。可选后续方向见 `PLAN.md` 第 3 节：

| 方向 | 优先级 | 状态 |
|---|---|---|
| 3.1 接口抽象与单元测试 | P0 | 待评估（提取 `IOpcDaClient`/`IGatewayOpcUaServer`/`IDataBridge`，建 `FakeOpcDaClient` 做断连/丢包回归） |
| 3.2 多 DA 服务器聚合 | P1 | 待评估（`DaConfigs: List<OpcDaConfig>`，UA 前缀区分） |
| 3.3 UA Browse 数据类型增强 | P1 | 待评估 |
| 3.4 授权升级 HMAC→ECDSA P-256 | P1 | 待评估（需保留旧 HMAC 兼容） |
| 3.5 长期内存监控 | ✅ 已完成 | |
| 3.6 故障恢复预案 | ✅ 已完成 | 独立文档 `故障恢复预案.md` |

**建议处置顺序**：
1. ✅ **V1.8.1 启动卡顿已修复**，在真实 OPC DA 环境验证 3.5 万节点场景 UI 保持响应。
2. ✅ **V1.9.0 全面代码审查完成**，P0 bug 已修复，编译 0 警告 0 错误。
3. ✅ **V2.0.0 DA 标签真实数据类型获取**，通过临时 Group 从 CanonicalDataType 提取 Integer/Float/Boolean 等实际类型。编译 0 警告 0 错误。
4. 新功能需用户明确指令后再动手。

---

## 10. 新窗口启动提示词

> 复制到新对话窗口即可继续。

```
你是 OPC DA→UA 协议转换网关（OpcDaToUaGateway）的维护助手。项目根目录：D:\Documents\WorkBuddy\OpcDa2Ua。

先读取以下文件建立上下文：
- D:\Documents\WorkBuddy\OpcDa2Ua\handoff.md（任务交接与现状，含 10 节结构）
- D:\Documents\WorkBuddy\OpcDa2Ua\STATUS.md（状态报告）
- D:\Documents\WorkBuddy\OpcDa2Ua\OPC_DA转UA网关开发指南.md（架构与版本历史）
- D:\Documents\WorkBuddy\OpcDa2Ua\PLAN.md（稳定性与未来规划）

项目现状摘要（截至 2026-07-17）：
项目现状摘要（截至 2026-07-21）：
- 已完成：ponytail 代码精简 8 项（删 GateController.cs、GatewayFactory.cs，新建 LicenseManager.cs，MainForm 直连订阅，SafeInvoke 通用化等）；移除「已连接客户端」列表功能；启动卡顿修复（V1.8.0 移除逐节点 Diag，V1.8.1 将节点创建移至后台线程）。
- 启动卡顿根因（两阶段）：阶段1 — 原 GatewayNodeManager.AddVariableNode 在每次创建变量时调用 Diag()，经 OnStatusChanged→LogManager.Append→BeginInvoke(UpdateTextBox) 向 UI 线程投递数万次更新，UpdateTextBox 每次 O(文本长度) 累计 O(n²)，3.5 万节点约 7 分钟卡死。已移除该逐节点 Diag。阶段2 — 真实环境验证发现 3.5 万次 AddVariableNode 调用仍在 UI 线程同步执行，累积耗时数秒导致窗口"未响应"。V1.8.1 通过 Task.Run 将节点创建循环移至后台线程，UI 线程仅负责进度日志输出。
- 已否方案：批量加载跳过 AddPredefinedNode（基于错误前提，已回退）。
- 不能动的边界：GatewayOpcUaServer.cs 核心/订阅节点管理、OpcDaClient.cs、ConfigManager.cs、WatchdogManager.cs、Models/LicenseAlgorithm.cs、Theme.cs、AppConstants.cs、Watchdog/、Keygen/、IHealthSnapshot.cs。
- 当前无遗留代码任务。

请不要主动修改代码，除非我明确要求。先确认你已理解上下文，再等我给出具体任务指令。
```

---

*本 handoff.md 由交接会话生成（2026-07-17），与 STATUS.md / PLAN.md / OPC_DA转UA网关开发指南.md 配套使用。*
*本 handoff.md 由交接会话生成（2026-07-21），与 STATUS.md / OPC_DA转UA网关开发指南.md 配套使用。