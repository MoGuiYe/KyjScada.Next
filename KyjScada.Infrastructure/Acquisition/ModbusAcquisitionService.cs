using KyjScada.Contracts;
using KyjScada.Domain.Config;
using KyjScada.Domain.Protocol;
using KyjScada.Infrastructure.Modbus;

namespace KyjScada.Infrastructure.Acquisition;

/// <summary>
/// Modbus 采集服务。按组连续读:一组一次往返。
/// 现场 4 个组 129 变量,按组读 4 次往返,逐个读要 129 次。
///
/// **传输层可替换**:同一个服务既能跑 Modbus TCP,也能跑串口 RTU ——
/// 差别只在 IModbusTransport 的实现,组划分/解码/字节序处理完全共用。
/// 铁律:只有采集器写总线;NULL 永不等于 0;地址由描述符算。
/// </summary>
public sealed class ModbusAcquisitionService : IAcquisitionService, IDeviceWriter
{
    private readonly DeviceConfig _device;
    private readonly IProtocolDescriptor _descriptor;
    private readonly IModbusTransport _client;

    /// <summary>Modbus TCP(默认,按连接配置里的 IP/端口建传输层)。</summary>
    public ModbusAcquisitionService (DeviceConfig device, IProtocolDescriptor descriptor)
        : this (device, descriptor, null)
    {
    }

    /// <summary>
    /// 指定传输层。传 null 时按连接类型自动选:
    /// Tcp → Modbus TCP;Serial → Modbus RTU 串口。
    /// </summary>
    public ModbusAcquisitionService (
        DeviceConfig device, IProtocolDescriptor descriptor, IModbusTransport? transport)
    {
        // 描述符为空属于调用方 bug,必须在构造时就明确报出来,
        // 否则会在采集时炸出一个和真实原因无关的空引用,极难排查
        if (descriptor is null)
        {
            throw new ArgumentNullException (nameof (descriptor), "协议描述符不能为空");
        }

        _device = device;
        _descriptor = descriptor;

        if (transport is not null)
        {
            _client = transport;
            return;
        }

        var conn = device.Connection;

        if (conn?.Kind == ConnectionKind.Serial)
        {
            _client = new ModbusRtuTransport (
                conn.PortName ?? "COM1",
                conn.BaudRate,
                conn.Parity,
                conn.DataBits,
                conn.StopBits,
                StationIdOf (device));
        }
        else if (string.Equals (_descriptor.Key, "ModbusRTUOverTCP", StringComparison.OrdinalIgnoreCase))
        {
            // RTU over TCP:帧是 RTU(带 CRC、无 MBAP),只是走 TCP 流。
            // 这里绝不能退化用 ModbusTcpTransport —— 那会发 MBAP 帧,网关不认。
            _client = new ModbusRtuOverTcpTransport (
                conn?.IpAddress ?? "127.0.0.1",
                conn?.Port ?? 502,
                StationIdOf (device));
        }
        else
        {
            var host = conn?.IpAddress ?? "127.0.0.1";
            var port = conn?.Port ?? 502;
            _client = new ModbusTcpTransport (host, port, StationIdOf (device));
        }
    }

    /// <summary>
    /// 从组起始地址的前缀推导从站号。
    /// 老系统的约定:"1.40001" 里的 1 是**从站地址**,不是区号;
    /// 没有前缀或解析失败时按 1 处理。
    /// </summary>
    public static byte StationIdOf (DeviceConfig device)
    {
        foreach (var g in device.GroupList)
        {
            var start = g.Start;

            if (string.IsNullOrWhiteSpace (start)) continue;

            var dot = start.IndexOf ('.');

            if (dot <= 0) continue;

            if (byte.TryParse (start.Substring (0, dot), out var id) && id > 0) return id;
        }

        return 1;
    }

    public string DeviceName => _device.DeviceName;

    /// <summary>
    /// 单轮采集的时间预算。设备掉线时每个分块读都要等满超时,
    /// 不给上限的话"一轮"能跑几十秒,期间界面显示的全是旧值。
    /// 超过预算就把剩下的组标成未采集并发布,让界面尽快反映"没数据"。
    /// </summary>
    public TimeSpan CycleBudget { get; set; } = TimeSpan.FromSeconds (4);

    public bool IsConnected => _client.IsConnected;

    /// <summary>最近一次连接/读取失败原因。成功一次就清空。</summary>
    public string? LastError { get; private set; }

