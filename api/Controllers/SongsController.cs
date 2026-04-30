using Dapper;
using Microsoft.AspNetCore.Mvc;
using Npgsql;
using Weepa.Api.Models.Requests;
using Weepa.Api.Services;
using Weepa.Api.Validation;

namespace Weepa.Api.Controllers;

[ApiController]
[Route("api/songs")]
public class SongsController : ControllerBase
{
    private readonly NpgsqlDataSource _dataSource;
    private readonly IDiscordNotifier _discord;

    public SongsController(NpgsqlDataSource dataSource, IDiscordNotifier discord)
    {
        _dataSource = dataSource;
        _discord = discord;
    }

    [HttpGet]
    public async Task<IActionResult> List(CancellationToken ct)
    {
        const string sql = """
            SELECT
                s.weepa_song_id   AS "weepaSongId",
                s.song            AS "song",
                s.effect          AS "effect",
                s.x_id            AS "xId",
                s.submit_name     AS "submitName",
                s.submit_date     AS "submitDate",
                s.approval_status AS "approvalStatus",
                ev.weepa_event_id AS "weepaEventId",
                ev.start_date     AS "eventStartDate",
                ev.end_date       AS "eventEndDate",
                ev.tallied_at     AS "eventTalliedAt"
            FROM weepa_songs s
            LEFT JOIN LATERAL (
                SELECT weepa_event_id, start_date, end_date, tallied_at
                FROM weepa_events e
                WHERE e.weepa_song_id = s.weepa_song_id
                ORDER BY start_date ASC
                LIMIT 1
            ) ev ON TRUE
            WHERE s.approval_status >= 0
            ORDER BY s.weepa_song_id DESC
            """;

        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var items = await conn.QueryAsync(new CommandDefinition(sql, cancellationToken: ct));
        return Ok(items);
    }

    [HttpPost]
    public async Task<IActionResult> Submit(
        [FromBody] SongSubmissionRequest request,
        CancellationToken ct)
    {
        var normalizedXId = XIdNormalizer.Normalize(request.XId);
        if (!XIdNormalizer.IsValid(normalizedXId))
        {
            ModelState.AddModelError(
                nameof(request.XId),
                "X ID の形式が正しくありません (半角英数字と _ のみ、1〜15文字)");
            return ValidationProblem(ModelState);
        }

        const string sql = """
            INSERT INTO weepa_songs
                (song, effect, x_id, submit_name, submit_reason)
            VALUES
                (@Song, @Effect, @XId, @SubmitName, @SubmitReason)
            RETURNING weepa_song_id
            """;

        var trimmedSong = request.Song.Trim();
        var trimmedName = request.SubmitName.Trim();
        var trimmedReason = string.IsNullOrWhiteSpace(request.SubmitReason)
            ? null
            : request.SubmitReason.Trim();

        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var id = await conn.ExecuteScalarAsync<int>(new CommandDefinition(
            sql,
            new
            {
                Song = trimmedSong,
                Effect = request.Effect,
                XId = normalizedXId,
                SubmitName = trimmedName,
                SubmitReason = trimmedReason,
            },
            cancellationToken: ct));

        // 投稿成功 → Discord 通知 (fire-and-forget; 失敗してもユーザーには影響なし)
        _ = _discord.NotifySongSubmittedAsync(new SongSubmittedNotification(
            id, trimmedSong, request.Effect, normalizedXId, trimmedName, trimmedReason));

        return Ok(new { weepaSongId = id });
    }
}
