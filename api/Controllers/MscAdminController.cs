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
