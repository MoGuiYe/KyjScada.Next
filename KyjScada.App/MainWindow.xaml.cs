using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using KyjScada.Contracts;
using KyjScada.Domain.Config;
using KyjScada.Domain.Protocol;
using KyjScada.Infrastructure.Acquisition;
using KyjScada.Infrastructure.Alarm;
using KyjScada.Infrastructure.Data;
using KyjScada.Infrastructure.Export;
using KyjScada.Infrastructure.Users;
using KyjScada.Runtime;

namespace KyjScada.App;

public partial class MainWindow : Window
{
    // 当前**显示**的设备用哪条总线;多设备并发时每台设备各有一条总线
    private IValueBus _bus = new ValueBus ();
    private readonly DispatcherTimer _uiTimer = new DispatcherTimer ();
    private DispatcherTimer _acqTimer = null;
    private IAcquisitionService _svc = null;
    private DeviceConfig _device = null;
    /// <summary>
    /// 实时监控表格的行。**必须是 ObservableCollection,不能用 List**。
    ///
    /// 用 List 时 BuildRows 里 Clear+Add 不通知界面,而紧跟着又把**同一个实例**
    /// 赋给 ValueGrid.ItemsSource —— WPF 看到引用没变就不重建行容器,
    /// 表格里留的还是上一批 ValueRow 对象,OnUiTick 更新的是新对象:
    /// 数据收到了、界面却一动不动(换设备/重连后必现)。
    /// 换成 ObservableCollection,Clear/Add 自己就会通知。
    /// </summary>
    private readonly System.Collections.ObjectModel.ObservableCollection<ValueRow> _rows
        = new System.Collections.ObjectModel.ObservableCollection<ValueRow> ();

    // 采集调度:同一时刻只采下拉框选中的那一台(自己的连接/总线/报警/归档)
    private AcquisitionCoordinator _coordinator = null;
    private DevicePipeline _pipeline = null;

    // 报警接线:状态机服务 + 落库仓储
    private AlarmService _alarm = null;
    private AlarmRepository _alarmRepo = null;
    private DateTime _lastAlarmWrite = DateTime.MinValue;
    private int _alarmTotalWritten = 0;

    // 归档接线:历史库 + 归档服务
    private HistoryRepository _historyRepo = null;
    private ArchiveService _archive = null;
    private DateTime _lastPurge = DateTime.MinValue;

    /// <summary>应用级设置(归档周期/保留期/写入限速),从 data\app_settings.ini 读取。</summary>
    private AppSettings _appSettings = new AppSettings ();

    public MainWindow ()
    {
        InitializeComponent ();

        _uiTimer.Interval = TimeSpan.FromMilliseconds (500);
        _uiTimer.Tick += OnUiTick;
        _uiTimer.Start ();

        Loaded += OnLoaded;
        Closed += OnClosed;

        // 趋势控件交互事件:光标读数 + 视图变化刷新统计
        TrendChart.CursorMoved += OnTrendCursorMoved;
        TrendChart.ViewChanged += OnTrendViewChanged;

        // Shift+拖拽框选 → 区间统计
        TrendChart.RangeSelected += OnTrendRangeSelected;

        // 实时趋势的跟随/重置/光标:只在这里订阅一次。
        // InitLiveTrend 在每次切换设备时都会重跑,若在那里 += 匿名 lambda,
        // 切 N 次后一次光标移动就会回调 N 次(越用越卡,且无法 -= 回收)。
        // 注意不能监听 ViewChanged —— SetSeries 内部也会触发它,那样每 tick 都会停跟随。
        LiveChart.UserAdjustedView += () => { _liveFollow = false; };
        LiveChart.UserResetView += () => { _liveFollow = true; };
        LiveChart.CursorMoved += OnLiveCursorMoved;

        // 首次切到趋势页时自动查一次
        MainTabs.SelectionChanged += (s, e) =>
        {
            if (MainTabs.SelectedIndex == 1 && !_trendQueried && TrendVarList.Items.Count > 0)
                RunTrendQuery ();
        };
    }

    private async void OnLoaded (object sender, RoutedEventArgs e)
    {
        // async void 事件处理器:异常必须自己兜住,否则直接终止进程
        try
        {
            await OnLoadedCore ();
        }
        catch (Exception ex)
        {
            StatusText.Text = "启动失败";
            FooterText.Text = ex.Message;
        }
    }

    private async Task OnLoadedCore ()
    {
        try
        {
            var dir = FindSettingsDir ();
            if  (dir is null)
            {
                StatusText.Text = "未找到配置目录";
                FooterText.Text = "请把 settings 目录放到程序目录下";
                return;
            }

            // 加载整个目录:现场按协议分文件(ModbusTCP/ModbusRTU/…,以后还有 OPC/西门子/三菱)
            var loaded = SettingsJsonCodec.LoadDirectory (dir);
            var project = loaded.Project;

            var devices = project.AllDevices ().ToList ();
            if  (devices.Count == 0)
            {
                StatusText.Text = "配置中没有设备";
                return;
            }

            _appSettings = AppSettings.Load ();

        _settingsDir = dir;
            _deviceList = devices;
            _deviceFiles = loaded.DeviceFiles;

            BuildDeviceSelector ();

            // 库与内嵌 MCP 是**进程级**资源,不依赖选没选设备。
            // 早先它们只在 ApplyDevice 里初始化:首启没记录设备时走 ApplyNoDevice,
            // 于是历史库/报警库/MCP 全都没起 —— 趋势、报表、报警查询一片空,
            // 外部 AI 也连不上,而界面看不出任何异常。
            EnsureDatabases ();

            // 只恢复**上次实际选过**的那台;没有记录就保持未选择,不替用户挑。
            // “自动挑一台能采的”看起来省事,实际是把通讯对象偷偷定了下来 ——
            // 现场换机器、换协议时谁都不知道为什么在采另一台。
            var remembered = LoadUiStateDevice ();

            var preferred = remembered is null
                ? null
                : devices.FirstOrDefault (d =>
                      string.Equals (d.DeviceName, remembered, StringComparison.OrdinalIgnoreCase));

            if (preferred is not null)
            {
                // 这里让 ApplyDevice 重建下拉框(BuildDeviceSelector 会按 _device 恢复选中项)。
                // 顺序很关键:此时 _device 还是 null,提前建出来的下拉框必然停在"未选择"上。
                ApplyDevice (preferred);
            }
            else
            {
                // 未选择:不建流水线、不通讯,界面给出明确引导。
                // **必须 await**:OnLoaded 已改成 async,不再走同步包装器。
                await ApplyNoDeviceAsync ();
            }

            // 文件多时不要把"排序第一个"当成主配置显示,会误导
            var fileText = loaded.Files.Count switch
            {
                0 => "(无)",
                1 => loaded.Files[0],
                2 => string.Join (" + ", loaded.Files),
                _ => loaded.Files.Count + " 个配置文件"
            };

            // 未选设备时 _device/_alarm/_archive 都是 null —— 直接拼会抛空引用,
            // 被下面的 catch 吞成"启动失败",把"请选设备"的引导整个盖掉(首启必现)。
            //
            // 引导必须**排在最前**:页脚是单行,配置信息放前面会把"该干什么"顶出屏幕。
            var head = _device is null
                ? "请在上方「设备」下拉框里选择要监控的设备(选择会记住)  ·  "
                : "当前监控 " + _device.DeviceName + "  ·  ";

            FooterText.Text = head
                + "配置: " + fileText
                + "   设备 " + devices.Count + " 台"
                + "   变量 " + _rows.Count + " 个"
                + "   报警 " + (_alarm?.WatchedVarCount ?? 0) + " 个"
                + "   归档 " + (_archive?.ArchiveVarCount ?? 0) + " 个";
        }
        catch (Exception ex)
        {
            StatusText.Text = "启动失败";
            FooterText.Text = ex.Message;
        }
    }

    // ==================== 设备选择 ====================

    private List<DeviceConfig> _deviceList = new List<DeviceConfig> ();

    /// <summary>设备名 → 来源配置文件(改字节序时写回用)。</summary>
    private Dictionary<string, string> _deviceFiles = new Dictionary<string, string> (StringComparer.OrdinalIgnoreCase);

    private string? _settingsDir;

    /// <summary>程序里改设备下拉时抑制 SelectionChanged 递归。</summary>
    private bool _switchingDevice;

    // ==================== 界面状态持久化 ====================

    /// <summary>
    /// 界面状态文件(记住"上次在看哪台设备")。
    ///
    /// 与设备配置分开:选哪台设备是**界面偏好**,不该写进设备的配置文件,
    /// 否则换个人上机就要改配置。字节序则相反,属于设备参数,写回配置。
    /// </summary>
    private static string UiStateFile
    {
        get
        {
            var dir = Path.Combine (AppContext.BaseDirectory, "data");
            Directory.CreateDirectory (dir);

            return Path.Combine (dir, "ui_state.ini");
        }
    }

    /// <summary>
    /// 记住当前选中的设备。
    ///
    /// 传空字符串表示"操作员主动选了未选择"——这个选择也要记住:
    /// 下次启动不能被程序自作主张挑一台就开始通讯。
    /// </summary>
    private static void SaveUiState (string deviceName)
    {
        try
        {
            File.WriteAllText (UiStateFile, "Device=" + (deviceName ?? "") + Environment.NewLine,
                new UTF8Encoding (false));
        }
        catch
        {
            // 记不住不影响使用
        }
    }

    private static string? LoadUiStateDevice ()
    {
        try
        {
            var f = UiStateFile;

            if (!File.Exists (f)) return null;

            foreach (var line in File.ReadAllLines (f))
            {
                var t = line.Trim ();

                if (t.StartsWith ("Device=", StringComparison.OrdinalIgnoreCase))
                {
                    return t.Substring ("Device=".Length).Trim ();
                }
            }
        }
        catch
        {
            // 读坏了就当没保存过
        }

        return null;
    }

    /// <summary>下拉框第 0 项:未选择设备。设备项从索引 1 开始。</summary>
    private const int NoDeviceIndex = 0;

    private void BuildDeviceSelector ()
    {
        _switchingDevice = true;

        DeviceBox.Items.Clear ();

        // 第 0 项是"未选择":设备选择本身是配置的一部分,默认不替用户决定采哪台
        DeviceBox.Items.Add ("— 未选择设备 —");

        foreach (var d in _deviceList)
        {
            var supported = AcquisitionFactory.IsCollectable (d.ProtocolKey);

            DeviceBox.Items.Add (d.DeviceName + "  [" + d.ProtocolKey + "]"
                + (supported ? "" : "  (未采集)"));
        }

        // 重建会把选中项清成 -1。**必须**按当前设备恢复回去:
        // 启动流程里是先设 SelectedIndex 再 ApplyDevice,而 ApplyDevice 又会调到这里,
        // 于是“记住的选择”每次都被自己抹掉 —— 表现为下拉框永远是空的。
        DeviceBox.SelectedIndex = IndexOfDevice (_device);

        _switchingDevice = false;
    }

    /// <summary>设备在 _deviceList 里的下标;下拉框下标 = 它 + 1(第 0 项是"未选择")。</summary>
    private int IndexOfDevice (DeviceConfig? device)
    {
        if (device is null) return NoDeviceIndex;

        for (var i = 0; i < _deviceList.Count; i++)
        {
            if (string.Equals (_deviceList[i].DeviceName, device.DeviceName, StringComparison.OrdinalIgnoreCase))
            {
                return i + 1;
            }
        }

        return NoDeviceIndex;
    }

    private async void OnDeviceChanged (object sender, SelectionChangedEventArgs e)
    {
        // async void 里没有 catch 会直接终止进程 —— 事件处理器必须兜住
        try
        {
            await OnDeviceChangedCore (sender, e);
        }
        catch (Exception ex)
        {
            if (!_closing) FooterText.Text = "切换设备失败: " + ex.Message;
        }
    }

    private async Task OnDeviceChangedCore (object sender, SelectionChangedEventArgs e)
    {
        if (_switchingDevice) return;

        var idx = DeviceBox.SelectedIndex;

        if (idx < 0) return;

        // 选"未选择"= 停掉通讯并记住这个选择。
        // **必须 await**:fire-and-forget 会与紧接着的选设备重建赛跑 ——
        // DropAllAsync 可能在新流水线建好之后才执行,把它清掉并释放。
        if (idx == NoDeviceIndex)
        {
            SaveUiState ("");
            await ApplyNoDeviceAsync ();

            return;
        }

        if (idx - 1 >= _deviceList.Count) return;

        ApplyDevice (_deviceList[idx - 1], rebuildSelector: false);
    }

    /// <summary>
    /// 切换当前监控的设备。
    ///
    /// **协议跟着设备走**:设备自带 ProtocolKey(来自它在配置里挂的协议列表),
    /// 工厂据此选实现。所以"换协议"只是换一台设备,不需要任何协议开关。
    /// </summary>
    private void ApplyDevice (DeviceConfig device, bool rebuildSelector = true)
    {
        // 只采这一台:切换设备时其它设备的流水线会在 EnsurePipeline 里被停掉并释放。
        // 每次连多台看起来"信息更全",实际是没人看的那几台在后台反复超时,
        // 而且 485 总线并发轮询会互相打断。
        _device = device;

        // 记住选择,下次启动直接回到这台
        SaveUiState (device.DeviceName);

        // 顺序很重要:先设 _device 再动 OrderBox,否则 OnOrderChanged 会拿旧设备判断
        OrderBox.SelectedIndex = (int)device.WordOrder;

        BuildRows ();
        EnsureDatabases ();
        InitTrend ();
        InitLiveTrend ();
        InitAlarmPage ();
        InitReport ();
        InitUsers ();

        if (rebuildSelector) BuildDeviceSelector ();

        // 重建流水线要先把旧连接停掉并释放,最长可能等 2 秒。
        // 这段等待**不能在界面线程上做**,否则每换一次设备整窗卡两秒。
        // 同时先把闸门的 Writer 摘掉:重建期间若有在途写入,
        // 绝不能让它打到马上就要被释放的旧服务上。
        if (_gate is not null) _gate.Writer = null;

        _ = ApplyDevicePipelineAsync (device, rebuildSelector);
    }

    /// <summary>
    /// 异步建立当前设备的流水线(界面部分已在 <see cref="ApplyDevice"/> 里同步做完)。
    ///
    /// 期间用户可能又切了设备:每一处 await 之后都要确认 _device 还是不是本次的目标,
    /// 否则会把"上一次选择"的结果写到界面上。
    /// </summary>
    private async Task ApplyDevicePipelineAsync (DeviceConfig device, bool rebuildSelector)
    {
        DevicePipeline? pipeline;

        try
        {
            pipeline = await EnsurePipelineAsync (device).ConfigureAwait (true);
        }
        catch (Exception ex)
        {
            if (!_closing && ReferenceEquals (_device, device)) FooterText.Text = "建立采集失败: " + ex.Message;

            return;
        }

        // 窗口已关 / 用户又切了设备:放弃本次结果
        if (_closing || !ReferenceEquals (_device, device)) return;

        _pipeline = pipeline;
        _svc = pipeline?.Service;
        _alarm = pipeline?.Alarm;
        _archive = pipeline?.Archive;

        if (pipeline is not null && pipeline.Service is not null)
        {
            // 总线切到这台设备自己的那条
            _bus = pipeline.Bus;

            StatusText.Text = "未连接";
            StatusText.Foreground = System.Windows.Media.Brushes.Salmon;

            StartAcquisition ();

            var plan = AcquisitionFactory.Create (device);

            DeviceText.Text = device.DeviceName + "  (" + plan.DisplayName + ")";

            // 写入器就是采集服务本身(共用同一个连接)
            BuildWritePage ();
            BuildJogPage ();
        }
        else
        {
            // 协议未实现:照样显示配置(变量全为"未采集"),只是不启动采集
            var plan = AcquisitionFactory.Create (device);

            DeviceText.Text = device.DeviceName + "  (" + plan.DisplayName + " · 未采集)";
            StatusText.Text = "该协议暂不采集";
            StatusText.Foreground = System.Windows.Media.Brushes.Goldenrod;

            FooterText.Text = pipeline?.UncollectableReason ?? plan.Message;

            StartAcquisition ();

            BuildWritePage ();
            BuildJogPage ();
        }
    }

    /// <summary>
    /// 建/取本次运行唯一的并发调度器。
    ///
    /// 采集器、报警状态机、归档器都按**设备**建,由调度器统一起停;
    /// 界面只关心"当前显示哪条流水线"。
    /// </summary>
    private AcquisitionCoordinator EnsureCoordinator ()
    {
        if (_coordinator is not null) return _coordinator;

        _coordinator = new AcquisitionCoordinator (
            device =>
            {
                var plan = AcquisitionFactory.Create (device);

                return plan.Supported ? plan.Service : null;
            },
            device =>
            {
                var alarm = new AlarmService { DefaultDeadband = _appSettings.DefaultAlarmDeadband };
                alarm.BuildFrom (device);

                return alarm;
            },
            device =>
            {
                if (_historyRepo is null) return null;

                try
                {
                    var archive = new ArchiveService (
                        _historyRepo,
                        TimeSpan.FromSeconds (_appSettings.ArchiveIntervalSeconds));

                    archive.BuildFrom (device);

                    return archive;
                }
                catch
                {
                    // 归档建不起来不能拖垮采集,历史缺失要能看见但不致命
                    return null;
                }
            })
        {
            Interval = TimeSpan.FromSeconds (1)
        };

        // 释放流水线前先等闸门里在途的写帧收尾。
        // 每次调用都重新读 _gate:闸门可能被重建(换设备/重读组态)。
        _coordinator.BeforeRelease = async timeout =>
        {
            var gate = _gate;

            if (gate is null) return true;

            return await gate.WaitForWritesAsync (timeout).ConfigureAwait (true);
        };

        // 报警落库在采集线程里发生:这里只做仓储写入,不碰界面
        _coordinator.AlarmSink = events =>
        {
            if (_alarmRepo is null) return 0;

            try
            {
                var n = _alarmRepo.WriteEvents (events.ToList());

                _lastAlarmWrite = DateTime.Now;
                _alarmTotalWritten += n;

                return n;
            }
            catch (Exception ex)
            {
                Dispatcher.BeginInvoke (new Action (() =>
                {
                    FooterText.Text = "报警落库失败: " + ex.Message;
                }));

                return 0;
            }
        };

        return _coordinator;
    }

