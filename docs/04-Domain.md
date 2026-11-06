# 04 Domain 领域层

> **读者**：要改配置模型、加协议、改报警规则、动用户权限的人。
> **读完能做到**：知道 `settings\*.json` 在内存里长什么样、协议描述符怎么写、
> 报警为什么必须用状态机而不是"拿上一帧比阈值"、权限位怎么加。
>
> `KyjScada.Domain`：2,227 行，只依赖 `Contracts` 与 `Newtonsoft.Json`，**没有 UI、没有数据库、没有串口**。

---

## 1. 文件地图

| 文件 | 行数 | 内容 | 改动频率 |
|---|---|---|---|
| `Config\ConfigModels.cs` | 221 | `VariableConfig` / `GroupConfig` / `DeviceConfig` / `ProjectConfig` / `WordOrder` | 高（加字段） |
| `Config\ConnectionConfig.cs` | 71 | `ConnectionKind` 枚举、连接参数、`Describe()` | 低 |
| `Config\SettingsJsonCodec.cs` | 580 | JSON ↔ 模型 双向、11 个规范键、字节序保真 | 高（改配置格式时） |
| `Alarm\AlarmStateMachine.cs` | 146 | 单变量单方向报警状态机（带死区） | 低（但很关键） |
| `History\HistorySchema.cs` | 60 | 历史库 DDL 常量（唯一权威） | 低（改表时动 4 处） |
| `Protocol\IProtocolDescriptor.cs` | 39 | 协议描述符接口 | 低 |
| `Protocol\OptionDefinition.cs` | 47 | 设备参数声明（`Options` 的元数据） | 中 |
| `Protocol\ProtocolRegistry.cs` | 75 | 11 个描述符登记 + 查找 + `ResolveAddress` | 中（加协议必改） |
| `Protocol\ProtocolKeyMap.cs` | 76 | 协议键 → 家族 / 是否已实现采集 | 中 |
| `Protocol\ModbusSerialDescriptors.cs` | 116 | RTU / RTU-over-TCP / ASCII | 低 |
| `Protocol\ModbusTcpDescriptor.cs` | 55 | Modbus TCP（MBAP） | 低 |
| `Protocol\PlcDescriptors.cs` | 300 | 西门子 S7 / 三菱 MC / 三菱 FX 串口 / 欧姆龙 Fins / Hostlink + `AddressCombiner` | 低 |
| `Protocol\OpcDescriptors.cs` | 110 | OPC DA / OPC UA | 低 |
| `Users\PasswordHasher.cs` | 70 | PBKDF2-SHA256（10 万次迭代） | 低 |
| `Users\PermissionBit.cs` | 105 | 10 个权限位 + 中文名 | 中（加权限位） |
| `Users\UserAccount.cs` | 60 | 用户实体 | 低 |
| `Extension\VisionMotionContracts.cs` | 90 | 视觉/运动扩展接口（**无实现**） | 预留 |

---

## 2. 配置模型（四层嵌套）

```
ProjectConfig                       一个"项目"（对应 JSON 数组里的一个对象）
├─ ProjectName
└─ DevicesByProtocol: Dictionary<string, List<DeviceConfig>>   ← 键是 11 个规范键
     └─ DeviceConfig
          ├─ DeviceName / IsActive / ProtocolKey
          ├─ Connection: ConnectionConfig      连接参数
          ├─ Options:    Dictionary<string,string>（大小写不敏感）
          ├─ WordOrder:  WordOrder（Abcd/Badc/Cdab/Dcba）
          └─ GroupList: List<GroupConfig>
                └─ GroupConfig
                     ├─ GroupName / IsActive / IsWritable
                     ├─ Start（"1.400001"）/ Length
                     └─ VarList: List<VariableConfig>
```

