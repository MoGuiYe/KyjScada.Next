
namespace KyjScada.Contracts;

/// <summary>
/// 采集服务统一接口。界面只依赖这个,不关心底层是 Modbus 还是别的协议。
///
/// 为什么要有这层:老系统在界面里直接 new 具体设备类,加一种协议要改界面。
/// 有了接口之后,界面代码一行不用动。
/// </summary>
public interface IAcquisitionService : IDisposable
{
    /// <summary>设备名(界面显示用)。</summary>
    string DeviceName { get; }

    /// <summary>当前是否已连上。</summary>
    bool IsConnected { get; }

    /// <summary>最近一次失败原因(连接不上/读取失败)。成功一次就清空。</summary>
    string? LastError { get; }

    /// <summary>采集一轮并入总线。返回成功采集的变量数。</summary>
    int CollectOnce(IValueBus bus, DateTime sampleTime);

    /// <summary>主动断开(重连前先调)。</summary>
    void Disconnect();
}

