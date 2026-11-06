# KyjScada.Next · MCP 原生集成骨架

> 阶段 2 产出:把 MCP 服务写进上位机骨架,**用真实数据库实测通过**。

---

## 这是什么

不是文档,是**能跑的代码**。已经在你的真实数据上验证:

- 组态:129 个变量(ModbusTCP.json)
- 数据库:6411 行 ActualData、37 个数据列、9103 条 SysLog
- 7 个 MCP 工具全部可用,23 项验收断言 0 失败

---

## 怎么跑

```powershell
cd D:\DeepSeekHarness\KyjScada.Next\KyjScada.Mcp
dotnet build

.\bin\Debug\net9.0\KyjScada.Mcp.exe `
  --settings "D:\DeepSeekHarness\KYJScadaProWpf\KYJScadaProWpf\bin\Debug\settings" `
  --db "D:\DeepSeekHarness\KYJScadaProWpf\KYJScadaProWpf\bin\Debug\database\KYJScada" `
  --port 18321
```

启动后 MCP 端点:`http://127.0.0.1:18321/mcp`

---

## 7 个工具

| 工具 | 用途 |
|---|---|
| `list_variables` | 变量元数据(中文描述/单位/归档/报警阈值) |
| `read_variable` | 单变量最新值 + 时间戳 + 新鲜度 |
| `read_variables` | 批量最新值 |
| `query_history` | 历史时序 |
| `query_stat` | 行数/最小/最大/平均 |
| `query_alarms` | 报警与操作日志 |
| `get_device_summary` | 设备 + 数据库概况 |

**全部只读。零风险。**

---

## 实测中发现并修掉的两个真问题

### ① 组态选错文件

`settings` 目录有三份 JSON,按字母序 `ModbusRTU.json` 排第一。

**直接取第一个 → 读 RTU 组态(139 变量),但现场实际是 ModbusTCP(129 变量)。**

修法:优先匹配 `TCP` 且排除 `Over`。

### ② 库里有异常值会骗过 AI

`LQT_Level` 实测分布:

| 值 | 行数 |
|---|---|
| `5.5` | 4554(正常) |
| `''` | 1188(空串) |
| `15.5` | 391 |
| `6.6` | 123 |
| `59` | 89 |
| **`15421` 等递增序列** | **每值 1 行** |

**后果**:算出来的平均是 `184`,最大 `15421`,而正常值约 `5.5`。AI 若直接采信会得出完全错误的结论。

修法:用组态报警上限做合理性判断,超过上限 10 倍就返回**数据质量警告**,提示先抽样确认。

---

## 设计铁律(已写进代码注释)

1. **只读打开**:`Mode=ReadOnly`,绝不干扰主程序 WAL
2. **NULL 必须显式返回 null**,绝不默认 0(`LQB1_Start` 实测 96% 是 NULL)
3. **必须带时间戳 + 新鲜度分级**:`fresh` / `stale` / `offline`,避免把归档值当实时值
4. **列名白名单**:`query_history` 只接受 PRAGMA 查出的真实列,杜绝注入
5. **聚合必须 `CAST AS REAL` + `GLOB '[0-9-]*'`**,不用 `typeof()`

---

## 目录

```
KyjScada.Mcp\
├─ KyjScada.Mcp.csproj      net9.0 + MCP SDK 2.3.0 + Microsoft.Data.Sqlite 9.0
├─ Program.cs               托管入口(正式集成时搬进 WPF App.OnStartup)
├─ Data\
│  ├─ Models.cs             组态模型
│  ├─ ConfigReader.cs       settings/*.json 读取(UTF-8-sig 容错)
│  └─ DbReader.cs           SQLite 只读访问
└─ Tools\
   └─ ReadOnlyTools.cs      7 个 MCP 工具
```

---

## 验收

```powershell
$node = 'C:\Users\Administrator\.dsh\dsh-runtimes\dsh-primary-runtime\dependencies\node\bin\node.exe'
& $node D:\DeepSeekHarness\agent\_accept.mjs
```

期望输出:`通过: 23 / 失败: 0 / 合计: 23`

---

## 下一步

| 步骤 | 内容 |
|---|---|
| 1 | 把托管代码搬进 WPF `App.OnStartup`(已验证可行) |
| 2 | 加内存实时值访问(需与主程序同进程) |
| 3 | 写工具 + 权限护栏(双端点) |
| 4 | 逐块迁移业务逻辑 |
