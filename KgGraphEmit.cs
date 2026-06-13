using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

/// kggraph emit mode — the ingest wire shape for trusty-tools ADR-0009.
///
/// Transforms the flat per-occurrence edge list into a deduplicated
/// node + edge document suitable for `POST /indexes/{id}/graph`, with
/// non-graph residue (dynamic-SQL flags) split into FactStore-shaped
/// `(subject, predicate, object)` triples.
///
/// Idempotency contract (ADR-0009): a node is its id; an edge is
/// `(from, to, relation)`. Re-scanning and re-ingesting must merge, not
/// duplicate — so this emitter dedupes, merges provenance, and orders
/// everything deterministically (ordinal sort) so identical inputs yield
/// byte-identical output.
internal static class KgGraphEmit
{
    /// A node is its id. Kind comes from the first edge endpoint that
    /// mentioned the id (kinds are consistent per id by construction —
    /// canonical table ids, schema-qualified routine names, Class.Method).
    internal record KgNode(string Id, string Kind);

    /// One deduplicated relation. `Kind` is the coarse #817-aligned kind
    /// (reads / writes / references / calls_function); `Relation` keeps the
    /// fine-grained original; `Tag` keeps the `custom:<relation>` escape-hatch
    /// key so a pre-#817 daemon can still ingest via Custom(String).
    /// Provenance lists every source file that asserted the relation.
    internal record KgEdge(
        string From, string FromKind, string To, string ToKind,
        string Kind, string Relation, string Tag,
        List<string> Provenance,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? LinkedServer);

    /// Non-graph residue, shaped for trusty-analyze's FactStore.
    internal record KgFact(string Subject, string Predicate, string Object, List<string> Provenance);

    /// The ingest document. The leading envelope identifies the producer and
    /// (optionally) the scanned tree's git HEAD, which the trusty-search ADR-0009
    /// endpoint requires (`producer`) / uses for cheap staleness checks (`gitSha`).
    /// `producerVersion` / `gitSha` follow the `linkedServer` convention: camelCase,
    /// omitted from JSON when null. No timestamps or machine names ever enter the
    /// envelope, so the document stays byte-deterministic (same tree ⇒ same bytes).
    internal record KgGraphDoc(
        string Schema, string Producer,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ProducerVersion,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? GitSha,
        List<KgNode> Nodes, List<KgEdge> Edges, List<KgFact> Facts);

    /// Schema bumped to @2 for the producer envelope (#2): the wire shape gained a
    /// new always-present top-level field (`producer`). The added fields are
    /// backward-compatible and trusty-search logs rather than enforces `schema`, so
    /// an @1 consumer still parses @2 — but the version bump keeps shape-versioning
    /// honest, letting a consumer tell enveloped (@2) from pre-envelope (@1) output.
    internal const string SchemaId = "navigatsql/kggraph@2";

    /// Replace-per-producer key the ADR-0009 endpoint requires (400 on missing/empty).
    internal const string Producer = "navigatsql";

    /// This assembly's informational version with `+<build-metadata>` stripped so the
    /// value stays a clean semver (the SDK appends the commit sha by default). Optional
    /// envelope field; null (and thus omitted) only if the attribute can't be read.
    internal static readonly string? ProducerVersion = ResolveVersion();

    private static string? ResolveVersion()
    {
        var info = typeof(KgGraphEmit).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (string.IsNullOrEmpty(info))
            info = typeof(KgGraphEmit).Assembly.GetName().Version?.ToString();
        if (string.IsNullOrEmpty(info)) return null;
        var plus = info.IndexOf('+');
        return plus >= 0 ? info[..plus] : info;
    }

    internal static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    /// Coarse edge kind aligned with the trusty-tools #817 first-class
    /// vocabulary (Reads / Writes / References / CallsFunction). Anything
    /// unrecognized degrades to "custom" rather than failing the emit.
    internal static string KindOf(string relation) => relation switch
    {
        "reads_table" => "reads",
        "writes_table" => "writes",
        "references" => "references",
        "calls_proc" => "calls_function",
        "calls_function" => "calls_function",
        _ => "custom",
    };

    private sealed class EdgeAgg
    {
        public required Edge First;
        public SortedSet<string> Files { get; } = new(StringComparer.Ordinal);
        public string? LinkedServer;
    }

