using System.Net.Sockets;

namespace KyjScada.Infrastructure.Modbus;

/// <summary>
/// Modbus TCP 客户端。
///
/// **关键:显式实现 WriteBool**。老系统 ModbusTCP 没实现这个方法,
/// 基类直接返回 CreateFailResult(),导致现场点动一直写不进去且不报错。
/// 这里把它作为一等公民实现,并有真实网络往返测试。
///
/// 并发模型:
///  - 内部用 SemaphoreSlim 保证同一时刻只有一个请求在途
///  - Modbus TCP 本身不支持流水线,并发请求会串帧
/// </summary>
public sealed class ModbusTcpClient : IDisposable
{
    private readonly string _host;
    private readonly int _port;
    private readonly byte _unitId;

    private TcpClient? _tcp;
    private NetworkStream? _stream;
    private ushort _transactionId;

    private readonly SemaphoreSlim _requestLock = new(1, 1);
    private readonly object _connectLock = new();

    /// <summary>等"在途帧"收尾的上限(断开/释放时用)。超时就直接关句柄,那是唯一能打断阻塞往返的手段。</summary>
    private static readonly TimeSpan InFlightDrainTimeout = TimeSpan.FromSeconds (2);

    private volatile bool _disposed;

    /// <summary>单次请求超时。</summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(3);

    /// <summary>
    /// 连接失败后的"冷静期":窗口内不再真的去连,直接抛上次的失败原因。
    /// 断线时界面上的写值/点动/急停都在界面线程等 I/O,没这个窗口时
    /// 每次点击都要重连一次,界面就会跟着卡。
    /// </summary>
    public TimeSpan ConnectBackoff { get; set; } = TimeSpan.FromSeconds(1);

    private DateTime _lastFailUtc = DateTime.MinValue;
    private string? _lastFailMessage;

    public ModbusTcpClient (string host, int port, byte unitId = 1)
    {
        _host = host;
        _port = port;
        _unitId = unitId;
    }

    public bool IsConnected
    {
        get
        {
            lock (_connectLock)
            {
                return _tcp is not null && _tcp.Connected;
            }
        }
    }

    /// <summary>建立连接(幂等)。</summary>
    public void Connect ()
    {
        EnsureNotDisposed ();

        lock (_connectLock)
        {
            if (_tcp is not null && _tcp.Connected) return;

            var since = DateTime.UtcNow - _lastFailUtc;

            if (_lastFailMessage is not null && since < ConnectBackoff)
            {
                throw new ModbusException (_lastFailMessage);
            }

            CloseInternal ();

            var tcp = new TcpClient ();

            try
            {
                var task = tcp.ConnectAsync (_host, _port);

                if (!task.Wait (Timeout))
                {
                    // 超时后这个 task 仍会自己抛异常,必须观察掉,否则是未观察异常
                    _ = task.ContinueWith (
                        static t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);

                    tcp.Dispose ();
                    throw new ModbusException ("连接 " + _host + ":" + _port + " 超时");
                }

                tcp.ReceiveTimeout = (int)Timeout.TotalMilliseconds;
                tcp.SendTimeout = (int)Timeout.TotalMilliseconds;
                tcp.NoDelay = true;

                _tcp = tcp;
                _stream = tcp.GetStream ();

                _lastFailMessage = null;
                _lastFailUtc = DateTime.MinValue;
            }
            catch (Exception ex)
            {
                tcp.Dispose ();

                var message = ex is ModbusException ? ex.Message
                    : "连接 " + _host + ":" + _port + " 失败: " + ex.Message;

                _lastFailMessage = message;
                _lastFailUtc = DateTime.UtcNow;

                throw new ModbusException (message, ex);
            }
        }
    }

    /// <summary>断开连接。</summary>
    public void Disconnect ()
    {
        // 已释放:只做收尾,不再碰信号量(它可能已经被 Dispose 掉了)
        if (_disposed)
        {
            lock (_connectLock) CloseInternal ();

            return;
        }

        // 不在请求线程上时:先等在途的那一帧收尾,再关流。
        // 不等的话会把别人正在读的帧腰斩成半帧 —— 对端的帧边界就此错位,
        // 之后每帧都从错误位置解析(现场表现是"一断就连续报 CRC/长度错")。
        if (!t_inRequest && _requestLock.Wait (InFlightDrainTimeout))
        {
            try
            {
                lock (_connectLock) CloseInternal ();
            }
            finally
            {
                _requestLock.Release ();
            }

            return;
        }

        // 本线程自己就在往返里(Execute 的失败分支),或是等超时了:
        // 直接关。句柄是唯一能打断阻塞往返的手段。
        lock (_connectLock) CloseInternal ();
    }

    /// <summary>
    /// 该线程当前是否正在一次请求-响应往返里。
    ///
    /// 为什么需要它:Execute 的异常分支会调 Disconnect 自我清理,而那时它就握着
    /// _requestLock —— SemaphoreSlim 不可重入,Disconnect 再去等它就会自己把自己卡死。
    /// </summary>
    [ThreadStatic] private static bool t_inRequest;

