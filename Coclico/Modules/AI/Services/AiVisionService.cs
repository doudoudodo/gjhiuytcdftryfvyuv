using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;

namespace Coclico.Services.AI;

public sealed record AttachedImage(
    string Base64Data,
    string MimeType,
    BitmapSource PreviewSource,
    string FileName);

public static class AiVisionService
{
    private const int MaxDimension = 1600;

    public static AttachedImage? CaptureScreen()
    {
        try
        {
            Rectangle bounds = System.Windows.Forms.Screen.PrimaryScreen?.Bounds
                ?? new Rectangle(0, 0, (int)SystemParameters.PrimaryScreenWidth, (int)SystemParameters.PrimaryScreenHeight);

            if (bounds.Width <= 0 || bounds.Height <= 0)
            {
                return null;
            }

            using var bitmap = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format32bppArgb);
            using var gfx = Graphics.FromImage(bitmap);
            gfx.CopyFromScreen(bounds.Left, bounds.Top, 0, 0, bounds.Size, CopyPixelOperation.SourceCopy);

            (byte[]? bytes, string? mime) = ProcessImageBitmap(bitmap, MaxDimension);
            string base64 = Convert.ToBase64String(bytes);
            BitmapSource preview = LoadBitmapSource(bytes);
            return new AttachedImage(base64, mime, preview, "Capture_Ecran.jpg");
        }
        catch (Exception ex)
        {
            LoggingService.LogException(ex, "AiVisionService.CaptureScreen");
            return null;
        }
    }

    public static AttachedImage? FromClipboard()
    {
        try
        {
            if (!Clipboard.ContainsImage())
            {
                return null;
            }

            BitmapSource? imageSource = Clipboard.GetImage();
            if (imageSource == null)
            {
                return null;
            }

            // Downscale and encode directly in WPF: no PNG encode/decode
            // round-trip through System.Drawing (halves the memory copies).
            (byte[] bytes, string mime) = ProcessBitmapSource(imageSource, MaxDimension);
            string base64 = Convert.ToBase64String(bytes);
            BitmapSource preview = LoadBitmapSource(bytes);

            return new AttachedImage(base64, mime, preview, "Presse_Papier.jpg");
        }
        catch (Exception ex)
        {
            LoggingService.LogException(ex, "AiVisionService.FromClipboard");
            return null;
        }
    }

    /// <summary>
    /// WPF-only variant of <see cref="ProcessImageBitmap"/> (JPEG, quality 85,
    /// capped to <paramref name="maxDimension"/>) for sources already in WPF format.
    /// </summary>
    public static (byte[] Bytes, string Mime) ProcessBitmapSource(BitmapSource source, int maxDimension = 1600)
    {
        source.Freeze();

        BitmapSource processed = source;
        if (source.PixelWidth > maxDimension || source.PixelHeight > maxDimension)
        {
            double scale = Math.Min(
                (double)maxDimension / source.PixelWidth,
                (double)maxDimension / source.PixelHeight);
            var transformed = new TransformedBitmap(source, new System.Windows.Media.ScaleTransform(scale, scale));
            transformed.Freeze();
            processed = transformed;
        }

        using var ms = new MemoryStream();
        var encoder = new JpegBitmapEncoder { QualityLevel = 85 };
        encoder.Frames.Add(BitmapFrame.Create(processed));
        encoder.Save(ms);
        return (ms.ToArray(), "image/jpeg");
    }

    public static AttachedImage? FromFilePath(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            string ext = Path.GetExtension(path).ToLowerInvariant();
            if (ext is not (".jpg" or ".jpeg" or ".png" or ".webp" or ".bmp"))
            {
                return null;
            }

            using var original = new Bitmap(path);
            (byte[]? bytes, string? mime) = ProcessImageBitmap(original, MaxDimension);
            string base64 = Convert.ToBase64String(bytes);
            BitmapSource preview = LoadBitmapSource(bytes);
            return new AttachedImage(base64, mime, preview, Path.GetFileName(path));
        }
        catch (Exception ex)
        {
            LoggingService.LogException(ex, "AiVisionService.FromFilePath");
            return null;
        }
    }

    public static (byte[] Bytes, string Mime) ProcessImageBitmap(Bitmap bitmap, int maxDimension = 1600)
    {
        Bitmap processed = bitmap;
        bool mustDispose = false;

        if (bitmap.Width > maxDimension || bitmap.Height > maxDimension)
        {
            float scale = Math.Min((float)maxDimension / bitmap.Width, (float)maxDimension / bitmap.Height);
            int targetW = Math.Max(1, (int)(bitmap.Width * scale));
            int targetH = Math.Max(1, (int)(bitmap.Height * scale));

            processed = new Bitmap(targetW, targetH, PixelFormat.Format32bppArgb);
            using var g = Graphics.FromImage(processed);
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.SmoothingMode = SmoothingMode.HighQuality;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.DrawImage(bitmap, 0, 0, targetW, targetH);
            mustDispose = true;
        }

        try
        {
            using var ms = new MemoryStream();
            ImageCodecInfo? jpegCodec = GetEncoder(ImageFormat.Jpeg);
            if (jpegCodec != null)
            {
                using var encoderParams = new EncoderParameters(1);
                encoderParams.Param[0] = new EncoderParameter(Encoder.Quality, 85L);
                processed.Save(ms, jpegCodec, encoderParams);
                return (ms.ToArray(), "image/jpeg");
            }
            else
            {
                processed.Save(ms, ImageFormat.Png);
                return (ms.ToArray(), "image/png");
            }
        }
        finally
        {
            if (mustDispose)
            {
                processed.Dispose();
            }
        }
    }

    private static ImageCodecInfo? GetEncoder(ImageFormat format)
    {
        ImageCodecInfo[] codecs = ImageCodecInfo.GetImageEncoders();
        foreach (ImageCodecInfo codec in codecs)
        {
            if (codec.FormatID == format.Guid)
            {
                return codec;
            }
        }
        return null;
    }

    private static BitmapSource LoadBitmapSource(byte[] bytes)
    {
        using var ms = new MemoryStream(bytes);
        var bi = new BitmapImage();
        bi.BeginInit();
        bi.CacheOption = BitmapCacheOption.OnLoad;
        bi.StreamSource = ms;
        bi.EndInit();
        bi.Freeze();
        return bi;
    }
}
