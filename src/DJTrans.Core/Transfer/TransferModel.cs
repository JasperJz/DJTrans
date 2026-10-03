namespace DJTrans.Core.Transfer;

public enum TransferDirection { Download, Upload }

public enum JobState
{
    Queued,            // 排队
    ResolvingConflict, // 等待冲突决策（Ask 策略）
    Transferring,      // 传输中
    Verifying,         // 校验中
    Paused,            // 手动暂停
    WaitingDevice,     // 设备离线，自动等待重连
    Done,              // 完成
    Skipped,           // 跳过（含增量去重/用户跳过）
    Failed,            // 失败
    Canceled,          // 取消
}

public enum ConflictPolicy
{
    /// <summary>智能跳过（默认）：目标同名且指纹相同（大小+首尾块哈希）自动跳过；不同则询问。</summary>
    SmartSkip,
    Ask,
    Skip,
    Overwrite,
    RenameNew, // 新文件自动改名 name (2).ext
}

public enum ConflictDecision { Skip, Overwrite, RenameNew }

public enum LayoutMode
{
    /// <summary>镜像相机目录结构：dest\DJI_001\xxx.jpg</summary>
    Mirror,
    /// <summary>按拍摄日期：dest\2026-10-03\xxx.jpg</summary>
    ByDate,
    /// <summary>平铺到目标根目录</summary>
    Flat,
}

public enum VerifyMode { SizeOnly, BlockHash }

public sealed record TransferOptions
{
    public ConflictPolicy ConflictPolicy { get; init; } = ConflictPolicy.SmartSkip;
    public VerifyMode Verify { get; init; } = VerifyMode.BlockHash;
    public bool PreserveModifiedTime { get; init; } = true;
    public bool StrictSkipVerification { get; init; }
}

public sealed record TransferRequest
{
    public required string SourcePath { get; init; }
    public required string DestPath { get; init; }
    public required long SizeBytes { get; init; }
    public required DateTime SourceMtimeUtc { get; init; }
    /// <summary>设备侧路径：Download 时是 SourcePath，Upload 时是 DestPath。用于设备离线判断。</summary>
    public required string DevicePath { get; init; }
    /// <summary>来源卷标识（续传指纹成分）。</summary>
    public required string VolumeKey { get; init; }
    public TransferDirection Direction { get; init; } = TransferDirection.Download;
}

/// <summary>作业的不可变快照，UI 只见此对象（每 ~100ms 刷新，避免万级事件风暴）。</summary>
public sealed record JobSnapshot
{
    public required long Id { get; init; }
    public required string BatchId { get; init; }
    public required string SourcePath { get; init; }
    public required string DestPath { get; init; }
    public required string Name { get; init; }
    public required TransferDirection Direction { get; init; }
    public required long SizeBytes { get; init; }
    public required long BytesDone { get; init; }
    public required JobState State { get; init; }
    public double SpeedBytesPerSec { get; init; }
    public string? Error { get; init; }
    public string? SkipReason { get; init; }
    public string? CompletionNote { get; init; }
    public long ResumedFromOffset { get; init; }
    public DateTime? StartedUtc { get; init; }
    public DateTime? FinishedUtc { get; init; }
    public double Progress => SizeBytes <= 0 ? (State is JobState.Done or JobState.Skipped ? 1 : 0) : Math.Min(1.0, (double)BytesDone / SizeBytes);
}

public sealed record EngineSnapshot
{
    public required IReadOnlyList<JobSnapshot> Jobs { get; init; }
    public bool GlobalPaused { get; init; }
    public long TotalBytes { get; init; }
    public long DoneBytes { get; init; }
    public long FinishedCount { get; init; }
    public long ActiveCount { get; init; }
    public long PendingCount { get; init; }
    public long FailedCount { get; init; }
    public double OverallSpeedBytesPerSec { get; init; }
    public TimeSpan Eta => OverallSpeedBytesPerSec <= 0
        ? Timeout.InfiniteTimeSpan
        : TimeSpan.FromSeconds(Math.Max(0, TotalBytes - DoneBytes) / OverallSpeedBytesPerSec);
    public bool AllSettled => Jobs.Count > 0 && Jobs.All(j => j.State is JobState.Done or JobState.Skipped or JobState.Failed or JobState.Canceled);
}
