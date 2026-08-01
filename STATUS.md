# OPC_DA转UA网关 — 项目状态报告

**生成时间：** 2026-07-23 17:00
**当前版本：** V2.4.0
**编译状态：** 0 警告 0 错误 ✅

---

## 1. 当前目标

对 OpcDaToUaGateway 项目进行代码瘦身与结构优化（ponytail 8 项），消除过度工程、冗余代码，提升可维护性。

**状态：全部执行完毕，编译通过，版本号已升级到 V2.4.0。**

> **版本演进（自 V1.5.0 起，详见 handoff.md / 开发指南版本历史）：**
> - **V1.5.1**（本会话）：修复 `RunningStateChanged` 跨线程异常（`SafeInvoke` 封送缺失）、`SafeInvoke` 缺句柄防护、删除 `LicenseManager` 死代码 `_requestGatewayStop`；编译 0 警告 0 错误。
> - **V1.6.0**：新增 OPC DA 同步/异步获取模式（`OpcDaConfig.Mode` + UI「数据获取」下拉 + `OpcDaClient.Start(mode)` 按模式分支）。
> - **V1.6.1–V1.6.3**：修复频繁翻转 bool 标签数值/时间戳不更新——根因为 OPC DA 服务器给 bool 标签打出的源时间戳长期冻结；`UpdateValue` 单调时间戳下限改为网关 UTC 时钟，快照时间戳改用网关接收时刻。
> - **V1.7.0**：定稿发布，移除 V1.6.2 临时诊断日志；三个项目版本号统一 1.7.0。
> - **V1.7.0+（UI微调）**：`AppConstants.WindowTitle` 去掉版本号（"OPC DA → OPC UA 网关 v" + AppVersion → "OPC DA → OPC UA 网关"）；`MainForm.cs` 数据获取下拉与「获取点位」按钮对齐到同一行。
> - **V1.7.0（运行时修复，2026-07-15）**：版本号保持 1.7.0 不变，修复三个运行时缺陷——① `ItemSelectionDialog` 浏览点位 `BeginInvoke` 句柄未创建异常（新增 `SafeBeginInvoke`，句柄未就绪时订阅 `HandleCreated` 延后执行）；② 标签监控「时间戳」列 UTC 直接显示导致北京用户慢 8 小时（显示层 `ToLocalTime()` 转换，数据模型保持 UTC）；③ `OpcDaClient` 质量判定误用操作结果 `value.Error.Succeeded` 致 UA 客户端大量质量 Bad（改为 `value.Quality.Status & 0xC0 == 0xC0` 质量位判定，移除误丢有效数据的硬跳过，新增 `IsQualityGood`）；编译 0 警告 0 错误。
> - **V1.7.0（时间戳同源统一，2026-07-15）**：版本号保持 1.7.0 不变，修复 UA 客户端与网关监控时间戳不一致——原 `DataBridge` 将 DA 源戳传给 UA、监控快照另取 `DateTime.UtcNow`，两侧基准不同源（DA 源戳冻结/时钟偏差时系统性错位）；改为 `OnDaDataChanged` 内统一取网关接收时刻 `recvUtc`（UTC）同源传给 UA 与本地快照，`UpdateValue` 简化为 `ts = max(srcUtc, lastTs+1tick)`，两侧严格同源；值差异经确认属 OPC UA 订阅正常延迟（UA 客户端按自身 `PublishingInterval`/`SamplingInterval` 收值），非网关缺陷，建议在 UA 客户端调小发布间隔；编译 0 警告 0 错误。
> - **V1.7.0（UA 设置区 UI 调整，2026-07-15）**：版本号保持 1.7.0 不变，调整「OPC UA 服务器设置」区——监听地址与安全模式下拉框宽度统一为 140；标签「端口」→「端口号」、「最大会话数」→「连接数」且二者左对齐；「自动接受客户端证书」移至安全模式下方独立行；删除「当前安全配置为开放模式，生产环境建议启用加密」警告标签及 `UpdateSecurityWarning` 逻辑；右侧空余区新增「已连接客户端」列表（`GetConnectedClients()` 经 `IServerInternal.SessionManager.GetSessions()` 读取活动会话名称，定期刷新、停止时清空）；分组高度 130→150；编译 0 警告 0 错误。
> - **V1.8.0（2026-07-16）**：升级版本号至 1.8.0（三个 `.csproj` 统一）。移除 V1.7.0 新增的「已连接客户端」列表功能——删除 `MainForm` 客户端列表控件与 `RefreshConnectedClients()` 定时刷新逻辑、`GatewayOpcUaServer.GetConnectedClients()` 及 `GatewayServer.ServerInternalAccess` 属性、`IGatewayOpcUaServer.GetConnectedClients()` 接口声明；OPC UA 设置区布局恢复紧凑。**启动卡顿修复：** 启动网关后窗口「未响应」已定位并修复。根因为 `GatewayNodeManager.AddVariableNode` 在每次创建变量节点时调用 `Diag()`，经 `OnStatusChanged`→`_log.Append`→`BeginInvoke` 向 UI 线程投递数万次日志更新（`LogManager.UpdateTextBox` 每次 O(文本长度)、累计 O(n²)），3.5 万节点场景导致约 7 分钟卡死；该 `Diag` 路径独立于 `DataBridge` 已节流的 `Log`，此前排查时未被覆盖。已移除该逐节点诊断调用，节点创建仍走 O(1) 的 `AddPredefinedNode`（经反编译 `Opc.Ua.Server.dll` 1.5.378.145 确认其仅做 `PredefinedNodes` 字典注册，无逐节点通知/地址空间重建）；`DataBridge.Start()` 进度日志维持每 ~5% 节流。编译 0 警告 0 错误。

