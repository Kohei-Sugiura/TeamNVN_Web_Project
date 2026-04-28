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

        if (!TryGetAutoWeeklySettings(out var settings, out var settingsError))
        {
            return settingsError!;
        }

        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

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

        // 承認時に開催枠 (event) を自動生成。
        // start = 既存全イベントの最遅 end_date の続き、無ければ次回の cron テンプレ枠
        var lastEnd = await conn.ExecuteScalarAsync<DateTime?>(new CommandDefinition(
            "SELECT MAX(end_date) FROM weepa_events",
            transaction: tx,
            cancellationToken: ct));

        var nowUtc = DateTimeOffset.UtcNow;
        DateTimeOffset start;
        if (lastEnd.HasValue && new DateTimeOffset(lastEnd.Value, TimeSpan.Zero) > nowUtc)
        {
            start = new DateTimeOffset(lastEnd.Value, TimeSpan.Zero);
        }
        else
        {
            var nowLocal = TimeZoneInfo.ConvertTime(nowUtc, settings.TimeZone);
            var startLocal = CalculateNextOccurrenceLocal(
                nowLocal, settings.DayOfWeek, settings.Hour, settings.Minute);
            start = startLocal.ToUniversalTime();
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
        return Ok(new { message = "ok", scheduledStart = start, scheduledEnd = end });
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

        if (!TryGetAutoWeeklySettings(out var settings, out var settingsError))
        {
            return settingsError!;
        }

        await using var conn = await _dataSource.OpenConnectionAsync(ct);

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

    [HttpPost("events/{id:int}/publish")]
    public async Task<IActionResult> PublishEvent(int id, CancellationToken ct)
    {
        var authFailure = Authenticate();
        if (authFailure is not null) return authFailure;

        await using var conn = await _dataSource.OpenConnectionAsync(ct);
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

    private bool TryGetAutoWeeklySettings(
        out AutoWeeklySettings settings,
        out IActionResult? error)
    {
        var timezoneId = _configuration["AUTO_WEEKLY_TIMEZONE"];
        if (string.IsNullOrWhiteSpace(timezoneId))
        {
            timezoneId = "Asia/Tokyo";
        }

        TimeZoneInfo timezone;
        try
        {
            timezone = TimeZoneInfo.FindSystemTimeZoneById(timezoneId);
        }
        catch
        {
            settings = default;
            error = ValidationProblemFromMessage(
                "AUTO_WEEKLY_TIMEZONE",
                $"タイムゾーン '{timezoneId}' は無効です");
            return false;
        }

        var dayValue = _configuration["AUTO_WEEKLY_DAY_OF_WEEK"];
        if (string.IsNullOrWhiteSpace(dayValue))
        {
            dayValue = "Wednesday";
        }

        if (!TryParseDayOfWeek(dayValue, out var dayOfWeek))
        {
            settings = default;
            error = ValidationProblemFromMessage(
                "AUTO_WEEKLY_DAY_OF_WEEK",
                "曜日は 0-6 または Sunday..Saturday で指定してください");
            return false;
        }

        if (!int.TryParse(_configuration["AUTO_WEEKLY_HOUR"], NumberStyles.Integer, CultureInfo.InvariantCulture, out var hour))
        {
            hour = 21;
        }

        if (!int.TryParse(_configuration["AUTO_WEEKLY_MINUTE"], NumberStyles.Integer, CultureInfo.InvariantCulture, out var minute))
        {
            minute = 0;
        }

        if (!int.TryParse(_configuration["AUTO_WEEKLY_DURATION_HOURS"], NumberStyles.Integer, CultureInfo.InvariantCulture, out var durationHours))
        {
            durationHours = 168;
        }

        if (hour is < 0 or > 23)
        {
            settings = default;
            error = ValidationProblemFromMessage(
                "AUTO_WEEKLY_HOUR",
                "時間は 0〜23 の範囲で指定してください");
            return false;
        }

        if (minute is < 0 or > 59)
        {
            settings = default;
            error = ValidationProblemFromMessage(
                "AUTO_WEEKLY_MINUTE",
                "分は 0〜59 の範囲で指定してください");
            return false;
        }

        if (durationHours <= 0)
        {
            settings = default;
            error = ValidationProblemFromMessage(
                "AUTO_WEEKLY_DURATION_HOURS",
                "期間は 1 以上の時間で指定してください");
            return false;
        }

        settings = new AutoWeeklySettings(timezone, dayOfWeek, hour, minute, durationHours);
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
