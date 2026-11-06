using KyjScada.Contracts;

namespace KyjScada.Host;

/// <summary>基于字典的配置实现。适合独立调试(代码里直接塞几行配置)。</summary>
public sealed class DictConfig : IModuleConfig
{
    private readonly Dictionary<string, string> _values;

    public DictConfig (Dictionary<string, string> values)
    {
        _values = values;
    }

    public string GetString (string key, string defaultValue = "")
        => _values.TryGetValue (key, out var v) ? v : defaultValue;

    public int GetInt (string key, int defaultValue = 0)
        => _values.TryGetValue (key, out var v) && int.TryParse (v, out var p) ? p : defaultValue;

    public bool GetBool (string key, bool defaultValue = false)
        => _values.TryGetValue (key, out var v) && bool.TryParse (v, out var p) ? p : defaultValue;

    public bool Has (string key) => _values.ContainsKey (key);
}
