using System.Net.Sockets;

namespace KyjScada.Infrastructure.Modbus;

/// <summary>
/// Modbus "RTU over TCP" 传输:帧格式与串口 RTU **完全一样**(从站号+PDU+CRC),
/// 只是把字节放到一个裸 TCP 流里,没有 MBAP 头。
///
/// 为什么不能直接用 Modbus TCP 客户端:
/// RTU over TCP 没有 7 字节 MBAP 头,而 Modbus TCP 必须先发 MBAP;
/// 用 MBAP 去问 RTU 网关,结果只能是超时或被判为坏帧。
///
/// 这类设备在工控现场很常见(串口服务器 / 串口转以太网模块)。
/// </summary>
public sealed class ModbusRtuOverTcpTransport : IModbusTransport
{
    private readonly string _host;
    private readonly int _port;
    private readonly byte _unitId;

    private TcpClient? _client;
    private NetworkStream? _stream;

    private readonly object _ioLock = new();

    public ModbusRtuOverTcpTransport(string host, int port, byte unitId = 1)
    {
        _host = string.IsNullOrWhiteSpace(host) ? "127.0.0.1" : host;
        _port = port <= 0 ? 502 : port;
        _unitId = unitId;
    }

    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(3);

    /// <summary>
    /// 连接失败后的"冷静期":在这个窗口内不再真的去连,直接把上次的失败原因抛出去。
    ///
    /// 为什么需要它:断线时界面上的写值/点动/急停都在**界面线程**上等 I/O,
    /// 每次点击都重新走一遍 Connect,网线拔掉那种情况内核要重试 SYN(几十秒),
    /// 界面就会长时间僵住。有了冷静期,连不上时写操作立即返回"通讯已断开"。
    /// </summary>
    public TimeSpan ConnectBackoff { get; set; } = TimeSpan.FromSeconds(1);

    private DateTime _lastFailUtc = DateTime.MinValue;
    private string? _lastFailMessage;

    public bool IsConnected => _client?.Connected == true && _stream is not null;

    public void Connect()
    {
        if (IsConnected) return;

        // 冷静期内直接失败:宁可报"通讯已断开",也不能让调用方干等
        var since = DateTime.UtcNow - _lastFailUtc;

        if (_lastFailMessage is not null && since < ConnectBackoff)
        {
            throw new ModbusException(_lastFailMessage);
        }

        Disconnect();

        var client = new TcpClient
        {
            ReceiveTimeout = (int)Timeout.TotalMilliseconds,
            SendTimeout = (int)Timeout.TotalMilliseconds
        };

        try
        {
            // **不能用 client.Connect(host, port)**:它没有超时参数,目标是"线断了"
            // 而不是"端口没开"时,内核要按 SYN 重试退避等几十秒,调用方(采集线程、
            // 更糟的是界面线程)就被挂在那里。这里改成可限时的等价写法。
            var task = client.ConnectAsync(_host, _port);

            if (!task.Wait(Timeout))
            {
                // 超时后这个 task 仍会自己抛异常,必须观察掉,否则是未观察异常
                _ = task.ContinueWith(static t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);

                throw new ModbusException(
                    "连接 " + _host + ":" + _port + " 超时("
                    + (int)Timeout.TotalMilliseconds + " ms 内没连上)");
            }

            client.NoDelay = true;

            _client = client;
            _stream = client.GetStream();

            _lastFailMessage = null;
            _lastFailUtc = DateTime.MinValue;
        }
        catch (Exception ex)
        {
            try { client.Dispose(); } catch { }

            var message = ex is ModbusException ? ex.Message
                : "连接 " + _host + ":" + _port + " 失败: " + ex.Message;

            _lastFailMessage = message;
            _lastFailUtc = DateTime.UtcNow;

            throw new ModbusException(message, ex);
        }
    }

    public void Disconnect()
    {
        try { _stream?.Dispose(); } catch { }
        try { _client?.Dispose(); } catch { }

        _stream = null;
        _client = null;
    }

    public ushort[] ReadHoldingRegisters(ushort startAddress, ushort quantity)
    {
        ValidateQuantity(quantity);

        var pdu = ModbusFrame.BuildReadHoldingRegisters(startAddress, quantity);

        return ModbusFrame.ParseRegisters(Execute(pdu, ModbusFunction.ReadHoldingRegisters));
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

        return ModbusFrame.ParseRegisters(Execute(pdu, ModbusFunction.ReadInputRegisters));
    }

    public bool[] ReadCoils(ushort startAddress, ushort quantity)
    {
        if (quantity == 0 || quantity > 2000)
        {
            throw new ModbusException("读取数量应在 1~2000,实际 " + quantity);
        }

        var pdu = ModbusFrame.BuildReadCoils(startAddress, quantity);

        return ModbusFrame.ParseCoils(Execute(pdu, ModbusFunction.ReadCoils), quantity);
    }

    public void WriteCoil(ushort address, bool value)
    {
        var pdu = ModbusFrame.BuildWriteSingleCoil(address, value);

        var data = Execute(pdu, ModbusFunction.WriteSingleCoil);

        ModbusFrame.VerifyWriteEcho(
            ModbusFunction.WriteSingleCoil, data, address, value ? (ushort)0xFF00 : (ushort)0x0000);
    }

