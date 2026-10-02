using System.Drawing;
using System.Runtime.InteropServices;

namespace DJTrans.Thumbs;

/// <summary>
/// Windows Shell 缩略图网关（IShellItemImageFactory，手写 COM 互操作）。
/// 调用线程必须为 STA（ThumbnailService 的工作线程已设置）。
/// 失败（无编解码器/不支持）返回 null，由调用方走降级链。
/// </summary>
internal static class ShellThumb
{
    private const int SIIGBF_RESIZETOFIT = 0x00;
    private const int SIIGBF_BIGGERSIZEOK = 0x01;
    private const int SIIGBF_THUMBNAILONLY = 0x08;

    [StructLayout(LayoutKind.Sequential)]
    private struct SIZE(int cx, int cy)
    {
        public int cx = cx;
        public int cy = cy;
    }

    [ComImport, Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItem
    {
        // 成员未使用：仅为 QI 转换到 IShellItemImageFactory 提供接口标识
    }

    [ComImport, Guid("BCC18B79-BA16-442F-80C4-8A59C30C463B"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemImageFactory
    {
        [PreserveSig]
        int GetImage(SIZE size, int flags, out IntPtr hbitmap);
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
    private static extern void SHCreateItemFromParsingName(string pszPath, IntPtr pbc, ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out object ppv);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern bool DeleteObject(IntPtr hObject);

    private static readonly Guid IidShellItem = new("43826D1E-E718-42EE-BC55-A1E261C37BFE");

    public static Bitmap? TryGetThumbnail(string path, int size)
    {
        try
        {
            var iid = IidShellItem;
            SHCreateItemFromParsingName(path, IntPtr.Zero, ref iid, out var obj);
            if (obj is not IShellItemImageFactory factory) return null;
            var hr = factory.GetImage(new SIZE(size, size), SIIGBF_THUMBNAILONLY | SIIGBF_RESIZETOFIT | SIIGBF_BIGGERSIZEOK, out var hbmp);
            if (hr != 0 || hbmp == IntPtr.Zero) return null;
            try
            {
                using var tmp = Image.FromHbitmap(hbmp, IntPtr.Zero);
                return new Bitmap(tmp);
            }
            finally
            {
                DeleteObject(hbmp);
            }
        }
        catch
        {
            return null;
        }
    }
}
