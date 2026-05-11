using Dapper;
using Microsoft.AspNetCore.Mvc;
using Npgsql;
using System.Security.Cryptography;
using System.Text;
using Weepa.Api.Models.Requests;
using Weepa.Api.Models.Responses;

namespace Weepa.Api.Controllers;

/// <summary>
/// MoonShotChallenge (MSC) 用の管理者向けエンドポイント。
/// 認証は WeePA 側 AdminController と同じ Basic 認証 (.env の ADMIN_BASIC_*)。
/// </summary>
[ApiController]
[Route("api/admin/msc")]
public class MscAdminController : ControllerBase
{
    private readonly NpgsqlDataSource _dataSource;
    private readonly IConfiguration _configuration;
    private readonly ILogger<MscAdminController> _logger;

    public MscAdminController(
        NpgsqlDataSource dataSource,
        IConfiguration configuration,
        ILogger<MscAdminController> logger)
    {
        _dataSource = dataSource;
        _configuration = configuration;
        _logger = logger;
    }

    [HttpGet("events")]
    public async Task<IActionResult> ListEvents(CancellationToken ct)
    {
        var authFailure = Authenticate();
        if (authFailure is not null) return authFailure;

        await using var conn = await _dataSource.OpenConnectionAsync(ct);

        const string eventsSql = """
            SELECT
                msc_event_id AS "MscEventId",
                title        AS "Title",
                description  AS "Description",
                start_date   AS "StartDate",
                end_date     AS "EndDate",
                tallied_at   AS "TalliedAt",
                created_at   AS "CreatedAt"
            FROM msc_events
            ORDER BY start_date DESC
            """;
        var events = (await conn.QueryAsync<MscEventResponse>(
            new CommandDefinition(eventsSql, cancellationToken: ct))).ToList();
        if (events.Count == 0) return Ok(events);

        var ids = events.Select(e => e.MscEventId).ToArray();
        const string songsSql = """
            SELECT
                msc_event_id AS "MscEventId",
                song_order   AS "SongOrder",
                song         AS "Song",
                effect       AS "Effect"
            FROM msc_songs
            WHERE msc_event_id = ANY(@Ids)
            ORDER BY msc_event_id, song_order
            """;
        var songsAll = await conn.QueryAsync<(int MscEventId, int SongOrder, string Song, string Effect)>(
            new CommandDefinition(songsSql, new { Ids = ids }, cancellationToken: ct));

        var songsByEvent = songsAll
            .GroupBy(s => s.MscEventId)
            .ToDictionary(g => g.Key, g => g
                .Select(s => new MscSongResponse
                {
                    SongOrder = s.SongOrder,
                    Song = s.Song,
                    Effect = s.Effect,
                })
                .ToList());

        foreach (var ev in events)
        {
            if (songsByEvent.TryGetValue(ev.MscEventId, out var list))
            {
                ev.Songs = list;
            }
        }
        return Ok(events);
    }

    [HttpPost("events")]
    public async Task<IActionResult> CreateEvent(
        [FromBody] MscEventCreateRequest request,
        CancellationToken ct)
    {
        var authFailure = Authenticate();
        if (authFailure is not null) return authFailure;

        if (request is null)
        {
            return BadRequest(new { message = "リクエストボディが必要です" });
        }

        if (request.EndDate <= request.StartDate)
        {
            ModelState.AddModelError(
                nameof(request.EndDate),
                "endDate は startDate より後の日時にしてください");
            return ValidationProblem(ModelState);
        }

        // songOrder が 1..N の連番になっているか
        var orders = request.Songs.Select(s => s.SongOrder).OrderBy(x => x).ToList();
        for (int i = 0; i < orders.Count; i++)
        {
            if (orders[i] != i + 1)
            {
                ModelState.AddModelError(
                    nameof(request.Songs),
                    $"songOrder は 1 から連番で指定してください (got: [{string.Join(',', orders)}])");
                return ValidationProblem(ModelState);
            }
        }

        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        var newId = await conn.ExecuteScalarAsync<int>(new CommandDefinition(
            """
            INSERT INTO msc_events (title, description, start_date, end_date)
            VALUES (@Title, @Description, @StartDate, @EndDate)
            RETURNING msc_event_id
            """,
            new
            {
                request.Title,
                Description = string.IsNullOrWhiteSpace(request.Description)
                    ? null
                    : request.Description.Trim(),
                StartDate = request.StartDate.ToUniversalTime(),
                EndDate = request.EndDate.ToUniversalTime(),
            },
            transaction: tx, cancellationToken: ct));

        foreach (var song in request.Songs.OrderBy(s => s.SongOrder))
        {
            await conn.ExecuteAsync(new CommandDefinition(
                """
                INSERT INTO msc_songs (msc_event_id, song_order, song, effect)
                VALUES (@MscEventId, @SongOrder, @Song, @Effect)
                """,
                new
                {
                    MscEventId = newId,
                    song.SongOrder,
                    Song = song.Song.Trim(),
                    song.Effect,
                },
                transaction: tx, cancellationToken: ct));
        }

        await tx.CommitAsync(ct);
        return Ok(new { mscEventId = newId });
    }

