# Source formatting

The root [.editorconfig](../../.editorconfig) applies to authored DB, Studio,
AppModel, shared-library and test code. StyleCop is a private build-time dependency
for all projects; its enabled layout rules and the SDK's code-style checks run in
normal builds.

[Git attributes](../../.gitattributes) keep authored source line endings consistent
on Windows and Unix checkouts without imposing new conversion rules on imported
or generated material.

- Use four spaces in C# and project XML, LF line endings and a final newline.
- Use Allman braces, with braces around control-flow bodies even for one statement.
- Expand single-line blocks/statements and separate elements with blank lines.
- Remove trailing whitespace from code. Preserve Markdown hard-break spaces.
- Namespace, accessibility and member-ordering preferences remain advisory.
  Formatting does not reorder initialization, change APIs or introduce new syntax.

With the pinned SDK and browser workload installed, restore the full solution and
run the formatter from the repository root:

```powershell
dotnet restore EntityForge.slnx
./eng/Format-Code.ps1
./eng/Format-Code.ps1 -Verify
```

The script applies only the agreed layout/brace diagnostics. It preserves generated
protobuf/parser sources, third-party implementations, Unicode data and fixtures.
Imported files retain their original attribution and licence text. Regenerate
generated files through their owning generator rather than formatting them.

Review the diff and run the [relevant builds and tests](building.md). Formatter
success alone does not establish behavioral equivalence. When changing generation
or importing new source, update the exclusions deliberately rather than weakening
the rules for authored code.