    public int CollectOnce (IValueBus bus, DateTime sampleTime)
    {
        var unsupported = !ProtocolKeyMap.IsImplementedInV1 (_descriptor.Key);
        if (unsupported) throw new ModbusException ("协议 " + _descriptor.Key + " 在 v1 尚未实现采集");

        var groups = _device.GroupList;
        if (groups is null || groups.Count == 0) return 0;

        var batch = new Dictionary<string, TagValue> (StringComparer.Ordinal);
        var successCount = 0;

        try
        {
            _client.Connect ();
        }
        catch (Exception ex)
        {
            // 连不上属于运行中的常态,不是配置错误。
            // 必须把全部变量标记为 NotCollected,绝不能保留上一轮的旧值 ——
            // 操作工看着冻结的旧数据以为是实时值,是老系统最危险的问题。
            //
            // 这里**故意接住所有异常**(不只是 ModbusException):串口打开失败抛
            // IOException/UnauthorizedAccessException,网线拔掉抛 SocketException,
            // DNS 失败抛 SocketException —— 只要漏出一种,整轮采集就跳过 Publish,
            // 界面会一直显示断线前的旧值,而且状态栏变成"异常"而不是"未连接"。
            LastError = DescribeFailure (ex, _device);
            FailAllGroups (groups, batch, sampleTime);
            bus.Publish (batch);
            return 0;
        }

        LastError = null;

        // ==================== 整轮预算 ====================
        // 设备掉线时每个分块读都要等满超时(2 秒),4 个组就是 8 秒起步,组多/分块多时更久。
        // 这段时间里一个点都不发布 —— 界面就一直显示断线前的最后一帧,
        // 操作工以为是实时值(老系统最危险的问题),而状态栏还写着"正常"。
        //
        // 这里给整轮设上限:超时就把**剩下的组直接标成未采集并发布**,
        // 保证界面最多迟钝一个预算周期,而不是无限期显示旧值。
        var cycleBudgetMs = Math.Max (2000, CycleBudget.TotalMilliseconds);
        var cycleStart = Environment.TickCount64;

        var aborted = false;

        foreach (var group in groups)
        {
            var vars = group.VarList;
            if (vars is null || vars.Count == 0) continue;

            if (Environment.TickCount64 - cycleStart > cycleBudgetMs)
            {
                aborted = true;

                break;
            }

            successCount += CollectGroup (group, batch, sampleTime);
        }

        if (aborted)
        {
            // 没轮到的点位必须显式标"未采集":留着不写等于让界面继续显示上一轮的旧值
            foreach (var group in groups)
            {
                var vars = group.VarList;
                if (vars is null) continue;

                foreach (var v in vars)
                {
                    if (string.IsNullOrEmpty (v.VarName)) continue;
                    if (batch.ContainsKey (v.VarName)) continue;

                    batch[v.VarName] = new TagValue (v.VarName, null, TagQuality.NotCollected, sampleTime);
                }
            }

            LastError ??= "本轮通讯超时(超过 " + (cycleBudgetMs / 1000.0).ToString ("0.#") + " 秒),已中断其余组的采集";
        }

        // 只有真正读到数据才清掉错误;连上了但一路读失败仍要保留原因
        if (successCount == 0) LastError ??= "已连接但未读到任何数据(从站无响应或地址不对)";
        else LastError = null;

        bus.Publish (batch);
        return successCount;
    }

    /// <summary>
    /// 把传输层抛出的任意异常翻译成现场看得懂的一句话。
    /// 界面状态栏直接显示这句,所以必须带上是哪台设备/哪个地址出的问题。
    /// </summary>
    private static string DescribeFailure (Exception ex, DeviceConfig device)
    {
        var where = device.Connection?.Describe () ?? device.DeviceName;

        if (ex is ModbusException) return ex.Message;

        if (ex is System.Net.Sockets.SocketException sock)
            return "连不上 " + where + ":" + sock.Message;

        if (ex is System.IO.IOException io)
            return "串口/网络读写失败 " + where + ":" + io.Message;

        if (ex is UnauthorizedAccessException)
            return "串口被占用或无权限:" + where;

        if (ex is TimeoutException)
            return "通讯超时:" + where;

        return "通讯失败 " + where + ":" + ex.Message;
    }

    private static void FailAllGroups (
        List<GroupConfig> groups, Dictionary<string, TagValue> batch, DateTime t)
    {
        foreach (var group in groups)
        {
            var vars = group.VarList;
            if (vars is null) continue;

            foreach (var v in vars)
            {
                batch[v.VarName] = new TagValue (
                    v.VarName, null, TagQuality.NotCollected, t);
            }
        }
    }

