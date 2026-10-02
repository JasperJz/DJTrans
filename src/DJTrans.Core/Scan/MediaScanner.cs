using System.IO.Enumeration;
using System.Runtime.InteropServices;

namespace DJTrans.Core.Scan;

/// <summary>
/// 扫描卷上的媒体目录（DCIM 优先），流式产出元数据。
/// 只读，绝不写设备。IO 错误（拔盘等）跳过该子树并计数，不抛出。
/// </summary>
public static class MediaScanner
{
    public sealed class ScanResult
    {
        public required List<MediaItem> Items { get; init; }
        public required string RootUsed { get; init; }
        public int SkippedEntries { get; init; }
        public TimeSpan Elapsed { get; init; }
    }

    public static string ResolveScanRoot(char letter)
    {
        var dcim = $"{letter}:\\DCIM";
        if (Directory.Exists(dcim)) return dcim;
        return $"{letter}:\\";
    }

    public static ScanResult Scan(char letter, string volumeKey, CancellationToken ct)
    {
        var root = ResolveScanRoot(letter);
        return Scan(root, volumeKey, ct);
    }

    public static ScanResult Scan(string rootDir, string volumeKey, CancellationToken ct)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var items = new List<MediaItem>();
        int skipped = 0;

        if (Directory.Exists(rootDir))
        {
            var opts = new EnumerationOptions
            {
                RecurseSubdirectories = true,
                IgnoreInaccessible = true,
                AttributesToSkip = FileAttributes.ReparsePoint,
                ReturnSpecialDirectories = false,
            };
            var scanner = new FileSystemEnumerable<MediaItem?>(rootDir,
                (ref FileSystemEntry entry) => ToItem(rootDir, volumeKey, ref entry), opts)
            {
                ShouldIncludePredicate = (ref FileSystemEntry entry) => !entry.IsDirectory,
            };
            foreach (var fse in scanner)
            {
                ct.ThrowIfCancellationRequested();
                if (fse is null) { skipped++; continue; }
                items.Add(fse);
            }
        }

        return new ScanResult
        {
            Items = items,
            RootUsed = rootDir,
            SkippedEntries = skipped,
            Elapsed = sw.Elapsed,
        };
    }

    private static MediaItem? ToItem(string rootDir, string volumeKey, ref FileSystemEntry entry)
    {
        if (entry.IsDirectory) return null;
        long size;
        try { size = entry.Length; } catch (IOException) { return null; }
        var fullPath = entry.ToFullPath();
        var name = entry.FileName.ToString();
        var ext = Path.GetExtension(name.AsSpan());
        if (ext.Length == 0) return null;
        var mtimeUtc = entry.LastWriteTimeUtc.UtcDateTime;
        string relDir;
        var fullDir = Path.GetDirectoryName(fullPath.AsSpan());
        if (fullDir.Length > rootDir.Length && fullDir.StartsWith(rootDir, StringComparison.OrdinalIgnoreCase))
        {
            relDir = fullDir.Slice(rootDir.Length).ToString().TrimStart('\\', '/');
            if (relDir.Length == 0) relDir = ".";
        }
        else relDir = ".";
        return new MediaItem
        {
            Path = fullPath,
            Name = name,
            Kind = MediaItem.KindFromExtension(ext.ToString()),
            SizeBytes = size,
            ModifiedUtc = mtimeUtc,
            TakenLocal = NameParser.TryParseTakenLocal(name),
            RelativeDir = relDir,
            VolumeKey = volumeKey,
        };
    }
}

/// <summary>卷信息与热插拔监视。卷到达后 exFAT 挂载可能延迟，调用方需就绪退避重试。</summary>
public sealed record VolumeInfo
{
    public required char Letter { get; init; }
    public required string Label { get; init; }
    public required long TotalBytes { get; init; }
    public required long FreeBytes { get; init; }
    /// <summary>卷序列号，来自 GetVolumeInformation；换卡后变化，是续传指纹的一部分。</summary>
    public required uint Serial { get; init; }
    public bool IsDjiLikely { get; init; }

    public string VolumeKey => $"{Letter}:|{Label}|{Serial:X8}";
    public string RootPath => $"{Letter}:\\";
    public string DisplayName => $"{Letter}: {Label}" + (IsDjiLikely ? " (DJI)" : "");
}

public sealed class VolumeChangedEventArgs : EventArgs
{
    public required IReadOnlyList<VolumeInfo> Added { get; init; }
    public required IReadOnlyList<VolumeInfo> Removed { get; init; }
    public required IReadOnlyList<VolumeInfo> Current { get; init; }
    public bool HasChanges => Added.Count > 0 || Removed.Count > 0;
}

public sealed class VolumeWatcher : IDisposable
{
    private readonly object _sync = new();
    private readonly Dictionary<char, VolumeInfo> _current = new();
    private readonly System.Threading.Timer _poll;
    private readonly SynchronizationContext? _uiContext;
    private List<Timer>? _pending;
    private bool _disposed;

