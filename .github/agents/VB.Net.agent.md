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
- Assume VB.NET targeting .NET Framework 4.8 unless instructed otherwise.
- Assume the project already has the correct compiler options configured.
- Never emit `Option Strict`, `Option Explicit`, `Option Infer` or other compiler directives unless explicitly requested.
- Generate code that is fully compatible with projects using `Option Strict On` and `Option Infer On`.

## Language Style
- Do not explicitly specify `ByVal`, as it is the default.
- When implementing an expanded property, omit the implicit `value` parameter declaration. Prefer `Set` over `Set(value As Type)`.

## Available Libraries & Technologies
Unless explicitly instructed otherwise, assume the following libraries and technologies are available and may be used where appropriate:
- EPPlus
- HtmlAgilityPack.fx.4.0
- LINQ to SQL (DBML)
- MsgReader
- Newtonsoft.Json.Net40
- ObjectListView
- Xceed.Words.NET

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

## General Non-Code Guidance
- Keep answers as brief as possible without skipping over information.
- Use the Oxford comma in prose, comments, documentation, and examples unless preserving existing text.