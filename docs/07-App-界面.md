# 07 App 界面层（WPF）

> **读者**：要改页面、改交互、改主题、排查"按钮点了没反应/界面卡顿/切设备状态不对"的人。
> **读完能做到**：知道 7 个 Tab 各自在哪、`MainWindow` 的 partial 拆分、启动/切设备/定时器生命周期、
> 趋势图控件怎么喂数据、写入与 MCP 回调如何回到 UI 线程。
>
> `KyjScada.App`：`net8.0-windows`，引用 `Contracts + Domain + Runtime + Infrastructure + Mcp`，无 NuGet。

---

## 1. 文件地图（实测）

| 文件 | 行数 | 职责 |
|---|---|---|
| `MainWindow.xaml` | 1,000 | 7 个 Tab 的布局与命名控件 |
| `MainWindow.xaml.cs` | 3,236 | **god class**：启动、设备切换、协调器、500ms/1s 定时器、实时表、报警面板、MCP 写回调 |
| `MainWindow.Jog.cs` | 348 | 「急停·点动」页：候选点、急停、点动开/关、强制写、权限 |
| `MainWindow.Report.cs` | 509 | 「报表」页：变量选择、时间范围、聚合粒度/班次对齐、查询、CSV/XLSX 导出 |
| `TrendChartControl.cs` | 1,756 | **自绘**趋势图（WPF Drawing）：多曲线、缩放/平移/框选、游标、限位/包络/事件标记 |
| `ConfigEditorWindow.xaml.cs` | 1,260 | 配置编辑器（模态）：文件→树→属性面板，增删组/变量，导出变量 CSV，验证，保存/重载 |
| `UserManageWindow.xaml.cs` | 327 | 用户管理（模态）：列表、增删改、改密码、权限位勾选、启停 |
| `SettingsWindow.xaml.cs` | 200 | 系统设置（模态）：归档/保留/死区/班次/写入限速/MCP 开关与端口；保存后重启 MCP |
| `LoginWindow.xaml.cs` | 63 | 登录（模态）：校验 `UserRepository` |
| `McpHost.cs` | 187 | 进程内 Kestrel MCP 宿主（static）：`Start/Stop/Endpoint/Status`，**后台线程起停** |
| `AppSettings.cs` | 172 | `app_settings.ini` 读写（10 个键） |
| `BatchThresholdWindow.cs` | 233 | 批量阈值/报警设置（代码构建窗口） |
| `App.xaml` | 357 | 主题 ResourceDictionary（暗色 Catppuccin 风格） |
| `App.xaml.cs` | 8 | 空壳（启动在 `MainWindow`） |
| `ValueRow.cs` | 57 | 实时表行，`INotifyPropertyChanged`（**不能删**，否则要回退到 `Items.Refresh` 卡顿） |
| `WriteRow.cs` / `TrendSelectItem.cs` / `AlarmRows.cs` | 35/34/26 | 各页绑定模型 |

> ⚠ `MainWindow.xaml.cs` 是 3,236 行的"上帝类"（见 README 的缺口清单）。改它时先用 `grep`/行号定位方法，
> 不要凭印象"大概在中间"。

---

## 2. 7 个 Tab（`MainWindow.xaml`）

| Tab（Header） | `x:Name`/行号附近 | 主要内容 | 关键控件名 |
|---|---|---|---|
| 实时数据 | 77 | 变量表 | `ValueGrid`(78) |
| 历史趋势 | 93 | 左侧变量多选 + 查询条件 + 自绘图 | `TrendChart`、`TrendVarList`、时间/分辨率控件 |
| 报警 | 428 | 当前报警/历史事件 | `AlarmHistoryGrid`(528)、确认按钮 |
| 报表 | 557 | 变量 + 时间范围 + 粒度/班次 + 导出 | `ReportGrid`(877)、`ReportFromBox`/`ReportToBox`(152px)、`ReportTemplateBox`(830) |
| 急停·点动 | 700 | 目标变量下拉 + 急停 + 点动 + 强制写 | `JogVarBox`(937)、`JogGrid`(966) |
| 参数设置 | 778 | 写入开关/闸门状态/审计、用户入口 | 构建 `CommandGate` 的地方 |
| （状态/配置类） | 900 | 设备信息/辅助 | — |

TabItem 统一 `Background="#1E1E2E" Foreground="#FFFFFF" FontSize="14"`；**选中态**由 `App.xaml` 模板控制
（白底 `#FFFFFF` + 黑字 `#11111B`，13.5:1，见第 7 节）。

---

## 3. 启动与设备切换（行号以 `MainWindow.xaml.cs` 为准）

