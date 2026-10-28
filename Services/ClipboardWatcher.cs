using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using Flow.Plugin.ClipHistory.Models;
using Clipboard = System.Windows.Clipboard;
using WinForms = System.Windows.Forms;

namespace Flow.Plugin.ClipHistory.Services;

public class ClipboardWatcher : IDisposable
{
    private const int WM_CLIPBOARDUPDATE = 0x031D;

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AddClipboardFormatListener(IntPtr hwnd);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RemoveClipboardFormatListener(IntPtr hwnd);

    private readonly ThumbnailService _thumbService;
    private readonly SettingsService? _settingsService;
    private Thread? _staThread;
    private HwndSource? _hwndSource;
    private volatile bool _isDisposed;
    private volatile bool _isInternalCopy;

    public event Action<ClipItem>? OnClipCaptured;

    public ClipboardWatcher(ThumbnailService thumbService, SettingsService? settingsService = null)
    {
        _thumbService = thumbService;
        _settingsService = settingsService;
        StartListenerThread();
    }

    public void MarkInternalCopy()
    {
        _isInternalCopy = true;
    }

    private void StartListenerThread()
    {
        var readyEvent = new ManualResetEventSlim(false);

        _staThread = new Thread(() =>
        {
            var parameters = new HwndSourceParameters("FlowClipHistoryListener")
            {
                WindowStyle = 0,
                Width = 0,
                Height = 0,
                PositionX = 0,
                PositionY = 0,
                ParentWindow = IntPtr.Zero
            };

            _hwndSource = new HwndSource(parameters);
            _hwndSource.AddHook(WndProc);

            AddClipboardFormatListener(_hwndSource.Handle);
            readyEvent.Set();

            System.Windows.Threading.Dispatcher.Run();
        })
        {
            IsBackground = true,
            Name = "FlowClipHistoryWatcherThread"
        };

        _staThread.SetApartmentState(ApartmentState.STA);
        _staThread.Start();
        readyEvent.Wait(2000);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_CLIPBOARDUPDATE && !_isDisposed)
        {
            if (_isInternalCopy)
            {
                _isInternalCopy = false;
                return IntPtr.Zero;
            }

            ThreadPool.QueueUserWorkItem(_ => ProcessClipboardContent());
        }
        return IntPtr.Zero;
    }

    private void ProcessClipboardContent()
    {
        // 稍作延时（80ms），确保复制来源应用已完成数据写入并释放剪贴板锁
        Thread.Sleep(80);

        for (int retry = 0; retry < 5; retry++)
        {
            try
            {
                ClipItem? captured = null;
                var sta = new Thread(() =>
                {
                    try
                    {
                        var maxTextBytes = (_settingsService?.Settings.MaxContentSizeKB ?? 1024) * 1024L;
                        var dataObj = Clipboard.GetDataObject();

                        // 1. 优先检测是否包含图片数据（截图工具、浏览器复制图片、画图、聊天软件等）
                        bool hasImage = false;
                        if (dataObj != null)
                        {
                            try
                            {
                                hasImage = Clipboard.ContainsImage() ||
                                           dataObj.GetDataPresent("PNG") ||
                                           dataObj.GetDataPresent("image/png") ||
                                           dataObj.GetDataPresent(System.Windows.DataFormats.Bitmap) ||
                                           dataObj.GetDataPresent("DeviceIndependentBitmap");
                            }
                            catch { }
                        }

                        if (!hasImage)
                        {
                            try
                            {
                                hasImage = WinForms.Clipboard.ContainsImage();
                            }
                            catch { }
                        }

                        if (hasImage)
                        {
                            BitmapSource? img = null;

                            // 1.1 优先提取原始 PNG 数据流（保留透明通道）
                            try
                            {
                                if (dataObj != null && dataObj.GetDataPresent("PNG"))
                                {
                                    var rawPng = dataObj.GetData("PNG");
                                    if (rawPng is MemoryStream ms)
                                    {
                                        var decoder = BitmapDecoder.Create(ms, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
                                        if (decoder.Frames.Count > 0) img = decoder.Frames[0];
                                    }
                                    else if (rawPng is Stream stream)
                                    {
                                        var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
                                        if (decoder.Frames.Count > 0) img = decoder.Frames[0];
                                    }
                                }
                            }
                            catch { }

                            // 1.2 回退到 WPF Clipboard.GetImage()
                            if (img == null)
                            {
                                try
                                {
                                    img = Clipboard.GetImage();
                                }
                                catch { }
                            }

                            // 1.3 回退到 WinForms.Clipboard.GetImage()（兼容更多第三方应用的 DIB/位图格式）
                            if (img == null)
                            {
                                try
                                {
                                    using var winFormsImage = WinForms.Clipboard.GetImage();
                                    if (winFormsImage != null)
                                    {
                                        using var ms = new MemoryStream();
                                        winFormsImage.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
                                        ms.Position = 0;
                                        var decoder = BitmapDecoder.Create(ms, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
                                        if (decoder.Frames.Count > 0) img = decoder.Frames[0];
                                    }
                                }
                                catch { }
                            }

                            if (img != null)
                            {
                                // 放宽图片内存限制：最大单边 10000 像素，未压缩像素上限 80MB (足以支持 4K/5K/8K 截图)
                                long estimatedBytes = (long)img.PixelWidth * img.PixelHeight * 4;
                                const long maxImageMemoryBytes = 80 * 1024 * 1024L;

                                if (img.PixelWidth > 0 && img.PixelHeight > 0 && estimatedBytes <= maxImageMemoryBytes)
                                {
                                    captured = ProcessImage(img);
                                }
                            }
                        }
                        // 2. 检测复制单张图片文件 (文件资源管理器中 Ctrl+C 复制图片文件)
                        else if (dataObj != null && dataObj.GetDataPresent(System.Windows.DataFormats.FileDrop))
                        {
                            try
                            {
                                var files = Clipboard.GetFileDropList();
                                if (files.Count == 1)
                                {
                                    var filePath = files[0];
                                    if (File.Exists(filePath))
                                    {
                                        var ext = Path.GetExtension(filePath).ToLowerInvariant();
                                        if (ext is ".png" or ".jpg" or ".jpeg" or ".bmp" or ".webp" or ".gif")
                                        {
                                            var fi = new FileInfo(filePath);
                                            if (fi.Length <= 40 * 1024 * 1024L)
                                            {
                                                var rawBytes = File.ReadAllBytes(filePath);
                                                using var ms = new MemoryStream(rawBytes);
                                                var decoder = BitmapDecoder.Create(ms, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
                                                if (decoder.Frames.Count > 0)
                                                {
                                                    captured = ProcessImage(decoder.Frames[0]);
                                                }
                                            }
                                        }
                                    }
                                }
                            }
                            catch { }
                        }

                        // 3. 文本内容捕获
                        if (captured == null && Clipboard.ContainsText())
                        {
                            var text = Clipboard.GetText();
                            if (!string.IsNullOrWhiteSpace(text))
                            {
                                var byteCount = Encoding.UTF8.GetByteCount(text);
                                if (byteCount <= maxTextBytes)
                                {
                                    captured = ProcessText(text);
                                }
                            }
                        }
                    }
                    catch { }
                });

                sta.SetApartmentState(ApartmentState.STA);
                sta.Start();
                sta.Join(1500);

                if (captured != null)
                {
                    OnClipCaptured?.Invoke(captured);
                    break;
                }
            }
            catch { }

            Thread.Sleep(100);
        }
    }

    private ClipItem ProcessText(string text)
    {
        string hash;
        using (var sha = SHA256.Create())
        {
            var bytes = Encoding.UTF8.GetBytes(text);
            var hashBytes = sha.ComputeHash(bytes);
            hash = BitConverter.ToString(hashBytes).Replace("-", "").ToLowerInvariant();
        }

        return new ClipItem
        {
            Type = ClipType.Text,
            Content = text,
            SearchText = text.ToLowerInvariant(),
            Hash = hash,
            CharCount = text.Length,
            CreatedAt = DateTime.Now,
            UpdatedAt = DateTime.Now
        };
    }

    private ClipItem ProcessImage(BitmapSource img)
    {
        var (rawPath, thumbPath, hash, width, height) = _thumbService.ProcessAndSaveImage(img);

        return new ClipItem
        {
            Type = ClipType.Image,
            Content = $"{width} × {height}",
            SearchText = $"[image] [图片] {width}x{height} {width}×{height}".ToLowerInvariant(),
            Hash = hash,
            ImagePath = rawPath,
            ThumbnailPath = thumbPath,
            ImageWidth = width,
            ImageHeight = height,
            CreatedAt = DateTime.Now,
            UpdatedAt = DateTime.Now
        };
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;

        if (_hwndSource != null)
        {
            try
            {
                var disp = _hwndSource.Dispatcher;
                if (disp != null && !disp.HasShutdownStarted && !disp.HasShutdownFinished)
                {
                    disp.Invoke(() =>
                    {
                        try
                        {
                            RemoveClipboardFormatListener(_hwndSource.Handle);
                            _hwndSource.RemoveHook(WndProc);
                            _hwndSource.Dispose();
                        }
                        catch { }
                        disp.InvokeShutdown();
                    });
                }
            }
            catch { }
        }
    }
}
