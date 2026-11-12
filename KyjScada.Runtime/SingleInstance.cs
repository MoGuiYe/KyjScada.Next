using System.Security.Cryptography;
using System.Text;

namespace KyjScada.Runtime;

/// <summary>
/// 上位机单实例守卫:**同一套安装只允许一个进程在跑**。
///
/// 为什么必须限定单实例(不是"最好只开一个"):
///  1. **串口只能被打开一次**。第二个进程打开同一个 COM 口直接失败,
///     或者(更糟)抢走第一个进程的句柄,采集看起来还在跑,实际一个点都不更新;
///  2. **历史库/报警库/用户库是同一批 SQLite 文件**。两个进程同时在 WAL 下写,
///     会撞 SQLITE_BUSY;各自的"重试 + 补写"自愈逻辑互相踩,表现为数据断段与偶发写库失败;
///  3. **MCP 端口只能绑一次**。第二个进程的 Kestrel 起不来,页脚报"启动失败",
///     操作工以为程序坏了;
///  4. 采集/归档/看门狗都是按"本进程独占设备"设计的,
///     两套自愈同时对一个设备动作,只会让现场更难判断到底是谁在写。
///
/// 实现要点:
///  · 命名 <see cref="Mutex"/> 判所有权。**不能用"某个文件存在"判**:进程崩溃后文件还在,
///    下次永远起不来;内核对象在进程退出时由系统回收,没有这个残留问题。
///  · 优先 <c>Global\</c>(跨会话,锁住整机);普通用户没有建全局对象的权限时
///    退到 <c>Local\</c>(本会话)—— 绝不因为权限问题把用户挡在门外。
///  · 名称里带**安装目录的哈希**:同一台机器上放两套不同版本/不同现场组态的副本时,
///    它们本来就是两套独立系统,不该互相阻塞。
///  · 额外的命名 <see cref="EventWaitHandle"/> 用来"把已经在跑的那个窗口叫到前台",
///    让重复启动的提示落到实处,而不是只丢一句"已在运行"。
/// </summary>
public sealed class SingleInstanceGuard : IDisposable
{
    /// <summary>名称前缀。改这里等于让所有现场同时失去单实例保护,别动。</summary>
    private const string Prefix = "KyjScada.Next.SingleInstance.";

    /// <summary>所有权作用域,按优先级排列:先整机,没权限再退本会话。</summary>
    private static readonly string[] Scopes = { "Global", "Local" };

    private readonly string _hash;

    private Mutex? _mutex;
    private EventWaitHandle? _wake;
    private Thread? _listener;

    private string? _scope;
    private int _ownerThreadId;
    private bool _holdsMutex;

    private volatile bool _stop;

    /// <summary>
    /// 连命名内核对象都建不出来(极端受限环境)。此时**放行启动** ——
    /// 因为没有单实例保护也比让现场完全打不开上位机更可接受;
    /// 真正的冲突(串口占用/端口占用)仍会在后面各自报错。
    /// </summary>
    public bool Degraded { get; private set; }

    private SingleInstanceGuard(string identity)
    {
        _hash = Hash(identity);
    }

    /// <summary>
    /// 尝试取得"第一个实例"的身份。
    /// </summary>
    /// <param name="identity">用于区分不同安装目录,通常传 exe 所在目录。</param>
    /// <param name="isFirst">true = 本进程是唯一实例;false = 已经有一个在跑。</param>
    public static SingleInstanceGuard Acquire(string identity, out bool isFirst)
    {
        var guard = new SingleInstanceGuard(identity ?? "");

        guard.AcquireCore(out isFirst);

        return guard;
    }

    private void AcquireCore(out bool isFirst)
    {
        isFirst = false;

        foreach (var scope in Scopes)
        {
            Mutex mutex;

            try
            {
                mutex = new Mutex(false, MutexName(scope), out _);
            }
            catch (UnauthorizedAccessException)
            {
                // 普通用户默认没有 SeCreateGlobalPrivilege:换 Local,不是故障
                continue;
            }
            catch (IOException)
            {
                continue;
            }

            bool acquired;

            try
            {
                // 必须用 WaitOne(0) 判所有权,**不能看 createdNew**:
                // createdNew=false 只说明"同名对象已存在",上一个进程崩溃后对象可能还在
                // 但已无人持有,那种情况必须允许启动。
                acquired = mutex.WaitOne(0);
            }
            catch (AbandonedMutexException)
            {
                // 上一个进程是被强杀/崩溃掉的。内核已经把所有权移交给我们 —— 正常启动。
                acquired = true;
            }
            catch (ObjectDisposedException)
            {
                continue;
            }

            _scope = scope;
            _mutex = mutex;

            if (!acquired)
            {
                // 有人在跑:放手,只留着去"叫醒"它
                mutex.Dispose();
                _mutex = null;

                return;
            }

            _holdsMutex = true;
            _ownerThreadId = Environment.CurrentManagedThreadId;
            isFirst = true;

            return;
        }

        Degraded = true;
        isFirst = true;
    }

