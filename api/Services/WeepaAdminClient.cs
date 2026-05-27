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

    public Task<HttpResponseMessage> SetSongExMaxAsync(int id, int? exScoreMax, CancellationToken ct) =>
        _http.PostAsJsonAsync($"/api/admin/songs/{id}/ex-max", new { exScoreMax }, ct);

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

    public Task<HttpResponseMessage> PublishEventAsync(int id, CancellationToken ct) =>
        _http.PostAsync($"/api/admin/events/{id}/publish", null, ct);

    public Task<HttpResponseMessage> UnpublishEventAsync(int id, CancellationToken ct) =>
        _http.PostAsync($"/api/admin/events/{id}/unpublish", null, ct);

    public Task<HttpResponseMessage> ShuffleScheduleAsync(CancellationToken ct) =>
        _http.PostAsync("/api/admin/schedule/shuffle", null, ct);

    // ============================================
    // MSC (MoonShotChallenge) loopback
    // ============================================

    public Task<HttpResponseMessage> PublishMscEventAsync(int id, CancellationToken ct) =>
        _http.PostAsync($"/api/admin/msc/events/{id}/publish", null, ct);

    public Task<HttpResponseMessage> UnpublishMscEventAsync(int id, CancellationToken ct) =>
        _http.PostAsync($"/api/admin/msc/events/{id}/unpublish", null, ct);

    public Task<HttpResponseMessage> ApproveMscSubmissionAsync(
        int eventId,
        string xId,
        int submitId,
        IReadOnlyList<(int PlayOrder, int Score)>? plays,
        CancellationToken ct)
    {
        object body = plays is { Count: > 0 }
            ? new { plays = plays.Select(p => new { playOrder = p.PlayOrder, score = p.Score }).ToArray() }
            : new { };
        return _http.PostAsJsonAsync(
            $"/api/admin/msc/submissions/{eventId}/{Uri.EscapeDataString(xId)}/{submitId}/approve",
            body, ct);
    }

    public Task<HttpResponseMessage> RejectMscSubmissionAsync(
        int eventId, string xId, int submitId, CancellationToken ct) =>
        _http.PostAsync(
            $"/api/admin/msc/submissions/{eventId}/{Uri.EscapeDataString(xId)}/{submitId}/reject",
            null, ct);
}
