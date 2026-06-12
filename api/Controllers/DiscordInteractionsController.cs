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
    private readonly MatchingManagerService _matching;
    private readonly MessageEditDebouncer _editDebouncer;

    public DiscordInteractionsController(
        WeepaAdminClient admin,
        IConfiguration config,
        ILogger<DiscordInteractionsController> logger,
        Npgsql.NpgsqlDataSource dataSource,
        DiscordCommandClient cmd,
        RankingScreenshot shot,
        MatchingManagerService matching,
        MessageEditDebouncer editDebouncer)
    {
        _admin = admin;
        _config = config;
        _logger = logger;
        _dataSource = dataSource;
        _cmd = cmd;
        _shot = shot;
        _matching = matching;
        _editDebouncer = editDebouncer;
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

        if (name == "room-panel")
        {
            var channelId = root.TryGetProperty("channel_id", out var cidEl) &&
                            ulong.TryParse(cidEl.GetString(), out var ch) ? ch : 0UL;
            if (channelId == 0) return EphemeralMessage("チャンネル ID を取得できません");

            var room = _matching.GetRoomByChannelId(channelId);
            if (room is null)
            {
                return EphemeralMessage("❌ ここはマッチング部屋ではありません");
            }

            _ = Task.Run(() => HandleRoomPanelAsync(token, channelId, room.Number));
            return Json(new
            {
                type = RESP_DEFERRED_CHANNEL_MESSAGE,
                data = new { flags = FLAG_EPHEMERAL },
            });
        }

        if (name == "matching-manager")
        {
            // チャンネル ID / ギルド ID は interaction payload から取得
            var channelId = root.TryGetProperty("channel_id", out var cid) &&
                            ulong.TryParse(cid.GetString(), out var ch) ? ch : 0UL;
            var guildId = root.TryGetProperty("guild_id", out var gid) &&
                          ulong.TryParse(gid.GetString(), out var g) ? g : 0UL;
            if (channelId == 0 || guildId == 0)
            {
                return EphemeralMessage("チャンネル / ギルド情報を取得できません");
            }

            // 待機部屋ロール ID が設定されていなければ早期エラー (仕様)
            var roleId = _config["MATCHING_WAITING_ROOM_ROLE_ID"];
            if (string.IsNullOrWhiteSpace(roleId) || !ulong.TryParse(roleId, out _))
            {
                return EphemeralMessage(
                    "MATCHING_WAITING_ROOM_ROLE_ID が .env に設定されていません。設定してから再実行してください。");
            }

            _ = Task.Run(() => HandleMatchingManagerCreateAsync(token, guildId, channelId));
            return Json(new
            {
                type = RESP_DEFERRED_CHANNEL_MESSAGE,
                data = new { flags = FLAG_EPHEMERAL },
            });
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

            // 部門別に発表する。参加者がいる部門だけスクショを投稿
            // (所属部門 = 各プレイヤーの最後の投稿の tier)
            var tierCounts = (await conn.QueryAsync<(int Tier, int Players)>(
                """
                SELECT tier AS "Tier", COUNT(*) AS "Players"
                FROM (
                    SELECT DISTINCT ON (x_id) x_id, tier
                    FROM weepa_scores
                    WHERE weepa_event_id = @Id AND approval_status = 1
                    ORDER BY x_id, submit_date DESC
                ) latest
                GROUP BY tier
                """,
                new { Id = targetId })).ToDictionary(r => r.Tier, r => r.Players);

            if (tierCounts.Count == 0)
            {
                await _cmd.FollowUpTextAsync(
                    token, $"🏆 集計結果 ({label}): まだ投稿がありません", ephemeral: true);
                return;
            }

            foreach (var tier in new[] { 1, 2, 3 })
            {
                if (!tierCounts.ContainsKey(tier)) continue;
                var png = await _shot.CaptureRankingAsync(targetId, tier);
                await _cmd.FollowUpWithImageAsync(
                    token,
                    $"🏆 集計結果 ({label}) — {TierName(tier)}",
                    png,
                    $"weepa-result-{targetId}-tier{tier}.png");
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "weepa-result handler failed");
            await _cmd.FollowUpTextAsync(
                token, $"❌ 画像生成中にエラーが発生しました: {ex.Message}",
                ephemeral: true);
        }
    }

    private static string TierName(int tier) => tier switch
    {
        1 => "ストイック部門",
        2 => "チャレンジ部門",
        3 => "カジュアル部門",
        _ => $"部門{tier}",
    };

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

    // ============================================
    // オンマチ会 (matching-manager) handlers
    // ============================================

    private async Task HandleRoomPanelAsync(string token, ulong channelId, int roomNumber)
    {
        try
        {
            var room = _matching.GetRoom(roomNumber);
            if (room is null)
            {
                await _cmd.FollowUpTextAsync(token, "❌ 部屋情報を取得できませんでした", ephemeral: true);
                return;
            }

            // 1. 既存アンカーを削除
            if (room.AnchorMessageId != 0)
            {
                await _cmd.DeleteChannelMessageAsync(channelId, room.AnchorMessageId);
            }

            // 2. 新しいアンカーを投稿 (= 最新メッセージとして下に出る)
            var payload = _matching.BuildRoomAnchorPayload(room);
            var newMsgId = await _cmd.CreateChannelMessageAsync(channelId, payload);
            if (newMsgId is null)
            {
                await _cmd.FollowUpTextAsync(token, "❌ パネルの再投稿に失敗しました", ephemeral: true);
                return;
            }

            // 3. state を新 ID で更新 (pin はしない: チャットで流れた時の bump 用なので pin 通知は出さない)
            await _matching.UpdateRoomAsync(roomNumber, r => r.AnchorMessageId = newMsgId.Value);

            // 4. ephemeral の "考え中" を消す
            await _cmd.DeleteOriginalInteractionResponseAsync(token);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "room-panel failed");
            await _cmd.FollowUpTextAsync(token, $"❌ エラー: {ex.Message}", ephemeral: true);
        }
    }

    private async Task HandleMatchingManagerCreateAsync(string token, ulong guildId, ulong channelId)
    {
        try
        {
            var payload = _matching.BuildMessagePayload();
            var msgId = await _cmd.CreateChannelMessageAsync(channelId, payload);
            if (msgId is null)
            {
                await _cmd.FollowUpTextAsync(
                    token,
                    "❌ 投稿に失敗しました。Bot に送信権限があるか確認してください。",
                    ephemeral: true);
                return;
            }
            await _matching.SetStateAsync(new MatchingManagerState
            {
                GuildId = guildId,
                ChannelId = channelId,
                MessageId = msgId.Value,
            });
            await _cmd.FollowUpTextAsync(
                token,
                $"✅ マッチング管理メッセージを <#{channelId}> に投稿しました (古い管理メッセージは手動で削除してください)",
                ephemeral: true);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "matching-manager create failed");
            await _cmd.FollowUpTextAsync(token, $"❌ エラー: {ex.Message}", ephemeral: true);
        }
    }

    private IActionResult HandleMatchingComponent(JsonElement root, string op, string[] parts)
    {
        var roleIdStr = _config["MATCHING_WAITING_ROOM_ROLE_ID"];
        if (string.IsNullOrWhiteSpace(roleIdStr) || !ulong.TryParse(roleIdStr, out var roleId))
        {
            return EphemeralMessage("MATCHING_WAITING_ROOM_ROLE_ID が未設定です");
        }

        // 入退室操作以外 (create-room / room-leave / play-end など) は別の関数群へ
        if (op == "create-room") return HandleMatchingCreateRoomOpen(root);
        if (op == "mode-select") return HandleMatchingCreateRoomSubmit(root);
        if (op == "join-room" && parts.Length >= 3 && int.TryParse(parts[2], out var jrn))
            return HandleMatchingJoinRoom(root, jrn);
        // 退出 / プレー開始 / プレー終了 / モード変更 ボタン
        if (op == "room-leave")    return HandleMatchingRoomLeave(root);
        if (op == "play-start")    return HandleMatchingPlayStart(root);
        if (op == "play-end")      return HandleMatchingPlayEnd(root);
        if (op == "change-mode")   return HandleMatchingChangeModeOpen(root);
        if (op == "split-room")    return HandleMatchingSplitRoom(root);
        // 退出確認 ephemeral 内のボタン (プレー中の退出時のみ表示)
        if (op == "confirm-leave") return HandleMatchingConfirmLeave(root);
        if (op == "cancel")        return HandleMatchingCancel(root);

        // 重要: root (JsonElement) は parent の using var doc が解放されると無効になる。
        // Task.Run へ渡す前に必要な値を string / ulong で取り出しておく。
        var token = root.GetProperty("token").GetString() ?? "";
        var guildId = root.TryGetProperty("guild_id", out var gid) &&
                      ulong.TryParse(gid.GetString(), out var g) ? g : 0UL;
        if (guildId == 0) return EphemeralMessage("ギルド ID を取得できません");

        var userId = 0UL;
        if (root.TryGetProperty("member", out var member) &&
            member.TryGetProperty("user", out var user) &&
            user.TryGetProperty("id", out var uid) &&
            ulong.TryParse(uid.GetString(), out var u))
        {
            userId = u;
        }
        if (userId == 0) return EphemeralMessage("ユーザー ID を取得できません");

        if (op == "join-wait" || op == "leave-wait")
        {
            var isJoin = op == "join-wait";

            // 既に部屋に居るユーザーは待機部屋へ入れない (1 人 1 部屋制約の徹底)
            if (isJoin)
            {
                var inRoom = _matching.FindRoomContaining(userId);
                if (inRoom is not null)
                {
                    return EphemeralMessage(
                        $"❌ 既に部屋 (オンマチ{MatchingManagerService.NumberToCircle(inRoom.Number)}) に入室中のため、待機部屋には入れません");
                }
            }

            _ = Task.Run(async () =>
            {
                var roleOk = isJoin
                    ? await _cmd.AddMemberRoleAsync(guildId, userId, roleId)
                    : await _cmd.RemoveMemberRoleAsync(guildId, userId, roleId);
                if (!roleOk)
                {
                    await _cmd.FollowUpTextAsync(
                        token,
                        $"❌ {(isJoin ? "入室" : "退出")}処理に失敗しました (Bot 権限 / ロール階層を確認してください)",
                        ephemeral: true);
                    return;
                }
                if (isJoin) await _matching.AddWaitingUserAsync(userId);
                else        await _matching.RemoveWaitingUserAsync(userId);

                // 管理メッセージ本体を debounce 経由で更新 (連続クリック対策)
                _matching.RefreshDashboard();
            });
            // type=6: deferred update message。クリック loading は即消える + ephemeral message 出ない
            return Json(new { type = RESP_DEFERRED_UPDATE_MESSAGE });
        }

        return InvalidComponent("未知の matching op");
    }

    // create-room button → モーダル (Label でラップした String Select 入り)
    private IActionResult HandleMatchingCreateRoomOpen(JsonElement root)
    {
        var nextNum = _matching.NextRoomNumber();
        if (nextNum == -1) return EphemeralMessage("❌ 現在満室です (9 部屋まで)");
        var userId = ExtractUserId(root);
        if (userId != 0)
        {
            var existing = _matching.FindRoomContaining(userId);
            if (existing is not null)
                return EphemeralMessage(
                    $"❌ 既に他の部屋 (オンマチ{MatchingManagerService.NumberToCircle(existing.Number)}) に入室中です");
        }

        var options = MatchingModes.All
            .Select((m, idx) => new
            {
                label = m.Display,
                value = m.Value,
                @default = idx == 0,
            })
            .ToArray();

        return Json(new
        {
            type = RESP_MODAL,
            data = new
            {
                title = "新規マッチング部屋",
                custom_id = "matching:create-modal",
                components = new object[]
                {
                    new
                    {
                        type = 18, // LABEL (Components V2)
                        label = "モードを選択",
                        component = new
                        {
                            type = 3, // STRING_SELECT
                            custom_id = "mode",
                            min_values = 1,
                            max_values = 1,
                            options,
                        },
                    },
                },
            },
        });
    }

    // 旧: ephemeral string-select 経由でこのコンポーネントが押されるケース (互換用に残す)
    private IActionResult HandleMatchingCreateRoomSubmit(JsonElement root)
    {
        var mode = "";
        if (root.TryGetProperty("data", out var data) &&
            data.TryGetProperty("values", out var values) &&
            values.ValueKind == JsonValueKind.Array &&
            values.GetArrayLength() > 0)
        {
            mode = values[0].GetString() ?? "";
        }
        return ProcessCreateRoomSubmission(root, mode, isModal: false);
    }

    // モーダル submit → Label でラップされた StringSelect の値を取り出す
    private IActionResult HandleMatchingCreateModalSubmit(JsonElement root)
    {
        var mode = ExtractModalSelectValue(root, "mode");
        return ProcessCreateRoomSubmission(root, mode, isModal: true);
    }

    // モード変更モーダル submit
    private IActionResult HandleMatchingChangeModeSubmit(JsonElement root)
    {
        var modeValue = ExtractModalSelectValue(root, "mode");
        var modeDisplay = MatchingModes.ToDisplay(modeValue);
        if (modeDisplay is null) return EphemeralMessage("❌ 不正なモード");

        var channelId = ExtractChannelId(root);
        var room = _matching.GetRoomByChannelId(channelId);
        if (room is null) return EphemeralMessage("❌ この部屋は管理対象外です");
        if (room.Status != RoomStatus.Recruiting)
            return EphemeralMessage("❌ プレー中はモード変更できません");

        var oldMode = room.Mode;
        if (oldMode == modeDisplay)
        {
            // 同じモードなら no-op (ephemeral で通知)
            return Json(new
            {
                type = RESP_CHANNEL_MESSAGE,
                data = new { flags = FLAG_EPHEMERAL, content = $"モードは既に {modeDisplay} です" },
            });
        }

        var token = root.GetProperty("token").GetString() ?? "";
        var roomNumber = room.Number;
        var roomRoleId = room.RoleId;
        _ = Task.Run(async () =>
        {
            try
            {
                await _matching.UpdateRoomAsync(roomNumber, r => r.Mode = modeDisplay);
                RefreshRoomAnchor(roomNumber);
                _matching.RefreshDashboard();
                // 部屋専用チャンネルに対象ロール mention でモード変更通知
                await _cmd.CreateChannelMessageAsync(channelId, new
                {
                    content = $"<@&{roomRoleId}> モードが {oldMode} → {modeDisplay} に変更されました。",
                    allowed_mentions = new { roles = new[] { roomRoleId.ToString() } },
                });
                // 確認 ephemeral を消す
                await _cmd.DeleteOriginalInteractionResponseAsync(token);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "change-mode failed");
                await _cmd.FollowUpTextAsync(token, $"❌ モード変更エラー: {ex.Message}", ephemeral: true);
            }
        });
        return Json(new
        {
            type = RESP_DEFERRED_CHANNEL_MESSAGE,
            data = new { flags = FLAG_EPHEMERAL },
        });
    }


    private static string ExtractModalSelectValue(JsonElement root, string targetCustomId)
    {
        if (!root.TryGetProperty("data", out var data)) return "";
        if (!data.TryGetProperty("components", out var topComps)) return "";
        foreach (var node in topComps.EnumerateArray())
        {
            // 直接 STRING_SELECT (type 3) のケースと、LABEL (type 18) が wrap しているケース両対応
            if (node.TryGetProperty("type", out var t))
            {
                var typeNum = t.GetInt32();
                if (typeNum == 18 && node.TryGetProperty("component", out var inner))
                {
                    if (TryReadSelectValue(inner, targetCustomId, out var v)) return v;
                }
                if (typeNum == 3)
                {
                    if (TryReadSelectValue(node, targetCustomId, out var v)) return v;
                }
                // ACTION_ROW (type 1) の中に StringSelect が居る可能性も一応見る
                if (typeNum == 1 && node.TryGetProperty("components", out var children))
                {
                    foreach (var c in children.EnumerateArray())
                    {
                        if (TryReadSelectValue(c, targetCustomId, out var v)) return v;
                    }
                }
            }
        }
        return "";
    }

    private static bool TryReadSelectValue(JsonElement el, string targetCustomId, out string value)
    {
        value = "";
        if (!el.TryGetProperty("custom_id", out var cid)) return false;
        if (cid.GetString() != targetCustomId) return false;
        if (!el.TryGetProperty("values", out var vals)) return false;
        if (vals.ValueKind != JsonValueKind.Array || vals.GetArrayLength() == 0) return false;
        value = vals[0].GetString() ?? "";
        return true;
    }

    private IActionResult ProcessCreateRoomSubmission(JsonElement root, string mode, bool isModal)
    {
        var modeDisplay = MatchingModes.ToDisplay(mode);
        if (modeDisplay is null) return EphemeralMessage("❌ 不正なモード");

        var guildId = ExtractGuildId(root);
        var userId = ExtractUserId(root);
        var token = root.GetProperty("token").GetString() ?? "";
        if (guildId == 0 || userId == 0) return EphemeralMessage("guild/user 取得不可");

        // 再チェック (race condition 対策)
        var nextNum = _matching.NextRoomNumber();
        if (nextNum == -1) return EphemeralMessage("❌ 現在満室です");
        var existing = _matching.FindRoomContaining(userId);
        if (existing is not null)
            return EphemeralMessage(
                $"❌ 既に他の部屋 (オンマチ{MatchingManagerService.NumberToCircle(existing.Number)}) に入室中です");

        var roleIdStr = _config["MATCHING_WAITING_ROOM_ROLE_ID"];
        if (!ulong.TryParse(roleIdStr, out var waitingRoleId))
            return EphemeralMessage("MATCHING_WAITING_ROOM_ROLE_ID 未設定");
        var categoryIdStr = _config["MATCHING_ROOM_CATEGORY_ID"];
        if (!ulong.TryParse(categoryIdStr, out var categoryId))
            return EphemeralMessage("MATCHING_ROOM_CATEGORY_ID 未設定");

        _ = Task.Run(() => DoCreateRoomAsync(token, guildId, userId, waitingRoleId, categoryId, nextNum, mode, modeDisplay));
        // モーダルの場合は新しい応答メッセージは出ない → deferred channel message + ephemeral で初期化
        // → DoCreateRoomAsync の EditOriginalInteractionResponseAsync が成功メッセージで上書き
        return Json(new
        {
            type = RESP_DEFERRED_CHANNEL_MESSAGE,
            data = new { flags = FLAG_EPHEMERAL },
        });
    }

    private async Task DoCreateRoomAsync(
        string token, ulong guildId, ulong creatorUserId, ulong waitingRoleId,
        ulong categoryId, int roomNumber, string modeValue, string modeDisplay)
    {
        try
        {
            // 1. ロール作成
            var circle = MatchingManagerService.NumberToCircle(roomNumber);
            var roleName = $"オンマチ{circle}";
            var roleId = await _cmd.CreateGuildRoleAsync(guildId, roleName);
            if (roleId is null)
            {
                await _cmd.FollowUpTextAsync(token, "❌ ロール作成に失敗しました", ephemeral: true);
                return;
            }

            // 2. チャンネル作成 (permission overwrites 付き)
            // 最小限の権限 (VIEW_CHANNEL=1024 / SEND_MESSAGES=2048 / READ_MESSAGE_HISTORY=65536)
            // のみ Bot が確実に持っているはずなので、ここに絞る。
            // ※ Discord は「自分が持っていない権限を allow に入れる」と 403 を返すため。
            const long basicAllow = 1024 + 2048 + 65536; // = 68608
            var appId = _config["DISCORD_APP_ID"] ?? "";
            var overwrites = new List<object>
            {
                new { id = guildId.ToString(),      type = 0, deny  = "1024",                 allow = "0" },
                new { id = roleId.Value.ToString(), type = 0, allow = basicAllow.ToString(),  deny  = "0" },
            };
            if (!string.IsNullOrEmpty(appId))
            {
                overwrites.Add(new { id = appId, type = 1, allow = basicAllow.ToString(), deny = "0" });
            }
            var channelPayload = new
            {
                name = $"🎮オンマチ{circle}",
                type = 0,
                parent_id = categoryId.ToString(),
                permission_overwrites = overwrites,
            };
            var channelId = await _cmd.CreateGuildChannelAsync(guildId, channelPayload);
            if (channelId is null)
            {
                await _cmd.DeleteGuildRoleAsync(guildId, roleId.Value);
                await _cmd.FollowUpTextAsync(token, "❌ チャンネル作成に失敗しました", ephemeral: true);
                return;
            }

            // 3. 作成者を入室処理 (待機部屋ロール剥奪 → 部屋ロール付与)
            await _cmd.RemoveMemberRoleAsync(guildId, creatorUserId, waitingRoleId);
            await _cmd.AddMemberRoleAsync(guildId, creatorUserId, roleId.Value);
            await _matching.RemoveWaitingUserAsync(creatorUserId);

            // 4. パスワード採番 + state 登録
            var pw = _matching.PickPassword() ?? "000000";
            var room = new MatchingRoom
            {
                Number = roomNumber,
                Mode = modeDisplay,
                Password = pw,
                ChannelId = channelId.Value,
                RoleId = roleId.Value,
                MemberUserIds = new List<ulong> { creatorUserId },
            };
            await _matching.AddRoomAsync(room);

            // 5. 部屋チャンネルにアンカーメッセージ投稿 + ピン
            var anchorPayload = _matching.BuildRoomAnchorPayload(room);
            var anchorMsgId = await _cmd.CreateChannelMessageAsync(channelId.Value, anchorPayload);
            if (anchorMsgId is not null)
            {
                await _matching.UpdateRoomAsync(roomNumber, r => r.AnchorMessageId = anchorMsgId.Value);
                await _cmd.PinMessageAsync(channelId.Value, anchorMsgId.Value);
            }

            // 6. 管理メッセージを更新 (Phase 1 メッセージ)
            _matching.RefreshDashboard();

            // 7. 待機部屋チャンネルに通知 (ロール mention 付き)
            await PostMatchingNotificationAsync(
                waitingRoleId,
                $"部屋: オンマチ{circle} {modeDisplay} が作成されました。");

            // 8. ephemeral の "考え中" 表示を消す (成功時は通知メッセージで十分なので個別 ephemeral は不要)
            await _cmd.DeleteOriginalInteractionResponseAsync(token);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "create room failed");
            await _cmd.FollowUpTextAsync(token, $"❌ 部屋作成中にエラー: {ex.Message}", ephemeral: true);
        }
    }

    private IActionResult HandleMatchingJoinRoom(JsonElement root, int roomNumber)
    {
        var room = _matching.GetRoom(roomNumber);
        if (room is null) return EphemeralMessage("❌ 対象の部屋が存在しません");

        var userId = ExtractUserId(root);
        var guildId = ExtractGuildId(root);
        var token = root.GetProperty("token").GetString() ?? "";
        if (userId == 0 || guildId == 0) return EphemeralMessage("user/guild 取得不可");

        var existing = _matching.FindRoomContaining(userId);
        if (existing is not null)
            return EphemeralMessage(
                $"❌ 既に他の部屋 (オンマチ{MatchingManagerService.NumberToCircle(existing.Number)}) に入室中です");

        var roleIdStr = _config["MATCHING_WAITING_ROOM_ROLE_ID"];
        ulong.TryParse(roleIdStr, out var waitingRoleId);

        _ = Task.Run(async () =>
        {
            await _cmd.RemoveMemberRoleAsync(guildId, userId, waitingRoleId);
            await _matching.RemoveWaitingUserAsync(userId);
            var ok = await _cmd.AddMemberRoleAsync(guildId, userId, room.RoleId);
            if (!ok)
            {
                await _cmd.FollowUpTextAsync(token, "❌ 入室処理に失敗しました", ephemeral: true);
                return;
            }
            await _matching.UpdateRoomAsync(roomNumber, r =>
            {
                if (!r.MemberUserIds.Contains(userId)) r.MemberUserIds.Add(userId);
            });

            // 部屋アンカー + 管理メッセージ更新 (debounce 経由)
            var refreshed = _matching.GetRoom(roomNumber);
            if (refreshed is not null && refreshed.AnchorMessageId != 0)
            {
                _editDebouncer.Schedule(
                    refreshed.ChannelId, refreshed.AnchorMessageId,
                    () =>
                    {
                        var latest = _matching.GetRoom(roomNumber);
                        return latest is null
                            ? new { } as object
                            : _matching.BuildRoomAnchorPayload(latest);
                    });
            }
            _matching.RefreshDashboard();
        });
        return Json(new { type = RESP_DEFERRED_UPDATE_MESSAGE });
    }

    // 退出ボタン: 募集中は即実行、プレー中は ephemeral で確認
    private IActionResult HandleMatchingRoomLeave(JsonElement root)
    {
        var channelId = ExtractChannelId(root);
        var room = _matching.GetRoomByChannelId(channelId);
        if (room is null) return EphemeralMessage("❌ この部屋は管理対象外です");

        var userId = ExtractUserId(root);
        var guildId = ExtractGuildId(root);
        var token = root.GetProperty("token").GetString() ?? "";
        if (userId == 0 || guildId == 0) return EphemeralMessage("user/guild 取得不可");
        if (!room.MemberUserIds.Contains(userId))
            return EphemeralMessage("❌ あなたはこの部屋の入室者ではありません");

        if (room.Status == RoomStatus.Playing)
        {
            // プレー中は確認 ephemeral を出す
            return Json(new
            {
                type = RESP_CHANNEL_MESSAGE,
                data = new
                {
                    flags = FLAG_EPHEMERAL,
                    content = $"オンマチ{MatchingManagerService.NumberToCircle(room.Number)} はプレー中です。本当に退出しますか?",
                    components = new object[]
                    {
                        new
                        {
                            type = 1,
                            components = new object[]
                            {
                                new { type = 2, style = 4, label = "退出する",   custom_id = "matching:confirm-leave" },
                                new { type = 2, style = 2, label = "キャンセル", custom_id = "matching:cancel" },
                            },
                        },
                    },
                },
            });
        }

        // 募集中はそのまま退出
        var roleIdStr = _config["MATCHING_WAITING_ROOM_ROLE_ID"];
        ulong.TryParse(roleIdStr, out var waitingRoleId);
        _ = Task.Run(() => DoLeaveRoomAsync(token, guildId, userId, waitingRoleId, room.Number, autoEnd: false));
        return Json(new { type = RESP_DEFERRED_UPDATE_MESSAGE });
    }

    // 確認 ephemeral の "退出する" → 実際の退出処理
    private IActionResult HandleMatchingConfirmLeave(JsonElement root)
    {
        var channelId = ExtractChannelId(root);
        var room = _matching.GetRoomByChannelId(channelId);
        if (room is null) return EphemeralMessage("❌ この部屋は管理対象外です");

        var userId = ExtractUserId(root);
        var guildId = ExtractGuildId(root);
        var token = root.GetProperty("token").GetString() ?? "";
        if (userId == 0 || guildId == 0) return EphemeralMessage("user/guild 取得不可");
        if (!room.MemberUserIds.Contains(userId))
            return EphemeralMessage("❌ 既に退出済みです");

        var roleIdStr = _config["MATCHING_WAITING_ROOM_ROLE_ID"];
        ulong.TryParse(roleIdStr, out var waitingRoleId);

        _ = Task.Run(async () =>
        {
            await DoLeaveRoomAsync(token, guildId, userId, waitingRoleId, room.Number, autoEnd: false);
            await _cmd.DeleteOriginalInteractionResponseAsync(token);
        });
        return Json(new { type = RESP_DEFERRED_UPDATE_MESSAGE });
    }

    // キャンセル → 確認 ephemeral を「キャンセルしました」に上書き
    private IActionResult HandleMatchingCancel(JsonElement root)
    {
        return Json(new
        {
            type = RESP_UPDATE_MESSAGE,
            data = new
            {
                content = "キャンセルしました。",
                components = Array.Empty<object>(),
            },
        });
    }

    // プレー開始ボタン → 募集中 → プレー中 に遷移 (即実行)
    private IActionResult HandleMatchingPlayStart(JsonElement root)
    {
        var channelId = ExtractChannelId(root);
        var room = _matching.GetRoomByChannelId(channelId);
        if (room is null) return EphemeralMessage("❌ この部屋は管理対象外です");
        if (room.Status != RoomStatus.Recruiting)
            return EphemeralMessage("❌ 既にプレー中です");
        var capacity = MatchingModes.CapacityOfDisplay(room.Mode);
        if (room.MemberUserIds.Count > capacity)
            return EphemeralMessage($"❌ 定員 ({capacity}) を超えているため開始できません");

        var token = root.GetProperty("token").GetString() ?? "";
        _ = Task.Run(async () =>
        {
            await _matching.UpdateRoomAsync(room.Number, r =>
            {
                r.Status = RoomStatus.Playing;
                r.PlayStartUtc = DateTimeOffset.UtcNow;
            });
            RefreshRoomAnchor(room.Number);
            // 状態遷移は debounce を介さず即時反映 (1 秒ラグで「反映されない」と見えるのを防ぐ)
            await _matching.RefreshDashboardNowAsync();
        });
        return Json(new { type = RESP_DEFERRED_UPDATE_MESSAGE });
    }

    // プレー終了ボタン → プレー中 → 募集中 に遷移 (入室者は残る、即実行)
    private IActionResult HandleMatchingPlayEnd(JsonElement root)
    {
        var channelId = ExtractChannelId(root);
        var room = _matching.GetRoomByChannelId(channelId);
        if (room is null) return EphemeralMessage("❌ この部屋は管理対象外です");
        if (room.Status != RoomStatus.Playing)
            return EphemeralMessage("❌ プレー中ではありません");

        var token = root.GetProperty("token").GetString() ?? "";
        var roomNumber = room.Number;
        var roomMode = room.Mode;
        var roleIdStr = _config["MATCHING_WAITING_ROOM_ROLE_ID"];
        ulong.TryParse(roleIdStr, out var waitingRoleId);

        _ = Task.Run(async () =>
        {
            await _matching.UpdateRoomAsync(roomNumber, r =>
            {
                r.Status = RoomStatus.Recruiting;
                r.PlayStartUtc = null;
            });
            RefreshRoomAnchor(roomNumber);
            // 状態遷移は debounce を介さず即時反映 (1 秒ラグで「反映されない」と見えるのを防ぐ)
            await _matching.RefreshDashboardNowAsync();
            // 待機部屋チャンネルに「プレーが終了しました」通知 (status: Playing→Recruiting の遷移時のみ)
            if (waitingRoleId != 0)
            {
                await PostMatchingNotificationAsync(
                    waitingRoleId,
                    $"部屋: オンマチ{MatchingManagerService.NumberToCircle(roomNumber)} {roomMode} のプレーが終了しました。");
            }
        });
        return Json(new { type = RESP_DEFERRED_UPDATE_MESSAGE });
    }

    // モード変更ボタン → Modal を返す
    private IActionResult HandleMatchingChangeModeOpen(JsonElement root)
    {
        var channelId = ExtractChannelId(root);
        var room = _matching.GetRoomByChannelId(channelId);
        if (room is null) return EphemeralMessage("❌ この部屋は管理対象外です");
        if (room.Status != RoomStatus.Recruiting)
            return EphemeralMessage("❌ プレー中はモード変更できません");

        var options = MatchingModes.All
            .Select(m => new
            {
                label = m.Display,
                value = m.Value,
                @default = m.Display == room.Mode,
            })
            .ToArray();

        return Json(new
        {
            type = RESP_MODAL,
            data = new
            {
                title = $"オンマチ{MatchingManagerService.NumberToCircle(room.Number)} モード変更",
                custom_id = "matching:change-mode-modal",
                components = new object[]
                {
                    new
                    {
                        type = 18, // LABEL
                        label = "新しいモード",
                        component = new
                        {
                            type = 3, // STRING_SELECT
                            custom_id = "mode",
                            min_values = 1,
                            max_values = 1,
                            options,
                        },
                    },
                },
            },
        });
    }

    // 部屋分割ボタン (Phase 4)
    private IActionResult HandleMatchingSplitRoom(JsonElement root)
    {
        var channelId = ExtractChannelId(root);
        var room = _matching.GetRoomByChannelId(channelId);
        if (room is null) return EphemeralMessage("❌ この部屋は管理対象外です");
        if (!_matching.CanSplitRoom(room))
            return EphemeralMessage("❌ 現在この部屋は分割できません");

        var guildId = ExtractGuildId(root);
        var token = root.GetProperty("token").GetString() ?? "";
        if (guildId == 0) return EphemeralMessage("guild 取得不可");

        var categoryIdStr = _config["MATCHING_ROOM_CATEGORY_ID"];
        if (!ulong.TryParse(categoryIdStr, out var categoryId))
            return EphemeralMessage("MATCHING_ROOM_CATEGORY_ID 未設定");
        var roleIdStr = _config["MATCHING_WAITING_ROOM_ROLE_ID"];
        ulong.TryParse(roleIdStr, out var waitingRoleId);

        _ = Task.Run(() => DoSplitRoomAsync(token, guildId, categoryId, waitingRoleId, room.Number));
        return Json(new
        {
            type = RESP_DEFERRED_CHANNEL_MESSAGE,
            data = new { flags = FLAG_EPHEMERAL },
        });
    }

    private async Task DoSplitRoomAsync(
        string token, ulong guildId, ulong categoryId, ulong waitingRoleId, int sourceRoomNumber)
    {
        var errors = new List<string>();
        try
        {
            var sourceRoom = _matching.GetRoom(sourceRoomNumber);
            if (sourceRoom is null)
            {
                await _cmd.FollowUpTextAsync(token, "❌ 元部屋が見つかりませんでした", ephemeral: true);
                return;
            }
            if (!_matching.CanSplitRoom(sourceRoom))
            {
                await _cmd.FollowUpTextAsync(token, "❌ 現在この部屋は分割できません", ephemeral: true);
                return;
            }

            var cap = MatchingModes.CapacityOfDisplay(sourceRoom.Mode);
            var (n, sizes) = MatchingManagerService.ComputeSplitDistribution(sourceRoom.MemberUserIds.Count, cap);

            // 1. メンバーをシャッフル
            var shuffled = sourceRoom.MemberUserIds.OrderBy(_ => Random.Shared.Next()).ToList();

            // 2. グループに分配
            var groups = new List<List<ulong>>();
            int idx = 0;
            for (int i = 0; i < n; i++)
            {
                groups.Add(shuffled.Skip(idx).Take(sizes[i]).ToList());
                idx += sizes[i];
            }

            // 3. 元部屋を state から先に削除 (Number 再利用のため)
            await _matching.RemoveRoomAsync(sourceRoomNumber);

            // 4. N 個の新部屋を作成
            var createdRooms = new List<MatchingRoom>();
            const long basicAllow = 1024 + 2048 + 65536;
            var appId = _config["DISCORD_APP_ID"] ?? "";
            for (int i = 0; i < n; i++)
            {
                var newNum = _matching.NextRoomNumber();
                if (newNum == -1)
                {
                    errors.Add("9 部屋上限を超えたため一部の新部屋を作成できませんでした");
                    break;
                }
                var circle = MatchingManagerService.NumberToCircle(newNum);

                var newRoleId = await _cmd.CreateGuildRoleAsync(guildId, $"オンマチ{circle}");
                if (newRoleId is null) { errors.Add($"オンマチ{circle} のロール作成失敗"); continue; }

                var overwrites = new List<object>
                {
                    new { id = guildId.ToString(),        type = 0, deny  = "1024",                 allow = "0" },
                    new { id = newRoleId.Value.ToString(), type = 0, allow = basicAllow.ToString(), deny  = "0" },
                };
                if (!string.IsNullOrEmpty(appId))
                    overwrites.Add(new { id = appId, type = 1, allow = basicAllow.ToString(), deny = "0" });

                var newChannelId = await _cmd.CreateGuildChannelAsync(guildId, new
                {
                    name = $"🎮オンマチ{circle}",
                    type = 0,
                    parent_id = categoryId.ToString(),
                    permission_overwrites = overwrites,
                });
                if (newChannelId is null)
                {
                    await _cmd.DeleteGuildRoleAsync(guildId, newRoleId.Value);
                    errors.Add($"オンマチ{circle} のチャンネル作成失敗");
                    continue;
                }

                var pw = _matching.PickPassword() ?? "000000";
                var newRoom = new MatchingRoom
                {
                    Number = newNum,
                    Mode = sourceRoom.Mode,
                    Password = pw,
                    ChannelId = newChannelId.Value,
                    RoleId = newRoleId.Value,
                    MemberUserIds = groups[i],
                };
                await _matching.AddRoomAsync(newRoom);
                createdRooms.Add(newRoom);

                // メンバーに新ロール付与
                foreach (var uid in groups[i])
                {
                    await _cmd.AddMemberRoleAsync(guildId, uid, newRoleId.Value);
                }

                // アンカー投稿 + ピン
                var anchorPayload = _matching.BuildRoomAnchorPayload(newRoom);
                var anchorMsgId = await _cmd.CreateChannelMessageAsync(newChannelId.Value, anchorPayload);
                if (anchorMsgId is not null)
                {
                    await _matching.UpdateRoomAsync(newNum, r => r.AnchorMessageId = anchorMsgId.Value);
                    await _cmd.PinMessageAsync(newChannelId.Value, anchorMsgId.Value);
                }

                // 待機部屋通知
                if (waitingRoleId != 0)
                {
                    await PostMatchingNotificationAsync(
                        waitingRoleId,
                        $"部屋: オンマチ{circle} {sourceRoom.Mode} が作成されました。");
                }
            }

            // 5. 元部屋のチャンネル/ロールを削除 (ロール削除で旧ロールも自動的に剥がれる)
            await _cmd.DeleteChannelAsync(sourceRoom.ChannelId);
            await _cmd.DeleteGuildRoleAsync(guildId, sourceRoom.RoleId);

            // 6. ダッシュボード更新
            _matching.RefreshDashboard();

            // 7. ephemeral の "考え中" を消す or エラー報告
            if (errors.Count == 0)
            {
                await _cmd.DeleteOriginalInteractionResponseAsync(token);
            }
            else
            {
                await _cmd.FollowUpTextAsync(
                    token,
                    $"⚠️ 部分的に失敗しました:\n- {string.Join("\n- ", errors)}",
                    ephemeral: true);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "split room failed");
            await _cmd.FollowUpTextAsync(token, $"❌ 分割エラー: {ex.Message}", ephemeral: true);
        }
    }

    // 部屋アンカーを debounce 経由で更新
    private void RefreshRoomAnchor(int roomNumber)
    {
        var room = _matching.GetRoom(roomNumber);
        if (room is null || room.AnchorMessageId == 0) return;
        _editDebouncer.Schedule(room.ChannelId, room.AnchorMessageId, () =>
        {
            var latest = _matching.GetRoom(roomNumber);
            return latest is null
                ? new { } as object
                : _matching.BuildRoomAnchorPayload(latest);
        });
    }

    // Phase 3 のプレー開始ボタン用に残す (退出は確認不要に変わった)
    private IActionResult BuildConfirmModal(string title, string labelText, string confirmLabel, string customId)
    {
        return Json(new
        {
            type = RESP_MODAL,
            data = new
            {
                title,
                custom_id = customId,
                components = new object[]
                {
                    new
                    {
                        type = 18, // LABEL
                        label = labelText,
                        component = new
                        {
                            type = 3, // STRING_SELECT
                            custom_id = "confirm",
                            min_values = 1,
                            max_values = 1,
                            options = new object[]
                            {
                                new { label = confirmLabel,    value = "yes" },
                                new { label = "キャンセル",   value = "no"  },
                            },
                        },
                    },
                },
            },
        });
    }

    private async Task DoLeaveRoomAsync(
        string token, ulong guildId, ulong userId, ulong waitingRoleId,
        int roomNumber, bool autoEnd)
    {
        try
        {
            var room = _matching.GetRoom(roomNumber);
            if (room is null) return;
            await _cmd.RemoveMemberRoleAsync(guildId, userId, room.RoleId);
            // 退出後は待機部屋に自動的には入れない (待機部屋に戻りたい人は手動で「入室」ボタンを押す)
            await _matching.UpdateRoomAsync(roomNumber, r => r.MemberUserIds.Remove(userId));

            var after = _matching.GetRoom(roomNumber);
            if (after is null) return;
            if (after.MemberUserIds.Count == 0)
            {
                // 無人 → 自動削除
                await DoEndRoomAsync(token, guildId, waitingRoleId, roomNumber, alreadyEmptied: true);
                return;
            }

            if (after.AnchorMessageId != 0)
            {
                _editDebouncer.Schedule(
                    after.ChannelId, after.AnchorMessageId,
                    () =>
                    {
                        var latest = _matching.GetRoom(roomNumber);
                        return latest is null
                            ? new { } as object
                            : _matching.BuildRoomAnchorPayload(latest);
                    });
            }
            _matching.RefreshDashboard();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "leave room failed");
            await _cmd.FollowUpTextAsync(token, $"❌ 退出処理エラー: {ex.Message}", ephemeral: true);
        }
    }

    private async Task DoEndRoomAsync(
        string token, ulong guildId, ulong waitingRoleId, int roomNumber,
        bool alreadyEmptied = false)
    {
        try
        {
            var room = _matching.GetRoom(roomNumber);
            if (room is null) return;

            // 残メンバー全員から部屋ロールを剥奪 (退出後は待機部屋に自動入室させない仕様)
            // → ロール削除で自動的に剥がれるが、明示的に消しておく
            if (!alreadyEmptied)
            {
                foreach (var uid in room.MemberUserIds.ToList())
                {
                    await _cmd.RemoveMemberRoleAsync(guildId, uid, room.RoleId);
                }
            }

            // チャンネル / ロール削除
            await _cmd.DeleteChannelAsync(room.ChannelId);
            await _cmd.DeleteGuildRoleAsync(guildId, room.RoleId);

            // state から除去
            await _matching.RemoveRoomAsync(roomNumber);

            // 管理メッセージ更新 (待機部屋への「プレー終了」通知は
            // status: Playing→Recruiting の遷移時のみ送る仕様なのでここでは出さない)
            _matching.RefreshDashboard();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "end room failed");
            await _cmd.FollowUpTextAsync(token, $"❌ プレー終了処理エラー: {ex.Message}", ephemeral: true);
        }
    }

    private async Task PostMatchingNotificationAsync(ulong waitingRoleId, string text)
    {
        var notifyChannelStr = _config["MATCHING_WAITING_ROOM_CHANNEL_ID"];
        if (!ulong.TryParse(notifyChannelStr, out var notifyChannelId)) return;
        var payload = new
        {
            content = $"<@&{waitingRoleId}> {text}",
            allowed_mentions = new { roles = new[] { waitingRoleId.ToString() } },
        };
        await _cmd.CreateChannelMessageAsync(notifyChannelId, payload);
    }

    private static ulong ExtractGuildId(JsonElement root) =>
        root.TryGetProperty("guild_id", out var g) &&
        ulong.TryParse(g.GetString(), out var u) ? u : 0UL;
    private static ulong ExtractUserId(JsonElement root)
    {
        if (root.TryGetProperty("member", out var m) &&
            m.TryGetProperty("user", out var u) &&
            u.TryGetProperty("id", out var i) &&
            ulong.TryParse(i.GetString(), out var v)) return v;
        if (root.TryGetProperty("user", out var u2) &&
            u2.TryGetProperty("id", out var i2) &&
            ulong.TryParse(i2.GetString(), out var v2)) return v2;
        return 0UL;
    }
    private static ulong ExtractChannelId(JsonElement root) =>
        root.TryGetProperty("channel_id", out var c) &&
        ulong.TryParse(c.GetString(), out var u) ? u : 0UL;

    private async Task<IActionResult> HandleComponentAsync(
        JsonElement root, CancellationToken ct)
    {
        var customId = root.GetProperty("data").GetProperty("custom_id").GetString() ?? "";
        var parts = customId.Split(':');
        if (parts.Length < 2) return InvalidComponent("custom_id 形式不正");

        var domain = parts[0];
        var op = parts[1];

        if (domain == "song" && parts.Length >= 3 && int.TryParse(parts[2], out var songId))
        {
            return await HandleSongComponentAsync(root, op, songId, ct);
        }

        if (domain == "songmax" && parts.Length >= 3 && int.TryParse(parts[2], out var smSongId))
        {
            int? estimatedMax = parts.Length >= 4 && int.TryParse(parts[3], out var em) ? em : null;
            return await HandleSongMaxComponentAsync(root, op, smSongId, estimatedMax, ct);
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

        if (domain == "matching" && parts.Length >= 2)
        {
            return HandleMatchingComponent(root, op, parts);
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

    private async Task<IActionResult> HandleSongMaxComponentAsync(
        JsonElement root, string op, int songId, int? estimatedMax, CancellationToken ct)
    {
        if (op == "approve")
        {
            if (estimatedMax is null) return InvalidComponent("approve には推定値が必要です");
            var resp = await _admin.SetSongExMaxAsync(songId, estimatedMax, ct);
            return await BuildUpdateMessageResponseAsync(
                root, resp, $"✅ 承認 (MAX = {estimatedMax})", ExtractActorName(root), ct);
        }
        if (op == "reject")
        {
            // 却下 = 何もせず ex_score_max は null のままにしておく
            return await BuildUpdateMessageResponseAsync(
                root, new HttpResponseMessage(System.Net.HttpStatusCode.OK),
                "❌ 却下 (MAX 未確定のまま)", ExtractActorName(root), ct);
        }
        if (op == "edit")
        {
            // 修正用モーダル (TextInput で MAX 値を直接入力)
            return Json(new
            {
                type = RESP_MODAL,
                data = new
                {
                    custom_id = $"songmax:editsubmit:{songId}",
                    title = "EX SCORE MAX 修正",
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
                                    custom_id = "max",
                                    label = "EX SCORE MAX (整数)",
                                    style = TEXT_INPUT_SHORT,
                                    min_length = 1,
                                    max_length = 6,
                                    required = true,
                                    value = (estimatedMax ?? 0).ToString(),
                                },
                            },
                        },
                    },
                },
            });
        }
        return InvalidComponent("未知の songmax op");
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

        if (parts.Length == 2 && parts[0] == "matching" && parts[1] == "create-modal")
        {
            return HandleMatchingCreateModalSubmit(root);
        }

        if (parts.Length == 3 && parts[0] == "songmax" && parts[1] == "editsubmit"
            && int.TryParse(parts[2], out var smSongId))
        {
            var smInputs = ParseModalInputs(root);
            if (!smInputs.TryGetValue("max", out var maxStr) || !int.TryParse(maxStr, out var newMax))
            {
                return EphemeralMessage("MAX を整数で入力してください");
            }
            if (newMax is < 0 or > 100_000)
            {
                return EphemeralMessage("MAX は 0〜100000 の範囲です");
            }
            var smResp = await _admin.SetSongExMaxAsync(smSongId, newMax, ct);
            return await BuildUpdateMessageResponseAsync(
                root, smResp, $"✅ 承認 (MAX = {newMax}、修正済み)", ExtractActorName(root), ct);
        }

        if (parts.Length == 2 && parts[0] == "matching" && parts[1] == "change-mode-modal")
        {
            return HandleMatchingChangeModeSubmit(root);
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
