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

public sealed record MscScoreSubmittedNotification(
    int EventId,
    int SubmitId,
    string XId,
    string SubmitName,
    string Platform,
    string EventTitle,
    IReadOnlyList<MscPlaySummary> Plays,
    int TotalScore,
    IReadOnlyList<MscSubmissionImageRef> Images);

public sealed record MscPlaySummary(int PlayOrder, int SongOrder, int Score, string Song, string Effect);

// PlayOrder 0 = 通しリザルト (summary)、1..4 = 各 Track のリザルト
public sealed record MscSubmissionImageRef(int PlayOrder, string Path, string ContentType);

public interface IDiscordNotifier
{
    Task NotifySongSubmittedAsync(SongSubmittedNotification n, CancellationToken ct = default);
    Task NotifyScoreSubmittedAsync(ScoreSubmittedNotification n, CancellationToken ct = default);
    Task NotifyMscScoreSubmittedAsync(MscScoreSubmittedNotification n, CancellationToken ct = default);
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

    public async Task NotifyMscScoreSubmittedAsync(
        MscScoreSubmittedNotification n,
        CancellationToken ct = default)
    {
        if (!_enabled) return;

        // 画像ファイルを存在確認した上で order に揃える (0 = summary, 1..4 = Track)
        var validImages = n.Images
            .Where(i => File.Exists(i.Path))
            .OrderBy(i => i.PlayOrder)
            .ToList();

        // Embed のメイン画像には通しリザルト (PlayOrder=0) を使う。それ以外は
        // Embed に紐付けない添付として送ると Discord が下部にギャラリー表示する
        var summary = validImages.FirstOrDefault(i => i.PlayOrder == 0);
        var summaryFileName = summary is not null ? Path.GetFileName(summary.Path) : null;

        var playLines = n.Plays
            .OrderBy(p => p.PlayOrder)
            .Select(p =>
            {
                var label = p.PlayOrder == 4
                    ? "EX Track"
                    : $"{p.PlayOrder}{(p.PlayOrder == 1 ? "st" : p.PlayOrder == 2 ? "nd" : "rd")} Track";
                return $"{label}: {p.Score:N0} — {p.Song} (`{p.Effect}`)";
            });

        var fields = new List<object>
        {
            new { name = "イベント", value = Truncate(n.EventTitle, 200), inline = false },
            new { name = "投稿者", value = $"{Truncate(n.SubmitName, 80)} (@{n.XId})", inline = false },
            new { name = "プレー環境", value = n.Platform == "arcade" ? "アーケード" : "コナステ", inline = true },
            new { name = "合計スコア", value = n.TotalScore.ToString("N0", CultureInfo.InvariantCulture), inline = true },
            new { name = "各 Track", value = Truncate(string.Join("\n", playLines), 1024), inline = false },
        };

        object embed = summaryFileName is not null
            ? new
            {
                title = "🌙 MSC スコア投稿",
                color = 0xbb00ff,
                fields,
                image = new { url = $"attachment://{summaryFileName}" },
            }
            : new
            {
                title = "🌙 MSC スコア投稿",
                color = 0xbb00ff,
                fields,
            };

        var attachmentList = validImages.Select((img, idx) => new
        {
            id = idx,
            filename = Path.GetFileName(img.Path),
            description = img.PlayOrder == 0
                ? "通しリザルト"
                : (img.PlayOrder == 4 ? "EX Track" : $"{img.PlayOrder}{(img.PlayOrder == 1 ? "st" : img.PlayOrder == 2 ? "nd" : "rd")} Track"),
        }).ToArray();

        var payload = new
        {
            embeds = new[] { embed },
            attachments = attachmentList,
            components = new[]
            {
                new
                {
                    type = 1,
                    components = new object[]
                    {
                        new { type = 2, style = 3, label = "承認",     custom_id = $"mscscore:approve:{n.EventId}:{n.XId}:{n.SubmitId}" },
                        new { type = 2, style = 4, label = "却下",     custom_id = $"mscscore:reject:{n.EventId}:{n.XId}:{n.SubmitId}" },
                        new { type = 2, style = 1, label = "修正承認", custom_id = $"mscscore:edit:{n.EventId}:{n.XId}:{n.SubmitId}" },
                    },
                },
            },
        };

        var openStreams = new List<FileStream>();
        try
        {
            using var content = new MultipartFormDataContent();
            var json = JsonSerializer.Serialize(payload);
            content.Add(new StringContent(json, Encoding.UTF8, "application/json"), "payload_json");

            for (int i = 0; i < validImages.Count; i++)
            {
                var img = validImages[i];
                var fs = File.OpenRead(img.Path);
                openStreams.Add(fs);
                var part = new StreamContent(fs);
                if (!string.IsNullOrWhiteSpace(img.ContentType) &&
                    MediaTypeHeaderValue.TryParse(img.ContentType, out var mt))
                {
                    part.Headers.ContentType = mt;
                }
                content.Add(part, $"files[{i}]", Path.GetFileName(img.Path));
            }

            using var resp = await _http.PostAsync(
                $"channels/{_channelId}/messages", content, ct);
            await LogIfFailureAsync(resp, "msc score notify", ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Discord MSC score notify failed");
        }
        finally
        {
            foreach (var s in openStreams) s.Dispose();
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
