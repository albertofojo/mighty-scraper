using MightyScraper.Models;

namespace MightyScraper.Core;

public interface IMarkdownConverter
{
    Task<string> ConvertToMarkdownAsync(LessonContent content, CancellationToken cancellationToken = default);

    string GetFilename(LessonContent content);

    Task<string> SaveAsync(
        LessonContent content,
        string outputDirectory,
        string? header = null,
        string? footer = null,
        CancellationToken cancellationToken = default);
}