- **V2.0.0（2026-07-21）**：升级版本号至 V2.0.0（三个 `.csproj` 同步）。**DA 标签真实数据类型获取**：浏览阶段通过临时 OPC DA Group 调用 AddItems，从 OpcDaItem.CanonicalDataType 提取实际数据类型（Integer、Float、Boolean 等），替代原有的固定 Variant 描述；分批处理（每批 500 个点位），失败时静默回退。编译 0 警告 0 错误。
> - **V2.2.0（2026-07-24）**：全面代码审查修复——String 类型标签显示修复、CSV 6 列格式统一、按钮导航移除、接口抽象与单元测试（14/14 通过）；编译 0 警告 0 错误。
> - **V2.3.0（2026-07-26）**：安全增强与代码质量优化——授权码 AES-256-CBC 加密存储、OnValuesChanged _disposedInt 防护、ConvertValue 日志告警、ConfigManager 锁策略拆分、定时器泄漏修复、SafeInvoke 死锁修复、TryReconnect 竞态修复、FormClosing 简化、AutoStartManager 分离、BuildUI 拆分为 9 子方法、xUnit1031 警告修复、HealthSnapshot 缓存 Process、常量统一、去除冗余注释；编译 0 错误 0 警告，测试 56/56 通过。
> - **V2.4.0（2026-08-01）**：试用累计运行时间使用 Windows DPAPI 持久化；自动启动收敛为单一 UI Timer；Sync 模式重连保持；同步 Read 增加重入门禁与 COM 安全释放；清理 `_gridTags` 既有警告；三个项目版本统一为 2.4.0；Debug/Release 0 警告 0 错误，测试 66/66 通过。
- **V1.9.0（2026-07-20）**：升级版本号至 V1.9.0（三个 `.csproj` 同步）。**全面代码审查 + 启动卡顿最终修复 + DA模式切换**：① DataBridge.StartAsync 异步启动（Task.Run 后台线程创建节点 + SynchronizationContext.Post 进度回调），3.5 万节点场景窗口保持响应；② 新增 DA 数据获取方式选择（异步订阅/同步轮询），UI 下拉框 + 配置持久化；③ 首次运行默认填充 ProgId Matrikon.OPC.Simulation.1，开箱即用；④ 未选择服务器时禁用「获取点位」「启动网关」按钮；⑤ Boolean 类型转换增强；⑥ SourceTimestamp 单调递增修复；⑦ Dispose 后重连检查、Monitor.Exit 安全检查、SafeInvoke 句柄防护；⑧ ConfigManager 实现 IDisposable；⑨ 版本号 1.5.0 → 1.9.0；⑩ 删除 PLAN.md。编译 0 警告 0 错误。

---

