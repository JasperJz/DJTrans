using System.Collections.Concurrent;
using System.IO.Hashing;
using DJTrans.Core.Scan;

namespace DJTrans.Core.Transfer;

/// <summary>
/// 传输引擎：作业队列 + N 个工作者线程。
/// UI 只通过 EngineSnapshot（~100ms 节流）观察状态；所有控制操作线程安全。
/// 断点续传协议见 TransferJournal；设备拔出时作业进入 WaitingDevice，重连后自动续传。
/// </summary>
public sealed class TransferEngine : IDisposable
{
    private sealed class JobCanceledException : Exception { public static readonly JobCanceledException Instance = new(); }
    private sealed class DeviceOfflineException : Exception { public static readonly DeviceOfflineException Instance = new(); }

    public sealed class TransferJob
    {
        public required long Id { get; init; }
        public required string BatchId { get; init; }
        public required TransferRequest Req { get; init; }
        public required TransferOptions Options { get; init; }
        public JobState State;
        public long BytesDone;
        public string? Error;
        public string? SkipReason;
        public long ResumedFromOffset;
        public DateTime? StartedUtc;
        public DateTime? FinishedUtc;
        public double EmaSpeed;
        public string CurrentDestPath;
        public readonly ManualResetEventSlim PauseGate = new(true);
        public volatile bool CancelRequested;
        /// <summary>worker 是否持有本作业（锁内维护）。决定 Resume/Cancel 走唤醒还是直接状态迁移。</summary>
        internal bool HeldByWorker;
        internal int Attempts;
        internal long LastSampleBytes;
        internal long LastSampleTicks;

        public TransferJob() => CurrentDestPath = null!;
    }

    private readonly object _sync = new();
    private readonly List<TransferJob> _jobs = new();
    private readonly HashSet<string> _busyDest = new(StringComparer.OrdinalIgnoreCase);
    private readonly Thread[] _workers;
    private readonly SemaphoreSlim _wake = new(0);
    private readonly ManualResetEventSlim _globalPause = new(true);
    private readonly CancellationTokenSource _cts = new();
    private readonly System.Threading.Timer _snapTimer;
    private volatile bool _dirty = true;
    private long _nextId = 1;
    private Func<TransferJob, ConflictDecision>? _conflictResolver;
    private volatile bool _disposed;

    /// <summary>快照节流推送（线程池线程）；UI 需自行封送。</summary>
    public event Action<EngineSnapshot>? SnapshotChanged;
    /// <summary>引擎日志（续传偏移、重试、降级等关键事件）。</summary>
    public event Action<string>? Logged;

    /// <summary>测试/模拟接缝：覆盖默认的"可移动卷在线"判定。</summary>
    public static Func<string, bool>? OnlineCheckOverride { get; set; }

    internal static bool IsDevicePathOnline(string path)
        => OnlineCheckOverride is { } f ? f(path) : VolumeWatcher.VolumePathOnline(path);

    public TransferEngine(int workers = 2, Func<TransferJob, ConflictDecision>? conflictResolver = null)
    {
        _conflictResolver = conflictResolver;
        _workers = new Thread[Math.Clamp(workers, 1, 8)];
        for (int i = 0; i < _workers.Length; i++)
        {
            _workers[i] = new Thread(WorkerLoop) { IsBackground = true, Name = $"DJTrans-Worker-{i}" };
            _workers[i].Start();
        }
        _snapTimer = new System.Threading.Timer(_ => PushSnapshot(), null, 100, 100);
    }

    public void SetConflictResolver(Func<TransferJob, ConflictDecision>? resolver) => _conflictResolver = resolver;

    // ---------- 入队 ----------

    public sealed record EnqueueResult(string BatchId, int Accepted, int RejectedDuplicate);

