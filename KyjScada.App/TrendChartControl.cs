using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using KyjScada.Domain.History;
using KyjScada.Runtime;

namespace KyjScada.App;

/// <summary>一条曲线。变量名 + 颜色 + 数据桶(含包络)+ 报警限值。</summary>
public sealed class TrendSeries
{
    public string VarName { get; set; } = "";
    public string Unit { get; set; } = "";
    public Color Color { get; set; }

    /// <summary>
    /// 聚合后的数据桶。每个桶带 min/max(包络) ——
    /// 只存一个值会把尖峰抹平,必须同时保留波动范围。
    /// </summary>
    public List<TrendBucket> Points { get; set; } = new();

    /// <summary>上限(0 = 未配置)。用于限值线和超限段变色。</summary>
    public double HighLimit { get; set; }

    /// <summary>下限(0 = 未配置)。</summary>
    public double LowLimit { get; set; }

    public bool HasHigh => !double.IsNaN(HighLimit) && HighLimit != 0;
    public bool HasLow => !double.IsNaN(LowLimit) && LowLimit != 0;
    public bool HasLimits => HasHigh || HasLow;

    /// <summary>
    /// 高高限:由上限外推 10%。配置里只有 H/L 两级,
    /// 但工业上习惯看 HH/H/L/LL 四线 —— 外推是换算而不是实际值,所以画成实线以区分。
    /// </summary>
    public double HighHighLimit => HasHigh ? HighLimit + Math.Abs(HighLimit) * 0.1 : 0;

    /// <summary>低低限:由下限外推 10%。</summary>
    public double LowLowLimit => HasLow ? LowLimit - Math.Abs(LowLimit) * 0.1 : 0;

    /// <summary>是否真的有包络信息(某些桶 min≠max,说明一个桶内聚合了多个原始点)。</summary>
    public bool HasEnvelope => Points.Any(p => !p.IsEmpty && !double.IsNaN(p.Min) && Math.Abs(p.Max - p.Min) > 1e-12);

    /// <summary>
    /// 是否数字量(Bool/Byte)。数字量必须画**阶跃线**:
    /// 折线会在 0→1 之间画出一条斜线,让人以为状态是渐变的。
    /// </summary>
    public bool IsDigital { get; set; }

    /// <summary>该值是否超出配置的报警限。</summary>
    public bool IsOverLimit(double v)
    {
        if (HasHigh && v > HighLimit) return true;
        if (HasLow && v < LowLimit) return true;
        return false;
    }
}

/// <summary>光标处某条曲线的读数。</summary>
public sealed record CursorReading(string VarName, string Unit, Color Color, bool HasData, double Value, DateTime Time)
{
    public string ValueText => HasData ? Value.ToString("G6", CultureInfo.InvariantCulture) : "--";
}

/// <summary>图上的事件标记(报警触发等),画成时间轴上的竖线。</summary>
public sealed record TrendEventMarker(DateTime Time, string Text, bool IsAlarm);

/// <summary>曲线显示方式。</summary>
public enum TrendDisplayMode
{
    /// <summary>叠加:所有曲线共用一条 Y 轴。适合量纲接近的变量对比。</summary>
    Overlay = 0,

    /// <summary>分屏:每曲线一个格子、独立量程。量纲差得远时必须用这个。</summary>
    Stacked = 1
}

/// <summary>
/// 历史趋势控件。自绘,支持多曲线叠加、滚轮缩放、拖拽平移、十字光标读数、双击复位。
///
/// 为什么不引第三方图表库:
///  老系统也没用第三方库,自己画的。图表库升级会连锁改代码,且发布体积敏感。
///  我们要的东西(Polyline + 坐标轴 + 交互)原生都能做,而且缩放/抽稀策略完全可控。
///
/// 与老项目对齐的行为:
///  - 滚轮缩放以鼠标位置为锚点(factor 0.75),不是以窗口中心
///  - 拖拽平移做了边界钳制:不能拖出数据区、不能拖到未来(否则操作员以为数据丢了)
///  - 十字光标显示每条曲线在该时刻的值
///  - Y 轴刻度用 1/2/5×10ⁿ 的 NiceStep,不是均匀等分(等分会出现 3.74 / 7.48 这种丑数字)
/// </summary>
public sealed class TrendChartControl : FrameworkElement
{
    // ---- 布局 ----
    private const double MarginLeft = 68;
    private const double MarginRight = 18;
    private const double MarginTop = 16;
    private const double MarginBottom = 34;

    // ---- 颜色 ----
    private static readonly Brush BackBrush = new SolidColorBrush(Color.FromRgb(0x11, 0x11, 0x1B));
    private static readonly Brush GridBrush = new SolidColorBrush(Color.FromRgb(0x2A, 0x2B, 0x3D));
    private static readonly Brush AxisBrush = new SolidColorBrush(Color.FromRgb(0x45, 0x47, 0x5A));
    private static readonly Brush TextBrush = new SolidColorBrush(Color.FromRgb(0x6C, 0x70, 0x86));
    private static readonly Brush CursorBrush = new SolidColorBrush(Color.FromRgb(0xF9, 0xE2, 0xAF));
    private static readonly Brush BandBrush = new SolidColorBrush(Color.FromArgb(0x22, 0x89, 0xB4, 0xFA));

    // ---- 数据 ----
    private readonly List<TrendSeries> _series = new();

    /// <summary>全部数据的时间范围(用于复位和平移钳制)。</summary>
    private DateTime _dataStart = DateTime.MinValue;
    private DateTime _dataEnd = DateTime.MinValue;

    // ---- 视图窗口 ----
    private DateTime _viewStart = DateTime.MinValue;
    private DateTime _viewEnd = DateTime.MinValue;

    // ---- 交互状态 ----
    private bool _isPanning;
    private Point _dragStart;
    private DateTime _dragViewStart;
    private DateTime _dragViewEnd;

    // 框选区间(Shift+拖拽)
    private bool _isSelecting;
    private DateTime? _selAnchor;

    private Point? _cursorPoint;

    // 最近一次实际渲染用的量程(锁定量程时抓取)
    private double _lastMin = double.NaN;
    private double _lastMax = double.NaN;

    /// <summary>量程是否已锁定。锁定后缩放/平移不再改变 Y 轴范围。</summary>
    public bool YLocked { get; private set; }

    private double _lockMin;
    private double _lockMax;

    /// <summary>框选区间变化时触发(Shift+拖拽结束)。null 表示清除框选。</summary>
    public event Action<DateTime?, DateTime?>? RangeSelected;

    /// <summary>光标时间变化时触发。null 表示光标离开绘图区。</summary>
    public event Action<DateTime?>? CursorMoved;

    /// <summary>视图窗口变化时触发(缩放/平移/复位)。</summary>
    public event Action? ViewChanged;

    /// <summary>
    /// **用户主动**调整了视图(滚轮缩放或拖拽平移)。
    /// 与 ViewChanged 的区别:SetSeries/SetView 也会让视图变,但那不是用户在操作。
    /// 实时趋势靠这个决定"是否停止跟随最新时刻"。
    /// </summary>
    public event Action? UserAdjustedView;

    /// <summary>用户双击复位(实时趋势据此恢复跟随)。</summary>
    public event Action? UserResetView;

    private bool _showLimits;

