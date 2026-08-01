# OPC DA 转 OPC UA 网关开发指南

**版本：2.4.0**

## 项目概述

OpcDaToUaGateway 是一个 Windows 桌面应用程序，充当 OPC DA（基于 COM 的传统工业协议）与 OPC UA（基于 TCP 的现代工业协议）之间的协议网关。它从 OPC DA 服务器读取实时数据，并通过内嵌的 OPC UA 服务器将数据重新发布，使现代 OPC UA 客户端能够访问OPC DA 数据源。

**目标平台：** .NET Framework 4.7.2，x86（因 OPC DA COM 组件为 32 位）。

**解决方案包含三个项目：**

| 项目 | 输出 | 用途 |
|---|---|---|
| OpcDaToUaGateway | OpcDaToUaGateway.exe (WinForms) | 主程序：UI、DA 客户端、UA 服务器、数据桥接、授权管理 |
| OpcDaToUaGateway.Watchdog | OpcDaToUaGateway.Watchdog.exe (无依赖) | 独立进程：监控主程序并自动重启 |
| OpcDaToUaGateway.Keygen | OpcDaToUaGateway.Keygen.exe (控制台) | 授权码计算工具：根据 PCID 生成授权码 |

**部署特点：** 通过 Costura.Fody 将所有托管依赖 DLL 嵌入主 exe，部署时无需附带外部 DLL 文件。

---

## 系统架构

### 整体数据流

```
┌─────────────────────────────────────────────────────────────────┐
│                     OpcDaToUaGateway.exe                        │
│                                                                 │
│  ┌──────────┐    ┌────────────┐    ┌───────────────────┐       │
│  │ OPC DA   │───▶│ OpcDa      │───▶│ DataBridge        │       │
│  │ Server   │    │ Client     │    │ (线程安全转发层)   │       │
│  │ (COM)    │    │            │    └─────────┬─────────┘       │
│  └──────────┘    └────────────┘              │                  │
│                                              ▼                  │
│                                     ┌───────────────────┐       │
│                                     │ GatewayOpcUaServer │       │
│                                     │ (内嵌 UA 服务器)   │       │
│                                     └─────────┬─────────┘       │
│                                               │                 │
│  ┌─────────────────────┐                      │                 │
│  │ LicenseAlgorithm    │                      │                 │
│  │ (PCID + HMAC 授权)  │                      │                 │
│  └─────────────────────┘                      │                 │
│                                               │ opc.tcp://
└───────────────────────────────────────────────┼─────────────────┘
                                                │
                                                ▼
                                      ┌──────────────────┐
                                      │ OPC UA Clients   │
                                      │ (SCADA / MES 等) │
                                      └──────────────────┘

┌─────────────────────────────────────┐
│ OpcDaToUaGateway.Watchdog.exe       │
│ (独立进程，监控主程序，崩溃自动重启) │
└─────────────────────────────────────┘

┌─────────────────────────────────────┐
│ OpcDaToUaGateway.Keygen.exe         │
│ (控制台工具，根据 PCID 计算授权码)   │
└─────────────────────────────────────┘
```

### 核心组件关系

```
Program.cs (STA 入口 + 单实例 Mutex)
  └── MainForm.cs (UI + 协调各 Manager)
        ├── AboutDialog.cs             → 关于对话框（版本、PCID、授权码输入）
        ├── OpcServerScanner.cs        → 发现 DA 服务器
        ├── ServerSelectionDialog      → 选择 DA 服务器
        ├── TextBoxExtensions.cs          → 占位符文本扩展（P/Invoke EM_SETCUEBANNER，共享）
        ├── ItemSelectionDialog        → 选择/导入/导出标签
        ├── Services/
        │     ├── LogManager.cs        → 日志管理（UI显示 + 文件写入 + 过期清理）
        │     ├── ConfigManager.cs     → 配置管理（加载/保存/向后兼容/开机启动）
        │     ├── WatchdogManager.cs   → 看门狗管理（启停/进程清理）
        │     └── GatewayManager.cs    → 网关生命周期（启动/停止/健康监控/DA重连）
        └── Models/
              ├── TagConfig.cs         → 数据模型 (TagConfig, AppConfig, OpcUaConfig 等)
              └── LicenseAlgorithm.cs  → 授权码算法 (PCID 生成 + HMAC 验证)
```

---

## 技术栈与依赖

### NuGet 包

| 包名 | 版本 | 用途 |
|---|---|---|
| Newtonsoft.Json | 13.0.4 | config.json 序列化/反序列化 |
| OPCFoundation.NetStandard.Opc.Ua.Server | 1.5.378.145 | OPC UA 服务器核心库 |
| OPCFoundation.NetStandard.Opc.Ua.Configuration | 1.5.378.145 | UA 应用配置与证书管理 |
| Technosoftware.DaAeHdaSolution.DaAeHdaClient | 2.0.1 | OPC DA 客户端 COM 封装 |
| Costura.Fody | 5.7.0 | 将所有托管 DLL 嵌入主 exe（编译时织入） |

### 框架引用

- `Microsoft.CSharp` — 支持 `dynamic` 关键字（COM Automation 接口需要）
- `System.Windows.Forms` — WinForms UI
- `System.Runtime.InteropServices` — COM 对象释放（`Marshal.ReleaseComObject`）
- `System.Management` — WMI 硬件信息查询（授权码 PCID 生成需要）

---

## 项目结构

```
OpcDaToUaGateway/
├── OpcDaToUaGateway.sln              # 解决方案文件（含三个项目）
├── OpcDaToUaGateway.csproj           # 主项目文件（含版本号 1.8.0）
├── FodyWeavers.xml                   # Costura.Fody DLL 嵌入配置
├── Program.cs                        # 应用程序入口 (STAThread + 单实例 Mutex)
├── MainForm.cs                       # 主窗口（UI 构建 + 协调各 Manager）
├── AboutDialog.cs                    # 关于对话框（版本、PCID、授权码输入）
├── OpcDaClient.cs                    # OPC DA 客户端
├── GatewayOpcUaServer.cs             # OPC UA 服务器 + 节点管理器
├── DataBridge.cs                     # 数据桥接器 (DA→UA)
├── OpcServerScanner.cs               # DA 服务器发现 (5种策略)
├── ServerSelectionDialog.cs          # 服务器选择对话框
├── TextBoxExtensions.cs            # P/Invoke 占位符文本扩展（共享）
├── ItemSelectionDialog.cs            # 标签选择/导入导出对话框
├── app.ico                           # 应用程序图标 (多尺寸 ICO)
├── config.json                       # 运行时配置文件
├── Models/
│   ├── TagConfig.cs                  # 数据模型 (TagConfig, AppConfig, OpcUaConfig 等)
│   └── LicenseAlgorithm.cs          # 授权码算法 (PCID 生成 + HMAC-SHA256 验证)
├── Services/
│   ├── LogManager.cs                 # 日志管理（UI显示 + 文件持久化 + 过期清理）
│   ├── ConfigManager.cs             # 配置管理（加载/保存/向后兼容/开机启动）
│   ├── WatchdogManager.cs           # 看门狗管理（启停/进程清理/状态事件）
│   └── GatewayManager.cs            # 网关生命周期（启动/停止/健康监控/DA重连）
├── Keygen/
│   ├── OpcDaToUaGateway.Keygen.csproj  # 授权码计算工具项目
│   └── Program.cs                    # 控制台入口（交互模式 + 命令行模式）
├── Watchdog/
│   ├── OpcDaToUaGateway.Watchdog.csproj
│   └── Program.cs                    # 看门狗独立进程
├── Certificates/                     # UA 证书目录 (运行时自动创建)
│   ├── Own/
│   ├── Trusted/
│   ├── Rejected/
│   └── Issuers/
└── logs/                             # 运行日志目录 (自动创建)
    ├── gateway_yyyy-MM-dd.log        # 主程序日志（按日分文件）
    └── watchdog.log                  # 看门狗日志（单文件，自动清理）
```

---

## 配置文件 (config.json)

```json
{
  "OpcDa": {
    "ServerProgId": "Matrikon.OPC.Simulation.1",
    "UpdateRateMs": 1000,
    "Mode": "Async",
    "Tags": [
      { "ItemId": "Random.Int32", "DisplayName": "...", "DataType": "Int32" }
    ]
  },
  "OpcUa": {
    "ServerName": "OpcDaToUaGateway",
    "Port": 4840,
    "NamespaceUri": "http://gateway.example.com/OpcDaBridge/",
    "ListenAddress": "localhost",
    "SecurityMode": "None",
    "SecurityPolicy": "None",
    "AutoAcceptCertificates": false,
    "MaxSessionCount": 50,
    "SessionTimeout": 120000
  },
  "LastConnectedProgId": "",
  "AutoConnectDa": false,
  "AutoStartUa": false,
  "AutoStartWithWindows": false,
  "EnableWatchdog": false,
  "AuthorizationCode": ""
}
```

**TagConfig 字段说明：**

| 字段 | 类型 | 说明 |
|---|---|---|
| ItemId | string | OPC DA 标签的完全限定名 |
| DisplayName | string | 在 UA 中显示的名称 |
| DataType | string | 数据类型：Boolean, Int16, Int32, UInt16, UInt32, Float, Double, String, DateTime |
| TagKey | string | 内部唯一标识（不持久化，加载时由 `AssignTagKeys` 分配） |

**默认值：** `OpcDaConfig.Tags` 属性默认初始化为 `new List<TagConfig>()`（而非 null），避免首次启动或空配置时对 null 集合做遍历/计数时抛出 NullReferenceException。

**OpcUaConfig 字段说明：**

| 字段 | 类型 | 默认值 | 说明 |
|---|---|---|---|
| ServerName | string | OpcDaToUaGateway | OPC UA 服务器名称 |
| Port | int | 4840 | TCP 监听端口 |
| NamespaceUri | string | http://gateway.example.com/OpcDaBridge/ | 自定义命名空间 URI |
| ListenAddress | string | localhost | 监听地址：`localhost` 仅本机，`0.0.0.0` 允许远程 |
| SecurityMode | string | None | 安全模式：None / Sign / SignAndEncrypt |
| SecurityPolicy | string | None | 安全策略：None / Basic256Sha256 / Basic128Rsa15 / Basic256 |
| AutoAcceptCertificates | bool | false | 自动接受不受信任的客户端证书（生产环境默认关闭，需手动信任） |
| MaxSessionCount | int | 50 | 最大并发 UA 会话数 |
| SessionTimeout | int | 120000 | 会话超时（毫秒） |

**AppConfig 授权字段：**

| 字段 | 类型 | 说明 |
|---|---|---|
| AuthorizationCode | string | 授权码（验证通过后自动保存到配置文件） |

OpcUaConfig 提供 `GetEffective*()` 系列方法处理空值/默认值回退，并通过 `GetEndpointUrl()` 生成完整的端点地址。

---

## 核心模块详解

### 1. Program — 应用程序入口

**职责：** STA 线程初始化、单实例检测、全局异常处理、启动主窗口。

**全局异常处理（v1.3.4）：**

注册 `Application.ThreadException`（UI 线程未捕获异常）和 `AppDomain.CurrentDomain.UnhandledException`（非 UI 线程未捕获异常）两个全局异常处理器。所有未捕获异常写入 `crash.log` 文件（追加模式，含时间戳和完整堆栈），防止程序因未处理异常静默崩溃而无法诊断。

**DllImport 属性（v1.3.4）：** 所有 `DllImport` 特性添加 `SetLastError = true`，使 Win32 API 调用失败后可通过 `Marshal.GetLastWin32Error()` 获取详细错误码，便于诊断窗口查找/前置等 P/Invoke 调用失败原因。

**单实例机制：**

使用命名 Mutex `OpcDaToUaGateway_SingleInstance` 在操作系统级别保证全局只有一个实例运行。当第二个实例启动时：