    /// <summary>
    /// 为指定设备取流水线并**只启动这一台**。
    ///
    /// 早先是 coord.Build(_deviceList) + StartAll():配置里几台就同时采几台。
    /// 现场要的是"上位机盯哪台就只跟哪台说话" ——
    ///   · 一条 485 总线上挂多台时并发轮询会互相打断、大面积超时;
    ///   · 停机检修的那台没人看,却还在后台反复超时刷错误;
    ///   · 采集对象必须由操作员在下拉框里明确指定,不能悄悄并发。
    ///
    /// 所以:只保留这一台的流水线,其余全部停掉并释放(dropMissing:true 由 Build 负责)。
    /// </summary>
    /// <summary>
    /// 同上,但释放旧流水线在**线程池**上做,不冻结界面。
    ///
    /// 这是切设备/重读组态/卡死看门狗共用的路径:旧流水线的 StopAndWait 最长 2 秒,
    /// 而掉线时看门狗会连续触发 —— 同步释放会让界面"一秒一卡"。
    /// </summary>
    private async Task<DevicePipeline?> EnsurePipelineAsync (DeviceConfig device)
    {
        var coord = EnsureCoordinator ();

        // 先把旧的**全部**停掉并释放,再建这一台。
        //
        // 为什么不能复用:通讯中断后采集循环可能卡死在一次同步读里(串口句柄失效、
        // 设备不回帧),此时 IsRunning 仍是 true、State 仍停在"正常",
        // 复用这条流水线等于**永远不会恢复**。
        // 顺序不能改:新连接必须在旧串口真的放手之后才开,否则抢同一个 COM 口必失败。
        await coord.DropAllAsync ().ConfigureAwait (true);

        coord.Build (new[] { device }, null, dropMissing: true);

        var pipeline = coord.Find (device.DeviceName);

        pipeline?.Start ();

        return pipeline;
    }

    /// <summary>强制重建当前设备的流水线(卡死看门狗与「重新连接」共用)。</summary>
    private async Task RebuildCurrentPipelineAsync (string reason)
    {
        var device = _device;

        if (device is null) return;

        if (_closing) return;

        DiagUi ("pipeline rebuild: " + reason);

        // 重建前先摘掉 Writer:在途写入不能打到即将释放的旧服务上
        if (_gate is not null) _gate.Writer = null;

        DevicePipeline? pipeline;

        try
        {
            pipeline = await EnsurePipelineAsync (device).ConfigureAwait (true);
        }
        catch (Exception ex)
        {
            FooterText.Text = "重建采集失败: " + ex.Message;

            return;
        }

        if (_closing || !ReferenceEquals (_device, device)) return;

        _pipeline = pipeline;
        _svc = pipeline?.Service;
        _alarm = pipeline?.Alarm;
        _archive = pipeline?.Archive;

        if (pipeline is not null)
        {
            _bus = pipeline.Bus;
        }

        // 闸门持有的是具体服务实例,重建后必须同步,否则写入还打到旧服务。
        // 重建失败(pipeline 为 null)时保持 null —— 宁可让写入被闸门拒绝,
        // 也不能继续指向已经释放的服务。
        if (_gate is not null) _gate.Writer = _svc as IDeviceWriter;

        _lastRounds = -1;
        _lastBusVersion = -1;
        _stuckSince = DateTime.MinValue;
    }

    /// <summary>
    /// 没选设备时的界面状态:停掉所有采集,所有页面清空,提示去下拉框选一台。
    /// </summary>
    /// <summary>
    /// 同上,但释放旧流水线不占界面线程。
    ///
    /// 早先是同步 Build(dropMissing:true),内部 Dispose 最长等 2 秒 —— 界面冻结;
    /// 更糟的是如果同时有异步重建正在 await,那条流水线已经不在字典里,
    /// 同步 Build 根本碰不到它,于是留下一条"没人持有、不显示、不停止"的幽灵流水线
    /// 一直占着 COM 口。现在统一走 DropAllAsync(它按代际清空,能覆盖这种情况)。
    /// </summary>
    private async Task ApplyNoDeviceAsync ()
    {
        _device = null;

        // 停掉全部流水线(含它们持有的串口/TCP 连接),释放放到线程池
        _coordinator?.StopAll ();

        if (_coordinator is not null)
        {
            await _coordinator.DropAllAsync ().ConfigureAwait (true);
        }

        _pipeline = null;
        _svc = null;
        _alarm = null;
        _archive = null;

        // 闸门立即失效:没选设备就不该还能下发命令
        if (_gate is not null)
        {
            _gate.Writer = null;

            SetGateEnabled (false);
        }

        _rows.Clear ();
        _writeRows.Clear ();
        _jogRows.Clear ();

        if (ValueGrid is not null) ValueGrid.ItemsSource = null;
        if (WriteList is not null) WriteList.ItemsSource = null;
        if (JogGrid is not null) JogGrid.ItemsSource = null;

        _trendAllVars.Clear ();
        _liveAllVars.Clear ();

        DeviceText.Text = "未选择设备";
        StatusText.Text = "未选择设备";
        StatusText.Foreground = System.Windows.Media.Brushes.Goldenrod;

        // 只有真的还是"未选设备"才写引导:否则会覆盖 OnLoaded 刚拼好的配置摘要
        // (异步释放的延续比同步的摘要拼接晚落地)。
        if (_device is null && !_closing)
        {
            FooterText.Text = "请在上方「设备」下拉框里选择要监控的设备 —— 选择会记住,下次启动自动恢复。";
        }

        if (AlarmPanel is not null) AlarmPanel.Visibility = Visibility.Collapsed;
    }

    /// <summary>
    /// 打开报警库与历史库(整个进程只开一次)。
    ///
    /// 报警状态机与归档器本身不在这里建 —— 它们按**设备**各建一份,
    /// 由 <see cref="EnsureCoordinator"/> 注入到各条流水线里。
    /// 库只有一份,多设备共用:报警行少、历史行多,分两个文件互不影响。
    /// </summary>
    private void EnsureDatabases ()
    {
        if (_alarmRepo is null)
        {
            try
            {
                var dbDir = Path.Combine (AppContext.BaseDirectory, "data");
                Directory.CreateDirectory (dbDir);

                _alarmRepo = new AlarmRepository (Path.Combine (dbDir, "alarms.db"));
                _alarmRepo.EnsureSchema ();
            }
            catch
            {
                // 报警库打不开不能拖垮监控,状态机照常工作,只是不落库
                _alarmRepo = null;
            }
        }

        if (_historyRepo is null)
        {
            try
            {
                var dbDir = Path.Combine (AppContext.BaseDirectory, "data");
                Directory.CreateDirectory (dbDir);

                _historyRepo = new HistoryRepository (Path.Combine (dbDir, "history.db"));
                _historyRepo.EnsureSchema ();
            }
            catch (Exception ex)
            {
                _historyRepo = null;
                FooterText.Text = "历史库打不开,趋势/报表不可用: " + ex.Message;
            }
        }

        // WAL 没启用成功要让现场看得见:它不会让功能立刻坏掉,
        // 但会在"归档线程写、界面/MCP 读"并发时表现为偶发写库失败。
        var walError = HistoryRepository.LastWalError;

        if (walError is not null)
        {
            DiagUi ("history db wal: " + walError);

            if (!_closing) FooterText.Text = "历史库警告: " + walError;
        }

        // MCP 只尝试启动一次:
        // EnsureDatabases 在 OnLoaded 与每次 ApplyDevice 都会被调到,
        // 每次都 StartMcp 会重复写页脚、重复起后台 Task(Kestrel 自身幂等,
        // 不会起两个监听,但界面提示会被反复覆盖)。
        if (!_mcpAttempted)
        {
            _mcpAttempted = true;

            StartMcp ();
        }
    }

    /// <summary>内嵌 MCP 是否已经尝试启动过(本进程内只试一次)。</summary>
    private bool _mcpAttempted;

    // ==================== 内嵌 MCP ====================

    /// <summary>
    /// 起内嵌 MCP(外部 AI 客户端读实时/历史数据,并在授权下写值)。
    /// 写回调每次都重新取当前的 CommandGate —— 换设备时闸门会被重建成新的。
    /// </summary>
    private void StartMcp ()
    {
        var settingsDir = Path.Combine (AppContext.BaseDirectory, "settings");

        var dbPath = Path.Combine (AppContext.BaseDirectory, "data", "history.db");

        // 开关在「系统设置」里;关掉时传 0(= 不启动),但端口值保留在 ini 里
        var port = _appSettings.McpEnabled ? _appSettings.McpPort : 0;

        if (port <= 0)
        {
            McpHost.Start (settingsDir, dbPath, 0, McpWrite);

            return;
        }

        // 启动要等 Kestrel 真正绑上端口(最多 10 秒),不能拿界面线程去等:
        // 端口被占用时那 10 秒会表现为"程序卡死"。启动期间界面照常可用。
        //
        // 页脚是**单行**的,谁最后写谁显示。启动完成是异步回来的,
        // 若无条件覆盖,会把同时写下的"请选择设备"这类操作引导顶掉
        // (首启实测:页脚只剩 MCP 状态,用户看不到该干什么)。
        // 所以:启动成功**不动页脚**(状态在「系统设置」里可查),只有失败才占用它。
        FooterText.Text = "MCP 正在启动…(token 文件:data\\mcp_token.txt)";

        Task.Run (() =>
        {
            McpHost.Start (settingsDir, dbPath, port, McpWrite);

            var status = McpHost.Status;

            var failed = status.StartsWith ("启动失败", StringComparison.Ordinal);

            Dispatcher.BeginInvoke (new Action (() =>
            {
                if (failed)
                {
                    FooterText.Text = "MCP " + status;

                    return;
                }

                // 只有在"页脚还是自己写的启动中"时才回填,避免盖掉别处的最新提示
                if (FooterText.Text == "MCP 正在启动…")
                {
                    FooterText.Text = "MCP " + status;
                }
                else
                {
                    DiagUi ("MCP " + status);
                }
            }));
        });
    }

    /// <summary>
    /// 「系统设置」里改了 MCP 端口或开关后按新设置重启。
    /// 先 Stop 再 Start:端口要释放,不能两个实例抢同一个口。
    /// </summary>
    private void RestartMcp ()
    {
        var settingsDir = Path.Combine (AppContext.BaseDirectory, "settings");

        var dbPath = Path.Combine (AppContext.BaseDirectory, "data", "history.db");

        var port = _appSettings.McpEnabled ? _appSettings.McpPort : 0;

        FooterText.Text = "MCP 正在按新设置重启…";

        // 在后台线程上停+起:ASP.NET 关停会阻塞几秒,不能拿界面线程去等
        Task.Run (() =>
        {
            McpHost.Stop ();

            McpHost.Start (settingsDir, dbPath, port, McpWrite);

            var status = McpHost.Status;

            Dispatcher.BeginInvoke (new Action (() =>
            {
                FooterText.Text = "MCP " + status;
            }));
        });
    }

    /// <summary>
    /// MCP 写回调。必须回到界面线程 —— CommandGate 与采集服务都在界面线程里建,
    /// 而且写入要串行、要按最小间隔排队,不能从 HTTP 线程直接碰。
    /// </summary>
    private string McpWrite (string varName, double value)
    {
        try
        {
            // 在**调用线程**(MCP 的 HTTP 线程)上做,不要 Dispatcher.Invoke:
            // 写入可能等一次完整的设备往返(断线时数秒),放进 UI 线程就是整窗冻结,
            // 而 CommandGate.Write 的注释本来就写着"不要在界面线程上调用"。
            // 只有最后更新文本框那一步才回 UI。
            return WriteFromMcpThread (varName, value);
        }
        catch (Exception ex)
        {
            return "失败:" + ex.Message;
        }
    }

    private string WriteFromMcpThread (string varName, double value)
    {
        try
        {
            {
                if (_gate is null) return "拒绝:写闸门尚未构建(请先打开「参数设置」页)。";

                if (!_gate.Enabled)
                {
                    return "拒绝:写入未启用。请登录具备「参数设置」权限的账号,并在「参数设置」页勾选「允许写入」。";
                }

                // 远程写入**不弹确认**(没人在这儿点"是"),所以先带 confirmed:false 问闸门:
                // 值危险或无法核实时它回 NeedsConfirm,这里直接拒 ——
                // 外部 AI 一句 write_variable("X", 5000) 不能绕过校验直发 PLC。
                var outcome = _gate.Write (varName, value, "MCP");

                var refused = "";

                if (outcome.Verdict == WriteVerdict.NeedsConfirm)
                {
                    if (outcome.BlocksRemote)
                    {
                        // 两类必须拒:值越界(哪都不能发)、无法核实(读不到当前值,没人看着不能凭猜下发)
                        var why = outcome.Unverifiable ? "当前值不可核实" : "超出写入范围";

                        refused = "拒绝:远程写入" + why + "(未下发任何数据)。"
                            + "请在「参数设置」页人工确认后写入。详情:" + outcome.Message;
                    }
                    else
                    {
                        // 仅"与当前值不同"是**正常操作** —— 远程改设定值就是干这个的。
                        // 这里显式重发一次表示"确认过了";早先一律拒绝等于把远程写废掉。
                        outcome = _gate.Write (varName, value, "MCP", null, confirmed: true);
                    }
                }

                // 远程写入必须让操作员在界面上看得见:谁写的、写了什么、成没成
                var verdict = refused.Length > 0
                    ? refused
                    : outcome.Success ? "成功" : "失败:" + outcome.Message;

                var line = "MCP 远程写入 " + NameWithComment (varName, CommentsOf (varName)) + " = " + value
                    + " → " + verdict;

                if (WriteResultText is not null) WriteResultText.Text = line;

                if (refused.Length > 0) return refused;

                return outcome.Success
                    ? "OK " + varName + " = " + value + "(" + outcome.Message + ")"
                    : "拒绝:" + outcome.Message;
            }
        }
        catch (Exception ex)
        {
            return "失败:" + ex.Message;
        }
    }

    // ==================== 历史趋势(多曲线 + 交互) ====================

    /// <summary>曲线配色。暗色背景上辨识度高,相邻颜色差别大。</summary>
    private static readonly System.Windows.Media.Color[] TrendColors =
    {
        System.Windows.Media.Color.FromRgb (0x89, 0xB4, 0xFA), // 蓝
        System.Windows.Media.Color.FromRgb (0xA6, 0xE3, 0xA1), // 绿
        System.Windows.Media.Color.FromRgb (0xFA, 0xB3, 0x87), // 橙
        System.Windows.Media.Color.FromRgb (0xF3, 0x8B, 0xA8), // 粉
        System.Windows.Media.Color.FromRgb (0xCB, 0xA6, 0xF7), // 紫
        System.Windows.Media.Color.FromRgb (0x94, 0xE2, 0xD5), // 青
        System.Windows.Media.Color.FromRgb (0xF9, 0xE2, 0xAF), // 黄
        System.Windows.Media.Color.FromRgb (0xEB, 0xA0, 0xAC), // 红粉
    };

    /// <summary>左侧全部可选变量(未过滤)。</summary>
    private readonly List<TrendSelectItem> _trendAllVars = new List<TrendSelectItem> ();

    /// <summary>最近一次查询是否成功过。</summary>
    private bool _trendQueried;

    /// <summary>
    /// 初始化趋势页:填充变量多选列表。只列归档变量 —— 没归档的查了也是空。
    /// </summary>
    private void InitTrend ()
    {
        _trendAllVars.Clear ();

        var idx = 0;

        foreach (var g in _device.GroupList)
        {
            if (g.VarList is null) continue;

            foreach (var v in g.VarList)
            {
                if (string.IsNullOrEmpty (v.VarName)) continue;
                if (!v.ArchiveEnable) continue;

                _trendAllVars.Add (new TrendSelectItem
                {
                    Name = v.VarName,
                    Comments = v.Comments ?? "",
                    Unit = v.Unit ?? "",
                    Color = TrendColors[idx % TrendColors.Length]
                });

                idx++;
            }
        }

        ApplyTrendFilter ("");

        if (TrendRangeBox.SelectedIndex < 0) TrendRangeBox.SelectedIndex = 1; // 默认最近 1 小时
        if (TrendResBox.SelectedIndex < 0) TrendResBox.SelectedIndex = 0;   // 默认自动
        if (TrendAggBox.SelectedIndex < 0) TrendAggBox.SelectedIndex = 0;   // 默认平均值
        if (TrendModeBox.SelectedIndex < 0) TrendModeBox.SelectedIndex = 0; // 默认叠加

        LoadTemplates ();

        // 默认选前 3 个,让人一打开就能看到东西
        if (TrendVarList.Items.Count > 0)
        {
            var take = Math.Min (3, TrendVarList.Items.Count);
            for (int i = 0; i < take; i++) TrendVarList.SelectedItems.Add (TrendVarList.Items[i]);
        }

        UpdateTrendSelectedText ();
    }

    /// <summary>按关键字过滤变量列表(搜不到就显示全部)。</summary>
    private void ApplyTrendFilter (string keyword)
    {
        var selected = new HashSet<string> (StringComparer.OrdinalIgnoreCase);
        foreach (var item in TrendVarList.SelectedItems) { if (item is TrendSelectItem t) selected.Add (t.Name); }

        TrendVarList.Items.Clear ();

        foreach (var v in _trendAllVars)
        {
            if (!string.IsNullOrEmpty (keyword) &&
                v.Name.IndexOf (keyword, StringComparison.OrdinalIgnoreCase) < 0)
            {
                continue;
            }

            TrendVarList.Items.Add (v);

            if (selected.Contains (v.Name)) TrendVarList.SelectedItems.Add (v);
        }
    }

