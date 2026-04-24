using Dapper;
using Microsoft.AspNetCore.Mvc;
using Npgsql;
using System.Security.Cryptography;
using System.Text;
using Weepa.Api.Models.Requests;

namespace Weepa.Api.Controllers;

/// <summary>
/// 管理者向け生DB CRUD（デバッグ/緊急修正用）。業務ロジックの整合性チェックは行わない。
/// 通常の業務フローは AdminController / 公開コントローラ側を使うこと。
/// </summary>
[ApiController]
[Route("api/admin/db")]
public class DbAdminController : ControllerBase
{
    private readonly NpgsqlDataSource _dataSource;
    private readonly IConfiguration _configuration;

    public DbAdminController(NpgsqlDataSource dataSource, IConfiguration configuration)
    {
        _dataSource = dataSource;
        _configuration = configuration;
    }

    // ============================================
    // Songs
    // ============================================

    [HttpGet("songs")]
    public async Task<IActionResult> ListSongs(CancellationToken ct)
    {
        var authFailure = Authenticate();
        if (authFailure is not null) return authFailure;

        const string sql = """
            SELECT
                weepa_song_id   AS "weepaSongId",
                song            AS "song",
                effect          AS "effect",
                x_id            AS "xId",
                submit_name     AS "submitName",
                submit_reason   AS "submitReason",
                submit_date     AS "submitDate",
                approval_status AS "approvalStatus"
            FROM weepa_songs
            ORDER BY weepa_song_id DESC
            """;

        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var items = await conn.QueryAsync(new CommandDefinition(sql, cancellationToken: ct));
        return Ok(items);
    }

    [HttpPost("songs")]
    public async Task<IActionResult> InsertSong(
        [FromBody] SongDbRequest request,
        CancellationToken ct)
    {
        var authFailure = Authenticate();
        if (authFailure is not null) return authFailure;

        const string sql = """
            INSERT INTO weepa_songs
                (song, effect, x_id, submit_name, submit_reason, submit_date, approval_status)
            VALUES
                (@Song, @Effect, @XId, @SubmitName, @SubmitReason,
                 COALESCE(@SubmitDate, NOW()),
                 COALESCE(@ApprovalStatus, 0))
            RETURNING weepa_song_id AS "weepaSongId"
            """;

        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        try
        {
            var id = await conn.ExecuteScalarAsync<int>(new CommandDefinition(
                sql,
                new
                {
                    request.Song,
                    request.Effect,
                    request.XId,
                    request.SubmitName,
                    request.SubmitReason,
                    SubmitDate = request.SubmitDate?.ToUniversalTime(),
                    request.ApprovalStatus,
                },
                cancellationToken: ct));
            return Ok(new { weepaSongId = id });
        }
        catch (PostgresException ex)
        {
            return BadRequest(new { message = ex.MessageText });
        }
    }

    [HttpPut("songs/{id:int}")]
    public async Task<IActionResult> UpdateSong(
        int id,
        [FromBody] SongDbRequest request,
        CancellationToken ct)
    {
        var authFailure = Authenticate();
        if (authFailure is not null) return authFailure;

        const string sql = """
            UPDATE weepa_songs SET
                song            = COALESCE(@Song, song),
                effect          = COALESCE(@Effect, effect),
                x_id            = COALESCE(@XId, x_id),
                submit_name     = COALESCE(@SubmitName, submit_name),
                submit_reason   = COALESCE(@SubmitReason, submit_reason),
                submit_date     = COALESCE(@SubmitDate, submit_date),
                approval_status = COALESCE(@ApprovalStatus, approval_status)
            WHERE weepa_song_id = @Id
            """;

        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        try
        {
            var affected = await conn.ExecuteAsync(new CommandDefinition(
                sql,
                new
                {
                    Id = id,
                    request.Song,
                    request.Effect,
                    request.XId,
                    request.SubmitName,
                    request.SubmitReason,
                    SubmitDate = request.SubmitDate?.ToUniversalTime(),
                    request.ApprovalStatus,
                },
                cancellationToken: ct));
            if (affected == 0) return NotFound(new { message = "対象の楽曲が存在しません" });
            return Ok(new { message = "ok" });
        }
        catch (PostgresException ex)
        {
            return BadRequest(new { message = ex.MessageText });
        }
    }

    [HttpDelete("songs/{id:int}")]
    public async Task<IActionResult> DeleteSong(int id, CancellationToken ct)
    {
        var authFailure = Authenticate();
        if (authFailure is not null) return authFailure;

        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        try
        {
            var affected = await conn.ExecuteAsync(new CommandDefinition(
                "DELETE FROM weepa_songs WHERE weepa_song_id = @Id",
                new { Id = id },
                cancellationToken: ct));
            if (affected == 0) return NotFound(new { message = "対象の楽曲が存在しません" });
            return Ok(new { message = "ok" });
        }
        catch (PostgresException ex)
        {
            return Conflict(new { message = ex.MessageText });
        }
    }

    // ============================================
    // Events
    // ============================================

