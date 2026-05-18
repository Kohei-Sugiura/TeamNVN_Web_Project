using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace Weepa.Api.Services;

/// <summary>
/// Discord Application Commands (Slash Commands) の登録と、
/// Interaction の deferred response → follow-up 投稿のためのクライアント。
/// </summary>
public sealed class DiscordCommandClient
{
    private readonly HttpClient _http;
    private readonly ILogger<DiscordCommandClient> _logger;
    private readonly string? _appId;
    private readonly string? _token;
    public bool Enabled { get; }

    public DiscordCommandClient(
        IHttpClientFactory factory,
        IConfiguration config,
        ILogger<DiscordCommandClient> logger)
    {
        _logger = logger;
        _appId = config["DISCORD_APP_ID"];
        _token = config["DISCORD_BOT_TOKEN"];
        Enabled = !string.IsNullOrWhiteSpace(_appId) && !string.IsNullOrWhiteSpace(_token);

        _http = factory.CreateClient("discord-cmd");
        _http.BaseAddress = new Uri("https://discord.com/api/v10/");
        if (Enabled)
        {
            _http.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bot", _token);
            _http.DefaultRequestHeaders.UserAgent.ParseAdd(
                "WeePA-API (https://team-nvn-server.com)");
        }
    }

    /// <summary>
    /// 起動時に冪等にコマンド一覧をサーバーに登録する (PUT は完全置換)。
    /// </summary>
    public async Task SyncCommandsAsync(CancellationToken ct = default)
    {
        if (!Enabled) return;

        // 全コマンド admin 限定 (default_member_permissions = "0" は
        // 「全 permission 不要」= ロール持ちでないと UI に出ない、の意味)。
        var commands = new object[]
        {
            new
            {
                name = "weepa-result",
                description = "WeePA イベントの集計結果ランキングを画像で投稿",
                type = 1, // CHAT_INPUT
                default_member_permissions = "0",
                options = new object[]
                {
                    new
                    {
                        name = "id",
                        description = "weepa_event_id (省略時は直近開催)",
                        type = 4, // INTEGER
                        required = false,
                    },
                },
            },
            new
            {
                name = "weepa-publish",
                description = "イベントを公開状態にする (tallied_at = NOW)",
                type = 1,
                default_member_permissions = "0",
                options = new object[]
                {
                    new
                    {
                        name = "id",
                        description = "weepa_event_id (省略時は直近の未公開イベント)",
                        type = 4,
                        required = false,
                    },
                },
            },
            new
            {
                name = "weepa-unpublish",
                description = "イベントを非公開に戻す (tallied_at = NULL)",
                type = 1,
                default_member_permissions = "0",
                options = new object[]
                {
                    new
                    {
                        name = "id",
                        description = "weepa_event_id (必須)",
                        type = 4,
                        required = true,
                    },
                },
            },
            new
            {
                name = "weepa-songdetail",
                description = "weepa_song_id から楽曲詳細を表示",
                type = 1,
                default_member_permissions = "0",
                options = new object[]
                {
                    new
                    {
                        name = "id",
                        description = "weepa_song_id",
                        type = 4,
                        required = true,
                    },
                },
            },
            new
            {
                name = "weepa-eventdetail",
                description = "weepa_event_id からイベント詳細を表示",
                type = 1,
                default_member_permissions = "0",
                options = new object[]
                {
                    new
                    {
                        name = "id",
                        description = "weepa_event_id",
                        type = 4,
                        required = true,
                    },
                },
            },
            new
            {
                name = "weepa-events-shuffle",
                description = "未開催 (キュー) のイベント順をランダムに入れ替える",
                type = 1,
                default_member_permissions = "0",
            },
            new
            {
                name = "weepa-events-schedules",
                description = "未開催の開催スケジュール (上位 20 件) を表示",
                type = 1,
                default_member_permissions = "0",
            },
            new
            {
                name = "weepa-x-post",
                description = "X 投稿用の課題曲告知テンプレートを生成",
                type = 1,
                default_member_permissions = "0",
                options = new object[]
                {
                    new
                    {
                        name = "level",
                        description = "レベル (例: 17)",
                        type = 3, // STRING
                        required = true,
                    },
                    new
                    {
                        name = "id",
                        description = "weepa_event_id (省略時は現在開催中)",
                        type = 4,
                        required = false,
                    },
                },
            },
            new
            {
                name = "msc-result",
                description = "MSC (ムンチャレ) イベントの集計結果ランキングを画像で投稿",
                type = 1,
                default_member_permissions = "0",
                options = new object[]
                {
                    new
                    {
                        name = "id",
                        description = "msc_event_id (省略時は直近開催)",
                        type = 4,
                        required = false,
                    },
                },
            },
            new
            {
                name = "msc-publish",
                description = "MSC イベントを公開状態にする (tallied_at = NOW)",
                type = 1,
                default_member_permissions = "0",
                options = new object[]
                {
                    new
                    {
                        name = "id",
                        description = "msc_event_id (省略時は直近の未公開イベント)",
                        type = 4,
                        required = false,
                    },
                },
            },
            new
            {
                name = "msc-unpublish",
                description = "MSC イベントを非公開に戻す (tallied_at = NULL)",
                type = 1,
                default_member_permissions = "0",
                options = new object[]
                {
                    new
                    {
                        name = "id",
                        description = "msc_event_id (必須)",
                        type = 4,
                        required = true,
                    },
                },
            },
            new
            {
                name = "msc-eventdetail",
                description = "msc_event_id から MSC イベント詳細を表示",
                type = 1,
                default_member_permissions = "0",
                options = new object[]
                {
                    new
                    {
                        name = "id",
                        description = "msc_event_id",
                        type = 4,
                        required = true,
                    },
                },
            },
            new
            {
                name = "msc-events-schedules",
                description = "MSC の開催予定 (start_date > NOW、上位 20 件) を表示",
                type = 1,
                default_member_permissions = "0",
            },
            new
            {
                name = "matching-manager",
                description = "オンマチ会 マッチング管理メッセージを投稿",
                type = 1,
                // ADMINISTRATOR (0x8) のみ
                default_member_permissions = "8",
            },
            new
            {
                name = "room-panel",
                description = "現在のマッチング部屋の管理パネルを最新メッセージとして再投稿",
                type = 1,
                // default_member_permissions を指定しない = 全員が使える
            },
        };

        try
        {
            using var resp = await _http.PutAsJsonAsync(
                $"applications/{_appId}/commands", commands, ct);
            if (!resp.IsSuccessStatusCode)
            {
                var body = await resp.Content.ReadAsStringAsync(ct);
                _logger.LogWarning(
                    "Discord command sync failed: {Status} {Body}",
                    (int)resp.StatusCode, body);
            }
            else
            {
                _logger.LogInformation("Discord slash commands synced");
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Discord command sync threw");
        }
    }

    /// <summary>
    /// deferred 応答後、follow-up メッセージを画像添付付きで送る。
    /// </summary>
    public async Task FollowUpWithImageAsync(
        string interactionToken,
        string content,
        byte[] imageBytes,
        string fileName,
        CancellationToken ct = default)
    {
        if (!Enabled) return;

        var payload = new
        {
            content,
            attachments = new[] { new { id = 0, filename = fileName } },
        };

        using var multipart = new MultipartFormDataContent();
        var jsonContent = new StringContent(
            JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
        multipart.Add(jsonContent, "payload_json");

        var imageContent = new ByteArrayContent(imageBytes);
        imageContent.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        multipart.Add(imageContent, "files[0]", fileName);

        try
        {
            using var resp = await _http.PostAsync(
                $"webhooks/{_appId}/{interactionToken}", multipart, ct);
            if (!resp.IsSuccessStatusCode)
            {
                var body = await resp.Content.ReadAsStringAsync(ct);
                _logger.LogWarning(
                    "Discord follow-up failed: {Status} {Body}",
                    (int)resp.StatusCode, body);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Discord follow-up threw");
        }
    }

    /// <summary>
    /// ギルドメンバーにロールを付与する (PUT)。404 = メンバーがいない、403 = 権限不足。
    /// </summary>
    public async Task<bool> AddMemberRoleAsync(
        ulong guildId, ulong userId, ulong roleId, CancellationToken ct = default)
    {
        if (!Enabled) return false;
        try
        {
            using var req = new HttpRequestMessage(
                HttpMethod.Put,
                $"guilds/{guildId}/members/{userId}/roles/{roleId}");
            req.Content = new StringContent("", Encoding.UTF8, "application/json");
            using var resp = await _http.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode)
            {
                var body = await resp.Content.ReadAsStringAsync(ct);
                _logger.LogWarning(
                    "add role failed {Status}: {Body}",
                    (int)resp.StatusCode, body);
                return false;
            }
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "add role threw");
            return false;
        }
    }

    /// <summary>
    /// ギルドメンバーからロールを剥奪する (DELETE)。
    /// </summary>
    public async Task<bool> RemoveMemberRoleAsync(
        ulong guildId, ulong userId, ulong roleId, CancellationToken ct = default)
    {
        if (!Enabled) return false;
        try
        {
            using var resp = await _http.DeleteAsync(
                $"guilds/{guildId}/members/{userId}/roles/{roleId}", ct);
            if (!resp.IsSuccessStatusCode &&
                resp.StatusCode != System.Net.HttpStatusCode.NotFound)
            {
                var body = await resp.Content.ReadAsStringAsync(ct);
                _logger.LogWarning(
                    "remove role failed {Status}: {Body}",
                    (int)resp.StatusCode, body);
                return false;
            }
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "remove role threw");
            return false;
        }
    }

