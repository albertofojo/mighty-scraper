using MightyScraper.Models;

namespace MightyScraper.Core;

public interface ICourseNavigator
{
    /// <summary>
    /// Navigates to the course page and returns the full sidebar structure without
    /// scraping any lesson content. Fast — one page load only.
    /// </summary>
    Task<IReadOnlyList<LessonMeta>> GetStructureAsync(string courseUrl, CancellationToken cancellationToken = default);

    /// <summary>
    /// Visits each lesson in <paramref name="structure"/> and streams the extracted
    /// HTML content. Yields as soon as each lesson is ready so callers can save
    /// progressively without buffering everything in memory.
    /// </summary>
    IAsyncEnumerable<LessonContent> ScrapeAsync(IReadOnlyList<LessonMeta> structure, int delayMs = 3_000, CancellationToken cancellationToken = default);
}
