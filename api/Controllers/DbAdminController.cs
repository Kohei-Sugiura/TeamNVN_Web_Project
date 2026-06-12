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
                e.tallied_at     AS "talliedAt",
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
                end_date      = COALESCE(@EndDate, end_date),
                tallied_at    = COALESCE(@TalliedAt, tallied_at)
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
                    TalliedAt = request.TalliedAt?.ToUniversalTime(),
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
                sc.tier           AS "tier",
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
                (weepa_event_id, x_id, submit_id, submit_name, score, ex_score, tier, image_path, submit_date, approval_status)
            VALUES
                (@WeepaEventId, @XId, @SubmitId, @SubmitName, @Score, @ExScore,
                 COALESCE(@Tier, 1),
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
                    request.Tier,
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
                tier            = COALESCE(@Tier, tier),
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
                    request.Tier,
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
    // MSC Events
    // ============================================

    [HttpGet("msc-events")]
    public async Task<IActionResult> ListMscEvents(CancellationToken ct)
    {
        var authFailure = Authenticate();
        if (authFailure is not null) return authFailure;
        const string sql = """
            SELECT msc_event_id AS "mscEventId",
                   title        AS "title",
                   description  AS "description",
                   start_date   AS "startDate",
                   end_date     AS "endDate",
                   tallied_at   AS "talliedAt",
                   created_at   AS "createdAt"
            FROM msc_events
            ORDER BY msc_event_id DESC
            """;
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var items = await conn.QueryAsync(new CommandDefinition(sql, cancellationToken: ct));
        return Ok(items);
    }

    [HttpPost("msc-events")]
    public async Task<IActionResult> InsertMscEvent(
        [FromBody] MscEventDbRequest request, CancellationToken ct)
    {
        var authFailure = Authenticate();
        if (authFailure is not null) return authFailure;
        const string sql = """
            INSERT INTO msc_events (title, description, start_date, end_date, tallied_at)
            VALUES (@Title, @Description, @StartDate, @EndDate, @TalliedAt)
            RETURNING msc_event_id AS "mscEventId"
            """;
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        try
        {
            var id = await conn.ExecuteScalarAsync<int>(new CommandDefinition(
                sql, new
                {
                    request.Title,
                    request.Description,
                    StartDate = request.StartDate?.ToUniversalTime(),
                    EndDate = request.EndDate?.ToUniversalTime(),
                    TalliedAt = request.TalliedAt?.ToUniversalTime(),
                }, cancellationToken: ct));
            return Ok(new { mscEventId = id });
        }
        catch (PostgresException ex) { return BadRequest(new { message = ex.MessageText }); }
    }

    [HttpPut("msc-events/{id:int}")]
    public async Task<IActionResult> UpdateMscEvent(
        int id, [FromBody] MscEventDbRequest request, CancellationToken ct)
    {
        var authFailure = Authenticate();
        if (authFailure is not null) return authFailure;
        const string sql = """
            UPDATE msc_events SET
                title       = COALESCE(@Title, title),
                description = COALESCE(@Description, description),
                start_date  = COALESCE(@StartDate, start_date),
                end_date    = COALESCE(@EndDate, end_date),
                tallied_at  = CASE WHEN @HasTalliedAt THEN @TalliedAt ELSE tallied_at END
            WHERE msc_event_id = @Id
            """;
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        try
        {
            var affected = await conn.ExecuteAsync(new CommandDefinition(
                sql, new
                {
                    Id = id,
                    request.Title,
                    request.Description,
                    StartDate = request.StartDate?.ToUniversalTime(),
                    EndDate = request.EndDate?.ToUniversalTime(),
                    HasTalliedAt = request.TalliedAt.HasValue,
                    TalliedAt = request.TalliedAt?.ToUniversalTime(),
                }, cancellationToken: ct));
            if (affected == 0) return NotFound(new { message = "対象 msc_event が存在しません" });
            return Ok(new { message = "ok" });
        }
        catch (PostgresException ex) { return BadRequest(new { message = ex.MessageText }); }
    }

    [HttpDelete("msc-events/{id:int}")]
    public async Task<IActionResult> DeleteMscEvent(int id, CancellationToken ct)
    {
        var authFailure = Authenticate();
        if (authFailure is not null) return authFailure;
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        try
        {
            var affected = await conn.ExecuteAsync(new CommandDefinition(
                "DELETE FROM msc_events WHERE msc_event_id = @Id",
                new { Id = id }, cancellationToken: ct));
            if (affected == 0) return NotFound(new { message = "対象 msc_event が存在しません" });
            return Ok(new { message = "ok" });
        }
        catch (PostgresException ex) { return Conflict(new { message = ex.MessageText }); }
    }

    // ============================================
    // MSC Songs (PK: msc_event_id, song_order)
    // ============================================

    [HttpGet("msc-songs")]
    public async Task<IActionResult> ListMscSongs(CancellationToken ct)
    {
        var authFailure = Authenticate();
        if (authFailure is not null) return authFailure;
        const string sql = """
            SELECT msc_event_id AS "mscEventId",
                   song_order   AS "songOrder",
                   song         AS "song",
                   effect       AS "effect"
            FROM msc_songs
            ORDER BY msc_event_id DESC, song_order ASC
            """;
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var items = await conn.QueryAsync(new CommandDefinition(sql, cancellationToken: ct));
        return Ok(items);
    }

    [HttpPost("msc-songs")]
    public async Task<IActionResult> InsertMscSong(
        [FromBody] MscSongDbRequest request, CancellationToken ct)
    {
        var authFailure = Authenticate();
        if (authFailure is not null) return authFailure;
        const string sql = """
            INSERT INTO msc_songs (msc_event_id, song_order, song, effect)
            VALUES (@MscEventId, @SongOrder, @Song, @Effect)
            RETURNING msc_event_id AS "mscEventId", song_order AS "songOrder"
            """;
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        try
        {
            var row = await conn.QuerySingleAsync(new CommandDefinition(
                sql, new
                {
                    request.MscEventId, request.SongOrder, request.Song, request.Effect,
                }, cancellationToken: ct));
            return Ok(row);
        }
        catch (PostgresException ex) { return BadRequest(new { message = ex.MessageText }); }
    }

    [HttpPut("msc-songs/{eventId:int}/{songOrder:int}")]
    public async Task<IActionResult> UpdateMscSong(
        int eventId, int songOrder, [FromBody] MscSongDbRequest request, CancellationToken ct)
    {
        var authFailure = Authenticate();
        if (authFailure is not null) return authFailure;
        const string sql = """
            UPDATE msc_songs SET
                song   = COALESCE(@Song, song),
                effect = COALESCE(@Effect, effect)
            WHERE msc_event_id = @EventId AND song_order = @SongOrder
            """;
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        try
        {
            var affected = await conn.ExecuteAsync(new CommandDefinition(
                sql, new { EventId = eventId, SongOrder = songOrder, request.Song, request.Effect },
                cancellationToken: ct));
            if (affected == 0) return NotFound(new { message = "対象 msc_song が存在しません" });
            return Ok(new { message = "ok" });
        }
        catch (PostgresException ex) { return BadRequest(new { message = ex.MessageText }); }
    }

    [HttpDelete("msc-songs/{eventId:int}/{songOrder:int}")]
    public async Task<IActionResult> DeleteMscSong(
        int eventId, int songOrder, CancellationToken ct)
    {
        var authFailure = Authenticate();
        if (authFailure is not null) return authFailure;
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        try
        {
            var affected = await conn.ExecuteAsync(new CommandDefinition(
                "DELETE FROM msc_songs WHERE msc_event_id = @EventId AND song_order = @SongOrder",
                new { EventId = eventId, SongOrder = songOrder }, cancellationToken: ct));
            if (affected == 0) return NotFound(new { message = "対象 msc_song が存在しません" });
            return Ok(new { message = "ok" });
        }
        catch (PostgresException ex) { return Conflict(new { message = ex.MessageText }); }
    }

    // ============================================
    // MSC Submissions (PK: msc_event_id, x_id, submit_id)
    // ============================================

    [HttpGet("msc-submissions")]
    public async Task<IActionResult> ListMscSubmissions(CancellationToken ct)
    {
        var authFailure = Authenticate();
        if (authFailure is not null) return authFailure;
        const string sql = """
            SELECT msc_event_id    AS "mscEventId",
                   x_id            AS "xId",
                   submit_id       AS "submitId",
                   submit_name     AS "submitName",
                   platform        AS "platform",
                   submit_date     AS "submitDate",
                   approval_status AS "approvalStatus"
            FROM msc_submissions
            ORDER BY submit_date DESC
            """;
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var items = await conn.QueryAsync(new CommandDefinition(sql, cancellationToken: ct));
        return Ok(items);
    }

    [HttpPost("msc-submissions")]
    public async Task<IActionResult> InsertMscSubmission(
        [FromBody] MscSubmissionDbRequest request, CancellationToken ct)
    {
        var authFailure = Authenticate();
        if (authFailure is not null) return authFailure;
        const string sql = """
            INSERT INTO msc_submissions
                (msc_event_id, x_id, submit_id, submit_name, platform, submit_date, approval_status)
            VALUES
                (@MscEventId, @XId, @SubmitId, @SubmitName, @Platform,
                 COALESCE(@SubmitDate, NOW()),
                 COALESCE(@ApprovalStatus, 0))
            RETURNING msc_event_id AS "mscEventId",
                      x_id         AS "xId",
                      submit_id    AS "submitId"
            """;
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        try
        {
            var row = await conn.QuerySingleAsync(new CommandDefinition(
                sql, new
                {
                    request.MscEventId, request.XId, request.SubmitId,
                    request.SubmitName, request.Platform,
                    SubmitDate = request.SubmitDate?.ToUniversalTime(),
                    request.ApprovalStatus,
                }, cancellationToken: ct));
            return Ok(row);
        }
        catch (PostgresException ex) { return BadRequest(new { message = ex.MessageText }); }
    }

    [HttpPut("msc-submissions/{eventId:int}/{xId}/{submitId:int}")]
    public async Task<IActionResult> UpdateMscSubmission(
        int eventId, string xId, int submitId,
        [FromBody] MscSubmissionDbRequest request, CancellationToken ct)
    {
        var authFailure = Authenticate();
        if (authFailure is not null) return authFailure;
        const string sql = """
            UPDATE msc_submissions SET
                submit_name     = COALESCE(@SubmitName, submit_name),
                platform        = COALESCE(@Platform, platform),
                submit_date     = COALESCE(@SubmitDate, submit_date),
                approval_status = COALESCE(@ApprovalStatus, approval_status)
            WHERE msc_event_id = @EventId AND x_id = @XId AND submit_id = @SubmitId
            """;
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        try
        {
            var affected = await conn.ExecuteAsync(new CommandDefinition(
                sql, new
                {
                    EventId = eventId, XId = xId, SubmitId = submitId,
                    request.SubmitName, request.Platform,
                    SubmitDate = request.SubmitDate?.ToUniversalTime(),
                    request.ApprovalStatus,
                }, cancellationToken: ct));
            if (affected == 0) return NotFound(new { message = "対象 msc_submission が存在しません" });
            return Ok(new { message = "ok" });
        }
        catch (PostgresException ex) { return BadRequest(new { message = ex.MessageText }); }
    }

    [HttpDelete("msc-submissions/{eventId:int}/{xId}/{submitId:int}")]
    public async Task<IActionResult> DeleteMscSubmission(
        int eventId, string xId, int submitId, CancellationToken ct)
    {
        var authFailure = Authenticate();
        if (authFailure is not null) return authFailure;
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        try
        {
            var affected = await conn.ExecuteAsync(new CommandDefinition(
                """
                DELETE FROM msc_submissions
                WHERE msc_event_id = @EventId AND x_id = @XId AND submit_id = @SubmitId
                """,
                new { EventId = eventId, XId = xId, SubmitId = submitId },
                cancellationToken: ct));
            if (affected == 0) return NotFound(new { message = "対象 msc_submission が存在しません" });
            return Ok(new { message = "ok" });
        }
        catch (PostgresException ex) { return Conflict(new { message = ex.MessageText }); }
    }

    // ============================================
    // MSC Scores (PK: msc_event_id, x_id, submit_id, play_order)
    // ============================================

    [HttpGet("msc-scores")]
    public async Task<IActionResult> ListMscScores(CancellationToken ct)
    {
        var authFailure = Authenticate();
        if (authFailure is not null) return authFailure;
        const string sql = """
            SELECT msc_event_id AS "mscEventId",
                   x_id         AS "xId",
                   submit_id    AS "submitId",
                   play_order   AS "playOrder",
                   song_order   AS "songOrder",
                   score        AS "score"
            FROM msc_scores
            ORDER BY msc_event_id DESC, x_id, submit_id, play_order
            """;
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var items = await conn.QueryAsync(new CommandDefinition(sql, cancellationToken: ct));
        return Ok(items);
    }

    [HttpPost("msc-scores")]
    public async Task<IActionResult> InsertMscScore(
        [FromBody] MscScoreDbRequest request, CancellationToken ct)
    {
        var authFailure = Authenticate();
        if (authFailure is not null) return authFailure;
        const string sql = """
            INSERT INTO msc_scores (msc_event_id, x_id, submit_id, play_order, song_order, score)
            VALUES (@MscEventId, @XId, @SubmitId, @PlayOrder, @SongOrder, @Score)
            RETURNING msc_event_id AS "mscEventId", x_id AS "xId", submit_id AS "submitId", play_order AS "playOrder"
            """;
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        try
        {
            var row = await conn.QuerySingleAsync(new CommandDefinition(
                sql, new
                {
                    request.MscEventId, request.XId, request.SubmitId,
                    request.PlayOrder, request.SongOrder, request.Score,
                }, cancellationToken: ct));
            return Ok(row);
        }
        catch (PostgresException ex) { return BadRequest(new { message = ex.MessageText }); }
    }

    [HttpPut("msc-scores/{eventId:int}/{xId}/{submitId:int}/{playOrder:int}")]
    public async Task<IActionResult> UpdateMscScore(
        int eventId, string xId, int submitId, int playOrder,
        [FromBody] MscScoreDbRequest request, CancellationToken ct)
    {
        var authFailure = Authenticate();
        if (authFailure is not null) return authFailure;
        const string sql = """
            UPDATE msc_scores SET
                song_order = COALESCE(@SongOrder, song_order),
                score      = COALESCE(@Score, score)
            WHERE msc_event_id = @EventId AND x_id = @XId AND submit_id = @SubmitId AND play_order = @PlayOrder
            """;
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        try
        {
            var affected = await conn.ExecuteAsync(new CommandDefinition(
                sql, new
                {
                    EventId = eventId, XId = xId, SubmitId = submitId, PlayOrder = playOrder,
                    request.SongOrder, request.Score,
                }, cancellationToken: ct));
            if (affected == 0) return NotFound(new { message = "対象 msc_score が存在しません" });
            return Ok(new { message = "ok" });
        }
        catch (PostgresException ex) { return BadRequest(new { message = ex.MessageText }); }
    }

    [HttpDelete("msc-scores/{eventId:int}/{xId}/{submitId:int}/{playOrder:int}")]
    public async Task<IActionResult> DeleteMscScore(
        int eventId, string xId, int submitId, int playOrder, CancellationToken ct)
    {
        var authFailure = Authenticate();
        if (authFailure is not null) return authFailure;
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        try
        {
            var affected = await conn.ExecuteAsync(new CommandDefinition(
                """
                DELETE FROM msc_scores
                WHERE msc_event_id = @EventId AND x_id = @XId
                  AND submit_id = @SubmitId AND play_order = @PlayOrder
                """,
                new { EventId = eventId, XId = xId, SubmitId = submitId, PlayOrder = playOrder },
                cancellationToken: ct));
            if (affected == 0) return NotFound(new { message = "対象 msc_score が存在しません" });
            return Ok(new { message = "ok" });
        }
        catch (PostgresException ex) { return Conflict(new { message = ex.MessageText }); }
    }

    // ============================================
    // MSC Submission Images (PK: msc_event_id, x_id, submit_id, play_order)
    // ============================================

    [HttpGet("msc-submission-images")]
    public async Task<IActionResult> ListMscSubmissionImages(CancellationToken ct)
    {
        var authFailure = Authenticate();
        if (authFailure is not null) return authFailure;
        const string sql = """
            SELECT msc_event_id AS "mscEventId",
                   x_id         AS "xId",
                   submit_id    AS "submitId",
                   play_order   AS "playOrder",
                   image_path   AS "imagePath"
            FROM msc_submission_images
            ORDER BY msc_event_id DESC, x_id, submit_id, play_order
            """;
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var items = await conn.QueryAsync(new CommandDefinition(sql, cancellationToken: ct));
        return Ok(items);
    }

    [HttpPost("msc-submission-images")]
    public async Task<IActionResult> InsertMscSubmissionImage(
        [FromBody] MscSubmissionImageDbRequest request, CancellationToken ct)
    {
        var authFailure = Authenticate();
        if (authFailure is not null) return authFailure;
        const string sql = """
            INSERT INTO msc_submission_images (msc_event_id, x_id, submit_id, play_order, image_path)
            VALUES (@MscEventId, @XId, @SubmitId, @PlayOrder, @ImagePath)
            RETURNING msc_event_id AS "mscEventId", x_id AS "xId", submit_id AS "submitId", play_order AS "playOrder"
            """;
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        try
        {
            var row = await conn.QuerySingleAsync(new CommandDefinition(
                sql, new
                {
                    request.MscEventId, request.XId, request.SubmitId,
                    request.PlayOrder, request.ImagePath,
                }, cancellationToken: ct));
            return Ok(row);
        }
        catch (PostgresException ex) { return BadRequest(new { message = ex.MessageText }); }
    }

    [HttpPut("msc-submission-images/{eventId:int}/{xId}/{submitId:int}/{playOrder:int}")]
    public async Task<IActionResult> UpdateMscSubmissionImage(
        int eventId, string xId, int submitId, int playOrder,
        [FromBody] MscSubmissionImageDbRequest request, CancellationToken ct)
    {
        var authFailure = Authenticate();
        if (authFailure is not null) return authFailure;
        const string sql = """
            UPDATE msc_submission_images SET
                image_path = COALESCE(@ImagePath, image_path)
            WHERE msc_event_id = @EventId AND x_id = @XId
              AND submit_id = @SubmitId AND play_order = @PlayOrder
            """;
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        try
        {
            var affected = await conn.ExecuteAsync(new CommandDefinition(
                sql, new
                {
                    EventId = eventId, XId = xId, SubmitId = submitId, PlayOrder = playOrder,
                    request.ImagePath,
                }, cancellationToken: ct));
            if (affected == 0) return NotFound(new { message = "対象 msc_submission_image が存在しません" });
            return Ok(new { message = "ok" });
        }
        catch (PostgresException ex) { return BadRequest(new { message = ex.MessageText }); }
    }

    [HttpDelete("msc-submission-images/{eventId:int}/{xId}/{submitId:int}/{playOrder:int}")]
    public async Task<IActionResult> DeleteMscSubmissionImage(
        int eventId, string xId, int submitId, int playOrder, CancellationToken ct)
    {
        var authFailure = Authenticate();
        if (authFailure is not null) return authFailure;
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        try
        {
            var affected = await conn.ExecuteAsync(new CommandDefinition(
                """
                DELETE FROM msc_submission_images
                WHERE msc_event_id = @EventId AND x_id = @XId
                  AND submit_id = @SubmitId AND play_order = @PlayOrder
                """,
                new { EventId = eventId, XId = xId, SubmitId = submitId, PlayOrder = playOrder },
                cancellationToken: ct));
            if (affected == 0) return NotFound(new { message = "対象 msc_submission_image が存在しません" });
            return Ok(new { message = "ok" });
        }
        catch (PostgresException ex) { return Conflict(new { message = ex.MessageText }); }
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
        // ブラウザのネイティブ認証ダイアログを出さないため WWW-Authenticate は付けない
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
