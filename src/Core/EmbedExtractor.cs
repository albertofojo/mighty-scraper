using Microsoft.Playwright;
using MightyScraper.Models;

namespace MightyScraper.Core;

/// <summary>
/// Extracts video embeds (Vimeo / YouTube / Loom) from a loaded lesson page's iframes, preserving the
/// private hash needed to access domain-restricted Vimeo videos. Shared by the diagnostic and downloader.
/// </summary>
public static class EmbedExtractor
{
    public static async Task<MediaEmbed[]> ExtractAsync(IPage page)
    {
        var srcs = await page.EvaluateAsync<string[]>("""
            () => {
                const out = [];
                for (const f of document.querySelectorAll('iframe')) {
                    const s = f.getAttribute('src') || f.getAttribute('data-src') || '';
                    if (s && (s.includes('vimeo') || s.includes('youtube') || s.includes('youtu.be') || s.includes('loom')))
                        out.push(s);
                }
                return out;
            }
            """);

        return srcs.Select(Classify).Where(e => e is not null).Select(e => e!).ToArray();
    }

    private static MediaEmbed? Classify(string src)
    {
        if (!Uri.TryCreate(src, UriKind.Absolute, out var uri))
            return null;

        var host = uri.Host;
        var path = uri.AbsolutePath;

        if (host.Contains("vimeo.com"))
            return new MediaEmbed("Vimeo", LastSegment(path), QueryValue(uri, "h"), src);

        if (host.Contains("youtube.com") || host.Contains("youtu.be"))
        {
            var id = path.StartsWith("/embed/", StringComparison.OrdinalIgnoreCase) ? path["/embed/".Length..] : LastSegment(path);
            return new MediaEmbed("YouTube", id, null, src);
        }

        if (host.Contains("loom.com"))
            return new MediaEmbed("Loom", LastSegment(path), null, src);

        return null;
    }

    private static string LastSegment(string path) =>
        path.Split('/', StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? "";

    private static string? QueryValue(Uri uri, string key)
    {
        foreach (var part in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = part.IndexOf('=');
            if (eq > 0 && part[..eq].Equals(key, StringComparison.OrdinalIgnoreCase))
                return Uri.UnescapeDataString(part[(eq + 1)..]);
        }
        return null;
    }
}
