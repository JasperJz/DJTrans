using DJTrans.Core.Transfer;

namespace DJTrans.Controls;

/// <summary>
/// 底部传输面板：作业表格 + 全局控制 + 汇总进度。由引擎快照（~100ms）驱动，纯只读渲染。
/// </summary>
public sealed class TransferDock : UserControl
{
    private readonly DataGridView _grid;
    private readonly ToolStrip _strip;
    private readonly ProgressBar _overall;
    private readonly Label _summary;
    private readonly Button _collapse;
    private readonly Panel _content;
    private bool _collapsed;
    private EngineSnapshot? _last;

    public event Action? RetryAllFailedRequested;
    public event Action? PauseAllRequested;
    public event Action? ResumeAllRequested;
    public event Action? ClearFinishedRequested;
    public event Action<long>? CancelRequested;

    public TransferDock()
    {
        Dock = DockStyle.Bottom;
        Height = 230;
        _content = new Panel { Dock = DockStyle.Fill };

        _strip = new ToolStrip { Dock = DockStyle.Top, GripStyle = ToolStripGripStyle.Hidden };
        _strip.Items.Add(new ToolStripButton("⏸ 全部暂停", null, (_, _) => PauseAllRequested?.Invoke()) { ToolTipText = "暂停所有传输" });
        _strip.Items.Add(new ToolStripButton("▶ 全部继续", null, (_, _) => ResumeAllRequested?.Invoke()) { ToolTipText = "继续所有传输" });
        _strip.Items.Add(new ToolStripButton("↻ 重试失败", null, (_, _) => RetryAllFailedRequested?.Invoke()));
        _strip.Items.Add(new ToolStripButton("✕ 取消选中", null, (_, _) => OnCancelSelected()));
        _strip.Items.Add(new ToolStripButton("🧹 清除已完成", null, (_, _) => ClearFinishedRequested?.Invoke()));
        _strip.Items.Add(new ToolStripLabel(""));

        _grid = new DataGridView
        {
            Dock = DockStyle.Fill,
            AllowUserToAddRows = false,
            AllowUserToDeleteRows = false,
            ReadOnly = true,
            RowHeadersVisible = false,
            SelectionMode = DataGridViewSelectionMode.FullRowSelect,
            MultiSelect = false,
            AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
            ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.AutoSize,
            EnableHeadersVisualStyles = true,
            BackgroundColor = SystemColors.Window,
        };
        _grid.Columns.Add("name", "文件");
        _grid.Columns.Add("dir", "方向");
        _grid.Columns.Add("state", "状态");
        _grid.Columns.Add("progress", "进度");
        _grid.Columns.Add("speed", "速度");
        _grid.Columns.Add("eta", "剩余");
        _grid.Columns.Add("note", "备注");
        _grid.Columns["name"]!.FillWeight = 34;
        _grid.Columns["dir"]!.FillWeight = 8;
        _grid.Columns["state"]!.FillWeight = 12;
        _grid.Columns["progress"]!.FillWeight = 14;
        _grid.Columns["speed"]!.FillWeight = 10;
        _grid.Columns["eta"]!.FillWeight = 8;
        _grid.Columns["note"]!.FillWeight = 30;
        _grid.DoubleBuffered(true);

        var bottom = new Panel { Dock = DockStyle.Bottom, Height = 30 };
        _overall = new ProgressBar { Dock = DockStyle.Fill, Minimum = 0, Maximum = 1000 };
        _summary = new Label
        {
            Dock = DockStyle.Right,
            Width = 520,
            TextAlign = ContentAlignment.MiddleRight,
            ForeColor = Color.FromArgb(90, 90, 90),
        };
        bottom.Controls.Add(_overall);
        bottom.Controls.Add(_summary);

        _collapse = new Button
        {
            Dock = DockStyle.Top,
            Height = 24,
            FlatStyle = FlatStyle.Flat,
            Text = "传输队列 ▲",
        };
        _collapse.FlatAppearance.BorderSize = 0;
        _collapse.FlatAppearance.MouseOverBackColor = Color.FromArgb(235, 243, 254);
        _collapse.Click += (_, _) => ToggleCollapse();

        _content.Controls.Add(_grid);
        _content.Controls.Add(bottom);
        _content.Controls.Add(_strip);

        Controls.Add(_content);
        Controls.Add(_collapse);
    }

    private void OnCancelSelected()
    {
        if (_grid.CurrentRow?.Tag is long id) CancelRequested?.Invoke(id);
    }

    public void ToggleCollapse(bool expand = false)
    {
        if (expand && !_collapsed) return;
        _collapsed = expand ? false : !_collapsed;
        _content.Visible = !_collapsed;
        Height = _collapsed ? _collapse.Height : 230;
        _collapse.Text = _collapsed ? "传输队列 ▼" : "传输队列 ▲";
    }

    public int VisibleItemCount => _last?.Jobs.Count ?? 0;

