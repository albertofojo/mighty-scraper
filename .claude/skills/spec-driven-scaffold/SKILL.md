skill_content = """# Skill: spec-driven-scaffold

## Metadata
* **Name:** spec-driven-scaffold
* **Trigger Command:** `/spec-driven-scaffold`
* **Description:** Enforces a strict spec-driven development loop by forcing the agent to design C# interfaces, data models (records), and test skeletons before writing any implementation code.

---

## 📋 Execution Protocol

When this skill is triggered or when a new core component/feature is requested, you must follow this exact multi-step protocol before writing any business logic.

### Step 1: Requirements Gathering & Clarification
1. Analyze the requested feature or module.
2. Identify all data inputs, external dependencies (like Playwright `IPage`), and expected outputs.
3. Define the boundary conditions and failure modes (e.g., timeout exceptions, element missing).

### Step 2: Contract & Data Modeling Design (The Spec)
Create or update files in the `src/Models/` and `src/Core/` directories containing **ONLY** abstractions:
1. **Data Models:** Use C# 14 strongly-typed `record` structures for immutable data exchange.
2. **Interfaces:** Define clear, async-first interface contracts (e.g., `ICourseNavigator`, `IMarkdownConverter`).
3. **No Implementation:** Do not write any class bodies or method implementations at this stage. Use `throw new NotImplementedException();` if a stub is strictly necessary for compilation.

### Step 3: Test Skeleton Generation
Before implementing the interface, write the corresponding test file inside the tests/MightyScraper.Tests/ directory, ensuring the test project uses xUnit:
1. Create a test skeleton matching the interface methods.
2. Define test cases for happy paths (successful scrape, correct conversion).
3. Define test cases for edge cases (React DOM detachment, empty content, missing elements).
4. Assertions must reflect the final expected behavior.

### Step 4: User Verification & Checkpoint
Stop execution and present the designed interfaces and test cases to the user. Use the following template to request approval:

```text
### 🔍 Spec-Driven Checkpoint: [Component Name]

I have drafted the specifications and test definitions for the upcoming feature. Please review before I proceed with the implementation:

1. **Interfaces & Models Created:**
   * `[Path to Interface/Model]` -> Brief explanation of responsibility.

2. **Test Cases Defined:**
   * `[Test Case Name]` -> Verifies [Behavior].

**Do you approve these contracts and test scopes? (Yes/No)**
```

### Step 5: Implementation (Post-Approval Only)
Only after the user provides explicit approval, create the concrete implementation classes (e.g., `CourseNavigator.cs`) and fill in the logic to satisfy both the interfaces and make the written tests pass.

### Step 6: Handling Rejection (Iterative Refinement)
If the user answers "No" to the Step 4 checkpoint:
1. DO NOT proceed to implementation.
2. Politely ask the user to provide specific feedback on what needs to be changed in the interfaces, data models, or test cases.
3. Once the user provides feedback, loop back to Step 2, update the abstractions, and present the checkpoint (Step 4) again.

## 🚫 Critical Constraints
* **NEVER** skip the checkpoint. Writing implementation code concurrently with the interface definition violates this skill.
* **NEVER** use generic types or loose string dicts where strongly-typed C# 14 features can be utilized.
* All interface methods interacting with Playwright automation **MUST** return `Task` or `Task<T>` and accept a `CancellationToken` if lifecycle management is required.

## 💡 Example of Expected Output (Step 2)
When generating the spec, aim for modern, concise C# that mirrors the existing models. The real contracts in the repo look like this — use them as the canonical reference and only extend them when the new feature genuinely needs new fields:

```csharp
// src/Models/LessonMeta.cs — discovery-phase metadata
public record LessonMeta(
    string  Href,
    string  Title,
    int     Order,
    string? ParentHref,
    bool    IsSection);

// src/Models/LessonContent.cs — scrape-phase payload, carries raw HTML plus identity
public record LessonContent(
    string  Title,
    string  RawHtml,
    int     Order,
    string  SectionTitle,
    string  Href       = "",
    string? ParentHref = null,
    bool    IsSection  = false);

// src/Core/ICourseNavigator.cs — two-phase contract (discover → stream-scrape)
public interface ICourseNavigator
{
    Task<IReadOnlyList<LessonMeta>> GetStructureAsync(
        string courseUrl,
        CancellationToken cancellationToken = default);

    IAsyncEnumerable<LessonContent> ScrapeAsync(
        IReadOnlyList<LessonMeta> structure,
        int delayMs = 3_000,
        CancellationToken cancellationToken = default);
}

// src/Core/IMarkdownConverter.cs — conversion + naming + persistence
public interface IMarkdownConverter
{
    Task<string> ConvertToMarkdownAsync(LessonContent content, CancellationToken cancellationToken = default);
    string GetFilename(LessonContent content);
    Task<string> SaveAsync(
        LessonContent content,
        string outputDirectory,
        string? header = null,
        string? footer = null,
        CancellationToken cancellationToken = default);
}
```

Guidelines when extending:
* Keep the `LessonMeta` → `LessonContent` split: discovery is cheap, scraping is expensive, and the cross-link map is built off the metadata before scraping starts.
* Any new field on `LessonContent` must also be wired through `CourseNavigator.TryExtractAsync` (producer) and `MarkdownConverter.SaveAsync` (consumer).
* The legacy aggregate records (`CourseLesson`, `CourseSection`, `CourseStructure`) are not used by the active pipeline; prefer evolving `LessonMeta`/`LessonContent` rather than reviving them.