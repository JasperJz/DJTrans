using System.IO.Hashing;
using DJTrans.Core.Scan;
using DJTrans.Core.Transfer;
using Xunit.Abstractions;

namespace DJTrans.Core.Tests.Transfer;

public sealed class TransferEngineTests : IDisposable
{
    private readonly ITestOutputHelper _out;
    private readonly string _root;
    private readonly string _srcDir;
    private readonly string _dstDir;

    public TransferEngineTests(ITestOutputHelper output)
    {
        _out = output;
        _root = Path.Combine(Path.GetTempPath(), "djtrans-tests", Guid.NewGuid().ToString("N"));
        _srcDir = Path.Combine(_root, "device", "DCIM", "DJI_001");
        _dstDir = Path.Combine(_root, "pc", "export");
        Directory.CreateDirectory(_srcDir);
        Directory.CreateDirectory(_dstDir);
        TransferEngine.OnlineCheckOverride = _ => true;
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch (IOException) { }
        TransferEngine.OnlineCheckOverride = null;
    }

    private string MakeSource(string name, long size, DateTime? mtime = null)
    {
        var p = Path.Combine(_srcDir, name);
        var buf = new byte[1024 * 1024];
        using var f = File.Create(p);
        long left = size;
        while (left > 0)
        {
            Random.Shared.NextBytes(buf);
            var take = (int)Math.Min(buf.Length, left);
            f.Write(buf, 0, take);
            left -= take;
        }
        var t = mtime ?? new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(p, t);
        return p;
    }

    private TransferRequest Req(string src, string? dstName = null, TransferDirection dir = TransferDirection.Download)
    {
        var fi = new FileInfo(src);
        return new TransferRequest
        {
            SourcePath = src,
            DestPath = Path.Combine(_dstDir, dstName ?? Path.GetFileName(src)),
            SizeBytes = fi.Length,
            SourceMtimeUtc = fi.LastWriteTimeUtc,
            DevicePath = dir == TransferDirection.Download ? src : Path.Combine(_dstDir, dstName ?? ""),
            VolumeKey = "TEST|VOL|0001",
            Direction = dir,
        };
    }

