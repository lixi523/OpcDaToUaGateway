# Handoff - OPC DA to UA Gateway (OpcDaToUaGateway) 项目交接文档

> **生成时间：** 2026-11-03
> **当前版本：** V2.8.0（主程序 / Watchdog / Keygen 三个 csproj 统一 2.8.0）
> **编译状态：** 0 警告 / 0 错误 ✅（Debug + Release）
> **测试状态：** 100/100 通过 ✅（原有 78 + 本次新增 22）
> **项目目录：** `D:\Documents\Code\OpcDa2Ua`
> **技术栈：** .NET Framework 4.7.2（**x86**）、WinForms、Costura.Fody 单 exe 打包；DA 客户端 `TitaniumAS.Opc.Client` 1.0.2，UA 服务端 `OPCFoundation.NetStandard.Opc.Ua.Server` 1.5.378.145
>
> ⚠️ **本文档取代了 V1.8.0–V2.7.0 的历史版 handoff.md**。旧版可从 git 恢复：`git show HEAD:handoff.md`。

---

## 1. 项目目标

`OpcDaToUaGateway` 是面向工业现场部署的 OPC DA → OPC UA 协议转换网关：

- **核心功能**：实时读取 OPC DA 标签，映射为 OPC UA 节点，供数据采集系统通过 UA 获取
- **质量要求**：7×24 小时稳定运行，0 天不宕机
- **关键约束**：面向工业现场部署，**UI 修改 = 现场修改**，任何 UI 线程阻塞都会造成生产事故

---

## 2. 当前进度

本会话（2026-11-03）处理了 5 个用户提出的问题 + 1 次升版，**全部已实现、编译 Debug/Release 0 警告 0 错误、测试 100/100，均未提交 git**。

| # | 问题 | 性质 | 状态 |
| --- | --- | --- | --- |
| 1 | 大规模（>1 万点位）扫描 DA 标签时，点位浏览窗口「未响应」 | 真实 Bug（UI 日志洪泛） | ✅ 已修复 |
| 2 | 标签扫描太慢 / 长时间看不到点位（>1 万） | 体验 + 性能 | ✅ 边扫描边显示 + 去冗探测 |
| 3 | `pSpace.OPCServer` 扫描到 0 个点位（服务器实有 3.5 万标签） | 真实 Bug（浏览策略/接口不兼容） | ✅ 已加多级兜底，**待现场复核** |
| 4 | OPC UA 客户端扫不到 UA 标签（UaExpert 只见 ns=0 节点） | 真实 Bug（UA 单文件夹子节点过多） | ✅ 已改为分支统一分组 `Batch_NNN` |
| 5 | 试用 30 分钟到期后「打不开软件」，无法进「关于」授权 | 真实 Bug（到期直接退出 + 授权后未恢复按钮） | ✅ 已修复 |
| 6 | 版本升级 | 升版 | ✅ 三个 csproj → V2.8.0，README/STATUS/故障恢复预案/handoff 同步 |

---

## 3. 已完成修改

### 3.1 大规模 DA 标签浏览「未响应」（`ItemSelectionDialog.cs` + `Services/LogManager.cs`）

**现象**：点位浏览对话框（点「获取点位」后）在整个浏览期间显示「未响应」。

**根因**：`ItemSelectionDialog.BeginBrowse` 的浏览回调把**每一条**诊断消息经 UI 线程封送两次——
`_logger?.Invoke(...)`（=`MainForm._log.Append` → `LogManager.UpdateTextBox`，每次 `AppendText` 为 O(文本长度)、累计 O(n²)）+ `SafeBeginInvoke(...)`。
大规模服务器浏览会输出**每个分支一条摘要 + 每 2000 点位进度 + 每 500 点位数据类型批次**，累计数千至上万条，把 UI 线程占满。与项目历史根因（V1.7.0 已连接客户端列表、V1.8.0 逐节点 `Diag`）同型（见 `docs/OPC_DA转UA网关开发指南.md` L353）。

