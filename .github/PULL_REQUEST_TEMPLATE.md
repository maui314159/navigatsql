## What & why

<!-- What does this change, and why? Link any issue: "Closes #NN". -->

## Checklist

- [ ] Tests added/updated and `dotnet test tests/NavigaTSql.Tests` passes
- [ ] `dotnet build navigatsql.csproj -warnaserror` is clean
- [ ] `dotnet format whitespace … --verify-no-changes` passes (formatting is canonical)
- [ ] `bash scripts/check_line_cap.sh` passes (no `.cs` file over 500 lines)
- [ ] Conventional-commit title (`feat:` / `fix:` / `docs:` / …)
- [ ] stdout stays JSON-only; diagnostics go to stderr
