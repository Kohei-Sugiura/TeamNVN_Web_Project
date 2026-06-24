using Dapper;
using Microsoft.AspNetCore.Mvc;
using Npgsql;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Weepa.Api.Models.Requests;
using Weepa.Api.Validation;

namespace Weepa.Api.Controllers;

/// <summary>
/// 管理者向けエンドポイント。
/// Step 6 で Basic 認証を後付けする予定。
/// </summary>
[ApiController]
[Route("api/admin")]
public class AdminController : ControllerBase
{
    private const string UploadsRoot = "/app/uploads";
    private readonly NpgsqlDataSource _dataSource;
    private readonly IConfiguration _configuration;
    private readonly ILogger<AdminController> _logger;

    public AdminController(
        NpgsqlDataSource dataSource,
        IConfiguration configuration,
        ILogger<AdminController> logger)
    {
        _dataSource = dataSource;
        _configuration = configuration;
        _logger = logger;
    }

    [HttpGet("songs/pending")]
    public async Task<IActionResult> PendingSongs(CancellationToken ct)
    {
        var authFailure = Authenticate();
        if (authFailure is not null) return authFailure;

        const string sql = """
            SELECT
                weepa_song_id AS "weepaSongId",
                song          AS "song",
                effect        AS "effect",
                x_id          AS "xId",
                submit_name   AS "submitName",
                submit_reason AS "submitReason",
                submit_date   AS "submitDate"
            FROM weepa_songs
            WHERE approval_status = 0
            ORDER BY submit_date ASC
            """;

        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var items = await conn.QueryAsync(sql);
        return Ok(items);
    }

