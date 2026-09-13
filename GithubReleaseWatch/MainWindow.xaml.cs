using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Forms; // NotifyIcon (WinForms)
using System.Windows.Input; // MouseButtonEventArgs / MouseEventArgs (避免被 WinForms 同名类覆盖)
using System.Windows.Media;
using System.Windows.Threading;
using MessageBox = System.Windows.MessageBox;

namespace GithubReleaseWatch
{
    public partial class MainWindow : Window
    {
        private AppConfig _config = new();
        public ObservableCollection<RepoViewModel> Repos { get; } = new();
        private NotifyIcon? _notifyIcon;
        private bool _reallyClosing = false; // 用户从托盘菜单"退出"时才真正关闭
        private DispatcherTimer? _rateLimitTimer;
        private DispatcherTimer? _rateLimitRedrawTimer;
        /// <summary>最近一次配额快照，用于本地重绘倒计时（不额外请求 API）。</summary>
        private GitHubApi.RateLimitSnapshot? _lastRateSnapshot;
        private ListBoxDragReorder? _dragReorder; // 左侧仓库列表拖拽排序
        /// <summary>正在同步「全部展开/折叠」主按钮状态时，避免递归触发 SetAllVersionsExpanded。</summary>
        private bool _syncingMasterToggle;
        /// <summary>全局鼠标滚轮钩子兜底，解决 WebBrowser 吞掉滚轮导致外层无法滚动的问题。</summary>
        private MouseWheelHook? _mouseWheelHook;
        /// <summary>WebBrowser 内部 JS 最近一次处理滚轮的时间，避免低层鼠标钩子重复滚动。</summary>
        private DateTime _lastScriptWheel;

        /// <summary>供 MouseWheelHook 判断 JS 桥接是否已处理当前滚轮事件。</summary>
        internal DateTime LastScriptWheel => _lastScriptWheel;

        /// <summary>复制 Markdown 按钮最近一次点击时间，用于防抖避免连续点击卡顿。</summary>
        private DateTime _lastCopyClick;

        public MainWindow()
        {
            InitializeComponent();
            _config = ConfigStore.Load();
            GitHubApi.Apply(_config.Token, _config.TokenEnabled, _config.Proxy);

            foreach (var repo in _config.Repos)
                Repos.Add(new RepoViewModel(repo, () => _config.ShowPrerelease));
            RepoList.ItemsSource = Repos;
            ChkPrerelease.IsChecked = _config.ShowPrerelease;

            DetailsContent.Visibility = Visibility.Collapsed;
            if (Repos.Count > 0) RepoList.SelectedIndex = 0;

            // 启用拖拽排序：释放时把 _config.Repos 也同步到 Repos 的顺序并落盘
            _dragReorder = new ListBoxDragReorder(RepoList, Repos, PersistOrder);

            InitNotifyIcon();

            // 安装鼠标滚轮钩子兜底
            try
            {
                _mouseWheelHook = new MouseWheelHook(this);
            }
            catch { /* 钩子安装失败不影响主功能 */ }

            // 根据 RefreshOnStartup 决定打开时是否自动刷新
            Loaded += async (_, _) =>
            {
                if (RightScrollViewer != null)
                    RightScrollViewer.ScrollChanged += RightScrollViewer_ScrollChanged;

                if (_config.RefreshOnStartup) await RefreshAllAsync();
                await RefreshRateLimitAsync();
            };

            // 配额显示：每 60 秒真正请求一次 /rate_limit（不计配额），
            // 另外每 20 秒用缓存的快照重绘文本，让"重置倒计时"能走字。
            // 窗口最小化到托盘时暂停，避免无谓请求。
            _rateLimitTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(60) };
            _rateLimitTimer.Tick += async (_, _) =>
            {
                if (!IsVisible) return;
                await RefreshRateLimitAsync();
            };
            _rateLimitTimer.Start();

            _rateLimitRedrawTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(20) };
            _rateLimitRedrawTimer.Tick += (_, _) =>
            {
                if (!IsVisible) return;
                RedrawRateLimitText();
            };
            _rateLimitRedrawTimer.Start();