### `ctor`（45）

```csharp
_uiTimer.Interval = TimeSpan.FromMilliseconds(500);
_uiTimer.Tick += OnUiTick;
Loaded += OnLoaded;
Closed += OnClosed;
TrendChart.CursorMoved += ...; TrendChart.ViewChanged += ...; TrendChart.RangeSelected += ...;
MainTabs.SelectionChanged: SelectedIndex==1 且 !_trendQueried → 自动查一次趋势
```

### `OnLoaded`（71–137）

1. `FindSettingsDir()` → `AppContext.BaseDirectory\settings`
2. `SettingsJsonCodec.LoadDirectory(dir)` → `ActiveProject` → `_deviceList = project.AllDevices()`
3. `_appSettings = AppSettings.Load()`（`data\app_settings.ini`）
4. `BuildDeviceSelector()`；从 `ui_state.ini` 读上次设备，否则选第一个 `IsCollectable` 的
5. `ApplyDevice(preferred)`
6. 页脚：`配置:… 设备 N 台 变量 M 个 报警 K 归档 J`；异常 → `StatusText="启动失败"` + 显示原因

### `ApplyDevice`（243–306）

- **不停止任何采集流水线**（`EnsureCoordinator` 只建一次；`_coordinator` 管所有设备）。
- 设 `OrderBox`（字节序）→ `BuildRows/EnsureDatabases/InitTrend/InitLiveTrend/InitAlarmPage/InitReport/InitUsers`
  → `EnsurePipeline` → 绑定 `_pipeline/_svc/_bus/_alarm/_archive` → `StartAcquisition()`
  → `DeviceText` 用 `AcquisitionFactory.Create(device).DisplayName`（不支持加"· 未采集"）
  → `BuildWritePage()/BuildJogPage()` → `SaveUiState`。

### `EnsureCoordinator` / `StartAcquisition`（314 起 / 1812）

- `new AcquisitionCoordinator { Interval=1s, ServiceFactory, AlarmFactory, ArchiveFactory, AlarmSink→_alarmRepo.WriteEvents }`
- `coordinator.Build(_deviceList); StartAll();` 懒创建 `_acqTimer` 1 s → `OnAcqTick`。

---

## 4. 两个定时器与线程约束

| 定时器 | 间隔 | 回调 | 做什么 | 禁止 |
|---|---|---|---|---|
| `_uiTimer` | 500 ms | `OnUiTick`（1967） | 刷 `_rows`（`ValueRow` 通知）、报警面板、趋势提示、实时趋势、写入页、点动页、报警页；>100 ms 记 diag | 阻塞、查大库、`Thread.Sleep` |
| `_acqTimer` | 1 s | `OnAcqTick`（1829） | 读 `_pipeline.State/LastError` 刷状态栏与颜色；多设备汇总；**每小时** `Archive.Purge(now, RetentionDays)`；>40 ms 记 diag | 直接采集、改控件 ItemsSource 以外的重活 |

采集、报警判定、归档**不在这两个定时器里**，在 `DevicePipeline` 的后台线程里（06 篇）。

> `OnUiTick` 里**不能**再加 `ValueGrid.Items.Refresh()`：实测 139 行 80~160 ms/次，
> 500 ms 一次会把 UI 线程吃满。`ValueRow` 已实现 `INotifyPropertyChanged`，只重绘变化单元格。

---

## 5. 趋势图 `TrendChartControl`（1,756 行，自绘）

- 纯 `FrameworkElement.OnRender` 绘制（网格、曲线、限位线、包络、事件标记、游标、选区），
  **不依赖第三方图表库**。
- 数据模型 `TrendSeries`：`VarName/Unit/Color/Points/HighLimit/LowLimit/HighHighLimit/LowLowLimit/IsDigital/YLocked` 等。
- 关键 API：`SetSeries(...)`、`Clear()`、`ResetView()`、`SetView(from,to)`、`TryGetRenderedRange`、
  `LockCurrentRange/UnlockRange`、`Zoom/ZoomAt`、`MoveCursorTo`、`GetReadingsAt`、`SetEvents`。
- 事件：`CursorMoved`（更新游标读数面板）、`ViewChanged/UserAdjustedView`（联动范围）、`RangeSelected`（框选缩放）。
- 显示开关：`ShowLimits/ShowEnvelope/ShowEvents`，`DisplayMode`，`EventMarkerCount/DrawnEventCount`。
- 喂数据的入口在 `MainWindow` 的趋势查询逻辑里（后台查 `HistoryRepository` → `TrendAggregator` → `Dispatcher` 回来调 `SetSeries`）。