    // ============================================
    // Submissions (スコア承認フロー)
    // ============================================

    private const string UploadsRoot = "/app/uploads";

    [HttpGet("submissions/pending")]
    public async Task<IActionResult> PendingSubmissions(CancellationToken ct)
    {
        var authFailure = Authenticate();
        if (authFailure is not null) return authFailure;

        await using var conn = await _dataSource.OpenConnectionAsync(ct);

        const string subsSql = """
            SELECT s.msc_event_id AS "MscEventId",
                   s.x_id         AS "XId",
                   s.submit_id    AS "SubmitId",
                   s.submit_name  AS "SubmitName",
                   s.platform     AS "Platform",
                   s.submit_date  AS "SubmitDate",
                   e.title        AS "EventTitle"
            FROM msc_submissions s
            JOIN msc_events e ON e.msc_event_id = s.msc_event_id
            WHERE s.approval_status = 0
            ORDER BY s.submit_date ASC
            """;
        var subs = (await conn.QueryAsync<MscPendingSubmissionResponse>(
            new CommandDefinition(subsSql, cancellationToken: ct))).ToList();
        if (subs.Count == 0) return Ok(subs);

        const string playsSql = """
            SELECT sc.msc_event_id AS "MscEventId",
                   sc.x_id         AS "XId",
                   sc.submit_id    AS "SubmitId",
                   sc.play_order   AS "PlayOrder",
                   sc.song_order   AS "SongOrder",
                   sc.score        AS "Score",
                   so.song         AS "Song",
                   so.effect       AS "Effect"
            FROM msc_scores sc
            JOIN msc_submissions s
              ON s.msc_event_id = sc.msc_event_id
             AND s.x_id        = sc.x_id
             AND s.submit_id   = sc.submit_id
            JOIN msc_songs so
              ON so.msc_event_id = sc.msc_event_id
             AND so.song_order   = sc.song_order
            WHERE s.approval_status = 0
            ORDER BY sc.msc_event_id, sc.x_id, sc.submit_id, sc.play_order
            """;
        var plays = await conn.QueryAsync<(int MscEventId, string XId, int SubmitId, int PlayOrder, int SongOrder, int Score, string Song, string Effect)>(
            new CommandDefinition(playsSql, cancellationToken: ct));

        const string imagesSql = """
            SELECT i.msc_event_id AS "MscEventId",
                   i.x_id         AS "XId",
                   i.submit_id    AS "SubmitId",
                   i.play_order   AS "PlayOrder",
                   i.image_path   AS "ImagePath"
            FROM msc_submission_images i
            JOIN msc_submissions s
              ON s.msc_event_id = i.msc_event_id
             AND s.x_id        = i.x_id
             AND s.submit_id   = i.submit_id
            WHERE s.approval_status = 0
            ORDER BY i.msc_event_id, i.x_id, i.submit_id, i.play_order
            """;
        var images = await conn.QueryAsync<(int MscEventId, string XId, int SubmitId, int PlayOrder, string ImagePath)>(
            new CommandDefinition(imagesSql, cancellationToken: ct));

        var playsByKey = plays
            .GroupBy(p => (p.MscEventId, p.XId, p.SubmitId))
            .ToDictionary(g => g.Key, g => g.Select(p => new MscPendingPlayResponse
            {
                PlayOrder = p.PlayOrder, SongOrder = p.SongOrder, Score = p.Score,
                Song = p.Song, Effect = p.Effect,
            }).ToList());
        var imagesByKey = images
            .GroupBy(i => (i.MscEventId, i.XId, i.SubmitId))
            .ToDictionary(g => g.Key, g => g.Select(i => new MscPendingImageResponse
            {
                PlayOrder = i.PlayOrder, ImagePath = i.ImagePath,
            }).ToList());

        foreach (var sub in subs)
        {
            var key = (sub.MscEventId, sub.XId, sub.SubmitId);
            if (playsByKey.TryGetValue(key, out var pp)) sub.Plays = pp;
            if (imagesByKey.TryGetValue(key, out var ii)) sub.Images = ii;
        }
        return Ok(subs);
    }

