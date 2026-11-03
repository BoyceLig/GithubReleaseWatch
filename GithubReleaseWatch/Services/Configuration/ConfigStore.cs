using System.Diagnostics;
using System.IO;
using System.Text.Json;

namespace GithubReleaseWatch;

/// <summary>配置读写；先完整写入临时文件，再原子替换，避免中断留下半份 JSON。</summary>
public static class ConfigStore
{
    private static readonly string DefaultPath = Path.Combine(AppContext.BaseDirectory, "config.json");
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    public static string? LastError { get; private set; }

    public static AppConfig Load(string? path = null)
    {
        path ??= DefaultPath;
        LastError = null;
        try
        {
            if (!File.Exists(path)) return new();
            var config = JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(path), JsonOptions) ?? new();
            config.Repos ??= new();
            config.Repos = config.Repos.Where(r => r != null).ToList();
            foreach (var repo in config.Repos)
            {
                repo.Owner ??= "";
                repo.Repo ??= "";
                repo.Url ??= "";
                repo.CurrentVersion ??= "";
                if (GitHubRepository.TryParse(repo.Url, out var parsed))
                {
                    repo.Owner = parsed.Owner;
                    repo.Repo = parsed.Name;
                    repo.Url = parsed.Url;
                }
            }
            if (config.Proxy?.ValidationError() is { } proxyError)
            {
                config.Proxy.Enabled = false;
                LastError = $"代理配置无效，已暂时停用：{proxyError}";
            }
            return config;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            LastError = $"配置读取失败：{ex.Message}";
            Trace.WriteLine(LastError);
            // 避免随后保存默认设置时覆盖唯一一份损坏配置。
            try { File.Copy(path, path + $".corrupt-{Guid.NewGuid():N}"); }
            catch (Exception backupError) { Trace.WriteLine($"[ConfigStore] Backup: {backupError.Message}"); }
            return new();
        }
    }

    public static bool Save(AppConfig config, string? path = null)
    {
        path ??= DefaultPath;
        string temporary = path + $".{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(config, JsonOptions));
            File.Move(temporary, path, overwrite: true);
            LastError = null;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            LastError = $"配置保存失败：{ex.Message}";
            Trace.WriteLine(LastError);
            return false;
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); }
            catch (Exception ex) { Trace.WriteLine($"[ConfigStore] Cleanup: {ex.Message}"); }
        }
    }
}
