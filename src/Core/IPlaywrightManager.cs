using Microsoft.Playwright;

namespace MightyScraper.Core;

public interface IPlaywrightManager : IAsyncDisposable
{
    Task<IPage> GetAuthenticatedPageAsync();
}
