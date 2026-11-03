using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using System.Diagnostics;

namespace GithubReleaseWatch;

public partial class MainWindow
{
    private int _lastShownRemaining = -1;
    private int _lastShownLimit = -1;

    private long _rateRefreshSequence;

    private async Task RefreshRateLimitAsync()
    {
        if (_closed) return;
        long request = ++_rateRefreshSequence;
        try
        {
            var snap = await GitHubApi.GetRateLimitSnapshotAsync();
            if (_closed || request != _rateRefreshSequence) return;
            if (snap.Ok)
            {
                _lastRateSnapshot = snap;
                RedrawRateLimitText(consumeFromPrevious: true);
                _lastShownRemaining = snap.Remaining;
                _lastShownLimit = snap.Limit;
            }
            else
            {
                TxtRateLimit.Text = "API 配额 获取失败";
            }
        }
        catch
        {
            if (!_closed && request == _rateRefreshSequence) TxtRateLimit.Text = "API 配额 获取失败";
        }
    }

    /// <summary>用缓存的快照重绘配额文本（本地计算，不消耗 API 配额）。</summary>
    private void RedrawRateLimitText(bool consumeFromPrevious = false)
    {
        var snap = _lastRateSnapshot;
        if (snap is not { Ok: true }) return;

        var mode = snap.Authenticated ? "认证" : "匿名";
        int used = snap.Limit - snap.Remaining;

        // 只在配额真的减少时提示消耗量；无变化不显示（避免无意义的 "±0"）
        string delta = "";
        if (consumeFromPrevious && _lastShownRemaining >= 0 && _lastShownLimit == snap.Limit)
        {
            int diff = _lastShownRemaining - snap.Remaining;
            if (diff > 0) delta = $"　本次消耗 {diff}";
        }

        TxtRateLimit.Text =
            $"API 配额 已用 {used}/{snap.Limit}，剩 {snap.Remaining}（{mode} · {ResetCountdown(snap.ResetAt)} 后重置）{delta}";
        TxtRateLimit.ToolTip = $"配额重置时间：{snap.ResetAt.LocalDateTime:yyyy-MM-dd HH:mm:ss}";
    }

    /// <summary>把重置时刻格式化成倒计时文本（如 "1 小时 23 分" / "45 分钟" / "已重置"）。</summary>
    private static string ResetCountdown(DateTimeOffset resetAt)
    {
        var span = resetAt.LocalDateTime - DateTime.Now;
        if (span <= TimeSpan.Zero) return "已重置";
        if (span.TotalHours >= 1)
            return $"{(int)span.TotalHours} 小时 {span.Minutes} 分";
        if (span.TotalMinutes >= 1)
            return $"{(int)span.TotalMinutes} 分钟";
        return $"{Math.Max(1, (int)span.TotalSeconds)} 秒";
    }

    private DispatcherTimer? _rateLimitTimer;
    private DispatcherTimer? _rateLimitRedrawTimer;
    /// <summary>最近一次配额快照，用于本地重绘倒计时（不额外请求 API）。</summary>
    private GitHubApi.RateLimitSnapshot? _lastRateSnapshot;
}
