using System.Text.RegularExpressions;

namespace Weepa.Api.Validation;

/// <summary>
/// X (Twitter) ID を正規化する。
///   @Abc_Player              -> Abc_Player
///   ＠Abc_Player (全角@)     -> Abc_Player
///   https://x.com/Abc_Player -> Abc_Player
///   https://twitter.com/Abc_Player -> Abc_Player
/// 大文字小文字はユーザー入力のまま保持する。
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

        return s;
    }

    public static bool IsValid(string normalized) => ValidIdRegex().IsMatch(normalized);

    [GeneratedRegex(
        @"^https?://(?:www\.)?(?:x|twitter)\.com/(?<id>[A-Za-z0-9_]+)",
        RegexOptions.IgnoreCase)]
    private static partial Regex XIdUrlRegex();

    [GeneratedRegex(@"^[A-Za-z0-9_]{1,15}$")]
    private static partial Regex ValidIdRegex();
}