    [HttpPost("songs/{id:int}/approve")]
    public async Task<IActionResult> ApproveSong(
        int id,
        [FromBody] SongApproveRequest? request,
        CancellationToken ct)
    {
        var authFailure = Authenticate();
        if (authFailure is not null) return authFailure;

        var song = string.IsNullOrWhiteSpace(request?.Song) ? null : request.Song.Trim();
        var effect = string.IsNullOrWhiteSpace(request?.Effect) ? null : request.Effect;

        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        var (settings, settingsError) = await LoadScheduleSettingsAsync(conn, ct, tx);
        if (settingsError is not null)
        {
            await tx.RollbackAsync(ct);
            return settingsError;
        }

        var affected = await conn.ExecuteAsync(new CommandDefinition(
            """
            UPDATE weepa_songs
            SET approval_status = 1,
                song            = COALESCE(@Song, song),
                effect          = COALESCE(@Effect, effect)
            WHERE weepa_song_id = @Id
              AND approval_status = 0
            """,
            new { Id = id, Song = song, Effect = effect },
            transaction: tx,
            cancellationToken: ct));

        if (affected == 0)
        {
            await tx.RollbackAsync(ct);
            return NotFound(new { message = "未承認の対象データが見つかりません" });
        }

        // 承認時に開催枠 (event) を自動生成し、未開催キュー内の **ランダム位置** に挿入する。
        // 同一人物が連続投稿しても順番が分散するため。位置 k (1..n) を選び:
        //   - k == n の場合: 末尾追加 (既存のスロット計算ロジック)
        //   - k <  n の場合: 位置 k 以降の event を 7 日 forward シフトして
        //                    新規イベントを元の k 番のスロットに入れる
        // k=0 (最直近=次に開催するイベント) は選ばない。新規承認曲がいきなり次回開催に
        // ならないようにするため。ただし未来イベントが 1 つも無い (ストック切れ) 場合のみ
        // 末尾追加 = 唯一の次イベントになるのは許容する。
        var futureEvents = (await conn.QueryAsync<(int weepaEventId, DateTime startDate, DateTime endDate)>(
            new CommandDefinition(
                """
                SELECT weepa_event_id AS weepaEventId,
                       start_date     AS startDate,
                       end_date       AS endDate
                FROM weepa_events
                WHERE start_date > NOW()
                ORDER BY start_date ASC
                FOR UPDATE
                """,
                transaction: tx, cancellationToken: ct))).ToList();

        var n = futureEvents.Count;
        // n==0: ストック無し → k=0 (末尾追加 = 唯一の次イベント、許容)
        // n>=1: 最直近 (k=0) を避け 1..n から選ぶ
        var k = n == 0 ? 0 : Random.Shared.Next(1, n + 1);

        DateTimeOffset start;
        if (k == n)
        {
            // 末尾追加: 既存全イベントの最遅 end_date が未来ならそれを基準、
            // でなければ NOW を起点に「設定通りの次のスロット」を求める
            var lastEnd = await conn.ExecuteScalarAsync<DateTime?>(new CommandDefinition(
                "SELECT MAX(end_date) FROM weepa_events",
                transaction: tx, cancellationToken: ct));
            var nowUtc = DateTimeOffset.UtcNow;
            DateTimeOffset anchor = lastEnd.HasValue && new DateTimeOffset(lastEnd.Value, TimeSpan.Zero) > nowUtc
                ? new DateTimeOffset(lastEnd.Value, TimeSpan.Zero)
                : nowUtc;
            var anchorLocal = TimeZoneInfo.ConvertTime(anchor, settings.TimeZone);
            var startLocal = CalculateNextOccurrenceLocal(
                anchorLocal, settings.DayOfWeek, settings.Hour, settings.Minute);
            start = startLocal.ToUniversalTime();
        }
        else
        {
            // 位置 k に挿入: k..n-1 を 7 日 forward シフト
            await conn.ExecuteAsync(new CommandDefinition(
                """
                UPDATE weepa_events
                SET start_date = start_date + INTERVAL '7 days',
                    end_date   = end_date   + INTERVAL '7 days'
                WHERE weepa_event_id = ANY(@Ids)
                """,
                new { Ids = futureEvents.Skip(k).Select(e => e.weepaEventId).ToArray() },
                transaction: tx, cancellationToken: ct));
            // 元々位置 k だった start_date を新規イベントが取る
            start = new DateTimeOffset(futureEvents[k].startDate, TimeSpan.Zero);
        }

        var end = start.AddHours(settings.DurationHours);

        await conn.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO weepa_events (weepa_song_id, start_date, end_date)
            VALUES (@SongId, @Start, @End)
            """,
            new { SongId = id, Start = start, End = end },
            transaction: tx,
            cancellationToken: ct));

        await tx.CommitAsync(ct);
        return Ok(new { message = "ok", scheduledStart = start, scheduledEnd = end, insertedAtPosition = k });
    }

    [HttpPost("songs/{id:int}/reject")]
    public async Task<IActionResult> RejectSong(int id, CancellationToken ct)
    {
        var authFailure = Authenticate();
        if (authFailure is not null) return authFailure;

        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var affected = await conn.ExecuteAsync(new CommandDefinition(
            """
            UPDATE weepa_songs
            SET approval_status = -1
            WHERE weepa_song_id = @Id
              AND approval_status = 0
            """,
            new { Id = id },
            cancellationToken: ct));

        if (affected == 0)
        {
            return NotFound(new { message = "未承認の対象データが見つかりません" });
        }

        return Ok(new { message = "ok" });
    }

    /// <summary>
    /// 楽曲の EX SCORE MAX 値を設定 (null 可。null を渡すと未確定状態に戻す)。
    /// /admin/ から Web UI 経由でも、Discord 経由 (loopback) でも叩く。
    /// </summary>
    [HttpPost("songs/{id:int}/ex-max")]
    public async Task<IActionResult> SetSongExMax(
        int id,
        [FromBody] SongExMaxRequest? request,
        CancellationToken ct)
    {
        var authFailure = Authenticate();
        if (authFailure is not null) return authFailure;

        if (request is null) return BadRequest(new { message = "リクエストボディが必要です" });
        if (request.ExScoreMax is < 0 or > 100_000)
        {
            return BadRequest(new { message = "ex_score_max は 0〜100000 の範囲です" });
        }

        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var affected = await conn.ExecuteAsync(new CommandDefinition(
            "UPDATE weepa_songs SET ex_score_max = @Max WHERE weepa_song_id = @Id",
            new { Id = id, Max = request.ExScoreMax },
            cancellationToken: ct));

        if (affected == 0)
        {
            return NotFound(new { message = "対象楽曲が見つかりません" });
        }
        return Ok(new { message = "ok", exScoreMax = request.ExScoreMax });
    }

    /// <summary>
    /// MAX 値未設定 (= ex_score_max IS NULL) かつ承認済楽曲の一覧。
    /// </summary>
    [HttpGet("songs/ex-max/pending")]
    public async Task<IActionResult> SongsPendingExMax(CancellationToken ct)
    {
        var authFailure = Authenticate();
        if (authFailure is not null) return authFailure;

        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<dynamic>(new CommandDefinition(
            """
            SELECT weepa_song_id AS "weepaSongId",
                   song          AS "song",
                   effect        AS "effect",
                   submit_name   AS "submitName"
            FROM weepa_songs
            WHERE ex_score_max IS NULL
              AND approval_status = 1
            ORDER BY weepa_song_id ASC
            """,
            cancellationToken: ct));
        return Ok(rows);
    }

    [HttpGet("scores/pending")]
    public async Task<IActionResult> PendingScores(CancellationToken ct)
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
                sc.submit_date    AS "submitDate",
                sc.image_path     AS "imagePath",
                so.song           AS "song",
                so.effect         AS "effect"
            FROM weepa_scores sc
            JOIN weepa_events ev ON ev.weepa_event_id = sc.weepa_event_id
            JOIN weepa_songs so ON so.weepa_song_id = ev.weepa_song_id
            WHERE sc.approval_status = 0
            ORDER BY sc.submit_date ASC
            """;

        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var items = await conn.QueryAsync(sql);
        return Ok(items);
    }