    public EnqueueResult EnqueueBatch(IEnumerable<TransferRequest> requests, TransferOptions options)
    {
        string batchId = Guid.NewGuid().ToString("N");
        int accepted = 0, rejected = 0;
        lock (_sync)
        {
            foreach (var req in requests)
            {
                if (!_busyDest.Add(req.DestPath)) { rejected++; continue; }
                var job = new TransferJob
                {
                    Id = _nextId++,
                    BatchId = batchId,
                    Req = req,
                    Options = options,
                    CurrentDestPath = req.DestPath,
                };
                _jobs.Add(job);
                accepted++;
            }
        }
        if (accepted > 0)
        {
            _dirty = true;
            for (int i = 0; i < accepted; i++) _wake.Release();
            Log($"批次 {batchId[..8]} 入队 {accepted} 项" + (rejected > 0 ? $"（{rejected} 项目标重复被拒）" : ""));
        }
        return new EnqueueResult(batchId, accepted, rejected);
    }

    // ---------- 控制 ----------

    public void PauseAll() { _globalPause.Reset(); _dirty = true; }
    public void ResumeAll() { _globalPause.Set(); _dirty = true; }
    public bool GlobalPaused => !_globalPause.IsSet;

    public void PauseJob(long id)
    {
        lock (_sync)
        {
            var j = _jobs.FirstOrDefault(x => x.Id == id);
            if (j is null || IsTerminal(j.State)) return;
            j.PauseGate.Reset();
            j.State = JobState.Paused; // 无论 worker 是否持有：held 时循环会在检查点停住；未持有则等待唤醒
        }
        _dirty = true;
    }

    public void ResumeJob(long id)
    {
        lock (_sync)
        {
            var j = _jobs.FirstOrDefault(x => x.Id == id);
            if (j is null || j.State != JobState.Paused) return;
            j.PauseGate.Set();
            j.State = j.HeldByWorker ? JobState.Transferring : JobState.Queued;
            if (!j.HeldByWorker) _wake.Release();
        }
        _dirty = true;
    }

    public void CancelJob(long id)
    {
        lock (_sync)
        {
            var j = _jobs.FirstOrDefault(x => x.Id == id);
            if (j is null || IsTerminal(j.State)) return;
            j.CancelRequested = true;
            j.PauseGate.Set(); // 解除 worker 的暂停等待
            if (!j.HeldByWorker)
            {
                // 无 worker 消费取消标志：直接迁移终态并释放目标锁（.djpart/journal 保留）
                j.State = JobState.Canceled;
                j.FinishedUtc = DateTime.UtcNow;
                _busyDest.Remove(j.CurrentDestPath);
            }
        }
        _dirty = true;
    }

    public void CancelBatch(string batchId)
    {
        lock (_sync)
        {
            foreach (var j in _jobs.Where(x => x.BatchId == batchId && !IsTerminal(x.State)))
            {
                j.CancelRequested = true;
                j.PauseGate.Set();
                if (!j.HeldByWorker)
                {
                    j.State = JobState.Canceled;
                    j.FinishedUtc = DateTime.UtcNow;
                    _busyDest.Remove(j.CurrentDestPath);
                }
            }
        }
        _dirty = true;
    }

    public void RetryJob(long id)
    {
        lock (_sync)
        {
            var j = _jobs.FirstOrDefault(x => x.Id == id);
            if (j is null || j.State is not (JobState.Failed or JobState.Canceled)) return;
            j.Error = null;
            j.Attempts = 0;
            j.CancelRequested = false;
            j.State = JobState.Queued;
            _busyDest.Add(j.CurrentDestPath);
        }
        _wake.Release();
        _dirty = true;
    }

    public void RetryAllFailed()
    {
        int n = 0;
        lock (_sync)
        {
            foreach (var j in _jobs.Where(x => x.State == JobState.Failed))
            {
                j.Error = null;
                j.Attempts = 0;
                j.CancelRequested = false;
                j.State = JobState.Queued;
                _busyDest.Add(j.CurrentDestPath);
                n++;
            }
        }
        for (int i = 0; i < n; i++) _wake.Release();
        _dirty = true;
    }

