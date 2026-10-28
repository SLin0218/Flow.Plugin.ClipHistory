using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;

namespace Flow.Plugin.ClipHistory.Services;

public class ThumbnailService
{
    private readonly string _imagesDir;
    private readonly string _thumbsDir;
    private readonly string _previewsDir;

    public string ImagesDir => _imagesDir;
    public string ThumbsDir => _thumbsDir;

    public ThumbnailService(string baseDataDir)
    {
        _imagesDir = Path.Combine(baseDataDir, "Images");
        _thumbsDir = Path.Combine(baseDataDir, "Thumbs");
        _previewsDir = Path.Combine(baseDataDir, "Previews");

        if (!Directory.Exists(_imagesDir)) Directory.CreateDirectory(_imagesDir);
        if (!Directory.Exists(_thumbsDir)) Directory.CreateDirectory(_thumbsDir);
        if (!Directory.Exists(_previewsDir)) Directory.CreateDirectory(_previewsDir);
    }

    /// <summary>
    /// 保存剪贴板图片并生成高效缩略图及卡片自适应预览图
    /// </summary>
    public (string rawPath, string thumbPath, string hash, int width, int height) ProcessAndSaveImage(BitmapSource source)
    {
        source = FixAlphaIfAllZero(source);

        if (source.CanFreeze && !source.IsFrozen)
        {
            source.Freeze();
        }

        int width = source.PixelWidth;
        int height = source.PixelHeight;

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(source));
        using var ms = new MemoryStream();
        encoder.Save(ms);
        var imageBytes = ms.ToArray();

        string hash;
        using (var sha = SHA256.Create())
        {
            var hashBytes = sha.ComputeHash(imageBytes);
            hash = BitConverter.ToString(hashBytes).Replace("-", "").ToLowerInvariant();
        }

        var rawPath = Path.Combine(_imagesDir, $"img_{hash}.png");
        var thumbPath = Path.Combine(_thumbsDir, $"thumb_{hash}.png");
        var prevPath = Path.Combine(_previewsDir, $"prev_{hash}.png");

        if (!File.Exists(rawPath))
        {
            File.WriteAllBytes(rawPath, imageBytes);
        }

        if (!File.Exists(thumbPath))
        {
            CreateThumbnail(source, thumbPath, maxDimension: 96);
        }

        if (!File.Exists(prevPath))
        {
            CreateScaledImage(source, prevPath, maxWidth: 440, maxHeight: 340);
        }

