namespace GithubReleaseWatch;

internal static class GitHubHtmlPage
{
    /// <summary>把 GitHub /markdown API 返回的 HTML 片段包装成完整页面（含基础 GitHub 风格 CSS）。</summary>
    public static string Wrap(string? htmlFragment)
    {
        if (string.IsNullOrWhiteSpace(htmlFragment))
            htmlFragment = "（无版本说明）";

        // HTML 保持不变，滚轮消息交给外层 WPF。
        const string wheelScript = @"<script>
                document.addEventListener('wheel', function(e) {
                    e.preventDefault(); e.stopPropagation();
                    var pixels = e.deltaY * (e.deltaMode === 1 ? 16 : e.deltaMode === 2 ? innerHeight : 1);
                    chrome.webview.postMessage({type:'wheel', value:Math.round(-pixels * 120 / 48)});
                }, {passive:false});
                </script>";

        return "<!DOCTYPE html><html><head>"
            + "<meta http-equiv='X-UA-Compatible' content='IE=edge'>"
            + "<meta charset='utf-8'>"
            + "<style>"
            + "html,body{overflow:hidden;height:auto;margin:0;padding:0;background:#fff;}"
            + "body{font-family:-apple-system,BlinkMacSystemFont,'Segoe UI',Helvetica,Arial,sans-serif;font-size:13px;line-height:1.6;color:#1F2328;padding:8px;}"
            + ".markdown-body h1,.markdown-body h2,.markdown-body h3,.markdown-body h4{margin:0.6em 0 0.4em;font-weight:600;line-height:1.25;}"
            + ".markdown-body h1{font-size:1.5em;border-bottom:1px solid #D8DEE4;padding-bottom:0.3em;}"
            + ".markdown-body h2{font-size:1.25em;border-bottom:1px solid #D8DEE4;padding-bottom:0.3em;}"
            + ".markdown-body p{margin:0.5em 0;}"
            + ".markdown-body ul,.markdown-body ol{padding-left:1.6em;margin:0.4em 0;}"
            + ".markdown-body li{margin:0.15em 0;}"
            + ".markdown-body code{font-family:SFMono-Regular,Consolas,'Liberation Mono',Menlo,monospace;background:rgba(175,184,193,0.2);padding:0.15em 0.3em;border-radius:4px;font-size:0.92em;}"
            + ".markdown-body pre{background:#F6F8FA;padding:12px;border-radius:6px;overflow:hidden;}"
            + ".markdown-body pre code{background:transparent;padding:0;}"
            + ".markdown-body a{color:#0969DA;text-decoration:none;}"
            + ".markdown-body a:hover{text-decoration:underline;}"
            + ".markdown-body table{border-collapse:collapse;width:100%;margin:0.6em 0;}"
            + ".markdown-body th,.markdown-body td{border:1px solid #D0D7DE;padding:6px 10px;}"
            + ".markdown-body th{background:#F6F8FA;font-weight:600;}"
            + ".markdown-body img{max-width:100%;height:auto;}"
            + "</style></head><body><div class='markdown-body'>"
            + htmlFragment
            + "</div>"
            + wheelScript
            + "</body></html>";
    }

}
