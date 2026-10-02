using DJTrans.Core.Transfer;

namespace DJTrans.Dialogs;

/// <summary>同名冲突决策对话框（Ask 策略；AC-09），支持"应用到全部"。</summary>
public sealed class ConflictDialog : Form
{
    public ConflictDecision Decision { get; private set; } = ConflictDecision.Skip;
    public bool ApplyToAll => _applyAll.Checked;

    private readonly CheckBox _applyAll;

    public ConflictDialog(TransferEngine.TransferJob job)
    {
        Text = "目标已存在同名文件";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false; MinimizeBox = false;
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(520, 240);
        Font = new Font("Segoe UI", 9F);

        var name = Path.GetFileName(job.Req.SourcePath);
        var info = new Label
        {
            Text = $"目标已存在：\n{job.CurrentDestPath}\n\n（内容与源不同）",
            Location = new Point(14, 14),
            Size = new Size(490, 70),
        };
        var question = new Label { Text = "如何处理？", Location = new Point(14, 96), AutoSize = true };
        _applyAll = new CheckBox
        {
            Text = "应用到本次全部剩余冲突",
            Location = new Point(14, 176),
            AutoSize = true,
        };

        var b1 = new Button { Text = "跳过", Location = new Point(14, 130), Width = 90, DialogResult = DialogResult.OK };
        b1.Click += (_, _) => Decision = ConflictDecision.Skip;
        var b2 = new Button { Text = "覆盖", Location = new Point(112, 130), Width = 90, DialogResult = DialogResult.OK };
        b2.Click += (_, _) => Decision = ConflictDecision.Overwrite;
        var b3 = new Button { Text = "保留两者(改名)", Location = new Point(210, 130), Width = 120, DialogResult = DialogResult.OK };
        b3.Click += (_, _) => Decision = ConflictDecision.RenameNew;
        var bc = new Button { Text = "取消传输", Location = new Point(414, 130), Width = 90, DialogResult = DialogResult.Cancel };

        AcceptButton = b1;
        CancelButton = bc;
        Controls.AddRange([info, question, b1, b2, b3, bc, _applyAll]);
    }
}