## 2. 已经完成的修改

### V2.4.0 安全增强与稳定性优化（2026-08-01）

| 操作 | 净减 |
|---|---|
| 授权码 AES-256-CBC 加密存储 | 新增 EncryptAuthCode/DecryptAuthCode + GetEncryptionKey |
| OnValuesChanged _disposedInt 防护 | +3 行 |
| ConvertValue 空 catch 日志 | +3 行 |
| ConfigManager 锁策略拆分 | 重构 DoSave → DoSaveCore/DoSave |
| WatchdogManager 定时器泄漏修复 | +2 行 |
| LogManager 文件重建降级 | +3 行 |
| SafeInvoke BeginInvoke 死锁修复 | 1 字符修改 |
| TryReconnect 竞态修复 | 重构 ~30 行 |
| FormClosing 标志位简化 | 移除 2 个标志位 |
| AutoStartManager 分离 | 新建 ~80 行，ConfigManager 减 ~70 行 |
| MainForm BuildUI 拆分 9 子方法 | 从 597 行 → 9 个方法 |
| 修复 xUnit1031 警告 | 3 处 Task.WaitAll → await Task.WhenAll |
| HealthSnapshot 缓存 Process | +5 行 |
| 硬编码字符串常量统一 | 替换 ~15 处 |
| 去除冗余修复注释 | 清理 12 个文件 |
| **编译验证** | **0 错误 0 警告** |
| **单元测试** | **56/56 通过** |

### ponytail-review（过度工程清理）

| 操作 | 净减 |
|---|---|
| 删除 `GatewayFactory.cs`（56 行），工厂调用改为直接 `new` | -56 |
| `HealthSnapshot` 嵌套类移至 `Models/`；内存缓存替代文件重读 | -89 |

### ponytail（代码精简 8 项）

| 项目 | 操作 | 净减 |
|---|---|---|
| R1 删 GateController | 删除 `Services/GateController.cs`（150 行），事件透传反模式根除 | -150 |
| R2 提取 LicenseManager | 新建 `Services/LicenseManager.cs`，封装授权/试用/事件通知 | -100 |
| R3 SafeInvoke | MainForm 提取 `SafeInvoke(Action)` 方法，替换 4 处重复模式 | -30 |
| R4 DataBridge 裁剪 | 删 `_cachedDisplayName` 字典、`TagSnapshot.Equals/GetHashCode`、7 条诊断日志 | -35 |
| R5 LogManager 减脂 | 删注释块、字段注释、死 catch（`OperationCanceledException`） | -80 |
| R6 HealthSnapshot 内联 | 内联 `CountStatusFlips`/`TrimDailyCache`/`RewriteDailyFile`/`ComputeGrowthRate` | -60 |
| R7 诊断 DEBUG 包裹 | `Diag()` 标记 `[Conditional("DEBUG")]`，Release 自动移除 | -30 |
| **合计** | | **≈ -485 行** |

---

## 3. 关键文件

| 文件 | 状态 | 说明 |
|---|---|---|
| MainForm.cs | 已修改 | 授权委托 `LicenseManager`；事件直连 `_gatewayMgr`；SafeInvoke 通用化 |
| Services/GatewayManager.cs | 已修改 | 新增 `RunningStateChanged` 事件；移除 `_factory` 字段 |
| Services/LicenseManager.cs | 新增 | 独立授权管理器：PCID 生成、授权码验证、Stopwatch 试用、状态事件 |
| Services/DataBridge.cs | 已修改 | 删除 `_cachedDisplayName` 字典和未使用的 `Equals`/`GetHashCode` |
| Services/LogManager.cs | 已修改 | 注释减脂；删除死 catch |
| Services/HealthSnapshot.cs | 已修改 | 内联 4 个私有方法；注释 90→30 行 |
| Models/SnapshotData.cs | 已存在 | 从 HealthSnapshot 嵌套类移出 |
| Models/DailySummary.cs | 已存在 | 从 HealthSnapshot 嵌套类移出 |
| Services/GatewayFactory.cs | 已删除 | YAGNI — 工厂层只有默认实现且被强转绕过 |
| Services/GateController.cs | 已删除 | 事件透传反模式，5 个事件均为 `=> OtherEvent?.Invoke(...)` |

