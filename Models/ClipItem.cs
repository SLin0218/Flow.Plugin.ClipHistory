using System;
using Flow.Plugin.ClipHistory.Services;

namespace Flow.Plugin.ClipHistory.Models;

public class ClipItem
{
    public long Id { get; set; }
    public ClipType Type { get; set; }

    /// <summary>
    /// 文本内容（若是图片，则为图片元数据信息如 "1920x1080"）
    /// </summary>
    public string Content { get; set; } = string.Empty;

    /// <summary>
    /// 用于极速检索的小写文本
    /// </summary>
    public string SearchText { get; set; } = string.Empty;

    /// <summary>
    /// 内容唯一指纹，用于连续复制去重
    /// </summary>
    public string Hash { get; set; } = string.Empty;

    /// <summary>
    /// 图片原始大图文件路径（仅图片有效）
    /// </summary>
    public string? ImagePath { get; set; }

    /// <summary>
    /// 图片轻量缩略图路径（供 Flow Launcher IcoPath 直接显示）
    /// </summary>
    public string? ThumbnailPath { get; set; }

    public int ImageWidth { get; set; }
    public int ImageHeight { get; set; }
    public int CharCount { get; set; }

    public bool IsPinned { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.Now;
    public DateTime UpdatedAt { get; set; } = DateTime.Now;

    /// <summary>
    /// 获取友好的时间显示
    /// </summary>
    public string GetFriendlyTime()
    {
        var diff = DateTime.Now - UpdatedAt;
        if (diff.TotalSeconds < 60) return I18n.T("刚刚", "just now");
        if (diff.TotalMinutes < 60) return string.Format(I18n.T("{0} 分钟前", "{0}m ago"), (int)diff.TotalMinutes);
        if (diff.TotalHours < 24) return string.Format(I18n.T("{0} 小时前", "{0}h ago"), (int)diff.TotalHours);
        if (diff.TotalDays < 7) return string.Format(I18n.T("{0} 天前", "{0}d ago"), (int)diff.TotalDays);
        return UpdatedAt.ToString("yyyy-MM-dd HH:mm");
    }

    /// <summary>
    /// 获取友好的列表副标题
    /// </summary>
    public string GetSubtitle()
    {
        var pinnedPrefix = IsPinned ? I18n.T("[置顶] ", "[Pinned] ") : "";
        if (Type == ClipType.Image)
        {
            return $"{pinnedPrefix}{ImageWidth} × {ImageHeight} · {GetFriendlyTime()}";
        }

        var lines = Content.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None).Length;
        return $"{pinnedPrefix}{CharCount} {I18n.T("字", "chars")} ({lines} {I18n.T("行", "lines")}) · {GetFriendlyTime()}";
    }
}