    [HttpPost("scores/{eventId:int}/{xId}/{submitId:int}/approve")]
    public async Task<IActionResult> ApproveScore(
        int eventId,
        string xId,
        int submitId,
        [FromBody] ScoreApproveRequest? request,
        CancellationToken ct)
    {
        var authFailure = Authenticate();
        if (authFailure is not null) return authFailure;

        var normalizedXId = XIdNormalizer.Normalize(xId);
        if (!XIdNormalizer.IsValid(normalizedXId))
        {
            ModelState.AddModelError(nameof(xId), "xId の形式が正しくありません");
            return ValidationProblem(ModelState);
        }

        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        var imagePath = await conn.QuerySingleOrDefaultAsync<string?>(new CommandDefinition(
            """
            SELECT image_path
            FROM weepa_scores
            WHERE weepa_event_id = @EventId
              AND x_id = @XId
              AND submit_id = @SubmitId
              AND approval_status = 0
            FOR UPDATE
            """,
            new
            {
                EventId = eventId,
                XId = normalizedXId,
                SubmitId = submitId,
            },
            transaction: tx,
            cancellationToken: ct));

        if (imagePath is null)
        {
            await tx.RollbackAsync(ct);
            return NotFound(new { message = "未承認の対象データが見つかりません" });
        }

        var filePath = ToUploadPhysicalPath(imagePath);
        if (filePath is not null)
        {
            try
            {
                if (System.IO.File.Exists(filePath))
                {
                    System.IO.File.Delete(filePath);
                }
            }
            catch (Exception ex)
            {
                await tx.RollbackAsync(ct);
                _logger.LogWarning(ex, "pending image delete failed: {Path}", filePath);
                return StatusCode(StatusCodes.Status500InternalServerError,
                    new { message = "画像削除に失敗したため承認できませんでした" });
            }
        }

        await conn.ExecuteAsync(new CommandDefinition(
            """
            UPDATE weepa_scores
            SET
                approval_status = 1,
                score = COALESCE(@Score, score),
                ex_score = CASE WHEN @HasExScore THEN @ExScore ELSE ex_score END
            WHERE weepa_event_id = @EventId
              AND x_id = @XId
              AND submit_id = @SubmitId
              AND approval_status = 0
            """,
            new
            {
                EventId = eventId,
                XId = normalizedXId,
                SubmitId = submitId,
                Score = request?.Score,
                ExScore = request?.ExScore,
                HasExScore = request is not null && request.ExScore.HasValue,
            },
            transaction: tx,
            cancellationToken: ct));

        await tx.CommitAsync(ct);
        return Ok(new { message = "ok" });
    }

