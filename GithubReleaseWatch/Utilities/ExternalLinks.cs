using System.Diagnostics;
using System.Windows;

namespace GithubReleaseWatch;

internal static class ExternalLinks
{
    public static bool TryOpen(string? value, out string? error)
    {
        error = null;
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
        {
            error = "链接必须是有效的 HTTP 或 HTTPS 地址。";
            return false;
        }
        try
        {
            Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            Trace.WriteLine($"[ExternalLinks] {ex.Message}");
            return false;
        }
    }

    public static void Open(Window owner, string? value)
    {
        if (!TryOpen(value, out var error))
            MessageBox.Show(owner, $"无法打开浏览器：{error}", "打开失败", MessageBoxButton.OK, MessageBoxImage.Warning);
    }
}
