---
name: VB.Net
description: VB.NET Coding Standard
---

# VB.Net

## Response Prefix
- Start every response with the user's current local date and time, followed by a newly generated random reference ID, using exactly this format: `dd/MM/yyyy HH:mm (Ref: XXXX-XXXX-XXXX-XXXX):`
- Replace each `X` with a random uppercase hexadecimal character (`0-9` or `A-F`), and generate a fresh reference ID for every response.
- Add a line break immediately after the prefix before writing the response body.

## Project Assumptions
- Generate code that is fully compatible with projects using `Option Strict On` and `Option Infer On`.
- New projects should be created with `Option Strict On` and `Option Infer On` enabled.
- New projects should be created in the same .NET version as existing projects unless explicitly requested otherwise.

## Language Style
- Do not explicitly specify `ByVal`, as it is the default.
- When implementing an expanded property, omit the implicit `value` parameter declaration. Prefer `Set` over `Set(value As Type)`.

## Generics
- When a generic class's type arguments can be inferred from a constructor argument, add a same-named non-generic companion class with Shared `Create` factory method(s) that delegate to `New GenericClass(Of T)(...)`, so callers can write `GenericClass.Create(arg)` instead of spelling out `New GenericClass(Of T)(arg)`.

## Available Libraries & Technologies
- Do not use libraries or technologies that are not already present in the project unless explicitly requested.

#### Guidelines:
- Prefer using these libraries over reimplementing equivalent functionality.
- Do not suggest replacing them with alternative libraries unless specifically requested or there is a clear technical reason.
- Assume all required references already exist.

## General
- Generate production-quality, maintainable code.
- Preserve existing comments unless instructed otherwise.
- Preserve the existing structure unless there is a clear improvement to readability, maintainability or performance.
- Keep solutions simple, readable and efficient.
- Minimise unrelated formatting or refactoring when modifying existing code.
- Preserve existing layout, naming conventions and formatting wherever practical, making the smallest change necessary to satisfy the request.
- Follow the surrounding code style when modifying existing code.

## Variables
- Prefer local type inference (`Dim x = ...`) wherever the inferred type is obvious.
- Use explicit types only when required by the language or when they significantly improve readability.
- Use the most specific type practical.
- Keep variable scope as small as possible.
- Use meaningful, descriptive names.
- Prefer `ReadOnly` where appropriate.
- Do not use late binding.
- Do not rely on implicit narrowing conversions.
- Naming:
  - Name **all variables, including local variables, method parameters, constructor parameters, and exception variables**, using **UpperCamelCase (PascalCase)**.
  - Exceptions:
    - **Iteration variables, lambda parameters, and other short-lived contextual variables** (for example `person` in `For Each person In People`, or `x` in a lambda) should use **lowerCamelCase**.
  - Backing fields:
    - Prefer auto-implemented properties unless custom logic or a backing field is required.
    - When an expanded property requires a backing field, prefix the property name with an underscore while preserving the property's casing (for example, `_Name` for the `Name` property).

## Strings
- Do not use `String.Format` unless explicitly requested or required for compatibility.
- Prefer interpolated strings (`$"..."`) for all formatted string construction.
- Prefer interpolated strings over string concatenation where they improve readability.

## Resource Management
- Use `Using` statements whenever working with `IDisposable` objects owned by the current method.
- Ensure resources are disposed correctly, even when exceptions occur.

## Formatting
- Use implicit line continuation wherever possible.
- Avoid the `_` line continuation character unless required by the language.
- Do not unnecessarily split short statements across multiple lines.
- Keep related expressions visually grouped.
- Preserve existing indentation and formatting style unless improving readability.

## Method Calls
- Prefer keeping method calls on a single line whenever they remain reasonably readable.
- Introduce line breaks only when doing so materially improves readability, such as when:
  - one or more arguments are long;
  - an argument contains a lambda expression, LINQ expression or object initialiser;
  - the overall call becomes difficult to read due to its length.
- Do not force every argument onto its own line.
- Short arguments may remain on the same line even when another argument is wrapped.
- When wrapping:
  - Keep the opening parenthesis on the first line.
  - Vertically align continuation lines beneath the first argument.
  - Wrap only the arguments that benefit from it where practical.
Example:
```vb
Dim ul = i00CodeLib.UsingLambda(Function(x) Return AVeryLongExpressionThatIsJustEasierToReadIfItIsOnItsOwnLine(),
                                Function(x) Return AnotherVeryLongExpressionThatIsJustEasierToReadIfItIsOnItsOwnLine()) 
```

Another acceptable example:
```vb
Foo(1, 2, 3,
    Function(x) Return AVeryLongExpressionThatIsJustEasierToReadIfItIsOnItsOwnLine(), 
    4, 5)
```

## Fluent Method Chains / LINQ
- Keep short fluent method chains on a single line.
- When a fluent method chain becomes long or difficult to read, place each chained method on its own line.
- Vertically align chained methods for readability.
Example:
```vb
Dim Test = dbml.TestData.Where(Function(x) x.DataType = RequestedType).
                         SelectMany(Function(x) x.SubData).
                         Where(Function(x) x.ID = "1234").
                         FirstOrDefault()
```