    [HttpPost("scores/{eventId:int}/{xId}/{submitId:int}/reject")]
    public async Task<IActionResult> RejectScore(
        int eventId,
        string xId,
        int submitId,
        CancellationToken ct)
    {
        var authFailure = Authenticate();
        if (authFailure is not null) return authFailure;

        var normalizedXId = XIdNormalizer.Normalize(xId);
        if (!XIdNormalizer.IsValid(normalizedXId))
        {
            ModelState.AddModelError(nameof(xId), "xId の形式が正しくありません");
            return ValidationProblem(ModelState);
        }

        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var affected = await conn.ExecuteAsync(new CommandDefinition(
            """
            UPDATE weepa_scores
            SET approval_status = -1
            WHERE weepa_event_id = @EventId
              AND x_id = @XId
              AND submit_id = @SubmitId
              AND approval_status = 0
            """,
            new
            {
                EventId = eventId,
                XId = normalizedXId,
                SubmitId = submitId,
            },
            cancellationToken: ct));

        if (affected == 0)
        {
            return NotFound(new { message = "未承認の対象データが見つかりません" });
        }

        return Ok(new { message = "ok" });
    }

    [HttpPost("events")]
    public async Task<IActionResult> CreateEvent(
        [FromBody] EventCreationRequest request,
        CancellationToken ct)
    {
        var authFailure = Authenticate();
        if (authFailure is not null) return authFailure;

        if (request.EndDate <= request.StartDate)
        {
            ModelState.AddModelError(
                nameof(request.EndDate),
                "endDate は startDate より後の日時である必要があります");
            return ValidationProblem(ModelState);
        }

        var startDate = request.StartDate.ToUniversalTime();
        var endDate = request.EndDate.ToUniversalTime();

        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        var approvalStatus = await conn.QuerySingleOrDefaultAsync<short?>(
            new CommandDefinition(
                "SELECT approval_status FROM weepa_songs WHERE weepa_song_id = @Id FOR UPDATE",
                new { Id = request.WeepaSongId },
                transaction: tx,
                cancellationToken: ct));

        if (approvalStatus is null)
        {
            await tx.RollbackAsync(ct);
            return NotFound(new { message = $"weepa_song_id {request.WeepaSongId} の楽曲が見つかりません" });
        }

        if (approvalStatus != 1)
        {
            await tx.RollbackAsync(ct);
            ModelState.AddModelError(
                nameof(request.WeepaSongId),
                "指定された楽曲は承認されていません");
            return ValidationProblem(ModelState);
        }

        var alreadyUsed = await conn.ExecuteScalarAsync<bool>(new CommandDefinition(
            "SELECT EXISTS (SELECT 1 FROM weepa_events WHERE weepa_song_id = @Id)",
            new { Id = request.WeepaSongId },
            transaction: tx,
            cancellationToken: ct));
        if (alreadyUsed)
        {
            await tx.RollbackAsync(ct);
            ModelState.AddModelError(
                nameof(request.WeepaSongId),
                "指定された楽曲は既にイベントで使用されています");
            return ValidationProblem(ModelState);
        }

        var eventId = await conn.ExecuteScalarAsync<int>(new CommandDefinition(
            """
            INSERT INTO weepa_events (weepa_song_id, start_date, end_date)
            VALUES (@WeepaSongId, @StartDate, @EndDate)
            RETURNING weepa_event_id
            """,
            new
            {
                request.WeepaSongId,
                StartDate = startDate,
                EndDate = endDate,
            },
            transaction: tx,
            cancellationToken: ct));

        await tx.CommitAsync(ct);

        return CreatedAtAction(
            actionName: "Get",
            controllerName: "Events",
            routeValues: new { id = eventId },
            value: new { weepaEventId = eventId });
    }