    public void WriteRegister(ushort address, ushort value)
    {
        var pdu = ModbusFrame.BuildWriteSingleRegister(address, value);

        var data = Execute(pdu, ModbusFunction.WriteSingleRegister);

        ModbusFrame.VerifyWriteEcho(ModbusFunction.WriteSingleRegister, data, address, value);
    }

    public void WriteRegisters(ushort startAddress, ushort[] values)
    {
        var pdu = ModbusFrame.BuildWriteMultipleRegisters(startAddress, values);

        var data = Execute(pdu, ModbusFunction.WriteMultipleRegisters);

        ModbusFrame.VerifyWriteMultipleEcho(
            ModbusFunction.WriteMultipleRegisters, data, startAddress, (ushort)values.Length);
    }

    private static void ValidateQuantity(ushort quantity)
    {
        if (quantity == 0 || quantity > 125)
        {
            throw new ModbusException("读取数量应在 1~125,实际 " + quantity);
        }
    }

    /// <summary>发一帧收一帧,返回 [功能码之后、CRC 之前] 的数据段(与串口实现一致)。</summary>
    private byte[] Execute(byte[] pdu, byte expectedFunction)
    {
        lock (_ioLock)
        {
            try
            {
                Connect();

                var stream = _stream ?? throw new ModbusException("TCP 连接未建立");

                var frame = ModbusRtuFrame.Wrap(_unitId, pdu);

                stream.Write(frame, 0, frame.Length);
                stream.Flush();

                // 与串口一致:给从站留一点处理时间,避免读得太急
                Thread.Sleep(10);

                // 整帧共用一个截止时间(不是每个分段各等一整个 Timeout)
                var deadline = Environment.TickCount64 + (long)Timeout.TotalMilliseconds;

                var response = ModbusRtuFrame.ReadResponse(count => ReadExactly(stream, count, deadline));

                if (!ModbusRtuFrame.VerifyCrc(response))
                {
                    throw new ModbusException("RTU-over-TCP 响应 CRC 校验失败");
                }

                if (response[0] != _unitId)
                {
                    throw new ModbusException(
                        "从站号不匹配: 期望 " + _unitId + ",实际 " + response[0]);
                }

                var function = response[1];

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

                var dataLength = response.Length - 4;

                if (dataLength < 0) throw new ModbusException("RTU 响应过短");

                var data = new byte[dataLength];

                Array.Copy(response, 2, data, 0, dataLength);

                return data;
            }
            catch (ModbusException)
            {
                // 协议层异常后 TCP 流里可能还挂着残帧,必须断开重连才能重新对齐
                Disconnect();

                throw;
            }
            catch (Exception ex)
            {
                Disconnect();
                throw new ModbusException(DescribeNetworkError(ex), ex);
            }
        }
    }

    /// <summary>
    /// 把 .NET 的英文网络异常翻译成现场看得懂的中文。
    /// 原来直接把 ex.Message 拼进去,界面会显示
    /// "TCP 通讯失败: Unable to read data from the transport connection: 由于连接方在
    /// 一段时间后没有正确答复…" —— 操作员看不懂,报障也说不清是断线还是设备不回。
    /// </summary>
    private string DescribeNetworkError(Exception ex)
    {
        if (ex is SocketException se)
        {
            return se.SocketErrorCode switch
            {
                SocketError.ConnectionRefused => "连接被拒绝(" + _host + ":" + _port + " 没有服务在监听)",
                SocketError.HostUnreachable or SocketError.NetworkUnreachable => "网络不可达(" + _host + ":" + _port + ")",
                SocketError.TimedOut => "连接超时(" + _host + ":" + _port + ")",
                SocketError.ConnectionReset or SocketError.ConnectionAborted => "连接被对方断开",
                _ => "网络错误: " + se.SocketErrorCode
            };
        }

        if (ex is TimeoutException) return "通讯超时(设备无响应)";

        if (ex is IOException)
        {
            // 读超时在 .NET 里就是 IOException("Unable to read data from the transport connection")
            return "通讯超时或对方断开:设备在 " + (int)Timeout.TotalMilliseconds + " ms 内没有回应";
        }

        return "通讯失败: " + ex.Message;
    }

    /// <summary>TCP 会分段返回,必须循环读满;整帧共用一个截止时间,不能每段各等一整个超时。</summary>
    private byte[] ReadExactly(NetworkStream stream, int count, long deadlineTick)
    {
        var buffer = new byte[count];
        var total = 0;

        while (total < count)
        {
            var remaining = deadlineTick - Environment.TickCount64;

            if (remaining <= 0)
            {
                throw new ModbusException(
                    "等待响应超时(" + (int)Timeout.TotalMilliseconds + " ms 内只收到 " + total + "/" + count + " 字节)");
            }

            stream.ReadTimeout = (int)Math.Max(1, Math.Min(remaining, int.MaxValue));

            var read = stream.Read(buffer, total, count - total);

            if (read <= 0) throw new ModbusException("连接被对方关闭");

            total += read;
        }

        return buffer;
    }

    public void Dispose()
    {
        Disconnect();
    }
}