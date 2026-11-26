using System.Runtime.InteropServices;

namespace YGORogueDuelistCardModInstaller.Core;

/// <summary>
/// 100% Native AOT-safe image resizer/JPEG encoder using direct Win32 GDI+ (gdiplus.dll) &amp; COM IStream P/Invoke.
/// Eliminates any dependency on System.Drawing.Common or WinForms so the entire Core library compiles under PublishAot.
/// </summary>
public static unsafe class CardArtEncoder
{
    private static readonly Guid JpegEncoderClsid = new("557CF401-1A04-11D3-9A73-0000F81EF32E");
    private static readonly Guid EncoderQualityGuid = new("1D5BE4B5-FA4A-452D-9CDD-5DB35105E7EB");
    private const int PixelFormat32bppArgb = 0x26200A;
    private const int InterpolationModeHighQualityBicubic = 7;
    private const int PixelOffsetModeHighQuality = 2;
    private const int EncoderParameterValueTypeLong = 4;

    private static readonly Lock InitLock = new();
    private static nint _gdiplusToken;

    private static void EnsureGdiPlusStarted()
    {
        if (_gdiplusToken != 0)
        {
            return;
        }

        lock (InitLock)
        {
            if (_gdiplusToken != 0)
            {
                return;
            }

            var input = new GdiplusStartupInput { GdiplusVersion = 1 };
            var status = GdiplusStartup(out _gdiplusToken, ref input, nint.Zero);
            if (status != 0)
            {
                throw new ModOperationException($"初始化 GDI+ 失败（状态码 {status}）。");
            }
        }
    }

    public static string ConvertArtToBase64(string imagePath)
    {
        EnsureGdiPlusStarted();

        nint srcImage = 0;
        nint dstBitmap = 0;
        nint graphics = 0;
        nint streamPtr = 0;

        try
        {
            CheckStatus(GdipLoadImageFromFile(imagePath, out srcImage), $"无法加载图片 {imagePath}");
            CheckStatus(GdipCreateBitmapFromScan0(288, 420, 0, PixelFormat32bppArgb, nint.Zero, out dstBitmap), "创建目标位图失败");
            CheckStatus(GdipGetImageGraphicsContext(dstBitmap, out graphics), "创建绘图上下文失败");
            CheckStatus(GdipSetInterpolationMode(graphics, InterpolationModeHighQualityBicubic), "设置插值模式失败");
            CheckStatus(GdipSetPixelOffsetMode(graphics, PixelOffsetModeHighQuality), "设置像素偏移模式失败");
            CheckStatus(GdipDrawImageRectI(graphics, srcImage, 0, 0, 288, 420), "缩放绘制图片失败");

            var hr = CreateStreamOnHGlobal(nint.Zero, true, out streamPtr);
            if (hr != 0 || streamPtr == 0)
            {
                throw new ModOperationException($"创建内存流失败（HRESULT 0x{hr:X8}）。");
            }

            long quality = 82L;
            var encParams = new EncoderParametersNative
            {
                Count = 1,
                Parameter = new EncoderParameterNative
                {
                    Guid = EncoderQualityGuid,
                    NumberOfValues = 1,
                    Type = EncoderParameterValueTypeLong,
                    Value = (nint)(&quality)
                }
            };

            var clsid = JpegEncoderClsid;
            CheckStatus(GdipSaveImageToStream(dstBitmap, streamPtr, ref clsid, ref encParams), "编码 JPEG 失败");

            hr = GetHGlobalFromStream(streamPtr, out var hGlobal);
            if (hr != 0 || hGlobal == 0)
            {
                throw new ModOperationException($"读取 JPEG 内存缓冲区失败（HRESULT 0x{hr:X8}）。");
            }

            var size = (int)GlobalSize(hGlobal);
            var locked = GlobalLock(hGlobal);
            if (locked == 0)
            {
                throw new ModOperationException("锁定 JPEG 内存缓冲区失败。");
            }

            try
            {
                var buffer = new byte[size];
                Marshal.Copy(locked, buffer, 0, size);
                return Convert.ToBase64String(buffer);
            }
            finally
            {
                GlobalUnlock(hGlobal);
            }
        }
        finally
        {
            if (streamPtr != 0) Marshal.Release(streamPtr);
            if (graphics != 0) GdipDeleteGraphics(graphics);
            if (dstBitmap != 0) GdipDisposeImage(dstBitmap);
            if (srcImage != 0) GdipDisposeImage(srcImage);
        }
    }

    private static void CheckStatus(int status, string message)
    {
        if (status != 0)
        {
            throw new ModOperationException($"{message}（GDI+ 错误码 {status}）");
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct GdiplusStartupInput
    {
        public uint GdiplusVersion;
        public nint DebugEventCallback;
        public int SuppressBackgroundThread;
        public int SuppressExternalCodecs;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct EncoderParameterNative
    {
        public Guid Guid;
        public uint NumberOfValues;
        public uint Type;
        public nint Value;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct EncoderParametersNative
    {
        public uint Count;
        public EncoderParameterNative Parameter;
    }

    [DllImport("gdiplus.dll", ExactSpelling = true)]
    private static extern int GdiplusStartup(out nint token, ref GdiplusStartupInput input, nint output);

    [DllImport("gdiplus.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    private static extern int GdipLoadImageFromFile(string filename, out nint image);

    [DllImport("gdiplus.dll", ExactSpelling = true)]
    private static extern int GdipCreateBitmapFromScan0(int width, int height, int stride, int format, nint scan0, out nint bitmap);

    [DllImport("gdiplus.dll", ExactSpelling = true)]
    private static extern int GdipGetImageGraphicsContext(nint image, out nint graphics);

    [DllImport("gdiplus.dll", ExactSpelling = true)]
    private static extern int GdipSetInterpolationMode(nint graphics, int interpolationMode);

    [DllImport("gdiplus.dll", ExactSpelling = true)]
    private static extern int GdipSetPixelOffsetMode(nint graphics, int pixelOffsetMode);

    [DllImport("gdiplus.dll", ExactSpelling = true)]
    private static extern int GdipDrawImageRectI(nint graphics, nint image, int x, int y, int width, int height);

    [DllImport("gdiplus.dll", ExactSpelling = true)]
    private static extern int GdipSaveImageToStream(nint image, nint stream, ref Guid clsidEncoder, ref EncoderParametersNative encoderParams);

    [DllImport("gdiplus.dll", ExactSpelling = true)]
    private static extern int GdipDeleteGraphics(nint graphics);

    [DllImport("gdiplus.dll", ExactSpelling = true)]
    private static extern int GdipDisposeImage(nint image);

    [DllImport("ole32.dll", ExactSpelling = true)]
    private static extern int CreateStreamOnHGlobal(nint hGlobal, [MarshalAs(UnmanagedType.Bool)] bool deleteOnRelease, out nint ppstm);

    [DllImport("ole32.dll", ExactSpelling = true)]
    private static extern int GetHGlobalFromStream(nint pstm, out nint phglobal);

    [DllImport("kernel32.dll", ExactSpelling = true)]
    private static extern nuint GlobalSize(nint hMem);

    [DllImport("kernel32.dll", ExactSpelling = true)]
    private static extern nint GlobalLock(nint hMem);

    [DllImport("kernel32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalUnlock(nint hMem);
}
