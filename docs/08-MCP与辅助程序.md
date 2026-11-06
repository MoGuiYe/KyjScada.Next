# 08 MCP、Verify、Migrate 与 Host

> **读者**：要让 AI/外部工具读写现场数据、要跑架构实证、要迁老库、要理解插件宿主现状的人。
> **读完能做到**：知道 MCP 9 个工具的输入输出与权限边界、`KyjScada.Mcp` 为什么"自己抄表结构"、
> `Verify` 的 6 项断言分别证明什么、`Migrate` 怎么用。

---

## 1. 进程内 MCP（App 里跑的那份）

### 宿主：`KyjScada.App\McpHost.cs`（187 行，static）

```csharp
McpHost.Start(settingsDir, dbPath, port, Func<string,double,string>? writeHandler)
McpHost.Stop()
McpHost.Endpoint / Status / Running / DiagLog
```

- 传输：**Streamable HTTP**，默认 `http://127.0.0.1:18321/mcp`（`AppSettings.McpPort/McpEnabled`）。
- 实现栈：`WebApplication` + `ModelContextProtocol.AspNetCore`（`WithHttpTransport().WithTools<...>().MapMcp("/mcp")`）。
- **线程模型**：Kestrel 跑在后台；`Start/Stop` 必须 `Task.Run` + 有界 `Wait`，
  **禁止**在 UI 线程 `StopAsync().GetAwaiter().GetResult()`（会卡 WPF `DispatcherSynchronizationContext`）。
  `SettingsWindow` 保存后走 `MainWindow.RestartMcpHost()`（后台停+起）。
- `writeHandler` = `MainWindow.McpWrite`：回 `Dispatcher.Invoke` → `CommandGate.Write(varName, value, "MCP")`，
  并把结果写到界面 `WriteResultText`（操作员可见）。
- 诊断：`data\mcp_debug.log` 每次启动追加；`DiagLog` 供设置窗口显示。
- `port <= 0` 或 `McpEnabled=0` → 不启动。

### 独立 MCP 进程：`KyjScada.Mcp\Program.cs`

```powershell
KyjScada.Mcp.exe --settings <dir> --db <history.db> --port 18321
KyjScada.Mcp.exe --settings ... --db ... --selftest   # 跑 9 个工具自检后退出
```

- `--selftest` 会依次调 `ListVariables("LQT")`、`ReadVariable("LQT_Level")`、`QueryStat`、
  `QueryAlarms`、`GetDeviceSummary`、`GetWriteStatus`、`WriteVariable("LQT_Level",50)`，逐条打印 `OK/FAIL`。
- 独立进程与 App 进程内 MCP **不是同一个实例**（端口冲突时二选一）；自检/调试常用独立进程。

---

## 2. MCP 工具（9 个）

| 工具 | 类 | 读/写 | 数据源 | 说明 |
|---|---|---|---|---|
| `list_variables` | `ReadOnlyTools` | 读 | `ConfigReader` 读 `settings\*.json` | 按关键字/设备筛变量 |
| `read_variable` | `ReadOnlyTools` | 读 | 配置 + 历史库最新值（视实现） | 单点当前/最近值与元信息 |
| `get_device_summary` | `ReadOnlyTools` | 读 | 配置 | 设备/组/变量数汇总 |
| `query_history` | `ReadOnlyTools` | 读 | `DbReader` → `history.db` | 时间窗原始点 |
| `query_stat` | `ReadOnlyTools` | 读 | `DbReader` / 聚合 | min/max/avg 等 |
| `query_alarms` | `ReadOnlyTools` | 读 | `DbReader` → `alarms.db` | 报警事件 |
| `query_log`(若存在) / 运行摘要 | `ReadOnlyTools` | 读 | `DbReader` | 视 `DbReader` 方法而定（含 `ActualData`/`SysLog` 兼容查询） |
| `get_write_status` | `WriteTools` | 读 | `writeHandler`/闸门状态 | 是否可写、原因（不下发） |
| `write_variable` | `WriteTools` | **写** | `writeHandler` → `CommandGate` | 返回 `OK ...` 或 `拒绝:...` |

实现文件：`KyjScada.Mcp\Tools\ReadOnlyTools.cs`（446 行）、`WriteTools.cs`（49 行）。
`DbReader`（531 行）用 **`Mode=ReadOnly`** 连接打开 SQLite，查 `History/VarDict/ActualData/SysLog/AlarmEvents`；
`ConfigReader`（100 行）直接读 JSON，不依赖 Domain 的 `SettingsJsonCodec`（又一处重复，见下）。

