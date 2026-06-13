# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this is

navigaT-SQL is a .NET 10 console tool that extracts **cross-tier data-flow
relationships** from a codebase and emits them as graph **edges**. It parses
T-SQL with Microsoft **ScriptDom** and C# with **Roslyn**, then chains the two
so a `csharp_method → proc → table` (or `csharp_method → table` for embedded
SQL / EF) dependency can be reconstructed — the database dependency graph that
call-graph and tree-sitter tools don't model.

It is a sidekick to [trusty-tools](https://github.com/bobmatnyc/trusty-tools):
every edge is stamped with a `custom:<relation>` tag that trusty-tools'
extensible `EdgeKind::Custom` ingests directly, and `--emit kggraph` produces the
deduplicated ingest document for that project's ADR-0009 wire shape.

## Commands

```bash
dotnet build                                   # build the exe (net10.0)
dotnet run -- <file.sql | dir | a.sql b.cs>    # extract; recursive *.sql + *.cs on a dir
dotnet run -- --ef <dir>                        # also run the Entity Framework pass
dotnet run -- --emit kggraph <dir>              # deduplicated nodes+edges+facts ingest doc
dotnet run -- <dir> > edges.json                # stdout = clean JSON; stderr = summary

dotnet test tests/NavigaTSql.Tests              # full suite
dotnet test tests/NavigaTSql.Tests --filter "FullyQualifiedName~SmokeTests"      # one class
dotnet test tests/NavigaTSql.Tests --filter "DisplayName~extracts_reads"         # one test
```

Build & install from repo (#3 — see README "Installation"):

```bash
# .NET global tool (cargo-install analog; needs a .NET 10 runtime on the box):
dotnet pack -c Release -o ./nupkg
dotnet tool install --global --add-source ./nupkg navigatsql   # -> `navigatsql` on PATH

# self-contained single binary for runtime-less servers (RID: linux-x64|osx-arm64|win-x64|…):
dotnet publish -c Release -r linux-x64 --self-contained -p:PublishSingleFile=true -o ./publish
```

The exe is a **.NET global tool** (`PackAsTool`, command `navigatsql`); `./nupkg` and
`./publish` are gitignored build output. The tool is framework-dependent — on a Homebrew
`dotnet` it needs `DOTNET_ROOT` set (`$(brew --prefix dotnet)/libexec`); the self-contained
publish bundles the runtime and needs nothing.

**stdout is JSON only, stderr is the human summary** — this separation is load-bearing
(the README documents piping stdout straight into an ingest step). Never write
diagnostics to stdout. The per-relation histogram and the **dynamic-SQL count**
(the single most important signal for how much a static extractor can see) go to stderr.

Exit codes: `0` on a completed scan (even with skipped/erroring files), `2` only for a
usage error or no input files found.

## Architecture

Everything funnels into one currency type — **`Edge`** (`Model.cs`): a
`(File, From, FromKind, To, ToKind, Relation, EdgeKindTag, LinkedServer?)` record.
All passes return `PassResult(Edges, Units, DynamicFlags, HadParseErrors)`, and
`Program.cs` concatenates every pass's edges into one `List<Edge>` before emitting.
To add a relation you produce more `Edge`s — you don't touch the emit/dedup machinery.

**`Program.cs`** — top-level-statements entry point. Parses flags (`--ef`,
`--emit edges|kggraph`), resolves paths to a deterministically-ordered file list,
dispatches each file by extension to a pass, and serializes. The per-file `try/catch`
is deliberate: one pathological file must never sink the run, so parse errors are
logged to stderr and skipped while the partial parse tree is still mined.

**`TSqlPass.cs`** — the ScriptDom pass and the largest unit. `RelationVisitor`
(a `TSqlFragmentVisitor`) walks the tree; `_scope`/`_scopeKind` mutate as it descends
into `CREATE/ALTER PROCEDURE|FUNCTION|VIEW` so reads/writes/calls attribute to the
enclosing routine (statements outside any routine go to `<script-level>`). Functions
and views are scopes too, which is what extends the transitive `proc → view/function → table`
graph. `TSqlPass.ExtractTableRefs(sql)` is a reentrant entry point used by the C# pass
to parse SQL strings lifted out of C#.

**`Canon()` (inside `RelationVisitor`) is the heart of node identity.** It
canonicalizes every table to `database.schema.table` (lowercased): schema defaults to
`dbo`, the database comes from a `USE` statement or the dump filename, linked-server
prefixes (`[srv].db.dbo.t`) are stripped to the node id and preserved as edge metadata,
and noise (temp tables `#x`, table variables, `sys.*`/`information_schema`, the legacy
`sysobjects`-style catalog, trigger pseudo-tables `deleted`/`inserted`, MERGE aliases,
CTE names) returns `(null, _)` and emits no edge. This convergence is why the same table
from many call sites becomes one graph node — preserve it when editing.

**`CSharpPass.cs`** — Roslyn syntactic pass (no semantic model). Two literal kinds:
a `ProcShaped` regex (`usp_`/`sp_`/`qry_`/`p_` convention, optionally db/schema-qualified)
yields `csharp_method --calls_proc--> proc`; a `SqlShaped` regex (looks like DML) routes
the string through `TSqlPass.ExtractTableRefs` and attributes its reads/writes to the
enclosing `Class.Method`. Convention matching was validated at 96% resolution; it
deliberately won't guess at arbitrary string arguments.

**`EfPass.cs`** — Roslyn, **only runs under `--ef`**, two phases. `BuildModel` scans all
`.cs` to map entity types → tables (from `DbSet<T>` and `[Table]`); `Extract` then emits
`table --references--> table` from reference navigation properties and
`csharp_method --reads_table/writes_table--> table` from `DbSet` usage in method bodies.
Inert (auto-disabled) if no `DbContext` is detected.

**`KgGraphEmit.cs`** — transforms the flat per-occurrence edge list into the
`--emit kggraph` ingest document. Dedups on `(from, to, relation)` and merges every
asserting file into `provenance`; maps the fine-grained relation to a coarse `#817`-aligned
`kind`; and splits `dynamic_sql` flags out as FactStore-shaped `(subject, predicate, object)`
triples — these never mint a `<dynamic>` graph node.

## Invariants to preserve

- **Determinism.** The input file list and all kggraph output are ordinal-sorted so
  identical inputs (in any order) yield byte-identical output, making re-ingest a no-op
  merge. Don't introduce ordering that depends on hash iteration, locale, or timestamps.
- **Dynamic SQL is flagged, never resolved.** `sp_executesql` / `EXEC(@sql)` are counted
  and surfaced, not parsed away. In kggraph they live in `facts`, not `edges`/`nodes`.
- **`custom:<relation>` is the ingest contract.** `EdgeKindTag` is the key trusty-tools
  reads; keep new relations tagged `custom:<relation>` and add their coarse mapping in
  `KgGraphEmit.KindOf`.
- Tests reach `internal` types (`TSqlPass`, `Edge`, …) via `InternalsVisibleTo.cs`. New
  testable types stay `internal`; the exe keeps essentially no public API.

## Tests

xUnit, one suite per concern under `tests/NavigaTSql.Tests/`: `TSqlReadsWrites`,
`TSqlCallsViewsDynamic`, `TSqlCanonNoiseFk`, `CSharpPass`, `EfPass`, `KgGraphEmit`,
plus `SmokeTests` (the wiring/pattern reference). The test project references the exe's
csproj directly; `navigatsql.csproj` excludes `tests/**` from its own compilation.

## Development workflow

The workflow mirrors the peer project [trusty-tools](https://github.com/bobmatnyc/trusty-tools)
(navigaT-SQL is its sidekick), adapted from Rust/`cargo` to .NET. Independent CI
gates run on every push to `main` and every PR (`.github/workflows/`):

| Gate | Local command | CI job |
|---|---|---|
| Format | `dotnet format whitespace <proj> --verify-no-changes` | `ci.yml` → Format check |
| Build (warnings = errors) | `dotnet build navigatsql.csproj -warnaserror` | `ci.yml` → Build |
| Test | `dotnet test tests/NavigaTSql.Tests` | `ci.yml` → Test |
| 500-line file cap | `bash scripts/check_line_cap.sh` | `line-cap.yml` |
| Internal-name denylist | `bash scripts/check_name_denylist.sh` | `name-denylist.yml` |

Run everything locally before pushing:

```bash
dotnet format whitespace navigatsql.csproj --verify-no-changes
dotnet format whitespace tests/NavigaTSql.Tests/NavigaTSql.Tests.csproj --verify-no-changes
dotnet build navigatsql.csproj -warnaserror
dotnet test tests/NavigaTSql.Tests
bash scripts/check_line_cap.sh
bash scripts/check_name_denylist.sh
```

To auto-fix formatting instead of just checking, drop `--verify-no-changes`. Formatting
is canonical (like `cargo fmt`): `.editorconfig` pins the conventions, so don't hand-align
in ways the formatter will undo — the Format gate is hard.

**500-line cap (ratcheted).** No tracked `.cs` file may exceed 500 lines. Files already
over the cap when the gate landed are grandfathered in `.line-cap-allowlist.tsv`
(`path<TAB>frozen-budget`); the ratchet lets them only **shrink** — a grandfathered file
that grows past its budget fails, and one that drops to ≤ 500 must have its entry removed.
Currently grandfathered: `TSqlReadsWritesTests.cs`, `TSqlCallsViewsDynamicTests.cs`. After
an intentional split (or any drop below budget) re-freeze with
`scripts/check_line_cap.sh --update` and commit the regenerated allowlist. A file nearing
500 lines is the signal to split into focused files before the next change lands on it.

**Pre-commit (optional, recommended).** `.pre-commit-config.yaml` wires hygiene hooks,
conventional-commit validation (commitizen), and the line-cap gate. One-time setup
(`uv tool install` is the isolated-global install; plain `pip install pre-commit` also works):

```bash
uv tool install pre-commit
pre-commit install --hook-type commit-msg --hook-type pre-commit
```

commitizen and the hygiene hooks are fetched and isolated by pre-commit itself — only
`pre-commit` needs to be on PATH. Run against the whole tree any time with
`pre-commit run --all-files`.

**Conventions:**
- **Conventional commits** — `feat:` / `fix:` / `docs:` / `test:` / `refactor:` … (already
  the repo's history style; enforced by commitizen if pre-commit is installed).
- **stdout is data, stderr is diagnostics** — the load-bearing invariant from Commands
  above; never log to stdout. This is the same discipline as trusty-tools' MCP daemons.
- **No warnings** — `-warnaserror` is a gate; fix warnings, don't suppress them.
- **Keep types `internal`** — see Invariants; the exe exposes no public API.
