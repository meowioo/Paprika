namespace Paprika.Models;

public sealed record MihomoTrafficRate(
    long UploadBytesPerSecond,
    long DownloadBytesPerSecond,
    string? ErrorMessage = null)
{
    public bool IsAvailable => string.IsNullOrWhiteSpace(ErrorMessage);

    public static MihomoTrafficRate Unavailable(string message)
    {
        return new MihomoTrafficRate(0, 0, message);
    }
}