    private static bool WaitUntil(Func<bool> cond, int timeoutMs = 60000)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            if (cond()) return true;
            Thread.Sleep(25);
        }
        return cond();
    }

    private static byte[] HashFile(string p) => XxHash3.Hash(File.ReadAllBytes(p));

    [Fact]
    public void SmallFile_CopiesAndVerifies_WithMtimePreserved()
    {
        using var eng = new TransferEngine(workers: 1);
        var src = MakeSource("DJI_20260101010101_0001_D.JPG", 300_000);
        var dst = Path.Combine(_dstDir, "DJI_20260101010101_0001_D.JPG");
        eng.EnqueueBatch([Req(src)], new TransferOptions());
        Assert.True(WaitUntil(() => eng.Snapshot().AllSettled), "timeout");
        var job = eng.Snapshot().Jobs.Single();
        Assert.Equal(JobState.Done, job.State);
        Assert.Equal(HashFile(src), HashFile(dst));
        Assert.Equal(new FileInfo(src).LastWriteTimeUtc, new FileInfo(dst).LastWriteTimeUtc);
        Assert.False(File.Exists(dst + ".djpart"));
        Assert.False(File.Exists(dst + ".djjournal"));
    }

    [Fact]
    public void MultiBlockLargeFile_CopiesAndVerifies()
    {
        using var eng = new TransferEngine(workers: 1);
        var src = MakeSource("DJI_20260101010102_0002_D.MP4", 20L * 1024 * 1024 + 12345); // 2.5 块
        var dst = Path.Combine(_dstDir, Path.GetFileName(src));
        eng.EnqueueBatch([Req(src)], new TransferOptions());
        Assert.True(WaitUntil(() => eng.Snapshot().AllSettled));
        Assert.Equal(JobState.Done, eng.Snapshot().Jobs.Single().State);
        Assert.Equal(HashFile(src), HashFile(dst));
    }

    [Fact]
    public void ZeroByteFile_Completes()
    {
        using var eng = new TransferEngine(workers: 1);
        var src = MakeSource("empty.jpg", 0);
        var dst = Path.Combine(_dstDir, "empty.jpg");
        eng.EnqueueBatch([Req(src)], new TransferOptions());
        Assert.True(WaitUntil(() => eng.Snapshot().AllSettled));
        Assert.Equal(JobState.Done, eng.Snapshot().Jobs.Single().State);
        Assert.True(File.Exists(dst) && new FileInfo(dst).Length == 0);
    }

    [Fact]
    public void ResumeFromValidJournal_ContinuesAtOffset_WithoutRecopyingPrefix()
    {
        var src = MakeSource("DJI_20260101010103_0003_D.MP4", 20L * 1024 * 1024); // 恰好 2 块
        var dst = Path.Combine(_dstDir, Path.GetFileName(src));
        // 手工构造 1 块的续传现场（模拟上次中断）
        var j = TransferJournal.Create(Req(src));
        using (var s = File.OpenRead(src))
        {
            var buf = new byte[TransferJournal.BlockSize];
            s.ReadExactly(buf);
            var h = new XxHash3();
            h.Append(buf);
            j.RecordBlock(h.GetCurrentHashAsUInt64(), TransferJournal.BlockSize);
        }
        Directory.CreateDirectory(_dstDir);
        using (var part = File.Create(dst + ".djpart"))
        using (var s = File.OpenRead(src))
        {
            var buf = new byte[TransferJournal.BlockSize];
            s.ReadExactly(buf);
            part.Write(buf);
        }
        j.Save();

        using var eng = new TransferEngine(workers: 1);
        eng.Logged += m => _out.WriteLine(m);
        eng.EnqueueBatch([Req(src)], new TransferOptions());
        Assert.True(WaitUntil(() => eng.Snapshot().AllSettled));
        var job = eng.Snapshot().Jobs.Single();
        Assert.Equal(JobState.Done, job.State);
        Assert.Equal(TransferJournal.BlockSize, job.ResumedFromOffset); // 前缀未重传
        Assert.Equal(HashFile(src), HashFile(dst));
    }

    [Fact]
    public void TornJournal_RebuildsPrefixFromPartialContent()
    {
        var src = MakeSource("DJI_20260101010104_0004_D.MP4", 20L * 1024 * 1024);
        var dst = Path.Combine(_dstDir, Path.GetFileName(src));
        Assert.False(File.Exists(dst)); // 前置：目标不存在（否则走冲突分支）
        // 手工构造现场：完整 journal + 1 块 part，再撕裂 journal 尾部
        var j = TransferJournal.Create(Req(src));
        using (var s = File.OpenRead(src))
        {
            var buf = new byte[TransferJournal.BlockSize];
            s.ReadExactly(buf);
            var h = new XxHash3();
            h.Append(buf);
            j.RecordBlock(h.GetCurrentHashAsUInt64(), TransferJournal.BlockSize);
        }
        j.Save();
        using (var part = File.Create(dst + ".djpart"))
        using (var s = File.OpenRead(src))
        {
            var buf = new byte[TransferJournal.BlockSize];
            s.ReadExactly(buf);
            part.Write(buf);
        }
        var jb = File.ReadAllBytes(dst + ".djjournal");
        Array.Resize(ref jb, jb.Length - 3); // 撕裂
        File.WriteAllBytes(dst + ".djjournal", jb);

        using var eng = new TransferEngine(workers: 1);
        eng.Logged += m => _out.WriteLine(m);
        eng.EnqueueBatch([Req(src)], new TransferOptions());
        Assert.True(WaitUntil(() => eng.Snapshot().AllSettled));
        var job = eng.Snapshot().Jobs.Single();
        Assert.Equal(JobState.Done, job.State);
        // 重建前缀后从 8MB 继续：ResumedFromOffset >= 8MB 证明前缀被保留
        Assert.True(job.ResumedFromOffset >= TransferJournal.BlockSize, $"offset={job.ResumedFromOffset}");
        Assert.Equal(HashFile(src), HashFile(dst));
    }

    [Fact]
    public void SmartSkip_SecondExport_IdenticalFileIsSkippedSilently()
    {
        var src = MakeSource("DJI_20260101010105_0005_D.JPG", 1_500_000);
        using var eng = new TransferEngine(workers: 1);
        eng.EnqueueBatch([Req(src)], new TransferOptions { ConflictPolicy = ConflictPolicy.SmartSkip });
        Assert.True(WaitUntil(() => eng.Snapshot().AllSettled));
        Assert.Equal(JobState.Done, eng.Snapshot().Jobs.Single().State);

        eng.RemoveFinished();
        eng.EnqueueBatch([Req(src)], new TransferOptions { ConflictPolicy = ConflictPolicy.SmartSkip });
        Assert.True(WaitUntil(() => eng.Snapshot().AllSettled));
        var job = eng.Snapshot().Jobs.Single();
        Assert.Equal(JobState.Skipped, job.State);
        Assert.Contains("增量跳过", job.SkipReason);
    }

    [Fact]
    public void SmartSkip_DifferentContent_AskResolverAppliesDecision()
    {
        var src1 = MakeSource("conflict.jpg", 500_000);
        var dst = Path.Combine(_dstDir, "conflict.jpg");
        File.Copy(src1, dst);
        // 源变化（同大小不同内容）
        var bytes = File.ReadAllBytes(src1);
        bytes[^1] ^= 0xFF;
        bytes[0] ^= 0xFF;
        File.WriteAllBytes(src1, bytes);
        File.SetLastWriteTimeUtc(src1, new FileInfo(src1).LastWriteTimeUtc.AddSeconds(1));

        ConflictDecision seen = ConflictDecision.Overwrite;
        using var eng = new TransferEngine(workers: 1, j =>
        {
            Assert.Equal(JobState.ResolvingConflict, j.State);
            return seen;
        });
        var req = Req(src1);
        eng.EnqueueBatch([req], new TransferOptions { ConflictPolicy = ConflictPolicy.SmartSkip });
        Assert.True(WaitUntil(() => eng.Snapshot().AllSettled));
        Assert.Equal(JobState.Done, eng.Snapshot().Jobs.Single().State);
        Assert.Equal(File.ReadAllBytes(src1), File.ReadAllBytes(dst)); // 已覆盖
    }

    [Fact]
    public void RenameNew_CreatesNumberedFile()
    {
        var src1 = MakeSource("rename.jpg", 400_000);
        var src2 = MakeSource("rename.jpg", 400_000, mtime: DateTime.UtcNow.AddDays(-1)); // 同名不同源
        var dst = Path.Combine(_dstDir, "rename.jpg");
        using var eng = new TransferEngine(workers: 1);
        eng.EnqueueBatch([Req(src1)], new TransferOptions { ConflictPolicy = ConflictPolicy.Overwrite });
        Assert.True(WaitUntil(() => eng.Snapshot().AllSettled));
        eng.RemoveFinished();
        eng.EnqueueBatch([Req(src2, "rename.jpg")], new TransferOptions { ConflictPolicy = ConflictPolicy.RenameNew });
        Assert.True(WaitUntil(() => eng.Snapshot().AllSettled));
        Assert.Equal(JobState.Done, eng.Snapshot().Jobs.Single().State);
        Assert.True(File.Exists(Path.Combine(_dstDir, "rename (2).jpg")));
        Assert.Equal(400_000, new FileInfo(dst).Length);
    }

    [Fact]
    public void QueueDedupes_SameDestTwice()
    {
        var src = MakeSource("dup.jpg", 300_000);
        using var eng = new TransferEngine(workers: 1);
        var r1 = eng.EnqueueBatch([Req(src)], new TransferOptions());
        var r2 = eng.EnqueueBatch([Req(src)], new TransferOptions());
        Assert.Equal(1, r1.Accepted);
        Assert.Equal(0, r2.Accepted);
        Assert.Equal(1, r2.RejectedDuplicate);
        Assert.True(WaitUntil(() => eng.Snapshot().AllSettled));
        Assert.Single(eng.Snapshot().Jobs);
    }

    [Fact]
    public void CancelKeepsPart_RetryCompletesWithResume()
    {
        var src = MakeSource("DJI_20260101010106_0006_D.MP4", 48L * 1024 * 1024); // 6 块
        var dst = Path.Combine(_dstDir, Path.GetFileName(src));
        using var eng = new TransferEngine(workers: 1);
        eng.Logged += m => _out.WriteLine(m);
        eng.EnqueueBatch([Req(src)], new TransferOptions());
        var snap = eng.Snapshot();
        Assert.True(WaitUntil(() => eng.Snapshot().Jobs.Any(j => j.BytesDone > 2L * 1024 * 1024)), "未开始传输");
        var id = eng.Snapshot().Jobs.Single().Id;
        eng.CancelJob(id);
        Assert.True(WaitUntil(() => eng.Snapshot().Jobs.Single().State == JobState.Canceled));
        Assert.True(File.Exists(dst + ".djpart"), "取消后应保留 .djpart 续传现场");
        Assert.True(File.Exists(dst + ".djjournal"));

        eng.RetryJob(id);
        Assert.True(WaitUntil(() => eng.Snapshot().AllSettled));
        var job = eng.Snapshot().Jobs.Single();
        Assert.Equal(JobState.Done, job.State);
        Assert.True(job.ResumedFromOffset > 0, "重试应从断点续传");
        Assert.Equal(HashFile(src), HashFile(dst));
    }

    [Fact]
    public void DeviceOffline_GoesWaiting_ThenAutoResumesWhenBack()
    {
        var src = MakeSource("DJI_20260101010107_0007_D.MP4", 16L * 1024 * 1024);
        var dst = Path.Combine(_dstDir, Path.GetFileName(src));
        bool online = false;
        TransferEngine.OnlineCheckOverride = _ => online;
        using var eng = new TransferEngine(workers: 1);
        eng.Logged += m => _out.WriteLine(m);
        eng.EnqueueBatch([Req(src)], new TransferOptions());
        Assert.True(WaitUntil(() => eng.Snapshot().Jobs.Single().State == JobState.WaitingDevice), "应进入等待设备");

        online = true;
        eng.CheckWaitingJobs();
        Assert.True(WaitUntil(() => eng.Snapshot().AllSettled));
        Assert.Equal(JobState.Done, eng.Snapshot().Jobs.Single().State);
        Assert.Equal(HashFile(src), HashFile(dst));
    }

    [Fact]
    public void ForgedBlockHash_SelfHealsByRecopyAndNeverShipsCorruptFile()
    {
        var src = MakeSource("DJI_20260101010108_0008_D.MP4", 20L * 1024 * 1024);
        var dst = Path.Combine(_dstDir, Path.GetFileName(src));
        // 伪造"已完成"现场：part=完整拷贝，journal 第 1 块哈希错误（末块正确以通过末块复验）
        File.Copy(src, dst + ".djpart");
        var j = TransferJournal.Create(Req(src));
        using (var s = File.OpenRead(src))
        {
            var buf = new byte[TransferJournal.BlockSize];
            for (int b = 0; b < 2; b++)
            {
                s.ReadExactly(buf);
                var h = new XxHash3();
                h.Append(buf);
                j.RecordBlock(b == 0 ? 0xDEADBEEFUL : h.GetCurrentHashAsUInt64(), TransferJournal.BlockSize);
            }
        }
        j.Save();

        using var eng = new TransferEngine(workers: 1);
        eng.Logged += m => _out.WriteLine(m);
        eng.EnqueueBatch([Req(src)], new TransferOptions());
        Assert.True(WaitUntil(() => eng.Snapshot().AllSettled, 120_000));
        var job = eng.Snapshot().Jobs.Single();
        // 终态必须是健康完成：校验失败→清场→自动重传（引擎自愈，绝不交付损坏文件）
        Assert.Equal(JobState.Done, job.State);
        Assert.Null(job.Error);
        Assert.Equal(HashFile(src), HashFile(dst));
        Assert.False(File.Exists(dst + ".djpart"));
    }

    [Fact]
    public void FingerprintMismatch_DestExistsDifferentFile_OverwritesWhenTold()
    {
        var src = MakeSource("over.jpg", 700_000);
        var dst = Path.Combine(_dstDir, "over.jpg");
        File.WriteAllText(dst, "not the same content at all");
        using var eng = new TransferEngine(workers: 1);
        eng.EnqueueBatch([Req(src)], new TransferOptions { ConflictPolicy = ConflictPolicy.Overwrite });
        Assert.True(WaitUntil(() => eng.Snapshot().AllSettled));
        Assert.Equal(JobState.Done, eng.Snapshot().Jobs.Single().State);
        Assert.Equal(HashFile(src), HashFile(dst));
    }

    [Fact]
    public void UploadDirection_WritesIntoDevicePath()
    {
        var src = MakeSource("__DJTRANS_TEST__upload.jpg", 256_000);
        var deviceDir = Path.Combine(_root, "device", "DCIM", "DJI_002");
        Directory.CreateDirectory(deviceDir);
        var dst = Path.Combine(deviceDir, "__DJTRANS_TEST__upload.jpg");
        var fi = new FileInfo(src);
        var req = new TransferRequest
        {
            SourcePath = src,
            DestPath = dst,
            SizeBytes = fi.Length,
            SourceMtimeUtc = fi.LastWriteTimeUtc,
            DevicePath = dst,
            VolumeKey = "TEST|VOL|0001",
            Direction = TransferDirection.Upload,
        };
        using var eng = new TransferEngine(workers: 1);
        eng.EnqueueBatch([req], new TransferOptions());
        Assert.True(WaitUntil(() => eng.Snapshot().AllSettled));
        Assert.Equal(JobState.Done, eng.Snapshot().Jobs.Single().State);
        Assert.Equal(HashFile(src), HashFile(dst));
    }

    [Fact]
    public void PauseAll_HaltsProgress_ResumeCompletes()
    {
        var src = MakeSource("DJI_20260101010109_0009_D.MP4", 24L * 1024 * 1024);
        using var eng = new TransferEngine(workers: 1);
        eng.EnqueueBatch([Req(src)], new TransferOptions());
        Assert.True(WaitUntil(() => eng.Snapshot().Jobs.Any(j => j.BytesDone > 1024 * 1024)));
        eng.PauseAll();
        long frozen = eng.Snapshot().Jobs.Single().BytesDone;
        Thread.Sleep(600);
        Assert.Equal(frozen, eng.Snapshot().Jobs.Single().BytesDone); // 暂停期间无进展
        eng.ResumeAll();
        Assert.True(WaitUntil(() => eng.Snapshot().AllSettled));
        Assert.Equal(JobState.Done, eng.Snapshot().Jobs.Single().State);
    }

    // ---------- R1-B1 状态机回归：冲突询问期间暂停 → 回答后必须停在 Paused，可恢复 ----------

    [Fact]
    public void PauseDuringConflictDialog_ThenAnswer_JobPausesNotDeadlocks()
    {
        var src = MakeSource("DJI_20260101010120_0020_D.MP4", 16L * 1024 * 1024);
        var dst = Path.Combine(_dstDir, Path.GetFileName(src));
        MakeSource("DJI_20260101010120_0020_D.MP4", 16L * 1024 * 1024); // 建两次随机内容不同
        File.Copy(src + "", dst, true); // 目标同名存在 → 触发 SmartSkip 的询问分支
        // 让两次内容必然不同：覆盖 dst 尾部
        using (var f = File.Open(dst, FileMode.Open, FileAccess.Write))
        {
            f.Seek(-1, SeekOrigin.End);
            f.WriteByte((byte)(new FileInfo(src).Length ^ 0x5A));
        }
        File.SetLastWriteTimeUtc(dst, DateTime.UtcNow.AddDays(-3));

        var resolverEntered = new TaskCompletionSource<long>(TaskCreationOptions.RunContinuationsAsynchronously);
        var answer = new TaskCompletionSource<ConflictDecision>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var eng = new TransferEngine(workers: 1, j =>
        {
            resolverEntered.TrySetResult(j.Id);
            return answer.Task.Result; // 阻塞到测试放行
        });
        eng.EnqueueBatch([Req(src)], new TransferOptions { ConflictPolicy = ConflictPolicy.SmartSkip });
        Assert.True(resolverEntered.Task.Wait(5000));
        long id = resolverEntered.Task.Result;

        // 询问期间暂停（R1-B1 路径1：曾导致 finally 无条件 Transferring + 关门 → 永久卡死）
        eng.PauseJob(id);
        answer.TrySetResult(ConflictDecision.Overwrite);
        Assert.True(WaitUntil(() => eng.Snapshot().Jobs.Single().State == JobState.Paused), "回答后应停在 Paused 而非卡在 Transferring");
        long bytes = eng.Snapshot().Jobs.Single().BytesDone;
        Thread.Sleep(500);
        Assert.Equal(bytes, eng.Snapshot().Jobs.Single().BytesDone); // 真的暂停了

        eng.ResumeJob(id);
        Assert.True(WaitUntil(() => eng.Snapshot().AllSettled));
        Assert.Equal(JobState.Done, eng.Snapshot().Jobs.Single().State);
        Assert.Equal(HashFile(src), HashFile(dst));
    }

    // ---------- R1-I2：worker 未持有的作业可被直接取消，且目标锁被释放 ----------

    [Fact]
    public void CancelQueuedJob_MigratesImmediately_AndFreesDestLock()
    {
        var srcBusy = MakeSource("DJI_20260101010121_0021_D.MP4", 8L * 1024 * 1024);
        var srcWait = MakeSource("DJI_20260101010122_0022_D.MP4", 4L * 1024 * 1024);
        var dstBusy = Path.Combine(_dstDir, Path.GetFileName(srcBusy));
        File.WriteAllBytes(dstBusy, "different"u8.ToArray());

        var resolverEntered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var answer = new TaskCompletionSource<ConflictDecision>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var eng = new TransferEngine(workers: 1, _ =>
        {
            resolverEntered.TrySetResult(true);
            return answer.Task.Result;
        });
        var r1 = eng.EnqueueBatch([Req(srcBusy)], new TransferOptions { ConflictPolicy = ConflictPolicy.SmartSkip });
        Assert.True(resolverEntered.Task.Wait(5000)); // worker 被作业1占住（询问中）
        var r2 = eng.EnqueueBatch([Req(srcWait)], new TransferOptions()); // 作业2 排队，无 worker 持有
        Assert.Equal(1, r2.Accepted);
        var id2 = eng.Snapshot().Jobs.First(j => j.SourcePath == srcWait).Id;

        eng.CancelJob(id2);
        var s = eng.Snapshot();
        Assert.Equal(JobState.Canceled, s.Jobs.First(j => j.Id == id2).State); // 立即终态

        // 目标锁已释放：同一目标可重新入队
        var r3 = eng.EnqueueBatch([Req(srcWait)], new TransferOptions());
        Assert.Equal(1, r3.Accepted);

        answer.TrySetResult(ConflictDecision.Skip); // 放行作业1
        Assert.True(WaitUntil(() => eng.Snapshot().AllSettled));
    }

    // ---------- R1-B1 路径2变体：排队中暂停 → 恢复必须唤醒 worker ----------

    [Fact]
    public void PauseQueuedJob_ThenResume_IsPickedUpAndCompletes()
    {
        var srcBusy = MakeSource("DJI_20260101010123_0023_D.MP4", 4L * 1024 * 1024);
        var srcWait = MakeSource("DJI_20260101010124_0024_D.MP4", 4L * 1024 * 1024);
        var dstBusy = Path.Combine(_dstDir, Path.GetFileName(srcBusy));
        File.WriteAllBytes(dstBusy, "different"u8.ToArray());
        var resolverEntered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var answer = new TaskCompletionSource<ConflictDecision>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var eng = new TransferEngine(workers: 1, _ =>
        {
            resolverEntered.TrySetResult(true);
            return answer.Task.Result;
        });
        eng.EnqueueBatch([Req(srcBusy)], new TransferOptions { ConflictPolicy = ConflictPolicy.SmartSkip });
        Assert.True(resolverEntered.Task.Wait(5000));
        eng.EnqueueBatch([Req(srcWait)], new TransferOptions());
        var id2 = eng.Snapshot().Jobs.First(j => j.SourcePath == srcWait).Id;

        eng.PauseJob(id2);
        Assert.Equal(JobState.Paused, eng.Snapshot().Jobs.First(j => j.Id == id2).State);
        answer.TrySetResult(ConflictDecision.Skip); // 释放 worker
        Thread.Sleep(300);
        Assert.Equal(JobState.Paused, eng.Snapshot().Jobs.First(j => j.Id == id2).State); // 暂停期间不被领取

        eng.ResumeJob(id2);
        Assert.True(WaitUntil(() => eng.Snapshot().Jobs.First(j => j.Id == id2).State == JobState.Done), "恢复后必须被 worker 领取并完成");
    }
}