    private void OnTrendSearchChanged (object sender, TextChangedEventArgs e)
    {
        ApplyTrendFilter (TrendSearchBox.Text ?? "");
    }

    private void OnTrendVarSelectionChanged (object sender, SelectionChangedEventArgs e)
    {
        UpdateTrendSelectedText ();
    }

    private void UpdateTrendSelectedText ()
    {
        var n = TrendVarList.SelectedItems.Count;
        TrendSelectedText.Text = n == 0 ? "未选择" : ("已选 " + n + " 个变量");
    }

    private void OnTrendSelectAll (object sender, RoutedEventArgs e)
    {
        TrendVarList.SelectAll ();
        UpdateTrendSelectedText ();
    }

    /// <summary>清空变量勾选(左侧列表)。</summary>
    private void OnTrendClearVarSelection (object sender, RoutedEventArgs e)
    {
        TrendVarList.UnselectAll ();
        UpdateTrendSelectedText ();
    }

    // ==================== 分析模板 ====================

    private List<TrendTemplate> _templates = new List<TrendTemplate> ();

    /// <summary>模板文件路径(与数据同目录)。</summary>
    private static string TemplateFile
    {
        get
        {
            var dir = Path.Combine (AppContext.BaseDirectory, "data");
            Directory.CreateDirectory (dir);

            return Path.Combine (dir, "trend_templates.ini");
        }
    }

    /// <summary>防止应用模板时逐个控件触发多次查询。</summary>
    private bool _applyingTemplate;

    private void LoadTemplates ()
    {
        try
        {
            _templates = File.Exists (TemplateFile)
                ? TrendTemplateStore.Parse (File.ReadAllText (TemplateFile, Encoding.UTF8))
                : new List<TrendTemplate> ();
        }
        catch
        {
            _templates = new List<TrendTemplate> ();
        }

        RefreshTemplateCombo ();
    }

    private void RefreshTemplateCombo ()
    {
        _applyingTemplate = true;

        var keep = TrendTemplateBox.SelectedItem as string;

        TrendTemplateBox.Items.Clear ();

        foreach (var t in _templates) TrendTemplateBox.Items.Add (t.Name);

        if (keep is not null && TrendTemplateBox.Items.Contains (keep))
        {
            TrendTemplateBox.SelectedItem = keep;
        }

        _applyingTemplate = false;
    }

    private void SaveTemplates ()
    {
        try
        {
            File.WriteAllText (TemplateFile, TrendTemplateStore.Serialize (_templates), Encoding.UTF8);
        }
        catch (Exception ex)
        {
            TrendStatsText.Text = "模板保存失败: " + ex.Message;
        }
    }

    /// <summary>选中模板即应用:勾选变量 + 恢复时间范围/分辨率/聚合/各开关。</summary>
    private void OnTrendTemplateSelected (object sender, SelectionChangedEventArgs e)
    {
        if (_applyingTemplate) return;

        var name = TrendTemplateBox.SelectedItem as string;
        if (string.IsNullOrEmpty (name)) return;

        var t = _templates.FirstOrDefault (x => x.Name == name);
        if (t is null) return;

        _applyingTemplate = true;

        try
        {
            // 变量勾选:先清再按名单勾(名单里没有的变量就不选)
            TrendVarList.UnselectAll ();

            var wanted = new HashSet<string> (t.Variables, StringComparer.OrdinalIgnoreCase);

            foreach (var obj in TrendVarList.Items)
            {
                if (obj is TrendSelectItem item && wanted.Contains (item.Name))
                {
                    TrendVarList.SelectedItems.Add (item);
                }
            }

            if (t.RangeIndex >= 0 && t.RangeIndex < TrendRangeBox.Items.Count)
                TrendRangeBox.SelectedIndex = t.RangeIndex;

            if (t.ResolutionIndex >= 0 && t.ResolutionIndex < TrendResBox.Items.Count)
                TrendResBox.SelectedIndex = t.ResolutionIndex;

            if (t.AggregateIndex >= 0 && t.AggregateIndex < TrendAggBox.Items.Count)
                TrendAggBox.SelectedIndex = t.AggregateIndex;

            ShowEnvelopeBox.IsChecked = t.ShowEnvelope;
            ShowLimitsBox.IsChecked = t.ShowLimits;
            ShowEventsBox.IsChecked = t.ShowEvents;

            TrendChart.ShowEnvelope = t.ShowEnvelope;
            TrendChart.ShowLimits = t.ShowLimits;
            TrendChart.ShowEvents = t.ShowEvents;

            TrendTemplateName.Text = t.Name;
        }
        finally
        {
            _applyingTemplate = false;
        }

        UpdateTrendSelectedText ();

        // 统一查一次,避免上面改控件触发一串查询
        RunTrendQuery ();
    }

    /// <summary>保存当前状态为模板。同名覆盖。</summary>
    private void OnTrendTemplateSave (object sender, RoutedEventArgs e)
    {
        var name = TrendTemplateStore.SanitizeName (TrendTemplateName.Text);

        var vars = TrendVarList.SelectedItems
            .OfType<TrendSelectItem> ()
            .Select (x => x.Name)
            .ToList ();

        if (vars.Count == 0)
        {
            TrendStatsText.Text = "请先选择变量再保存模板";
            return;
        }

        var t = new TrendTemplate (
            name, vars,
            TrendRangeBox.SelectedIndex,
            TrendResBox.SelectedIndex,
            TrendAggBox.SelectedIndex,
            ShowEnvelopeBox.IsChecked == true,
            ShowLimitsBox.IsChecked == true,
            ShowEventsBox.IsChecked == true);

        // 同名覆盖,否则追加
        var idx = _templates.FindIndex (x => x.Name == name);

        if (idx >= 0) _templates[idx] = t;
        else _templates.Add (t);

        SaveTemplates ();
        RefreshTemplateCombo ();

        TrendTemplateBox.SelectedItem = name;
        TrendStatsText.Text = "已保存模板「" + name + "」(" + vars.Count + " 个变量)";
    }

    private void OnTrendTemplateDelete (object sender, RoutedEventArgs e)
    {
        var name = TrendTemplateBox.SelectedItem as string;

        if (string.IsNullOrEmpty (name))
        {
            TrendStatsText.Text = "请先选择一个模板";
            return;
        }

        _templates.RemoveAll (x => x.Name == name);

        SaveTemplates ();
        RefreshTemplateCombo ();

        TrendStatsText.Text = "已删除模板「" + name + "」";
    }

    /// <summary>清除图上的框选区间。</summary>
    private void OnTrendClearChartSelection (object sender, RoutedEventArgs e)
    {
        TrendChart.ClearSelection ();
    }

    /// <summary>
    /// 时间范围下拉 → 实际区间。
    /// 前 5 项是相对跨度,后面是工艺语义时段(今天/昨天/本周/上周/本月/上月)——
    /// 现场最常用的是"上个班""今天""昨天",而不是精确 6 小时。
    /// 周起点按国内习惯取周一。
    /// </summary>
    private (DateTime from, DateTime to) ResolveTrendRange ()
    {
        var now = DateTime.Now;

        switch (TrendRangeBox.SelectedIndex)
        {
            case 0: return (now.AddMinutes (-10), now);
            case 2: return (now.AddHours (-6), now);
            case 3: return (now.AddHours (-24), now);
            case 4: return (now.AddDays (-7), now);

            // 今天:零点到现在
            case 5: return (now.Date, now);

            // 昨天:昨天零点到昨天最后一刻
            case 6:
            {
                var y = now.Date.AddDays (-1);
                return (y, y.AddDays (1).AddSeconds (-1));
            }

            // 本周:本周一零点到现在
            case 7: return (StartOfWeek (now), now);

            // 上周:上周一到上周日末
            case 8:
            {
                var thisMon = StartOfWeek (now);
                var lastMon = thisMon.AddDays (-7);
                return (lastMon, thisMon.AddSeconds (-1));
            }

            // 本月:1 号零点到现在
            case 9: return (new DateTime (now.Year, now.Month, 1), now);

            // 上月:上月 1 号到上月最后一天末
            case 10:
            {
                var firstThis = new DateTime (now.Year, now.Month, 1);
                var firstLast = firstThis.AddMonths (-1);
                return (firstLast, firstThis.AddSeconds (-1));
            }

            default: return (now.AddHours (-1), now);
        }
    }

    /// <summary>本週起点(周一)。国内习惯周一为一周第一天,不是周日。</summary>
    private static DateTime StartOfWeek (DateTime d)
    {
        var diff = (int)d.DayOfWeek - (int)DayOfWeek.Monday;
        if (diff < 0) diff += 7;

        return d.Date.AddDays (-diff);
    }

    private void OnTrendRangeChanged (object sender, SelectionChangedEventArgs e)
    {
        // 范围改了自动重查,少点一次按钮
        if (_historyRepo is null) return;
        if (!IsLoaded) return;
        if (TrendVarList.SelectedItems.Count == 0) return;

        RunTrendQuery ();
    }

    private void OnTrendQuery (object sender, RoutedEventArgs e)
    {
        RunTrendQuery ();
    }

    /// <summary>查询并绘制所有勾选的变量。</summary>
    private void RunTrendQuery ()
    {
        _ = RunTrendQueryAsync ();
    }

    /// <summary>后台查询用的中间结果。</summary>
    private sealed record TrendBuildResult(
        List<TrendSeries> Series, int TotalRaw, int TotalBuckets, double Interval);

    private int _trendGen;

    /// <summary>
    /// 异步查询。大区间(7 天)同步查会把 UI 卡住几秒,所以放后台线程。
    ///
    /// 用**代次号**处理并发:用户快速切范围时会有多个查询在跑,
    /// 只有最后一次的结果允许上屏,旧结果直接丢弃。
    /// </summary>
    private async Task RunTrendQueryAsync ()
    {
        if (_historyRepo is null)
        {
            TrendStatsText.Text = "历史库不可用";
            TrendChart.Clear ();
            return;
        }

        var selected = TrendVarList.SelectedItems
            .OfType<TrendSelectItem> ()
            .ToList ();

        if (selected.Count == 0)
        {
            TrendStatsText.Text = "请在左侧选择变量";
            TrendChart.Clear ();
            return;
        }

        var (from, to) = ResolveTrendRange ();

        var resolution = ResFromIndex (TrendResBox.SelectedIndex);
        var aggregate = TrendAggBox.SelectedIndex >= 0
            ? (TrendAggregate)TrendAggBox.SelectedIndex
            : TrendAggregate.Average;

        var gen = ++_trendGen;
        var sw = System.Diagnostics.Stopwatch.StartNew ();

        TrendStatsText.Text = "正在查询 " + selected.Count + " 条曲线……";

        var cts = new CancellationTokenSource ();
        _trendCts = cts;

        TrendCancelBtn.IsEnabled = true;

        try
        {
            // 数据库读取 + 聚合都在后台线程
            var token = cts.Token;

            var built = await Task.Run (() => BuildTrendSeries (selected, from, to, resolution, aggregate, token), token);

            // 已经有更新的查询发起了,本次结果作废
            if (gen != _trendGen) return;

            TrendChart.SetSeries (built.Series);
            _trendSeriesCache = built.Series;
            _trendQueried = true;

            // 报警事件竖线:和曲线用同一个时间范围
            LoadTrendEvents (from, to);

            sw.Stop ();

            // usedInterval=0 表示没聚合(点数少,走原始点)
            var resText = built.Interval <= 0
                ? "原始"
                : TrendAggregator.ResolutionName (FromInterval (built.Interval));

            TrendInfoText.Text = selected.Count + " 条曲线  " + built.TotalBuckets + " 点  "
                + (to - from).TotalMinutes.ToString ("F0") + " 分钟"
                + "  ·  " + resText + " / " + TrendAggregator.AggregateName (aggregate)
                + "  ·  " + sw.ElapsedMilliseconds + " ms";

            // 原始点远多于桶数时,状态栏注明降采样
            _trendDownsampled = built.TotalRaw > built.TotalBuckets * 1.5
                && resolution != TrendResolution.Raw;

            UpdateTrendStats ();

            if (built.TotalBuckets == 0)
            {
                TrendStatsText.Text = "该时间范围内没有归档数据("
                    + from.ToString ("MM-dd HH:mm") + " ~ " + to.ToString ("MM-dd HH:mm") + ")";
            }

            // 原始数据表开着就同步刷新
            if (ShowRawBox.IsChecked == true) BuildRawTable ();
        }
        catch (OperationCanceledException)
        {
            if (gen == _trendGen) TrendStatsText.Text = "查询已取消";
        }
        catch (Exception ex)
        {
            if (gen == _trendGen)
            {
                TrendStatsText.Text = "查询失败: " + ex.Message;
                TrendChart.Clear ();
            }
        }
        finally
        {
            if (gen == _trendGen)
            {
                TrendCancelBtn.IsEnabled = false;
                _trendCts = null;
            }

            cts.Dispose ();
        }
    }

    /// <summary>在后台线程组装曲线数据(不含任何 UI 操作)。</summary>
    private TrendBuildResult BuildTrendSeries (
        List<TrendSelectItem> selected, DateTime from, DateTime to,
        TrendResolution resolution, TrendAggregate aggregate,
        CancellationToken token)
    {
        var series = new List<TrendSeries> (selected.Count);

        var totalRaw = 0;
        var totalBuckets = 0;
        var usedInterval = 0.0;

        foreach (var item in selected)
        {
            // 每查完一条检查一次取消:SQL 本身没法中途打断,
            // 但能立刻停掉剩下的曲线,不让用户白等
            token.ThrowIfCancellationRequested ();

            // 多取一些原始点:聚合前不能先截断,否则尾部数据会丢
            var raw = _historyRepo.Query (item.Name, from, to, 200000);

            var buckets = TrendAggregator.Process (
                raw, from, to, resolution, aggregate, out var interval);

            if (interval > usedInterval) usedInterval = interval;

            var cfg = FindVarConfig (item.Name);

            series.Add (new TrendSeries
            {
                VarName = item.Name,
                Comments = item.Comments,
                Unit = item.Unit,
                Color = item.Color,
                Points = buckets,
                HighLimit = cfg is not null && cfg.HighAlarmEnable ? cfg.HighAlarmValue : 0,
                LowLimit = cfg is not null && cfg.LowAlarmEnable ? cfg.LowAlarmValue : 0,
                // 开关量画阶跃线,画折线会让人以为状态是渐变的
                IsDigital = cfg is not null && IsDigitalType (cfg.VarType)
            });

            totalRaw += raw.Count;
            totalBuckets += buckets.Count (b => !b.IsEmpty);
        }

        return new TrendBuildResult (series, totalRaw, totalBuckets, usedInterval);
    }

    /// <summary>
    /// 更新底部统计(按当前视图窗口算,缩放后会变)。
    ///
    /// 最小值/最大值取**包络的上下界**,不是取代表值 —— 否则聚合后看到的
    /// "最大 20" 可能掩盖了这段时间里真实到过 200 的尖峰。
    /// </summary>
    private void UpdateTrendStats ()
    {
        if (!TrendChart.HasData)
        {
            TrendStatsText.Text = "";
            return;
        }

        // 有框选时优先显示区间统计(这是"分析"而不是"看")
        if (_selFrom is not null && _selTo is not null)
        {
            TrendStatsText.Text = BuildRangeStats (_selFrom.Value, _selTo.Value);
            return;
        }

        var (vs, ve) = TrendChart.ViewRange;

        var parts = new List<string> ();
        parts.Add ("视图 " + vs.ToString ("MM-dd HH:mm:ss") + " ~ " + ve.ToString ("HH:mm:ss"));

        foreach (var s in _trendSeriesCache)
        {
            var visible = s.Points
                .Where (p => !p.IsEmpty && p.Time >= vs && p.Time <= ve)
                .ToList ();

            if (visible.Count == 0)
            {
                parts.Add (NameWithComment (s.VarName, s.Comments) + " 无数据");
                continue;
            }

            // 包络优先:真实极值
            var lo = visible.Min (p => double.IsNaN (p.Min) ? p.Value : p.Min);
            var hi = visible.Max (p => double.IsNaN (p.Max) ? p.Value : p.Max);
            var avg = visible.Average (p => p.Value);
            var unit = string.IsNullOrEmpty (s.Unit) ? "" : s.Unit;

            parts.Add (NameWithComment (s.VarName, s.Comments) + ": " + visible.Count + "点, 最小 " + lo.ToString ("G5")
                + ", 最大 " + hi.ToString ("G5")
                + ", 平均 " + avg.ToString ("G5") + (unit.Length > 0 ? " " + unit : ""));
        }

        if (_trendDownsampled) parts.Add ("已降采样");

        TrendStatsText.Text = string.Join ("    |    ", parts);
    }