| 类 | 关键成员 | 备注 |
|---|---|---|
| `ProjectConfig` | `GetDevices(protocolKey)`、`AllDevices()`、`AllVariables()`、`Clone()` | `AllDevices` 会**跳过空列表**；`AllVariables` 含全部组 |
| `DeviceConfig` | `Connection`、`Options`（`OrdinalIgnoreCase`）、`GroupList`、`WordOrder`（默认 `Abcd`） | `Options` 用扩展方法 `EnsureOption` 之类读写 |
| `GroupConfig` | `GroupName`、`IsActive`（默认 true）、`IsWritable`（默认 false）、`Start`、`Length`、`VarList` | `IsWritable` 只在为 true 时才写进 JSON |
| `VariableConfig` | `VarName`、`VarType`（默认 `"Float"`）、`Start`、`VarAddress`、`Comments`、`Unit`、`Scale`（1）、`Offset`（0）、`ArchiveEnable`、`HighAlarm*`、`LowAlarm*`、`AlarmDeadband`、`Clone()` | `Start` 是**绝对偏移**（见 02 篇第 4 节） |
| `ConnectionConfig` | `Kind`（默认 `Tcp`）、`IpAddress`、`Port`、`PortName`、`BaudRate`=9600、`Parity`、`DataBits`=8、`StopBits`=1、`Describe()` | `Describe()` 产出的字符串直接显示在状态栏/错误里，改格式会影响现场排查习惯 |
| `WordOrder` | `Abcd=0` 大端、`Badc=1` 字节交换、`Cdab=2` 字交换（**现场在用**）、`Dcba=3` 小端 | 决定 Float/Double 的字节组装 |

`Clone()` 是**深拷贝**（组、变量、Options 字典各自新建）。配置校验/预览时用它，避免改到运行配置。

---

## 3. `SettingsJsonCodec`：JSON 读写（580 行，最容易被忽视的关键件）

### 公共 API

```csharp
public static readonly string[] CanonicalKeys;                       // 11 个键，顺序与老文件一致
public static string KeyToProtocol(string listKey);                  // "ModbusRTUList" → "ModbusRTU"
public static string ProtocolToKey(string protocolKey);               // 反向，补 "List"
public static ProjectConfig? ActiveProject(IEnumerable<ProjectConfig>); // 第一个"有设备"的项目
public static List<ProjectConfig> Parse(string json);
public static string ToJson(IEnumerable<ProjectConfig> projects, bool indented = true);
public static List<ProjectConfig> LoadFile(string path);
public sealed record LoadedSettings(...);                             // 合并结果 + 设备→文件名 映射
public static LoadedSettings LoadDirectory(string dir);
public static bool TryUpdateDeviceWordOrder(string filePath, string deviceName, WordOrder order);
public static void SaveFile(string path, IEnumerable<ProjectConfig> projects);
```

### 往返保真（Round-trip）规则 —— 这是本文件存在的理由

老的配置编辑器"读进来再存出去"会丢东西，现场表现为"改了字节序，浮点数全乱"。现在保证：

| 字段 | 规则 |
|---|---|
| `DataFormat` | 解析时 `ParseWordOrder("0"/"1"/"2"/"3")` → `DeviceConfig.WordOrder`；写回时 `obj["DataFormat"] = ((int)order).ToString()`，**原键名不变** |
| `IsWritable` | 只在 `true` 时写（`if (group.IsWritable) obj["IsWritable"] = true;`），`false` 不写——保持与老文件一致 |
| `Options` | 按**协议描述符声明的键**写回；未声明的未知键也原样保留（不丢用户自定义） |
| 11 个 `*List` 键 | 始终按 `CanonicalKeys` 顺序写出，空数组也写（老编辑器依赖这个顺序） |
| 缩进 | `indented=true`（默认），换行用 `\n`，UTF-8 无 BOM |
| 数值格式 | `Scale`/`Offset` 等 float 按 `ToString(CultureInfo.InvariantCulture)`，避免逗号小数点 |

### ⚠ `DataFormat` 不在 `Options` 里

现场 JSON 里 `"DataFormat": "2"` 与 `"IsShortAddress": "True"` 看起来像一类，实际不是：
- `DataFormat` 是**设备级具名键**，由 `ParseWordOrder` 专门解析；
- `IsShortAddress` 是 `Options` 里的一项（协议描述符声明）。
所以给 `Options` 加新键要改描述符；给 `DataFormat` 加新值要改 `ParseWordOrder` + 采集端字节组装。**两条路完全不同。**

