using System.Windows;
using System.Windows.Threading;

namespace KyjScada.App;

public partial class App : Application
{
    protected override void OnStartup (StartupEventArgs e)
    {
        base.OnStartup (e);

        // async void 事件处理器(写入/急停/点动)里的异常会直接冒到 Dispatcher:
        // 不兜住就是整个进程退出、窗口凭空消失。这里记录并提示,让操作工知道"刚才没生效"。
        DispatcherUnhandledException += (_, args) =>
        {
            try
            {
                MessageBox.Show (
                    "程序捕获到未处理异常:\n\n" + args.Exception.Message +
                    "\n\n刚才的操作可能没有生效,请检查连接/权限后重试。",
                    "未处理异常", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            catch
            {
                // 提示框本身失败时不要再抛第二轮
            }

            args.Handled = true;
        };
    }
}