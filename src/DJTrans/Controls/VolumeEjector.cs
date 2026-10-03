using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace DJTrans.Controls;

/// <summary>
/// 卷安全弹出：FSCTL_LOCK_VOLUME + FSCTL_DISMOUNT_VOLUME（刷新写入并卸载卷），
/// 卸载卷不等于系统级设备弹出；用户仍需通过 Windows 安全移除硬件。
/// </summary>
internal static class VolumeEjector
{
    private const uint GENERIC_READ = 0x80000000;
    private const uint GENERIC_WRITE = 0x40000000;
    private const uint FILE_SHARE_READ = 1, FILE_SHARE_WRITE = 2;
    private const uint OPEN_EXISTING = 3;
    private const uint FSCTL_LOCK_VOLUME = 0x00090018;
    private const uint FSCTL_DISMOUNT_VOLUME = 0x00090020;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string lpFileName, uint dwDesiredAccess, uint dwShareMode,
        IntPtr lpSecurityAttributes, uint dwCreationDisposition, uint dwFlagsAndAttributes, IntPtr hTemplateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(SafeFileHandle hDevice, uint dwIoControlCode,
        IntPtr lpInBuffer, uint nInBufferSize, IntPtr lpOutBuffer, uint nOutBufferSize, out uint lpBytesReturned, IntPtr lpOverlapped);

    /// <returns>成功返回 null；失败返回用户可读原因。</returns>
    public static string? TryDismount(char letter)
    {
        using var h = CreateFileW($"\\\\.\\{letter}:", GENERIC_READ | GENERIC_WRITE,
            FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
        if (h.IsInvalid)
            return "无法打开卷（可能没有管理员权限或设备忙）";
        if (!DeviceIoControl(h, FSCTL_LOCK_VOLUME, IntPtr.Zero, 0, IntPtr.Zero, 0, out _, IntPtr.Zero))
            return "卷被占用（请关闭正在使用该设备的程序，如资源管理器/播放器）";
        if (!DeviceIoControl(h, FSCTL_DISMOUNT_VOLUME, IntPtr.Zero, 0, IntPtr.Zero, 0, out _, IntPtr.Zero))
            return "卸载卷失败（设备忙）";
        return null;
    }
}
