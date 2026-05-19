using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using MightyScraper.Core;

if (args.Length == 0)
{
    Console.Error.WriteLine("Usage: MightyScraper <course-url> [--skip N]");
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

var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddSingleton<IPlaywrightManager, PlaywrightManager>();
builder.Services.AddTransient<ICourseNavigator, CourseNavigator>();
builder.Services.AddTransient<IMarkdownConverter, MarkdownConverter>();
builder.Services.AddTransient<ScraperApp>();

using var host = builder.Build();

var app = host.Services.GetRequiredService<ScraperApp>();
await app.RunAsync(courseUrl, skipCount, delayMs);

return 0;