**修复**：
- `LogManager` 新增 `AppendFileOnly(string)`：**仅写文件日志、完全不触碰 UI 文本框**；抽出 `EnqueueFileLine` 复用队列写入与队列满告警（补 `ObjectDisposedException` 捕获，注意其派生自 `InvalidOperationException`，catch 顺序须在前）。
- `ItemSelectionDialog` 新增 `internal static class BrowseLogRouter`：
  - `IsUiMilestone`（进 UI 日志）= 命中「尝试连接 OPC DA 服务器 / 连接成功 / 浏览阶段完成 / 连接失败 / 已达最大点位上限」或以 `[DCOM]` 开头；
  - `IsLabelMilestone`（同步状态栏）= 上述阶段关键字（不含 `[DCOM]`）；
  - 其余高频诊断 → 只走 `_fileOnlyLogger`（= `_log.AppendFileOnly`），**不做 UI 封送**。
- 主构造函数新增可选参数 `Action<string> fileOnlyLogger`；`MainForm.BtnFetchTags_Click` 传入 `_log.AppendFileOnly`。

**结果**：浏览期间 UI 只剩约 5 次日志 + 阶段状态栏更新，对话框保持响应；文件日志仍保留全部分支摘要，离线排查能力不变。

### 3.2 DA 标签浏览性能与「边扫描边显示」（`OpcDaClient.cs` + `ItemSelectionDialog.cs`）

**根因（经 `ilspycmd` 反编译 TitaniumAS 1.0.2 确认）**：
- `OpcDaBrowserAuto` 优先用 `OpcDaBrowser3`（`IOPCBrowse`），`GetElements` 内部按 `OpcConfiguration.BatchSize = 1024` **分页**，逐层遍历（每个分支一次 COM 往返）；
- **默认 `GetElements` 不取元素属性**（`allProperties=false`）→ `ExtractDataType` 恒返回 `Variant` → `FillRealDataTypes` 必须对**全部**点位再走一遍临时 Group 的 `AddItems`（N/500 次 COM 往返）；
- 对话框**只有等「浏览 + 数据类型填充」全结束才显示点位**，故大点位量时长时间空列表。

**修复**：
- `BrowseAllItems` 新增可选回调 `Action<IReadOnlyList<OpcDaItemInfo>> onItemsDiscovered`；`BrowseRecursive` 新增 `streamProgress`，**每积累 `AppConstants.DaBrowseStreamChunk = 1000` 个点位就把增量投递一次**（只投增量、不重复；`GetRange` 返回同一批对象引用，后续类型填充会就地更新）。
- `ItemSelectionDialog` 接线增量回调，`AppendDiscoveredItems` 边扫描边把点位追加到列表（虚拟模式更新 `VirtualListSize` + `Invalidate`），状态栏显示「正在浏览… 已发现 N 个点位」；`OnBrowseComplete` 仍以完整结果重刷（类型已填充）。
- `FillRealDataTypes` **只探测「类型仍未知」的点位**；全部已知时直接跳过临时 Group。
- 新增**分阶段耗时日志**（浏览阶段 / 数据类型填充阶段，单位 ms）与**根层统计**（`[Browse] 根层统计: 共 N 元素, X 叶子, Y 分支, Z 其他`）。

### 3.3 `pSpace.OPCServer` 扫描到 0 点位（`OpcDaClient.cs`）

**证据**：日志显示连接成功、浏览**正常返回 0 元素且不抛异常**；诊断弹窗显示 `OpcDaBrowser2 失败: HRESULT E_FAIL`、`OpcDaBrowser1 根层 1 个元素`；其它 OPC 客户端能扫到标签（结构为 `模拟站.tag1 … 模拟站.tag35000`）。

**根因**（反编译确认）：
- `TitaniumAS` 的 `OpcBrowse.Browse` 内部 `do { try{...} catch(Exception){break;} } while(...)` —— **会吞掉 COM 异常并返回空数组**；于是 `OpcDaBrowser3` 返回「空但无异常」，`OpcDaBrowserAuto` 只在**抛异常**时才回退 → 不回退。
- FLAT 命名空间服务器需要 DA2 的 `BrowseOPCItemIDs(OPC_FLAT)` 才能列出点位（OPC Foundation issue #878）；部分服务器对 `ChangeBrowsePosition(OPC_BROWSE_TO,"")` 返回 E_FAIL。

