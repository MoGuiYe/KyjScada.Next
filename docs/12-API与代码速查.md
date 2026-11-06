# 12 API 与代码速查（grep 友好）

> **读者**：知道要改什么、但想“5 秒定位到文件:行号”的人。
> **用法**：Ctrl+F 类名/方法名 → 看“文件:行号 → 一句话”，再打开代码。
> 行号以 `D:\DeepSeekHarness\KyjScada.Next` 下源码为准；**只列 `bin/obj` 之外的源文件**。
> 代码变动后行号会漂移，用 `Select-String -Path ... -Pattern '...'` 重新定位。

---

## A. 快速定位入口（从哪里开始读）

| 想查 | 入口文件:符号 |
|---|---|
| 程序启动/加载 | `KyjScada.App\MainWindow.xaml.cs:71` `OnLoaded` |
| 切设备 | `KyjScada.App\MainWindow.xaml.cs:243` `ApplyDevice` |
| 启动采集 | `KyjScada.App\MainWindow.xaml.cs:1812` `StartAcquisition` / `EnsureCoordinator` 314 |
| UI 定时器 | `KyjScada.App\MainWindow.xaml.cs:1967` `OnUiTick` |
| 采集定时器 | `KyjScada.App\MainWindow.xaml.cs:1829` `OnAcqTick` |
| MCP 写回调 | `KyjScada.App\MainWindow.xaml.cs:499` `McpWrite` |
| 重启 MCP | `KyjScada.App\MainWindow.xaml.cs:470` `RestartMcpHost` |
| 趋势查询 | `KyjScada.App\MainWindow.xaml.cs` 内 `RunTrendQueryAsync`（grep） |
| 设备下拉 | `KyjScada.App\MainWindow.xaml.cs` `BuildDeviceSelector` |
| 用户登录/种子 | `KyjScada.App\MainWindow.xaml.cs:2397` `SeedDefaultAdmin`；`LoginWindow.xaml.cs` |

---

## B. Domain（配置 / 协议 / 报警 / 用户）

### B.1 配置模型 `KyjScada.Domain\Config\ConfigModels.cs`
- `VariableConfig`（字段：`VarName, VarType, Start, VarAddress, Comments, Unit, Scale, Offset, ArchiveEnable, HighAlarmEnable/Value/Note, LowAlarmEnable/Value/Note, AlarmDeadband, Clone`）
- `GroupConfig`：`GroupName, IsActive, IsWritable, Start, Length, VarList, Clone`
- `DeviceConfig`：`DeviceName, IsActive, ProtocolKey, Connection, Options, WordOrder, GroupList, Clone`
- `ProjectConfig`：`ProjectName, DevicesByProtocol, GetDevices, AllDevices, AllVariables, Clone`
- `WordOrder` enum：`Abcd=0, Badc=1, Cdab=2, Dcba=3`

### B.2 配置编解码 `KyjScada.Domain\Config\SettingsJsonCodec.cs`
- `CanonicalKeys`（11 个，行 20）
- `KeyToProtocol` 35 / `ProtocolToKey` 42
- `ActiveProject` 53
- `Parse` 61 / `ToJson` 259
- `LoadFile` 406 / `LoadedSettings` 413 / `LoadDirectory` 427
- `TryUpdateDeviceWordOrder` 509 / `SaveFile` 544
- `ParseWordOrder` 378（把 `DataFormat` 解析进 `WordOrder`）

### B.3 连接 `KyjScada.Domain\Config\ConnectionConfig.cs`
- `ConnectionKind` enum：`Tcp/Serial/Opc`
- `ConnectionConfig`：`Kind, IpAddress, Port, PortName, BaudRate, Parity, DataBits, StopBits, Clone, Describe`

### B.4 报警状态机 `KyjScada.Domain\Alarm\AlarmStateMachine.cs`
- `AlarmStateMachine(direction, threshold, deadband)` 构造 55
- `Update(value, at)` 77 → `AlarmTransition?`
- `Reset(at)` 135
- `Level/Direction/Threshold/Deadband`
- `AlarmTransition` record：`VarName?` 无、`Direction, From, To, Value, At`，`IsTrigger`

### B.5 历史表定义 `KyjScada.Domain\History\HistorySchema.cs`
- `TimeFormat` = `"yyyy-MM-dd HH:mm:ss"`
- `VarDictDdl / HistoryDdl / IndexDdl` 常量

