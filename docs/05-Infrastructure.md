# 05 Infrastructure 基础设施层

> **读者**：要改采集/写入的 Modbus 实现、改数据库读写、改导出/迁移的人。
> **读完能做到**：知道 `ModbusAcquisitionService` 怎么一轮一轮读、`IModbusTransport` 抽象边界在哪、
> 三个 SQLite 仓储各自的表与并发模型、XLSX/老库迁移怎么用。
>
> `KyjScada.Infrastructure`：4,648 行（不含 obj），依赖 `Contracts + Domain`，
> NuGet：`Microsoft.Data.Sqlite 8.0.11`、`System.IO.Ports 8.0.0`。**无 `System.Windows`**。

---

## 1. 文件地图（实测行数）

| 文件 | 行数 | 职责 |
|---|---|---|
| `Acquisition\AcquisitionFactory.cs` | 93 | 按协议创建 `IAcquisitionService`；不支持的协议返回 null + 原因 |
| `Acquisition\ModbusAcquisitionService.cs` | 802 | **核心**：采集 + 写入（`IAcquisitionService, IDeviceWriter`），连接/重试/解码/地址解析 |
| `Modbus\IModbusTransport.cs` | 33 | 传输抽象（TCP/RTU/RTU-over-TCP 共用上层） |
| `Modbus\ModbusTcpClient.cs` | 417 | TCP MBAP 客户端（读/写寄存器与线圈） |
| `Modbus\ModbusTcpTransport.cs` | 46 | `IModbusTransport` 的 TCP 包装 |
| `Modbus\ModbusRtuTransport.cs` | 362 | 串口 RTU（收发 + 帧间隔 + CRC） |
| `Modbus\ModbusRtuOverTcpTransport.cs` | 328 | **RTU 帧跑裸 TCP**（现场协议，无 MBAP） |
| `Modbus\ModbusFrame.cs` | 375 | MBAP/功能码封装辅助 |
| `Modbus\ModbusRtuFrame.cs` | 148 | RTU 帧组装/解析 |
| `Modbus\ModbusCrc16.cs` | 43 | CRC16/MODBUS |
| `Modbus\ModbusValueCodec.cs` | 168 | 工程值 ↔ 寄存器字（**字节序**、类型宽度、写入编码） |
| `Data\HistoryRepository.cs` | 383 | `history.db` 长表读写、`VarDict` 缓存、WAL |
| `Data\LegacyMigrator.cs` | 357 | 老库（宽表 `ActualData`/`SysLog`）→ 长表迁移 |
| `Alarm\AlarmRepository.cs` | 275 | `alarms.db` 事件写入/查询/确认/老库 `AckAt` 迁移 |
| `Users\UserRepository.cs` | 259 | `users.db` 账号 CRUD、种子管理员、登录校验 |
| `Export\XlsxWriter.cs` | 376 | 无第三方库写 `.xlsx`（多 sheet） |
| `Config\ConfigValidator.cs` | 183 | 配置静态校验（地址/类型/报警/重复名），返回可读错误列表 |

---

## 2. 采集链路：`AcquisitionFactory` → `ModbusAcquisitionService`

### `AcquisitionFactory`

- `IsCollectable(device)` / `Create(device, out reason)`：根据 `device.ProtocolKey` 和
  `ProtocolKeyMap.IsImplementedInV1` 决定是否能建服务。
- 目前只支持 `ModbusTCP`、`ModbusRTU`、`ModbusRTUOverTCP`。其它协议返回 `null`，
  原因字符串如"协议 OPCUA 在 v1 尚未实现采集"（界面显示"· 未采集"）。
- **创建 transport 的分支**（`ModbusAcquisitionService` 构造里）：
  - `ModbusRTU` → `ModbusRtuTransport`（串口）
  - `ModbusRTUOverTCP` → `ModbusRtuOverTcpTransport`（裸 TCP + RTU 帧）
  - 其它 Modbus → `ModbusTcpTransport` / `ModbusTcpClient`（MBAP）

### `ModbusAcquisitionService`（802 行）—— 读写一体

实现 `IAcquisitionService` + `IDeviceWriter`，**同一实例、同一连接**（内部锁保证写不被读插入）。

#### `CollectOnce(bus, sampleTime)` 的真实行为