    private int CollectGroup (GroupConfig group, Dictionary<string, TagValue> batch, DateTime t)
    {
        var vars = group.VarList;
        var parsed = ParseGroupStart (group.Start);

        if (parsed is null)
        {
            FailAll (vars, batch, t);
            return 0;
        }

        var fn = parsed.Value.Fn;
        var baseOffset = parsed.Value.Offset;
        var length = group.Length > 0 ? group.Length : SpanOf (vars, baseOffset);

        if (length <= 0)
        {
            FailAll (vars, batch, t);
            return 0;
        }

        ushort[] raw;
        bool[] arrived;

        try
        {
            var spans = Spans (vars, baseOffset);

            if (fn == ModbusFunction.ReadCoils)
            {
                raw = CoilsToWords (ReadCoilsChunked (baseOffset, length, spans, out arrived));
            }
            else if (fn == ModbusFunction.ReadInputRegisters)
            {
                raw = ReadRegistersChunked (baseOffset, length, spans, true, out arrived);
            }
            else
            {
                raw = ReadRegistersChunked (baseOffset, length, spans, false, out arrived);
            }
        }
        catch (Exception ex)
        {
            // 记录原因:串口没插、从站没响应、CRC 错都会走到这里。
            // 不记的话界面只能显示"未连接",现场无从排查。
            // 同 Connect:这里也必须兜住所有异常类型,否则会跳过 Publish 留下旧值。
            LastError ??= DescribeFailure (ex, _device);

            FailAll (vars, batch, t);
            return 0;
        }

        var okCount = 0;

        foreach (var v in vars)
        {
            // 变量 Start 是【绝对偏移】,而 raw 是从【组起始】开始的连续块。
            // 必须减去组基准,否则设定值组(基准 100)和测试组(基准 200)全部越界。
            if (!TryParseVarAddress (v, out var abs, out var bit))
            {
                batch[v.VarName] = new TagValue (v.VarName, null, TagQuality.NotCollected, t);
                continue;
            }

            var rel = abs - baseOffset;

            // 这个点要的那几个寄存器是否都真的到了(短响应时后面的点可能没到)
            var need = fn == ModbusFunction.ReadCoils || bit >= 0 ? 1 : RegisterCount (v);
            var complete = rel >= 0 && rel + need <= arrived.Length && AllArrived (arrived, rel, need);

            var val = complete ? ExtractVar (raw, v, fn, rel, bit, _device.WordOrder) : null;
            var ok = val.HasValue;
            var tv = new TagValue (v.VarName, ok ? val.Value : null, ok ? TagQuality.Good : TagQuality.NotCollected, t);
            batch[v.VarName] = tv;
            if (ok) okCount++;
        }

        return okCount;
    }

    private static bool AllArrived (bool[] arrived, int offset, int count)
    {
        for (var i = 0; i < count; i++)
        {
            if (!arrived[offset + i]) return false;
        }

        return true;
    }

    private static void FailAll (List<VariableConfig> vars, Dictionary<string, TagValue> batch, DateTime t)
    {
        foreach (var v in vars)
        {
            batch[v.VarName] = new TagValue (v.VarName, null, TagQuality.NotCollected, t);
        }
    }

    /// <summary>单帧读寄存器的协议上限(PDU 253 字节 → 125 个寄存器)。</summary>
    private const int MaxRegistersPerRead = 125;

    /// <summary>单帧读线圈的协议上限。</summary>
    private const int MaxCoilsPerRead = 2000;

    /// <summary>
    /// 分帧计划:把 [0, count) 切成每帧不超过 <paramref name="maxPerChunk"/> 的若干段。
    ///
    /// 两条硬约束:
    ///  1. **不能把多寄存器变量劈在两帧里**。32 位值(2 寄存器)/64 位值(4 寄存器)
    ///     若前一半取自这一帧、后一半取自下一帧,两次读取之间设备值变了,拼出来的是一个
    ///     从未真实存在过的数 —— 现场表现是"偶尔蹦出一个离谱的值"。
    ///     所以边界落在一个变量的中间时,把本帧截短到它的起点。
    ///  2. 每帧的绝对地址必须留在 16 位地址空间内。组起始 65500、长度 200 时,
    ///     第二帧地址会 (ushort) 回绕成 89 —— 不报错,但读到的是**别的点位**,
    ///     比读失败危险得多。
    /// </summary>
    private static List<(int Offset, int Length)> PlanChunks (
        int count, int maxPerChunk, IReadOnlyList<(int Offset, int Length)> spans)
    {
        var plan = new List<(int, int)> ();
        var done = 0;

        while (done < count)
        {
            var take = Math.Min (maxPerChunk, count - done);

            foreach (var s in spans)
            {
                if (s.Length <= 1) continue;                      // 单寄存器/位点不会跨帧
                if (s.Offset <= done || s.Offset >= done + take) continue;
                if (s.Offset + s.Length <= done + take) continue; // 完整落在本帧内

                // 这个变量跨过了本帧末尾:截短到它的起点
                take = s.Offset - done;
            }

            // 变量本身比单帧上限还长(理论上不会发生)时不能退化成死循环
            if (take <= 0) take = Math.Min (maxPerChunk, count - done);

            plan.Add((done, take));
            done += take;
        }

        return plan;
    }

    /// <summary>帧地址边界检查:越界就明确报错,绝不静默回绕到别的点位。</summary>
    private static void CheckAddress (int address, int count)
    {
        if (address < 0 || address + count > 0x10000)
        {
            throw new ArgumentOutOfRangeException (
                nameof (address),
                "组地址越界:" + address + " + " + count + " 超出 Modbus 16 位地址空间,请检查组配置");
        }
    }

