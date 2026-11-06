namespace KyjScada.Infrastructure.Tests;

using System.Net;
using System.Net.Sockets;
using KyjScada.Infrastructure.Modbus;

/// <summary>
/// 最小 Modbus TCP 从站模拟器。真监听、真解析、真存状态。
/// 不用 mock:老系统 WriteBool 缺陷正是看起来调用了实际没写进去,
/// mock 会掩盖它。必须真发帧、真改线圈、再读回验证。
/// </summary>
public sealed class ModbusSlaveSimulator : IDisposable
{
    private readonly TcpListener _listener;
    private readonly bool[] _coils = new bool[256];
    private readonly ushort[] _regs = new ushort[256];
    private readonly object _gate = new object ();
    private volatile bool _running = true;
    private int _count;

    /// <summary>true = Modbus RTU Over TCP(RTU 帧 + CRC,无 MBAP 头)。</summary>
    public bool RtuOverTcp { get; }

    public int Port { get; }
    public int RequestCount => Volatile.Read (ref _count);

    public ModbusSlaveSimulator () : this (false)
    {
    }

    /// <summary>
    /// <paramref name="rtuOverTcp"/>=true 时按**现场那种**协议应答:
    /// 帧 = 站号 + PDU + CRC16,没有 MBAP 头。
    /// 这条分支存在的意义:以前只用 MBAP 模拟器测,结果现场跑的是 RTU Over TCP,
    /// 单测全绿但现场不通 —— 现在两种帧格式都得真收发一遍。
    /// </summary>
    public ModbusSlaveSimulator (bool rtuOverTcp)
    {
        RtuOverTcp = rtuOverTcp;

        _listener = new TcpListener (IPAddress.Loopback, 0);
        _listener.Start ();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        var t = new Thread (AcceptLoop) { IsBackground = true };
        t.Start ();
    }

    public bool GetCoil (int i) { lock (_gate) return _coils[i]; }
    public ushort GetRegister (int i) { lock (_gate) return _regs[i]; }
    public void SetRegister (int i, ushort v) { lock (_gate) _regs[i] = v; }

    private void AcceptLoop ()
    {
        while (_running)
        {
            try
            {
                var c = _listener.AcceptTcpClient ();
                var t = new Thread (() => Serve (c)) { IsBackground = true };
                t.Start ();
            }
            catch { return; }
        }
    }

    private void Serve (TcpClient client)
    {
        NetworkStream s = null;
        try
        {
            s = client.GetStream ();
            while (_running)
            {
                if (RtuOverTcp)
                {
                    if (!ServeRtuOnce (s)) return;
                    continue;
                }

                var h = new byte[ModbusFrame.MbapHeaderLength];
                var okH = ReadExact (s, h, h.Length);
                var len = (ushort)((h[4] << 8) | h[5]);
                var pduLen = len >= 2 ? len - 1 : 0;
                var pdu = new byte[pduLen];
                var okP = pduLen > 0 && ReadExact (s, pdu, pduLen);
                if (!(okH && okP)) return;
                Interlocked.Increment (ref _count);
                var resp = Handle (pdu);
                var total = (ushort)(resp.Length + 1);
                var f = new byte[ModbusFrame.MbapHeaderLength + resp.Length];
                f[0] = h[0]; f[1] = h[1]; f[2] = 0; f[3] = 0;
                f[4] = (byte)(total >> 8); f[5] = (byte)(total & 0xFF); f[6] = h[6];
                Array.Copy (resp, 0, f, ModbusFrame.MbapHeaderLength, resp.Length);
                s.Write (f, 0, f.Length);
                s.Flush ();
            }
        }
        catch { }
        finally
        {
            try { s?.Dispose (); } catch { }
            try { client.Dispose (); } catch { }
        }
    }


    /// <summary>
    /// 处理一个 RTU 帧:读 7 字节头(站号+功能码+2+2),再按功能码决定是否多读。
    /// 本模拟器只覆盖采集用到的 1/2/3/4 和写入用到的 5/6/16。
    /// </summary>
    private bool ServeRtuOnce (NetworkStream s)
    {
        // RTU 帧头固定 6 字节:站号(1) + 功能码(1) + 地址(2) + 数量/值(2),后跟 CRC(2)
        var head = new byte[6];
        if (!ReadExact (s, head, head.Length)) return false;

        var station = head[0];
        var fn = head[1];

        byte[] pdu;

        if (fn == ModbusFunction.WriteMultipleRegisters)
        {
            var bc = new byte[1];
            if (!ReadExact (s, bc, 1)) return false;

            var byteCount = bc[0];
            var data = new byte[byteCount];
            if (byteCount > 0 && !ReadExact (s, data, byteCount)) return false;

            var crc = new byte[2];
            if (!ReadExact (s, crc, 2)) return false;

            pdu = new byte[6 + byteCount];
            Array.Copy (head, 1, pdu, 0, 5);
            pdu[5] = byteCount;
            Array.Copy (data, 0, pdu, 6, byteCount);
        }
        else
        {
            var crc = new byte[2];
            if (!ReadExact (s, crc, 2)) return false;

            pdu = new byte[5];
            Array.Copy (head, 1, pdu, 0, 5);
        }

        Interlocked.Increment (ref _count);

        var resp = Handle (pdu);
        var frame = new byte[1 + resp.Length + 2];
        frame[0] = station;
        Array.Copy (resp, 0, frame, 1, resp.Length);

        var c = ModbusCrc16.Compute (frame, 0, frame.Length - 2);
        frame[frame.Length - 2] = (byte)(c & 0xFF);
        frame[frame.Length - 1] = (byte)(c >> 8);

        s.Write (frame, 0, frame.Length);
        s.Flush ();

        return true;
    }

