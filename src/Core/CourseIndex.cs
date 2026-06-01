using System.Text;
using MightyScraper.Models;

namespace MightyScraper.Core;

/// <summary>
/// Writes <c>index.md</c> — the course entry point: a navigable table of contents linking every lesson
/// <c>.md</c>, grouped by module (section). Built from the discovered structure + the converter's filename
/// mapping, so it mirrors the sidebar hierarchy. Lessons whose <c>.md</c> isn't on disk are listed but not linked.
/// </summary>
public static class CourseIndex
{
    public static string Write(IReadOnlyList<LessonMeta> structure, IMarkdownConverter converter, string outputDir)
    {
        string FileFor(LessonMeta l) => converter.GetFilename(new LessonContent(l.Title, "", l.Order, ""));

        var sb = new StringBuilder();
        sb.Append("# Índice del curso\n\n");
        sb.Append($"> Puerta de entrada al curso — {structure.Count(l => !l.IsSection)} lecciones.\n");

        foreach (var item in structure.OrderBy(l => l.Order))
        {
            var file   = FileFor(item);
            var exists = File.Exists(Path.Combine(outputDir, file));
            // Angle-bracket link destinations tolerate spaces/parentheses in filenames.
            var label  = exists ? $"[{item.Title}](<{file}>)" : $"{item.Title} _(no descargado)_";

            if (item.IsSection)
                sb.Append($"\n## {label}\n\n");
            else
                sb.Append($"- {label}\n");
        }

        var path = Path.Combine(outputDir, "index.md");
        File.WriteAllText(path, sb.ToString());
        return path;
    }
}