    /// <summary>组内所有"多寄存器变量"的跨度(相对组起点的偏移),给 <see cref="PlanChunks"/> 用。</summary>
    private static List<(int Offset, int Length)> Spans (List<VariableConfig> vars, int baseOffset)
    {
        var spans = new List<(int, int)> ();

        foreach (var v in vars)
        {
            var abs = VarOffset (v);
            if (abs < 0) continue;

            var rel = abs - baseOffset;
            if (rel < 0) continue;

            spans.Add((rel, RegisterCount (v)));
        }

        return spans;
    }

    /// <summary>
    /// 按协议上限分段读寄存器。
    ///
    /// <paramref name="arrived"/> 标出哪些位置真的读到了:从站返回的寄存器数比请求少时
    /// (老旧从站、最后一个不完整的缓冲区),原来直接 Array.Copy(take) 会抛异常,
    /// 整组一起变成"通讯失败" —— 一百多个点全红,只因为最后一帧少了一个寄存器。
    /// 现在按实到的长度拷贝,点位级判断"我这几个寄存器到齐没有"。
    /// </summary>
    private ushort[] ReadRegistersChunked (
        int start, int count, IReadOnlyList<(int Offset, int Length)> spans, bool input, out bool[] arrived)
    {
        var all = new ushort[count];
        arrived = new bool[count];

        foreach (var (offset, take) in PlanChunks (count, MaxRegistersPerRead, spans))
        {
            CheckAddress (start + offset, take);

            var part = input
                ? _client.ReadInputRegisters ((ushort)(start + offset), (ushort)take)
                : _client.ReadHoldingRegisters ((ushort)(start + offset), (ushort)take);

            var got = part is null ? 0 : Math.Min (part.Length, take);

            if (got > 0) Array.Copy (part!, 0, all, offset, got);

            for (var i = 0; i < got; i++) arrived[offset + i] = true;

            if (got < take)
            {
                LastError ??= "从站返回的寄存器数不足(" + got + "/" + take
                    + "),地址 " + (start + offset) + " 之后的点按未采集处理";
            }
        }

        return all;
    }

    /// <summary>按协议上限分段读线圈(判定口径同 <see cref="ReadRegistersChunked"/>)。</summary>
    private bool[] ReadCoilsChunked (
        int start, int count, IReadOnlyList<(int Offset, int Length)> spans, out bool[] arrived)
    {
        var all = new bool[count];
        arrived = new bool[count];

        foreach (var (offset, take) in PlanChunks (count, MaxCoilsPerRead, spans))
        {
            CheckAddress (start + offset, take);

            var part = _client.ReadCoils ((ushort)(start + offset), (ushort)take);
            var got = part is null ? 0 : Math.Min (part.Length, take);

            if (got > 0) Array.Copy (part!, 0, all, offset, got);

            for (var i = 0; i < got; i++) arrived[offset + i] = true;

            if (got < take)
            {
                LastError ??= "从站返回的线圈数不足(" + got + "/" + take
                    + "),地址 " + (start + offset) + " 之后的点按未采集处理";
            }
        }

        return all;
    }

    private static (byte Fn, ushort Offset)? ParseGroupStart (string start)
    {
        if (string.IsNullOrWhiteSpace (start)) return null;

        var dot = start.IndexOf ('.');
        var pure = dot >= 0 ? start.Substring (dot + 1) : start;
        if (!int.TryParse (pure, out var n)) return null;

        if (n >= 400001 && n <= 465536) return (ModbusFunction.ReadHoldingRegisters, (ushort)(n - 400001));
        if (n >= 40001 && n <= 49999) return (ModbusFunction.ReadHoldingRegisters, (ushort)(n - 40001));
        if (n >= 300001 && n <= 365536) return (ModbusFunction.ReadInputRegisters, (ushort)(n - 300001));
        if (n >= 30001 && n <= 39999) return (ModbusFunction.ReadInputRegisters, (ushort)(n - 30001));
        if (n >= 100001 && n <= 165536) return (ModbusFunction.ReadCoils, (ushort)(n - 100001));
        if (n >= 10001 && n <= 19999) return (ModbusFunction.ReadCoils, (ushort)(n - 10001));
        if (n >= 1 && n <= 9999) return (ModbusFunction.ReadCoils, (ushort)(n - 1));
        return null;
    }

    /// <summary>按组内相对偏移算跨度(不是绝对地址),用于 Length 缺失时兜底。</summary>
    private static int SpanOf (List<VariableConfig> vars, int baseOffset)
    {
        var max = 0;
        foreach (var v in vars)
        {
            var abs = VarOffset (v);
            if (abs < 0) continue;
            var rel = abs - baseOffset;
            if (rel < 0) continue;
            var span = rel + RegisterCount (v);
            if (span > max) max = span;
        }
        return max;
    }