1. `new Mutex(true, name, out createdNew)` 返回 `createdNew = false`
2. 通过 Win32 API `FindWindow` 查找已有窗口句柄
3. 若窗口最小化则 `ShowWindow(SW_RESTORE)` 还原
4. `SetForegroundWindow` 将窗口前置激活
5. 弹出提示信息后退出

```
第 1 个实例: Mutex createdNew=true → Application.Run(MainForm)
第 2 个实例: Mutex createdNew=false → FindWindow → SetForegroundWindow → MessageBox → return
```

### 2. OpcDaClient — OPC DA 客户端

**职责：** 连接 OPC DA 服务器，订阅标签数据，通过事件向 DataBridge 推送数据变化。

**构造函数新增 host 参数，支持连接远程 OPC DA 服务器（默认 localhost）。**

**数据采集策略：**
- **异步订阅（主）：** DA 服务器的 `DataChangedEvent` 回调，数据变化时自动触发
- **定时同步读取（兜底）：** 每 5 分钟（`SyncIntervalMs = 300000`）通过 `DoSyncRead()` 主动从 DA 服务器拉取一次全部点位值，防止异步回调丢包导致数据长期停滞。同步读取结果通过 `OnDataChanged` 事件投递，与异步回调共用同一数据通路，DataBridge 无需改动
- **获取模式（V1.6.0）：** 通过 `OpcDaConfig.Mode`（`Async`/`Sync`，UI「数据获取」下拉设置，默认 `Async`）显式选择数据获取方式。`Async` 维持异步订阅 + 5 分钟同步兜底；`Sync` 关闭订阅回调、置 `IsSubscribed=false`，改为按 `UpdateRateMs` 定时 `group.Read` 主动轮询。对应 `OpcDaClient.Start(int updateRateMs, DaAcquisitionMode mode)` 按模式分支，`TryReconnect` 自动复用模式。非 `"Sync"`（含空值/笔误）一律回退 `Async`
- **异常上报（v1.3.4）：** `OnDataChangedEvent` 中原有的空 catch 块替换为通过 `OnStatusChanged` 事件上报异常信息，避免 DA 回调异常被静默吞没导致数据丢失而无诊断线索

**质量判定（2026-07-15 修复）：** 数据质量必须用语义正确的 OPC DA 数据质量位——`value.Quality.Status` 高 2 位 `0xC0` 表示 Good（即 `((int)quality.Status & 0xC0) == 0xC0`），而非操作结果 `value.Error.Succeeded`（后者仅表示本次读取/订阅操作是否成功，与数据质量无关）。原先误用 `Error.Succeeded` 做硬跳过，会把 pSpace 等服务器订阅回调中 `Error.Succeeded=false` 但数据质量良好的有效数据丢弃，导致 UA 客户端看到大量点位质量 Bad。修复后按真实质量上送（良好即 Good，确属坏质量才标 Bad），新增 `IsQualityGood(OpcDaQuality)` 公共方法，异步 `OnValuesChanged` 与定时 `DoSyncRead` 两处回调同步修正。

**TagKey 机制：**

所有内部字典以 `TagKey`（而非 `ItemId`）为键。v1.3.4 将 `_itemIdToTagKeys` 和 `_tagKeyToItem` 从 `Dictionary` 替换为 `ConcurrentDictionary`，消除了每笔 DA 回调中手动加锁和浅拷贝的开销（原有实现在每次回调时对字典做浅拷贝后遍历，现直接线程安全读取）。当 OPC DA 回调用 `ItemName` 标识数据时，通过反向映射 `_itemIdToTagKeys`（`ConcurrentDictionary<string, List<string>>`）将同一 ItemId 的数据分发给所有匹配的 TagKey：

```
DA 回调 (ItemName="Tag1", Value=42)
  → _itemIdToTagKeys["Tag1"] = ["0_Tag1", "5_Tag1"]
  → OnDataChanged("0_Tag1", 42, ...)
  → OnDataChanged("5_Tag1", 42, ...)
```

**浏览地址空间：**

`BrowseAllItems` 静态方法递归浏览 DA 服务器地址空间（最大深度 10 层，上限 50000 条），不做全局去重，确保不同分支下的同名标签全部返回。标签订阅采用分批提交（每批 2000 个），避免大量标签时单次 COM 调用超时。每个返回的 `OpcDaItemInfo` 的 `Description` 字段记录父节点路径。Factory 和 Server 对象均使用 `using` 块确保 COM RCW 正确释放。

**浏览分页（v1.3.6）：** `MaxElementsReturned` 从 0（无限制）改为 `BrowsePageSize = 500`，配合已有的 `BrowseNext` 分页循环，每页最多 500 个元素，单次 COM 调用控制在 2 秒内，避免大量点位（35000+）场景下单次调用超过 DCOM 默认 20 秒超时导致 `0x80004005` 连接断开。浏览过程中每收集约 2000 个点位输出一次进度日志。

**关键方法签名：**

```csharp
public OpcDaClient(string progId, List<TagConfig> tags, string host = "localhost") // 构造（支持远程 DA 服务器）
public void Start(int updateRateMs, DaAcquisitionMode mode)      // 连接 + 订阅/轮询（按 mode 分支）+ 启动 5 分钟同步定时器（Async 模式）
public bool TryReconnect(int updateRateMs)                       // 内部看门狗重连
public static List<OpcDaItemInfo> BrowseAllItems(string progId)  // 浏览地址空间
private void DoSyncRead()                                       // 5 分钟定时同步读取（兜底保障）
```

**关键常量：**

| 常量 | 值 | 说明 |
|---|---|---|
| AddItemBatchSize | 2000 | 订阅添加的批量提交大小 |
| BrowsePageSize | 500 | Browse 分页大小（H-25，防止 DCOM 超时） |
| MaxBrowseItems | 50000 | 浏览最大点位上限 |
| MaxBrowseDepth | 10 | 浏览最大递归深度 |
| SyncIntervalMs | 300000 | 定时同步读取间隔（5分钟） |

### 3. GatewayOpcUaServer — OPC UA 服务器

**职责：** 在本地启动 OPC UA TCP 服务器，动态创建与 DA 标签对应的变量节点，接收 DataBridge 的更新并通知 UA 客户端。

**实现 IDisposable。v1.3.4：Dispose() 时若 UA 服务器仍在运行则先调用 StopAsync() 再释放资源，防止资源泄漏。**

**构造函数接受 `OpcUaConfig` 对象**，启动时根据配置动态构建服务器参数：

```csharp
public GatewayOpcUaServer(OpcUaConfig uaConfig)
```

**可配置参数：**

| 参数 | 来源 | 说明 |
|---|---|---|
| 监听地址 | `uaConfig.ListenAddress` | `localhost` 或 `0.0.0.0` |
| 端口 | `uaConfig.Port` | 默认 4840 |
| 安全模式 | `uaConfig.SecurityMode` | None / Sign / SignAndEncrypt |
| 安全策略 | `uaConfig.SecurityPolicy` | None / Basic256Sha256 等 |
| 证书自动接受 | `uaConfig.AutoAcceptCertificates` | 开发阶段建议 true |
| 连接数 | `uaConfig.MaxSessionCount` | 默认 50 |
| 会话超时 | `uaConfig.SessionTimeout` | 默认 120000ms |

安全策略集合根据 `secMode` 条件构建：当安全模式为 `None` 时才添加 `None` 策略（兼容调试），配置了 Sign 或 SignAndEncrypt 模式时排除 `None` 策略，强制客户端使用加密连接（v1.3.4 修复：原先始终包含 None 策略导致安全模式下仍可降级为无加密）。

**证书 SubjectName（v1.3.4）：** 生成自签名证书时 SubjectName 使用实际监听地址（当 `ListenAddress` 为 `0.0.0.0` 时使用 `Environment.MachineName`），替代原先硬编码的 `localhost`，确保证书主题与实际绑定地址匹配，避免客户端证书验证失败。

**节点管理器 (GatewayNodeManager)：**

继承 `CustomNodeManager2`，管理 UA 地址空间。使用 `_variableCache` 缓存 BaseDataVariableState 引用，UpdateValue 查找从 O(n) → O(1)。根据 ItemId 的点分隔路径自动创建层级文件夹结构：

```
ItemId = "Bucket_Brigade.Int4"
  → 创建文件夹 "Bucket_Brigade" (NodeId: Folder_Bucket_Brigade)
  → 创建变量 "Bucket_Brigade.Int4" (NodeId: DaTag_{tagKey})
```

**无分支 ItemId 批量分组（v1.3.6）：** 当 OPC DA 服务器的 ItemId 不含 "." 分隔符（即没有层级分支）时，不将所有变量直接挂在一个节点下。而是在 DaTags 下自动创建 `FlatTags > Batch_000 / Batch_001 / ...` 层级结构，每批 `FlatBatchSize = 1000` 个变量。此设计有两重目的：

1. **绕开 MasterNodeManager 路由缓存问题：** `_daTagsFolder` 在服务器初始化阶段创建时路由表为空，直接挂载变量不可见。运行时创建的 FlatTags 及其子文件夹不受此限制。
2. **避免单节点引用过多：** SDK 内部 Browse 机制在单节点下有数万个子引用时可能触发边界条件。

```
无分支 ItemId 的地址空间结构：
  Objects → OPC DA Tags → Flat Tags → Batch_000 (变量 0~999)
                                     → Batch_001 (变量 1000~1999)
                                     → Batch_002 (变量 2000~2999)
                                     → ...

有分支 ItemId 的地址空间结构：
  Objects → OPC DA Tags → Device1 → Group1 → [变量]
```

**运行时节点创建（v1.3.6）：** 变量和文件夹节点使用 `parent.AddChild(instance)` + `AddPredefinedNode(SystemContext, instance)` 两步创建。SDK 1.5.378.145 的 `CreateNode` API 内部 `instance.Create(..., assignNodeIds=true)` 在大批量（35000+）场景下触发 `BaseDataVariableState` 内部 NRE，绕开此步骤后稳定性已验证。`AddChild` 建立的 `Organizes` 引用是 UA 客户端 Browse 遍历的基础机制。

**⚠ 启动性能红线（v1.8.0 修复）：** `AddVariableNode` 在大批量（3.5 万+）场景下被高频调用，**严禁在方法体内调用 `Diag()` / `Log()` 等逐节点诊断**——`Diag` 经 `OnStatusChanged` → `LogManager.Append` → `BeginInvoke` 向 UI 线程投递数万次日志更新，`UpdateTextBox` 每次 O(文本长度)、累计 O(n²)，会导致窗口约 7 分钟「未响应」。`AddPredefinedNode` 本身为 O(1)（反编译 `Opc.Ua.Server.dll` 1.5.378.145 确认仅做 `PredefinedNodes` 字典注册 + 空子节点递归，无逐节点通知/地址空间重建），**无需也不应做"批量加载"跳过**——跳过它会绕过必要的节点注册，且对性能无益。进度反馈应放在 `DataBridge.Start()` 中按 ~5% 节流输出（见上文）。

**诊断功能（v1.3.6）：**

| 属性 | 说明 |
|---|---|
| `VariableCount` | 节点管理器实际注册的变量节点数 |
| `DaTagsChildrenCount` | DaTags 根文件夹的直接子对象数（通过 `GetChildren` 实时计算） |
| `FlatTagsChildrenCount` | Batch_000 的 `GetChildren` 子对象数（验证无分支点位引用是否建立） |
| `FlatTagsBrowseCount` | 模拟 UA 客户端 Browse 操作的引用计数（通过 `INodeBrowser` 直接测试 Browse 可见性） |

启动时 DataBridge 会输出上述所有诊断值，帮助快速定位 UA 客户端浏览异常。

**NamespaceIndex 立即持久化（v1.3.8）：** 服务器启动后回写 `_uaConfig.NamespaceIndex` 时通过 `OnConfigChanged` 回调 → `GatewayManager.ConfigDirty` 事件 → `MainForm` 立即调用 `ConfigManager.Save()`。不依赖 500ms 防抖定时器，消除进程崩溃导致索引丢失的窗口。

**NodeId 命名规则：**

