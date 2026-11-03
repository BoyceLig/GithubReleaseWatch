using System;
using System.Windows;
using System.Windows.Controls;
using MessageBox = System.Windows.MessageBox;

namespace GithubReleaseWatch
{
    public partial class SettingsWindow : Window
    {
        private bool _initializing = true;
        private bool _closed;
        private int _previewRequest;

        public ProxyConfig ProxyResult { get; private set; } = new();
        public string TokenResult { get; private set; } = "";
        public bool TokenEnabledResult { get; private set; } = false;
        public bool RefreshOnStartupResult { get; private set; } = true;

        public SettingsWindow(ProxyConfig proxy, string? token, bool tokenEnabled, bool refreshOnStartup)
        {
            InitializeComponent();

            ChkEnabled.IsChecked = proxy.Enabled;
            CmbType.SelectedIndex = string.Equals(proxy.Type, "Socks5", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
            TxtHost.Text = proxy.Host;
            TxtPort.Text = proxy.Port > 0 ? proxy.Port.ToString() : "";
            TxtUser.Text = proxy.Username ?? "";
            TxtPwd.Password = proxy.Password ?? "";
            TxtToken.Text = token ?? "";
            ChkTokenEnabled.IsChecked = tokenEnabled;
            ChkRefreshOnStartup.IsChecked = refreshOnStartup;
            UpdateProxyEnabled();

            _initializing = false;
            Closed += (_, _) => { _closed = true; _previewRequest++; };

            // 异步加载当前配额显示
            Loaded += async (_, _) => await RefreshRateLimitAsync();
        }

        private void ChkEnabled_Changed(object sender, RoutedEventArgs e) => UpdateProxyEnabled();

        private void UpdateProxyEnabled()
        {
            if (ProxyPanel != null) ProxyPanel.IsEnabled = ChkEnabled.IsChecked == true;
        }

        private async System.Threading.Tasks.Task RefreshRateLimitAsync()
        {
            int request = ++_previewRequest;
            try
            {
                var proxy = CollectProxy();
                if (proxy.ValidationError() is { } error) { TxtRateLimit.Text = error; return; }
                var snap = await GitHubApi.GetRateLimitSnapshotAsync(TxtToken.Text.Trim(), ChkTokenEnabled.IsChecked == true, proxy);
                if (_closed || request != _previewRequest) return;
                if (snap.Ok)
                {
                    var mode = snap.Authenticated ? "已认证（Token）" : "未认证（匿名）";
                    int used = snap.Limit - snap.Remaining;
                    TxtRateLimit.Text =
                        $"当前配额：已用 {used}/{snap.Limit}，剩 {snap.Remaining}（{mode}，{snap.ResetAt.LocalDateTime:HH:mm} 重置）";
                }
                else
                {
                    TxtRateLimit.Text = $"当前配额：获取失败 — {snap.Error}";
                }
            }
            catch (Exception ex)
            {
                if (!_closed && request == _previewRequest) TxtRateLimit.Text = $"当前配额：获取失败 — {ex.Message}";
            }
        }

        private ProxyConfig CollectProxy()
        {
            var type = (CmbType.SelectedItem as ComboBoxItem)?.Content as string == "SOCKS5" ? "Socks5" : "Http";
            return new ProxyConfig
            {
                Enabled = ChkEnabled.IsChecked == true,
                Type = type,
                Host = TxtHost.Text.Trim(),
                Port = int.TryParse(TxtPort.Text.Trim(), out var port) ? port : 0,
                Username = string.IsNullOrWhiteSpace(TxtUser.Text) ? null : TxtUser.Text.Trim(),
                Password = string.IsNullOrEmpty(TxtPwd.Password) ? null : TxtPwd.Password
            };
        }

        /// <summary>仅测试代理连通性。结果只显示可访问/不可访问。</summary>
        private async void BtnTestProxy_Click(object sender, RoutedEventArgs e)
        {
            var proxy = CollectProxy();
            if (!proxy.Enabled)
            {
                MessageBox.Show(this, "当前未启用代理，请先勾选「启用代理」并填写 IP/端口。",
                    "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            if (!ValidateProxy(proxy)) return;
            BtnTestProxy.IsEnabled = false;
            try
            {
                await GitHubApi.TestProxyAsync(proxy);
                if (_closed) return;
                MessageBox.Show(this,
                    $"✅ 走代理 {proxy.Host}:{proxy.Port}（{proxy.Type}）可访问 GitHub API。",
                    "测试代理", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                if (_closed) return;
                MessageBox.Show(this, "❌ 走代理无法访问 GitHub API：" + ex.Message, "测试代理",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                BtnTestProxy.IsEnabled = true;
            }
        }

        /// <summary>仅测试 Token 是否有效（带 Token 调用 GitHub rate_limit，应返回 5000/小时）。</summary>
        private async void BtnTestToken_Click(object sender, RoutedEventArgs e)
        {
            var token = TxtToken.Text.Trim();
            if (string.IsNullOrEmpty(token))
            {
                MessageBox.Show(this, "请先填写 GitHub Token（可点上方「🔑 获取 Token」按钮申请）。",
                    "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            var proxy = CollectProxy();
            if (!ValidateProxy(proxy)) return;
            BtnTestToken.IsEnabled = false;
            try
            {
                var result = await GitHubApi.TestTokenAsync(token, proxy);
                if (_closed) return;
                if (result.RateLimit >= 5000)
                {
                    MessageBox.Show(this,
                        $"✅ Token 有效！\n\nGitHub 已识别该 Token\n速率配额：{result.RateRemaining} / {result.RateLimit} 次/小时",
                        "测试 Token", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                else
                {
                    MessageBox.Show(this,
                        $"⚠ Token 已发送，但 GitHub 返回 limit={result.RateLimit}（期望 5000）。\n\n可能 Token 无效、或 GitHub 已撤销该 Token 的认证权限。请重新生成 Token 后再试。",
                        "测试 Token", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
                await RefreshRateLimitAsync(); // 刷新显示
            }
            catch (Exception ex)
            {
                if (_closed) return;
                MessageBox.Show(this, "❌ Token 测试失败：" + ex.Message, "测试 Token",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                BtnTestToken.IsEnabled = true;
            }
        }

        /// <summary>Token 启用开关变化时立即刷新配额显示（启用后会显示 5000/小时）。</summary>
        private async void ChkTokenEnabled_Changed(object sender, RoutedEventArgs e)
        {
            if (!_initializing && IsLoaded) await RefreshRateLimitAsync();
        }

        /// <summary>打开申请流程参考窗口（独立窗口，不在设置里展开）。</summary>
        private void BtnShowAllHelp_Click(object sender, RoutedEventArgs e)
        {
            new HelpWindow { Owner = this }.ShowDialog();
        }

        /// <summary>打开 GitHub 令牌创建页（只读公开仓库无需勾选任何权限）。</summary>
        private void BtnGetToken_Click(object sender, RoutedEventArgs e)
        {
            ExternalLinks.Open(this, "https://github.com/settings/tokens/new?description=GithubReleaseWatch");
        }

        private bool ValidateProxy(ProxyConfig proxy)
        {
            if (proxy.ValidationError() is not { } error) return true;
            MessageBox.Show(this, error, "格式错误", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }

        private void BtnOk_Click(object sender, RoutedEventArgs e)
        {
            var proxy = CollectProxy();
            if (!ValidateProxy(proxy)) return;
            ProxyResult = proxy;
            TokenResult = TxtToken.Text.Trim();
            TokenEnabledResult = ChkTokenEnabled.IsChecked == true;
            RefreshOnStartupResult = ChkRefreshOnStartup.IsChecked == true;
            DialogResult = true;
        }
    }
}
