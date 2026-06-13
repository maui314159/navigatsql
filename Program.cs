// navigaT-SQL — cross-tier T-SQL + C# relation extractor (trusty-tools sidekick, #580).
//
// Two passes, one unified edge list:
//   T-SQL (ScriptDom, TSqlPass): proc --reads_table/writes_table/calls_proc--> resource,
//                                with dynamic_sql flagged.
//   C#    (Roslyn,   CSharpPass): csharp_method --calls_proc--> proc.
// Chaining csharp_method->proc with proc->table reconstructs the cross-tier
// method -> proc -> table dependency that no tree-sitter tool models.
//
// Accepts files or directories (recursive *.sql and *.cs). Recovers from per-file
// parse errors instead of aborting. Every edge is stamped with its source file and
// carries the custom:<relation> tag trusty-tools' extensible EdgeKind accepts.

using System.Text.Json;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

// --emit edges   (default) : flat per-occurrence edge array (original output)
// --emit kggraph           : deduplicated nodes+edges+facts ingest document (ADR-0009 wire shape)
bool ef = false;
bool dbFromFilename = false;
bool trustySetup = false;
string emit = "edges";
var paths = new List<string>();
bool badArgs = false;
for (int i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--ef": ef = true; break;
        case "--db-from-filename": dbFromFilename = true; break;
        case "--trusty-setup": trustySetup = true; break;
        case "--emit" when i + 1 < args.Length: emit = args[++i]; break;
        case "--emit": Console.Error.WriteLine("error: --emit requires a value (edges | kggraph)"); badArgs = true; break;
        case var a when a.StartsWith("--emit=", StringComparison.Ordinal): emit = a["--emit=".Length..]; break;
        case var a when a.StartsWith("--", StringComparison.Ordinal):
            Console.Error.WriteLine($"error: unknown flag: {a}"); badArgs = true; break;
        default: paths.Add(args[i]); break;
    }
}
if (emit is not ("edges" or "kggraph"))
{
    Console.Error.WriteLine($"error: unknown --emit mode: {emit} (expected: edges | kggraph)");
    badArgs = true;
}
// --trusty-setup is an informational command: print how to feed output into
// trusty-search and exit 0, before any file/usage handling (it needs no inputs).
if (trustySetup)
{
    PrintWiringGuide();
    return 0;
}
if (badArgs || paths.Count == 0)
{
    Console.Error.WriteLine("navigaT-SQL — extract cross-tier T-SQL + C# data-flow relations.");
    Console.Error.WriteLine("usage: navigatsql [--ef] [--db-from-filename] [--emit edges|kggraph] <file | directory> [more ...]");
    Console.Error.WriteLine("       directories scanned recursively for *.sql (T-SQL) and *.cs (C#)");
    Console.Error.WriteLine("       --emit edges       : flat edge array (default)");
    Console.Error.WriteLine("       --emit kggraph     : deduplicated nodes+edges+facts ingest document");
    Console.Error.WriteLine("       --db-from-filename : use each .sql file's name as its database context");
    Console.Error.WriteLine("       --trusty-setup     : print how to wire output into trusty-search, then exit");
    Console.Error.WriteLine("       JSON -> stdout ; summary -> stderr");
    return 2;
}

// Resolve inputs to a concrete, de-duplicated, deterministically-ordered file list.
var files = new List<string>();
foreach (var p in paths)
{
    if (File.Exists(p)) files.Add(Path.GetFullPath(p));
    else if (Directory.Exists(p)) files.AddRange(EnumerateCode(p));
    else Console.Error.WriteLine($"warning: path not found, skipping: {p}");
}
files = files.Distinct().OrderBy(f => f, StringComparer.Ordinal).ToList();
if (files.Count == 0) { Console.Error.WriteLine("no .sql or .cs files found."); return 2; }

// EF mode (--ef): phase 1 builds the entity↔table model across all .cs; the main
// loop then emits EF relationship + access edges per file. Inert if no EF detected.
EfModel? efModel = null;
if (ef)
{
    var csList = files.Where(f => f.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)).ToList();
    efModel = EfPass.BuildModel(csList);
    Console.Error.WriteLine($"EF model: {efModel.EntityToTable.Count} entities, {efModel.DbSetToEntity.Count} DbSets" +
        (efModel.IsEmpty ? " — no EF detected, EF pass inert" : ""));
    if (efModel.IsEmpty) efModel = null;
}

