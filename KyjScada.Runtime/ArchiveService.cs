using KyjScada.Contracts;
using KyjScada.Domain.Config;

namespace KyjScada.Runtime;

/// <summary>
/// 归档服务:定时把实时值总线的快照写入历史长表。
///
/// 设计要点:
///  1. **只归档 ArchiveEnable=true 的变量**。现场 129 个变量里只有 35 个需要存历史,
///     全存会让库膨胀 3.7 倍。
///  2. **只存有效值**。Value 为 null 或质量差的跳过 —— 不写 NULL 占位。
///  3. **与采集周期解耦**。采集 1 秒一次,归档 10 秒一次,由 ShouldWrite 判断。
///  4. **写失败不中断**。一次数据库失败只记 LastError,下个周期继续。
///
/// 容量实测:35 变量 × 10 秒间隔 ≈ 6.5 GB/年。
/// </summary>
public sealed class ArchiveService
{
    private readonly IHistoryRepository _repo;

    /// <summary>需要归档的变量名(仅 ArchiveEnable=true)。</summary>
    private readonly HashSet<string> _archiveVars = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>变量名 → 单位(建字典表用)。</summary>
    private readonly Dictionary<string, string> _units = new(StringComparer.OrdinalIgnoreCase);

    private DateTime _lastWrite = DateTime.MinValue;

    /// <summary>
    /// 上次**尝试**写库的时刻(成功失败都算)。节拍以它为准 ——
    /// 若只在成功时推进,写库持续失败就会每个采集周期(1 秒)重试一次,
    /// 10 秒归档退化成 1 秒,错误日志与 LastError 被刷满。
    /// </summary>
    private DateTime _lastAttempt = DateTime.MinValue;

    /// <summary>
    /// 节拍用的单调时钟(毫秒),与 _lastAttempt 同步推进。
    /// 系统时间被往回校时之后 "now - _lastAttempt" 是负数,按它判节拍会一直不写;
    /// 这时改用单调时钟兜底,归档不会停到系统时间追上为止。
    /// </summary>
    private long _lastTick;
    private bool _hasTick;

    /// <summary>
    /// 上一批写库失败、还没落地的新点。失败时不清掉,下个节拍连同新点一起重写,
    /// 否则"失败那一刻的数据"永久空洞 —— 事后看曲线是一段没有采样的空档,
    /// 而不是一个可以补回来的缺口。
    /// </summary>
    private readonly List<HistoryPoint> _pending = new ();

    /// <summary>待补写队列上限。写库长期失败时不能无限吃内存。</summary>
    private const int MaxPendingPoints = 20000;

    /// <summary>累计写入行数(诊断用)。</summary>
    public long TotalWritten { get; private set; }

    /// <summary>最近一次错误。null = 无错误。</summary>
    public string? LastError { get; private set; }

    /// <summary>最近一次成功写库时刻。</summary>
    public DateTime LastWrite => _lastWrite;

    /// <summary>归档间隔。</summary>
    public TimeSpan Interval { get; }

    /// <summary>需要归档的变量数。</summary>
    public int ArchiveVarCount => _archiveVars.Count;

    /// <summary>最近一次清理删除的行数(诊断用)。</summary>
    public int LastPurged { get; private set; }

    public ArchiveService(IHistoryRepository repo, TimeSpan? interval = null)
    {
        _repo = repo;
        Interval = interval ?? TimeSpan.FromSeconds(10);
    }

    /// <summary>从设备配置编译归档清单。只收 ArchiveEnable=true 的变量。</summary>
    public void BuildFrom(DeviceConfig device)
    {
        _archiveVars.Clear();
        _units.Clear();

        foreach (var group in device.GroupList)
        {
            if (group.VarList is null) continue;

            foreach (var v in group.VarList)
            {
                if (string.IsNullOrEmpty(v.VarName)) continue;
                if (!v.ArchiveEnable) continue;

                _archiveVars.Add(v.VarName);
                _units[v.VarName] = v.Unit ?? "";
            }
        }
    }

    /// <summary>是否到了该写库的时刻。调用方每个采集周期调一次。</summary>
    public bool ShouldWrite(DateTime now)
    {
        // 从未尝试过:立刻可写
        if (_lastAttempt == DateTime.MinValue) return true;

        if (now < _lastAttempt)
        {
            // 时钟被往回拨(或调用方给的时刻比上次还早):按真实流逝的时间判,不看墙上时钟
            return !_hasTick
                || Environment.TickCount64 - _lastTick >= (long)Interval.TotalMilliseconds;
        }

        return now - _lastAttempt >= Interval;
    }

    /// <summary>
    /// 把快照里需要归档的变量写入历史库。
    /// 返回实际写入行数;无可写数据返回 0。
    /// </summary>
    public int Archive(ValueSnapshot snapshot, DeviceConfig device)
    {
        if (snapshot is null || _archiveVars.Count == 0) return 0;

        var sampleTime = snapshot.Timestamp;
        var points = new List<HistoryPoint>(_archiveVars.Count);

        foreach (var varName in _archiveVars)
        {
            var tv = snapshot[varName];

            // 只存有效值
            if (tv is null) continue;
            if (tv.Quality == TagQuality.Bad || tv.Quality == TagQuality.NotCollected) continue;

            var value = tv.AsDouble();
            if (value is null || double.IsNaN(value.Value) || double.IsInfinity(value.Value)) continue;

            _units.TryGetValue(varName, out var unit);

            points.Add(new HistoryPoint(
                sampleTime,
                varName,
                device.DeviceName ?? "",
                unit ?? "",
                value.Value,
                tv.Quality == TagQuality.Good ? 0 : 1));
        }

        if (points.Count == 0) return 0;

        // 先补上一批没写成的点,再追加本批
        var batch = new List<HistoryPoint>(_pending.Count + points.Count);
        batch.AddRange(_pending);
        batch.AddRange(points);

        // 节拍无论成败都推进:失败不推进的话 10 秒归档会退化成每个采集周期(1 秒)重试,
        // 库写不进去时每秒撞一次错误、LastError 与日志被刷满,现场反而看不到真正原因。
        AdvancePace(sampleTime);

        try
        {
            var written = _repo.WritePoints(batch);
            _pending.Clear();
            _lastWrite = sampleTime;
            TotalWritten += written;
            LastError = null;
            return written;
        }
        catch (Exception ex)
        {
            // 归档失败不能拖垮监控
            LastError = ex.Message;

            _pending.AddRange(batch);

            if (_pending.Count > MaxPendingPoints)
            {
                // 只保留最新的:老点补写价值低,而且内存必须封顶
                _pending.RemoveRange(0, _pending.Count - MaxPendingPoints);
            }

            return 0;
        }
    }

    private void AdvancePace(DateTime sampleTime)
    {
        // 采集时刻可能比上次的还早(补采/设备时间回退),节拍仍取较晚的那个,
        // 否则一次时间回退会把接下来一整段的归档都放行。
        if (sampleTime > _lastAttempt) _lastAttempt = sampleTime;

        _lastTick = Environment.TickCount64;
        _hasTick = true;
    }

    /// <summary>
    /// 删除早于保留期的历史数据。
    /// retentionDays <= 0 表示不清理(保留全部)。
    /// </summary>
    public int Purge(DateTime now, int retentionDays)
    {
        if (retentionDays <= 0) return 0;

        try
        {
            var cutoff = now.AddDays(-retentionDays);
            LastPurged = _repo.DeleteBefore(cutoff);
            return LastPurged;
        }
        catch (Exception ex)
        {
            LastError = "清理失败: " + ex.Message;
            return 0;
        }
    }
}
