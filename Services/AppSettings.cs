namespace Flow.Plugin.ClipHistory.Services;

public class AppSettings
{
    /// <summary>
    /// 最大保存条目数（平衡性能与历史留存，默认 500 条）
    /// </summary>
    public int MaxHistoryCount { get; set; } = 500;

    /// <summary>
    /// 单条内容大小上限（KB），平衡内存占用与高吞吐，默认 1024 KB (1 MB)
    /// </summary>
    public int MaxContentSizeKB { get; set; } = 1024;
}
