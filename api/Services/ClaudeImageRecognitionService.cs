using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Processing;

namespace Weepa.Api.Services;

public sealed record ScoreRecognitionResult(bool Success, int? Score, int? ExScore, string? Error);

/// <summary>EX SCORE MAX 値推定の結果。Success=true で ExScore/MaxDiff のどちらかが null の場合は「推定不能」。</summary>
public sealed record ExMaxEstimateResult(bool Success, int? ExScore, int? MaxDiff, string? Error)
{
    public int? EstimatedMax => (ExScore.HasValue && MaxDiff.HasValue) ? ExScore + MaxDiff : null;
}

/// <summary>
/// Claude (Anthropic) vision API でリザルト画像から SCORE / EX SCORE を抽出する。
/// 失敗・タイムアウト・JSON パース不能はすべて Result の Success=false で表す。
/// </summary>
public sealed class ClaudeImageRecognitionService
{
    private readonly HttpClient _http;
    private readonly IConfiguration _config;
    private readonly ILogger<ClaudeImageRecognitionService> _logger;
    private readonly string? _apiKey;
    public bool Enabled { get; }

    public ClaudeImageRecognitionService(
        IHttpClientFactory factory,
        IConfiguration config,
        ILogger<ClaudeImageRecognitionService> logger)
    {
        _config = config;
        _logger = logger;
        _apiKey = config["ANTHROPIC_API_KEY"];
        Enabled = !string.IsNullOrWhiteSpace(_apiKey);

        _http = factory.CreateClient("anthropic");
        var timeout = config.GetValue<int?>("Anthropic:TimeoutSeconds") ?? 30;
        _http.Timeout = TimeSpan.FromSeconds(timeout);

        if (Enabled)
        {
            // x-api-key ヘッダはリクエスト毎に付けるが、共通の version ヘッダはここで設定
            var version = config["Anthropic:ApiVersion"] ?? "2023-06-01";
            _http.DefaultRequestHeaders.Add("anthropic-version", version);
            _http.DefaultRequestHeaders.UserAgent.ParseAdd(
                "WeePA-API (https://team-nvn-server.com)");
        }
    }

    public async Task<ScoreRecognitionResult> RecognizeAsync(
        byte[] imageBytes,
        string contentType,
        CancellationToken ct)
    {
        var systemPrompt = _config["Anthropic:SystemPrompt"] ?? "";
        var userPrompt = _config["Anthropic:UserPrompt"] ?? "";
        return await CallClaudeAsync(imageBytes, contentType, systemPrompt, userPrompt, ParseResponse, ct);
    }

    public async Task<ExMaxEstimateResult> EstimateExMaxAsync(
        byte[] imageBytes,
        string contentType,
        CancellationToken ct)
    {
        if (!Enabled)
        {
            return new ExMaxEstimateResult(false, null, null,
                "画像認識機能が無効です (ANTHROPIC_API_KEY 未設定)");
        }

        // Anthropic API は base64 で 5MB 上限。スマホ撮影画像 (10MB 超) は弾かれるので
        // 長辺 1600px / JPEG 0.85 にリサイズして送る (認識精度には十分)。
        try
        {
            (imageBytes, contentType) = await ShrinkIfNeededAsync(imageBytes, contentType, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "shrink image for ex-max estimate failed");
        }

        var systemPrompt = _config["Anthropic:ExMaxSystemPrompt"] ?? "";
        var userPrompt = _config["Anthropic:ExMaxUserPrompt"] ?? "";

        // ScoreRecognitionResult をラッパーとして借用しつつ Body だけ別パースさせるため、
        // 内部で素の JSON body をハンドリングする小さな ParseExMax を渡す
        var result = await CallClaudeAsync<ExMaxEstimateResult>(
            imageBytes, contentType, systemPrompt, userPrompt,
            body => ParseExMaxResponse(body),
            ct,
            errorFactory: e => new ExMaxEstimateResult(false, null, null, e));
        return result;
    }

    private async Task<ScoreRecognitionResult> CallClaudeAsync(
        byte[] imageBytes, string contentType,
        string systemPrompt, string userPrompt,
        Func<string, ScoreRecognitionResult> parser,
        CancellationToken ct)
        => await CallClaudeAsync<ScoreRecognitionResult>(
            imageBytes, contentType, systemPrompt, userPrompt, parser, ct,
            errorFactory: e => new ScoreRecognitionResult(false, null, null, e));

