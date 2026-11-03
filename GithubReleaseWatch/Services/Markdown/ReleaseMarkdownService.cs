namespace GithubReleaseWatch;

/// <summary>保留 GitHub GFM 渲染，同时限制并发，空内容及网络失败均有可见回退。</summary>
internal static class ReleaseMarkdownService
{
    private static readonly SemaphoreSlim RenderSlots = new(4);

    public static Task RenderAsync(IEnumerable<ReleaseInfo> releases, string owner, string repo) =>
        Task.WhenAll(releases.Where(r => !r.Draft && string.IsNullOrEmpty(r.BodyHtml))
            .Select(r => RenderOneAsync(r, owner, repo)));

    private static async Task RenderOneAsync(ReleaseInfo release, string owner, string repo)
    {
        if (string.IsNullOrWhiteSpace(release.Body))
        {
            release.BodyHtml = GitHubHtmlPage.Wrap(null);
            return;
        }
        await RenderSlots.WaitAsync();
        try
        {
            var html = await GitHubApi.RenderMarkdownAsync(release.Body, owner, repo);
            release.BodyHtml = GitHubHtmlPage.Wrap(html);
        }
        catch
        {
            release.BodyHtml = GitHubHtmlPage.Wrap($"<pre>{System.Net.WebUtility.HtmlEncode(release.Body)}</pre>");
        }
        finally { RenderSlots.Release(); }
    }
}