    /// <summary>
    /// 是否显示报警上下限线,并把超限段标红。
    /// **默认关闭** —— 量程小的时候几条限值线会把曲线本身压得看不清,
    /// 需要时才打开。关闭时超限段也不标红,保证"看到的颜色就是曲线本身"。
    /// </summary>
    public bool ShowLimits
    {
        get => _showLimits;
        set
        {
            if (_showLimits == value) return;

            _showLimits = value;
            InvalidateVisual();
        }
    }

    private bool _showEnvelope = true;

    /// <summary>
    /// 是否显示最小/最大包络带。**默认开启**。
    ///
    /// 为什么默认开:聚合后每条线只画一个代表值,尖峰会被抹平。
    /// 包络带把该时间段内的真实波动范围画出来,防止"看着平稳其实超限过"。
    /// 只在真的有包络数据(桶内点数 > 1)时才画,原始模式不画。
    /// </summary>
    public bool ShowEnvelope
    {
        get => _showEnvelope;
        set
        {
            if (_showEnvelope == value) return;

            _showEnvelope = value;
            InvalidateVisual();
        }
    }

    private bool _showEvents = true;

    /// <summary>是否显示事件竖线(报警触发点)。</summary>
    public bool ShowEvents
    {
        get => _showEvents;
        set
        {
            if (_showEvents == value) return;

            _showEvents = value;
            InvalidateVisual();
        }
    }

    /// <summary>已加载的事件标记数(诊断用)。</summary>
    public int EventMarkerCount => _events.Count;

    /// <summary>上一次渲染实际画出的竖线条数(诊断用)。</summary>
    public int DrawnEventCount { get; private set; }

    private TrendDisplayMode _displayMode = TrendDisplayMode.Overlay;

    /// <summary>叠加 / 分屏。</summary>
    public TrendDisplayMode DisplayMode
    {
        get => _displayMode;
        set
        {
            if (_displayMode == value) return;

            _displayMode = value;
            InvalidateVisual();
        }
    }

    private List<TrendEventMarker> _events = new();

    /// <summary>
    /// 设置事件标记。最多画 200 条,防止密集报警把图糊住。
    ///
    /// **必须按时间升序**:绘图循环用 `break` 提前结束(事件已排序才成立)。
    /// 数据源(SQLite 查询)默认是倒序的,这里统一排一次。
    /// </summary>
    public void SetEvents(IEnumerable<TrendEventMarker> events)
    {
        _events = (events ?? Enumerable.Empty<TrendEventMarker>())
            .OrderBy(e => e.Time)
            .ToList();

        InvalidateVisual();
    }

    public TrendChartControl()
    {
        ClipToBounds = true;
        Focusable = true;
        SnapsToDevicePixels = true;
    }

    /// <summary>当前视图窗口(供外部显示)。</summary>
    public (DateTime start, DateTime end) ViewRange => (_viewStart, _viewEnd);

    public bool HasData => _series.Count > 0 && _dataStart <= _dataEnd;

    /// <summary>设置数据并复位视图到全范围。</summary>
    public void SetSeries(IEnumerable<TrendSeries> series)
    {
        _series.Clear();

        DateTime min = DateTime.MaxValue;
        DateTime max = DateTime.MinValue;

        foreach (var s in series)
        {
            if (s.Points.Count == 0) continue;

            _series.Add(s);

            if (s.Points[0].Time < min) min = s.Points[0].Time;
            if (s.Points[^1].Time > max) max = s.Points[^1].Time;
        }

        if (_series.Count == 0)
        {
            _dataStart = _dataEnd = DateTime.MinValue;
            _viewStart = _viewEnd = DateTime.MinValue;
            _cursorPoint = null;
            InvalidateVisual();
            return;
        }

        // 单点数据也给一个可见窗口,否则时间跨度 0 会除零
        if (max <= min) max = min.AddSeconds(1);

        _dataStart = min;
        _dataEnd = max;
        _viewStart = min;
        _viewEnd = max;

        _cursorPoint = null;
        InvalidateVisual();
        ViewChanged?.Invoke();
    }

    public void Clear()
    {
        SetSeries(Array.Empty<TrendSeries>());
    }

    /// <summary>复位到全部数据范围。</summary>
    public void ResetView()
    {
        if (!HasData) return;

        _viewStart = _dataStart;
        _viewEnd = _dataEnd;
        InvalidateVisual();
        ViewChanged?.Invoke();

        // 只有用户双击才恢复"跟随最新";程序调用 ResetView 不算
        UserResetView?.Invoke();
    }

    /// <summary>
    /// 强制设置视图窗口(实时趋势用:窗口跟着"现在"滚,不跟着数据范围)。
    /// 不触发 ViewChanged —— 调用方自己知道窗口变了,避免回调里再刷新造成循环。
    /// </summary>
    public void SetView(DateTime from, DateTime to)
    {
        if (to <= from) return;

        if (_viewStart == from && _viewEnd == to) return;

        _viewStart = from;
        _viewEnd = to;

        InvalidateVisual();
    }

    /// <summary>
    /// 取当前实际渲染用的 Y 量程。没有渲染过返回 false。
    /// 锁定量程前先抓这个,避免锁定一个"理论值"而和屏幕上的刻度对不上。
    /// </summary>
    public bool TryGetRenderedRange(out double min, out double max)
    {
        min = _lastMin;
        max = _lastMax;

        return !double.IsNaN(min) && !double.IsNaN(max) && max > min;
    }

    /// <summary>锁定当前量程。锁定后缩放/平移不再重算 Y 轴,防止量程跳变。</summary>
    public bool LockCurrentRange()
    {
        if (!TryGetRenderedRange(out var lo, out var hi)) return false;

        _lockMin = lo;
        _lockMax = hi;
        YLocked = true;

        InvalidateVisual();
        return true;
    }

    public void UnlockRange()
    {
        if (!YLocked) return;

        YLocked = false;
        InvalidateVisual();
    }

    /// <summary>当前框选区间。两端都为 null 表示没有框选。</summary>
    public DateTime? SelectionStart { get; private set; }

    public DateTime? SelectionEnd { get; private set; }

    /// <summary>清除框选。</summary>
    public void ClearSelection()
    {
        if (SelectionStart is null && SelectionEnd is null) return;

        SelectionStart = null;
        SelectionEnd = null;
        _isSelecting = false;
        _selAnchor = null;

        InvalidateVisual();
        RangeSelected?.Invoke(null, null);
    }

    /// <summary>
    /// 把光标移到指定时刻(事件列表点击行用)。
    /// 超出当前视图就先调整视图窗口再定位 —— 否则光标会落到画外看不见。
    /// </summary>
    public void MoveCursorTo(DateTime time)
    {
        if (!HasData) return;

        // 不在视图内:把视图窗口平移到包含它(保持当前跨度)
        if (time < _viewStart || time > _viewEnd)
        {
            var span = (_viewEnd - _viewStart).TotalSeconds;

            _viewStart = time - TimeSpan.FromSeconds(span / 2);
            _viewEnd = _viewStart.AddSeconds(span);

            ClampView();
            ViewChanged?.Invoke();
        }

        var x = XAtTime(time);
        var y = (PlotRect.Top + PlotRect.Bottom) / 2;

        _cursorPoint = new Point(x, y);

        InvalidateVisual();
        CursorMoved?.Invoke(time);
    }

    /// <summary>以视图中心为锚点缩放。factor &lt; 1 放大, &gt; 1 缩小。</summary>
    public void Zoom(double factor)
    {
        if (!HasData) return;

        var anchor = _viewStart + TimeSpan.FromSeconds((_viewEnd - _viewStart).TotalSeconds / 2);
        ZoomAt(anchor, factor);
    }

