using System.Text.RegularExpressions;

namespace DJTrans.Core.Scan;

public enum MediaKind
{
    Photo,
    Video,
    Proxy,     // .lrf 低码流代理
    Subtitle,  // .srt
    Other,
}

/// <summary>设备上的单个媒体文件（不可变快照）。</summary>
public sealed record MediaItem
{
    public required string Path { get; init; }
    public required string Name { get; init; }
    public required MediaKind Kind { get; init; }
    public required long SizeBytes { get; init; }
    public required DateTime ModifiedUtc { get; init; }
    /// <summary>从文件名解析出的拍摄时间（本地时区）；解析失败为 null，展示时回退 ModifiedUtc。</summary>
    public DateTime? TakenLocal { get; init; }
    /// <summary>相对扫描根（DCIM）的目录，如 "DJI_001"。</summary>
    public required string RelativeDir { get; init; }
    /// <summary>来源卷标识 "E:|OsmoAction|123456"，用于指纹与断点续传防换卡。</summary>
    public required string VolumeKey { get; init; }

    public DateTime SortTimeLocal => TakenLocal ?? ModifiedUtc.ToLocalTime();

    public static MediaKind KindFromExtension(string ext) => ext.ToLowerInvariant() switch
    {
        ".jpg" or ".jpeg" or ".jpe" or ".png" or ".heic" or ".heif" or ".webp" => MediaKind.Photo,
        ".dng" or ".arw" or ".cr2" or ".cr3" or ".nef" or ".raf" or ".rw2" => MediaKind.Photo,
        ".mp4" or ".mov" or ".m4v" or ".avi" or ".mkv" => MediaKind.Video,
        ".lrf" => MediaKind.Proxy,
        ".srt" or ".vtt" or ".ssa" or ".ttf" => MediaKind.Subtitle,
        _ => MediaKind.Other,
    };
}

/// <summary>
/// DJI 文件名解析：DJI_YYYYMMDDHHMMSS_NNNN_XXXX。
/// 任何解析失败都返回 null，调用方回退文件 mtime，绝不丢条目（PLAN §0 假设条款）。
/// </summary>
public static partial class NameParser
{
    [GeneratedRegex(@"^DJI[_\-]?(\d{14})[_\-](\d{3,5})(?:[_\-](\w+))?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DjiPattern();

    public static DateTime? TryParseTakenLocal(string fileName)
    {
        if (string.IsNullOrEmpty(fileName)) return null;
        var stem = Path.GetFileNameWithoutExtension(fileName);
        if (stem.Length is < 10 or > 64) return null;
        var m = DjiPattern().Match(stem);
        if (!m.Success) return null;
        var digits = m.Groups[1].Value;
        if (digits.Length != 14 || !long.TryParse(digits, out var packed)) return null;
        int second = (int)(packed % 100); packed /= 100;
        int minute = (int)(packed % 100); packed /= 100;
        int hour = (int)(packed % 100); packed /= 100;
        int day = (int)(packed % 100); packed /= 100;
        int month = (int)(packed % 100); packed /= 100;
        int year = (int)packed;
        if (year is < 2000 or > 2100 || month is < 1 or > 12 || day is < 1 or > 31 ||
            hour > 23 || minute > 59 || second > 59) return null;
        try
        {
            // 相机时间戳按本地墙钟时间对待，不做时区换算（跨 DST 边界也不漂移）
            return new DateTime(year, month, day, hour, minute, second, DateTimeKind.Unspecified);
        }
        catch (ArgumentOutOfRangeException)
        {
            return null; // 2 月 30 日等：回退 mtime，绝不因文件名异常丢弃条目
        }
    }
}