### `LoadDirectory` 的语义

- 扫描目录下所有 `*.json`（`*.json.bak` 之类不匹配），逐个 `LoadFile`；
- 只有一个文件时直接返回；多个文件时**按设备名合并**成一个 `ProjectConfig`，
  并记录 `DeviceFiles`（设备名 → 文件路径），供"界面改字节序回写"使用；
- 解析失败的文件**跳过并记 `Errors`**，不整体崩（现场有半截文件也能启动）。

`ActiveProject` 必须用它取"当前项目"——老文件里可能有 2~3 个空壳项目对象（见 02 篇）。

---

## 4. 报警状态机（`AlarmStateMachine`，146 行）

### 为什么是状态机（代码注释里的两次事故）

老项目试过两种"从上一帧值推导状态"的方案，都错：

| 方案 | 失效场景 |
|---|---|
| A：`last >= 阈值` 视为已触发 | 过程量从死区**下沿**退出时 `last` 掉到阈值以下，状态被忘记 → **消除事件永远等不到，报警永久挂起** |
| B：`last >= 阈值 - 死区` 视为已触发 | 死区内一直为 true → 正常上升沿（99.5 → 100.5）**触发不了** |

只有**独立状态位**能同时做对。实测背景：老系统液位在阈值 54 附近反复穿越，
同一条报警产生 **181 次触发 / 383 次消除**（消除比触发多，说明状态被错乱重置过）。

### 语义（上限为例；下限镜像）

```
值 > 阈值              → 触发 Active
值 < 阈值 - 死区        → 消除 Normal
值在 [阈值-死区, 阈值]  → 保持原状态（不抖动）
```

下限：`值 < 阈值` 触发；`值 > 阈值 + 死区` 消除。

### API 与边界

```csharp
var m = new AlarmStateMachine(AlarmDirection.High, threshold: 54.0, deadband: 0.5);
AlarmTransition? t = m.Update(value, at);   // null = 本帧没有状态转移
m.Reset(at);                                // 强制回正常（操作员确认）
m.Level;                                    // 当前状态
```

| 输入 | 行为 |
|---|---|
| `value == null` | **不改变状态**，返回 null（"断线不等于正常，也不等于报警"） |
| `NaN` / `±Infinity` | 同上，忽略 |
| 构造时 `threshold` 非有限值 | 抛 `ArgumentException` |
| 构造时 `deadband < 0` | 抛 `ArgumentException` |

`AlarmTransition(Direction, From, To, Value, At)` 是 `record`，`AlarmService` 把它转成
`Contracts.AlarmEvent` 再落库。**`Reset` 产生的转移 `Value = double.NaN`**（没有真实采样值）。

一个变量若同时开了高低报警，就是**两个独立的 `AlarmStateMachine` 实例**（方向不同，状态互不影响）。

---

## 5. 历史库定义（`HistorySchema`）

这个文件是**表结构的唯一权威**；`Infrastructure` 和 `Mcp` 都应当引用它（Mcp 目前没引用，见 08 篇）。

```csharp
public const string TimeFormat = "yyyy-MM-dd HH:mm:ss";
public const string VarDictDdl  = ...;  // VarDict(VarId PK, VarName UNIQUE, DeviceName, Unit)
public const string HistoryDdl  = ...;  // History(Id PK AI, SampleTime, VarId, Value, Quality)
public const string IndexDdl    = ...;  // idx_history_time / idx_history_var_time
```

`SampleTime` 用**本地时间字符串**（不是 UTC）：现场对表看时间用、报表按班次分组用，
改成 UTC 会让所有历史数据时间错 8 小时。要改必须写迁移。

---

## 6. 协议描述符（加协议的入口）

### 接口

