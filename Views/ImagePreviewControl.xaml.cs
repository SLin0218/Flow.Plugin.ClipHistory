using System;
using System.Diagnostics;
using System.IO;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using Flow.Plugin.ClipHistory.Models;
using Flow.Plugin.ClipHistory.Services;

namespace Flow.Plugin.ClipHistory.Views;

public partial class ImagePreviewControl : UserControl
{
    private readonly string? _imagePath;

    public ImagePreviewControl(ClipItem item)
    {
        InitializeComponent();
        _imagePath = item.ImagePath;
        LoadImage(item);
    }

    private void LoadImage(ClipItem item)
    {
        try
        {
            if (!string.IsNullOrEmpty(item.ImagePath) && File.Exists(item.ImagePath))
            {
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.UriSource = new Uri(item.ImagePath, UriKind.Absolute);
                bitmap.EndInit();
                bitmap.Freeze();

                ImgPreview.Source = bitmap;

                var fileInfo = new FileInfo(item.ImagePath);
                var sizeStr = FormatFileSize(fileInfo.Length);
                int w = item.ImageWidth > 0 ? item.ImageWidth : bitmap.PixelWidth;
                int h = item.ImageHeight > 0 ? item.ImageHeight : bitmap.PixelHeight;

                TxtInfo.Text = $"{w} × {h}  ·  {sizeStr}  ·  {item.CreatedAt:yyyy-MM-dd HH:mm:ss}";
                ToolTip = item.ImagePath;
            }
            else
            {
                TxtInfo.Text = I18n.T("图片文件不存在", "Image file not found");
            }
        }
        catch (Exception ex)
        {
            TxtInfo.Text = I18n.T("加载图片失败: ", "Failed to load image: ") + ex.Message;
        }
    }

    private static string FormatFileSize(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
        return $"{bytes / (1024.0 * 1024.0):F2} MB";
    }

    private void OnImageMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2 && !string.IsNullOrEmpty(_imagePath) && File.Exists(_imagePath))
        {
            try
            {
                Process.Start(new ProcessStartInfo(_imagePath) { UseShellExecute = true });
            }
            catch
            {
            }
        }
    }
}
