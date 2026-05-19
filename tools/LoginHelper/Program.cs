// LoginHelper — run once from the solution root to authenticate and save the session.
// Usage (from D:\src\mighty-scrapper):
//   dotnet run --project tools/LoginHelper -- <mighty-networks-base-url>
// Example:
//   dotnet run --project tools/LoginHelper -- https://yourcommunity.mn.co

using Microsoft.Playwright;

if (args.Length == 0)
{
    Console.Error.WriteLine("ERROR: Provide the Mighty Networks base URL as the first argument.");
    Console.Error.WriteLine("  dotnet run --project tools/LoginHelper -- https://yourcommunity.mn.co");
    return 1;
}

var baseUrl  = args[0];
var authDir  = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "auth"));
var statePath = Path.Combine(authDir, "state.json");

// Resolve auth/ relative to the solution root regardless of where dotnet run is called from
if (!Path.IsPathRooted(authDir) || !Directory.Exists(Path.GetDirectoryName(authDir)!))
    authDir = Path.GetFullPath("auth");

Directory.CreateDirectory(authDir);

Console.WriteLine("=== MightyScraper — Login Helper ===");
Console.WriteLine();
Console.WriteLine($"  Browser profile : {authDir}");
Console.WriteLine($"  State file      : {statePath}");
Console.WriteLine($"  Target URL      : {baseUrl}");
Console.WriteLine();
Console.WriteLine("A Chrome browser window will open.");
Console.WriteLine("Complete the Google SSO login manually, wait until you can see the");
Console.WriteLine("course content, then come back here and press ENTER.");
Console.WriteLine();

using var playwright = await Playwright.CreateAsync();

// Headed + persistent profile so Google cookies survive between runs.
// Using 'chrome' channel when available gives the best Google SSO compatibility;
// falls back gracefully to bundled Chromium if Chrome is not installed.
IBrowserContext context;
try
{
    context = await playwright.Chromium.LaunchPersistentContextAsync(authDir, new()
    {
        Headless = false,
        Channel  = "chrome",
        // Remove Playwright's automation banner and disable the JS flag that Google checks
        Args            = ["--disable-blink-features=AutomationControlled"],
        IgnoreDefaultArgs = ["--enable-automation"],
    });
}
catch (PlaywrightException)
{
    Console.WriteLine("  (System Chrome not found — falling back to bundled Chromium)");
    context = await playwright.Chromium.LaunchPersistentContextAsync(authDir, new()
    {
        Headless          = false,
        Args              = ["--disable-blink-features=AutomationControlled"],
        IgnoreDefaultArgs = ["--enable-automation"],
    });
}

await using (context)
{
    var page = context.Pages.Count > 0 ? context.Pages[0] : await context.NewPageAsync();

    await page.GotoAsync(baseUrl);

    Console.Write("Waiting for you to log in... press ENTER when done: ");
    Console.ReadLine();

    // Verify the session looks valid before saving
    var title = await page.TitleAsync();
    var url   = page.Url;
    Console.WriteLine();
    Console.WriteLine($"  Current page : {title}");
    Console.WriteLine($"  Current URL  : {url}");
    Console.WriteLine();

    // StorageState captures cookies + localStorage — used by the main scraper as fallback
    await context.StorageStateAsync(new() { Path = statePath });
}

Console.WriteLine($"Session saved to  : {statePath}");
Console.WriteLine($"Browser profile   : {authDir}");
Console.WriteLine();
Console.WriteLine("You can now run the main scraper:");
Console.WriteLine("  dotnet run --project src -- <course-url>");

return 0;