    /// <summary>
    /// 解析变量地址。返回 (绝对偏移, 位号)。位号 != 0 表示这是寄存器里的某一位。
    ///
    /// 现场配置里 Start 有三种写法,必须区分开:
    ///   · `100`        → 纯偏移(设定值组:组基准 100,变量 100 → 相对 0)
    ///   · `200.01`     → **偏移.位号**(测试组:寄存器 200 的第 1 位),不是小数!
    ///   · `1.40101`    → 完整地址(站号.寄存器),用于导出/显示
    ///
    /// 老系统的 `ModbusAddressAnalysis` 也是按"站号.寄存器.位"三段处理,
    /// 把第二个点误当小数会算出负偏移,表现为"这个点永远读不到/写不进"。
    /// </summary>
    private static bool TryParseVarAddress (VariableConfig v, out int abs, out int bit)
    {
        abs = -1;
        // -1 = 没有位号;0 是合法的"第 0 位"。以前拿 0 当哨兵,
        // 于是 `40001.0` 这种点位会被当成"整寄存器"读,值永远是错的。
        bit = -1;

        var raw = v.Start;

        if (string.IsNullOrWhiteSpace (raw)) raw = v.VarAddress;
        if (string.IsNullOrWhiteSpace (raw)) return false;

        var parts = raw.Split ('.');

        if (parts.Length == 1)
        {
            if (!int.TryParse (parts[0], out var only)) return false;

            // 单段也要归一:现场有 `40001` 这种写法(等于偏移 0),
            // 也有 `100` 这种纯偏移。漏掉归一会让偏移变成 40001 → 直接越界读不到。
            abs = NormalizeOffset (only);
            bit = -1;
            return true;
        }

        if (parts.Length >= 2)
        {
            var seg0Ok = int.TryParse (parts[0], out var seg0);
            var seg1Ok = int.TryParse (parts[1], out var seg1);

            if (!seg1Ok) return false;

            // 第二段是"寄存器号"(>=40001 这种) → 说明整体是完整地址 站号.寄存器
            if (seg1 >= 40001)
            {
                if (!seg0Ok) return false;

                abs = NormalizeRegister (seg1);
                bit = -1;
                return true;
            }

            // 否则按"偏移.位号"
            if (!seg0Ok) return false;

            abs = NormalizeOffset (seg0);

            // 位号必须在 0~15,否则说明这不是位地址
            if (parts.Length >= 2 && seg1 >= 0 && seg1 <= 15) bit = seg1;

            return true;
        }

        return false;
    }

    /// <summary>把 4xxxx/3xxxx 这类地址归一到寄存器偏移。</summary>
    private static int NormalizeRegister (int n)
    {
        if (n >= 400001) return n - 400001;
        if (n >= 40001 && n <= 49999) return n - 40001;
        if (n >= 300001) return n - 300001;
        if (n >= 30001 && n <= 39999) return n - 30001;

        return n;
    }

    /// <summary>纯偏移:4xxxx 形式也归一到偏移,否则原样。</summary>
    private static int NormalizeOffset (int n)
    {
        if (n >= 400001) return n - 400001;
        if (n >= 40001 && n <= 49999) return n - 40001;

        return n;
    }

    /// <summary>
    /// 地址解析诊断:返回"绝对偏移 / 位号 / 是否成功"。给测试和现场排查用。
    /// </summary>
    public static string DescribeAddress (VariableConfig v)
    {
        var ok = TryParseVarAddress (v, out var abs, out var bit);

        return (ok ? "OK" : "FAIL")
            + " start='" + v.Start + "' varAddr='" + v.VarAddress + "'"
            + " abs=" + abs + " bit=" + bit;
    }

    private static int VarOffset (VariableConfig v)
    {
        return TryParseVarAddress (v, out var abs, out _) ? abs : -1;
    }

    private static double? ExtractVar (
        ushort[] raw, VariableConfig v, byte fn, int rel, int bit, WordOrder order)
    {
        if (raw is null || raw.Length == 0) return null;
        if (rel < 0) return null;

        var count = RegisterCount (v);
        if (rel + count > raw.Length) return null;

        if (fn == ModbusFunction.ReadCoils) return raw[rel] != 0 ? 1.0 : 0.0;

        // 位地址(`200.01` 这种):取寄存器的某一位,而不是整个字
        if (bit >= 0)
        {
            return ((raw[rel] >> bit) & 0x1) != 0 ? 1.0 : 0.0;
        }

        var slice = new ushort[count];
        Array.Copy (raw, rel, slice, 0, count);
        return ConvertRawToValue (slice, v, order);
    }

    private static ushort[] CoilsToWords (bool[] coils)
    {
        if (coils is null) return Array.Empty<ushort> ();
        var words = new ushort[coils.Length];
        var i = 0;
        while (i < coils.Length)
        {
            words[i] = coils[i] ? (ushort)1 : (ushort)0;
            i++;
        }
        return words;
    }