    public void RemoveFinished(bool includeFailed = true, bool includeCanceled = true)
    {
        lock (_sync)
        {
            for (int i = _jobs.Count - 1; i >= 0; i--)
            {
                var s = _jobs[i].State;
                var removable = s == JobState.Done || s == JobState.Skipped
                    || (includeFailed && s == JobState.Failed)
                    || (includeCanceled && s == JobState.Canceled);
                if (removable)
                {
                    _busyDest.Remove(_jobs[i].CurrentDestPath);
                    _jobs.RemoveAt(i);
                }
            }
        }
        _dirty = true;
    }

    /// <summary>设备热插拔事件到达后由 UI 调用：唤醒设备已回来的 WaitingDevice 作业。</summary>
    public void CheckWaitingJobs()
    {
        int n = 0;
        lock (_sync)
        {
            foreach (var j in _jobs.Where(x => x.State == JobState.WaitingDevice))
            {
                if (!IsDevicePathOnline(j.Req.DevicePath)) continue;
                j.State = JobState.Queued;
                n++;
            }
        }
        for (int i = 0; i < n; i++) _wake.Release();
        if (n > 0) Log($"设备恢复，{n} 个作业重新排队续传");
        if (n > 0) _dirty = true;
    }

    // ---------- 快照 ----------

    public EngineSnapshot Snapshot()
    {
        lock (_sync)
        {
            var jobs = new JobSnapshot[_jobs.Count];
            long total = 0, done = 0, finished = 0, active = 0, pending = 0, failed = 0;
            double speed = 0;
            for (int i = 0; i < _jobs.Count; i++)
            {
                var j = _jobs[i];
                total += j.Req.SizeBytes;
                long bytes = Interlocked.Read(ref j.BytesDone);
                if (j.State is JobState.Done or JobState.Skipped) { done += j.Req.SizeBytes; bytes = j.Req.SizeBytes; }
                else done += Math.Min(bytes, j.Req.SizeBytes);
                if (j.State is JobState.Done or JobState.Skipped) finished++;
                else if (j.State is JobState.Transferring or JobState.Verifying or JobState.ResolvingConflict) { active++; speed += j.EmaSpeed; }
                else if (j.State == JobState.Failed) failed++;
                else pending++;
                jobs[i] = new JobSnapshot
                {
                    Id = j.Id,
                    BatchId = j.BatchId,
                    SourcePath = j.Req.SourcePath,
                    DestPath = j.CurrentDestPath,
                    Name = Path.GetFileName(j.Req.SourcePath),
                    Direction = j.Req.Direction,
                    SizeBytes = j.Req.SizeBytes,
                    BytesDone = bytes,
                    State = j.State,
                    SpeedBytesPerSec = j.EmaSpeed,
                    Error = j.Error,
                    SkipReason = j.SkipReason,
                    ResumedFromOffset = j.ResumedFromOffset,
                    StartedUtc = j.StartedUtc,
                    FinishedUtc = j.FinishedUtc,
                };
            }
            return new EngineSnapshot
            {
                Jobs = jobs,
                GlobalPaused = !_globalPause.IsSet,
                TotalBytes = total,
                DoneBytes = done,
                FinishedCount = finished,
                ActiveCount = active,
                PendingCount = pending,
                FailedCount = failed,
                OverallSpeedBytesPerSec = speed,
            };
        }
    }

    private void PushSnapshot()
    {
        if (!_dirty || _disposed) return;
        _dirty = false;
        var snap = Snapshot();
        SnapshotChanged?.Invoke(snap);
    }

    private void Log(string msg)
    {
        Logged?.Invoke($"[{DateTime.Now:HH:mm:ss.fff}] {msg}");
    }

    // ---------- 工作线程 ----------