**修复（多层兜底）**：
1. 主策略拿到 **0 点位** 时，显式回退 `RootSafeBrowser2` → `OpcDaBrowser2` → `OpcDaBrowser1` 重试，并记录各策略根层元素数。
2. 新增 `private sealed class RootSafeBrowser2 : OpcDaBrowser2`——空 `itemId` 时**跳过 `ChangeBrowsePosition`**（根层本就在根位置）。
3. 放宽下钻条件：`IsItem==false` 的元素**一律尝试下钻**（不再依赖 `OPC_BROWSE_HASCHILDREN` 标志；`ItemId` 非空 + 深度 ≤ `MaxBrowseDepth=10` 保护）。部分服务器不设置该标志，旧逻辑会漏掉整棵子树。
4. 终极端兜底：仍为 0 时改用 `server.As<OpcBrowseServerAddressSpace>().BrowseOpcItemIds(OpcDaBrowseType.Flat, …)` **一次性全量枚举**（`GetItemId(name)` 取 ItemId，失败回退用名称）。

### 3.4 OPC UA 客户端扫不到 UA 标签（`GatewayOpcUaServer.cs`）

**证据**：网关启动日志 `命名空间索引: 2, 实际变量数: 35000`（节点都建了），但 UaExpert 只见标准 ns=0 节点；3.5 万变量的 `ItemId` 均为 `模拟站.tagN` → **全部直接挂在同一个 UA 文件夹 `DaTags/模拟站` 下**。

**根因**：项目 H-24-1 记录「单文件夹直接子节点过多（35000+）会触发 SDK 的 Browse 不可见边界条件」，当时只对**无分支(Flat)** 场景做了分批（`FlatTags/Batch_NNN`），**漏了「一个大分支下挂几万变量」**。

**修复**：
- `GatewayNodeManager` 新增按分支路径计数的 `_branchChildCount`；`AddVariableNode` 中新增调用 `GetOrCreateBranchBatchFolder`。
- **分支变量统一分组**到该分支的 `Batch_NNN` 子文件夹：`模拟站/Batch_000/tag1..tag1000`、`Batch_001/tag1001..tag2000`…（每批 `AppConstants.UaFlatBatchSize = 1000`）。分支文件夹本身**只保留 Batch 子文件夹**（不再有 1000 个直接子节点）。
- 新增 `SafeIdPart(path)`（保留中英文数字，其余替换 `_`）生成 Batch 文件夹 NodeId：`Folder_{safePath}_B{idx:D3}`。
- 新增诊断日志：`[诊断] 分支变量分组：模拟站 → Folder_模拟站_B000（Batch_000）`。

> 注：用户明确要求「序号 1–1000 的标签也放在组里」，故**所有有分支的标签都会多一层 `Batch_NNN`**（小分支如 `b.c/Batch_000/d` 也一样）。
> `ComputeUaBrowsePath`（导出用）**当前无任何调用者**，未同步 Batch 层级，无回归影响。

### 3.5 试用 30 分钟到期行为（`MainForm.cs`）

**现象**：到期后「无法打开软件」，进不了「关于」输入授权码。

**根因**：`MainForm.LoadConfiguration` 里的 `handleTrialExpiredAsync` 在**运行中到期**与**启动时已到期**两种情况都直接 `Close()` 退出程序；且到期退出走 `SignalGracefulExit()`，**Watchdog 仍在运行**；授权成功后也没有把「启动网关」重新启用。

**修复（按用户要求）**：
- 新增 `_licenseLocked`，`SetUiRunningState` 改为 `_btnStart.Enabled = !isRunning && !_licenseLocked;`（防止网关停止事件把按钮重新点亮）。
- **启动即已到期**：程序**正常启动**，仅禁用「启动网关」，其它功能（浏览/获取点位/关于）保持可用；提示延迟到窗口 `Shown` 后弹一次（构造函数中无窗口句柄，不能用 `BeginInvoke`/弹窗）。
- **运行中到期**：独立 `handleTrialExpiredDuringRunAsync` → 停止网关 → 提示 → `_watchdogMgr.Stop()` **关闭看门狗进程** → 退出主程序。之后用户可**重新打开**软件。
- `StatusChanged` 处理器中：`IsLicensed && _licenseLocked` → 解锁并 `ApplyLicenseLock()` 恢复「启动网关」。
- 保留 `BtnStart_Click` 里「到期则提示不可启动」的兜底判断。

### 3.6 版本升级 V2.8.0

