using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using MightyScraper.Models;

namespace MightyScraper.Core;

/// <summary>
/// Read-only status check (CLI flag <c>--check</c>): does one discovery pass and diffs the live course
/// structure against what's on disk — which lessons lack a <c>.md</c> (new or pending), which referenced
/// videos lack a local MP4, how many transcripts are present, and any orphan <c>.md</c> no longer in the
/// course. Cheap: it reads the existing <c>.md</c> files for video ids instead of navigating every lesson.
/// </summary>
public sealed class CourseStatus(
    ICourseNavigator navigator,
    IMarkdownConverter converter,
    ILogger<CourseStatus> logger)
{
    private static readonly Regex VimeoId    = new(@"vimeo\.com/(?:video/)?(\d+)", RegexOptions.Compiled);
    private static readonly string[] VideoExt = [".mp4", ".mkv", ".webm", ".mov"];
    private static readonly string[] SubExt   = [".vtt", ".srt"];

    public async Task RunAsync(string courseUrl, CancellationToken ct = default)
    {
        var outputDir = Path.GetFullPath(Path.Combine("output", SlugFromUrl(courseUrl)));
        var mediaDir  = Path.Combine(outputDir, "media");

        var structure = (await navigator.GetStructureAsync(courseUrl, ct))
            .Where(l => !string.IsNullOrEmpty(l.Href))
            .OrderBy(l => l.Order)
            .ToList();

        string FileFor(LessonMeta l) => converter.GetFilename(new LessonContent(l.Title, "", l.Order, ""));

        var mediaFiles = Directory.Exists(mediaDir)
            ? Directory.GetFiles(mediaDir).Select(Path.GetFileName).OfType<string>().ToArray()
            : [];

        // ── md presence + video ids referenced by the .md already on disk ────────────
        var expected   = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var missingMd  = new List<LessonMeta>();
        var videoRefs  = new List<(LessonMeta Lesson, string Id)>();
        var mdPresent  = 0;

        foreach (var item in structure)
        {
            var file = FileFor(item);
            expected.Add(file);
            var path = Path.Combine(outputDir, file);

            if (File.Exists(path))
            {
                mdPresent++;
                foreach (Match m in VimeoId.Matches(File.ReadAllText(path)))
                    videoRefs.Add((item, m.Groups[1].Value));
            }
            else
            {
                missingMd.Add(item);
            }
        }

        // ── video / transcript presence, keyed by the referenced ids ─────────────────
        bool HasFile(string id, string[] exts) =>
            mediaFiles.Any(f => f.Contains(id, StringComparison.Ordinal)
                             && exts.Contains(Path.GetExtension(f).ToLowerInvariant()));

        var distinctVideos = videoRefs
            .GroupBy(v => v.Id)
            .Select(g => g.First())
            .ToList();

        var missingVideos = distinctVideos.Where(v => !HasFile(v.Id, VideoExt)).ToList();
        var subsOnDisk    = distinctVideos.Count(v => HasFile(v.Id, SubExt));

        // ── orphan .md (on disk but no longer in the course) ─────────────────────────
        var orphanMd = Directory.Exists(outputDir)
            ? Directory.GetFiles(outputDir, "*.md")
                .Select(Path.GetFileName).OfType<string>()
                .Where(f => !f.Equals("index.md", StringComparison.OrdinalIgnoreCase) && !expected.Contains(f))
                .ToList()
            : [];

        Report(courseUrl, structure, mdPresent, missingMd, distinctVideos.Count, missingVideos, subsOnDisk, orphanMd, mediaFiles.Length == 0);
    }

    private void Report(
        string courseUrl,
        IReadOnlyList<LessonMeta> structure,
        int mdPresent,
        IReadOnlyList<LessonMeta> missingMd,
        int videosReferenced,
        IReadOnlyList<(LessonMeta Lesson, string Id)> missingVideos,
        int subsOnDisk,
        IReadOnlyList<string> orphanMd,
        bool noMediaDir)
    {
        logger.LogInformation("──────── Estado del curso ────────");
        logger.LogInformation("  Lecciones en el curso        : {N}", structure.Count);
        logger.LogInformation("  Con .md en disco             : {N}", mdPresent);
        logger.LogInformation("  Sin .md (nuevas o pendientes): {N}", missingMd.Count);
        foreach (var l in missingMd.Take(15))
            logger.LogInformation("       - [{N:D2}] {Title}", l.Order + 1, l.Title);
        if (missingMd.Count > 15)
            logger.LogInformation("       … y {N} más", missingMd.Count - 15);

        logger.LogInformation("  Vídeos referenciados en .md  : {N}", videosReferenced);
        logger.LogInformation("  Vídeos descargados (MP4)     : {N}", videosReferenced - missingVideos.Count);
        logger.LogInformation("  Vídeos que faltan            : {N}", missingVideos.Count);
        foreach (var v in missingVideos.Take(15))
            logger.LogInformation("       - [{N:D2}] {Title} (id={Id})", v.Lesson.Order + 1, v.Lesson.Title, v.Id);
        if (missingVideos.Count > 15)
            logger.LogInformation("       … y {N} más", missingVideos.Count - 15);

        logger.LogInformation("  Transcripciones .vtt en disco: {N}  (los demás pueden no tener subtítulos)", subsOnDisk);
        if (orphanMd.Count > 0)
            logger.LogInformation("  .md huérfanos (ya no en curso): {N}", orphanMd.Count);
        logger.LogInformation("──────────────────────────────────");

        // Actionable summary
        var upToDate = missingMd.Count == 0 && missingVideos.Count == 0;
        if (upToDate)
        {
            logger.LogInformation("✅ Estás al día: todas las lecciones tienen .md y todos los vídeos están descargados.");
            return;
        }

        if (noMediaDir && missingMd.Count == 0)
            logger.LogInformation("ℹ️  Tienes todos los .md pero aún no has descargado medios.");

        logger.LogInformation("Para ponerte al día:");
        if (missingMd.Count > 0)
            logger.LogInformation("   .md   →  dotnet run --project src -- {Url}", courseUrl);
        if (missingVideos.Count > 0 || missingMd.Count > 0)
            logger.LogInformation("   medios→  dotnet run --project src -- {Url} --download-media both", courseUrl);
    }

    private static string SlugFromUrl(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri)
            ? string.Concat(string.Join("-",
                    uri.Segments.Select(s => s.Trim('/')).Where(s => s.Length > 1))
                  .Split(Path.GetInvalidFileNameChars()))
                .Replace(' ', '-').Trim('-')
            : url;
}
