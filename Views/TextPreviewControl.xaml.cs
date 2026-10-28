using System;
using System.Windows.Controls;
using Flow.Plugin.ClipHistory.Models;
using Flow.Plugin.ClipHistory.Services;

namespace Flow.Plugin.ClipHistory.Views;

public partial class TextPreviewControl : UserControl
{
    public TextPreviewControl(ClipItem item)
    {
        InitializeComponent();
        LoadContent(item);
    }

    private void LoadContent(ClipItem item)
    {
        TxtContent.Text = item.Content;

        var lines = item.Content.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None).Length;
        TxtInfo.Text = $"{item.CharCount} {I18n.T("字", "chars")}  ·  {lines} {I18n.T("行", "lines")}  ·  {item.CreatedAt:yyyy-MM-dd HH:mm:ss}";
    }
}
