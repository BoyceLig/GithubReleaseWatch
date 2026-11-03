using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using System.Diagnostics;

namespace GithubReleaseWatch;

public partial class MainWindow
{
    /// <summary>"完成"（右侧按钮）：当前版本提升到最新版本。</summary>
    private void BtnComplete_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedRepo is { } vm) CompleteUpgrade(vm);
    }

    /// <summary>
    /// 把指定仓库的当前版本提升到最新版本并落盘。
    /// 右侧"完成"按钮与左侧列表右键菜单共用这一份逻辑。
    /// </summary>
    private void CompleteUpgrade(RepoViewModel vm)
    {
        if (vm.LatestVisible is not { } latest)
        {
            MessageBox.Show(this, $"{vm.DisplayName} 没有可用版本（可能全部被 Pre-release 过滤）。", "提示",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        vm.SetCurrent(latest.TagName);
        if (!SaveConfig()) return;
        TxtStatus.Text = $"✅ {vm.DisplayName} 当前版本已更新为 {latest.TagName}";
    }

    /// <summary>把某个具体版本设为当前版本。</summary>
    private void BtnMarkRelease_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: ReleaseInfo release } && SelectedRepo is { } vm)
        {
            vm.SetCurrent(release.TagName);
            SaveConfig();
        }
    }

    private async void BtnCopyMarkdown_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: ReleaseInfo release }) return;

        // 防抖：250ms 内忽略重复点击，避免连续触发造成 UI 卡顿。
        var now = DateTime.Now;
        if (now - _lastCopyClick < TimeSpan.FromMilliseconds(250))
            return;
        _lastCopyClick = now;

        var text = release.Body ?? "";
        TxtStatus.Text = $"📋 正在复制 {release.TagName}…";

        const int maxRetries = 5;
        for (int i = 0; i < maxRetries; i++)
        {
            try
            {
                if (text.Length == 0) System.Windows.Clipboard.Clear();
                else System.Windows.Clipboard.SetText(text);
                TxtStatus.Text = $"📋 已复制 {release.TagName} 的 Markdown 原文";
                return;
            }
            catch
            {
                // 剪贴板被其他进程占用时异步重试，不阻塞 UI 线程
                if (i < maxRetries - 1)
                    await Task.Delay(50);
                if (_closed) return;
            }
        }

        TxtStatus.Text = $"⚠ 复制 {release.TagName} 失败，剪贴板正忙";
    }

    private void BtnOpenGitHub_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedRepo is { } vm)
            ExternalLinks.Open(this, vm.Config.Url);
    }

    // 展开状态属于版本数据，不依赖控件是否已生成。
    private void BtnToggleVersionBody_Click(object sender, RoutedEventArgs e) => SyncMasterToggleState();

    private void SyncMasterToggleState()
    {
        if (BtnToggleAllVersions == null) return;
        var releases = SelectedRepo?.VisibleReleases.ToList() ?? new();
        _syncingMasterToggle = true;
        try
        {
            bool expanded = releases.Count > 0 && releases.All(r => r.IsExpanded);
            BtnToggleAllVersions.IsChecked = expanded;
            BtnToggleAllVersions.Content = expanded ? "▶ 全部折叠" : "▼ 全部展开";
        }
        finally { _syncingMasterToggle = false; }
    }

    private void BtnToggleAllVersions_Checked(object sender, RoutedEventArgs e) => SetAllVersionsExpanded(true);
    private void BtnToggleAllVersions_Unchecked(object sender, RoutedEventArgs e) => SetAllVersionsExpanded(false);

    private void SetAllVersionsExpanded(bool expanded)
    {
        if (_syncingMasterToggle) return;
        if (SelectedRepo is { } vm)
            foreach (var release in vm.VisibleReleases) release.IsExpanded = expanded;
        SyncMasterToggleState();
    }

    /// <summary>正在同步「全部展开/折叠」主按钮状态时，避免递归触发 SetAllVersionsExpanded。</summary>
    private bool _syncingMasterToggle;
    /// <summary>复制 Markdown 按钮最近一次点击时间，用于防抖避免连续点击卡顿。</summary>
    private DateTime _lastCopyClick;

}