    /// Cross-pass proc-identity resolution counts (#6), surfaced on stderr.
    /// `Resolved` bare C#-tier proc/function targets were re-pointed to their unique
    /// schema-qualified definition; `Unresolved` had no matching definition and
    /// `Ambiguous` matched more than one (different schema/db) — both kept bare, never
    /// guessed. Not part of the wire doc (ingest-irrelevant), so returned out-of-band.
    internal record ResolveStats(int Resolved, int Unresolved, int Ambiguous);

    /// Final dotted segment of a node id (`dbo.qry_X` -> `qry_X`; bare id unchanged).
    private static string LeafName(string id)
    {
        var dot = id.LastIndexOf('.');
        return dot < 0 ? id : id[(dot + 1)..];
    }

    /// The C# pass mints a proc target from the call site, which is almost always
    /// **bare** (`qry_X`), while the T-SQL pass mints the definition **schema-qualified**
    /// (`dbo.qry_X`); left alone they are two nodes and the cross-tier
    /// `method -> proc -> table` chain dead-ends at the bare sink (#6). After dedup we
    /// know the whole edge set, so we re-point each bare proc/function target at its
    /// definition node **iff exactly one** defined routine shares its leaf name. The C#
    /// side genuinely cannot know the schema, so the T-SQL definition is the only place
    /// the qualification exists; an unambiguous-definition match (not a `dbo` default)
    /// gets non-`dbo` schemas right for free. Ambiguous (same leaf under >1 schema/db) or
    /// undefined targets stay bare and are counted, never guessed. Pure function of the
    /// edge set (sorted scans, ordinal/leaf-insensitive index) so output stays
    /// byte-deterministic — `--emit edges` and per-pass output are untouched.
    ///
    /// Caveat: the unambiguous-definition match is confident, not omniscient. If the
    /// real callee's schema was never scanned but a same-named routine elsewhere was,
    /// the bare target resolves to that one — resolution assumes a reasonably complete
    /// scan, trading a rare wrong-schema join for fixing the common bare-vs-qualified
    /// seam. ("Never guess" governs ambiguity within what we saw, not missing inputs.)
    private static (Dictionary<string, string> Resolution, Dictionary<string, string> DefinedKind, ResolveStats Stats)
        ResolveProcIdentity(IReadOnlyCollection<Edge> edges)
    {
        // Defined routine nodes: anything the T-SQL pass saw the body of, i.e. a
        // proc/function appearing as an edge `From`. Kind is consistent per id.
        var definedKind = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var e in edges)
        {
            if (e.Relation == "dynamic_sql") continue;
            if (e.FromKind is "proc" or "function") definedKind.TryAdd(e.From, e.FromKind);
        }

