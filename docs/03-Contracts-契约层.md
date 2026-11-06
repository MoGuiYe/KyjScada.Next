# 03 Contracts 契约层

> **读者**：要新增/修改跨层接口的人，或者要理解"为什么 Runtime 不认识 SQLite"的人。
> **读完能做到**：知道每个接口谁实现、谁调用、语义边界在哪；改接口时知道会连带改哪些实现与假对象。

`KyjScada.Contracts` 是 **348 行、零项目引用、零 NuGet** 的项目。它的存在意义：
让上层（Runtime）不依赖下层（Infrastructure）。所以 Runtime 里只有接口和值对象，没有 `SqliteConnection`、
没有 `SerialPort`、没有 `System.Windows`。

---

## 1. 契约清单（10 个接口 / 6 个值对象，全部在 `KyjScada.Contracts\`）

| 接口 | 文件 | 实现者（全仓库） |
|---|---|---|
| `IAcquisitionService` | `IAcquisitionService.cs` | `ModbusAcquisitionService`；测试 `DevicePipelineTests.FakeService` |
| `IDeviceWriter` | `IDeviceWriter.cs` | `ModbusAcquisitionService`（同一个类，读**写共用同一连接**）；测试 `FakeWriter`×2 |
| `IHistoryRepository` | `IHistoryRepository.cs` | `HistoryRepository`；测试 `ArchiveServiceTests.FakeRepo` |
| `IValueBus : IValueReader, IValueWriter` | `IValueBus.cs` | `ValueBus` |
| `IValueReader` / `IValueWriter` | `IValueBus.cs` | （同上，拆分是为了让"只读方"拿不到 `Publish`） |
| `IModule` | `IModule.cs` | `KyjScada.Verify\FakeModules.cs` 里 4 个假模块；**正式代码里没有实现者** |
| `IModuleContext` | `IModule.cs` | `Host\ModuleContext.cs` |
| `IModuleConfig` | `IModule.cs` | `Host\DictConfig.cs` |
| `IModuleLogger` | `IModuleLogger.cs` | `Host\HostLogger`（内部 `ScopedLogger`）；测试里用委托 sink |

另外几个跨层接口不在 Contracts（因为它们依赖具体领域模型）：

| 接口 | 所在项目 | 文件 |
|---|---|---|
| `IProtocolDescriptor` | Domain | `Protocol\IProtocolDescriptor.cs`（11 个描述符实现） |
| `IModbusTransport` | Infrastructure | `Modbus\IModbusTransport.cs` |
| `IVisionSystem` / `IMotionSystem` | Domain | `Extension\VisionMotionContracts.cs`（**扩展预留，暂无实现**） |
| `IModuleConfigSource` | Host | `ModuleHost.cs`（`DictConfigSource` 实现） |

---

## 2. 采集与写入

### `IAcquisitionService`

```csharp
public interface IAcquisitionService : IDisposable
{
    string DeviceName { get; }
    bool IsConnected { get; }
    string? LastError { get; }
    int CollectOnce(IValueBus bus, DateTime sampleTime);   // 返回本轮产出的点数
    void Disconnect();
}
```

**语义约定**（`ModbusAcquisitionService` 的实际行为，行号 110–156）：

- **通讯失败不抛异常**：连不上 / 某组读失败时，把**全部变量标记为 `TagQuality.NotCollected`**
  后照样 `bus.Publish`，返回 0，原因写进 `LastError`。
  代码注释明确写了这是故意的："绝不能保留上一轮的旧值 —— 操作工看着冻结的旧数据以为是实时值，
  是老系统最危险的问题"。所以断线时界面应显示 `--`，而不是最后那个数。
- **唯一的例外**：协议在 v1 未实现时 `CollectOnce` 直接 `throw ModbusException`（行 112–113）。
  这条路径由 `AcquisitionFactory` 提前挡掉（不支持采集的设备根本不会建服务）。
- `CollectOnce` 内部自行 `bus.Publish(...)`，返回值只是"这轮采到多少个点"（诊断/界面用）。
- `sampleTime` 由调用方给（`DevicePipeline` 传当前 tick 时刻），用于 `TagValue.ChangedAt`
  和归档的 `SampleTime`；**不用于** `ValueSnapshot.Timestamp`（见下节）。
- `Disconnect` 幂等；`Dispose` = `Disconnect` + 释放 transport。
- **同一个实例同时是 `IDeviceWriter`**：写入和读取共用一条连接，写到一半不会被读操作插入
  （内部有锁）。这意味着**不要**给同一个设备建两个采集服务实例。

### `IDeviceWriter`

