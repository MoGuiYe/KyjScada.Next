namespace KyjScada.Contracts;

public interface IModuleLogger
{
    void Info (string message);

    void Warn (string message);

    void LogError (string message, Exception? ex = null);
}
