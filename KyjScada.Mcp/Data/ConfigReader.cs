using System.Text.Json;

namespace KyjScada.Mcp.Data;

/// <summary>
/// 从 settings/*.json 读取组态。
/// 实测要点(见 07 号报告 §2.5):
///  - 文件是单元素数组 [ {...} ]
///  - ModbusTCP.json 无 BOM,另两份有 UTF-8 BOM → 统一用 UTF-8-sig 解码容错
///  - 三份文件并不同步(TCP 129 变量 / RTU 139 变量)
/// </summary>
public sealed class ConfigReader
{
    private readonly string _settingsDir;

    // 缓存里同时存文件时间戳:组态可能在程序运行期间被界面/手工改掉,
    // 只按文件名缓存会让 MCP 永远返回旧组态(改完配置再问 AI,答案还是改之前的)。
    private readonly Dictionary<string, (ProjectConfig Config, DateTime Stamp)> _cache = new();

    // MCP 是 Kestrel 上的多请求并发,普通 Dictionary 并发读写在极少数情况下会死循环/丢条目。
    private readonly object _cacheLock = new();

    /// <summary>最近一次加载失败的原因(供工具回给调用方);成功时为 null。</summary>
    /// 读写都过 _cacheLock:同一个 ConfigReader 会被多个 MCP 请求并发使用,
    /// 而 Load 的失败分支是在锁外写的,工具侧又会同时在读。
    public string? LastError
    {
        get
        {
            lock (_cacheLock) return _lastError;
        }

        private set
        {
            lock (_cacheLock) _lastError = value;
        }
    }

    private string? _lastError;

    public ConfigReader(string settingsDir)
    {
        _settingsDir = settingsDir;
    }

    /// <summary>列出 settings 目录下可用的组态文件。</summary>
    public IReadOnlyList<string> ListConfigFiles()
    {
        if (!Directory.Exists(_settingsDir)) return Array.Empty<string>();

        return Directory.GetFiles(_settingsDir, "*.json")
            .Select(Path.GetFileName)
            .Where(n => n != null)
            .Select(n => n!)
            .OrderBy(n => n)
            .ToList();
    }

    /// <summary>
    /// 按文件名加载组态(带缓存),**把失败原因直接返回给调用方**。
    ///
    /// 为什么不用实例字段传错误:MCP 是多请求并发的,LastError 属于实例,
    /// 请求 A 正在读、请求 B 成功加载后把它清空,A 拿到的就是 null ——
    /// “组态坏了”于是被回成“没有组态”,AI 转头去查别的东西。
    /// 失败原因跟着这一次调用走,才不会串味。
    /// </summary>
    public bool TryLoad(string fileName, out ProjectConfig? project, out string? error)
    {
        project = null;
        error = null;

        var path = Path.Combine(_settingsDir, fileName);

        if (!File.Exists(path))
        {
            // 文件不存在也要给原因,不能留着上一次调用的错误
            error = "组态文件 " + fileName + " 不存在";

            return false;
        }

        var stamp = File.GetLastWriteTimeUtc(path);

        lock (_cacheLock)
        {
            if (_cache.TryGetValue(fileName, out var hit) && hit.Stamp == stamp)
            {
                project = hit.Config;

                return true;
            }
        }

        LastError = null;

        // 读/解析都可能失败,而且都很平常:
        //  · 主程序正在保存组态 → 文件被独占,读会抛 IOException
        //  · 保存到一半 → JSON 截断,Deserialize 抛 JsonException
        // 以前这两种异常直接冒到 MCP 协议层,客户端只看到 "An error occurred",
        // 既不知道是哪一个文件,也不知道是"正在写"还是"文件坏了"。
        byte[] bytes;

        try
        {
            bytes = File.ReadAllBytes(path);
        }
        catch (Exception ex)
        {
            LastError = error = "读取组态文件 " + fileName + " 失败:" + ex.Message;

            return false;
        }

        var text = DecodeUtf8WithBom(bytes);

        List<ProjectConfig>? list;

        try
        {
            list = JsonSerializer.Deserialize<List<ProjectConfig>>(text, JsonOptions);
        }
        catch (Exception ex)
        {
            LastError = error = "组态文件 " + fileName + " 不是合法的 JSON(可能正在被改写):" + ex.Message;

            return false;
        }

        var loaded = list?.FirstOrDefault();

        if (loaded == null)
        {
            LastError = error = "组态文件 " + fileName + " 里没有项目配置(空数组?)";

            return false;
        }

        lock (_cacheLock) _cache[fileName] = (loaded, stamp);

        project = loaded;

        return true;
    }

    /// <summary>只要结果、不要原因的调用方用这个。</summary>
    public ProjectConfig? Load(string fileName)
    {
        return TryLoad(fileName, out var project, out _) ? project : null;
    }

    /// <summary>列出组态里的全部变量(扁平化)。</summary>
    public IEnumerable<VariableConfig> EnumerateVariables(ProjectConfig project)
    {
        foreach (var dev in project.ModbusTCPList)
        {
            foreach (var g in dev.GroupList)
            {
                foreach (var v in g.VarList)
                {
                    yield return v;
                }
            }
        }

        foreach (var dev in project.ModbusRTUList)
        {
            foreach (var g in dev.GroupList)
            {
                foreach (var v in g.VarList)
                {
                    yield return v;
                }
            }
        }
    }

    /// <summary>
    /// UTF-8-sig 解码:自动剥离 BOM。
    /// 实测 ModbusTCP.json 无 BOM、另两份有 BOM,必须容错。
    /// </summary>
    private static string DecodeUtf8WithBom(byte[] bytes)
    {
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
        {
            return new System.Text.UTF8Encoding(false).GetString(bytes, 3, bytes.Length - 3);
        }

        return System.Text.Encoding.UTF8.GetString(bytes);
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };
}