| 节点类型 | 格式 | 示例 |
|---|---|---|
| 文件夹 | `Folder_{路径}` | `Folder_Random`, `Folder_Bucket_Brigade` |
| 变量 | `DaTag_{tagKey}` | `DaTag_Random.Int32`, `DaTag_0_Tag1` |

**关键方法签名：**

```csharp
public async Task StartAsync()
public void AddVariableNode(string tagKey, string itemId, string displayName, BuiltInType dataType)
public void UpdateValue(string tagKey, object value, bool isGood, DateTime sourceTimestamp)  // 坏质量使用 StatusCodes.Bad（非 Uncertain）；SourceTimestamp 基准为 DataBridge 统一传入的网关接收时刻 recvUtc（UTC），取 max(srcUtc, lastTs+1tick) 保证严格单调递增且两侧同源（UA SourceTimestamp 与监控快照时间戳代表同一瞬间，见 V1.7.0 时间戳同源统一）
public async Task StopAsync()
```

### 4. DataBridge — 数据桥接器

**职责：** 接收 DA 数据变化事件，进行类型转换后转发到 UA 服务器。维护线程安全的标签值快照供 UI 显示。

**实现 IDisposable，Dispose 时取消 DA 事件订阅。v1.3.4：添加 `volatile _disposed` 标志，Dispose() 改为幂等（多次调用安全），`OnDaDataChanged` 回调入口处检查 `_disposed` 标志防止已释放后仍处理数据。**

**性能优化：**
- 构造时缓存每个标签的 BuiltInType 和 DisplayName，避免每次数据变化做字符串解析
- `Start()` 方法使用 `TryGetValue` 替代 `ContainsKey` + 索引器的双重查找（v1.3.4）
- **启动验证（v1.3.6）：** 节点创建完成后输出诊断数据 — 命名空间索引、实际变量数、DaTags 子对象数、FlatTags 子对象数和 INodeBrowser Browse 计数。任何异常值输出 ⚠ 警告

**线程安全设计：**

| 机制 | 用途 |
|---|---|
| `ConcurrentDictionary<string, TagConfig>` | 标签配置映射（TagKey→TagConfig） |
| `ConcurrentDictionary<string, TagSnapshot>` | 值快照缓存（TagKey→TagSnapshot） |
| `Interlocked.Increment/Exchange` | 统计计数器原子更新 |
| `Volatile.Read` | 统计值读取 |
| 不可变 `TagSnapshot` 对象 | 整体替换而非原地修改，消除竞态条件 |

**快照顺序保证：** `_orderedKeys` 列表在构造时确定标签顺序，`GetSnapshots()` 按此顺序返回，确保与 UI 表格行一一对应。

**类型转换：** `ConvertValue` 方法根据 TagConfig 中配置的 `DataType`，将 DA 返回的 COM VARIANT 值转换为对应的 .NET 类型（`Convert.ToInt32`、`Convert.ToDouble` 等）。

**时间戳同源统一（2026-07-15 修复）：** `OnDaDataChanged` 在 DA 回调入口统一取 `DateTime recvUtc = DateTime.UtcNow`（网关接收时刻），同一值**同时**传给 `_uaServer.UpdateValue(...)` 与本地快照 `TagSnapshot.Timestamp`，确保 UA SourceTimestamp 与监控表格时间戳代表同一瞬间、严格同源。原实现将 DA 源戳 `value.Timestamp.LocalDateTime` 传给 UA、监控快照另取一次 `DateTime.UtcNow`，两侧基准不同源——当 DA 服务器时钟与本机存在偏差、或源戳冻结（bool 类标签实测 8–9 分钟才动）时，UA 与监控显示系统性错位。修改后 `UpdateValue` 内部简化为 `ts = max(srcUtc, lastTs+1tick)`（`srcUtc` 即传入的 `recvUtc`），不再引入独立的网关时钟基准。`recvUtc` 本身即 UTC，符合 OPC UA 对 SourceTimestamp 为 UTC 的规范。

**值差异说明（非缺陷）：** UA 客户端看到的「当前值」天然比监控滞后**最多一个发布周期**——网关把最新值推给 UA 服务器后，UA 客户端按自身订阅配置的 `PublishingInterval`/`SamplingInterval` 收值，而监控界面轮询更频繁。这是 OPC UA 标准订阅语义，非网关 bug；建议在 UA 客户端调小发布间隔（如 200–500ms）并设足够大 `QueueSize` 以收敛差异。

### 5. OpcServerScanner — DA 服务器发现

**职责：** 通过 5 种策略扫描系统中已注册的 OPC DA 服务器，合并去重后返回。

**诊断日志（v1.3.4）：** `DiagnosticLog` 从静态 `List<string>` 替换为 `ConcurrentBag<string>`，确保多线程扫描时日志追加的线程安全。通过 `GetDiagnosticLog()` 方法对外暴露，返回 `IReadOnlyList<string>` 防止外部修改。

**发现策略（按顺序执行）：**

1. **注册表组件类别扫描** — 读取 `HKCR\Component Categories\{catId}\CLSID`，检查 DA 1.0/2.0/3.0 三个类别 GUID
2. **STA 线程 COM 枚举** — 在独立 STA 线程（15 秒超时）中使用 `IOPCServerList` 接口枚举 CLSID
3. **HKCR ProgId 全量扫描** — 遍历 `HKEY_CLASSES_ROOT` 所有子键，寻找含 `LocalServer32` 且名称包含 "OPC" 的 ProgId
4. **OPC Foundation 已安装组件** — 读取 `HKLM\SOFTWARE\OPC Foundation\Installed Components`
5. **ProgId 验证** — 通过 `VersionIndependentProgID` 和 `ProgId` 注册表键解析 ProgId

**类别 GUID 常量：**

| 版本 | GUID |
|---|---|
| OPC DA 1.0 | `63D5F430-CFE4-11D1-B2C8-0060083BA1FB` |
| OPC DA 2.0 | `63D5F432-CFE4-11D1-B2C8-0060083BA1FB` |
| OPC DA 3.0 | `CC603642-66D7-48F1-B69A-B625E73652D7` |

### 6. MainForm — 主窗口（协调层）

**职责：** UI 构建与交互、系统托盘、授权管理、协调四个 Manager 类完成具体业务逻辑。

MainForm 自 v1.3.1 起仅作为 UI 协调层，所有业务逻辑已拆分至 `Services/` 下的四个管理类。MainForm 持有这些 Manager 的实例，订阅其事件，并将用户操作转发给对应的 Manager。

**窗口标题提取为 `internal const string WindowTitle`，Program.cs 通过 `MainForm.WindowTitle` 引用，避免版本号不一致。**

**UI 区域划分：**

| 区域 | 内容 |
|---|---|
| OPC DA 服务器 | ProgId 输入框、浏览按钮、获取点位按钮 |
| OPC UA 服务器设置 | 监听地址、端口号、安全模式、证书策略、连接数、端点 URL 预览、右侧「已连接客户端」列表 |
| 控制面板 | 启动/停止按钮、导出点表、自动选项（DA/UA/开机/守护）、状态标签、关于按钮 |
| 标签数据监控 | DataGridView 虚拟模式（VirtualMode）实时显示标签值、质量、时间戳，支持 50000+ 行 |
| 运行日志 | 文本框由 LogManager 管理（同时写入文件） |

**管理器初始化顺序：**

```
new MainForm(startMinimized)
  → BuildUI()
    1. new LogManager(txtLog)           // 日志管理器（最先创建，其他 Manager 需要引用）
    2. new ConfigManager(_log)          // 配置管理器
    3. new WatchdogManager(_log)        // 看门狗管理器（订阅 StatusChanged 事件）
  → LoadConfiguration()
    4. _configMgr.Load()                // 加载 config.json
    5. new GatewayManager(_log, Config) // 网关管理器（需要配置对象）
    6. InitializeLicense()              // 验证授权码或启动试用计时器
```

**事件订阅模式：**

各 Manager 通过 `Action<string, Color>` 事件向 MainForm 报告状态变化，MainForm 通过 `Invoke` 在 UI 线程更新标签文本和颜色：

```csharp
_watchdogMgr.StatusChanged += (text, color) => Invoke((MethodInvoker)(() => {
    lblWatchdogStatus.Text = text;
    lblWatchdogStatus.ForeColor = color;
}));
```

**UI 刷新（v1.3.5 虚拟模式）：** `RefreshStats` 方法（由 `_refreshTimer` 每秒触发）采用 DataGridView 虚拟模式（`VirtualMode = true`）实现大数据量下的实时数据刷新。

传统模式下 DataGridView 为每行创建实际的 Row/Cell 对象，50000 行 × 5 列 = 250000 个单元格对象，每秒全量更新会卡死 UI 线程。虚拟模式彻底改变了这一机制：

```
UpdateTagGrid(tags):
  → _gridTags = tags             // 缓存标签列表
  → _dgvTags.RowCount = 50000    // 只设置行数，不创建行对象

RefreshStats() (每秒触发):
  → _cachedSnapshots = bridge.GetSnapshots()  // 每周期只构建一次快照列表
  → InvalidateRow(firstVisible .. lastVisible) // 仅使可见行失效

CellValueNeeded 事件 (DataGridView 渲染时按需触发):
  → 列 0-1: 从 _gridTags[rowIndex] 读取 DisplayName / ItemId
  → 列 2-4: 从 _cachedSnapshots[rowIndex] 读取 Value / Quality / Timestamp

**时间戳本地化（2026-07-15 修复）：** `TagSnapshot.Timestamp` 在内存中为 UTC（符合 OPC UA SourceTimestamp 规范），监控表格显示时通过 `snap.Timestamp.ToLocalTime().ToString("HH:mm:ss.fff")` 转为本地时间，避免北京用户(UTC+8)看到的时间比实际墙钟慢 8 小时。数据模型不改动，仅 UI 展示层转换。

CellFormatting 事件:
  → 质量列 (列3): "Good" 绿色 / 其他 红色

Scroll 事件:
  → Invalidate() 触发重绘，新可见行自动通过 CellValueNeeded 获取数据
```

屏幕上通常仅 20~30 行可见，因此每秒仅触发约 100~150 次 `CellValueNeeded` 回调（20 行 × 5 列），而非 250000 次。配合双缓冲（`DoubleBuffered = true`）和列排序禁用（`SortMode = NotSortable`），50000 行场景下滚动和刷新均保持流畅。

**网关启动 3 步流程（委托给 GatewayManager）：**

```
BtnStart_Click (async void)
  → 检查 _trialExpired（试用到期则拒绝启动）
  → await _gatewayMgr.StartAsync()
    1. GatewayOpcUaServer.StartAsync()  → 启动 UA 服务器
    2. OpcDaClient.Start()              → 连接 DA 服务器 + 订阅
    3. DataBridge.Start()               → 创建 UA 节点 + 订阅 DA 事件
  → 启动 _refreshTimer (1s, UI 刷新)
  → 启动 _healthTimer (10s, 调用 _gatewayMgr.CheckHealth())
  → 禁用 OPC UA 设置区控件（防止运行时修改）
```

**关闭流程（异步两阶段，v1.3.5）：**

```
用户点击关闭 → OnFormClosing
  → 第 1 次进入: _closeInProgress=false
    → 取消关闭, _closeInProgress=true
    → 异步清理: try { await _gatewayMgr.StopAsync() } catch { ... }
    → _watchdogMgr.SignalGracefulExit()   // 通知看门狗：正常退出，不要重启
    → 释放 _autoStartTimer
    → _isShuttingDone=true, _forceClose=true, Close()
  → 第 2 次进入: _forceClose=true
    → dispose NotifyIcon, base.OnFormClosing()
```