    /// <summary>
    /// 框选区间统计。每曲线一行,12 项。
    ///
    /// 超限/合格时长按配置的报警上下限算;没配限值的曲线这两项显示 "--"。
    /// </summary>
    private string BuildRangeStats (DateTime from, DateTime to)
    {
        var lines = new List<string> ();

        var dur = to - from;
        lines.Add ("【区间统计】" + from.ToString ("MM-dd HH:mm:ss") + " ~ " + to.ToString ("HH:mm:ss")
            + "  跨度 " + TrendStatsCalculator.FormatDuration (dur.TotalSeconds));

        foreach (var s in _trendSeriesCache)
        {
            var cfg = FindVarConfig (s.VarName);

            double? hi = cfg is not null && cfg.HighAlarmEnable ? cfg.HighAlarmValue : null;
            double? lo = cfg is not null && cfg.LowAlarmEnable ? cfg.LowAlarmValue : null;

            var st = TrendStatsCalculator.Compute (s.Points, from, to, hi, lo);

            var unit = string.IsNullOrEmpty (s.Unit) ? "" : s.Unit;

            if (!st.HasData)
            {
                lines.Add (s.VarName + ": 该区间无有效数据");
                continue;
            }

            var limitPart = (hi is not null || lo is not null)
                ? "覆盖 " + TrendStatsCalculator.FormatDuration (st.CoveredSeconds)
                  + " (超限 " + TrendStatsCalculator.FormatDuration (st.OverLimitSeconds)
                  + " / 合格 " + TrendStatsCalculator.FormatDuration (st.InLimitSeconds) + ")"
                  + " · 切换 " + st.SwitchCount + " 次"
                : "未配限值 · 覆盖 " + TrendStatsCalculator.FormatDuration (st.CoveredSeconds);

            // 框选跨度与覆盖时长差距大时提示:差额是没采到数据的空洞
            var gap = dur.TotalSeconds - st.CoveredSeconds;
            if (gap > Math.Max(60, dur.TotalSeconds * 0.1))
            {
                limitPart += " · 缺失 " + TrendStatsCalculator.FormatDuration (gap);

                // 再给明细:是"断了一次很久"还是"断了很多次"
                var threshold = TrendStatsCalculator.GapThreshold (s.Points);
                var gaps = TrendStatsCalculator.FindGaps (s.Points, from, to, threshold);

                if (gaps.Count > 0)
                {
                    var longest = gaps.Max (g => g.Seconds);

                    limitPart += " (" + gaps.Count + " 处, 最长 "
                        + TrendStatsCalculator.FormatDuration (longest) + ")";
                }
            }

            lines.Add (s.VarName
                + ": 有效 " + st.ValidCount + " 点"
                + " · 最小 " + st.Min.ToString ("G5")
                + " · 最大 " + st.Max.ToString ("G5")
                + " · 平均 " + st.Average.ToString ("G5")
                + " · 标准差 " + st.StdDev.ToString ("G4")
                + " · 首 " + st.First.ToString ("G5")
                + " · 末 " + st.Last.ToString ("G5")
                + " · 变化 " + st.Delta.ToString ("G5")
                + " · 积分 " + st.Integral.ToString ("G6")
                + (unit.Length > 0 ? " " + unit : "")
                + " · " + limitPart);
        }

        return string.Join (Environment.NewLine, lines);
    }

    /// <summary>原始点数远多于显示点数时为 true,用于状态栏提示"看到的不是全部原始点"。</summary>
    private bool _trendDownsampled;

    /// <summary>把秒间隔换算回 TrendResolution(用于显示"自动"实际选了哪一档)。</summary>
    private static TrendResolution FromInterval (double seconds)
    {
        if (Math.Abs (seconds - 1) < 0.01) return TrendResolution.S1;
        if (Math.Abs (seconds - 10) < 0.01) return TrendResolution.S10;
        if (Math.Abs (seconds - 30) < 0.01) return TrendResolution.S30;
        if (Math.Abs (seconds - 60) < 0.01) return TrendResolution.M1;
        if (Math.Abs (seconds - 300) < 0.01) return TrendResolution.M5;
        if (Math.Abs (seconds - 1800) < 0.01) return TrendResolution.M30;
        if (Math.Abs (seconds - 3600) < 0.01) return TrendResolution.H1;
        if (Math.Abs (seconds - 86400) < 0.01) return TrendResolution.D1;

        return TrendResolution.Auto;
    }

    /// <summary>当前图表里的曲线(统计用)。</summary>
    private List<TrendSeries> _trendSeriesCache = new List<TrendSeries> ();

    /// <summary>是否开关量(Bool/Byte)。开关量趋势必须画阶跃线。</summary>
    private static bool IsDigitalType (string? varType)
    {
        var t = (varType ?? "").Trim ().ToLowerInvariant ();

        return t is "bool" or "boolean" or "byte" or "sbyte";
    }

    /// <summary>按变量名找配置(取报警限值用)。</summary>
    private VariableConfig FindVarConfig (string varName)
    {
        foreach (var g in _device.GroupList)
        {
            if (g.VarList is null) continue;

            foreach (var v in g.VarList)
            {
                if (string.Equals (v.VarName, varName, StringComparison.OrdinalIgnoreCase))
                    return v;
            }
        }

        return null;
    }

    /// <summary>
    /// 变量名 → 组态中文描述。查不到返回 ""。
    ///
    /// 为什么搜**所有设备**而不是只看 _device:报警历史里可能混着别的设备的点位
    /// (切过设备之后旧报警还在列表里),只查当前设备会出现"这一段空白备注"。
    /// 结果缓存起来:报警表一次刷 1000 行,逐行线性搜索会把界面拖卡。
    /// </summary>
    private string CommentsOf (string varName)
    {
        if (string.IsNullOrWhiteSpace (varName)) return "";

        if (_commentCache.TryGetValue (varName, out var cached)) return cached;

        var text = "";

        foreach (var dev in _deviceList)
        {
            if (dev?.GroupList is null) continue;

            foreach (var g in dev.GroupList)
            {
                if (g.VarList is null) continue;

                foreach (var v in g.VarList)
                {
                    if (!string.Equals (v.VarName, varName, StringComparison.OrdinalIgnoreCase)) continue;

                    text = (v.Comments ?? "").Trim();

                    break;
                }
            }

            if (text.Length > 0) break;
        }

        _commentCache[varName] = text;

        return text;
    }

    /// <summary>变量名 → 备注 的缓存(组态重载时清空)。</summary>
    private readonly Dictionary<string, string> _commentCache = new (StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// "变量名(备注)" —— 界面上凡出现变量名的地方都该用它。
    ///
    /// 现场变量名几乎全是英文缩写,只写 LQB1_Fre_Set 等于没写;
    /// 但反过来只显示备注也不行:对配置、写脚本、报故障都要用变量名。
    /// 所以固定成"名字在前、备注在后"的一句话。
    /// </summary>
    private static string NameWithComment (string varName, string? comments)
    {
        var note = (comments ?? "").Trim();

        return note.Length == 0 ? varName : varName + "(" + note + ")";
    }

    private void OnTrendZoomIn (object sender, RoutedEventArgs e)
    {
        TrendChart.Zoom (0.7);
    }

    private void OnTrendZoomOut (object sender, RoutedEventArgs e)
    {
        TrendChart.Zoom (1.0 / 0.7);
    }

    private void OnTrendResetView (object sender, RoutedEventArgs e)
    {
        TrendChart.ResetView ();
    }

    /// <summary>显示/隐藏上下限线。默认不勾选。</summary>
    private void OnShowLimitsChanged (object sender, RoutedEventArgs e)
    {
        if (TrendChart is null) return;

        TrendChart.ShowLimits = ShowLimitsBox.IsChecked == true;
    }

    /// <summary>显示/隐藏最小最大包络带。默认勾选。</summary>
    private void OnShowEnvelopeChanged (object sender, RoutedEventArgs e)
    {
        if (TrendChart is null) return;

        TrendChart.ShowEnvelope = ShowEnvelopeBox.IsChecked == true;
    }

    /// <summary>显示/隐藏报警事件竖线。</summary>
    private void OnShowEventsChanged (object sender, RoutedEventArgs e)
    {
        if (TrendChart is null) return;

        TrendChart.ShowEvents = ShowEventsBox.IsChecked == true;
    }

    /// <summary>叠加 / 分屏切换。</summary>
    private void OnTrendModeChanged (object sender, SelectionChangedEventArgs e)
    {
        if (TrendChart is null) return;

        TrendChart.DisplayMode = TrendModeBox.SelectedIndex == 1
            ? TrendDisplayMode.Stacked
            : TrendDisplayMode.Overlay;
    }

    // ==================== 面板折叠 / 曲线最大化 ====================

    private bool _leftPanelHidden;
    private bool _trendMaximized;

    /// <summary>隐藏/显示左侧位号面板。</summary>
    private void OnTrendTogglePanel (object sender, RoutedEventArgs e)
    {
        _leftPanelHidden = !_leftPanelHidden;

        if (!_leftPanelHidden) _trendMaximized = false;

        ApplyTrendLayout ();
    }

    /// <summary>曲线最大化:同时收掉左面板、光标栏、底部统计。</summary>
    private void OnTrendToggleMaximize (object sender, RoutedEventArgs e)
    {
        _trendMaximized = !_trendMaximized;
        _leftPanelHidden = _trendMaximized;

        ApplyTrendLayout ();
    }

    private void ApplyTrendLayout ()
    {
        TrendLeftCol.Width = _leftPanelHidden ? new GridLength (0) : new GridLength (230);

        TrendLeftPanel.Visibility = _leftPanelHidden ? Visibility.Collapsed : Visibility.Visible;
        TrendPanelBtn.Content = _leftPanelHidden ? "显示面板" : "隐藏面板";

        // 最大化时连光标栏和底部统计一起收掉,把纵向空间全给曲线
        var hideExtras = _trendMaximized;

        TrendCursorBar.Visibility = hideExtras ? Visibility.Collapsed : Visibility.Visible;
        TrendBottomPanel.Visibility = hideExtras ? Visibility.Collapsed : Visibility.Visible;

        TrendMaxBtn.Content = _trendMaximized ? "还原布局" : "曲线最大化";
    }

    // ==================== 原始数据预览表 ====================

    private void OnShowRawChanged (object sender, RoutedEventArgs e)
    {
        var show = ShowRawBox.IsChecked == true;

        TrendRawPanel.Visibility = show ? Visibility.Visible : Visibility.Collapsed;

        if (show) BuildRawTable ();
    }

    /// <summary>
    /// 构建原始数据预览表。列 = 时间 + 各曲线;行以第一条曲线的时刻为基准,
    /// 其余曲线用最近点填充(与 CSV 导出同一套对齐逻辑)。
    /// 只放前 400 行:预览是为了核对,不是用来翻全量数据。
    /// </summary>
    private void BuildRawTable ()
    {
        const int MaxPreviewRows = 400;

        var table = new System.Data.DataTable ();

        table.Columns.Add ("时间", typeof (string));

        var series = new List<TrendCsvSeries> ();
        var colNames = new List<string> ();

        foreach (var s in _trendSeriesCache)
        {
            if (s.Points.Count == 0) continue;

            var col = string.IsNullOrEmpty (s.Unit) ? s.VarName : s.VarName + "(" + s.Unit + ")";

            // 重名时加后缀,避免 DataTable 列名冲突抛异常
            var unique = col;
            var n = 2;

            while (colNames.Contains (unique)) unique = col + "_" + n++;

            colNames.Add (unique);

            table.Columns.Add (unique, typeof (string));

            series.Add (new TrendCsvSeries (s.VarName, s.Unit, s.Points));
        }

        if (series.Count == 0)
        {
            TrendRawGrid.ItemsSource = null;
            TrendRawHeader.Text = "原始数据(无)";
            return;
        }

        // 基准时间轴 = 第一条有数据的曲线
        var basePts = series[0].Points;

        var tolerances = new double[series.Count];

        for (int i = 0; i < series.Count; i++)
        {
            var med = series[i].Points.Count > 1
                ? (series[i].Points[^1].Time - series[i].Points[0].Time).TotalSeconds / (series[i].Points.Count - 1)
                : 0;

            tolerances[i] = Math.Max (1, med * 2);
        }

        var rows = 0;

        foreach (var bp in basePts)
        {
            if (rows >= MaxPreviewRows) break;
            if (bp.IsEmpty || double.IsNaN (bp.Value)) continue;

            var row = table.NewRow ();

            row[0] = bp.Time.ToString ("MM-dd HH:mm:ss");

            for (int i = 0; i < series.Count; i++)
            {
                var v = i == 0 ? bp.Value : TrendCsv.TryGetAt (series[i].Points, bp.Time, tolerances[i]);

                row[i + 1] = v.HasValue
                    ? v.Value.ToString ("G6", System.Globalization.CultureInfo.InvariantCulture)
                    : "";
            }

            table.Rows.Add (row);
            rows++;
        }

        TrendRawGrid.ItemsSource = table.DefaultView;

        TrendRawHeader.Text = "原始数据 " + rows + " 行"
            + (basePts.Count > MaxPreviewRows ? "(仅显示前 " + MaxPreviewRows + " 行)" : "");
    }

    // ==================== 查询取消 ====================

    private CancellationTokenSource _trendCts;

    private void OnTrendCancelQuery (object sender, RoutedEventArgs e)
    {
        try
        {
            _trendCts?.Cancel ();

            TrendStatsText.Text = "已取消查询";
        }
        catch
        {
            // 取消失败不影响后续查询
        }
    }

    /// <summary>
    /// 加载查询区间内的报警触发事件,画成时间轴竖线,并填充底部事件列表。
    /// 只取触发事件(消除事件会让竖线数量翻倍,反而看不清)。
    /// </summary>
    private void LoadTrendEvents (DateTime from, DateTime to)
    {
        if (_alarmRepo is null)
        {
            TrendChart.SetEvents (Array.Empty<TrendEventMarker> ());
            BindEventList (new List<TrendEventRow> ());
            return;
        }

        try
        {
            var rows = _alarmRepo.Query (null, from, to, 500);

            var triggers = rows.Where (r => r.IsTrigger).ToList ();

            var markers = triggers
                .Select (r => new TrendEventMarker (
                    r.At,
                    NameWithComment (r.VarName, CommentsOf (r.VarName)) + (r.Direction == 0 ? " 上限" : " 下限") + "报警",
                    true))
                .ToList ();

            TrendChart.SetEvents (markers);

            // 列表按时间升序,便于顺着看
            var list = triggers
                .OrderBy (r => r.At)
                .Select (r => new TrendEventRow
                {
                    At = r.At,
                    TimeText = r.At.ToString ("HH:mm:ss"),
                    Text = NameWithComment (r.VarName, CommentsOf (r.VarName))
                           + (r.Direction == 0 ? " 上限" : " 下限")
                           + " (" + r.Value.ToString ("G5") + ")"
                })
                .ToList ();

            BindEventList (list);
        }
        catch
        {
            // 事件读不出来不影响曲线显示
            TrendChart.SetEvents (Array.Empty<TrendEventMarker> ());
            BindEventList (new List<TrendEventRow> ());
        }
    }

    private void BindEventList (List<TrendEventRow> rows)
    {
        TrendEventList.ItemsSource = rows;

        TrendEventHeader.Text = rows.Count == 0
            ? "报警事件(无)"
            : "报警事件 " + rows.Count + " 条(点击定位)";
    }

    /// <summary>点事件行 → 光标跳到该时刻。</summary>
    private void OnTrendEventSelected (object sender, SelectionChangedEventArgs e)
    {
        if (TrendEventList.SelectedItem is not TrendEventRow row) return;

        TrendChart.MoveCursorTo (row.At);
    }

    private DateTime? _selFrom;
    private DateTime? _selTo;

    /// <summary>切换锁定量程。锁定后缩放/平移不再改变 Y 轴范围,防止量程跳变。</summary>
    private void OnTrendToggleLock (object sender, RoutedEventArgs e)
    {
        if (TrendChart is null) return;

        if (TrendChart.YLocked)
        {
            TrendChart.UnlockRange();
            LockRangeBtn.Content = "锁定量程";
        }
        else
        {
            if (TrendChart.LockCurrentRange())
            {
                LockRangeBtn.Content = "解锁量程";
            }
            else
            {
                // 还没渲染过,拿不到量程
                TrendStatsText.Text = "图表还没渲染,无法锁定量程";
            }
        }
    }

    /// <summary>
    /// 导出 PNG。用 RenderTargetBitmap 直接把控件渲染成位图 ——
    /// 不用截屏,避免被遮挡窗口和系统缩放比例影响。
    /// </summary>
    private void OnTrendExportPng (object sender, RoutedEventArgs e)
    {
        if (!TrendChart.HasData)
        {
            TrendStatsText.Text = "没有可导出的曲线,请先查询";
            return;
        }

        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Title = "导出趋势图",
            Filter = "PNG 图片 (*.png)|*.png",
            FileName = "历史趋势_" + DateTime.Now.ToString ("yyyyMMdd_HHmmss") + ".png"
        };

        if (dlg.ShowDialog () != true) return;

        try
        {
            var w = (int)TrendChart.ActualWidth;
            var h = (int)TrendChart.ActualHeight;

            if (w < 20 || h < 20)
            {
                TrendStatsText.Text = "图表尚未就绪,请稍后再试";
                return;
            }

            var bmp = new System.Windows.Media.Imaging.RenderTargetBitmap (
                w, h, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);

            bmp.Render (TrendChart);

            var enc = new System.Windows.Media.Imaging.PngBitmapEncoder ();
            enc.Frames.Add (System.Windows.Media.Imaging.BitmapFrame.Create (bmp));

            using (var fs = File.Create (dlg.FileName))
            {
                enc.Save (fs);
            }

            TrendStatsText.Text = "已导出图片 " + w + "×" + h + "  →  " + dlg.FileName;
        }
        catch (Exception ex)
        {
            TrendStatsText.Text = "导出图片失败: " + ex.Message;
        }
    }

    /// <summary>
    /// 导出 CSV。
    ///
    /// 原子写:先写 .tmp,再替换目标文件 —— 磁盘满或中途崩溃不会留下半个文件。
    /// 编码带 BOM:Excel 双击打开中文不乱码(不带 BOM 的 UTF-8 在中文 Excel 里是乱码)。
    /// </summary>
    private void OnTrendExportCsv (object sender, RoutedEventArgs e)
    {
        if (_trendSeriesCache.Count == 0)
        {
            TrendStatsText.Text = "没有可导出的曲线,请先查询";
            return;
        }

        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Title = "导出趋势数据",
            Filter = "CSV 文件 (*.csv)|*.csv",
            FileName = "历史趋势_" + DateTime.Now.ToString ("yyyyMMdd_HHmmss") + ".csv"
        };

        if (dlg.ShowDialog () != true) return;