    private void WorkerLoop()
    {
        while (!_cts.IsCancellationRequested)
        {
            TransferJob? job = null;
            lock (_sync)
            {
                job = _jobs.FirstOrDefault(j => j.State == JobState.Queued);
                if (job is not null)
                {
                    job.State = JobState.Transferring; // 占位，防其他 worker 重复领取
                    job.HeldByWorker = true;
                }
            }
            if (job is null)
            {
                try { _wake.Wait(500); }
                catch (ObjectDisposedException) { break; } // 退出竞态
                continue;
            }
            try
            {
                RunJob(job);
            }
            catch (Exception ex)
            {
                lock (_sync) job.State = JobState.Failed;
                job.Error = ex.Message;
                job.FinishedUtc = DateTime.UtcNow;
            }
            finally
            {
                lock (_sync)
                {
                    job.HeldByWorker = false;
                    if (IsTerminal(job.State)) _busyDest.Remove(job.CurrentDestPath);
                }
                _dirty = true;
            }
        }
    }

    private static bool IsTerminal(JobState s) => s is JobState.Done or JobState.Skipped or JobState.Failed or JobState.Canceled;

    private void RunJob(TransferJob job)
    {
        job.StartedUtc ??= DateTime.UtcNow;
        job.LastSampleTicks = DateTime.UtcNow.Ticks;
        while (true)
        {
            try
            {
                RunOnce(job);
                return;
            }
            catch (JobCanceledException)
            {
                job.State = JobState.Canceled;
                job.Error = null;
                job.FinishedUtc = DateTime.UtcNow;
                Log($"#{job.Id} 已取消（保留续传现场 {job.BytesDone / 1024 / 1024}MB）");
                return;
            }
            catch (DeviceOfflineException)
            {
                job.State = JobState.WaitingDevice;
                job.Error = "设备已断开，等待重新连接后续传";
                job.PauseGate.Set();
                Log($"#{job.Id} 设备离线，等待重连（已完成 {Volatile.Read(ref job.BytesDone) / 1024 / 1024}MB）");
                return;
            }
            catch (Exception ex) when (ex is IOException or TimeoutException)
            {
                job.Attempts++;
                bool online = IsDevicePathOnline(job.Req.DevicePath);
                if (!online) throw new DeviceOfflineException();
                if (job.CancelRequested) throw new JobCanceledException();
                if (job.Attempts > 3)
                {
                    job.State = JobState.Failed;
                    job.Error = $"IO 错误（重试 {job.Attempts - 1} 次后放弃）：{ex.Message}";
                    job.FinishedUtc = DateTime.UtcNow;
                    Log($"#{job.Id} 失败：{job.Error}");
                    return;
                }
                int delay = 400 << (job.Attempts - 1);
                Log($"#{job.Id} IO 错误第 {job.Attempts} 次，{delay}ms 后重试：{ex.Message}");
                Thread.Sleep(delay);
                lock (_sync) if (job.State != JobState.Paused) job.State = JobState.Transferring; // 尊重暂停期间的状态
            }
            catch (UnauthorizedAccessException ex)
            {
                job.State = JobState.Failed;
                job.Error = "访问被拒绝：" + ex.Message;
                job.FinishedUtc = DateTime.UtcNow;
                return;
            }
        }
    }

