# OPC DA → OPC UA 网关（OpcDaToUaGateway）

![Build](https://github.com/lixi523/OpcDaToUaGateway/actions/workflows/build.yml/badge.svg)

> 版本：**V2.7.0** ｜ 协议转换网关：将 OPC DA 数据源实时映射为 OPC UA 服务器，供上位 SCADA/MES/工业平台订阅。

---

## 1. 项目简介

OpcDaToUaGateway 是一个 .NET Framework 4.7.2 协议转换网关，将 **OPC DA（COM/DCOM）** 数据源实时映射为 **OPC UA 服务器**（基于 Open62541 .NET）。上位 SCADA/MES/工业平台通过标准 OPC UA 订阅接口（`opc.tcp://<IP>:4840`）获取 DA 点位数据，无需改造现有 DA 系统。

---

## 2. 快速开始

### 2.1 前置条件

- Windows 10/11（x64）
- .NET Framework 4.7.2 Runtime
- OPC DA 服务器（如 Matrikon OPC Simulation Server、Siemens OPC DA Server 等）
- Visual Studio 2022（编译用，可选）

### 2.2 编译运行

```bash
# 编译
dotnet build OpcDaToUaGateway.sln -c Release -v minimal

# 或 MSBuild
msbuild OpcDaToUaGateway.sln /p:Configuration=Release /t:Rebuild
```

### 2.3 部署步骤

1. 将 `bin/Release/net472/` 下所有文件复制到目标机器
2. 编辑 `config.json` 配置 DA 服务器 ProgId 和 UA 监听端口
3. 运行 `OpcDaToUaGateway.exe`
4. 用 UA 客户端连接 `opc.tcp://<本机IP>:4840` 订阅数据

---

## 3. 核心功能

| 功能 | 说明 |
| --- | --- |
| OPC DA 浏览 | 自动遍历 DA 服务器层级结构，获取标签名、路径、数据类型 |
| UA 地址空间映射 | 按 DA 层级自动创建 VariableNode，保持相同树形结构 |
| 实时数据同步 | 支持异步订阅（推荐）和同步轮询两种模式 |
| 授权管理 | 内置 License 机制，到期前提示 |
| 看门狗 | Watchdog 子进程守护主程序，崩溃自动重启 |
| 密钥生成器 | Keygen 工具生成授权码 |

---

## 4. 配置说明

`config.json` 示例：

```json
{
  "DaServer": {
    "ProgId": "Matrikon.OPC.Simulation.1",
    "Host": "",
    "BrowseRoot": ""
  },
  "UaServer": {
    "ListenAddresses": ["opc.tcp://0.0.0.0:4840"],
    "SecurityMode": "None",
    "MaxSessionCount": 50,
    "SessionTimeout": 120000
  },
  "DataBridge": {
    "Mode": "Async",
    "SamplingInterval": 1000,
    "MaxTagCount": 50000
  },
  "LastConnectedProgId": ""
}
```

---

## 5. 架构概览

```
┌─────────────┐     ┌──────────────────┐     ┌──────────────────┐
│  OPC DA     │────▶│  OpcDaToUaGateway │────▶│  OPC UA Clients  │
│  Server     │     │                  │     │  (SCADA/MES/etc) │
└─────────────┘     └──────────────────┘     └──────────────────┘
                       ┌────────┬───────┬──────────┐
                       │Browse  │Bridge │ UA Server│
                       │Module │ Module│  Module  │
                       └────────┴───────┴──────────┘
```

详细架构说明见 [`docs/OPC_DA转UA网关开发指南.md`](docs/OPC_DA转UA网关开发指南.md)。

---

## 6. 版本历史

| 版本 | 日期 | 变更摘要 |
| --- | --- | --- |
| **V2.7.0** | 2026-09-17 | **V2.6.0 代码审查修复收尾 + 版本升级**：① H1 质量位修复，`OpcDaClient` 改用 `OpcDaQualityMaster` 数据质量位判定 Good/Bad，避免 BAD 质量被误标为 Good；② H2 DI 注入修复，`GatewayManager` 不再用 `as` 具体类型转型破坏接口注入；③ H3 看门狗心跳事件重启后周期性重试 `OpenExisting`，避免守护进程先启动时心跳检测永久失效；④ H4/M3 日志错配修复，`WatchdogManager` 收窄异常并修正日志文案；⑤ H5/L1/L2 `DataBridge` 订阅先 `-=` 再 `+=`，空标签 `StartAsync` 行为与 `Start` 统一；⑥ M1 常量清理，恢复 `AppConstants` 中相关常量为唯一来源并替换硬编码；⑦ M2/M4/M5/M6/L3-L7 完成节点上限诊断日志、UI 实时列、授权说明、版本单一来源等修复。UI 修复：OPC DA 浏览窗口虚拟列表复选框鼠标点击勾选/取消不生效（`ItemCheck` 后刷新已显示行）；主界面“关于...”按钮文本还原。文档：开发指南等移入 `docs/`，新增 V2.6.0 审查报告，移除根目录旧文档。Debug 0 警告 0 错误，67/67 测试通过。 |
| **V2.6.0** | 2026-09-15 | **代码审查报告 P0/P1 修复 + Keygen 目录仓库移除 + 版本升级**：① DataBridge 订阅幂等（Start/StartAsync 前先 -=）与 StartAsync null 安全取值；② OpcDaClient 批量重建字典 O(n²)→锁外预构建+锁内原子替换引用，readonly 字段改可赋值；③ OnValuesChanged 批次内异常限流（仅首条+计数）；④ Cleanup 超时分支由永不超时的 int.MaxValue 改为有界 30s 并直接释放 COM 资源，不再排队挂起工作项；⑤ Program 异常处理器空 catch 改为记录 Debug 二次异常；⑥ LogManager 空 catch 注释修正；⑦ DataBridge _cachedTypes 改 ConcurrentDictionary；⑧ Keygen/ 源码目录从仓库移除（本地保留，.gitignore 防止重新纳入），同步更新 csproj/sln/文档。Debug/Release 0 警告 0 错误，67/67 测试通过。 |
| **V2.5.0** | 2026-08-06 | **代码审查修复（第二轮）**：修复 GatewayManager.StartAsync 构造函数注入失效（局部变量覆盖字段）；EffectiveMaxReconnectAttempts 快照消除热更新TOCTOU；HealthSnapshot.RotateOldSnapshots 移到锁外；ConfigManager 临时文件改用GUID随机名；Program.cs 异常处理器前移到Bootstrap初始化之前并防护MessageBox二次异常；AutoStartManager WScript.Shell null 友好错误；测试修复：5处同步方法误用ThrowsAnyAsync改为ThrowsAny、DeriveKey断言优化、Task.Delay时序测试改用ManualResetEventSlim。Debug/Release 0 警告 0 错误，67/67 测试通过。 |
| **V2.4.0** | 2026-08-01 | **安全增强与稳定性优化**：授权码加密存储；试用累计运行时间使用 Windows DPAPI 持久化；自动启动收敛为单一 WinForms Timer；OPC DA 同步采集重连后保持 Sync 模式；同步读取增加单读取门禁与安全释放。当前测试 66/66 通过，0 警告 0 错误。 |
| **V2.2.0** | 2026-07-23 | **全面代码审查修复**：String 类型标签显示修复、CSV 格式统一、按钮导航移除、图标更新、接口抽象与单元测试。编译 0 警告 0 错误。 |
| **V2.0.0** | 2026-07-21 | **DA 标签真实数据类型获取**：浏览阶段通过临时 OPC DA Group 的 `CanonicalDataType` 提取实际数据类型；分批处理，失败时回退。编译 0 警告 0 错误。 |
| V1.9.0 | 2026-07-20 | **全面代码审查 + 启动卡顿最终修复 + DA模式切换**：① DataBridge.StartAsync 异步启动（`Task.Run` 后台线程创建节点 + SynchronizationContext.Post 进度回调），3.5 万节点场景窗口保持响应；② 新增 DA 数据获取方式选择（异步订阅/同步轮询），UI 下拉框 + 配置持久化；③ 首次运行默认填充 ProgId `Matrikon.OPC.Simulation.1`，开箱即用；④ 未选择服务器时禁用「获取点位」「启动网关」按钮；⑤ Boolean 类型转换增强（支持字符串 "true"/"1"/"yes" 等）；⑥ SourceTimestamp 单调递增修复（bool 翻转标签可被 UA 客户端正确检测）；⑦ Dispose 后重连检查、Monitor.Exit 安全检查、SafeInvoke 句柄防护；⑧ ConfigManager 实现 IDisposable；⑨ 版本号 1.5.0 → 1.9.0；⑩ 删除 PLAN.md（文档整合完成）。编译 0 警告 0 错误。 |
| V1.8.1 | 2026-07-17 | **启动卡顿最终修复**：3.5 万次 `AddVariableNode` 移至后台线程（`Task.Run`），UI 线程通过 `SynchronizationContext.Post` 安全输出进度日志。真实环境验证窗口保持响应。 |
| V1.8.0 | 2026-07-16 | 移除 V1.7.0 新增的「已连接客户端」列表功能；修复启动窗口「未响应」卡顿根因（逐节点 `Diag()` 导致 O(n²) 日志洪泛）。编译 0 警告 0 错误。 |
| V1.7.0 | 2026-07-15 | 定稿发布：修复 3 个运行时缺陷（`SafeBeginInvoke` 句柄防护、UTC 时间戳显示转换、DA 质量判定修正 `value.Quality.Status & 0xC0`）；UA 设置区 UI 调整。 |
| V1.6.0 | — | 新增 OPC DA 同步/异步获取模式。 |
| V1.5.1 | — | 修复 `RunningStateChanged` 跨线程异常、`SafeInvoke` 封装与缺句柄防护。 |

---

## 7. 文档索引

| 文档 | 用途 | 读者 |
| --- | --- | --- |
| [`docs/使用文档.md`](docs/使用文档.md) | 安装部署、配置、操作、授权、日志、FAQ | 现场实施/运维 |
| [`docs/故障恢复预案.md`](docs/故障恢复预案.md) | 9 类故障场景识别与恢复步骤 | 运维/值班 |
| [`docs/OPC_DA转UA网关开发指南.md`](docs/OPC_DA转UA网关开发指南.md) | 架构、模块、版本演进史 | 开发者 |
| [`handoff.md`](handoff.md) | 交接说明与边界约定 | 接手开发者 |
| [`docs/STATUS.md`](docs/STATUS.md) | 当前状态与风险（含版本历史、编译状态、风险点） | 团队 |
| [`docs/本地编译步骤.md`](docs/本地编译步骤.md) | 本机/CI 编译实操 | 构建负责人 |
| [`docs/代码审查报告_V2.6.0.md`](docs/代码审查报告_V2.6.0.md) | V2.6.0 全项目代码审查报告（H1-H5/M1-M6/L1-L7） | 开发者 |

---

## 8. 发布包安全说明

> **⚠️ 重要：客户发布包中不包含 Keygen 授权码生成工具**

- Keygen (`OpcDaToUaGateway.Keygen.exe`) 为内部运维工具，**严禁**随客户部署包分发
- 若客户获取 Keygen，可自行生成合法授权码，**等同于绕过整个商业授权体系**
- CI 流水线已配置白名单打包：GitHub Release 产物仅含主程序 + 必要 DLL + config.json
- 授权码由管理员在内部环境生成，通过安全渠道（加密邮件/内部系统）提供给客户输入
- 客户端仅支持输入授权码，**不支持**自行生成

---

## 8. 风险与建议

- **DCOM 配置**：OPC DA 依赖 DCOM，跨机器访问需配置 DCOM 安全权限（`dcomcnfg`）
- **大规模点位**：超过 3 万点位时 UA 地址空间构建耗时较长，建议使用异步模式
- **授权到期**：License 到期后网关停止服务，请提前续期

---

*本文档基于 V2.7.0 源码整理。如遇文档与软件实际行为不符，以软件界面为准。*