---

## 3. ⚠ `KyjScada.Mcp` 的架构债务（改表/改配置格式时必看）

| 债务 | 现状 | 后果 |
|---|---|---|
| **零项目引用** | `.csproj` 不引用 Domain/Infrastructure/Contracts | 拿不到 `HistorySchema`/配置模型/`PermissionBit` |
| **表结构抄了一份** | `Data\Models.cs` + `DbReader` 里的 SQL 自己写 | 给 `History/AlarmEvents` 加列要改 **Domain + Infrastructure + MCP 三处** |
| **配置解析可能重复** | `ConfigReader` 独立读 JSON | JSON 字段语义变了（如 `DataFormat`）要同步 MCP |
| **NuGet 重复** | 自己带 `Microsoft.Data.Sqlite` | 版本升级要两边对齐 |

这是"诚实的已知缺口"（README 里写了）。推荐方向：让 MCP 引用 `Contracts+Domain`（至少拿到 `HistorySchema` 和配置模型），
但当前**没做**——改动前先全局搜表名/列名。

---

## 4. `KyjScada.Host`（插件宿主，403 行）—— 当前未接入主流程

- 只依赖 `Contracts`。`ModuleHost`：`Provide<T>` 注入服务、`Load(modules)` 做依赖检查（`Requires`）+ 拓扑序 `Init`、
  `StartAllAsync/StopAllAsync` 并发且隔离故障；`LoadReport { Loaded, Rejected, Failed }`。
- 辅助：`ServiceRegistry`、`ModuleContext`、`DictConfig/DictConfigSource`、`HostLogger`（`ScopedLogger`）。
- **正式 `KyjScada.App` 没有任何 `IModule` 实现**，宿主只在 `KyjScada.Verify` 里被驱动（证明契约可用）。
  这不是"坏了"，是"还没把采集/报警/归档拆成模块"。

---

## 5. `KyjScada.Verify`（237 行）—— 架构实证，6 项

```powershell
.\KyjScada.Verify\bin\Debug\net8.0\KyjScada.Verify.exe
# 期望：全部 PASS，退出码 0；任一失败退出码 1
```

| 测试 | 证明什么 |
|---|---|
| `Test1_IndependentRun` | 模块可独立加载/启动 |
| `Test2_FaultIsolation` | 一个模块 `Init`/运行失败不拖垮其他模块 |
| `Test3_DependencyCheck` | `Requires` 缺失时被 `Rejected`（而不是运行时空引用） |
| `Test4_WriteConflict` | 写冲突/仲裁行为符合预期 |
| `Test5_NullSemantics` | 空值语义（`TagValue`/快照缺失）正确 |
| `Test6_SnapshotConsistency` | 快照一致性（不可变/版本） |

依赖 `Contracts + Host + Runtime`。它不连数据库、不连设备，**纯内存**，适合做架构回归。

---

## 6. `KyjScada.Migrate`（150 行）—— 老库一次性迁移

```powershell
.\KyjScada.Migrate\bin\Debug\net8.0\KyjScada.Migrate.exe `
  --from D:\old\history.db --to D:\new\data\history.db [--device 空压机ModbusRTUOverTCP] [--dry-run]
```

- 读老库宽表 `ActualData` / `SysLog` → 写新库 `VarDict + History`（长表）。
- 拒绝 `--from` 与 `--to` 相同；`--dry-run` 只统计。控制台按 UTF-8 输出。
- 依赖 `Domain + Infrastructure`。**现场升级只跑一次**，跑前备份 `--to`。

---

## 7. 排障速查

| 现象 | 查哪里 |
|---|---|
| MCP 连不上 | `data\mcp_debug.log`；设置窗口"复制端点"确认端口；`McpEnabled`/防火墙；是否独立进程占了同一端口 |
| 写入返回"拒绝" | 原因字符串里会写（未启用/权限/未知变量/不可写/限速）；界面「参数设置」勾选 + 登录 `CanWrite` |
| 写入成功但设备没动 | 组 `IsWritable`/组名不匹配、变量类型 64 位/字符串不支持、地址解析错（`DescribeAddress`）；查审计记录 |
| 报警查不到 / 表结构错 | `alarms.db` 是否有 `AckAt`（`EnsureSchema` 会补）；MCP 查的是 `dbPath` 同目录推导的文件？ |
| `--selftest` FAIL | 先确认 `--settings/--db` 指向真实运行目录的 `settings\` 与 `data\history.db`；库里有没有 `LQT_*` 数据 |