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
        public ProxyConfig Copy() => (ProxyConfig)MemberwiseClone();

        public string? ValidationError()
        {
            if (!Enabled) return null;
            if (string.IsNullOrWhiteSpace(Host)) return "请填写代理服务器地址。";
            if (Port is < 1 or > 65535) return "端口号无效（1-65535）。";
            if (Uri.CheckHostName(Host.Trim('[', ']')) == UriHostNameType.Unknown)
                return "代理服务器请只填写 IP 或域名，不要包含协议或路径。";
            return null;
        }

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
        [JsonPropertyName("published_at")] public DateTime? PublishedAt { get; set; }
        [JsonPropertyName("html_url")] public string HtmlUrl { get; set; } = "";

        [JsonIgnore]
        public string PublishedAtText =>
            PublishedAt is null || PublishedAt == default(DateTime) ? "" : PublishedAt.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm");

        private bool _isExpanded = true;
        [JsonIgnore]
        public bool IsExpanded
        {
            get => _isExpanded;
            set
            {
                if (_isExpanded == value) return;
                _isExpanded = value;
                OnPropertyChanged();
            }
        }

        /// <summary>GitHub /markdown API 渲染后的 HTML。</summary>
        private string? _bodyHtml;
        [JsonIgnore]
        public string? BodyHtml
        {
            get => _bodyHtml;
            set { if (_bodyHtml == value) return; _bodyHtml = value; OnPropertyChanged(); }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string? name = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name!));
    }

}