```csharp
public interface IDeviceWriter
{
    bool WriteVariable(string varName, double value, out string error);
}
```

- 只接受 `double`（Bool 用 0/1 表示，编码成线圈），`out error` 是**中文**原因，直接显示给操作员。
- **不要**从界面/HTTP 线程直接调它——必须过 `CommandGate`（否则没有权限、限速、审计）。
  `CommandGate.Writer` 的类型就是 `IDeviceWriter`。

---

## 3. 值总线（实时数据的唯一形态）

```csharp
public interface IValueReader
{
    long Version { get; }
    ValueSnapshot GetSnapshot();
    TagValue? GetValue(string tagName);
}

public interface IValueWriter
{
    void Publish(IReadOnlyDictionary<string, TagValue> values);
}

public interface IValueBus : IValueReader, IValueWriter { }
```

### `ValueSnapshot`（不可变）

| 成员 | 含义 |
|---|---|
| `long Version` | 每次 `Publish` 自增；界面/趋势可用来判断"有没有新数据" |
| `DateTime Timestamp` | **`Publish` 发生的时刻（`DateTime.Now`）**，不是 `CollectOnce` 传进来的 `sampleTime`（两者通常相差毫秒级，但断线重连后可能差很多；归档用的是后者） |
| `bool QualityGood` | ⚠ 当前实现**恒为 `true`**（`ValueBus.Publish` 里写死）。想用"整轮质量"判断要先补实现，别依赖它 |
| `IReadOnlyDictionary<string,TagValue> Values` | 位号 → 值 |
| `TagValue? this[string tagName]` | 取不到返回 `null`（**不抛异常**，`null` 有明确语义：未采集/不存在） |

### `TagValue`（不可变）

| 成员 | 含义 |
|---|---|
| `Name` | 位号（= `VarName`） |
| `object? Value` | 原始值（`double/float/int/long/short/byte/bool/string/null`） |
| `TagQuality Quality` | `Good=0 / Uncertain=1 / Bad=2 / NotCollected=3` |
| `DateTime ChangedAt` | **这个值上次变化的时刻**，不是采样时刻（界面"时间"列显示它） |
| `AsDouble()` | 宽松转换：`bool→1/0`，`string→double.TryParse`，其他类型 → `null` |
| `AsBool()` | 宽松转换：数值 `!=0`，字符串 `"1"/"true"/"True"` → true |

**为什么要 `object?` 加宽松转换**：设备类型五花八门（Bool 位、Short、Float、String），
上层只关心"当数字用 / 当开关用"，不想为每种类型写一遍 switch。

### `ValueBus`（实现，55 行，`KyjScada.Runtime\ValueBus.cs`）

- `Publish` **整体替换**字典（先复制传入字典，再在锁里换引用），**不合并上一轮**：
  这一轮没采到的点会从快照里消失 → 界面按"未采集"显示 `--`。
  （代码注释的原话：`Publish 由采集线程独占调用`、`读多写少，快照复制比锁竞争更划算`）
- `GetSnapshot()` 在锁内返回 `new ValueSnapshot(...)`，但 `ValueSnapshot` 持有的是**同一个字典引用**；
  因为字典只在 `Publish` 时整体换新对象、从不原地改，所以读侧仍然安全（无锁读）。
- `Version++` 在锁内自增（不是 `Interlocked`，因为已经在锁里）。
- `_timestamp = DateTime.Now` 在 `Publish` 里赋值，所以一次 `CollectOnce` 里多次 `Publish`
  会让快照时间跟着跳；正常路径是一轮一次 `Publish`。
- `_qualityGood` 目前写死 `true`，见上表。

---

## 4. 历史存储

```csharp
public readonly record struct HistoryPoint(
    DateTime SampleTime,
    string VarName,
    string DeviceName,
    string Unit,
    double Value,
    int Quality);          // 0=Good，1=非 Good

public interface IHistoryRepository
{
    int WritePoints(IReadOnlyList<HistoryPoint> points);   // 单事务批量写，返回写入行数
    int DeleteBefore(DateTime cutoff);                     // 保留期清理，返回删除行数
}
```

- **只有两个方法**是故意的：存储实现（SQLite / 以后可能换别的）只需要"批量写"和"按时间删"，
  查询属于另一条链路（界面直接持有 `HistoryRepository` 具体类型做复杂查询，因为查询形状太业务化，
  强行抽象会让接口膨胀）。
- `HistoryPoint.Quality` 是 `int` 而非枚举：表里就是 `INTEGER`，避免层间枚举口径不一致。
- `WritePoints` **全成全败**（一个事务），所以 `ArchiveService` 可以放心"一次写一批"。