    [HttpGet("schedule")]
    public async Task<IActionResult> GetSchedule(CancellationToken ct)
    {
        var authFailure = Authenticate();
        if (authFailure is not null) return authFailure;

        await using var conn = await _dataSource.OpenConnectionAsync(ct);

        var (settings, settingsError) = await LoadScheduleSettingsAsync(conn, ct);
        if (settingsError is not null)
        {
            return settingsError;
        }

        // 現在開催中のイベント
        var current = await conn.QuerySingleOrDefaultAsync(new CommandDefinition(
            """
            SELECT
                e.weepa_event_id AS "weepaEventId",
                e.weepa_song_id  AS "weepaSongId",
                s.song           AS "song",
                s.effect         AS "effect",
                s.x_id           AS "xId",
                s.submit_name    AS "submitName",
                e.start_date     AS "startDate",
                e.end_date       AS "endDate",
                e.tallied_at     AS "talliedAt"
            FROM weepa_events e
            JOIN weepa_songs s ON s.weepa_song_id = e.weepa_song_id
            WHERE NOW() BETWEEN e.start_date AND e.end_date
            ORDER BY e.start_date DESC
            LIMIT 1
            """,
            cancellationToken: ct));

        // 過去のイベント (admin 用、未公開含む)
        var past = await conn.QueryAsync(new CommandDefinition(
            """
            SELECT
                e.weepa_event_id AS "weepaEventId",
                e.weepa_song_id  AS "weepaSongId",
                s.song           AS "song",
                s.effect         AS "effect",
                s.x_id           AS "xId",
                s.submit_name    AS "submitName",
                e.start_date     AS "startDate",
                e.end_date       AS "endDate",
                e.tallied_at     AS "talliedAt"
            FROM weepa_events e
            JOIN weepa_songs s ON s.weepa_song_id = e.weepa_song_id
            WHERE e.end_date < NOW()
            ORDER BY e.end_date DESC
            """,
            cancellationToken: ct));

        // 未来のイベント (= 開催キュー)。承認時に events に直接スケジュール枠が
        // 作られているので、ここでは start_date 順に並べるだけ。
        var queue = await conn.QueryAsync(new CommandDefinition(
            """
            SELECT
                e.weepa_event_id AS "weepaEventId",
                e.weepa_song_id  AS "weepaSongId",
                s.song           AS "song",
                s.effect         AS "effect",
                s.x_id           AS "xId",
                s.submit_name    AS "submitName",
                e.start_date     AS "startDate",
                e.end_date       AS "endDate"
            FROM weepa_events e
            JOIN weepa_songs s ON s.weepa_song_id = e.weepa_song_id
            WHERE e.start_date > NOW()
            ORDER BY e.start_date ASC
            """,
            cancellationToken: ct));

        return Ok(new
        {
            config = new
            {
                timezone = settings.TimeZone.Id,
                dayOfWeek = settings.DayOfWeek.ToString(),
                hour = settings.Hour,
                minute = settings.Minute,
                durationHours = settings.DurationHours,
            },
            current,
            queue,
            past,
        });
    }