v1.3.5 修复：将 `_watchdogMgr.Stop()` 替换为 `_watchdogMgr.SignalGracefulExit()`，使托盘退出时看门狗进程继续运行（仅在取消勾选"进程守护"时才停止看门狗）。添加 `_isShuttingDown` 标志防止重复进入清理代码，同时确保第二次 `Close()` 调用能正常到达 `base.OnFormClosing()` 完成窗口销毁。`StopAsync` 调用包裹在 try-catch 中，防止异步清理异常导致窗口无法关闭而陷入僵尸状态。`_autoStartTimer` 从局部变量提升为字段，在 OnFormClosing 中显式 Dispose 防止定时器泄漏。

### 6.1 LogManager — 日志管理器

**职责：** 线程安全的日志 UI 显示、异步队列文件持久化、过期日志清理。

**文件：** `Services/LogManager.cs`

LogManager 封装了 TextBox 控件，提供跨线程安全的 `Append()` 方法。文件写入通过 `BlockingCollection` 队列在独立后台线程完成，不阻塞 UI。其他 Manager 持有 LogManager 引用，直接调用 `Append()` 记录日志。

**核心参数：**

| 参数 | 值 | 说明 |
|---|---|---|
| MaxDays | 30 | 日志文件保留天数 |
| CleanupInterval | 500 | 每 N 次写入触发一次过期清理 |
| MaxTextLength | 100,000 | UI 文本框最大字符数 |
| TrimToLength | 50,000 | 超过上限时截断到此长度 |
| Queue Capacity | 10,000 | 写入队列最大容量（满时丢弃新条目） |

**异步写入架构：**

```
调用方（任意线程）
  → Append(message)
    1. UI 更新: Invoke → UpdateTextBox()     // 同步更新 UI（必须实时）
    2. 文件写入: TryAdd → BlockingCollection  // 非阻塞入队
                                    ↓
                        后台线程 WriterLoop (IsBackground)
                          → GetConsumingEnumerable()
                          → 日期变化时切换 StreamWriter（按日命名文件）
                          → StreamWriter.WriteLine() (AutoFlush=false，每 10 次写入 Flush 一次)
                          → 每 500 次写入触发 DoCleanup()
```

**线程安全机制：** UI 更新使用 `InvokeRequired` + `Invoke` 确保在 WinForms 线程执行；文件写入在独立后台线程完成，与 UI 线程完全解耦。构造函数中启动 `LogWriter` 后台线程，`Dispose()` 时通过 `CompleteAdding()` 通知线程排空队列后退出。

**Dispose 竞态修复（v1.3.4）：**
- 添加 `volatile _disposed` 标志防止重复 Dispose
- `Join` 等待后台线程超时从 1s 延长至 5s，确保大缓冲区充分刷写
- 后台线程退出后再安全关闭 `fileWriter`（原先可能在写入线程仍持有 StreamWriter 时 Dispose）
- 调用 `queue.Dispose()` 释放 `BlockingCollection` 内部资源

**关键方法签名：**

```csharp
public LogManager(TextBox textBox)           // 绑定 UI 文本框 + 启动后台写入线程
public void Append(string message)           // 线程安全的日志追加（UI 同步 + 文件异步）
public void CleanupOldFiles()                // 异步触发过期日志清理（ThreadPool）
public void Dispose()                        // 停止后台线程，刷写剩余队列
```

### 6.2 ConfigManager — 配置管理器

**职责：** config.json 加载/保存、向后兼容默认值填充、TagKey 分配、开机启动快捷方式管理。

**文件：** `Services/ConfigManager.cs`

ConfigManager 封装了配置的序列化/反序列化逻辑，并通过 `Config` 属性暴露 `AppConfig` 对象供所有 Manager 使用。

**保存机制：**

- Save 采用防抖机制（500ms），多次快速调用只写一次磁盘。v1.3.4：使用 `Timer.Change()` 复用同一定时器实例替代原先的 dispose + recreate 模式，减少 GC 压力并避免定时器泄漏
- `SaveImmediate()` 在 lock 内执行 `DoSave()`，修复原先在锁外执行导致的并发写入竞态（v1.3.4）
- 文件写入使用原子操作（先写临时文件，再 File.Replace），防止进程崩溃导致配置损坏

**加载流程：**

```
Load()
  1. 读取 config.json → RawJson
  2. JsonConvert.DeserializeObject<AppConfig>()
  3. ApplyBackwardCompatDefaults()   // 补充新增字段的默认值（旧配置文件兼容）
  4. TagConfig.AssignTagKeys()       // 分配内部唯一标识
  → 返回 true/false 表示成功/失败
```

**开机启动：** 通过反射调用 `WScript.Shell` COM 对象在 Windows 启动文件夹创建 `.lnk` 快捷方式（参数 `--minimized`），COM 对象通过 `Marshal.ReleaseComObject()` 正确释放。

**关键方法签名：**

```csharp
public ConfigManager(LogManager log)              // 注入日志管理器
public bool Load()                                // 加载 + 兼容 + TagKey 分配
public void Save()                                // 序列化保存到 config.json
public void SaveProgId(string progId)             // 保存最后连接的 ProgId
public void SetAutoStart(bool enable)             // 创建/删除开机启动快捷方式
```

### 6.3 WatchdogManager — 看门狗管理器

**职责：** 管理外部看门狗进程的生命周期（启动、停止、优雅退出信号），通过事件通知 UI 状态变化。

**文件：** `Services/WatchdogManager.cs`

**IPC 机制（v1.3.5）：**

| 机制 | 名称 | 用途 |
|---|---|---|
| 命名 EventWaitHandle | `OpcDaToUaGateway_Watchdog_Stop` | 优雅停止信号（仅取消勾选"进程守护"时发送） |
| 命名 EventWaitHandle | `OpcDaToUaGateway_Watchdog_Heartbeat` | 主程序心跳信号（ManualResetEvent，定时 Set 表示存活） |
| 命名 EventWaitHandle | `OpcDaToUaGateway_GracefulExit` | 主程序正常退出信号（托盘退出时设置，告知看门狗不要重启） |
| 进程名检测 | `Process.GetProcessesByName` | 残留进程清理 |

**v1.3.5 生命周期重设计：**

看门狗的生命周期与主程序的退出方式解耦：

| 退出方式 | 看门狗行为 |
|---|---|
| 取消勾选"进程守护" | 发送 StopEvent → 看门狗进程退出 |
| 托盘图标右键退出 | 发送 GracefulExitEvent → 看门狗继续运行但不重启主程序 |
| 主程序崩溃 | 无信号 → 看门狗检测到进程消失后自动重启 |

**SignalGracefulExit() 方法：** 主程序正常退出时调用，先停止心跳定时器，再设置 `OpcDaToUaGateway_GracefulExit` 事件。看门狗在监控循环中检测到此事件后跳过重启逻辑，并自动 Reset 事件以供下次使用。

**线程安全（v1.3.5）：** Start/Stop 操作均持有 `_lock` 对象，防止并发调用导致重复启动或竞态停止。

**关键方法签名：**

```csharp
public bool IsRunning { get; }                        // 进程是否存活
public event Action<string, Color> StatusChanged      // 状态变化事件
public void Start()                                   // 清理残留 + 启动看门狗进程 + 启动心跳
public void Stop()                                    // 信号通知 + 等待 + 强制终止（仅取消勾选时调用）
public void SignalGracefulExit()                      // 设置优雅退出事件（托盘退出时调用）
```

### 6.4 GatewayManager — 网关生命周期管理器

**职责：** 管理 OPC DA 客户端、OPC UA 服务器、DataBridge 的完整生命周期，包括启动、停止、健康监控和 DA 重连。

**文件：** `Services/GatewayManager.cs`

**核心参数：**

| 参数 | 值 | 说明 |
|---|---|---|
| MaxReconnectAttempts | 50 | DA 重连最大尝试次数 |

**UI 日志过滤（v1.3.6）：** DA 和 UA 状态回调中以 `[诊断]` 开头的消息不会显示在 UI 运行日志中（仍写入日志文件），避免大量节点注册时的诊断信息刷屏。过滤逻辑在 `StartAsync()` 中的 `OnStatusChanged` 事件订阅中实现。

**线程安全：** 所有字段（_daClient, _uaServer, _bridge）标记为 volatile，StartAsync/StopAsync 使用 lock 保护

**启动 3 步流程：**

```
StartAsync()
  1. _uaServer = new GatewayOpcUaServer(_config.OpcUa) → await StartAsync()
  2. _daClient = new OpcDaClient(Config.OpcDa.ServerProgId, tags) → Start(updateRateMs, Config.OpcDa.GetEffectiveMode())
  3. _bridge = new DataBridge(_daClient, _uaServer, tags) → Start()
  → IsRunning = true
```

v1.3.4 启动回滚链：启动过程包裹在 try-catch 中，若步骤 2 或 3 失败，自动逆序 Dispose 已创建的资源（如步骤 2 失败则 Dispose _uaServer，步骤 3 失败则 Stop _daClient + Dispose _uaServer），防止半启动状态下资源泄漏。

**健康监控：** `CheckHealth()` 由 MainForm 的 `_healthTimer` 每 10 秒调用一次，检查 `_daClient.IsConnected`，若断开则调用 `TryReconnect()`，最多重试 50 次。超过重试上限后停止网关并通知 UI。

**停止顺序（v1.3.4）：** `StopAsync` 先 Dispose DataBridge（取消 DA 事件订阅），再 Stop OpcDaClient，最后 StopAsync GatewayOpcUaServer。正确的释放顺序确保 Bridge 不会在 DA 客户端已释放后仍收到回调。

**关键方法签名：**

```csharp
public bool IsRunning { get; }                             // 网关是否正在运行
public OpcDaClient DaClient { get; }                       // DA 客户端实例
public DataBridge Bridge { get; }                          // 数据桥接实例
public event Action<string, Color> DaStatusChanged         // DA 状态变化事件
public event Action<string, Color> UaStatusChanged         // UA 状态变化事件
public async Task StartAsync()                             // 3 步启动
public async Task StopAsync()                              // 逆序停止
public void CheckHealth()                                  // 健康检查 + 重连
```

### 7. AboutDialog — 关于对话框

**职责：** 显示程序版本号、技术栈信息、运行环境、机器码 (PCID) 和授权码输入。

通过控制面板区的"关于"按钮打开。展示内容包括：程序名称与版本号（v1.3.4 起从 `Assembly.GetExecutingAssembly().GetName().Version` 动态读取，不再硬编码 `const string AppVersion`，消除版本不同步风险）、功能描述、技术栈（.NET Framework 版本、OPC UA SDK 版本、DA 客户端版本）、运行时信息（CLR 版本、操作系统版本、进程位数）。

**Icon 资源管理（v1.3.4）：** 对话框 Icon 使用 `using` 块加载，确保 `Icon` 对象在使用后立即释放 GDI 句柄。

**授权信息区域：**

| 控件 | 功能 |
|---|---|
| 当前状态标签 | 显示"已授权"（绿色）或"未授权（试用中）"（橙红色） |
| PCID 文本框 | 只读显示本机机器码，旁有"复制"按钮 |
| 授权码输入框 | 输入授权码（格式 XXXX-XXXX-XXXX-XXXX-XXXX） |
| 验证授权按钮 | 提交授权码，DialogResult.OK 返回给 MainForm 处理 |

**与 MainForm 的交互：** AboutDialog 通过构造函数接收 `pcid` 和 `isLicensed` 参数。用户输入授权码点击验证后，DialogResult.OK 关闭对话框，MainForm 读取 `AuthorizationCode` 属性并调用 `ApplyAuthorizationCode()` 进行验证。

### 8. LicenseAlgorithm — 授权码算法

**职责：** 生成机器唯一标识 (PCID) 和计算/验证授权码。主程序和 Keygen 工具共享此文件。

**算法流程：**

