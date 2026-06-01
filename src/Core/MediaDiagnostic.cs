using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Playwright;
using MightyScraper.Models;

namespace MightyScraper.Core;

/// <summary>
/// One-off diagnostic (CLI flag <c>--diagnose-media [N]</c>). Visits a sample of N video-bearing
/// lessons, intercepts the player network calls (Vimeo <c>config</c>, YouTube <c>player</c>,
/// Loom <c>api/graphql</c>) and combines them with the DOM iframes to report, per lesson:
/// provider + id + private hash, available download streams (progressive MP4 / HLS / DASH / DRM)
/// and available captions/transcript languages. Writes a JSON dump next to the scrape output.
/// This does NOT download anything — it only tells us what is feasible and what is free.
/// </summary>
public sealed class MediaDiagnostic(
    IPlaywrightManager playwrightManager,
    ICourseNavigator navigator,
    ILogger<MediaDiagnostic> logger)
{
    private const string ContentSelector = ".detail-layout-description";

    // Matches the Vimeo player *document* URL (player.vimeo.com/video/{id}?h=…), not its sub-resources
    // like /video/{id}/config — the document body carries the inline player config.
    private static readonly Regex VimeoDocRegex =
        new(@"player\.vimeo\.com/video/(\d+)(?:$|\?)", RegexOptions.Compiled);

    public async Task RunAsync(
        string courseUrl,
        int skipCount = 0,
        int sampleSize = 5,
        CancellationToken cancellationToken = default)
    {
        logger.LogInformation("Media diagnostic starting (sampling up to {N} video lessons)", sampleSize);

        var structure = await navigator.GetStructureAsync(courseUrl, cancellationToken);
        var lessons = structure
            .Where(l => !l.IsSection && !string.IsNullOrEmpty(l.Href))
            .Skip(skipCount)
            .ToList();

        logger.LogInformation("Discovered {Total} lessons — scanning from #{From} until {N} have video",
            structure.Count, skipCount + 1, sampleSize);

        var page = await playwrightManager.GetAuthenticatedPageAsync();
        var reports = new List<LessonMediaReport>();

        foreach (var lesson in lessons)
        {
            if (cancellationToken.IsCancellationRequested) break;
            if (reports.Count(r => r.Embeds.Length > 0) >= sampleSize) break;

            var report = await ProbeLessonAsync(page, lesson, cancellationToken);
            reports.Add(report);

            if (report.Embeds.Length > 0)
                LogLessonFindings(report);
            else
                logger.LogInformation("[{N:D2}] {Title} — no video embeds", lesson.Order + 1, lesson.Title);
        }

        WriteJsonReport(courseUrl, reports);
        LogSummary(reports);
    }

    // ── Per-lesson probe ─────────────────────────────────────────────────────────

    private async Task<LessonMediaReport> ProbeLessonAsync(IPage page, LessonMeta lesson, CancellationToken ct)
    {
        // Handler attached BEFORE navigation — the Vimeo embed document (which carries the inline
        // player config with progressive/hls/text_tracks) loads with the page, so a post-nav handler
        // would miss it. Buckets are per-lesson because the handler is detached in `finally`.
        var vimeoDocs    = new ConcurrentQueue<(string Id, string Body)>();
        var passive      = new ConcurrentQueue<(string Url, string Body)>();
        var docTasks     = new ConcurrentBag<Task>();
        var passiveTasks = new ConcurrentBag<Task>();
        var sawManifest  = false;

        void Handler(object? _, IResponse response)
        {
            var u = response.Url;
            if (u.Contains(".m3u8") || u.Contains("master.json") || u.Contains("/playlist")) sawManifest = true;

            var m = VimeoDocRegex.Match(u);
            if (m.Success)
                docTasks.Add(CaptureDocAsync(response, m.Groups[1].Value, vimeoDocs));
            else if (IsInterestingUrl(u))
                passiveTasks.Add(CaptureBodyAsync(response, passive));
        }

        page.Response += Handler;
        try
        {
            try
            {
                await page.GotoAsync(lesson.Href);
                await page.WaitForLoadStateAsync(LoadState.DOMContentLoaded);
                await page.Locator(ContentSelector).First.WaitForAsync(new() { Timeout = 15_000 });
            }
            catch (Exception ex)
            {
                logger.LogWarning("[{N:D2}] Navigation/content wait failed: {Msg}", lesson.Order + 1, ex.Message);
            }

            var embeds = await EmbedExtractor.ExtractAsync(page);
            var details = new List<VideoDetail>();

            if (embeds.Length > 0)
            {
                // Nudge any lazy player into loading its embed document, then let captures settle.
                try { await page.Locator("iframe").First.ScrollIntoViewIfNeededAsync(new() { Timeout = 4_000 }); }
                catch { /* best effort */ }

                try { await Task.WhenAll(docTasks.Concat(passiveTasks)).WaitAsync(TimeSpan.FromSeconds(6), ct); }
                catch { /* partial captures are fine */ }

                // If a Vimeo embed exists but its document hasn't been captured yet (lazy), wait a bit more.
                if (embeds.Any(e => e.Provider == "Vimeo") && vimeoDocs.IsEmpty)
                {
                    try { await page.WaitForResponseAsync(r => VimeoDocRegex.IsMatch(r.Url), new() { Timeout = 8_000 }); }
                    catch { /* still nothing — fall back to manifest-only signal */ }
                    try { await Task.WhenAll(docTasks).WaitAsync(TimeSpan.FromSeconds(4), ct); } catch { }
                }

                var docs = vimeoDocs.ToArray();
                foreach (var e in embeds.Where(e => e.Provider == "Vimeo"))
                {
                    var doc = docs.FirstOrDefault(d => d.Id == e.Id);
                    details.Add(doc.Body is not null
                        ? ParseVimeoInlineConfig(e.Id, doc.Body, sawManifest)
                        : new VideoDetail("Vimeo", e.Id, null, null,
                            false, [], sawManifest, false, false, [],
                            Note: sawManifest ? "HLS visto; config inline no capturado" : "config no capturado"));
                }

                details.AddRange(ParseCaptures(passive.ToArray()));
            }

            return new LessonMediaReport(lesson.Order, lesson.Title, lesson.Href, embeds, details.ToArray());
        }
        finally
        {
            page.Response -= Handler;
        }
    }

    private static async Task CaptureBodyAsync(IResponse response, ConcurrentQueue<(string, string)> sink)
    {
        try { sink.Enqueue((response.Url, await response.TextAsync())); }
        catch { /* body unavailable (frame navigated away / binary) — ignore */ }
    }

    private static async Task CaptureDocAsync(IResponse response, string id, ConcurrentQueue<(string, string)> sink)
    {
        try { sink.Enqueue((id, await response.TextAsync())); }
        catch { /* body unavailable — ignore */ }
    }

    /// <summary>
    /// Extracts the inline Vimeo player config JSON embedded in the player document HTML and parses it
    /// (reusing <see cref="ParseVimeoConfig"/>). Modern Vimeo players ship config inline in the embed
    /// page rather than via a separate /config XHR, which is why network interception alone missed it.
    /// </summary>
    private VideoDetail ParseVimeoInlineConfig(string id, string html, bool manifestSeen)
    {
        var json = ExtractPlayerConfig(html);
        if (json is null)
            return new VideoDetail("Vimeo", id, null, null,
                false, [], manifestSeen, false, false, [], Note: "config inline no parseable");

        try
        {
            var detail = ParseVimeoConfig(json);
            return manifestSeen && !detail.HasHls ? detail with { HasHls = true } : detail;
        }
        catch
        {
            return new VideoDetail("Vimeo", id, null, null,
                false, [], manifestSeen, false, false, [], Note: "config inline no parseable");
        }
    }

    /// <summary>Finds and brace-balances the player config object inside the embed HTML.</summary>
    private static string? ExtractPlayerConfig(string html)
    {
        // Vimeo's config object is the JSON whose first keys are cdn_url / player_url / vimeo_api_url.
        foreach (var anchor in (string[])["{\"cdn_url\"", "{\"vimeo_api_url\"", "{\"player_url\"", "{\"request\""])
        {
            var i = html.IndexOf(anchor, StringComparison.Ordinal);
            if (i < 0) continue;
            var json = ExtractBalancedObject(html, i);
            if (json is null) continue;
            try { using var _ = JsonDocument.Parse(json); return json; } catch { /* try next anchor */ }
        }
        return null;
    }

    /// <summary>Returns the balanced <c>{…}</c> substring starting at <paramref name="start"/>, honoring strings.</summary>
    private static string? ExtractBalancedObject(string s, int start)
    {
        int depth = 0;
        bool inStr = false, esc = false;
        for (var i = start; i < s.Length; i++)
        {
            var c = s[i];
            if (inStr)
            {
                if (esc) esc = false;
                else if (c == '\\') esc = true;
                else if (c == '"') inStr = false;
            }
            else if (c == '"') inStr = true;
            else if (c == '{') depth++;
            else if (c == '}' && --depth == 0) return s[start..(i + 1)];
        }
        return null;
    }

    // ── Network capture parsing ──────────────────────────────────────────────────

    private static VideoDetail[] ParseCaptures((string Url, string Body)[] captures)
    {
        var details = new List<VideoDetail>();
        foreach (var (url, body) in captures)
        {
            try
            {
                if (url.Contains("youtubei/v1/player"))
                    details.Add(ParseYouTubePlayer(body));
                else if (url.Contains("loom.com"))
                    details.Add(ParseLoom(url, body));
            }
            catch { /* malformed/partial body — skip this capture */ }
        }
        return details.ToArray();
    }

    private static VideoDetail ParseVimeoConfig(string body)
    {
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;

        string? id = null, title = null;
        double? duration = null;
        if (root.TryGetProperty("video", out var video))
        {
            if (video.TryGetProperty("id", out var idEl))       id = idEl.ToString();
            if (video.TryGetProperty("title", out var tEl))     title = tEl.GetString();
            if (video.TryGetProperty("duration", out var dEl) && dEl.TryGetDouble(out var d)) duration = d;
        }

        bool hasProgressive = false, hasHls = false, hasDash = false;
        var qualities = new List<string>();
        var tracks    = new List<TextTrackInfo>();

        if (root.TryGetProperty("request", out var request))
        {
            if (request.TryGetProperty("files", out var files))
            {
                if (files.TryGetProperty("progressive", out var prog) && prog.ValueKind == JsonValueKind.Array)
                {
                    hasProgressive = prog.GetArrayLength() > 0;
                    foreach (var p in prog.EnumerateArray())
                        if (p.TryGetProperty("quality", out var q))
                            qualities.Add(q.GetString() ?? "?");
                }
                hasHls  = files.TryGetProperty("hls", out _);
                hasDash = files.TryGetProperty("dash", out _);
            }

            if (request.TryGetProperty("text_tracks", out var tt) && tt.ValueKind == JsonValueKind.Array)
            {
                foreach (var t in tt.EnumerateArray())
                {
                    var lang  = t.TryGetProperty("lang", out var l)  ? l.GetString() ?? "?" : "?";
                    var kind  = t.TryGetProperty("kind", out var k)  ? k.GetString() ?? ""  : "";
                    var label = t.TryGetProperty("label", out var b) ? b.GetString()        : null;
                    tracks.Add(new TextTrackInfo(lang, kind, label));
                }
            }
        }

        var drm = body.Contains("\"drm\"", StringComparison.OrdinalIgnoreCase);
        return new VideoDetail("Vimeo", id, title, duration,
            hasProgressive, qualities.ToArray(), hasHls, hasDash, drm, tracks.ToArray(), Note: "");
    }

    private static VideoDetail ParseYouTubePlayer(string body)
    {
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;

        string? id = null, title = null;
        double? duration = null;
        if (root.TryGetProperty("videoDetails", out var vd))
        {
            if (vd.TryGetProperty("videoId", out var idEl))       id = idEl.GetString();
            if (vd.TryGetProperty("title", out var tEl))          title = tEl.GetString();
            if (vd.TryGetProperty("lengthSeconds", out var lEl)
                && double.TryParse(lEl.GetString(), out var secs)) duration = secs;
        }

        var tracks = new List<TextTrackInfo>();
        if (root.TryGetProperty("captions", out var caps)
            && caps.TryGetProperty("playerCaptionsTracklistRenderer", out var rend)
            && rend.TryGetProperty("captionTracks", out var ct) && ct.ValueKind == JsonValueKind.Array)
        {
            foreach (var t in ct.EnumerateArray())
            {
                var lang = t.TryGetProperty("languageCode", out var l) ? l.GetString() ?? "?" : "?";
                var kind = t.TryGetProperty("kind", out var k) ? k.GetString() ?? "" : ""; // "asr" = auto-generated
                tracks.Add(new TextTrackInfo(lang, kind, null));
            }
        }

        // YouTube downloads go through yt-dlp; streams are always present (no progressive/hls flags here).
        return new VideoDetail("YouTube", id, title, duration,
            HasProgressive: false, ProgressiveQualities: [], HasHls: false, HasDash: false, LikelyDrm: false,
            TextTracks: tracks.ToArray(), Note: "Descarga vía yt-dlp; subtítulos vía yt-dlp --write-auto-sub.");
    }

    private static VideoDetail ParseLoom(string url, string body)
    {
        // Loom's transcript/stream live behind GraphQL; full parsing is provider-specific.
        // For the diagnostic we only flag presence so we know whether to dig deeper.
        var hasMp4        = body.Contains("mp4", StringComparison.OrdinalIgnoreCase) || url.Contains("transcoded");
        var hasTranscript = body.Contains("transcript", StringComparison.OrdinalIgnoreCase)
                         || body.Contains("caption", StringComparison.OrdinalIgnoreCase);
        var note = $"Loom; mp4 detectado: {hasMp4}; transcripción detectada: {hasTranscript}. Descarga vía yt-dlp.";
        return new VideoDetail("Loom", null, null, null,
            HasProgressive: hasMp4, ProgressiveQualities: [], HasHls: false, HasDash: false, LikelyDrm: false,
            TextTracks: [], Note: note);
    }

    // ── Reporting ────────────────────────────────────────────────────────────────

    private void LogLessonFindings(LessonMediaReport r)
    {
        logger.LogInformation("[{N:D2}] {Title} — {Count} embed(s)", r.Order + 1, r.Title, r.Embeds.Length);
        foreach (var e in r.Embeds)
            logger.LogInformation("        • {Provider} id={Id} hash={Hash}", e.Provider, e.Id, e.Hash ?? "—");
        foreach (var d in r.Details)
        {
            var streams = d.Provider == "Vimeo"
                ? (d.Note.Length > 0
                    ? d.Note
                    : $"progressive={d.HasProgressive}({string.Join("/", d.ProgressiveQualities)}) hls={d.HasHls} dash={d.HasDash} drm={d.LikelyDrm}")
                : d.Note;
            var subs = d.TextTracks.Length > 0
                ? string.Join(", ", d.TextTracks.Select(t => $"{t.Lang}{(t.Kind.Length > 0 ? $"[{t.Kind}]" : "")}"))
                : "ninguna";
            logger.LogInformation("        → {Provider} '{Title}' {Dur} | {Streams} | subtítulos: {Subs}",
                d.Provider, d.Title ?? "?", FormatDuration(d.DurationSeconds), streams, subs);
        }
    }

    private void LogSummary(IReadOnlyList<LessonMediaReport> reports)
    {
        var withVideo = reports.Where(r => r.Embeds.Length > 0).ToList();
        var byProvider = withVideo
            .SelectMany(r => r.Embeds)
            .GroupBy(e => e.Provider)
            .Select(g => $"{g.Key}={g.Count()}");

        var totalEmbeds  = withVideo.Sum(r => r.Embeds.Length);
        var allDetails   = withVideo.SelectMany(r => r.Details).ToList();
        var configsOk    = allDetails.Count(d => d.Provider == "Vimeo" && d.Note.Length == 0);
        var anySubs       = allDetails.Any(d => d.TextTracks.Length > 0);
        var anyProgressive = allDetails.Any(d => d.HasProgressive);
        var anyHls        = allDetails.Any(d => d.HasHls);
        var anyDrm        = allDetails.Any(d => d.LikelyDrm);

        logger.LogInformation("──────── Diagnóstico de medios ────────");
        logger.LogInformation("  Lecciones escaneadas      : {Total}", reports.Count);
        logger.LogInformation("  Con vídeo                 : {N}", withVideo.Count);
        logger.LogInformation("  Proveedores               : {P}", string.Join(", ", byProvider));
        logger.LogInformation("  Configs Vimeo leídos OK   : {Ok}/{Total}", configsOk, totalEmbeds);
        logger.LogInformation("  ¿Algún MP4 progresivo?    : {V}  (descarga directa fácil)", anyProgressive);
        logger.LogInformation("  ¿HLS disponible?          : {V}  (descarga vía yt-dlp/ffmpeg)", anyHls);
        logger.LogInformation("  ¿Subtítulos disponibles?  : {V}  (transcripción GRATIS si true)", anySubs);
        logger.LogInformation("  ¿Indicios de DRM?         : {V}", anyDrm);
        logger.LogInformation("───────────────────────────────────────");
    }

    private void WriteJsonReport(string courseUrl, IReadOnlyList<LessonMediaReport> reports)
    {
        var outputDir = Path.Combine("output", SlugFromUrl(courseUrl));
        Directory.CreateDirectory(outputDir);
        var path = Path.Combine(outputDir, "_media-diagnostic.json");

        var json = JsonSerializer.Serialize(reports, new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        });
        File.WriteAllText(path, json);
        logger.LogInformation("Detalle completo escrito en: {Path}", Path.GetFullPath(path));
    }

    // ── Small helpers ────────────────────────────────────────────────────────────

    private static string FormatDuration(double? seconds) =>
        seconds is null ? "?" : TimeSpan.FromSeconds(seconds.Value).ToString(@"mm\:ss");

    private static bool IsInterestingUrl(string url) =>
        url.Contains("youtubei/v1/player")
        || (url.Contains("loom.com") && (url.Contains("/api/") || url.Contains("graphql") || url.Contains("transcoded")));

    private static string SlugFromUrl(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri)
            ? string.Concat(string.Join("-",
                    uri.Segments.Select(s => s.Trim('/')).Where(s => s.Length > 1))
                  .Split(Path.GetInvalidFileNameChars()))
                .Replace(' ', '-').Trim('-')
            : url;

    // ── Report models ────────────────────────────────────────────────────────────

    public sealed record TextTrackInfo(string Lang, string Kind, string? Label);

    public sealed record VideoDetail(
        string Provider, string? Id, string? Title, double? DurationSeconds,
        bool HasProgressive, string[] ProgressiveQualities, bool HasHls, bool HasDash, bool LikelyDrm,
        TextTrackInfo[] TextTracks, string Note);

    public sealed record LessonMediaReport(int Order, string Title, string Href, MediaEmbed[] Embeds, VideoDetail[] Details);
}