    private void RunOnce(TransferJob job)
    {
        var req = job.Req;

        // 设备在线检查（Download 源侧 / Upload 目标侧）
        if (!IsDevicePathOnline(req.DevicePath)) throw DeviceOfflineException.Instance;

        string dest = job.CurrentDestPath;
        var destDir = Path.GetDirectoryName(dest);
        if (!string.IsNullOrEmpty(destDir)) Directory.CreateDirectory(destDir);

        // 空间预检（每个作业开始时复核一次）
        if (!CheckFreeSpace(dest, Math.Max(0, req.SizeBytes - Volatile.Read(ref job.BytesDone)) + 32L * 1024 * 1024))
        {
            job.State = JobState.Failed;
            job.Error = $"目标磁盘空间不足（需要 {Fmt(req.SizeBytes - Volatile.Read(ref job.BytesDone))}，可用 {Fmt(GetFreeSpace(dest))}）";
            job.FinishedUtc = DateTime.UtcNow;
            return;
        }

        // ---------- 冲突决策 ----------
        bool overwrite = false;
        if (File.Exists(dest))
        {
            var policy = job.Options.ConflictPolicy;            // 智能跳过：目标同名同大小且首尾块指纹一致 → 静默跳过（增量导出，不打扰用户）
            if (policy == ConflictPolicy.SmartSkip
                && req.SizeBytes == GetFileLength(dest)
                && TransferJournal.QuickFingerprintEqual(req.SourcePath, dest, req.SizeBytes, req.SourceMtimeUtc))
            {
                TransferJournal.DeleteFiles(dest);
                job.State = JobState.Skipped;
                job.SkipReason = "目标已存在相同内容（增量跳过）";
                Interlocked.Exchange(ref job.BytesDone, req.SizeBytes);
                job.FinishedUtc = DateTime.UtcNow;
                Log($"#{job.Id} 增量跳过：{Path.GetFileName(dest)}");
                return;
            }

            ConflictDecision? decision = policy switch
            {
                ConflictPolicy.Skip => ConflictDecision.Skip,
                ConflictPolicy.Overwrite => ConflictDecision.Overwrite,
                ConflictPolicy.RenameNew => ConflictDecision.RenameNew,
                _ => null, // Ask 或 SmartSkip（指纹不同）→ 询问
            };
            if (decision is null) decision = AskUser(job);
            switch (decision)
            {
                case ConflictDecision.Skip:
                    TransferJournal.DeleteFiles(dest); // 清理可能的陈旧续传现场
                    job.State = JobState.Skipped;
                    job.SkipReason = "目标已存在（用户选择跳过）";
                    Interlocked.Exchange(ref job.BytesDone, req.SizeBytes);
                    job.FinishedUtc = DateTime.UtcNow;
                    Log($"#{job.Id} 跳过：{Path.GetFileName(dest)}");
                    return;
                case ConflictDecision.Overwrite:
                    overwrite = true;
                    break;
                case ConflictDecision.RenameNew:
                    {
                        var renamed = FindFreeName(dest);
                        lock (_sync)
                        {
                            _busyDest.Remove(dest);
                            _busyDest.Add(renamed);
                        }
                        dest = renamed;
                        break;
                    }
            }
            job.CurrentDestPath = dest;
        }

        // ---------- 断点续传准备 ----------
        var journal = TransferJournal.TryLoad(req with { DestPath = dest });
        string part = dest + ".djpart";
        long startOffset = 0;

        if (journal is not null && File.Exists(part))
        {
            var partLen = GetFileLength(part);
            if (partLen < journal.Offset)
            {
                journal = RebuildFromPartial(job, dest, part);
            }
            else
            {
                // 末块复验：journal 是源侧哈希的权威，末块不符则回退一块
                if (!VerifyLastBlock(part, journal))
                {
                    int keepBlocks = journal.BlockHashes.Count - 1;
                    journal.TruncateToBlocks(keepBlocks, (long)keepBlocks * TransferJournal.BlockSize);
                    journal.Save();
                    Log($"#{job.Id} 续传末块校验不符，回退至 {journal.Offset / 1024 / 1024}MB");
                }
            }
            if (journal is { Offset: > 0 })
            {
                job.ResumedFromOffset = journal.Offset;
                Interlocked.Exchange(ref job.BytesDone, journal.Offset);
                Interlocked.Exchange(ref job.LastSampleBytes, journal.Offset); // 速度从续传点起算
                Log($"#{job.Id} 断点续传：{Path.GetFileName(dest)} 从 {journal.Offset / 1024 / 1024}MB 继续（共 {req.SizeBytes / 1024 / 1024}MB）");
            }
            startOffset = journal.Offset;
        }
            else if (File.Exists(part))
            {
                // journal 缺失/损坏/指纹不符 → 对读重建前缀（保正确性优先）
                journal = RebuildFromPartial(job, dest, part);
                if (journal is { Offset: > 0 })
                {
                    job.ResumedFromOffset = journal.Offset;
                    Interlocked.Exchange(ref job.BytesDone, journal.Offset);
                    Interlocked.Exchange(ref job.LastSampleBytes, journal.Offset);
                    Log($"#{job.Id} journal 缺失，按内容重建前缀 {journal.Offset / 1024 / 1024}MB 后续传");
                }
                startOffset = journal.Offset;
            }
        else
        {
            journal = TransferJournal.Create(req with { DestPath = dest });
            journal.Save();
        }

        // ---------- 复制 ----------
        using (var src = OpenSource(req.SourcePath))
        using (var dst = new FileStream(part, FileMode.OpenOrCreate, FileAccess.Write, FileShare.None, 1024 * 1024))
        {
            var actualSize = src.Length;
            if (actualSize != req.SizeBytes)
                throw new IOException($"源文件大小已变化（索引 {req.SizeBytes}，实际 {actualSize}），可能设备上的内容已更新");
            src.Position = startOffset;
            if (dst.Length != startOffset) dst.SetLength(startOffset);
            dst.Position = startOffset;

            var buf = new byte[1024 * 1024];
            var blockHash = new XxHash3();
            long blockBytes = startOffset % TransferJournal.BlockSize;
            if (blockBytes != 0)
                throw new IOException("续传偏移未按块对齐，需要整文件重传（内部一致性错误）");
            long copied = startOffset;
            while (copied < actualSize)
            {
                job.PauseGate.Wait(_cts.Token);
                _globalPause.Wait(_cts.Token);
                if (job.CancelRequested) throw JobCanceledException.Instance;

                int want = (int)Math.Min(buf.Length, actualSize - copied);
                int read = src.Read(buf, 0, want);
                if (read <= 0) throw new IOException("源读取意外终止（设备可能已断开）");
                dst.Write(buf, 0, read);
                blockHash.Append(buf.AsSpan(0, read));
                blockBytes += read;
                copied += read;

                if (blockBytes == TransferJournal.BlockSize || copied == actualSize)
                {
                    journal.RecordBlock(blockHash.GetCurrentHashAsUInt64(), (int)blockBytes);
                    journal.Save();
                    dst.Flush();
                    blockHash = new XxHash3();
                    blockBytes = 0;
                }
                Interlocked.Exchange(ref job.BytesDone, copied);
                UpdateSpeed(job, copied);
                _dirty = true;
            }
            dst.Flush(flushToDisk: true);
        }

        // ---------- 校验（受暂停/取消控制） ----------
        if (job.Options.Verify == VerifyMode.BlockHash)
        {
            job.State = JobState.Verifying;
            _dirty = true;
            VerifyPart(job, part, journal, req.SizeBytes);
        }
        else if (GetFileLength(part) != req.SizeBytes)
        {
            throw new IOException($"校验失败：大小不符（{GetFileLength(part)} != {req.SizeBytes}）");
        }

        // ---------- 收尾 ----------
        File.Move(part, dest, overwrite: overwrite || File.Exists(dest));
        if (job.Options.PreserveModifiedTime)
            File.SetLastWriteTimeUtc(dest, req.SourceMtimeUtc);
        TransferJournal.DeleteJournalFile(dest + ".djjournal");
        job.State = JobState.Done;
        job.FinishedUtc = DateTime.UtcNow;
        _dirty = true;
        Log($"#{job.Id} 完成：{Path.GetFileName(dest)}（{Fmt(req.SizeBytes)}）");
    }