```
PCID 生成:
  1. 通过 WMI 查询硬件信息: CPU ProcessorId, BaseBoard SerialNumber, BIOS SerialNumber
  2. 组合: rawId = "{cpuId}|{boardSerial}|{biosSerial}"
  3. PCID = SHA256(rawId) 取前 8 字节 = 16 位十六进制字符
  4. v1.3.4: 若三项 WMI 查询全部失败则抛出 InvalidOperationException，
     防止所有机器共享 "UNKNOWN" PCID 导致授权码通用

授权码生成:
  1. 输入 PCID 转大写
  2. HMAC-SHA256(32字节密钥, PCID)
  3. 取前 20 字节
  4. 格式化为 XXXX-XXXX-XXXX-XXXX-XXXX (共 29 字符)

授权码验证:
  1. 重新计算 HMAC-SHA256(密钥, PCID)
  2. 常量时间比较（XOR 逐位对比），防止时序攻击
```

**密钥（v1.3.5）：** 采用三层 XOR 混淆方案，32 字节密钥分为 `_layer1`、`_layer2`、`_layer3` 三个静态数组嵌入 IL 中，运行时通过 `DeriveKey()` 方法逐层异或还原并经 SHA256 派生最终 HMAC 密钥。相比 v1.3.4 的明文硬编码密钥，大幅提高了逆向工程难度。主程序和 Keygen 必须使用相同的三层混淆数据。密钥中包含版本号标识，版本升级时同步更新。

**关键方法签名：**

```csharp
public static string GeneratePCID()                        // WMI 硬件信息 → 16 字符 PCID
public static string GenerateAuthCode(string pcid)         // PCID → 授权码
public static bool VerifyAuthCode(string pcid, string authCode) // 验证授权码
```

### 9. 授权管理机制

**试用期：** 未授权时软件可运行 30 分钟，到期后自动关闭且无法再启动。

**授权流程：**

```
程序启动 → InitializeLicense()
  1. 生成 PCID
  2. 检查 config.json 中已保存的 AuthorizationCode
     → 验证通过: _isLicensed=true, 状态栏显示"已授权"
     → 验证失败或无授权码: 启动 30 分钟试用计时器
  3. 试用计时器每秒更新状态栏剩余时间
     → 最后 5 分钟: 红色警告
     → 5~10 分钟: 橙色提示
  4. 试用到期: 停止网关 → 弹出提示 → 退出程序
```

**授权码输入：** 用户点击"关于"→ 复制 PCID → 发给管理员 → 管理员使用 Keygen 工具生成授权码 → 用户输入授权码 → MainForm.ApplyAuthorizationCode() 验证并保存到 config.json。

**关键状态变量：**

| 变量 | 说明 |
|---|---|
| `_isLicensed` | 是否已授权 |
| `_pcid` | 本机机器码 |
| `_licenseTimer` | 试用倒计时定时器（每秒触发） |
| `_licenseStartTime` | 试用开始时间 |
| `_trialExpired` | 试用是否已到期（到期后禁止启动网关） |

### 10. 看门狗进程 (Watchdog)

**职责：** 独立进程监控主程序，崩溃后自动重启。与主程序通过命名事件通信。

**IPC 机制（v1.3.5）：**

| 机制 | 名称 | 方向 | 用途 |
|---|---|---|---|
| 命名 Mutex | `OpcDaToUaGateway_SingleInstance` | Program 内部 | 主程序单实例保证 |
| 命名 Mutex | `OpcDaToUaGateway_Watchdog_Mutex` | 看门狗内部 | 看门狗单实例保证 |
| 命名 EventWaitHandle | `OpcDaToUaGateway_Watchdog_Stop` | 主程序→看门狗 | 优雅停止信号（仅取消勾选"进程守护"） |
| 命名 EventWaitHandle | `OpcDaToUaGateway_Watchdog_Heartbeat` | 主程序→看门狗 | 心跳信号（ManualResetEvent） |
| 命名 EventWaitHandle | `OpcDaToUaGateway_GracefulExit` | 主程序→看门狗 | 正常退出信号（托盘退出时设置） |
| 进程名监控 | `Process.GetProcessesByName` | 看门狗→主程序 | 崩溃检测 |

**心跳检测机制（v1.3.5）：**

主程序 WatchdogManager 使用 `ManualResetEvent` 定期 Set 心跳事件。看门狗通过 `WaitOne(timeout)` 检测心跳：若超时未收到信号则判定主程序无响应（即使进程仍存在）。心跳事件使用 `ManualResetEvent` 语义：`WaitOne` 不消费信号，检测后需显式 `Reset()` 以准备下一轮检测。

**监控算法（v1.3.5）：**

```
循环:
  1. 检查停止信号 (StopEvent) → 收到则退出看门狗
  2. 检查主进程是否存活
  3. 检查心跳事件 (HeartbeatEvent.WaitOne)
     → 收到: Reset() 准备下一轮
     → 超时: 主程序无响应，等待进程退出后重启
  4. 若主进程不存活:
     a. 检查优雅退出信号 (GracefulExitEvent)
        → 已设置: Reset() 事件，跳过重启（主程序正常退出）
        → 未设置: 执行重启流程（主程序崩溃）
     b. 检查快速重启计数 (60秒内最多3次)
     c. 等待 3 秒 (期间持续检查停止信号)
     d. 启动主程序
  5. 等待 5 秒 (期间持续检查停止信号)
```

**v1.3.5 关键设计决策：**
- 看门狗是**完全独立的进程**，主程序关闭或崩溃不影响看门狗运行
- **托盘退出不关闭看门狗**：主程序正常退出时通过 `GracefulExitEvent` 告知看门狗，看门狗继续运行但跳过重启
- **只有取消勾选"进程守护"复选框时才会停止看门狗**（发送 StopEvent）
- 看门狗 exe 在编译时由 MSBuild Target 自动构建并复制到主程序输出目录
- **进程句柄释放（v1.3.4）：** `Process.Start()` 返回的 `Process` 对象包裹在 `using` 块中，确保 OS 进程句柄在启动后立即释放
- **日志路径（v1.3.5）：** 看门狗日志写入 `logs/watchdog.log`（与主程序日志统一在 logs 目录下）

### 11. 授权码计算工具 (Keygen)

**职责：** 独立的控制台工具，根据目标机器的 PCID 计算授权码。

**运行模式：**

| 模式 | 触发方式 | 说明 |
|---|---|---|
| 交互模式 | 无参数启动 | 菜单选择：查看本机 PCID / 输入 PCID 生成授权码 |
| 命令行模式 | `Keygen.exe <PCID>` | 直接输出授权码，适合脚本调用 |

**EOF 处理（v1.3.4）：** 交互模式下 `Console.ReadLine()` 返回 null（stdin EOF，如管道关闭或 Ctrl+Z）时不再抛出 NullReferenceException，而是安全退出。

**项目结构：** Keygen 项目通过 `<Compile Include>` 链接主项目的 `Models/LicenseAlgorithm.cs`，确保算法实现完全一致。编译时由主项目的 `BuildAndCopyKeygen` MSBuild Target 自动构建并复制到同一输出目录。

### 12. ItemSelectionDialog — 标签选择对话框

**职责：** 浏览 DA 服务器地址空间，选择/导入/导出标签点位。

**过滤防抖（v1.3.4）：** 过滤文本框输入后启动 200ms 防抖定时器（`Timer`），用户连续输入期间不触发过滤，停止输入 200ms 后才执行过滤逻辑，避免大数据量下每次击键都阻塞 UI。

**勾选状态保持（v1.3.4）：** 过滤操作会隐藏不匹配的项，但原先恢复全部显示时会丢失已勾选项。现使用 `_checkedItemIds` HashSet 在过滤前缓存当前勾选项的 ItemId，过滤后根据缓存恢复勾选状态，确保用户在过滤/清除过滤过程中不丢失已选标签。

**辅助方法（v1.3.4）：** 提取 `CreateListViewItem` 辅助方法，统一创建 ListView 行的逻辑（设置 Text、Tag、SubItems），消除重复代码。

**SafeBeginInvoke 防护（2026-07-15 修复）：** `BeginBrowse` 在 `Task.Run` 后台线程执行 `BrowseAllItems`，其 `logger` 回调需更新 UI 控件。原代码裸调 `BeginInvoke`，当后台线程首条日志在对话框窗口句柄创建前触发时抛 `InvalidOperationException: 在创建窗口句柄之前，不能在控件上调用 Invoke 或 BeginInvoke`。新增 `SafeBeginInvoke(Action)`——`IsDisposed` 直接返回，`IsHandleCreated` 则正常 `BeginInvoke`，**句柄未就绪时订阅 `HandleCreated` 事件，待 UI 线程创建句柄后再执行**，确保竞态不崩溃且 `OnBrowseComplete`/`OnBrowseFailed` 终态回调不丢失（与 `MainForm.SafeInvoke` 防护范式一致）。替换三处裸 `BeginInvoke`（状态标签更新、`OnBrowseComplete`、`OnBrowseFailed`）。

### 13. ServerSelectionDialog — 服务器选择对话框

**职责：** 显示已发现的 DA 服务器列表，支持过滤搜索，选择目标服务器。

**零分配过滤（v1.3.4）：** 过滤比较从 `ToLowerInvariant().Contains()` 改为 `IndexOf(filter, StringComparison.OrdinalIgnoreCase)`，避免每次比较都创建小写字符串副本，在服务器列表较大时减少 GC 压力。

---

## TagKey 机制详解

TagKey 是解决同一 ItemId 在多个节点下出现时数据错位的内部标识机制。

**分配时机：**
- `LoadConfiguration()` 加载配置后立即调用 `TagConfig.AssignTagKeys()`
- `ItemSelectionDialog` 选择标签后立即调用

**分配规则：**
- 所有 ItemId 唯一时：`TagKey = ItemId`（透明传递）
- 存在重复 ItemId 时：所有标签统一使用 `TagKey = "{列表索引}_{ItemId}"` 格式

**使用位置：**

| 组件 | 用途 |
|---|---|
| `OpcDaClient._tagKeyToItem` | TagKey → OPC DA 订阅项 |
| `OpcDaClient._itemIdToTagKeys` | ItemId → TagKey 列表（反向映射） |
| `DataBridge._tagMap` | TagKey → TagConfig |
| `DataBridge._snapshots` | TagKey → TagSnapshot |
| `GatewayNodeManager._nodeMap` | TagKey → UA NodeId |
| UA NodeId | `DaTag_{tagKey}` |

**重要：** TagKey 不持久化到 config.json，每次加载时重新分配。

---

## 系统托盘与开机启动

### 系统托盘

- 使用 `NotifyIcon` + `ContextMenuStrip`（菜单项：显示主窗口、退出）
- 双击托盘图标 = 显示主窗口
- 关闭按钮行为：最小化到托盘（非退出），除非 `_forceClose=true`
- 最小化时自动隐藏到托盘（`OnResize` 处理）
- 恢复窗口顺序：`Show()` → `ShowInTaskbar = true` → `WindowState = Normal` → `Activate()`

### 开机启动

开机启动快捷方式管理已封装在 `ConfigManager.SetAutoStart()` 中：

- 通过 `WScript.Shell` COM 自动化（反射调用）在 Windows 启动文件夹创建 `.lnk` 快捷方式
- 快捷方式参数：`--minimized`（启动时直接最小化到托盘）
- COM 对象通过 `Marshal.ReleaseComObject()` 正确释放

### 命令行参数

| 参数 | 效果 |
|---|---|
| `--minimized` | 启动时最小化到系统托盘 |

---

## 日志系统

### 主程序日志

主程序日志由 `Services/LogManager.cs` 统一管理，文件写入采用异步队列模式（后台线程 + `BlockingCollection`），不阻塞 UI：

| 属性 | 值 |
|---|---|
| 文件路径 | `logs/gateway_yyyy-MM-dd.log`（每天一个文件） |
| 保留期限 | 30 天 |
| 清理触发 | 每 500 次写入检查一次 |
| 清理方式 | 按文件 `LastWriteTime` 删除过期文件 |
| 编码 | UTF-8 with BOM |

UI 日志文本框超过 100,000 字符时自动截断到后 50,000 字符。

### 看门狗日志

