# KyjScada.Next 工程文档 · 总目录

> **这份文档集的唯一目标**：换一个从没看过这个仓库的人（或另一个 AI 模型），只读这几篇，就能
> 改对一个功能、不碰坏现场数据、并且知道改完怎么验证。
>
> 写作原则：**只写代码里真实存在的东西**。类型名、方法名、文件路径、命令都是可核对的；
> 凡是"还没做/有坑"的地方都直接写出来，不粉饰。

---

## 0. 一句话认识这个项目

KyjScada.Next 是老的 `KYJScadaProWpf`（单窗口几千行、采集/界面/存储缠在一起）的重写版：
**.NET 8 + WPF**，按 `Domain → Contracts → Infrastructure → Runtime → App/Host` 分层，
采集协议做成"描述符 + 传输"插件式，历史数据改成**长表**（一行一个变量值），
并内置一个 **MCP 服务**让外部 AI 客户端能只读查询历史/报警。

现场当前实际跑的是：**Modbus RTU over TCP**（RTU 帧 + CRC16，走 TCP 连接，不是 Modbus TCP 的 MBAP），
目标是 `127.0.0.1:502` 的模拟器；配置里另有 Modbus TCP、Modbus RTU（串口）两套文件备用。

---

## 1. 五次命令看懂全貌

```powershell
# 1) 编译(必须先杀掉正在跑的界面,否则文件被占用 → MSB3027)
taskkill /IM KyjScada.App.exe /F
cd D:\DeepSeekHarness\KyjScada.Next
dotnet build KyjScada.Next.sln -v m          # 期望: 0 错误（124 条既有警告）

# 2) 跑界面
.\KyjScada.App\bin\Debug\net8.0-windows\KyjScada.App.exe

# 3) 跑测试(纯控制台,不是 xUnit)
.\KyjScada.Domain.Tests\bin\Debug\net8.0\KyjScada.Domain.Tests.exe
.\KyjScada.Infrastructure.Tests\bin\Debug\net8.0\KyjScada.Infrastructure.Tests.exe

# 4) 跑"模块化架构实证"(7 组 / 32 条断言,证明模块能独立加载/故障隔离/依赖检查)
.\KyjScada.Verify\bin\Debug\net8.0\KyjScada.Verify.exe

# 5) MCP 独立进程自检(不启界面、不开 HTTP,直接调 9 个工具函数)
.\KyjScada.Mcp\bin\Debug\net8.0\KyjScada.Mcp.exe --settings "<...>\settings" --db "<...>\data\history.db" --selftest
```

当前基线（2026-11-06 实测）：

| 检查 | 结果 |
|---|---|
| `dotnet build` | 0 错误 / 约 120 条既有警告（nullable、未使用字段一类提示，非本轮引入） |
| `KyjScada.Domain.Tests` | **43 通过 / 0 失败** |
| `KyjScada.Infrastructure.Tests` | **316 通过 / 0 失败 / 1 跳过** |
| `KyjScada.Verify` | **7 个测试组 / 32 条断言全 PASS** |

---

## 2. 文档地图：按"你想干什么"查

