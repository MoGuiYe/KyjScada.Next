using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Threading;

namespace KyjScada.App;

public partial class App : Application
{
    /// <summary>一分钟内最多弹几次"未处理异常"。连续刷屏会盖住真正要看的那个提示。</summary>
    private int _dialogsShown;
    private DateTime _dialogWindowStart = DateTime.MinValue;

    /// <summary>单实例守卫。整个进程生命周期持有,退出时释放。</summary>
    private KyjScada.Runtime.SingleInstanceGuard? _instance;

    protected override void OnStartup(StartupEventArgs e)
    {
        // ==================== 单实例:必须在一切初始化之前 ====================
        // 上位机多开会互相抢串口、同时写三个 SQLite 库、抢同一个 MCP 端口。
        // 这几条路径各自都有自愈逻辑,但它们自愈的前提恰恰是"只有一个进程"——
        // 两个进程同时自愈只会互相踩。所以这里先判,判不过就直接退出。
        if (!EnsureSingleInstance()) return;

        base.OnStartup(e);

        // async void 事件处理器(写入/急停/点动)里的异常会直接冒到 Dispatcher:
        // 不兜住就是整个进程退出、窗口凭空消失;但**不能无条件 Handled=true** ——
        // 内存耗尽/栈损坏这类异常继续跑下去,后面每一个操作都是在损坏状态上继续,
        // 现场看到的是"越点越乱、数据不对",比直接退出危险得多。
        DispatcherUnhandledException += (_, args) =>
        {
            var fatal = IsFatal(args.Exception);

            // 落盘完整堆栈:现场只有 exe,没有调试器,不留日志等于事后完全无法定位。
            WriteCrashLog("界面线程", args.Exception, fatal);

            if (fatal)
            {
                // 受控退出:先尽力留下可读提示,再以非 0 退出码结束,让外面的看门狗能发现。
                try
                {
                    MessageBox.Show(
                        "程序遇到无法继续运行的错误,即将退出:\n\n" + args.Exception.Message +
                        "\n\n详细信息已写入 crash.log,请把该文件发给技术支持。",
                        "致命错误", MessageBoxButton.OK, MessageBoxImage.Error);
                }
                catch
                {
                    // 提示框本身失败时直接退出
                }

                args.Handled = true;
                Shutdown(1);

                return;
            }

            if (ShouldShowDialog())
            {
                try
                {
                    MessageBox.Show(
                        "程序捕获到未处理异常:\n\n" + args.Exception.Message +
                        "\n\n刚才的操作可能没有生效,请检查连接/权限后重试。\n" +
                        "详细信息已写入 crash.log。",
                        "未处理异常", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
                catch
                {
                    // 提示框本身失败时不要再抛第二轮
                }
            }

            // 普通业务异常:记完日志继续跑,不能让一次点击失败把窗口关掉
            args.Handled = true;
        };

        // 后台线程的异常(采集线程、Task.Run)不会走 Dispatcher 那条路。
        // 这一条无法阻止进程退出,但至少要留下堆栈 —— 否则现场只看到"程序没了"。
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception ex)
            {
                WriteCrashLog("后台线程", ex, true);
            }
        };

        // 忘记 await 的 Task 抛出的异常:默认要等 GC 才触发,而且抛在终结器线程上。
        // 这里标记为已观察 + 写日志,避免"莫名其妙的进程崩溃"。
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            WriteCrashLog("未观察的任务", args.Exception, false);
            args.SetObserved();
        };

        // ==================== 主窗口在这里显式创建 ====================
        // 不再用 App.xaml 的 StartupUri:那条路径由 WPF 在 OnStartup **返回之后**自动建窗口,
        // 于是"判出重复启动并 Shutdown"与"窗口照建不误"谁先谁后取决于框架内部状态,
        // 现场可能看到第二个窗口闪一下就没了。显式创建则顺序完全确定。
        var window = new MainWindow();

        MainWindow = window;

