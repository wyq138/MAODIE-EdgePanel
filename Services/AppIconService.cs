using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Xaml.Media.Imaging;

namespace EdgePanel.Services;

/// <summary>
/// 提取应用的显示图标。用 Shell 的 IShellItemImageFactory（Windows 资源管理器
/// 取图标/缩略图用的同一个接口），拿到的 HBITMAP 自带正确的 alpha，不会像
/// GDI DrawIconEx 那样把透明区画成黑块。
/// </summary>
internal static class AppIconService
{
    private static readonly Dictionary<string, WriteableBitmap?> Cache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>取图标（带缓存）。取不到返回 null，由界面用占位字形兜底。</summary>
    public static WriteableBitmap? GetIcon(string? path, int size = 32)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        var key = $"{path}|{size}";
        if (Cache.TryGetValue(key, out var cached))
        {
            return cached;
        }

        var bitmap = Extract(path, size);
        Cache[key] = bitmap;
        return bitmap;
    }

    private static WriteableBitmap? Extract(string path, int size)
    {
        IShellItemImageFactory? factory = null;
        var hBitmap = IntPtr.Zero;
        var hdc = IntPtr.Zero;

        try
        {
            var iid = IID_IShellItemImageFactory;
            SHCreateItemFromParsingName(path, IntPtr.Zero, ref iid, out factory);

            var hr = factory.GetImage(
                new SIZE(size, size),
                SIIGBF.ICONONLY | SIIGBF.BIGGERSIZEOK,
                out hBitmap);

            if (hr != 0 || hBitmap == IntPtr.Zero)
            {
                return null;
            }

            var bmi = new BITMAPINFO
            {
                bmiHeader = new BITMAPINFOHEADER
                {
                    biSize = Marshal.SizeOf<BITMAPINFOHEADER>(),
                    biWidth = size,
                    biHeight = -size, // 负数 = 自上而下
                    biPlanes = 1,
                    biBitCount = 32,
                    biCompression = BI_RGB,
                },
            };

            var pixels = new byte[size * size * 4];
            hdc = CreateCompatibleDC(IntPtr.Zero);
            if (hdc == IntPtr.Zero)
            {
                return null;
            }

            // 注意：GetDIBits 要求位图未被选进任何 DC，这里 hdc 只作为参数传入
            if (GetDIBits(hdc, hBitmap, 0, (uint)size, pixels, ref bmi, DIB_RGB_COLORS) == 0)
            {
                return null;
            }

            var bitmap = new WriteableBitmap(size, size);
            using (var stream = bitmap.PixelBuffer.AsStream())
            {
                stream.Write(pixels, 0, pixels.Length);
            }

            bitmap.Invalidate();
            return bitmap;
        }
        catch (Exception ex)
        {
            App.Log($"提取图标失败 {path}：{ex.Message}");
            return null;
        }
        finally
        {
            if (hBitmap != IntPtr.Zero)
            {
                _ = DeleteObject(hBitmap);
            }

            if (hdc != IntPtr.Zero)
            {
                _ = DeleteDC(hdc);
            }

            if (factory is not null)
            {
                _ = Marshal.ReleaseComObject(factory);
            }
        }
    }

    private static readonly Guid IID_IShellItemImageFactory = new("bcc18b79-ba16-442f-80c4-8a59c30c463b");

    private const int BI_RGB = 0;
    private const uint DIB_RGB_COLORS = 0;

    [StructLayout(LayoutKind.Sequential)]
    private struct SIZE
    {
        public int cx;
        public int cy;

        public SIZE(int cx, int cy)
        {
            this.cx = cx;
            this.cy = cy;
        }
    }

    [Flags]
    private enum SIIGBF
    {
        RESIZETOFIT = 0x00,
        BIGGERSIZEOK = 0x01,
        MEMORYONLY = 0x02,
        ICONONLY = 0x04,
        THUMBNAILONLY = 0x08,
        INCACHEONLY = 0x10,
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public int biSize;
        public int biWidth;
        public int biHeight;
        public short biPlanes;
        public short biBitCount;
        public int biCompression;
        public int biSizeImage;
        public int biXPelsPerMeter;
        public int biYPelsPerMeter;
        public int biClrUsed;
        public int biClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFO
    {
        public BITMAPINFOHEADER bmiHeader;
        public int bmiColors;
    }

    [ComImport]
    [Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemImageFactory
    {
        [PreserveSig]
        int GetImage(SIZE size, SIIGBF flags, out IntPtr phbm);
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
    private static extern void SHCreateItemFromParsingName(
        string pszPath,
        IntPtr pbc,
        ref Guid riid,
        [MarshalAs(UnmanagedType.Interface)] out IShellItemImageFactory ppv);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(IntPtr obj);

    [DllImport("gdi32.dll")]
    private static extern int GetDIBits(
        IntPtr hdc, IntPtr hbm, uint start, uint lines, byte[] bits, ref BITMAPINFO info, uint usage);
}