    private void CloseInternal ()
    {
        try
        {
            _stream?.Dispose ();
        }
        catch
        {
            // 关闭失败不影响后续重连
        }

        try
        {
            _tcp?.Dispose ();
        }
        catch
        {
            // 同上
        }

        _stream = null;
        _tcp = null;
    }

    /// <summary>
    /// 写单个线圈。**老系统缺失的方法**。
    /// 写失败抛异常,绝不静默返回失败。
    /// </summary>
    public void WriteBool (ushort address, bool value)
    {
        var pdu = ModbusFrame.BuildWriteSingleCoil (address, value);

        Execute (pdu, ModbusFunction.WriteSingleCoil, responseData =>
        {
            var expectedRaw = value ? ModbusFrame.CoilOn : ModbusFrame.CoilOff;

            ModbusFrame.VerifyWriteEcho (ModbusFunction.WriteSingleCoil, responseData, address, expectedRaw);
        });
    }

    /// <summary>写单个寄存器。</summary>
    public void WriteRegister (ushort address, ushort value)
    {
        var pdu = ModbusFrame.BuildWriteSingleRegister (address, value);

        Execute (pdu, ModbusFunction.WriteSingleRegister, responseData =>
        {
            ModbusFrame.VerifyWriteEcho (ModbusFunction.WriteSingleRegister, responseData, address, value);
        });
    }

    /// <summary>写多个寄存器(功能码 16)。写 Float/Int32 这类占 2 寄存器的值必须用这个。</summary>
    public void WriteRegisters (ushort startAddress, ushort[] values)
    {
        var pdu = ModbusFrame.BuildWriteMultipleRegisters (startAddress, values);

        Execute (pdu, ModbusFunction.WriteMultipleRegisters, responseData =>
        {
            ModbusFrame.VerifyWriteMultipleEcho (
                ModbusFunction.WriteMultipleRegisters, responseData, startAddress, (ushort)values.Length);
        });
    }

    /// <summary>读保持寄存器。</summary>
    public ushort[] ReadHoldingRegisters (ushort startAddress, ushort quantity)
    {
        if (quantity == 0 || quantity > 125)
        {
            throw new ModbusException ("读取数量应在 1~125,实际 " + quantity);
        }

        var pdu = ModbusFrame.BuildReadHoldingRegisters (startAddress, quantity);

        ushort[]? result = null;

        Execute (pdu, ModbusFunction.ReadHoldingRegisters, responseData =>
        {
            result = ModbusFrame.ParseRegisters (responseData);
        });

        return result ?? Array.Empty<ushort> ();
    }

    /// <summary>读输入寄存器(功能码 04)。</summary>
    public ushort[] ReadInputRegisters (ushort startAddress, ushort quantity)
    {
        ValidateQuantity (quantity);

        var pdu = new byte[5];
        pdu[0] = ModbusFunction.ReadInputRegisters;
        pdu[1] = (byte)(startAddress >> 8);
        pdu[2] = (byte)(startAddress & 0xFF);
        pdu[3] = (byte)(quantity >> 8);
        pdu[4] = (byte)(quantity & 0xFF);

        ushort[] result = null;

        Execute (pdu, ModbusFunction.ReadInputRegisters, responseData =>
        {
            result = ModbusFrame.ParseRegisters (responseData);
        });

        return result ?? Array.Empty<ushort> ();
    }

    private static void ValidateQuantity (ushort quantity)
    {
        var bad = quantity == 0 || quantity > 125;
        if (bad) throw new ModbusException ("读取数量应在 1~125,实际 " + quantity);
    }

    /// <summary>读线圈。</summary>
    public bool[] ReadCoils (ushort startAddress, ushort quantity)
    {
        if (quantity == 0 || quantity > 2000)
        {
            throw new ModbusException ("读取数量应在 1~2000,实际 " + quantity);
        }

        var pdu = ModbusFrame.BuildReadCoils (startAddress, quantity);

        bool[]? result = null;

        Execute (pdu, ModbusFunction.ReadCoils, responseData =>
        {
            result = ModbusFrame.ParseCoils (responseData, quantity);
        });

        return result ?? Array.Empty<bool> ();
    }

    /// <summary>
    /// 已释放之后再调读写,必须明确报 ObjectDisposedException。
    ///
    /// 原来只有 Dispose 自己判 _disposed:释放之后 _requestLock 已经被 Dispose 掉,
    /// 这时任何一个在途调用走到 _requestLock.Wait 都会抛 ObjectDisposedException,
    /// 被上层 catch 成"通讯失败: ..." —— 现场看不出是"设备已经换了/关了",
    /// 只会以为通讯坏了,反复重连一个已经不存在的服务。
    /// </summary>
    private void EnsureNotDisposed ()
    {
        if (_disposed) throw new ObjectDisposedException (nameof (ModbusTcpClient));
    }