    /// <summary>以 anchor 为锚点缩放时间窗口。</summary>
    public void ZoomAt(DateTime anchor, double factor)
    {
        if (!HasData) return;

        var total = (_viewEnd - _viewStart).TotalSeconds;
        var next = total * factor;

        // 下限 5 秒(再细没意义),上限 365 天
        if (next < 5) next = 5;
        if (next > 86400 * 365) next = 86400 * 365;

        var ratio = total <= 0 ? 0.5 : (anchor - _viewStart).TotalSeconds / total;

        _viewStart = anchor - TimeSpan.FromSeconds(next * ratio);
        _viewEnd = _viewStart + TimeSpan.FromSeconds(next);

        ClampView();
        InvalidateVisual();
        ViewChanged?.Invoke();
    }

    /// <summary>把视图窗口夹在数据范围内:不能拖出数据区,也不能拖到未来。</summary>
    private void ClampView()
    {
        var total = (_viewEnd - _viewStart).TotalSeconds;
        if (total <= 0) return;

        // 窗口比数据全长还大:直接贴齐数据
        if (total >= (_dataEnd - _dataStart).TotalSeconds)
        {
            _viewStart = _dataStart;
            _viewEnd = _dataEnd;
            return;
        }

        // 左边界:不能早于数据起点
        if (_viewStart < _dataStart)
        {
            _viewStart = _dataStart;
            _viewEnd = _viewStart.AddSeconds(total);
        }

        // 右边界:不能晚于数据终点
        if (_viewEnd > _dataEnd)
        {
            _viewEnd = _dataEnd;
            _viewStart = _viewEnd.AddSeconds(-total);
        }
    }

    /// <summary>光标时刻各曲线的读数(供外部图例显示)。</summary>
    public List<CursorReading> GetReadingsAt(DateTime time)
    {
        var list = new List<CursorReading>(_series.Count);

        foreach (var s in _series)
        {
            if (s.Points.Count == 0)
            {
                list.Add(new CursorReading(s.VarName, s.Unit, s.Color, false, double.NaN, time));
                continue;
            }

            // 取时间上最近的点(二分查找会更快,但单曲线通常几千点,线性也够快)
            var nearest = s.Points[0];
            var bestDiff = Math.Abs((nearest.Time - time).TotalSeconds);

            foreach (var p in s.Points)
            {
                var diff = Math.Abs((p.Time - time).TotalSeconds);
                if (diff < bestDiff)
                {
                    bestDiff = diff;
                    nearest = p;
                }
            }

            // 距离该时刻超过一定阈值视为无数据(避免硬拉一个很远的点)
            var span = (_viewEnd - _viewStart).TotalSeconds;
            var tolerance = Math.Max(30, span / 100);

            var has = bestDiff <= tolerance;

            list.Add(new CursorReading(s.VarName, s.Unit, s.Color, has, nearest.Value, nearest.Time));
        }

        return list;
    }

