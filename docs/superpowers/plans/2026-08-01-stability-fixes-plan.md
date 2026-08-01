# Stability Fixes Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 修复试用时间重启重置、重复自动启动、同步模式重连退化及 OPC DA 同步读取/释放竞态。

**Architecture:** 用独立 `TrialStateStore` 管理 DPAPI 状态，`LicenseManager` 只组合历史累计时间和本次运行时间；自动启动只由配置加载完成后调度一次；`OpcDaClient` 保存有效采集模式，并通过原子门禁和完成事件协调 Timer Read 与 COM 清理。修改保持局部，不重构无关模块。

**Tech Stack:** C# 8.0、.NET Framework 4.7.2、WinForms、Windows DPAPI、xUnit、TitaniumAS OPC DA Client。

> 当前工作树包含用户未提交改动：执行期间不得 commit，不得清理或覆盖无关改动。

---

## 文件结构

- Create: `Services/TrialStateStore.cs` — DPAPI 保护的试用累计秒数存储。
- Create: `Tests/TrialStateStoreTests.cs` — 状态往返、损坏状态和累计时间测试。
- Modify: `Services/LicenseManager.cs` — 使用持久化累计时间计算倒计时并定期保存。
- Modify: `MainForm.cs` — 移除早期 Timer，仅在配置完成后调度一次 UI Timer。
- Modify: `OpcDaClient.cs` — 保存模式、单读取门禁、等待在途读取后清理 COM。
- Create: `Tests/OpcDaClientLifecycleTests.cs` — 测试模式选择和读取门禁辅助逻辑。
- Modify: `README.md` — 记录四项行为修复及当前验证状态。

### Task 1: DPAPI 试用状态存储

- [ ] **Step 1: 写失败测试**

在 `Tests/TrialStateStoreTests.cs` 添加：临时路径写入 `125` 秒后可读回；随机损坏文件返回 `TrialStateLoadResult.Invalid`；不存在文件返回 `NotFound`。

- [ ] **Step 2: 验证测试失败**

Run: `dotnet test Tests/OpcDaToUaGateway.Tests.csproj -c Release --filter TrialStateStoreTests`
Expected: FAIL，类型 `TrialStateStore` 不存在。

- [ ] **Step 3: 实现最小状态存储**

在 `Services/TrialStateStore.cs` 定义：

```csharp
internal enum TrialStateLoadResult { Success, NotFound, Invalid }

internal sealed class TrialStateStore
{
    internal TrialStateStore(string filePath = null);
    internal TrialStateLoadResult TryLoad(out long elapsedSeconds);
    internal bool TrySave(long elapsedSeconds);
}
```

默认路径为 `%ProgramData%\OpcDaToUaGateway\trial.dat`；载荷为版本号 `1` 和非负 `Int64` 秒数；使用 `ProtectedData.Protect/Unprotect(..., DataProtectionScope.LocalMachine)`；写入采用临时文件加原子替换/移动；异常返回失败，不向外抛出。

为测试访问内部类型，在主项目增加 `Properties/AssemblyInfo.cs`：

```csharp
[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("OpcDaToUaGateway.Tests")]
```

- [ ] **Step 4: 运行定向测试**

Run: `dotnet test Tests/OpcDaToUaGateway.Tests.csproj -c Release --filter TrialStateStoreTests`
Expected: PASS。

### Task 2: LicenseManager 累计运行时间

- [ ] **Step 1: 写失败测试**

在 `Tests/LicenseTrialClockTests.cs` 测试纯计算辅助方法：历史 `120` 秒加本次 `30` 秒得到 `150` 秒；超过总试用时间时剩余值为零；保存值按非负整数秒截断。

- [ ] **Step 2: 验证测试失败**

Run: `dotnet test Tests/OpcDaToUaGateway.Tests.csproj -c Release --filter LicenseTrialClockTests`
Expected: FAIL，辅助类型不存在。

- [ ] **Step 3: 实现累计时间接入**

在 `Services/LicenseManager.cs` 增加内部静态纯计算辅助方法，并注入可选 `TrialStateStore`。初始化未授权状态时：

- `Success`：加载历史秒数；
- `NotFound`：从零开始；
- `Invalid`：历史秒数设为完整试用时长并记录 fail-closed 日志。

每次 Tick 使用历史时间加 `_trialStopwatch.Elapsed`；每 60 秒保存一次；到期时立即保存完整试用时间；Apply 授权前和 Dispose 时保存。保存失败记录日志但不重置内存累计值。

- [ ] **Step 4: 运行授权相关测试**

Run: `dotnet test Tests/OpcDaToUaGateway.Tests.csproj -c Release --filter "LicenseTrialClockTests|LicenseAlgorithmTests|TrialStateStoreTests"`
Expected: PASS。

### Task 3: 单一自动启动 Timer

- [ ] **Step 1: 删除早期 Timer 创建**

