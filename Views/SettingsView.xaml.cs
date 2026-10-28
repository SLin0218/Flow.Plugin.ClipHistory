using System;
using System.Windows;
using System.Windows.Controls;
using Flow.Plugin.ClipHistory.Services;

namespace Flow.Plugin.ClipHistory.Views;

public partial class SettingsView : UserControl
{
    private readonly SettingsService _settingsService;
    private readonly Action<bool> _clearHistoryAction;
    private bool _isInitializing = true;

    public SettingsView(SettingsService settingsService, Action<bool> clearHistoryAction)
    {
        InitializeComponent();
        _settingsService = settingsService;
        _clearHistoryAction = clearHistoryAction;

        ApplyLocalization();
        LoadSettings();
        _isInitializing = false;
    }

    private void ApplyLocalization()
    {
        TbMaxHistoryLabel.Text = I18n.T("历史记录最大保存条数:", "Max History Records:");
        TbMaxHistoryUnit.Text = I18n.T("条 (默认 500)", "items (default: 500)");

        TbMaxSizeLabel.Text = I18n.T("单条内容捕获上限:", "Max Content Size Limit:");
        TbMaxSizeUnit.Text = I18n.T("KB (默认 1024 KB)", "KB (default: 1024 KB)");

        TbMaintenanceHeader.Text = I18n.T("快速清理历史", "Data Maintenance");
        BtnClearUnpinned.Content = I18n.T("清除所有（保留固定）", "Clear All (Keep Pinned)");
        BtnClearAll.Content = I18n.T("清除所有", "Clear All");
    }

    private void LoadSettings()
    {
        var s = _settingsService.Settings;
        TbMaxHistoryCount.Text = s.MaxHistoryCount.ToString();
        TbMaxContentSizeKB.Text = s.MaxContentSizeKB.ToString();
    }

    private void OnMaxHistoryCountChanged(object sender, TextChangedEventArgs e)
    {
        if (_isInitializing) return;

        if (int.TryParse(TbMaxHistoryCount.Text.Trim(), out int val) && val >= 50 && val <= 10000)
        {
            _settingsService.Update(s => s.MaxHistoryCount = val);
        }
    }

    private void OnMaxContentSizeKBChanged(object sender, TextChangedEventArgs e)
    {
        if (_isInitializing) return;

        if (int.TryParse(TbMaxContentSizeKB.Text.Trim(), out int val) && val >= 64 && val <= 51200)
        {
            _settingsService.Update(s => s.MaxContentSizeKB = val);
        }
    }

    private void BtnClearUnpinned_Click(object sender, RoutedEventArgs e)
    {
        _clearHistoryAction(true);
        TbStatus.Text = I18n.T("已清除所有（保留固定）！", "Cleared all (kept pinned)!");
    }

    private void BtnClearAll_Click(object sender, RoutedEventArgs e)
    {
        _clearHistoryAction(false);
        TbStatus.Text = I18n.T("已清除所有！", "All history cleared!");
    }
}
