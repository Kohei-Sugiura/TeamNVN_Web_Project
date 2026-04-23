using Dapper;
using Microsoft.AspNetCore.Mvc;
using Npgsql;
using Weepa.Api.Models.Requests;
using Weepa.Api.Validation;

namespace Weepa.Api.Controllers;

[ApiController]
[Route("api/songs")]
public class SongsController : ControllerBase
{
    private readonly NpgsqlDataSource _dataSource;

    public SongsController(NpgsqlDataSource dataSource)
    {
        _dataSource = dataSource;
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

        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var id = await conn.ExecuteScalarAsync<int>(new CommandDefinition(
            sql,
            new
            {
                Song = request.Song.Trim(),
                Effect = request.Effect,
                XId = normalizedXId,
                SubmitName = request.SubmitName.Trim(),
                SubmitReason = string.IsNullOrWhiteSpace(request.SubmitReason)
                    ? null
                    : request.SubmitReason.Trim(),
            },
            cancellationToken: ct));

        return Ok(new { weepaSongId = id });
    }
}
