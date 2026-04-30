using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace Weepa.Api.Services;

public sealed record SongSubmittedNotification(
    int WeepaSongId,
    string Song,
    string Effect,
    string XId,
    string SubmitName,
    string? SubmitReason);

public sealed record ScoreSubmittedNotification(
    int EventId,
    int SubmitId,
    string XId,
    string SubmitName,
    int Score,
    int? ExScore,
    string Song,
    string Effect,
    string ImageFilePath,
    string ImageContentType);

public interface IDiscordNotifier
{
    Task NotifySongSubmittedAsync(SongSubmittedNotification n, CancellationToken ct = default);
    Task NotifyScoreSubmittedAsync(ScoreSubmittedNotification n, CancellationToken ct = default);
}

internal sealed class DiscordNotifier : IDiscordNotifier
{
    private readonly HttpClient _http;
    private readonly ILogger<DiscordNotifier> _logger;
    private readonly string? _channelId;
    private readonly bool _enabled;

    public DiscordNotifier(
        IHttpClientFactory factory,
        IConfiguration config,
        ILogger<DiscordNotifier> logger)
    {
        _logger = logger;
        var token = config["DISCORD_BOT_TOKEN"];
        _channelId = config["DISCORD_NOTIFY_CHANNEL_ID"];
        _enabled =
            !string.IsNullOrWhiteSpace(token) &&
            !string.IsNullOrWhiteSpace(_channelId);

        _http = factory.CreateClient("discord");
        _http.BaseAddress = new Uri("https://discord.com/api/v10/");
        if (_enabled)
        {
            _http.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bot", token);
            _http.DefaultRequestHeaders.UserAgent.ParseAdd(
                "WeePA-API (https://team-nvn-server.com)");
        }
    }

    public async Task NotifySongSubmittedAsync(
        SongSubmittedNotification n,
        CancellationToken ct = default)
    {
        if (!_enabled) return;

        var payload = new
        {
            embeds = new[]
            {
                new
                {
                    title = $"🎵 楽曲提案 #{n.WeepaSongId}",
                    color = 0x00e5ff,
                    fields = new object[]
                    {
                        new { name = "楽曲", value = Truncate(n.Song, 1024), inline = true },
                        new { name = "譜面", value = n.Effect, inline = true },
                        new { name = "提案者", value = $"{Truncate(n.SubmitName, 80)} (@{n.XId})", inline = false },
                        new
                        {
                            name = "理由",
                            value = string.IsNullOrWhiteSpace(n.SubmitReason)
                                ? "(なし)"
                                : Truncate(n.SubmitReason, 1024),
                            inline = false,
                        },
                    },
                },
            },
            components = new[]
            {
                new
                {
                    type = 1,
                    components = new object[]
                    {
                        new { type = 2, style = 3, label = "承認", custom_id = $"song:approve:{n.WeepaSongId}" },
                        new { type = 2, style = 4, label = "却下", custom_id = $"song:reject:{n.WeepaSongId}" },
                    },
                },
            },
        };

        try
        {
            using var resp = await _http.PostAsJsonAsync(
                $"channels/{_channelId}/messages", payload, ct);
            await LogIfFailureAsync(resp, "song notify", ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Discord song notify failed");
        }
    }

    public async Task NotifyScoreSubmittedAsync(
        ScoreSubmittedNotification n,
        CancellationToken ct = default)
    {
        if (!_enabled) return;
        if (!File.Exists(n.ImageFilePath))
        {
            _logger.LogWarning(
                "Discord notify: image file missing {Path}", n.ImageFilePath);
            return;
        }

        var imageFileName = Path.GetFileName(n.ImageFilePath);

        var payload = new
        {
            embeds = new[]
            {
                new
                {
                    title = "🏆 スコア投稿",
                    color = 0xff3d8a,
                    fields = new object[]
                    {
                        new { name = "イベント", value = $"{Truncate(n.Song, 200)} ({n.Effect})", inline = false },
                        new { name = "投稿者", value = $"{Truncate(n.SubmitName, 80)} (@{n.XId})", inline = false },
                        new { name = "Score", value = n.Score.ToString("N0", CultureInfo.InvariantCulture), inline = true },
                        new { name = "EX",    value = n.ExScore?.ToString("N0", CultureInfo.InvariantCulture) ?? "-", inline = true },
                    },
                    image = new { url = $"attachment://{imageFileName}" },
                },
            },
            attachments = new[] { new { id = 0, filename = imageFileName } },
            components = new[]
            {
                new
                {
                    type = 1,
                    components = new object[]
                    {
                        new { type = 2, style = 3, label = "承認",     custom_id = $"score:approve:{n.EventId}:{n.XId}:{n.SubmitId}" },
                        new { type = 2, style = 4, label = "却下",     custom_id = $"score:reject:{n.EventId}:{n.XId}:{n.SubmitId}" },
                        new { type = 2, style = 1, label = "修正承認", custom_id = $"score:edit:{n.EventId}:{n.XId}:{n.SubmitId}" },
                    },
                },
            },
        };

        try
        {
            using var content = new MultipartFormDataContent();
            var json = JsonSerializer.Serialize(payload);
            var jsonContent = new StringContent(json, Encoding.UTF8, "application/json");
            content.Add(jsonContent, "payload_json");

            await using var fs = File.OpenRead(n.ImageFilePath);
            var imageContent = new StreamContent(fs);
            if (MediaTypeHeaderValue.TryParse(n.ImageContentType, out var mediaType))
            {
                imageContent.Headers.ContentType = mediaType;
            }
            content.Add(imageContent, "files[0]", imageFileName);

            using var resp = await _http.PostAsync(
                $"channels/{_channelId}/messages", content, ct);
            await LogIfFailureAsync(resp, "score notify", ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Discord score notify failed");
        }
    }

    private async Task LogIfFailureAsync(
        HttpResponseMessage resp, string label, CancellationToken ct)
    {
        if (resp.IsSuccessStatusCode) return;
        string body;
        try { body = await resp.Content.ReadAsStringAsync(ct); }
        catch { body = "(failed to read body)"; }
        _logger.LogWarning(
            "Discord {Label} returned {Status}: {Body}",
            label, (int)resp.StatusCode, body);
    }

    private static string Truncate(string s, int max) =>
        s.Length <= max ? s : s.Substring(0, max - 1) + "…";
}