var allEdges = new List<Edge>();
int sqlFiles = 0, csFiles = 0, procs = 0, members = 0, dynamic = 0, filesWithErrors = 0, filesUnreadable = 0;

foreach (var file in files)
{
    string text;
    try
    {
        text = File.ReadAllText(file);
    }
    catch (Exception ex)
    {
        filesUnreadable++;
        Console.Error.WriteLine($"read error: {Rel(file)}: {ex.Message}");
        continue;
    }

    var rel = Rel(file);
    try
    {
        switch (Path.GetExtension(file).ToLowerInvariant())
        {
            case ".sql":
                var s = TSqlPass.Run(rel, text, dbFromFilename);
                sqlFiles++; procs += s.Units; dynamic += s.DynamicFlags;
                if (s.HadParseErrors) filesWithErrors++;
                allEdges.AddRange(s.Edges);
                break;
            case ".cs":
                SyntaxNode csRoot;
                try { csRoot = CSharpSyntaxTree.ParseText(text).GetRoot(); }
                catch (Exception ex) { filesWithErrors++; Console.Error.WriteLine($"parse failed: {rel}: {ex.Message}"); break; }
                csFiles++;
                var c = CSharpPass.Run(rel, csRoot);
                members += c.Units;
                allEdges.AddRange(c.Edges);
                if (efModel is not null)
                {
                    var e = EfPass.Extract(rel, csRoot, efModel);
                    members += e.Units;
                    allEdges.AddRange(e.Edges);
                }
                break;
        }
    }
    catch (Exception ex)
    {
        // One pathological file must not sink the run.
        filesWithErrors++;
        Console.Error.WriteLine($"extract failed: {rel}: {ex.Message}");
    }
}

// stdout: clean JSON only, so it pipes straight into an ingest step.
if (emit == "kggraph")
{
    // Resolve the scanned tree's HEAD once (never per-file). Multi-root scans that
    // span repos resolve to null and omit gitSha. `paths` are the user-given roots.
    var gitSha = Git.CommonHeadSha(paths);
    var doc = KgGraphEmit.Build(allEdges, gitSha, out var resolve);
    Console.WriteLine(JsonSerializer.Serialize(doc, KgGraphEmit.JsonOpts));
    Console.Error.WriteLine(
        $"kggraph: {doc.Nodes.Count} node(s), {doc.Edges.Count} deduplicated edge(s) " +
        $"(from {allEdges.Count(e => e.Relation != "dynamic_sql")} occurrences), {doc.Facts.Count} fact(s). " +
        $"producer={KgGraphEmit.Producer}, gitSha={gitSha ?? "<none>"}.");
    // Cross-tier proc identity (#6): how many bare C#-tier proc targets joined to a
    // schema-qualified T-SQL definition vs. stayed bare (unresolved/ambiguous).
    Console.Error.WriteLine(
        $"kggraph proc identity: {resolve.Resolved} bare C# target(s) resolved to a schema-qualified " +
        $"definition, {resolve.Unresolved} unresolved, {resolve.Ambiguous} ambiguous (left bare).");
    // Point at the wiring recipe right when an ingest-shaped document was produced.
    Console.Error.WriteLine(
        "-> Ingest into trusty-search (>= 0.24.5) via POST /indexes/{id}/graph; " +
        "run `navigatsql --trusty-setup` for the full recipe.");
}
else
{
    Console.WriteLine(JsonSerializer.Serialize(allEdges, new JsonSerializerOptions { WriteIndented = true }));
}

// stderr: human summary + per-relation histogram.
var byRel = allEdges.GroupBy(e => e.Relation)
    .OrderBy(g => g.Key, StringComparer.Ordinal)
    .Select(g => $"{g.Key}={g.Count()}");
