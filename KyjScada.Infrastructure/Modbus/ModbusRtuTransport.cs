using System.IO.Ports;

namespace KyjScada.Infrastructure.Modbus;

/// <summary>
/// Modbus RTU 串口传输。
///
/// 与 TCP 的差别只在帧:串口发的是 [从站号][PDU][CRC16 低][CRC16 高],
/// 没有 MBAP 头;而且**没法靠长度字段知道要读多少字节**,
/// 必须先读功能码再按功能码推算(见 ModbusRtuFrame.ResponseLength)。
///
/// 读串口最容易踩的坑:SerialPort 的 Read 可能只返回一部分字节,
/// 必须循环读到够数,否则会拿到半截帧并一直报 CRC 错。
/// </summary>
public sealed class ModbusRtuTransport : IModbusTransport
{
    private readonly string _portName;
    private readonly int _baudRate;
    private readonly Parity _parity;
    private readonly int _dataBits;
    private readonly StopBits _stopBits;
    private readonly byte _unitId;

    private SerialPort? _port;
    private readonly object _ioLock = new();

    /// <summary>已释放。Dispose 之后不允许再用:否则会悄悄重开串口,把端口重新占住。</summary>
    private volatile bool _disposed;

    private void EnsureNotDisposed()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(ModbusRtuTransport));
    }

    public ModbusRtuTransport(
        string portName, int baudRate, int parity, int dataBits, int stopBits, byte unitId = 1)
    {
        _portName = portName;
        _baudRate = baudRate > 0 ? baudRate : 9600;
        _parity = MapParity(parity);
        _dataBits = dataBits > 0 ? dataBits : 8;
        _stopBits = MapStopBits(stopBits);
        _unitId = unitId;
    }

    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>
    /// 打开串口失败后的"冷静期":窗口内不再真的去开,直接把上次的失败原因抛出去。
    /// 断线时界面上的写值/点动/急停都在界面线程上等 I/O,
    /// 每次点击都重新 open 一次串口(拔线时可能要等到驱动超时),界面就会僵住。
    /// </summary>
    public TimeSpan ConnectBackoff { get; set; } = TimeSpan.FromSeconds(1);

    private DateTime _lastFailUtc = DateTime.MinValue;
    private string? _lastFailMessage;

    public bool IsConnected => _port?.IsOpen == true;

    /// <summary>老配置里 Parity 是数字:0=None 1=Odd 2=Even 3=Mark 4=Space。</summary>
    private static Parity MapParity(int value)
    {
        return value switch
        {
            0 => Parity.None,
            1 => Parity.Odd,
            2 => Parity.Even,
            3 => Parity.Mark,
            4 => Parity.Space,
            _ => Parity.None
        };
    }

    /// <summary>StopBits:0=None 1=One 2=Two 3=OnePointFive。</summary>
    private static StopBits MapStopBits(int value)
    {
        return value switch
        {
            0 => StopBits.None,
            1 => StopBits.One,
            2 => StopBits.Two,
            3 => StopBits.OnePointFive,
            _ => StopBits.One
        };
    }

    public void Connect()
    {
        EnsureNotDisposed();
        if (IsConnected) return;

        var since = DateTime.UtcNow - _lastFailUtc;

        if (_lastFailMessage is not null && since < ConnectBackoff)
        {
            throw new ModbusException(_lastFailMessage);
        }

        Disconnect();

        var port = new SerialPort(_portName, _baudRate, _parity, _dataBits, _stopBits)
        {
            ReadTimeout = (int)Timeout.TotalMilliseconds,
            WriteTimeout = (int)Timeout.TotalMilliseconds
        };

        try
        {
            port.Open();

            _port = port;

            _lastFailMessage = null;
            _lastFailUtc = DateTime.MinValue;
        }
        catch (Exception ex)
        {
            try { port.Dispose(); } catch { }

            // 串口不存在/被占用抛的是 IOException、UnauthorizedAccessException,
            // 采集器只认 ModbusException;不在这里统一,异常会一路冒到界面变成"异常"。
            var message = "打开串口 " + _portName + " 失败: " + ex.Message;

            _lastFailMessage = message;
            _lastFailUtc = DateTime.UtcNow;

            throw new ModbusException(message, ex);
        }
    }

    public void Disconnect()
    {
        // 与在途帧互斥:直接关串口会把正在读/写的那一帧腰斩成半帧,
        // 之后每一帧都从错误的边界开始解析(现场表现:一断就连续报 CRC 错)。
        // Monitor 可重入,Execute 的失败分支在本线程调它不会自锁。
        lock (_ioLock)
        {
            try
            {
                if (_port is not null)
                {
                    if (_port.IsOpen) _port.Close();
                    _port.Dispose();
                }
            }
            catch
            {
                // 关串口失败不影响后续重连
            }
            finally
            {
                _port = null;
            }
        }
    }

    public ushort[] ReadHoldingRegisters(ushort startAddress, ushort quantity)
    {
        ValidateQuantity(quantity);

        var pdu = ModbusFrame.BuildReadHoldingRegisters(startAddress, quantity);

        var data = Execute(pdu, ModbusFunction.ReadHoldingRegisters);

        return ModbusFrame.ParseRegisters(data);
    }

    public ushort[] ReadInputRegisters(ushort startAddress, ushort quantity)
    {
        ValidateQuantity(quantity);

        var pdu = new byte[5];
        pdu[0] = ModbusFunction.ReadInputRegisters;
        pdu[1] = (byte)(startAddress >> 8);
        pdu[2] = (byte)(startAddress & 0xFF);
        pdu[3] = (byte)(quantity >> 8);
        pdu[4] = (byte)(quantity & 0xFF);

        var data = Execute(pdu, ModbusFunction.ReadInputRegisters);

        return ModbusFrame.ParseRegisters(data);
    }

    public bool[] ReadCoils(ushort startAddress, ushort quantity)
    {
        if (quantity == 0 || quantity > 2000)
        {
            throw new ModbusException("读取数量应在 1~2000,实际 " + quantity);
        }

        var pdu = ModbusFrame.BuildReadCoils(startAddress, quantity);

        var data = Execute(pdu, ModbusFunction.ReadCoils);

        return ModbusFrame.ParseCoils(data, quantity);
    }

    private static void ValidateQuantity(ushort quantity)
    {
        if (quantity == 0 || quantity > 125)
        {
            throw new ModbusException("读取数量应在 1~125,实际 " + quantity);
        }
    }

    /// <summary>写单个线圈(功能码 05)。回显不符视为失败。</summary>
    public void WriteCoil(ushort address, bool value)
    {
        var pdu = ModbusFrame.BuildWriteSingleCoil(address, value);

        var data = Execute(pdu, ModbusFunction.WriteSingleCoil);

        ModbusFrame.VerifyWriteEcho(
            ModbusFunction.WriteSingleCoil, data, address, value ? (ushort)0xFF00 : (ushort)0x0000);
    }

    /// <summary>写单个保持寄存器(功能码 06)。回显不符视为失败。</summary>
    public void WriteRegister(ushort address, ushort value)
    {
        var pdu = ModbusFrame.BuildWriteSingleRegister(address, value);

        var data = Execute(pdu, ModbusFunction.WriteSingleRegister);

        ModbusFrame.VerifyWriteEcho(ModbusFunction.WriteSingleRegister, data, address, value);
    }

    /// <summary>写多个保持寄存器(功能码 16)。回显只带起始地址和数量。</summary>
    public void WriteRegisters(ushort startAddress, ushort[] values)
    {
        var pdu = ModbusFrame.BuildWriteMultipleRegisters(startAddress, values);

        var data = Execute(pdu, ModbusFunction.WriteMultipleRegisters);

        ModbusFrame.VerifyWriteMultipleEcho(
            ModbusFunction.WriteMultipleRegisters, data, startAddress, (ushort)values.Length);
    }

    /// <summary>
    /// 发一帧收一帧。返回"从功能码之后到 CRC 之前"的字节,
    /// 与 TCP 侧 ParseResponse 返回的 Data 结构一致,这样解析器可以共用。
    /// </summary>
    private byte[] Execute(byte[] pdu, byte expectedFunction)
    {
        lock (_ioLock)
        {
            try
            {
                Connect();

                var port = _port ?? throw new ModbusException("串口未打开");

                var frame = ModbusRtuFrame.Wrap(_unitId, pdu);

                port.DiscardInBuffer();
                port.Write(frame, 0, frame.Length);

                // 写完不要立刻阻塞读:RS485 收发换向和从站处理都需要几毫秒,
                // 老项目同样是写完 sleep(20ms) 再收。
                // 少了这段,真实 485 设备上会出现"偶发超时",虚拟串口则看不出来。
                Thread.Sleep(15);

                // 整帧共用一个截止时间:串口按字节超时,一段一段读时若每段各等一个
                // ReadTimeout,125 个寄存器最坏能等几分钟 —— 界面看起来就是卡死。
                var deadline = Environment.TickCount64 + (long)Timeout.TotalMilliseconds;

                var response = ReadResponse(port, expectedFunction, deadline);

                if (!ModbusRtuFrame.VerifyCrc(response))
                {
                    throw new ModbusException("RTU 响应 CRC 校验失败");
                }

                if (response[0] != _unitId)
                {
                    throw new ModbusException(
                        "从站号不匹配: 期望 " + _unitId + ", 实际 " + response[0]);
                }

                var function = response[1];

                // 异常响应:功能码最高位置 1
                if ((function & 0x80) != 0)
                {
                    var errorCode = response.Length > 2 ? response[2] : (byte)0;

                    throw new ModbusException(
                        "设备返回异常: 功能码 0x" + (function & 0x7F).ToString("X2")
                        + ",异常码 " + errorCode
                        + "(" + ModbusFrame.DescribeExceptionCode(errorCode) + ")");
                }

                if (function != expectedFunction)
                {
                    throw new ModbusException(
                        "功能码不匹配: 期望 0x" + expectedFunction.ToString("X2")
                        + ",实际 0x" + function.ToString("X2"));
                }

                // 去掉从站号(1)与 CRC(2),保留 [功能码][数据...] 之后的部分
                var dataLength = response.Length - 3 - 1;

                if (dataLength < 0) throw new ModbusException("RTU 响应过短");

                var data = new byte[dataLength];

                Array.Copy(response, 2, data, 0, dataLength);

                return data;
            }
            catch (ModbusException)
            {
                throw;
            }
            catch (Exception ex)
            {
                Disconnect();

                // 读超时在 .NET 里是 IOException/TimeoutException,原文是英文,
                // 界面直接显示会让人看不懂是"断线"还是"设备不回"。
                var message = ex is TimeoutException or IOException
                    ? "串口超时:设备在 " + (int)Timeout.TotalMilliseconds + " ms 内没有回应(" + _portName + ")"
                    : "串口通讯失败: " + ex.Message;

                throw new ModbusException(message, ex);
            }
        }
    }

    /// <summary>
    /// 按功能码推算长度,分段读到够数。
    ///
    /// **必须精确跟踪已读字节数**:读类响应比写响应多一个"字节数"字段,
    /// 若续读长度统一按"总长-2"算就会多要 1 字节,结果是永远等不到 → 必定超时。
    /// 这是本轮真实踩到的 bug。
    /// </summary>
    private byte[] ReadResponse(SerialPort port, byte expectedFunction, long deadlineTick)
    {
        // 帧读取逻辑统一放在 ModbusRtuFrame:串口和 RTU-over-TCP 共用一份,
        // 差异只在"怎么读到 N 个字节"。expectedFunction 由上层校验,这里不需要。
        _ = expectedFunction;

        return ModbusRtuFrame.ReadResponse(count => ReadExact(port, count, deadlineTick, Timeout));
    }

    /// <summary>读满指定字节数。串口可能分段返回,必须循环;整帧共用一个截止时间。</summary>
    private static byte[] ReadExact(SerialPort port, int count, long deadlineTick, TimeSpan timeout)
    {
        var buffer = new byte[count];
        var total = 0;

        while (total < count)
        {
            var remaining = deadlineTick - Environment.TickCount64;

            if (remaining <= 0)
            {
                throw new ModbusException(
                    "等待响应超时(" + (int)timeout.TotalMilliseconds + " ms 内只收到 " + total + "/" + count + " 字节)");
            }

            port.ReadTimeout = (int)Math.Max(1, Math.Min(remaining, int.MaxValue));

            var read = port.Read(buffer, total, count - total);

            if (read <= 0) throw new ModbusException("串口读取超时");

            total += read;
        }

        return buffer;
    }

    public void Dispose()
    {
        _disposed = true;

        Disconnect();
    }
}