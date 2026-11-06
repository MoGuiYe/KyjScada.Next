using KyjScada.Contracts;

namespace KyjScada.Host;

/// <summary>内存配置源。单模块调试时直接塞配置。</summary>
public sealed class DictConfigSource : IModuleConfigSource
{
    private readonly Dictionary<string, Dictionary<string, string>> _sections;

    public DictConfigSource ()
    {
        _sections = new Dictionary<string, Dictionary<string, string>>();
    }

    public void Set (string moduleId, string key, string value)
    {
        if (!_sections.TryGetValue (moduleId, out var sec))
        {
            sec = new Dictionary<string, string>();
            _sections[moduleId] = sec;
        }

        sec[key] = value;
    }

    public IModuleConfig ForModule (string moduleId)
    {
        var sec = _sections.TryGetValue (moduleId, out var s) ? s : new Dictionary<string, string>();
        return new DictConfig (sec);
    }
}