int csProc = allEdges.Count(e => e.FromKind == "csharp_method" && e.Relation == "calls_proc");
int csEmbedded = allEdges.Count(e => e.FromKind == "csharp_method" && (e.Relation == "reads_table" || e.Relation == "writes_table"));
Console.Error.WriteLine(
    $"navigaT-SQL: {sqlFiles} .sql + {csFiles} .cs scanned " +
    $"({filesWithErrors} with parse errors, {filesUnreadable} unreadable). " +
    $"{allEdges.Count} relation(s): {procs} proc/func/view def(s), {members} C# member(s) w/ data access, " +
    $"{csProc} method->proc, {csEmbedded} method->table (embedded SQL / EF), {dynamic} dynamic-SQL flagged. " +
    $"[{string.Join(", ", byRel)}]");
return 0;

// Recursively enumerate *.sql and *.cs, skipping VCS/build/dependency + generated files.
static IEnumerable<string> EnumerateCode(string dir)
{
    string[] skip =
    {
        $"{Path.DirectorySeparatorChar}.git{Path.DirectorySeparatorChar}",
        $"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}",
        $"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}",
        $"{Path.DirectorySeparatorChar}node_modules{Path.DirectorySeparatorChar}",
    };
    bool keep(string f)
    {
        if (skip.Any(s => f.Contains(s, StringComparison.OrdinalIgnoreCase))) return false;
        if (f.EndsWith(".Designer.cs", StringComparison.OrdinalIgnoreCase)) return false;
        if (f.EndsWith(".g.cs", StringComparison.OrdinalIgnoreCase)) return false;
        return true;
    }
    foreach (var pat in new[] { "*.sql", "*.cs" })
        foreach (var f in Directory.EnumerateFiles(dir, pat, SearchOption.AllDirectories))
        {
            var full = Path.GetFullPath(f);
            if (keep(full)) yield return full;
        }
}

// Path relative to the working directory, for compact, portable provenance.
static string Rel(string full) => Path.GetRelativePath(Directory.GetCurrentDirectory(), full);

// The canonical "how to feed trusty-search" message, printed by --trusty-setup (and
// pointed at from the kggraph summary). stderr only, so stdout stays data-clean even
// here. Kept in sync with README "Feeding the graph (trusty-tools integration)".
static void PrintWiringGuide()
{
    var w = Console.Error;
    w.WriteLine("navigaT-SQL -> trusty-search wiring");
    w.WriteLine("==================================");
    w.WriteLine("`--emit kggraph` output is the NATIVE ingest body for trusty-search's");
    w.WriteLine("contributed knowledge graph -- no converter or shim needed.");
    w.WriteLine();
    w.WriteLine("Requires trusty-search >= 0.24.5 (the POST /indexes/{id}/graph endpoint;");
    w.WriteLine("0.24.0-0.24.4 lack the route and return 404).");
    w.WriteLine("  check:   curl -s http://127.0.0.1:7878/health        # see \"version\"");
    w.WriteLine("  upgrade: curl -s -X POST http://127.0.0.1:7878/upgrade \\");
    w.WriteLine("             -H 'content-type: application/json' -d '{\"confirm\":true}'");
    w.WriteLine();
    w.WriteLine("Ingest (replace-per-producer):");
    w.WriteLine("  ID=<your index id>      # list: curl -s http://127.0.0.1:7878/indexes");
    w.WriteLine("  navigatsql --emit kggraph <repo> \\");
    w.WriteLine("    | curl -sS -X POST http://127.0.0.1:7878/indexes/$ID/graph \\");
    w.WriteLine("           -H 'content-type: application/json' --data-binary @-");
    w.WriteLine();
    w.WriteLine("When to re-run: navigaT-SQL is one-shot (no watcher). Re-run the pipe");
    w.WriteLine("  whenever the SQL/C# source changes; a trusty-search reindex does NOT");
    w.WriteLine("  refresh this overlay, so the refresh is on you. Replace-per-producer");
    w.WriteLine("  makes re-POSTing safe and idempotent -- wire it into CI on merge.");
    w.WriteLine();
    w.WriteLine("Query:      GET /indexes/{id}/graph/neighbors  (or the search_kg MCP tool)");
    w.WriteLine("Standalone: no trusty-search? --emit kggraph / --emit edges is plain JSON;");
    w.WriteLine("            consume it directly.");
    w.WriteLine("Full guide: README \"Feeding the graph (trusty-tools integration)\".");
}