## Control Flow
- Prefer early returns over deeply nested `If` statements.
- Keep nesting to a minimum.
- Use guard clauses where they improve readability.

## Booleans & Flags
- When checking whether a Boolean is `False`, use an explicit comparison such as `x = False` rather than `Not x`.
- When checking flag enum values, prefer `.HasFlag()` over bitwise comparisons where practical.
- Express flag enum values using bit-shifted values for readability and consistency, such as `0`, `1 << 0`, `1 << 1`, and `1 << 2`.

## LINQ
- Use LINQ where it improves readability.
- Avoid unnecessarily complex LINQ expressions when a simple loop is clearer.
- Materialise queries only when necessary.

## Error Handling
- Catch only exceptions that can be handled meaningfully.
- Do not swallow exceptions silently.
- Preserve stack traces when rethrowing exceptions (`Throw`, not `Throw ex`).

## Comments
- Preserve existing comments.
- Add comments only when they explain intent, assumptions or non-obvious behaviour.
- Do not add comments that merely describe what the code already states.

## Performance
- Avoid unnecessary allocations.
- Avoid repeated enumeration of collections.
- Cache expensive results when appropriate.
- Prefer appropriate algorithms and data structures over premature micro-optimisations.

## Code Changes
- Preserve existing behaviour unless instructed otherwise.
- Keep changes focused on the requested task.
- Do not introduce unnecessary abstractions.
- Prefer clear, maintainable code over clever or overly compact solutions.
- When modifying existing code, return the smallest practical diff rather than rewriting unrelated code.

## Commit Messages
- Unless the user specifies a different format for a given commit, structure the message as one section per top-level project touched:
  ```
  {ProjectPath}:
  - Point 1
  - Point 2
  ```
- **The header and every bullet must be separated by a real line-break character (`\n`), never joined onto one line.** Do not write the message as one flowing paragraph with `-` used as an inline separator between clauses (e.g. `ChunkedStream: - Point 1 - Point 2` is wrong even though it contains the right words). When building the message through a shell command (a heredoc passed to `-m`, for example), put an actual newline between `{ProjectPath}:` and the first bullet, and between every subsequent bullet.
- `git commit`'s own terminal confirmation line and `git log --oneline` are **not** reliable checks here: both display (or echo back) a correctly newline-separated, multi-bullet message as one long visual line whenever there is no blank line separating a short subject from the body, which is the normal shape of these messages. A commit that looks collapsed in that output can still be correctly formatted underneath - and, just as importantly, a genuinely broken (single-line) commit will *also* look the same in that output, so it cannot be used to catch the mistake either. Always verify the raw stored message directly before considering a commit (or an amend) done, e.g. `git log -1 --format=%B | cat -A` (or `git show -s --format=%B`), and confirm each bullet ends its own line (a trailing `$` under `cat -A`).
- `ProjectPath` is the project's path relative to the solution root, with backslashes and with any leading underscore stripped from each folder name (e.g. `_Samples\EmbeddedFileSystemSample` becomes `Samples\EmbeddedFileSystemSample`).
- If the project sits at the solution root (e.g. `ChunkedStream`), do not put a slash in front of it.
- If the change does not directly relate to any one project (e.g. repo-wide tooling or instructions), use `[General]` in place of a project name.
- If the commit creates, updates, or otherwise relates to a plan document (e.g. under `.claude/plans`), use `[Plans]` in place of a project name, with one bullet per plan touched. Name each plan by its full path relative to the repo root, starting with a leading slash (e.g. `\.claude\plans\deduplication-feature.md`). A `[Plans]` section always comes first, before every other section. Also add a bullet - `{Verb} Plan {FileNameWithoutExtension}` (e.g. `Added Plan deduplication-feature`) - to the bullet list of every project section the plan relates to, even if that commit didn't otherwise touch that project, so the connection is visible from that section too.
- Changes to a project's own test project (e.g. `_Tests\UnitTests`) are folded into the main project's section rather than given their own section.
- List every touched project as its own section (single line break), each with its own bullet list. `[Plans]` always comes first if present; the remaining sections (every project section and `[General]`, if present) are then ordered however is most logical for the commit - `[General]` does not have to come last, and a project section can come before or after it.
- Each bullet should be in the most logical order, and is a single, short, plain sentence describing one thing that changed - simple enough that anyone reading it gets the gist without needing more context. Do not pack multiple ideas into one bullet or explain implementation detail.
- If the user asks to commit and gives only a job number (e.g. `#12345`) as the commit comment, use that job number as the first line, followed by a single line break, then the standard message format described above.
- If the branch being committed to has a name whose final path segment is a number (e.g. `AnyPath/1234` or `AnyPath/1234.3`), automatically prepend that job number as the first line, followed by a single line break, then the standard message format described above. The job number is the leading integer of the final path segment, ignoring any `.n` suffix, so both `AnyPath/1234` and `AnyPath/1234.3` give `#1234`.
- Never add a co-author, "Generated with"/AI-attribution line, or any similar credit/tool footer to a commit message.

## General Non-Code Guidance
- Keep answers as brief as possible without skipping over information.
- Use the Oxford comma in prose, comments, documentation, and examples unless preserving existing text.