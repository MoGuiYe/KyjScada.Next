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
        return now - _lastWrite >= Interval;
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

        try
        {
            var written = _repo.WritePoints(points);
            _lastWrite = sampleTime;
            TotalWritten += written;
            LastError = null;
            return written;
        }
        catch (Exception ex)
        {
            // 归档失败不能拖垮监控
            LastError = ex.Message;
            return 0;
        }
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
