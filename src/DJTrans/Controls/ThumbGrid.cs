using System.Drawing.Drawing2D;
using DJTrans.Core.Scan;
using DJTrans.Thumbs;

namespace DJTrans.Controls;

/// <summary>
/// 自绘虚拟缩略图网格：只绘制可视行，支持海量条目。
/// 选择状态自管（位集），支持 单击/Ctrl/Shift/框选/Ctrl+A 全选/Ctrl+I 反选。
/// </summary>
public sealed class ThumbGrid : Control
{
    private MediaItem[] _items = [];
    private readonly Dictionary<string, int> _keyToIndex = new(StringComparer.Ordinal);
    private Image?[] _thumbs = [];
    private readonly Queue<int> _loadOrder = new();
    private int _loadedCount;
    private bool[] _sel = [];
    private int _selCount;
    private long _selBytes;
    private int _hover = -1;
    private int _anchor = -1;
    private bool _rubberActive;
    private Point _rubberStart;
    private Rectangle _rubberRect;
    private Rectangle _lastRubberScreen;
    private readonly ToolTip _tip = new() { AutomaticDelay = 400 };

    private int _tileW = 132, _tileH = 132;   // 缩略图区域
    private const int LabelH = 36;
    private const int Pad = 10;
    private VScrollBar _vbar = null!;
    private int _scrollPos;
    // 绘制用字体缓存（避免每格每帧分配 GDI Font）
    private Font _fBadge = new(FontFamily.GenericSansSerif, 7.5f, FontStyle.Bold);
    private Font _fText = new(FontFamily.GenericSansSerif, 8.25f);
    private Font _fEmpty = new(FontFamily.GenericSansSerif, 11f);

    private static readonly Color Accent = Color.FromArgb(0, 120, 215);
    private static readonly Color BackColorLight = Color.FromArgb(248, 249, 250);
    private static readonly Color TileBack = Color.White;
    private static readonly Color LabelColor = Color.FromArgb(60, 60, 60);

    /// <summary>选择变化（UI 线程）。</summary>
    public event Action? SelectionChanged;
    /// <summary>双击/回车激活条目。</summary>
    public event Action<MediaItem>? ItemActivated;
    /// <summary>可视范围变化（滚动/缩放/数据变化），主窗体据此请求缩略图。</summary>
    public event Action? VisibleRangeChanged;

