using System.IO.Hashing;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DJTrans.Core.Transfer;

/// <summary>
/// 断点续传日志：与 .djpart 成对存放在目标目录（destPath + ".djjournal"）。
/// 记录源指纹(volKey,size,mtime)、已完成偏移、每 8MB 块的 XxHash3（哈希在读取源时同步计算，续传与最终校验都以此为基准）。
/// 写入原子性：temp + File.Replace；自校验和检测撕裂写。损坏时调用方降级为 RebuildFromPartial（源与 .djpart 逐块对读重建前缀）。
/// </summary>
public sealed class TransferJournal
{
    public const int BlockSize = 8 * 1024 * 1024;
    public const int MaxPreviewBytes = 8 * 1024 * 1024;

    public required string SourcePath { get; init; }
    public required string VolumeKey { get; init; }
    public required long SourceSize { get; init; }
    public required long SourceMtimeUtcTicks { get; init; }
    public required string DestPath { get; init; }

    [JsonInclude]
    public long Offset { get; set; }

    [JsonInclude]
    public List<ulong> BlockHashes { get; set; } = new();

    [JsonIgnore]
    public string JournalPath => DestPath + ".djjournal";
    [JsonIgnore]
    public string PartPath => DestPath + ".djpart";

    public static TransferJournal Create(TransferRequest req)
        => new()
        {
            SourcePath = req.SourcePath,
            VolumeKey = req.VolumeKey,
            SourceSize = req.SizeBytes,
            SourceMtimeUtcTicks = req.SourceMtimeUtc.Ticks,
            DestPath = req.DestPath,
        };

    public bool FingerprintMatches(TransferRequest req)
        => string.Equals(SourcePath, req.SourcePath, StringComparison.OrdinalIgnoreCase)
           && string.Equals(VolumeKey, req.VolumeKey, StringComparison.OrdinalIgnoreCase)
           && SourceSize == req.SizeBytes
           && SourceMtimeUtcTicks == req.SourceMtimeUtc.Ticks;

    /// <summary>块长恒为 BlockSize，只有最后一块可以短。</summary>
    public void RecordBlock(ulong hash, int blockLength)    {
        BlockHashes.Add(hash);
        Offset += blockLength;
    }

    public void TruncateToBlocks(int blockCount, long offset)
    {
        if (blockCount < 0 || blockCount > BlockHashes.Count) throw new ArgumentOutOfRangeException(nameof(blockCount));
        BlockHashes.RemoveRange(blockCount, BlockHashes.Count - blockCount);
        Offset = offset;
    }

    public void Save()
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(this, JsonOpts);
        var selfHash = XxHash3.HashToUInt64(json);
        using var ms = new MemoryStream(json.Length + 16);
        ms.Write(json);
        ms.Write(Encoding.ASCII.GetBytes($"\n#{selfHash:X16}"));
        var tmp = JournalPath + ".tmp";
        File.WriteAllBytes(tmp, ms.ToArray());
        if (File.Exists(JournalPath)) File.Replace(tmp, JournalPath, null);
        else File.Move(tmp, JournalPath);
    }

    /// <summary>加载并校验（指纹+自校验和+偏移一致性）。任何不一致返回 null，由调用方降级重建。</summary>
    public static TransferJournal? TryLoad(TransferRequest req)
    {
        try
        {
            if (!File.Exists(req.DestPath + ".djjournal")) return null;
            var bytes = File.ReadAllBytes(req.DestPath + ".djjournal");
            var nl = bytes.LastIndexOf((byte)'\n');
            if (nl < 0 || nl + 1 >= bytes.Length || bytes[nl + 1] != (byte)'#') return null;
            var body = bytes.AsSpan(0, nl).ToArray();
            var tail = Encoding.ASCII.GetString(bytes, nl + 2, bytes.Length - nl - 2).TrimEnd('\r', '\n', ' ');
            if (!ulong.TryParse(tail, System.Globalization.NumberStyles.HexNumber, null, out var stored)) return null;
            if (XxHash3.HashToUInt64(body) != stored) return null; // 撕裂/损坏
            var j = JsonSerializer.Deserialize<TransferJournal>(body, JsonOpts);
            if (j is null) return null;
            if (!j.FingerprintMatches(req)) return null;
            if (j.SourceSize < 0 || j.Offset < 0 || j.Offset > j.SourceSize || j.BlockHashes is null) return null;
            long expectedBlocks = j.Offset / BlockSize + (j.Offset % BlockSize == 0 ? 0 : 1);
            if (j.BlockHashes.Count != expectedBlocks) return null;
            if (j.Offset != j.SourceSize && j.Offset % BlockSize != 0) return null;
            return j;
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException or NotSupportedException)
        {
            return null;
        }
    }

    public static void DeleteFiles(string destPath)
    {
        DeleteJournalFile(destPath + ".djjournal");
        DeleteJournalFile(destPath + ".djpart");
    }

    public static void DeleteJournalFile(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = false };

    /// <summary>
    /// 快速指纹比对。mtimeHint 命中（目标 mtime == 源 mtime，我们自己的导出会保留）时
    /// 只比对首尾各 2MB；否则（外来文件）保守比对首尾各 8MB。
    /// </summary>
    public static bool QuickFingerprintEqual(string fileA, string fileB, long sizeHint, DateTime? mtimeHint = null)
    {
        try
        {
            using var a = File.Open(fileA, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var b = File.Open(fileB, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (a.Length != b.Length) return false;
            if (sizeHint >= 0 && a.Length != sizeHint) return false;
            bool trusted = false;
            if (mtimeHint is { } mt)
                trusted = File.GetLastWriteTimeUtc(fileB) == mt;
            int n = (int)Math.Min(trusted ? 2L * 1024 * 1024 : MaxPreviewBytes, a.Length);
            if (!HashRangeEqual(a, b, 0, n)) return false;
            if (a.Length > n)
                return HashRangeEqual(a, b, a.Length - n, n);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    public static bool FullContentEqual(string fileA, string fileB, long sizeHint, Action? checkpoint = null)
    {
        using var a = File.Open(fileA, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var b = File.Open(fileB, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (a.Length != b.Length || a.Length != sizeHint) return false;
        var bufA = new byte[256 * 1024];
        var bufB = new byte[bufA.Length];
        long remaining = a.Length;
        while (remaining > 0)
        {
            checkpoint?.Invoke();
            int take = (int)Math.Min(bufA.Length, remaining);
            if (!ReadExact(a, bufA, take) || !ReadExact(b, bufB, take)
                || !bufA.AsSpan(0, take).SequenceEqual(bufB.AsSpan(0, take))) return false;
            remaining -= take;
        }
        return true;
    }

    private static bool HashRangeEqual(FileStream a, FileStream b, long offset, int length)
    {
        var bufA = new byte[256 * 1024];
        var bufB = new byte[256 * 1024];
        a.Position = offset; b.Position = offset;
        long remaining = length;
        while (remaining > 0)
        {
            var take = (int)Math.Min(bufA.Length, remaining);
            if (!ReadExact(a, bufA, take) || !ReadExact(b, bufB, take)) return false;
            if (XxHash3.HashToUInt64(bufA.AsSpan(0, take)) != XxHash3.HashToUInt64(bufB.AsSpan(0, take))) return false;
            remaining -= take;
        }
        return true;
    }

    internal static bool ReadExact(FileStream s, byte[] buf, int count)
    {
        int read = 0;
        while (read < count)
        {
            var n = s.Read(buf, read, count - read);
            if (n <= 0) return false;
            read += n;
        }
        return true;
    }
}
