# 06 Runtime 运行时层

> **读者**：要改多设备并发、采集周期、报警落库时机、归档策略、写闸门/限速/审计、趋势聚合的人。
> **读完能做到**：知道一条 `DevicePipeline` 每秒做什么、`CommandGate` 的 6 步校验顺序、
> 归档为什么只在 `ShouldWrite` 为真时写、`ValueBus` 为什么是整体替换。
>
> `KyjScada.Runtime`：2,067 行，依赖 `Contracts + Domain`，**无 NuGet、无 SQL、无串口、无 UI**。

---

## 1. 文件地图（实测行数）

| 文件 | 行数 | 职责 |
|---|---|---|
| `ValueBus.cs` | 55 | 进程内实时值总线（`IValueBus`）：`Publish`/`GetSnapshot`/`Version` |
| `AcquisitionCoordinator.cs` | 182 | 管理多条 `DevicePipeline`：按设备建流水线、并发启动/停止、`AlarmSink` |
| `DevicePipeline.cs` | 218 | **单设备**周期循环：采集 → 报警判定 → 报警落库 → 归档；状态机（正常/未连接/异常） |
| `AlarmService.cs` | 177 | 按设备配置为每个(变量,方向)建 `AlarmStateMachine`，吃快照产出 `AlarmEvent` |
| `ArchiveService.cs` | 152 | 归档清单编译（只 `ArchiveEnable`）、间隔判断、只写有效值、`Purge` |
| `CommandGate.cs` | 296 | 写入唯一通道：启用/已知/可写/值合法/占用+限速 → `IDeviceWriter` → 审计 |
| `JogPoints.cs` | 81 | 点动/急停候选集判定（**安全逻辑**）：组是否可写、筛选 Bool 点 |
| `TrendAggregator.cs` | 343 | 历史点按分辨率分桶（avg/min/max/首末）、数字量"变位"、包络 |
| `TrendStatsCalculator.cs` | 240 | 趋势统计（min/max/avg/标准差等） |
| `TrendCsv.cs` | 164 | 趋势数据导出 CSV |
| `TrendTemplateStore.cs` | 159 | 趋势模板读写（`trend_templates.ini`） |

---

## 2. `ValueBus`（55 行）

- 内部 `_lock` + `_values` 字典 + `_version` + `_timestamp` + `_qualityGood`。
- `Publish(values)`：**整体复制**传入字典 → 锁内替换 `_values`、`_version++`、`_timestamp = DateTime.Now`、
  `_qualityGood = true`（**目前恒写死 true**，见 03 篇）。
- `GetSnapshot()`：锁内 `new ValueSnapshot(...)`，持有当前字典引用；因为字典只整体替换、从不原地改，读安全。
- `GetValue(name)`：锁内查字典，找不到返回 `null`。
- **不做"保留旧值"合并**：某轮没采到的点会从快照消失 → 界面显示 `--`。
  "断线不冻结旧值"这个语义是由**采集服务**保证的（它会把没采到的点显式写成 `NotCollected` 再 Publish）。

---

## 3. `AcquisitionCoordinator` + `DevicePipeline`

### Coordinator（182 行）

- `Interval`（默认 1 s）、`ServiceFactory`、`AlarmFactory`、`ArchiveFactory`、`AlarmSink`（`Action<IReadOnlyList<AlarmEvent>>`）。
- `Build(devices)`：清空旧流水线，为每台 `AcquisitionFactory.IsCollectable` 的设备创建一条 `DevicePipeline`，
  分别注入 `IAcquisitionService`/`IValueBus`（新建）/`AlarmService`/`ArchiveService`；**不支持采集的设备不建**。
- `StartAll()`：每条 pipeline 启一个后台循环线程；`StopAll()`：置停标志并等待退出。
- `Find(name)` / `Pipelines`：界面据此拿到"当前设备那条"（`_pipeline`）及其 `Bus/Service/Alarm/Archive`。

### Pipeline（218 行）—— 每秒一次的循环

```
while (!stopping)
  1. CollectOnce(bus, tickNow)      ← 采集服务内部 Publish（含失败时 NotCollected）
  2. snap = bus.GetSnapshot()
  3. events = Alarm.ProcessSnapshot(snap, tickNow)   ← 只在状态翻转时产出
  4. if (events.Count>0) AlarmSink(events)           ← 落 alarms.db
  5. if (Archive.ShouldWrite(tickNow)) Archive.Archive(snap, device)
  6. 更新 State/LastCycleMs/LastError；Sleep 到下一个 tick
```

状态（供界面状态栏）：`正常` / `已连接(无数据)` / `未连接` / `异常`（颜色映射在 `MainWindow.OnAcqTick`）。
`LastError` 来自采集服务或归档失败信息。

---

## 4. `AlarmService`（177 行）

- `BuildFrom(device, defaultDeadband)`：遍历变量，为 `HighAlarmEnable`/`LowAlarmEnable` 各建一个
  `AlarmStateMachine`（key = `varName + "\0" + direction`）；阈值/备注/死区来自变量配置，
  变量 `AlarmDeadband==0` 时用 `defaultDeadband`。
- `ProcessSnapshot(snapshot, now)`：对每个已注册变量取 `snapshot[varName].AsDouble()`，
  调 `machine.Update(value, now)`；非 null 的 `AlarmTransition` 转成 `AlarmEvent` 返回。
