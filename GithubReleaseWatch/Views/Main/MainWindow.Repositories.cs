using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using System.Diagnostics;

namespace GithubReleaseWatch;

public partial class MainWindow
{
    private bool SaveConfig()
    {
        if (ConfigStore.Save(_config)) return true;
        TxtStatus.Text = ConfigStore.LastError;
        return false;
    }

    private RepoViewModel? FindRepository(string owner, string repo, RepoViewModel? exclude = null) =>
        Repos.FirstOrDefault(vm => !ReferenceEquals(vm, exclude) && new GitHubRepository(owner, repo).Matches(vm.Config));

    private void RemoveRepository(RepoViewModel vm)
    {
        if (MessageBox.Show(this, $"确定移除 {vm.DisplayName} 吗？", "确认移除",
            MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
        Repos.Remove(vm);
        _refreshRequests.Remove(vm);
        _config.Repos.Remove(vm.Config);
        SaveConfig();
    }

    private async void BtnAdd_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new AddRepoWindow { Owner = this };
        if (dialog.ShowDialog() != true) return;

        // 查重：owner+repo 完全相同的视为重复（忽略大小写）
        string owner = dialog.OwnerName, repo = dialog.RepoName;
        var duplicate = FindRepository(owner, repo);
        if (duplicate != null)
        {
            MessageBox.Show(this,
                $"仓库 {owner}/{repo} 已在监测列表中。\n\n请勿重复添加，如需刷新可直接右键该卡片选择「🔄 刷新此仓库」。",
                "重复添加", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var config = new RepoConfig
        {
            Url = dialog.RepoUrl,
            Owner = owner,
            Repo = repo,
            CurrentVersion = dialog.CurrentVersion
        };
        var vm = new RepoViewModel(config, () => _config.ShowPrerelease);
        Repos.Add(vm);
        _config.Repos.Add(config);
        SaveConfig();

        await RefreshRepoAsync(vm);

        // 未填写当前版本时，默认取最新版本
        if (string.IsNullOrEmpty(config.CurrentVersion) && vm.LatestVisible != null)
        {
            vm.SetCurrent(vm.LatestVisible.TagName);
            SaveConfig();
        }
    }

    private void BtnRemove_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedRepo is not { } vm)
        {
            MessageBox.Show(this, "请先在左侧选择要移除的仓库。", "提示",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        RemoveRepository(vm);
    }

    private void RepoList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // 拖拽排序过程中 ListBox 选中项会不断变化，跳过右侧渲染避免卡顿
        if (_dragReorder?.IsDragging == true) return;

        StopSmoothScroll();
        RightScrollViewer.ScrollToVerticalOffset(0);
        var vm = SelectedRepo;
        DetailsContent.DataContext = vm;
        DetailsContent.Visibility = vm is null ? Visibility.Collapsed : Visibility.Visible;
        Placeholder.Visibility = vm is null ? Visibility.Visible : Visibility.Collapsed;
        SyncMasterToggleState();


    }

    private void ChkPrerelease_Changed(object sender, RoutedEventArgs e)
    {
        if (_initializing) return;
        _config.ShowPrerelease = ChkPrerelease.IsChecked == true;
        SaveConfig();
        foreach (var vm in Repos) vm.OnFilterChanged();
        StopSmoothScroll();
        SyncMasterToggleState();
    }

    // ===== 拖拽排序：把容器事件转发给 helper，保持选中、避免误触发 =====

    private void RepoListItem_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        => _dragReorder?.OnItemMouseDown(sender, e);

    /// <summary>
    /// 右键按下时先选中该卡片，让右键菜单的操作对象与选中项保持一致（与资源管理器行为相同）。
    /// 不影响左键点击切换；拖拽排序只监听左键（OnItemMouseDown 里过滤了 ChangedButton），互不干扰。
    /// </summary>
    private void RepoListItem_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is ListBoxItem { IsSelected: false } item)
            item.IsSelected = true;
    }

    /// <summary>拖拽排序后：让 _config.Repos 与 Repos 顺序保持一致并写盘。</summary>
    private void PersistOrder()
    {
        _config.Repos.Clear();
        foreach (var vm in Repos) _config.Repos.Add(vm.Config);
        SaveConfig();
    }

    // ===== 右键菜单（每个仓库卡片） =====

