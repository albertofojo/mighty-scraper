using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using MightyScraper.Core;

const string HelpText = """
MightyScraper — scraper de cursos Mighty Networks a Markdown (+ vídeos y transcripciones)

Uso:
  dotnet run --project src -- <course-url> [opciones]

Modos (excluyentes; sin ninguno, hace scrape):
  (por defecto)              Scrape incremental → un .md por lección + index.md
                             (salta las lecciones cuyo .md ya existe)
  --check                    Informe de estado curso-vs-disco (qué falta). No descarga nada.
  --diagnose-media [N]       Inspecciona los vídeos de N lecciones con vídeo (streams +
                             subtítulos) y escribe _media-diagnostic.json. No descarga. (def. N=5)
  --download-media [modo]    Descarga vídeos y/o subtítulos vía yt-dlp, enriquece los .md y
                             regenera index.md. modo = both | video | subs (def. both).

Opciones:
  --skip N                   Salta las primeras N lecciones (scrape y descarga). (def. 0)
  --delay MS                 Pausa entre lecciones durante el scrape, en ms. (def. 3000)
  --force                    Re-scrapea también las lecciones cuyo .md ya existe.
  --limit N                  Máximo de vídeos a descargar (solo con --download-media). (def. 0 = sin límite)
  -h, --help                 Muestra esta ayuda.

Requisitos de descarga:
  yt-dlp (pip install -U yt-dlp) y, para vídeo, ffmpeg (winget install Gyan.FFmpeg).
  Autenticación: dotnet run --project tools/LoginHelper -- <course-url>

Ejemplos:
  dotnet run --project src -- https://training.lidr.co/spaces/21956761/content
  dotnet run --project src -- https://training.lidr.co/spaces/21956761/content --check
  dotnet run --project src -- https://training.lidr.co/spaces/21956761/content --diagnose-media 5
  dotnet run --project src -- https://training.lidr.co/spaces/21956761/content --download-media both
  dotnet run --project src -- https://training.lidr.co/spaces/21956761/content --download-media subs --limit 1
""";

if (args.Contains("--help") || args.Contains("-h"))
{
    Console.WriteLine(HelpText);
    return 0;
}

if (args.Length == 0)
{
    Console.Error.WriteLine(HelpText);
    return 1;
}

var courseUrl = args[0];

var skipCount = 0;
var skipIdx   = Array.IndexOf(args, "--skip");
if (skipIdx >= 0 && skipIdx + 1 < args.Length && int.TryParse(args[skipIdx + 1], out var n))
    skipCount = n;

var delayMs  = 3_000;
var delayIdx = Array.IndexOf(args, "--delay");
if (delayIdx >= 0 && delayIdx + 1 < args.Length && int.TryParse(args[delayIdx + 1], out var d))
    delayMs = d;

// Cap on how many videos to download (0 = no limit). Useful for a quick 1-video test.
var maxVideos = 0;
var limitIdx  = Array.IndexOf(args, "--limit");
if (limitIdx >= 0 && limitIdx + 1 < args.Length && int.TryParse(args[limitIdx + 1], out var lim))
    maxVideos = lim;

// --force: re-scrape lessons even if their .md already exists (default is incremental).
var force = Array.IndexOf(args, "--force") >= 0;

// --check: read-only status report (course vs disk). No scraping, no downloads.
var check = Array.IndexOf(args, "--check") >= 0;

// Diagnostic mode: inspect what the video players expose (download streams + captions),
// sampling N video-bearing lessons. No content is downloaded.
var diagnoseIdx   = Array.IndexOf(args, "--diagnose-media");
var diagnoseMedia = diagnoseIdx >= 0;
var diagnoseSample = 5;
if (diagnoseMedia && diagnoseIdx + 1 < args.Length && int.TryParse(args[diagnoseIdx + 1], out var s))
    diagnoseSample = s;

// Download mode: harvest each lesson's Vimeo embeds and pull video and/or subtitles via yt-dlp.
var downloadIdx   = Array.IndexOf(args, "--download-media");
var downloadMedia = downloadIdx >= 0;
var mediaMode     = MediaMode.Both;
if (downloadMedia && downloadIdx + 1 < args.Length)
    mediaMode = args[downloadIdx + 1].ToLowerInvariant() switch
    {
        "video" => MediaMode.Video,
        "subs"  => MediaMode.Subs,
        _       => MediaMode.Both,
    };

var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddSingleton<IPlaywrightManager, PlaywrightManager>();
builder.Services.AddTransient<ICourseNavigator, CourseNavigator>();
builder.Services.AddTransient<IMarkdownConverter, MarkdownConverter>();
builder.Services.AddTransient<ScraperApp>();
builder.Services.AddTransient<MediaDiagnostic>();
builder.Services.AddTransient<MediaDownloader>();
builder.Services.AddTransient<CourseStatus>();

using var host = builder.Build();

if (check)
{
    var status = host.Services.GetRequiredService<CourseStatus>();
    await status.RunAsync(courseUrl);
}
else if (diagnoseMedia)
{
    var diagnostic = host.Services.GetRequiredService<MediaDiagnostic>();
    await diagnostic.RunAsync(courseUrl, skipCount, diagnoseSample);
}
else if (downloadMedia)
{
    var downloader = host.Services.GetRequiredService<MediaDownloader>();
    await downloader.RunAsync(courseUrl, skipCount, mediaMode, maxVideos);
}
else
{
    var app = host.Services.GetRequiredService<ScraperApp>();
    await app.RunAsync(courseUrl, skipCount, delayMs, force);
}

return 0;
