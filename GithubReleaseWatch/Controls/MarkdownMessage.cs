using System.Text.Json;

namespace GithubReleaseWatch;

/// <summary>浏览器消息边界；忽略缺字段、错误类型和越界数字，避免异常逃逸到 UI 线程。</summary>
internal readonly record struct MarkdownMessage(string Type, double Value)
{
    public static bool TryParse(string json, out MarkdownMessage message)
    {
        message = default;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String ||
                !root.TryGetProperty("value", out var value) || value.ValueKind != JsonValueKind.Number ||
                !value.TryGetDouble(out var number) || !double.IsFinite(number)) return false;
            string? name = type.GetString();
            if (name == "height" && number >= 0 && number <= 1_000_000 ||
                name == "wheel" && value.TryGetInt32(out _))
            {
                message = new(name!, number);
                return true;
            }
            return false;
        }
        catch (JsonException) { return false; }
    }
}
