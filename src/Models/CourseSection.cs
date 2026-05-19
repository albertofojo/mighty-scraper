namespace MightyScraper.Models;

public record CourseSection(
    string Title,
    IReadOnlyList<CourseLesson> Lessons,
    int Order
);
