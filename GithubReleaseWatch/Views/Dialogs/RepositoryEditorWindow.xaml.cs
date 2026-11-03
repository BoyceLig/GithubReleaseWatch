using System.Windows;
using MessageBox = System.Windows.MessageBox;

namespace GithubReleaseWatch
{
    public partial class RepositoryEditorWindow : Window
    {
        public string RepoUrl { get; private set; } = "";
        public string OwnerName { get; private set; } = "";
        public string RepoName { get; private set; } = "";
        public string CurrentVersion { get; private set; } = "";

        protected RepositoryEditorWindow(bool editing, string repoUrl = "", string currentVersion = "")
        {
            InitializeComponent();
            Title = editing ? "编辑仓库" : "添加仓库";
            ConfirmButton.Content = editing ? "保存" : "添加";
            VersionHint.Text = editing ? "当前版本号（留空表示未设置）" : "当前版本号（可留空，默认取最新版本）";
            TxtUrl.Text = repoUrl;
            TxtVersion.Text = currentVersion;
            if (editing) TxtUrl.SelectAll();
            Loaded += (_, _) => TxtUrl.Focus();
        }

        private void BtnOk_Click(object sender, RoutedEventArgs e)
        {
            if (!GitHubRepository.TryParse(TxtUrl.Text, out var repository))
            {
                MessageBox.Show(this, "无法识别 GitHub 地址，请输入类似 https://github.com/owner/repo 的地址。",
                    "格式错误", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            OwnerName = repository.Owner;
            RepoName = repository.Name;
            RepoUrl = repository.Url;
            CurrentVersion = TxtVersion.Text.Trim();
            DialogResult = true;
        }
    }
}