    private byte[] Handle (byte[] pdu)
    {
        var fn = pdu[0];
        switch (fn)
        {
            case ModbusFunction.ReadCoils: return ReadCoils (pdu);
            case ModbusFunction.ReadHoldingRegisters: return ReadRegs (pdu);
            case ModbusFunction.WriteSingleCoil: return WriteCoil (pdu);
            case ModbusFunction.WriteSingleRegister: return WriteReg (pdu);
            case ModbusFunction.WriteMultipleRegisters: return WriteRegs (pdu);
            default: return Err (fn, 1);
        }
    }

    private byte[] ReadRegs (byte[] pdu)
    {
        var start = (ushort)((pdu[1] << 8) | pdu[2]);
        var qty = (ushort)((pdu[3] << 8) | pdu[4]);
        var r = new byte[2 + qty * 2];
        r[0] = ModbusFunction.ReadHoldingRegisters;
        r[1] = (byte)(qty * 2);
        var i = 0;
        lock (_gate)
        {
            while (i < qty)
            {
                var v = _regs[start + i];
                r[2 + i * 2] = (byte)(v >> 8);
                r[3 + i * 2] = (byte)(v & 0xFF);
                i++;
            }
        }
        return r;
    }

    private byte[] ReadCoils (byte[] pdu)
    {
        var start = (ushort)((pdu[1] << 8) | pdu[2]);
        var qty = (ushort)((pdu[3] << 8) | pdu[4]);
        var bytes = (qty + 7) / 8;
        var r = new byte[2 + bytes];
        r[0] = ModbusFunction.ReadCoils;
        r[1] = (byte)bytes;
        lock (_gate)
        {
            var i = 0;
            while (i < qty)
            {
                if (_coils[start + i]) r[2 + i / 8] |= (byte)(1 << (i % 8));
                i++;
            }
        }
        return r;
    }

    private byte[] WriteCoil (byte[] pdu)
    {
        var addr = (ushort)((pdu[1] << 8) | pdu[2]);
        var raw = (ushort)((pdu[3] << 8) | pdu[4]);
        var legal = raw == ModbusFrame.CoilOn || raw == ModbusFrame.CoilOff;
        if (!legal) return Err (pdu[0], 3);
        lock (_gate) _coils[addr] = raw == ModbusFrame.CoilOn;
        return new byte[] { ModbusFunction.WriteSingleCoil, pdu[1], pdu[2], pdu[3], pdu[4] };
    }

    private byte[] WriteReg (byte[] pdu)
    {
        var addr = (ushort)((pdu[1] << 8) | pdu[2]);
        var v = (ushort)((pdu[3] << 8) | pdu[4]);
        lock (_gate) _regs[addr] = v;
        return new byte[] { ModbusFunction.WriteSingleRegister, pdu[1], pdu[2], pdu[3], pdu[4] };
    }

    /// <summary>写多个寄存器(功能码 16)。回显只带起始地址与数量。</summary>
    private byte[] WriteRegs (byte[] pdu)
    {
        var start = (ushort)((pdu[1] << 8) | pdu[2]);
        var qty = (ushort)((pdu[3] << 8) | pdu[4]);
        var byteCount = pdu[5];

        if (byteCount != qty * 2) return Err (pdu[0], 3);

        lock (_gate)
        {
            for (int i = 0; i < qty; i++)
            {
                _regs[start + i] = (ushort)((pdu[6 + i * 2] << 8) | pdu[7 + i * 2]);
            }
        }

        return new byte[] { ModbusFunction.WriteMultipleRegisters, pdu[1], pdu[2], pdu[3], pdu[4] };
    }

    private static byte[] Err (byte fn, byte code)
    {
        return new byte[] { (byte)(fn | 0x80), code };
    }

    private static bool ReadExact (NetworkStream s, byte[] buf, int count)
    {
        var total = 0;
        while (total < count)
        {
            var n = s.Read (buf, total, count - total);
            if (n <= 0) return false;
            total += n;
        }
        return true;
    }

    public void Dispose ()
    {
        _running = false;
        try { _listener.Stop (); } catch { }
    }
}


