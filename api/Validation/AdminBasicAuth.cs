using System.Security.Cryptography;
using System.Text;

namespace Weepa.Api.Validation;

/// <summary>
/// 管理者 Basic 認証の検証ヘルパー。
/// AdminController / DbAdminController の Authenticate と同じ検証を、
/// 公開エンドポイント側 (未公開ランキングの管理者アクセス判定等) からも使えるよう切り出したもの。
/// </summary>
public static class AdminBasicAuth
{
    public static bool IsAuthorized(HttpRequest request, IConfiguration configuration)
    {
        var expectedUser = configuration["ADMIN_BASIC_USER"];
        var expectedPassword = configuration["ADMIN_BASIC_PASSWORD"];
        if (string.IsNullOrWhiteSpace(expectedUser) || string.IsNullOrWhiteSpace(expectedPassword))
        {
            return false;
        }

        if (!request.Headers.TryGetValue("Authorization", out var authHeaderValues))
        {
            return false;
        }

        var authHeader = authHeaderValues.ToString();
        const string prefix = "Basic ";
        if (!authHeader.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        string decoded;
        try
        {
            var encoded = authHeader[prefix.Length..].Trim();
            decoded = Encoding.UTF8.GetString(Convert.FromBase64String(encoded));
        }
        catch
        {
            return false;
        }

        var separatorIndex = decoded.IndexOf(':');
        if (separatorIndex <= 0)
        {
            return false;
        }

        var user = decoded[..separatorIndex];
        var password = decoded[(separatorIndex + 1)..];
        return FixedTimeEquals(user, expectedUser) && FixedTimeEquals(password, expectedPassword);
    }

    /// <summary>Puppeteer (内部スクショ) 用の Basic 認証ヘッダー値を作る。未設定なら null。</summary>
    public static string? BuildHeaderValue(IConfiguration configuration)
    {
        var user = configuration["ADMIN_BASIC_USER"];
        var password = configuration["ADMIN_BASIC_PASSWORD"];
        if (string.IsNullOrWhiteSpace(user) || string.IsNullOrWhiteSpace(password))
        {
            return null;
        }
        var token = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{user}:{password}"));
        return $"Basic {token}";
    }

    private static bool FixedTimeEquals(string left, string right)
    {
        var leftBytes = Encoding.UTF8.GetBytes(left);
        var rightBytes = Encoding.UTF8.GetBytes(right);
        if (leftBytes.Length != rightBytes.Length)
        {
            return false;
        }
        return CryptographicOperations.FixedTimeEquals(leftBytes, rightBytes);
    }
}
