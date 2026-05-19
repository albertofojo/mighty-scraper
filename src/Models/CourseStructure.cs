namespace MightyScraper.Models;

public record CourseStructure(
    string CourseTitle,
    string Url,
    IReadOnlyList<CourseSection> Sections
);
