using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using SharpDX;
using DXGI = SharpDX.DXGI;
using D3D11 = SharpDX.Direct3D11;

namespace DesktopAgentWpf.Services;

public sealed class ScreenshotService
{
    private const int SampleGrid = 5;
    private readonly Action<string>? _log;
    private readonly bool _verbose;
    private readonly object _captureLock = new();
    private int _blankDuplicationCount;
    private DateTime _duplicationRetryAfterUtc = DateTime.MinValue;
    private DateTime _lastBlankLogUtc = DateTime.MinValue;
    private DateTime _lastDuplicationErrorLogUtc = DateTime.MinValue;

    public ScreenshotService(Action<string>? log, bool verbose)
    {
        _log = log;
        _verbose = verbose;
    }

    public string CapturePng(string outputPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        Bitmap? bitmap = null;
        bool usedDuplication;
        bool usedGdi;

        lock (_captureLock)
        {
            bitmap = CaptureBitmap(out usedDuplication, out usedGdi);
        }

        using (bitmap)
        {
            bitmap.Save(outputPath, ImageFormat.Png);
        }

        if (usedGdi)
        {
            Log("Screenshot captured with GDI fallback.");
        }
        else if (usedDuplication)
        {
            Log("Screenshot captured with desktop duplication.");
        }

        SaveDebugCopy(outputPath);
        return outputPath;
    }

    public byte[] CaptureJpegBytes(long quality, int maxWidth, int maxHeight, out int width, out int height)
    {
        Bitmap? bitmap = null;
        lock (_captureLock)
        {
            bitmap = CaptureBitmap(out _, out _);
        }

        using (bitmap)
        using (var resized = ResizeIfNeeded(bitmap, maxWidth, maxHeight))
        using (var ms = new MemoryStream())
        {
            width = resized.Width;
            height = resized.Height;
            var encoder = GetJpegEncoder();
            if (encoder != null)
            {
                var q = Math.Clamp(quality, 40L, 95L);
                using var encParams = new EncoderParameters(1);
                encParams.Param[0] = new EncoderParameter(Encoder.Quality, q);
                resized.Save(ms, encoder, encParams);
            }
            else
            {
                resized.Save(ms, ImageFormat.Jpeg);
            }
            return ms.ToArray();
        }
    }

    private Bitmap CaptureBitmap(out bool usedDuplication, out bool usedGdi)
    {
        usedDuplication = false;
        usedGdi = false;

        if (DateTime.UtcNow < _duplicationRetryAfterUtc)
        {
            usedGdi = true;
            return CaptureWithGdiBitmap();
        }

        try
        {
            var bitmap = TryCaptureWithDuplication(out var blank);
            usedDuplication = true;
            if (!blank)
            {
                _blankDuplicationCount = 0;
                _duplicationRetryAfterUtc = DateTime.MinValue;
                return bitmap;
            }

            _blankDuplicationCount++;
            if (_blankDuplicationCount >= 3)
            {
                _duplicationRetryAfterUtc = DateTime.UtcNow.AddSeconds(30);
                LogThrottled(ref _lastBlankLogUtc, "Screenshot appears blank (duplication). Forcing GDI for 30s.");
            }
            else
            {
                LogThrottled(ref _lastBlankLogUtc, "Screenshot appears blank (duplication). Falling back to GDI.");
            }
            bitmap.Dispose();
        }
        catch (Exception ex)
        {
            LogThrottled(ref _lastDuplicationErrorLogUtc, $"Desktop duplication failed: {ex.Message}. Falling back to GDI.");
        }

        usedGdi = true;
        return CaptureWithGdiBitmap();
    }

    private Bitmap TryCaptureWithDuplication(out bool isBlank)
    {
        isBlank = false;

        using var factory = new DXGI.Factory1();
        using var adapter = factory.GetAdapter1(0);
        using var device = new D3D11.Device(adapter, D3D11.DeviceCreationFlags.BgraSupport);
        using var output = adapter.Outputs[0];
        using var output1 = output.QueryInterface<DXGI.Output1>();
        using var duplication = output1.DuplicateOutput(device);

        DXGI.Resource? screenResource = null;
        DXGI.OutputDuplicateFrameInformation frameInfo;

        var acquired = false;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var result = duplication.TryAcquireNextFrame(1000, out frameInfo, out screenResource);
            if (result == DXGI.ResultCode.WaitTimeout)
            {
                Thread.Sleep(30);
                continue;
            }

            if (result.Failure)
            {
                throw new InvalidOperationException($"AcquireNextFrame failed: {result}");
            }

            acquired = true;
            break;
        }

        if (!acquired || screenResource == null)
        {
            throw new InvalidOperationException("Failed to capture screenshot frame.");
        }