### B.6 协议 `KyjScada.Domain\Protocol\`
- `IProtocolDescriptor`：`Key, DisplayName, ConnectionKind, Options, BuildAddress, ValidateAddress, ValidateOptions`
- `ProtocolRegistry`：`Register, Find, All, AllKeys, ResolveAddress`
- `ProtocolKeyMap`：`IsModbusFamily, IsSiemensFamily, IsMelsecFamily, IsOmronFamily, IsOpcFamily, IsImplementedInV1`
- `OptionDefinition`：`Key, DisplayName, Type, DefaultValue, Choices, Required`
- `OptionType`：`Text, Int, Bool, Enum`
- 描述符实现：`ModbusRtuDescriptor, ModbusRtuOverTcpDescriptor, ModbusAsciiDescriptor, ModbusTcpDescriptor, OpcDaDescriptor, OpcUaDescriptor, SiemensDescriptor, MelsecDescriptor, MelsecFXSerialDescriptor, OmronDescriptor, OmronHostlinkDescriptor`

### B.7 用户 `KyjScada.Domain\Users\`
- `PasswordHasher`：`Iterations=100000, SaltBytes=16, HashBytes=32, NewSalt, Hash, Verify`
- `PermissionBit`：`None, CanView, CanWrite, CanAckAlarm, CanQueryHistory, CanViewReport, CanEditConfig, CanManageUsers, CanEditSystem, CanJog, CanForce, All`
- `PermissionNames`：`Of(bit), Describe(bits)`

---

## C. Infrastructure（采集 / Modbus / 仓储）

### C.1 采集 `KyjScada.Infrastructure\Acquisition\`
- `AcquisitionFactory`：`IsCollectable(device), Create(device, out reason)`
- `ModbusAcquisitionService`：`DeviceName, IsConnected, LastError, CollectOnce(bus,sampleTime), Disconnect, WriteVariable(name,value,out error), DescribeAddress(v), ParseGroupStart, TryParseVarAddress`

### C.2 Modbus `KyjScada.Infrastructure\Modbus\`
- `IModbusTransport`：读写寄存器/线圈、`Connect/Disconnect/Dispose`
- `ModbusTcpClient` / `ModbusTcpTransport`
- `ModbusRtuTransport` / `ModbusRtuOverTcpTransport`
- `ModbusFrame` / `ModbusRtuFrame` / `ModbusCrc16`
- `ModbusValueCodec`：`RegisterCountForWrite(varType,out reason), Encode(value,v,order)`

### C.3 仓储 `KyjScada.Infrastructure\Data\` + `Alarm` + `Users`
- `HistoryRepository`：`WritePoints, DeleteBefore, WriteBatch, WriteBatchIfAbsent, Query, QueryAll, GetVarDict, Count, EnsureSchema, GetOrCreateVarId`
- `AlarmRepository`：`WriteEvents, Query, Acknowledge, AcknowledgeAll, DeleteBefore, EnsureSchema`（含 `AckAt` 迁移）
- `UserRepository`：`Create, Get, Login, List, Update, Delete, SeedDefaultAdmin, ChangePassword`
- `LegacyMigrator`：`--from/--to/--device/--dry-run`

### C.4 其它
- `Config\ConfigValidator`：`Validate(device) → errors`
- `Export\XlsxWriter`：多 sheet xlsx 生成

---

## D. Runtime（调度 / 闸门 / 服务）

### D.1 总线 `KyjScada.Runtime\ValueBus.cs`
- `ValueBus`：`Version, Publish, GetSnapshot, GetValue`

### D.2 调度 `KyjScada.Runtime\`
- `AcquisitionCoordinator`：`Interval, ServiceFactory, AlarmFactory, ArchiveFactory, AlarmSink, Build(devices), StartAll, StopAll, Pipelines, Find`
- `DevicePipeline`：`State, LastError, LastCycleMs, Bus, Service, Alarm, Archive, Start/Stop`

### D.3 报警/归档 `KyjScada.Runtime\`
- `AlarmService`：`BuildFrom(device,defaultDeadband), ProcessSnapshot(snapshot,now) → List<AlarmEvent>, GetActiveAlarms`
- `ArchiveService`：`BuildFrom(device), ShouldWrite(now), Archive(snapshot,device), Purge(now,retentionDays), Interval, ArchiveVarCount, TotalWritten, LastError, LastWrite, LastPurged`

### D.4 写入 `KyjScada.Runtime\CommandGate.cs`
- `CommandGate`：`Enabled, MinInterval, BuildFromDevice, Write(name,value,moduleId,rawValue), IsWritable(name), ReasonOf(name), WritableCount, Audit`
- `WriteOutcome`：`Success, Verdict, Message, ClaimOwner`
- `WriteVerdict`：`Disabled, UnknownVar, NotWritable, BadValue, ...`

