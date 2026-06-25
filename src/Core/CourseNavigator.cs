using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using Microsoft.Playwright;
using MightyScraper.Models;

namespace MightyScraper.Core;

public sealed class CourseNavigator(
    IPlaywrightManager playwrightManager,
    ILogger<CourseNavigator> logger) : ICourseNavigator
{
    private const string SidebarSelector    = "a[class*='coursework-mighty-menu-list-item']";
    private const string ContentSelector    = ".detail-layout-description";
    // The React sidebar renders progressively; large courses (180+ lessons) can take well over 8 s to paint.
    private const int    SidebarTimeoutMs   = 30_000;
    // Polite delay between lesson requests — prevents rate-limiting after ~90 rapid hits

    // ── ICourseNavigator ─────────────────────────────────────────────────────────

    public async Task<IReadOnlyList<LessonMeta>> GetStructureAsync(
        string courseUrl,
        CancellationToken cancellationToken = default)
    {
        var page = await playwrightManager.GetAuthenticatedPageAsync();

        await page.GotoAsync(courseUrl);
        await page.WaitForLoadStateAsync(LoadState.DOMContentLoaded);

        var landedUrl = page.Url;
        logger.LogInformation("Landed at: {Url}", landedUrl);

        var isLoginPage = landedUrl.Contains("/sign_in", StringComparison.OrdinalIgnoreCase)
                       || landedUrl.Contains("/login",   StringComparison.OrdinalIgnoreCase)
                       || !landedUrl.Contains("training.lidr.co", StringComparison.OrdinalIgnoreCase);

        if (isLoginPage)
            throw new InvalidOperationException(
                $"Session expired — redirected to '{landedUrl}'. " +
                "Run the login helper again: dotnet run --project tools/LoginHelper -- <url>");

        var links = await DiscoverLessonsAsync(page);

        logger.LogInformation("Discovered {Count} lessons in sidebar", links.Length);

        return links
            .Select(l => new LessonMeta(l.Href, l.Title, l.Index, l.ParentHref, l.IsSection))
            .ToArray();
    }

    public async IAsyncEnumerable<LessonContent> ScrapeAsync(
        IReadOnlyList<LessonMeta> structure,
        int delayMs = 3_000,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var page = await playwrightManager.GetAuthenticatedPageAsync();
        var first = true;

        foreach (var lesson in structure)
        {
            if (cancellationToken.IsCancellationRequested) yield break;

            // Polite gap between requests — skip before the very first lesson
            if (!first && delayMs > 0)
                await Task.Delay(delayMs, cancellationToken);
            first = false;

            var content = await TryExtractAsync(page, lesson);
            if (content is not null)
                yield return content;
        }
    }

    // ── Private helpers ──────────────────────────────────────────────────────────

    private async Task<LessonLink[]> DiscoverLessonsAsync(IPage page)
    {
        try
        {
            await page.Locator(SidebarSelector).First.WaitForAsync(new() { Timeout = SidebarTimeoutMs });
        }
        catch (Exception)
        {
            await LogDomDiagnosticAsync(page);
            throw new InvalidOperationException(
                $"Sidebar selector '{SidebarSelector}' not found after {SidebarTimeoutMs / 1000} s. " +
                "Check the log output above for candidate selectors, " +
                "then update SidebarSelector in CourseNavigator.cs.");
        }

        return await page.EvaluateAsync<LessonLink[]>("""
            () => {
                const result = [];
                let sectionHref = null;
                let idx = 0;
                for (const li of document.querySelectorAll('li.menu-list-item-container')) {
                    const a = li.querySelector('a[class*="coursework-mighty-menu-list-item"]');
                    if (!a || !a.href.trim()) continue;
                    const isLevel2  = li.classList.contains('level-2');
                    const isSection = li.classList.contains('is-section') || li.classList.contains('level-0');
                    if (!isLevel2) sectionHref = a.href;
                    result.push({
                        href:       a.href,
                        title:      a.innerText.trim(),
                        parentHref: isLevel2 ? sectionHref : null,
                        isSection:  isSection,
                        index:      idx++
                    });
                }
                return result;
            }
            """);
    }

    private async Task LogDomDiagnosticAsync(IPage page)
    {
        logger.LogWarning("=== DOM diagnostic — sidebar selector '{S}' not found ===", SidebarSelector);
        logger.LogWarning("Page title : {T}", await page.TitleAsync());
        logger.LogWarning("Page URL   : {U}", page.Url);

        var parentClasses = await page.EvaluateAsync<string[]>("""
            () => {
                const seen = new Set();
                return [...document.querySelectorAll('a[href]')]
                    .map(a => {
                        const p = a.closest('[class]');
                        return p ? p.className.trim() : '(no parent class)';
                    })
                    .filter(c => !seen.has(c) && seen.add(c))
                    .slice(0, 20);
            }
            """);

        logger.LogWarning("Distinct parent-class values of <a> elements ({Count}):", parentClasses.Length);
        foreach (var cls in parentClasses)
            logger.LogWarning("  class=\"{Cls}\"", cls);

        var candidates = new[]
        {
            "[class*='sidebar'] a", "[class*='drawer'] a", "[class*='nav'] a",
            "[class*='menu'] a",    "[class*='content-list'] a", "nav a", "aside a",
        };

        logger.LogWarning("Candidate selector match counts:");
        foreach (var sel in candidates)
        {
            var count = await page.Locator(sel).CountAsync();
            if (count > 0)
                logger.LogWarning("  {Count,3} matches → {Sel}", count, sel);
        }

        // Dump the real structure of a few sidebar items so we can fix the selector precisely.
        var menuSample = await page.EvaluateAsync<string[]>("""
            () => {
                const out = [];
                const containers = document.querySelectorAll('li.menu-list-item-container');
                out.push('li.menu-list-item-container count: ' + containers.length);
                let shown = 0;
                for (const li of containers) {
                    if (shown >= 4) break;
                    const a = li.querySelector('a');
                    out.push('--- item ---');
                    out.push('li.class = ' + li.className);
                    out.push('anchor   = ' + (a ? (a.tagName + ' | class=' + a.className + ' | href=' + (a.getAttribute('href') || '(none)')) : 'NO <a> INSIDE'));
                    out.push('html     = ' + li.outerHTML.replace(/\s+/g, ' ').slice(0, 500));
                    shown++;
                }
                const token = document.querySelector("[class*='coursework-mighty-menu-list-item']");
                out.push("first [class*='coursework-mighty-menu-list-item'] = " + (token ? token.tagName + ' | class=' + token.className : 'NONE'));
                return out;
            }
            """);
        logger.LogWarning("Sidebar item structure sample:");
        foreach (var line in menuSample)
            logger.LogWarning("  {Line}", line);

        logger.LogWarning("=== end of DOM diagnostic ===");
    }

    private async Task<LessonContent?> TryExtractAsync(IPage page, LessonMeta lesson)
    {
        // Navigation failure → hard skip (can't recover without the page)
        try
        {
            await page.GotoAsync(lesson.Href);
            await page.WaitForLoadStateAsync(LoadState.DOMContentLoaded);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "[{Idx:D2}] Navigation failed for '{Title}' — skipping", lesson.Order + 1, lesson.Title);
            return null;
        }

        // Session check — if the server redirected us to login mid-run, abort now
        var landedUrl = page.Url;
        if (landedUrl.Contains("/sign_in", StringComparison.OrdinalIgnoreCase)
            || landedUrl.Contains("/login",   StringComparison.OrdinalIgnoreCase)
            || !landedUrl.Contains("training.lidr.co", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Session expired at lesson {lesson.Order + 1} — redirected to '{landedUrl}'. " +
                "Run the login helper again: dotnet run --project tools/LoginHelper -- <url>");
        }

        // Content selector miss → soft fail: save a stub so cross-links still work
        var html = "";
        try
        {
            await page.Locator(ContentSelector).First.WaitForAsync(new() { Timeout = 20_000 });
            html = await page.Locator(ContentSelector).First.InnerHTMLAsync();
        }
        catch (Exception)
        {
            logger.LogWarning("[{Idx:D2}] Content not found for '{Title}' — saving stub", lesson.Order + 1, lesson.Title);
        }

        logger.LogInformation("[{Idx:D2}] Scraped: {Title}", lesson.Order + 1, lesson.Title);
        return new LessonContent(
            lesson.Title, html, lesson.Order, SectionTitle: string.Empty,
            Href: lesson.Href, ParentHref: lesson.ParentHref, IsSection: lesson.IsSection);
    }

    private sealed class LessonLink
    {
        public string  Href       { get; init; } = "";
        public string  Title      { get; init; } = "";
        public int     Index      { get; init; }
        public string? ParentHref { get; init; }
        public bool    IsSection  { get; init; }
    }
}