    [HttpGet("events")]
    public async Task<IActionResult> ListEvents(CancellationToken ct)
    {
        var authFailure = Authenticate();
        if (authFailure is not null) return authFailure;

        const string sql = """
            SELECT
                e.weepa_event_id AS "weepaEventId",
                e.weepa_song_id  AS "weepaSongId",
                e.start_date     AS "startDate",
                e.end_date       AS "endDate",
                s.song           AS "song",
                s.effect         AS "effect"
            FROM weepa_events e
            LEFT JOIN weepa_songs s ON s.weepa_song_id = e.weepa_song_id
            ORDER BY e.weepa_event_id DESC
            """;

        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var items = await conn.QueryAsync(new CommandDefinition(sql, cancellationToken: ct));
        return Ok(items);
    }

    [HttpPost("events")]
    public async Task<IActionResult> InsertEvent(
        [FromBody] EventDbRequest request,
        CancellationToken ct)
    {
        var authFailure = Authenticate();
        if (authFailure is not null) return authFailure;

        const string sql = """
            INSERT INTO weepa_events (weepa_song_id, start_date, end_date)
            VALUES (@WeepaSongId, @StartDate, @EndDate)
            RETURNING weepa_event_id AS "weepaEventId"
            """;

        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        try
        {
            var id = await conn.ExecuteScalarAsync<int>(new CommandDefinition(
                sql,
                new
                {
                    request.WeepaSongId,
                    StartDate = request.StartDate?.ToUniversalTime(),
                    EndDate = request.EndDate?.ToUniversalTime(),
                },
                cancellationToken: ct));
            return Ok(new { weepaEventId = id });
        }
        catch (PostgresException ex)
        {
            return BadRequest(new { message = ex.MessageText });
        }
    }

    [HttpPut("events/{id:int}")]
    public async Task<IActionResult> UpdateEvent(
        int id,
        [FromBody] EventDbRequest request,
        CancellationToken ct)
    {
        var authFailure = Authenticate();
        if (authFailure is not null) return authFailure;

        const string sql = """
            UPDATE weepa_events SET
                weepa_song_id = COALESCE(@WeepaSongId, weepa_song_id),
                start_date    = COALESCE(@StartDate, start_date),
                end_date      = COALESCE(@EndDate, end_date)
            WHERE weepa_event_id = @Id
            """;

        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        try
        {
            var affected = await conn.ExecuteAsync(new CommandDefinition(
                sql,
                new
                {
                    Id = id,
                    request.WeepaSongId,
                    StartDate = request.StartDate?.ToUniversalTime(),
                    EndDate = request.EndDate?.ToUniversalTime(),
                },
                cancellationToken: ct));
            if (affected == 0) return NotFound(new { message = "対象のイベントが存在しません" });
            return Ok(new { message = "ok" });
        }
        catch (PostgresException ex)
        {
            return BadRequest(new { message = ex.MessageText });
        }
    }

    [HttpDelete("events/{id:int}")]
    public async Task<IActionResult> DeleteEvent(int id, CancellationToken ct)
    {
        var authFailure = Authenticate();
        if (authFailure is not null) return authFailure;

        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        try
        {
            var affected = await conn.ExecuteAsync(new CommandDefinition(
                "DELETE FROM weepa_events WHERE weepa_event_id = @Id",
                new { Id = id },
                cancellationToken: ct));
            if (affected == 0) return NotFound(new { message = "対象のイベントが存在しません" });
            return Ok(new { message = "ok" });
        }
        catch (PostgresException ex)
        {
            return Conflict(new { message = ex.MessageText });
        }
    }

    // ============================================
    // Scores
    // ============================================

    [HttpGet("scores")]
    public async Task<IActionResult> ListScores(CancellationToken ct)
    {
        var authFailure = Authenticate();
        if (authFailure is not null) return authFailure;

        const string sql = """
            SELECT
                sc.weepa_event_id AS "weepaEventId",
                sc.x_id           AS "xId",
                sc.submit_id      AS "submitId",
                sc.submit_name    AS "submitName",
                sc.score          AS "score",
                sc.ex_score       AS "exScore",
                sc.image_path     AS "imagePath",
                sc.submit_date    AS "submitDate",
                sc.approval_status AS "approvalStatus",
                so.song           AS "song",
                so.effect         AS "effect"
            FROM weepa_scores sc
            LEFT JOIN weepa_events ev ON ev.weepa_event_id = sc.weepa_event_id
            LEFT JOIN weepa_songs so ON so.weepa_song_id = ev.weepa_song_id
            ORDER BY sc.submit_date DESC
            """;

        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var items = await conn.QueryAsync(new CommandDefinition(sql, cancellationToken: ct));
        return Ok(items);
    }

