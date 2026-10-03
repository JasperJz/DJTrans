using System.Text.Json;
using DJTrans.Core.Transfer;

namespace DJTrans;

/// <summary>应用设置（%LocalAppData%\DJTrans\settings.json，原子写入）。</summary>
public sealed class AppSettings
{
    public string LastExportDir { get; set; } = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
    public LayoutMode Layout { get; set; } = LayoutMode.Mirror;
    public ConflictPolicy Conflict { get; set; } = ConflictPolicy.SmartSkip;
    public bool Verify { get; set; } = true;
    public bool StrictSkipVerification { get; set; }
    public int Workers { get; set; } = 2;
    public bool IncludeProxy { get; set; }

    private static string PathFor() => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DJTrans", "settings.json");

    public static AppSettings Load()
    {
        try
        {
            var p = PathFor();
            if (File.Exists(p))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(p)) ?? new AppSettings();
        }
        catch { /* 损坏则用默认 */ }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            var p = PathFor();
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(p)!);
            var tmp = p + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
            if (File.Exists(p)) File.Replace(tmp, p, null);
            else File.Move(tmp, p);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }
}
