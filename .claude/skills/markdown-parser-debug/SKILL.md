# Skill: markdown-parser-debug

## Metadata
* **Name:** markdown-parser-debug
* **Trigger Command:** `/markdown-parser-debug`
* **Description:** Creates an isolated debugging loop for the ReverseMarkdown HTML-to-Markdown conversion, allowing rapid refinement of parsing rules without running the full Playwright browser suite.

---

## 📋 Execution Protocol

When this skill is triggered, you must help the user debug and refine the HTML-to-Markdown conversion logic using the `ReverseMarkdown` library.

### Step 1: Input Gathering
Ask the user to provide a snippet of the raw HTML they want to convert, or ask for the file path if they have saved the raw HTML locally.

### Step 2: Isolated Test Setup
Create a temporary C# script or a specific test in the `tests/MightyScraper.Tests/` directory that isolates the `MarkdownConverter` logic.
* Initialize the `ReverseMarkdown.Converter` with explicit configuration (e.g., `Config.UnknownTagsOption.Drop`, `Config.GithubFlavored = true`).
* Feed the user's raw HTML into the converter.

### Step 3: Execution & Output
Run the isolated test or script and capture the generated Markdown output.

### Step 4: Verification Checkpoint
Present the results to the user using the exact template below. DO NOT modify the main application code yet.

```text
### 🔬 Markdown Parser Debug Result

**1. ⚙️ Current Configuration:**
   * [List active ReverseMarkdown config options, e.g., UnknownTags = Drop]

**2. 📄 Generated Markdown:**
----------
[Insert generated markdown here]
----------

**3. 🤔 Analysis & Recommendations:**
   * [Identify any lingering HTML artifacts, unwanted navigation elements, or formatting issues]
   * [Suggest C# code changes: e.g., pre-processing the HTML using Regex or AngleSharp/HtmlAgilityPack to remove `<div class="sidebar">` before passing it to ReverseMarkdown]

**Does the output look correct, or should we refine the pre-processing/configuration? (Looks Good / Refine)**
```

### Step 5: Refinement (Iterative Loop)
* If the user answers "Refine" (and provides feedback):
  1. Update the conversion logic (e.g., add HTML node removals or adjust ReverseMarkdown config).
  2. Re-run the conversion.
  3. Present the Step 4 checkpoint again.
* If the user answers "Looks Good":
  1. Apply the finalized configuration and pre-processing logic to the main `src/Core/MarkdownConverter.cs` file.

---

## 🚫 Critical Constraints
* **NEVER** run the full Playwright scraping suite during this debug loop. This must be an isolated string-to-string transformation test to save time and resources.
* Be proactive in suggesting pre-processing steps. Often, `ReverseMarkdown` needs help by stripping out raw UI containers (like `#left-drawer-region` or `#alert-bar-region`) before the conversion happens.