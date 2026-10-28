using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows.Controls;
using Flow.Launcher.Plugin;
using Flow.Launcher.Plugin.SharedModels;
using Flow.Plugin.ClipHistory.Models;
using Flow.Plugin.ClipHistory.Services;
using Flow.Plugin.ClipHistory.Views;

namespace Flow.Plugin.ClipHistory;

public class Main : IPlugin, IContextMenu, ISettingProvider, ISavable, IDisposable
{
    public const string PluginIcon = "Images/icon.svg";
    public const string TextIcon = "Images/poll-h.svg";
    public const string ImageIcon = "Images/picture.svg";
    public const string PasteIcon = "Images/paste.svg";
    public const string PinIcon = "Images/pin.svg";
    public const string TrashIcon = "Images/trash.svg";

    private PluginInitContext _context = null!;
    private SettingsService _settingsService = null!;
    private ThumbnailService _thumbService = null!;
    private DatabaseService _db = null!;
    private ClipboardWatcher _watcher = null!;
    private SettingsView? _settingsView;

    private readonly List<ClipItem> _cache = new();
    private readonly object _lock = new();

    public void Init(PluginInitContext context)
    {
        _context = context;

        var baseDataDir = context.CurrentPluginMetadata.PluginSettingsDirectoryPath;
        if (string.IsNullOrWhiteSpace(baseDataDir))
        {
            baseDataDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "FlowLauncher",
                "Settings",
                "Plugins",
                "Flow.Plugin.ClipHistory");
        }

        if (!Directory.Exists(baseDataDir))
        {
            Directory.CreateDirectory(baseDataDir);
        }

        _settingsService = new SettingsService(baseDataDir);
        _thumbService = new ThumbnailService(baseDataDir);
        _db = new DatabaseService(Path.Combine(baseDataDir, "clip_history.db"));

        lock (_lock)
        {
            _cache.AddRange(_db.LoadRecent(_settingsService.Settings.MaxHistoryCount));
            SortCache();
        }