        return (rawPath, thumbPath, hash, width, height);
    }

    public string GetOrCreatePreviewImage(string? rawImagePath)
    {
        if (string.IsNullOrEmpty(rawImagePath) || !File.Exists(rawImagePath))
        {
            return rawImagePath ?? string.Empty;
        }

        try
        {
            var fileName = Path.GetFileNameWithoutExtension(rawImagePath);
            string hash = fileName.StartsWith("img_") ? fileName.Substring(4) : fileName;
            var prevPath = Path.Combine(_previewsDir, $"prev_{hash}.png");

            if (File.Exists(prevPath))
            {
                return prevPath;
            }

            var rawBytes = File.ReadAllBytes(rawImagePath);
            using var stream = new MemoryStream(rawBytes);
            var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            if (decoder.Frames.Count > 0)
            {
                var frame = decoder.Frames[0];
                CreateScaledImage(frame, prevPath, maxWidth: 440, maxHeight: 340);
                return prevPath;
            }
        }
        catch { }

        return rawImagePath;
    }

    public void DeleteImageFiles(string? rawImagePath, string? thumbPath = null)
    {
        var filesToDelete = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (!string.IsNullOrEmpty(rawImagePath))
        {
            filesToDelete.Add(rawImagePath);
            var fileName = Path.GetFileNameWithoutExtension(rawImagePath);
            string hash = fileName.StartsWith("img_") ? fileName.Substring(4) : fileName;
            filesToDelete.Add(Path.Combine(_thumbsDir, $"thumb_{hash}.png"));
            filesToDelete.Add(Path.Combine(_previewsDir, $"prev_{hash}.png"));
        }

        if (!string.IsNullOrEmpty(thumbPath))
        {
            filesToDelete.Add(thumbPath);
            var fileName = Path.GetFileNameWithoutExtension(thumbPath);
            string hash = fileName.StartsWith("thumb_") ? fileName.Substring(6) : fileName;
            filesToDelete.Add(Path.Combine(_imagesDir, $"img_{hash}.png"));
            filesToDelete.Add(Path.Combine(_previewsDir, $"prev_{hash}.png"));
        }

        DeleteFilesSafe(filesToDelete);
    }

    public static void DeleteFilesSafe(IEnumerable<string> files)
    {
        var list = files.Where(f => !string.IsNullOrEmpty(f)).Distinct().ToList();
        if (list.Count == 0) return;

        Task.Run(async () =>
        {
            foreach (var file in list)
            {
                for (int i = 0; i < 5; i++)
                {
                    try
                    {
                        if (File.Exists(file))
                        {
                            File.Delete(file);
                        }
                        break;
                    }
                    catch (IOException)
                    {
                        await Task.Delay(100);
                    }
                    catch
                    {
                        break;
                    }
                }
            }
        });
    }

    public static BitmapSource FixAlphaIfAllZero(BitmapSource source)
    {
        if (source == null) return source!;

        var formattedSource = new FormatConvertedBitmap();
        formattedSource.BeginInit();
        formattedSource.Source = source;
        formattedSource.DestinationFormat = System.Windows.Media.PixelFormats.Bgra32;
        formattedSource.EndInit();

        int width = formattedSource.PixelWidth;
        int height = formattedSource.PixelHeight;
        if (width <= 0 || height <= 0) return source;

        int stride = width * 4;
        byte[] pixels = new byte[stride * height];
        formattedSource.CopyPixels(pixels, stride, 0);

        bool hasNonZeroAlpha = false;
        for (int i = 3; i < pixels.Length; i += 4)
        {
            if (pixels[i] != 0)
            {
                hasNonZeroAlpha = true;
                break;
            }
        }

        if (!hasNonZeroAlpha && pixels.Length > 0)
        {
            for (int i = 3; i < pixels.Length; i += 4)
            {
                pixels[i] = 255;
            }

            var fixedBitmap = BitmapSource.Create(
                width,
                height,
                formattedSource.DpiX > 0 ? formattedSource.DpiX : 96,
                formattedSource.DpiY > 0 ? formattedSource.DpiY : 96,
                System.Windows.Media.PixelFormats.Bgra32,
                null,
                pixels,
                stride);
            fixedBitmap.Freeze();
            return fixedBitmap;
        }

        if (formattedSource.CanFreeze && !formattedSource.IsFrozen)
        {
            formattedSource.Freeze();
        }
        return formattedSource;
    }

    private void CreateThumbnail(BitmapSource source, string savePath, int maxDimension)
    {
        double scale = 1.0;
        int maxSide = Math.Max(source.PixelWidth, source.PixelHeight);
        if (maxSide > maxDimension)
        {
            scale = (double)maxDimension / maxSide;
        }

        var transformed = new TransformedBitmap(source, new System.Windows.Media.ScaleTransform(scale, scale));
        if (transformed.CanFreeze && !transformed.IsFrozen)
        {
            transformed.Freeze();
        }

        var thumbEncoder = new PngBitmapEncoder();
        thumbEncoder.Frames.Add(BitmapFrame.Create(transformed));
        using var fs = new FileStream(savePath, FileMode.Create, FileAccess.Write);
        thumbEncoder.Save(fs);
    }

    private void CreateScaledImage(BitmapSource source, string savePath, int maxWidth, int maxHeight)
    {
        double scaleX = (double)maxWidth / source.PixelWidth;
        double scaleY = (double)maxHeight / source.PixelHeight;
        double scale = Math.Min(1.0, Math.Min(scaleX, scaleY));

        BitmapSource outputSource = source;
        if (scale < 1.0)
        {
            var transformed = new TransformedBitmap(source, new System.Windows.Media.ScaleTransform(scale, scale));
            if (transformed.CanFreeze && !transformed.IsFrozen)
            {
                transformed.Freeze();
            }
            outputSource = transformed;
        }

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(outputSource));
        using var fs = new FileStream(savePath, FileMode.Create, FileAccess.Write);
        encoder.Save(fs);
    }
}