---

## 6. 写入 UI 与 `McpWrite` 回调

- 「参数设置」页构建 `CommandGate`（绑定 `IDeviceWriter` = 当前设备的 `ModbusAcquisitionService`）；
  `Enabled` 由勾选框 + 登录权限共同决定。
- `McpHost.Start(..., writeHandler: McpWrite)` 把写能力暴露给 MCP。
- `McpWrite(varName, value)`（行 499）：**`Dispatcher.Invoke`** 回 UI 线程 →
  检查 `_gate`/`Enabled` → `_gate.Write(varName, value, "MCP")` →
  把"谁写的/写了什么/成没成"写到 `WriteResultText`（操作员必须看得见）→ 返回 `OK ...` 或 `拒绝:...`。
- MCP 起停（`RestartMcpHost`，行 470）：**`Task.Run` 里 `McpHost.Stop()+Start()`**，
  不能在 UI 线程 `GetResult()` 等 Kestrel 关停（会卡死/死锁）。

---

## 7. 主题与对比度（改 UI 必看）

`App.xaml` 是全局 ResourceDictionary（357 行），暗色基调：

| 角色 | 颜色 | 用途 / 对比度 |
|---|---|---|
| `TextStrong` | `#FFFFFF` | 主文字 |
| `TextLabel` | `#C6CDE0` | 次级标签（对 `#1E1E2E` 10.3:1） |
| `TextDim` | `#8C93AB` | 弱化文字（对 `#181825` 5.75:1） |
| `BtnBg` / `BtnBgHover` / `BtnBorder` | `#313244 / #3E4157 / #5A5F7A` | 按钮；`#FFFFFF/#313244`=12.57:1 |
| `FieldBg` | `#11111B` | 输入框底（`#FFFFFF/#11111B`=13.5:1） |
| `Accent` | `#89B4FA` | 焦点/选中下划线 |
| 窗口底 | `#1E1E2E` | — |

**三条血的教训**（别再踩）：

1. WPF 默认 `Button` 模板的 disabled 触发器会强制 `#F4F4F4` 底 + `GrayText`（1.31:1，现场看不见）。
   → `App.xaml` 里**重写了 Button ControlTemplate**，disabled 也保持可读（6.93:1）。
2. `TabItem` 选中态在 Aero 下默认白底，若文字继承 `#FFFFFF` 就白白一片。
   → 选中 = `#FFFFFF` 背景 + `TextElement.Foreground="#11111B"`（**模板触发器里设 `TextElement.Foreground`** 才能压过继承）。
3. 自定义 `ComboBox` 模板后忘了 `MinHeight` → 高度塌到 16 px。现在 `MinHeight=22`。
   `TextBox/PasswordBox` `MinHeight=20`；报表时间框 `Width=152`（130 会裁字）。

改样式时**像素验证**：用 `capture-desktop.ps1 -WindowPid (Get-Process KyjScada.App).Id` 截图，
或开 UIA 量 rect。最大化窗口比屏幕外扩 8 px（见 10 篇），别拿截图坐标直接当 UIA 坐标。

---

## 8. 弹窗与 UIA

`ShowDialog` 的窗口**不在**主窗口的 UIA 子树里（`RootElement.FindAll(Children,pid)` 找不到）。
自动化/排查要用 `EnumWindows + GetWindowThreadProcessId + GetWindowTextW(Unicode)` 找 HWND，
再 `AutomationElement.FromHandle`。密码框用剪贴板 + `SendKeys '^a'^v'`（见 10 篇脚本）。

---

## 9. 改这里的回归清单

| 改动 | 至少验证 |
|---|---|
| `OnUiTick` / `OnAcqTick` | `--ui-diag` 看 `ui-latency.log`，单次 <100 ms / <40 ms；不卡顿 |
| `ApplyDevice` / 切设备 | 切换后状态栏正确、实时表刷新、**其他设备仍在采**（Verify/状态汇总） |
| `ValueRow` / 表绑定 | 改值能刷新、不卡；**不要**加回 `Items.Refresh()` |
| 主题/模板 | 7 个 Tab 全看一遍（含选中/禁用/下拉/输入框/表头）；浅底控件文字必须深色 |
| `McpWrite` / `RestartMcpHost` | MCP 开关保存后端口可连、写入回执出现在 `WriteResultText`、关程序不卡死 |
| 趋势图 | 查 1~8 条曲线、缩放/框选/游标/导出 CSV、包络/限位开关 |
| 报表 | 班次对齐、聚合粒度、CSV+XLSX 导出、空范围不崩 |