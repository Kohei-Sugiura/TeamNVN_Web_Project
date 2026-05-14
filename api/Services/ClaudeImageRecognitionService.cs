using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Weepa.Api.Services;

public sealed record ScoreRecognitionResult(bool Success, int? Score, int? ExScore, string? Error);

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
        if (!Enabled)
        {
            return new ScoreRecognitionResult(false, null, null,
                "画像認識機能が無効です (ANTHROPIC_API_KEY 未設定)");
        }

        var url = _config["Anthropic:ApiUrl"] ?? "https://api.anthropic.com/v1/messages";
        var model = _config["Anthropic:Model"] ?? "claude-haiku-4-5-20251001";
        var maxTokens = _config.GetValue<int?>("Anthropic:MaxTokens") ?? 256;
        var systemPrompt = _config["Anthropic:SystemPrompt"] ?? "";
        var userPrompt = _config["Anthropic:UserPrompt"] ?? "";

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
                    return new ScoreRecognitionResult(false, null, null,
                        $"画像認識 API でエラーが発生しました ({(int)resp.StatusCode})");
                }

                return ParseResponse(body);
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

        return new ScoreRecognitionResult(false, null, null,
            $"画像認識 API への接続に失敗しました: {lastEx?.Message ?? "unknown"}");
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
