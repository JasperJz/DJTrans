namespace DJTrans.Controls;

/// <summary>WinForms 没有原生 ToolStripCheckBox，用宿主包装。</summary>
public sealed class ToolStripCheckBox : ToolStripControlHost
{
    public ToolStripCheckBox(string text)
        : base(new CheckBox { Text = text, AutoSize = true, Padding = new Padding(2, 2, 2, 2) })
    {
    }
    private CheckBox Inner => (CheckBox)Control;
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public bool Checked { get => Inner.Checked; set => Inner.Checked = value; }
    public event EventHandler? CheckedChanged
    {
        add => Inner.CheckedChanged += value;
        remove => Inner.CheckedChanged -= value;
    }
}
