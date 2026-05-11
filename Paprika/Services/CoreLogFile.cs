using System.Text;

namespace Paprika.Services;

internal static class CoreLogFile
{
    public static IReadOnlyList<string> ReadLinesShared(string logPath)
    {
        // mihomo 运行时会长期持有日志写入句柄；读取端必须允许共享读写，
        // 否则“实时日志”和“最近 100 条”会在 Windows 上偶发文件占用错误。
        using var stream = new FileStream(
            logPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);

        var lines = new List<string>();
        while (reader.ReadLine() is { } line)
        {
            lines.Add(line);
        }

        return lines;
    }
}
