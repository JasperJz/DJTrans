using DJTrans.Controls;
using DJTrans.Core.Scan;
using DJTrans.Core.Transfer;
using DJTrans.Dialogs;
using DJTrans.Thumbs;

namespace DJTrans;

public sealed class MainForm : Form
{
    private readonly AppSettings _settings;
    private readonly VolumeWatcher _watcher;
    private readonly TransferEngine _engine;
    private readonly ThumbnailService _thumbs;
    private readonly SemaphoreSlim _logLock = new(1, 1);
    private readonly ToolStrip _toolbar;
    private readonly ListBox _deviceList;
    private readonly StatusStrip _status;
    private readonly ToolStripStatusLabel _stDevice;
    private readonly ToolStripStatusLabel _stItems;
    private readonly ToolStripStatusLabel _stSel;
    private readonly ToolStripStatusLabel _stTransfer;

    private readonly List<VolumeInfo> _volumes = [];
    private VolumeInfo? _current;
    private MediaItem[] _allItems = [];
    private MediaItem[] _viewItems = [];
    private CancellationTokenSource? _scanCts;
    /// <summary>会话级冲突决策（-1 未定；0 Skip/1 Overwrite/2 RenameNew）。跨线程读写需原子性。</summary>
    private volatile int _conflictApplyAll = -1;
    private int _filterKind = 0;   // 0全部 1照片 2视频 3其他
    private int _sortMode = 0;     // 0时间新→旧 1时间旧→新 2大→小 3名称
    private readonly ThumbGrid _grid = new() { Dock = DockStyle.Fill };
    private readonly TransferDock _dock = new();

    public MainForm()
    {
        Text = $"DJTrans — DJI 相机素材传输 v{Version}";
        Font = new Font("Segoe UI", 9F);
        ClientSize = new Size(1280, 800);
        MinimumSize = new Size(960, 600);
        KeyPreview = true;

        _settings = AppSettings.Load();
        _watcher = new VolumeWatcher(postToUiContext: true);
        _engine = new TransferEngine(_settings.Workers, ResolveConflict);
        _thumbs = new ThumbnailService(SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext());
        _engine.Logged += m =>
        {
            System.Diagnostics.Debug.WriteLine(m);
            try
            {
                _logLock.Wait();
                var logPath = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DJTrans", "engine.log");
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(logPath)!);
                File.AppendAllText(logPath, m + Environment.NewLine);
            }
            catch { }
            finally { _logLock.Release(); }
        };

