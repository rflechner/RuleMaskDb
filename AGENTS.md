# Coding guidelines

These guidelines apply to all code written or modified in this repository.

## Use English

- Write code in English, including names of files, classes, methods, functions, variables, and constants.
- Write code comments and documentation in English.
- Choose descriptive names that make intent clear; avoid obscure abbreviations.

## Document the code

- Document public APIs, important contracts, and non-obvious behavior using the language's documentation conventions.
- Explain the intent, constraints, and reasons behind complex decisions rather than restating what the code does.
- Keep documentation and comments up to date when changing the code.

## Prioritize readability

- Favor clear, straightforward implementations over clever or overly compact code.
- Follow the SOLID principles: single responsibility, open/closed, Liskov substitution, interface segregation, and dependency inversion.
- Apply these principles pragmatically; introduce abstractions when they clarify responsibilities or dependencies, and avoid unnecessary complexity.
- Keep classes and modules focused on a cohesive responsibility.

## Express domain concepts explicitly

- Favor a pragmatic Domain-Driven Design (DDD) approach: use the domain's vocabulary consistently in types, methods, and properties.
- Represent meaningful concepts with small, focused objects rather than passing raw collections, tuples, or primitives whose meaning callers must reconstruct.
- Keep invariants, state interpretation, and related behavior inside the object that owns the concept.
- Expose expressive properties and methods so calling code communicates intent. For example, prefer `page.IsEmpty`, `page.RowCount`, and `page.ContinuationKey` to scattered collection checks and indexing.
- Make names reflect what is actually known: a full page `RequiresNextPage`; it does not prove that more rows exist.
- Prefer immutable state and read-only collections where practical, especially for value objects and operation results.
- Introduce domain objects when they clarify the code; keep them focused and avoid unnecessary architectural layers or abstractions.

## Keep methods short and focused

- Give each method or function one clear purpose.
- Split long methods into small, well-named helpers at meaningful responsibility boundaries.
- Avoid deeply nested control flow; use guard clauses and early returns where they improve clarity.
- Prefer readable parameter lists and expressions; break up complex expressions when needed.

## Give the code breathing room

- Separate logical steps with blank lines and keep distinct responsibilities visually separated.
- Use consistent indentation and follow the repository's existing formatting conventions.
- Avoid dense blocks, multiple statements on one line, and unnecessary blank lines.