    /// <summary>
    /// 执行一次请求-响应往返。串行化,校验事务号,失败抛异常。
    /// </summary>
    private void Execute (byte[] pdu, byte expectedFunction, Action<byte[]> onResponse)
    {
        EnsureNotDisposed ();

        if (!_requestLock.Wait (Timeout))
        {
            throw new ModbusException ("等待请求锁超时");
        }

        t_inRequest = true;

        try
        {
            Connect ();

            var transactionId = NextTransactionId ();

            var frame = ModbusFrame.BuildFrame (transactionId, _unitId, pdu);

            var stream = _stream ?? throw new ModbusException ("连接未建立");

            stream.Write (frame, 0, frame.Length);
            stream.Flush ();

            var response = ReadResponse (stream);

            var (function, data) = ModbusFrame.ParseResponse (response, transactionId, _unitId);

            if (function != expectedFunction)
            {
                throw new ModbusException (
                    "功能码不匹配: 期望 0x" + expectedFunction.ToString ("X2")
                    + ",实际 0x" + function.ToString ("X2"));
            }

            onResponse (data);
        }
        catch (ModbusException)
        {
            // 协议层异常(超时/CRC/半帧)之后流里可能还留着残帧,
            // 不断开的话下一帧会从错误的边界开始读 —— 现场表现为"一断就连续报错"。
            Disconnect ();

            throw;
        }
        catch (Exception ex)
        {
            // 网络异常后连接已不可信,断开以便下次重连
            Disconnect ();

            throw new ModbusException ("通讯失败: " + DescribeNetworkError (ex), ex);
        }
        finally
        {
            t_inRequest = false;

            _requestLock.Release ();
        }
    }

    /// <summary>
    /// 把 .NET 的英文网络异常翻译成现场看得懂的中文(界面直接显示这句)。
    /// </summary>
    private string DescribeNetworkError (Exception ex)
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

        if (ex is System.IO.IOException)
        {
            // 读超时在 .NET 里表现为 IOException("Unable to read data from...")
            return "通讯超时或对方断开:设备在 " + (int)Timeout.TotalMilliseconds + " ms 内没有回应";
        }

        return ex.Message;
    }

    private ushort NextTransactionId ()
    {
        _transactionId++;

        // 0 保留为无效值,跳过
        if (_transactionId == 0) _transactionId = 1;

        return _transactionId;
    }

    /// <summary>读完整响应帧。先读 7 字节头,再按长度字段读剩余。</summary>
    private byte[] ReadResponse (NetworkStream stream)
    {
        var header = new byte[ModbusFrame.MbapHeaderLength];

        // 整帧共用一个截止时间,否则分段读时每段各等一整个 Timeout,
        // 125 个寄存器最坏能等十几分钟(现场表现为"软件死了")。
        var deadline = Environment.TickCount64 + (long)Timeout.TotalMilliseconds;

        ReadExactly (stream, header, 0, ModbusFrame.MbapHeaderLength, deadline);

        var declaredLength = (ushort)((header[4] << 8) | header[5]);

        // 长度字段包含单元号(1 字节) + PDU
        if (declaredLength < 2 || declaredLength > 260)
        {
            throw new ModbusException ("响应长度字段异常: " + declaredLength);
        }

        var remaining = declaredLength - 1;

        var frame = new byte[ModbusFrame.MbapHeaderLength + remaining];

        Array.Copy (header, 0, frame, 0, ModbusFrame.MbapHeaderLength);

        ReadExactly (stream, frame, ModbusFrame.MbapHeaderLength, remaining, deadline);

        return frame;
    }

    /// <summary>必须读满指定字节数,否则抛异常(TCP 可能分段返回);整帧共用一个截止时间。</summary>
    private static void ReadExactly (NetworkStream stream, byte[] buffer, int offset, int count, long deadlineTick)
    {
        var total = 0;

        while (total < count)
        {
            var remaining = deadlineTick - Environment.TickCount64;

            if (remaining <= 0)
            {
                throw new ModbusException (
                    "等待响应超时(只收到 " + total + "/" + count + " 字节)");
            }

            stream.ReadTimeout = (int)Math.Max (1, Math.Min (remaining, int.MaxValue));

            var read = stream.Read (buffer, offset + total, count - total);

            if (read <= 0)
            {
                throw new ModbusException ("连接被对方关闭");
            }

            total += read;
        }
    }

    public void Dispose ()
    {
        if (_disposed) return;

        // 先立旗:之后的读写/连接一律明确拒绝,不再去碰即将释放的信号量
        _disposed = true;

        // 给在途帧一拍时间收尾(最多 InFlightDrainTimeout),再关连接 ——
        // 直接腰斩会让对端看到半帧,帧边界错位后每一帧都解析失败。
        // 等不到(卡死的往返)也只能关句柄:那是唯一能打断阻塞 I/O 的手段。
        var drained = false;

        try
        {
            drained = _requestLock.Wait (InFlightDrainTimeout);
        }
        catch (ObjectDisposedException)
        {
            // 已经释放过了:忽略,下面照常关连接
        }

        lock (_connectLock) CloseInternal ();

        if (drained)
        {
            try { _requestLock.Release (); } catch (SemaphoreFullException) { }
        }

        _requestLock.Dispose ();
    }
}