    private static ushort RegisterCount (VariableConfig v)
    {
        var t = (v.VarType ?? "").Trim ().ToLowerInvariant ();
        switch (t)
        {
            // 64 位占 4 个寄存器。Double 漏掉会让后续地址整体错位。
            case "double": case "long": case "int64": case "ulong": case "uint64":
                return 4;
            // 32 位占 2 个寄存器。
            case "float": case "single": case "int": case "int32": case "uint": case "uint32": case "dword":
                return 2;
            // 16 位及以下占 1 个寄存器(寄存器是最小寻址单位)。
            default:
                return 1;
        }
    }

    private static bool NeedsByteSwap (WordOrder order)
        => order == WordOrder.Badc || order == WordOrder.Dcba;

    /// <summary>交换单个寄存器内的高/低字节。</summary>
    private static ushort SwapRegisterBytes (ushort raw) => (ushort)((raw >> 8) | (raw << 8));

    private static double? ConvertRawToValue (ushort[] raw, VariableConfig v, WordOrder order)
    {
        var t = (v.VarType ?? "").Trim ().ToLowerInvariant ();
        double eng;
        switch (t)
        {
            case "bool": eng = raw[0] != 0 ? 1.0 : 0.0; break;
            // Byte 取寄存器的一个字节,不是整个 16 位。
            // 老系统按 DataFormat 决定取高字节还是低字节:ABCD/CDAB 取高,BADC/DCBA 取低。
            case "byte":
                eng = (order == WordOrder.Badc || order == WordOrder.Dcba)
                    ? (byte)(raw[0] & 0xFF)
                    : (byte)(raw[0] >> 8);
                break;
            case "sbyte":
                // 与 byte 同样的高/低字节约定,只是带符号:
                // 以前没有这个分支,写 -1 会落到 default 按无符号读成 65535。
                eng = (order == WordOrder.Badc || order == WordOrder.Dcba)
                    ? (sbyte)(raw[0] & 0xFF)
                    : (sbyte)(raw[0] >> 8);
                break;
            case "short":
                // 16 位单寄存器:BADC/DCBA 下字节是反的,必须和写入编码对称
                // (以前这里不交换,而 Encode 交换 —— 写了值读回来的永远是别人)。
                eng = (short)(NeedsByteSwap (order) ? SwapRegisterBytes (raw[0]) : raw[0]);
                break;
            case "ushort": case "word":
                eng = NeedsByteSwap (order) ? SwapRegisterBytes (raw[0]) : raw[0];
                break;
            case "int": case "int32":
                if (raw.Length < 2) return null;
                eng = BitConverter.ToInt32 (ToBytes (raw, 2, order), 0);
                break;
            case "uint": case "uint32": case "dword":
                if (raw.Length < 2) return null;
                eng = BitConverter.ToUInt32 (ToBytes (raw, 2, order), 0);
                break;
            case "float": case "single":
                if (raw.Length < 2) return null;
                eng = BitConverter.ToSingle (ToBytes (raw, 2, order), 0);
                break;
            case "double":
                if (raw.Length < 4) return null;
                eng = BitConverter.ToDouble (ToBytes (raw, 4, order), 0);
                break;
            case "long": case "int64":
                if (raw.Length < 4) return null;
                eng = BitConverter.ToInt64 (ToBytes (raw, 4, order), 0);
                break;
            case "ulong": case "uint64":
                if (raw.Length < 4) return null;
                eng = BitConverter.ToUInt64 (ToBytes (raw, 4, order), 0);
                break;
            default: eng = raw[0]; break;
        }
        var scale = v.Scale;
        var useScale = scale == 0f ? 1f : scale;
        var result = eng * useScale + v.Offset;
        if (double.IsNaN (result) || double.IsInfinity (result)) return null;
        return result;
    }

    /// <summary>
    /// 把 count 个寄存器按 WordOrder 拼成小端字节序(供 BitConverter 直接读)。
    /// Abcd=寄存器顺序不变(大端);Cdab=寄存器顺序反转(字交换);
    /// Badc=寄存器内字节交换;Dcba=寄存器反转且字节交换(等同全字节反转)。
    /// </summary>
    private static byte[] ToBytes (ushort[] raw, int count, WordOrder order)
    {
        var regs = new ushort[count];
        Array.Copy (raw, regs, count);

        var swapWords = order == WordOrder.Cdab || order == WordOrder.Dcba;
        var swapBytes = order == WordOrder.Badc || order == WordOrder.Dcba;

        if (swapWords) Array.Reverse (regs);

        if (swapBytes)
        {
            var i = 0;
            while (i < regs.Length)
            {
                var x = regs[i];
                regs[i] = (ushort)((x << 8) | (x >> 8));
                i++;
            }
        }

        // 每寄存器 2 字节,按 大端(hi 在前) 展开成 网络序字节。
        var bytes = new byte[count * 2];
        var k = 0;
        while (k < count)
        {
            bytes[k * 2] = (byte)(regs[k] >> 8);
            bytes[k * 2 + 1] = (byte)(regs[k] & 0xFF);
            k++;
        }

        // BitConverter 在小端机上按小端解释,所以先反转成小端存放。
        if (BitConverter.IsLittleEndian) Array.Reverse (bytes);
        return bytes;
    }

