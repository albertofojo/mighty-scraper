namespace MightyScraper.Models;

public record LessonContent(
    string  Title,
    string  RawHtml,
    int     Order,
    string  SectionTitle,
    string  Href       = "",
    string? ParentHref = null,
    bool    IsSection  = false
);
