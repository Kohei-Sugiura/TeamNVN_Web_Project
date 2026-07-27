using System.Text.Json;

namespace Weepa.Api.Services;

public sealed class MatchingManagerState
{
    public ulong GuildId { get; set; }
    public ulong ChannelId { get; set; }
    public ulong MessageId { get; set; }
    // 開設中のマッチング部屋
    public List<MatchingRoom> Rooms { get; set; } = new();
}

public sealed class MatchingRoom
{
    public int Number { get; set; }              // 1〜9 (表示は ①〜⑨)
    public string Mode { get; set; } = "";       // 例: "ARENA MODE (NORMAL)"
    public string Password { get; set; } = "";   // 部屋番号 6 桁 (例: 部屋① → "111111")
    public ulong ChannelId { get; set; }
    public ulong RoleId { get; set; }
    public List<ulong> MemberUserIds { get; set; } = new();
}

public static class MatchingModes
{
    // (内部値, 表示名, 定員)
    public static readonly (string Value, string Display, int Capacity)[] All = new[]
    {
        ("ARENA_NORMAL",  "ARENA MODE (NORMAL)",   4),
        ("ARENA_ULT",     "ARENA MODE (ULTIMATE)", 4),
        ("SINGLE_NORMAL", "SINGLE MODE (NORMAL)",   2),
        ("SINGLE_ULT",    "SINGLE MODE (ULTIMATE)", 2),
        ("MEGAMIX",       "MEGAMIX BATTLE",         2),
    };

    public static string? ToDisplay(string value)
    {
        foreach (var (v, d, _) in All) if (v == value) return d;
        return null;
    }

    public static int CapacityOfDisplay(string display)
    {
        foreach (var (_, d, c) in All) if (d == display) return c;
        return 4; // fallback
    }
}

/// <summary>
/// オンマチ会 (マッチング管理) Bot の永続化付き状態管理。
/// uploads ボリューム上の JSON ファイルに保存。再起動後もメッセージ ID を保持して
/// 同じメッセージを編集し続けるために使う。
/// </summary>
public sealed class MatchingManagerService
{
    // /app/uploads は docker-compose の名前付きボリュームなので
    // コンテナ再ビルドや再起動でも残る
    private const string StatePath = "/app/uploads/matching-manager-state.json";

    private readonly ILogger<MatchingManagerService> _logger;
    private readonly MessageEditDebouncer _editDebouncer;
    private readonly SemaphoreSlim _fileLock = new(1, 1);
    // 入退室/作成/分割/モード変更を直列化するロック。
    // 複数ユーザーの同時操作による採番競合・二重入室・消えた部屋への操作を防ぐ
    private readonly SemaphoreSlim _opLock = new(1, 1);
    private MatchingManagerState? _state;

    public MatchingManagerService(
        ILogger<MatchingManagerService> logger,
        MessageEditDebouncer editDebouncer)
    {
        _logger = logger;
        _editDebouncer = editDebouncer;
        // 同期初期化: 起動時に 1 回だけ読む
        try
        {
            if (File.Exists(StatePath))
            {
                var json = File.ReadAllText(StatePath);
                _state = JsonSerializer.Deserialize<MatchingManagerState>(json);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "failed to load matching manager state");
        }
    }

    /// <summary>
    /// ダッシュボード (管理メッセージ) を debounce 経由で更新する。
    /// </summary>
    public void RefreshDashboard()
    {
        var s = _state;
        if (s is null || s.MessageId == 0) return;
        _editDebouncer.Schedule(s.ChannelId, s.MessageId, BuildMessagePayload);
    }

    public MatchingManagerState? GetState() => _state;

    public async Task SetStateAsync(MatchingManagerState state, CancellationToken ct = default)
    {
        await _fileLock.WaitAsync(ct);
        try
        {
            _state = state;
            await SaveUnlockedAsync(ct);
        }
        finally
        {
            _fileLock.Release();
        }
    }

