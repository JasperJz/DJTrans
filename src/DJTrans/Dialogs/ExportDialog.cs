using DJTrans.Core.Transfer;
using DJTrans.Controls;

namespace DJTrans.Dialogs;

/// <summary>导出对话框：目标目录/布局/冲突策略/校验 + 目标盘空间预检（AC-06）。</summary>
public sealed class ExportDialog : Form
{
    private readonly TextBox _dir;
    private readonly ComboBox _layout;
    private readonly ComboBox _conflict;
    private readonly CheckBox _verify;
    private readonly CheckBox _strictSkip;
    private readonly Label _space;
    private readonly Button _ok;
    private readonly Button _cancel;
    private readonly long _needBytes;

    public string DestDir { get; private set; } = "";
    public LayoutMode DestLayout { get; private set; } = LayoutMode.Mirror;
    public ConflictPolicy Conflict { get; private set; } = ConflictPolicy.SmartSkip;
    public bool Verify => _verify.Checked;
    public bool StrictSkipVerification => _strictSkip.Checked;

    public ExportDialog(IReadOnlyList<Core.Scan.MediaItem> selected, string lastDir)
    {
        Text = "导出到电脑";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(620, 320);
        Font = new Font("Segoe UI", 9F);

        long bytes = selected.Sum(i => i.SizeBytes);
        _needBytes = bytes;
        var label0 = new Label { Text = $"将导出 {selected.Count} 个文件，共 {ThumbGrid.FmtBytes(bytes)}", Location = new Point(14, 14), AutoSize = true };

        var l1 = new Label { Text = "目标目录:", Location = new Point(14, 50), AutoSize = true };
        _dir = new TextBox { Location = new Point(90, 47), Width = 400, Text = lastDir };
        var browse = new Button { Text = "浏览…", Location = new Point(496, 45), Width = 50, FlatStyle = FlatStyle.Flat };
        browse.Click += (_, _) =>
        {
            using var fbd = new FolderBrowserDialog { SelectedPath = Directory.Exists(_dir.Text) ? _dir.Text : "" };
            if (fbd.ShowDialog(this) == DialogResult.OK) _dir.Text = fbd.SelectedPath;
        };

        var l2 = new Label { Text = "目录组织:", Location = new Point(14, 82), AutoSize = true };
        _layout = new ComboBox { Location = new Point(90, 78), Width = 200, DropDownStyle = ComboBoxStyle.DropDownList };
        _layout.Items.Add("保持相机目录结构（镜像）");
        _layout.Items.Add("按拍摄日期分文件夹");
        _layout.Items.Add("全部放到目标目录");
        _layout.SelectedIndex = 0;

        var l3 = new Label { Text = "同名冲突:", Location = new Point(14, 114), AutoSize = true };
        _conflict = new ComboBox { Location = new Point(90, 110), Width = 260, DropDownStyle = ComboBoxStyle.DropDownList };
        _conflict.Items.Add("智能跳过（大小与首尾指纹匹配时跳过）");
        _conflict.Items.Add("总是询问");
        _conflict.Items.Add("覆盖");
        _conflict.Items.Add("跳过");
        _conflict.Items.Add("保留两者（新文件自动改名）");
        _conflict.SelectedIndex = 0;

        _verify = new CheckBox { Text = "写后读回校验（XxHash3 分块比对）", Location = new Point(14, 146), AutoSize = true, Checked = true };

        _strictSkip = new CheckBox { Text = "严格跳过：逐字节比对整个已有文件（较慢）", Location = new Point(14, 174), AutoSize = true };
        _space = new Label { Location = new Point(14, 202), AutoSize = true, ForeColor = Color.FromArgb(90, 90, 90) };
        Action upd = () =>
        {
            try
            {
                var root = System.IO.Path.GetPathRoot(System.IO.Path.GetFullPath(_dir.Text.Trim()));
                var di = new DriveInfo(root!);
                var free = di.AvailableFreeSpace;
                _space.Text = $"目标盘 {root} 可用 {ThumbGrid.FmtBytes(free)}";
                if (free < _needBytes + 64L * 1024 * 1024)
                {
                    _space.Text += $"  ⚠ 空间不足（还需约 {ThumbGrid.FmtBytes(_needBytes + 64L * 1024 * 1024)}），可能导致传输失败";
                    _space.ForeColor = Color.Firebrick;
                }
                else _space.ForeColor = Color.FromArgb(90, 90, 90);
            }
            catch
            {
                _space.Text = "目标目录无效";
                _space.ForeColor = Color.Firebrick;
            }
        };
        _dir.TextChanged += (_, _) => upd();
        upd();

        _ok = new Button { Text = "开始导出", DialogResult = DialogResult.OK, Location = new Point(350, 252), Width = 100 };
        _cancel = new Button { Text = "取消", DialogResult = DialogResult.Cancel, Location = new Point(456, 252), Width = 90 };
        _ok.Click += (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(_dir.Text) || !Directory.Exists(_dir.Text))
            {
                MessageBox.Show(this, "请选择有效的目标目录", "DJTrans", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                DialogResult = DialogResult.None;
                return;
            }
            DestDir = _dir.Text;
            DestLayout = (LayoutMode)_layout.SelectedIndex;
            Conflict = _conflict.SelectedIndex switch
            {
                1 => ConflictPolicy.Ask,
                2 => ConflictPolicy.Overwrite,
                3 => ConflictPolicy.Skip,
                4 => ConflictPolicy.RenameNew,
                _ => ConflictPolicy.SmartSkip,
            };
        };
        AcceptButton = _ok;
        CancelButton = _cancel;

        Controls.AddRange([label0, l1, _dir, browse, l2, _layout, l3, _conflict, _verify, _strictSkip, _space, _ok, _cancel]);
    }

    public void Preset(AppSettings s)
    {
        _layout.SelectedIndex = (int)s.Layout;
        _conflict.SelectedIndex = s.Conflict switch
        {
            ConflictPolicy.Ask => 1,
            ConflictPolicy.Overwrite => 2,
            ConflictPolicy.Skip => 3,
            ConflictPolicy.RenameNew => 4,
            _ => 0,
        };
        _verify.Checked = s.Verify;
        _strictSkip.Checked = s.StrictSkipVerification;
    }
}