```csharp
public interface IProtocolDescriptor
{
    string Key { get; }                               // "ModbusRTU"，与 JSON 键 "ModbusRTUList" 对应
    string DisplayName { get; }                       // "Modbus RTU"
    ConnectionKind ConnectionKind { get; }            // Tcp / Serial / Opc
    IReadOnlyList<OptionDefinition> Options { get; }  // 设备参数声明
    string BuildAddress(string groupStart, string varOffset, DeviceConfig device);
    string? ValidateAddress(string fullAddress, DeviceConfig device);  // null = 通过
    string? ValidateOptions(DeviceConfig device);                      // null = 通过
}
```

### 11 个描述符（`ProtocolRegistry` 静态构造里全部登记）

| Key | DisplayName | 连接 | v1 是否实现采集 |
|---|---|---|---|
| `ModbusTCP` | Modbus TCP | Tcp | ✅ MBAP 帧 |
| `ModbusRTU` | Modbus RTU | Serial | ✅ 串口 + CRC16 |
| `ModbusRTUOverTCP` | Modbus RTU Over TCP | Tcp | ✅ **RTU 帧跑裸 TCP（现场在用）** |
| `ModbusASCII` | Modbus ASCII | Serial | ❌ 描述符在，采集未实现 |
| `Siemens` | 西门子 S7 | Tcp | ❌ |
| `Melsec` | 三菱 MC | Tcp | ❌ |
| `MelsecFXSerial` | 三菱 FX 串口 | Serial | ❌ |
| `Omron` | 欧姆龙 Fins | Tcp | ❌ |
| `OmronHostlink` | 欧姆龙 Hostlink | Serial | ❌ |
| `OPCDA` | OPC DA | Opc | ❌ |
| `OPCUA` | OPC UA | Opc | ❌ |

"是否实现采集"由 `ProtocolKeyMap.IsImplementedInV1` 决定（**只认 3 个 Modbus**）。
未实现的协议：设备下拉能选、界面显示"· 未采集"，`AcquisitionFactory` 不建服务，
`CollectOnce` 若被误调会抛 `ModbusException`。

### `OptionDefinition`（设备参数元数据）

```csharp
new OptionDefinition("IsShortAddress", "短地址", OptionType.Bool, "False")
```

`OptionType`：`Text/Int/Bool/Enum`；`Choices` 给 Enum 用；`Required` 表示必填。
界面/配置编辑器据此**自动生成**参数控件（不用为每个协议写一遍 UI）。
实际声明：

| 描述符 | Options |
|---|---|
| `ModbusTCP` | `IsShortAddress`(Bool,False) |
| `ModbusRTU` / `ModbusRTUOverTCP` / `ModbusASCII` | `IsShortAddress`(Bool,False)、`CycleTimes`(Int,3)、`CheckCycleTimes`(Int,3) |

⚠ `CycleTimes` / `CheckCycleTimes` **声明了但采集实现没读**（重试策略写死），见 02 篇缺口清单。

### 两份地址实现（必须知道）

`BuildAddress`（这里）用于**显示/校验/导出**；真正采集用的地址解析在
`Infrastructure\ModbusAcquisitionService.ParseGroupStart/TryParseVarAddress`。
**改地址规则要同时改两处**（详见 02 篇 4.4）。

`AddressCombiner`（`PlcDescriptors.cs` 里）是给 PLC 家族拼地址用的静态工具
（`DB1.10` / `M.110` 这类），Modbus 家族不用它。

---

## 7. 用户与权限

### `PasswordHasher`

```csharp
public const int Iterations = 100_000;
public const int SaltBytes  = 16;
public const int HashBytes  = 32;
static string NewSalt();
static string Hash(string password, string saltHex, int iterations = Iterations);
static bool Verify(string password, string saltHex, string expectedHashHex, int iterations = Iterations);
```

- `Hash` = PBKDF2-SHA256(password, salt, iterations, 32 字节)，输出 **hex 字符串**存库。
- `Verify` 用**记录里的** `iterations` 重算，所以老账号（低迭代次数）仍能登录。
- 密码策略（长度/复杂度）不在这里，在 `UserManageWindow`/`UserRepository` 的校验里。

### `PermissionBit`（`[Flags]`，10 位）