    /// <summary>
    /// Claude vision API への共通呼び出し。レスポンス body を parser に渡してドメイン固有の
    /// 結果型を返す。失敗時は errorFactory で同型の結果を作る。
    /// </summary>
    private async Task<T> CallClaudeAsync<T>(
        byte[] imageBytes, string contentType,
        string systemPrompt, string userPrompt,
        Func<string, T> parser,
        CancellationToken ct,
        Func<string, T> errorFactory)
    {
        if (!Enabled)
        {
            return errorFactory("画像認識機能が無効です (ANTHROPIC_API_KEY 未設定)");
        }

        var url = _config["Anthropic:ApiUrl"] ?? "https://api.anthropic.com/v1/messages";
        var model = _config["Anthropic:Model"] ?? "claude-haiku-4-5-20251001";
        var maxTokens = _config.GetValue<int?>("Anthropic:MaxTokens") ?? 256;

        var content = new List<object>
        {
            new
            {
                type = "image",
                source = new
                {
                    type = "base64",
                    media_type = contentType,
                    data = Convert.ToBase64String(imageBytes),
                },
            },
        };
        if (!string.IsNullOrWhiteSpace(userPrompt))
        {
            content.Add(new { type = "text", text = userPrompt });
        }

        var payload = new
        {
            model,
            max_tokens = maxTokens,
            temperature = 0,
            system = systemPrompt,
            messages = new object[]
            {
                new { role = "user", content },
            },
        };

        var json = JsonSerializer.Serialize(payload);

        // 5xx / ネットワークエラーは指数バックオフでリトライ
        var delays = new[] { 1000, 2000, 4000 };
        Exception? lastEx = null;
        for (int attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Post, url);
                req.Headers.Add("x-api-key", _apiKey);
                req.Content = new StringContent(json, Encoding.UTF8, "application/json");

                using var resp = await _http.SendAsync(req, ct);
                var body = await resp.Content.ReadAsStringAsync(ct);

                if ((int)resp.StatusCode >= 500 || resp.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
                {
                    _logger.LogWarning(
                        "Anthropic API transient {Status} (attempt {Attempt}): {Body}",
                        (int)resp.StatusCode, attempt + 1, Truncate(body, 500));
                    if (attempt < 2) await Task.Delay(delays[attempt], ct);
                    continue;
                }

                if (!resp.IsSuccessStatusCode)
                {
                    _logger.LogWarning(
                        "Anthropic API error {Status}: {Body}",
                        (int)resp.StatusCode, Truncate(body, 500));
                    return errorFactory($"画像認識 API でエラーが発生しました ({(int)resp.StatusCode})");
                }

                return parser(body);
            }
            catch (TaskCanceledException) when (!ct.IsCancellationRequested)
            {
                lastEx = new TimeoutException("Anthropic API タイムアウト");
                _logger.LogWarning("Anthropic API timeout (attempt {Attempt})", attempt + 1);
                if (attempt < 2) await Task.Delay(delays[attempt], ct);
            }
            catch (HttpRequestException ex)
            {
                lastEx = ex;
                _logger.LogWarning(ex, "Anthropic API HTTP error (attempt {Attempt})", attempt + 1);
                if (attempt < 2) await Task.Delay(delays[attempt], ct);
            }
        }