    private static readonly Dictionary<JobState, string> StateText = new()
    {
        [JobState.Queued] = "排队中",
        [JobState.ResolvingConflict] = "等待冲突决策",
        [JobState.Transferring] = "传输中",
        [JobState.Verifying] = "校验中",
        [JobState.Paused] = "已暂停",
        [JobState.WaitingDevice] = "等待设备",
        [JobState.Done] = "完成",
        [JobState.Skipped] = "跳过",
        [JobState.Failed] = "失败",
        [JobState.Canceled] = "已取消",
    };

    public void ApplySnapshot(EngineSnapshot snap)
    {
        UpdateSummary(snap);
        // 万级作业保护：>300 行时不再全量重建表格（C2 改虚拟模式），只刷新摘要
        if (_collapsed || snap.Jobs.Count > 300) return;
        if (_last is not null && SameJobs(_last, snap)) return;
        _last = snap;
        int scroll = _grid.FirstDisplayedScrollingRowIndex;
        var selectedTag = _grid.CurrentRow?.Tag as long?;
        _grid.SuspendLayout();
        _grid.Rows.Clear();
        foreach (var j in snap.Jobs)
        {
            var stateColor = j.State switch
            {
                JobState.Failed => Color.Firebrick,
                JobState.Done => Color.SeaGreen,
                JobState.Skipped or JobState.Canceled => Color.Gray,
                JobState.WaitingDevice => Color.DarkOrange,
                _ => SystemColors.WindowText,
            };
            string note = j.Error ?? j.SkipReason ?? j.CompletionNote ?? (j.ResumedFromOffset > 0 ? $"断点续传自 {ThumbGrid.FmtBytes(j.ResumedFromOffset)}" : "");
            int row = _grid.Rows.Add(
                j.Name,
                j.Direction == TransferDirection.Download ? "相机→电脑" : "电脑→相机",
                StateText[j.State],
                j.State is JobState.Done or JobState.Skipped ? "100%" : $"{j.Progress * 100:F0}%",
                j.State is JobState.Transferring or JobState.Verifying ? FmtSpeed(j.SpeedBytesPerSec) : "",
                j.State is JobState.Transferring or JobState.Verifying && j.SpeedBytesPerSec > 0 ? FmtEta((long)((j.SizeBytes - j.BytesDone) / Math.Max(1, j.SpeedBytesPerSec))) : "",
                note);
            _grid.Rows[row].Tag = j.Id;
            _grid.Rows[row].Cells["state"]!.Style.ForeColor = stateColor;
        }
        _grid.ResumeLayout();
        if (scroll >= 0 && scroll < _grid.RowCount) _grid.FirstDisplayedScrollingRowIndex = scroll;
        if (selectedTag is { } tid)
            for (int i = 0; i < _grid.RowCount; i++)
                if ((long)_grid.Rows[i].Tag! == tid) { _grid.Rows[i].Selected = true; break; }
        UpdateSummary(snap);
    }

    private static bool SameJobs(EngineSnapshot a, EngineSnapshot b)
        => a.Jobs.Count == b.Jobs.Count && a.DoneBytes == b.DoneBytes && a.FinishedCount == b.FinishedCount;

    private void UpdateSummary(EngineSnapshot s)
    {
        var total = ThumbGrid.FmtBytes(s.TotalBytes);
        var done = ThumbGrid.FmtBytes(s.DoneBytes);
        _overall.Maximum = 1000;
        _overall.Value = s.TotalBytes <= 0 ? 0 : (int)Math.Clamp((double)s.DoneBytes / s.TotalBytes * 1000, 0, 1000);
        var parts = new List<string> { $"共 {s.Jobs.Count} 项" };
        if (s.ActiveCount > 0) parts.Add($"传输中 {s.ActiveCount}");
        if (s.PendingCount > 0) parts.Add($"排队 {s.PendingCount}");
        if (s.FailedCount > 0) parts.Add($"失败 {s.FailedCount}");
        parts.Add($"{done} / {total}");
        if (s.OverallSpeedBytesPerSec > 0 && !s.AllSettled)
            parts.Add($"{FmtSpeed(s.OverallSpeedBytesPerSec)}，剩余约 {FmtEta((long)((s.TotalBytes - s.DoneBytes) / s.OverallSpeedBytesPerSec))}");
        if (s.GlobalPaused) parts.Add("【已全局暂停】");
        _summary.Text = string.Join(" · ", parts);
    }

    internal static string FmtSpeed(double bps)
        => bps >= 1 << 20 ? $"{bps / 1024 / 1024:F1} MB/s" : $"{bps / 1024:F0} KB/s";

    internal static string FmtEta(long sec)
        => sec >= 3600 ? $"{sec / 3600}h{sec % 3600 / 60:00}m" : sec >= 60 ? $"{sec / 60}m{sec % 60:00}s" : $"{sec}s";
}

internal static class DgvExtensions
{
    public static void DoubleBuffered(this DataGridView dgv, bool setting)
    {
        var pi = typeof(DataGridView).GetProperty("DoubleBuffered", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        pi?.SetValue(dgv, setting, null);
    }
}