    /// <summary>
    /// 校验设备布局:每个变量的相对偏移必须落在其组长度内。
    /// 返回问题描述列表,空列表表示布局可用。
    /// 这是配置期诊断,不是运行期逻辑 —— 现场改完配置能立刻知道哪个变量越界。
    /// </summary>
    public static List<string> ValidateLayout (DeviceConfig device)
    {
        var problems = new List<string> ();
        if (device?.GroupList is null) return problems;

        foreach (var g in device.GroupList)
        {
            var parsed = ParseGroupStart (g.Start);
            if (parsed is null)
            {
                problems.Add ("组 " + g.GroupName + ":起始地址无法解析 (" + g.Start + ")");
                continue;
            }

            var baseOffset = parsed.Value.Offset;
            var length = g.Length;
            if (length <= 0)
            {
                problems.Add ("组 " + g.GroupName + ":长度无效 (" + length + ")");
                continue;
            }

            if (g.VarList is null) continue;

            foreach (var v in g.VarList)
            {
                var abs = VarOffset (v);
                if (abs < 0)
                {
                    problems.Add ("变量 " + v.VarName + ":地址无法解析 (" + v.Start + ")");
                    continue;
                }

                var rel = abs - baseOffset;
                var count = RegisterCount (v);

                if (rel < 0)
                {
                    problems.Add ("变量 " + v.VarName + ":偏移 " + rel + " 小于组基准(地址在组起点之前)");
                    continue;
                }

                if (rel + count > length)
                {
                    problems.Add ("变量 " + v.VarName + ":偏移 " + rel + "+" + count
                        + " 超出组长 " + length);
                }
            }
        }

        return problems;
    }

    // ==================== 写入(实现 IDeviceWriter) ====================

    /// <summary>
    /// 该点是否可写。规则只有两条:**区可写**(保持寄存器 4xxxx / 线圈 0xxxx-1xxxx),
    /// 且**类型支持编码**。输入寄存器(3xxxx)是只读区,写了设备会回异常码 02。
    /// </summary>
    public static bool CanWrite (string? groupStart, VariableConfig v, out string reason)
    {
        var parsed = ParseGroupStart (groupStart ?? "");

        if (parsed is null)
        {
            reason = "组起始地址无法解析";
            return false;
        }

        if (parsed.Value.Fn == ModbusFunction.ReadInputRegisters)
        {
            reason = "只读区(输入寄存器),不能写";
            return false;
        }

        if (parsed.Value.Fn == ModbusFunction.ReadDiscreteInputs)
        {
            reason = "只读区(离散输入),不能写";
            return false;
        }

        var type = (v.VarType ?? "").Trim ().ToLowerInvariant ();

        // 位地址必须拒绝:Modbus 没有"写单个位"的功能码,要读-改-写;
        // 而写入路径遇到寄存器区 Bool 又提示"去加位号",两处一起把这条配置变成死路。
        if (TryParseVarAddress (v, out _, out var bit) && bit >= 0)
        {
            reason = "寄存器位地址(4xxxx.NN)不支持写入:Modbus 没有写单个位的功能码,"
                + "写整字会清零同寄存器其他位;该点请改用 0xxxx 线圈地址";
            return false;
        }

        if (type is "string" or "bytearray")
        {
            reason = "字符串/字节数组暂不支持写入";
            return false;
        }

        if (type is "double" or "long" or "int64" or "ulong" or "uint64")
        {
            reason = "64 位类型暂不支持写入(需一次写 4 寄存器,尚未实现)";
            return false;
        }

        if (ModbusValueCodec.Encode (0, v, WordOrder.Abcd) is null && type != "bool")
        {
            reason = "类型 " + v.VarType + " 不支持写入";
            return false;
        }

        reason = "";
        return true;
    }

