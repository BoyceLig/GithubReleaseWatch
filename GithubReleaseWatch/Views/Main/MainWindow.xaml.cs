using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input; // MouseButtonEventArgs / MouseEventArgs
using System.Windows.Media;
using System.Windows.Threading;
using MessageBox = System.Windows.MessageBox;

namespace GithubReleaseWatch
{
    public partial class MainWindow : Window
    {
        private AppConfig _config = new();
        private bool _initializing = true;
        private bool _closed;
        public ObservableCollection<RepoViewModel> Repos { get; } = new();
        private ListBoxDragReorder? _dragReorder; // 左侧仓库列表拖拽排序
        public MainWindow()
        {
            InitializeComponent();
            _config = ConfigStore.Load();
            GitHubApi.Apply(_config.Token, _config.TokenEnabled, _config.Proxy);

            foreach (var repo in _config.Repos)
                Repos.Add(new RepoViewModel(repo, () => _config.ShowPrerelease));
            RepoList.ItemsSource = Repos;
            ChkPrerelease.IsChecked = _config.ShowPrerelease;
            _initializing = false;
            if (ConfigStore.LastError != null) TxtStatus.Text = ConfigStore.LastError;

            DetailsContent.Visibility = Visibility.Collapsed;
            if (Repos.Count > 0) RepoList.SelectedIndex = 0;

            // 启用拖拽排序：释放时把 _config.Repos 也同步到 Repos 的顺序并落盘
            _dragReorder = new ListBoxDragReorder(RepoList, Repos, PersistOrder);

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
            // 窗口最小化或关闭时暂停，避免无谓请求。
            _rateLimitTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(60) };
            _rateLimitTimer.Tick += async (_, _) =>
            {
                if (!IsVisible || WindowState == WindowState.Minimized || _closed) return;
                await RefreshRateLimitAsync();
            };
            _rateLimitTimer.Start();

            _rateLimitRedrawTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(20) };
            _rateLimitRedrawTimer.Tick += (_, _) =>
            {
                if (!IsVisible || WindowState == WindowState.Minimized || _closed) return;
                RedrawRateLimitText();
            };
            _rateLimitRedrawTimer.Start();

            // 关闭时释放计时器、拖拽事件和浏览器资源
            Closed += (_, _) =>
            {
                _rateLimitTimer?.Stop();
                _rateLimitRedrawTimer?.Stop();
                _closed = true;
                StopSmoothScroll();
                _dragReorder?.Dispose();
                foreach (var view in FindVisualChildren<GitHubMarkdownView>(DetailsContent))
                    view.DisposeBrowser();
            };
        }

        private RepoViewModel? SelectedRepo => RepoList.SelectedItem as RepoViewModel;

        /// <summary>设置：代理 + Token，保存后用新设置立即重新拉取。</summary>
        private async void BtnSettings_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new SettingsWindow(_config.Proxy ?? new ProxyConfig(), _config.Token, _config.TokenEnabled, _config.RefreshOnStartup)
            {
                Owner = this
            };
            if (dialog.ShowDialog() != true) return;

            _config.Proxy = dialog.ProxyResult;
            _config.Token = string.IsNullOrWhiteSpace(dialog.TokenResult) ? null : dialog.TokenResult;
            _config.TokenEnabled = dialog.TokenEnabledResult;
            _config.RefreshOnStartup = dialog.RefreshOnStartupResult;
            GitHubApi.Apply(_config.Token, _config.TokenEnabled, _config.Proxy);
            SaveConfig();
            await RefreshAllAsync();
            await RefreshRateLimitAsync();
        }

        /// <summary>从其他实例/提示框激活本窗口（不重复创建，已显示则前置）。</summary>
        internal void ShowMainWindow()
        {
            if (!IsVisible) Show();
            if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
            Activate();
            Topmost = true;
            Topmost = false;
            Focus();
        }

        /// <summary>显示"程序已在运行"的模态提示框，以主窗口为 owner。</summary>
        internal void ShowAlreadyRunningAlert()
        {
            ShowMainWindow();

            // 使用自定义 Window 而不是 MessageBox，避免 MessageBox 关闭时把焦点/键盘事件误传给主窗口。
            var alert = new Window
            {
                Owner = this,
                Title = "提示",
                Width = 420,
                Height = 180,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                ResizeMode = ResizeMode.NoResize,
                ShowInTaskbar = false,
                Background = System.Windows.Media.Brushes.White,
                BorderBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(209, 213, 219)),
                BorderThickness = new Thickness(1),
            };

            var root = new Grid();
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

            var title = new TextBlock
            {
                Text = "提示",
                FontSize = 15,
                FontWeight = FontWeights.Bold,
                Margin = new Thickness(20, 16, 20, 0),
                Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(17, 24, 39))
            };
            Grid.SetRow(title, 0);

            var body = new TextBlock
            {
                Text = "GithubReleaseWatch 已经在运行中。",
                FontSize = 13,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(20, 12, 20, 0),
                Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(55, 65, 81))
            };

            var okButton = new System.Windows.Controls.Button
            {
                Content = "确定",
                Width = 80,
                Height = 28,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Right,
                Margin = new Thickness(0, 20, 20, 0)
            };
            okButton.Click += (_, _) => alert.Close();

            var bodyPanel = new StackPanel();
            bodyPanel.Children.Add(body);
            bodyPanel.Children.Add(okButton);
            Grid.SetRow(bodyPanel, 1);

            root.Children.Add(title);
            root.Children.Add(bodyPanel);
            alert.Content = root;

            // 确定按钮支持按回车关闭
            alert.Loaded += (_, _) => okButton.Focus();

            alert.ShowDialog();
        }

        private void BtnAbout_Click(object sender, RoutedEventArgs e)
        {
            new AboutWindow { Owner = this }.ShowDialog();
        }

    }
}
