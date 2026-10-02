using DJTrans.Core.Scan;

namespace DJTrans.Core.Tests.Scan;

public sealed class NameParserTests
{
    [Theory]
    [InlineData("DJI_20251230064054_0002_D.MP4", 2025, 12, 30, 6, 40, 54)]
    [InlineData("DJI_20240316033351_0001_D.JPG", 2024, 3, 16, 3, 33, 51)]
    [InlineData("DJI_20260228235959_99999_WAAF.mp4", 2026, 2, 28, 23, 59, 59)]
    [InlineData("DJI-20260101000000-0001-D.MOV", 2026, 1, 1, 0, 0, 0)]
    public void Parses_DjiNames(string file, int y, int mo, int d, int h, int mi, int s)
    {
        var t = NameParser.TryParseTakenLocal(file);
        Assert.Equal(new DateTime(y, mo, d, h, mi, s), t);
    }

    [Theory]
    [InlineData("DJI_0130.JPG")]              // 旧机型短命名
    [InlineData("IMG_1234.jpg")]
    [InlineData("DJI_20251330064054_0002_D.MP4")] // 13月
    [InlineData("DJI_20251232256160_0002_D.MP4")] // 非法时分秒
    [InlineData("DJI_19990101000000_0001_D.MP4")] // 年份过旧
    [InlineData("random.bin")]
    [InlineData("")]
    [InlineData("DJI_20251230064054_0002_D_extended_suffix_extra_long_name_beyond_reasonable.MP4")]
    public void InvalidNames_ReturnNull_FallbackToMtime(string file)
    {
        Assert.Null(NameParser.TryParseTakenLocal(file));
    }

    [Fact]
    public void KindFromExtension_MapsCorrectly()
    {
        Assert.Equal(MediaKind.Photo, MediaItem.KindFromExtension(".JPG"));
        Assert.Equal(MediaKind.Photo, MediaItem.KindFromExtension(".dng"));
        Assert.Equal(MediaKind.Video, MediaItem.KindFromExtension(".MP4"));
        Assert.Equal(MediaKind.Video, MediaItem.KindFromExtension(".mov"));
        Assert.Equal(MediaKind.Proxy, MediaItem.KindFromExtension(".lrf"));
        Assert.Equal(MediaKind.Subtitle, MediaItem.KindFromExtension(".srt"));
        Assert.Equal(MediaKind.Other, MediaItem.KindFromExtension(".bin"));
        Assert.Equal(MediaKind.Other, MediaItem.KindFromExtension(""));
    }
}

public sealed class MediaScannerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "djtrans-scan", Guid.NewGuid().ToString("N"));

    public MediaScannerTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "DCIM", "DJI_001"));
        Directory.CreateDirectory(Path.Combine(_root, "DCIM", "DJI_002", "sub"));
        Directory.CreateDirectory(Path.Combine(_root, "MISC"));
        File.WriteAllText(Path.Combine(_root, "DCIM", "DJI_001", "DJI_20251230064054_0002_D.MP4"), new string('x', 100));
        File.WriteAllText(Path.Combine(_root, "DCIM", "DJI_001", "DJI_20251230064054_0002_D.LRF"), new string('x', 10));
        File.WriteAllText(Path.Combine(_root, "DCIM", "DJI_001", "DJI_20251230070727_0003_D.JPG"), new string('y', 200));
        File.WriteAllText(Path.Combine(_root, "DCIM", "DJI_002", "sub", "DJI_20251230191255_0009_D.DNG"), new string('z', 300));
        File.WriteAllText(Path.Combine(_root, "DCIM", "DJI_001", "notes.srt"), "1\n00:00 → x\n");
        File.WriteAllText(Path.Combine(_root, "MISC", "cal.bin"), "misc"); // MISC 不在扫描根内
    }

    public void Dispose() { try { Directory.Delete(_root, true); } catch (IOException) { } }

    [Fact]
    public void Scan_FindsAllMedia_WithKindsAndDates()
    {
        var r = MediaScanner.Scan(Path.Combine(_root, "DCIM"), "V|K", CancellationToken.None);
        Assert.Equal(5, r.Items.Count);
        Assert.Equal(0, r.SkippedEntries);
        var mp4 = r.Items.Single(i => i.Name.EndsWith(".MP4"));
        Assert.Equal(MediaKind.Video, mp4.Kind);
        Assert.Equal(100, mp4.SizeBytes);
        Assert.Equal(new DateTime(2025, 12, 30, 6, 40, 54), mp4.TakenLocal);
        Assert.Equal("DJI_001", mp4.RelativeDir);
        Assert.Equal("V|K", mp4.VolumeKey);
        Assert.Equal(MediaKind.Proxy, r.Items.Single(i => i.Name.EndsWith(".LRF")).Kind);
        Assert.Equal(MediaKind.Photo, r.Items.Single(i => i.Name.EndsWith(".JPG")).Kind);
        Assert.Equal(MediaKind.Photo, r.Items.Single(i => i.Name.EndsWith(".DNG")).Kind);
        Assert.Equal("DJI_002\\sub", r.Items.Single(i => i.Name.EndsWith(".DNG")).RelativeDir);
        Assert.Equal(MediaKind.Subtitle, r.Items.Single(i => i.Name == "notes.srt").Kind);
        Assert.Null(r.Items.Single(i => i.Name == "notes.srt").TakenLocal); // 回退 mtime，不丢条目
    }

    [Fact]
    public void Scan_MissingRoot_ReturnsEmpty()
    {
        var r = MediaScanner.Scan(Path.Combine(_root, "NOPE"), "V|K", CancellationToken.None);
        Assert.Empty(r.Items);
    }

    [Fact]
    public void Scan_Cancellation_Throws()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() =>
            MediaScanner.Scan(Path.Combine(_root, "DCIM"), "V|K", cts.Token));
    }

    [Fact]
    public void ResolveScanRoot_ProducesValidRoot()
    {
        char letter = _root[0]; // 临时目录所在盘
        var root = MediaScanner.ResolveScanRoot(letter);
        Assert.Matches($"^{letter}:\\\\(DCIM)?$", root);
    }
}