    /// <summary>
    /// 写入一个工程值。**与采集共用同一个连接** ——
    /// 串口只能被打开一次,另开一个连接会直接失败或抢走数据。
    /// </summary>
    public bool WriteVariable (string varName, double value, out string error)
    {
        error = "";

        VariableConfig? target = null;
        GroupConfig? targetGroup = null;

        foreach (var g in _device.GroupList)
        {
            if (g.VarList is null) continue;

            foreach (var v in g.VarList)
            {
                if (string.Equals (v.VarName, varName, StringComparison.OrdinalIgnoreCase))
                {
                    target = v;
                    targetGroup = g;
                    break;
                }
            }

            if (target is not null) break;
        }

        if (target is null || targetGroup is null)
        {
            error = "变量不存在: " + varName;
            return false;
        }

        if (!CanWrite (targetGroup.Start, target, out var reason))
        {
            error = reason;
            return false;
        }

        var parsed = ParseGroupStart (targetGroup.Start ?? "");
        if (parsed is null)
        {
            error = "组起始地址无法解析";
            return false;
        }

        if (!TryParseVarAddress (target, out var abs, out var bit))
        {
            error = "变量地址无法解析: " + target.Start;
            return false;
        }

        // 位地址写不进去:Modbus 没有"写某一个位"的功能码,
        // 要读-改-写整个寄存器,还得防止并发覆盖。宁可明确拒绝,别让人以为写成功了。
        if (bit >= 0)
        {
            error = "位地址(" + target.Start + ")需要读改写,当前不支持单独写某一位";
            return false;
        }

        var baseOffset = parsed.Value.Offset;
        var rel = abs - baseOffset;

        if (rel < 0)
        {
            error = "变量地址在组起点之前,无法写入";
            return false;
        }

        try
        {
            var type = (target.VarType ?? "").Trim ().ToLowerInvariant ();

            // 读的时候是  工程值 = 原始值 * Scale + Offset(Scale==0 当 1),
            // 下发前必须做逆运算,否则带 Scale/Offset 的点写进去的是工程值,
            // 设备按原始值解释 —— 读回来会是另一个数(现场表现为"设了 10 实际 100")。
            var targetScale = target.Scale == 0f ? 1f : target.Scale;
            var rawValue = (value - target.Offset) / targetScale;

            if (double.IsNaN (rawValue) || double.IsInfinity (rawValue))
            {
                error = "换算后的值非法(请检查 Scale/Offset)";
                return false;
            }

            // 写入必须发【绝对地址】= 组基准 + 组内相对偏移。
            // 读取时 rel 只是"这块连续数据里的下标",写入时少了基准就会写到别的区 ——
            // 实测漏掉这一步会把设定值写到实时值寄存器上,现场属于严重事故。
            var writeAddress = (ushort)abs;

            if (type is "bool" or "boolean")
            {
                // Bool 点落在哪个区由组起始地址的功能码决定:
                // 0xxxx(线圈)才是 WriteCoil;3xxxx/4xxxx 里的是"寄存器里的 1 位",
                // 用 WriteCoil 会写到同号线圈上 —— 完全是另一个点。
                if (parsed.Value.Fn == ModbusFunction.ReadCoils)
                {
                    // 线圈只有 true/false 两态,**不能拿逆换算后的 rawValue 判**:
                    // 组态给 Bool 配了 Offset=1 时,写 0 会算出 raw=-1,-1 != 0 于是线圈置位 ——
                    // 开关语义彻底反过来。Bool 的输入本来就是 0/1 工程值,直接用。
                    _client.WriteCoil (writeAddress, Math.Abs (value) > 1e-9 && !double.IsNaN (value));
                    LastError = null;
                    return true;
                }

                // 寄存器区的 Bool **必须**带位号(4xxxx.NN)。
                // 早先这里直接 WriteRegister(0 或 1),等于把整字写死:
                // 一个寄存器常按位打包 16 个状态/命令,写一个 Bool 会把其余 15 位清零,
                // 现场表现是"改了一个开关,旁边十几个点全掉了" —— 而且读回来一切正常,
                // 很难查到是这里。
                // Modbus 也没有"写某一位"的功能码,只能读-改-写,还得防并发覆盖;
                // 无位号时无从知道该动哪一位,所以明确拒绝。
                // 提示不能再写"加位号":加了位号的点在 CanWrite 里同样被拒(见上面的位地址分支),
                // 照着旧提示改配置只会让这个点从可写列表里消失,现场以为"功能坏了"。
                error = "Bool 变量 " + target.Start + " 落在寄存器区(4xxxx),本版本不支持寄存器位写入:"
                    + "Modbus 没有写单个位的功能码,写整字会把同寄存器其余 15 位清零;"
                    + "该点请改用 0xxxx 线圈地址";

                return false;
            }

            // 写入前用与读取相反的字节序做编码,保证读回来值不变
            var regs = ModbusValueCodec.Encode (rawValue, target, _device.WordOrder);

            if (regs is null)
            {
                error = "值无法编码为 " + target.VarType;
                return false;
            }

            if (regs.Length == 1)
            {
                _client.WriteRegister (writeAddress, regs[0]);
            }
            else
            {
                // Float / Int32 占 2 个寄存器,必须用功能码 16
                _client.WriteRegisters (writeAddress, regs);
            }

            LastError = null;
            return true;
        }
        catch (ModbusException ex)
        {
            error = ex.Message;
            LastError = ex.Message;
            return false;
        }
        catch (Exception ex)
        {
            error = "写入失败: " + ex.Message;
            LastError = error;
            return false;
        }
    }

    public void Disconnect () { _client.Disconnect (); }

    public void Dispose () { _client.Dispose (); }
}