        // Index defined ids by leaf name, case-insensitively: SQL identifiers are
        // case-insensitive, so `qry_X` and `QRY_X` are the same routine for matching.
        var byLeaf = new Dictionary<string, SortedSet<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var id in definedKind.Keys)
        {
            var leaf = LeafName(id);
            if (!byLeaf.TryGetValue(leaf, out var set))
                byLeaf[leaf] = set = new SortedSet<string>(StringComparer.Ordinal);
            set.Add(id);
        }

        // Truly bare proc/function targets (single segment — no explicit schema) that
        // aren't already a definition node. A schema-qualified-but-undefined target is
        // left alone: it stated its schema, so we never silently re-point it. Sorted
        // for deterministic counting independent of input order.
        var bareTargets = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var e in edges)
        {
            if (e.Relation == "dynamic_sql") continue;
            if (e.ToKind is "proc" or "function"
                && !e.To.Contains('.', StringComparison.Ordinal)
                && !definedKind.ContainsKey(e.To))
                bareTargets.Add(e.To);
        }

        var resolution = new Dictionary<string, string>(StringComparer.Ordinal);
        int unresolved = 0, ambiguous = 0;
        foreach (var bare in bareTargets)
        {
            // `set` holds the definition ids whose leaf matches `bare`; `bare` itself is
            // never among them (it is not a definition, by the precondition above).
            // Exactly one match resolves; more than one is a real cross-schema/db
            // collision we refuse to guess between.
            if (!byLeaf.TryGetValue(bare, out var set)) { unresolved++; continue; }
            if (set.Count == 1) resolution[bare] = set.First();
            else ambiguous++;
        }

        return (resolution, definedKind, new ResolveStats(resolution.Count, unresolved, ambiguous));
    }

    /// `gitSha` is the resolved HEAD of the scanned tree (or null — omitted), passed
    /// in by the caller; the emitter never shells out itself. Optional so existing
    /// callers/tests that don't supply it still get a valid (sha-less) document.
    internal static KgGraphDoc Build(IEnumerable<Edge> edges, string? gitSha = null)
        => Build(edges, gitSha, out _);

    /// Overload exposing the proc-identity resolution counts (#6) for the stderr
    /// summary; the wire doc itself never carries them.
    internal static KgGraphDoc Build(IEnumerable<Edge> edges, string? gitSha, out ResolveStats stats)
    {
        var edgeList = edges as IReadOnlyCollection<Edge> ?? edges.ToList();
        var (resolution, definedKind, resolveStats) = ResolveProcIdentity(edgeList);
        stats = resolveStats;

        var edgeAggs = new Dictionary<(string From, string To, string Relation), EdgeAgg>();
        var factAggs = new Dictionary<(string Subject, string Predicate, string Object), SortedSet<string>>();

        foreach (var e0 in edgeList)
        {
            // dynamic_sql is a flag about a scope, not a node->node relation:
            // it spills to the FactStore side of the contract.
            if (e0.Relation == "dynamic_sql")
            {
                var fk = (e0.From, e0.Relation, e0.To);
                if (!factAggs.TryGetValue(fk, out var files))
                    factAggs[fk] = files = new SortedSet<string>(StringComparer.Ordinal);
                files.Add(e0.File);
                continue;
            }

            // Re-point a bare proc/function target onto its schema-qualified definition
            // node before dedup, so the chain joins and the bare sink node disappears.
            var e = resolution.TryGetValue(e0.To, out var rid)
                ? e0 with { To = rid, ToKind = definedKind[rid] }
                : e0;

            var key = (e.From, e.To, e.Relation);
            if (!edgeAggs.TryGetValue(key, out var agg))
                edgeAggs[key] = agg = new EdgeAgg { First = e, LinkedServer = e.LinkedServer };
            agg.Files.Add(e.File);
            agg.LinkedServer ??= e.LinkedServer;
        }

        // Nodes derive from graph-edge endpoints only (the synthetic
        // `<dynamic>` target of dynamic_sql flags never becomes a node).
        // First kind wins; ids are kind-consistent by canonicalization.
        var nodes = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var agg in edgeAggs.Values
                     .OrderBy(a => a.First.From, StringComparer.Ordinal)
                     .ThenBy(a => a.First.To, StringComparer.Ordinal)
                     .ThenBy(a => a.First.Relation, StringComparer.Ordinal))
        {
            nodes.TryAdd(agg.First.From, agg.First.FromKind);
            nodes.TryAdd(agg.First.To, agg.First.ToKind);
        }

        // Explicit ordinal ordering everywhere: identical inputs (in any
        // order) must produce byte-identical output on any machine/culture.
        return new KgGraphDoc(
            SchemaId, Producer, ProducerVersion, gitSha,
            nodes.OrderBy(kv => kv.Key, StringComparer.Ordinal)
                 .Select(kv => new KgNode(kv.Key, kv.Value)).ToList(),
            edgeAggs.Values
                 .OrderBy(a => a.First.From, StringComparer.Ordinal)
                 .ThenBy(a => a.First.To, StringComparer.Ordinal)
                 .ThenBy(a => a.First.Relation, StringComparer.Ordinal)
                 .Select(a => new KgEdge(
                     a.First.From, a.First.FromKind, a.First.To, a.First.ToKind,
                     KindOf(a.First.Relation), a.First.Relation, a.First.EdgeKindTag,
                     a.Files.ToList(), a.LinkedServer)).ToList(),
            factAggs
                 .OrderBy(kv => kv.Key.Subject, StringComparer.Ordinal)
                 .ThenBy(kv => kv.Key.Predicate, StringComparer.Ordinal)
                 .ThenBy(kv => kv.Key.Object, StringComparer.Ordinal)
                 .Select(kv => new KgFact(
                     kv.Key.Subject, kv.Key.Predicate, kv.Key.Object, kv.Value.ToList())).ToList());
    }
}
