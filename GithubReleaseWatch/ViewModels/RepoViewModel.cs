using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;
using SolidColorBrush = System.Windows.Media.SolidColorBrush;

namespace GithubReleaseWatch
{
    /// <summary>仓库项的界面状态：最新版本、徽章（有更新/最新/失败）、过滤后的版本列表。</summary>
    public class RepoViewModel : INotifyPropertyChanged
    {
        private static readonly Brush UpdateBrush = Frozen(Color.FromRgb(234, 88, 12));
        private static readonly Brush UpToDateBrush = Frozen(Color.FromRgb(22, 163, 74));
        private static readonly Brush ErrorBrush = Frozen(Color.FromRgb(220, 38, 38));
        private static readonly Brush MutedBrush = Frozen(Color.FromRgb(107, 114, 128));

        private readonly Func<bool> _showPrerelease;
        private List<ReleaseInfo> _releases = new();
        private bool _loading;
        private string? _error;

        public RepoViewModel(RepoConfig config, Func<bool> showPrerelease)
        {
            Config = config;
            _showPrerelease = showPrerelease;
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        private static Brush Frozen(Color color)
        {
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            return brush;
        }

        private void Notify([CallerMemberName] string? name = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name!));

        private void NotifySummary()
        {
            Notify(nameof(LatestVisible));
            Notify(nameof(LatestVersion));
            Notify(nameof(BadgeText));
            Notify(nameof(BadgeBrush));
            Notify(nameof(ErrorText));
        }

        public RepoConfig Config { get; }

        public string DisplayName => $"{Config.Owner}/{Config.Repo}";

        public string CurrentVersion =>
            string.IsNullOrWhiteSpace(Config.CurrentVersion) ? "（未设置）" : Config.CurrentVersion;

        /// <summary>按 Pre-release 开关过滤后，最多展示 3 个版本。</summary>
        public IEnumerable<ReleaseInfo> VisibleReleases => Filtered().Take(3);

        /// <summary>按 Pre-release 开关过滤后的最新版本。</summary>
        public ReleaseInfo? LatestVisible => Filtered().FirstOrDefault();

        private IEnumerable<ReleaseInfo> Filtered() =>
            _releases.Where(r => !r.Prerelease || _showPrerelease());

        public bool IsLoading
        {
            get => _loading;
            set { if (_loading == value) return; _loading = value; Notify(); NotifySummary(); }
        }

        public string? Error => _error;

        public string ErrorText => string.IsNullOrEmpty(Error) ? "" : "⚠ " + Error;

        public string LatestVersion
        {
            get
            {
                if (IsLoading) return "加载中…";
                if (Error != null) return "加载失败";
                if (LatestVisible != null) return LatestVisible.TagName;
                return _releases.Count > 0 ? "无稳定版本" : "无 Release";
            }
        }

        public string BadgeText
        {
            get
            {
                if (IsLoading) return "加载中";
                if (Error != null) return "失败";
                if (LatestVisible == null) return "无版本";
                if (string.IsNullOrWhiteSpace(Config.CurrentVersion)) return "未设置";
                return VersionUtil.Compare(LatestVisible.TagName, Config.CurrentVersion) > 0 ? "有更新" : "最新";
            }
        }

        public Brush BadgeBrush => BadgeText switch
        {
            "有更新" => UpdateBrush,
            "最新" => UpToDateBrush,
            "失败" => ErrorBrush,
            _ => MutedBrush,
        };

        public void SetReleases(List<ReleaseInfo> releases)
        {
            var expanded = _releases.GroupBy(r => r.TagName).ToDictionary(g => g.Key, g => g.First().IsExpanded);
            // API 缓存对象与界面状态分离，避免两个仓库项共享折叠状态。
            _releases = releases.Where(r => !r.Draft).Select(r => new ReleaseInfo
            {
                TagName = r.TagName,
                Name = r.Name,
                Body = r.Body,
                BodyHtml = r.BodyHtml,
                Prerelease = r.Prerelease,
                Draft = r.Draft,
                PublishedAt = r.PublishedAt,
                HtmlUrl = r.HtmlUrl,
                IsExpanded = expanded.GetValueOrDefault(r.TagName, true)
            }).ToList();
            _error = null;
            Notify(nameof(Error));
            Notify(nameof(VisibleReleases));
            NotifySummary();
        }

        public void SetError(string message)
        {
            _releases.Clear();
            _error = message;
            Notify(nameof(Error));
            Notify(nameof(VisibleReleases));
            NotifySummary();
        }

        /// <summary>"完成"：把当前版本提升到指定版本号。</summary>
        public void SetCurrent(string version)
        {
            Config.CurrentVersion = version;
            Notify(nameof(CurrentVersion));
            NotifySummary();
        }

        /// <summary>Pre-release 开关变化后重新计算过滤结果。</summary>
        public void OnFilterChanged()
        {
            Notify(nameof(VisibleReleases));
            NotifySummary();
        }

        /// <summary>
        /// 仓库配置（Owner / Repo / Url 等）被外部修改后，通知界面刷新所有依赖属性。
        /// 用于「自动迁移重定向地址」场景。
        /// </summary>
        public void OnConfigChanged()
        {
            Notify(nameof(DisplayName));
            Notify(nameof(Config));
            Notify(nameof(CurrentVersion));
            NotifySummary();
        }
    }
}
