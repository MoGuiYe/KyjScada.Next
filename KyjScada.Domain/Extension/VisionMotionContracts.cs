namespace KyjScada.Domain.Extension;

/// <summary>
/// 视觉系统契约(P3 预留,当前无实现)。
///
/// 为什么只留接口:视觉工位(定位/读码/外观检测)高度依赖选型 —— 海康 VM、Cognex VisionPro、
/// 基恩士、开源 Halcon/OpenCV 的取像与结果协议各不相同。硬做一套只会做成谁都用不了的中间层。
/// 这里只固定"结果怎么回传给 SCADA"这一层,将来接哪家都在适配器里翻译。
///
/// 接线方式(将来):视觉工位把检测结果(OK/NG/测量值/条码)写入 PLC 寄存器,
/// SCADA 照常采集 —— 与现有 Modbus 采集链路零改动。本接口只用于"需要 SCADA 主动触发拍照"的场合。
/// </summary>
public interface IVisionSystem
{
    /// <summary>工位标识。</summary>
    string StationName { get; }

    /// <summary>是否已连接(未连接时调用应快速失败而不是阻塞)。</summary>
    bool IsConnected { get; }

    /// <summary>
    /// 触发一次拍照检测。返回检测结果,不抛异常 —— 失败信息放 <see cref="VisionResult.Message"/>。
    ///
    /// 上层必须能容忍超时:视觉偶尔卡住不能把采集线程拖死,所以由调用方决定超时时间。
    /// </summary>
    Task<VisionResult> TriggerAsync(int timeoutMs, CancellationToken ct = default);
}

/// <summary>一次视觉检测的结果。</summary>
/// <param name="Success">检测是否完成(不代表工件是 OK)。</param>
/// <param name="Verdict">OK / NG / 无法判断。</param>
/// <param name="Measurements">测量项名 → 数值(如 孔径 12.03)。</param>
/// <param name="Barcode">读到的条码,没有则为 null。</param>
/// <param name="Message">失败或说明文字。</param>
public sealed record VisionResult(
    bool Success,
    string Verdict,
    IReadOnlyDictionary<string, double> Measurements,
    string? Barcode,
    string? Message)
{
    /// <summary>便捷构造:一次成功的 OK 结果。</summary>
    public static VisionResult Ok(string? barcode = null) =>
        new(true, "OK", new Dictionary<string, double>(), barcode, null);

    /// <summary>便捷构造:失败的 NG 结果。</summary>
    public static VisionResult Ng(string message) =>
        new(false, "NG", new Dictionary<string, double>(), null, message);
}

/// <summary>
/// 运动系统契约(P3 预留,当前无实现)。
///
/// 为什么只留接口:运动控制(单轴点到点 / 插补 / 回零)的安全责任不能放在 SCADA 上。
/// 硬性约定 —— **运动的实时控制由 PLC/运动控制器完成**,SCADA 只做参数下发与状态监视;
/// 任何"从 SCADA 直接发脉冲"的实现都不能用于有安全要求的现场。
/// </summary>
public interface IMotionSystem
{
    /// <summary>控制器/轴组标识。</summary>
    string UnitName { get; }

    /// <summary>是否已连接。</summary>
    bool IsConnected { get; }

    /// <summary>当前各轴位置(轴名 → 位置)。</summary>
    Task<IReadOnlyDictionary<string, double>> ReadPositionsAsync(CancellationToken ct = default);

    /// <summary>
    /// 下发定位目标。只写目标值,由控制器执行运动 —— SCADA 不负责插补与加减速。
    /// </summary>
    Task<bool> MoveToAsync(string axis, double position, CancellationToken ct = default);

    /// <summary>回零。</summary>
    Task<bool> HomeAsync(string axis, CancellationToken ct = default);
}