        // ---------- 工具栏 ----------
        _toolbar = new ToolStrip { GripStyle = ToolStripGripStyle.Hidden, RenderMode = ToolStripRenderMode.System };
        _toolbar.Items.Add(new ToolStripButton("⟳ 重新扫描", null, (_, _) => RescanCurrent()) { ToolTipText = "重新扫描当前设备" });
        _toolbar.Items.Add(new ToolStripSeparator());
        var filter = new ToolStripComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 90 };
        filter.Items.AddRange(["全部类型", "仅照片", "仅视频", "其他"]);
        filter.SelectedIndex = 0;
        filter.SelectedIndexChanged += (_, _) => { _filterKind = filter.SelectedIndex; ApplyView(); };
        _toolbar.Items.Add(filter);
        var sort = new ToolStripComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 110 };
        sort.Items.AddRange(["最新优先", "最早优先", "最大优先", "按名称"]);
        sort.SelectedIndex = 0;
        sort.SelectedIndexChanged += (_, _) => { _sortMode = sort.SelectedIndex; ApplyView(); };
        _toolbar.Items.Add(sort);
        var proxyBox = new ToolStripCheckBox("包含代理/字幕(.lrf/.srt)");
        proxyBox.Checked = _settings.IncludeProxy;
        proxyBox.CheckedChanged += (_, _) => { _settings.IncludeProxy = proxyBox.Checked; ApplyView(); };
        _toolbar.Items.Add(proxyBox);
        _toolbar.Items.Add(new ToolStripSeparator());
        _toolbar.Items.Add(new ToolStripButton("🔍-", null, (_, _) => _grid.Zoom(-24)));
        _toolbar.Items.Add(new ToolStripButton("🔍+", null, (_, _) => _grid.Zoom(24)));
        _toolbar.Items.Add(new ToolStripSeparator());
        _toolbar.Items.Add(new ToolStripButton("☑ 全选", null, (_, _) => _grid.SelectAll()) { ToolTipText = "Ctrl+A" });
        _toolbar.Items.Add(new ToolStripButton("🔄 反选", null, (_, _) => _grid.InvertSelection()) { ToolTipText = "Ctrl+I" });
        _toolbar.Items.Add(new ToolStripSeparator());
        _toolbar.Items.Add(new ToolStripButton("⬇ 导出所选…", null, (_, _) => ExportSelected()) { ToolTipText = "导出到电脑（相机 → PC）" });
        _toolbar.Items.Add(new ToolStripButton("⬆ 上传到设备…", null, (_, _) => UploadToDevice()));
        _toolbar.Items.Add(new ToolStripButton("🗑 删除设备文件…", null, (_, _) => DeleteSelected()));
        _toolbar.Items.Add(new ToolStripButton("⏏ 卸载卷", null, (_, _) => EjectCurrent()));

        // ---------- 设备列表 ----------
        var left = new Panel { Dock = DockStyle.Left, Width = 220 };
        var lbl = new Label { Text = "设备", Dock = DockStyle.Top, Height = 26, TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(8, 0, 0, 0), BackColor = Color.FromArgb(240, 240, 240) };
        _deviceList = new ListBox { Dock = DockStyle.Fill, DrawMode = DrawMode.OwnerDrawFixed, ItemHeight = 44, IntegralHeight = false, BorderStyle = BorderStyle.FixedSingle };
        _deviceList.DrawItem += (_, e) =>
        {
            if (e.Index < 0 || e.Index >= _volumes.Count) return;
            var v = _volumes[e.Index];
            e.DrawBackground();
            var sel = (e.State & DrawItemState.Selected) != 0;
            using var nameBrush = new SolidBrush(sel ? Color.White : Color.FromArgb(40, 40, 40));
            using var subBrush = new SolidBrush(sel ? Color.White : Color.Gray);
            e.Graphics.DrawString(v.DisplayName, new Font(Font, FontStyle.Bold), nameBrush, e.Bounds.X + 8, e.Bounds.Y + 5);
            var pct = v.TotalBytes <= 0 ? 0 : (int)((v.TotalBytes - v.FreeBytes) * 100 / v.TotalBytes);
            e.Graphics.DrawString($"{ThumbGrid.FmtBytes(v.FreeBytes)} 可用 · 已用 {pct}%", new Font(Font.FontFamily, 8.25f), subBrush, e.Bounds.X + 8, e.Bounds.Y + 23);
            e.DrawFocusRectangle();
        };
        _deviceList.SelectedIndexChanged += (_, _) =>
        {
            if (_deviceList.SelectedIndex >= 0 && _deviceList.SelectedIndex < _volumes.Count)
                SelectDevice(_volumes[_deviceList.SelectedIndex]);
        };
        left.Controls.Add(_deviceList);
        left.Controls.Add(lbl);

        // ---------- 网格 / Dock / 状态 ----------
        _grid.SelectionChanged += UpdateStatus;
        _grid.VisibleRangeChanged += () => _grid.RequestVisibleThumbnails(_thumbs);
        // 双击/回车：用系统默认应用打开（照片→看图工具，视频→默认播放器）
        _grid.ItemActivated += item =>
        {
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(item.Path) { UseShellExecute = true }); }
            catch (Exception ex)
            {
                MessageBox.Show(this, "无法打开文件：" + ex.Message, "DJTrans", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        };

        _dock.PauseAllRequested += () => _engine.PauseAll();
        _dock.ResumeAllRequested += () => _engine.ResumeAll();
        _dock.RetryAllFailedRequested += () => { _conflictApplyAll = -1; _engine.RetryAllFailed(); };
        _dock.ClearFinishedRequested += () => _engine.RemoveFinished();
        _dock.CancelRequested += id => _engine.CancelJob(id);
        _dock.ToggleCollapse();

        _status = new StatusStrip();
        _stDevice = new ToolStripStatusLabel("未检测到设备");
        _stItems = new ToolStripStatusLabel("");
        _stSel = new ToolStripStatusLabel("");
        _stTransfer = new ToolStripStatusLabel("") { Spring = true, TextAlign = ContentAlignment.MiddleRight };
        _status.Items.AddRange([_stDevice, new ToolStripStatusLabel("|"), _stItems, new ToolStripStatusLabel("|"), _stSel, _stTransfer]);

        Controls.Add(_grid);
        Controls.Add(left);
        Controls.Add(_dock);
        Controls.Add(_status);
        Controls.Add(_toolbar);

        // ---------- 事件接线 ----------
        _watcher.VolumesChanged += OnVolumesChanged;
        _engine.SnapshotChanged += snap =>
        {
            if (_uiGone) return;
            try { BeginInvoke(() => { _dock.ApplySnapshot(snap); UpdateStatus(); }); }
            catch (InvalidOperationException) { _uiGone = true; } // 窗口已销毁的退出竞态
        };
        _thumbs.ThumbReady += (key, img) => { if (!_uiGone) _grid.SetThumb(key, img); };

        Load += (_, _) =>
        {
            EnableDarkTitle();
            _watcher.Start();
        };
        FormClosing += (_, _) => _settings.Save();
        FormClosed += (_, _) =>
        {
            _uiGone = true;
            _engine.Dispose();   // 先停引擎（工作者不再产生事件）
            _thumbs.Dispose();
            _watcher.Dispose();
        };
    }

    private volatile bool _uiGone;

    protected override void WndProc(ref Message m)
    {
        const int WM_DEVICECHANGE = 0x0219;
        if (m.Msg == WM_DEVICECHANGE) _watcher.NotifyDeviceChange();
        base.WndProc(ref m);
    }

    private static string Version => typeof(MainForm).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

    private void EnableDarkTitle()
    {
        try
        {
            int on = 1;
            DwmSetWindowAttribute(Handle, 20 /*DWMWA_USE_IMMERSIVE_DARK_MODE*/, ref on, sizeof(int));
        }
        catch { /* 老系统无此属性 */ }
    }

    [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int val, int size);

    // ---------- 设备 ----------

    private void OnVolumesChanged(object? sender, VolumeChangedEventArgs e)
    {
        _volumes.Clear();
        _volumes.AddRange(e.Current);
        _deviceList.Items.Clear();
        foreach (var v in _volumes) _deviceList.Items.Add(v.DisplayName);
        _engine.CheckWaitingJobs();

        var cur = _current;
        if (cur is not null && e.Removed.Any(r => r.Letter == cur.Letter))
        {
            _current = null;
            _allItems = [];
            ApplyView();
            _stDevice.Text = $"设备 {cur.DisplayName} 已移除";
        }
        if (_current is null && e.Current.Count > 0)
        {
            var pick = e.Current.FirstOrDefault(v => v.IsDjiLikely) ?? e.Current[0];
            _deviceList.SelectedIndex = _volumes.IndexOf(pick);
            SelectDevice(pick);
        }
        else if (_current is not null && _volumes.Contains(_current))
        {
            _deviceList.SelectedIndex = _volumes.IndexOf(_current);
        }
    }

    private void SelectDevice(VolumeInfo v)
    {
        if (_current?.Letter == v.Letter) return;
        _current = v;
        _stDevice.Text = $"设备：{v.DisplayName}（{ThumbGrid.FmtBytes(v.FreeBytes)} 可用）";
        RescanCurrent();
    }

    private void RescanCurrent()
    {
        if (_current is null) return;
        _scanCts?.Cancel();
        _scanCts?.Dispose();
        _scanCts = new CancellationTokenSource();
        var vol = _current;
        var ct = _scanCts.Token;
        _stItems.Text = "扫描中…";
        Task.Run(() =>
        {
            try
            {
                var result = MediaScanner.Scan(vol.Letter, vol.VolumeKey, ct);
                BeginInvoke(() =>
                {
                    if (_current?.Letter != vol.Letter) return;
                    _allItems = result.Items.ToArray();
                    ApplyView();
                    _stItems.Text = $"扫描完成：{_allItems.Length} 项（{result.Elapsed.TotalSeconds:F1}s）";
                });
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                BeginInvoke(() => { _stItems.Text = $"扫描失败：{ex.Message}"; });
            }
        }, ct);
    }

    // ---------- 视图 ----------

    private void ApplyView()
    {
        IEnumerable<MediaItem> q = _allItems;
        if (!_settings.IncludeProxy)
            q = q.Where(i => i.Kind is not (MediaKind.Proxy or MediaKind.Subtitle));
        q = _filterKind switch
        {
            1 => q.Where(i => i.Kind == MediaKind.Photo),
            2 => q.Where(i => i.Kind == MediaKind.Video),
            3 => q.Where(i => i.Kind is MediaKind.Proxy or MediaKind.Subtitle or MediaKind.Other),
            _ => q,
        };
        _viewItems = _sortMode switch
        {
            1 => q.OrderBy(i => i.SortTimeLocal).ToArray(),
            2 => q.OrderByDescending(i => i.SizeBytes).ToArray(),
            3 => q.OrderBy(i => i.Name, StringComparer.OrdinalIgnoreCase).ToArray(),
            _ => q.OrderByDescending(i => i.SortTimeLocal).ToArray(),
        };
        _grid.SetItems(_viewItems);
        UpdateStatus();
    }

    private void UpdateStatus()
    {
        _stItems.Text = $"{_viewItems.Length} 项媒体";
        _stSel.Text = _grid.SelectionCount > 0
            ? $"已选 {_grid.SelectionCount} 项（{ThumbGrid.FmtBytes(_grid.SelectionBytes)}）"
            : "未选择";
        var s = _engine.Snapshot();
        if (s.Jobs.Count > 0)
        {
            _stTransfer.Text = $"传输：{s.Jobs.Count} 项 · " +
                (s.GlobalPaused ? "已暂停" : s.AllSettled ? "全部结束" : TransferDock.FmtSpeed(s.OverallSpeedBytesPerSec));
        }
        else _stTransfer.Text = "";
    }

    // ---------- 导出 ----------

    private void ExportSelected()
    {
        var selected = _grid.SelectedItems;
        if (selected.Count == 0)
        {
            MessageBox.Show(this, "请先选择要导出的文件（点击缩略图，Ctrl+点击多选，或全选/反选）", "DJTrans", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        if (_current is null) return;
        using var dlg = new ExportDialog(selected, _settings.LastExportDir);
        dlg.Preset(_settings);
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        _settings.LastExportDir = dlg.DestDir;
        _settings.Layout = dlg.DestLayout;
        _settings.Conflict = dlg.Conflict;
        _settings.Verify = dlg.Verify;
        _settings.StrictSkipVerification = dlg.StrictSkipVerification;
        _settings.Save();

        var volume = _current;
        var reqs = selected.Select(it => new TransferRequest
        {
            SourcePath = it.Path,
            DestPath = DestPathFor(dlg.DestDir, dlg.DestLayout, it),
            SizeBytes = it.SizeBytes,
            SourceMtimeUtc = it.ModifiedUtc,
            DevicePath = it.Path,
            VolumeKey = volume.VolumeKey,
            Direction = TransferDirection.Download,
        }).ToList();
        _conflictApplyAll = -1;
        _engine.EnqueueBatch(reqs, new TransferOptions
        {
            ConflictPolicy = dlg.Conflict,
            Verify = dlg.Verify ? VerifyMode.BlockHash : VerifyMode.SizeOnly,
            StrictSkipVerification = dlg.StrictSkipVerification,
        });
        _dock.ToggleCollapse(true);
    }

    internal static string DestPathFor(string root, LayoutMode layout, MediaItem it) => layout switch
    {
        LayoutMode.ByDate => Path.Combine(root, it.SortTimeLocal.ToString("yyyy-MM-dd"), it.Name),
        LayoutMode.Flat => Path.Combine(root, it.Name),
        _ => Path.Combine(root, it.RelativeDir, it.Name),
    };

    /// <summary>工作者线程回调：封送到 UI 弹冲突对话框并阻塞等待决策（带超时兜底）。</summary>
    private ConflictDecision ResolveConflict(TransferEngine.TransferJob job)
    {
        int all = _conflictApplyAll;
        if (all >= 0) return (ConflictDecision)all;
        var tcs = new TaskCompletionSource<ConflictDecision>(TaskCreationOptions.RunContinuationsAsynchronously);
        BeginInvoke(() =>
        {
            using var dlg = new ConflictDialog(job);
            if (dlg.ShowDialog(this) == DialogResult.OK)
            {
                if (dlg.ApplyToAll) _conflictApplyAll = (int)dlg.Decision;
                tcs.TrySetResult(dlg.Decision);
            }
            else tcs.TrySetResult(ConflictDecision.Skip);
        });
        return tcs.Task.Wait(TimeSpan.FromMinutes(10)) ? tcs.Task.Result : ConflictDecision.Skip;
    }

    // ---------- 上传 / 删除 / 弹出 ----------

    private void UploadToDevice()
    {
        var vol = _current;
        if (vol is null)
        {
            MessageBox.Show(this, "请先选择设备", "DJTrans", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        using var ofd = new OpenFileDialog { Multiselect = true, Title = "选择要上传到设备的文件" };
        if (ofd.ShowDialog(this) != DialogResult.OK) return;
        var destDir = Path.Combine(MediaScanner.ResolveScanRoot(vol.Letter), "DJTRANS_IN");
        var reqs = ofd.FileNames.Select(f =>
        {
            var fi = new FileInfo(f);
            var dst = Path.Combine(destDir, fi.Name);
            return new TransferRequest
            {
                SourcePath = f,
                DestPath = dst,
                SizeBytes = fi.Length,
                SourceMtimeUtc = fi.LastWriteTimeUtc,
                DevicePath = dst,
                VolumeKey = vol.VolumeKey,
                Direction = TransferDirection.Upload,
            };
        }).ToList();
        _conflictApplyAll = -1;
        _engine.EnqueueBatch(reqs, new TransferOptions
        {
            ConflictPolicy = ConflictPolicy.SmartSkip,
            Verify = VerifyMode.BlockHash,
        });
        _dock.ToggleCollapse(true);
    }

    private void DeleteSelected()
    {
        var selected = _grid.SelectedItems;
        if (selected.Count == 0)
        {
            MessageBox.Show(this, "请先选择要删除的文件", "DJTrans", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        long bytes = selected.Sum(i => i.SizeBytes);
        var msg = $"即将从设备永久删除 {selected.Count} 个文件（共 {ThumbGrid.FmtBytes(bytes)}）。\n\n" +
                  "⚠ 可移动设备没有回收站，删除后无法恢复。\n⚠ 强烈建议先导出再删除。\n\n确定继续？";
        var r = MessageBox.Show(this, msg, "删除确认", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);
        if (r != DialogResult.Yes) return;
        int ok = 0, fail = 0;
        foreach (var it in selected)
        {
            try
            {
                File.Delete(it.Path);
                _thumbs.Invalidate(it);
                ok++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                fail++;
            }
        }
        if (fail > 0)
            MessageBox.Show(this, $"{ok} 个已删除，{fail} 个删除失败（文件可能被占用）", "DJTrans", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        RescanCurrent();
    }

    private void EjectCurrent()
    {
        if (_current is null) return;
        var s = _engine.Snapshot();
        if (s.Jobs.Count > 0 && !s.AllSettled)
        {
            MessageBox.Show(this, "还有传输未完成，等全部结束后再弹出。", "DJTrans", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        var vol = _current;
        var err = VolumeEjector.TryDismount(vol.Letter);
        if (err is null)
        {
            _stDevice.Text = $"已卸载 {vol.DisplayName}；请通过 Windows 安全移除硬件";
            MessageBox.Show(this, $"已卸载 {vol.DisplayName}。请继续通过 Windows 任务栏的“安全移除硬件”弹出设备，成功后再拔线。", "DJTrans", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        else
        {
            MessageBox.Show(this, $"弹出失败：{err}", "DJTrans", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }
}
