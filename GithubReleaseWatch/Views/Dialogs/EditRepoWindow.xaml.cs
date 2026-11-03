namespace GithubReleaseWatch;

public sealed class EditRepoWindow : RepositoryEditorWindow
{
    public EditRepoWindow(string repoUrl, string currentVersion)
        : base(editing: true, repoUrl, currentVersion) { }
}
