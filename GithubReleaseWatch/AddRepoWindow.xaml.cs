using System.Text.RegularExpressions;
using System.Windows;
using MessageBox = System.Windows.MessageBox;

namespace GithubReleaseWatch
{
    public partial class AddRepoWindow : Window
    {
        private static readonly Regex UrlRegex =
            new(@"github\.com/([\w.\-]+)/([\w.\-]+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        public string RepoUrl { get; private set; } = "";
        public string OwnerName { get; private set; } = "";
        public string RepoName { get; private set; } = "";
        public string CurrentVersion { get; private set; } = "";

        public AddRepoWindow()
        {
            InitializeComponent();
            TxtUrl.Focus();
        }

        private void BtnOk_Click(object sender, RoutedEventArgs e)
        {
            var match = UrlRegex.Match(TxtUrl.Text.Trim());
            if (!match.Success)
            {
                MessageBox.Show(this, "无法识别 GitHub 地址，请输入类似 https://github.com/owner/repo 的地址。",
                    "格式错误", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            OwnerName = match.Groups[1].Value;
            RepoName = match.Groups[2].Value.TrimEnd('.'); // 去掉末尾 ".git" 之类
            RepoUrl = $"https://github.com/{OwnerName}/{RepoName}";
            CurrentVersion = TxtVersion.Text.Trim();
            DialogResult = true;
        }
    }
}