            // 关闭时清理托盘图标和全局鼠标钩子
            Closed += (_, _) =>
            {
                _rateLimitTimer?.Stop();
                _rateLimitRedrawTimer?.Stop();
                _notifyIcon?.Dispose();
                try { _mouseWheelHook?.Dispose(); }
                catch { }
            };
        }

        private RepoViewModel? SelectedRepo => RepoList.SelectedItem as RepoViewModel;

        private async Task RefreshAllAsync()
        {
            if (Repos.Count == 0) return;
            TxtStatus.Text = "刷新中…";
            await Task.WhenAll(Repos.Select(RefreshRepoAsync));
            TxtStatus.Text = $"已刷新 {Repos.Count} 个仓库 · {DateTime.Now:HH:mm:ss}";
        }

        private async Task RefreshRepoAsync(RepoViewModel vm)
        {
            vm.IsLoading = true;
            try
            {
                var result = await GitHubApi.GetReleasesAsync(vm.Config.Owner, vm.Config.Repo);

                // 先用 GitHub /markdown API 把可见版本的 Body 渲染成 HTML，再通知界面绑定。
                // 这样 WebBrowser 首次显示时就有内容，避免先空白再闪一下。
                await RenderReleasesMarkdownAsync(vm, result.Releases);

                vm.SetReleases(result.Releases);

                // 检测到 GitHub 自动跟随了重定向 —— 自动更新本地 URL 到新位置
                if (!string.IsNullOrEmpty(result.RedirectedTo))
                {
                    HandleRedirectedUrl(vm, result.RedirectedTo);
                }
            }
            catch (Exception ex)
            {
                vm.SetError(ex.Message);
            }
            finally
            {
                vm.IsLoading = false;
            }
        }

        /// <summary>
        /// 自动更新因改名而重定向的仓库 URL（同步 Owner / Repo / Url 并写回磁盘 + 刷新界面）。
        /// 只改 Url 而不改 Owner/Repo 会导致下次刷新仍用旧地址 → 又 301，陷入死循环。
        /// </summary>
        private void HandleRedirectedUrl(RepoViewModel vm, string newUrl)
        {
            if (string.Equals(vm.Config.Url, newUrl, StringComparison.OrdinalIgnoreCase)) return;

            // 从新 URL 解析 owner/repo：https://github.com/{owner}/{repo}[/...]
            var m = System.Text.RegularExpressions.Regex.Match(
                newUrl, @"github\.com/([^/]+)/([^/?#]+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (!m.Success) return;

            var newOwner = m.Groups[1].Value;
            var newRepo = m.Groups[2].Value.TrimEnd('/');

            var oldName = vm.DisplayName;
            vm.Config.Url = $"https://github.com/{newOwner}/{newRepo}";
            vm.Config.Owner = newOwner;
            vm.Config.Repo = newRepo;
            vm.OnConfigChanged();          // 通知界面刷新 DisplayName 等计算属性
            ConfigStore.Save(_config);

            var newName = $"{newOwner}/{newRepo}";
            TxtStatus.Text = oldName == newName
                ? $"📎 {newName} 地址已规范化"
                : $"📎 {oldName} 已自动迁移到 {newName}（GitHub 重定向）";
        }

        /// <summary>把 GitHub /markdown API 返回的 HTML 片段包装成完整页面（含基础 GitHub 风格 CSS）。</summary>
        private static string WrapGitHubHtml(string? htmlFragment)
        {
            if (string.IsNullOrWhiteSpace(htmlFragment))
                htmlFragment = "（无版本说明）";

            // overflow:hidden 让 WebBrowser 内部不滚动；
            // JS 把 wheel 事件转发给 WPF 外层 RightScrollViewer，实现统一滚动。
            const string wheelScript = @"
<script>
(function(){
  if (window.__grwWheelHook) return;
  window.__grwWheelHook = true;
  function onWheel(e){
    var evt = e || window.event;
    var delta = evt.wheelDelta || (-evt.detail * 40);
    if (delta) {
      try {
        if (window.external && window.external.ScrollOuter) {
          window.external.ScrollOuter(delta);
        }
      } catch(ex) {}
    }
    if (evt.preventDefault) evt.preventDefault();
    evt.returnValue = false;
    if (evt.stopPropagation) evt.stopPropagation();
    evt.cancelBubble = true;
    return false;
  }
  // 捕获阶段监听，确保 document 内任意子元素触发 wheel 都能拦截
  if (document.addEventListener) {
    document.addEventListener('mousewheel', onWheel, true);
    document.addEventListener('DOMMouseScroll', onWheel, true);
  } else if (document.attachEvent) {
    document.attachEvent('onmousewheel', onWheel);
  }
  if (window.addEventListener) {
    window.addEventListener('mousewheel', onWheel, true);
  } else if (window.attachEvent) {
    window.attachEvent('onmousewheel', onWheel);
  }
})();
</script>";

            return "<!DOCTYPE html><html><head>"
                + "<meta http-equiv='X-UA-Compatible' content='IE=edge'>"
                + "<meta charset='utf-8'>"
                + "<style>"
                + "html,body{overflow:hidden;height:auto;margin:0;padding:0;background:#fff;}"
                + "body{font-family:-apple-system,BlinkMacSystemFont,'Segoe UI',Helvetica,Arial,sans-serif;font-size:13px;line-height:1.6;color:#1F2328;padding:8px;}"
                + ".markdown-body h1,.markdown-body h2,.markdown-body h3,.markdown-body h4{margin:0.6em 0 0.4em;font-weight:600;line-height:1.25;}"
                + ".markdown-body h1{font-size:1.5em;border-bottom:1px solid #D8DEE4;padding-bottom:0.3em;}"
                + ".markdown-body h2{font-size:1.25em;border-bottom:1px solid #D8DEE4;padding-bottom:0.3em;}"
                + ".markdown-body p{margin:0.5em 0;}"
                + ".markdown-body ul,.markdown-body ol{padding-left:1.6em;margin:0.4em 0;}"
                + ".markdown-body li{margin:0.15em 0;}"
                + ".markdown-body code{font-family:SFMono-Regular,Consolas,'Liberation Mono',Menlo,monospace;background:rgba(175,184,193,0.2);padding:0.15em 0.3em;border-radius:4px;font-size:0.92em;}"
                + ".markdown-body pre{background:#F6F8FA;padding:12px;border-radius:6px;overflow:hidden;}"
                + ".markdown-body pre code{background:transparent;padding:0;}"
                + ".markdown-body a{color:#0969DA;text-decoration:none;}"
                + ".markdown-body a:hover{text-decoration:underline;}"
                + ".markdown-body table{border-collapse:collapse;width:100%;margin:0.6em 0;}"
                + ".markdown-body th,.markdown-body td{border:1px solid #D0D7DE;padding:6px 10px;}"
                + ".markdown-body th{background:#F6F8FA;font-weight:600;}"
                + ".markdown-body img{max-width:100%;height:auto;}"
                + "</style></head><body><div class='markdown-body'>"
                + htmlFragment
                + "</div>"
                + wheelScript
                + "</body></html>";
        }

        /// <summary>用 GitHub /markdown API 渲染 Release Body，失败则回退为转义的 Markdown 原文。</summary>
        private static async Task RenderReleasesMarkdownAsync(RepoViewModel vm, List<ReleaseInfo> releases)
        {
            var toRender = releases
                .Where(r => !r.Draft && string.IsNullOrEmpty(r.BodyHtml) && !string.IsNullOrWhiteSpace(r.Body))
                .ToList();
            if (toRender.Count == 0) return;

            var tasks = toRender.Select(async r =>
            {
                try
                {
                    var html = await GitHubApi.RenderMarkdownAsync(r.Body, vm.Config.Owner, vm.Config.Repo);
                    r.BodyHtml = WrapGitHubHtml(html);
                }
                catch
                {
                    r.BodyHtml = WrapGitHubHtml($"<pre>{System.Net.WebUtility.HtmlEncode(r.Body)}</pre>");
                }
            });
            await Task.WhenAll(tasks);
        }

        private int _lastShownRemaining = -1;
        private int _lastShownLimit = -1;

        private async Task RefreshRateLimitAsync()
        {
            try
            {
                var snap = await GitHubApi.GetRateLimitSnapshotAsync();
                if (snap.Ok)
                {
                    _lastRateSnapshot = snap;
                    RedrawRateLimitText(consumeFromPrevious: true);
                    _lastShownRemaining = snap.Remaining;
                    _lastShownLimit = snap.Limit;
                }
                else
                {
                    TxtRateLimit.Text = "API 配额 获取失败";
                }
            }
            catch
            {
                TxtRateLimit.Text = "API 配额 获取失败";
            }
        }

        /// <summary>用缓存的快照重绘配额文本（本地计算，不消耗 API 配额）。</summary>
        private void RedrawRateLimitText(bool consumeFromPrevious = false)
        {
            var snap = _lastRateSnapshot;
            if (snap is not { Ok: true }) return;

            var mode = snap.Authenticated ? "认证" : "匿名";
            int used = snap.Limit - snap.Remaining;

            // 只在配额真的减少时提示消耗量；无变化不显示（避免无意义的 "±0"）
            string delta = "";
            if (consumeFromPrevious && _lastShownRemaining >= 0 && _lastShownLimit == snap.Limit)
            {
                int diff = _lastShownRemaining - snap.Remaining;
                if (diff > 0) delta = $"　本次消耗 {diff}";
            }

            TxtRateLimit.Text =
                $"API 配额 已用 {used}/{snap.Limit}，剩 {snap.Remaining}（{mode} · {ResetCountdown(snap.ResetAt)} 后重置）{delta}";
            TxtRateLimit.ToolTip = $"配额重置时间：{snap.ResetAt.LocalDateTime:yyyy-MM-dd HH:mm:ss}";
        }

        /// <summary>把重置时刻格式化成倒计时文本（如 "1 小时 23 分" / "45 分钟" / "已重置"）。</summary>
        private static string ResetCountdown(DateTimeOffset resetAt)
        {
            var span = resetAt.LocalDateTime - DateTime.Now;
            if (span <= TimeSpan.Zero) return "已重置";
            if (span.TotalHours >= 1)
                return $"{(int)span.TotalHours} 小时 {span.Minutes} 分";
            if (span.TotalMinutes >= 1)
                return $"{(int)span.TotalMinutes} 分钟";
            return $"{Math.Max(1, (int)span.TotalSeconds)} 秒";
        }

        private async void BtnRefresh_Click(object sender, RoutedEventArgs e)
        {
            await RefreshAllAsync();
            await RefreshRateLimitAsync();
        }

        private async void BtnAdd_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new AddRepoWindow { Owner = this };
            if (dialog.ShowDialog() != true) return;

            // 查重：owner+repo 完全相同的视为重复（忽略大小写）
            string owner = dialog.OwnerName, repo = dialog.RepoName;
            var duplicate = Repos.FirstOrDefault(v =>
                string.Equals(v.Config.Owner, owner, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(v.Config.Repo, repo, StringComparison.OrdinalIgnoreCase));
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
            ConfigStore.Save(_config);

            await RefreshRepoAsync(vm);

            // 未填写当前版本时，默认取最新版本
            if (string.IsNullOrEmpty(config.CurrentVersion) && vm.LatestVisible != null)
            {
                vm.SetCurrent(vm.LatestVisible.TagName);
                ConfigStore.Save(_config);
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
            if (MessageBox.Show(this, $"确定移除 {vm.DisplayName} 吗？", "确认移除",
                MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;

            Repos.Remove(vm);
            _config.Repos.Remove(vm.Config);
            ConfigStore.Save(_config);
        }

        private void RepoList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            // 拖拽排序过程中 ListBox 选中项会不断变化，跳过右侧渲染避免卡顿
            if (_dragReorder?.IsDragging == true) return;

            var vm = SelectedRepo;
            DetailsContent.DataContext = vm;
            DetailsContent.Visibility = vm is null ? Visibility.Collapsed : Visibility.Visible;
            Placeholder.Visibility = vm is null ? Visibility.Visible : Visibility.Collapsed;
        }

        private void ChkPrerelease_Changed(object sender, RoutedEventArgs e)
        {
            _config.ShowPrerelease = ChkPrerelease.IsChecked == true;
            ConfigStore.Save(_config);
            foreach (var vm in Repos) vm.OnFilterChanged();
        }

        /// <summary>设置：代理 + Token，保存后用新设置立即重新拉取。</summary>
        private async void BtnSettings_Click(object sender, RoutedEventArgs e)
        {
            _config.Proxy ??= new ProxyConfig();
            var dialog = new SettingsWindow(_config.Proxy, _config.Token, _config.TokenEnabled, _config.RefreshOnStartup)
            {
                Owner = this
            };
            if (dialog.ShowDialog() != true) return;

            _config.Proxy = dialog.ProxyResult;
            _config.Token = string.IsNullOrWhiteSpace(dialog.TokenResult) ? null : dialog.TokenResult;
            _config.TokenEnabled = dialog.TokenEnabledResult;
            _config.RefreshOnStartup = dialog.RefreshOnStartupResult;
            GitHubApi.Apply(_config.Token, _config.TokenEnabled, _config.Proxy);
            ConfigStore.Save(_config);
            await RefreshAllAsync();
            await RefreshRateLimitAsync();
        }

        /// <summary>"完成"：当前版本提升到最新版本。</summary>
        private void BtnComplete_Click(object sender, RoutedEventArgs e)
        {
            if (SelectedRepo is not { } vm) return;
            if (vm.LatestVisible is not { } latest)
            {
                MessageBox.Show(this, "该仓库没有可用版本（可能全部被 Pre-release 过滤）。", "提示",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            vm.SetCurrent(latest.TagName);
            ConfigStore.Save(_config);
        }

        /// <summary>把某个具体版本设为当前版本。</summary>
        private void BtnMarkRelease_Click(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement { DataContext: ReleaseInfo release } && SelectedRepo is { } vm)
            {
                vm.SetCurrent(release.TagName);
                ConfigStore.Save(_config);
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

            var text = string.IsNullOrWhiteSpace(release.Body) ? "" : release.Body;
            TxtStatus.Text = $"📋 正在复制 {release.TagName}…";

            const int maxRetries = 5;
            for (int i = 0; i < maxRetries; i++)
            {
                try
                {
                    System.Windows.Clipboard.SetText(text);
                    TxtStatus.Text = $"📋 已复制 {release.TagName} 的 Markdown 原文";
                    return;
                }
                catch
                {
                    // 剪贴板被其他进程占用时异步重试，不阻塞 UI 线程
                    if (i < maxRetries - 1)
                        await Task.Delay(50);
                }
            }

            TxtStatus.Text = $"⚠ 复制 {release.TagName} 失败，剪贴板正忙";
        }

        private void BtnOpenGitHub_Click(object sender, RoutedEventArgs e)
        {
            if (SelectedRepo is { } vm)
                Process.Start(new ProcessStartInfo(vm.Config.Url) { UseShellExecute = true });
        }

        // ===== 任务栏常驻图标 =====

        private void InitNotifyIcon()
        {
            _notifyIcon = new NotifyIcon
            {
                Icon = LoadTrayIcon(),
                Visible = true, // 打开软件即出现，不等关闭窗口
                Text = "GithubReleaseWatch — GitHub Release 监测"
            };

            var menu = new ContextMenuStrip();
            menu.Items.Add("📖 显示主窗口", null, (_, _) => ShowMainWindow());
            menu.Items.Add("🔄 刷新全部", null, async (_, _) => await RefreshAllAsync());
            menu.Items.Add("⚙ 设置", null, (_, _) => BtnSettings_Click(this, new RoutedEventArgs()));
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("❌ 退出", null, (_, _) => ExitApp());
            _notifyIcon.ContextMenuStrip = menu;

            _notifyIcon.DoubleClick += (_, _) => ShowMainWindow();
            _notifyIcon.BalloonTipClicked += (_, _) => ShowMainWindow();
        }

        /// <summary>
        /// 加载托盘图标：优先读项目内嵌资源 GithubReleaseWatch.ico；读不到则回退到系统应用图标。
        /// </summary>
        private static Icon LoadTrayIcon()
        {
            try
            {
                var asmDir = System.IO.Path.GetDirectoryName(typeof(MainWindow).Assembly.Location)!;
                var icoPath = System.IO.Path.Combine(asmDir, "GithubReleaseWatch.ico");
                if (System.IO.File.Exists(icoPath))
                    return new Icon(icoPath);
            }
            catch { /* 忽略，回退 */ }
            return SystemIcons.Application;
        }

        /// <summary>从托盘恢复主窗口（不重复创建，已显示则前置）。</summary>
        private void ShowMainWindow()
        {
            if (!IsVisible) Show();
            if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
            Activate();
            Topmost = true;
            Topmost = false;
            Focus();
        }

        /// <summary>真正退出应用（区分于点 X 触发的"最小化到托盘"）。</summary>
        private void ExitApp()
        {
            _reallyClosing = true;
            if (_notifyIcon != null) _notifyIcon.Visible = false;
            System.Windows.Application.Current.Shutdown();
        }

        /// <summary>点窗口关闭按钮 → 最小化到托盘（除非用户从托盘菜单"退出"）。</summary>
        protected override void OnClosing(CancelEventArgs e)
        {
            if (!_reallyClosing)
            {
                e.Cancel = true;
                Hide();
                // 托盘图标打开软件时已常驻，这里只需提示"已最小化"
                if (_notifyIcon != null)
                {
                    _notifyIcon.BalloonTipTitle = "GithubReleaseWatch";
                    _notifyIcon.BalloonTipText = "已最小化到系统托盘，双击图标可重新打开窗口，右键可退出。";
                    _notifyIcon.ShowBalloonTip(2500);
                }
            }
            base.OnClosing(e);
        }

        // ===== 拖拽排序：把容器事件转发给 helper，保持选中、避免误触发 =====

        private void RepoListItem_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
            => _dragReorder?.OnItemMouseDown(sender, e);

        /// <summary>拖拽排序后：让 _config.Repos 与 Repos 顺序保持一致并写盘。</summary>
        private void PersistOrder()
        {
            _config.Repos.Clear();
            foreach (var vm in Repos) _config.Repos.Add(vm.Config);
            ConfigStore.Save(_config);
        }

        // ===== 右键菜单（每个仓库卡片） =====

        private void CtxRefreshRepo_Click(object sender, RoutedEventArgs e)
        {
            if (sender is MenuItem { DataContext: RepoViewModel vm })
                _ = RefreshRepoAsync(vm);
        }

        private void CtxOpenGitHub_Click(object sender, RoutedEventArgs e)
        {
            if (sender is MenuItem { DataContext: RepoViewModel vm })
                Process.Start(new ProcessStartInfo(vm.Config.Url) { UseShellExecute = true });
        }

        private void CtxRemoveRepo_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not MenuItem { DataContext: RepoViewModel vm }) return;
            if (MessageBox.Show(this, $"确定移除 {vm.DisplayName} 吗？", "确认移除",
                MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;

            Repos.Remove(vm);
            _config.Repos.Remove(vm.Config);
            ConfigStore.Save(_config);
        }

        private void BtnAbout_Click(object sender, RoutedEventArgs e)
        {
            new AboutWindow { Owner = this }.ShowDialog();
        }

        // ===== 滚轮穿透：版本卡片内的 WebBrowser/Border 把滚轮事件转给外层 ScrollViewer =====

        /// <summary>
        /// 鼠标在版本信息卡片内滚动时，直接转发给外层 RightScrollViewer，
        /// 让右侧标题栏固定、三个版本信息共用外层面板滚动。
        /// </summary>
        private void VersionBody_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            e.Handled = true;
            RightScrollViewer?.ScrollToVerticalOffset(
                RightScrollViewer.VerticalOffset - WheelDeltaToOffset(e.Delta));
        }

        /// <summary>外层 ScrollViewer 滚动时，重新裁剪所有 WebBrowser 到当前视口。</summary>
        private void RightScrollViewer_ScrollChanged(object sender, ScrollChangedEventArgs e)
        {
            if (e.VerticalChange == 0 && e.HorizontalChange == 0) return;
            // 推迟到渲染前统一裁剪，避免拖动滚动条时频繁调用 SetWindowRgn 造成撕裂/闪烁。
            Dispatcher.BeginInvoke(ClipAllWebBrowsers, DispatcherPriority.Render);
        }

        /// <summary>遍历右侧所有 WebBrowser，把它们裁剪到 RightScrollViewer 当前可见视口。</summary>
        private void ClipAllWebBrowsers()
        {
            if (RightScrollViewer == null) return;
            try
            {
                foreach (var wb in FindVisualChildren<System.Windows.Controls.WebBrowser>(RightScrollViewer))
                {
                    if (wb.IsLoaded && wb.IsVisible)
                        WebBrowserAirspaceHelper.ClipToScrollViewerViewport(wb, RightScrollViewer);
                }
            }
            catch { }
        }

        private static IEnumerable<T> FindVisualChildren<T>(DependencyObject parent) where T : DependencyObject
        {
            if (parent == null) yield break;
            int count = VisualTreeHelper.GetChildrenCount(parent);
            for (int i = 0; i < count; i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i);
                if (child is T t) yield return t;
                foreach (var grandChild in FindVisualChildren<T>(child))
                    yield return grandChild;
            }
        }

        /// <summary>供 WebBrowser 内部 JavaScript 调用的滚动入口。</summary>
        public void ScrollOuter(int delta)
        {
            _lastScriptWheel = DateTime.Now;
            Dispatcher.BeginInvoke(() =>
            {
                if (RightScrollViewer == null) return;
                RightScrollViewer.ScrollToVerticalOffset(
                    RightScrollViewer.VerticalOffset - WheelDeltaToOffset(delta));
            }, DispatcherPriority.Input);
        }

        /// <summary>
        /// 把鼠标滚轮 delta 转换为与 WPF ScrollViewer 默认滚动速度一致的偏移量（DIP）。
        /// WPF 默认每 120 delta 滚动 SystemParameters.WheelScrollLines 行，每行约 16 DIP。
        /// </summary>
        private static double WheelDeltaToOffset(double delta)
        {
            double lines = SystemParameters.WheelScrollLines;
            if (lines <= 0) lines = 3; // 滚轮被系统禁用时回退到 3 行
            return delta / Mouse.MouseWheelDeltaForOneLine * lines * 16.0;
        }

        // ===== 版本折叠/展开 =====

        /// <summary>单个版本的折叠/展开切换。</summary>
        private void BtnToggleVersionBody_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not System.Windows.Controls.Primitives.ToggleButton btn) return;

            // 在 DataTemplate 中，ToggleButton 位于 Grid 内，Grid 位于 StackPanel 内，
            // 因此要从当前按钮沿可视化树向上找到 StackPanel，再取其中的 WebBrowser。
            DependencyObject? current = btn;
            StackPanel? container = null;
            while (current != null)
            {
                if (current is StackPanel sp)
                {
                    container = sp;
                    break;
                }
                current = System.Windows.Media.VisualTreeHelper.GetParent(current);
            }

            if (container == null) return;
            var viewer = FindVisualChild<System.Windows.Controls.WebBrowser>(container);
            if (viewer == null) return;

            bool expanded = btn.IsChecked == true;
            viewer.Visibility = expanded ? Visibility.Visible : Visibility.Collapsed;
            btn.Content = expanded ? "▼" : "▶";

            SyncMasterToggleState();
        }

        /// <summary>
        /// 根据当前各子版本卡片的展开状态同步顶部「全部展开/折叠」主按钮。
        /// 全部展开时主按钮显示「▶ 全部折叠」并 IsChecked=true；否则显示「▼ 全部展开」。
        /// </summary>
        private void SyncMasterToggleState()
        {
            var itemsControl = FindVisualChild<ItemsControl>(DetailsContent);
            if (itemsControl == null) return;

            int total = 0, expanded = 0;
            for (int i = 0; i < itemsControl.Items.Count; i++)
            {
                if (itemsControl.ItemContainerGenerator.ContainerFromIndex(i) is ContentPresenter cp)
                {
                    var toggle = FindVisualChild<System.Windows.Controls.Primitives.ToggleButton>(cp, "BtnToggleVersionBody");
                    if (toggle != null)
                    {
                        total++;
                        if (toggle.IsChecked == true) expanded++;
                    }
                }
            }

            _syncingMasterToggle = true;
            try
            {
                BtnToggleAllVersions.IsChecked = total > 0 && expanded == total;
            }
            finally
            {
                _syncingMasterToggle = false;
            }
        }

        /// <summary>全部展开。</summary>
        private void BtnToggleAllVersions_Checked(object sender, RoutedEventArgs e)
        {
            BtnToggleAllVersions.Content = "▶ 全部折叠";
            if (!_syncingMasterToggle)
                SetAllVersionsExpanded(true);
        }

        /// <summary>全部折叠。</summary>
        private void BtnToggleAllVersions_Unchecked(object sender, RoutedEventArgs e)
        {
            BtnToggleAllVersions.Content = "▼ 全部展开";
            if (!_syncingMasterToggle)
                SetAllVersionsExpanded(false);
        }

        /// <summary>遍历 ItemsControl 中所有版本卡片，统一设置 Body 可见性。</summary>
        private void SetAllVersionsExpanded(bool expanded)
        {
            // DataTemplate 中的控件需要等布局完成后才能找到，使用 Loaded 优先级延迟遍历
            Dispatcher.BeginInvoke(() =>
            {
                var itemsControl = FindVisualChild<ItemsControl>(DetailsContent);
                if (itemsControl == null) return;

                for (int i = 0; i < itemsControl.Items.Count; i++)
                {
                    if (itemsControl.ItemContainerGenerator.ContainerFromIndex(i) is ContentPresenter cp)
                    {
                        var toggle = FindVisualChild<System.Windows.Controls.Primitives.ToggleButton>(cp, "BtnToggleVersionBody");
                        var viewer = FindVisualChild<System.Windows.Controls.WebBrowser>(cp);
                        if (toggle != null) { toggle.IsChecked = expanded; toggle.Content = expanded ? "▼" : "▶"; }
                        if (viewer != null) viewer.Visibility = expanded ? Visibility.Visible : Visibility.Collapsed;
                    }
                }
            }, DispatcherPriority.Loaded);
        }

        /// <summary>在可视化树中查找指定类型的子元素。</summary>
        private static T? FindVisualChild<T>(DependencyObject parent, string? name = null) where T : DependencyObject
        {
            int count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent);
            for (int i = 0; i < count; i++)
            {
                var child = System.Windows.Media.VisualTreeHelper.GetChild(parent, i);
                if (child is T t && (name == null || (t is FrameworkElement fe && fe.Name == name)))
                    return t;
                var found = FindVisualChild<T>(child, name);
                if (found != null) return found;
            }
            return null;
        }

        /// <summary>沿可视化树向上查找指定类型的父元素。</summary>
        private static T? FindVisualParent<T>(DependencyObject child) where T : DependencyObject
        {
            var current = child;
            while (current != null)
            {
                if (current is T t) return t;
                current = System.Windows.Media.VisualTreeHelper.GetParent(current);
            }
            return null;
        }
    }
}