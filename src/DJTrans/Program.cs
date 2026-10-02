namespace DJTrans;

internal static class Program
{
    /// <summary>单实例互斥（R0-B3 采纳项）：双开进程会写坏同一 .djpart 现场。</summary>
    private static Mutex? _singleInstance;

    [STAThread]
    private static void Main()
    {
        _singleInstance = new Mutex(true, @"Global\DJTrans.SingleInstance", out var isNew);
        if (!isNew)
        {
            MessageBox.Show("DJTrans 已在运行。", "DJTrans", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm());
        GC.KeepAlive(_singleInstance);
    }
}
