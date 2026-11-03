using System.Text.RegularExpressions;

namespace GithubReleaseWatch;

/// <summary>添加、编辑和迁移仓库共用的 GitHub 地址解析。</summary>
public sealed record GitHubRepository(string Owner, string Name)
{
    private static readonly Regex Segment = new(@"^[\w.\-]+$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    public string Url => $"https://github.com/{Owner}/{Name}";

    public static bool TryParse(string? value, out GitHubRepository repository)
    {
        repository = null!;
        value = value?.Trim();
        if (string.IsNullOrEmpty(value)) return false;
        if (value.StartsWith("github.com/", StringComparison.OrdinalIgnoreCase)) value = "https://" + value;
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            (uri.Scheme != "https" && uri.Scheme != "http") ||
            !string.Equals(uri.Host, "github.com", StringComparison.OrdinalIgnoreCase) ||
            !string.IsNullOrEmpty(uri.UserInfo) || !uri.IsDefaultPort) return false;

        var parts = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2) return false;
        string owner = parts[0], name = parts[1];
        if (name.EndsWith(".git", StringComparison.OrdinalIgnoreCase)) name = name[..^4];
        if (!Segment.IsMatch(owner) || !Segment.IsMatch(name) ||
            owner is "." or ".." || name is "." or "..") return false;
        repository = new(owner, name);
        return true;
    }

    public bool Matches(RepoConfig config) =>
        string.Equals(Owner, config.Owner, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(Name, config.Repo, StringComparison.OrdinalIgnoreCase);
}