    [HttpPut("schedule/reorder")]
    public async Task<IActionResult> ReorderSchedule(
        [FromBody] ScheduleReorderRequest request,
        CancellationToken ct)
    {
        var authFailure = Authenticate();
        if (authFailure is not null) return authFailure;

        if (request?.Order is null || request.Order.Length == 0)
        {
            return BadRequest(new { message = "order が空です" });
        }

        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        // 未来イベント (= キュー) 全件をロックして読み取り
        var upcoming = (await conn.QueryAsync<(int weepaEventId, DateTime startDate, DateTime endDate)>(
            new CommandDefinition(
                """
                SELECT weepa_event_id AS weepaEventId,
                       start_date     AS startDate,
                       end_date       AS endDate
                FROM weepa_events
                WHERE start_date > NOW()
                ORDER BY start_date ASC
                FOR UPDATE
                """,
                transaction: tx, cancellationToken: ct))).ToList();

        var ids = upcoming.Select(x => x.weepaEventId).ToHashSet();
        if (request.Order.Length != ids.Count
            || request.Order.Any(id => !ids.Contains(id)))
        {
            await tx.RollbackAsync(ct);
            return BadRequest(new
            {
                message = "order に未来イベントでない id が含まれているか、件数が一致しません",
                expected = ids.OrderBy(x => x).ToArray(),
                got = request.Order,
            });
        }

        // 現在の (start, end) スロット列を、新しい順序の event_id にあてがう
        var slots = upcoming.Select(x => (x.startDate, x.endDate)).ToList();
        for (var i = 0; i < request.Order.Length; i++)
        {
            await conn.ExecuteAsync(new CommandDefinition(
                """
                UPDATE weepa_events
                SET start_date = @Start, end_date = @End
                WHERE weepa_event_id = @Id
                """,
                new { Id = request.Order[i], Start = slots[i].startDate, End = slots[i].endDate },
                transaction: tx,
                cancellationToken: ct));
        }

        await tx.CommitAsync(ct);
        return Ok(new { message = "ok" });
    }

    [HttpPost("schedule/shuffle")]
    public async Task<IActionResult> ShuffleSchedule(CancellationToken ct)
    {
        var authFailure = Authenticate();
        if (authFailure is not null) return authFailure;

        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        var upcoming = (await conn.QueryAsync<(int weepaEventId, DateTime startDate, DateTime endDate)>(
            new CommandDefinition(
                """
                SELECT weepa_event_id AS weepaEventId,
                       start_date     AS startDate,
                       end_date       AS endDate
                FROM weepa_events
                WHERE start_date > NOW()
                ORDER BY start_date ASC
                FOR UPDATE
                """,
                transaction: tx, cancellationToken: ct))).ToList();

        if (upcoming.Count <= 1)
        {
            await tx.RollbackAsync(ct);
            return Ok(new { message = "shuffle 対象が 0〜1 件のためスキップ", count = upcoming.Count });
        }

        // Fisher-Yates で id 列をシャッフル
        var ids = upcoming.Select(x => x.weepaEventId).ToArray();
        for (int i = ids.Length - 1; i > 0; i--)
        {
            int j = Random.Shared.Next(0, i + 1);
            (ids[i], ids[j]) = (ids[j], ids[i]);
        }

        var slots = upcoming.Select(x => (x.startDate, x.endDate)).ToList();
        for (var i = 0; i < ids.Length; i++)
        {
            await conn.ExecuteAsync(new CommandDefinition(
                """
                UPDATE weepa_events
                SET start_date = @Start, end_date = @End
                WHERE weepa_event_id = @Id
                """,
                new { Id = ids[i], Start = slots[i].startDate, End = slots[i].endDate },
                transaction: tx, cancellationToken: ct));
        }

        await tx.CommitAsync(ct);
        return Ok(new { message = "ok", count = ids.Length });
    }

