# 稳定性问题 2～5 修复设计

## 范围

本次仅处理以下四项已确认问题：

1. 未授权试用时间在进程重启后重置；
2. `MainForm` 创建两个自动启动 Timer，并从线程池调用 UI 事件处理器；
3. `OpcDaClient` 同步采集模式在重连后退化为异步模式；
4. `OpcDaClient` 定时同步读取可重入，并可能与 COM 资源释放并发。

不在本次范围内：授权算法迁移、Keygen 发布方式、UA 身份认证、配置热重载重构及其他审查发现。

## 设计

### 1. 试用累计运行时间

新增内部 `TrialStateStore`，将试用累计运行时间保存在：

`%ProgramData%\OpcDaToUaGateway\trial.dat`

状态使用 Windows DPAPI `DataProtectionScope.LocalMachine` 保护。文件内容包含格式版本和累计秒数。`LicenseManager` 使用“历史累计秒数 + 当前进程 Stopwatch”计算剩余试用时间。

保存时机：

- 试用期间每 60 秒；
- `LicenseManager.Dispose()`；
- 有效授权码应用前。

状态文件不存在时视为首次试用，从 0 开始。文件存在但无法读取、解密、解析或数值非法时采用 fail-closed：视为试用已到期并记录日志。授权成功后保留试用状态，避免授权后失效重新获得完整试用期。

`TrialStateStore` 接受可覆盖的文件路径，便于在测试临时目录中验证 DPAPI 往返、累计值和损坏状态。

### 2. 单一自动启动流程

`SetupEventHandlersAndAutoStart()` 只初始化健康 Timer 和窗体事件，不再创建自动启动 Timer。

`LoadConfiguration()` 在配置、网关管理器和授权管理器初始化完成后，根据配置最多创建一个一次性 WinForms Timer。Tick 回调捕获自己的 Timer 实例，先停止、解绑、释放，再清除共享字段。自动启动直接在 UI 线程调用现有启动处理器，不使用 `Task.Run`。

保持现有产品语义：只有 `AutoStartUa` 且存在标签时启动网关；`AutoConnectDa` 仅恢复已选服务器，不新增自动连接行为。

### 3. 重连保持采集模式

`OpcDaClient.Start(updateRateMs, mode)` 记录当前采集模式。`TryReconnect(updateRateMs)` 使用该模式调用双参数 `Start`，不再使用默认异步重载。

模式字段初始值为 `Async`，保持现有默认行为。

### 4. 同步读取与 COM 生命周期

增加原子读取门禁，确保同一 `OpcDaClient` 最多一个 `DoSyncRead()` 在途。回调进入后增加在途计数，退出时在 `finally` 中清除门禁并通知等待者。

清理顺序：

1. 禁止新读取；
2. 停止并释放读取 Timer；
3. 有限等待在途读取完成；
4. 读取完成后释放 Group 和 Server；
5. 清空映射并更新连接状态。

若等待超时，为避免与仍在执行的 COM Read 并发释放，不释放 Group/Server，记录错误并让清理返回失败。重连因此返回失败；Dispose 记录资源未安全释放。等待必须有限，避免 UI 永久阻塞。

重新启动成功前重新允许读取。所有退出路径必须保持门禁和通知状态一致。

## 测试与验证

新增测试覆盖：

- DPAPI 状态写入和读取；
- 累计运行时间跨 `LicenseManager` 实例延续；
- 损坏状态 fail-closed；
- 采集模式保存/重连选择逻辑的可测试辅助行为；
- 单读取门禁不允许并发进入。

最终验证：

```bash
dotnet build OpcDaToUaGateway.sln -c Debug
dotnet build OpcDaToUaGateway.sln -c Release
dotnet test Tests/OpcDaToUaGateway.Tests.csproj -c Release
```

同步更新 `README.md`，说明试用累计时间持久化、自动启动修复、同步模式重连保持及同步读取生命周期保护。版本号保持现状，不在本次擅自升级。
