# MightyScraper - C# Playwright Markdown Exporter

## 🎯 Project Context
This tool is a C# .NET console application designed to scrape course content from a Mighty Networks-based Single Page Application (SPA) and export it as structured Markdown (`.md`) files. The target platform relies heavily on React and dynamic rendering.

## 🛠️ Tech Stack
* **Language & Framework:** C# 13 / .NET 9 (target framework `net9.0` in the `.csproj`).
* **Browser Automation:** `Microsoft.Playwright` 1.59.x.
* **HTML to Markdown:** `ReverseMarkdown` (GitHub-flavored) + `HtmlAgilityPack` for pre-cleaning.
* **Hosting / DI:** `Microsoft.Extensions.Hosting` generic host with constructor-injected services (`IPlaywrightManager`, `ICourseNavigator`, `IMarkdownConverter`, `ScraperApp`).
* **Auxiliary tooling:** `tools/LoginHelper` — separate executable for the one-off interactive Google SSO login that creates the persistent browser profile under `./auth/`.

## 🏗️ Current Architecture (two-phase pipeline)
The scraper is organized around a **discover-then-stream** pipeline. Do not collapse these phases into a single pass — the cross-link map between lessons depends on the structure being known up front.

1. **Phase 1 — Discovery (`ICourseNavigator.GetStructureAsync`):** one page load on the course URL, JS-side traversal of `li.menu-list-item-container` via `Page.EvaluateAsync`, returns a flat `IReadOnlyList<LessonMeta>` with `Href / Title / Order / ParentHref / IsSection`. Section vs. lesson is detected by the CSS classes `level-0` / `is-section` (sections) and `level-2` (child lessons).
2. **Phase 2 — Progressive scrape (`ICourseNavigator.ScrapeAsync`):** returns `IAsyncEnumerable<LessonContent>`. `ScraperApp` consumes it lesson-by-lesson, builds breadcrumb header + children footer using the pre-computed `href → filename` map, and writes each `.md` immediately. Interruptions are recoverable via `--skip N`.

## 🧠 Development Philosophy (Spec-Driven)
* **Think Before Coding:** Adopt a spec-driven development approach. Before implementing a new feature or complex logic, outline the intended behavior, state the expected inputs/outputs, and write the necessary interfaces or tests.
* **Modern C#:** Favor `record` types for data models (see `LessonMeta`, `LessonContent`), primary constructors on services, collection expressions (`[]`), and `IAsyncEnumerable` for streaming the scrape.
* **Clean Code:** Maintain strict separation of concerns. `Program.cs` parses CLI and wires DI; `ScraperApp` orchestrates the pipeline; `PlaywrightManager` handles browser lifecycle; `CourseNavigator` handles DOM interactions; `MarkdownConverter` handles HTML cleanup, conversion, naming and file writing.

## 🔐 Authentication Strategy (CRITICAL: GOOGLE SSO)
The target platform uses Google Single Sign-On (SSO). **DO NOT attempt to automate the login flow.** Google's anti-bot protections will block Playwright.
* **Implementation:** `PlaywrightManager` uses `Chromium.LaunchPersistentContextAsync` pointing to `./auth/`, with `--disable-blink-features=AutomationControlled` and `IgnoreDefaultArgs = ["--enable-automation"]` to hide automation fingerprints. A `state.json` is also written by the login helper as a cookie-only fallback.
* **Login helper:** `tools/LoginHelper/Program.cs` runs **headed** (preferring system Chrome via `Channel = "chrome"`, falling back to bundled Chromium), waits for the user to log in manually, then saves the profile. This is the only supported way to (re)create credentials.
* **Session validation:** Both `GetStructureAsync` and `TryExtractAsync` re-check `page.Url` after navigation. If it contains `/sign_in`, `/login`, or is off the expected `training.lidr.co` host, throw `InvalidOperationException` with the exact command the user must run.
* **Agent Behavior:** If the session is invalid or the auth directory is missing, **stop execution gracefully** and tell the user to re-run the login helper. Never write code that types credentials into Google login fields.

## 🕸️ Playwright & Scraping Rules
* **No Magic Sleeps for UI waits:** `Task.Delay()` is strictly forbidden for waiting on UI elements or page loads. The only legitimate `Task.Delay` is the optional polite gap between lessons in `ScrapeAsync`, controlled by the `--delay` CLI flag (default 3000 ms, set to 0 to disable). New code must not introduce additional delays.
* **Explicit Waits:** Use `Locator.WaitForAsync()`, `Page.WaitForSelectorAsync()`, `Page.WaitForURLAsync()`, or `Page.WaitForLoadStateAsync(LoadState.NetworkIdle / DOMContentLoaded)` for React's dynamic DOM updates.
* **Resilience:** Wrap navigation and extraction per-lesson in `try/catch`. On content-selector timeout, log and emit a *stub* `LessonContent` (empty HTML) so the file is still produced and cross-links survive. Hard-fail only on session expiration.
* **DOM Selectors:** Prefer semantic locators (`GetByRole`, `GetByText`, `GetByTestId`). Current real-world selectors (Mighty Networks):
  * Sidebar items: `a[class*='coursework-mighty-menu-list-item']` (and the JS pass uses `li.menu-list-item-container` for hierarchy).
  * Lesson content container: `.detail-layout-description`.
  * If these change, update the constants at the top of `CourseNavigator.cs` — they are the single source of truth. The DOM-diagnostic dump in `LogDomDiagnosticAsync` is the intended escape hatch when a selector misses.

## 📝 Markdown Conversion Rules
* Extract only the lesson content container; everything else (sidebars, headers, navigation) is filtered by the selector itself.
* `MarkdownConverter` strips UI chrome before conversion via XPath (`//button`, `*[class*='modal']`, `*[class*='complete-btn']`, `*[class*='overlay']`). Embedded iframes (Vimeo / YouTube `/embed/` / Loom) are rewritten to public watch URLs and replaced by `<a>` tags so the link survives the Markdown conversion.
* `ReverseMarkdown` is configured GitHub-flavored, with `UnknownTags = Drop`, `RemoveComments = true`, `SmartHrefHandling = true`.
* File naming: `NN-Sanitised-Title.md` (1-indexed, `D2` padded). Title cleaning strips trailing status emojis (🟢🔴🟡) and the `— NN min` suffix before sanitising path-invalid characters and spaces → `-`.
* Cross-links: `ScraperApp` builds an `href → filename` dictionary from the discovery phase **before** any file is written, then injects a breadcrumb header (`> **Módulo:** [Padre](padre.md)`) on child lessons and a "Lecciones del módulo" footer listing children on section lessons.

## 🤖 Claude Code Instructions
1.  When creating new files, adhere to the folder structure: `/src/Core`, `/src/Models`, `/tests/MightyScraper.Tests`, `/tools/<HelperName>`.
2.  If you need to install NuGet packages, use the terminal commands directly (e.g., `dotnet add package Microsoft.Playwright`).
3.  Target `net9.0` in every `.csproj` (current state of the repo). Do not bump TFMs without the user asking.
4.  Before doing large refactors (e.g., changing the discover/scrape split, swapping `ReverseMarkdown`, restructuring DI), explain your plan briefly first.
5.  Run `dotnet build` frequently to ensure no compilation errors are introduced; run `dotnet test` after touching `MarkdownConverter` or its inputs.
6.  Never commit or alter the `auth/` directory — it contains personal Google cookies.