### D.5 点动 `KyjScada.Runtime\JogPoints.cs`
- `JogPoint`：`GroupName, VarName, VarType, Comments, IsBool`
- `JogPoints`：`IsWritableGroup(g), Select(device,isWritable), EStopTargets(points)`

### D.6 趋势 `KyjScada.Runtime\`
- `TrendAggregator`：分桶/变位/包络
- `TrendStatsCalculator`：min/max/avg/σ
- `TrendCsv`：导出
- `TrendTemplateStore`：模板 ini 读写

---

## E. Contracts（接口与值对象）

### E.1 `KyjScada.Contracts\`
- `IAcquisitionService`：`DeviceName, IsConnected, LastError, CollectOnce, Disconnect`
- `IDeviceWriter`：`WriteVariable(name,value,out error)`
- `IHistoryRepository`：`WritePoints, DeleteBefore`；`HistoryPoint` record struct
- `IValueBus/IValueReader/IValueWriter`：`Version, GetSnapshot, GetValue, Publish`
- `ValueSnapshot`：`Version, Timestamp, QualityGood, Values, indexer`
- `TagValue`：`Name, Value, Quality, ChangedAt, AsDouble, AsBool`
- `TagQuality`：`Good, Uncertain, Bad, NotCollected`
- `AlarmDirection`：`High, Low`；`AlarmLevel`：`Normal, Active`
- `AlarmEvent` / `ActiveAlarm` records
- `IModule/IModuleContext/IModuleConfig/ModuleDescriptor/IModuleLogger`

---

## F. Host `KyjScada.Host\`
- `ModuleHost`：`Provide<T>, Load(modules, onlyIds), StartAllAsync, StopAllAsync, Loaded, LoadedModule, LoadReport`
- `ServiceRegistry, ModuleContext, DictConfig, DictConfigSource, HostLogger`

---

## G. App（界面/主题/图表）

### G.1 `KyjScada.App\MainWindow.xaml.cs`（3236 行）
- 字段：`_appSettings, _deviceList, _deviceFiles, _device, _coordinator, _pipeline, _svc, _bus, _alarm, _archive, _historyRepo, _alarmRepo, _gate, _rows, _uiTimer, _acqTimer, UiStateFile`
- 方法：`OnLoaded, ApplyDevice, EnsureCoordinator, StartAcquisition, OnAcqTick, OnUiTick, BuildDeviceSelector, SaveUiState, McpWrite, RestartMcpHost, BuildRows, EnsureDatabases, InitTrend, InitLiveTrend, InitAlarmPage, InitReport, InitUsers, BuildWritePage, BuildJogPage, RefreshDeviceBadge, DiagUi`

### G.2 `KyjScada.App\` partial/组件
- `MainWindow.Jog.cs`：`BuildJogPage, ApplyJogPermissions, OnJogEnableChanged, OnEstop, OnJogOn/Off, JogPoint, OnForceWrite, TickJogPage`
- `MainWindow.Report.cs`：`InitReport, OnReportVarSelectionChanged, OnReportSelectAll, OnReportClearVars, ReportIntervalSeconds, GrainName, AlignToShiftStart, OnRunReport, TryParseReportTime, OnExportReport, Csv, OnExportReportExcel`
- `TrendChartControl.cs`：`TrendSeries, RangeSelected, CursorMoved, ViewChanged, UserAdjustedView, UserResetView, ViewRange, HasData, SetSeries, Clear, ResetView, SetView, TryGetRenderedRange, LockCurrentRange, UnlockRange, SelectionStart/End, ClearSelection, MoveCursorTo, Zoom, ZoomAt, GetReadingsAt, ShowLimits/ShowEnvelope/ShowEvents, EventMarkerCount, DrawnEventCount, DisplayMode, SetEvents`
- `ConfigEditorWindow.xaml.cs`：`ConfigEditorWindow, InitFileList, OnFileChanged, Load, BuildTree, OnTreeSelectionChanged, BuildProps/BuildProjectProps/BuildDeviceProps/BuildGroupProps/BuildVarProps, FindTypeIndex, AddLabel/AddText/AddInt/AddFloat/AddCheck/AddCombo, MakeRow, MarkDirty, HeaderOf, OnAddGroup, OnAddVariable, OnDeleteNode, RebuildKeepNothing, ResolveDevice/ResolveGroup, OnExportVars, Csv, OnValidate, OnSave, OnReload, CurrentProject`
- `UserManageWindow.xaml.cs` / `SettingsWindow.xaml.cs` / `LoginWindow.xaml.cs` / `BatchThresholdWindow.cs`
- `McpHost.cs`：`Start, Stop, Endpoint, Status, Running, DiagLog`
- `AppSettings.cs`：`ArchiveIntervalSeconds, RetentionDays, DefaultAlarmDeadband, ShiftHours, ShiftStartHour, WriteMinIntervalMs, McpEnabled, McpPort, Load, Save`
- `ValueRow.cs / WriteRow.cs / TrendSelectItem.cs / AlarmRows.cs`