1. 协议未实现 → `throw ModbusException`（通常不会发生，工厂已挡）。
2. 建一个本轮 `batch` 字典。
3. `Connect()`：**接住所有异常**（`SocketException/IOException/UnauthorizedAccessException/TimeoutException` 等），
   失败则 `FailAllGroups`（全部变量标 `NotCollected`）→ `bus.Publish(batch)` → 返回 0，
   `LastError = DescribeFailure(...)`。**绝不保留上一轮旧值**（代码注释写死的安全要求）。
4. 逐组 `CollectGroup`：`ParseGroupStart` 得到功能码+基址，按 `Length`（或 `SpanOf` 兜底）读一整块，
   再用 `TryParseVarAddress` 把每个变量映射到块内偏移解码。
5. 一组读失败：只把**该组**变量标 `NotCollected`（不影响其他组），记录 `LastError`。
6. `bus.Publish(batch)`；全组 0 成功时保留错误"已连接但未读到任何数据…"。

#### 地址解析（采集真正用的那份）

- `ParseGroupStart("1.400001")` → `(0x03, 0)`；`"1.000001"` → `(0x01, 0)`（按数值区间，见 02 篇 4.1）。
- `TryParseVarAddress` 支持 `100`（纯偏移）、`1.40101`（站号.寄存器）、`200.05`（**偏移.位号**）。
  解析失败/越界 → 单点 `NotCollected`。
- `DescribeAddress(v)` 返回诊断字符串（测试/排查用）。
- ⚠ **与 `Domain\Protocol\*Descriptor.BuildAddress` 是两套实现**，改规则要两边一起改。

#### 写入

- 通过 `IDeviceWriter.WriteVariable(varName, value, out error)`：按变量类型选 0x05 写线圈（Bool）
  或 0x06/0x10 写寄存器；编码用 `ModbusValueCodec.Encode`（与读取互逆，注释里写了推导）。
- `RegisterCountForWrite`：Bool=0(线圈)、16 位=1、32 位=2、64 位/字符串=-1（不支持）。
- 调用方必须是 `CommandGate`（它已做完权限/限速/审计）；采集服务**不**做这些。

---

## 3. Modbus 传输层

| 类 | 场景 | 帧格式 | 关键点 |
|---|---|---|---|
| `ModbusTcpClient`/`ModbusTcpTransport` | `ModbusTCP` | MBAP (事务ID+协议0+长度+单元ID) + PDU | 标准 TCP 502 |
| `ModbusRtuTransport` | `ModbusRTU` | `[addr][func][data][crc16]` | 串口参数来自 `ConnectionConfig`；有帧间隔（3.5 字符时间）与超时 |
| `ModbusRtuOverTcpTransport` | `ModbusRTUOverTCP`（现场） | **RTU 帧直接跑 TCP，无 MBAP** | 现场网关就是这种；CRC16 校验仍在 |

`IModbusTransport` 定义读保持/输入寄存器、读线圈、写单/多寄存器、写单线圈与 `Connect/Disconnect/Dispose`。
上层（采集服务）只认接口，不关心底下是串口还是 TCP。

### `ModbusValueCodec` 与字节序

- 读取解码和写入编码**必须互逆**（写下去读回来值不能变）。
- `WordOrder` 影响 32/64 位：
  - `Abcd`=大端（寄存器顺序+字节顺序都自然）
  - `Badc`=字节交换
  - `Cdab`=**字交换**（现场 `DataFormat "2"`）
  - `Dcba`=全反
- Bool 取线圈位 / 寄存器某位（`200.05` 的 bit=5）在采集服务的 `ExtractVar` 里处理。

---

## 4. 三个 SQLite 仓储（并发与模式）

### `HistoryRepository`（383 行，`IHistoryRepository` 实现 + 扩展查询）

- 连接：`SqliteConnectionStringBuilder { DataSource, Mode=ReadWriteCreate, Cache=Shared }`，
  打开后 `PRAGMA journal_mode=wal; PRAGMA synchronous=normal;`。
- `WritePoints`（接口方法）：把 `HistoryPoint` 转成内部记录，走 `WriteBatch`（单事务）。
- `VarDict` 缓存：`_varIdCache`（`Dictionary<string,int>`，`OrdinalIgnoreCase`）+ `_cacheLock`；
  `GetOrCreateVarId` 先查缓存 → 查库 → `INSERT ...; SELECT last_insert_rowid()`。