    [HttpPost("scores")]
    public async Task<IActionResult> InsertScore(
        [FromBody] ScoreDbRequest request,
        CancellationToken ct)
    {
        var authFailure = Authenticate();
        if (authFailure is not null) return authFailure;

        const string sql = """
            INSERT INTO weepa_scores
                (weepa_event_id, x_id, submit_id, submit_name, score, ex_score, image_path, submit_date, approval_status)
            VALUES
                (@WeepaEventId, @XId, @SubmitId, @SubmitName, @Score, @ExScore,
                 COALESCE(@ImagePath, ''),
                 COALESCE(@SubmitDate, NOW()),
                 COALESCE(@ApprovalStatus, 0))
            RETURNING weepa_event_id AS "weepaEventId",
                      x_id           AS "xId",
                      submit_id      AS "submitId"
            """;

        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        try
        {
            var row = await conn.QuerySingleAsync(new CommandDefinition(
                sql,
                new
                {
                    request.WeepaEventId,
                    request.XId,
                    request.SubmitId,
                    request.SubmitName,
                    request.Score,
                    request.ExScore,
                    request.ImagePath,
                    SubmitDate = request.SubmitDate?.ToUniversalTime(),
                    request.ApprovalStatus,
                },
                cancellationToken: ct));
            return Ok(row);
        }
        catch (PostgresException ex)
        {
            return BadRequest(new { message = ex.MessageText });
        }
    }

    [HttpPut("scores/{eventId:int}/{xId}/{submitId:int}")]
    public async Task<IActionResult> UpdateScore(
        int eventId,
        string xId,
        int submitId,
        [FromBody] ScoreDbRequest request,
        CancellationToken ct)
    {
        var authFailure = Authenticate();
        if (authFailure is not null) return authFailure;

        const string sql = """
            UPDATE weepa_scores SET
                submit_name     = COALESCE(@SubmitName, submit_name),
                score           = COALESCE(@Score, score),
                ex_score        = COALESCE(@ExScore, ex_score),
                image_path      = COALESCE(@ImagePath, image_path),
                submit_date     = COALESCE(@SubmitDate, submit_date),
                approval_status = COALESCE(@ApprovalStatus, approval_status)
            WHERE weepa_event_id = @EventId
              AND x_id = @XId
              AND submit_id = @SubmitId
            """;

        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        try
        {
            var affected = await conn.ExecuteAsync(new CommandDefinition(
                sql,
                new
                {
                    EventId = eventId,
                    XId = xId,
                    SubmitId = submitId,
                    request.SubmitName,
                    request.Score,
                    request.ExScore,
                    request.ImagePath,
                    SubmitDate = request.SubmitDate?.ToUniversalTime(),
                    request.ApprovalStatus,
                },
                cancellationToken: ct));
            if (affected == 0) return NotFound(new { message = "対象のスコアが存在しません" });
            return Ok(new { message = "ok" });
        }
        catch (PostgresException ex)
        {
            return BadRequest(new { message = ex.MessageText });
        }
    }

    [HttpDelete("scores/{eventId:int}/{xId}/{submitId:int}")]
    public async Task<IActionResult> DeleteScore(
        int eventId,
        string xId,
        int submitId,
        CancellationToken ct)
    {
        var authFailure = Authenticate();
        if (authFailure is not null) return authFailure;

        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        try
        {
            var affected = await conn.ExecuteAsync(new CommandDefinition(
                """
                DELETE FROM weepa_scores
                WHERE weepa_event_id = @EventId
                  AND x_id = @XId
                  AND submit_id = @SubmitId
                """,
                new { EventId = eventId, XId = xId, SubmitId = submitId },
                cancellationToken: ct));
            if (affected == 0) return NotFound(new { message = "対象のスコアが存在しません" });
            return Ok(new { message = "ok" });
        }
        catch (PostgresException ex)
        {
            return Conflict(new { message = ex.MessageText });
        }
    }

    // ============================================
    // Basic 認証（AdminController と同じ仕組み、共有ヘルパは後日）
    // ============================================

    private IActionResult? Authenticate()
    {
        var expectedUser = _configuration["ADMIN_BASIC_USER"];
        var expectedPassword = _configuration["ADMIN_BASIC_PASSWORD"];
        if (string.IsNullOrWhiteSpace(expectedUser) || string.IsNullOrWhiteSpace(expectedPassword))
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable,
                new { message = "管理者認証情報がサーバーに設定されていません" });
        }

        if (!Request.Headers.TryGetValue("Authorization", out var authHeaderValues))
        {
            return UnauthorizedWithChallenge();
        }

        var authHeader = authHeaderValues.ToString();
        const string prefix = "Basic ";
        if (!authHeader.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return UnauthorizedWithChallenge();
        }

        string decoded;
        try
        {
            var encoded = authHeader[prefix.Length..].Trim();
            decoded = Encoding.UTF8.GetString(Convert.FromBase64String(encoded));
        }
        catch
        {
            return UnauthorizedWithChallenge();
        }

        var separatorIndex = decoded.IndexOf(':');
        if (separatorIndex <= 0)
        {
            return UnauthorizedWithChallenge();
        }

        var user = decoded[..separatorIndex];
        var password = decoded[(separatorIndex + 1)..];

        if (!FixedTimeEquals(user, expectedUser) || !FixedTimeEquals(password, expectedPassword))
        {
            return UnauthorizedWithChallenge();
        }

        return null;
    }

    private IActionResult UnauthorizedWithChallenge()
    {
        Response.Headers.WWWAuthenticate = "Basic realm=\"WeePA Admin\", charset=\"UTF-8\"";
        return Unauthorized();
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
