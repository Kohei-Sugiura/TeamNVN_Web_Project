using PuppeteerSharp;

namespace Weepa.Api.Services;

/// <summary>
/// Puppeteer (Chromium) を使ってランキング画面を PNG 化するサービス。
/// 内部ポート (http://nginx:8081) 経由でランキングページを開いてスクショする。
/// 未公開ランキングの API は管理者認証が必要なため、ページのリクエストに
/// 管理者 Basic 認証ヘッダーを付けて開く (公開前でも画像化できる)。
/// </summary>
public sealed class RankingScreenshot : IAsyncDisposable
{
    private readonly ILogger<RankingScreenshot> _logger;
    private readonly IConfiguration _configuration;
    private readonly SemaphoreSlim _launchLock = new(1, 1);
    private IBrowser? _browser;

    public RankingScreenshot(ILogger<RankingScreenshot> logger, IConfiguration configuration)
    {
        _logger = logger;
        _configuration = configuration;
    }

    private async Task<IBrowser> GetBrowserAsync()
    {
        if (_browser is { IsClosed: false }) return _browser;
        await _launchLock.WaitAsync();
        try
        {
            if (_browser is { IsClosed: false }) return _browser;
            var execPath = Environment.GetEnvironmentVariable("PUPPETEER_EXECUTABLE_PATH")
                ?? "/usr/bin/chromium";
            _browser = await Puppeteer.LaunchAsync(new LaunchOptions
            {
                Headless = true,
                ExecutablePath = execPath,
                Args = new[]
                {
                    "--no-sandbox",
                    "--disable-dev-shm-usage",
                    "--disable-gpu",
                    "--font-render-hinting=none",
                },
            });
            return _browser;
        }
        finally
        {
            _launchLock.Release();
        }
    }

    // tier (1-3) 指定時はその部門のみのビュー (?tier=N) を撮る
    public Task<byte[]> CaptureRankingAsync(int eventId, int? tier = null, CancellationToken ct = default) =>
        CaptureAsync($"/weepa/ranking/?eventId={eventId}" + (tier is int t ? $"&tier={t}" : ""), ct);

    public Task<byte[]> CaptureMscRankingAsync(int eventId, CancellationToken ct = default) =>
        CaptureAsync($"/msc/ranking/?eventId={eventId}", ct);

    private async Task<byte[]> CaptureAsync(string path, CancellationToken ct)
    {
        var browser = await GetBrowserAsync();
        var page = await browser.NewPageAsync();
        try
        {
            // 未公開ランキング API 用に管理者 Basic 認証を付与するが、
            // 内部 (http://nginx:8081) 宛のリクエストにだけ付ける。
            // SetExtraHttpHeaders で全リクエストに付けると Google Fonts
            // (fonts.gstatic.com) が Authorization 付きで 404 になり、Web
            // フォントがロードできず別フォントで描画されてしまうため
            // (= スクショだけ見た目が変わる不具合)。
            var auth = Weepa.Api.Validation.AdminBasicAuth.BuildHeaderValue(_configuration);
            if (auth is not null)
            {
                await page.SetRequestInterceptionAsync(true);
                page.Request += async (_, e) =>
                {
                    try
                    {
                        if (e.Request.Url.StartsWith("http://nginx:8081", StringComparison.OrdinalIgnoreCase))
                        {
                            var headers = new Dictionary<string, string>(e.Request.Headers)
                            {
                                ["Authorization"] = auth,
                            };
                            await e.Request.ContinueAsync(new Payload { Headers = headers });
                        }
                        else
                        {
                            await e.Request.ContinueAsync();
                        }
                    }
                    catch
                    {
                        // 競合で既に処理済み等は無視
                    }
                };
            }

            // MSC ランキングは曲数によって幅が変わるので余裕を持って広めに。
            // .screenshot-mode で width: max-content にすると、コンテナは内容に
            // ぴったり収まるサイズで描画される (viewport は上限としてだけ機能)。
            await page.SetViewportAsync(new ViewPortOptions
            {
                Width = 2200,
                Height = 1200,
                DeviceScaleFactor = 2,
            });

            // 内部用 nginx ポート (HTTPS リダイレクトせず、認証無し)
            var url = $"http://nginx:8081{path}";
            await page.GoToAsync(url, new NavigationOptions
            {
                WaitUntil = new[] { WaitUntilNavigation.Networkidle0 },
                Timeout = 20_000,
                // 新しい Chromium は referrerPolicy に文字列バリデーションが厳しいので明示
                ReferrerPolicy = "noReferrer",
            });

            // ランキング描画完了 (table もしくは empty メッセージ) を待つ
            await page.WaitForSelectorAsync(
                ".ranking-detail-table, .ranking-section .empty",
                new WaitForSelectorOptions { Timeout = 10_000 });

            // Web フォントがロードされるまで少し待つ
            await page.EvaluateExpressionAsync("document.fonts && document.fonts.ready");

            // viewport 制約を外してコンテナを内容ぴったりに広げる
            await page.EvaluateExpressionAsync(
                "document.body.classList.add('screenshot-mode')");

            // .container 全体をキャプチャ。要素スクショなので viewport 高さを超える
            // 縦長エントリでも element の bounding box まで自動で伸びる。
            var element = await page.QuerySelectorAsync(".container");
            byte[] bytes = element is null
                ? await page.ScreenshotDataAsync(new ScreenshotOptions { Type = ScreenshotType.Png, FullPage = true })
                : await element.ScreenshotDataAsync(new ElementScreenshotOptions { Type = ScreenshotType.Png });
            return bytes;
        }
        finally
        {
            await page.CloseAsync();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_browser is { IsClosed: false })
        {
            try { await _browser.CloseAsync(); }
            catch (Exception ex) { _logger.LogWarning(ex, "browser close failed"); }
        }
        _launchLock.Dispose();
    }
}