        try
        {
            // 有框选就只导框选区间,否则导当前视图
            DateTime from, to;

            if (_selFrom is not null && _selTo is not null)
            {
                from = _selFrom.Value;
                to = _selTo.Value;
            }
            else
            {
                var (vs, ve) = TrendChart.ViewRange;
                from = vs;
                to = ve;
            }

            // 只导出当前可见曲线,并裁剪到导出区间
            var csvSeries = new List<TrendCsvSeries> ();

            foreach (var s in _trendSeriesCache)
            {
                var pts = s.Points
                    .Where (p => p.Time >= from && p.Time <= to)
                    .ToList ();

                csvSeries.Add (new TrendCsvSeries (s.VarName, s.Unit, pts));
            }

            var header = TrendCsv.BuildHeader (csvSeries);
            var rows = TrendCsv.BuildRows (csvSeries);

            var tmp = dlg.FileName + ".tmp";

            using (var sw = new StreamWriter (tmp, false, new UTF8Encoding (true)))
            {
                sw.WriteLine (header);

                foreach (var r in rows) sw.WriteLine (r);
            }

            // 原子替换:同卷内不会出现"目标暂时不存在",失败也不会丢原文件
            AtomicFileWrite.Replace (tmp, dlg.FileName);

            TrendStatsText.Text = "已导出 " + rows.Count + " 行 × " + csvSeries.Count + " 条曲线  →  " + dlg.FileName;
        }
        catch (Exception ex)
        {
            TrendStatsText.Text = "导出失败: " + ex.Message;
        }
    }

    /// <summary>框选区间变化 → 算 12 项统计。</summary>
    private void OnTrendRangeSelected (DateTime? from, DateTime? to)
    {
        _selFrom = from;
        _selTo = to;

        UpdateTrendStats();
    }

    /// <summary>
    /// 分辨率下拉索引 → 枚举。
    /// 下拉顺序是「自动」在前、「原始值」在后,而枚举是 Raw=0、Auto=1,
    /// 所以不能用强制转换,必须显式映射,否则自动会被当成原始值。
    /// </summary>
    private static TrendResolution ResFromIndex (int idx)
    {
        switch (idx)
        {
            case 0: return TrendResolution.Auto;
            case 1: return TrendResolution.Raw;
            case 2: return TrendResolution.S1;
            case 3: return TrendResolution.S10;
            case 4: return TrendResolution.S30;
            case 5: return TrendResolution.M1;
            case 6: return TrendResolution.M5;
            case 7: return TrendResolution.M30;
            case 8: return TrendResolution.H1;
            case 9: return TrendResolution.D1;
            default: return TrendResolution.Auto;
        }
    }

    private void OnTrendResChanged (object sender, SelectionChangedEventArgs e)
    {
        if (_historyRepo is null || !IsLoaded) return;
        if (TrendVarList.SelectedItems.Count == 0) return;

        RunTrendQuery ();
    }

    private void OnTrendAggChanged (object sender, SelectionChangedEventArgs e)
    {
        if (_historyRepo is null || !IsLoaded) return;
        if (TrendVarList.SelectedItems.Count == 0) return;

        RunTrendQuery ();
    }

    /// <summary>光标移动 → 更新每条曲线的读数。</summary>
    private void OnTrendCursorMoved (DateTime? time)
    {
        if (time is null)
        {
            TrendCursorPanel.ItemsSource = null;
            return;
        }

        var readings = TrendChart.GetReadingsAt (time.Value);

        var list = new List<CursorReadout> (readings.Count);

        foreach (var r in readings)
        {
            var unit = string.IsNullOrEmpty (r.Unit) ? "" : " " + r.Unit;

            list.Add (new CursorReadout
            {
                Label = r.VarName,
                Comments = CommentsOf (r.VarName),
                Value = r.ValueText + unit,
                Color = r.Color
            });
        }

        TrendCursorPanel.ItemsSource = list;
    }

    /// <summary>视图窗口变化(缩放/平移/复位)→ 刷新统计。</summary>
    private void OnTrendViewChanged ()
    {
        if (_trendQueried) UpdateTrendStats ();
    }

    private void BuildRows ()
    {
        _rows.Clear ();
        var groups = _device.GroupList;

        foreach (var g in groups)
        {
            var vars = g.VarList;
            if  (vars is null) continue;

            foreach (var v in vars)
            {
                _rows.Add (new ValueRow
                {
                    Name = v.VarName,
                    Comments = v.Comments ?? "",
                    VarType = v.VarType ?? "",
                    Unit = v.Unit ?? "",
                    ValueText = "--",
                    QualityText = "未采集",
                    TimeText = ""
                });
            }
        }

        ValueGrid.ItemsSource = _rows;
    }

    /// <summary>
    /// 启动**当前选中那台**设备的采集,并挂一个界面侧状态刷新定时器。
    ///
    /// 这里不再 Build(_deviceList)+StartAll():那是并发采集,现场不允许
    /// (485 总线互相打断、没人看的设备在后台刷超时)。采哪台由下拉框决定,
    /// 流水线的建立与启动在 ApplyDevice → EnsurePipeline 里完成。
    ///
    /// 这个定时器只负责把状态写到界面上,不在界面线程里读设备,
    /// 所以设备超时不会把窗口卡住。
    /// </summary>
    private void StartAcquisition ()
    {
        if (_coordinator is null) EnsureCoordinator ();

        if (_acqTimer is null)
        {
            _acqTimer = new DispatcherTimer ();
            _acqTimer.Interval = TimeSpan.FromSeconds (1);
            _acqTimer.Tick += OnAcqTick;
            _acqTimer.Start ();
        }
    }

    /// <summary>界面侧刷新:把当前设备的流水线状态翻译成顶部状态栏文字。</summary>
    private void OnAcqTick (object sender, EventArgs e)
    {
        var acqBody = System.Diagnostics.Stopwatch.StartNew();

        if (_pipeline is null) return;

        // ==================== 卡死看门狗 ====================
        // 采集循环可能卡死在一次同步读里(串口句柄失效、设备不回帧):任务没结束、
        // IsRunning 仍为 true、State 仍停在"正常",但轮次不再增长、总线不再更新。
        // 后果就是界面**永远**显示断线前的最后一帧,而且怎么重选设备都没用。
        // 这里用"轮次是否还在涨"判定,超过 5 个采集周期就强制重建流水线。
        if (_pipeline.Service is not null && _pipeline.IsRunning)
        {
            var busVersion = _bus.Version;

            if (_pipeline.Rounds != _lastRounds || busVersion != _lastBusVersion)
            {
                // 轮次或数据任一在动,就说明循环是活的
                _lastRounds = _pipeline.Rounds;
                _lastBusVersion = busVersion;
                _stuckSince = DateTime.MinValue;
            }
            else
            {
                if (_stuckSince == DateTime.MinValue) _stuckSince = DateTime.Now;

                // 阈值必须大于"设备掉线时跑完一轮"的最坏耗时,否则会把正在正常等超时的
                // 那一轮砍掉 —— 结果是永远跑不完首轮、界面永远没数据(实测踩过)。
                // 服务侧有 CycleBudget 兜住单轮耗时,这里只负责救"真的挂了"的线程。
                var limit = TimeSpan.FromSeconds (
                    Math.Max (30, _pipeline.Interval.TotalSeconds * 30));

                if (DateTime.Now - _stuckSince > limit
                    && DateTime.Now - _lastRebuildAt > TimeSpan.FromSeconds (60))
                {
                    _lastRebuildAt = DateTime.Now;

                    StatusText.Text = "通讯无响应,正在重连…";
                    StatusText.Foreground = System.Windows.Media.Brushes.Goldenrod;

                    _ = RebuildCurrentPipelineAsync ("轮次停在 " + _pipeline.Rounds + "、数据版本停在 " + busVersion
                        + ",已 " + limit.TotalSeconds.ToString ("0") + " 秒没有变化,判定卡死");

                    return;
                }
            }
        }

        var state = _pipeline.State;

        switch (state)
        {
            case "正常":
                StatusText.Text = _pipeline.LastCount > 0 ? "通讯正常" : "通讯正常(无数据)";
                StatusText.Foreground = System.Windows.Media.Brushes.LightGreen;
                break;
            case "已连接(无数据)":
                StatusText.Text = "通讯正常(无数据)";
                StatusText.Foreground = System.Windows.Media.Brushes.LightGreen;
                break;
            case "未连接":
                StatusText.Text = "未连接";
                StatusText.Foreground = System.Windows.Media.Brushes.Salmon;
                break;
            case "异常":
                StatusText.Text = "通讯异常";
                StatusText.Foreground = System.Windows.Media.Brushes.Salmon;
                break;
            default:
                StatusText.Text = state;
                break;
        }

        // 失败原因始终显示出来,否则"未连接"三个字对现场毫无帮助
        if (!string.IsNullOrEmpty (_pipeline.LastError))
        {
            FooterText.Text = "采集异常: " + _pipeline.LastError;
        }
        else if (_coordinator is not null)
        {
            // 只采一台:状态栏就说这一台。早先这里列"并发 N 台",
            // 现在并发已经取消,再显示会让人以为后台还连着别的设备。
            var parts = new List<string> ();

            foreach (var p in _coordinator.Pipelines)
            {
                parts.Add (p.DeviceName + "=" + p.State
                    + "(" + p.LastCount + "点" + (p.Rounds > 0 ? ",第" + p.Rounds + "轮" : "") + ")");
            }

            FooterText.Text = parts.Count == 0
                ? "未选择设备(不通讯)"
                : "当前采集: " + string.Join (" | ", parts);
        }

        // 报警判定与归档都在流水线线程里做,这里不再重复调用(否则会写两遍)

        // 保留期清理:每小时最多跑一次。库是全局的,用当前流水线的归档器清一次即可。
        if (_pipeline?.Archive is not null && DateTime.Now - _lastPurge > TimeSpan.FromHours (1))
        {
            try
            {
                _pipeline.Archive.Purge (DateTime.Now, _appSettings.RetentionDays);
                _lastPurge = DateTime.Now;
            }
            catch (Exception ex)
            {
                FooterText.Text = "历史清理失败: " + ex.Message;
            }
        }

        RefreshDeviceBadge ();

        acqBody.Stop();

        if (acqBody.ElapsedMilliseconds > 40)
        {
            DiagUi ("acqTick body=" + acqBody.ElapsedMilliseconds + "ms state=" + (_pipeline?.State ?? "-")
                + " lastError=" + (_pipeline?.LastError ?? "-"));
        }
    }

    // ==================== 界面卡顿诊断(默认关闭) ====================

    // 默认不开:现场不需要多写文件,也不需要多算一遍统计。
    // 需要定位"界面卡"时用 KyjScada.App.exe --ui-diag 启动(或设环境变量 KYJ_UI_DIAG=1),
    // 每 10 秒会在 data\ui-latency.log 里落一行 STAT,可直接对比"断线 / 正常"两种状态。
    private static readonly bool _diagEnabled =
        Environment.GetCommandLineArgs ().Any (a => a == "--ui-diag")
        || Environment.GetEnvironmentVariable ("KYJ_UI_DIAG") == "1";

    /// <summary>
    /// 窗口正在关闭。
    ///
    /// 异步重建/重读组态的延续(ConfigureAwait(true))会回到界面线程,
    /// 那时窗口可能已经关掉:定时器被重建、StartAcquisition 又跑起来、
    /// 甚至去访问已释放的控件。所有异步延续开头都要先看这个标志。
    /// </summary>
    private volatile bool _closing;


    /// <summary>卡死看门狗:最近一次看到的轮次/总线版本,以及"两者都不再变化"的起点。</summary>
    private int _lastRounds = -1;
    private long _lastBusVersion = -1;
    private DateTime _stuckSince = DateTime.MinValue;
    private DateTime _lastRebuildAt = DateTime.MinValue;

    private readonly System.Diagnostics.Stopwatch _uiClock = System.Diagnostics.Stopwatch.StartNew ();
    private long _uiLastMs;

    private int _statTicks;
    private long _statSince;
    private long _statGapSum;
    private long _statGapMax;
    private long _statBodySum;
    private long _statBodyMax;
    private long _statGridSum;
    private long _statGridMax;

    /// <summary>把界面线程卡顿写进 data\ui-latency.log,便于离线看曲线;失败不影响运行。</summary>
    private static void DiagUi (string line)
    {
        if (!_diagEnabled) return;

        try
        {
            var dir = Path.Combine (AppContext.BaseDirectory, "data");

            if (!Directory.Exists (dir)) Directory.CreateDirectory (dir);

            File.AppendAllText (Path.Combine (dir, "ui-latency.log"),
                DateTime.Now.ToString ("HH:mm:ss.fff") + " " + line + Environment.NewLine);
        }
        catch
        {
            // 诊断落盘失败不能反过来影响界面
        }
    }

    /// <summary>顶部设备标签后面挂一个"N 台在采"的徽标,方便确认并发真的在跑。</summary>
    private void RefreshDeviceBadge ()
    {
        if (_coordinator is null || DeviceText is null) return;

        var running = _coordinator.Pipelines.Count (p => p.IsRunning);

        if (running <= 1) return;

        if (DeviceText.Text.Contains ("台并发"))
        {
            var cut = DeviceText.Text.IndexOf ("  [", StringComparison.Ordinal);

            if (cut > 0) DeviceText.Text = DeviceText.Text.Substring (0, cut);
        }

        DeviceText.Text = DeviceText.Text + "  [" + running + " 台并发]";
    }

    private void OnUiTick (object sender, EventArgs e)
    {
        var uiGap = _uiClock.ElapsedMilliseconds - _uiLastMs;
        _uiLastMs = _uiClock.ElapsedMilliseconds;

        var uiBody = System.Diagnostics.Stopwatch.StartNew();

        if  (_rows.Count == 0) return;

        var snap = _bus.GetSnapshot ();

        foreach (var row in _rows)
        {
            var tv = snap[row.Name];

            if  (tv is null)
            {
                row.ValueText = "--";
                row.QualityText = "未采集";
                continue;
            }

            var d = tv.AsDouble ();
            row.ValueText = d.HasValue ? FormatValue (d.Value, row.VarType) : "--";
            row.QualityText = QualityText (tv.Quality);
            row.TimeText = tv.ChangedAt.ToString ("HH:mm:ss");
        }

        var seg = System.Diagnostics.Stopwatch.StartNew ();
        var tRows = seg.ElapsedMilliseconds;

        // 这里曾经有一句 ValueGrid.Items.Refresh():
        // 它让 DataGrid 丢弃并重建全部 139 行容器,实测每次 80~160 ms,
        // 而定时器 500 ms 就跑一次 —— 界面线程长期被占,这就是"卡顿"的来源。
        // 现在 ValueRow 实现了 INotifyPropertyChanged,只重画真正变化的单元格,
        // 所以整表重画这一步必须去掉(留着反而把上面的收益全抵消)。
        var tGrid = seg.ElapsedMilliseconds;

        RefreshAlarmPanel ();
        var tAlarm = seg.ElapsedMilliseconds;

        UpdateTrendHint ();
        var tHint = seg.ElapsedMilliseconds;

        TickLiveTrend ();
        var tTrend = seg.ElapsedMilliseconds;

        TickWritePage ();
        var tWrite = seg.ElapsedMilliseconds;

        TickJogPage ();
        var tJog = seg.ElapsedMilliseconds;

        TickAlarmPage ();
        var tAlarmPage = seg.ElapsedMilliseconds;

        uiBody.Stop();

        if (uiGap > 800 || uiBody.ElapsedMilliseconds > 100)
        {
            var snapNow = _bus.GetSnapshot ();
            var pipeBus = _pipeline?.Bus;

            DiagUi ("uiTick gap=" + uiGap + "ms body=" + uiBody.ElapsedMilliseconds + "ms rows=" + _rows.Count
                + " snapN=" + snapNow.Values.Count + " busVer=" + _bus.Version
                + " pipeBusVer=" + (pipeBus?.Version ?? -1)
                + " sameBus=" + ReferenceEquals (_bus, pipeBus)
                + " firstRowHit=" + (snapNow[_rows.Count > 0 ? _rows[0].Name : ""] is not null)
                + " state=" + (_pipeline?.State ?? "-")
                + " acqCycle=" + (_pipeline?.LastCycleMs ?? 0) + "ms"
                + " | rows=" + tRows + " grid=" + (tGrid - tRows) + " alarm=" + (tAlarm - tGrid)
                + " hint=" + (tHint - tAlarm) + " trend=" + (tTrend - tHint)
                + " write=" + (tWrite - tTrend) + " jog=" + (tJog - tWrite)
                + " alarmPage=" + (tAlarmPage - tJog));
        }

        // 汇总统计:每 10 秒一行,便于对比"通讯断开 / 通讯正常"两种状态
        if (_diagEnabled)
        {
            _statTicks++;
            _statGapSum += uiGap;
            if (uiGap > _statGapMax) _statGapMax = uiGap;
            _statBodySum += uiBody.ElapsedMilliseconds;
            if (uiBody.ElapsedMilliseconds > _statBodyMax) _statBodyMax = uiBody.ElapsedMilliseconds;
            _statGridSum += tGrid - tRows;
            if (tGrid - tRows > _statGridMax) _statGridMax = tGrid - tRows;

            if (_uiClock.ElapsedMilliseconds - _statSince >= 10000)
            {
                DiagUi ("STAT n=" + _statTicks + " avgGap=" + (_statGapSum / _statTicks)
                    + " maxGap=" + _statGapMax
                    + " avgBody=" + (_statBodySum / _statTicks) + " maxBody=" + _statBodyMax
                    + " avgGrid=" + (_statGridSum / _statTicks) + " maxGrid=" + _statGridMax
                    + " state=" + (_pipeline?.State ?? "-"));

                _statSince = _uiClock.ElapsedMilliseconds;
                _statTicks = 0;
                _statGapSum = 0;
                _statGapMax = 0;
                _statBodySum = 0;
                _statBodyMax = 0;
                _statGridSum = 0;
                _statGridMax = 0;
            }
        }
    }

    /// <summary>趋势页底部操作提示(附当前视图跨度,让人知道缩放到了什么尺度)。</summary>
    private void UpdateTrendHint ()
    {
        if (TrendHintText is null || TrendChart is null) return;

        var hint = "操作:滚轮缩放 · 拖拽平移 · 双击复位 · 鼠标移动读光标处数值";

        if (TrendChart.HasData)
        {
            var (vs, ve) = TrendChart.ViewRange;
            var span = ve - vs;

            var spanText = span.TotalHours >= 1
                ? span.TotalHours.ToString ("F1") + " 小时"
                : span.TotalMinutes.ToString ("F1") + " 分钟";

            hint += "     当前视图跨度 " + spanText;
        }

        if (TrendChart.EventMarkerCount > 0)
        {
            hint += "     报警事件 " + TrendChart.DrawnEventCount + " 条";
        }

        TrendHintText.Text = hint;
    }

    // ==================== 实时趋势 ====================