        return errorFactory($"画像認識 API への接続に失敗しました: {lastEx?.Message ?? "unknown"}");
    }

    private ExMaxEstimateResult ParseExMaxResponse(string body)
    {
        string? text;
        try
        {
            using var doc = JsonDocument.Parse(body);
            var content = doc.RootElement.GetProperty("content");
            if (content.ValueKind != JsonValueKind.Array || content.GetArrayLength() == 0)
                return new ExMaxEstimateResult(false, null, null, "画像認識結果が空でした");
            text = content[0].GetProperty("text").GetString();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Anthropic ex-max response shape unexpected: {Body}", Truncate(body, 500));
            return new ExMaxEstimateResult(false, null, null, "画像認識結果のパースに失敗しました");
        }

        if (string.IsNullOrWhiteSpace(text))
            return new ExMaxEstimateResult(false, null, null, "画像認識結果が空でした");

        var cleaned = StripCodeFence(text).Trim();
        try
        {
            using var doc = JsonDocument.Parse(cleaned);
            var root = doc.RootElement;
            var ex = TryReadNullableInt(root, "ex_score");
            var diff = TryReadNullableInt(root, "max_diff");
            // 値域チェック
            if (ex is < 0 or > 100_000) ex = null;
            if (diff is < 0 or > 100_000) diff = null;
            return new ExMaxEstimateResult(true, ex, diff, null);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "ex-max JSON parse failed: {Text}", Truncate(cleaned, 200));
            return new ExMaxEstimateResult(false, null, null, "推定結果の JSON パースに失敗しました");
        }
    }

    private ScoreRecognitionResult ParseResponse(string body)
    {
        string? text;
        long? inputTokens = null;
        long? outputTokens = null;
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (root.TryGetProperty("usage", out var usage))
            {
                if (usage.TryGetProperty("input_tokens", out var it)) inputTokens = it.GetInt64();
                if (usage.TryGetProperty("output_tokens", out var ot)) outputTokens = ot.GetInt64();
            }

            var content = root.GetProperty("content");
            if (content.ValueKind != JsonValueKind.Array || content.GetArrayLength() == 0)
            {
                return new ScoreRecognitionResult(false, null, null, "画像認識結果が空でした");
            }
            text = content[0].GetProperty("text").GetString();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Anthropic response shape unexpected: {Body}", Truncate(body, 500));
            return new ScoreRecognitionResult(false, null, null, "画像認識結果のパースに失敗しました");
        }

        _logger.LogInformation(
            "Anthropic recognize: input={Input} output={Output} text={Text}",
            inputTokens, outputTokens, Truncate(text ?? "", 200));

        if (string.IsNullOrWhiteSpace(text))
        {
            return new ScoreRecognitionResult(false, null, null, "画像認識結果が空でした");
        }

        // コードブロック (```json ... ``` または ``` ... ```) を除去
        var cleaned = StripCodeFence(text).Trim();

        try
        {
            using var doc = JsonDocument.Parse(cleaned);
            var root = doc.RootElement;
            int? score = TryReadNullableInt(root, "score");
            int? ex    = TryReadNullableInt(root, "ex_score");

            // 値域チェック
            if (score is < 0 or > 10_000_000) score = null;
            if (ex    is < 0 or > 100_000)    ex = null;

            return new ScoreRecognitionResult(true, score, ex, null);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "JSON parse failed: {Text}", Truncate(cleaned, 200));
            return new ScoreRecognitionResult(false, null, null, "画像認識結果の JSON パースに失敗しました");
        }
    }

    private static int? TryReadNullableInt(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var v)) return null;
        if (v.ValueKind == JsonValueKind.Null) return null;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i)) return i;
        if (v.ValueKind == JsonValueKind.String &&
            int.TryParse(v.GetString(), out var s)) return s;
        return null;
    }

    /// <summary>
    /// 4MB 超または長辺 1600px 超の画像を長辺 1600px / JPEG 0.85 にリサイズして返す。
    /// Anthropic vision API の 5MB 制限を回避するため。
    /// </summary>
    private static async Task<(byte[] Bytes, string ContentType)> ShrinkIfNeededAsync(
        byte[] input, string contentType, CancellationToken ct)
    {
        // 4MB 未満なら何もしない (Anthropic 5MB 制限に十分余裕)
        if (input.Length < 4 * 1024 * 1024) return (input, contentType);

        using var image = Image.Load(input);
        const int maxDim = 1600;
        if (image.Width > maxDim || image.Height > maxDim)
        {
            image.Mutate(x => x.Resize(new ResizeOptions
            {
                Mode = ResizeMode.Max,
                Size = new SixLabors.ImageSharp.Size(maxDim, maxDim),
            }));
        }
        await using var ms = new MemoryStream();
        await image.SaveAsJpegAsync(ms, new JpegEncoder { Quality = 85 }, ct);
        return (ms.ToArray(), "image/jpeg");
    }

    private static readonly Regex CodeFenceRegex = new(
        @"```(?:json)?\s*([\s\S]*?)\s*```",
        RegexOptions.Compiled);

    private static string StripCodeFence(string text)
    {
        var m = CodeFenceRegex.Match(text);
        return m.Success ? m.Groups[1].Value : text;
    }

    private static string Truncate(string s, int max) =>
        s.Length <= max ? s : s.Substring(0, max) + "…";
}