public sealed class JournalUnitTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "djtrans-tests", Guid.NewGuid().ToString("N"));
    public JournalUnitTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } }

    private TransferRequest MakeReq(string src, string dst, long size, DateTime mtime) => new()
    {
        SourcePath = src, DestPath = dst, SizeBytes = size, SourceMtimeUtc = mtime,
        DevicePath = src, VolumeKey = "V|1", Direction = TransferDirection.Download,
    };

    [Fact]
    public void SaveLoad_Roundtrips()
    {
        var req = MakeReq("C:\\a.jpg", Path.Combine(_dir, "a.jpg"), 20L * 1024 * 1024, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        var j = TransferJournal.Create(req);
        j.RecordBlock(42, TransferJournal.BlockSize);
        j.Save();
        var loaded = TransferJournal.TryLoad(req);
        Assert.NotNull(loaded);
        Assert.Equal(TransferJournal.BlockSize, loaded!.Offset);
        Assert.Equal(42UL, loaded.BlockHashes.Single());
    }

    [Fact]
    public void FingerprintMismatch_ReturnsNull()
    {
        var req = MakeReq("C:\\a.jpg", Path.Combine(_dir, "a.jpg"), 12345, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        var j = TransferJournal.Create(req);
        j.Save();
        var req2 = req with { SizeBytes = 999 };
        Assert.Null(TransferJournal.TryLoad(req2));
    }

    [Fact]
    public void QuickFingerprint_DetectsSameAndDifferent()
    {
        var a = Path.Combine(_dir, "a.bin");
        var b = Path.Combine(_dir, "b.bin");
        var c = Path.Combine(_dir, "c.bin");
        var data = new byte[1024 * 1024];
        Random.Shared.NextBytes(data);
        File.WriteAllBytes(a, data);
        File.WriteAllBytes(b, data);
        var d2 = (byte[])data.Clone();
        d2[^1] ^= 0xFF;
        File.WriteAllBytes(c, d2);
        Assert.True(TransferJournal.QuickFingerprintEqual(a, b, data.Length));
        Assert.False(TransferJournal.QuickFingerprintEqual(a, c, data.Length));
        Assert.False(TransferJournal.QuickFingerprintEqual(a, c + "x", data.Length)); // 不存在
    }

    [Fact]
    public void FindFreeName_Increments()
    {
        var p = Path.Combine(_dir, "f.jpg");
        File.WriteAllText(p, "x");
        var n1 = TransferEngine.FindFreeName(p);
        Assert.Equal("f (2).jpg", Path.GetFileName(n1));
        File.WriteAllText(n1, "x");
        var n2 = TransferEngine.FindFreeName(p);
        Assert.Equal("f (3).jpg", Path.GetFileName(n2));
    }
}
