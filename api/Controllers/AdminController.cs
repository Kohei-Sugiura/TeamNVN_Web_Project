using Dapper;
using Microsoft.AspNetCore.Mvc;
using Npgsql;
using Weepa.Api.Models.Requests;

namespace Weepa.Api.Controllers;

/// <summary>
/// 管理者向けエンドポイント。
/// Step 6 で Basic 認証を後付けする予定。
/// </summary>
[ApiController]
[Route("api/admin")]
public class AdminController : ControllerBase
{
    private readonly NpgsqlDataSource _dataSource;

    public AdminController(NpgsqlDataSource dataSource)
    {
        _dataSource = dataSource;
    }

    [HttpPost("events")]
    public async Task<IActionResult> CreateEvent(
        [FromBody] EventCreationRequest request,
        CancellationToken ct)
    {
        if (request.EndDate <= request.StartDate)
        {
            ModelState.AddModelError(
                nameof(request.EndDate),
                "endDate は startDate より後の日時である必要があります");
            return ValidationProblem(ModelState);
        }

        await using var conn = await _dataSource.OpenConnectionAsync(ct);

        var approvalStatus = await conn.QuerySingleOrDefaultAsync<short?>(
            new CommandDefinition(
                "SELECT approval_status FROM weepa_songs WHERE weepa_song_id = @Id",
                new { Id = request.WeepaSongId },
                cancellationToken: ct));

        if (approvalStatus is null)
        {
            return NotFound(new { message = $"weepa_song_id {request.WeepaSongId} の楽曲が見つかりません" });
        }

        if (approvalStatus != 1)
        {
            ModelState.AddModelError(
                nameof(request.WeepaSongId),
                "指定された楽曲は承認されていません");
            return ValidationProblem(ModelState);
        }

        const string insertSql = """
            INSERT INTO weepa_events (weepa_song_id, start_date, end_date)
            VALUES (@WeepaSongId, @StartDate, @EndDate)
            RETURNING weepa_event_id
            """;

        var eventId = await conn.ExecuteScalarAsync<int>(new CommandDefinition(
            insertSql,
            new
            {
                request.WeepaSongId,
                request.StartDate,
                request.EndDate,
            },
            cancellationToken: ct));

        return CreatedAtAction(
            actionName: "Get",
            controllerName: "Events",
            routeValues: new { id = eventId },
            value: new { weepaEventId = eventId });
    }
}
