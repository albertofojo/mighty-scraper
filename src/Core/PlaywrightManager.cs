using Microsoft.Extensions.Logging;
using Microsoft.Playwright;

namespace MightyScraper.Core;

public sealed class PlaywrightManager(ILogger<PlaywrightManager> logger) : IPlaywrightManager
{
    // Same directory the LoginHelper wrote the persistent browser profile to.
    // Using the full profile (not just state.json) avoids losing IndexedDB / service-worker
    // session data that Google SSO stores outside of cookies and localStorage.
    private static readonly string AuthDir = "auth";

    private IPlaywright?     _playwright;
    private IBrowserContext? _context;   // LaunchPersistentContextAsync returns the context directly
    private IPage?           _page;

    public async Task<IPage> GetAuthenticatedPageAsync()
    {
        if (_page is not null)
            return _page;

        await InitAsync();
        _page = await _context!.NewPageAsync();
        return _page;
    }

    private async Task InitAsync()
    {
        var absoluteAuthDir = Path.GetFullPath(AuthDir);

        if (!Directory.Exists(absoluteAuthDir))
            throw new InvalidOperationException(
                $"Auth profile directory not found at '{absoluteAuthDir}'. " +
                "Run the login helper first: dotnet run --project tools/LoginHelper -- <url>");

        logger.LogInformation("Launching headless Chromium using persistent profile at '{Dir}'", absoluteAuthDir);

        _playwright = await Playwright.CreateAsync();
        _context = await _playwright.Chromium.LaunchPersistentContextAsync(absoluteAuthDir, new()
        {
            Headless          = true,
            Args              = ["--disable-blink-features=AutomationControlled"],
            IgnoreDefaultArgs = ["--enable-automation"],
        });
    }

    // IAsyncDisposable — no IBrowser with persistent context; dispose order: page → context → playwright
    public async ValueTask DisposeAsync()
    {
        if (_page is not null)
        {
            await _page.CloseAsync();
            _page = null;
        }

        if (_context is not null)
        {
            await _context.DisposeAsync();
            _context = null;
        }

        _playwright?.Dispose();
        _playwright = null;
    }
}