    // ================= 交互 =================

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);

        if (!HasData) return;

        var p = e.GetPosition(this);
        if (!InPlot(p)) return;

        // 向上滚放大(窗口变窄),向下滚缩小
        var factor = e.Delta > 0 ? 0.75 : 1.0 / 0.75;

        ZoomAt(TimeAtX(p.X), factor);
        UserAdjustedView?.Invoke();
        e.Handled = true;
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);

        if (!HasData) return;

        var p = e.GetPosition(this);
        if (!InPlot(p)) return;

        if (e.ClickCount == 2)
        {
            // 双击复位(同时清掉框选,回到全局视图)
            ResetView();
            e.Handled = true;
            return;
        }

        // Shift + 拖拽 = 框选区间做统计(不启动平移)
        if (Keyboard.Modifiers == ModifierKeys.Shift)
        {
            _isSelecting = true;
            _selAnchor = TimeAtX(p.X);

            SelectionStart = _selAnchor;
            SelectionEnd = _selAnchor;

            CaptureMouse();
            Cursor = Cursors.IBeam;
            e.Handled = true;
            return;
        }

        _isPanning = true;
        _dragStart = p;
        _dragViewStart = _viewStart;
        _dragViewEnd = _viewEnd;

        CaptureMouse();
        Cursor = Cursors.SizeWE;
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);

        var p = e.GetPosition(this);

        if (_isSelecting)
        {
            var t = TimeAtX(Math.Max(MarginLeft, Math.Min(p.X, MarginLeft + PlotWidth)));
            var a = _selAnchor ?? t;

            SelectionStart = a < t ? a : t;
            SelectionEnd = a < t ? t : a;

            InvalidateVisual();
            return;
        }

        if (_isPanning)
        {
            var dx = p.X - _dragStart.X;
            var total = (_dragViewEnd - _dragViewStart).TotalSeconds;

            // 向右拖 → 内容右移 → 视图时间向前
            var shift = -dx / (PlotWidth <= 0 ? 1 : PlotWidth) * total;

            _viewStart = _dragViewStart.AddSeconds(shift);
            _viewEnd = _dragViewEnd.AddSeconds(shift);

            ClampView();
            InvalidateVisual();
            ViewChanged?.Invoke();
            UserAdjustedView?.Invoke();
            return;
        }

        if (!InPlot(p))
        {
            if (_cursorPoint is not null)
            {
                _cursorPoint = null;
                InvalidateVisual();
                CursorMoved?.Invoke(null);
            }
            return;
        }

        _cursorPoint = p;
        InvalidateVisual();
        CursorMoved?.Invoke(TimeAtX(p.X));
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);

        if (_isSelecting)
        {
            _isSelecting = false;
            ReleaseMouseCapture();
            Cursor = Cursors.Arrow;
            e.Handled = true;

            // 宽度不足 1 秒视为误点:清除框选,避免留下一个看不见的零宽区间
            var s = SelectionStart;
            var t = SelectionEnd;

            if (s is null || t is null || (t.Value - s.Value).TotalSeconds < 1)
            {
                SelectionStart = null;
                SelectionEnd = null;
                _selAnchor = null;

                InvalidateVisual();
                RangeSelected?.Invoke(null, null);
            }
            else
            {
                InvalidateVisual();
                RangeSelected?.Invoke(s, t);
            }

            return;
        }

        if (_isPanning)
        {
            _isPanning = false;
            ReleaseMouseCapture();
            Cursor = Cursors.Arrow;
            e.Handled = true;
        }
    }

    /// <summary>
    /// 鼠标捕获丢失时的兜底。不处理会导致"松手了还在拖动/还在框选",
    /// 老项目在这里踩过坑(P4-15g)。
    /// </summary>
    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);

        _isPanning = false;
        _isSelecting = false;
        Cursor = Cursors.Arrow;
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);

        if (_cursorPoint is not null && !_isPanning && !_isSelecting)
        {
            _cursorPoint = null;
            InvalidateVisual();
            CursorMoved?.Invoke(null);
        }
    }

    // ================= 坐标计算 =================

    private double PlotWidth => ActualWidth - MarginLeft - MarginRight;
    private double PlotHeight => ActualHeight - MarginTop - MarginBottom;

    private Rect PlotRect => new(MarginLeft, MarginTop, Math.Max(0, PlotWidth), Math.Max(0, PlotHeight));

    private bool InPlot(Point p) => PlotRect.Contains(p);

    private DateTime TimeAtX(double x)
    {
        var total = (_viewEnd - _viewStart).TotalSeconds;
        if (PlotWidth <= 0) return _viewStart;

        var ratio = (x - MarginLeft) / PlotWidth;
        ratio = Math.Max(0, Math.Min(1, ratio));

        return _viewStart.AddSeconds(total * ratio);
    }

    private double XAtTime(DateTime t)
    {
        var total = (_viewEnd - _viewStart).TotalSeconds;
        if (total <= 0) return MarginLeft;

        return MarginLeft + (t - _viewStart).TotalSeconds / total * PlotWidth;
    }

    // ================= 绘制 =================

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);

        var w = ActualWidth;
        var h = ActualHeight;

        if (w <= 0 || h <= 0) return;

        dc.DrawRectangle(BackBrush, null, new Rect(0, 0, w, h));

        // 先算再判:Rect 构造函数不接受负数,不能让它承担校验职责
        if (PlotWidth < 20 || PlotHeight < 20) return;

        var plot = PlotRect;

        if (!HasData)
        {
            DrawCenteredText(dc, "选择变量后点击「查询」", plot);
            return;
        }

        // 只取视图窗口内的数据算量程
        var (min, max) = ComputeYRange();

        // 记下本次实际用的量程,锁定量程时从这里抓
        _lastMin = min;
        _lastMax = max;

        // 分屏模式:每曲线一个 pane、各自独立量程
        if (_displayMode == TrendDisplayMode.Stacked)
        {
            DrawStacked(dc, plot);
            DrawEventMarkers(dc, plot);
            DrawSelection(dc, plot);
            DrawStackedCursor(dc, plot);
            return;
        }

        DrawGrid(dc, plot, min, max);
        DrawAxes(dc, plot);
        DrawLimitLines(dc, plot, min, max);
        // 包络带先画(在曲线下面),这样曲线本身不会被半透明色块盖住
        if (_showEnvelope) DrawEnvelope(dc, plot, min, max);

        DrawAllSeries(dc, plot, min, max);
        DrawEventMarkers(dc, plot);
        DrawLegend(dc, plot);
        DrawSelection(dc, plot);
        DrawCursor(dc, plot);
    }

    /// <summary>
    /// 分屏显示。每曲线一个 pane,各自独立量程。
    ///
    /// 为什么必须有这个:温度 1700、压力 12、流量 5,量纲差两个数量级,
    /// 叠加在一条 Y 轴上,小的那几条永远贴在底部成直线,等于没画。
    /// 老项目用分屏替代"双 Y 轴" —— 双轴永远有"谁的量程说了算"的歧义,
    /// 而分屏每格独立,读数不会误解。
    /// </summary>
    private void DrawStacked(DrawingContext dc, Rect plot)
    {
        var visible = _series.Where(s => s.Points.Count > 0).ToList();

        if (visible.Count == 0) return;

        const double gap = 6;

        var paneH = (plot.Height - gap * (visible.Count - 1)) / visible.Count;

        // 太小就退回叠加,否则每格连刻度都放不下
        if (paneH < 46)
        {
            DrawGrid(dc, plot, _lastMin, _lastMax);
            DrawAxes(dc, plot);
            DrawAllSeries(dc, plot, _lastMin, _lastMax);
            DrawLegend(dc, plot);
            return;
        }

        for (int i = 0; i < visible.Count; i++)
        {
            var s = visible[i];

            var top = plot.Top + i * (paneH + gap);
            var pane = new Rect(plot.Left, top, plot.Width, paneH);

            var (lo, hi) = ComputeSeriesRange(s);

            // 每格自己的横网格 + Y 刻度
            DrawPaneGrid(dc, pane, lo, hi);

            // 每格自己的限值线(比叠加模式更准:用这条曲线自己的限)
            if (_showLimits) DrawPaneLimits(dc, s, pane, lo, hi);

            // 包络
            if (_showEnvelope) DrawPaneEnvelope(dc, s, pane, lo, hi);

            // 曲线本体
            var pen = MakePen(s.Color, 1.5);
            DrawOneSeriesGeometry(dc, s, pane, lo, hi, pen);

            // 左上角标题:变量名 + 单位 + 该格量程
            DrawPaneTitle(dc, s, pane, lo, hi);
        }

        // 共用时间轴(只在最底部画一次)
        DrawTimeAxisOnly(dc, plot);
    }

    /// <summary>单条曲线在视图窗口内的量程(含包络上下界),上下留 8% 余量。</summary>
    private (double min, double max) ComputeSeriesRange(TrendSeries s)
    {
        if (YLocked) return (_lockMin, _lockMax);

        var min = double.MaxValue;
        var max = double.MinValue;
        var any = false;

        foreach (var p in s.Points)
        {
            if (p.Time < _viewStart) continue;
            if (p.Time > _viewEnd) break;

            if (p.IsEmpty) continue;
            if (double.IsNaN(p.Value) || double.IsInfinity(p.Value)) continue;

            var lo = double.IsNaN(p.Min) ? p.Value : p.Min;
            var hi = double.IsNaN(p.Max) ? p.Value : p.Max;

            if (double.IsNaN(lo) || double.IsInfinity(lo)) lo = p.Value;
            if (double.IsNaN(hi) || double.IsInfinity(hi)) hi = p.Value;

            if (lo < min) min = lo;
            if (hi > max) max = hi;
            any = true;
        }

        if (!any) return (0, 1);

        if (Math.Abs(max - min) < 1e-9)
        {
            var pad = Math.Abs(max) < 1e-9 ? 1 : Math.Abs(max) * 0.1;
            return (min - pad, max + pad);
        }

        var span = max - min;

        return (min - span * 0.08, max + span * 0.08);
    }

    /// <summary>单个 pane 的横网格 + Y 刻度(4 档,格子矮就少画)。</summary>
    private void DrawPaneGrid(DrawingContext dc, Rect pane, double min, double max)
    {
        var gridPen = new Pen(GridBrush, 1);
        gridPen.Freeze();

        var step = NiceStep((max - min) / (pane.Height < 80 ? 2 : 4));
        var first = Math.Ceiling(min / step) * step;

        var tickCount = (int)Math.Floor((max - first) / step) + 1;
        var digits = step >= 100 ? 0 : (step >= 1 ? 1 : 2);

        for (int i = 0; i < tickCount; i++)
        {
            var v = Math.Round((first + i * step) / step) * step;

            if (v < min || v > max) continue;

            var y = YOf(v, min, max, pane);

            dc.DrawLine(gridPen, new Point(pane.Left, y), new Point(pane.Right, y));

            var label = digits == 0
                ? v.ToString("F0", CultureInfo.InvariantCulture)
                : v.ToString("F" + digits, CultureInfo.InvariantCulture);

            DrawText(dc, label, new Point(4, y - 7), 10, TextBrush);
        }

        // pane 边框
        var axisPen = new Pen(AxisBrush, 1);
        axisPen.Freeze();

        dc.DrawLine(axisPen, new Point(pane.Left, pane.Top), new Point(pane.Left, pane.Bottom));
    }

    private void DrawPaneLimits(DrawingContext dc, TrendSeries s, Rect pane, double min, double max)
    {
        DrawLimitSet(dc, s, pane, min, max);
    }

    /// <summary>单曲线包络(在指定 pane 内)。逻辑同 DrawEnvelope,只是范围由调用方给。</summary>
    private void DrawPaneEnvelope(DrawingContext dc, TrendSeries s, Rect pane, double min, double max)
    {
        if (!s.HasEnvelope) return;

        var brush = new SolidColorBrush(s.Color) { Opacity = 0.25 };
        brush.Freeze();

        var edge = new Pen(new SolidColorBrush(s.Color) { Opacity = 0.6 }, 1);
        edge.Freeze();

        var segment = new List<TrendBucket>();

        foreach (var p in s.Points)
        {
            if (p.Time < _viewStart) continue;
            if (p.Time > _viewEnd) break;

            var valid = !p.IsEmpty
                && !double.IsNaN(p.Min) && !double.IsNaN(p.Max)
                && !double.IsInfinity(p.Min) && !double.IsInfinity(p.Max);

            if (!valid)
            {
                FlushEnvelope(dc, pane, min, max, segment, brush, edge);
                segment = new List<TrendBucket>();
                continue;
            }

            segment.Add(p);
        }

        FlushEnvelope(dc, pane, min, max, segment, brush, edge);
    }

    /// <summary>pane 左上角:变量名 + 单位 + 当前量程。</summary>
    private void DrawPaneTitle(DrawingContext dc, TrendSeries s, Rect pane, double min, double max)
    {
        var brush = new SolidColorBrush(s.Color);
        brush.Freeze();

        var unit = string.IsNullOrEmpty(s.Unit) ? "" : " " + s.Unit;
        var text = s.VarName + unit + "   [" + min.ToString("G5") + " ~ " + max.ToString("G5") + "]";

        var ft = MakeText(text, 11, brush);

        dc.DrawRectangle(BackBrush, null, new Rect(pane.Left + 4, pane.Top + 2, ft.Width + 8, 15));
        dc.DrawText(ft, new Point(pane.Left + 8, pane.Top + 3));
    }

    /// <summary>只画时间轴刻度(分屏模式共用底部一条)。</summary>
    private void DrawTimeAxisOnly(DrawingContext dc, Rect plot)
    {
        var pen = new Pen(GridBrush, 1);
        pen.Freeze();

        var total = (_viewEnd - _viewStart).TotalSeconds;
        var timeStep = PickTimeStep(total, plot.Width);

        string fmt;
        if (timeStep < 60) fmt = "HH:mm:ss";
        else if (total < 172800) fmt = "HH:mm";
        else if (total < 5184000) fmt = "MM-dd HH:mm";
        else fmt = "yyyy-MM-dd";

        var offset = (_viewStart - _viewStart.Date).TotalSeconds;
        var firstTick = Math.Ceiling(offset / timeStep) * timeStep;

        for (var sec = firstTick; sec <= total; sec += timeStep)
        {
            var t = _viewStart.Date.AddSeconds(sec);
            if (t < _viewStart) continue;
            if (t > _viewEnd) break;

            var x = XAtTime(t);

            dc.DrawLine(pen, new Point(x, plot.Top), new Point(x, plot.Bottom));

            var ft = MakeText(t.ToString(fmt, CultureInfo.InvariantCulture), 10, TextBrush);
            var lx = x - ft.Width / 2;

            if (lx < 2) lx = 2;
            if (lx + ft.Width > plot.Right) lx = plot.Right - ft.Width;

            dc.DrawText(ft, new Point(lx, plot.Bottom + 5));
        }
    }

    /// <summary>分屏模式的光标:一条竖线贯穿全部 pane,各 pane 在自己的曲线上标点。</summary>
    private void DrawStackedCursor(DrawingContext dc, Rect plot)
    {
        if (_cursorPoint is null) return;

        var p = _cursorPoint.Value;
        if (!plot.Contains(p)) return;

        var pen = new Pen(CursorBrush, 1) { DashStyle = DashStyles.Dash };
        pen.Freeze();

        dc.DrawLine(pen, new Point(p.X, plot.Top), new Point(p.X, plot.Bottom));

        var time = TimeAtX(p.X);
        var visible = _series.Where(s => s.Points.Count > 0).ToList();

        const double gap = 6;
        var paneH = (plot.Height - gap * (visible.Count - 1)) / visible.Count;

        if (paneH < 46) return;

        var span = (_viewEnd - _viewStart).TotalSeconds;
        var tolerance = Math.Max(30, span / 100);

        for (int i = 0; i < visible.Count; i++)
        {
            var s = visible[i];
            var pane = new Rect(plot.Left, plot.Top + i * (paneH + gap), plot.Width, paneH);

            var rec = FindNearest(s.Points, time);
            if (rec is null) continue;

            if (Math.Abs((rec.Time - time).TotalSeconds) > tolerance) continue;

            var (lo, hi) = ComputeSeriesRange(s);
            var y = YOf(rec.Value, lo, hi, pane);

            var brush = new SolidColorBrush(s.Color);
            brush.Freeze();

            dc.DrawEllipse(brush, null, new Point(p.X, y), 3, 3);
        }

        var ft = MakeText(time.ToString("MM-dd HH:mm:ss", CultureInfo.InvariantCulture), 10.5, CursorBrush);
        var tx = Math.Max(plot.Left, Math.Min(p.X - ft.Width / 2, plot.Right - ft.Width));

        dc.DrawRectangle(BackBrush, null, new Rect(tx - 3, plot.Top - 14, ft.Width + 6, 15));
        dc.DrawText(ft, new Point(tx, plot.Top - 13));
    }

    /// <summary>
    /// 画报警限值线(HH/H/L/LL 四线)。
    ///
    /// 叠加模式只画第一条有限值的曲线 —— 多条曲线各自一套限值会挤在一起看不出,
    /// 老项目也是这个取舍("避免误读")。分屏模式每格画自己的,见 DrawPaneLimits。
    ///
    /// 线型约定:HH/LL 是**外推值**(由 H/L 推 10%),画实线;
    /// H/L 是**真实配置**,画虚线。这样一眼能分出"哪条是配的、哪条是算的"。
    /// </summary>
    private void DrawLimitLines(DrawingContext dc, Rect plot, double min, double max)
    {
        if (!_showLimits) return;

        var s = _series.FirstOrDefault(x => x.HasLimits);
        if (s is null) return;

        DrawLimitSet(dc, s, plot, min, max);
    }

    private void DrawLimitSet(DrawingContext dc, TrendSeries s, Rect plot, double min, double max)
    {
        var red = Color.FromRgb(0xFF, 0x4B, 0x4B);
        var amber = Color.FromRgb(0xFF, 0xC1, 0x07);

        if (s.HasHigh)
        {
            DrawOneLimit(dc, plot, min, max, s.HighHighLimit, red, "高高限 " + s.HighHighLimit.ToString("G5"), solid: true);
            DrawOneLimit(dc, plot, min, max, s.HighLimit, amber, "上限 " + s.HighLimit.ToString("G5"), solid: false);
        }

        if (s.HasLow)
        {
            DrawOneLimit(dc, plot, min, max, s.LowLowLimit, red, "低低限 " + s.LowLowLimit.ToString("G5"), solid: true);
            DrawOneLimit(dc, plot, min, max, s.LowLimit, amber, "下限 " + s.LowLimit.ToString("G5"), solid: false);
        }
    }

    private void DrawOneLimit(
        DrawingContext dc, Rect plot, double min, double max,
        double value, Color color, string label, bool solid)
    {
        // 限值在量程外就不画,免得把量程撑大
        if (value < min || value > max) return;

        var y = YOf(value, min, max, plot);

        var pen = new Pen(new SolidColorBrush(color) { Opacity = solid ? 0.7 : 0.55 }, solid ? 1.6 : 1);

        if (!solid) pen.DashStyle = new DashStyle(new double[] { 6, 4 }, 0);

        pen.Freeze();

        dc.DrawLine(pen, new Point(plot.Left, y), new Point(plot.Right, y));

        var ft = MakeText(label, 10, new SolidColorBrush(color) { Opacity = 0.85 });

        // 标签放右侧,避免和左侧 Y 轴数字重叠
        var tx = plot.Right - ft.Width - 4;
        dc.DrawRectangle(BackBrush, null, new Rect(tx - 3, y - 13, ft.Width + 6, 13));
        dc.DrawText(ft, new Point(tx, y - 12.5));
    }

    /// <summary>
    /// 按视图窗口内的可见数据算 Y 量程,上下留 8% 余量。
    /// **包络的 min/max 也参与量程计算** —— 否则包络带会被裁掉一半,
    /// 看到的最大值就不是真实最大值了。
    /// </summary>
    private (double min, double max) ComputeYRange()
    {
        // 锁定量程:直接返回锁定值,不再随数据变化 —— 这是防量程跳变的关键
        if (YLocked) return (_lockMin, _lockMax);

        var min = double.MaxValue;
        var max = double.MinValue;
        var any = false;

        foreach (var s in _series)
        {
            foreach (var p in s.Points)
            {
                if (p.Time < _viewStart) continue;
                if (p.Time > _viewEnd) break;

                if (p.IsEmpty) continue;

                // 包络上下界一起纳入量程
                var lo = double.IsNaN(p.Min) ? p.Value : p.Min;
                var hi = double.IsNaN(p.Max) ? p.Value : p.Max;

                if (double.IsNaN(lo) || double.IsInfinity(lo)) lo = p.Value;
                if (double.IsNaN(hi) || double.IsInfinity(hi)) hi = p.Value;

                if (double.IsNaN(p.Value) || double.IsInfinity(p.Value)) continue;

                if (lo < min) min = lo;
                if (hi > max) max = hi;
                any = true;
            }
        }

        if (!any)
        {
            min = 0;
            max = 1;
        }

        if (Math.Abs(max - min) < 1e-9)
        {
            // 全平:给对称区间
            var pad = Math.Abs(max) < 1e-9 ? 1 : Math.Abs(max) * 0.1;
            min -= pad;
            max += pad;
        }
        else
        {
            var span = max - min;
            min -= span * 0.08;
            max += span * 0.08;
        }

        return (min, max);
    }

    private void DrawGrid(DrawingContext dc, Rect plot, double min, double max)
    {
        var pen = new Pen(GridBrush, 1);
        pen.Freeze();

        // 水平刻度:用 1/2/5×10ⁿ 的步长,避免丑数字
        var step = NiceStep((max - min) / 5);

        var first = Math.Ceiling(min / step) * step;

        // 用索引推算刻度值,不要用 v += step 累加 ——
        // 浮点累加会漂移,导致相邻两个刻度格式化出同一个字符串(比如两个 "4200")。
        // 大数还可能出现 3999.9999 这种脏值,按步长精度圆整后显示。
        var tickCount = (int)Math.Floor((max - first) / step) + 1;

        // 步长决定小数位:步长 >=100 不要小数;>=1 保留 1 位;更小保留 2 位
        var digits = step >= 100 ? 0 : (step >= 1 ? 1 : 2);

        for (int i = 0; i < tickCount; i++)
        {
            var v = first + i * step;

            // 按步长精度圆整,消除累加/除法带来的尾数
            v = Math.Round(v / step) * step;

            var y = YOf(v, min, max, plot);

            dc.DrawLine(pen, new Point(plot.Left, y), new Point(plot.Right, y));

            var label = digits == 0
                ? v.ToString("F0", CultureInfo.InvariantCulture)
                : v.ToString("F" + digits, CultureInfo.InvariantCulture);

            DrawText(dc, label, new Point(4, y - 7), 10.5, TextBrush);
        }

        // 垂直线:时间刻度。用阶梯步长而不是等分 —— 等分会得到 18:47:13 这种
        // 没有意义的刻度点,操作员没法在脑子里对上"上个整点"。
        var total = (_viewEnd - _viewStart).TotalSeconds;
        var timeStep = PickTimeStep(total, plot.Width);

        // 步长决定格式:秒级看时分秒,小时级看时分,天级看日期
        string fmt;
        if (timeStep < 60) fmt = "HH:mm:ss";
        else if (total < 172800) fmt = "HH:mm";
        else if (total < 5184000) fmt = "MM-dd HH:mm";
        else fmt = "yyyy-MM-dd";

        // 刻度对齐:从视图起点的整步长位置开始,而不是从 start 起算(否则刻度漂移)
        var origin = _viewStart;
        var offset = (origin - origin.Date).TotalSeconds;
        var firstTick = Math.Ceiling(offset / timeStep) * timeStep;

        for (var sec = firstTick; sec <= total; sec += timeStep)
        {
            var t = origin.Date.AddSeconds(sec);
            if (t < _viewStart) continue;
            if (t > _viewEnd) break;

            var x = XAtTime(t);

            dc.DrawLine(pen, new Point(x, plot.Top), new Point(x, plot.Bottom));

            // 天数跨度的刻度对齐到日期边界后,origin.Date 可能差一天,用当前 t 直接格式化
            var label = t.ToString(fmt, CultureInfo.InvariantCulture);
            var ft = MakeText(label, 10.5, TextBrush);

            // 边界标签防溢出
            var lx = x - ft.Width / 2;
            if (lx < 2) lx = 2;
            if (lx + ft.Width > plot.Right) lx = plot.Right - ft.Width;

            dc.DrawText(ft, new Point(lx, plot.Bottom + 5));
        }
    }

    /// <summary>
    /// 选时间刻度步长。用工业上认的阶梯(1/2/5/10/15/30秒,1/2/5/10/15/30分,1/2/6/12时,1/2/7天),
    /// 按可用宽度取能放下的最小步长。
    /// </summary>
    private static double PickTimeStep(double totalSeconds, double plotWidth)
    {
        // 台阶表(秒)
        double[] steps =
        {
            1, 2, 5, 10, 15, 30,
            60, 120, 300, 600, 900, 1800,
            3600, 7200, 10800, 21600, 43200,
            86400, 172800, 604800
        };

        // 目标:每 110 像素一个刻度
        var wantTicks = Math.Max(2, plotWidth / 110);
        var ideal = totalSeconds / wantTicks;

        foreach (var s in steps)
        {
            if (s >= ideal) return s;
        }

        return steps[^1];
    }

    private void DrawAxes(DrawingContext dc, Rect plot)
    {
        var pen = new Pen(AxisBrush, 1);
        pen.Freeze();

        dc.DrawLine(pen, new Point(plot.Left, plot.Top), new Point(plot.Left, plot.Bottom));
        dc.DrawLine(pen, new Point(plot.Left, plot.Bottom), new Point(plot.Right, plot.Bottom));
    }

    /// <summary>
    /// 画一条单色曲线。数字量(IsDigital)用**阶跃线**:
    /// 先水平走到下一个时刻,再垂直跳到新值。
    ///
    /// 为什么不能画折线:Bool 从 0 变 1 画斜线,图上看起来像是"逐渐升到 1"，
    /// 而实际是瞬间切换。开关量、运行状态、限位信号都属于这类。
    /// </summary>
    private void DrawOneSeriesGeometry(
        DrawingContext dc, TrendSeries s, Rect plot, double min, double max, Pen pen)
    {
        var geo = new StreamGeometry();
        StreamGeometryContext? ctx = null;

        double prevY = 0;
        var hasPrev = false;

        foreach (var p in s.Points)
        {
            if (p.Time < _viewStart) continue;
            if (p.Time > _viewEnd) break;

            // 空桶或坏值:断开,不连成直线
            if (p.IsEmpty || double.IsNaN(p.Value) || double.IsInfinity(p.Value))
            {
                ctx?.Close();
                ctx = null;
                hasPrev = false;
                continue;
            }

            var x = XAtTime(p.Time);
            var y = YOf(p.Value, min, max, plot);

            if (ctx is null)
            {
                ctx = geo.Open();
                ctx.BeginFigure(new Point(x, y), false, false);
            }
            else if (s.IsDigital && hasPrev)
            {
                // 阶跃:水平段 + 垂直段
                ctx.LineTo(new Point(x, prevY), true, false);
                ctx.LineTo(new Point(x, y), true, false);
            }
            else
            {
                ctx.LineTo(new Point(x, y), true, false);
            }

            prevY = y;
            hasPrev = true;
        }

        ctx?.Close();
        geo.Freeze();

        dc.DrawGeometry(null, pen, geo);
    }

    private static readonly Pen OverLimitPen = MakePen(Color.FromRgb(0xFF, 0x4B, 0x4B), 1.8);

    private static Pen MakePen(Color c, double thickness)
    {
        var p = new Pen(new SolidColorBrush(c), thickness);
        p.Freeze();
        return p;
    }

    /// <summary>
    /// 画所有曲线。ShowLimits 打开时,每曲线拆成"正常段"和"超限段"两个图元,
    /// 超限部分用红色,一眼能定位异常区间(老项目同样做法)。
    /// ShowLimits 关闭时只画单色曲线 —— 不标红,保证颜色语义单纯。
    /// </summary>
    private void DrawAllSeries(DrawingContext dc, Rect plot, double min, double max)
    {
        foreach (var s in _series)
        {
            if (s.Points.Count == 0) continue;

            var normalPen = MakePen(s.Color, 1.6);

            // 不显示限值、或数字量(阶跃线不支持超限分段配色):单条几何
            if (!_showLimits || s.IsDigital)
            {
                DrawOneSeriesGeometry(dc, s, plot, min, max, normalPen);
                continue;
            }

            // 显示限值时:拆成正常段/超限段两条几何
            var geos = new[] { new StreamGeometry(), new StreamGeometry() };
            StreamGeometryContext? ctx = null;
            var curState = -1;
            var hasAny = new bool[2];

            foreach (var p in s.Points)
            {
                if (p.Time < _viewStart) continue;
                if (p.Time > _viewEnd) break;

                // NaN/Infinity:断开当前线段,不连成直线(缺数连直线会误判工艺正常)
                if (double.IsNaN(p.Value) || double.IsInfinity(p.Value))
                {
                    ctx?.Close();
                    ctx = null;
                    curState = -1;
                    continue;
                }

                var over = s.IsOverLimit(p.Value);
                var st = over ? 1 : 0;

                var pt = new Point(XAtTime(p.Time), YOf(p.Value, min, max, plot));

                if (ctx is null || curState != st)
                {
                    // 状态切换:关掉旧上下文,开新的
                    ctx?.Close();

                    ctx = geos[st].Open();
                    ctx.BeginFigure(pt, false, false);

                    curState = st;
                    hasAny[st] = true;
                }
                else
                {
                    ctx.LineTo(pt, true, false);
                }
            }

            ctx?.Close();

            if (hasAny[0]) { geos[0].Freeze(); dc.DrawGeometry(null, normalPen, geos[0]); }
            if (hasAny[1]) { geos[1].Freeze(); dc.DrawGeometry(null, OverLimitPen, geos[1]); }
        }
    }

    /// <summary>
    /// 画事件竖线。报警触发点上画红色虚线 + 顶部倒三角,
    /// 让"曲线为什么在这里异常"和"当时报了警"在图上能直接对上。
    /// </summary>
    private void DrawEventMarkers(DrawingContext dc, Rect plot)
    {
        DrawnEventCount = 0;

        if (!_showEvents || _events.Count == 0) return;

        var alarmPen = new Pen(new SolidColorBrush(Color.FromArgb(0xAA, 0xFF, 0x4B, 0x4B)), 1)
        {
            DashStyle = new DashStyle(new double[] { 3, 4 }, 0)
        };
        alarmPen.Freeze();

        var infoPen = new Pen(new SolidColorBrush(Color.FromArgb(0x88, 0xFF, 0xC1, 0x07)), 1)
        {
            DashStyle = new DashStyle(new double[] { 3, 4 }, 0)
        };
        infoPen.Freeze();

        var alarmBrush = new SolidColorBrush(Color.FromArgb(0xCC, 0xFF, 0x4B, 0x4B));
        alarmBrush.Freeze();

        var infoBrush = new SolidColorBrush(Color.FromArgb(0xAA, 0xFF, 0xC1, 0x07));
        infoBrush.Freeze();

        var drawn = 0;

        foreach (var e in _events)
        {
            if (drawn >= 200) break;
            if (e.Time < _viewStart) continue;
            if (e.Time > _viewEnd) break;

            var x = XAtTime(e.Time);
            var pen = e.IsAlarm ? alarmPen : infoPen;
            var brush = e.IsAlarm ? alarmBrush : infoBrush;

            dc.DrawLine(pen, new Point(x, plot.Top + 7), new Point(x, plot.Bottom));

            // 顶部倒三角
            var tri = new StreamGeometry();

            using (var c = tri.Open())
            {
                c.BeginFigure(new Point(x, plot.Top + 7), true, true);
                c.LineTo(new Point(x - 4, plot.Top), true, false);
                c.LineTo(new Point(x + 4, plot.Top), true, false);
            }

            tri.Freeze();
            dc.DrawGeometry(brush, null, tri);

            drawn++;
        }

        DrawnEventCount = drawn;
    }

    /// <summary>图例:左上角,显示变量名和颜色。</summary>
    private void DrawLegend(DrawingContext dc, Rect plot)
    {
        var x = plot.Left + 8;
        var y = plot.Top + 6;

        foreach (var s in _series)
        {
            var brush = new SolidColorBrush(s.Color);
            brush.Freeze();

            dc.DrawRectangle(brush, null, new Rect(x, y + 3, 10, 3));

            var ft = MakeText(s.VarName, 11, brush);
            dc.DrawText(ft, new Point(x + 14, y));

            y += 16;
        }
    }

    /// <summary>
    /// 画最小/最大包络带。
    ///
    /// 做法:沿时间轴上行画 max、下行画 min,组成闭合多边形填充。
    /// 每个桶是独立的段 —— 空桶处必须断开,否则会把两段无关的数据连成一片。
    ///
    /// 为什么必须画:聚合后一条线只代表一个值(平均/最小/最大...),
    /// 尖峰在视觉上被抹平。包络带把"这段时间真实波动范围"还原出来。
    /// 老项目注释原话:"平均值会削平峰值,重要信号必须同时看波动范围"。
    /// </summary>
    private void DrawEnvelope(DrawingContext dc, Rect plot, double min, double max)
    {
        foreach (var s in _series)
        {
            if (!s.HasEnvelope) continue;

            // 透明度要够:太淡等于没画。填充 0.25 + 描边 0.6 在深色底上能看清,
            // 又不会盖住中间的曲线本身。
            var brush = new SolidColorBrush(s.Color) { Opacity = 0.25 };
            brush.Freeze();

            var edge = new Pen(new SolidColorBrush(s.Color) { Opacity = 0.6 }, 1);
            edge.Freeze();

            // 收集连续的有效桶段
            var segment = new List<TrendBucket>();

            foreach (var p in s.Points)
            {
                if (p.Time < _viewStart) continue;
                if (p.Time > _viewEnd) break;

                var valid = !p.IsEmpty
                    && !double.IsNaN(p.Min) && !double.IsNaN(p.Max)
                    && !double.IsInfinity(p.Min) && !double.IsInfinity(p.Max);

                if (!valid)
                {
                    // 断段:把已收集的段画出来再清空
                    FlushEnvelope(dc, plot, min, max, segment, brush, edge);
                    segment = new List<TrendBucket>();
                    continue;
                }

                segment.Add(p);
            }

            FlushEnvelope(dc, plot, min, max, segment, brush, edge);
        }
    }

    private void FlushEnvelope(
        DrawingContext dc, Rect plot, double min, double max,
        List<TrendBucket> segment, Brush fill, Pen edge)
    {
        // 少于 2 个点画不出带,跳过
        if (segment.Count < 2) return;

        var geo = new StreamGeometry();

        using (var ctx = geo.Open())
        {
            // 上沿:按时间顺序走 max
            ctx.BeginFigure(new Point(XAtTime(segment[0].Time), YOf(segment[0].Max, min, max, plot)), true, true);

            for (int i = 1; i < segment.Count; i++)
            {
                ctx.LineTo(new Point(XAtTime(segment[i].Time), YOf(segment[i].Max, min, max, plot)), true, false);
            }

            // 下沿:反向走 min,闭合成多边形
            for (int i = segment.Count - 1; i >= 0; i--)
            {
                ctx.LineTo(new Point(XAtTime(segment[i].Time), YOf(segment[i].Min, min, max, plot)), true, false);
            }
        }

        geo.Freeze();

        dc.DrawGeometry(fill, edge, geo);
    }

    /// <summary>十字光标:竖线 + 各曲线交点处的标记。</summary>
    private void DrawCursor(DrawingContext dc, Rect plot)
    {
        if (_cursorPoint is null) return;

        var p = _cursorPoint.Value;
        if (!plot.Contains(p)) return;

        var pen = new Pen(CursorBrush, 1) { DashStyle = DashStyles.Dash };
        pen.Freeze();

        dc.DrawLine(pen, new Point(p.X, plot.Top), new Point(p.X, plot.Bottom));

        // 各曲线在光标时刻的交点
        var time = TimeAtX(p.X);
        var (min, max) = ComputeYRange();

        foreach (var s in _series)
        {
            if (s.Points.Count == 0) continue;

            var nearest = FindNearest(s.Points, time);
            if (nearest is null) continue;

            var span = (_viewEnd - _viewStart).TotalSeconds;
            var tolerance = Math.Max(30, span / 100);

            // nearest 是 TrendBucket 本身(引用类型),直接用它的 Time/Value
            var rec = nearest;

            if (Math.Abs((rec.Time - time).TotalSeconds) > tolerance) continue;

            var y = YOf(rec.Value, min, max, plot);

            var brush = new SolidColorBrush(s.Color);
            brush.Freeze();

            dc.DrawEllipse(brush, null, new Point(p.X, y), 3, 3);
        }

        // 顶部时间标签
        var ft = MakeText(time.ToString("MM-dd HH:mm:ss", CultureInfo.InvariantCulture), 10.5, CursorBrush);
        var tx = Math.Max(plot.Left, Math.Min(p.X - ft.Width / 2, plot.Right - ft.Width));

        dc.DrawRectangle(BackBrush, null, new Rect(tx - 3, plot.Top - 14, ft.Width + 6, 15));
        dc.DrawText(ft, new Point(tx, plot.Top - 13));
    }

    /// <summary>
    /// 画框选区间。半透明色块 + 两端时间标签 ——
    /// 框选的目的是"算这段时间的统计",必须让人看清选的是哪一段。
    /// </summary>
    private void DrawSelection(DrawingContext dc, Rect plot)
    {
        var s = SelectionStart;
        var t = SelectionEnd;

        if (s is null || t is null) return;

        var x1 = XAtTime(s.Value);
        var x2 = XAtTime(t.Value);

        if (x2 < x1) (x1, x2) = (x2, x1);

        // 太窄就不画(1 秒以内基本是误点)
        if (x2 - x1 < 2) return;

        var fill = new SolidColorBrush(Color.FromArgb(0x33, 0x89, 0xB4, 0xFA));
        fill.Freeze();

        var pen = new Pen(new SolidColorBrush(Color.FromArgb(0x99, 0x89, 0xB4, 0xFA)), 1);
        pen.Freeze();

        var rect = new Rect(x1, plot.Top, x2 - x1, plot.Height);
        dc.DrawRectangle(fill, pen, rect);

        // 区间时长标注在框内顶部
        var dur = t.Value - s.Value;
        var label = dur.TotalHours >= 1
            ? dur.TotalHours.ToString("F2") + " 小时"
            : dur.TotalMinutes >= 1
                ? dur.TotalMinutes.ToString("F1") + " 分钟"
                : ((int)dur.TotalSeconds) + " 秒";

        var ft = MakeText(label, 10, CursorBrush);
        var lx = x1 + ((x2 - x1) - ft.Width) / 2;
        lx = Math.Max(plot.Left, Math.Min(lx, plot.Right - ft.Width));

        dc.DrawRectangle(BackBrush, null, new Rect(lx - 3, plot.Top + 3, ft.Width + 6, 14));
        dc.DrawText(ft, new Point(lx, plot.Top + 4));
    }

    private static TrendBucket? FindNearest(List<TrendBucket> pts, DateTime time)
    {
        if (pts.Count == 0) return null;

        var best = pts[0];
        var bestDiff = Math.Abs((best.Time - time).TotalSeconds);

        foreach (var p in pts)
        {
            var diff = Math.Abs((p.Time - time).TotalSeconds);
            if (diff < bestDiff)
            {
                bestDiff = diff;
                best = p;
            }
        }

        return best;
    }

    private void DrawCenteredText(DrawingContext dc, string text, Rect plot)
    {
        var ft = MakeText(text, 13, TextBrush);
        dc.DrawText(ft, new Point(
            plot.Left + (plot.Width - ft.Width) / 2,
            plot.Top + (plot.Height - ft.Height) / 2));
    }

    private static void DrawText(DrawingContext dc, string text, Point at, double size, Brush brush)
    {
        dc.DrawText(MakeText(text, size, brush), at);
    }

    private static FormattedText MakeText(string text, double size, Brush brush)
    {
        return new FormattedText(
            text,
            CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight,
            new Typeface("Segoe UI"),
            size,
            brush,
            1.0);
    }

    private double YOf(double v, double min, double max, Rect plot)
    {
        if (max - min <= 0) return plot.Bottom;

        return plot.Bottom - (v - min) / (max - min) * plot.Height;
    }

    /// <summary>把步长规整到 1/2/5 × 10ⁿ,和老项目 ChartMath.NiceStep 一致。</summary>
    private static double NiceStep(double rough)
    {
        if (rough <= 0 || double.IsNaN(rough) || double.IsInfinity(rough)) return 1;

        var mag = Math.Pow(10, Math.Floor(Math.Log10(rough)));
        var norm = rough / mag;

        double step;
        if (norm <= 1) step = 1;
        else if (norm <= 2) step = 2;
        else if (norm <= 5) step = 5;
        else step = 10;

        return step * mag;
    }
}
