# navigaT-SQL

[![NuGet](https://img.shields.io/nuget/v/navigatsql.svg?logo=nuget&label=nuget)](https://www.nuget.org/packages/navigatsql)
[![CI](https://github.com/maui314159/navigatsql/actions/workflows/ci.yml/badge.svg)](https://github.com/maui314159/navigatsql/actions/workflows/ci.yml)
[![License: MIT](https://img.shields.io/badge/license-MIT-yellow.svg)](LICENSE)

> **naviga·T-SQL** — *navigate* your T-SQL. The sidekick to [trusty-tools](https://github.com/bobmatnyc/trusty-tools): where `trusty` is a play on *Rust*, `navigaT-SQL` hides *T-SQL* inside *navigate*.

A small .NET tool that parses **T-SQL** with Microsoft **ScriptDom** and extracts the
**resource / data-flow relationships** a codebase's stored procedures have with tables
and with each other — the layer that call-graph and tree-sitter tools don't model.

It emits relations as graph **edges** tagged with the exact `custom:<relation>` string
that trusty-tools' extensible knowledge graph (`EdgeKind::Custom`) accepts, so the output
is directly ingestible with no core change beyond the extensible-edge work.

## Why this exists

Across mature code-intelligence tools, **nobody models the cross-tier database
dependency graph** (verified against GitNexus and codebase-memory-mcp): GitNexus has no
standalone SQL at all; codebase-memory-mcp parses SQL with a generic tree-sitter grammar
but **drops stored procedures entirely** and has no SQL data-flow edges. tree-sitter SQL
grammars are too weak for real T-SQL — which is why this uses ScriptDom, the canonical,
MIT-licensed Microsoft T-SQL parser.

The host-language (C#) side is **already indexed by trusty-search**, so the genuinely new
work is (1) the T-SQL relation graph (this tool) and (2) the C# → proc bridge (roadmap).

## What it extracts

For every `CREATE` / `ALTER PROCEDURE` (statements outside a proc are attributed to
`<script-level>`):

| Relation | T-SQL construct | Edge tag |
|---|---|---|
| `reads_table` | `FROM` / `JOIN` sources; `MERGE … USING` source | `custom:reads_table` |
| `writes_table` | `INSERT` / `UPDATE` / `DELETE` / `MERGE` target (`UPDATE p … FROM dbo.T AS p` aliases resolved) | `custom:writes_table` |
| `calls_proc` | `EXEC procname` | `custom:calls_proc` |
| `calls_function` | scalar UDF calls (`dbo.fn_X(…)`); table-valued UDFs in `FROM` (`dbo.tvf_X(…)`) — built-ins skipped | `custom:calls_function` |
| `references` | foreign keys (`CREATE`/`ALTER TABLE … FOREIGN KEY`) — `table -> table` | `custom:references` |
| `dynamic_sql` | `sp_executesql`, `sp_execute`, `EXEC(@sql)` — **flagged, never silently dropped** | `custom:dynamic_sql` |

`CREATE`/`ALTER FUNCTION` and `CREATE`/`ALTER VIEW` bodies are scopes too
(`FromKind: function` / `view`), so their own `reads_table`/`writes_table` extend the
transitive `proc → function/view → table` graph (view expansion).

**C# pass (Roslyn):** for every C# member, `csharp_method --calls_proc--> proc`, from
stored-proc-name string literals (`usp_*`/`sp_*`/`qry_*`) at data-access call sites.
Chaining these with the T-SQL `proc -> table` edges reconstructs the cross-tier
**method -> proc -> table** dependency that no tree-sitter tool models. Validated on a
real ~1,000-proc production codebase: **96%** of C# proc references resolve to an extracted
proc; **882** C# methods transitively reach **3,010** distinct method→table pairs.

**Embedded SQL (Dapper / raw queries):** string literals that look like SQL are parsed
with the T-SQL pass and their table reads/writes attributed directly to the enclosing C#
method — `csharp_method → reads_table/writes_table → table`. This recovers data access
that lives in C# strings rather than in `.sql` files or stored-proc calls (the dominant
pattern in Dapper-style codebases).

## Installation

navigaT-SQL ships as a published [.NET global tool](https://www.nuget.org/packages/navigatsql),
with from-source and self-contained options too.

### From NuGet (recommended)

On any machine with the .NET 10 runtime:

```bash
dotnet tool install --global navigatsql
navigatsql --trusty-setup                                  # how to wire into trusty-search
navigatsql --emit kggraph path/to/repo > repo-kggraph.json
```

Update or remove with `dotnet tool update --global navigatsql` /
`dotnet tool uninstall --global navigatsql`. The command lands in `~/.dotnet/tools`; add
that to `PATH` if it isn't already (`export PATH="$PATH:$HOME/.dotnet/tools"`).

> **Runtime discovery.** A global tool is framework-dependent. If .NET was installed where
> the apphost can't auto-find it — notably **Homebrew** on macOS — the tool prints *"You must
> install .NET to run this application."* Fix by pointing `DOTNET_ROOT` at the runtime, e.g.
> `export DOTNET_ROOT="$(brew --prefix dotnet)/libexec"`. Official-installer, `apt`, and
> tarball installs register their location and need no such step. (Or use the self-contained
> binary below — it bundles the runtime and never needs `DOTNET_ROOT`.)

### From source

Build and install from a clone — requires the .NET 10 SDK:

```bash
git clone https://github.com/maui314159/navigatsql
cd navigatsql

dotnet pack -c Release -o ./nupkg
dotnet tool install --global --add-source ./nupkg navigatsql
navigatsql --trusty-setup                                  # how to wire into trusty-search
```

### Self-contained binary (arbitrary servers, no .NET runtime)

The analog of trusty-tools' prebuilt Homebrew binary. Produces one standalone executable
with the runtime bundled in — copy it to any server of the same OS/architecture and run
it; **no .NET install required on the target**:

```bash
# choose the target's runtime identifier (RID):
#   linux-x64 · linux-arm64 · win-x64 · osx-arm64 · osx-x64
dotnet publish -c Release -r linux-x64 --self-contained \
  -p:PublishSingleFile=true -o ./publish

# ./publish/navigatsql is a single file — drop it onto the server's PATH:
scp ./publish/navigatsql server:/usr/local/bin/
ssh server navigatsql --trusty-setup                       # how to wire into trusty-search
ssh server navigatsql --emit kggraph /srv/app > app-kggraph.json
```

### Build & test (development)

```bash
dotnet build                          # build the exe (net10.0), no install
dotnet test tests/NavigaTSql.Tests    # full xUnit suite
dotnet run -- <file.sql | dir>        # run straight from the repo
```

`dotnet run -- <args>` (used throughout this README) and the installed `navigatsql <args>`
command are interchangeable. See [CLAUDE.md](./CLAUDE.md#development-workflow) for the full
CI-gate workflow (format, warnings-as-errors, 500-line cap).

## Usage

```bash
dotnet run -- <file.sql>            # single file
dotnet run -- path/to/repo          # directory, recursive *.sql (T-SQL) + *.cs (C#)
dotnet run -- a.sql b.sql dir/      # any mix

# clean JSON on stdout, human summary on stderr — pipe the edges anywhere:
dotnet run -- ~/src/myapp > myapp-edges.json

# treat each .sql file's name as its database context (for whole-database dumps,
# e.g. SALES_LIVE.sql -> sales_live.dbo.t); off by default:
dotnet run -- --db-from-filename path/to/db-dumps

# ingest wire shape (deduplicated nodes+edges+facts, ADR-0009):
dotnet run -- --emit kggraph path/to/repo > repo-kggraph.json
```

- **stdout** is clean JSON (an array of edges) so it pipes straight into an ingest step.
- **stderr** carries the summary + per-relation histogram. The **dynamic-SQL count is the
  single most important number** for judging how much of a codebase a static extractor can
  ever see.
- **Error recovery:** a per-file parse error is reported and skipped (ScriptDom's partial
  tree is still mined) — one bad script never sinks the run. Exit code is `0` on a completed
  scan, `2` only for a usage error / no files found.

### Output schema

```json
[
  {
    "File": "Procedures/usp_GetProperty.sql",
    "From": "dbo.usp_GetProperty",
    "FromKind": "proc",
    "To": "dbo.Property",
    "ToKind": "table",
    "Relation": "reads_table",
    "EdgeKindTag": "custom:reads_table"
  }
]
```

`File` is the source path (relative to the working directory) so multi-file output stays
traceable. `EdgeKindTag` is the trusty-tools ingest key.

### `--emit kggraph` — ingest document (trusty-tools ADR-0009 wire shape)

The default `edges` mode emits one record **per occurrence**. `--emit kggraph`
emits the deduplicated **ingest document** for trusty-tools'
`POST /indexes/{id}/graph` (ADR-0009): nodes + edges + facts, with the
idempotency contract *a node is its id; an edge is `(from, to, relation)`*.

```json
{
  "schema": "navigatsql/kggraph@2",
  "producer": "navigatsql",           // required by the ingest endpoint (400 if missing)
  "producerVersion": "0.1.0",         // optional; this tool's assembly version
  "gitSha": "dbfd800…",               // optional; HEAD of the scanned tree (omitted off-repo)
  "nodes": [ { "id": "dbo.property", "kind": "table" } ],
  "edges": [
    {
      "from": "dbo.usp_GetProperty", "fromKind": "proc",
      "to": "dbo.property", "toKind": "table",
      "kind": "reads",                  // coarse #817-aligned kind
      "relation": "reads_table",        // fine-grained original
      "tag": "custom:reads_table",      // Custom(String) escape-hatch key
      "provenance": ["Procedures/usp_GetProperty.sql"],
      "linkedServer": "srv"             // only when referenced via [srv].db.dbo.t
    }
  ],
  "facts": [
    { "subject": "dbo.usp_X", "predicate": "dynamic_sql", "object": "<dynamic>",
      "provenance": ["Procedures/usp_X.sql"] }
  ]
}
```

- **Producer envelope:** `producer` is the constant `"navigatsql"` the trusty-search
  ADR-0009 endpoint requires; `producerVersion` and `gitSha` are optional and follow
  the `linkedServer` convention (camelCase, omitted when null). `gitSha` is the HEAD of
  the scanned tree (`git -C <root> rev-parse HEAD`), enabling cheap staleness checks
  (overlay-SHA vs repo-HEAD). It is resolved **once** per run, never per-file, and
  degrades to omitted (never an error) outside a git repo. Multi-root scans that **span
  repositories** omit `gitSha` rather than guess; roots sharing one HEAD keep it. The
  `schema` is `@2` because the envelope added the always-present `producer` field — the
  change is backward-compatible (an `@1` consumer still parses it) but the bump lets a
  consumer tell enveloped output from pre-envelope output.
- **Dedup + provenance:** identical `(from, to, relation)` assertions from many
  files collapse into one edge whose `provenance` lists every source file.
- **Facts split:** `dynamic_sql` flags are not node→node relations — they're
  emitted as FactStore-shaped triples, and never mint a `<dynamic>` node.
- **Deterministic:** output is ordinal-sorted everywhere and the envelope carries no
  timestamps or machine names; identical inputs (in any order, same tree) produce
  byte-identical documents, so re-ingest is a no-op merge.

## Feeding the graph (trusty-tools integration)

`--emit kggraph` is not just a dump format — it is the **native ingest body** for
trusty-search's contributed-graph endpoint. There is no converter and no shim: the
document POSTs as-is. Which path you use depends only on whether you run trusty-search.

> **TL;DR — run `navigatsql --trusty-setup`.** It prints this whole recipe (endpoint,
> version requirement, the exact `curl`) to stderr and exits, so it's safe to run any
> time — including as the last step of an install.

> **Version requirement.** The ingest endpoint lives on **trusty-search**, not
> trusty-analyze, and shipped in **trusty-search ≥ 0.24.5** (ADR-0009, merged in
> [trusty-tools#1129](https://github.com/bobmatnyc/trusty-tools/pull/1129)). Earlier
> daemons (0.24.0–0.24.4) don't register the route and return a bare `404` for the
> `POST` — check yours with `curl -s http://127.0.0.1:7878/health` (the `version` field)
> and upgrade (`POST /upgrade {"check":true}` then `{"confirm":true}`) before wiring the
> pipeline. Until then, use the standalone path below.

### 1. Ingest into trusty-search (the intended path)

trusty-search exposes `POST /indexes/{id}/graph` — a durable **contributed graph
overlay** that survives restart *and* reindex, stored separately from the chunk-derived
graph. navigaT-SQL's `--emit kggraph` document is exactly that endpoint's wire shape, so
you pipe one straight into the other:

```bash
# the daemon defaults to http://127.0.0.1:7878; {id} is an existing index id
# (list them with the trusty-search `list_indexes` MCP tool, or GET /indexes).
ID=my-index

navigatsql --emit kggraph path/to/repo \
  | curl -sS -X POST "http://127.0.0.1:7878/indexes/$ID/graph" \
         -H 'content-type: application/json' --data-binary @-
```

The JSON response reports what landed plus the post-merge graph totals:

```json
{ "producer": "navigatsql", "replaced": true,
  "nodes_received": 412, "edges_received": 3010,
  "graph_nodes": 18233, "graph_edges": 51904,
  "unknown_edge_tags_dropped": 0 }
```

- **Replace-per-producer.** Each ingest atomically replaces navigaT-SQL's *previous*
  contribution to that index — so tables/procs deleted from the codebase never leave
  stale edges; just re-run after a scan. The index's derived graph and any other
  producer's contribution are untouched.
- **Identity is self-contained.** Contributed canonical ids (`db.schema.table`,
  schema-qualified routines, `Class.Method`) are stored as their own nodes, never merged
  into trusty-search's bare-name code symbols, so the cross-tier `method → proc → table`
  chain stays sound regardless of the host graph.
- **Schema is logged, not enforced** — `navigatsql/kggraph@2` ingests fine; the field set
  is the contract. The endpoint reads `nodes` + `edges` and ignores the `facts` array
  (see below). Errors: `400` if `producer` is empty, `404` if the index id is unknown.

Then traverse the merged graph with `GET /indexes/{id}/graph/neighbors`
(`node`, `direction=in|out|both`, `edge_kinds=writes,reads,…`, `max_hops=1..4`) or the
trusty-search `search_kg` MCP tool — e.g. *"what writes table X"* is
`?node=db.dbo.x&direction=in&edge_kinds=writes`.

**Dynamic-SQL facts (optional).** The graph endpoint ignores the `facts` array —
dynamic-SQL flags aren't node→node edges. If you want them queryable, load each
`(subject, predicate, object)` triple into trusty-analyze's FactStore via its
`upsert_fact` MCP tool. This is optional enrichment; the graph is complete without it.

### 2. Standalone (no trusty-search)

`--emit edges` (per-occurrence) and `--emit kggraph` (deduplicated) are both plain JSON on
stdout. Consume them directly — load into any graph/store, diff two runs, or query with
`jq`. `EdgeKindTag` (`custom:<relation>`) is the stable key if you ingest elsewhere.

A one-shot `navigatsql push` subcommand wrapping the `curl` above is the only convenience
still on the roadmap; the wire contract itself is complete and live today.

## Scope & limits (honest)

Flagged rather than silently resolved:

- **Dynamic SQL** (`sp_executesql` / `EXEC(@sql)`) — detected and counted, not parsed.
- **CTE / temp-table / table-variable** names appear as their alias, not the underlying tables.
- **Views** in a `FROM` are emitted as table reads, not recursively expanded.
- **Database context** comes from a `USE <db>` statement, or — only with `--db-from-filename` —
  from each `.sql` file's name. Without either, 1/2-part names canonicalize to `schema.table`
  (no database qualifier) rather than guessing one.

## Tests

```bash
dotnet test tests/NavigaTSql.Tests
```

201 xUnit tests cover the T-SQL passes (reads/writes/calls/UDF/views/dynamic/FK),
canonical table identity + noise exclusion, the C# proc bridge + embedded SQL, the
EF extractor, and the kggraph emit (dedup/provenance/facts/determinism). CI runs them on every push (`.github/workflows/ci.yml`).

## Roadmap

**Built:** file-SQL, embedded-SQL (Dapper), and EF/ORM (`--ef`) extraction; canonical
table identity + noise exclusion; the C#→proc bridge.

**Remaining:**
- **JOIN-inferred relationships** for DBs that don't declare FKs ([#4](https://github.com/maui314159/navigatsql/issues/4)).
- **Construct concept nodes** in kggraph (`construct:*` + `custom:uses_*`) so "where is X used?" is a one-call graph query ([#5](https://github.com/maui314159/navigatsql/issues/5)).
- **Column-level lineage** — the high-ceiling next step.
- Harden heuristics: interpolated/concatenated SQL, `.Set<T>()`, Fluent-API `HasForeignKey`.
- A one-shot **`push` subcommand** wrapping the ingest `curl`. The wire contract is
  already complete and live: `--emit kggraph` POSTs as-is to trusty-search's
  `POST /indexes/{id}/graph` endpoint (ADR-0009, in trusty-search ≥ 0.24.5 via
  [trusty-tools#1129](https://github.com/bobmatnyc/trusty-tools/pull/1129)) — see
  [Feeding the graph](#feeding-the-graph-trusty-tools-integration). The subcommand is
  pure convenience over the documented pipe.

## License

MIT — see [LICENSE](./LICENSE). Optional by deployment: a codebase with no T-SQL never runs it.