        window.Show();
    }

    /// <summary>
    /// 单实例判定 + 重复启动提示。
    /// 返回 false 表示本次是"多开",已经提示过并准备退出,调用方必须直接 return。
    /// </summary>
    private bool EnsureSingleInstance()
    {
        // 以 exe 所在目录作为身份:同一台机器上放两套独立安装(不同现场组态/不同版本)
        // 本来就是两套系统,不该互相阻塞;同一套安装复制到别处也一样。
        _instance = KyjScada.Runtime.SingleInstanceGuard.Acquire(AppContext.BaseDirectory, out var isFirst);

        if (_instance.Degraded)
        {
            // 极端受限环境连命名内核对象都建不出来。宁可放行也不能让现场完全打不开,
            // 真正的冲突(串口/端口占用)后面各自会报出来。
            return true;
        }

        if (isFirst)
        {
            // 有人再开一个时,把本窗口叫到前台 —— 让"不允许重复启动"这句话有下文,
            // 而不是让人以为程序点了没反应。
            _instance.StartListening(() => Dispatcher.BeginInvoke(new Action(BringMainWindowToFront)));

            return true;
        }

        // 先叫醒已有实例(失败了也要照实提示,不能假装切过去了)
        var raised = _instance.SignalExistingInstance();

        _instance.Dispose();
        _instance = null;

        ShowAlreadyRunningMessage(raised);

        // 用 0 退出:这是**正常的用户操作被拒绝**,不是程序故障;
        // 返回非 0 会让现场看门狗/计划任务把它当成崩溃去重试。
        Shutdown(0);

        return false;
    }

    private void ShowAlreadyRunningMessage(bool raised)
    {
        var tail = raised
            ? "已尝试把正在运行的那个窗口切到前台。"
            : "正在运行的那个窗口没能唤到前台,请到任务栏里找一下(程序可能被最小化了)。";

        try
        {
            MessageBox.Show(
                "上位机已经在运行,**不允许重复启动**。\n\n"
                + "同时开两个会出这些问题:\n"
                + "  · 两个进程抢同一个串口/网口 —— 采集会断,写值会失败;\n"
                + "  · 两个进程同时写历史库和报警库 —— 会锁库、数据会断段;\n"
                + "  · MCP 端口只能被一个进程占用 —— 第二个进程的 MCP 起不来。\n\n"
                + tail,
                "不允许重复启动",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
        catch
        {
            // 提示框本身失败(极端会话限制)也不能继续启动
        }
    }

    /// <summary>把已有主窗口叫到前台:还原最小化、激活、抢一次前台焦点。</summary>
    private void BringMainWindowToFront()
    {
        var window = MainWindow;

        if (window is null) return;

        try
        {
            if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;

            window.Show();
            window.Activate();

            // Windows 有"前台锁":后台进程直接 Activate 常常只闪一下任务栏。
            // 瞬时置顶再取消是业界常用的强制前台手法,对用户可见的副作用只有一次重绘。
            var previous = window.Topmost;

            window.Topmost = true;
            window.Topmost = previous;

            window.Focus();
        }
        catch
        {
            // 窗口正在关闭等情况下激活会抛:不能让监听线程因此退出
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        // 释放单实例所有权。不释放的话,虽然进程退出后内核对象会被回收,
        // 但在"关闭窗口 → 进程还没退出"的那一小段里,再点图标会被误判成多开。
        try { _instance?.Dispose(); } catch { }
        _instance = null;

        base.OnExit(e);
    }

    /// <summary>
    /// 判断"继续跑是否还有意义"。
    /// 这几类异常意味着进程状态已经不可信(内存/栈/元数据),继续执行只会产生错误数据。
    /// </summary>
    private static bool IsFatal(Exception ex)
    {
        return ex is OutOfMemoryException
            or AccessViolationException
            or InsufficientExecutionStackException
            or BadImageFormatException
            or CannotUnloadAppDomainException;
    }

    private bool ShouldShowDialog()
    {
        var now = DateTime.Now;

        if ((now - _dialogWindowStart).TotalMinutes >= 1)
        {
            _dialogWindowStart = now;
            _dialogsShown = 0;
        }

        _dialogsShown++;

        // 第 4 次起只写日志:连续弹窗会把界面卡在一个关不掉的框上
        return _dialogsShown <= 3;
    }

    /// <summary>
    /// 把完整堆栈追加到 exe 旁边的 crash.log。
    /// 写日志本身失败(磁盘满/只读目录)绝不能再抛 —— 那会把原本要报的异常换成写日志异常。
    /// </summary>
    private static void WriteCrashLog(string source, Exception ex, bool fatal)
    {
        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, "crash.log");

            // 单文件上限 2MB:现场常年不清理,不能让日志把磁盘吃掉
            if (File.Exists(path) && new FileInfo(path).Length > 2 * 1024 * 1024)
            {
                var old = path + ".old";

                try
                {
                    if (File.Exists(old)) File.Delete(old);
                    File.Move(path, old);
                }
                catch
                {
                    // 轮转失败就直接往后面追加(文件已经超限也不会致命)
                }
            }

            var text = new StringBuilder();
            text.AppendLine("==== " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff")
                                         + " [" + source + "] " + (fatal ? "致命" : "可恢复") + " ====");
            text.AppendLine(ex.ToString());
            text.AppendLine();

            File.AppendAllText(path, text.ToString(), new UTF8Encoding(false));
        }
        catch
        {
            // 见上:日志失败不改变异常处理流程
        }
    }
}