### G.3 主题 `KyjScada.App\App.xaml`
- Brushes：`TextStrong #FFFFFF, TextLabel #C6CDE0, TextDim #8C93AB, BtnBg #313244, BtnBgHover #3E4157, BtnBorder #5A5F7A, FieldBg #11111B, Accent #89B4FA`
- Implicit styles：`Button, CheckBox, TextBox/PasswordBox (MinHeight 20), ComboBox (MinHeight 22), ComboBoxItem, TabControl, TabItem (selected #FFFFFF + TextElement.Foreground #11111B + 2px Accent), DataGridColumnHeader (#313244/#FFFFFF)`

---

## H. MCP `KyjScada.Mcp\`
- `Program.cs`：`--settings/--db/--port/--selftest`；跑 9 工具自检
- `Data\ConfigReader.cs`：读 settings JSON
- `Data\DbReader.cs`：`HistoryRow, StatResult, LogRow, DbSummary, CanOpen, GetSummary`，`Mode=ReadOnly`
- `Data\Models.cs`：自抄表结构
- `Tools\ReadOnlyTools.cs`：446 行 7+ 工具
- `Tools\WriteTools.cs`：49 行（`get_write_status, write_variable`）

---

## I. Verify / Migrate
- `KyjScada.Verify\Program.cs`：`Main, Test1_IndependentRun…Test6_SnapshotConsistency, _pass/_fail`
- `KyjScada.Migrate\Program.cs`：`--from/--to/--device/--dry-run/--help`

---

## J. 测试项目
- `KyjScada.Domain.Tests\Program.cs` (222), `TestRunner.cs` (43), `Assert.cs` (50), `AlarmTests.cs` (177), `ProtocolTests.cs` (174)
- `KyjScada.Infrastructure.Tests\Program.cs` (478) + 21 个 `*Tests.cs`（见 11 篇表）

---

## K. 常用 grep 模板

```powershell
# 在 KyjScada.Next 根下搜符号（排除 bin/obj）
Get-ChildItem -Recurse -Filter *.cs -File | Where-Object { $_.FullName -notmatch '\\(bin|obj)\\' } |
  Select-String -Pattern 'SymbolName' -Encoding UTF8 |
  ForEach-Object { "{0,-40} {1,5}  {2}" -f $_.Filename, $_.LineNumber, $_.Line.Trim() }

# 搜 SQL/表名（MCP 与 Infrastructure 两处都要改）
Select-String -Path 'KyjScada.Infrastructure\**\*.cs','KyjScada.Mcp\**\*.cs' -Pattern 'History|VarDict|AlarmEvents|ActualData' -Encoding UTF8

# 搜 Scale/Offset（确认是否已接入）
Select-String -Path 'KyjScada.*\*.cs' -Pattern 'Scale|Offset' -Encoding UTF8

# 搜 DataFormat / WordOrder
Select-String -Path 'KyjScada.*\*.cs' -Pattern 'DataFormat|ParseWordOrder|WordOrder' -Encoding UTF8
```

## L. 常见定位陷阱
- `SettingsJsonCodec` 的 `ParseWordOrder` 在 `SettingsJsonCodec.cs:378` 附近；`DataFormat` **不是** `Options` 键。
- `ModbusAcquisitionService` 的地址解析在 `ParseGroupStart`(281) 与 `TryParseVarAddress`(326)；显示/校验用 `Domain\Protocol\*Descriptor.BuildAddress`（两套）。
- `ValueBus.Publish` 恒写 `_qualityGood = true`；`Timestamp = DateTime.Now`（不是 `sampleTime`）。
- `AlarmRepository.EnsureSchema` 才会补 `AckAt` 列；`CREATE TABLE IF NOT EXISTS` 不够。
- `McpHost.Stop` 必须 `Task.Run`；UI 线程调用会死锁。
- ` capture-desktop.ps1 -WindowPid` 可靠，`-WindowTitle` 对 `ShowDialog` 窗可能失败；最大化窗口外扩 8px。