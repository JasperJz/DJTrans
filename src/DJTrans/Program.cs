namespace DJTrans;

internal static class Program
{
    /// <summary>单实例互斥（R0-B3 采纳项）：双开进程会写坏同一 .djpart 现场。</summary>
    private static Mutex? _singleInstance;

    [STAThread]
    private static void Main()
    {
        // 全局异常捕获：任何未处理异常写入 crash.log（带堆栈），下次报错有据可查
        Application.ThreadException += (_, e) => Handle(e.Exception, "UI线程");
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            if (e.ExceptionObject is Exception ex) Handle(ex, "后台线程", fatal: true);
        };
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            CrashLog.Write(e.Exception, "未观察Task");
            e.SetObserved();
        };

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

    private static void Handle(Exception ex, string source, bool fatal = false)
    {
        CrashLog.Write(ex, source);
        var msg = $"DJTrans 遇到了一个错误（{source}），详细信息已记录到：\n{CrashLog.FilePath}\n\n{ex.Message}";
        if (fatal)
        {
            MessageBox.Show(msg, "DJTrans", MessageBoxButtons.OK, MessageBoxIcon.Error);
            Environment.Exit(1);
        }
        else
        {
            // CatchException 模式：用户可继续使用；不中断应用
            MessageBox.Show(msg, "DJTrans", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }
}

/// <summary>崩溃/异常日志（%LocalAppData%\DJTrans\crash.log，带堆栈，线程安全）。</summary>
internal static class CrashLog
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    public static string FilePath => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DJTrans", "crash.log");

    public static void Write(Exception ex, string source)
    {
        try
        {
            Gate.Wait();
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(FilePath)!);
            System.IO.File.AppendAllText(FilePath,
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] ({source}) {ex}\n{new string('-', 80)}\n");
        }
        catch { }
        finally { Gate.Release(); }
    }
}
