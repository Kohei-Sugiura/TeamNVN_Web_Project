using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Weepa.Api.Models.Responses;
using Weepa.Api.Services;

namespace Weepa.Api.Controllers;

/// <summary>
/// リザルト画像から SCORE / EX SCORE を Claude vision で抽出する。
/// 公開エンドポイントだが IP ベースのレート制限 ("recognize" ポリシー) を適用。
/// </summary>
[ApiController]
[Route("api/score")]
public class ScoreRecognitionController : ControllerBase
{
    private const long MaxImageBytes = 10L * 1024 * 1024; // 10 MB

    private static readonly HashSet<string> AllowedContentTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "image/jpeg",
        "image/jpg",
        "image/png",
        "image/webp",
    };

    private readonly ClaudeImageRecognitionService _svc;
    private readonly ILogger<ScoreRecognitionController> _logger;

    public ScoreRecognitionController(
        ClaudeImageRecognitionService svc,
        ILogger<ScoreRecognitionController> logger)
    {
        _svc = svc;
        _logger = logger;
    }

    [HttpPost("recognize")]
    [EnableRateLimiting("recognize")]
    [RequestSizeLimit(MaxImageBytes)]
    [RequestFormLimits(MultipartBodyLengthLimit = MaxImageBytes)]
    public async Task<IActionResult> Recognize(
        [FromForm(Name = "image")] IFormFile? image,
        CancellationToken ct)
    {
        if (!_svc.Enabled)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new ScoreRecognitionResponse
            {
                Success = false,
                Error = "画像認識機能が無効です",
            });
        }

        if (image is null || image.Length == 0)
        {
            return BadRequest(new ScoreRecognitionResponse
            {
                Success = false,
                Error = "画像ファイルが指定されていません",
            });
        }
        if (image.Length > MaxImageBytes)
        {
            return BadRequest(new ScoreRecognitionResponse
            {
                Success = false,
                Error = "画像サイズが上限 (10MB) を超えています",
            });
        }

        // ブラウザによっては content-type が "image/jpg" になることがある → 正規化
        var contentType = image.ContentType.Equals("image/jpg", StringComparison.OrdinalIgnoreCase)
            ? "image/jpeg" : image.ContentType;
        if (!AllowedContentTypes.Contains(contentType))
        {
            return BadRequest(new ScoreRecognitionResponse
            {
                Success = false,
                Error = "対応していない画像形式です (JPEG / PNG / WebP)",
            });
        }

        byte[] bytes;
        try
        {
            using var ms = new MemoryStream();
            await image.CopyToAsync(ms, ct);
            bytes = ms.ToArray();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "image read failed");
            return BadRequest(new ScoreRecognitionResponse
            {
                Success = false,
                Error = "画像の読み込みに失敗しました",
            });
        }

        var result = await _svc.RecognizeAsync(bytes, contentType, ct);
        if (!result.Success)
        {
            return StatusCode(StatusCodes.Status502BadGateway, new ScoreRecognitionResponse
            {
                Success = false,
                Error = result.Error,
            });
        }

        return Ok(new ScoreRecognitionResponse
        {
            Success = true,
            Score = result.Score,
            ExScore = result.ExScore,
        });
    }
}
