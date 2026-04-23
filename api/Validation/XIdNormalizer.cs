using System.Text.RegularExpressions;

namespace Weepa.Api.Validation;

/// <summary>
/// X (Twitter) ID を正規化する。
///   @abc_player              -> abc_player
///   ＠abc_player (全角@)     -> abc_player
///   https://x.com/abc_player -> abc_player
///   https://twitter.com/abc_player -> abc_player
///   大文字 -> 小文字
/// </summary>
public static partial class XIdNormalizer
{
    public static string Normalize(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return string.Empty;

        var s = raw.Trim().Replace('＠', '@');

        var urlMatch = XIdUrlRegex().Match(s);
        if (urlMatch.Success)
        {
            s = urlMatch.Groups["id"].Value;
        }
        else
        {
            s = s.TrimStart('@');
        }

        return s.ToLowerInvariant();
    }

    public static bool IsValid(string normalized) => ValidIdRegex().IsMatch(normalized);

    [GeneratedRegex(
        @"^https?://(?:www\.)?(?:x|twitter)\.com/(?<id>[A-Za-z0-9_]+)",
        RegexOptions.IgnoreCase)]
    private static partial Regex XIdUrlRegex();

    [GeneratedRegex(@"^[A-Za-z0-9_]{1,15}$")]
    private static partial Regex ValidIdRegex();
}