    [HttpPost("submissions/{eventId:int}/{xId}/{submitId:int}/approve")]
    public async Task<IActionResult> ApproveSubmission(
        int eventId,
        string xId,
        int submitId,
        [FromBody] MscSubmissionApproveRequest? request,
        CancellationToken ct)
    {
        var authFailure = Authenticate();
        if (authFailure is not null) return authFailure;

        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        // 対象が未承認かロック取得
        var exists = await conn.QuerySingleOrDefaultAsync<int?>(new CommandDefinition(
            """
            SELECT 1 FROM msc_submissions
            WHERE msc_event_id = @EventId AND x_id = @XId AND submit_id = @SubmitId
              AND approval_status = 0
            FOR UPDATE
            """,
            new { EventId = eventId, XId = xId, SubmitId = submitId },
            transaction: tx, cancellationToken: ct));
        if (exists is null)
        {
            await tx.RollbackAsync(ct);
            return NotFound(new { message = "未承認の対象データが見つかりません" });
        }

        // スコア修正があれば反映 (任意)
        if (request?.Plays is { Count: > 0 } corrections)
        {
            foreach (var c in corrections)
            {
                if (c.Score < 0 || c.Score > 10_000_000)
                {
                    await tx.RollbackAsync(ct);
                    return BadRequest(new
                    {
                        message = $"playOrder={c.PlayOrder} の score が範囲外 (0..10,000,000)",
                    });
                }
                await conn.ExecuteAsync(new CommandDefinition(
                    """
                    UPDATE msc_scores
                    SET score = @Score
                    WHERE msc_event_id = @EventId
                      AND x_id = @XId
                      AND submit_id = @SubmitId
                      AND play_order = @PlayOrder
                    """,
                    new
                    {
                        EventId = eventId, XId = xId, SubmitId = submitId,
                        c.PlayOrder, c.Score,
                    },
                    transaction: tx, cancellationToken: ct));
            }
        }

        // 画像 path をリストアップ (TX 内で取得、コミット後に物理削除)
        var imagePaths = (await conn.QueryAsync<string>(new CommandDefinition(
            """
            SELECT image_path
            FROM msc_submission_images
            WHERE msc_event_id = @EventId AND x_id = @XId AND submit_id = @SubmitId
            """,
            new { EventId = eventId, XId = xId, SubmitId = submitId },
            transaction: tx, cancellationToken: ct))).ToList();

        // 承認状態を 1 に
        await conn.ExecuteAsync(new CommandDefinition(
            """
            UPDATE msc_submissions
            SET approval_status = 1
            WHERE msc_event_id = @EventId AND x_id = @XId AND submit_id = @SubmitId
              AND approval_status = 0
            """,
            new { EventId = eventId, XId = xId, SubmitId = submitId },
            transaction: tx, cancellationToken: ct));

        await tx.CommitAsync(ct);

        // コミット成功後に pending 画像を物理削除 (承認時クリーンアップ)
        foreach (var publicPath in imagePaths)
        {
            var phys = ToUploadPhysicalPath(publicPath);
            if (phys is null) continue;
            try
            {
                if (System.IO.File.Exists(phys)) System.IO.File.Delete(phys);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "approved image delete failed: {Path}", phys);
            }
        }

