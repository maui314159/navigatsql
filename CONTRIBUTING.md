# Contributing to navigaT-SQL

Thanks for your interest in improving navigaT-SQL! It's a small, focused .NET tool;
contributions that keep it sharp, honest, and deterministic are very welcome.

## Development setup

Requires the **.NET 10 SDK**. From a clone:

```bash
dotnet build                          # build the exe (net10.0)
dotnet test tests/NavigaTSql.Tests    # run the full xUnit suite
dotnet run -- sample.sql              # try it on the bundled sample
```

## CI gates (run these before opening a PR)

Every push and PR must pass these independent gates:

```bash
dotnet format whitespace navigatsql.csproj --verify-no-changes
dotnet format whitespace tests/NavigaTSql.Tests/NavigaTSql.Tests.csproj --verify-no-changes
dotnet build navigatsql.csproj -warnaserror
dotnet test tests/NavigaTSql.Tests
bash scripts/check_line_cap.sh
bash scripts/check_name_denylist.sh
```

- **Formatting is canonical** (like `cargo fmt`): `.editorconfig` pins the rules; don't
  hand-align in ways the formatter will undo. Drop `--verify-no-changes` to auto-fix.
- **Warnings are errors** — fix them, don't suppress.
- **500-line file cap** — no tracked `.cs` file may exceed 500 lines (a couple of legacy
  test files are grandfathered and may only shrink). Split before you grow past it.
- **Internal-name denylist** — a grep gate that fails if a confidential/internal name slips
  into a tracked file. Keep examples and fixtures generic.

A `.pre-commit-config.yaml` wires these plus conventional-commit validation; install with
`pre-commit install --hook-type commit-msg --hook-type pre-commit` (optional but recommended).

## Conventions

- **Conventional commits** — `feat:` / `fix:` / `docs:` / `test:` / `refactor:` …
- **stdout is data, stderr is diagnostics** — never write anything but the JSON document to
  stdout; the summary and per-relation histogram go to stderr. This separation is load-bearing.
- **Keep types `internal`** — the exe exposes essentially no public API; tests reach internals
  via `InternalsVisibleTo`.
- **Determinism** — identical inputs must produce byte-identical output. Don't introduce
  ordering that depends on hash iteration, locale, or timestamps.

See [CLAUDE.md](./CLAUDE.md) for a deeper tour of the architecture and the invariants to preserve.

## Pull requests

1. Branch off `main`.
2. Make the change and add/adjust tests.
3. Run the gates above.
4. Open the PR with a conventional-commit-style title and a clear description.

By contributing, you agree that your contributions are licensed under the [MIT License](./LICENSE).