| 位 | 名称 | 中文 | 门禁在哪 |
|---|---|---|---|
| 0 | `CanView` | 查看 | 登录即可看（所有页） |
| 1 | `CanWrite` | 参数设置 | `CommandGate.Enabled` 的前提 |
| 2 | `CanAckAlarm` | 报警确认 | 报警页「确认」按钮 |
| 3 | `CanQueryHistory` | 历史查询 | 趋势页查询 |
| 4 | `CanViewReport` | 报表查看 | 报表页 |
| 5 | `CanEditConfig` | 配置修改 | 「配置编辑器」按钮 |
| 6 | `CanManageUsers` | 用户管理 | 「用户管理」按钮 |
| 7 | `CanEditSystem` | 系统设置 | 「系统设置」按钮 |
| 8 | `CanJog` | 点动 | 急停/点动页「允许点动」 |
| 9 | `CanForce` | 强制写 | 强制写按钮（最危险，只给 admin） |

`All` 是这 10 位的并集。`PermissionNames.Describe(bits)` 按位展开成"查看、参数设置……"。
**加权限位**：加常量 + `PermissionNames.Of` 加 case + 界面 `ApplyXP Permission` 检查 + 用户管理窗口的勾选框，
共 4 处（见 09 篇配方）。

### 种子账号

`UserRepository.SeedDefaultAdmin()`：库为空时建
`admin / admin123`（`All` 权限）与 `op1 / op123`（只读 + 报警确认）。
**上线前必须改 admin 密码**，否则任何人都能写参数。

---

## 8. 扩展预留（`Extension\VisionMotionContracts.cs`）

`IVisionSystem`（拍照/取结果/标定）与 `IMotionSystem`（点位运动/回零/急停）**只有接口，没有实现**，
也没有任何代码引用。它是给"以后接视觉相机/运动控制器"留的位置。
不要把它当成能用的功能；要接就新写实现 + 一个描述符，不要改这两个接口的形状（会牵连未来的模块）。

---

## 9. 本层的测试（`KyjScada.Domain.Tests`，43 项全过）

| 文件 | 行数 | 覆盖 |
|---|---|---|
| `AlarmTests.cs` | 177 | 死区滞回、`null` 不改变状态、NaN、Reset、上限/下限镜像、181/383 场景 |
| `ProtocolTests.cs` | 174 | 11 个描述符的 `BuildAddress`/`ValidateAddress`、`Options` 声明、`ProtocolRegistry` 查找 |
| `Program.cs` | 222 | 配置解析/往返保真（`DataFormat`、`IsWritable`、11 键顺序）、`ActiveProject` 选空壳 |
| `TestRunner.cs` / `Assert.cs` | 43/50 | 手写测试框架（不是 xUnit） |

运行：

```powershell
.\KyjScada.Domain.Tests\bin\Debug\net8.0\KyjScada.Domain.Tests.exe
# 期望末行：43 通过 / 0 失败
```

**改本层任何规则性代码，先跑这个。** 它是"容器里能跑"的（无 WPF、无数据库）。

---

## 10. 改这里时的四条注意

1. **加配置字段**：`ConfigModels.cs` 加属性（带默认值）→ `SettingsJsonCodec.Parse` 读 →
   `ToJson` 写（**只在非默认时写**，保持老文件整洁）→ `Clone()` 里复制 → 加一条往返测试。
   漏掉 `Clone()` 会表现为"配置编辑器里改了、另一处没变"。
2. **别在 Domain 里 `DateTime.Now`**：状态机/模型接收外部传入的时间（`Update(value, at)`），
   这样测试可复现。目前 `HistorySchema` 只有格式常量，没有取时间。
3. **改 `AlarmStateMachine` 前先读注释**：两段失效方案的分析是血的教训，别"简化"成单比较。
4. **`ProtocolKeyMap` 里三个家族判断是给未来 PLC 用的**，`IsImplementedInV1` 才是采集的真实门禁。
   新增一个已实现的协议，要同时改 `IsImplementedInV1` + `AcquisitionFactory`（见 09 篇配方）。