- `GetActiveAlarms()`：遍历所有 machine，`Level==Active` 的生成 `ActiveAlarm`（含 `Threshold`/`Note`/`TriggeredAt`）。
- **无状态持久化**：重启后状态机从 `Normal` 开始，已触发但未消除的报警不会自动补一条"触发"事件；
  "当前报警"要靠 `alarms.db` 历史推导或等下一次状态变化（这是现状）。

---

## 5. `ArchiveService`（152 行）

- `BuildFrom(device)`：编译 `_archiveVars`（仅 `ArchiveEnable=true` 且 `VarName` 非空）与 `_units`。
- `ShouldWrite(now)`：`now - _lastWrite >= Interval`（默认 10 s，`AppSettings.ArchiveIntervalSeconds`）。
- `Archive(snapshot, device)`：
  - 遍历 `_archiveVars`，取 `snapshot[varName]`；
  - 跳过 `null`、`Bad`、`NotCollected`、`AsDouble()==null`、`NaN`、`Infinity`；
  - `HistoryPoint(sampleTime=snapshot.Timestamp, ..., Quality = Good?0:1)`；
  - `repo.WritePoints(points)` 单事务；成功更新 `TotalWritten`/`_lastWrite`，失败只记 `LastError`（**不中断监控**）。
- `Purge(now, retentionDays)`：`retentionDays<=0` 不清理；否则 `DeleteBefore(now - retentionDays)`。
  界面在 `OnAcqTick` 里**每小时**最多触发一次（避免频繁删）。

---

## 6. `CommandGate`（296 行）—— 写入安全的核心

**所有下发到设备的写**（界面、MCP、以后任何模块）都必须走 `gate.Write(...)`。

### `Write(varName, value, moduleId, rawValue=null)` 的 6 步顺序（校验全在下发前）

1. `!Enabled` → `Disabled`（"写入未启用，请先登录并勾选允许写入"）
2. `varName` 空 / 不在 `_vars` → `UnknownVar`
3. `!IsWritable`（`_vars` 里无或类型/地址不可写）→ `NotWritable`，`ReasonOf` 给中文原因
4. `NaN/Infinity` → `BadValue`
5. `_claims.TryClaim` + 最小间隔（`MinInterval`，来自 `WriteMinIntervalMs=800`，按点独立）→ 占用/限速拒绝
6. `Audit` → `_writer.WriteVariable(...)` → `Audit` 结果

返回 `WriteOutcome(Success, Verdict, Message, ClaimOwner)`。**Verdict** 用于统计与排障，不要只看布尔。

### 构建与状态

- `BuildFromDevice(device, writer, now)`：把设备里"组可写（`JogPoints.IsWritableGroup`）且单点可写"的变量
  编进 `_vars`；`WritableCount` 供界面显示。
- `Enabled`：由「参数设置」页勾选控制（通常还要求登录 + `CanWrite` 权限）。
- `IsWritable(name)` / `ReasonOf(name)`：界面用来决定按钮是否可点、提示文案。

### 审计

`WriteAudit` 记录 `When/ModuleId/VarName/RawValue/Result/Error/ClaimOwner`。
界面「参数设置」页可以展示最近记录；MCP 写入用 `moduleId="MCP"`，便于区分来源。

---

## 7. `JogPoints`（81 行）—— 安全边界

- `IsWritableGroup(group)`：`group.IsWritable==true` 优先；否则组名含 `设定/参数/Set/SP`（老配置兜底）。
  **实时测量值组绝不开放**（代码注释："写进去会干扰设备"）。
- `Select(device, isWritablePredicate)`：在可写组里筛出 `isWritable(name)` 为真的变量，返回 `JogPoint`。
- `EStopTargets(points)`：只取 `IsBool` 的点（急停 = 把所有 Bool 置 0/停）。

`JogPoints` 是纯逻辑、无 UI，专门为了能在 `KyjScada.Infrastructure.Tests\JogPointsTests.cs` 里单测。

---

## 8. 趋势计算（纯函数风格，易测）

- `TrendAggregator`（343）：把 `HistoryRecord` 序列按分辨率（秒/分/时/班次…）分桶，
  每桶输出 avg/min/max/首/末；**数字量**按"变位"聚合（不做平均），可选包络。
- `TrendStatsCalculator`（240）：对一段序列算 min/max/avg/σ 等。
- `TrendCsv`：把聚合结果写 CSV。
- `TrendTemplateStore`：`trend_templates.ini` 的 `[section]` 读写。

这些类**不碰数据库、不碰控件**：输入是记录/参数，输出是 DTO。趋势页在后台线程查库拿记录 → 丢给聚合器 → 回 UI 绑图。

---

## 9. 改这里的检查清单

| 改动 | 风险 | 必跑 |
|---|---|---|
| `DevicePipeline` 循环顺序/异常处理 | 漏报警、漏归档、卡死采集线程 | `DevicePipelineTests`、`AlarmServiceTests`、`ArchiveServiceTests` |
| `CommandGate` 校验顺序 | 绕过权限/审计/限速（安全事故） | `WritePathTests`、`JogPointsTests` |
| `ValueBus` 发布语义 | 快照撕裂、`--` 显示错乱、趋势"变位"判定错 | `DevicePipelineTests` + 手工看实时表 |
| `ArchiveService` 过滤条件 | 写入 NaN 炸库 / 漏存 / 多存导致库膨胀 | `ArchiveServiceTests` |
| `JogPoints.IsWritableGroup` | 把测量值组判成可写（**现场危险**） | `JogPointsTests`（含"实时组不可写"用例） |

```powershell
.\KyjScada.Infrastructure.Tests\bin\Debug\net8.0\KyjScada.Infrastructure.Tests.exe  # 292/0
```