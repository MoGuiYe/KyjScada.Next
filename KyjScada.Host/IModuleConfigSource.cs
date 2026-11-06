using KyjScada.Contracts;

namespace KyjScada.Host;

/// <summary>
/// 模块配置源。每个模块独立一个配置分区,互不干扰。
/// 独立调试的关键:单模块调试时只需给它一个最小 JSON 即可。
/// </summary>
public interface IModuleConfigSource
{
    IModuleConfig ForModule (string moduleId);
}