---

## 4. 不能动的边界

| 文件 | 原因 |
|---|---|
| GatewayOpcUaServer.cs | OPC UA 服务器核心逻辑，修改风险极高（V1.6.3 仅在 `UpdateValue` 内调整 SourceTimestamp 生成策略，未触及节点/订阅核心） |
| OpcDaClient.cs | OPC DA COM 客户端，线程安全和生命周期管理敏感（V1.6.0 仅在 `Start` 内按模式分支、V1.6.2 临时诊断，均为外科手术式改动，未触动 COM 连接/释放时序） |
| ConfigManager.cs | 配置管理，未纳入优化范围 |
| Services/WatchdogManager.cs | 看门狗进程管理，仅通过事件交互 |
| Models/LicenseAlgorithm.cs | 授权算法（PCID + HMAC），修改导致已授权用户失效 |
| Theme.cs / AppConstants.cs | 设计系统常量，与代码优化无关 |
| Watchdog/ 目录 | 看门狗独立项目 |
| Keygen/ 目录 | 授权码生成工具 |

---

## 5. 已经否掉的方案

| 方案 | 否定原因 |
|---|---|
| 工厂模式（GatewayFactory） | 只有默认实现，`StartAsync` 内强转回具体类型，抽象被完全绕过 |
| GateController 中间层 | 5 个事件均为透传，零业务逻辑 |
| DataBridge 接口全量保留 + 强转 | "半吊子抽象"不如不用 |
| TagSnapshot 值语义（Equals/GetHashCode） | 全项目无使用场景 |
| LogManager `WriterLoop` 内联注释 | 注释量：代码量 ≈ 2:1，冗余 |
| 诊断日志全量保留 | 生产环境无法操作，仅调试期有用 |

---

## 6. 已经跑过的命令和测试结果

| 命令 | 结果 |
|---|---|
| `dotnet build OpcDaToUaGateway.csproj -c Debug` | 0 警告 0 错误（R1-R7 每轮修改后均执行） |
| 文件存在性检查 | `GateController.cs`、`GatewayFactory.cs` 已物理删除 |
| 引用搜索 | 全项目 0 引用已删除文件 |
| Watchdog/Keygen 编译 | 通过 `.csproj` Target 自动触发，编译成功 |

**注意：未执行运行时测试**（无 OPC DA 环境）。编译通过 ≠ 运行时行为正确。

---

## 7. 当前风险点

| 风险 | 等级 | 说明 |
|---|---|---|
| 事件订阅泄漏 / 跨线程异常 | ✅ 已解决(V1.5.1) | `RunningStateChanged` 补 `SafeInvoke` 封送；`SafeInvoke` 增加 `IsDisposed/IsHandleCreated` 防护 |
| SafeInvoke 死锁 | ✅ 已缓解 | 保持 `Invoke`（与 `LogManager` 一致），增加句柄防护规避关闭期 `ObjectDisposedException` |
| 频繁翻转 bool 标签不更新 | ✅ 已解决(V1.6.3) | `UpdateValue` 单调时间戳下限改为网关 UTC 时钟；快照时间戳改用网关接收时刻 |
| 未运行时测试 | 🔴 高 | OPC DA 实际环境仍待验证（沙箱无 DA 服务器）；逻辑已静态核对，编译 0 警告 0 错误 |
| [Conditional("DEBUG")] 排障信息丢失 | 🟡 中（设计取舍） | R7 有意为之，Release 模式移除诊断日志 |

---

## 8. 下一步计划

**当前无明确待办项。** PLAN.md 第 3 节（未来规划）6 个方向：

| 方向 | 状态 |
|---|---|
| 3.1 接口抽象与单元测试基础设施 | 已完成 |
| 3.5 长期内存监控 | 已完成 |
| 3.2 遥测历史（JSON Lines 持久化） | 待评估 |
| 3.3 告警历史 | 待评估 |
| 3.4 脚本钩子 | 待评估 |
| 3.6 配置校验 | 待评估 |

**建议**：
1. 在实际 OPC DA 环境中启动 V1.5.0，验证事件链路
2. 如需进一步优化：优先评估 3.2（遥测历史），health/ 目录已有基础
3. 如需新增功能：按后续指令执行
