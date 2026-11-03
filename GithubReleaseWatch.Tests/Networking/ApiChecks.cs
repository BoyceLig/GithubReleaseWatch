using System.Net;
using System.Net.Http;
using static GithubReleaseWatch.Tests.Program;

namespace GithubReleaseWatch.Tests;

internal sealed class FakeHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond) : HttpMessageHandler
{
    internal bool Disposed;
    internal int Requests;
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
    {
        Interlocked.Increment(ref Requests);
        var result = await respond(request);
        result.RequestMessage ??= request;
        return result;
    }
    protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
    internal static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(json) };
    internal static void Apply(FakeHandler handler, string? token = null) =>
        GitHubApi.ApplySession(new(new HttpClient(handler), token, null));
}

internal static class ApiChecks
{
    internal static async Task RunAsync()
    {
        await TestAsync("API cache ignores repository casing and handles nullable fields", async () =>
        {
            var handler = new FakeHandler(_ => Task.FromResult(FakeHandler.Json("[null,{\"tag_name\":null,\"body\":null,\"draft\":true,\"published_at\":null},{\"tag_name\":\"v1\"}]")));
            FakeHandler.Apply(handler);
            var result = await GitHubApi.GetReleasesAsync("owner", "repo");
            await GitHubApi.GetReleasesAsync("OWNER", "REPO");
            Check(handler.Requests == 1 && result.Releases.Count == 2 && result.Releases[0].Body == "", "cache/null field handling");
        });
        await TestAsync("Changing network settings lets old requests finish without stale cache reuse", async () =>
        {
            var pending = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
            var previous = new FakeHandler(_ => pending.Task);
            FakeHandler.Apply(previous);
            var request = GitHubApi.GetReleasesAsync("a", "b");
            var next = new FakeHandler(_ => Task.FromResult(FakeHandler.Json("[{\"tag_name\":\"new\"}]")));
            FakeHandler.Apply(next);
            Check(!previous.Disposed, "inflight client disposed early");
            pending.SetResult(FakeHandler.Json("[{\"tag_name\":\"old\"}]"));
            Check((await request).Releases.Single().TagName == "old" && previous.Disposed, "old request completion/disposal");
            Check((await GitHubApi.GetReleasesAsync("a", "b")).Releases.Single().TagName == "new" && next.Requests == 1, "new settings reused old cache");
        });
        await TestAsync("HTTP errors and malformed reset headers return actionable messages", async () =>
        {
            foreach (var (status, expected) in new[] { (HttpStatusCode.NotFound, "404"), (HttpStatusCode.Unauthorized, "401"), (HttpStatusCode.ProxyAuthenticationRequired, "407"), (HttpStatusCode.TooManyRequests, "速率受限") })
            {
                var handler = new FakeHandler(_ =>
                {
                    var response = FakeHandler.Json("{}", status);
                    response.Headers.TryAddWithoutValidation("X-RateLimit-Reset", "9223372036854775807");
                    return Task.FromResult(response);
                });
                FakeHandler.Apply(handler);
                try { await GitHubApi.GetReleasesAsync("a", "b"); throw new Exception("request should fail"); }
                catch (InvalidOperationException ex) { Check(ex.Message.Contains(expected), ex.Message); }
            }
        });
        await TestAsync("Repository ID redirects resolve canonical names", async () =>
        {
            var handler = new FakeHandler(request =>
            {
                if (request.RequestUri!.AbsolutePath == "/repositories/123")
                    return Task.FromResult(FakeHandler.Json("{\"full_name\":\"new-owner/new-repo\"}"));
                var response = FakeHandler.Json("[]");
                response.RequestMessage = new(HttpMethod.Get, "https://api.github.com/repositories/123/releases");
                return Task.FromResult(response);
            });
            FakeHandler.Apply(handler);
            var result = await GitHubApi.GetReleasesAsync("old-owner", "old-repo");
            Check(result.RedirectedTo == "https://github.com/new-owner/new-repo" && handler.Requests == 2, "canonical URL");
        });
        await TestAsync("Rate-limit parser covers success and invalid JSON", async () =>
        {
            FakeHandler.Apply(new(_ => Task.FromResult(FakeHandler.Json("{\"resources\":{\"core\":{\"limit\":5000,\"remaining\":4999,\"reset\":1700000000}}}"))), "test-token");
            var snapshot = await GitHubApi.GetRateLimitSnapshotAsync();
            Check(snapshot.Ok && snapshot.Authenticated && snapshot.Remaining == 4999, "rate limit");
            FakeHandler.Apply(new(_ => Task.FromResult(FakeHandler.Json("not-json"))));
            Check(!(await GitHubApi.GetRateLimitSnapshotAsync()).Ok, "malformed rate limit");
        });
        await TestAsync("Empty release notes get a placeholder without a network call", async () =>
        {
            var handler = new FakeHandler(_ => throw new Exception("should not access network"));
            FakeHandler.Apply(handler);
            var release = new ReleaseInfo();
            await ReleaseMarkdownService.RenderAsync([release], "a", "b");
            Check(handler.Requests == 0 && release.BodyHtml!.Contains("无版本说明"), "blank notes");
        });
        await TestAsync("Markdown failures preserve escaped source", async () =>
        {
            FakeHandler.Apply(new(_ => Task.FromResult(FakeHandler.Json("{}", HttpStatusCode.ServiceUnavailable))));
            var release = new ReleaseInfo { Body = "<script>alert(1)</script>" };
            await ReleaseMarkdownService.RenderAsync([release], "a", "b");
            Check(release.BodyHtml!.Contains("&lt;script&gt;alert(1)&lt;/script&gt;"), "fallback escaped");
        });
        await TestAsync("Markdown requests preserve GitHub GFM mode and repository context", async () =>
        {
            FakeHandler.Apply(new(async request =>
            {
                Check(request.Method == HttpMethod.Post && request.RequestUri!.AbsolutePath == "/markdown", "markdown endpoint");
                using var json = System.Text.Json.JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
                Check(json.RootElement.GetProperty("mode").GetString() == "gfm" &&
                    json.RootElement.GetProperty("context").GetString() == "owner/repo", "GitHub rendering context");
                return new(HttpStatusCode.OK) { Content = new StringContent("<p>rendered</p>") };
            }));
            Check(await GitHubApi.RenderMarkdownAsync("#issue", "owner", "repo") == "<p>rendered</p>", "HTML response");
        });
        await TestAsync("Markdown concurrency is bounded and rendered HTML is reused", async () =>
        {
            int active = 0, maximum = 0;
            var handler = new FakeHandler(async _ =>
            {
                int current = Interlocked.Increment(ref active);
                maximum = Math.Max(maximum, current);
                await Task.Delay(20);
                Interlocked.Decrement(ref active);
                return new(HttpStatusCode.OK) { Content = new StringContent("<p>GitHub rendered HTML</p>") };
            });
            FakeHandler.Apply(handler);
            var releases = Enumerable.Range(0, 12).Select(i => new ReleaseInfo { Body = "note " + i }).ToList();
            await ReleaseMarkdownService.RenderAsync(releases, "a", "b");
            await ReleaseMarkdownService.RenderAsync(releases, "a", "b");
            Check(maximum <= 4 && handler.Requests == 12 && releases.All(r => r.BodyHtml!.Contains("GitHub rendered HTML")), "render limits/cache");
        });
    }
}
