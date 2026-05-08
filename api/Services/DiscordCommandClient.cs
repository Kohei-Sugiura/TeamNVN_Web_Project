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

        // /weepa-result <id?>: ランキング画面の PNG を Discord に投稿
        // id 省略時は直近開催のイベント
        var commands = new[]
        {
            new
            {
                name = "weepa-result",
                description = "WeePA イベントの集計結果ランキングを画像で投稿します",
                type = 1, // CHAT_INPUT
                options = new object[]
                {
                    new
                    {
                        name = "id",
                        description = "イベント ID (省略時は直近開催のイベント)",
                        type = 4, // INTEGER
                        required = false,
                    },
                },
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