| 属性 | 值 |
|---|---|
| 文件路径 | `logs/watchdog.log`（单文件，与主程序日志统一在 logs 目录下） |
| 保留期限 | 7 天 |
| 清理触发 | 每 100 次写入，且文件 > 10KB |
| 清理方式 | 解析 `[yyyy-MM-dd HH:mm:ss]` 时间戳，移除过期行后重写文件 |

---

## CSV 导入/导出

### 主程序标签导出

从当前配置的标签列表导出，格式：

```csv
# 服务器: Matrikon.OPC.Simulation.1
# 导出时间: 2026-06-12 10:30:00
# 点位数: 8
# 刷新频率: 1000 ms

序号,ItemId,显示名称,数据类型
1,Random.Int32,Matrikon..._Random.Int32,Int32
```

### 点表对话框导入/导出

"获取点表"对话框（`ItemSelectionDialog`）提供独立的 CSV 导入导出按钮：

**导出格式：**
```csv
# Server: Matrikon.OPC.Simulation.1
# Export time: 2026-06-12 10:30:00
# Selected items: 5

序号,ItemId,名称,数据类型,描述
1,Random.Int32,Random.Int32,Variant,
```

**导入逻辑：**
- 跳过 `#` 注释行和一行表头
- 支持 RFC 4180 引号格式（`ParseCsvLine` 方法）
- 取第 2 列（或唯一列）作为 ItemId
- 与已浏览列表匹配并勾选
- 若浏览列表为空（离线模式），直接从 CSV 创建点位

---

## 构建与部署

### 版本号管理

版本号在 `.csproj` 中统一管理：

```xml
<Version>1.8.0</Version>
<AssemblyVersion>1.8.0.0</AssemblyVersion>
<FileVersion>1.8.0.0</FileVersion>
```

同时硬编码在以下位置（需同步更新）：
- `AboutDialog.cs` → 从 Assembly 版本读取（`Assembly.GetExecutingAssembly().GetName().Version`），不再硬编码常量
- `MainForm.cs` → `internal const string WindowTitle`（Program.cs 通过 `MainForm.WindowTitle` 引用，不再各自硬编码）
- `Keygen/Program.cs` → 控制台标题中的版本号
- `Models/LicenseAlgorithm.cs` → SecretKey 中的版本标识字节
- `TextBoxExtensions.cs` → 新增共享文件（P/Invoke 占位符文本扩展）

### 构建命令

```bash
# 完整清理 + 构建（推荐）
dotnet build-server shutdown    # 关闭 Roslyn 编译器缓存服务
rm -rf obj bin                  # 清理中间产物
dotnet build -c Release --no-incremental

# 仅构建主项目（自动触发看门狗和 Keygen 构建）
dotnet build OpcDaToUaGateway.csproj -c Release
```

**重要：** .NET SDK 10.0.301 的 Roslyn 编译器服务（VBCSCompiler）可能缓存过期的编译产物，导致 MSB3030 "找不到 exe" 错误。构建前执行 `dotnet build-server shutdown` 可解决此问题。

### 构建管线

主项目 `.csproj` 包含两个自定义 MSBuild Target：

```xml
<Target Name="BuildAndCopyWatchdog" BeforeTargets="Build">
    <Exec Command="dotnet build Watchdog/...csproj -c $(Configuration) -v q" />
    <Copy SourceFiles="Watchdog/bin/.../OpcDaToUaGateway.Watchdog.exe"
          DestinationFolder="$(OutputPath)" />
</Target>

<Target Name="BuildAndCopyKeygen" BeforeTargets="Build">
    <Exec Command="dotnet build Keygen/...csproj -c $(Configuration) -v q" />
    <Copy SourceFiles="Keygen/bin/.../OpcDaToUaGateway.Keygen.exe"
          DestinationFolder="$(OutputPath)" />
</Target>
```

编译主项目前会自动编译看门狗和 Keygen 项目并复制 exe 到同一输出目录。

**防止源文件冲突：** `<Compile Remove="Watchdog\**" />` 和 `<Compile Remove="Keygen\**" />` 排除子目录中的 .cs 文件，避免 CS0579 重复程序集属性错误。

### DLL 嵌入（Costura.Fody）

通过 `FodyWeavers.xml` 配置 Costura.Fody 在编译后将所有托管依赖 DLL 嵌入主 exe：

```xml
<Weavers>
  <Costura>
    <IncludeAssemblies>
      Newtonsoft.Json
      Opc.Ua.Configuration
      Opc.Ua.Core
      ... (共 33 个程序集)
    </IncludeAssemblies>
  </Costura>
</Weavers>
```

**csproj 引用配置：**

```xml
<PackageReference Include="Costura.Fody" Version="5.7.0">
  <PrivateAssets>all</PrivateAssets>
</PackageReference>
```

嵌入后主 exe 从约 300KB 增长到约 7.8MB，输出目录不再包含任何第三方 DLL。

### 解决方案级别构建

`.sln` 文件中看门狗和 Keygen 项目已移除 `Build.0` 行（仅保留 `ActiveCfg`），避免解决方案级别重复构建子项目。

### 部署文件清单

```
OpcDaToUaGateway.exe              # 主程序（已嵌入所有 DLL）
OpcDaToUaGateway.exe.config       # .NET 运行时声明（必须随 exe 部署）
OpcDaToUaGateway.Watchdog.exe     # 看门狗进程
OpcDaToUaGateway.Keygen.exe       # 授权码计算工具（管理员使用）
config.json                       # 配置文件（需随程序一起部署）
app.ico                           # 应用图标
```

可选文件：
- `OpcDaToUaGateway.pdb` — 调试符号（生产环境可省略）

### 运行环境要求

- Windows 7/10/11 或 Windows Server
- .NET Framework 4.7.2 Runtime（Windows 10/11 通常自带）
- 32 位 OPC DA 服务器（本程序以 x86 运行）
- OPC DA 服务器的 COM 组件已正确注册

---

## 关键设计决策

### 为什么选择 .NET Framework 而非 .NET Core/5+？

OPC DA 基于 COM，Technosoftware DaAeHdaClient 库依赖 .NET Framework 的 COM Interop。虽然 .NET Core 3.0+ 也支持 COM Interop，但该库的目标框架是 net472。

### 为什么必须以 x86 运行？

OPC DA 服务器是 32 位 COM 进程外服务器（LocalServer32）。64 位进程无法直接调用 32 位 COM 组件（需要 COM Surrogate 代理）。以 x86 运行确保 COM 调用在同一进程位宽下完成。

### 为什么使用 `[STAThread]`？

COM 的 Single-Threaded Apartment 模型要求所有 COM 调用在同一线程上执行。`[STAThread]` 标记主线程为 STA，WinForms 消息循环会正确调度 COM 回调。

### 为什么看门狗是独立进程而非线程？

如果看门狗是主程序内的线程，主程序崩溃时看门狗也会随之终止，无法实现崩溃重启。独立进程通过命名 EventWaitHandle 通信，互不影响。v1.3.5 起进一步细化了退出语义：主程序正常退出时通过 `GracefulExitEvent` 告知看门狗"这是计划内的退出，不要重启"，而崩溃时由于无法设置此事件，看门狗自动执行重启逻辑。这样既保证了崩溃恢复能力，又避免了正常退出后看门狗反复拉起主程序。

### 为什么表单关闭采用异步两阶段？

`OnFormClosing` 中直接调用 `_uaServer.StopAsync().GetAwaiter().GetResult()` 会在 UI 线程上同步等待异步操作，可能因 `SynchronizationContext` 回调导致死锁。改为：第一阶段取消关闭→异步清理→重新触发关闭，第二阶段直接退出。v1.3.5 进一步增加 `_isShuttingDone` 标志和 `_closeInProgress` 守卫，确保清理代码只执行一次，同时允许最终的 `Close()` 调用正常到达 `base.OnFormClosing()` 完成窗口销毁（修复了原先可能卡在僵尸状态的问题）。

### 为什么标签数据监控使用 DataGridView 虚拟模式？

DA 回调在线程池线程上执行，UI 刷新在 WinForms 定时器线程上执行。如果 `TagSnapshot` 是可变的（直接修改属性），两个线程会同时读写同一对象产生竞态。改为每次数据变化时用新对象替换整个快照，消除竞态。

### 为什么运行时节点创建用 AddChild + AddPredefinedNode 而非 CreateNode？

SDK 1.5.378.145 的 `CreateNode` 内部调用 `instance.Create(..., assignNodeIds=true)`，在大批量（35000+）场景下对 `BaseDataVariableState` 触发内部 NRE。`CreateNode` 的核心逻辑就是 `parent.AddChild` + `instance.Create` + `AddPredefinedNode`，绕开 `instance.Create` 后，`AddChild` 建立 `Organizes` 引用（Ua 客户端 Browse 遍历的基础）、`AddPredefinedNode` 注册到内部字典（MasterNodeManager 路由查询），两步均经过实际验证稳定。

### 为什么使用 Costura.Fody 嵌入 DLL？

传统部署需要附带 35+ 个 DLL 文件，容易遗漏或版本冲突。Costura.Fody 在编译后将所有托管 DLL 作为资源嵌入主 exe，运行时自动从资源加载，实现"单文件部署"（config.json 和看门狗/Keygen exe 除外）。

### 为什么使用命名 Mutex 实现单实例？

命名 Mutex 是操作系统级内核对象，跨进程可见，比文件锁或进程名检测更可靠。即使程序异常崩溃，操作系统也会自动释放 Mutex，不会留下"死锁"。

### 为什么授权码使用 HMAC-SHA256 而非对称加密？

HMAC 是单向函数，验证时只需重新计算并比较，无需存储解密逻辑。即使授权码被截获也无法反推出 PCID 或密钥。v1.3.5 起密钥采用三层 XOR 混淆 + SHA256 运行时派生方案，避免明文密钥直接暴露在 IL 中。对于工业网关场景，这提供了合理的防复制保护。

### 为什么 PCID 基于 WMI 硬件信息？

WMI 硬件标识（CPU ProcessorId、主板序列号、BIOS 序列号）在同一台机器上稳定不变，但不同机器几乎不可能重复。这确保了授权码与特定硬件绑定，防止授权码被复制到其他机器使用。

---

## 已知限制与改进方向

### 当前限制

1. **OPC UA 安全模式** — 默认 `AutoAcceptCertificates = false`（v1.3.5 起），生产环境需手动信任客户端证书；开发调试阶段可临时设为 true
2. **数据类型映射** — 浏览时统一标记为 "Variant"，未从 DA 服务器读取实际数据类型
3. **单服务器** — 当前仅支持连接一个 OPC DA 服务器
4. **授权码密钥** — 采用三层 XOR 混淆 + SHA256 派生（v1.3.5），但静态数据仍在 IL 中，可通过深度反编译获取，适合基本授权管理场景
5. **无分支 UA 可见性（v1.3.6）** — 当 OPC DA 服务器 ItemId 不含层级时，已通过批量分组方案（FlatTags/Batch_N）缓解，但大批量 Browse 可见性取决于 SDK 内部引用表机制，需持续验证

### 可优化方向

1. **接口抽象** — 为 OpcDaClient、GatewayOpcUaServer、DataBridge 提取接口，支持模拟模式和单元测试
2. **OPC DA 服务器多实例** — 支持同时连接多个 DA 服务器，聚合数据到同一 UA 服务器
3. **在线授权管理** — 支持联网验证授权码，替代纯离线 HMAC 方案
4. **UA 地址空间** — 支持非标准路径分隔符（目前仅支持 "."）

---

## 版本历史

