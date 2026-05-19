# Playwright & Scraping Engineering Guidelines

## 1. Resilience and Waits
* **STRICTLY FORBIDDEN:** Using `Thread.Sleep()` or `Task.Delay()` to wait for an element to appear or a page to load.
* **Mandatory:** Use DOM event-based waits:
  * `await page.WaitForURLAsync(...)` after a navigation action.
  * `await page.WaitForSelectorAsync(...)` or `await locator.WaitForAsync()` for dynamic React elements.
  * `await page.WaitForLoadStateAsync(LoadState.NetworkIdle)` only if it is strictly necessary to load all asynchronous content from Mighty Networks before extracting the HTML.

## 2. Locator Strategy
* Always prioritize semantic and accessibility locators over standard CSS and XPath selectors.
* Order of preference:
  1. `Page.GetByRole()`
  2. `Page.GetByText()`
  3. `Page.GetByTestId()` (if the platform exposes `data-testid`)
  4. Structured CSS selectors (e.g., `.left-drawer-region a`) only as a last resort.

## 3. Error Handling and Retries
* The SPA DOM (React) can cause `PlaywrightException` errors (e.g., "Node is detached from document") if the DOM mutates exactly when you attempt to read it.
* Wrap critical navigation and extraction interactions inside `try/catch` blocks.
* If a selector fails due to a timeout, log the error (using `ILogger` or `Console.WriteLine` temporarily) and use `continue` to proceed to the next element; do not crash the entire application execution.

## 4. State Management and Authentication
* Always assume the user is already authenticated via the `auth/state.json` file.
* Never write navigation flows that look for username/password inputs, and do not interact with Google Accounts iframes. If, upon loading the home page, the selector indicating an active session (e.g., a user avatar) is missing, throw an `InvalidOperationException` requesting the user to regenerate the `state.json` file manually.

## 5. Content Extraction
* Extract the raw HTML from the target container using `await locator.InnerHTMLAsync()`.
* Ensure you remove or ignore unwanted elements (such as "mark as completed" buttons, overlapping modals, or injected UI components that do not belong to the educational text) before passing the HTML to `ReverseMarkdown`.