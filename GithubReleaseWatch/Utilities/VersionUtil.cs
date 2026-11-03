using System.Numerics;

namespace GithubReleaseWatch;

/// <summary>支持 v 前缀、预发布标识和任意长度数字；非标准 tag 保留按文本比较。</summary>
public static class VersionUtil
{
    public static string Normalize(string? version)
    {
        string value = version?.Trim() ?? "";
        return value.Length > 1 && value.StartsWith("v", StringComparison.OrdinalIgnoreCase) ? value[1..] : value;
    }

    public static int Compare(string? left, string? right)
    {
        var a = Normalize(left).Split('+')[0].Split('-', 2);
        var b = Normalize(right).Split('+')[0].Split('-', 2);
        var coreA = a[0].Split('.');
        var coreB = b[0].Split('.');
        for (int i = 0; i < Math.Max(coreA.Length, coreB.Length); i++)
        {
            int result = CompareSegment(i < coreA.Length ? coreA[i] : "0", i < coreB.Length ? coreB[i] : "0");
            if (result != 0) return result;
        }
        if (a.Length != b.Length) return a.Length == 1 ? 1 : -1;
        if (a.Length == 1) return 0;
        var preA = a[1].Split('.');
        var preB = b[1].Split('.');
        for (int i = 0; i < Math.Min(preA.Length, preB.Length); i++)
        {
            bool numericA = IsNumeric(preA[i]), numericB = IsNumeric(preB[i]);
            int result = numericA != numericB ? (numericA ? -1 : 1) : CompareSegment(preA[i], preB[i]);
            if (result != 0) return result;
        }
        return preA.Length.CompareTo(preB.Length);
    }

    private static int CompareSegment(string a, string b) =>
        IsNumeric(a) && IsNumeric(b) && BigInteger.TryParse(a, out var na) && BigInteger.TryParse(b, out var nb)
            ? na.CompareTo(nb) : string.CompareOrdinal(a, b);

    private static bool IsNumeric(string value) => value.Length > 0 && value.All(c => c is >= '0' and <= '9');
}
