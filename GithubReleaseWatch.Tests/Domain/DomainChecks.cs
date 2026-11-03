using System.IO;
using System.Text.Json;
using static GithubReleaseWatch.Tests.Program;

namespace GithubReleaseWatch.Tests;

internal static class DomainChecks
{
    internal static void Run()
    {
        Test("GitHub URL normalization and .git suffix", () =>
        {
            foreach (var url in new[] { " https://github.com/Owner/Repo.git ", "github.com/Owner/Repo/releases/tag/v1", "HTTP://GITHUB.COM/Owner/Repo?tab=readme#notes" })
                Check(GitHubRepository.TryParse(url, out var repo) && repo.Owner == "Owner" && repo.Name == "Repo" && repo.Url == "https://github.com/Owner/Repo", url);
        });
        Test("Reject malformed and lookalike repository addresses", () =>
        {
            foreach (var url in new[] { "", "https://github.com/owner", "https://notgithub.com/a/b", "https://github.com.evil/a/b", "https://github.com@evil/a/b", "file:///github.com/a/b", "https://github.com/a/.git", "https://github.com:123/a/b", "https://github.com/a/b%2fc" })
                Check(!GitHubRepository.TryParse(url, out _), url);
        });
        Test("Version prefixes, unequal segments and large numeric values", () =>
        {
            Check(VersionUtil.Compare(" v1.2 ", "1.2.0") == 0, "prefix/trailing zero");
            Check(VersionUtil.Compare("1.10", "1.9") > 0, "numeric ordering");
            Check(VersionUtil.Compare("1.999999999999999999999", "1.99999999999999999999") > 0, "overflow");
            Check(VersionUtil.Compare(null, null) == 0, "empty tags");
            Check(VersionUtil.Compare("1.0.0+build.1", "1.0.0+build.2") == 0, "metadata");
        });
        Test("SemVer prerelease precedence", () =>
        {
            string[] ordered = ["1.0.0-alpha", "1.0.0-alpha.1", "1.0.0-alpha.beta", "1.0.0-beta", "1.0.0-beta.2", "1.0.0-beta.11", "1.0.0-rc.1", "1.0.0"];
            for (int i = 0; i < ordered.Length - 1; i++)
                Check(VersionUtil.Compare(ordered[i], ordered[i + 1]) < 0 && VersionUtil.Compare(ordered[i + 1], ordered[i]) > 0, ordered[i]);
        });
        Test("Drag destinations cover first, last, single and unchanged positions", () =>
        {
            Check(ListBoxDragReorder.DestinationIndex(0, 3, 3) == 2, "to end");
            Check(ListBoxDragReorder.DestinationIndex(2, 0, 3) == 0, "to start");
            Check(ListBoxDragReorder.DestinationIndex(1, 2, 3) == 1, "same slot");
            Check(ListBoxDragReorder.DestinationIndex(0, 1, 1) == 0, "single");
            for (int count = 1; count < 10; count++)
                for (int source = 0; source < count; source++)
                    for (int slot = 0; slot <= count; slot++)
                    {
                        var items = Enumerable.Range(0, count).ToList();
                        int destination = ListBoxDragReorder.DestinationIndex(source, slot, count);
                        items.RemoveAt(source); items.Insert(destination, source);
                        Check(items.Count == count && items.Distinct().Count() == count, "lost item");
                    }
        });
        Test("Invalid browser messages cannot escape numeric/type boundaries", () =>
        {
            foreach (var json in new[] { "null", "[]", "{}", "{", "{\"type\":1,\"value\":1}", "{\"type\":\"height\",\"value\":-1}", "{\"type\":\"height\",\"value\":1e999}", "{\"type\":\"wheel\",\"value\":2147483648}", "{\"type\":\"wheel\",\"value\":1.1}", "{\"type\":\"unknown\",\"value\":1}" })
                Check(!MarkdownMessage.TryParse(json, out _), json);
            Check(MarkdownMessage.TryParse("{\"type\":\"height\",\"value\":5000}", out var message) && message.Value == 5000, "height");
            Check(MarkdownMessage.TryParse("{\"type\":\"wheel\",\"value\":-120}", out _), "wheel");
        });
        Test("Nullable GitHub draft timestamps", () =>
        {
            var release = JsonSerializer.Deserialize<ReleaseInfo>("{\"draft\":true,\"published_at\":null}")!;
            Check(release.PublishedAtText == "", "null publication");
        });
        Test("Proxy validation includes disabled settings, range and IPv6", () =>
        {
            Check(new ProxyConfig { Enabled = false, Port = -1 }.ValidationError() == null, "disabled");
            Check(new ProxyConfig { Enabled = true, Host = "localhost", Port = 0 }.ValidationError() != null, "zero port");
            Check(new ProxyConfig { Enabled = true, Host = "http://localhost", Port = 80 }.ValidationError() != null, "protocol");
            Check(new ProxyConfig { Enabled = true, Host = "::1", Port = 7890 }.ValidationError() == null, "IPv6");
        });
        Test("Config roundtrip, null collections and atomic failure preserve files", () =>
        {
            string root = Path.Combine(AppContext.BaseDirectory, "test-data", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            string path = Path.Combine(root, "config.json");
            try
            {
                File.WriteAllText(path, "{\"Repos\":null}");
                Check(ConfigStore.Load(path).Repos.Count == 0, "null list");
                File.WriteAllText(path, "{\"Repos\":[null,{\"Url\":\"https://github.com/a/b.git\",\"CurrentVersion\":null}],\"Proxy\":{\"Enabled\":true,\"Host\":\"localhost\",\"Port\":0}}");
                var config = ConfigStore.Load(path);
                Check(config.Repos.Count == 1 && config.Repos[0].Repo == "b" && config.Repos[0].CurrentVersion == "", "normalization");
                Check(config.Proxy?.Enabled == false, "invalid proxy on startup");
                Check(ConfigStore.Save(config, path), "save");
                string before = File.ReadAllText(path);
                using (var locked = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                    Check(!ConfigStore.Save(new(), path), "locked target should fail");
                Check(File.ReadAllText(path) == before && !Directory.GetFiles(root, "*.tmp").Any(), "old file preserved / no temp leak");
                File.WriteAllText(path, "broken json");
                Check(ConfigStore.Load(path).Repos.Count == 0 && Directory.GetFiles(root, "*.corrupt-*").Length == 1, "corrupt backup");
                Check(ConfigStore.Save(new(), path), "recover");
            }
            finally { Directory.Delete(root, true); }
        });
        Test("Summary notifications keep release controls and expansion intact", () =>
        {
            bool showPrerelease = false;
            var vm = new RepoViewModel(new() { CurrentVersion = "1.0" }, () => showPrerelease);
            var source = new List<ReleaseInfo> { new() { TagName = "2.0-beta", Prerelease = true }, new() { TagName = "1.1" }, new() { TagName = "draft", Draft = true } };
            vm.SetReleases(source);
            var visible = vm.VisibleReleases.Single(); visible.IsExpanded = false;
            var notifications = new List<string?>(); vm.PropertyChanged += (_, e) => notifications.Add(e.PropertyName);
            vm.SetCurrent("1.1"); vm.IsLoading = true; vm.IsLoading = false;
            Check(!notifications.Contains(nameof(vm.VisibleReleases)) && ReferenceEquals(visible, vm.VisibleReleases.Single()), "unnecessary card recreation");
            vm.SetReleases(source);
            Check(!vm.VisibleReleases.Single().IsExpanded && source[1].IsExpanded, "expansion independent from API cache");
            showPrerelease = true; vm.OnFilterChanged();
            Check(vm.VisibleReleases.Count() == 2, "filter");
            vm.Config.CurrentVersion = "2.0"; notifications.Clear(); vm.OnConfigChanged();
            Check(notifications.Contains(nameof(vm.CurrentVersion)), "edited version binding");
        });
        Test("Empty, draft-only and prerelease-only lists keep correct summaries", () =>
        {
            bool show = false;
            var vm = new RepoViewModel(new(), () => show);
            vm.SetReleases([]);
            Check(vm.LatestVersion == "无 Release" && !vm.VisibleReleases.Any(), "empty list");
            vm.SetReleases([new() { Draft = true }]);
            Check(vm.LatestVersion == "无 Release", "draft-only");
            vm.SetReleases(Enumerable.Range(0, 8).Select(i => new ReleaseInfo { TagName = $"1.0-beta.{8 - i}", Prerelease = true }).ToList());
            Check(vm.LatestVersion == "无稳定版本" && !vm.VisibleReleases.Any(), "filtered preview");
            show = true; vm.OnFilterChanged();
            Check(vm.VisibleReleases.Count() == 3 && vm.BadgeText == "未设置", "three-card limit / missing current version");
            vm.SetError("test error");
            Check(vm.BadgeText == "失败" && !vm.VisibleReleases.Any(), "error clears old notes");
        });
    }
}