    private void CtxRefreshRepo_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { DataContext: RepoViewModel vm })
            _ = RefreshRepoAsync(vm);
    }

    /// <summary>右键菜单"完成：升级到最新"：把该仓库当前版本提升到最新版本。</summary>
    private void CtxComplete_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { DataContext: RepoViewModel vm })
            CompleteUpgrade(vm);
    }

    private void CtxOpenGitHub_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { DataContext: RepoViewModel vm })
            ExternalLinks.Open(this, vm.Config.Url);
    }

    /// <summary>
    /// 右键菜单"编辑仓库"：打开与「添加仓库」同样式的编辑面板，保存后**原地**更新数据：
    /// - 不重建 RepoViewModel、不改动 Repos 集合顺序 → 拖拽排序结果、选中项保持不变
    /// - 只改版本号时不发网络请求，已有版本列表原样保留
    /// - 地址变化时自动重新拉取该仓库的 Release 列表
    /// - 右侧版本卡片的展开/折叠状态与滚动位置在数据刷新后自动恢复
    /// </summary>
    private async void CtxEditRepo_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { DataContext: RepoViewModel vm }) return;

        var dialog = new EditRepoWindow(vm.Config.Url, vm.Config.CurrentVersion ?? "") { Owner = this };
        if (dialog.ShowDialog() != true) return;

        bool urlChanged =
            !string.Equals(dialog.OwnerName, vm.Config.Owner, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(dialog.RepoName, vm.Config.Repo, StringComparison.OrdinalIgnoreCase);
        bool versionChanged =
            !string.Equals(dialog.CurrentVersion, vm.Config.CurrentVersion ?? "", StringComparison.Ordinal);

        // 改成了列表里已有的其他仓库 → 拒绝（忽略大小写；与自己相同不算重复）
        if (urlChanged)
        {
            var duplicate = FindRepository(dialog.OwnerName, dialog.RepoName, vm);
            if (duplicate != null)
            {
                MessageBox.Show(this,
                    $"仓库 {dialog.OwnerName}/{dialog.RepoName} 已在监测列表中，无法改为重复地址。",
                    "重复地址", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
        }

        if (!urlChanged && !versionChanged)
        {
            TxtStatus.Text = $"ℹ️ {vm.DisplayName} 未做任何更改";
            return;
        }

        // 只改版本号时保留滚动位置；展开状态由 ReleaseInfo 保存。
        double scrollOffset = RightScrollViewer?.VerticalOffset ?? 0;

        if (urlChanged)
        {
            var oldName = vm.DisplayName;
            vm.Config.Url = dialog.RepoUrl;
            vm.Config.Owner = dialog.OwnerName;
            vm.Config.Repo = dialog.RepoName;
            vm.Config.CurrentVersion = dialog.CurrentVersion;
            vm.SetReleases(new());
            vm.OnConfigChanged();          // 左侧卡片名 + 右侧标题即时更新
            SaveConfig();

            TxtStatus.Text = $"✏️ {oldName} 已改为 {vm.DisplayName}，正在刷新…";
            await RefreshRepoAsync(vm);
            TxtStatus.Text = string.IsNullOrEmpty(vm.Error)
                ? $"✏️ 已更新为 {vm.DisplayName}"
                : $"⚠ {vm.DisplayName} 地址已更新，但刷新失败：{vm.Error}";
        }
        else
        {
            // 只改版本号：不发网络请求，左侧徽章与右侧信息即时更新
            vm.SetCurrent(dialog.CurrentVersion);
            SaveConfig();
            TxtStatus.Text = string.IsNullOrEmpty(dialog.CurrentVersion)
                ? $"✏️ {vm.DisplayName} 已清除当前版本"
                : $"✏️ {vm.DisplayName} 当前版本已改为 {dialog.CurrentVersion}";
        }

        if (!urlChanged && ReferenceEquals(vm, SelectedRepo) && RightScrollViewer != null && scrollOffset > 0)
        {
            _ = Dispatcher.BeginInvoke(() =>
                { if (ReferenceEquals(vm, SelectedRepo)) RightScrollViewer.ScrollToVerticalOffset(scrollOffset); }, DispatcherPriority.Loaded);
        }
    }

    private void CtxRemoveRepo_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { DataContext: RepoViewModel vm }) return;
        RemoveRepository(vm);
    }

}
