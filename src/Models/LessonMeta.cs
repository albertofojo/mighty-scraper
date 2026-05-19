namespace MightyScraper.Models;

public record LessonMeta(
    string  Href,
    string  Title,
    int     Order,
    string? ParentHref,
    bool    IsSection
);
