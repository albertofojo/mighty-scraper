using MightyScraper.Core;
using MightyScraper.Models;

namespace MightyScraper.Tests;

public class MarkdownConverterTests
{
    private readonly MarkdownConverter _converter = new();

    // ---------------------------------------------------------------------------
    // 1. HTML → Markdown heading hierarchy
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task ConvertToMarkdownAsync_WithH1AndH2_PreservesHeadingHierarchy()
    {
        // Arrange
        var content = new LessonContent("L", "<h1>Title</h1><h2>Sub</h2>", 0, "");

        // Act
        var md = await _converter.ConvertToMarkdownAsync(content);

        // Assert
        Assert.Contains("# Title", md);
        Assert.Contains("## Sub", md);
    }

    // ---------------------------------------------------------------------------
    // 2. Vimeo iframe → watch link  (also verifies no raw <iframe> leaks through)
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task ConvertToMarkdownAsync_WithVimeoIframe_EmitsWatchLinkAndDropsTag()
    {
        // Arrange — embed URL matches the real pattern we found in the diagnostic
        var content = new LessonContent("L",
            """<iframe src="https://player.vimeo.com/video/668395738?h=abc123"></iframe>""",
            0, "");

        // Act
        var md = await _converter.ConvertToMarkdownAsync(content);

        // Assert — embed URL is rewritten to the public watch URL
        Assert.Contains("https://vimeo.com/668395738", md);
        // Assert — no raw HTML leaks into the markdown
        Assert.DoesNotContain("<iframe", md);
        Assert.DoesNotContain("player.vimeo.com", md);
    }

    // ---------------------------------------------------------------------------
    // 3. UI stripping — button text is removed, surrounding content is kept
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task ConvertToMarkdownAsync_WithButton_StripsButtonAndKeepsSiblingContent()
    {
        // Arrange
        var content = new LessonContent("L",
            "<p>Lesson text</p><button class='complete-btn'>Mark as completed</button>",
            0, "");

        // Act
        var md = await _converter.ConvertToMarkdownAsync(content);

        // Assert — button text gone, lesson content preserved
        Assert.DoesNotContain("Mark as completed", md);
        Assert.Contains("Lesson text", md);
    }

    // ---------------------------------------------------------------------------
    // 4. Filename — trailing status emoji + time estimate are stripped
    // ---------------------------------------------------------------------------

    [Fact]
    public void GetFilename_WithStatusEmojiAndTime_ProducesCleanFilename()
    {
        // Arrange — title exactly as it appears in the Mighty Networks sidebar
        var content = new LessonContent("🎥 Caso práctico 🟢 — 53 min", "", 33, "");

        // Act
        var filename = _converter.GetFilename(content);

        // Assert
        Assert.Equal("34-🎥-Caso-práctico.md", filename);
    }

    // ---------------------------------------------------------------------------
    // 5. SaveAsync — header (module breadcrumb) and footer (children list) are
    //    written to the file around the converted markdown body
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task SaveAsync_WritesCrossLinksAroundContent()
    {
        // Arrange
        var content  = new LessonContent("Hello", "<h1>Hello</h1>", 0, "");
        var tempDir  = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(tempDir);

        var header = "> **Módulo:** [Parent](parent.md)\n\n";
        var footer = "\n\n---\n\n## Lecciones del módulo\n\n- [Child](child.md)";

        try
        {
            // Act
            var filePath = await _converter.SaveAsync(content, tempDir, header, footer);
            var written  = await File.ReadAllTextAsync(filePath);

            // Assert — structure: header · body · footer
            Assert.StartsWith("> **Módulo:**", written);
            Assert.Contains("# Hello",       written);
            Assert.EndsWith("- [Child](child.md)", written);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }
}