        _watcher = new ClipboardWatcher(_thumbService, _settingsService);
        _watcher.OnClipCaptured += OnClipCaptured;
        _settingsService.OnSettingsChanged += OnSettingsChanged;
    }

    private void SortCache()
    {
        _cache.Sort((a, b) =>
        {
            int pinComp = b.IsPinned.CompareTo(a.IsPinned);
            if (pinComp != 0) return pinComp;
            return b.UpdatedAt.CompareTo(a.UpdatedAt);
        });
    }

    private void OnClipCaptured(ClipItem item)
    {
        var maxLimit = _settingsService.Settings.MaxHistoryCount;
        lock (_lock)
        {
            _db.Upsert(item, maxLimit);

            var existingIndex = _cache.FindIndex(c => c.Hash == item.Hash);
            if (existingIndex >= 0)
            {
                var existing = _cache[existingIndex];
                existing.UpdatedAt = DateTime.Now;
                if (!existing.IsPinned)
                {
                    _cache.RemoveAt(existingIndex);
                    var firstUnpinned = _cache.FindIndex(c => !c.IsPinned);
                    if (firstUnpinned >= 0) _cache.Insert(firstUnpinned, existing);
                    else _cache.Add(existing);
                }
            }
            else
            {
                var firstUnpinned = _cache.FindIndex(c => !c.IsPinned);
                if (firstUnpinned >= 0) _cache.Insert(firstUnpinned, item);
                else _cache.Insert(0, item);
            }

            if (_cache.Count > maxLimit)
            {
                var lastUnpinned = _cache.FindLastIndex(c => !c.IsPinned);
                if (lastUnpinned >= 0) _cache.RemoveAt(lastUnpinned);
            }
        }
    }

    private void OnSettingsChanged(AppSettings settings)
    {
        lock (_lock)
        {
            var maxLimit = settings.MaxHistoryCount;
            while (_cache.Count > maxLimit)
            {
                var lastUnpinned = _cache.FindLastIndex(c => !c.IsPinned);
                if (lastUnpinned >= 0) _cache.RemoveAt(lastUnpinned);
                else break;
            }
        }
    }

    public List<Result> Query(Query query)
    {
        var rawSearch = query.Search?.Trim() ?? string.Empty;
        var searchLower = rawSearch.ToLowerInvariant();
        var results = new List<Result>();

        // 1. 设置快捷入口
        if (searchLower == "set" || searchLower == "config" || searchLower == "设置" || searchLower == "配置")
        {
            results.Add(new Result
            {
                Title = I18n.T("打开 ClipHistory 扩展设置", "Open ClipHistory Settings"),
                SubTitle = I18n.T("配置最大保存条数与单条大小限制", "Configure max history count and content size limits"),
                IcoPath = PluginIcon,
                Score = 5000,
                Action = _ =>
                {
                    _context.API.OpenSettingDialog();
                    return true;
                }
            });
        }

        // 2. 清理快捷操作
        if (searchLower == "clear" || searchLower == "clean" || searchLower == "清空" || searchLower == "清除")
        {
            int unpinnedCount;
            int totalCount;
            lock (_lock)
            {
                unpinnedCount = _cache.Count(c => !c.IsPinned);
                totalCount = _cache.Count;
            }

            var actionKeyword = string.IsNullOrEmpty(query.ActionKeyword) ? "ch" : query.ActionKeyword;

            results.Add(new Result
            {
                Title = I18n.T("清除所有（保留固定）", "Clear All (Keep Pinned)"),
                SubTitle = string.Format(I18n.T("立即清理常规未固定历史 (当前共 {0} 条)", "Clear unpinned history ({0} items)"), unpinnedCount),
                IcoPath = TrashIcon,
                Score = 4000,
                Action = _ =>
                {
                    ClearHistory(preservePinned: true);
                    _context.API.ChangeQuery($"{actionKeyword} ", true);
                    return false;
                }
            });

            results.Add(new Result
            {
                Title = I18n.T("清除所有", "Clear All"),
                SubTitle = string.Format(I18n.T("清空所有历史记录，包含固定项 (当前共 {0} 条)", "Clear all history including pinned ({0} items)"), totalCount),
                IcoPath = TrashIcon,
                Score = 3900,
                Action = _ =>
                {
                    ClearHistory(preservePinned: false);
                    _context.API.ChangeQuery($"{actionKeyword} ", true);
                    return false;
                }
            });
        }

        // 3. 过滤或展示剪贴板条目（采用 Flow Launcher 官方 API 进行模糊匹配与拼音检索）
        List<Result> itemResults = new();
        lock (_lock)
        {
            if (string.IsNullOrWhiteSpace(rawSearch))
            {
                int baseScore = 2000;
                foreach (var item in _cache.Take(80))
                {
                    int score = item.IsPinned ? baseScore + 3000 : baseScore;
                    itemResults.Add(CreateResultFromItem(item, score, null));
                    baseScore = Math.Max(100, baseScore - 5);
                }
            }
            else
            {
                foreach (var item in _cache)
                {
                    MatchResult match;
                    string titleText;

                    if (item.Type == ClipType.Image)
                    {
                        titleText = $"{I18n.T("图片", "Image")} · {item.ImageWidth} × {item.ImageHeight}";
                        var searchTarget = $"{item.ImageWidth}x{item.ImageHeight} {item.Content} 图片 image";
                        match = _context.API.FuzzySearch(rawSearch, searchTarget);
                    }
                    else
                    {
                        var singleLine = item.Content.Trim().Replace("\r\n", " ").Replace("\n", " ");
                        titleText = singleLine.Length > 90 ? singleLine.Substring(0, 90) + "..." : singleLine;

                        // 优先对列表标题进行模糊/拼音检索（确保高亮索引精准对应 UI）
                        match = _context.API.FuzzySearch(rawSearch, titleText);

                        // 若标题未达到精度要求，但在前 500 字符内能够命中，则扩大范围
                        if (!match.IsSearchPrecisionScoreMet() && singleLine.Length > titleText.Length)
                        {
                            var sample = singleLine.Length > 500 ? singleLine.Substring(0, 500) : singleLine;
                            var contentMatch = _context.API.FuzzySearch(rawSearch, sample);
                            if (contentMatch.IsSearchPrecisionScoreMet())
                            {
                                match = contentMatch;
                            }
                        }
                    }

                    if (match.IsSearchPrecisionScoreMet())
                    {
                        int finalScore = match.Score + (item.IsPinned ? 3000 : 0);
                        var highlight = match.MatchData?.Where(idx => idx < titleText.Length).ToList();
                        itemResults.Add(CreateResultFromItem(item, finalScore, highlight));
                    }
                }

                itemResults.Sort((a, b) => b.Score.CompareTo(a.Score));
                if (itemResults.Count > 80)
                {
                    itemResults = itemResults.Take(80).ToList();
                }
            }
        }

        results.AddRange(itemResults);

        if (results.Count == 0)
        {
            results.Add(new Result
            {
                Title = string.IsNullOrEmpty(rawSearch)
                    ? I18n.T("剪贴板历史为空", "Clipboard history is empty")
                    : string.Format(I18n.T("未找到与 \"{0}\" 匹配的剪贴板历史", "No records matching \"{0}\""), rawSearch),
                SubTitle = string.IsNullOrEmpty(rawSearch)
                    ? I18n.T("复制任意文本或图片后即可在此快速检索", "Copy any text or image to search here")
                    : I18n.T("请尝试更换关键词搜索", "Try different search keywords"),
                IcoPath = PluginIcon,
                Score = 0
            });
        }

        return results;
    }

    private Result CreateResultFromItem(ClipItem item, int score, List<int>? highlightData = null)
    {
        string title;
        string icoPath;

        if (item.Type == ClipType.Image)
        {
            title = $"{I18n.T("图片", "Image")} · {item.ImageWidth} × {item.ImageHeight}";
            icoPath = item.IsPinned ? PinIcon : ImageIcon;
        }
        else
        {
            var singleLine = item.Content.Trim().Replace("\r\n", " ").Replace("\n", " ");
            title = singleLine.Length > 90 ? singleLine.Substring(0, 90) + "..." : singleLine;
            icoPath = item.IsPinned ? PinIcon : TextIcon;
        }

        var res = new Result
        {
            Title = title,
            TitleHighlightData = highlightData,
            SubTitle = item.GetSubtitle(),
            IcoPath = icoPath,
            CopyText = item.Type == ClipType.Image ? item.ImagePath ?? string.Empty : item.Content,
            Score = score,
            ContextData = item,
            Action = _ =>
            {
                KeyboardSimulator.SetToClipboard(item, _watcher);
                return true;
            }
        };

        if (item.Type == ClipType.Image && !string.IsNullOrEmpty(item.ImagePath) && File.Exists(item.ImagePath))
        {
            res.Preview = new Result.PreviewInfo
            {
                IsMedia = true,
                PreviewImagePath = item.ImagePath,
                FilePath = item.ImagePath,
                Description = $"{I18n.T("分辨率", "Resolution")}: {item.ImageWidth} × {item.ImageHeight}\n" +
                              $"{I18n.T("时间", "Time")}: {item.CreatedAt:yyyy-MM-dd HH:mm:ss}\n" +
                              $"{I18n.T("文件", "File")}: {item.ImagePath}"
            };
            res.PreviewPanel = new Lazy<UserControl>(() => new ImagePreviewControl(item));
        }
        else
        {
            res.Preview = new Result.PreviewInfo
            {
                Description = item.Content
            };
            res.PreviewPanel = new Lazy<UserControl>(() => new TextPreviewControl(item));
        }

        return res;
    }

    public List<Result> LoadContextMenus(Result selectedResult)
    {
        var menu = new List<Result>();
        if (selectedResult.ContextData is not ClipItem item)
        {
            return menu;
        }

        // 1. 复制
        menu.Add(new Result
        {
            Title = I18n.T("复制", "Copy"),
            SubTitle = I18n.T("复制到剪贴板", "Copy to clipboard"),
            IcoPath = PasteIcon,
            Action = _ =>
            {
                KeyboardSimulator.SetToClipboard(item, _watcher);
                return true;
            }
        });

        // 2. 固定到顶部 / 取消固定
        menu.Add(new Result
        {
            Title = item.IsPinned ? I18n.T("取消固定", "Unpin from Top") : I18n.T("固定到顶部", "Pin to Top"),
            SubTitle = item.IsPinned 
                ? I18n.T("取消此记录的置顶状态", "Unpin this item from top") 
                : I18n.T("置顶到剪贴板历史顶部，清空常规历史时受保护", "Pin to top of history (preserved on clear)"),
            IcoPath = PinIcon,
            Action = _ =>
            {
                item.IsPinned = !item.IsPinned;
                _db.TogglePin(item.Id, item.IsPinned);
                lock (_lock)
                {
                    SortCache();
                }
                _context.API.ReQuery(false);
                return false;
            }
        });

        // 3. 删除
        menu.Add(new Result
        {
            Title = I18n.T("删除", "Delete"),
            SubTitle = I18n.T("从历史记录中永久删除此项", "Permanently remove this item from history"),
            IcoPath = TrashIcon,
            Action = _ =>
            {
                _thumbService.DeleteImageFiles(item.ImagePath, item.ThumbnailPath);
                _db.Delete(item.Id);
                lock (_lock)
                {
                    _cache.RemoveAll(c => c.Id == item.Id);
                }
                _context.API.ReQuery(false);
                return false;
            }
        });

        return menu;
    }

    public void ClearHistory(bool preservePinned = true)
    {
        _db.ClearAll(preservePinned);
        lock (_lock)
        {
            if (preservePinned)
            {
                _cache.RemoveAll(c => !c.IsPinned);
            }
            else
            {
                _cache.Clear();
            }
        }
    }

    public Control CreateSettingPanel()
    {
        return _settingsView ??= new SettingsView(_settingsService, ClearHistory);
    }

    public void Save()
    {
        _settingsService?.Save();
    }

    public void Dispose()
    {
        _watcher?.Dispose();
        _db?.Dispose();
    }
}
