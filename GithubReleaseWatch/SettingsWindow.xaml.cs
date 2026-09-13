using System;
using System.Windows;
using System.Windows.Controls;
using MessageBox = System.Windows.MessageBox;

namespace GithubReleaseWatch
{
    public partial class SettingsWindow : Window
    {
        public ProxyConfig ProxyResult { get; private set; } = new();
        public string TokenResult { get; private set; } = "";
        public bool TokenEnabledResult { get; private set; } = false;
        public bool RefreshOnStartupResult { get; private set; } = true;

        public SettingsWindow(ProxyConfig proxy, string? token, bool tokenEnabled, bool refreshOnStartup)
        {
            InitializeComponent();

            ChkEnabled.IsChecked = proxy.Enabled;
            CmbType.SelectedIndex = proxy.Type == "Socks5" ? 1 : 0;
            TxtHost.Text = proxy.Host;
            TxtPort.Text = proxy.Port > 0 ? proxy.Port.ToString() : "";
            TxtUser.Text = proxy.Username ?? "";
            TxtPwd.Password = proxy.Password ?? "";
            TxtToken.Text = token ?? "";
            ChkTokenEnabled.IsChecked = tokenEnabled;
            ChkRefreshOnStartup.IsChecked = refreshOnStartup;
            UpdateProxyEnabled();

            // 异步加载当前配额显示
            Loaded += async (_, _) => await RefreshRateLimitAsync();
        }

        private void ChkEnabled_Changed(object sender, RoutedEventArgs e) => UpdateProxyEnabled();

        private void UpdateProxyEnabled() => ProxyPanel.IsEnabled = ChkEnabled.IsChecked == true;

        private async System.Threading.Tasks.Task RefreshRateLimitAsync()
        {
            try
            {
                var snap = await GitHubApi.GetRateLimitSnapshotAsync();
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
                TxtRateLimit.Text = $"当前配额：获取失败 — {ex.Message}";
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
                Password = string.IsNullOrWhiteSpace(TxtPwd.Password) ? null : TxtPwd.Password
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
            if (string.IsNullOrWhiteSpace(proxy.Host))
            {
                MessageBox.Show(this, "请先填写代理服务器地址。", "提示",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            BtnTestProxy.IsEnabled = false;
            try
            {
                await GitHubApi.TestProxyAsync(proxy);
                MessageBox.Show(this,
                    $"✅ 走代理 {proxy.Host}:{proxy.Port}（{proxy.Type}）可访问 GitHub API。",
                    "测试代理", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
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
            BtnTestToken.IsEnabled = false;
            try
            {
                var result = await GitHubApi.TestTokenAsync(token, CollectProxy());
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
            GitHubApi.Apply(TxtToken.Text.Trim(), ChkTokenEnabled.IsChecked == true, CollectProxy());
            await RefreshRateLimitAsync();
        }

        /// <summary>打开申请流程参考窗口（独立窗口，不在设置里展开）。</summary>
        private void BtnShowAllHelp_Click(object sender, RoutedEventArgs e)
        {
            new HelpWindow { Owner = this }.ShowDialog();
        }

        /// <summary>打开 GitHub 令牌创建页（只读公开仓库无需勾选任何权限）。</summary>
        private void BtnGetToken_Click(object sender, RoutedEventArgs e)
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "https://github.com/settings/tokens/new?description=GithubReleaseWatch",
                UseShellExecute = true
            });
        }

        private void BtnOk_Click(object sender, RoutedEventArgs e)
        {
            var proxy = CollectProxy();
            if (proxy.Enabled)
            {
                if (string.IsNullOrWhiteSpace(proxy.Host))
                {
                    MessageBox.Show(this, "请填写代理服务器地址。", "格式错误", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
                if (proxy.Port is < 1 or > 65535)
                {
                    MessageBox.Show(this, "端口号无效（1-65535）。", "格式错误", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
            }
            ProxyResult = proxy;
            TokenResult = TxtToken.Text.Trim();
            TokenEnabledResult = ChkTokenEnabled.IsChecked == true;
            RefreshOnStartupResult = ChkRefreshOnStartup.IsChecked == true;
            DialogResult = true;
        }
    }
}