/// <summary>可选的实时变量(全部变量,不限于归档变量 —— 实时只看总线)。</summary>
private readonly List<TrendSelectItem> _liveAllVars = new List<TrendSelectItem> ();

/// <summary>每个变量一份滚动缓冲(时间升序)。</summary>
private readonly Dictionary<string, List<TrendBucket>> _liveBuffers =
    new Dictionary<string, List<TrendBucket>> (StringComparer.OrdinalIgnoreCase);

/// <summary>暂停采集(曲线冻结,缓冲不再增长)。</summary>
private bool _livePaused;

/// <summary>是否跟随最新时刻。用户缩放/平移后置 false,双击复位后恢复。</summary>
private bool _liveFollow = true;

private DateTime _liveLastSample = DateTime.MinValue;

private void InitLiveTrend ()
{
    _liveAllVars.Clear ();

    var idx = 0;

    foreach (var g in _device.GroupList)
    {
        if (g.VarList is null) continue;

        foreach (var v in g.VarList)
        {
            if (string.IsNullOrEmpty (v.VarName)) continue;

            _liveAllVars.Add (new TrendSelectItem
            {
                Name = v.VarName,
                Comments = v.Comments ?? "",
                Unit = v.Unit ?? "",
                Color = TrendColors[idx % TrendColors.Length]
            });

            idx++;
        }
    }

    ApplyLiveFilter ("");

    if (LiveWindowBox.SelectedIndex < 0) LiveWindowBox.SelectedIndex = 1; // 默认 5 分钟
    if (LiveModeBox.SelectedIndex < 0) LiveModeBox.SelectedIndex = 0;     // 默认叠加

    // 事件订阅已移到构造函数(只订阅一次),这里不再 +=

    // 默认选前 3 个
    var take = Math.Min (3, LiveVarList.Items.Count);
    for (int i = 0; i < take; i++) LiveVarList.SelectedItems.Add (LiveVarList.Items[i]);

    UpdateLiveSelectedText ();
}

private void ApplyLiveFilter (string keyword)
{
    var selected = new HashSet<string> (StringComparer.OrdinalIgnoreCase);
    foreach (var item in LiveVarList.SelectedItems) { if (item is TrendSelectItem t) selected.Add (t.Name); }

    LiveVarList.Items.Clear ();

    foreach (var v in _liveAllVars)
    {
        if (!string.IsNullOrEmpty (keyword) &&
            v.Name.IndexOf (keyword, StringComparison.OrdinalIgnoreCase) < 0)
        {
            continue;
        }

        LiveVarList.Items.Add (v);

        if (selected.Contains (v.Name)) LiveVarList.SelectedItems.Add (v);
    }
}

private void OnLiveSearchChanged (object sender, TextChangedEventArgs e)
{
    ApplyLiveFilter (LiveSearchBox.Text ?? "");
}

private void OnLiveVarSelectionChanged (object sender, SelectionChangedEventArgs e)
{
    UpdateLiveSelectedText ();
}

private void UpdateLiveSelectedText ()
{
    var n = LiveVarList.SelectedItems.Count;
    LiveSelectedText.Text = n == 0 ? "未选择" : ("已选 " + n + " 个变量");
}

private void OnLiveSelectAll (object sender, RoutedEventArgs e)
{
    LiveVarList.SelectAll ();
    UpdateLiveSelectedText ();
}

private void OnLiveClearVars (object sender, RoutedEventArgs e)
{
    LiveVarList.UnselectAll ();
    UpdateLiveSelectedText ();
}

private void OnLiveWindowChanged (object sender, SelectionChangedEventArgs e)
{
    _liveFollow = true;   // 换窗口自然要重新跟随
}

private void OnLiveModeChanged (object sender, SelectionChangedEventArgs e)
{
    if (LiveChart is null) return;

    LiveChart.DisplayMode = LiveModeBox.SelectedIndex == 1
        ? TrendDisplayMode.Stacked
        : TrendDisplayMode.Overlay;
}

private void OnLiveTogglePause (object sender, RoutedEventArgs e)
{
    _livePaused = !_livePaused;

    LivePauseBtn.Content = _livePaused ? "继续" : "暂停";
    LivePauseBtn.Background = _livePaused
        ? new System.Windows.Media.SolidColorBrush (System.Windows.Media.Color.FromRgb (0x4B, 0x2A, 0x2A))
        : new System.Windows.Media.SolidColorBrush (System.Windows.Media.Color.FromRgb (0x31, 0x32, 0x44));
}

private void OnLiveClearData (object sender, RoutedEventArgs e)
{
    _liveBuffers.Clear ();
    _liveLastSample = DateTime.MinValue;
    _liveFollow = true;

    LiveChart.Clear ();
    LiveValuePanel.ItemsSource = null;
}

/// <summary>窗口分钟数。</summary>
private int LiveWindowMinutes => LiveWindowBox.SelectedIndex switch
{
    0 => 1,
    2 => 15,
    3 => 30,
    4 => 60,
    _ => 5
};

/// <summary>
/// 每个 UI tick 喂一次实时数据。
///
/// 数据源是 ValueBus 快照(采集服务每秒发布一次),不是重新读 PLC ——
/// 实时趋势和历史趋势的区别只在"窗口怎么滚",取数都走同一份总线。
/// </summary>
private void TickLiveTrend ()
{
    if (MainTabs.SelectedIndex != 3) return;
    if (_livePaused) return;

    var snap = _bus.GetSnapshot ();

    // 同一时刻只采一次(UI 500ms 刷新,采集 1 秒,避免重复点)
    if (snap.Timestamp == _liveLastSample) return;

    _liveLastSample = snap.Timestamp;

    var selected = LiveVarList.SelectedItems.OfType<TrendSelectItem> ().ToList ();

    if (selected.Count == 0)
    {
        LiveChart.Clear ();
        LiveValuePanel.ItemsSource = null;
        LiveInfoText.Text = "请在左侧选择变量";
        return;
    }

    var window = TimeSpan.FromMinutes (LiveWindowMinutes);
    var cutoff = DateTime.Now - window - TimeSpan.FromMinutes (LiveWindowMinutes * 0.5);

    // 追加采样
    foreach (var item in selected)
    {
        if (!_liveBuffers.TryGetValue (item.Name, out var buf))
        {
            buf = new List<TrendBucket> ();
            _liveBuffers[item.Name] = buf;
        }

        var tv = snap[item.Name];
        var v = tv?.AsDouble ();

        if (v.HasValue)
        {
            buf.Add (new TrendBucket (snap.Timestamp, v.Value, v.Value, v.Value, 1));
        }

        // 裁掉窗口外 1.5 倍以前的数据,防止缓冲无限增长
        var drop = 0;
        while (drop < buf.Count && buf[drop].Time < cutoff) drop++;
        if (drop > 0) buf.RemoveRange (0, drop);
    }

    // 组装曲线
    var series = new List<TrendSeries> (selected.Count);

    foreach (var item in selected)
    {
        if (!_liveBuffers.TryGetValue (item.Name, out var buf) || buf.Count == 0) continue;

        var cfg = FindVarConfig (item.Name);

        series.Add (new TrendSeries
        {
            VarName = item.Name,
            Comments = item.Comments,
            Unit = item.Unit,
            Color = item.Color,
            Points = buf,
            HighLimit = cfg is not null && cfg.HighAlarmEnable ? cfg.HighAlarmValue : 0,
            LowLimit = cfg is not null && cfg.LowAlarmEnable ? cfg.LowAlarmValue : 0,
            IsDigital = cfg is not null && IsDigitalType (cfg.VarType)
        });
    }

    LiveChart.SetSeries (series);

    if (_liveFollow)
    {
        var now = DateTime.Now;
        LiveChart.SetView (now - window, now);
    }

    // 当前值
    var readings = new List<CursorReadout> (selected.Count);

    foreach (var item in selected)
    {
        var tv = snap[item.Name];
        var v = tv?.AsDouble ();
        var unit = string.IsNullOrEmpty (item.Unit) ? "" : " " + item.Unit;

        readings.Add (new CursorReadout
        {
            Label = item.Name,
            Comments = item.Comments,
            Value = (v.HasValue ? FormatValue (v.Value, FindVarConfig (item.Name)?.VarType ?? "") : "--") + unit,
            Color = item.Color
        });
    }

    LiveValuePanel.ItemsSource = readings;

    LiveInfoText.Text = selected.Count + " 条曲线  ·  " + LiveWindowMinutes + " 分钟窗口"
        + (_livePaused ? "  ·  已暂停" : (_liveFollow ? "" : "  ·  已停止跟随(双击恢复)"));
}

/// <summary>实时趋势光标读数。</summary>
private void OnLiveCursorMoved (DateTime? time)
{
    if (time is null || !LiveChart.HasData) return;

    var readings = LiveChart.GetReadingsAt (time.Value);

    var list = new List<CursorReadout> (readings.Count);

    foreach (var r in readings)
    {
        var unit = string.IsNullOrEmpty (r.Unit) ? "" : " " + r.Unit;

        list.Add (new CursorReadout
        {
            Label = r.VarName,
            Comments = CommentsOf (r.VarName),
            Value = r.ValueText + unit,
            Color = r.Color
        });
    }

    LiveValuePanel.ItemsSource = list;
}

// ==================== 用户与权限 ====================

private UserRepository _userRepo;

private KyjScada.Domain.Users.UserAccount _currentUser;

/// <summary>
/// 初始化用户库:建表 + 首次运行种默认管理员。
/// 账号库与历史/报警同目录不同文件,避免历史库膨胀拖慢登录。
/// </summary>
private void InitUsers ()
{
    try
    {
        var dir = Path.Combine (AppContext.BaseDirectory, "data");
        Directory.CreateDirectory (dir);

        _userRepo = new UserRepository (Path.Combine (dir, "users.db"));
        _userRepo.EnsureSchema ();

        if (_userRepo.SeedDefaultAdmin ("admin123", DateTime.Now))
        {
            FooterText.Text = "已创建默认管理员 admin / admin123,请登录后立即修改密码。";
        }

        UpdateUserDisplay ();
    }
    catch (Exception ex)
    {
        _userRepo = null;
        FooterText.Text = "用户库初始化失败: " + ex.Message;
    }
}

private void UpdateUserDisplay ()
{
    if (_currentUser is null)
    {
        UserText.Text = "未登录";
        UserText.Foreground = System.Windows.Media.Brushes.Goldenrod;
        LoginBtn.Content = "登录";
    }

    else
    {
        UserText.Text = _currentUser.UserName + "(" + _currentUser.PermissionText + ")";
        UserText.Foreground = System.Windows.Media.Brushes.LightGreen;
        LoginBtn.Content = "注销";
    }

    // 用户管理入口:没有权限就禁用(逻辑层仍会再校验一次)
    if (UserManageBtn is not null)
    {
        UserManageBtn.IsEnabled = HasPermission (KyjScada.Domain.Users.PermissionBit.CanManageUsers);
    }

    UpdatePermissionDependentButtons ();
}

/// <summary>当前登录用户是否具备某权限位(未登录一律 false)。</summary>
private bool HasPermission (KyjScada.Domain.Users.PermissionBit bit)
{
    return _currentUser is not null && _currentUser.Has (bit);
}

/// <summary>
/// 按权限刷新受控按钮的可用性。
/// **界面禁用只是提示,真正拦截在各自的事件处理里** —— 两边都做才算安全。
/// </summary>
private void UpdatePermissionDependentButtons ()
{
    if (ConfigEditorBtn is not null)
    {
        ConfigEditorBtn.IsEnabled = HasPermission (KyjScada.Domain.Users.PermissionBit.CanEditConfig);
    }

    if (AlarmAckSelectedBtn is not null)
    {
        AlarmAckSelectedBtn.IsEnabled = HasPermission (KyjScada.Domain.Users.PermissionBit.CanAckAlarm);
    }

    if (AlarmAckAllBtn is not null)
    {
        AlarmAckAllBtn.IsEnabled = HasPermission (KyjScada.Domain.Users.PermissionBit.CanAckAlarm);
    }

    if (SystemSettingsBtn is not null)
    {
        SystemSettingsBtn.IsEnabled = HasPermission (KyjScada.Domain.Users.PermissionBit.CanEditSystem);
    }

    // 急停/点动页:权限变化时只刷新按钮可用性(不重建清单,避免丢掉用户当前选择)
    if (_jogPageBuilt) ApplyJogPermissions ();
}

/// <summary>打开系统设置(归档周期/保留期/写入限速),需「系统设置」权限。</summary>
private void OnOpenSystemSettings (object sender, RoutedEventArgs e)
{
    if (!HasPermission (KyjScada.Domain.Users.PermissionBit.CanEditSystem))
    {
        FooterText.Text = "没有「系统设置」权限,请用有权限的账号登录";
        return;
    }

    var win = new SettingsWindow (_appSettings) { Owner = this };

    var mcpBefore = _appSettings.McpEnabled ? _appSettings.McpPort : 0;

    win.ShowDialog ();

    // 重新读一次:保存后归档周期等会变(运行中的采集不重建,提示重启生效)
    _appSettings = AppSettings.Load ();

    // MCP 的端口/开关是唯一能当场生效的:MCP 与采集器相互独立,重启不影响通讯
    var mcpAfter = _appSettings.McpEnabled ? _appSettings.McpPort : 0;

    if (mcpBefore != mcpAfter)
    {
        RestartMcp ();
    }
}

private void OnLoginClick (object sender, RoutedEventArgs e)
{
    if (_currentUser is not null)
    {
        // 注销:关掉写入并清空身份
        _currentUser = null;

        // 必须走 SetGateEnabled:只关 _gate 和写入页的勾选框,点动页那个勾还亮着,
        // 用户看到"闸门已开"。两页共用一个闸门,状态就得一处改、两处同步。
        SetGateEnabled (false);

        UpdateUserDisplay ();
        ApplyWritePermission ();

        FooterText.Text = "已注销。";
        return;
    }

    if (_userRepo is null)
    {
        FooterText.Text = "用户库不可用,无法登录";
        return;
    }

    var dlg = new LoginWindow (_userRepo) { Owner = this };

    if (dlg.ShowDialog () != true || dlg.User is null) return;

    _currentUser = dlg.User;

    UpdateUserDisplay ();
    ApplyWritePermission ();

    FooterText.Text = "已登录:" + _currentUser.UserName + "  权限:" + _currentUser.PermissionText;
}

/// <summary>打开用户管理(需「用户管理」权限)。</summary>
private void OnOpenUserManage (object sender, RoutedEventArgs e)
{
    if (_userRepo is null)
    {
        FooterText.Text = "用户库不可用";
        return;
    }

    if (_currentUser is null
        || !_currentUser.Has (KyjScada.Domain.Users.PermissionBit.CanManageUsers))
    {
        FooterText.Text = "需要「用户管理」权限才能打开";
        return;
    }

    var win = new UserManageWindow (_userRepo) { Owner = this };

    win.ShowDialog ();

    // 自己的权限可能被改过:重新从库里读一次,避免界面还显示旧权限
    try
    {
        var refreshed = _userRepo.Find (_currentUser.UserName);

        if (refreshed is not null) _currentUser = refreshed;
    }
    catch
    {
        // 读失败就保持原权限,不影响使用
    }

    UpdateUserDisplay ();
    ApplyWritePermission ();
}

/// <summary>
/// 按当前用户权限放开/收紧"允许写入"。
/// **权限判断在这里做,而不是靠界面藏按钮** —— 藏起来只是不方便,不是安全。
/// </summary>
private void ApplyWritePermission ()
{
    if (WriteEnableBox is null) return;

    var canWrite = _currentUser is not null
        && _currentUser.Has (KyjScada.Domain.Users.PermissionBit.CanWrite);

    WriteEnableBox.IsEnabled = canWrite;

    if (!canWrite)
    {
        // 同样走统一入口:收回权限时两页的勾选框和提示文字要一起变
        SetGateEnabled (false);
    }

    if (WriteInfoText is not null)
    {
        WriteInfoText.Text = canWrite
            ? "可写点 " + _writeRows.Count + " 个(已具备参数设置权限)"
            : "可写点 " + _writeRows.Count + " 个 · 需登录且有「参数设置」权限才能写入";
    }
}

// ==================== 参数设置(写入) ====================

private CommandGate _gate;

/// <summary>同上:必须可观察,否则重建写入页后表格仍显示旧行。</summary>
    private readonly System.Collections.ObjectModel.ObservableCollection<WriteRow> _writeRows
        = new System.Collections.ObjectModel.ObservableCollection<WriteRow> ();