    /// <summary>
    /// 任意のチャンネルにメッセージを投稿し、その message ID を返す。
    /// Bot にチャンネルへの送信権限が無い場合は null。
    /// </summary>
    public async Task<ulong?> CreateChannelMessageAsync(
        ulong channelId, object payload, CancellationToken ct = default)
    {
        if (!Enabled) return null;
        try
        {
            using var resp = await _http.PostAsJsonAsync(
                $"channels/{channelId}/messages", payload, ct);
            if (!resp.IsSuccessStatusCode)
            {
                var body = await resp.Content.ReadAsStringAsync(ct);
                _logger.LogWarning(
                    "create message failed {Status}: {Body}",
                    (int)resp.StatusCode, body);
                return null;
            }
            var body2 = await resp.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(body2);
            if (doc.RootElement.TryGetProperty("id", out var id) &&
                ulong.TryParse(id.GetString(), out var u))
            {
                return u;
            }
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "create message threw");
            return null;
        }
    }

    public async Task<bool> DeleteChannelMessageAsync(
        ulong channelId, ulong messageId, CancellationToken ct = default)
    {
        if (!Enabled) return false;
        try
        {
            using var resp = await _http.DeleteAsync(
                $"channels/{channelId}/messages/{messageId}", ct);
            if (!resp.IsSuccessStatusCode &&
                resp.StatusCode != System.Net.HttpStatusCode.NotFound)
            {
                var body = await resp.Content.ReadAsStringAsync(ct);
                _logger.LogWarning("delete channel message failed {Status}: {Body}", (int)resp.StatusCode, body);
                return false;
            }
            return true;
        }
        catch (Exception ex) { _logger.LogWarning(ex, "delete channel message threw"); return false; }
    }

    public async Task<bool> EditChannelMessageAsync(
        ulong channelId, ulong messageId, object payload, CancellationToken ct = default)
    {
        if (!Enabled) return false;
        try
        {
            using var req = new HttpRequestMessage(
                new HttpMethod("PATCH"),
                $"channels/{channelId}/messages/{messageId}")
            {
                Content = JsonContent.Create(payload),
            };
            using var resp = await _http.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode)
            {
                var body = await resp.Content.ReadAsStringAsync(ct);
                _logger.LogWarning("edit channel message failed {Status}: {Body}", (int)resp.StatusCode, body);
                return false;
            }
            return true;
        }
        catch (Exception ex) { _logger.LogWarning(ex, "edit channel message threw"); return false; }
    }

    /// <summary>
    /// ギルドにロールを作成して role ID を返す。失敗時 null。
    /// </summary>
    public async Task<ulong?> CreateGuildRoleAsync(
        ulong guildId, string name, CancellationToken ct = default)
    {
        if (!Enabled) return null;
        try
        {
            using var resp = await _http.PostAsJsonAsync(
                $"guilds/{guildId}/roles",
                new { name, mentionable = false }, ct);
            if (!resp.IsSuccessStatusCode)
            {
                var body = await resp.Content.ReadAsStringAsync(ct);
                _logger.LogWarning("create role failed {Status}: {Body}", (int)resp.StatusCode, body);
                return null;
            }
            var body2 = await resp.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(body2);
            if (doc.RootElement.TryGetProperty("id", out var id) &&
                ulong.TryParse(id.GetString(), out var u)) return u;
            return null;
        }
        catch (Exception ex) { _logger.LogWarning(ex, "create role threw"); return null; }
    }

    public async Task<bool> DeleteGuildRoleAsync(
        ulong guildId, ulong roleId, CancellationToken ct = default)
    {
        if (!Enabled) return false;
        try
        {
            using var resp = await _http.DeleteAsync($"guilds/{guildId}/roles/{roleId}", ct);
            if (!resp.IsSuccessStatusCode &&
                resp.StatusCode != System.Net.HttpStatusCode.NotFound)
            {
                var body = await resp.Content.ReadAsStringAsync(ct);
                _logger.LogWarning("delete role failed {Status}: {Body}", (int)resp.StatusCode, body);
                return false;
            }
            return true;
        }
        catch (Exception ex) { _logger.LogWarning(ex, "delete role threw"); return false; }
    }

    /// <summary>
    /// ギルドにチャンネルを作成し、その channel ID を返す。payload は Discord API 仕様準拠。
    /// </summary>
    public async Task<ulong?> CreateGuildChannelAsync(
        ulong guildId, object payload, CancellationToken ct = default)
    {
        if (!Enabled) return null;
        try
        {
            using var resp = await _http.PostAsJsonAsync(
                $"guilds/{guildId}/channels", payload, ct);
            if (!resp.IsSuccessStatusCode)
            {
                var body = await resp.Content.ReadAsStringAsync(ct);
                _logger.LogWarning("create channel failed {Status}: {Body}", (int)resp.StatusCode, body);
                return null;
            }
            var body2 = await resp.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(body2);
            if (doc.RootElement.TryGetProperty("id", out var id) &&
                ulong.TryParse(id.GetString(), out var u)) return u;
            return null;
        }
        catch (Exception ex) { _logger.LogWarning(ex, "create channel threw"); return null; }
    }

    public async Task<bool> DeleteChannelAsync(ulong channelId, CancellationToken ct = default)
    {
        if (!Enabled) return false;
        try
        {
            using var resp = await _http.DeleteAsync($"channels/{channelId}", ct);
            if (!resp.IsSuccessStatusCode &&
                resp.StatusCode != System.Net.HttpStatusCode.NotFound)
            {
                var body = await resp.Content.ReadAsStringAsync(ct);
                _logger.LogWarning("delete channel failed {Status}: {Body}", (int)resp.StatusCode, body);
                return false;
            }
            return true;
        }
        catch (Exception ex) { _logger.LogWarning(ex, "delete channel threw"); return false; }
    }

    public async Task<bool> PinMessageAsync(
        ulong channelId, ulong messageId, CancellationToken ct = default)
    {
        if (!Enabled) return false;
        try
        {
            using var req = new HttpRequestMessage(
                HttpMethod.Put, $"channels/{channelId}/pins/{messageId}");
            using var resp = await _http.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode)
            {
                var body = await resp.Content.ReadAsStringAsync(ct);
                _logger.LogWarning("pin message failed {Status}: {Body}", (int)resp.StatusCode, body);
                return false;
            }
            return true;
        }
        catch (Exception ex) { _logger.LogWarning(ex, "pin message threw"); return false; }
    }

    /// <summary>
    /// 元 (interaction が紐付くメッセージ) を削除する。
    /// deferred ephemeral 応答後に "考え中" 表示も含めて消したい時に使う。
    /// </summary>
    public async Task<bool> DeleteOriginalInteractionResponseAsync(
        string interactionToken, CancellationToken ct = default)
    {
        if (!Enabled) return false;
        try
        {
            using var resp = await _http.DeleteAsync(
                $"webhooks/{_appId}/{interactionToken}/messages/@original", ct);
            if (!resp.IsSuccessStatusCode &&
                resp.StatusCode != System.Net.HttpStatusCode.NotFound)
            {
                var body = await resp.Content.ReadAsStringAsync(ct);
                _logger.LogWarning(
                    "delete original failed {Status}: {Body}",
                    (int)resp.StatusCode, body);
                return false;
            }
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "delete original threw");
            return false;
        }
    }

    /// <summary>
    /// 元 (interaction が紐付くメッセージ) を編集する。
    /// component interaction で type=6 (DEFERRED_UPDATE_MESSAGE) を返した後に呼び、
    /// クリックされたメッセージ本体を更新するために使う。
    /// </summary>
    public async Task<bool> EditOriginalInteractionResponseAsync(
        string interactionToken, object payload, CancellationToken ct = default)
    {
        if (!Enabled) return false;
        try
        {
            var req = new HttpRequestMessage(
                new HttpMethod("PATCH"),
                $"webhooks/{_appId}/{interactionToken}/messages/@original")
            {
                Content = JsonContent.Create(payload),
            };
            using var resp = await _http.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode)
            {
                var body = await resp.Content.ReadAsStringAsync(ct);
                _logger.LogWarning(
                    "edit original failed {Status}: {Body}",
                    (int)resp.StatusCode, body);
                return false;
            }
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "edit original threw");
            return false;
        }
    }

    /// <summary>
    /// follow-up テキストのみ (エラーメッセージ用)。
    /// </summary>
    public async Task FollowUpTextAsync(
        string interactionToken,
        string content,
        bool ephemeral = false,
        CancellationToken ct = default)
    {
        if (!Enabled) return;
        var payload = ephemeral
            ? (object)new { content, flags = 64 }
            : new { content };
        try
        {
            using var resp = await _http.PostAsJsonAsync(
                $"webhooks/{_appId}/{interactionToken}", payload, ct);
            if (!resp.IsSuccessStatusCode)
            {
                var body = await resp.Content.ReadAsStringAsync(ct);
                _logger.LogWarning(
                    "Discord follow-up text failed: {Status} {Body}",
                    (int)resp.StatusCode, body);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Discord follow-up text threw");
        }
    }
}