| 我想…… | 先读 | 再读 | 主要改这里 |
|---|---|---|---|
| 搞懂整体结构和数据怎么流 | [01 架构与数据流](01-架构与数据流.md) | — | — |
| 改配置、加设备/变量、搞清 JSON 字段 | [02 配置与数据文件](02-配置与数据文件.md) | 09 的配方 2 | `settings\*.json`（**不是** bin 里改！） |
| 加一种新协议（西门子/三菱/OPC…） | [09 扩展手册](09-扩展手册.md#配方-1加一种协议) | 03 Domain | `KyjScada.Domain\Protocol\` + `AcquisitionFactory` |
| 改采集行为（周期、重连、读块） | [05 Infrastructure](05-Infrastructure.md) | 06 Runtime | `ModbusAcquisitionService.cs` / `DevicePipeline.cs` |
| 改报警判定（阈值、死区、确认） | [04 Domain](04-Domain.md) + [06 Runtime](06-Runtime.md) | — | `AlarmStateMachine.cs` / `AlarmService.cs` |
| 改归档（写库周期、保留期、容量） | [06 Runtime](06-Runtime.md) | 02 的表结构 | `ArchiveService.cs` / `HistoryRepository.cs` |
| 改界面（页签、颜色、按钮） | [07 App 界面](07-App-界面.md) | — | `MainWindow.xaml(.cs)` / `App.xaml` |
| 加一个页签 | [09 扩展手册](09-扩展手册.md#配方-4加一个页签) | 07 | `MainWindow.xaml` + `MainWindow.xaml.cs` |
| 给 AI 开放更多 MCP 工具 | [08 MCP 与辅助程序](08-MCP与辅助程序.md) | 09 配方 5 | `KyjScada.Mcp\Tools\` |
| 查现场问题（通讯断、库锁、卡顿） | [10 构建调试与排障](10-构建调试与排障.md) | — | — |
| 加测试 / 判断改完能不能交付 | [11 测试与验收基线](11-测试与验收基线.md) | — | `KyjScada.*.Tests\` |
| 要改历史库表结构（跨设备同名变量、时间精度） | [13 历史库改造方案](13-历史库改造方案-跨设备与时间精度.md) | 02 的表结构 | `HistorySchema.cs` / `HistoryRepository.cs` |
| 接读屏软件 / 写 UI 自动化脚本 | [14 无障碍与 UI 自动化](14-无障碍与UI自动化.md) | 07 | 各窗口 `.xaml` 的 `AutomationProperties` |

---

## 3. 目录与"哪些文件是数据"

```
D:\DeepSeekHarness\KyjScada.Next\        ← 源码根（没有 git 仓库）
├─ KyjScada.Next.sln                     11 个项目
├─ KyjScada.Contracts\      (348 行)     接口/契约，零引用
├─ KyjScada.Domain\         (2.2k 行)    配置模型、协议描述符、报警状态机、密码、历史表定义
├─ KyjScada.Infrastructure\ (4.6k 行)    Modbus 帧/传输、采集服务、三个 SQLite 仓储、迁移、XLSX
├─ KyjScada.Runtime\        (2.1k 行)    并发调度、写闸门、归档、报警服务、趋势聚合/统计
├─ KyjScada.App\            (8.4k 行 + 1.7k XAML)  WPF 界面、主题、自绘图表、MCP 宿主
├─ KyjScada.Host\           (403 行)     插件式模块宿主（IModule 加载/隔离/依赖检查）
├─ KyjScada.Mcp\            (1.3k 行)    MCP 服务器（独立进程 & 进程内）
├─ KyjScada.Verify\         (351 行)     模块化架构实证程序
├─ KyjScada.Migrate\        (150 行)     老库 → 新库一次性迁移 CLI
├─ KyjScada.Domain.Tests\                配置/协议/报警状态机测试（43）
├─ KyjScada.Infrastructure.Tests\        Modbus/仓储/趋势/MCP 测试（316）
├─ docs\                                  ← 本文档集
└─ PROGRESS.md                           开发过程记录（含每轮的实测证据，改代码前值得翻）

KyjScada.App\bin\Debug\net8.0-windows\    ← 运行时目录（程序以"程序目录"为根找文件）
├─ KyjScada.App.exe
├─ settings\  ModbusTCP.json / ModbusRTU.json / ModbusRTUOverTCP.json
└─ data\      history.db / alarms.db / users.db / app_settings.ini / ui_state.ini
              trend_templates.ini / mcp_debug.log / ui-latency.log(诊断时才有)
```

**关键区分**：`bin\Debug\net8.0-windows\settings\*.json` 是**当前生效的现场配置**，
源码树里没有 `settings\`。改配置就是改这三个 JSON；界面上"配置编辑器"改的也是它们。

---

## 4. 红线（这几条不是建议，是硬约束）

1. **不要动运行中的库文件**：`data\history.db`（含 `-wal`/`-shm`）在程序运行时被占用。
   改表结构必须停机，或先备份再改。`_kyj_copy.db`（老库副本）**绝对不能删**——迁移测试要用。
2. **不要让 `KyjScada.Mcp` 以写方式打开库**：它连接串写死了 `Mode=ReadOnly`。
   要加写入能力，必须走进程内 `McpHost` 的 `writeHandler`，不能改成读写连接（会和界面抢锁）。
3. **不要相信 WPF 默认模板**：禁用态按钮/选中态页签的配色由模板触发器决定，
   在 XAML 上写 `Foreground` 会被模板覆盖（已踩过：禁用按钮对比度只有 1.31:1，肉眼几乎看不见）。
   改配色一律改 `App.xaml` 里的模板，不要只改控件属性。
4. **页签选中时文字必须是黑色 `#11111B`**（用户明确要求，白底白字看不清）。
5. **不要在 UI 线程上 `.GetAwaiter().GetResult()`**：启动/停止 MCP 曾因此死锁。
   必须 `Task.Run(...)` + 有界 `Wait`。
6. **切设备不等于停采集**：`ApplyDevice` 只切"当前显示哪条流水线"，
   其它设备继续采集（老系统的安全盲区就是切页签把别的设备停了）。
7. **控制台可能显示乱码**（GBK/UTF-8 混用），不是程序坏了；看日志用 UTF-8 读文件。

---

## 5. 术语表（文档里一直在用）

| 词 | 含义 | 代码里的位置 |
|---|---|---|
| 项目 Project | 一个 `settings\*.json` 文件里的一级数组元素，含 11 个协议键 | `ProjectConfig` |
| 设备 Device | 一个通讯端点（IP:端口 或 串口），含多个组 | `DeviceConfig` |
| 组 Group | 一段连续寄存器区（`Start` + `Length`），一次读取的单位 | `GroupConfig` |
| 变量 Variable | 一个位号（如 `LQT_Level`），有类型/缩放/报警/归档开关 | `VariableConfig` |
| 位号 Tag | 变量名在运行时的身份，总线按 tag 存值 | `ValueSnapshot[tag]` |
| 快照 Snapshot | 一轮采集的结果（不可变，带 `Version`/`Timestamp`） | `ValueSnapshot` |
| 流水线 Pipeline | 一台设备一条：采集 + 报警 + 归档 + 总线，独立线程 | `DevicePipeline` |
| 调度器 Coordinator | 管所有流水线的创建/起停 | `AcquisitionCoordinator` |
| 写闸门 Gate | 所有写入的唯一通道：开关/可写性/限速/占用/审计 | `CommandGate` |
| 归档 Archive | 把 `ArchiveEnable=true` 的变量按间隔写进 `History` 长表 | `ArchiveService` |
| 长表 | 一行一个变量值（不是一行 N 列宽表），省空间、加变量不改表 | `History` 表 |
| 描述符 Descriptor | 一个协议"怎么把配置翻译成地址"的规则对象 | `IProtocolDescriptor` |
| 事务边界 | 一次 `WritePoints` 内所有点同进同出，不半途可见 | `HistoryRepository` |

---

## 6. 版本与已知缺口（诚实清单）

- 目标框架：`net8.0-windows`（App）/ `net8.0`（其余）。目录里残留 `net9.0` 旧产物，**不要参考**。
- 测试框架：自研控制台 runner（`TestRunner` + `Assert`），**不是 xUnit/NUnit**，没有 `dotnet test`。
- 没有 DI 容器：`MainWindow` 手工 `new` 出所有服务（见 07 篇"组合根"）。
- `MainWindow.xaml.cs` 有 **3,236 行**，是最大的单文件；拆分方案见 07 篇末尾。
- `VariableConfig.Scale/Offset` 目前**只读写不生效**：采集链路没有套用（`ModbusValueCodec` 不读它们）。
  现场配置里都是 `1.0/0.0`，所以没暴露问题；要做工程量变换得自己加。
- `KyjScada.Mcp` **没有任何 ProjectReference**：它自己复制了一份 SQLite 表结构知识
  （`Data\Models.cs`），所以表结构一改两处都要改。见 08 篇。