/// <summary>
/// 构建参数设置页:把当前设备里**真正可写**的点列出来。
///
/// 可写判定交给采集层的 CanWrite(区是否可写 + 类型是否支持),
/// 闸门只负责校验与下发 —— 两边规则只维护一份。
/// </summary>
/// <param name="preserveGate">
    /// true = 保留闸门当前的 Enabled 状态(重读组态走这条)。
    /// 默认 false:换设备要重新授权 —— 界面显示"未允许写入"时后台也必须真的关掉。
    /// </param>
    private void BuildWritePage (bool preserveGate = false)
{
    _writeRows.Clear ();

    // 闸门**只在第一次建**:重建会换掉实例,Enabled 被重置成 false、限速表与审计一起清空,
    // 而当时可能还有在途写入握着旧实例 —— 关一下配置编辑器(哪怕只看一眼)就变成
    // “写入被静默关闭 + 审计断电”。换设备/重读配置只更新 Writer 与可写点清单。
    if (_gate is null)
    {
        _gate = new CommandGate
            {
                Enabled = false,
                MinInterval = TimeSpan.FromMilliseconds (_appSettings.WriteMinIntervalMs)
            };
    }
    else
    {
        _gate.MinInterval = TimeSpan.FromMilliseconds (_appSettings.WriteMinIntervalMs);
    }

    // 值判定统一挂在闸门上:所有写入口(界面行/Bool 按钮/点动/急停/MCP)都过它
    _gate.ValueGuard = EvaluateWrite;

    if (_svc is not null) _gate.Writer = _svc as IDeviceWriter;

    if (_device is not null)
    {
        // 变量名 → 所属组(判定区可写与位地址都要用)
        var varGroup = new Dictionary<string, GroupConfig> (StringComparer.OrdinalIgnoreCase);

        foreach (var g in _device.GroupList)
        {
            if (g.VarList is null) continue;

            foreach (var v in g.VarList)
            {
                if (!string.IsNullOrEmpty (v.VarName)) varGroup[v.VarName] = g;
            }
        }

        bool WritableByGroup (VariableConfig v)
        {
            if (!varGroup.TryGetValue (v.VarName, out var g)) return false;

            return IsWritableGroup (g) && ModbusAcquisitionService.CanWrite (g.Start, v, out _);
        }

        string ReasonOfVar (VariableConfig v)
        {
            if (!varGroup.TryGetValue (v.VarName, out var g)) return "找不到所属组";

            if (!IsWritableGroup (g)) return "组「" + g.GroupName + "」未开放写入";

            ModbusAcquisitionService.CanWrite (g.Start, v, out var reason);

            return reason;
        }

        _gate.BuildWritables (_device, WritableByGroup, ReasonOfVar);

        foreach (var g in _device.GroupList)
        {
            if (g.VarList is null) continue;
            if (!IsWritableGroup (g)) continue;

            foreach (var v in g.VarList)
            {
                if (string.IsNullOrEmpty (v.VarName)) continue;
                if (!_gate.IsWritable (v.VarName)) continue;

                _writeRows.Add (new WriteRow
                {
                    GroupName = g.GroupName,
                    VarName = v.VarName,
                    Unit = v.Unit ?? "",
                    Comments = v.Comments ?? "",
                    CurrentText = "--",
                    InputText = "",
                    // Bool 行用开关按钮,不给数字输入框
                    IsBool = string.Equals ((v.VarType ?? "").Trim (), "bool", StringComparison.OrdinalIgnoreCase)
                });
            }
        }
    }

    // 集合是 ObservableCollection,Clear/Add 自己会通知界面 ——
    // 不再做 ItemsSource=null 的往返:那会额外丢掉滚动位置/选中项/焦点。
    if (WriteList.ItemsSource is null) WriteList.ItemsSource = _writeRows;

    if (!preserveGate)
    {
        // 换设备重建写入页:勾选框清空的同时闸门必须真的关掉,
        // 否则界面显示"未允许写入"、后台仍能下发命令(点动页的勾也是同一个闸门)。
        SetGateEnabled (false);
    }
    else
    {
        // 重读组态:勾选框按闸门实际状态回填,但**不动 Enabled**
        if (WriteEnableBox is not null) WriteEnableBox.IsChecked = _gate?.Enabled == true;
    }

    WriteInfoText.Text = "可写点 " + _writeRows.Count + " 个";

    WriteResultText.Text = _writeRows.Count == 0
        ? "当前设备没有开放写入的组(需把设定值组标记为可写)"
        : "勾选「允许写入」后即可下发。设定值写入前请确认设备处于允许修改的状态。";

    // 按当前登录用户收紧/放开写入开关
    ApplyWritePermission ();
}

/// <summary>
/// 该组是否开放写入。判定规则已抽到 <see cref="KyjScada.Runtime.JogPoints"/>(可单测)。
/// </summary>
private static bool IsWritableGroup (GroupConfig g) => KyjScada.Runtime.JogPoints.IsWritableGroup (g);


/// <summary>刷新可写点的当前值。</summary>
private void TickWritePage ()
{
    if (MainTabs.SelectedIndex != 4 || _writeRows.Count == 0) return;

    var snap = _bus.GetSnapshot ();

    foreach (var row in _writeRows)
    {
        var tv = snap[row.VarName];
        var d = tv?.AsDouble ();

        row.CurrentText = d.HasValue ? d.Value.ToString ("G6") : "--";
    }
}

/// <summary>
/// 统一开关命令闸门:两个页签(参数设置 / 点动)操作的是同一个 CommandGate,
/// 勾选框和提示文字必须一起回填,否则一页改了另一页还显示旧状态。
///
/// 还要注意:WPF 里**代码给 IsChecked 赋值同样会触发 Checked/Unchecked**,
/// 两个处理函数各自回填对方就会互相触发。所有回填集中在这里,用 _syncingGate 挡住。
/// </summary>
private void SetGateEnabled (bool enabled)
{
    if (_gate is null) return;

    _gate.Enabled = enabled;

    _syncingGate = true;

    try
    {
        if (WriteEnableBox is not null) WriteEnableBox.IsChecked = enabled;
        if (JogEnableBox is not null) JogEnableBox.IsChecked = enabled;
    }
    finally
    {
        _syncingGate = false;
    }

    if (WriteInfoText is not null)
    {
        WriteInfoText.Text = !enabled
            ? "写入已关闭"
            : _gate.Writer is null
                ? "当前设备没有可用写入器(协议未采集或未连接)"
                : "写入已启用,可写点 " + _writeRows.Count + " 个";
    }

    if (JogGateText is not null) JogGateText.Text = enabled ? "闸门已开" : "闸门已关";
}

private void OnWriteEnableChanged (object sender, RoutedEventArgs e)
{
    if (_gate is null) return;

    // 由 SetGateEnabled 回填勾选框引起的事件:状态已经同步过了,不要再走一遍
    if (_syncingGate) return;

    var wantOn = WriteEnableBox.IsChecked == true;

    // 勾"允许写入"本身也要校验权限:没有 CanWrite 的人不能靠勾这个框拿写能力
    if (wantOn && !(_currentUser is not null
        && _currentUser.Has (KyjScada.Domain.Users.PermissionBit.CanWrite)))
    {
        SetGateEnabled (false);

        if (WriteInfoText is not null) WriteInfoText.Text = "没有「参数设置」权限,请先用有权限的账号登录";

        return;
    }

    SetGateEnabled (wantOn);
}

/// <summary>Bool 行:写 1(开)。和点动页同一套语义,只是从参数设置页发起。</summary>
private void OnWriteBoolOn (object sender, RoutedEventArgs e) => WriteBoolRow (sender, true);

/// <summary>Bool 行:写 0(关)。</summary>
private void OnWriteBoolOff (object sender, RoutedEventArgs e) => WriteBoolRow (sender, false);

/// <summary>
/// Bool 行的公共写入:1/0 两态,不需要用户填数字。
///
/// 这里**不做二次确认**:参数设置页本来就有"允许写入"总闸门 + 权限校验,
/// 而且开关量是现场最常点的操作,每点一次弹一个确认框会让操作员直接放弃使用。
/// 危险动作(急停/强制写)才需要确认。
/// </summary>
private async void WriteBoolRow (object sender, bool on)
{
    if (_gate is null) return;

    if (sender is not Button btn || btn.Tag is not WriteRow row) return;

    btn.IsEnabled = false;
    WriteResultText.Text = "正在写入「" + row.VarName + "」…";

    var gate = _gate;
    var name = row.VarName;

    WriteOutcome result;

    try
    {
        // 与点动一致:Bool 就是 1.0 / 0.0,中间不存在别的取值。
        // 这里显式 confirmed:true —— "写1(开)/写0(关)"按钮本身就是人的确认动作,
        // 不再为"值与当前不同"二次弹框;越界与无法核实仍由闸门拦下(返回 NeedsConfirm)。
        result = await gate.WriteAsync (name, on ? 1.0 : 0.0, "hmi", null, confirmed: true)
            .ConfigureAwait (true);
    }
    catch (Exception ex)
    {
        btn.IsEnabled = true;

        WriteResultText.Text = "✘ " + name + " 写入异常: " + ex.Message;

        return;
    }

    btn.IsEnabled = true;

    WriteResultText.Text = (result.Success ? "✔ " : "✘ ") + name + (on ? " 写1(开): " : " 写0(关): ") + result.Message;
}

    /// <summary>
    /// 闸门的值判定回调:把“组态 + 当前采样”翻译成 WriteCheck。
    ///
    /// 放在这里(而不是各页面)是因为闸门是所有写入口的唯一汇合点:
    /// 界面数值行、Bool 按钮、点动、急停、MCP 远程写,全都要过 CommandGate.Write。
    /// 判定口径本身在 Runtime/WriteGuard(纯函数,已单测)。
    ///
    /// 注意:这里**不用报警阈值**当写入范围 —— 频率给定 10Hz 低于报警低限 20Hz 是正常操作。
    /// 写入范围只认组态里的 WriteLowLimit/WriteHighLimit,没配就只做差异确认。
    /// </summary>
    private KyjScada.Runtime.WriteCheck EvaluateWrite (string varName, double value)
    {
        var cfg = FindVariableConfig (varName);

        var isBool = cfg is not null
            && string.Equals ((cfg.VarType ?? "").Trim (), "bool", StringComparison.OrdinalIgnoreCase);

        var limits = new KyjScada.Runtime.WriteLimits (
            cfg?.WriteHighLimit,
            cfg?.WriteLowLimit,
            cfg?.Unit ?? "",
            isBool);

        return KyjScada.Runtime.WriteGuard.Evaluate (value, CurrentSampleOf (varName), limits);
    }

    /// <summary>
    /// 当前采样值 + 是否可信。
    ///
    /// 只看“有没有值”是不够的:断线时总线里还留着最后一次的值,质量标记已经是 Bad,
    /// 拿这种值去比较等于用过期数据做安全判断 —— 所以质量不是 Good 一律按不可信处理。
    /// </summary>
    private KyjScada.Runtime.CurrentSample CurrentSampleOf (string varName)
    {
        try
        {
            var tv = _bus?.GetSnapshot ()[varName];

            if (tv is null) return KyjScada.Runtime.CurrentSample.Unknown;

            if (tv.Quality != TagQuality.Good) return KyjScada.Runtime.CurrentSample.Unknown;

            var d = tv.AsDouble ();

            return d.HasValue
                ? KyjScada.Runtime.CurrentSample.Of (d.Value)
                : KyjScada.Runtime.CurrentSample.Unknown;
        }
        catch
        {
            // 总线还没建立/变量不在本设备里:当成“读不到”,交给 WriteGuard 强制确认
            return KyjScada.Runtime.CurrentSample.Unknown;
        }
    }
/// <summary>按变量名找组态定义(找不到返回 null)。</summary>
private VariableConfig? FindVariableConfig (string varName)
{
    if (_device?.GroupList is null) return null;

    foreach (var g in _device.GroupList)
    {
        if (g.VarList is null) continue;

        foreach (var v in g.VarList)
        {
            if (string.Equals (v.VarName, varName, StringComparison.OrdinalIgnoreCase)) return v;
        }
    }

    return null;
}

/// <summary>点某一行的「写入」。</summary>
private async void OnWriteRow (object sender, RoutedEventArgs e)
{
    if (_gate is null) return;

    if (sender is not Button btn || btn.Tag is not WriteRow row) return;

    if (!double.TryParse (row.InputText, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out var value))
    {
        WriteResultText.Text = "「" + row.VarName + "」的输入不是有效数字: " + row.InputText;

        return;
    }

    // 值判定在闸门里做(ValueGuard)。这里先“不带确认”地试一次:
    //   · 返回 NeedsConfirm → 说明值可疑,弹框问人;点了是再用 confirmed:true 重发;
    //   · 其它失败(未启用/无权限/不可写/限速)→ 直接显示原因。
    // 顺序很重要:权限与可写性在闸门里**先于**值判定,所以没权限的人不会被弹框骚扰。

    // 写值要走一次"连接+发帧+等回帧",断线时这里可能要等几秒(网线被拔的那种更久)。
    // **绝不能在界面线程上等**:那会把整个窗口冻住,用户看到的就是"卡顿/无响应"。
    // 放到后台线程执行,期间禁掉按钮并提示"正在写入"。
    btn.IsEnabled = false;
    WriteResultText.Text = "正在写入「" + row.VarName + "」…";

    var gate = _gate;
    var name = row.VarName;

    WriteOutcome result;

        // 写入可能抛(连接断开、服务已释放等)。async void 里没有 catch 会直接终止进程,
        // 而且按钮会永远停在 disabled —— 这里兜住并把原因显示出来。
        try
        {
            result = await gate.WriteAsync (name, value, "hmi").ConfigureAwait (true);
        }
        catch (Exception ex)
        {
            btn.IsEnabled = true;

            WriteResultText.Text = "✘ " + name + " 写入异常: " + ex.Message;

            return;
        }

    // 闸门说“要确认”:此时**一个字节都没下发**,问过人再用 confirmed 重发
    if (result.Verdict == WriteVerdict.NeedsConfirm)
    {
        var answer = MessageBox.Show (
            "要写入的值与当前状态不一致,请确认:\n\n"
            + "变量: " + NameWithComment (name, row.Comments)
            + (string.IsNullOrEmpty (row.GroupName) ? "" : "  (" + row.GroupName + ")") + "\n"
            + "输入值: " + value.ToString ("0.######") + "\n\n"
            + result.Message
            + "\n\n确认要下发这个值吗?",
            result.OutOfRange ? "写入确认(超出写入范围)" : "写入确认",
            MessageBoxButton.YesNo,
            result.OutOfRange ? MessageBoxImage.Warning : MessageBoxImage.Question);

        if (answer != MessageBoxResult.Yes)
        {
            btn.IsEnabled = true;

            WriteResultText.Text = "已取消:" + name + " 未写入";

            return;
        }

        try
        {
            result = await gate.WriteAsync (name, value, "hmi", null, confirmed: true).ConfigureAwait (true);
        }
        catch (Exception ex)
        {
            btn.IsEnabled = true;

            WriteResultText.Text = "✘ " + name + " 写入异常: " + ex.Message;

            return;
        }
    }

    btn.IsEnabled = true;

    WriteResultText.Text = (result.Success ? "✔ " : "✘ ") + name + ": " + result.Message;
}

// ==================== 报警页 ====================

private DateTime _lastAlarmPageRefresh = DateTime.MinValue;

/// <summary>初始化报警页(默认最近 1 小时)。</summary>
private void InitAlarmPage ()
{
    if (AlarmRangeBox.SelectedIndex < 0) AlarmRangeBox.SelectedIndex = 0;

    RefreshAlarmPage ();
}

/// <summary>报警页时间范围。</summary>
private (DateTime from, DateTime to) ResolveAlarmRange ()
{
    var now = DateTime.Now;

    switch (AlarmRangeBox.SelectedIndex)
    {
        case 1: return (now.AddHours (-24), now);
        case 2: return (now.AddDays (-7), now);
        case 3: return (DateTime.MinValue, now);
        default: return (now.AddHours (-1), now);
    }
}

/// <summary>
/// 刷新报警页:当前报警(实时状态机)+ 历史报警(SQLite)。
///
/// 两者来源不同,不能混为一谈:当前报警是内存状态机,重启清零;
/// 历史报警是落库事件,重启仍在。库里的最后一条未必等于"现在还在报警" ——
/// 比如报警后来消除了,库里有消除事件,但状态机现在是 Normal。
/// </summary>
private void RefreshAlarmPage ()
{
    RefreshActiveAlarms ();
    RefreshAlarmHistory ();
}

private void RefreshActiveAlarms ()
{
    if (_alarm is null)
    {
        ActiveAlarmList.ItemsSource = null;
        ActiveAlarmHeader.Text = "当前报警(状态机未初始化)";
        return;
    }

    var active = _alarm.GetActiveAlarms ();

    var rows = new List<ActiveAlarmRow> (active.Count);

    foreach (var a in active)
    {
        var durText = "";

        if (a.TriggeredAt != DateTime.MinValue)
        {
            var dur = DateTime.Now - a.TriggeredAt;

            durText = dur.TotalHours >= 1
                ? ((int)dur.TotalHours) + "小时" + dur.Minutes + "分"
                : dur.TotalMinutes >= 1
                    ? ((int)dur.TotalMinutes) + "分" + dur.Seconds + "秒"
                    : ((int)dur.TotalSeconds) + "秒";
        }

        rows.Add (new ActiveAlarmRow
        {
            VarName = a.VarName,
            Comments = CommentsOf (a.VarName),
            DirectionText = a.DirectionText,
            ValueText = double.IsNaN (a.Value) ? "--" : a.Value.ToString ("G6"),
            ThresholdText = "阈值 " + a.Threshold.ToString ("G6"),
            DurationText = durText,
            Note = a.Note ?? ""
        });
    }

    ActiveAlarmList.ItemsSource = rows;

    ActiveAlarmHeader.Text = active.Count == 0
        ? "当前报警(无)"
        : "当前报警 " + active.Count + " 条";
}

