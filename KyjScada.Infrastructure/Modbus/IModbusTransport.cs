namespace KyjScada.Infrastructure.Modbus;

/// <summary>
/// Modbus 传输层接口。**同一套寄存器解析逻辑,可以跑在 TCP 或串口 RTU 上**。
///
/// 分成传输层是为了不让"帧格式差异"污染采集逻辑:
/// 组划分、变量解码、字节序处理都和走 TCP 还是串口无关。
/// </summary>
public interface IModbusTransport : IDisposable
{
    bool IsConnected { get; }

    TimeSpan Timeout { get; set; }

    void Connect();

    void Disconnect();

    ushort[] ReadHoldingRegisters(ushort startAddress, ushort quantity);

    ushort[] ReadInputRegisters(ushort startAddress, ushort quantity);

    bool[] ReadCoils(ushort startAddress, ushort quantity);

    /// <summary>写单个线圈(功能码 05)。</summary>
    void WriteCoil(ushort address, bool value);

    /// <summary>写单个保持寄存器(功能码 06)。</summary>
    void WriteRegister(ushort address, ushort value);

    /// <summary>写多个保持寄存器(功能码 16)。</summary>
    void WriteRegisters(ushort startAddress, ushort[] values);
}