    /// <summary>
    /// マッチング操作を直列化して実行する。呼び出し側の背景タスクの最外殻で 1 回だけ使う
    /// (非再入ロックのため、この中から再度呼ぶとデッドロックする)。
    /// ボタン押下時の事前チェックは UX 用でしかないので、確定判断はこの中で再検証すること。
    /// </summary>
    public async Task RunSerializedAsync(Func<Task> op, CancellationToken ct = default)
    {
        await _opLock.WaitAsync(ct);
        try { await op(); }
        finally { _opLock.Release(); }
    }

    // ========================
    // Room 管理
    // ========================

    /// <summary>1〜9 のうち未使用最小の番号を返す。全部使用中なら -1。</summary>
    public int NextRoomNumber()
    {
        var used = new HashSet<int>(_state?.Rooms.Select(r => r.Number) ?? Enumerable.Empty<int>());
        for (int i = 1; i <= 9; i++) if (!used.Contains(i)) return i;
        return -1;
    }

    /// <summary>部屋番号のゾロ目 6 桁をパスワードとして返す (部屋① → "111111")。</summary>
    public static string RoomPassword(int roomNumber) =>
        new string((char)('0' + roomNumber), 6);

    /// <summary>ユーザーが既に何らかの部屋に入室しているか確認 (1 人 1 部屋制約用)。</summary>
    public MatchingRoom? FindRoomContaining(ulong userId)
    {
        return _state?.Rooms.FirstOrDefault(r => r.MemberUserIds.Contains(userId));
    }

    public MatchingRoom? GetRoom(int number)
    {
        return _state?.Rooms.FirstOrDefault(r => r.Number == number);
    }

    public async Task AddRoomAsync(MatchingRoom room, CancellationToken ct = default)
    {
        await _fileLock.WaitAsync(ct);
        try
        {
            if (_state is null) return;
            _state.Rooms.Add(room);
            await SaveUnlockedAsync(ct);
        }
        finally { _fileLock.Release(); }
    }

    public async Task UpdateRoomAsync(int number, Action<MatchingRoom> mutate, CancellationToken ct = default)
    {
        await _fileLock.WaitAsync(ct);
        try
        {
            if (_state is null) return;
            var room = _state.Rooms.FirstOrDefault(r => r.Number == number);
            if (room is null) return;
            mutate(room);
            await SaveUnlockedAsync(ct);
        }
        finally { _fileLock.Release(); }
    }

    public async Task RemoveRoomAsync(int number, CancellationToken ct = default)
    {
        await _fileLock.WaitAsync(ct);
        try
        {
            if (_state is null) return;
            _state.Rooms.RemoveAll(r => r.Number == number);
            await SaveUnlockedAsync(ct);
        }
        finally { _fileLock.Release(); }
    }

    /// <summary>
    /// 部屋分割の活性条件チェック。
    /// - ARENA: 5 人以上
    /// - SINGLE / MEGAMIX (定員 2): 4 人以上かつ偶数
    /// - 既存部屋数 - 1 + 新部屋数 ≦ 9
    /// </summary>
    public bool CanSplitRoom(MatchingRoom room)
    {
        var n = room.MemberUserIds.Count;
        var cap = MatchingModes.CapacityOfDisplay(room.Mode);

        bool memberOk = cap switch
        {
            4 => n >= 5,
            2 => n >= 4 && n % 2 == 0,
            _ => false,
        };
        if (!memberOk) return false;

        var newRooms = (int)Math.Ceiling((double)n / cap);
        var currentRooms = _state?.Rooms.Count ?? 0;
        return (currentRooms - 1 + newRooms) <= 9;
    }

    /// <summary>
    /// 分割時のメンバー配分を計算する。N 個の部屋に M 人を「人数差 ≦ 1」で配分。
    /// 余り Extra 個の部屋を (Base+1) 人、残り (N - Extra) 個を Base 人にする。
    /// </summary>
    public static (int RoomCount, int[] PerRoomSize) ComputeSplitDistribution(int memberCount, int capacity)
    {
        var n = (int)Math.Ceiling((double)memberCount / capacity);
        var baseSize = memberCount / n;
        var extra = memberCount % n;
        var sizes = new int[n];
        for (int i = 0; i < n; i++) sizes[i] = baseSize + (i < extra ? 1 : 0);
        return (n, sizes);
    }