    public ThumbGrid()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                 | ControlStyles.UserPaint | ControlStyles.ResizeRedraw | ControlStyles.UserMouse, true);
        Cursor = Cursors.Hand;
        _vbar = new VScrollBar { Dock = DockStyle.Right, SmallChange = 1 };
        _vbar.Scroll += (_, _) => { _scrollPos = _vbar.Value; Invalidate(); VisibleRangeChanged?.Invoke(); };
        Controls.Add(_vbar);
    }

    // ---------- 数据 ----------

    public MediaItem[] Items => _items;
    public int SelectionCount => _selCount;
    public long SelectionBytes => _selBytes;
    public IReadOnlyList<MediaItem> SelectedItems => _items.Where((_, i) => _sel[i]).ToList();

    public void SetItems(MediaItem[] items)
    {
        foreach (var t in _thumbs) t?.Dispose();
        _items = items;
        _thumbs = new Image?[items.Length];
        _loadOrder.Clear();
        _loadedCount = 0;
        _keyToIndex.Clear();
        for (int i = 0; i < items.Length; i++) _keyToIndex[ThumbnailService.KeyFor(items[i])] = i;
        _sel = new bool[items.Length];
        _selCount = 0; _selBytes = 0;
        _hover = -1; _anchor = -1;
        _scrollPos = 0; _vbar.Value = 0;
        UpdateScroll();
        Invalidate();
        SelectionChanged?.Invoke();
        VisibleRangeChanged?.Invoke();
    }

    public void SetThumb(string key, Image? img)
    {
        if (!_keyToIndex.TryGetValue(key, out var idx) || (uint)idx >= (uint)_thumbs.Length) return;
        var old = _thumbs[idx];
        _thumbs[idx] = img;
        if (old is null && img is not null)
        {
            _loadedCount++;
            _loadOrder.Enqueue(idx);
        }
        else if (old is not null && img is null)
        {
            _loadedCount--;
        }
        old?.Dispose();
        if (img is not null) EvictBeyondCap();
        InvalidateTile(idx);
    }

    /// <summary>网格侧缩略图上限：可视区+预读的 3 倍且 ≥600。超出按加载序淘汰（可视区豁免），
    /// 防 10k+ 大卡滚动耗尽内存/GDI。</summary>
    private void EvictBeyondCap()
    {
        var (vf, vl) = VisibleRange();
        int cap = Math.Max(600, (vl - vf + 1 + Columns * 2) * 3);
        int guard = 0;
        while (_loadedCount > cap && _loadOrder.Count > 0 && guard++ < 10000)
        {
            var idx = _loadOrder.Dequeue();
            if (idx >= _thumbs.Length || _thumbs[idx] is null) continue;
            if (idx >= vf && idx <= vl + Columns * 2)
            {
                _loadOrder.Enqueue(idx); // 可视区/预读豁免，重新排队
                continue;
            }
            _thumbs[idx]!.Dispose();
            _thumbs[idx] = null;
            _loadedCount--;
            InvalidateTile(idx);
        }
    }

    private void InvalidateTile(int idx)
    {
        var r = TileRect(idx);
        if (r.HasValue) Invalidate(r.Value);
    }

    // ---------- 布局 ----------

    private int CellW => _tileW + Pad * 2;
    private int CellH => _tileH + LabelH + Pad * 2;
    private int Columns => Math.Max(1, (ClientWidth) / CellW);
    private int ClientWidth => ClientSize.Width - (_vbar.Visible ? _vbar.Width : 0);
    private int Rows => (_items.Length + Columns - 1) / Columns;

    private void UpdateScroll()
    {
        var contentH = Rows * CellH;
        var visibleH = Math.Max(1, ClientSize.Height);
        var max = Math.Max(0, contentH - visibleH);
        _vbar.Visible = max > 0;
        _vbar.Minimum = 0;
        _vbar.Maximum = Math.Max(1, contentH);
        _vbar.LargeChange = Math.Max(1, visibleH);
        _vbar.SmallChange = Math.Max(16, CellH / 4);
        if (_scrollPos > max) _scrollPos = max;
        if (_vbar.Value != _scrollPos) _vbar.Value = _scrollPos;
    }

    private (int first, int last) VisibleRange()
    {
        int firstRow = Math.Max(0, _scrollPos / CellH);
        int lastRow = Math.Min(Rows - 1, (_scrollPos + ClientSize.Height) / CellH);
        int first = Math.Max(0, firstRow * Columns);
        int last = Math.Min(_items.Length - 1, (lastRow + 1) * Columns - 1);
        return first <= last ? (first, last) : (0, -1);
    }

    private Rectangle? TileRect(int idx)
    {
        if (_items.Length == 0) return null;
        int col = idx % Columns;
        int row = idx / Columns;
        var used = Columns * CellW;
        var startX = Math.Max(0, (ClientWidth - used) / 2);
        var x = startX + col * CellW + Pad;
        var y = row * CellH - _scrollPos + Pad;
        return new Rectangle(x, y, _tileW, _tileH);
    }

    private int HitTest(Point p)
    {
        for (int i = VisibleRange().first; i <= VisibleRange().last; i++)
        {
            var cell = CellRect(i);
            if (cell.Contains(p)) return i;
        }
        return -1;
    }

    private Rectangle CellRect(int idx)
    {
        var r = TileRect(idx) ?? Rectangle.Empty;
        r.Inflate(Pad, Pad);
        r.Height += LabelH;
        return r;
    }

    // ---------- 绘制 ----------

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(BackColorLight);
        g.InterpolationMode = InterpolationMode.HighQualityBilinear;
        var (first, last) = VisibleRange();
        for (int i = first; i <= last; i++)
        {
            var item = _items[i];
            var tile = TileRect(i)!.Value;
            var cell = CellRect(i);

            // 背景
            using (var bg = new SolidBrush(_sel[i] ? Color.FromArgb(217, 235, 253) : TileBack))
                g.FillRectangle(bg, cell);
            if (_sel[i])
                using (var pen = new Pen(Accent, 2))
                    g.DrawRectangle(pen, cell.X + 1, cell.Y + 1, cell.Width - 2, cell.Height - 2);

            // 缩略图
            var img = _thumbs[i];
            if (img is not null)
            {
                DrawFitted(g, img, tile);
            }
            else
            {
                DrawPlaceholder(g, tile, item);
            }

            // 类型角标（视频/代理）
            var badge = item.Kind switch
            {
                MediaKind.Video => "MP4",
                MediaKind.Proxy => "LRF",
                MediaKind.Subtitle => "SRT",
                MediaKind.Other => Path.GetExtension(item.Name).TrimStart('.').ToUpperInvariant(),
                _ => null,
            };
            if (badge is not null)
            {
                var f = _fBadge;
                var sz = g.MeasureString(badge, f);
                var br = new Rectangle(tile.Right - (int)sz.Width - 8, tile.Bottom - (int)sz.Height - 8, (int)sz.Width + 8, (int)sz.Height + 4);
                using (var b = new SolidBrush(Color.FromArgb(170, 0, 0, 0))) g.FillRectangle(b, br);
                using var fg = new SolidBrush(Color.White);
                g.DrawString(badge, f, fg, br.X + 4, br.Y + 1);
            }
            if (item.Kind == MediaKind.Video)
            {
                DrawPlayGlyph(g, tile);
            }

            // 文件名（两行截断）+ 大小
            var labelRect = new Rectangle(cell.X + Pad, tile.Bottom + 2, cell.Width - Pad * 2, LabelH);
            using (var nameBrush = new SolidBrush(LabelColor))
            {
                var name = item.Name;
                var line1 = name;
                string? line2 = null;
                if (name.Length > 18)
                {
                    line1 = name[..16] + "…";
                }
                var f = _fText;
                g.DrawString(line1, f, nameBrush, labelRect.X, labelRect.Y);
                var meta = $"{FmtBytes(item.SizeBytes)} · {item.SortTimeLocal:MM-dd HH:mm}";
                using var metaBrush = new SolidBrush(Color.FromArgb(130, 130, 130));
                g.DrawString(meta, f, metaBrush, labelRect.X, labelRect.Y + 15);
                _ = line2;
            }
        }

        // 空态
        if (_items.Length == 0)
        {
            var f = _fEmpty;
            var txt = "没有可显示的媒体文件";
            var sz = g.MeasureString(txt, f);
            g.DrawString(txt, f, Brushes.Gray, (ClientWidth - sz.Width) / 2, (ClientSize.Height - sz.Height) / 2);
        }
    }

    private static void DrawFitted(Graphics g, Image img, Rectangle tile)
    {
        double scale = Math.Min((double)tile.Width / img.Width, (double)tile.Height / img.Height);
        var w = (int)(img.Width * scale);
        var h = (int)(img.Height * scale);
        var x = tile.X + (tile.Width - w) / 2;
        var y = tile.Y + (tile.Height - h) / 2;
        g.DrawImage(img, new Rectangle(x, y, w, h));
    }

    private void DrawPlaceholder(Graphics g, Rectangle tile, MediaItem item)
    {
        using var b = new SolidBrush(Color.FromArgb(233, 236, 239));
        g.FillRectangle(b, tile);
        var ext = Path.GetExtension(item.Name).TrimStart('.').ToUpperInvariant();
        var kind = item.Kind switch
        {
            MediaKind.Photo => "📷",
            MediaKind.Video => "🎬",
            _ => "📄",
        };
        using var f = _fText;
        var txt = $"{kind} {ext}";
        var sz = g.MeasureString(txt, f);
        g.DrawString(txt, f, Brushes.Gray, tile.X + (tile.Width - sz.Width) / 2, tile.Y + (tile.Height - sz.Height) / 2);
    }

    private static void DrawPlayGlyph(Graphics g, Rectangle tile)
    {
        var cx = tile.X + tile.Width / 2;
        var cy = tile.Y + tile.Height / 2;
        var pts = new[]
        {
            new Point(cx - 7, cy - 11), new Point(cx + 12, cy), new Point(cx - 7, cy + 11),
        };
        using var b = new SolidBrush(Color.FromArgb(110, 0, 0, 0));
        g.FillPolygon(b, pts);
    }

    internal static string FmtBytes(long b) => b >= 1L << 30 ? $"{b / 1024.0 / 1024 / 1024:F1}GB" : b >= 1 << 20 ? $"{b / 1024.0 / 1024:F0}MB" : $"{b / 1024.0:F0}KB";

    // ---------- 输入 ----------

    protected override void OnMouseDown(MouseEventArgs e)
    {
        Focus();
        var idx = HitTest(e.Location);
        if (e.Button == MouseButtons.Left)
        {
            if (idx >= 0)
            {
                if ((ModifierKeys & Keys.Control) != 0)
                {
                    ToggleSelect(idx);
                    _anchor = idx;
                }
                else if ((ModifierKeys & Keys.Shift) != 0 && _anchor >= 0)
                {
                    SelectRange(Math.Min(_anchor, idx), Math.Max(_anchor, idx), exclusive: true);
                }
                else
                {
                    SelectExclusive(idx);
                    _anchor = idx;
                }
            }
            else
            {
                // 空白处按下：开始框选
                _rubberActive = true;
                _rubberStart = e.Location;
                _rubberRect = new Rectangle(e.Location, Size.Empty);
                if ((ModifierKeys & Keys.Control) == 0) ClearSelection();
            }
        }
        base.OnMouseDown(e);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        var idx = HitTest(e.Location);
        if (idx != _hover)
        {
            _hover = idx;
            Invalidate();
            if (idx >= 0)
            {
                var it = _items[idx];
                _tip.SetToolTip(this, $"{it.Name}\n{FmtBytes(it.SizeBytes)} · {it.SortTimeLocal:yyyy-MM-dd HH:mm:ss}\n{it.RelativeDir}");
            }
            else _tip.SetToolTip(this, null);
        }
        if (_rubberActive)
        {
            if (_lastRubberScreen != Rectangle.Empty)
                ControlPaint.DrawReversibleFrame(_lastRubberScreen, Color.DimGray, FrameStyle.Dashed); // 擦除上一帧
            _rubberRect = Rectangle.FromLTRB(
                Math.Min(_rubberStart.X, e.X), Math.Min(_rubberStart.Y, e.Y),
                Math.Max(_rubberStart.X, e.X), Math.Max(_rubberStart.Y, e.Y));
            if (_rubberRect.Width > 4 || _rubberRect.Height > 4)
            {
                _lastRubberScreen = RectangleToScreen(_rubberRect);
                ControlPaint.DrawReversibleFrame(_lastRubberScreen, Color.DimGray, FrameStyle.Dashed);
            }
        }
        base.OnMouseMove(e);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        if (_rubberActive)
        {
            _rubberActive = false;
            if (_lastRubberScreen != Rectangle.Empty)
                ControlPaint.DrawReversibleFrame(_lastRubberScreen, Color.DimGray, FrameStyle.Dashed);
            _lastRubberScreen = Rectangle.Empty;
            if (_rubberRect.Width > 4 || _rubberRect.Height > 4)
            {
                var additive = (ModifierKeys & Keys.Control) != 0;
                if (!additive) ClearSelection();
                var (first, last) = VisibleRange();
                for (int i = first; i <= last; i++)
                {
                    var cell = CellRect(i);
                    if (cell.IntersectsWith(_rubberRect)) SetSelected(i, true);
                }
                SelectionChanged?.Invoke();
            }
            Invalidate();
        }
        base.OnMouseUp(e);
    }

    protected override void OnMouseDoubleClick(MouseEventArgs e)
    {
        var idx = HitTest(e.Location);
        if (idx >= 0 && idx < _items.Length) ItemActivated?.Invoke(_items[idx]);
        base.OnMouseDoubleClick(e);
    }

    protected override bool IsInputKey(Keys keyData) => keyData switch
    {
        Keys.Up or Keys.Down or Keys.Left or Keys.Right or Keys.Home or Keys.End
        or Keys.PageUp or Keys.PageDown => true,
        _ => base.IsInputKey(keyData),
    };

    protected override void OnKeyDown(KeyEventArgs e)
    {
        switch (e.KeyCode)
        {
            case Keys.A when e.Control:
                SelectAll(); e.Handled = true; break;
            case Keys.I when e.Control:
                InvertSelection(); e.Handled = true; break;
            case Keys.Enter when _hover >= 0 && _hover < _items.Length:
                ItemActivated?.Invoke(_items[_hover]); e.Handled = true; break;
            case Keys.Down: ScrollBy(CellH); e.Handled = true; break;
            case Keys.Up: ScrollBy(-CellH); e.Handled = true; break;
            case Keys.PageDown: ScrollBy(ClientSize.Height); e.Handled = true; break;
            case Keys.PageUp: ScrollBy(-ClientSize.Height); e.Handled = true; break;
            case Keys.Home: ScrollTo(0); e.Handled = true; break;
            case Keys.End: ScrollTo(int.MaxValue); e.Handled = true; break;
        }
        base.OnKeyDown(e);
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        ScrollBy(-e.Delta / 120 * CellH);
        base.OnMouseWheel(e);
    }

    private void ScrollBy(int dy)
    {
        ScrollTo(_scrollPos + dy);
    }

    private void ScrollTo(int pos)
    {
        var max = Math.Max(0, Rows * CellH - ClientSize.Height);
        _scrollPos = Math.Clamp(pos, 0, max);
        if (_vbar.Value != _scrollPos) _vbar.Value = _scrollPos;
        Invalidate();
        VisibleRangeChanged?.Invoke();
    }

    protected override void OnResize(EventArgs e)
    {
        UpdateScroll();
        Invalidate();
        VisibleRangeChanged?.Invoke();
        base.OnResize(e);
    }

    protected override void OnHandleDestroyed(EventArgs e)
    {
        foreach (var t in _thumbs) t?.Dispose();
        base.OnHandleDestroyed(e);
    }

    // ---------- 选择 ----------

    public void SelectAll()
    {
        Array.Fill(_sel, true);
        Recount();
        Invalidate();
        SelectionChanged?.Invoke();
    }

    public void ClearSelection()
    {
        Array.Fill(_sel, false);
        Recount();
        Invalidate();
        SelectionChanged?.Invoke();
    }

    /// <summary>反选当前视图（AC-05）。</summary>
    public void InvertSelection()
    {
        for (int i = 0; i < _sel.Length; i++) _sel[i] = !_sel[i];
        Recount();
        Invalidate();
        SelectionChanged?.Invoke();
    }

    private void ToggleSelect(int idx)
    {
        SetSelected(idx, !_sel[idx]);
        SelectionChanged?.Invoke();
    }

    private void SelectExclusive(int idx)
    {
        Array.Fill(_sel, false);
        SetSelected(idx, true);
        SelectionChanged?.Invoke();
    }

    private void SelectRange(int from, int to, bool exclusive)
    {
        if (exclusive) Array.Fill(_sel, false);
        for (int i = from; i <= to; i++) SetSelected(i, true);
        SelectionChanged?.Invoke();
    }

    private void SetSelected(int idx, bool on)
    {
        if (idx < 0 || idx >= _sel.Length || _sel[idx] == on) return;
        _sel[idx] = on;
        if (on)
        {
            _selCount++;
            _selBytes += _items[idx].SizeBytes;
        }
        else
        {
            _selCount--;
            _selBytes -= _items[idx].SizeBytes;
        }
        InvalidateTile(idx);
    }

    private void Recount()
    {
        _selCount = 0; _selBytes = 0;
        for (int i = 0; i < _items.Length; i++)
            if (_sel[i]) { _selCount++; _selBytes += _items[i].SizeBytes; }
    }

    // ---------- 缩放 ----------

    public void Zoom(int delta)
    {
        int New() => Math.Clamp(_tileW + delta, 84, 220);
        _tileW = New();
        _tileH = New();
        UpdateScroll();
        Invalidate();
        VisibleRangeChanged?.Invoke();
    }

    public void RequestVisibleThumbnails(ThumbnailService svc)
    {
        var (first, last) = VisibleRange();
        var total = _items.Length;
        for (int i = first; i <= Math.Min(last + Columns, total - 1); i++)
            svc.Request(_items[i], priority: 0);
        for (int i = first - Columns; i >= Math.Max(0, first - Columns * 3); i--)
            svc.Request(_items[i], priority: 5);
    }
}
