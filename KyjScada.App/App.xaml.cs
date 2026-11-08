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

    protected override void OnStartup(StartupEventArgs e)
    {
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