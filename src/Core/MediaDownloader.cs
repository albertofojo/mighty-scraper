using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Playwright;
using MightyScraper.Models;

namespace MightyScraper.Core;

public enum MediaMode { Both, Video, Subs }

/// <summary>
/// Downloads lesson videos and/or their subtitle tracks via <c>yt-dlp</c> (+ <c>ffmpeg</c> for HLS→MP4).
/// Reuses the authenticated browser to harvest each Vimeo embed's private hash (the saved <c>.md</c> files
/// no longer carry it), then shells out to yt-dlp with the lesson page as Referer so Vimeo's domain-privacy
/// check passes. Resumable: video downloads use a yt-dlp download-archive.
/// </summary>
public sealed class MediaDownloader(
    IPlaywrightManager playwrightManager,
    ICourseNavigator navigator,
    IMarkdownConverter converter,
    ILogger<MediaDownloader> logger)
{
    private const string ContentSelector = ".detail-layout-description";
    private static readonly string[] VideoExtensions = [".mp4", ".mkv", ".webm", ".mov"];
    private static readonly string[] SubExtensions   = [".vtt", ".srt"];

    public async Task RunAsync(string courseUrl, int skipCount, MediaMode mode, int maxVideos = 0, CancellationToken ct = default)
    {
        var ytDlp = await ResolveYtDlpAsync();
        if (ytDlp is null)
        {
            logger.LogError("yt-dlp no está disponible (ni en PATH ni como módulo de Python). Instálalo:  pip install -U yt-dlp");
            return;
        }
        logger.LogInformation("yt-dlp: {Cmd}", string.Join(' ', new[] { ytDlp.Value.Exe }.Concat(ytDlp.Value.Prefix)));

        if (mode != MediaMode.Subs && !await CanRunAsync("ffmpeg", "-version"))
        {
            logger.LogError("ffmpeg no está instalado (necesario para fusionar el HLS en MP4). Instálalo:  winget install Gyan.FFmpeg");
            logger.LogError("  (Mientras tanto puedes bajar solo subtítulos con:  --download-media subs)");
            return;
        }

        var outputDir = Path.GetFullPath(Path.Combine("output", SlugFromUrl(courseUrl)));
        var mediaDir  = Path.Combine(outputDir, "media");
        Directory.CreateDirectory(mediaDir);
        var archive = Path.Combine(mediaDir, "_archive.txt");

        var structure = await navigator.GetStructureAsync(courseUrl, ct);
        var lessons = structure
            // Sections can embed videos too, and keeping them aligns --skip with the NN- file numbering.
            .Where(l => !string.IsNullOrEmpty(l.Href))
            .Skip(skipCount)
            .ToList();

        logger.LogInformation("Descarga de medios — modo {Mode}, {N} lecciones a revisar desde #{From}{Limit}",
            mode, lessons.Count, skipCount + 1, maxVideos > 0 ? $" (límite {maxVideos} vídeo(s))" : "");
        logger.LogInformation("  Salida : {Dir}", mediaDir);

        var page = await playwrightManager.GetAuthenticatedPageAsync();
        var downloaded = new List<(LessonMeta Lesson, MediaEmbed Embed)>();
        int videos = 0, ok = 0, fail = 0;

        foreach (var lesson in lessons)
        {
            if (ct.IsCancellationRequested) break;

            try
            {
                await page.GotoAsync(lesson.Href);
                await page.WaitForLoadStateAsync(LoadState.DOMContentLoaded);
                try { await page.Locator(ContentSelector).First.WaitForAsync(new() { Timeout = 15_000 }); } catch { /* lesson may have no content container */ }
            }
            catch (Exception ex)
            {
                logger.LogWarning("[{N:D2}] navegación falló: {Msg} — saltando", lesson.Order + 1, ex.Message);
                continue;
            }

            var embeds = (await EmbedExtractor.ExtractAsync(page))
                .Where(e => e.Provider == "Vimeo")
                .ToArray();
            if (embeds.Length == 0) continue;

            foreach (var embed in embeds)
            {
                if (ct.IsCancellationRequested) break;
                videos++;
                var code = await RunYtDlpAsync(ytDlp.Value, embed, lesson, mediaDir, archive, mode, ct);
                if (code == 0) { ok++; downloaded.Add((lesson, embed)); } else fail++;

                if (maxVideos > 0 && videos >= maxVideos) goto done;
            }
        }

    done:

        // Post-process: enrich each lesson's .md with online + local media links, then (re)build the index.
        var enriched = EnrichMarkdown(outputDir, mediaDir, downloaded);
        var indexPath = CourseIndex.Write(structure, converter, outputDir);

        logger.LogInformation("──────── Descarga de medios ────────");
        logger.LogInformation("  Vídeos encontrados : {V}", videos);
        logger.LogInformation("  yt-dlp OK          : {Ok}", ok);
        logger.LogInformation("  Con error          : {Fail}", fail);
        logger.LogInformation("  .md enriquecidos   : {E}", enriched);
        logger.LogInformation("  Índice             : {Idx}", indexPath);
        logger.LogInformation("  Salida             : {Dir}", mediaDir);
        logger.LogInformation("────────────────────────────────────");
    }

    private async Task<int> RunYtDlpAsync((string Exe, string[] Prefix) ytDlp, MediaEmbed embed, LessonMeta lesson, string mediaDir, string archive, MediaMode mode, CancellationToken ct)
    {
        var url = string.IsNullOrEmpty(embed.Hash)
            ? $"https://player.vimeo.com/video/{embed.Id}"
            : $"https://player.vimeo.com/video/{embed.Id}?h={embed.Hash}";

        // Name aligned with the lesson .md (same NN-Slug base), suffixed with the Vimeo id to disambiguate
        // multiple embeds in one lesson. Subtitles inherit the stem as "<base>-<id>.<lang>.vtt".
        var outTemplate = Path.Combine(mediaDir, $"{LessonBaseName(lesson)}-{embed.Id}.%(ext)s");

        var args = new List<string>(ytDlp.Prefix)
        {
            "--no-progress", "--no-warnings",
            "--referer", lesson.Href,
            "-o", outTemplate,
        };

        if (mode == MediaMode.Subs)
        {
            args.Add("--skip-download");
        }
        else
        {
            args.Add("--merge-output-format"); args.Add("mp4");
            args.Add("--download-archive");    args.Add(archive);   // resume support
        }

        if (mode != MediaMode.Video)
        {
            args.Add("--write-subs");
            args.Add("--sub-langs");  args.Add("es.*,en.*");
            args.Add("--sub-format"); args.Add("vtt");
        }

        args.Add(url);

        logger.LogInformation("[{N:D2}] yt-dlp id={Id} ({Mode}) — {Title}", lesson.Order + 1, embed.Id, mode, lesson.Title);
        return await RunProcessAsync(ytDlp.Exe, args, ct);
    }

    private async Task<int> RunProcessAsync(string file, IReadOnlyList<string> args, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(file)
        {
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            UseShellExecute        = false,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var proc = new Process { StartInfo = psi };
        proc.OutputDataReceived += (_, d) => { if (!string.IsNullOrWhiteSpace(d.Data)) logger.LogInformation("    {Line}", d.Data); };
        proc.ErrorDataReceived  += (_, d) => { if (!string.IsNullOrWhiteSpace(d.Data)) logger.LogWarning("    {Line}", d.Data); };

        proc.Start();
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();
        await proc.WaitForExitAsync(ct);
        return proc.ExitCode;
    }

    private string LessonBaseName(LessonMeta lesson) =>
        Path.GetFileNameWithoutExtension(converter.GetFilename(new LessonContent(lesson.Title, "", lesson.Order, "")));

    // ── Markdown enrichment ──────────────────────────────────────────────────────

    /// <summary>
    /// For every downloaded video, rewrites its lesson <c>.md</c> so the video reference carries both a working
    /// online link (with the Vimeo hash) and links to the local MP4 / transcript. Idempotent via a per-id marker.
    /// </summary>
    private int EnrichMarkdown(string outputDir, string mediaDir, IReadOnlyList<(LessonMeta Lesson, MediaEmbed Embed)> items)
    {
        var localFiles = Directory.Exists(mediaDir)
            ? Directory.GetFiles(mediaDir).Select(Path.GetFileName).Where(f => f is not null).Select(f => f!).ToArray()
            : [];

        var enriched = 0;
        foreach (var (lesson, embed) in items)
        {
            var mdPath = Path.Combine(outputDir, converter.GetFilename(new LessonContent(lesson.Title, "", lesson.Order, "")));
            if (!File.Exists(mdPath))
            {
                logger.LogWarning("[{N:D2}] .md no encontrado para enriquecer: {File}", lesson.Order + 1, Path.GetFileName(mdPath));
                continue;
            }

            var marker = $"<!-- media:{embed.Id} -->";
            var md = File.ReadAllText(mdPath);
            if (md.Contains(marker, StringComparison.Ordinal)) continue;   // already enriched

            // Match the files belonging to this embed by its id (works regardless of the naming scheme).
            var mine  = localFiles.Where(f => f.Contains(embed.Id, StringComparison.Ordinal)).ToArray();
            var video = mine.FirstOrDefault(f => VideoExtensions.Contains(Path.GetExtension(f).ToLowerInvariant()));
            var subs  = mine.Where(f => SubExtensions.Contains(Path.GetExtension(f).ToLowerInvariant())).ToArray();

            var line = BuildMediaLine(embed, video, subs) + " " + marker;

            // Replace the original online-only watch link (the line that mentions this video's id) in place;
            // otherwise append a small media section at the end.
            var lines = md.Replace("\r\n", "\n").Split('\n').ToList();
            var idx = lines.FindIndex(l =>
                l.Contains(embed.Id, StringComparison.Ordinal) &&
                (l.Contains("vimeo", StringComparison.OrdinalIgnoreCase)
                 || l.Contains("youtu", StringComparison.OrdinalIgnoreCase)
                 || l.Contains("loom", StringComparison.OrdinalIgnoreCase)));

            if (idx >= 0) lines[idx] = line;
            else lines.AddRange(["", "---", "", line]);

            File.WriteAllText(mdPath, string.Join("\n", lines));
            enriched++;
        }
        return enriched;
    }

    private static string BuildMediaLine(MediaEmbed embed, string? video, IReadOnlyList<string> subs)
    {
        var parts = new List<string> { $"[▶️ Ver online](<{OnlineUrl(embed)}>)" };
        if (video is not null)
            parts.Add($"[⬇️ MP4](<media/{video}>)");
        foreach (var sub in subs)
        {
            var lang = Path.GetExtension(Path.GetFileNameWithoutExtension(sub)).TrimStart('.');  // "…-id.es.vtt" → "es"
            parts.Add($"[📝 Transcripción{(lang.Length > 0 ? $" ({lang})" : "")}](<media/{sub}>)");
        }
        return "🎬 **Vídeo:** " + string.Join(" · ", parts);
    }

    private static string OnlineUrl(MediaEmbed embed) => embed.Provider switch
    {
        "Vimeo"   => string.IsNullOrEmpty(embed.Hash) ? $"https://vimeo.com/{embed.Id}" : $"https://vimeo.com/{embed.Id}/{embed.Hash}",
        "YouTube" => $"https://www.youtube.com/watch?v={embed.Id}",
        "Loom"    => $"https://www.loom.com/share/{embed.Id}",
        _         => embed.EmbedUrl,
    };

    /// <summary>Resolves how to invoke yt-dlp: prefer it on PATH, else fall back to <c>python -m yt_dlp</c>.</summary>
    private static async Task<(string Exe, string[] Prefix)?> ResolveYtDlpAsync()
    {
        if (await CanRunAsync("yt-dlp", "--version"))
            return ("yt-dlp", []);
        if (await CanRunAsync("python", "-m", "yt_dlp", "--version"))
            return ("python", ["-W", "ignore", "-m", "yt_dlp"]);  // -W ignore: silence the requests/urllib3 import warning
        return null;
    }

    private static async Task<bool> CanRunAsync(string file, params string[] args)
    {
        try
        {
            var psi = new ProcessStartInfo(file)
            {
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
                UseShellExecute        = false,
            };
            foreach (var a in args) psi.ArgumentList.Add(a);

            using var p = Process.Start(psi)!;
            await p.WaitForExitAsync();
            return p.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    private static string SlugFromUrl(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri)
            ? string.Concat(string.Join("-",
                    uri.Segments.Select(s => s.Trim('/')).Where(s => s.Length > 1))
                  .Split(Path.GetInvalidFileNameChars()))
                .Replace(' ', '-').Trim('-')
            : url;
}
