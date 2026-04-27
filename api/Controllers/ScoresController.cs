using Dapper;
using Microsoft.AspNetCore.Mvc;
using Npgsql;
using Weepa.Api.Models.Requests;
using Weepa.Api.Validation;

namespace Weepa.Api.Controllers;

[ApiController]
[Route("api/events/{eventId:int}/scores")]
public class ScoresController : ControllerBase
{
    private const long MaxImageBytes = 20L * 1024 * 1024; // 20MB
    private const string UploadsRoot = "/app/uploads/pending";

    private static readonly Dictionary<string, string> AllowedContentTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["image/jpeg"] = "jpg",
        ["image/jpg"]  = "jpg",
        ["image/png"]  = "png",
        ["image/webp"] = "webp",
    };

    private readonly NpgsqlDataSource _dataSource;
    private readonly ILogger<ScoresController> _logger;

    public ScoresController(NpgsqlDataSource dataSource, ILogger<ScoresController> logger)
    {
        _dataSource = dataSource;
        _logger = logger;
    }

    [HttpPost]
    [RequestSizeLimit(25_000_000)]
    [RequestFormLimits(MultipartBodyLengthLimit = 25_000_000)]
    public async Task<IActionResult> Submit(
        int eventId,
        [FromForm] ScoreSubmissionRequest request,
        CancellationToken ct)
    {
        // 1. 画像バリデーション
        var image = request.Image;
        if (image.Length == 0)
        {
            ModelState.AddModelError(nameof(request.Image), "画像ファイルが空です");
            return ValidationProblem(ModelState);
        }
        if (image.Length > MaxImageBytes)
        {
            ModelState.AddModelError(nameof(request.Image),
                $"画像サイズが大きすぎます ({image.Length / 1024 / 1024} MB / 上限 20 MB)");
            return ValidationProblem(ModelState);
        }
        if (!AllowedContentTypes.TryGetValue(image.ContentType, out var extension))
        {
            ModelState.AddModelError(nameof(request.Image),
                "画像形式は JPEG / PNG / WebP のみ対応しています");
            return ValidationProblem(ModelState);
        }

        // 2. X ID 正規化
        var normalizedXId = XIdNormalizer.Normalize(request.XId);
        if (!XIdNormalizer.IsValid(normalizedXId))
        {
            ModelState.AddModelError(nameof(request.XId),
                "X ID の形式が正しくありません (半角英数字と _ のみ、1〜15文字)");
            return ValidationProblem(ModelState);
        }

        // 3. イベント存在 + 受付期間内チェック
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var eventState = await conn.QuerySingleOrDefaultAsync<(bool exists, bool inPeriod)?>(
            new CommandDefinition("""
                SELECT
                    TRUE AS exists,
                    (NOW() BETWEEN start_date AND end_date) AS in_period
                FROM weepa_events
                WHERE weepa_event_id = @EventId
                """,
                new { EventId = eventId },
                cancellationToken: ct));

        if (eventState is null)
        {
            return NotFound(new { message = $"event_id {eventId} が見つかりません" });
        }
        if (!eventState.Value.inPeriod)
        {
            return BadRequest(new { message = "現在このイベントは投稿を受け付けていません" });
        }

        // 4. 画像を disk に保存 (先に保存、失敗したら DB にも入れない)
        var eventDir = Path.Combine(UploadsRoot, eventId.ToString());
        Directory.CreateDirectory(eventDir);
        var fileName = $"{Guid.NewGuid():N}.{extension}";
        var fullPath = Path.Combine(eventDir, fileName);
        var publicImagePath = $"/uploads/pending/{eventId}/{fileName}";

        await using (var stream = System.IO.File.Create(fullPath))
        {
            await image.CopyToAsync(stream, ct);
        }

        // 5. DB 挿入 (submit_id トリガー衝突時は最大3回リトライ)
        const string insertSql = """
            INSERT INTO weepa_scores
                (weepa_event_id, x_id, submit_name, score, ex_score, image_path)
            VALUES
                (@EventId, @XId, @SubmitName, @Score, @ExScore, @ImagePath)
            RETURNING submit_id
            """;

        int? submitId = null;
        for (int attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                submitId = await conn.ExecuteScalarAsync<int>(new CommandDefinition(
                    insertSql,
                    new
                    {
                        EventId = eventId,
                        XId = normalizedXId,
                        request.SubmitName,
                        request.Score,
                        request.ExScore,
                        ImagePath = publicImagePath,
                    },
                    cancellationToken: ct));
                break;
            }
            catch (PostgresException ex) when (ex.SqlState == "23505")
            {
                _logger.LogWarning(
                    "submit_id trigger collision (attempt {Attempt}) for event={EventId} xId={XId}",
                    attempt + 1, eventId, normalizedXId);
                await Task.Delay(50 * (attempt + 1), ct);
            }
        }

        if (submitId is null)
        {
            // 衝突リトライで確定できず → 保存した画像を消してから失敗返却
            try { System.IO.File.Delete(fullPath); } catch { /* swallow */ }
            return StatusCode(StatusCodes.Status503ServiceUnavailable,
                new { message = "投稿処理が混み合っています。時間を置いて再度お試しください" });
        }

        return Ok(new { message = "ok" });
    }
}
