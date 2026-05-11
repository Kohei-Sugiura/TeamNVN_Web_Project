using Dapper;
using Microsoft.AspNetCore.Mvc;
using Npgsql;
using System.Text.Json;
using Weepa.Api.Models.Requests;
using Weepa.Api.Models.Responses;
using Weepa.Api.Validation;

namespace Weepa.Api.Controllers;

/// <summary>
/// MoonShotChallenge (MSC) 公開エンドポイント。
/// - GET /api/msc/events/{id}: イベント詳細 (公開/非公開問わず eventId 直指定なら返す)
/// - GET /api/msc/events/current: 現在開催中のイベント
/// - POST /api/msc/events/{eventId}/submissions: スコア提出 (multipart)
/// </summary>
[ApiController]
[Route("api/msc")]
public class MscController : ControllerBase
{
    private const long MaxImageBytes = 20L * 1024 * 1024; // 20MB / 枚
    private const long MaxTotalBytes = 110L * 1024 * 1024; // 全体 ~110MB (画像 5 枚分余裕)
    private const string UploadsRoot = "/app/uploads/pending/msc";

    private static readonly Dictionary<string, string> AllowedContentTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["image/jpeg"] = "jpg",
        ["image/jpg"]  = "jpg",
        ["image/png"]  = "png",
        ["image/webp"] = "webp",
    };

    private readonly NpgsqlDataSource _dataSource;
    private readonly ILogger<MscController> _logger;

    public MscController(NpgsqlDataSource dataSource, ILogger<MscController> logger)
    {
        _dataSource = dataSource;
        _logger = logger;
    }

    [HttpGet("events/{id:int}")]
    public async Task<IActionResult> Get(int id, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var ev = await conn.QuerySingleOrDefaultAsync<MscEventResponse>(new CommandDefinition(
            """
            SELECT msc_event_id AS "MscEventId",
                   title        AS "Title",
                   description  AS "Description",
                   start_date   AS "StartDate",
                   end_date     AS "EndDate",
                   tallied_at   AS "TalliedAt",
                   created_at   AS "CreatedAt"
            FROM msc_events
            WHERE msc_event_id = @Id
            """,
            new { Id = id },
            cancellationToken: ct));
        if (ev is null) return NotFound(new { message = $"msc_event_id {id} が見つかりません" });

        var songs = await conn.QueryAsync<MscSongResponse>(new CommandDefinition(
            """
            SELECT song_order AS "SongOrder", song AS "Song", effect AS "Effect"
            FROM msc_songs
            WHERE msc_event_id = @Id
            ORDER BY song_order
            """,
            new { Id = id }, cancellationToken: ct));
        ev.Songs = songs.ToList();
        return Ok(ev);
    }

    [HttpGet("events/current")]
    public async Task<IActionResult> Current(CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var ev = await conn.QuerySingleOrDefaultAsync<MscEventResponse>(new CommandDefinition(
            """
            SELECT msc_event_id AS "MscEventId",
                   title        AS "Title",
                   description  AS "Description",
                   start_date   AS "StartDate",
                   end_date     AS "EndDate",
                   tallied_at   AS "TalliedAt",
                   created_at   AS "CreatedAt"
            FROM msc_events
            WHERE NOW() BETWEEN start_date AND end_date
            ORDER BY start_date DESC
            LIMIT 1
            """,
            cancellationToken: ct));

        if (ev is null) return Ok(new { @event = (object?)null });

        var songs = await conn.QueryAsync<MscSongResponse>(new CommandDefinition(
            """
            SELECT song_order AS "SongOrder", song AS "Song", effect AS "Effect"
            FROM msc_songs
            WHERE msc_event_id = @Id
            ORDER BY song_order
            """,
            new { Id = ev.MscEventId }, cancellationToken: ct));
        ev.Songs = songs.ToList();
        return Ok(new { @event = ev });
    }

    [HttpPost("events/{eventId:int}/submissions")]
    [RequestSizeLimit(MaxTotalBytes)]
    [RequestFormLimits(MultipartBodyLengthLimit = MaxTotalBytes)]
    public async Task<IActionResult> Submit(
        int eventId,
        [FromForm] MscSubmissionRequest request,
        CancellationToken ct)
    {
        // 1. X ID 正規化
        var normalizedXId = XIdNormalizer.Normalize(request.XId);
        if (!XIdNormalizer.IsValid(normalizedXId))
        {
            ModelState.AddModelError(nameof(request.XId),
                "X ID の形式が正しくありません (半角英数字と _ のみ、1〜15文字)");
            return ValidationProblem(ModelState);
        }

        // 2. plays JSON parse
        List<MscPlayInput> plays;
        try
        {
            plays = JsonSerializer.Deserialize<List<MscPlayInput>>(
                request.Plays,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                ?? new List<MscPlayInput>();
        }
        catch (JsonException)
        {
            ModelState.AddModelError(nameof(request.Plays), "plays は JSON 配列で送信してください");
            return ValidationProblem(ModelState);
        }

        // 3. イベント + 曲数を取得
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var ev = await conn.QuerySingleOrDefaultAsync<(bool InPeriod, int SongCount)?>(new CommandDefinition(
            """
            SELECT
                (NOW() BETWEEN start_date AND end_date) AS InPeriod,
                (SELECT COUNT(*) FROM msc_songs WHERE msc_event_id = @EventId)::int AS SongCount
            FROM msc_events
            WHERE msc_event_id = @EventId
            """,
            new { EventId = eventId }, cancellationToken: ct));

        if (ev is null) return NotFound(new { message = $"msc_event_id {eventId} が見つかりません" });
        var n = ev.Value.SongCount;
        if (n < 2)
        {
            return BadRequest(new { message = "課題曲が未設定のイベントです" });
        }
        if (!ev.Value.InPeriod)
        {
            return BadRequest(new { message = "現在このイベントは投稿を受け付けていません" });
        }

        // 4. plays の検証
        if (plays.Count < n || plays.Count > 4)
        {
            ModelState.AddModelError(nameof(request.Plays),
                $"plays は {n} 件以上 4 件以下です (このイベントは課題曲 {n} 曲)");
            return ValidationProblem(ModelState);
        }

        // play_order が 1..plays.Count の連番か
        var orders = plays.Select(p => p.PlayOrder).OrderBy(x => x).ToList();
        for (int i = 0; i < orders.Count; i++)
        {
            if (orders[i] != i + 1)
            {
                ModelState.AddModelError(nameof(request.Plays),
                    $"playOrder は 1..{plays.Count} の連番で指定してください");
                return ValidationProblem(ModelState);
            }
        }

        foreach (var p in plays)
        {
            if (p.Score < 0 || p.Score > 10_000_000)
            {
                ModelState.AddModelError(nameof(request.Plays),
                    $"playOrder={p.PlayOrder} の score が範囲外 (0..10,000,000)");
                return ValidationProblem(ModelState);
            }
            if (p.SongOrder < 1 || p.SongOrder > n)
            {
                ModelState.AddModelError(nameof(request.Plays),
                    $"playOrder={p.PlayOrder} の songOrder は 1..{n} の範囲です (got {p.SongOrder})");
                return ValidationProblem(ModelState);
            }
            // 必修枠
            if (p.PlayOrder <= n && p.SongOrder != p.PlayOrder)
            {
                ModelState.AddModelError(nameof(request.Plays),
                    $"playOrder={p.PlayOrder} は指定楽曲枠なので songOrder={p.PlayOrder} を指定してください");
                return ValidationProblem(ModelState);
            }
        }

        // 5. 画像の検証 (各画像個別の content-type + サイズ)
        var imagesByPlayOrder = new Dictionary<int, (IFormFile File, string Ext)>();
        var allFiles = new (int Order, IFormFile? File)[]
        {
            (0, request.ImageSummary),
            (1, request.ImagePlay1),
            (2, request.ImagePlay2),
            (3, request.ImagePlay3),
            (4, request.ImagePlay4),
        };
        foreach (var (order, file) in allFiles)
        {
            if (file is null) continue;
            if (file.Length == 0)
            {
                ModelState.AddModelError("Image", $"image (play_order={order}) が空です");
                return ValidationProblem(ModelState);
            }
            if (file.Length > MaxImageBytes)
            {
                ModelState.AddModelError("Image", $"image (play_order={order}) が 20MB を超えています");
                return ValidationProblem(ModelState);
            }
            if (!AllowedContentTypes.TryGetValue(file.ContentType, out var ext))
            {
                ModelState.AddModelError("Image", $"image (play_order={order}) は JPEG/PNG/WebP のみ");
                return ValidationProblem(ModelState);
            }
            // konaste 専用画像は plays に対応する play_order でないなら拒否
            if (order >= 1 && order > plays.Count)
            {
                ModelState.AddModelError("Image",
                    $"plays.Count={plays.Count} なので imagePlay{order} は受け付けません");
                return ValidationProblem(ModelState);
            }
            imagesByPlayOrder[order] = (file, ext);
        }

        if (request.Platform == "arcade")
        {
            if (!imagesByPlayOrder.ContainsKey(0))
            {
                ModelState.AddModelError("Image", "arcade では imageSummary (通しリザルト画像) が必須です");
                return ValidationProblem(ModelState);
            }
        }
        else // konaste: summary + 必修枠 (play 1..n) 全部必須、リトライ枠 (n+1..4) は任意
        {
            if (!imagesByPlayOrder.ContainsKey(0))
            {
                ModelState.AddModelError("Image", "konaste では summary 画像が必須です");
                return ValidationProblem(ModelState);
            }
            for (int i = 1; i <= n; i++)
            {
                if (!imagesByPlayOrder.ContainsKey(i))
                {
                    ModelState.AddModelError("Image",
                        $"konaste では Track {i} (指定楽曲) のリザルト画像が必須です");
                    return ValidationProblem(ModelState);
                }
            }
        }

        // 6. submission + scores + images を 1 TX で保存
        await using var tx = await conn.BeginTransactionAsync(ct);

        // 6a. msc_submissions に header 行 (submit_id はトリガで自動採番)
        int submitId = 0;
        bool ok = false;
        for (int attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                submitId = await conn.ExecuteScalarAsync<int>(new CommandDefinition(
                    """
                    INSERT INTO msc_submissions (msc_event_id, x_id, submit_name, platform)
                    VALUES (@EventId, @XId, @SubmitName, @Platform)
                    RETURNING submit_id
                    """,
                    new
                    {
                        EventId = eventId,
                        XId = normalizedXId,
                        request.SubmitName,
                        request.Platform,
                    },
                    transaction: tx, cancellationToken: ct));
                ok = true;
                break;
            }
            catch (PostgresException ex) when (ex.SqlState == "23505")
            {
                _logger.LogWarning(
                    "msc submit_id collision (attempt {Attempt}) for event={EventId} xId={XId}",
                    attempt + 1, eventId, normalizedXId);
                await Task.Delay(50 * (attempt + 1), ct);
            }
        }
        if (!ok)
        {
            await tx.RollbackAsync(ct);
            return StatusCode(StatusCodes.Status503ServiceUnavailable,
                new { message = "投稿処理が混み合っています。時間を置いて再度お試しください" });
        }

        // 6b. msc_scores
        foreach (var p in plays.OrderBy(p => p.PlayOrder))
        {
            await conn.ExecuteAsync(new CommandDefinition(
                """
                INSERT INTO msc_scores (msc_event_id, x_id, submit_id, play_order, song_order, score)
                VALUES (@EventId, @XId, @SubmitId, @PlayOrder, @SongOrder, @Score)
                """,
                new
                {
                    EventId = eventId,
                    XId = normalizedXId,
                    SubmitId = submitId,
                    p.PlayOrder,
                    p.SongOrder,
                    p.Score,
                },
                transaction: tx, cancellationToken: ct));
        }

        // 6c. 画像を disk に保存 + msc_submission_images に登録
        var savedPaths = new List<string>();
        try
        {
            var dir = Path.Combine(UploadsRoot, eventId.ToString(), submitId.ToString());
            Directory.CreateDirectory(dir);
            foreach (var (order, info) in imagesByPlayOrder)
            {
                var fileName = $"play{order}.{info.Ext}";
                var fullPath = Path.Combine(dir, fileName);
                await using (var stream = System.IO.File.Create(fullPath))
                {
                    await info.File.CopyToAsync(stream, ct);
                }
                savedPaths.Add(fullPath);
                var publicPath = $"/uploads/pending/msc/{eventId}/{submitId}/{fileName}";
                await conn.ExecuteAsync(new CommandDefinition(
                    """
                    INSERT INTO msc_submission_images (msc_event_id, x_id, submit_id, play_order, image_path)
                    VALUES (@EventId, @XId, @SubmitId, @PlayOrder, @ImagePath)
                    """,
                    new
                    {
                        EventId = eventId,
                        XId = normalizedXId,
                        SubmitId = submitId,
                        PlayOrder = order,
                        ImagePath = publicPath,
                    },
                    transaction: tx, cancellationToken: ct));
            }
        }
        catch
        {
            // 画像保存途中で失敗 → 既保存ファイルを掃除して TX rollback
            foreach (var path in savedPaths)
            {
                try { System.IO.File.Delete(path); } catch { /* swallow */ }
            }
            await tx.RollbackAsync(ct);
            throw;
        }

        await tx.CommitAsync(ct);
        return Ok(new { message = "ok", submitId });
    }
}