| 版本 | 日期 | 变更 |
|---|---|---|
| **V2.1.0** | 2026-07-23 | **全面代码审查修复**：① String 类型标签在 DA 浏览时数据类型显示错误（`ExtractDataType`/`FillRealDataTypes` 中 `!= "String"` 排除条件移除）；② `ItemSelectionDialog` 独立 CSV 导出格式与 `CsvTagExporter` 统一为 5 列格式（表头 `序号,ItemId,名称,数据类型,描述`，头部 `#修改C3`，空行 `,,,,`）；③ MainForm 图标加载路径改为 `opc-da-opc-ua.ico`；④ `ConfigManager` FileSystemWatcher debounce Timer 泄漏修复（提升为字段 + `Interlocked.Exchange`）；⑤ `FillRealDataTypes` 临时 Group 添加 UpdateRate；⑥ `GatewayOpcUaServer.StopAsync()` 添加 `_disposedInt` 守卫；⑦ `HealthSnapshot` 空 catch 替换为 Debug.WriteLine；⑧ Watchdog 重启传递 `--minimized` 参数；⑨ `DataBridge.Start()` 标记 `[Obsolete]`。编译 0 警告 0 错误。\n| 2.0.0 | 2026-07-21 |**DA 标签真实数据类型获取**：浏览阶段通过临时 OPC DA Group 调用 AddItems，从 OpcDaItem.CanonicalDataType 提取实际数据类型（Integer、Float、Boolean 等），替代原有的固定 Variant 描述；分批处理（每批 500 个点位），失败时静默回退不影响浏览结果。版本号 1.5.0 → 2.0.0。编译 0 警告 0 错误。
| 1.9.0 | 2026-07-20 |**全面代码审查 + 启动卡顿最终修复 + DA模式切换**：① DataBridge.StartAsync 异步启动（Task.Run 后台线程创建节点 + SynchronizationContext.Post 进度回调），3.5 万节点场景窗口保持响应；② 新增 DA 数据获取方式选择（异步订阅/同步轮询），UI 下拉框 + 配置持久化；③ 首次运行默认填充 ProgId Matrikon.OPC.Simulation.1，开箱即用；④ 未选择服务器时禁用获取点位和启动网关按钮；⑤ Boolean 类型转换增强（支持字符串 true/1/yes 等）；⑥ SourceTimestamp 单调递增修复（bool 翻转标签可被 UA 客户端正确检测）；⑦ Dispose 后重连检查、Monitor.Exit 安全检查、SafeInvoke 句柄防护；⑧ ConfigManager 实现 IDisposable；⑨ 版本号 1.5.0 → 1.9.0；⑩ 删除 PLAN.md（文档整合完成）。编译 0 警告 0 错误。
| 1.8.0 | 2026-07-16 | **移除「已连接客户端」列表功能并升级版本号**：撤销 V1.7.0(2026-07-15) 在「OPC UA 服务器设置」区右侧新增的「已连接客户端」列表——删除 `MainForm` 的 `_grpClients`/`_lstClients` 控件、`RefreshConnectedClients()` 定时器刷新逻辑及 `SetUiRunningState` 停止清空逻辑；删除 `GatewayOpcUaServer.GetConnectedClients()` 方法与 `GatewayServer.ServerInternalAccess` 属性、`IGatewayOpcUaServer.GetConnectedClients()` 接口声明；OPC UA 设置区分组高度回退至 150、布局恢复紧凑。撤销原因：该列表每 1~3 秒在 UI 线程经 `SessionManager.GetSessions()` 访问 OPC UA SDK 会话管理器，与 UA 客户端请求线程竞争 SDK 内部锁，导致窗口「未响应」；移除后该访问路径彻底消除。**启动卡顿修复：** 启动网关后窗口「未响应」已定位并修复——根因为 `GatewayNodeManager.AddVariableNode` 在每次创建变量节点时调用 `Diag()`，经 `OnStatusChanged`→`_log.Append`→`BeginInvoke` 向 UI 线程投递数万次日志更新（`LogManager.UpdateTextBox` 每次 O(文本长度)、累计 O(n²)），3.5 万节点场景导致约 7 分钟卡死；该 `Diag` 路径独立于 `DataBridge` 已节流的 `Log`，此前排查时未被覆盖（"已排除诊断日志洪泛"结论不准确）。已移除该逐节点诊断调用；节点创建仍走 O(1) 的 `AddPredefinedNode`（经反编译 `Opc.Ua.Server.dll` 1.5.378.145 确认其仅做 `PredefinedNodes` 字典注册 + 空子节点递归，无逐节点通知/地址空间重建）。三个项目版本号统一升至 1.8.0；编译 0 警告 0 错误 ✅ |
| 1.7.0 (UA 设置区 UI 调整) | 2026-07-15 | **调整「OPC UA 服务器设置」区（版本号保持 1.7.0 不变）**：① 监听地址与安全模式下拉框宽度统一为 140；② 标签「端口」→「端口号」、「最大会话数」→「连接数」，且端口号与连接数左对齐（标签 x=250、控件 x=310）；③ 「自动接受客户端证书」移至安全模式下方独立行；④ 删除「当前安全配置为开放模式，生产环境建议启用加密」警告标签及其 `UpdateSecurityWarning` 逻辑；⑤ 右侧空余区新增「已连接客户端」列表（`GroupBox` + `ListBox`），由 UA 服务器 `GetConnectedClients()`（经 `IServerInternal.SessionManager.GetSessions()` 读取活动会话的 `SessionDiagnostics.SessionName` / `ClientDescription.ApplicationName` / `ApplicationUri`）借 `RefreshStats` 定时器定期刷新、停止时清空；`OPC UA 服务器设置` 分组高度 130→150；编译 0 警告 0 错误 ✅ |
| 1.7.0 (时间戳同源统一) | 2026-07-15 | **修复 UA 客户端与网关监控时间戳不一致（版本号保持 1.7.0 不变）**：原 `DataBridge` 将 DA 源戳 `value.Timestamp.LocalDateTime` 传给 UA、`UpdateValue` 内算 `max(DA源戳UTC, 网关UTC, last+1tick)`，而监控快照另取 `DateTime.UtcNow`，两侧时间戳基准不同源（DA 源戳冻结或时钟偏差时系统性错位）；改为 `OnDaDataChanged` 内统一取网关接收时刻 `recvUtc`（UTC）同源传给 UA 与本地快照，`UpdateValue` 简化为 `ts = max(srcUtc, lastTs+1tick)`，保证 UA SourceTimestamp 与监控快照时间戳严格同源、代表同一瞬间；值差异经确认属 OPC UA 订阅正常延迟（UA 客户端按自身 `PublishingInterval`/`SamplingInterval` 收值，滞后最多一个发布周期），非网关缺陷，建议在 UA 客户端调小发布间隔收敛；编译 0 警告 0 错误 ✅ |
| 1.7.0 (运行时修复) | 2026-07-15 | **三个运行时缺陷修复（版本号保持 1.7.0 不变）**：① **浏览点位 BeginInvoke 句柄异常**：`ItemSelectionDialog` 后台线程在对话框窗口句柄创建前裸调 `BeginInvoke` 抛 `InvalidOperationException`，新增 `SafeBeginInvoke`（句柄未就绪时订阅 `HandleCreated` 延后执行，与 `MainForm.SafeInvoke` 防护范式一致）替换三处裸调用；② **监控时间戳显示错误**：标签数据监控「时间戳」列直接显示 UTC 的 `TagSnapshot.Timestamp`，北京用户(UTC+8)看到的时间慢 8 小时，改为显示层 `ToLocalTime()` 转换（数据模型仍保持 UTC 以保证 UA SourceTimestamp 规范）；③ **UA 客户端质量大量 Bad**：`OpcDaClient` 质量判定误用操作结果 `value.Error.Succeeded`，应改用数据质量位 `value.Quality.Status & 0xC0 == 0xC0`（OPC DA 规范，高 2 位 0xC0=Good），并移除因操作结果误丢有效数据的硬跳过，新增 `IsQualityGood(OpcDaQuality)` 公共方法（异步 `OnValuesChanged` / 定时 `DoSyncRead` 两处回调同步修正）；编译 0 警告 0 错误 ✅ |
| 1.7.0 | 2026-07-13 | **定稿发布**：整合 V1.6.0~V1.6.3 全部变更，并移除 V1.6.2 临时诊断日志（`[诊断-DA]`/`[诊断-bool]`）；三个项目版本号统一升至 1.7.0；编译 0 警告 0 错误 ✅ |
| 1.7.0+ | 2026-07-13 | **UI 排版微调**：`AppConstants.WindowTitle` 由 `"OPC DA → OPC UA 网关 v" + AppVersion` 改为 `"OPC DA → OPC UA 网关"`（去掉版本号）；`MainForm.cs` 数据获取下拉（`lblDaMode`/`_cmbDaMode`）从 y=100/96 上移至 y=70/66，与「获取点位」按钮（y=65）对齐到同一行 |
| 1.6.3 | 2026-07-13 | **翻转标签数值/时间戳不更新（根因修复）**：根因为 OPC DA 服务器给 bool 标签打出的源时间戳长期冻结（实测约 8-9 分钟才变化一次），而数值本身在快速翻转。`GatewayNodeManager.UpdateValue` 将单调时间戳下限由 `last+1 tick` 改为**网关当前 UTC 时钟**（取 max(DA源戳, 网关UTC, last+1tick)），冻结戳下时间戳按真实时间推进，OPC UA 变化检测每轮都触发；`DataBridge` 快照时间戳改用网关接收时刻(UTC)，消除 UI「数值在动、时间戳冻结」误导显示 |
| 1.6.2 | 2026-07-13 | **bool 冻结排查（临时诊断）**：新增一次性丢弃诊断（`[诊断-DA]`）与 bool 到达诊断（`[诊断-bool]`），并鲁棒化 Boolean 类型转换（`short`/`int`/`string` 兜底）。**该版本诊断日志已在 V1.7.0 移除** |
| 1.6.1 | 2026-07-13 | **翻转标签时间戳去重修复（初版）**：`UpdateValue` 将 DA 源时间戳原样当作 UA SourceTimestamp 改为网关时钟生成的严格单调递增 UTC 时间戳，修复 DA 源戳重复/非单调被 OPC UA 按(值,状态,源时间戳)去重、导致翻转标签卡住的问题（初版力度不足，V1.6.3 强化） |
| 1.6.0 | 2026-07-13 | **新增 OPC DA 同步/异步获取模式**：`OpcDaConfig.Mode`/`GetEffectiveMode()` + UI「数据获取」下拉（`Async`/`Sync`，默认 `Async`）；`OpcDaClient.Start(updateRateMs, mode)` 按模式分支——Async 维持订阅+5分钟兜底，Sync 按 `UpdateRateMs` 定时 `group.Read` 轮询；`TryReconnect` 复用模式 |
| 1.5.0 | 2026-07-04 | **ponytail 代码优化（8项）**：
**R1** 删除 `Services/GateController.cs`（~150 行），其 5 个事件均为 `=> OtherEvent?.Invoke(...)` 透传，无业务逻辑；MainForm 直接订阅 `_gatewayMgr` / `_watchdogMgr` 事件；GatewayManager 新增 `RunningStateChanged` 事件；
**R2** 新建 `Services/LicenseManager.cs`，封装授权码验证 / Stopwatch 试用期计时 / 状态事件通知；MainForm 删除 `_isLicensed` / `_pcid` / `_licenseTimer` / `_trialStopwatch` / `_trialExpired` 五个字段，UI 通过事件回调更新状态栏；
**R3** MainForm 提取 `SafeInvoke(Action)` 方法，替换 4 处 `InvokeRequired → Invoke` 模式；
**R4** DataBridge 删除 `_cachedDisplayName` 字典（永远命中 `TryGetValue`）和 `TagSnapshot.Equals` / `GetHashCode`（全项目无人使用）；`Start()` 中 7 条 `⚠` 诊断日志迁至 `GatewayOpcUaServer` 的 `[Conditional("DEBUG")]` 方法；
**R5** LogManager 文件头 80 字注释块移除、5 个字段注释移除、`OperationCanceledException` 死 catch 移除、WriterLoop 头部 5 段注释移除（净减 ~80 行）；
**R6** HealthSnapshot `CountStatusFlips` / `TrimDailyCache` / `RewriteDailyFile` / `ComputeGrowthRate` 内联到 `AppendDailySummary`；注释从 90→30 行；
**R7** `GatewayOpcUaServer.Diag()` 标记 `[Conditional("DEBUG")]`，Release 模式下所有诊断日志自动移除；
三个项目版本号统一升到 1.5.0；编译 0 警告 0 错误 ✅ |
| 1.4.3 | 2026-07-03 | **ponytail-review 执行（过度工程清理）**：删除 `Services/GatewayFactory.cs`（56 行），生产代码从未使用该工厂层，抽象半吊子（StartAsync 中强转回具体类型）；`GatewayManager` 移除 `_factory` 字段与构造函数可选参数，三处组件创建改为直接 `new`；`SnapshotData` / `DailySummary` 从 `HealthSnapshot` 嵌套私有类移至 `Models/` 目录（`Models/SnapshotData.cs`、`Models/DailySummary.cs`），`HealthSnapshot.cs` 从 439 行降至 ~320 行；`IHealthSnapshot.GenerateDailySummary()` 返回类型从 `object` 修正为 `DailySummary`；`ComputeGrowthRate()` 改用 `_dailyCache` 内存列表替代每次重读 `health_daily.jsonl` 文件（新增 `LoadDailyCache()` / `TrimDailyCache()` / `RewriteDailyFile()` 三个私有方法，`AppendDailySummary()` 写入后同步更新内存缓存）；`IOpcDaClient` / `IGatewayOpcUaServer` / `IDataBridge` 三个接口保留（被 `GateController` 和 `MainForm` 实际使用，是有价值的抽象）；净减约 89 行；编译 0 警告 0 错误 |
| 1.4.2 | 2026-07-03 | **PLAN 3.5 长期内存监控**：H-36 HealthSnapshot 升级。新增 IHealthSnapshot.OnAlert 事件与 GenerateDailySummary() 公开方法；HealthSnapshot 内部 DailySummary 类（17 字段：工作集/私有内存/GC 堆/更新速率/DA 断开次数/增长率等）+ AppendDailySummary / ComputeGrowthRate / RotateDailyFile 三方法；Capture 末尾 00:05~00:10 聚合窗口检查 + _captureLock 防重入；GateController 转发 MemoryAlertRaised 事件；MainForm.ShowMemoryAlert 更新状态栏颜色（黄色 20%、红色 50%）；数据持久化 health/health_daily.jsonl 追加 O(1)，90 天自动轮转；幂等保护同日只写一次；.NET Framework 4.7.2 兼容（TakeLast 改 Skip+Take）；编译 0 警告 0 错误 |
| 1.4.1 | 2026-07-03 | **PLAN 3.1 接口抽象与单元测试基础设施**：新建 `Services/Interfaces/IOpcDaClient.cs`、`IGatewayOpcUaServer.cs`、`IDataBridge.cs` 三个核心接口，解耦 GatewayManager/GateController 与具体类实现；OpcDaClient/GatewayOpcUaServer/DataBridge 三个实现类添加接口继承；GatewayManager 字段类型（_daClient/_uaServer/_bridge）改为接口类型，公开属性同步调整；DataBridge.GetSnapshots() 返回类型从 `List<TagSnapshot>` 改为 `IReadOnlyList<TagSnapshot>`；OpcDaClient.IsConnected 由 `public volatile bool` 字段改为 `private volatile bool _isConnected` + `public bool IsConnected => _isConnected` 属性包装（满足接口契约 + 保持线程安全）；IGatewayOpcUaServer 新增 `NamespaceUri` 属性；MainForm._cachedSnapshots 字段类型同步调整为 IReadOnlyList；新建 `FakeOpcDaClient` 测试桩（提供 RaiseDataChanged/RaiseDisconnected/RaiseConnected 显式触发方法，模拟现场故障场景）；编译 0 警告 0 错误 |
| 1.4.0 | 2026-06-24 | **第二轮代码审查 P0/P1 修复（7项）**：<br>**P0-R5** RefreshStats 自适应哈希修复（TagSnapshot 覆写值语义 GetHashCode/Equals，降频机制恢复，CPU 降低 67%）；<br>**P0-R8** ConfigManager.DoSave 异常恢复修复（finally 块用原始引用恢复 Tags 替代 new List，杜绝序列化异常导致数据丢失）；<br>**P1-R1** ConvertValue COM Variant 兼容性增强（DBNull/边缘类型显式处理）；<br>**P1-R2** OnFormClosing 移除冗余 Timer Dispose；<br>**P1-R3** ConvertValue 预编译委托缓存（switch-case → 13 个 Func 委托，消除热路径分支预测开销）；<br>**P1-R6** AppConstants 常量化迁移（11 个分散 Magic Number 统一引用）；<br>**MainForm.WindowTitle** 改用 AppConstants 引用消除硬编码重复 |
| 1.3.9 | 2026-06-24 | **N-7 UA 节点注册顺序修复**：DataBridge.Start() 原先遍历 `_tagMap.Values`（ConcurrentDictionary 迭代顺序不保证与插入顺序一致），改为按 `_orderedKeys` 有序列表遍历，确保 OPC UA 地址空间中的节点注册顺序与 OPC DA 扫描顺序完全一致 |
| 1.3.8 | 2026-06-23 | 代码分析 v2 增量修复（6 项）：**N-1 导出 async**（BtnExportTags_Click 已有 async+Task.Run，35k 标签不冻 UI）；**N-2 SetUiRunningState**（按钮状态管理已统一提取为独立方法）；**N-3 ComputeUaBrowsePath 移至业务层**（静态方法已从 MainForm 移至 GatewayOpcUaServer）；**N-4 移除冗余 AssignTagKeys**（MainForm 中已删除重复调用，对话框已提前分配）；**N-5 NamespaceIndex 立即持久化**（新增 GatewayOpcUaServer.OnConfigChanged 回调 → GatewayManager.ConfigDirty 事件 → MainForm 直接触发 ConfigManager.Save()，不依赖 500ms 防抖定时器，防止进程崩溃导致索引丢失）；**N-6 导出字符串拼接优化**（$"" 插值 → 连续 sb.Append 调用，减少中间 string 分配）；版本号 1.3.7→1.3.8 |
| 1.3.7 | 2026-06-23 | 增量功能与修复：**Task #35 导出点表升级**（9 列 UA 完整信息：NodeId/NamespaceUri/EndpointUrl/BrowsePath）；**Task #36 导入后立即分配 TagKey**（ItemSelectionDialog.BtnOK_Click 中提前分配）；**导出按钮运行中恢复可用**；**NamespaceIndex 三级回退**（运行时实际值 → 配置值 → 默认 2）；**FlatBatchSize 硬编码消除**（统一使用 AppConstants.UaFlatBatchSize）；**H-26 TitaniumAS 迁移**（Technosoftware 商业库 → TitaniumAS.Opc.Client MIT 开源）；**BouncyCastle 2.4.0→2.6.2** |
| 1.3.6 | 2026-06-23 | 关键功能修复与优化：**H-23 CreateNode NRE 修复**（SDK 1.5.378.145 兼容，改用 AddChild + AddPredefinedNode 两步创建节点）；**H-24 无分支 UA 可见性修复**（GetOrCreateFolder 不直接返回 _daTagsFolder，绕开 MasterNodeManager 初始化路由缓存）；**H-24-1 批量分组方案**（FlatTags/Batch_N 层级，每批 1000 个变量，避免单节点引用过多触发 SDK 边界条件）；**H-25 DA 浏览超时修复**（BrowsePageSize=500 分页，解决 35000+ 点位单次 COM 调用超 DCOM 20s 限制）；**定时同步兜底**（5 分钟 DoSyncRead 全量拉取，防止异步回调丢包导致数据停滞）；**UI 诊断过滤**（[诊断] 前缀消息不写入运行日志，减少 UI 刷屏）；**深度诊断工具**（INodeBrowser Browse 计数、GetChildren 子对象验证、FlatTags 层级诊断） |
| 1.3.5 | 2026-06-18 | 关键安全与健壮性修复（17项CRITICAL）：C-01 注册表单例不Dispose、C-02 STA线程独立字典+协作取消、C-03 GatewayManager TOCTOU竞态修复、C-04 CheckHealth加锁读DA客户端、C-05 OpcDaClient生命周期互斥锁、C-06 UA服务器StopAsync死锁修复（Task.Run包装）、C-07 LogManager后台线程无限等待、C-08 IOException安全降级、C-09 SaveProgId加锁保护、C-10 AutoAcceptCertificates默认值改为false、C-11 授权码密钥三层XOR混淆+SHA256派生、C-12 心跳事件ManualResetEvent修复、C-13 WatchdogManager线程安全锁、C-14 配置加载失败禁用交互控件、C-15 OnFormClosing双阶段修复允许正常销毁、C-16 crash.log线程安全写入、C-17 Dispose原子化（Interlocked.Exchange）；看门狗生命周期重设计：托盘退出不关闭看门狗（SignalGracefulExit机制）、watchdog.log移至logs目录、三个项目版本统一1.3.5、Keygen/Watchdog添加应用图标、全局代码注释完善 |
| 1.3.4 | 2026-06-16 | 关键修复与优化：P0 健壮性（全局异常处理器+crash.log、OnFormClosing 死锁修复、GatewayManager 启动回滚链、DA 回调异常上报、LogManager Dispose 竞态修复）；P1 性能（OpcDaClient ConcurrentDictionary、DataBridge 双查找消除、ItemSelectionDialog 防抖+辅助方法、ServerSelectionDialog 零分配过滤、MainForm 批量布局刷新）；P2 资源（DataBridge 幂等 Dispose、UA Server 停止后释放、autoStartTimer 生命周期、StopAsync 正确释放顺序、Watchdog 进程句柄释放、AboutDialog Icon 释放）；P3 安全策略（安全模式 None 策略条件添加）；P4 安全（PCID 生成失败异常、证书 SubjectName 使用实际监听地址）；P5 代码质量（版本号统一从 Assembly 读取、DiagnosticLog 线程安全、Tags 默认空列表、过滤保留勾选状态、ConfigManager 定时器复用+锁竞态修复、Keygen EOF 处理、DllImport SetLastError） |
| 1.3.3 | 2026-06-15 | 全面优化：P0 线程安全（GatewayManager volatile+lock、OpcDaClient 字典加锁、ConfigManager 原子写入、async void 异常捕获）；P1 性能（UA 节点 O(1) 缓存、DataBridge 类型缓存、配置保存防抖）；P2 资源（DataBridge/UA Server IDisposable、LogManager 定时 Flush、Timer 完整释放）；P3 设计（TextBoxExtensions 共享提取、窗口标题常量、config.json PreserveNewest）；P4 功能（移除数据双重投递、DA 远程服务器支持） |
| 1.3.2 | 2026-06-15 | LogManager 异步日志写入：文件持久化改为 BlockingCollection 队列 + 后台线程模式，StreamWriter 常驻打开（按日切换），消除高频日志对 UI 的阻塞；LogManager 实现 IDisposable，OnFormClosing 时优雅关闭 |
| 1.3.1 | 2026-06-15 | MainForm 职责拆分：提取 LogManager（日志管理）、ConfigManager（配置管理）、WatchdogManager（看门狗管理）、GatewayManager（网关生命周期）四个独立服务类，MainForm 从约 1700 行精简至约 650 行，仅保留 UI 协调与用户交互逻辑 |
| 1.3.0 | 2026-06-12 | 授权码机制（PCID + HMAC-SHA256）、30 分钟试用倒计时、关于对话框增加 PCID 显示和授权码输入、授权码计算工具 (Keygen)、System.Management WMI 引用 |
| 1.2.0 | 2026-06-12 | 单实例限制、OPC UA 可配置设置（监听地址/安全策略/证书/会话数）、关于对话框、Costura.Fody DLL 嵌入、窗口标题显示版本号 |
| 1.1.0 | - | TagKey 机制、DataBridge 线程安全、COM 泄漏修复、异步关闭、BrowseAllItems 去重移除 |
| 1.0.0 | - | 初始版本：DA→UA 网关、看门狗、系统托盘、日志、配置管理 |
