using System;
using System.IO;
using System.Threading;
using Flow.Plugin.ClipHistory.Models;
using WinForms = System.Windows.Forms;

namespace Flow.Plugin.ClipHistory.Services;

public static class KeyboardSimulator
{
    public static void ClearClipboard(ClipboardWatcher? watcher = null)
    {
        watcher?.MarkInternalCopy();

        var sta = new Thread(() =>
        {
            try
            {
                WinForms.Clipboard.Clear();
            }
            catch { }
        });

        sta.SetApartmentState(ApartmentState.STA);
        sta.Start();
        sta.Join(2000);
    }

    public static void SetToClipboard(ClipItem item, ClipboardWatcher? watcher = null)
    {
        watcher?.MarkInternalCopy();

        var sta = new Thread(() =>
        {
            try
            {
                if (item.Type == ClipType.Text)
                {
                    WinForms.Clipboard.SetDataObject(item.Content, true, 10, 50);
                }
                else if (item.Type == ClipType.Image && !string.IsNullOrEmpty(item.ImagePath) && File.Exists(item.ImagePath))
                {
                    var imageBytes = File.ReadAllBytes(item.ImagePath);
                    using var imgStream = new MemoryStream(imageBytes);
                    using var image = new System.Drawing.Bitmap(imgStream);

                    var dataObj = new WinForms.DataObject();
                    dataObj.SetData(WinForms.DataFormats.Bitmap, true, image);

                    var files = new System.Collections.Specialized.StringCollection { item.ImagePath };
                    dataObj.SetFileDropList(files);

                    var pngStream = new MemoryStream(imageBytes);
                    dataObj.SetData("PNG", false, pngStream);

                    WinForms.Clipboard.SetDataObject(dataObj, true, 10, 50);
                }
            }
            catch { }
        });

        sta.SetApartmentState(ApartmentState.STA);
        sta.Start();
        sta.Join(3000);
    }
}
