using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;

namespace Weepa.Api.Services;

/// <summary>
/// 自分自身の /api/admin/* を Basic 認証で叩く HTTP loopback クライアント。
/// Discord interactions などコントローラ外から AdminController のロジックを再利用するため。
/// </summary>
public sealed class WeepaAdminClient
{
    private readonly HttpClient _http;

    public WeepaAdminClient(IHttpClientFactory factory, IConfiguration config)
    {
        _http = factory.CreateClient("admin-loopback");
        _http.BaseAddress = new Uri("http://localhost:8080");
        var user = config["ADMIN_BASIC_USER"] ?? "";
        var pass = config["ADMIN_BASIC_PASSWORD"] ?? "";
        var raw = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{user}:{pass}"));
        _http.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Basic", raw);
    }

    public Task<HttpResponseMessage> ApproveSongAsync(int id, CancellationToken ct) =>
        _http.PostAsync($"/api/admin/songs/{id}/approve", null, ct);

    public Task<HttpResponseMessage> RejectSongAsync(int id, CancellationToken ct) =>
        _http.PostAsync($"/api/admin/songs/{id}/reject", null, ct);

    public Task<HttpResponseMessage> ApproveScoreAsync(
        int eventId,
        string xId,
        int submitId,
        int? score,
        int? exScore,
        CancellationToken ct)
    {
        object body;
        if (score.HasValue || exScore.HasValue)
        {
            body = new { score, exScore };
        }
        else
        {
            body = new { };
        }
        return _http.PostAsJsonAsync(
            $"/api/admin/scores/{eventId}/{Uri.EscapeDataString(xId)}/{submitId}/approve",
            body, ct);
    }

    public Task<HttpResponseMessage> RejectScoreAsync(
        int eventId,
        string xId,
        int submitId,
        CancellationToken ct) =>
        _http.PostAsync(
            $"/api/admin/scores/{eventId}/{Uri.EscapeDataString(xId)}/{submitId}/reject",
            null, ct);
}