    /// <summary>事件在后台线程触发；若构造时捕获到 UI 上下文则投递到 UI 线程。</summary>
    public event EventHandler<VolumeChangedEventArgs>? VolumesChanged;

    public VolumeWatcher(bool postToUiContext = true)
    {
        _uiContext = postToUiContext ? SynchronizationContext.Current : null;
        _poll = new System.Threading.Timer(_ => Refresh("poll"), null, 2000, 2000);
    }

    public void Start() => Refresh("start");

    /// <summary>UI 收到 WM_DEVICECHANGE 后调用：短退避三次刷新，覆盖卷未就绪窗口。</summary>
    public void NotifyDeviceChange()
    {
        Schedule(120);
        Schedule(500);
        Schedule(1500);
    }

    private void Schedule(int delayMs)
    {
        lock (_sync)
        {
            if (_disposed) return;
            var t = new Timer(_ => { lock (_sync) { _pending?.Remove((Timer)_!); } Refresh("notify"); }, null, delayMs, Timeout.Infinite);
            (_pending ??= new()).Add(t);
        }
    }

    public IReadOnlyList<VolumeInfo> Current
    {
        get { lock (_sync) return _current.Values.OrderBy(v => v.Letter).ToList(); }
    }

    public static bool VolumePathOnline(string path)
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(path));
            if (string.IsNullOrEmpty(root)) return false;
            var di = new DriveInfo(root);
            return di.IsReady && di.DriveType == DriveType.Removable;
        }
        catch (Exception e) when (e is ArgumentException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    public void Refresh(string reason)
    {
        List<VolumeInfo> added, removed, current;
        lock (_sync)
        {
            if (_disposed) return;
            Dictionary<char, VolumeInfo> next;
            try
            {
                next = DriveInfo.GetDrives()
                    .Where(d => d.DriveType == DriveType.Removable && d.IsReady)
                    .Select(d => TryBuildVolume(d.Name[0]))
                    .Where(v => v is not null)
                    .ToDictionary(v => v!.Letter, v => v!);
            }
            catch (IOException) { return; } // 拔盘瞬间 GetDrives 可能抛错，等下一轮轮询
            catch (UnauthorizedAccessException) { return; }

            added = next.Values.Where(v => !_current.ContainsKey(v.Letter)).OrderBy(v => v.Letter).ToList();
            var nextKeys = next.Keys.ToHashSet();
            removed = _current.Values.Where(v => !nextKeys.Contains(v.Letter)).OrderBy(v => v.Letter).ToList();
            current = next.Values.OrderBy(v => v.Letter).ToList();
            foreach (var r in removed) _current.Remove(r.Letter);
            foreach (var a in added) _current[a.Letter] = a;
        }

        if (added.Count > 0 || removed.Count > 0)
            Raise(new VolumeChangedEventArgs { Added = added, Removed = removed, Current = current });
    }

    private void Raise(VolumeChangedEventArgs args)
    {
        var handler = VolumesChanged;
        if (handler is null) return;
        if (_uiContext is not null) _uiContext.Post(_ => handler(this, args), null);
        else ThreadPool.QueueUserWorkItem(_ => handler(this, args));
    }

    internal static VolumeInfo? TryBuildVolume(char letter)
    {
        try
        {
            var di = new DriveInfo(letter.ToString());
            if (!di.IsReady) return null;
            var label = string.IsNullOrWhiteSpace(di.VolumeLabel) ? "可移动磁盘" : di.VolumeLabel.Trim();
            uint serial = GetVolumeSerial(letter);
            return new VolumeInfo
            {
                Letter = letter,
                Label = label,
                TotalBytes = di.TotalSize,
                FreeBytes = di.AvailableFreeSpace,
                Serial = serial,
                IsDjiLikely = IsDjiVolume(letter, label),
            };
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static bool IsDjiVolume(char letter, string label)
    {
        var l = label.ToLowerInvariant();
        if (l.Contains("dji") || l.Contains("osmo") || l.Contains("action") || l.Contains("pocket")) return true;
        try
        {
            var dcim = $"{letter}:\\DCIM";
            if (!Directory.Exists(dcim)) return false;
            return Directory.EnumerateDirectories(dcim).Any(d =>
            {
                var n = Path.GetFileName(d);
                return n.StartsWith("DJI", StringComparison.OrdinalIgnoreCase) || n.EndsWith("MEDIA", StringComparison.OrdinalIgnoreCase);
            });
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool GetVolumeInformationW(string rootPathName, IntPtr volumeName, uint volumeNameSize,
        out uint volumeSerialNumber, out uint maxComponentLength, out uint fileSystemFlags, IntPtr fsName, uint fsNameSize);

    internal static uint GetVolumeSerial(char letter)
    {
        try
        {
            if (GetVolumeInformationW($"{letter}:\\", IntPtr.Zero, 0, out var serial, out _, out _, IntPtr.Zero, 0))
                return serial;
        }
        catch { /* 就绪竞态，序列号回退 0，指纹仍含 label */ }
        return 0;
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            _poll.Dispose();
            if (_pending is not null) foreach (var t in _pending) t.Dispose();
            _pending = null;
        }
    }
}
