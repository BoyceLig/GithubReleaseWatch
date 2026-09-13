using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace GithubReleaseWatch
{
    /// <summary>单个仓库的监测配置（持久化到 config.json）。</summary>
    public class RepoConfig
    {
        public string Url { get; set; } = "";
        public string Owner { get; set; } = "";
        public string Repo { get; set; } = "";
        public string CurrentVersion { get; set; } = "";
    }

    /// <summary>应用全局配置。</summary>
    public class AppConfig
    {
        public List<RepoConfig> Repos { get; set; } = new();
        public bool ShowPrerelease { get; set; } = false;
        public string? Token { get; set; }
        /// <summary>Token 启用开关：勾选后才真正把 Token 带到请求里；取消勾选只保留 Token 字符串。</summary>
        public bool TokenEnabled { get; set; } = false;
        public ProxyConfig? Proxy { get; set; }
        /// <summary>打开软件时是否自动刷新所有仓库。</summary>
        public bool RefreshOnStartup { get; set; } = true;
    }

    /// <summary>代理设置（HTTP / SOCKS5，可选账号密码）。</summary>
    public class ProxyConfig
    {
        public bool Enabled { get; set; } = false;
        public string Type { get; set; } = "Http"; // Http | Socks5
        public string Host { get; set; } = "";
        public int Port { get; set; } = 7890;
        public string? Username { get; set; }
        public string? Password { get; set; }
    }

    /// <summary>GitHub Release（API 返回字段的子集 + 展示用计算属性）。</summary>
    public class ReleaseInfo : INotifyPropertyChanged
    {
        [JsonPropertyName("tag_name")] public string TagName { get; set; } = "";
        [JsonPropertyName("name")] public string Name { get; set; } = "";
        [JsonPropertyName("body")] public string Body { get; set; } = "";
        [JsonPropertyName("prerelease")] public bool Prerelease { get; set; }
        [JsonPropertyName("draft")] public bool Draft { get; set; }
        [JsonPropertyName("published_at")] public DateTime PublishedAt { get; set; }
        [JsonPropertyName("html_url")] public string HtmlUrl { get; set; } = "";

        [JsonIgnore] public string BodyText =>
            string.IsNullOrWhiteSpace(Body) ? "（无版本说明）" : Body.Trim();

        [JsonIgnore] public System.Windows.Documents.FlowDocument BodyDocument =>
            MarkdownRenderer.Render(Body);

        [JsonIgnore] public string PublishedAtText =>
            PublishedAt == default ? "" : PublishedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm");

        /// <summary>GitHub /markdown API 渲染后的 HTML（由界面 WebBrowser 显示）。</summary>
        private string? _bodyHtml;
        [JsonIgnore]
        public string? BodyHtml
        {
            get => _bodyHtml;
            set { _bodyHtml = value; OnPropertyChanged(); }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string? name = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name!));
    }

    /// <summary>config.json 读写（保存在 exe 同目录）。</summary>
    public static class ConfigStore
    {
        private static readonly string ConfigPath = Path.Combine(AppContext.BaseDirectory, "config.json");
        private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

        public static AppConfig Load()
        {
            try
            {
                if (!File.Exists(ConfigPath)) return new AppConfig();
                return JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(ConfigPath), JsonOptions)
                       ?? new AppConfig();
            }
            catch
            {
                return new AppConfig();
            }
        }

        public static void Save(AppConfig config)
        {
            try
            {
                File.WriteAllText(ConfigPath, JsonSerializer.Serialize(config, JsonOptions));
            }
            catch
            {
                // 配置写入失败（目录只读等）时忽略，避免崩溃
            }
        }
    }
}