        using (screenResource)
        using (var texture = screenResource.QueryInterface<D3D11.Texture2D>())
        {
            var desc = texture.Description;
            var stagingDesc = new D3D11.Texture2DDescription
            {
                Width = desc.Width,
                Height = desc.Height,
                MipLevels = 1,
                ArraySize = 1,
                Format = desc.Format,
                SampleDescription = new DXGI.SampleDescription(1, 0),
                Usage = D3D11.ResourceUsage.Staging,
                BindFlags = D3D11.BindFlags.None,
                CpuAccessFlags = D3D11.CpuAccessFlags.Read,
                OptionFlags = D3D11.ResourceOptionFlags.None
            };

            using var staging = new D3D11.Texture2D(device, stagingDesc);
            device.ImmediateContext.CopyResource(texture, staging);

            var mapSource = device.ImmediateContext.MapSubresource(staging, 0, D3D11.MapMode.Read, D3D11.MapFlags.None);

            var bitmap = new Bitmap(desc.Width, desc.Height, PixelFormat.Format32bppArgb);
            var boundsRect = new Rectangle(0, 0, desc.Width, desc.Height);
            var mapDest = bitmap.LockBits(boundsRect, ImageLockMode.WriteOnly, bitmap.PixelFormat);

            for (var y = 0; y < desc.Height; y++)
            {
                var sourcePtr = mapSource.DataPointer + y * mapSource.RowPitch;
                var destPtr = mapDest.Scan0 + y * mapDest.Stride;
                Utilities.CopyMemory(destPtr, sourcePtr, desc.Width * 4);
            }

            bitmap.UnlockBits(mapDest);
            device.ImmediateContext.UnmapSubresource(staging, 0);
            duplication.ReleaseFrame();

            isBlank = IsMostlyBlank(bitmap);
            return bitmap;
        }
    }

    private static bool IsMostlyBlank(Bitmap bitmap)
    {
        var width = bitmap.Width;
        var height = bitmap.Height;
        if (width == 0 || height == 0)
        {
            return true;
        }

        int min = 255;
        int max = 0;

        for (var y = 0; y < SampleGrid; y++)
        {
            var py = (int)((y + 0.5) / SampleGrid * height);
            for (var x = 0; x < SampleGrid; x++)
            {
                var px = (int)((x + 0.5) / SampleGrid * width);
                var color = bitmap.GetPixel(Math.Clamp(px, 0, width - 1), Math.Clamp(py, 0, height - 1));
                var lum = (color.R + color.G + color.B) / 3;
                min = Math.Min(min, lum);
                max = Math.Max(max, lum);
            }
        }

        return (max - min) < 8;
    }

    private Bitmap CaptureWithGdiBitmap()
    {
        var left = GetSystemMetrics(76);
        var top = GetSystemMetrics(77);
        var width = GetSystemMetrics(78);
        var height = GetSystemMetrics(79);

        if (width <= 0 || height <= 0)
        {
            width = GetSystemMetrics(0);
            height = GetSystemMetrics(1);
            left = 0;
            top = 0;
        }

        var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bitmap);
        g.CopyFromScreen(left, top, 0, 0, new Size(width, height), CopyPixelOperation.SourceCopy);
        return bitmap;
    }

    private static ImageCodecInfo? GetJpegEncoder()
    {
        return ImageCodecInfo.GetImageDecoders()
            .FirstOrDefault(codec => codec.FormatID == ImageFormat.Jpeg.Guid);
    }

    private static Bitmap ResizeIfNeeded(Bitmap source, int maxWidth, int maxHeight)
    {
        if (maxWidth <= 0 && maxHeight <= 0)
        {
            return (Bitmap)source.Clone();
        }

        var targetW = maxWidth > 0 ? maxWidth : source.Width;
        var targetH = maxHeight > 0 ? maxHeight : source.Height;

        var scale = Math.Min((double)targetW / source.Width, (double)targetH / source.Height);
        if (scale >= 1.0)
        {
            return (Bitmap)source.Clone();
        }

        var newW = Math.Max(1, (int)Math.Round(source.Width * scale));
        var newH = Math.Max(1, (int)Math.Round(source.Height * scale));
        var resized = new Bitmap(newW, newH, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(resized);
        g.CompositingMode = System.Drawing.Drawing2D.CompositingMode.SourceCopy;
        g.CompositingQuality = System.Drawing.Drawing2D.CompositingQuality.HighSpeed;
        g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBilinear;
        g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighSpeed;
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.None;
        g.DrawImage(source, new Rectangle(0, 0, newW, newH));
        return resized;
    }

    private void SaveDebugCopy(string sourcePath)
    {
        try
        {
            var debugDir = Path.Combine(Directory.GetCurrentDirectory(), "debug_screens");
            Directory.CreateDirectory(debugDir);
            var debugPath = Path.Combine(debugDir, $"screenshot_{DateTime.Now:yyyyMMdd_HHmmss_fff}.png");
            File.Copy(sourcePath, debugPath, true);
            Log($"Screenshot saved to {debugPath}");
        }
        catch (Exception ex)
        {
            Log($"Failed to save debug screenshot: {ex.Message}");
        }
    }

    private void Log(string message)
    {
        if (_verbose)
        {
            _log?.Invoke(message);
        }
    }

    private void LogThrottled(ref DateTime lastLogUtc, string message)
    {
        if (!_verbose)
        {
            return;
        }

        var now = DateTime.UtcNow;
        if ((now - lastLogUtc) < TimeSpan.FromSeconds(10))
        {
            return;
        }

        lastLogUtc = now;
        _log?.Invoke(message);
    }

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);
}