- `OpcDaToUaGateway.csproj` / `Watchdog/OpcDaToUaGateway.Watchdog.csproj` / `Keygen/OpcDaToUaGateway.Keygen.csproj`：`Version` / `AssemblyVersion` / `FileVersion` → **2.8.0**（产物 `FileVersion=2.8.0.0`）。
- `README.md`：顶部版本行、版本历史新增 V2.8.0 条目、页脚。
- `docs/STATUS.md`：当前版本 V2.8.0 + 版本演进新增条目。
- `docs/故障恢复预案.md`：证书自动重建的「自 v2.7.1 起」订正为「自 v2.8.0 起」。

---

## 4. 关键文件

### 本次改动

| 文件 | 改动 | 说明 |
| --- | --- | --- |
| `OpcDaClient.cs` | 大量 | 浏览流式投递 + 放宽下钻 + `RootSafeBrowser2` + 多级兜底 + FLAT 枚举 + 类型探测去冗 + 阶段耗时/根层统计日志 |
| `ItemSelectionDialog.cs` | 较多 | `BrowseLogRouter` 分流、`fileOnlyLogger`、边扫描边显示、0 点位时把浏览诊断附进提示弹窗 |
| `Services/LogManager.cs` | + | `AppendFileOnly` + `EnqueueFileLine` |
| `GatewayOpcUaServer.cs` | 中 | 分支变量统一分组 `Batch_NNN`（`GetOrCreateBranchBatchFolder` / `SafeIdPart` / `_branchChildCount`） |
| `MainForm.cs` | 中 | 试用到期行为（`_licenseLocked` / `ApplyLicenseLock` / `ShowTrialExpiredNoticeOnce` / 运行中到期关闭看门狗） |
| `AppConstants.cs` | +1 | 新增 `DaBrowseStreamChunk = 1000` |
| `README.md` / `docs/STATUS.md` / `docs/故障恢复预案.md` | 文档 | 版本与变更同步 |
| `Tests/BrowseLogRouterTests.cs` | **新建** | 22 个路由用例（高频诊断不得进 UI；里程碑进 UI/状态栏） |

### 其他文件（本次未改，理解用）

| 文件 | 说明 |
| --- | --- |
| `DataBridge.cs` | `OnDaDataChanged` 取 `DateTime.UtcNow` 同源传给 UA 与快照 —— **正确，勿改**；UA 变量创建走 `AddVariableNode` |
| `Models/TagConfig.cs` | `OpcUaConfig`、`GetEffectiveListenAddress()`、`NamespaceUri` **必须非空**（否则 `GatewayNodeManager` 构造抛异常 → `BadInternalError 'Unexpected error starting application'`） |
| `Services/WatchdogManager.cs` | `Stop()` = 完全终止看门狗进程；`SignalGracefulExit()` = 仅告知不重启（看门狗保持运行） |

---

## 5. 不能动的边界

| 文件 / 模块 | 原因 |
| --- | --- |
| `GatewayOpcUaServer.cs` 内 `UpdateValue` / 订阅 / 节点管理核心 | UA Server 线程安全，修改风险极高（本次仅在**文件夹创建/分组**处改动） |
| `OpcDaClient.cs` COM 连接/断开逻辑 | OPC DA COM 客户端，线程模型复杂，连接/超时/释放时序敏感 |
| `ConfigManager.cs` 配置逻辑 | 影响稳定性 |
| `Services/WatchdogManager.cs` | 看门狗进程管理 |
| `Models/LicenseAlgorithm.cs` | 授权算法（PCID + HMAC），修改会导致历史授权失效 |
| `Theme.cs` / `AppConstants.cs` | 全局样式常量 |
| `Watchdog/` / `Keygen/` 目录 | 独立子项目；Keygen 严禁进客户发布包（等同破解授权） |
| `Services/Interfaces/IHealthSnapshot.cs` | 健康快照接口 |

**本会话额外确立的约束**：

- `TagSnapshot.Timestamp` 内存中**必须保持 UTC** —— UA `SourceTimestamp` 规范要求，本地化只允许在显示层
- 项目 `PlatformTarget` 为 **x86**；外部工具/测试程序集编译须匹配平台，否则 `BadImageFormatException`
- 主 exe 经 Costura 合并，**不能被直接引用**；需通过 Tests 项目引用
- `LogManager.Append` 的 UI 更新是 O(文本长度)；**任何高频日志都必须走 `AppendFileOnly`**，否则会重演 UI「未响应」

---

## 6. 已经否掉的方案

