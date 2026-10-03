using System.Collections.Concurrent;
using System.Drawing.Imaging;
using System.IO.Hashing;
using DJTrans.Core.Scan;
using DJTrans.Core.Thumbs;

namespace DJTrans.Thumbs;

/// <summary>
/// 缩略图服务：磁盘缓存（%LocalAppData%\DJTrans\thumbcache）+ 内存 LRU（≤300 张）
/// + 2 个 STA 工作线程（Shell 调用），按可视区优先出图。
/// 生成链：磁盘缓存 → Shell → DNG 内嵌预览 → null（占位）。
/// 图片通过 UI 上下文回调推送，回调参数为缓存键。
/// </summary>
public sealed class ThumbnailService : IDisposable
{
    private sealed record ThumbRequest(MediaItem Item, int Priority);

    private readonly string _cacheDir;
    private readonly ConcurrentDictionary<string, byte> _inFlight = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> _noThumb = new(StringComparer.Ordinal); // 负缓存：确认无图的不再重试
    // 优先队列：小值先出
    private readonly ConcurrentQueue<ThumbRequest> _hiQ = new();
    private readonly ConcurrentQueue<ThumbRequest> _loQ = new();
    private readonly AutoResetEvent _wake = new(false);
    private readonly Thread[] _workers;
    private readonly object _memLock = new();
    private readonly LinkedList<string> _lru = new();
    private readonly Dictionary<string, Image> _mem = new(StringComparer.Ordinal);
    private long _memBytes;
    private const long MemCapBytes = 150L * 1024 * 1024;
    private const int MemCapCount = 300;
    private volatile bool _disposed;
    private readonly SynchronizationContext? _ui;

    /// <summary>thumb 就绪（UI 线程回调）：key → 图（可能为 null 表示确认无图，用占位）。</summary>
    public event Action<string, Image?>? ThumbReady;

    public ThumbnailService(SynchronizationContext ui)
    {
        _ui = ui;
        _cacheDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DJTrans", "thumbcache");
        Directory.CreateDirectory(_cacheDir);
        _workers = new Thread[2];
        for (int i = 0; i < _workers.Length; i++)
        {
            _workers[i] = new Thread(WorkerLoop) { IsBackground = true, Name = $"DJTrans-Thumb-{i}" };
            _workers[i].SetApartmentState(ApartmentState.STA);
            _workers[i].Start();
        }
    }

    public static string KeyFor(MediaItem m)
    {
        var raw = $"{m.VolumeKey}|{m.Path.ToLowerInvariant()}|{m.SizeBytes}|{m.ModifiedUtc.Ticks}";
        return Convert.ToHexString(System.IO.Hashing.XxHash3.Hash(System.Text.Encoding.UTF8.GetBytes(raw)));
    }

    public void Request(MediaItem item, int priority)
    {
        if (_disposed) return;
        var key = KeyFor(item);
        if (_noThumb.ContainsKey(key)) return;
        // 内存 LRU 命中：同步回填（Request 由 UI 线程调用，SetThumb→Invalidate 安全）
        lock (_memLock)
        {
            if (_mem.TryGetValue(key, out var cached))
            {
                _lru.Remove(key);
                _lru.AddLast(key);
                ThumbReady?.Invoke(key, cached.Clone() as Image);
                return;
            }
        }
        if (!_inFlight.TryAdd(key, 0)) return;
        var r = new ThumbRequest(item, priority);
        if (priority <= 0) _hiQ.Enqueue(r); else _loQ.Enqueue(r);
        _wake.Set();
    }

    private void WorkerLoop()
    {
        while (!_disposed)
        {
            ThumbRequest? r = null;
            if (_hiQ.TryDequeue(out var hi)) r = hi;
            else if (_loQ.TryDequeue(out var lo)) r = lo;
            if (r is null)
            {
                _wake.WaitOne(400);
                continue;
            }
            var key = KeyFor(r.Item);
            Image? produced = null;
            try
            {
                produced = GetOrCreate(r.Item, key);
                if (produced is not null) Store(key, produced);
                else _noThumb[key] = 0;
                // 缓存中的 Image 归服务所有；给 UI 一个独立克隆，避免跨线程 Dispose 竞态
                Image? snapshot = produced?.Clone() as Image;
                _ui?.Post(k =>
                {
                    var (kk, im) = ((string, Image?))k!;
                    ThumbReady?.Invoke(kk, im);
                }, (key, snapshot));
            }
            catch
            {
                // 瞬态异常（拔盘/IO）不进负缓存：设备回来后同 key 仍可重试
                _ui?.Post(k => ThumbReady?.Invoke((string)k!, null), key);
            }
            finally
            {
                _inFlight.TryRemove(key, out _);
            }
        }
    }

