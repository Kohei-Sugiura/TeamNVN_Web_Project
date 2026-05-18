using System.Text;
using System.Text.Json;
using Dapper;
using Microsoft.AspNetCore.Mvc;
using Weepa.Api.Services;

namespace Weepa.Api.Controllers;

[ApiController]
[Route("api/discord")]
public class DiscordInteractionsController : ControllerBase
{
    // Discord Interaction types
    private const int TYPE_PING = 1;
    private const int TYPE_APPLICATION_COMMAND = 2;
    private const int TYPE_MESSAGE_COMPONENT = 3;
    private const int TYPE_MODAL_SUBMIT = 5;

    // Interaction Response types
    private const int RESP_PONG = 1;
    private const int RESP_CHANNEL_MESSAGE = 4;
    private const int RESP_DEFERRED_UPDATE_MESSAGE = 6;
    private const int RESP_UPDATE_MESSAGE = 7;
    private const int RESP_MODAL = 9;

    // Component types
    private const int COMP_ACTION_ROW = 1;
    private const int COMP_BUTTON = 2;
    private const int COMP_TEXT_INPUT = 4;

    // Text Input style
    private const int TEXT_INPUT_SHORT = 1;

    // Response message flag
    private const int FLAG_EPHEMERAL = 64;

    // Interaction Response type for deferred channel message
    private const int RESP_DEFERRED_CHANNEL_MESSAGE = 5;

    private readonly WeepaAdminClient _admin;
    private readonly IConfiguration _config;
    private readonly ILogger<DiscordInteractionsController> _logger;
    private readonly Npgsql.NpgsqlDataSource _dataSource;
    private readonly DiscordCommandClient _cmd;
    private readonly RankingScreenshot _shot;

    public DiscordInteractionsController(
        WeepaAdminClient admin,
        IConfiguration config,
        ILogger<DiscordInteractionsController> logger,
        Npgsql.NpgsqlDataSource dataSource,
        DiscordCommandClient cmd,
        RankingScreenshot shot)
    {
        _admin = admin;
        _config = config;
        _logger = logger;
        _dataSource = dataSource;
        _cmd = cmd;
        _shot = shot;
    }

    [HttpPost("interactions")]
    public async Task<IActionResult> Interactions(CancellationToken ct)
    {
        var publicKeyHex = _config["DISCORD_PUBLIC_KEY"];
        if (string.IsNullOrWhiteSpace(publicKeyHex))
        {
            _logger.LogWarning("DISCORD_PUBLIC_KEY 未設定。Interactions を 503 で拒否");
            return StatusCode(StatusCodes.Status503ServiceUnavailable);
        }

        // Discord は raw body 全体に対して Ed25519 署名するので、生バイトで読む
        byte[] rawBytes;
        using (var ms = new MemoryStream())
        {
            await Request.Body.CopyToAsync(ms, ct);
            rawBytes = ms.ToArray();
        }
        var rawBody = Encoding.UTF8.GetString(rawBytes);

        var sig = Request.Headers["X-Signature-Ed25519"].ToString();
        var ts = Request.Headers["X-Signature-Timestamp"].ToString();
        if (string.IsNullOrEmpty(sig) || string.IsNullOrEmpty(ts))
        {
            return Unauthorized();
        }

        byte[] publicKey, signature;
        try
        {
            publicKey = Convert.FromHexString(publicKeyHex);
            signature = Convert.FromHexString(sig);
        }
        catch (FormatException)
        {
            return Unauthorized();
        }

        var tsBytes = Encoding.UTF8.GetBytes(ts);
        var msg = new byte[tsBytes.Length + rawBytes.Length];
        Buffer.BlockCopy(tsBytes, 0, msg, 0, tsBytes.Length);
        Buffer.BlockCopy(rawBytes, 0, msg, tsBytes.Length, rawBytes.Length);
        bool ok;
        try { ok = Ed25519Verifier.Verify(publicKey, msg, signature); }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Ed25519 verify threw");
            return Unauthorized();
        }
        if (!ok) return Unauthorized();

        // Parse + dispatch
        using var doc = JsonDocument.Parse(rawBody);
        var root = doc.RootElement;
        var type = root.GetProperty("type").GetInt32();

        if (type == TYPE_PING)
        {
            return Json(new { type = RESP_PONG });
        }

        if (type == TYPE_APPLICATION_COMMAND)
        {
            return HandleApplicationCommand(root);
        }

        if (type == TYPE_MESSAGE_COMPONENT)
        {
            return await HandleComponentAsync(root, ct);
        }

        if (type == TYPE_MODAL_SUBMIT)
        {
            return await HandleModalAsync(root, ct);
        }

