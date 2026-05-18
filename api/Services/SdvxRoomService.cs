using System.Collections.Concurrent;

namespace Weepa.Api.Services;

public enum SdvxRoomStatus
{
    Finished = 0, // 終了 (初期状態 = 未開始 もこれを使う)
    Matching = 1, // マッチング中
    Song1 = 2,    // 選曲中〜1曲目プレー中
    Song2 = 3,    // 2曲目プレー中
    Song3 = 4,    // 3曲目プレー中
    Song4 = 5,    // 4曲目プレー中
}

public sealed class SdvxRoomState
{
    public required ulong MessageId { get; init; }
    public required ulong ChannelId { get; init; }
    public required string Name { get; set; }
    public SdvxRoomStatus Status { get; set; } = SdvxRoomStatus.Finished;
    public DateTimeOffset? PlayStartUtc { get; set; }
}

/// <summary>
/// SDVX マッチング掲示板 Bot の部屋状態管理 (メモリのみ、再起動でリセット)。
/// </summary>
public sealed class SdvxRoomService
{
    private readonly ConcurrentDictionary<ulong, SdvxRoomState> _rooms = new();

    public SdvxRoomState Register(ulong messageId, ulong channelId, string name)
    {
        var state = new SdvxRoomState
        {
            MessageId = messageId,
            ChannelId = channelId,
            Name = name,
        };
        _rooms[messageId] = state;
        return state;
    }

    public bool Remove(ulong messageId, out SdvxRoomState? removed)
    {
        var ok = _rooms.TryRemove(messageId, out var r);
        removed = r;
        return ok;
    }

    public SdvxRoomState? Get(ulong messageId)
    {
        return _rooms.TryGetValue(messageId, out var r) ? r : null;
    }

    public static bool IsPlayingStatus(SdvxRoomStatus s) =>
        s is SdvxRoomStatus.Song1 or SdvxRoomStatus.Song2
          or SdvxRoomStatus.Song3 or SdvxRoomStatus.Song4;

    public IReadOnlyList<SdvxRoomState> GetPlaying()
    {
        return _rooms.Values
            .Where(r => IsPlayingStatus(r.Status))
            .ToList();
    }

    /// <summary>
    /// メッセージ表示用 Embed + ボタンの payload を構築する。
    /// "playing" 状態のみ開始時刻と経過時間を出す。
    /// </summary>
    public object BuildMessagePayload(SdvxRoomState s)
    {
        string statusLabel = s.Status switch
        {
            SdvxRoomStatus.Finished => "🔵 終了/空き",
            SdvxRoomStatus.Matching => "🟡 マッチング中",
            SdvxRoomStatus.Song1 => "🟢 選曲中〜1曲目プレー中",
            SdvxRoomStatus.Song2 => "🟢 2曲目プレー中",
            SdvxRoomStatus.Song3 => "🟢 3曲目プレー中",
            SdvxRoomStatus.Song4 => "🟢 4曲目プレー中",
            _ => "?",
        };

        // 1〜4曲目は全部緑、マッチング中は黄色、終了 (= 初期含む) は水色
        int color = s.Status switch
        {
            SdvxRoomStatus.Finished => 0x00e5ff,
            SdvxRoomStatus.Matching => 0xffd23d,
            _ when IsPlayingStatus(s.Status) => 0x4ddc7c,
            _ => 0x888888,
        };

        // テキストはまとめて 1 つの Text Display に流す (改行で区切る)
        // - ステータス / 開始時間: heading 3 で大きめに見せる
        // - 部屋管理 ID: -# で小さい subtext にして目立たなく
        var lines = new List<string>
        {
            $"## 🎮 {s.Name}",
            "",
            $"### {statusLabel}",
        };

        if (IsPlayingStatus(s.Status) && s.PlayStartUtc.HasValue)
        {
            var startJst = s.PlayStartUtc.Value.ToOffset(TimeSpan.FromHours(9));
            var elapsed = DateTimeOffset.UtcNow - s.PlayStartUtc.Value;
            var elapsedMin = (int)Math.Floor(elapsed.TotalMinutes);
            var elapsedSec = (int)(elapsed.TotalSeconds - elapsedMin * 60);
            string elapsedStr = elapsedMin >= 1
                ? $"{elapsedMin}分{elapsedSec:D2}秒経過"
                : $"{elapsedSec}秒経過";
            lines.Add($"### ⏱️ {startJst:HH:mm} ({elapsedStr})");
        }

        if (s.MessageId != 0)
        {
            lines.Add("");
            lines.Add($"-# ID: `{s.MessageId}`");
        }

        // Components V2: flags = IS_COMPONENTS_V2 (1 << 15 = 32768) で
        // Container (type 17) の中に Text Display + Separator + Action Row を入れて
        // 全体を 1 つの枠で囲む。embeds は使わない。
        return new
        {
            flags = 32768,
            components = new object[]
            {
                new
                {
                    type = 17, // CONTAINER
                    accent_color = color,
                    components = new object[]
                    {
                        new
                        {
                            type = 10, // TEXT_DISPLAY
                            content = string.Join("\n", lines),
                        },
                        new
                        {
                            type = 14, // SEPARATOR
                            divider = true,
                            spacing = 1,
                        },
                        new
                        {
                            type = 10, // TEXT_DISPLAY (ドロップダウンへの誘導文)
                            content = "### 🔄 状態を変更",
                        },
                        new
                        {
                            type = 1, // ACTION_ROW
                            components = new object[]
                            {
                                new
                                {
                                    type = 3, // STRING_SELECT
                                    custom_id = "sdvxroom:status",
                                    placeholder = "▼ ここをクリック",
                                    min_values = 1,
                                    max_values = 1,
                                    options = new object[]
                                    {
                                        new { label = "終了/空き",   value = "end",   emoji = new { name = "🔵" } },
                                        new { label = "マッチング中", value = "match", emoji = new { name = "🟡" } },
                                        new { label = "1曲目",       value = "s1",    emoji = new { name = "🟢" } },
                                        new { label = "2曲目",       value = "s2",    emoji = new { name = "🟢" } },
                                        new { label = "3曲目",       value = "s3",    emoji = new { name = "🟢" } },
                                        new { label = "4曲目",       value = "s4",    emoji = new { name = "🟢" } },
                                    },
                                },
                            },
                        },
                    },
                },
            },
        };
    }
}
