using System.Diagnostics;
using Microsoft.Extensions.Logging;
using MightyScraper.Models;

namespace MightyScraper.Core;

public sealed class ScraperApp(
    ICourseNavigator navigator,
    IMarkdownConverter converter,
    ILogger<ScraperApp> logger)
{
    public async Task RunAsync(string courseUrl, int skipCount = 0, int delayMs = 3_000, CancellationToken cancellationToken = default)
    {
        var outputDir = Path.Combine("output", SlugFromUrl(courseUrl));
        Directory.CreateDirectory(outputDir);

        logger.LogInformation("Scrape starting");
        logger.LogInformation("  URL    : {Url}", courseUrl);
        logger.LogInformation("  Output : {Dir}", Path.GetFullPath(outputDir));

        var totalWatch = Stopwatch.StartNew();

        // ── Phase 1: one page load to get full sidebar structure ─────────────────
        IReadOnlyList<LessonMeta> structure;
        try
        {
            structure = await navigator.GetStructureAsync(courseUrl, cancellationToken);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            logger.LogCritical(ex, "Failed to discover course structure");
            throw;
        }

        // Build href → filename map now — cross-links are ready before the first save
        var hrefToFile = structure
            .Where(l => !string.IsNullOrEmpty(l.Href))
            .ToDictionary(
                l => l.Href,
                l => converter.GetFilename(new LessonContent(l.Title, "", l.Order, "")));

        var toScrape = skipCount > 0 ? structure.Skip(skipCount).ToList() : structure;

        if (skipCount > 0)
            logger.LogInformation("Skipping first {Skip} lessons — resuming from lesson {From}", skipCount, skipCount + 1);

        logger.LogInformation(
            "Structure ready ({Count} lessons, {Remaining} to scrape) — starting progressive save...",
            structure.Count, toScrape.Count);

        // ── Phase 2: stream scrape → save each lesson as it arrives ─────────────
        var saved   = 0;
        var skipped = 0;

        try
        {
            await foreach (var lesson in navigator.ScrapeAsync(toScrape, delayMs, cancellationToken))
            {
                var n = lesson.Order + 1;

                // Breadcrumb for child lessons
                string? header = null;
                if (lesson.ParentHref is not null
                    && hrefToFile.TryGetValue(lesson.ParentHref, out var parentFile))
                {
                    var parentTitle = structure.FirstOrDefault(l => l.Href == lesson.ParentHref)?.Title ?? "";
                    header = $"> **Módulo:** [{parentTitle}]({parentFile})\n\n";
                }

                // Children list for section/module lessons
                string? footer = null;
                var children = structure.Where(l => l.ParentHref == lesson.Href).ToList();
                if (children.Count > 0)
                {
                    var links = children.Select(c =>
                        hrefToFile.TryGetValue(c.Href, out var f) ? $"- [{c.Title}]({f})" : $"- {c.Title}");
                    footer = $"\n\n---\n\n## Lecciones del módulo\n\n{string.Join("\n", links)}";
                }

                var lessonWatch = Stopwatch.StartNew();
                try
                {
                    var filePath = await converter.SaveAsync(lesson, outputDir, header, footer, cancellationToken);
                    lessonWatch.Stop();
                    saved++;
                    logger.LogInformation(
                        "[{N:D2}] Saved : {File}  ({Ms} ms)",
                        n, Path.GetFileName(filePath), lessonWatch.ElapsedMilliseconds);
                }
                catch (Exception ex)
                {
                    skipped++;
                    logger.LogWarning(ex, "[{N:D2}] Failed to save '{Title}'", n, lesson.Title);
                }
            }
        }
        catch (OperationCanceledException)
        {
            logger.LogWarning("Scrape cancelled after {Saved} lesson(s) saved", saved);
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Scrape aborted after {Saved} lesson(s) saved", saved);
            throw;
        }

        totalWatch.Stop();
        logger.LogInformation(
            "Scrape finished — {Saved} saved, {Skipped} skipped, {Elapsed:F1}s total",
            saved, skipped, totalWatch.Elapsed.TotalSeconds);
        logger.LogInformation("  Output : {Dir}", Path.GetFullPath(outputDir));
    }

    // ── Private helpers ─────────────────────────────────────────────────────────

    private static string SlugFromUrl(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri)
            ? Sanitize(string.Join("-",
                uri.Segments
                   .Select(s => s.Trim('/'))
                   .Where(s => s.Length > 1)))
            : Sanitize(url);

    private static string Sanitize(string name) =>
        string.Concat(name.Split(Path.GetInvalidFileNameChars()))
              .Replace(' ', '-')
              .Trim('-');
}
