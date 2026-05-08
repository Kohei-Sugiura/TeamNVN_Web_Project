using PuppeteerSharp;

namespace Weepa.Api.Services;

/// <summary>
/// Puppeteer (Chromium) を使ってランキング画面を PNG 化するサービス。
/// 内部ポート (http://nginx:8081) 経由でランキングページを開いてスクショする。
/// 公開/非公開どちらの集計結果でも eventId 直指定なら表示できる仕様を利用。
/// </summary>
public sealed class RankingScreenshot : IAsyncDisposable
{
    private readonly ILogger<RankingScreenshot> _logger;
    private readonly SemaphoreSlim _launchLock = new(1, 1);
    private IBrowser? _browser;

    public RankingScreenshot(ILogger<RankingScreenshot> logger)
    {
        _logger = logger;
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

    public async Task<byte[]> CaptureRankingAsync(int eventId, CancellationToken ct = default)
    {
        var browser = await GetBrowserAsync();
        var page = await browser.NewPageAsync();
        try
        {
            await page.SetViewportAsync(new ViewPortOptions
            {
                Width = 900,
                Height = 1200,
                DeviceScaleFactor = 2,
            });

            // 内部用 nginx ポート (HTTPS リダイレクトせず、認証無し)
            var url = $"http://nginx:8081/weepa/ranking/?eventId={eventId}";
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
