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

        if (name == "weepa-result")
        {
            int? eventId = null;
            if (data.TryGetProperty("options", out var opts))
            {
                foreach (var opt in opts.EnumerateArray())
                {
                    if (opt.GetProperty("name").GetString() == "id" &&
                        opt.TryGetProperty("value", out var v) &&
                        v.TryGetInt32(out var i))
                    {
                        eventId = i;
                    }
                }
            }
            // 3 秒以内に応答が必要なので一旦 deferred を返してから裏で生成
            _ = Task.Run(() => HandleWeepaResultAsync(token, eventId));
            return Json(new { type = RESP_DEFERRED_CHANNEL_MESSAGE });
        }

        return Json(new
        {
            type = RESP_CHANNEL_MESSAGE,
            data = new { content = $"未対応のコマンド: /{name}", flags = FLAG_EPHEMERAL },
        });
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

    private async Task<IActionResult> HandleModalAsync(
        JsonElement root, CancellationToken ct)
    {
        var customId = root.GetProperty("data").GetProperty("custom_id").GetString() ?? "";
        var parts = customId.Split(':');
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
