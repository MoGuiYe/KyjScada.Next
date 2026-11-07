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
/// 这里的原则:任何失败路径都不能让目标文件消失,也不能留下 .tmp。
/// </summary>
public static class AtomicFileWrite
{
    /// <summary>
    /// 用已写好的临时文件替换目标文件。成功返回后 <paramref name="tmpPath"/> 不再存在;
    /// 失败则抛异常,并已尽力清掉临时文件、保住原目标。
    /// </summary>
    /// <param name="tmpPath">内容已写完整(已关闭)的临时文件</param>
    /// <param name="targetPath">要被替换的目标文件(可不存在)</param>
    public static void Replace(string tmpPath, string targetPath)
    {
        if (!File.Exists(tmpPath)) throw new FileNotFoundException("临时文件不存在", tmpPath);

        try
        {
            if (!File.Exists(targetPath))
            {
                // 目标不存在:同卷 Move 本身就是原子的
                File.Move(tmpPath, targetPath);

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

            // 兜底:把旧文件改名(改名是原子操作,内容仍在),腾出位置后再 Move 新的进来。
            // 失败就把旧文件改回去 —— 绝不能像原来那样 File.Delete 之后才发现 Move 不行。
            var oldPath = targetPath + ".old";

            File.Move(targetPath, oldPath, overwrite: true);

            try
            {
                File.Move(tmpPath, targetPath);
            }
            catch
            {
                File.Move(oldPath, targetPath, overwrite: true);

                throw;
            }

            TryDelete(oldPath);
        }
        catch
        {
            TryDelete(tmpPath);

            throw;
        }
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