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

    /// <summary>按文件名加载组态(带缓存)。</summary>
    public ProjectConfig? Load(string fileName)
    {
        var path = Path.Combine(_settingsDir, fileName);
        if (!File.Exists(path)) return null;

        var stamp = File.GetLastWriteTimeUtc(path);

        lock (_cacheLock)
        {
            if (_cache.TryGetValue(fileName, out var hit) && hit.Stamp == stamp)
            {
                return hit.Config;
            }
        }

        var bytes = File.ReadAllBytes(path);
        var text = DecodeUtf8WithBom(bytes);

        var list = JsonSerializer.Deserialize<List<ProjectConfig>>(text, JsonOptions);
        var project = list?.FirstOrDefault();
        if (project == null) return null;

        lock (_cacheLock) _cache[fileName] = (project, stamp);

        return project;
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