    public static string NumberToCircle(int n) => $"{(char)(0x2460 + n - 1)}";

    public static string FormatPassword(string pw) =>
        pw.Length == 6 ? $"{pw[..3]} {pw[3..]}" : pw;

    private async Task SaveUnlockedAsync(CancellationToken ct)
    {
        if (_state is null) return;
        var json = JsonSerializer.Serialize(_state, new JsonSerializerOptions
        {
            WriteIndented = true,
        });
        Directory.CreateDirectory(Path.GetDirectoryName(StatePath)!);
        await File.WriteAllTextAsync(StatePath, json, ct);
    }

    /// <summary>
    /// マッチング管理用メッセージ (統合ボード) の payload を構築する。
    /// 入室 / 退出 / モード変更 / 部屋分割 をすべてここから操作する。
    /// </summary>
    public object BuildMessagePayload()
    {
        var rooms = (_state?.Rooms ?? new List<MatchingRoom>())
            .OrderBy(r => r.Number)
            .ToList();

        var inner = new List<object>
        {
            new
            {
                type = 10,
                content = string.Join("\n", new[]
                {
                    "# 🎮 オンマチ会 マッチング管理",
                    "",
                    "## マッチング部屋",
                }),
            },
            // 新規マッチング部屋作成ボタンは「マッチング部屋」見出し直下に置く
            new
            {
                type = 1,
                components = new object[]
                {
                    new
                    {
                        type = 2, style = 1,
                        label = "新規マッチング部屋を作成",
                        custom_id = "matching:create-room",
                        emoji = new { name = "🆕" },
                        disabled = rooms.Count >= 9,
                    },
                },
            },
        };

        if (rooms.Count == 0)
        {
            inner.Add(new { type = 10, content = "-# 現在開設中の部屋はありません" });
        }
        else
        {
            foreach (var r in rooms)
            {
                // 部屋ごとに罫線 (1 部屋目の前にも引く → ボタン直下にも区切りが入る)
                inner.Add(new { type = 14, divider = true, spacing = 1 });
                var memberLines = r.MemberUserIds.Count > 0
                    ? string.Join("\n", r.MemberUserIds.Select(u => $"- <@{u}>"))
                    : "-# 誰も居ません";
                var capacity = MatchingModes.CapacityOfDisplay(r.Mode);
                var info = string.Join("\n", new[]
                {
                    $"### 🎮 オンマチ{NumberToCircle(r.Number)} (部屋番号 {FormatPassword(r.Password)})",
                    "**モード**",
                    $"{r.Mode}",
                    $"-# 定員: {capacity} 名",
                    "",
                    "**入室者**",
                    memberLines,
                });
                inner.Add(new { type = 10, content = info });
                inner.Add(new
                {
                    type = 1,
                    components = new object[]
                    {
                        new
                        {
                            type = 2, style = 3,
                            label = "入室",
                            custom_id = $"matching:join-room:{r.Number}",
                            emoji = new { name = "🎮" },
                        },
                        new
                        {
                            type = 2, style = 4,
                            label = "退出",
                            custom_id = $"matching:leave-room:{r.Number}",
                            emoji = new { name = "🚪" },
                        },
                        new
                        {
                            type = 2, style = 1,
                            label = "モード変更",
                            custom_id = $"matching:change-mode:{r.Number}",
                            emoji = new { name = "🔁" },
                        },
                        new
                        {
                            type = 2, style = 2,
                            label = "部屋分割",
                            custom_id = $"matching:split-room:{r.Number}",
                            emoji = new { name = "✂" },
                            disabled = !CanSplitRoom(r),
                        },
                    },
                });
            }
        }

        return new
        {
            flags = 32768,
            allowed_mentions = new { parse = Array.Empty<string>() },
            components = new object[]
            {
                new
                {
                    type = 17,
                    accent_color = 0x00e5ff,
                    components = inner,
                },
            },
        };
    }
}