    [HttpPut("schedule/config")]
    public async Task<IActionResult> UpdateScheduleConfig(
        [FromBody] ScheduleConfigRequest request,
        CancellationToken ct)
    {
        var authFailure = Authenticate();
        if (authFailure is not null) return authFailure;

        if (request is null
            || request.DayOfWeek is < 0 or > 6
            || request.Hour is < 0 or > 23
            || request.Minute is < 0 or > 59
            || request.DurationHours <= 0)
        {
            return BadRequest(new
            {
                message = "値が範囲外です。dayOfWeek=0..6, hour=0..23, minute=0..59, durationHours>=1",
            });
        }

        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        // 設定を更新
        await conn.ExecuteAsync(new CommandDefinition(
            """
            UPDATE weepa_schedule_config
            SET day_of_week    = @DayOfWeek,
                hour           = @Hour,
                minute         = @Minute,
                duration_hours = @DurationHours
            WHERE id = 1
            """,
            new
            {
                request.DayOfWeek,
                request.Hour,
                request.Minute,
                request.DurationHours,
            },
            transaction: tx,
            cancellationToken: ct));

        // 新しい設定で未来イベント (queue) の start/end を再計算
        var (settings, settingsError) = await LoadScheduleSettingsAsync(conn, ct, tx);
        if (settingsError is not null)
        {
            await tx.RollbackAsync(ct);
            return settingsError;
        }

        // 起点 = 現在開催中の end_date があればそれ、なければ NOW
        var currentEnd = await conn.ExecuteScalarAsync<DateTime?>(new CommandDefinition(
            "SELECT end_date FROM weepa_events WHERE start_date <= NOW() AND end_date > NOW() ORDER BY end_date DESC LIMIT 1",
            transaction: tx,
            cancellationToken: ct));

        var nowUtc = DateTimeOffset.UtcNow;
        DateTimeOffset anchor = currentEnd.HasValue
            ? new DateTimeOffset(currentEnd.Value, TimeSpan.Zero)
            : nowUtc;

        var anchorLocal = TimeZoneInfo.ConvertTime(anchor, settings.TimeZone);
        var firstStartLocal = CalculateNextOccurrenceLocal(
            anchorLocal, settings.DayOfWeek, settings.Hour, settings.Minute);
        var firstStartUtc = firstStartLocal.ToUniversalTime();

        var upcoming = (await conn.QueryAsync<int>(new CommandDefinition(
            "SELECT weepa_event_id FROM weepa_events WHERE start_date > NOW() ORDER BY start_date ASC FOR UPDATE",
            transaction: tx,
            cancellationToken: ct))).ToList();

        // 開催の cadence は曜日ベースなので 1 週間 (168h) 固定。durationHours は
        // 「開催が続く時間」のみを意味する (例: 165h ならイベント終了後3時間空けて
        // 次回が同じ曜日同じ時刻に始まる)
        const double WeekHours = 24.0 * 7.0;
        for (var i = 0; i < upcoming.Count; i++)
        {
            var newStart = firstStartUtc.AddHours(i * WeekHours);
            var newEnd = newStart.AddHours(settings.DurationHours);
            await conn.ExecuteAsync(new CommandDefinition(
                "UPDATE weepa_events SET start_date = @Start, end_date = @End WHERE weepa_event_id = @Id",
                new { Id = upcoming[i], Start = newStart, End = newEnd },
                transaction: tx,
                cancellationToken: ct));
        }

        await tx.CommitAsync(ct);
        return Ok(new { message = "ok", recomputed = upcoming.Count });
    }

    [HttpPost("events/{id:int}/publish")]
    public async Task<IActionResult> PublishEvent(int id, CancellationToken ct)
    {
        var authFailure = Authenticate();
        if (authFailure is not null) return authFailure;

        await using var conn = await _dataSource.OpenConnectionAsync(ct);

        // 未処理のスコア (approval_status=0) があれば公開を拒否する
        var pendingCount = await conn.ExecuteScalarAsync<int>(new CommandDefinition(
            "SELECT COUNT(*) FROM weepa_scores WHERE weepa_event_id = @Id AND approval_status = 0",
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
            "UPDATE weepa_events SET tallied_at = NOW() WHERE weepa_event_id = @Id AND tallied_at IS NULL",
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
            "UPDATE weepa_events SET tallied_at = NULL WHERE weepa_event_id = @Id",
            new { Id = id },
            cancellationToken: ct));

        if (affected == 0)
        {
            return NotFound(new { message = "対象イベントが見つかりません" });
        }

        return Ok(new { message = "ok" });
    }