| 方案 | 否决原因 |
| --- | --- |
| 浏览日志全部保留在 UI 运行日志（只减少输出频率） | 治标；分支摘要本身有价值，改走文件通道更彻底 |
| 大规模浏览时对未标记 `HasChildren` 的分支“不”下钻 | 已在 3.3 反其道行之——实测部分服务器不设置该标志，必须下钻 |
| 用 `RootSafeBrowser2` 替代主策略 `OpcDaBrowserAuto` | Browser3(`IOPCBrowse`) 对多数服务器更优；仅在主策略返回 0 时兜底使用 |
| 把「有分支的大文件夹」首批并入 `Batch_000` 但保留小分支直挂 | 需在超阈值时迁移已建子节点，实现复杂、风险高；改为**统一分组**（用户已接受） |
| 试用到期直接退出程序（原实现） | 导致无法重开、无法进「关于」授权；改为「只关闭进程 + 未授权禁用启动按钮」 |

---

## 7. 当前风险点

### 🔴 待现场复核

| 风险 | 说明 |
| --- | --- |
| **`pSpace.OPCServer` 兜底方案未在本机实测** | 本机无该服务器；`RootSafeBrowser2`/放宽下钻/`OPC_FLAT` 枚举三条兜底是否真正扫到 3.5 万点位，需用现场环境验证（诊断弹窗会打印各策略根层元素数与 FLAT 枚举数量） |
| **UA 分支统一分组后的浏览结构变化** | 所有有分支标签都多一层 `Batch_NNN`；需确认现场 SCADA 的 NodeId/浏览路径配置随之调整（NodeId 仍是 `DaTag_{tagKey}`，未变） |

### 🟡 设计权衡（已知，可接受）

| 风险 | 说明 |
| --- | --- |
| 分支/扁平分批阈值均为 1000 | 单文件夹 ≤1000 子节点实测可被客户端 Browse；阈值过大有不可见风险，过小结构碎片化 |
| `ComputeUaBrowsePath` 未同步 Batch 层级 | 当前无调用者；若将来启用导出 BrowsePath，需补 `Batch_NNN` |
| `FillRealDataTypes` 仍对未知类型点位全量探测 | 默认浏览不带属性，故通常仍需探测；若后续改为浏览带属性可整段跳过 |

### 🟡 历史遗留（本次未处理）

| 风险 | 说明 |
| --- | --- |
| `ItemSelectionDialog` 右键会触发行勾选切换 | MouseDown/MouseUp 未检查 `e.Button`；右键点行会静默改变导入内容 |
| 拖拽会切换错误的行 | `MouseUp` 沿用 `MouseDown` 的 ItemId，未重新命中行 |
| 证书主题与监听地址耦合 / 自动重建可能销毁有效证书 | 见历史版 handoff（`git show HEAD:handoff.md`）§7；仍未拍板 |

---

## 8. 已经跑过的测试

| 测试 / 验证 | 结果 |
| --- | --- |
| `dotnet build OpcDaToUaGateway.sln -c Debug` | 0 警告 / 0 错误 ✅ |
| `dotnet build OpcDaToUaGateway.sln -c Release` | 0 警告 / 0 错误 ✅ |
| `dotnet test Tests/OpcDaToUaGateway.Tests.csproj` | **100/100 通过** ✅ |
| ├─ 原有测试（78） | 78/78 ✅ 无回归 |
| └─ `BrowseLogRouterTests`（新增 22） | 22/22 ✅（高频诊断→文件、里程碑→UI、`[DCOM]` 只进日志不进状态栏） |
| 反编译核实（TitaniumAS 1.0.2） | `OpcBrowse.Browse` 吞异常返回空；`OpcDaBrowserAuto` 优先 Browser3；默认 `GetElements` 不带属性；`OpcConfiguration.BatchSize=1024` ✅ |

> **未执行**：需现场 `pSpace.OPCServer`（3.5 万标签）+ UaExpert 的运行时验证；长时间稳定性测试。

---

## 9. 下一步计划

### 待用户拍板 / 现场验证

