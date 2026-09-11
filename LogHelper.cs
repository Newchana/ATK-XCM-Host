using System.IO;

namespace XcmHost;

/// <summary>
/// 统一文件日志：%TEMP%/ATK_XCM/XcmHost.log，带大小轮转（超 2MB 则备份为 .1 后重建），
/// 避免托盘常驻运行时日志无限增长。
/// </summary>
internal static class LogHelper
{
    private static readonly string Dir = Path.Combine(Path.GetTempPath(), "ATK_XCM");
    private static readonly string Path_ = Path.Combine(Dir, "XcmHost.log");
    private const long MaxBytes = 2 * 1024 * 1024;

    public static void Write(string tag, string message)
    {
        try
        {
            Directory.CreateDirectory(Dir);
            var info = new FileInfo(Path_);
            if (info.Exists && info.Length > MaxBytes)
            {
                string bak = Path_ + ".1";
                try { File.Delete(bak); } catch { /* ignore */ }
                try { File.Move(Path_, bak); } catch { /* ignore */ }
            }
            File.AppendAllText(Path_,
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [{tag}] {message}{Environment.NewLine}");
        }
        catch { /* 日志失败不能影响主流程 */ }
    }
}