    private async Task<(AutoWeeklySettings settings, IActionResult? error)> LoadScheduleSettingsAsync(
        Npgsql.NpgsqlConnection conn,
        CancellationToken ct,
        Npgsql.NpgsqlTransaction? tx = null)
    {
        var row = await conn.QuerySingleOrDefaultAsync<(short DayOfWeek, short Hour, short Minute, int DurationHours, string Timezone)?>(
            new CommandDefinition(
                """
                SELECT day_of_week AS DayOfWeek,
                       hour        AS Hour,
                       minute      AS Minute,
                       duration_hours AS DurationHours,
                       timezone    AS Timezone
                FROM weepa_schedule_config
                WHERE id = 1
                """,
                transaction: tx,
                cancellationToken: ct));

        if (row is null)
        {
            return (default, StatusCode(StatusCodes.Status503ServiceUnavailable,
                new { message = "weepa_schedule_config の初期行が存在しません" }));
        }

        TimeZoneInfo timezone;
        try
        {
            timezone = TimeZoneInfo.FindSystemTimeZoneById(row.Value.Timezone);
        }
        catch
        {
            return (default, ValidationProblemFromMessage(
                "timezone", $"タイムゾーン '{row.Value.Timezone}' は無効です"));
        }

        var settings = new AutoWeeklySettings(
            timezone,
            (DayOfWeek)row.Value.DayOfWeek,
            row.Value.Hour,
            row.Value.Minute,
            row.Value.DurationHours);
        return (settings, null);
    }

    private bool TryGetAutoWeeklySettings(
        out AutoWeeklySettings settings,
        out IActionResult? error)
    {
        // フォールバック用 (起動直後に DB が無い等の状況)。通常は LoadScheduleSettingsAsync を使う。
        TimeZoneInfo timezone;
        try
        {
            timezone = TimeZoneInfo.FindSystemTimeZoneById("Asia/Tokyo");
        }
        catch
        {
            settings = default;
            error = ValidationProblemFromMessage("timezone", "タイムゾーンが取得できません");
            return false;
        }

        settings = new AutoWeeklySettings(timezone, DayOfWeek.Wednesday, 21, 0, 168);
        error = null;
        return true;
    }

    private IActionResult ValidationProblemFromMessage(string key, string message)
    {
        ModelState.AddModelError(key, message);
        return ValidationProblem(ModelState);
    }

    private static bool TryParseDayOfWeek(string value, out DayOfWeek dayOfWeek)
    {
        if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var numeric)
            && numeric is >= 0 and <= 6)
        {
            dayOfWeek = (DayOfWeek)numeric;
            return true;
        }

        return Enum.TryParse(value, ignoreCase: true, out dayOfWeek);
    }

    private static DateTimeOffset CalculateNextOccurrenceLocal(
        DateTimeOffset nowLocal,
        DayOfWeek dayOfWeek,
        int hour,
        int minute)
    {
        var dayDiff = ((int)dayOfWeek - (int)nowLocal.DayOfWeek + 7) % 7;
        var candidate = new DateTimeOffset(
            nowLocal.Year,
            nowLocal.Month,
            nowLocal.Day,
            hour,
            minute,
            0,
            nowLocal.Offset).AddDays(dayDiff);

        if (candidate < nowLocal)
        {
            candidate = candidate.AddDays(7);
        }

        return candidate;
    }

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
        // WWW-Authenticate: Basic を返すとブラウザがネイティブの認証ダイアログを
        // 強制表示してしまう。/admin/login/ で独自ログイン UI を持っているので、
        // 401 だけ返してフロント側 (admin-common.js) に redirect させる。
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

    private static string? ToUploadPhysicalPath(string imagePath)
    {
        const string publicPrefix = "/uploads/";
        if (!imagePath.StartsWith(publicPrefix, StringComparison.Ordinal))
        {
            return null;
        }

        var relative = imagePath[publicPrefix.Length..]
            .Replace('/', Path.DirectorySeparatorChar);
        return Path.Combine(UploadsRoot, relative);
    }

    private readonly record struct AutoWeeklySettings(
        TimeZoneInfo TimeZone,
        DayOfWeek DayOfWeek,
        int Hour,
        int Minute,
        int DurationHours);
}