---

## 5. 报警契约

```csharp
public enum AlarmDirection { High = 0, Low = 1 }
public enum AlarmLevel     { Normal = 0, Active = 1 }

public sealed record AlarmEvent(
    string VarName,
    AlarmDirection Direction,
    AlarmLevel From,
    AlarmLevel To,
    double Value,
    DateTime At)
{
    public bool IsTrigger => To == AlarmLevel.Active;
    public string DirectionText => Direction == AlarmDirection.High ? "上限" : "下限";
    public string TypeText => IsTrigger ? "触发" : "消除";
}

public sealed record ActiveAlarm(
    string VarName,
    AlarmDirection Direction,
    double Threshold,
    double Value,
    DateTime TriggeredAt,
    string? Note)
{
    public string DirectionText => Direction == AlarmDirection.High ? "上限" : "下限";
}
```

| 类型 | 谁产生 | 谁消费 |
|---|---|---|
| `AlarmEvent` | `AlarmStateMachine.Update()` 返回的 `AlarmTransition` 被 `AlarmService` 转成它 | `AlarmRepository.WriteEvents`（落库）、`AcquisitionCoordinator.AlarmSink` |
| `ActiveAlarm` | `AlarmService.GetActiveAlarms()`（实时推导，不落库） | 报警页表格、MCP `query_alarms` 的"当前报警"部分 |

`AlarmEvent` 只有**翻转**时产生（Normal→Active 或 Active→Normal），不是每个采样周期都产一行。
`record` 的相等性是按值的，所以在测试里可以直接 `Assert.Equal(expected, actual)`。

---

## 6. 模块宿主契约（`IModule` 等）—— 现状要看清

```csharp
public interface IModule
{
    string Id { get; }
    string DisplayName { get; }
    Type[] Requires { get; }          // 依赖的服务类型（如 typeof(IValueBus)）
    void Init(IModuleContext context);
    Task StartAsync(CancellationToken ct);
    Task StopAsync(CancellationToken ct);
}
```

诚实说明：**正式运行路径没有用这套宿主**。`KyjScada.App` 里没有任何类实现 `IModule`；
唯一的使用者是 `KyjScada.Verify`（`FakeModules.cs` 4 个假模块 + `ModuleHost` 加载它们），
用来证明"模块能独立加载 / 初始化失败被隔离 / 缺依赖被拒绝 / 服务能注入"。

它存在的价值：
1. 契约先立住，将来把"采集模块/报警模块/归档模块"做成插件时不用再设计接口；
2. `Verify` 是**架构回归测试**——谁把模块耦合写回去，这个程序会 FAIL。

`ModuleHost` 的行为（`Host\ModuleHost.cs`，199 行）：

- `Load(modules, onlyIds)`：先解析依赖（`Requires` 必须能在 `ServiceRegistry` 里找到），
  再按拓扑序 `Init`；依赖缺失 → `Rejected`，`Init` 抛异常 → `Failed`，其余进 `Loaded`。
- `Provide<T>(instance)`：把宿主自己的服务（总线/日志/配置）注入给模块。
- `StartAllAsync` / `StopAllAsync`：并发启动，任一失败只影响自己（故障隔离）。
- `LoadReport` 里 `Loaded/Rejected/Failed` 三个列表就是界面/日志要展示的东西。

---

## 7. 改契约的规则（会连带改哪里）

| 改动 | 连带必须改 |
|---|---|
| 给 `IAcquisitionService` 加成员 | `ModbusAcquisitionService` + `DevicePipelineTests.FakeService` |
| 给 `IHistoryRepository` 加成员 | `HistoryRepository` + `ArchiveServiceTests.FakeRepo` |
| 给 `IDeviceWriter` 加成员 | `ModbusAcquisitionService` + `JogPointsTests.FakeWriter` + `WritePathTests.FakeWriter` |
| 给 `IModule` 加成员 | `FakeModules.cs` 的 4 个类（否则 Verify 编不过） |
| 改 `HistoryPoint` 字段 | `ArchiveService.Archive`（构造处）+ `HistoryRepository`（写库）+ 所有测试构造点 |
| 改 `TagValue` 语义（如 `ChangedAt` 含义） | 界面"时间"列（`OnUiTick`）、趋势聚合的"变位判定"、MCP `read_variable` |

规则：**加成员可以，改语义要全仓库搜**。改语义比加成员危险得多——
`ChangedAt` 的语义被 4 个地方依赖，改错不会编译报错，而是"时间列看着不对"这种现场才发现的 bug。