private void RefreshAlarmHistory ()
{
    if (_alarmRepo is null)
    {
        AlarmHistoryGrid.ItemsSource = null;
        AlarmHistoryHeader.Text = "历史报警(报警库不可用)";
        return;
    }

    try
    {
        var (from, to) = ResolveAlarmRange ();

        var search = (AlarmSearchBox?.Text ?? "").Trim ();

        var rows = _alarmRepo.Query (
            string.IsNullOrEmpty (search) ? null : search,
            from == DateTime.MinValue ? null : from,
            to,
            1000);

        var list = new List<AlarmHistoryRow> (rows.Count);

        foreach (var r in rows)
        {
            list.Add (new AlarmHistoryRow
            {
                Id = r.Id,
                TimeText = r.At.ToString ("MM-dd HH:mm:ss"),
                VarName = r.VarName,
                Comments = CommentsOf (r.VarName),
                TypeText = r.TypeText,
                DirectionText = r.DirectionText,
                ValueText = r.Value.ToString ("G6"),
                AckText = r.AckText
            });
        }

        AlarmHistoryGrid.ItemsSource = list;

        var unacked = rows.Count (x => !x.IsAcked);

        AlarmHistoryHeader.Text = "历史报警 " + rows.Count + " 条"
            + (unacked > 0 ? "(未确认 " + unacked + " 条)" : "(全部已确认)");

        AlarmPageInfo.Text = "未确认 " + unacked + " 条";
    }
    catch (Exception ex)
    {
        AlarmHistoryHeader.Text = "历史报警(查询失败)";
        AlarmFooterText.Text = "查询失败: " + ex.Message;
    }
}

private void OnAlarmRangeChanged (object sender, SelectionChangedEventArgs e)
{
    if (!IsLoaded) return;

    RefreshAlarmPage ();
}

private void OnAlarmSearchChanged (object sender, TextChangedEventArgs e)
{
    if (!IsLoaded) return;

    RefreshAlarmHistory ();
}

private void OnAlarmRefresh (object sender, RoutedEventArgs e)
{
    RefreshAlarmPage ();
}

/// <summary>确认选中的历史报警。</summary>
private void OnAlarmAckSelected (object sender, RoutedEventArgs e)
{
    if (!HasPermission (KyjScada.Domain.Users.PermissionBit.CanAckAlarm))
    {
        AlarmFooterText.Text = "没有「报警确认」权限,请用有权限的账号登录";
        return;
    }

    if (_alarmRepo is null)
    {
        AlarmFooterText.Text = "报警库不可用,无法确认";
        return;
    }

    var selected = AlarmHistoryGrid.SelectedItems
        .OfType<AlarmHistoryRow> ()
        .ToList ();

    if (selected.Count == 0)
    {
        AlarmFooterText.Text = "请先在历史报警表里选中要确认的行(Ctrl/Shift 可多选)";
        return;
    }

    var now = DateTime.Now;
    var n = 0;

    foreach (var row in selected)
    {
        n += _alarmRepo.Acknowledge (row.Id, now);
    }

    RefreshAlarmHistory ();

    AlarmFooterText.Text = "已确认 " + n + " 条";
}

/// <summary>确认当前范围内的全部未确认报警。</summary>
private void OnAlarmAckAll (object sender, RoutedEventArgs e)
{
    if (!HasPermission (KyjScada.Domain.Users.PermissionBit.CanAckAlarm))
    {
        AlarmFooterText.Text = "没有「报警确认」权限,请用有权限的账号登录";
        return;
    }

    if (_alarmRepo is null)
    {
        AlarmFooterText.Text = "报警库不可用,无法确认";
        return;
    }

    var (from, to) = ResolveAlarmRange ();

    // 只确认当前范围内的,不要把十年前的历史报警一起确认掉
    var n = _alarmRepo.AcknowledgeAll (to, DateTime.Now, from == DateTime.MinValue ? null : from);

    RefreshAlarmHistory ();

    AlarmFooterText.Text = n == 0 ? "没有需要确认的报警" : ("已确认 " + n + " 条");
}

/// <summary>报警页定时刷新(只在该页可见时做,避免无谓 IO)。</summary>
private void TickAlarmPage ()
{
    if (MainTabs.SelectedIndex != 2) return;

    // 2 秒一次:报警列表不需要 500ms 那么勤,也更省库查询
    if (DateTime.Now - _lastAlarmPageRefresh < TimeSpan.FromSeconds (2)) return;

    _lastAlarmPageRefresh = DateTime.Now;

    RefreshActiveAlarms ();
    RefreshAlarmHistory ();
}

/// <summary>刷新底部活动报警栏。无报警时隐藏,有报警时红底显示。</summary>
    private void RefreshAlarmPanel ()
    {
        if  (_alarm is null) return;

        var active = _alarm.GetActiveAlarms ();

        if  (active.Count == 0)
        {
            AlarmPanel.Visibility = Visibility.Collapsed;
            AlarmText.Text = "";
            return;
        }

        AlarmPanel.Visibility = Visibility.Visible;

        var parts = new List<string> (active.Count);
        foreach (var a in active)
        {
            // TriggeredAt 未记录时(刚启动尚无触发事件)不显示持续时间,避免出现天文数字
            var durText = "";
            if  (a.TriggeredAt != DateTime.MinValue)
            {
                var dur = DateTime.Now - a.TriggeredAt;
                durText = "已持续 " + (dur.TotalHours >= 1
                    ? ((int)dur.TotalHours) + "小时"
                    : dur.TotalMinutes >= 1
                        ? ((int)dur.TotalMinutes) + "分钟"
                        : ((int)dur.TotalSeconds) + "秒") + ",";
            }

            var valText = double.IsNaN (a.Value) ? "--" : a.Value.ToString ("F2");

            // 底部报警栏是**所有页面常驻**的,只写 LQB1_Fre_Set 等于让人去背变量表
            parts.Add (NameWithComment (a.VarName, CommentsOf (a.VarName)) + " " + a.DirectionText + "报警(" + durText
                + "当前 " + valText + ", 阈值 " + a.Threshold.ToString ("F2") + ")");
        }

        AlarmText.Text = active.Count + " 个活动报警:  " + string.Join ("    |    ", parts);
    }

    /// <summary>
    /// 重新从磁盘读组态,并把当前设备的对象换成新的一份。
    ///
    /// 只做界面侧能安全重建的部分:写入页/点动页的可写判定(IsWritable、CanWrite)
    /// 全部重新算一遍。采集用的连接/寄存器映射是在流水线里编译的,
    /// 那部分仍要重启程序 —— 提示里照实说。
    /// </summary>
    private async void ReloadConfigFromDisk ()
    {
        try
        {
            var dir = FindSettingsDir () ?? _settingsDir;

            if (dir is null) return;

            var loaded = SettingsJsonCodec.LoadDirectory (dir);

            // 有文件解析失败就**中止重建**:残缺组态会让设备列表整体变样,
            // 下拉框索引错位之后切设备,写入可能打到另一台 PLC 上。
            if (loaded.Skipped.Count > 0)
            {
                FooterText.Text = "重读配置已中止:" + string.Join ("; ", loaded.Skipped.Take (3))
                    + " —— 配置有损坏,请修好后再试(界面仍按旧组态运行)。";

                return;
            }

            var devices = loaded.Project.AllDevices ().ToList ();

            if (devices.Count == 0)
            {
                FooterText.Text = "配置编辑器已关闭:重读后没有任何设备,请检查配置文件(界面仍按旧组态运行)。";

                return;
            }

            var name = _device?.DeviceName;

            var fresh = devices.FirstOrDefault (d =>
                            string.Equals (d.DeviceName, name, StringComparison.OrdinalIgnoreCase))
                        ?? _device;

            _deviceList = devices;
            _deviceFiles = loaded.DeviceFiles;

            // 备注可能被改过:缓存必须清,否则界面还显示旧备注(与写入页/点动页对不上)
            _commentCache.Clear ();

            BuildDeviceSelector ();

            if (fresh is not null)
            {
                _device = fresh;

                // 必须用 IndexOfDevice(下拉框第 0 项是"未选择",设备项 = i+1)。
                // 用 _deviceList.IndexOf 会把高亮移到**前一台**上,
                // 而 _device/_流水线/写入页用的却是后一台 —— 按屏幕操作会写到别的 PLC。
                _switchingDevice = true;
                DeviceBox.SelectedIndex = IndexOfDevice (fresh);
                _switchingDevice = false;
            }

            // 采集服务是按组态**编译**进内存的(寄存器映射、可写点、Scale),
            // 换了组态却继续用旧服务,就会出现“按旧地址发到别的寄存器、还回显写入成功”。
            // 所以这里重建这一台的流水线,闸门持有的 Writer 随之换新。
            // 闸门**实例本身保留**:Enabled、限速表、审计记录都不受影响。
            var reconnected = false;

            if (_device is not null)
            {
                if (_gate is not null) _gate.Writer = null;

                reconnected = await RestartServiceAsync ();
            }
            else
            {
                // 未选设备:没有流水线可重建,保持"不通讯"状态即可
                // (早先这里会 Build(new[]{null}) 直接空引用,整个重读被 catch 吞成失败)
                await ApplyNoDeviceAsync ();
            }

            // 闸门若是第一次建(从没打开过参数设置页),这里补建一次。
            // preserveGate:true —— 重读组态**不能**动写入许可,
            // 否则"关一下配置编辑器"就等于把写入静默关掉,而提示还写着"许可保持原状态"。
            //
            // **只在重建成功时**重建页面:失败时 _svc 已被清空,
            // 拿它建页会把已释放的旧服务重新挂回 _gate.Writer。
            if (reconnected)
            {
                BuildWritePage (preserveGate: true);
                BuildJogPage ();
            }

            FooterText.Text = _device is null
                ? "配置已重新读取。当前未选择设备,不进行通讯 —— 请在上方「设备」下拉框里选择。"
                : reconnected
                    ? "配置已重新读取并重连采集:写入页/点动页按新组态刷新,写入许可保持原状态。"
                    : "配置已重新读取,但重连采集失败:未建立连接,写入已停用。请检查设备/串口后重选设备。";
        }
        catch (Exception ex)
        {
            FooterText.Text = "重读配置失败: " + ex.Message;
        }
    }

    /// <summary>打开配置编辑器(独立窗口)。</summary>
    private void OnOpenConfigEditor (object sender, RoutedEventArgs e)
    {
        if (!HasPermission (KyjScada.Domain.Users.PermissionBit.CanEditConfig))
        {
            FooterText.Text = "没有「配置修改」权限,请用有权限的账号登录";
            return;
        }

        var dir = FindSettingsDir ();

        if (dir is null)
        {
            FooterText.Text = "未找到配置目录,无法打开配置编辑器";
            return;
        }

        // 默认打开当前正在用的那个协议文件(文件名 = <ProtocolKey>.json),没有就退到目录里第一个 json
        var proto = string.IsNullOrWhiteSpace (_device?.ProtocolKey) ? "ModbusTCP" : _device!.ProtocolKey;

        var preferred = Path.Combine (dir, proto + ".json");

        var path = File.Exists (preferred)
            ? preferred
            : Directory.GetFiles (dir, "*.json").OrderBy (f => f, StringComparer.OrdinalIgnoreCase).FirstOrDefault ();

        if (path is null)
        {
            FooterText.Text = "配置目录里没有 json 文件";
            return;
        }

        try
        {
            var win = new ConfigEditorWindow (path) { Owner = this };

            win.ShowDialog ();

            // 编辑完立刻重读组态:用户勾了「允许下发写入」回到主界面还是 0 个点,
            // 会以为勾了没用(实际只是没重启)。这里直接把盘上的配置读回来,
            // 重建写入页/点动页;采集侧改动仍要重启才彻底生效,所以在提示里说清。
            ReloadConfigFromDisk ();
        }
        catch (Exception ex)
        {
            FooterText.Text = "打开配置编辑器失败: " + ex.Message;
        }
    }

    private async void OnOrderChanged (object sender, SelectionChangedEventArgs e)
    {
        // async void 里没有 catch 会直接终止进程:这里必须兜住
        try
        {
            await OnOrderChangedCore (sender, e);
        }
        catch (Exception ex)
        {
            if (!_closing) FooterText.Text = "切换字节序失败: " + ex.Message;
        }
    }

    private async Task OnOrderChangedCore (object sender, SelectionChangedEventArgs e)
    {
        if  (_device is null) return;

        var idx = OrderBox.SelectedIndex;
        if  (idx < 0) return;

        var order = (WordOrder)idx;
        if  (_device.WordOrder == order) return;

        _device.WordOrder = order;

        // 字节序属于设备参数,必须写回该设备自己的配置文件,
        // 否则重启后又变回旧值,现场会以为"设置没生效"
        var saved = false;
        var fileName = "";

        if  (_deviceFiles.TryGetValue (_device.DeviceName, out var filePath))
        {
            saved = SettingsJsonCodec.TryUpdateDeviceWordOrder (filePath, _device.DeviceName, order);
            fileName = Path.GetFileName (filePath);
        }

        var reconnected = await RestartServiceAsync ();

        var prefix = saved
            ? "字节序已设为 " + OrderName (order) + ",并已保存到 " + fileName
            : "字节序已设为 " + OrderName (order) + "(未能写回配置文件,下次启动会恢复)";

        FooterText.Text = reconnected
            ? prefix
            : prefix + " —— 但重建连接失败,采集未启动,写入已停用";
    }

    private static string OrderName (WordOrder o)
    {
        switch (o)
        {
            case WordOrder.Badc: return "BADC";
            case WordOrder.Cdab: return "CDAB";
            case WordOrder.Dcba: return "DCBA";
            default: return "ABCD";
        }
    }

    /// <summary>
    /// 重建当前设备的连接(字节序变更、重读组态共用)。
    ///
    /// 异步:旧流水线的 StopAndWait 最长 2 秒,同步做会让"改一次字节序卡两秒"。
    /// </summary>
    /// <returns>true = 新连接已建立;false = 未建立(调用方**不能**拿旧服务重建页面)。</returns>
    private async Task<bool> RestartServiceAsync ()
    {
        var device = _device;

        if (device is null)
        {
            // 未选设备:没有可重建的对象,把引用彻底清干净
            ClearReleasedService ();

            return false;
        }

        // 重建前先摘掉 Writer:在途写入不能打到即将释放的旧服务上
        if (_gate is not null) _gate.Writer = null;

        // 字节序是连接参数,必须重建该设备的连接才能生效。
        // 只重启这一台,别把其它设备的采集一起打断。
        var coord = EnsureCoordinator ();

        DevicePipeline? pipeline;

        try
        {
            pipeline = await coord.RestartAsync (device).ConfigureAwait (true);
        }
        catch (Exception ex)
        {
            FooterText.Text = "重建连接失败: " + ex.Message;

            // 失败时把引用清干净 —— 继续持有已 Dispose 的服务,
            // 后面 BuildWritePage 会把 _gate.Writer 又挂回那个废对象上,
            // 结果是"按旧地址发到别的寄存器"或者直接崩。
            ClearReleasedService ();

            return false;
        }

        // 期间用户又切了设备 / 有更新的重建跑过:本次结果作废
        if (!ReferenceEquals (_device, device) || pipeline is null)
        {
            ClearReleasedService ();

            return false;
        }

        _svc = pipeline.Service;
        _pipeline = pipeline;
        _bus = pipeline.Bus;
        _alarm = pipeline.Alarm;
        _archive = pipeline.Archive;

        if (_gate is not null) _gate.Writer = _svc as IDeviceWriter;

        return true;
    }

    /// <summary>
    /// 连接已释放后把界面引用清干净。
    ///
    /// 只置 _gate.Writer=null 是不够的:BuildWritePage 会拿 _svc 重新挂回 Writer,
    /// 于是"清空"被下一次建页抵消掉。_svc/_pipeline 必须一起清。
    /// </summary>
    private void ClearReleasedService ()
    {
        _svc = null;
        _pipeline = null;

        if (_gate is not null) _gate.Writer = null;
    }
    /// <summary>按变量类型格式化。Bool 不显示 0.00/1.00,整数不带小数位。</summary>
    private static string FormatValue (double v, string varType)
    {
        var t = (varType ?? "").Trim ().ToLowerInvariant ();

        switch (t)
        {
            case "bool": return v != 0 ? "ON" : "OFF";
            case "byte": case "short": case "ushort": case "int": case "int32":
            case "uint": case "uint32": case "word": case "dword":
            case "long": case "int64": case "ulong": case "uint64":
                return ((long)Math.Round (v)).ToString ();
            default:
                return v.ToString ("F2");
        }
    }

    private static string QualityText (TagQuality q)
    {
        switch (q)
        {
            case TagQuality.Good: return "正常";
            case TagQuality.Uncertain: return "可疑";
            case TagQuality.Bad: return "通讯失败";
            case TagQuality.NotCollected: return "未采集";
            default: return "未知";
        }
    }

    /// <summary>找配置目录(settings / Settings)。</summary>
    private static string FindSettingsDir ()
    {
        var baseDir = AppContext.BaseDirectory;

        foreach (var name in new[] { "settings", "Settings" })
        {
            var dir = Path.Combine (baseDir, name);
            if (Directory.Exists (dir)) return dir;
        }

        return null;
    }

    private void OnClosed (object sender, EventArgs e)
    {
        // 先立标志再停定时器:在途的异步重建延续会据此直接放弃,
        // 不会在窗口关掉之后又把采集/定时器拉起来。
        _closing = true;

        // 先停界面定时器,再停各设备流水线;采集服务由流水线负责释放
        if (_acqTimer != null) { _acqTimer.Stop (); _acqTimer = null; }

        if (_uiTimer != null) _uiTimer.Stop ();

        // MCP 要在通道释放前关:它可能还有在途请求要读写。
        // 用后台停:写工具会 Dispatcher.Invoke 回界面线程,这里同步等 StopAsync
        // 会和那条在途请求互相等待,关窗时偶发卡死。状态已同步清空,不会再有新写请求。
        McpHost.StopInBackground ();

        _coordinator?.Dispose ();
        _coordinator = null;
        _pipeline = null;
        _svc = null;
    }
}
