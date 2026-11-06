using KyjScada.Contracts;
using KyjScada.Domain.Config;
using KyjScada.Domain.Protocol;

namespace KyjScada.Infrastructure.Acquisition;

/// <summary>采集器创建结果。不支持的协议也要给出**明确原因**,不能静默不采。</summary>
public sealed record AcquisitionPlan(
    bool Supported,
    string ProtocolKey,
    string DisplayName,
    string Message,
    IAcquisitionService? Service);

/// <summary>
/// 采集器工厂。**按设备自带的 ProtocolKey 分派**,与配置文件叫什么名字无关。
///
/// 这是"换配置就换协议"的落点:
///  - 配置里设备挂在哪个协议列表下(如 SiemensList),加载出来 ProtocolKey 就是什么;
///  - 工厂按这个键选实现,界面不需要知道协议细节;
///  - 未实现的协议返回 Supported=false + 原因,界面据此显示"该设备不采集",
///    **绝不能让程序崩,也不能静默返回空值**(静默失败是老系统最难查的问题)。
///
/// 新增一种协议 = 写一个 IAcquisitionService 实现 + 在下面 switch 里加一行。
/// </summary>
public static class AcquisitionFactory
{
    /// <summary>创建采集器。device.ProtocolKey 决定实现。</summary>
    public static AcquisitionPlan Create(DeviceConfig? device)
    {
        if (device is null)
        {
            return new AcquisitionPlan(false, "", "", "设备为空", null);
        }

        var key = device.ProtocolKey ?? "";

        var descriptor = ProtocolRegistry.Find(key);

        if (descriptor is null)
        {
            return new AcquisitionPlan(false, key, key,
                "未知协议: " + key, null);
        }

        var name = descriptor.DisplayName;

        if (!ProtocolKeyMap.IsImplementedInV1(key))
        {
            return new AcquisitionPlan(false, key, name,
                "协议 " + name + " 尚未实现采集,该设备暂不采集", null);
        }

        try
        {
            switch (key)
            {
                case "ModbusTCP":
                    return new AcquisitionPlan(true, key, name, "",
                        new ModbusAcquisitionService(device, descriptor));

                case "ModbusRTU":
                    // 串口 RTU:传输层由服务内部按 Connection.Kind=Serial 自动选
                    return new AcquisitionPlan(true, key, name, "",
                        new ModbusAcquisitionService(device, descriptor));

                case "ModbusRTUOverTCP":
                    // RTU 帧跑裸 TCP:服务内部按协议键选 ModbusRtuOverTcpTransport
                    return new AcquisitionPlan(true, key, name, "",
                        new ModbusAcquisitionService(device, descriptor));

                default:
                    // IsImplementedInV1 说支持,但这里没对应实现 —— 属于漏配,明确报出来
                    return new AcquisitionPlan(false, key, name,
                        "协议 " + name + " 标记为已实现,但工厂缺少对应实现", null);
            }
        }
        catch (Exception ex)
        {
            return new AcquisitionPlan(false, key, name,
                "创建采集器失败: " + ex.Message, null);
        }
    }

    /// <summary>该协议当前是否能采集(界面用来灰掉/提示)。</summary>
    public static bool IsCollectable(string? protocolKey)
    {
        if (string.IsNullOrWhiteSpace(protocolKey)) return false;

        return ProtocolKeyMap.IsImplementedInV1(protocolKey)
            && ProtocolRegistry.Find(protocolKey) is not null;
    }
}