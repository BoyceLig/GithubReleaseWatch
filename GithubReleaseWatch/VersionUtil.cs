using System;

namespace GithubReleaseWatch
{
    /// <summary>版本号比较：自动忽略 v/V 前缀，按 "." 分段做数字比较。</summary>
    public static class VersionUtil
    {
        public static string Normalize(string? version)
        {
            var v = version?.Trim() ?? "";
            if (v.Length > 1 && v.StartsWith("v", StringComparison.OrdinalIgnoreCase))
                v = v[1..];
            return v;
        }

        /// <summary>比较两个版本号：大于返回正数，小于返回负数，相等返回 0。</summary>
        public static int Compare(string? left, string? right)
        {
            var a = Normalize(left).Split('.', StringSplitOptions.RemoveEmptyEntries);
            var b = Normalize(right).Split('.', StringSplitOptions.RemoveEmptyEntries);
            var count = Math.Max(a.Length, b.Length);
            for (var i = 0; i < count; i++)
            {
                var sa = i < a.Length ? a[i] : "0";
                var sb = i < b.Length ? b[i] : "0";
                var result = CompareSegment(sa, sb);
                if (result != 0) return result;
            }
            return 0;
        }

        private static int CompareSegment(string a, string b)
        {
            if (int.TryParse(a, out var na) && int.TryParse(b, out var nb))
                return na.CompareTo(nb);
            return string.CompareOrdinal(a, b);
        }
    }
}
