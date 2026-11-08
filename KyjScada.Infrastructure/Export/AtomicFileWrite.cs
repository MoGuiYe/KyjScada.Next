namespace KyjScada.Infrastructure.Export;

/// <summary>
/// 导出文件的落盘收尾:先把内容写进 <c>目标.tmp</c>,再原子替换目标文件。
///
/// 为什么值得单独一个类:
///  1. 三个导出入口(趋势 CSV / 报表 CSV / 组态变量表)原来各抄了一份"Replace 失败就
///     先 Delete 再 Move"的代码。那段写法在 Delete 之后 Move 失败时会把用户原来的文件弄没,
///     而它恰恰是给"目标被 Excel 占用"这种最常见场景准备的兜底 —— 越是最常见越不能丢文件。
///  2. 中途抛异常时 .tmp 会永远留在导出目录里。现场习惯反复点"导出",每次留一个几十 MB
///     的临时文件,最后把磁盘塞满。
///
/// 这里的原则:任何失败路径都不能让目标文件消失,也不能静默丢掉 .tmp 里唯一的那份新内容。
/// </summary>
public static class AtomicFileWrite
{
    /// <summary>
    /// 用已写好的临时文件替换目标文件。成功返回后 <paramref name="tmpPath"/> 不再存在;
    /// 失败则抛异常,并已尽力清掉临时文件、保住原目标。
    /// </summary>
    /// <param name="tmpPath">内容已写完整(已关闭)的临时文件</param>
    /// <param name="targetPath">要被替换的目标文件(可不存在)</param>
    /// <exception cref="FileNotFoundException">临时文件不存在(调用方写盘就没成功)</exception>
    /// <exception cref="IOException">替换失败;若回滚也失败,异常消息里带着两个文件的实际位置</exception>
    public static void Replace(string tmpPath, string targetPath)
    {
        if (!File.Exists(tmpPath)) throw new FileNotFoundException("临时文件不存在", tmpPath);

        if (!File.Exists(targetPath))
        {
            // 目标不存在:同卷 Move 本身就是原子的
            try
            {
                File.Move(tmpPath, targetPath);
            }
            catch
            {
                TryDelete(tmpPath);

                throw;
            }

            return;
        }

        // 首选:系统级替换。同卷内原子,且保留目标文件的 ACL/创建时间。
        try
        {
            File.Replace(tmpPath, targetPath, null);

            return;
        }
        catch
        {
            // 部分文件系统(FAT/exFAT、网络盘)不支持,或被别的进程握着句柄
        }

        // 次选:覆盖式移动。Windows 走 MoveFileEx(替换已存在),同卷内同样原子。
        try
        {
            File.Move(tmpPath, targetPath, overwrite: true);

            return;
        }
        catch
        {
            // 跨卷或被独占:只能走"先挪开旧的、再挪进新的"
        }

        // 兜底单独成方法:它内部要"回滚失败仍保留文件",不能被外层 catch 再清一遍。
        ReplaceByRename(tmpPath, targetPath);
    }

    /// <summary>
    /// 最后的兜底:把旧文件改名腾位(改名是原子操作,内容仍在),再把新文件挪进来;
    /// 失败则把旧文件改回原名。
    /// </summary>
    private static void ReplaceByRename(string tmpPath, string targetPath)
    {
        // 1. .old 名字必须唯一。用固定的 target.old + overwrite:true 会把上一次失败
        //    留下的 .old(那是唯一还能救回来的旧内容)直接覆盖掉。
        var oldPath = targetPath + "." + Guid.NewGuid().ToString("N") + ".old";

        try
        {
            File.Move(targetPath, oldPath, overwrite: true);
        }
        catch
        {
            // 连改名都做不成(目标被独占且不允许改名):目标还在原位,清掉 tmp 报错即可
            TryDelete(tmpPath);

            throw;
        }

        try
        {
            File.Move(tmpPath, targetPath);
        }
        catch (Exception moveEx)
        {
            try
            {
                File.Move(oldPath, targetPath, overwrite: true);
            }
            catch (Exception rollbackEx)
            {
                // 2. 回滚失败意味着目标位置已经空了。此时**什么都不删**:
                //    .tmp 是这次唯一的新内容,.old 是唯一的旧内容,都留给现场手工恢复,
                //    并把两个真实路径写进异常 —— 只说"导出失败"等于让人无处下手。
                throw new IOException(
                    "替换失败且回滚失败。旧文件保留在 " + oldPath + ",新内容保留在 " + tmpPath,
                    new AggregateException(moveEx, rollbackEx));
            }

            TryDelete(tmpPath);

            throw;
        }

        TryDelete(oldPath);
    }

    /// <summary>
    /// 测试入口:直接把"兜底改名替换"这条路径暴露出来。
    ///
    /// 正常运行时它只在 <c>File.Replace</c> 和覆盖式 Move 都失败时才会走到,
    /// 而这两种失败在单测里没法在同一卷上稳定制造(.NET 的跨卷 Move 已经会
    /// 自动退化成复制+删除)。不暴露的话,"第一步失败之后必须把旧文件改回来"
    /// 这条保命逻辑永远没有用例覆盖 —— 而那正是现场唯一还能救回文件的分支。
    /// </summary>
    internal static void ReplaceViaRename(string tmpPath, string targetPath)
    {
        ReplaceByRename(tmpPath, targetPath);
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch
        {
            // 删不掉就算了:不能因为清理临时文件盖掉原始异常
        }
    }
}