        return Ok(new { message = "ok" });
    }

    // ============================================
    // 公開 / 非公開トグル (WeePA と同じく tallied_at で表現)
    // ============================================

    [HttpPost("events/{id:int}/publish")]
    public async Task<IActionResult> PublishEvent(int id, CancellationToken ct)
    {
        var authFailure = Authenticate();
        if (authFailure is not null) return authFailure;

        await using var conn = await _dataSource.OpenConnectionAsync(ct);

        var pendingCount = await conn.ExecuteScalarAsync<int>(new CommandDefinition(
            "SELECT COUNT(*) FROM msc_submissions WHERE msc_event_id = @Id AND approval_status = 0",
            new { Id = id },
            cancellationToken: ct));
        if (pendingCount > 0)
        {
            return BadRequest(new
            {
                message = $"未処理（承認も却下もされていない）スコアが {pendingCount} 件あります。先にスコア承認タブで処理してください。",
                pendingCount,
            });
        }

        var affected = await conn.ExecuteAsync(new CommandDefinition(
            "UPDATE msc_events SET tallied_at = NOW() WHERE msc_event_id = @Id AND tallied_at IS NULL",
            new { Id = id },
            cancellationToken: ct));
        if (affected == 0)
        {
            return NotFound(new { message = "未公開のイベントが見つかりません（既に公開済み or 存在しない）" });
        }
        return Ok(new { message = "ok" });
    }

    [HttpPost("events/{id:int}/unpublish")]
    public async Task<IActionResult> UnpublishEvent(int id, CancellationToken ct)
    {
        var authFailure = Authenticate();
        if (authFailure is not null) return authFailure;

        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var affected = await conn.ExecuteAsync(new CommandDefinition(
            "UPDATE msc_events SET tallied_at = NULL WHERE msc_event_id = @Id",
            new { Id = id },
            cancellationToken: ct));
        if (affected == 0) return NotFound(new { message = "対象イベントが見つかりません" });
        return Ok(new { message = "ok" });
    }

    [HttpPost("submissions/{eventId:int}/{xId}/{submitId:int}/reject")]
    public async Task<IActionResult> RejectSubmission(
        int eventId, string xId, int submitId, CancellationToken ct)
    {
        var authFailure = Authenticate();
        if (authFailure is not null) return authFailure;

        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var affected = await conn.ExecuteAsync(new CommandDefinition(
            """
            UPDATE msc_submissions
            SET approval_status = -1
            WHERE msc_event_id = @EventId AND x_id = @XId AND submit_id = @SubmitId
              AND approval_status = 0
            """,
            new { EventId = eventId, XId = xId, SubmitId = submitId },
            cancellationToken: ct));
        if (affected == 0) return NotFound(new { message = "未承認の対象データが見つかりません" });
        return Ok(new { message = "ok" });
    }

    private static string? ToUploadPhysicalPath(string imagePath)
    {
        // 想定 image_path: "/uploads/pending/msc/{event_id}/{submit_id}/play{N}.{ext}"
        const string prefix = "/uploads/";
        if (!imagePath.StartsWith(prefix, StringComparison.Ordinal)) return null;
        var rel = imagePath[prefix.Length..];
        if (rel.Contains("..")) return null;
        return Path.Combine(UploadsRoot, rel.Replace('/', Path.DirectorySeparatorChar));
    }

    // ============================================
    // Auth (AdminController と同じ Basic 認証ロジック。
    // 認証ヘッダだけ確認、WWW-Authenticate は返さない)
    // ============================================
    private IActionResult? Authenticate()
    {
        var expectedUser = _configuration["ADMIN_BASIC_USER"];
        var expectedPassword = _configuration["ADMIN_BASIC_PASSWORD"];
        if (string.IsNullOrWhiteSpace(expectedUser) ||
            string.IsNullOrWhiteSpace(expectedPassword))
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable,
                new { message = "管理者認証情報がサーバーに設定されていません" });
        }

        if (!Request.Headers.TryGetValue("Authorization", out var authHeaderValues))
        {
            return Unauthorized();
        }
        var authHeader = authHeaderValues.ToString();
        const string prefix = "Basic ";
        if (!authHeader.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return Unauthorized();
        }

        string decoded;
        try
        {
            var encoded = authHeader[prefix.Length..].Trim();
            decoded = Encoding.UTF8.GetString(Convert.FromBase64String(encoded));
        }
        catch
        {
            return Unauthorized();
        }

        var sepIndex = decoded.IndexOf(':');
        if (sepIndex <= 0) return Unauthorized();

        var user = decoded[..sepIndex];
        var password = decoded[(sepIndex + 1)..];
        if (!FixedTimeEquals(user, expectedUser) ||
            !FixedTimeEquals(password, expectedPassword))
        {
            return Unauthorized();
        }
        return null;
    }

    private static bool FixedTimeEquals(string left, string right)
    {
        var leftBytes = Encoding.UTF8.GetBytes(left);
        var rightBytes = Encoding.UTF8.GetBytes(right);
        if (leftBytes.Length != rightBytes.Length) return false;
        return CryptographicOperations.FixedTimeEquals(leftBytes, rightBytes);
    }
}
