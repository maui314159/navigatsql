# navigaT-SQL ↔ trusty-tools — Interface Proposal (clean-room)

**Date:** 2026-06-07 · **Scope:** how navigaT-SQL's output lands in trusty-analyze's knowledge graph (KG) and is queried. Derived clean-room from three lenses — **the data**, the **indexing** path, the **query** path — and validated on two real codebases. Deliberately *not* anchored to the earlier Option A/B/C proposals.

## Decisions that frame everything
- **Target = trusty-analyze's KG** (the graph). The **FactStore is spillover only** — for things that aren't node→node graph data (e.g. `dynamic_sql` flags, confidence notes).
- **Zero trusty-search changes.** trusty-search is only the source of `CodeChunk`s for the derived graph.
- **Additive only.** No edge-kind surgery on trusty-search, no rebuild-wipe coupling, no speculative config.

The required footprint turned out small: **one new file + ~4 touched files**, all additive.

---

## Lens 1 — The data we contribute (the contract)

navigaT-SQL emits a typed graph. Independent of trusty's current model, the contract is:

**Node kinds:** `table`, `view`, `proc`, `function`, `csharp_method`. (EF entities are resolved to their `table` at extraction, so they need no separate node kind.)

**Edge kinds:**
| Edge | From → To | Tag |
|---|---|---|
| `reads_table` / `writes_table` | proc/function/view/**csharp_method** → table | `custom:reads_table` … |
| `calls_proc` | proc/**csharp_method** → proc | `custom:calls_proc` |
| `calls_function` | proc/function/view → function | `custom:calls_function` |
| `references` | table → table (FK / EF nav) | `custom:references` |
| `dynamic_sql` | proc → `<dynamic>` | spillover → FactStore |

**Identity (canonical):** tables = `database.schema.table` lowercased (linked-server stripped to metadata); procs/functions/views = schema-qualified original-case names; `csharp_method` = `Class.Method`. **Idempotency:** a node is its id; an edge is `(from, to, relation)` — re-runs merge, not duplicate. **Provenance:** source file per edge.

**Graph-shaped vs spillover:** everything above except `dynamic_sql` (and confidence/free-form notes) is graph data. The non-graph remainder goes to the FactStore as `(subject, predicate, object)` triples.

---

## Lens 2 — Indexing modification (minimal, additive)

**What exists (verified):** trusty-analyze has exactly **one durable store — the FactStore** (`core/facts.rs`, single redb table). The KG is **recomputed from chunks on every request** (`service/mod.rs:732 graph_for_index`), and the SCIP overlay is **in-memory only** (`service/mod.rs:101 scip_overlays`, lost on restart). There is **no durable graph store** and **no rebuild-wipe hazard inside analyze** (it holds no graph state to rebuild).

**Modification:**
1. **Add a durable graph-overlay store** — new file `core/graph_store.rs` (~80–100 lines): redb table `kg_overlays` keyed by `index_id` → JSON `KgGraph`. Idempotent `merge_into` via the existing `KgGraph::merge` (dedupes nodes by id, edges by `(from,to,kind)`).
2. **Add `POST /indexes/{id}/graph`** accepting a `KgGraph` JSON payload (same shape `GET /graph` already returns) → `graph_store.merge_into` + in-memory overlay. No protobuf/SCIP encoding needed.
3. **Load overlays on startup** into `scip_overlays` so contributed data survives restarts.
4. **Extend the schema** (`types/graph.rs`): add `KgNodeKind::{Table, View, StoredProcedure}` (+ one `Display` arm each — `KgNodeKind` is a closed exhaustive match) and `KgEdgeKind::{Reads, Writes, Invokes}` (`References` already exists; `KgEdgeKind` has no `Display`/exhaustive matches, so it's cheap). Optionally a `Custom(String)` hatch for future producers.

Touched: `core/graph_store.rs` (new), `core/mod.rs` (re-export), `service/mod.rs` (`AnalyzerAppState` field + route + `ingest_scip` write-through + startup load), `main.rs` (construct the store), `types/graph.rs` (enum + Display arms). **No trusty-search involvement.**

---

## Lens 3 — Query modification (minimal, additive)

**What exists (verified):** the KG has **no traversal**. `KgGraph` is `Vec<KgNode> + Vec<KgEdge>` with only `merge/node_count/edge_count`. `GET /graph` returns the **whole graph as JSON**, walked client-side; `GET /entities?kind=` filters nodes only. No edge-kind filter, no reverse lookup, no reachability. (At 15k-file scale this dump is impractical.)

**Modification:** add **one server-side traversal endpoint** `GET /indexes/{id}/graph/neighbors` + one MCP tool `graph_neighbors`, params: `node`, `direction` (in/out), `edge_kind`, `node_kind`, `max_hops`. BFS over the Vecs (O(hops×edges)). That single primitive answers every target query:

| Query | Call |
|---|---|
| what writes table X | `node=<table>&direction=inbound&edge_kind=writes` |
| what does this method touch | `node=<method>&direction=outbound&max_hops=N` |
| callers of a deprecated proc/table | `node=<x>&direction=inbound&max_hops=N` |

Touched: `types/graph.rs` (kinds, shared with Lens 2), `service/mod.rs` (route + ~80-line handler), `mcp/mod.rs` + `mcp/descriptors.rs` (tool).

---

## Identity alignment — the one real open question
navigaT-SQL names C# methods `Class.Method`; trusty-analyze's own C# tree-sitter nodes use `csharp:Method:<file>:<name>`. To **unify** contributed method nodes with trusty's derived C# graph, ids must align. Options:
- **(v1, recommended) self-contained contributed graph** — navigaT-SQL nodes carry a distinct id prefix (e.g. `db:` / `ext:`); they form their own connected cross-tier graph (method→proc→table), queried directly. Table/proc/function nodes are the join surface and need no trusty counterpart. **No alignment needed for the queries we validated.**
- **(later) align to trusty's scheme** via the existing `get_chunks` seam (reconstruct `csharp:Method:<file>:<name>`), so contributed edges attach to trusty's derived C# nodes.

v1 works without alignment because the valuable queries live entirely within the contributed graph (proven below).

## Invocation (minimal)
- **v1 batch:** `navigatsql ingest --index <id> <paths>` → emits the `KgGraph` JSON → `POST /indexes/{id}/graph`. Run on demand or in CI. The "when" is a CI step or manual run; no new trigger hook in core.
- **later:** an MCP-sidecar wrapper so the agent invokes extraction on demand. Not required for v1.

---

## Validation (both archetypes, on real data)
Ran the proposed data model + traversal against actual navigaT-SQL output. Same model, same queries, two very different codebases:

| Query | Proc-centric codebase | EF + SSDT codebase |
|---|---|---|
| what writes the hottest table | one table ← **~140 procs** | one table ← **~110 writers = EF C# methods + procs, unified** |
| what does a C# method touch (transitive) | a service method → 3 tables (method→proc→table, cross-DB) | a delete-service method → **~200 tables** |
| callers of a hot proc | a hot proc ← **~65 callers** | a system proc ← **~50 callers** |
| graph size | ~5.8k nodes / ~34.6k edges | ~7k nodes / ~20.8k edges |

The EF + SSDT "what writes the hottest table" result — **EF methods and SQL procs that hit the same table, unified in one answer** — is the cross-tier payoff, and it falls out of the proposed model with no special-casing. The design holds across the proc-centric and ORM/SSDT archetypes.

---

## Footprint & why this is not invasive
- **1 new file** (`graph_store.rs`) + ~4 touched (`types/graph.rs`, `service/mod.rs`, `mcp/*`, `main.rs`).
- **Additive:** new redb table, new endpoint, new MCP tool, enum additions. Nothing existing changes behavior.
- **Zero trusty-search changes.** No edge-kind surgery on the search graph, no rebuild-wipe coupling.
- FactStore untouched except as optional spillover.

## Open questions / next steps
1. **Identity alignment** (above) — confirm v1 self-contained is acceptable.
2. **`KgEdge`/`KgNode` provenance + linked-server metadata** — carry in `KgNode.extra` / add a small field on `KgEdge`.
3. **navigaT-SQL `--emit kggraph`** — add an output mode that emits the `KgGraph` JSON shape (today it emits its native edge list; the mapping is mechanical).
4. **Index-deletion housekeeping** — analyze isn't notified when trusty-search drops an index (pre-existing for the SCIP overlay; out of scope).
5. Then: tracer-bullet the real ingest (`navigatsql ingest` → `POST /graph` → `graph_neighbors`) end-to-end against a live analyze daemon.
