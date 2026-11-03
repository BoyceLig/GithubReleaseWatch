using System.IO;
using System.Reflection;
using System.Net.Http;
using System.Windows.Controls;
using static GithubReleaseWatch.Tests.Program;

namespace GithubReleaseWatch.Tests;

internal static class WindowChecks
{
    private static Task Refresh(MainWindow window, RepoViewModel vm) =>
        (Task)typeof(MainWindow).GetMethod("RefreshRepoAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, [vm])!;

    internal static async Task RunAsync()
    {
        // This is the test executable's output directory, never the installed app's config.
        ConfigStore.Save(new() { RefreshOnStartup = false }, Path.Combine(AppContext.BaseDirectory, "config.json"));
        Test("Moved XAML views load their embedded icons and about image", () =>
        {
            System.Windows.Window[] windows = [new MainWindow(), new AddRepoWindow(),
                new EditRepoWindow("https://github.com/a/b", "1.0"), new SettingsWindow(new(), "", false, false),
                new AboutWindow(), new HelpWindow(), new InputDialog("Test", "Prompt")];
            try
            {
                foreach (var window in windows)
                    Check(window.Icon != null && window.Icon.Width > 0, $"{window.GetType().Name} icon");
                var about = windows.OfType<AboutWindow>().Single();
                Check(about.ImgIcon.Source != null && about.ImgIcon.Visibility == System.Windows.Visibility.Visible,
                    "about image fallback after resource move");
            }
            finally { foreach (var window in windows) window.Close(); }
        });
        Test("Shared repository editor preserves add/edit labels and initial values", () =>
        {
            var add = new AddRepoWindow();
            var edit = new EditRepoWindow("https://github.com/a/b", "v1.0");
            try
            {
                Check(add.Title == "添加仓库" && (string)add.ConfirmButton.Content == "添加", "add labels");
                Check(edit.Title == "编辑仓库" && (string)edit.ConfirmButton.Content == "保存" &&
                    edit.TxtUrl.Text == "https://github.com/a/b" && edit.TxtVersion.Text == "v1.0", "edit values");
                Check(Version.TryParse(AppInfo.Version.Split('-')[0], out var version) && version.Build >= 0,
                    "application version metadata");
            }
            finally { add.Close(); edit.Close(); }
        });
        Test("Settings edits do not apply global network configuration before Save", () =>
        {
            var handler = new FakeHandler(_ => Task.FromResult(FakeHandler.Json("[]")));
            FakeHandler.Apply(handler);
            var settings = new SettingsWindow(new(), "", false, false);
            settings.ChkTokenEnabled.IsChecked = true;
            settings.Close();
            Check(!handler.Disposed && handler.Requests == 0, "cancelled settings changed active session");
        });
        Test("Selected repository and expansion master work before template generation", () =>
        {
            var window = new MainWindow();
            try
            {
                var vm = new RepoViewModel(new() { Owner = "a", Repo = "b" }, () => false);
                vm.SetReleases([new() { TagName = "1.0" }, new() { TagName = "0.9" }]);
                window.Repos.Add(vm); window.RepoList.SelectedItem = vm;
                window.BtnToggleAllVersions.IsChecked = false;
                Check(vm.VisibleReleases.All(r => !r.IsExpanded), "collapse all");
                window.BtnToggleAllVersions.IsChecked = true;
                Check(vm.VisibleReleases.All(r => r.IsExpanded), "expand all");
                vm.SetCurrent("1.0");
                Check(window.DetailsContent.IsVisible == false || window.DetailsContent.DataContext == vm, "details selection");
            }
            finally { window.Close(); }
        });
        await TestAsync("Refresh races cannot overwrite a newly edited repository", async () =>
        {
            var window = new MainWindow();
            try
            {
                var vm = new RepoViewModel(new() { Owner = "a", Repo = "old" }, () => false);
                window.Repos.Add(vm); window.RepoList.SelectedItem = vm;
                var oldResponse = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
                FakeHandler.Apply(new(request => request.RequestUri!.AbsolutePath.Contains("/old/")
                    ? oldResponse.Task : Task.FromResult(FakeHandler.Json("[{\"tag_name\":\"new-version\"}]"))));
                var previous = Refresh(window, vm);
                vm.Config.Repo = "new";
                var current = Refresh(window, vm);
                await current;
                oldResponse.SetResult(FakeHandler.Json("[{\"tag_name\":\"stale-version\"}]"));
                await previous;
                Check(vm.LatestVisible?.TagName == "new-version" && !vm.IsLoading, "stale response overwrote edit");
            }
            finally { window.Close(); }
        });
        await TestAsync("Removing a repository while refreshing prevents late mutation", async () =>
        {
            var window = new MainWindow();
            try
            {
                var vm = new RepoViewModel(new() { Owner = "a", Repo = "b" }, () => false);
                window.Repos.Add(vm);
                var pending = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
                FakeHandler.Apply(new(_ => pending.Task));
                var refresh = Refresh(window, vm);
                window.Repos.Remove(vm);
                pending.SetResult(FakeHandler.Json("[{\"tag_name\":\"late\"}]"));
                await refresh;
                Check(vm.LatestVisible == null, "late mutation of removed repository");
            }
            finally { window.Close(); }
        });
    }
}
