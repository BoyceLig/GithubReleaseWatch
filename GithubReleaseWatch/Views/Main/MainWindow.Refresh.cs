using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using System.Diagnostics;

namespace GithubReleaseWatch;

public partial class MainWindow
{
    private readonly Dictionary<RepoViewModel, long> _refreshRequests = new();
    private long _refreshSequence;

    private async Task RefreshAllAsync()
    {
        var snapshot = Repos.ToArray();
        if (snapshot.Length == 0 || _closed) return;
        TxtStatus.Text = "刷新中…";
        await Task.WhenAll(snapshot.Select(RefreshRepoAsync));
        if (!_closed) TxtStatus.Text = $"已刷新 {snapshot.Length} 个仓库 · {DateTime.Now:HH:mm:ss}";
    }

    private async Task RefreshRepoAsync(RepoViewModel vm)
    {
        long request = ++_refreshSequence;
        _refreshRequests[vm] = request;
        string owner = vm.Config.Owner, repo = vm.Config.Repo;
        bool IsCurrent() => !_closed && Repos.Contains(vm) &&
            _refreshRequests.GetValueOrDefault(vm) == request &&
            new GitHubRepository(owner, repo).Matches(vm.Config);
        vm.IsLoading = true;
        try
        {
            var result = await GitHubApi.GetReleasesAsync(owner, repo);
            if (!IsCurrent()) return;
            await ReleaseMarkdownService.RenderAsync(result.Releases, owner, repo);
            if (!IsCurrent()) return;
            vm.SetReleases(result.Releases);
            if (!string.IsNullOrEmpty(result.RedirectedTo)) HandleRedirectedUrl(vm, result.RedirectedTo);
        }
        catch (Exception ex)
        {
            if (IsCurrent()) vm.SetError(ex.Message);
        }
        finally
        {
            if (_refreshRequests.GetValueOrDefault(vm) == request)
            {
                _refreshRequests.Remove(vm);
                vm.IsLoading = false;
                if (ReferenceEquals(vm, SelectedRepo)) SyncMasterToggleState();
            }
        }
    }

    /// <summary>
    /// 自动更新因改名而重定向的仓库 URL（同步 Owner / Repo / Url 并写回磁盘 + 刷新界面）。
    /// 只改 Url 而不改 Owner/Repo 会导致下次刷新仍用旧地址 → 又 301，陷入死循环。
    /// </summary>
    private void HandleRedirectedUrl(RepoViewModel vm, string newUrl)
    {
        if (string.Equals(vm.Config.Url, newUrl, StringComparison.OrdinalIgnoreCase)) return;

        if (!GitHubRepository.TryParse(newUrl, out var repository)) return;
        if (FindRepository(repository.Owner, repository.Name, vm) != null)
        {
            TxtStatus.Text = $"⚠ {vm.DisplayName} 已迁移到列表中已有的 {repository.Owner}/{repository.Name}";
            return;
        }
        var newOwner = repository.Owner;
        var newRepo = repository.Name;

        var oldName = vm.DisplayName;
        vm.Config.Url = $"https://github.com/{newOwner}/{newRepo}";
        vm.Config.Owner = newOwner;
        vm.Config.Repo = newRepo;
        vm.OnConfigChanged();          // 通知界面刷新 DisplayName 等计算属性
        SaveConfig();

        var newName = $"{newOwner}/{newRepo}";
        TxtStatus.Text = oldName == newName
            ? $"📎 {newName} 地址已规范化"
            : $"📎 {oldName} 已自动迁移到 {newName}（GitHub 重定向）";
    }

    private async void BtnRefresh_Click(object sender, RoutedEventArgs e)
    {
        BtnRefresh.IsEnabled = false;
        try { await RefreshAllAsync(); await RefreshRateLimitAsync(); }
        finally { BtnRefresh.IsEnabled = true; }
    }

}
