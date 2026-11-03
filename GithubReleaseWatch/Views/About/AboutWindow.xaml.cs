using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using MessageBox = System.Windows.MessageBox;
using Path = System.IO.Path;

namespace GithubReleaseWatch
{
    public partial class AboutWindow : Window
    {
        public AboutWindow()
        {
            InitializeComponent();
            LoadAppIcon();
            TxtVersion.Text = AppInfo.Version;
            TxtRuntime.Text = $"{Environment.Version} ({(Environment.Is64BitProcess ? "x64" : "x86")})";
        }

        /// <summary>
        /// 同步预加载应用图标（CacheOption=OnLoad 立即解码），
        /// 避免 Image 异步加载导致窗口首帧空白。
        /// 优先读 exe 同目录的 AppIcon.png；失败则尝试 pack:// 资源；再失败则隐藏图标区。
        /// </summary>
        private void LoadAppIcon()
        {
            var candidates = new[]
            {
                // 1) exe 同目录（随发布复制，最稳）
                Path.Combine(AppContext.BaseDirectory, "AppIcon.png"),
                // 2) 程序集内嵌资源
                null,
            };

            foreach (var path in candidates)
            {
                try
                {
                    var bmp = new BitmapImage();
                    bmp.BeginInit();
                    if (path != null)
                    {
                        if (!File.Exists(path)) continue;
                        bmp.UriSource = new Uri(path, UriKind.Absolute);
                    }
                    else
                    {
                        bmp.UriSource = new Uri("pack://application:,,,/GithubReleaseWatch;component/Resources/Icons/AppIcon.png", UriKind.Absolute);
                    }
                    bmp.CacheOption = BitmapCacheOption.OnLoad; // 立即解码并缓存，不再持有文件句柄
                    bmp.EndInit();
                    bmp.Freeze();
                    ImgIcon.Source = bmp;
                    return;
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[AboutWindow] AppIcon load failed ({path ?? "pack"}): {ex.Message}");
                }
            }

            // 全部失败：隐藏图标区，不影响其余信息显示
            ImgIcon.Visibility = Visibility.Collapsed;
        }

        private void TxtGithub_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (sender is TextBlock text) ExternalLinks.Open(this, text.Text);
        }
    }
}
