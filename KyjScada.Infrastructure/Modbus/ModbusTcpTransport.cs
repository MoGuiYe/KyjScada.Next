namespace KyjScada.Infrastructure.Modbus;

/// <summary>
/// Modbus TCP 传输适配器。把既有的 ModbusTcpClient 包装成 IModbusTransport,
/// 这样采集层不用关心走的是 TCP 还是串口。
/// </summary>
public sealed class ModbusTcpTransport : IModbusTransport
{
    private readonly ModbusTcpClient _client;

    public ModbusTcpTransport(string host, int port, byte unitId = 1)
    {
        _client = new ModbusTcpClient(host, port, unitId);

        Timeout = _client.Timeout;
    }

    public TimeSpan Timeout
    {
        get => _client.Timeout;
        set => _client.Timeout = value;
    }

    public bool IsConnected => _client.IsConnected;

    public void Connect() => _client.Connect();

    public void Disconnect() => _client.Disconnect();

    public ushort[] ReadHoldingRegisters(ushort startAddress, ushort quantity)
        => _client.ReadHoldingRegisters(startAddress, quantity);

    public ushort[] ReadInputRegisters(ushort startAddress, ushort quantity)
        => _client.ReadInputRegisters(startAddress, quantity);

    public bool[] ReadCoils(ushort startAddress, ushort quantity)
        => _client.ReadCoils(startAddress, quantity);

    public void WriteCoil(ushort address, bool value) => _client.WriteBool(address, value);

    public void WriteRegister(ushort address, ushort value) => _client.WriteRegister(address, value);

    public void WriteRegisters(ushort startAddress, ushort[] values) => _client.WriteRegisters(startAddress, values);

    public void Dispose() => _client.Dispose();
}