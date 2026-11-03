using System.Collections.Concurrent;
using System.Net.Http;

namespace GithubReleaseWatch;

/// <summary>一次网络配置的快照。切换设置后，进行中的请求完成才释放旧 HttpClient。</summary>
internal sealed class GitHubApiSession(HttpClient http, string? token, ProxyConfig? proxy) : IDisposable
{
    private int _references = 1;
    public HttpClient Http { get; } = http;
    public string? Token { get; } = token;
    public ProxyConfig? Proxy { get; } = proxy;
    public ConcurrentDictionary<string, (DateTime At, List<ReleaseInfo> Data, string? RedirectedTo)> Cache { get; } =
        new(StringComparer.OrdinalIgnoreCase);

    public void AddReference() => Interlocked.Increment(ref _references);
    public void Dispose()
    {
        if (Interlocked.Decrement(ref _references) == 0) Http.Dispose();
    }
}
