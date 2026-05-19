# Skill: playwright-auditor

## Metadata
* **Name:** playwright-auditor
* **Trigger Command:** `/playwright-auditor`
* **Description:** Audits Playwright interaction code to enforce strict resilience, semantic locators, and proper wait strategies, preventing brittle scraping implementations.

---

## 📋 Execution Protocol

When this skill is triggered, or when you are about to finalize modifications in files related to Playwright automation (e.g., `PlaywrightManager.cs`, `CourseNavigator.cs`), you must execute this audit before moving on.

### Step 1: Anti-Pattern Scanning
Scan the target C# file for the following strictly forbidden patterns:
* `Thread.Sleep(...)` or `Task.Delay(...)` used for waiting on the UI or network.
  * **Known legitimate exception:** the polite inter-lesson gap inside `CourseNavigator.ScrapeAsync`, controlled by the `delayMs` parameter / `--delay` CLI flag. Any *other* `Task.Delay` is a violation.
* Hardcoded CSS selectors or XPath where semantic locators (e.g., text, role, aria-attributes) are feasible.
  * The Mighty Networks DOM exposes neither `data-testid` nor stable roles for sidebar items, so the existing `a[class*='coursework-mighty-menu-list-item']` and `.detail-layout-description` selectors live in `CourseNavigator.cs` as named constants. Flag *new* string-literal selectors scattered across the codebase; do **not** flag those two constants unless the user is changing them.
* Missing `await` on Playwright async calls.
* `BrowserType.LaunchAsync` (non-persistent) being introduced in production code — the project relies on `LaunchPersistentContextAsync` for SSO cookie reuse. Headed `LaunchAsync` is only acceptable inside `tools/LoginHelper`.

### Step 2: Locator Evaluation
Verify the locator strategy:
* Ensure `Page.GetByRole()`, `Page.GetByText()`, or `Page.GetByTestId()` are prioritized.
* If a generic CSS selector (like `.left-drawer-region a`) is used, evaluate if it's the absolute last resort and flag it for review.

### Step 3: Resilience & Error Handling Check
* Check if DOM reads and clicks are wrapped in `try/catch` blocks (specifically catching `PlaywrightException` or `TimeoutException`).
* Verify that the scraper does not crash the entire process on a single element failure (it should log the error and gracefully continue/skip to the next item, mirroring the *stub-emission* fallback in `TryExtractAsync`).
* Verify that session-expiration checks (URL contains `/sign_in`, `/login`, or off-host) are **not** swallowed by the per-lesson try/catch — they must propagate and abort the run with a clear message pointing at `tools/LoginHelper`.

### Step 4: Audit Report Presentation
Present the audit findings to the user using the following template. DO NOT modify the codebase yet.

```text
### 🛡️ Playwright Audit Report: [Filename]

1. **🚨 Violations Found:**
   * [List of forbidden patterns, e.g., "Line 45: Task.Delay(2000) used instead of WaitForSelectorAsync."]
   * *(If none: "No strict violations found.")*

2. **⚠️ Locator Warnings:**
   * [List of brittle locators, e.g., "Line 50: Used Page.Locator('.class') instead of GetByRole."]
   * *(If none: "Locator strategy looks solid.")*

3. **💡 Recommended Fixes:**
   * [Provide the specific C# code snippets to fix the flagged issues.]

**Do you want me to apply these fixes? (Yes/No)**
```

### Step 5: Remediation (Post-Approval Only)
Only if the user answers "Yes", apply the recommended fixes to the code. 
If the user answers "No", ask for clarification on which parts of the audit they disagree with or want to handle differently.

## 🚫 Critical Constraints
* **NEVER** apply automated fixes without presenting the Audit Report (Step 4) first.
* **MUST** strictly adhere to the rules defined in `.claude/rules/scraping-guidelines.md`.

## 💡 Example of Remediation
If the scanned code looks like this:

```csharp
await Task.Delay(3000); // Wait for React to load
var title = await page.Locator(".lesson-title").InnerTextAsync();
```

You must flag it as a violation and recommend a fix utilizing C# 14 and explicit waits:

```csharp
// Recommended Fix:
var titleLocator = page.Locator(".lesson-title");
await titleLocator.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible });
var title = await titleLocator.InnerTextAsync();

// OR even better, using semantic locators if applicable:
// var title = await page.GetByRole(AriaRole.Heading, new() { Level = 1 }).InnerTextAsync();
```