修改 `MainForm.SetupEventHandlersAndAutoStart()`：保留健康 Timer、窗体事件和日志，不创建 `_autoStartTimer`。

- [ ] **Step 2: 收敛调度方法**

新增 `ScheduleAutoStartIfNeeded()`，仅在 `Config.AutoStartUa && Config.OpcDa.Tags?.Count > 0` 时创建一次 Timer。创建前释放旧字段；Tick 捕获局部 Timer，停止、解绑、Dispose，仅在字段仍指向自己时清空字段，然后 `HideToTray()` 并直接调用 `BtnStart_Click`。

删除 `AutoStartTick()` 和 `LoadConfiguration()` 中匿名重复 Timer，改为在授权管理器事件初始化完成后调用 `ScheduleAutoStartIfNeeded()`。

- [ ] **Step 3: 静态检查与构建**

Run: `rg -n "Task.Run\(\(\) => BtnStart_Click|new Timer \{ Interval = 1000 \}|AutoStartTick" MainForm.cs`
Expected: 仅保留 `ScheduleAutoStartIfNeeded()` 内一个 1 秒 Timer，且无 `Task.Run` 启动 UI。

Run: `dotnet build OpcDaToUaGateway.csproj -c Debug`
Expected: 成功，无新增警告。

### Task 4: OpcDaClient 模式保持

- [ ] **Step 1: 写失败测试**

在 `Tests/OpcDaClientLifecycleTests.cs` 测试内部 `OpcDaClientLifecycleState`：默认模式为 Async；记录 Sync 后重连模式为 Sync；记录 Async 后为 Async。

- [ ] **Step 2: 实现最小生命周期状态**

在 `OpcDaClient.cs` 增加内部小型状态类或内部可测方法，保存 `DaAcquisitionMode`。`Start(updateRateMs, mode)` 在创建 Timer 前记录 mode；`TryReconnect()` 调用 `Start(updateRateMs, savedMode)`。

- [ ] **Step 3: 运行定向测试**

Run: `dotnet test Tests/OpcDaToUaGateway.Tests.csproj -c Release --filter OpcDaClientLifecycleTests`
Expected: PASS。

### Task 5: 单读取门禁与安全清理

- [ ] **Step 1: 扩展失败测试**

为 `OpcDaClientLifecycleState` 增加测试：首次 `TryEnterRead()` 成功；第二次失败；`ExitRead()` 后再次成功；`StopReadsAndWait(timeout)` 在无在途读取时成功；有在途读取时先阻塞，退出后成功；超时返回 false。

- [ ] **Step 2: 实现同步协调**

状态对象使用 `Interlocked` 和 `ManualResetEventSlim`：

```csharp
internal bool TryEnterRead();
internal void ExitRead();
internal void EnableReads();
internal bool StopReadsAndWait(TimeSpan timeout);
```

`DoSyncRead()` 进入失败即返回，主体用 `try/finally` 调用 `ExitRead()`。启动前 `EnableReads()`。Cleanup 和重连先停止 Timer，再有限等待读取结束；只有等待成功才释放 COM。等待超时记录明确日志并返回失败，重连不继续创建新连接。

- [ ] **Step 3: 运行生命周期测试和全部测试**

Run: `dotnet test Tests/OpcDaToUaGateway.Tests.csproj -c Release --filter OpcDaClientLifecycleTests`
Expected: PASS。

Run: `dotnet test Tests/OpcDaToUaGateway.Tests.csproj -c Release`
Expected: 全部通过。

### Task 6: README、构建和审查

- [ ] **Step 1: 更新 README**

在 V2.3.0 状态说明中补充：试用时间按累计运行时间持久化、自动启动单 Timer/UI 线程执行、Sync 重连保持模式、同步读取停止后等待在途 Read。修正测试数为最终实际结果，不升级版本号。

- [ ] **Step 2: 完整验证**

Run:

```bash
dotnet build OpcDaToUaGateway.sln -c Debug
dotnet build OpcDaToUaGateway.sln -c Release
dotnet test Tests/OpcDaToUaGateway.Tests.csproj -c Release --no-build
```

Expected: 两种配置构建成功，全部测试通过；不得新增警告。原有 `_gridTags` 警告若仍存在，记录为既有问题，不在本次无关清理。

- [ ] **Step 3: 检查差异范围**

Run: `git diff -- Services/TrialStateStore.cs Services/LicenseManager.cs MainForm.cs OpcDaClient.cs Tests/TrialStateStoreTests.cs Tests/LicenseTrialClockTests.cs Tests/OpcDaClientLifecycleTests.cs README.md Properties/AssemblyInfo.cs`
Expected: 每项修改都可追溯到四个目标问题，无无关格式化或重构。

- [ ] **Step 4: 独立 review**

重点检查 DPAPI fail-closed、Timer UI 线程、COM Read 等待是否可能死锁，以及 Dispose/重连所有异常路径。