| 事项 | 说明 |
| --- | --- |
| `pSpace` 扫描结果复核 | 用新 exe 对 `pSpace.OPCServer` 重扫，确认扫到 3.5 万点位；若仍 0，把诊断弹窗截图发来 |
| UA 标签复核 | UaExpert 展开 `Objects → OPC DA Tags → 模拟站`，确认 `Batch_000`(1–1000) … `Batch_034` |
| 试用到期复现 | 跑满 30 分钟观察：网关停止 + 看门狗进程消失 + 程序退出；重开仅「启动网关」置灰；「关于」授权后恢复 |
| 提交/发布 | 工作区改动尚未提交 git；是否 `git commit`（如 `release: V2.8.0`） |

### 建议优先修（技术债）

1. `ItemSelectionDialog` 右键误切换 / 拖拽切换错误行（历史遗留）
2. `ComputeUaBrowsePath` 同步 Batch 层级（若启用导出 BrowsePath）
3. 补齐 `pSpace`/FLAT 服务器浏览的自动化测试（需 DA 服务器模拟）

---

## 10. 新窗口启动提示词

> 复制以下内容到新对话窗口即可接续：
>
> 我是 OPC DA→UA 协议转换网关（OpcDaToUaGateway）维护助手。项目目录：**`D:\Documents\Code\OpcDa2Ua`**
>
> 先读取 **`handoff.md`**（交接文档，含 10 节结构），必要时再读 `docs/OPC_DA转UA网关开发指南.md`、`docs/STATUS.md`。
>
> **当前状态（2026-11-03，V2.8.0，未提交 git）**：
> - Debug/Release 0 警告 0 错误；测试 **100/100**（原 78 + `BrowseLogRouterTests` 22）
>
> **本会话解决的问题**：
> 1. 大规模浏览 DA 标签「未响应」→ 浏览诊断日志分流（高频只落文件，`LogManager.AppendFileOnly`）
> 2. 浏览慢/看不到点位 → 边扫描边显示（每 1000 点位增量投递）+ 类型探测去冗 + 阶段耗时日志
> 3. `pSpace.OPCServer` 扫描 0 点位 → 放宽下钻 + `RootSafeBrowser2` + `Browser2/Browser1` 兜底 + `OPC_FLAT` 全量枚举
> 4. UA 客户端扫不到标签 → 分支变量统一分组到 `Batch_NNN`（避免单文件夹 3.5 万子节点不可见）
> 5. 试用到期行为 → 到期只关闭主程序与 Watchdog、可重开，未授权禁用「启动网关」，授权后恢复
>
> **重要的 API / 行为事实（实测或反编译确认，勿凭记忆推翻）**：
> - `TitaniumAS.OpcBrowse.Browse` 内部 `catch(Exception){break;}` **吞异常返回空数组** → `OpcDaBrowserAuto` 在「空但不抛异常」时**不回退**
> - `OpcDaBrowserAuto` 优先 `OpcDaBrowser3`(`IOPCBrowse`)；`OpcConfiguration.BatchSize=1024` 分页；默认 `GetElements` **不取元素属性** → `ExtractDataType` 恒 `Variant`
> - FLAT 命名空间服务器需 DA2 `BrowseOPCItemIDs(OPC_FLAT)`（OPC Foundation issue #878）
> - 部分服务器对 `ChangeBrowsePosition(OPC_BROWSE_TO,"")` 报 E_FAIL；`OpcDaBrowser1` 根层用 `OPC_BROWSE_UP` 回根不受影响
> - UA 单文件夹直接子节点过多 → 客户端 Browse 不到（H-24/H-24-1）；分批阈值 `UaFlatBatchSize = 1000`
> - `LogManager.UpdateTextBox` 每次 O(文本长度)；**高频日志必须走 `AppendFileOnly`**
> - 项目是 **x86**；主 exe 经 Costura 合并不能被直接引用
>
> **不能动的边界**：`GatewayOpcUaServer.cs` 节点/订阅/`UpdateValue` 核心、`OpcDaClient.cs` COM 连接/断开、`ConfigManager.cs`、`WatchdogManager.cs`、`Models/LicenseAlgorithm.cs`、`Theme.cs`/`AppConstants.cs`、`Watchdog/`、`Keygen/`、`IHealthSnapshot.cs`。另 `TagSnapshot.Timestamp` 内存中必须保持 UTC。
>
> 不要擅自修改代码，除非用户明确要求。遵循用户指令。

---

*本 handoff.md 由当前会话生成，2026-11-03。历史版（V1.8.0–V2.7.0）可经 `git show HEAD:handoff.md` 取回。*