        return Json(new
        {
            type = RESP_CHANNEL_MESSAGE,
            data = new { content = "未対応のインタラクションです", flags = FLAG_EPHEMERAL },
        });
    }

    private IActionResult HandleApplicationCommand(JsonElement root)
    {
        var data = root.GetProperty("data");
        var name = data.GetProperty("name").GetString() ?? "";
        var token = root.GetProperty("token").GetString() ?? "";

        var idArg = TryGetIntOption(data, "id");

        if (name == "weepa-result")
        {
            // 3 秒以内に応答が必要なので一旦 deferred を返してから裏で生成
            _ = Task.Run(() => HandleWeepaResultAsync(token, idArg));
            return Json(new { type = RESP_DEFERRED_CHANNEL_MESSAGE });
        }

        if (name == "weepa-publish")
        {
            _ = Task.Run(() => HandleWeepaPublishAsync(token, idArg));
            return Json(new { type = RESP_DEFERRED_CHANNEL_MESSAGE });
        }

        if (name == "weepa-unpublish")
        {
            if (!idArg.HasValue)
            {
                return Json(new
                {
                    type = RESP_CHANNEL_MESSAGE,
                    data = new { content = "❌ id 引数が必要です", flags = FLAG_EPHEMERAL },
                });
            }
            _ = Task.Run(() => HandleWeepaUnpublishAsync(token, idArg.Value));
            return Json(new { type = RESP_DEFERRED_CHANNEL_MESSAGE });
        }

        if (name == "weepa-songdetail")
        {
            if (!idArg.HasValue)
            {
                return Json(new
                {
                    type = RESP_CHANNEL_MESSAGE,
                    data = new { content = "❌ id 引数が必要です", flags = FLAG_EPHEMERAL },
                });
            }
            _ = Task.Run(() => HandleSongDetailAsync(token, idArg.Value));
            return Json(new { type = RESP_DEFERRED_CHANNEL_MESSAGE });
        }

        if (name == "weepa-eventdetail")
        {
            if (!idArg.HasValue)
            {
                return Json(new
                {
                    type = RESP_CHANNEL_MESSAGE,
                    data = new { content = "❌ id 引数が必要です", flags = FLAG_EPHEMERAL },
                });
            }
            _ = Task.Run(() => HandleEventDetailAsync(token, idArg.Value));
            return Json(new { type = RESP_DEFERRED_CHANNEL_MESSAGE });
        }

        if (name == "weepa-events-shuffle")
        {
            _ = Task.Run(() => HandleEventsShuffleAsync(token));
            return Json(new { type = RESP_DEFERRED_CHANNEL_MESSAGE });
        }

        if (name == "weepa-events-schedules")
        {
            _ = Task.Run(() => HandleEventsSchedulesAsync(token));
            return Json(new { type = RESP_DEFERRED_CHANNEL_MESSAGE });
        }

        if (name == "weepa-x-post")
        {
            var level = TryGetStringOption(data, "level");
            if (string.IsNullOrWhiteSpace(level))
            {
                return Json(new
                {
                    type = RESP_CHANNEL_MESSAGE,
                    data = new { content = "❌ level 引数が必要です", flags = FLAG_EPHEMERAL },
                });
            }
            _ = Task.Run(() => HandleWeepaXPostAsync(token, idArg, level));
            return Json(new
            {
                type = RESP_DEFERRED_CHANNEL_MESSAGE,
                data = new { flags = FLAG_EPHEMERAL },
            });
        }

        if (name == "msc-result")
        {
            _ = Task.Run(() => HandleMscResultAsync(token, idArg));
            return Json(new { type = RESP_DEFERRED_CHANNEL_MESSAGE });
        }

        if (name == "msc-publish")
        {
            _ = Task.Run(() => HandleMscPublishAsync(token, idArg));
            return Json(new { type = RESP_DEFERRED_CHANNEL_MESSAGE });
        }

        if (name == "msc-unpublish")
        {
            if (!idArg.HasValue)
            {
                return Json(new
                {
                    type = RESP_CHANNEL_MESSAGE,
                    data = new { content = "❌ id 引数が必要です", flags = FLAG_EPHEMERAL },
                });
            }
            _ = Task.Run(() => HandleMscUnpublishAsync(token, idArg.Value));
            return Json(new { type = RESP_DEFERRED_CHANNEL_MESSAGE });
        }

        if (name == "msc-eventdetail")
        {
            if (!idArg.HasValue)
            {
                return Json(new
                {
                    type = RESP_CHANNEL_MESSAGE,
                    data = new { content = "❌ id 引数が必要です", flags = FLAG_EPHEMERAL },
                });
            }
            _ = Task.Run(() => HandleMscEventDetailAsync(token, idArg.Value));
            return Json(new { type = RESP_DEFERRED_CHANNEL_MESSAGE });
        }

        if (name == "msc-events-schedules")
        {
            _ = Task.Run(() => HandleMscEventsSchedulesAsync(token));
            return Json(new { type = RESP_DEFERRED_CHANNEL_MESSAGE });
        }

        return Json(new
        {
            type = RESP_CHANNEL_MESSAGE,
            data = new { content = $"未対応のコマンド: /{name}", flags = FLAG_EPHEMERAL },
        });
    }

    private static int? TryGetIntOption(JsonElement data, string optionName)
    {
        if (!data.TryGetProperty("options", out var opts)) return null;
        foreach (var opt in opts.EnumerateArray())
        {
            if (opt.GetProperty("name").GetString() == optionName &&
                opt.TryGetProperty("value", out var v) &&
                v.TryGetInt32(out var i))
            {
                return i;
            }
        }
        return null;
    }

    private static string? TryGetStringOption(JsonElement data, string optionName)
    {
        if (!data.TryGetProperty("options", out var opts)) return null;
        foreach (var opt in opts.EnumerateArray())
        {
            if (opt.GetProperty("name").GetString() == optionName &&
                opt.TryGetProperty("value", out var v) &&
                v.ValueKind == JsonValueKind.String)
            {
                return v.GetString();
            }
        }
        return null;
    }

    private static readonly string[] JapaneseDayNames = { "日", "月", "火", "水", "木", "金", "土" };

    private async Task HandleWeepaXPostAsync(string token, int? eventId, string level)
    {
        try
        {
            await using var conn = await _dataSource.OpenConnectionAsync();
            var row = await conn.QuerySingleOrDefaultAsync<dynamic>(
                eventId.HasValue
                    ? """
                      SELECT e.weepa_event_id, e.end_date, s.song, s.effect,
                             s.submit_name, s.submit_reason
                      FROM weepa_events e
                      JOIN weepa_songs s ON s.weepa_song_id = e.weepa_song_id
                      WHERE e.weepa_event_id = @Id
                      """
                    : """
                      SELECT e.weepa_event_id, e.end_date, s.song, s.effect,
                             s.submit_name, s.submit_reason
                      FROM weepa_events e
                      JOIN weepa_songs s ON s.weepa_song_id = e.weepa_song_id
                      WHERE e.start_date > NOW()
                      ORDER BY e.start_date ASC
                      LIMIT 1
                      """,
                new { Id = eventId ?? 0 });
            if (row is null)
            {
                await _cmd.FollowUpTextAsync(
                    token,
                    eventId.HasValue
                        ? $"❌ event_id {eventId} は存在しません"
                        : "❌ 次回開催のイベントがありません",
                    ephemeral: true);
                return;
            }

            var endJst = ((DateTime)row.end_date).ToUniversalTime().AddHours(9);
            var dayName = JapaneseDayNames[(int)endJst.DayOfWeek];
            var endStr = $"{endJst.Year}/{endJst.Month}/{endJst.Day}({dayName}) {endJst.Hour:D2}:{endJst.Minute:D2}";
            var reason = string.IsNullOrWhiteSpace((string?)row.submit_reason)
                ? "(未設定)"
                : (string)row.submit_reason;

            var template = $$"""
                【#WeePA】

                今週の課題曲はこちら！

                ・{{row.song}}
                　┗{{row.effect}}･･･LV{{level}}

                Selected by {{row.submit_name}}
                選曲理由：{{reason}}

                ↓提出はこちら↓
                https://team-nvn-server.com/weepa/score/

                提出期限：{{endStr}}まで
                """;

            await _cmd.FollowUpTextAsync(token, template, ephemeral: true);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "weepa-x-post handler failed");
            await _cmd.FollowUpTextAsync(token, $"❌ エラー: {ex.Message}", ephemeral: true);
        }
    }

    private async Task HandleWeepaResultAsync(string token, int? eventId)
    {
        try
        {
            await using var conn = await _dataSource.OpenConnectionAsync();

            // 対象イベントの解決
            int targetId;
            string label;
            if (eventId.HasValue)
            {
                var exists = await conn.ExecuteScalarAsync<int?>(
                    "SELECT weepa_event_id FROM weepa_events WHERE weepa_event_id = @Id",
                    new { Id = eventId.Value });
                if (exists is null)
                {
                    await _cmd.FollowUpTextAsync(
                        token, $"❌ event_id {eventId.Value} は存在しません", ephemeral: true);
                    return;
                }
                targetId = eventId.Value;
                label = $"event #{targetId}";
            }
            else
            {
                // 直近開催 = start_date が NOW 以前で最新
                var latest = await conn.QuerySingleOrDefaultAsync<int?>(
                    """
                    SELECT weepa_event_id FROM weepa_events
                    WHERE start_date <= NOW()
                    ORDER BY start_date DESC
                    LIMIT 1
                    """);
                if (latest is null)
                {
                    await _cmd.FollowUpTextAsync(
                        token, "❌ まだ開催されたイベントがありません", ephemeral: true);
                    return;
                }
                targetId = latest.Value;
                label = $"直近開催 (event #{targetId})";
            }

            var png = await _shot.CaptureRankingAsync(targetId);
            await _cmd.FollowUpWithImageAsync(
                token,
                $"🏆 集計結果 ({label})",
                png,
                $"weepa-result-{targetId}.png");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "weepa-result handler failed");
            await _cmd.FollowUpTextAsync(
                token, $"❌ 画像生成中にエラーが発生しました: {ex.Message}",
                ephemeral: true);
        }
    }

    private async Task HandleWeepaPublishAsync(string token, int? eventId)
    {
        try
        {
            await using var conn = await _dataSource.OpenConnectionAsync();

            int targetId;
            if (eventId.HasValue)
            {
                var exists = await conn.ExecuteScalarAsync<int?>(
                    "SELECT weepa_event_id FROM weepa_events WHERE weepa_event_id = @Id",
                    new { Id = eventId.Value });
                if (exists is null)
                {
                    await _cmd.FollowUpTextAsync(
                        token, $"❌ event_id {eventId.Value} は存在しません", ephemeral: true);
                    return;
                }
                targetId = eventId.Value;
            }
            else
            {
                // 直近の未公開 (= 終了済 AND tallied_at IS NULL) を選出
                var latest = await conn.QuerySingleOrDefaultAsync<int?>(
                    """
                    SELECT weepa_event_id FROM weepa_events
                    WHERE end_date < NOW() AND tallied_at IS NULL
                    ORDER BY end_date DESC
                    LIMIT 1
                    """);
                if (latest is null)
                {
                    await _cmd.FollowUpTextAsync(
                        token, "❌ 公開対象のイベント (終了済かつ未公開) がありません",
                        ephemeral: true);
                    return;
                }
                targetId = latest.Value;
            }

            using var resp = await _admin.PublishEventAsync(targetId, default);
            if (resp.IsSuccessStatusCode)
            {
                await _cmd.FollowUpTextAsync(
                    token, $"✅ event #{targetId} を公開しました");
            }
            else
            {
                var body = await resp.Content.ReadAsStringAsync();
                await _cmd.FollowUpTextAsync(
                    token, $"❌ 公開失敗 ({(int)resp.StatusCode}): {body}",
                    ephemeral: true);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "weepa-publish handler failed");
            await _cmd.FollowUpTextAsync(
                token, $"❌ エラー: {ex.Message}", ephemeral: true);
        }
    }

    private async Task HandleWeepaUnpublishAsync(string token, int eventId)
    {
        try
        {
            await using var conn = await _dataSource.OpenConnectionAsync();
            var exists = await conn.ExecuteScalarAsync<int?>(
                "SELECT weepa_event_id FROM weepa_events WHERE weepa_event_id = @Id",
                new { Id = eventId });
            if (exists is null)
            {
                await _cmd.FollowUpTextAsync(
                    token, $"❌ event_id {eventId} は存在しません", ephemeral: true);
                return;
            }

            using var resp = await _admin.UnpublishEventAsync(eventId, default);
            if (resp.IsSuccessStatusCode)
            {
                await _cmd.FollowUpTextAsync(
                    token, $"✅ event #{eventId} を非公開に戻しました");
            }
            else
            {
                var body = await resp.Content.ReadAsStringAsync();
                await _cmd.FollowUpTextAsync(
                    token, $"❌ 非公開化失敗 ({(int)resp.StatusCode}): {body}",
                    ephemeral: true);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "weepa-unpublish handler failed");
            await _cmd.FollowUpTextAsync(
                token, $"❌ エラー: {ex.Message}", ephemeral: true);
        }
    }

    private async Task HandleSongDetailAsync(string token, int songId)
    {
        try
        {
            await using var conn = await _dataSource.OpenConnectionAsync();
            var row = await conn.QuerySingleOrDefaultAsync<dynamic>(
                """
                SELECT weepa_song_id, song, effect, x_id, submit_name, submit_reason,
                       submit_date, approval_status,
                       (SELECT COUNT(*) FROM weepa_events e
                        WHERE e.weepa_song_id = s.weepa_song_id) AS event_count
                FROM weepa_songs s
                WHERE weepa_song_id = @Id
                """,
                new { Id = songId });
            if (row is null)
            {
                await _cmd.FollowUpTextAsync(
                    token, $"❌ weepa_song_id {songId} は存在しません",
                    ephemeral: true);
                return;
            }
            var status = ((short)row.approval_status) switch
            {
                -1 => "却下",
                0 => "未承認",
                1 => "承認済",
                _ => "?"
            };
            var reason = string.IsNullOrWhiteSpace((string?)row.submit_reason)
                ? "(なし)" : (string)row.submit_reason;
            var submitDate = ((DateTime)row.submit_date).ToUniversalTime()
                .AddHours(9).ToString("yyyy-MM-dd HH:mm");
            var eventCount = (long)row.event_count;
            var msg = $"""
                🎵 **楽曲 #{songId}** {row.song}
                譜面: `{row.effect}`
                提案者: {row.submit_name} (@{row.x_id})
                理由: {reason}
                投稿: {submitDate} JST
                ステータス: {status} ({row.approval_status}) / 紐付き event: {eventCount} 件
                """;
            await _cmd.FollowUpTextAsync(token, msg);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "weepa-songdetail handler failed");
            await _cmd.FollowUpTextAsync(
                token, $"❌ エラー: {ex.Message}", ephemeral: true);
        }
    }

    private async Task HandleEventDetailAsync(string token, int eventId)
    {
        try
        {
            await using var conn = await _dataSource.OpenConnectionAsync();
            var row = await conn.QuerySingleOrDefaultAsync<dynamic>(
                """
                SELECT e.weepa_event_id, e.weepa_song_id, e.start_date, e.end_date,
                       e.tallied_at, s.song, s.effect, s.submit_name,
                       (SELECT COUNT(*) FROM weepa_scores sc
                        WHERE sc.weepa_event_id = e.weepa_event_id) AS scores_total,
                       (SELECT COUNT(*) FROM weepa_scores sc
                        WHERE sc.weepa_event_id = e.weepa_event_id AND sc.approval_status = 1) AS scores_approved,
                       (SELECT COUNT(*) FROM weepa_scores sc
                        WHERE sc.weepa_event_id = e.weepa_event_id AND sc.approval_status = 0) AS scores_pending
                FROM weepa_events e
                JOIN weepa_songs s ON s.weepa_song_id = e.weepa_song_id
                WHERE e.weepa_event_id = @Id
                """,
                new { Id = eventId });
            if (row is null)
            {
                await _cmd.FollowUpTextAsync(
                    token, $"❌ weepa_event_id {eventId} は存在しません",
                    ephemeral: true);
                return;
            }

            var nowUtc = DateTime.UtcNow;
            var startUtc = ((DateTime)row.start_date).ToUniversalTime();
            var endUtc = ((DateTime)row.end_date).ToUniversalTime();
            var talliedAtNullable = (DateTime?)row.tallied_at;
            string state;
            if (nowUtc < startUtc) state = "🕒 開催前";
            else if (nowUtc < endUtc) state = "🟢 開催中";
            else if (talliedAtNullable is null) state = "⏳ 集計中 (未公開)";
            else state = "✅ 公開済";

            var startJst = startUtc.AddHours(9).ToString("yyyy-MM-dd HH:mm");
            var endJst = endUtc.AddHours(9).ToString("yyyy-MM-dd HH:mm");
            var talliedJst = talliedAtNullable.HasValue
                ? talliedAtNullable.Value.ToUniversalTime().AddHours(9).ToString("yyyy-MM-dd HH:mm") + " JST"
                : "(未集計)";

            var msg = $"""
                📅 **イベント #{eventId}**
                楽曲: {row.song} (`{row.effect}`) — song #{row.weepa_song_id}
                提案者: {row.submit_name}
                開催期間: {startJst} ~ {endJst} JST
                状態: {state}
                集計: {talliedJst}
                スコア: {row.scores_approved} 承認済 / {row.scores_pending} 未承認 / 計 {row.scores_total} 件
                """;
            await _cmd.FollowUpTextAsync(token, msg);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "weepa-eventdetail handler failed");
            await _cmd.FollowUpTextAsync(
                token, $"❌ エラー: {ex.Message}", ephemeral: true);
        }
    }

    private async Task HandleEventsShuffleAsync(string token)
    {
        try
        {
            using var resp = await _admin.ShuffleScheduleAsync(default);
            if (resp.IsSuccessStatusCode)
            {
                var body = await resp.Content.ReadAsStringAsync();
                await _cmd.FollowUpTextAsync(token, $"✅ キューをシャッフルしました\n```{body}```");
            }
            else
            {
                var body = await resp.Content.ReadAsStringAsync();
                await _cmd.FollowUpTextAsync(
                    token, $"❌ シャッフル失敗 ({(int)resp.StatusCode}): {body}",
                    ephemeral: true);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "weepa-events-shuffle failed");
            await _cmd.FollowUpTextAsync(token, $"❌ エラー: {ex.Message}", ephemeral: true);
        }
    }

    private async Task HandleEventsSchedulesAsync(string token)
    {
        try
        {
            await using var conn = await _dataSource.OpenConnectionAsync();
            var rows = (await conn.QueryAsync<dynamic>(
                """
                SELECT e.weepa_event_id, e.start_date, e.end_date, e.tallied_at,
                       s.song, s.effect, s.submit_name
                FROM weepa_events e
                JOIN weepa_songs s ON s.weepa_song_id = e.weepa_song_id
                WHERE e.start_date > NOW()
                ORDER BY e.start_date ASC
                LIMIT 20
                """)).ToList();
            if (rows.Count == 0)
            {
                await _cmd.FollowUpTextAsync(
                    token, "📅 開催スケジュールに登録されたイベントはありません");
                return;
            }
            var sb = new StringBuilder();
            sb.AppendLine("📅 **開催スケジュール (上位 20 件)**");
            for (int i = 0; i < rows.Count; i++)
            {
                var r = rows[i];
                var s = ((DateTime)r.start_date).ToUniversalTime().AddHours(9).ToString("MM/dd HH:mm");
                var e2 = ((DateTime)r.end_date).ToUniversalTime().AddHours(9).ToString("MM/dd HH:mm");
                sb.AppendLine($"{i + 1}. `{s}~{e2}` {r.song} (`{r.effect}`) by {r.submit_name}");
            }
            await _cmd.FollowUpTextAsync(token, sb.ToString());
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "weepa-events-schedules failed");
            await _cmd.FollowUpTextAsync(token, $"❌ エラー: {ex.Message}", ephemeral: true);
        }
    }

    // ============================================
    // MSC (MoonShotChallenge) handlers
    // ============================================

    private async Task HandleMscResultAsync(string token, int? eventId)
    {
        try
        {
            await using var conn = await _dataSource.OpenConnectionAsync();
            int targetId;
            string label;
            if (eventId.HasValue)
            {
                var exists = await conn.ExecuteScalarAsync<int?>(
                    "SELECT msc_event_id FROM msc_events WHERE msc_event_id = @Id",
                    new { Id = eventId.Value });
                if (exists is null)
                {
                    await _cmd.FollowUpTextAsync(
                        token, $"❌ msc_event_id {eventId.Value} は存在しません", ephemeral: true);
                    return;
                }
                targetId = eventId.Value;
                label = $"event #{targetId}";
            }
            else
            {
                var latest = await conn.QuerySingleOrDefaultAsync<int?>(
                    """
                    SELECT msc_event_id FROM msc_events
                    WHERE start_date <= NOW()
                    ORDER BY start_date DESC
                    LIMIT 1
                    """);
                if (latest is null)
                {
                    await _cmd.FollowUpTextAsync(
                        token, "❌ まだ開催された MSC イベントがありません", ephemeral: true);
                    return;
                }
                targetId = latest.Value;
                label = $"直近開催 (event #{targetId})";
            }

            var png = await _shot.CaptureMscRankingAsync(targetId);
            await _cmd.FollowUpWithImageAsync(
                token, $"🌙 MSC 集計結果 ({label})", png, $"msc-result-{targetId}.png");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "msc-result handler failed");
            await _cmd.FollowUpTextAsync(
                token, $"❌ 画像生成中にエラーが発生しました: {ex.Message}", ephemeral: true);
        }
    }

    private async Task HandleMscPublishAsync(string token, int? eventId)
    {
        try
        {
            await using var conn = await _dataSource.OpenConnectionAsync();
            int targetId;
            if (eventId.HasValue)
            {
                var exists = await conn.ExecuteScalarAsync<int?>(
                    "SELECT msc_event_id FROM msc_events WHERE msc_event_id = @Id",
                    new { Id = eventId.Value });
                if (exists is null)
                {
                    await _cmd.FollowUpTextAsync(
                        token, $"❌ msc_event_id {eventId.Value} は存在しません", ephemeral: true);
                    return;
                }
                targetId = eventId.Value;
            }
            else
            {
                var latest = await conn.QuerySingleOrDefaultAsync<int?>(
                    """
                    SELECT msc_event_id FROM msc_events
                    WHERE end_date < NOW() AND tallied_at IS NULL
                    ORDER BY end_date DESC
                    LIMIT 1
                    """);
                if (latest is null)
                {
                    await _cmd.FollowUpTextAsync(
                        token, "❌ 公開対象の MSC イベント (終了済かつ未公開) がありません", ephemeral: true);
                    return;
                }
                targetId = latest.Value;
            }

            using var resp = await _admin.PublishMscEventAsync(targetId, default);
            if (resp.IsSuccessStatusCode)
            {
                await _cmd.FollowUpTextAsync(token, $"✅ MSC event #{targetId} を公開しました");
            }
            else
            {
                var body = await resp.Content.ReadAsStringAsync();
                await _cmd.FollowUpTextAsync(
                    token, $"❌ 公開失敗 ({(int)resp.StatusCode}): {body}", ephemeral: true);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "msc-publish handler failed");
            await _cmd.FollowUpTextAsync(token, $"❌ エラー: {ex.Message}", ephemeral: true);
        }
    }

    private async Task HandleMscUnpublishAsync(string token, int eventId)
    {
        try
        {
            await using var conn = await _dataSource.OpenConnectionAsync();
            var exists = await conn.ExecuteScalarAsync<int?>(
                "SELECT msc_event_id FROM msc_events WHERE msc_event_id = @Id",
                new { Id = eventId });
            if (exists is null)
            {
                await _cmd.FollowUpTextAsync(
                    token, $"❌ msc_event_id {eventId} は存在しません", ephemeral: true);
                return;
            }

            using var resp = await _admin.UnpublishMscEventAsync(eventId, default);
            if (resp.IsSuccessStatusCode)
            {
                await _cmd.FollowUpTextAsync(token, $"✅ MSC event #{eventId} を非公開に戻しました");
            }
            else
            {
                var body = await resp.Content.ReadAsStringAsync();
                await _cmd.FollowUpTextAsync(
                    token, $"❌ 非公開化失敗 ({(int)resp.StatusCode}): {body}", ephemeral: true);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "msc-unpublish handler failed");
            await _cmd.FollowUpTextAsync(token, $"❌ エラー: {ex.Message}", ephemeral: true);
        }
    }

    private async Task HandleMscEventDetailAsync(string token, int eventId)
    {
        try
        {
            await using var conn = await _dataSource.OpenConnectionAsync();
            var ev = await conn.QuerySingleOrDefaultAsync<dynamic>(
                """
                SELECT e.msc_event_id, e.title, e.description, e.start_date, e.end_date,
                       e.tallied_at,
                       (SELECT COUNT(*) FROM msc_submissions s
                        WHERE s.msc_event_id = e.msc_event_id) AS subs_total,
                       (SELECT COUNT(*) FROM msc_submissions s
                        WHERE s.msc_event_id = e.msc_event_id AND s.approval_status = 1) AS subs_approved,
                       (SELECT COUNT(*) FROM msc_submissions s
                        WHERE s.msc_event_id = e.msc_event_id AND s.approval_status = 0) AS subs_pending
                FROM msc_events e
                WHERE e.msc_event_id = @Id
                """,
                new { Id = eventId });
            if (ev is null)
            {
                await _cmd.FollowUpTextAsync(
                    token, $"❌ msc_event_id {eventId} は存在しません", ephemeral: true);
                return;
            }
            var songs = (await conn.QueryAsync<dynamic>(
                """
                SELECT song_order, song, effect
                FROM msc_songs WHERE msc_event_id = @Id ORDER BY song_order
                """,
                new { Id = eventId })).ToList();

            var nowUtc = DateTime.UtcNow;
            var startUtc = ((DateTime)ev.start_date).ToUniversalTime();
            var endUtc = ((DateTime)ev.end_date).ToUniversalTime();
            var talliedAtNullable = (DateTime?)ev.tallied_at;
            string state;
            if (nowUtc < startUtc) state = "🕒 開催前";
            else if (nowUtc < endUtc) state = "🟢 開催中";
            else if (talliedAtNullable is null) state = "⏳ 集計中 (未公開)";
            else state = "✅ 公開済";

            var startJst = startUtc.AddHours(9).ToString("yyyy-MM-dd HH:mm");
            var endJst = endUtc.AddHours(9).ToString("yyyy-MM-dd HH:mm");
            var talliedJst = talliedAtNullable.HasValue
                ? talliedAtNullable.Value.ToUniversalTime().AddHours(9).ToString("yyyy-MM-dd HH:mm") + " JST"
                : "(未公開)";

            var songsLine = string.Join("\n",
                songs.Select(s => $"  {s.song_order}. {s.song} (`{s.effect}`)"));
            if (string.IsNullOrEmpty(songsLine)) songsLine = "  (曲未設定)";

            var description = string.IsNullOrWhiteSpace((string?)ev.description)
                ? "(なし)" : (string)ev.description;

            var msg = $"""
                🌙 **MSC イベント #{eventId}** {ev.title}
                開催期間: {startJst} ~ {endJst} JST
                状態: {state}
                集計: {talliedJst}
                説明: {description}
                課題曲:
                {songsLine}
                投稿: {ev.subs_approved} 承認済 / {ev.subs_pending} 未承認 / 計 {ev.subs_total} 件
                """;
            await _cmd.FollowUpTextAsync(token, msg);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "msc-eventdetail handler failed");
            await _cmd.FollowUpTextAsync(token, $"❌ エラー: {ex.Message}", ephemeral: true);
        }
    }

    private async Task HandleMscEventsSchedulesAsync(string token)
    {
        try
        {
            await using var conn = await _dataSource.OpenConnectionAsync();
            var rows = (await conn.QueryAsync<dynamic>(
                """
                SELECT msc_event_id, title, start_date, end_date, tallied_at
                FROM msc_events
                WHERE start_date > NOW()
                ORDER BY start_date ASC
                LIMIT 20
                """)).ToList();
            if (rows.Count == 0)
            {
                await _cmd.FollowUpTextAsync(token, "🌙 開催予定の MSC イベントはありません");
                return;
            }
            var sb = new StringBuilder();
            sb.AppendLine("🌙 **MSC 開催予定 (上位 20 件)**");
            for (int i = 0; i < rows.Count; i++)
            {
                var r = rows[i];
                var s = ((DateTime)r.start_date).ToUniversalTime().AddHours(9).ToString("MM/dd HH:mm");
                var e2 = ((DateTime)r.end_date).ToUniversalTime().AddHours(9).ToString("MM/dd HH:mm");
                sb.AppendLine($"{i + 1}. `{s}~{e2}` #{r.msc_event_id} {r.title}");
            }
            await _cmd.FollowUpTextAsync(token, sb.ToString());
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "msc-events-schedules failed");
            await _cmd.FollowUpTextAsync(token, $"❌ エラー: {ex.Message}", ephemeral: true);
        }
    }

    private async Task<IActionResult> HandleComponentAsync(
        JsonElement root, CancellationToken ct)
    {
        var customId = root.GetProperty("data").GetProperty("custom_id").GetString() ?? "";
        var parts = customId.Split(':');
        if (parts.Length < 3) return InvalidComponent("custom_id 形式不正");

        var domain = parts[0];
        var op = parts[1];

        if (domain == "song" && parts.Length >= 3 && int.TryParse(parts[2], out var songId))
        {
            return await HandleSongComponentAsync(root, op, songId, ct);
        }

        if (domain == "score" && parts.Length >= 5 &&
            int.TryParse(parts[2], out var eventId) &&
            int.TryParse(parts[4], out var submitId))
        {
            var xId = parts[3];
            return await HandleScoreComponentAsync(root, op, eventId, xId, submitId, ct);
        }

        if (domain == "mscscore" && parts.Length >= 5 &&
            int.TryParse(parts[2], out var mscEventId) &&
            int.TryParse(parts[4], out var mscSubmitId))
        {
            var xId = parts[3];
            return await HandleMscScoreComponentAsync(root, op, mscEventId, xId, mscSubmitId, ct);
        }

        return InvalidComponent("未知の custom_id");
    }

    private async Task<IActionResult> HandleSongComponentAsync(
        JsonElement root, string op, int songId, CancellationToken ct)
    {
        var actor = ExtractActorName(root);
        HttpResponseMessage resp;
        string label;
        if (op == "approve")
        {
            resp = await _admin.ApproveSongAsync(songId, ct);
            label = "✅ 承認";
        }
        else if (op == "reject")
        {
            resp = await _admin.RejectSongAsync(songId, ct);
            label = "❌ 却下";
        }
        else
        {
            return InvalidComponent("未知の song op");
        }

        return await BuildUpdateMessageResponseAsync(root, resp, label, actor, ct);
    }

    private async Task<IActionResult> HandleScoreComponentAsync(
        JsonElement root, string op,
        int eventId, string xId, int submitId,
        CancellationToken ct)
    {
        if (op == "approve")
        {
            var resp = await _admin.ApproveScoreAsync(eventId, xId, submitId, null, null, ct);
            return await BuildUpdateMessageResponseAsync(
                root, resp, "✅ 承認", ExtractActorName(root), ct);
        }
        if (op == "reject")
        {
            var resp = await _admin.RejectScoreAsync(eventId, xId, submitId, ct);
            return await BuildUpdateMessageResponseAsync(
                root, resp, "❌ 却下", ExtractActorName(root), ct);
        }
        if (op == "edit")
        {
            // 修正承認 → モーダルを返してスコア入力させる
            return Json(new
            {
                type = RESP_MODAL,
                data = new
                {
                    custom_id = $"score:editsubmit:{eventId}:{xId}:{submitId}",
                    title = "スコア修正承認",
                    components = new object[]
                    {
                        new
                        {
                            type = COMP_ACTION_ROW,
                            components = new object[]
                            {
                                new
                                {
                                    type = COMP_TEXT_INPUT,
                                    custom_id = "score",
                                    label = "Score (整数)",
                                    style = TEXT_INPUT_SHORT,
                                    min_length = 1,
                                    max_length = 10,
                                    required = true,
                                },
                            },
                        },
                        new
                        {
                            type = COMP_ACTION_ROW,
                            components = new object[]
                            {
                                new
                                {
                                    type = COMP_TEXT_INPUT,
                                    custom_id = "exScore",
                                    label = "EX Score (任意)",
                                    style = TEXT_INPUT_SHORT,
                                    max_length = 10,
                                    required = false,
                                },
                            },
                        },
                    },
                },
            });
        }
        return InvalidComponent("未知の score op");
    }

    private async Task<IActionResult> HandleMscScoreComponentAsync(
        JsonElement root, string op,
        int eventId, string xId, int submitId,
        CancellationToken ct)
    {
        if (op == "approve")
        {
            var resp = await _admin.ApproveMscSubmissionAsync(eventId, xId, submitId, null, ct);
            return await BuildUpdateMessageResponseAsync(
                root, resp, "✅ 承認", ExtractActorName(root), ct);
        }
        if (op == "reject")
        {
            var resp = await _admin.RejectMscSubmissionAsync(eventId, xId, submitId, ct);
            return await BuildUpdateMessageResponseAsync(
                root, resp, "❌ 却下", ExtractActorName(root), ct);
        }
        if (op == "edit")
        {
            // 提出中の plays を取得して、その分だけテキスト入力をモーダルに並べる
            var plays = await LoadMscSubmissionPlaysAsync(eventId, xId, submitId, ct);
            if (plays.Count == 0)
            {
                return EphemeralMessage("対象の提出が見つかりません");
            }
            var components = new List<object>();
            foreach (var p in plays.OrderBy(p => p.PlayOrder))
            {
                var label = p.PlayOrder == 4
                    ? $"EX Track ({p.Song})"
                    : $"{p.PlayOrder}{(p.PlayOrder == 1 ? "st" : p.PlayOrder == 2 ? "nd" : "rd")} Track ({p.Song})";
                // Discord の text input label は 45 文字制限
                if (label.Length > 45) label = label.Substring(0, 44) + "…";
                components.Add(new
                {
                    type = COMP_ACTION_ROW,
                    components = new object[]
                    {
                        new
                        {
                            type = COMP_TEXT_INPUT,
                            custom_id = $"play{p.PlayOrder}",
                            label,
                            style = TEXT_INPUT_SHORT,
                            min_length = 1,
                            max_length = 8,
                            required = true,
                            value = p.Score.ToString(),
                        },
                    },
                });
            }
            return Json(new
            {
                type = RESP_MODAL,
                data = new
                {
                    custom_id = $"mscscore:editsubmit:{eventId}:{xId}:{submitId}",
                    title = "MSC スコア修正承認",
                    components,
                },
            });
        }
        return InvalidComponent("未知の mscscore op");
    }

    private async Task<List<(int PlayOrder, int Score, string Song)>> LoadMscSubmissionPlaysAsync(
        int eventId, string xId, int submitId, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var rows = (await conn.QueryAsync<(int PlayOrder, int Score, string Song)>(
            new CommandDefinition(
                """
                SELECT sc.play_order AS "PlayOrder",
                       sc.score      AS "Score",
                       so.song       AS "Song"
                FROM msc_scores sc
                JOIN msc_songs so
                  ON so.msc_event_id = sc.msc_event_id
                 AND so.song_order   = sc.song_order
                WHERE sc.msc_event_id = @EventId
                  AND sc.x_id         = @XId
                  AND sc.submit_id    = @SubmitId
                ORDER BY sc.play_order
                """,
                new { EventId = eventId, XId = xId, SubmitId = submitId },
                cancellationToken: ct))).ToList();
        return rows;
    }

    private async Task<IActionResult> HandleModalAsync(
        JsonElement root, CancellationToken ct)
    {
        var customId = root.GetProperty("data").GetProperty("custom_id").GetString() ?? "";
        var parts = customId.Split(':');

        if (parts.Length == 5 && parts[0] == "mscscore" && parts[1] == "editsubmit")
        {
            return await HandleMscScoreEditModalAsync(root, parts, ct);
        }

        if (parts.Length != 5 || parts[0] != "score" || parts[1] != "editsubmit")
        {
            return EphemeralMessage("未対応のモーダル");
        }
        if (!int.TryParse(parts[2], out var eventId) ||
            !int.TryParse(parts[4], out var submitId))
        {
            return EphemeralMessage("モーダルパラメータ不正");
        }
        var xId = parts[3];

        var inputs = ParseModalInputs(root);
        if (!inputs.TryGetValue("score", out var scoreStr) ||
            !int.TryParse(scoreStr, out var score))
        {
            return EphemeralMessage("Score を整数で入力してください");
        }
        int? exScore = null;
        if (inputs.TryGetValue("exScore", out var exStr) &&
            !string.IsNullOrWhiteSpace(exStr))
        {
            if (!int.TryParse(exStr, out var ex))
            {
                return EphemeralMessage("EX Score を整数で入力してください");
            }
            exScore = ex;
        }

        var resp = await _admin.ApproveScoreAsync(eventId, xId, submitId, score, exScore, ct);
        var actor = ExtractActorName(root);
        return await BuildUpdateMessageResponseAsync(
            root, resp, $"✅ 承認 (修正: {score}{(exScore.HasValue ? $" / EX {exScore}" : "")})", actor, ct);
    }

    private async Task<IActionResult> HandleMscScoreEditModalAsync(
        JsonElement root, string[] parts, CancellationToken ct)
    {
        if (!int.TryParse(parts[2], out var eventId) ||
            !int.TryParse(parts[4], out var submitId))
        {
            return EphemeralMessage("モーダルパラメータ不正");
        }
        var xId = parts[3];

        var inputs = ParseModalInputs(root);
        var corrections = new List<(int PlayOrder, int Score)>();
        for (int i = 1; i <= 4; i++)
        {
            if (!inputs.TryGetValue($"play{i}", out var s) || string.IsNullOrWhiteSpace(s)) continue;
            if (!int.TryParse(s, out var v))
            {
                return EphemeralMessage($"Track {i} のスコアを整数で入力してください");
            }
            if (v < 0 || v > 10_000_000)
            {
                return EphemeralMessage($"Track {i} のスコアは 0〜10,000,000 の範囲です");
            }
            corrections.Add((i, v));
        }
        if (corrections.Count == 0)
        {
            return EphemeralMessage("スコアが 1 件も入力されていません");
        }

        var resp = await _admin.ApproveMscSubmissionAsync(eventId, xId, submitId, corrections, ct);
        var actor = ExtractActorName(root);
        var summary = string.Join(", ", corrections.Select(c => $"T{c.PlayOrder}={c.Score:N0}"));
        return await BuildUpdateMessageResponseAsync(
            root, resp, $"✅ 承認 (修正: {summary})", actor, ct);
    }

    private static Dictionary<string, string> ParseModalInputs(JsonElement root)
    {
        var result = new Dictionary<string, string>();
        if (!root.TryGetProperty("data", out var data)) return result;
        if (!data.TryGetProperty("components", out var rows)) return result;
        foreach (var row in rows.EnumerateArray())
        {
            if (!row.TryGetProperty("components", out var inner)) continue;
            foreach (var input in inner.EnumerateArray())
            {
                var id = input.GetProperty("custom_id").GetString() ?? "";
                var val = input.TryGetProperty("value", out var v) ? (v.GetString() ?? "") : "";
                if (!string.IsNullOrEmpty(id)) result[id] = val;
            }
        }
        return result;
    }

    private async Task<IActionResult> BuildUpdateMessageResponseAsync(
        JsonElement root,
        HttpResponseMessage adminResp,
        string label,
        string actor,
        CancellationToken ct)
    {
        if (!adminResp.IsSuccessStatusCode)
        {
            string body;
            try { body = await adminResp.Content.ReadAsStringAsync(ct); }
            catch { body = "(no body)"; }
            _logger.LogWarning(
                "admin endpoint returned {Status}: {Body}",
                (int)adminResp.StatusCode, body);
            return EphemeralMessage(
                $"処理に失敗しました ({(int)adminResp.StatusCode})");
        }

        // 元メッセージの embed を保ったまま、ボタンを消してステータス行を追加
        var embeds = ExtractEmbedsForUpdate(root, label, actor);
        return Json(new
        {
            type = RESP_UPDATE_MESSAGE,
            data = new
            {
                embeds,
                components = Array.Empty<object>(),
            },
        });
    }

    private static List<object> ExtractEmbedsForUpdate(
        JsonElement root, string label, string actor)
    {
        var result = new List<object>();
        if (!root.TryGetProperty("message", out var msg)) return result;
        if (!msg.TryGetProperty("embeds", out var embedsEl)) return result;

        foreach (var e in embedsEl.EnumerateArray())
        {
            var fields = new List<object>();
            if (e.TryGetProperty("fields", out var fEl))
            {
                foreach (var f in fEl.EnumerateArray())
                {
                    fields.Add(new
                    {
                        name   = f.GetProperty("name").GetString() ?? "",
                        value  = f.GetProperty("value").GetString() ?? "",
                        inline = f.TryGetProperty("inline", out var i) && i.GetBoolean(),
                    });
                }
            }
            fields.Add(new { name = "ステータス", value = $"{label} ({actor})", inline = false });

            var rebuilt = new Dictionary<string, object?>
            {
                ["fields"] = fields,
            };
            if (e.TryGetProperty("title", out var t)) rebuilt["title"] = t.GetString();
            if (e.TryGetProperty("color", out var c)) rebuilt["color"] = c.GetInt32();
            if (e.TryGetProperty("image", out var img))
            {
                if (img.TryGetProperty("url", out var u))
                {
                    rebuilt["image"] = new { url = u.GetString() };
                }
            }
            result.Add(rebuilt);
        }
        return result;
    }

    private static string ExtractActorName(JsonElement root)
    {
        // member.user.global_name → member.user.username → "unknown"
        if (root.TryGetProperty("member", out var member) &&
            member.TryGetProperty("user", out var user))
        {
            if (user.TryGetProperty("global_name", out var g) &&
                g.ValueKind == JsonValueKind.String)
            {
                var s = g.GetString();
                if (!string.IsNullOrEmpty(s)) return s;
            }
            if (user.TryGetProperty("username", out var u))
            {
                return u.GetString() ?? "unknown";
            }
        }
        if (root.TryGetProperty("user", out var u2) &&
            u2.TryGetProperty("username", out var name))
        {
            return name.GetString() ?? "unknown";
        }
        return "unknown";
    }

    private IActionResult InvalidComponent(string reason)
    {
        _logger.LogWarning("invalid component interaction: {Reason}", reason);
        return EphemeralMessage($"不正な操作: {reason}");
    }

    private IActionResult EphemeralMessage(string text) =>
        Json(new
        {
            type = RESP_CHANNEL_MESSAGE,
            data = new { content = text, flags = 64 }, // 64 = EPHEMERAL
        });

    private new IActionResult Json(object payload) =>
        new ContentResult
        {
            Content = JsonSerializer.Serialize(payload),
            ContentType = "application/json",
            StatusCode = 200,
        };
}
