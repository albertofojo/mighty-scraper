using System.Text.RegularExpressions;
using HtmlAgilityPack;
using MightyScraper.Models;
using ReverseMarkdown;

namespace MightyScraper.Core;

public sealed class MarkdownConverter : IMarkdownConverter
{
    // Strips trailing status circles + time estimate: "🟢 — 53 min", "🔴 — 5 min", "— 32 min"
    private static readonly Regex TrailingTimeAndStatusRegex = new(
        @"\s*(🟢|🔴|🟡)*\s*[—–]\s*\d+\s*min\s*$",
        RegexOptions.Compiled);
    // XPath expressions for UI chrome that must be stripped before conversion
    private static readonly string[] UnwantedXPaths =
    [
        "//button",
        "//*[contains(@class,'modal')]",
        "//*[contains(@class,'complete-btn')]",
        "//*[contains(@class,'overlay')]",
    ];

    private readonly Converter _converter = new(new Config
    {
        // Drop any HTML tag ReverseMarkdown doesn't recognise instead of leaking it as raw HTML
        UnknownTags       = Config.UnknownTagsOption.Drop,
        GithubFlavored    = true,   // fenced code blocks, task lists, tables
        RemoveComments    = true,
        SmartHrefHandling = true,
    });

    // ── IMarkdownConverter ──────────────────────────────────────────────────────

    public Task<string> ConvertToMarkdownAsync(
        LessonContent content,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(content.RawHtml))
            return Task.FromResult(string.Empty);

        var markdown = _converter.Convert(StripUiElements(content.RawHtml)).Trim();
        return Task.FromResult(markdown);
    }

    public string GetFilename(LessonContent content) => BuildFilename(content);

    public async Task<string> SaveAsync(
        LessonContent content,
        string outputDirectory,
        string? header = null,
        string? footer = null,
        CancellationToken cancellationToken = default)
    {
        var markdown = await ConvertToMarkdownAsync(content, cancellationToken);
        var fullPath = Path.Combine(outputDirectory, BuildFilename(content));

        var finalContent = string.Concat(header ?? "", markdown, footer ?? "");
        await File.WriteAllTextAsync(fullPath, finalContent, cancellationToken);
        return fullPath;
    }

    // ── Private helpers ─────────────────────────────────────────────────────────

    private static string StripUiElements(string html)
    {
        var doc = new HtmlDocument();
        doc.LoadHtml(html);

        ReplaceIframesWithLinks(doc);

        foreach (var xpath in UnwantedXPaths)
        {
            var nodes = doc.DocumentNode.SelectNodes(xpath);
            if (nodes is null) continue;
            foreach (var node in nodes.ToList())
                node.Remove();
        }

        return doc.DocumentNode.OuterHtml;
    }

    private static void ReplaceIframesWithLinks(HtmlDocument doc)
    {
        var iframes = doc.DocumentNode.SelectNodes("//iframe");
        if (iframes is null) return;

        foreach (var iframe in iframes.ToList())
        {
            var src = iframe.GetAttributeValue("src", "").Trim();
            if (string.IsNullOrEmpty(src)) continue;

            var watchUrl = ToWatchUrl(src);
            var link = doc.CreateElement("p");
            var anchor = doc.CreateElement("a");
            anchor.SetAttributeValue("href", watchUrl);
            anchor.InnerHtml = $"📹 {watchUrl}";
            link.AppendChild(anchor);
            iframe.ParentNode.ReplaceChild(link, iframe);
        }
    }

    private static string ToWatchUrl(string src)
    {
        if (!Uri.TryCreate(src, UriKind.Absolute, out var uri))
            return src;

        if (uri.Host.Contains("vimeo.com"))
        {
            // player.vimeo.com/video/12345?h=abc → vimeo.com/12345
            var id = uri.Segments.LastOrDefault(s => s.Trim('/').Length > 0)?.Trim('/');
            return id is not null ? $"https://vimeo.com/{id}" : src;
        }

        if (uri.Host.Contains("youtube.com") && uri.AbsolutePath.StartsWith("/embed/"))
            return $"https://www.youtube.com/watch?v={uri.AbsolutePath["/embed/".Length..]}";

        if (uri.Host.Contains("loom.com"))
            return src.Replace("/embed/", "/share/", StringComparison.OrdinalIgnoreCase);

        return src;
    }

    private static string BuildFilename(LessonContent content) =>
        $"{content.Order + 1:D2}-{SanitizeTitle(CleanTitle(content.Title))}.md";

    private static string CleanTitle(string title)
    {
        // Remove trailing status circles and time estimate: "🟢 — 53 min"
        title = TrailingTimeAndStatusRegex.Replace(title, "").Trim();
        // Remove any leftover trailing status circles with no time estimate
        while (title.EndsWith("🟢") || title.EndsWith("🔴") || title.EndsWith("🟡"))
            title = title[..^2].TrimEnd(); // each emoji is 2 UTF-16 chars (surrogate pair)
        return title;
    }

    private static string SanitizeTitle(string title)
    {
        var invalid = Path.GetInvalidFileNameChars();
        return string.Concat(title.Split(invalid))
            .Replace(' ', '-')
            .Trim('-')
            .Trim();
    }
}