    /// <summary>
    /// 第一个实例调用:开始监听"有人又想开一个"的通知。
    /// 收到通知时回调(回调在监听线程上,调用方自己回 UI 线程)。
    /// </summary>
    public void StartListening(Action onActivate)
    {
        if (!_holdsMutex || Degraded || _scope is null) return;

        try
        {
            _wake = new EventWaitHandle(false, EventResetMode.AutoReset, EventName(_scope));
        }
        catch (UnauthorizedAccessException)
        {
            return;
        }
        catch (IOException)
        {
            return;
        }

        var wake = _wake;

        _listener = new Thread(() =>
        {
            // 带超时的等待:停止时不用靠"再 Set 一次"来唤醒,也不会因为漏一次通知就永远阻塞
            while (!_stop)
            {
                bool signaled;

                try
                {
                    signaled = wake.WaitOne(500);
                }
                catch (ObjectDisposedException)
                {
                    return;
                }
                catch (AbandonedMutexException)
                {
                    return;
                }

                if (!signaled) continue;

                try
                {
                    onActivate();
                }
                catch
                {
                    // 回调(切窗口)失败不能打死监听线程,否则以后再开第二个就静默无反应
                }
            }
        })
        {
            IsBackground = true,
            Name = "single-instance-listener"
        };

        _listener.Start();
    }

    /// <summary>
    /// 第二个实例调用:尝试把正在运行的那个窗口叫到前台。
    /// 返回 false 表示没找到(对方刚退出,或通知通道不可用)—— 提示语要照实说。
    /// </summary>
    public bool SignalExistingInstance()
    {
        foreach (var scope in Scopes)
        {
            EventWaitHandle? existing = null;

            try
            {
                if (!EventWaitHandle.TryOpenExisting(EventName(scope), out existing)) continue;
            }
            catch (UnauthorizedAccessException)
            {
                continue;
            }
            catch (IOException)
            {
                continue;
            }

            try
            {
                existing.Set();

                return true;
            }
            catch (ObjectDisposedException)
            {
                // 对方正在退出:继续试下一个作用域
            }
            finally
            {
                existing.Dispose();
            }
        }

        return false;
    }

    private string MutexName(string scope) => scope + @"\" + Prefix + _hash;

    private string EventName(string scope) => scope + @"\" + Prefix + _hash + ".Activate";

    /// <summary>
    /// 稳定哈希。**不能用 string.GetHashCode()** —— 它按进程随机化,
    /// 同一次运行里相同路径得到不同值,两个进程会各自建一个互不相干的内核对象,
    /// 单实例保护等于没做。
    /// </summary>
    private static string Hash(string identity)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(identity ?? ""));

        var sb = new StringBuilder(16);

        for (var i = 0; i < 8; i++) sb.Append(bytes[i].ToString("x2"));

        return sb.ToString();
    }

    public void Dispose()
    {
        _stop = true;

        // 立刻叫醒监听线程(它正在 WaitOne(500)),不用干等半秒
        try { _wake?.Set(); } catch { /* 已释放/无权限:忽略 */ }

        // 监听线程是后台线程,最多 500ms 自己退;这里**不 Join** ——
        // 关窗路径上多等一次就是在界面线程上白卡,而这个线程本来不影响进程退出。
        _listener = null;

        if (_holdsMutex && _mutex is not null)
        {
            // ReleaseMutex 必须由**取得所有权的那条线程**调用,否则抛 ApplicationException。
            // 正常路径(OnStartup → OnExit)都在界面线程,这里只是防御。
            if (Environment.CurrentManagedThreadId == _ownerThreadId)
            {
                try { _mutex.ReleaseMutex(); } catch { /* 已释放:忽略 */ }
            }

            _holdsMutex = false;
        }

        try { _wake?.Dispose(); } catch { }
        try { _mutex?.Dispose(); } catch { }

        _wake = null;
        _mutex = null;
    }
}