    private ConflictDecision AskUser(TransferJob job)
    {
        var resolver = _conflictResolver;
        if (resolver is null) return ConflictDecision.Skip;
        lock (_sync) job.State = JobState.ResolvingConflict;
        _dirty = true;
        try
        {
            return resolver(job);
        }
        finally
        {
            lock (_sync)
            {
                // 询问期间可能被暂停/取消：只恢复仍在询问态的作业
                if (job.State == JobState.ResolvingConflict)
                    job.State = JobState.Transferring;
            }
            _dirty = true;
        }
    }

    internal static string FindFreeName(string path)
    {
        var dir = Path.GetDirectoryName(path) ?? "";
        var name = Path.GetFileNameWithoutExtension(path);
        var ext = Path.GetExtension(path);
        for (int i = 2; i < 1000; i++)
        {
            var candidate = Path.Combine(dir, $"{name} ({i}){ext}");
            if (!File.Exists(candidate) && !File.Exists(candidate + ".djpart") && !File.Exists(candidate + ".djjournal")) return candidate;
        }
        throw new IOException("无法生成不冲突的文件名");
    }

    private static FileStream OpenSource(string path)
    {
        try { return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024); }
        catch (FileNotFoundException) { throw DeviceOfflineException.Instance; }
        catch (DirectoryNotFoundException) { throw DeviceOfflineException.Instance; }
        catch (IOException) { throw new IOException("源打开失败：" + path); }
    }

    /// <summary>journal 不可信时：源与 .djpart 逐块对读，保留匹配前缀（对齐到块），截断 part 并重建 journal。</summary>
    private TransferJournal RebuildFromPartial(TransferJob job, string dest, string part)
    {
        var req = job.Req;
        var journal = TransferJournal.Create(req with { DestPath = dest });
        long partLen = GetFileLength(part);
        long srcLen = req.SizeBytes;
        int fullBlocks = (int)Math.Min(partLen / TransferJournal.BlockSize, Math.Max(0, srcLen / TransferJournal.BlockSize));
        using var src = OpenSource(req.SourcePath);
        using var p = new FileStream(part, FileMode.Open, FileAccess.Read, FileShare.None);
        var bufSrc = new byte[512 * 1024];
        var bufPart = new byte[512 * 1024];
        int verifiedBlocks = 0;
        for (int b = 0; b < fullBlocks; b++)
        {
            var hash = new XxHash3();
            long blockStart = (long)b * TransferJournal.BlockSize;
            long done = 0;
            bool mismatch = false;
            while (done < TransferJournal.BlockSize)
            {
                int take = (int)Math.Min(bufSrc.Length, TransferJournal.BlockSize - done);
                if (!TransferJournal.ReadExact(src, bufSrc, take) || !TransferJournal.ReadExact(p, bufPart, take)) { mismatch = true; break; }
                if (!bufSrc.AsSpan(0, take).SequenceEqual(bufPart.AsSpan(0, take))) { mismatch = true; break; }
                hash.Append(bufSrc.AsSpan(0, take));
                done += take;
            }
            if (mismatch) break;
            journal.RecordBlock(hash.GetCurrentHashAsUInt64(), TransferJournal.BlockSize);
            verifiedBlocks++;
        }
        long keep = (long)verifiedBlocks * TransferJournal.BlockSize;
        p.Dispose();
        using (var pw = new FileStream(part, FileMode.Open, FileAccess.Write, FileShare.None))
            pw.SetLength(keep);
        journal.Save();
        return journal;
    }

    private static bool VerifyLastBlock(string part, TransferJournal journal)
    {
        if (journal.BlockHashes.Count == 0 || journal.Offset == 0) return true;
        int k = journal.BlockHashes.Count;
        long blockStart = (long)(k - 1) * TransferJournal.BlockSize;
        int blockLen = (int)(journal.Offset - blockStart);
        if (blockLen <= 0) return false;
        try
        {
            using var p = new FileStream(part, FileMode.Open, FileAccess.Read, FileShare.Read);
            var hash = new XxHash3();
            var buf = new byte[512 * 1024];
            p.Position = blockStart;
            long done = 0;
            while (done < blockLen)
            {
                int take = (int)Math.Min(buf.Length, blockLen - done);
                if (!TransferJournal.ReadExact(p, buf, take)) return false;
                hash.Append(buf.AsSpan(0, take));
                done += take;
            }
            return hash.GetCurrentHashAsUInt64() == journal.BlockHashes[k - 1];
        }
        catch (IOException) { return false; }
    }

    private void VerifyPart(TransferJob job, string part, TransferJournal journal, long expectedSize)
    {
        using var p = new FileStream(part, FileMode.Open, FileAccess.Read, FileShare.None, 1024 * 1024);
        if (p.Length != expectedSize)
            throw new IOException($"校验失败：大小不符（{p.Length} != {expectedSize}）");
        var buf = new byte[512 * 1024];
        for (int b = 0; b < journal.BlockHashes.Count; b++)
        {
            job.PauseGate.Wait(_cts.Token);
            _globalPause.Wait(_cts.Token);
            if (job.CancelRequested) throw JobCanceledException.Instance;
            long blockStart = (long)b * TransferJournal.BlockSize;
            int blockLen = (int)Math.Min(TransferJournal.BlockSize, expectedSize - blockStart);
            var hash = new XxHash3();
            long done = 0;
            while (done < blockLen)
            {
                int take = (int)Math.Min(buf.Length, blockLen - done);
                if (!TransferJournal.ReadExact(p, buf, take)) throw new IOException("校验读取失败");
                hash.Append(buf.AsSpan(0, take));
                done += take;
            }
            if (hash.GetCurrentHashAsUInt64() != journal.BlockHashes[b])
            {
                TransferJournal.DeleteFiles(journal.DestPath);
                throw new IOException($"校验失败：第 {b + 1} 块哈希不符（已清空现场，重试将完整重传）");
            }
        }
    }

    private static void UpdateSpeed(TransferJob job, long totalBytes)
    {
        long now = DateTime.UtcNow.Ticks;
        long prev = Interlocked.Read(ref job.LastSampleTicks);
        long delta = now - prev;
        if (delta >= 300 * TimeSpan.TicksPerMillisecond)
        {
            long bytes = totalBytes - Interlocked.Read(ref job.LastSampleBytes);
            double inst = bytes / (delta / (double)TimeSpan.TicksPerSecond);
            job.EmaSpeed = job.EmaSpeed <= 0 ? inst : 0.7 * job.EmaSpeed + 0.3 * inst;
            Interlocked.Exchange(ref job.LastSampleTicks, now);
            Interlocked.Exchange(ref job.LastSampleBytes, totalBytes);
        }
    }

    // ---------- 空间 ----------

    public static long GetFreeSpace(string path)
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(path));
            if (string.IsNullOrEmpty(root)) return 0;
            return new DriveInfo(root).AvailableFreeSpace;
        }
        catch (Exception e) when (e is ArgumentException or IOException or UnauthorizedAccessException) { return 0; }
    }

    public static bool CheckFreeSpace(string destPath, long neededBytes)
        => GetFreeSpace(destPath) >= neededBytes;

    internal static string Fmt(long bytes)
        => bytes >= 1L << 30 ? $"{bytes / 1024.0 / 1024 / 1024:F2} GB"
         : bytes >= 1L << 20 ? $"{bytes / 1024.0 / 1024:F1} MB"
         : $"{bytes / 1024.0:F0} KB";

    internal static long GetFileLength(string path)
    {
        try { return new FileInfo(path).Length; }
        catch (IOException) { return -1; }
        catch (UnauthorizedAccessException) { return -1; }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _cts.Cancel();
        _globalPause.Set();
        _snapTimer.Dispose();
        lock (_sync)
        {
            foreach (var j in _jobs)
            {
                j.CancelRequested = true;
                j.PauseGate.Set();
            }
        }
        foreach (var w in _workers) w.Join(8000);
        try { _wake.Dispose(); } catch (ObjectDisposedException) { }
        try { _cts.Dispose(); } catch (ObjectDisposedException) { }
    }
}