    /// <summary>统一降采样到 ≤320px：控制缓存与克隆的内存/GDI 上限（DNG 内嵌预览可达 4000px+）。</summary>
    private static Image Downscale(Image src)
    {
        const int cap = 320;
        if (src.Width <= cap && src.Height <= cap) return src;
        double scale = Math.Min((double)cap / src.Width, (double)cap / src.Height);
        var w = Math.Max(1, (int)(src.Width * scale));
        var h = Math.Max(1, (int)(src.Height * scale));
        var bmp = new Bitmap(w, h);
        using (var g = Graphics.FromImage(bmp))
        {
            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
            g.DrawImage(src, 0, 0, w, h);
        }
        src.Dispose();
        return bmp;
    }

    private Image? GetOrCreate(MediaItem item, string key)
    {
        var cacheFile = Path.Combine(_cacheDir, key + ".jpg");
        try
        {
            if (File.Exists(cacheFile))
            {
                using var fs = File.OpenRead(cacheFile);
                return new Bitmap(fs, true);
            }
        }
        catch { /* 缓存损坏 → 重新生成 */ }

        Image? produced = null;
        if (item.Kind == MediaKind.Photo && item.Path.EndsWith(".dng", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                using var fs = File.OpenRead(item.Path);
                var jpg = DngPreviewExtractor.TryExtractPreviewJpeg(fs);
                if (jpg is { Length: > 0 })
                    using (var ms = new MemoryStream(jpg))
                        produced = Downscale(new Bitmap(ms, true));
            }
            catch { }
        }
        if (produced is null)
        {
            var shell = ShellThumb.TryGetThumbnail(item.Path, 320);
            if (shell is not null) produced = shell;
        }
        if (produced is not null)
        {
            produced = Downscale(produced);
            try
            {
                var tmp = cacheFile + ".tmp";
                using (var fs = File.Create(tmp))
                    produced.Save(fs, ImageFormat.Jpeg);
                if (File.Exists(cacheFile)) File.Delete(tmp);
                else File.Move(tmp, cacheFile);
            }
            catch { /* 缓存写失败不影响本次出图 */ }
        }
        return produced;
    }

    private void Store(string key, Image? img)
    {
        if (img is null) return;
        lock (_memLock)
        {
            if (_mem.ContainsKey(key)) return;
            _mem[key] = img;
            _lru.AddLast(key);
            _memBytes += EstimateImageBytes(img);
            // 淘汰时不 Dispose：绘图线程可能正在使用；交给 GC 终结器释放
            while ((_mem.Count > MemCapCount || _memBytes > MemCapBytes) && _lru.Count > 0)
            {
                var oldest = _lru.First!.Value;
                _lru.RemoveFirst();
                if (_mem.Remove(oldest, out var evicted))
                    _memBytes -= EstimateImageBytes(evicted);
            }
        }
    }

    private static long EstimateImageBytes(Image img) => (long)img.Width * img.Height * 4;

    /// <summary>删除设备文件后清理对应缓存条目（S7 采纳项）。</summary>
    public void Invalidate(MediaItem item)
    {
        var key = KeyFor(item);
        lock (_memLock)
        {
            _lru.Remove(key);
            _mem.Remove(key);
        }
        _noThumb.TryRemove(key, out _);
        try { File.Delete(Path.Combine(_cacheDir, key + ".jpg")); } catch (IOException) { }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _wake.Set();
        foreach (var w in _workers) w.Join(2000);
        lock (_memLock) { _mem.Clear(); _lru.Clear(); } // Image 不显式 Dispose，交 GC
        _wake.Dispose();
    }
}
