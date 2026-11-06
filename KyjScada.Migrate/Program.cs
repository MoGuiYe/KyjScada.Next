using KyjScada.Infrastructure.Data;

namespace KyjScada.Migrate;

/// <summary>
/// 老系统数据迁移工具(一次性)。
///
/// 用法:
///   KyjScada.Migrate --from "老库.db" --to "history.db" [--device 设备名] [--dry-run]
///
/// 为什么做成独立命令行而不是界面按钮:
///  迁移只在换系统时做一次。做成按钮就得处理"重复点""中途取消""库被占"三种状态,
///  而命令行天然可预演(--dry-run)、可重跑、可在大库上跑几十分钟不担心界面卡死。
/// </summary>
public static class Program
{
    public static int Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;

        if (args.Length == 0 || args.Contains("--help") || args.Contains("-h"))
        {
            PrintUsage();

            return args.Length == 0 ? 2 : 0;
        }

        var from = Value(args, "--from");
        var to = Value(args, "--to");

        if (string.IsNullOrWhiteSpace(from) || string.IsNullOrWhiteSpace(to))
        {
            Console.WriteLine("缺参数: --from 与 --to 都必须给。\n");
            PrintUsage();

            return 2;
        }

        if (Path.GetFullPath(from) == Path.GetFullPath(to))
        {
            Console.WriteLine("源库和目标库是同一个文件,拒绝执行(会污染老库)。");

            return 2;
        }

        if (!File.Exists(from))
        {
            Console.WriteLine("找不到老库: " + from);

            return 2;
        }

        var device = Value(args, "--device") ?? "老系统迁移";

        if (args.Contains("--dry-run"))
        {
            return DryRun(from, to, device);
        }

        Console.WriteLine("开始迁移(不覆盖新库已有数据,可重复执行)...\n");

        var migrator = new LegacyMigrator(from, to) { DeviceName = device };

        try
        {
            var report = migrator.Run();

            Console.WriteLine(report.Describe());

            return report.MigratedRows > 0 ? 0 : 1;
        }
        catch (Exception ex)
        {
            Console.WriteLine("迁移失败: " + ex.Message);
            Console.WriteLine(ex.StackTrace);

            return 1;
        }
    }

    /// <summary>只读老库、不写新库:先让现场看清"能迁多少、会跳过多少"。</summary>
    private static int DryRun(string from, string to, string device)
    {
        // 预演库放在目标库同目录 —— 不用系统 TEMP,现场机器上 TEMP 常被组策略限制
        var targetDir = Path.GetDirectoryName(Path.GetFullPath(to)) ?? ".";

        var probeTarget = Path.Combine(targetDir, "_kyj_dryrun_" + Guid.NewGuid().ToString("N") + ".db");

        Console.WriteLine("预演模式:只统计,不写正式库(" + to + ")。\n");

        try
        {
            var migrator = new LegacyMigrator(from, probeTarget) { DeviceName = device };
            var report = migrator.Run();

            Console.WriteLine(report.Describe());
            Console.WriteLine("\n预演写入的临时库: " + probeTarget + "(可直接删除)");

            return 0;
        }
        finally
        {
            TryDelete(probeTarget);
            TryDelete(probeTarget + "-wal");
            TryDelete(probeTarget + "-shm");
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
            // 临时文件删不掉不影响迁移结果
        }
    }

    private static string? Value(string[] args, string key)
    {
        var i = Array.IndexOf(args, key);

        if (i < 0 || i + 1 >= args.Length) return null;

        return args[i + 1].Trim('"');
    }

    private static void PrintUsage()
    {
        Console.WriteLine(
            "老系统数据迁移工具 — 把老库 ActualData 宽表搬成新系统的长表 History\n" +
            "\n" +
            "参数:\n" +
            "  --from <老库路径>     必填。老系统的 sqlite 库(含 ActualData / SysLog)\n" +
            "  --to   <新库路径>     必填。新系统 history.db(不存在会自动建表)\n" +
            "  --device <设备名>     选填。写进 VarDict 用于追溯来源,默认「老系统迁移」\n" +
            "  --dry-run             选填。只统计不落正式库,先看能迁多少\n" +
            "\n" +
            "特点:\n" +
            "  · 只 INSERT 不 DROP,重复执行安全\n" +
            "  · 空串 / -- / 非数字一律跳过,绝不写成 0\n" +
            "  · 老 SysLog 原样存进 LegacySysLog,不改写成报警\n" +
            "\n" +
            "示例:\n" +
            "  KyjScada.Migrate --from D:\\old\\KYJ.db --to D:\\app\\data\\history.db --dry-run\n" +
            "  KyjScada.Migrate --from D:\\old\\KYJ.db --to D:\\app\\data\\history.db");
    }
}