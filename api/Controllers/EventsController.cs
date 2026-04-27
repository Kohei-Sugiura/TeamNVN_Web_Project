using Dapper;
using Microsoft.AspNetCore.Mvc;
using Npgsql;
using Weepa.Api.Models.Responses;

namespace Weepa.Api.Controllers;

[ApiController]
[Route("api/events")]
public class EventsController : ControllerBase
{
    private readonly NpgsqlDataSource _dataSource;

    public EventsController(NpgsqlDataSource dataSource)
    {
        _dataSource = dataSource;
    }

    [HttpGet("recent")]
    public async Task<IActionResult> Recent(
        [FromQuery] int limit = 3,
        CancellationToken ct = default)
    {
        if (limit < 1) limit = 1;
        if (limit > 50) limit = 50;

        const string sql = """
            SELECT
                e.weepa_event_id AS "WeepaEventId",
                e.weepa_song_id  AS "WeepaSongId",
                s.song           AS "Song",
                s.effect         AS "Effect",
                e.start_date     AS "StartDate",
                e.end_date       AS "EndDate"
            FROM weepa_events e
            JOIN weepa_songs s ON s.weepa_song_id = e.weepa_song_id
            ORDER BY e.start_date DESC
            LIMIT @Limit
            """;

        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var events = await conn.QueryAsync<EventResponse>(
            new CommandDefinition(sql, new { Limit = limit }, cancellationToken: ct));
        return Ok(events);
    }

    [HttpGet("past")]
    public async Task<IActionResult> Past(CancellationToken ct)
    {
        const string sql = """
            SELECT
                e.weepa_event_id AS "WeepaEventId",
                e.weepa_song_id  AS "WeepaSongId",
                s.song           AS "Song",
                s.effect         AS "Effect",
                e.start_date     AS "StartDate",
                e.end_date       AS "EndDate"
            FROM weepa_events e
            JOIN weepa_songs s ON s.weepa_song_id = e.weepa_song_id
            WHERE e.end_date < NOW()
            ORDER BY e.end_date DESC
            """;

        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var events = await conn.QueryAsync<EventResponse>(
            new CommandDefinition(sql, cancellationToken: ct));
        return Ok(events);
    }

    [HttpGet("current")]
    public async Task<IActionResult> Current(CancellationToken ct)
    {
        const string sql = """
            SELECT
                e.weepa_event_id AS "WeepaEventId",
                e.weepa_song_id  AS "WeepaSongId",
                s.song           AS "Song",
                s.effect         AS "Effect",
                e.start_date     AS "StartDate",
                e.end_date       AS "EndDate"
            FROM weepa_events e
            JOIN weepa_songs s ON s.weepa_song_id = e.weepa_song_id
            WHERE NOW() BETWEEN e.start_date AND e.end_date
            ORDER BY e.start_date DESC
            LIMIT 1
            """;

        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var ev = await conn.QuerySingleOrDefaultAsync<EventResponse>(
            new CommandDefinition(sql, cancellationToken: ct));

        return Ok(new { @event = ev });
    }

    [HttpGet("{id:int}")]
    public async Task<IActionResult> Get(int id, CancellationToken ct)
    {
        const string sql = """
            SELECT
                e.weepa_event_id AS "WeepaEventId",
                e.weepa_song_id  AS "WeepaSongId",
                s.song           AS "Song",
                s.effect         AS "Effect",
                e.start_date     AS "StartDate",
                e.end_date       AS "EndDate"
            FROM weepa_events e
            JOIN weepa_songs s ON s.weepa_song_id = e.weepa_song_id
            WHERE e.weepa_event_id = @Id
            LIMIT 1
            """;

        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var ev = await conn.QuerySingleOrDefaultAsync<EventResponse>(
            new CommandDefinition(sql, new { Id = id }, cancellationToken: ct));

        if (ev is null) return NotFound();
        return Ok(ev);
    }

    [HttpGet("{id:int}/ranking")]
    public async Task<IActionResult> Ranking(int id, CancellationToken ct)
    {
        const string sql = """
            WITH best AS (
                SELECT DISTINCT ON (x_id)
                    x_id,
                    submit_name,
                    score,
                    ex_score,
                    submit_date
                FROM weepa_scores
                WHERE weepa_event_id = @EventId
                  AND approval_status = 1
                ORDER BY x_id, score DESC, ex_score DESC NULLS LAST, submit_date ASC
            )
            SELECT
                ROW_NUMBER() OVER (
                    ORDER BY score DESC, ex_score DESC NULLS LAST, submit_date ASC
                ) AS "Rank",
                x_id         AS "XId",
                submit_name  AS "SubmitName",
                score        AS "Score",
                ex_score     AS "ExScore",
                submit_date  AS "SubmitDate"
            FROM best
            ORDER BY "Rank"
            """;

        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var entries = await conn.QueryAsync<RankingEntry>(
            new CommandDefinition(sql, new { EventId = id }, cancellationToken: ct));

        return Ok(entries);
    }
}
