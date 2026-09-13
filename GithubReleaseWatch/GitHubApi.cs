using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace GithubReleaseWatch
{
    /// <summary>GitHub Releases API 访问（可选 Token 认证 + HTTP/SOCKS5 代理 + 60 秒内存缓存）。</summary>
    public static class GitHubApi
    {
        private static HttpClient _http = CreateClient(null, null);

        // 60 秒缓存：同一 owner/repo 在窗口期内直接返回上次结果，避免用户反复点浪费 API 配额。
        private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(60);
        private static readonly ConcurrentDictionary<string, (DateTime At, List<ReleaseInfo> Data, string? RedirectedTo)> _cache = new();

        /// <summary>当前生效的代理设置（用于错误提示与连接诊断）。</summary>
        private static ProxyConfig? _currentProxy;
        /// <summary>当前生效的 Token（启用时）；未启用时为 null。</summary>
        private static string? _currentToken;

        /// <summary>应用 Token、Token 启用状态、代理设置（重建 HttpClient 并清缓存）。</summary>
        public static void Apply(string? token, bool tokenEnabled, ProxyConfig? proxy)
        {
            var effectiveToken = tokenEnabled ? token : null;
            var old = _http;
            _http = CreateClient(effectiveToken, proxy);
            _currentProxy = proxy;
            _currentToken = effectiveToken;
            old.Dispose();
            _cache.Clear(); // 配置变化后强制重新拉取
        }

        private static HttpClient CreateClient(string? token, ProxyConfig? proxy)
        {
            var handler = new SocketsHttpHandler
            {
                // 允许自动跟随重定向：GitHub 对已改名的仓库会返回 301，
                // 默认 HttpClient 不跟随，会被误报为失败。
                AllowAutoRedirect = true,
                MaxAutomaticRedirections = 5
            };
            if (proxy is { Enabled: true } && !string.IsNullOrWhiteSpace(proxy.Host))
            {
                var scheme = proxy.Type == "Socks5" ? "socks5" : "http";
                var webProxy = new WebProxy(new Uri($"{scheme}://{proxy.Host}:{proxy.Port}"));
                if (!string.IsNullOrEmpty(proxy.Username))
                    webProxy.Credentials = new NetworkCredential(proxy.Username, proxy.Password ?? "");
                handler.Proxy = webProxy;
            }
            var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("GithubReleaseWatch/1.0");
            client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
            if (!string.IsNullOrWhiteSpace(token))
                client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.Trim());
            return client;
        }

        /// <summary>测试连接结果（包含剩余配额）。</summary>
        public record TestResult(int RateLimit, int RateRemaining, bool Authenticated);

        /// <summary>当前速率配额快照（用于主面板/设置面板展示，/rate_limit 端点本身不计配额）。</summary>
        public record RateLimitSnapshot(int Limit, int Remaining, DateTimeOffset ResetAt, bool Authenticated, bool Ok, string? Error);

        /// <summary>测试代理：仅验证代理连通性，不使用 Token（始终匿名调用 rate_limit）。</summary>
        public static Task<TestResult> TestProxyAsync(ProxyConfig? proxy) =>
            TestConnectionAsync(token: null, proxy);

        /// <summary>测试 Token：验证 Token 是否有效（以当前代理或直连调用 rate_limit）。</summary>
        /// <remarks>返回的 TestResult.Authenticated 表示本次请求是否带了 Token；RateLimit=5000 即代表 Token 已被 GitHub 识别并提升到认证配额。</remarks>
        public static Task<TestResult> TestTokenAsync(string? token, ProxyConfig? proxy) =>
            TestConnectionAsync(token, proxy);

        /// <summary>用指定设置测试连接，并返回剩余配额（设置窗口"测试连接"按钮用）。</summary>
        public static async Task<TestResult> TestConnectionAsync(string? token, ProxyConfig? proxy)
        {
            using var client = CreateClient(token, proxy);
            using var response = await SendAsync(client, "https://api.github.com/rate_limit", proxy);
            response.EnsureSuccessStatusCode();

            await using var stream = await response.Content.ReadAsStreamAsync();
            using var doc = await JsonDocument.ParseAsync(stream);
            var resources = doc.RootElement.GetProperty("resources").GetProperty("core");
            var limit = resources.GetProperty("limit").GetInt32();
            var remaining = resources.GetProperty("remaining").GetInt32();
            // 有 Token 时通常 limit=5000，否则=60
            return new TestResult(limit, remaining, !string.IsNullOrWhiteSpace(token));
        }

        /// <summary>
        /// 获取当前生效通道的速率配额快照（主面板/设置面板用，调用 /rate_limit，**不消耗配额**）。
        /// </summary>
        /// <remarks>
        /// GitHub 官方保证 /rate_limit 端点不计配额：https://docs.github.com/en/rest/rate-limit
        /// 所以无论测试还是显示配额，都可以放心频繁调用。
        /// </remarks>
        public static async Task<RateLimitSnapshot> GetRateLimitSnapshotAsync()
        {
            try
            {
                using var response = await SendAsync(_http, "https://api.github.com/rate_limit", _currentProxy);
                response.EnsureSuccessStatusCode();
                await using var stream = await response.Content.ReadAsStreamAsync();
                using var doc = await JsonDocument.ParseAsync(stream);
                var core = doc.RootElement.GetProperty("resources").GetProperty("core");
                var limit = core.GetProperty("limit").GetInt32();
                var remaining = core.GetProperty("remaining").GetInt32();
                var resetUnix = core.GetProperty("reset").GetInt64();
                return new RateLimitSnapshot(limit, remaining,
                    DateTimeOffset.FromUnixTimeSeconds(resetUnix), !string.IsNullOrWhiteSpace(_currentToken),
                    Ok: true, Error: null);
            }
            catch (Exception ex)
            {
                return new RateLimitSnapshot(0, 0, DateTimeOffset.MinValue, !string.IsNullOrWhiteSpace(_currentToken),
                    Ok: false, Error: ex.Message);
            }
        }

        /// <summary>仓库 Release 列表（带 60 秒缓存）。</summary>
        public record ReleasesResult(List<ReleaseInfo> Releases, string? RedirectedTo);

        /// <summary>拉取仓库的 Release 列表（带 60 秒缓存）。返回 (列表, 是否重定向到新 owner/repo)。</summary>
        public static async Task<ReleasesResult> GetReleasesAsync(string owner, string repo)
        {
            var key = $"{owner}/{repo}";
            if (_cache.TryGetValue(key, out var hit) && DateTime.UtcNow - hit.At < CacheTtl)
                return new ReleasesResult(hit.Data, hit.RedirectedTo);

            var url = $"https://api.github.com/repos/{owner}/{repo}/releases?per_page=30";
            using var response = await SendAsync(_http, url, _currentProxy);

            if (response.StatusCode == HttpStatusCode.NotFound)
                throw new InvalidOperationException(
                    "仓库不存在或不可访问（404）。若该仓库近期改过名字，请用新地址重新添加。");
            if ((int)response.StatusCode is 301 or 302 or 307 or 308)
                throw new InvalidOperationException(
                    "仓库地址已变更（GitHub 返回重定向），自动跟随失败。请到 GitHub 确认新地址后用「➕ 添加仓库」重新添加。");
            if ((int)response.StatusCode == 401)
                throw new InvalidOperationException("Token 无效（401），请勾选「启用 Token」并检查 Token 设置。");
            if (response.StatusCode == HttpStatusCode.ProxyAuthenticationRequired)
                throw new InvalidOperationException("代理需要认证（407），请检查代理用户名/密码。");
            if ((int)response.StatusCode is 403 or 429)
            {
                var remaining = response.Headers.TryGetValues("X-RateLimit-Remaining", out var vals)
                    ? vals.FirstOrDefault() ?? "0"
                    : "0";
                var reset = response.Headers.TryGetValues("X-RateLimit-Reset", out var rv)
                    ? long.TryParse(rv.FirstOrDefault(), out var ts) ? DateTimeOffset.FromUnixTimeSeconds(ts).ToLocalTime().ToString("HH:mm") : "?"
                    : "?";
                // 判断本次请求是否真的带了 Token（区分"未启用"和"已启用但配额耗尽"）
                var hasToken = !string.IsNullOrWhiteSpace(_currentToken);

                if (hasToken)
                {
                    // 已认证：配额是按 Token 独立计数的 5000/小时，被刷爆说明自己用得太狠或被其他工具共用同一 Token
                    throw new InvalidOperationException(
                        $"GitHub API 速率受限（已认证 Token，本小时还剩 {remaining}/5000 次，{reset} 重置）。" +
                        " 该配额按 Token 独立统计（与你是否走代理无关）。" +
                        " 常见原因：① 同一 Token 被其他工具（如 CI / 其他脚本）共用；" +
                        "② 本机短时间内大量刷新。建议换一个专用 Token，或等待重置。");
                }

                if (_currentProxy is { Enabled: true })
                {
                    throw new InvalidOperationException(
                        $"GitHub API 速率受限（未认证，本小时还剩 {remaining}/60 次，{reset} 重置）。" +
                        " 当前走代理，未认证配额按「代理出口 IP」统计，公共代理节点可能被他人共享消耗。" +
                        " 配置并勾选「启用 Token」可提升到 5000/小时，且按 Token 独立计算，不受代理共享影响。");
                }

                throw new InvalidOperationException(
                    $"GitHub API 速率受限（未认证，本小时还剩 {remaining}/60 次，{reset} 重置）。" +
                    " 未认证配额为 60/小时，请在「⚙ 设置」中配置并勾选「启用 Token」提升到 5000/小时。");
            }
            response.EnsureSuccessStatusCode();

            await using var stream = await response.Content.ReadAsStreamAsync();
            var releases = await JsonSerializer.DeserializeAsync<List<ReleaseInfo>>(stream);
            releases ??= new List<ReleaseInfo>();

            // 检查"自动跟随重定向"是否实际触发了：响应 RequestUri 与请求路径不同即视为发生了迁移。
            // 注意：GitHub 对改名/转移的仓库，重定向目标是 **数字仓库 ID** 形式的路由
            //   https://api.github.com/repositories/836976013/releases
            // 而不是新的 owner/repo 路径，所以不能靠正则从 URL 里直接抠出新名字，
            // 需要再查一次 /repositories/{id} 才能拿到 full_name。
            string? canonicalUrl = response.RequestMessage?.RequestUri?.AbsoluteUri;
            string? redirectedTo = null;
            if (canonicalUrl != null && !canonicalUrl.Contains($"/{owner}/{repo}", StringComparison.OrdinalIgnoreCase))
            {
                redirectedTo = await ResolveCanonicalUrlAsync(canonicalUrl);
            }

            _cache[key] = (DateTime.UtcNow, releases, redirectedTo);
            return new ReleasesResult(releases, redirectedTo);
        }

        /// <summary>
        /// 调用 GitHub /markdown API 将 Markdown 渲染为 HTML（与 GitHub 网页保持一致）。
        /// 该端点通常不计入 API 配额，支持 GFM 模式与仓库上下文（用于解析 #issue 等引用）。
        /// </summary>
        public static async Task<string> RenderMarkdownAsync(string markdown, string? owner = null, string? repo = null)
        {
            if (string.IsNullOrWhiteSpace(markdown)) return "";

            var payload = new
            {
                text = markdown,
                mode = "gfm",
                context = (!string.IsNullOrWhiteSpace(owner) && !string.IsNullOrWhiteSpace(repo))
                    ? $"{owner}/{repo}"
                    : null
            };

            using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.github.com/markdown")
            {
                Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json")
            };
            request.Headers.Accept.Clear();
            request.Headers.Accept.ParseAdd("text/html");

            using var response = await _http.SendAsync(request);
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadAsStringAsync();
        }

        /// <summary>
        /// 把重定向后的 URL 解析成可用的新仓库地址。
        /// 两种情况：
        ///   1) /repos/{newOwner}/{newRepo}/releases  → 直接取
        ///   2) /repositories/{id}/releases           → 查 /repositories/{id} 拿 full_name
        /// 解析失败返回 null（不影响主流程，下次刷新会再试）。
        /// </summary>
        private static async Task<string?> ResolveCanonicalUrlAsync(string canonicalUrl)
        {
            // 情况 1：直接是 owner/repo 路径
            var m = System.Text.RegularExpressions.Regex.Match(
                canonicalUrl, @"/repos/([^/]+)/([^/?#]+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (m.Success)
                return $"https://github.com/{m.Groups[1].Value}/{m.Groups[2].Value}";

            // 情况 2：/repositories/{id}
            var idMatch = System.Text.RegularExpressions.Regex.Match(
                canonicalUrl, @"/repositories/(\d+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (!idMatch.Success) return null;

            try
            {
                using var resp = await SendAsync(_http, $"https://api.github.com/repositories/{idMatch.Groups[1].Value}", _currentProxy);
                if (!resp.IsSuccessStatusCode) return null;
                await using var stream = await resp.Content.ReadAsStreamAsync();
                using var doc = await JsonDocument.ParseAsync(stream);
                if (doc.RootElement.TryGetProperty("full_name", out var fullName))
                {
                    var fn = fullName.GetString();
                    if (!string.IsNullOrWhiteSpace(fn))
                        return $"https://github.com/{fn}";
                }
            }
            catch
            {
                // 解析失败不抛，交由下次刷新重试
            }
            return null;
        }

        /// <summary>发送请求，并把网络层异常翻译成可操作的提示。</summary>
        private static async Task<HttpResponseMessage> SendAsync(HttpClient client, string url, ProxyConfig? proxy)
        {
            try
            {
                return await client.GetAsync(url);
            }
            catch (TaskCanceledException)
            {
                throw new InvalidOperationException(
                    proxy is { Enabled: true }
                        ? $"请求超时（30 秒）。请检查代理 {proxy.Host}:{proxy.Port} 是否可用、类型（HTTP/SOCKS5）是否匹配。"
                        : "请求超时（30 秒）。请检查网络连接，或在'设置'中配置代理。");
            }
            catch (HttpRequestException ex)
            {
                var hint = proxy is { Enabled: true }
                    ? $"代理 {proxy.Host}:{proxy.Port}（{proxy.Type}）连接失败，请确认地址/端口/类型正确且代理正在运行。"
                    : "直连失败，请在'设置'中配置代理（HTTP 或 SOCKS5）。";
                throw new InvalidOperationException($"{hint} 详情：{ex.Message}", ex);
            }
            catch (SocketException ex)
            {
                throw new InvalidOperationException($"网络不可达：{ex.Message}", ex);
            }
        }
    }
}