- 查询：`Query(varName, from, to, limit)`（JOIN VarDict）、`QueryAll`、`GetVarDict`、`Count`、`DeleteBefore`。
- **`SampleTime` 存本地时间字符串**（`HistorySchema.TimeFormat`），不是 UTC。

### `AlarmRepository`（275 行）

- 表 `AlarmEvents`（含 `AckAt`）；`EnsureSchema` 做幂等迁移：老库无 `AckAt` 时
  `PRAGMA table_info` → `ALTER TABLE ADD COLUMN AckAt TEXT NULL`。
- `WriteEvents` 批量插入；`Query(from,to,varName,limit)`；`Acknowledge(id, at)` 仅在 `AckAt IS NULL` 时更新；
  `AcknowledgeAll(at)`；`DeleteBefore`。

### `UserRepository`（259 行）

- 表 `Users`（`UserName UNIQUE`，密码 PBKDF2 字段见 04 篇）。
- `SeedDefaultAdmin(password, now)`：仅库无用户时建 `admin`（`PermissionBit.All`）。
  **默认密码由调用方传入**（`MainWindow` 传 `"admin123"`，`LoginWindow` 提示里写死这串）。
- 登录校验：查账号 → `IsActive` → `PasswordHasher.Verify`（用库里存的 `Iterations`）→ 更新 `LastLoginAt`。

⚠ `alarms.db` / `users.db` / `history.db` 是**三个独立文件**。MCP 的 `DbReader` 默认只打开 `history.db`，
读报警/日志时按同目录约定找（见 08 篇）；路径变了要三处一起对。

---

## 5. `LegacyMigrator`（357 行）

- 用途：把老系统的**宽表** SQLite（`ActualData` 一列一个变量、`SysLog`）迁到新长表。
- CLI：`KyjScada.Migrate --from <老库> --to <新库> [--device <设备名>] [--dry-run]`；拒绝同一路径。
- 迁移时按变量列名建 `VarDict`、逐行拆成 `History` 记录；`--dry-run` 只统计不写。
- **不在日常运行路径上**，只在升级现场时用一次。

---

## 6. `XlsxWriter`（376 行）与 `ConfigValidator`（183 行）

- `XlsxWriter`：手写 OpenXML zip 结构（`[Content_Types].xml` + `_rels` + `xl/workbook.xml` + sheets + sharedStrings），
  **不依赖 EPPlus/NPOI**，避免许可证和体积。报表页「导出 Excel」走它。
- `ConfigValidator`：检查设备/组/变量的地址可解析性、类型合法性、报警阈值（上限>下限？死区非负）、
  位号重复、组长度与跨度。配置编辑器「验证」按钮调用；返回结构化错误（含定位信息）供界面标红。

---

## 7. 改这里的高频陷阱

| 改动 | 必须同时检查 |
|---|---|
| 改 `TryParseVarAddress` / `ParseGroupStart` | 02 篇 4.4 的"两套地址实现"；跑 Domain.Tests + Infrastructure.Tests 的地址/写路径用例 |
| 改 `ModbusValueCodec` 的字节序/类型 | 读写互逆（`WritePathTests`、`ModbusRtuTests`）；现场 `DataFormat=2 (CDAB)` |
| 改 `HistoryRepository` 写入/查询 SQL | `HistorySchema` 常量、MCP `DbReader` 自己的 SQL（它没引用 Domain）、趋势/报表查询 |
| 改 `AlarmRepository` 表结构 | `EnsureSchema` 的老库迁移路径；MCP `QueryAlarms` |
| 给 transport 加重试/超时 | 注意别阻塞采集线程太久（1 s 周期）；失败仍要 `NotCollected` 发布，不要吞掉 |
| 新增 Modbus 功能码 | `IModbusTransport` 接口 + 3 个 transport 实现 + 帧封装 |

✅ **最小回归**（改 Infrastructure 后至少跑）：

```powershell
.\KyjScada.Infrastructure.Tests\bin\Debug\net8.0\KyjScada.Infrastructure.Tests.exe
# 期望：292/0
```