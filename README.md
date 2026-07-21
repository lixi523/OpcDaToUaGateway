# OPC DA → OPC UA 网关（OpcDaToUaGateway）

![Build](https://github.com/lixi523/OpcDaToUaGateway/actions/workflows/build.yml/badge.svg)

> 版本：**V2.0.0** ｜ 协议转换网关：将 OPC DA 数据源实时映射为 OPC UA 服务器，供上位 SCADA/MES/工业平台订阅。

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
|---|---|
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

详细架构说明见 [`OPC_DA转UA网关开发指南.md`](OPC_DA转UA网关开发指南.md)。

---

## 6. 版本历史

| 版本 | 日期 | 变更摘要 |
|---|---|---|
| **V2.0.0** | 2026-07-21 | **DA 标签真实数据类型获取**：浏览阶段通过临时 OPC DA Group 调用 `AddItems`，从 `OpcDaItem.CanonicalDataType` 提取实际数据类型（Integer、Float、Boolean 等），替代原有的固定 "Variant" 描述；分批处理（每批 500 个点位），失败时静默回退不影响浏览结果。版本号 1.5.0 → 2.0.0。编译 0 警告 0 错误。 |
| V1.9.0 | 2026-07-20 | **全面代码审查 + 启动卡顿最终修复 + DA模式切换**：① DataBridge.StartAsync 异步启动（`Task.Run` 后台线程创建节点 + SynchronizationContext.Post 进度回调），3.5 万节点场景窗口保持响应；② 新增 DA 数据获取方式选择（异步订阅/同步轮询），UI 下拉框 + 配置持久化；③ 首次运行默认填充 ProgId `Matrikon.OPC.Simulation.1`，开箱即用；④ 未选择服务器时禁用「获取点位」「启动网关」按钮；⑤ Boolean 类型转换增强（支持字符串 "true"/"1"/"yes" 等）；⑥ SourceTimestamp 单调递增修复（bool 翻转标签可被 UA 客户端正确检测）；⑦ Dispose 后重连检查、Monitor.Exit 安全检查、SafeInvoke 句柄防护；⑧ ConfigManager 实现 IDisposable；⑨ 版本号 1.5.0 → 1.9.0；⑩ 删除 PLAN.md（文档整合完成）。编译 0 警告 0 错误。 |
| V1.8.1 | 2026-07-17 | **启动卡顿最终修复**：3.5 万次 `AddVariableNode` 移至后台线程（`Task.Run`），UI 线程通过 `SynchronizationContext.Post` 安全输出进度日志。真实环境验证窗口保持响应。 |
| V1.8.0 | 2026-07-16 | 移除 V1.7.0 新增的「已连接客户端」列表功能；修复启动窗口「未响应」卡顿根因（逐节点 `Diag()` 导致 O(n²) 日志洪泛）。编译 0 警告 0 错误。 |
| V1.7.0 | 2026-07-15 | 定稿发布：修复 3 个运行时缺陷（`SafeBeginInvoke` 句柄防护、UTC 时间戳显示转换、DA 质量判定修正 `value.Quality.Status & 0xC0`）；UA 设置区 UI 调整。 |
| V1.6.0 | — | 新增 OPC DA 同步/异步获取模式。 |
| V1.5.1 | — | 修复 `RunningStateChanged` 跨线程异常、`SafeInvoke` 封装与缺句柄防护。 |

---

## 7. 文档索引

| 文档 | 用途 | 读者 |
|---|---|---|
| [`使用文档.md`](使用文档.md) | 安装部署、配置、操作、授权、日志、FAQ | 现场实施/运维 |
| [`故障恢复预案.md`](故障恢复预案.md) | 9 类故障场景识别与恢复步骤 | 运维/值班 |
| [`OPC_DA转UA网关开发指南.md`](OPC_DA转UA网关开发指南.md) | 架构、模块、版本演进史 | 开发者 |
| [`handoff.md`](handoff.md) | 交接说明与边界约定 | 接手开发者 |
| [`STATUS.md`](STATUS.md) 当前状态与风险（含版本历史、编译状态、风险点） | 团队 |
| [`本地编译步骤.md`](本地编译步骤.md) | 本机/CI 编译实操 | 构建负责人 |

---

## 8. 风险与建议

- **DCOM 配置**：OPC DA 依赖 DCOM，跨机器访问需配置 DCOM 安全权限（`dcomcnfg`）
- **大规模点位**：超过 3 万点位时 UA 地址空间构建耗时较长，建议使用异步模式
- **授权到期**：License 到期后网关停止服务，请提前续期

---

*本文档基于 V2.0.0 源码整理。如遇文档与软件实际行为不符，以软件界面为准。*
