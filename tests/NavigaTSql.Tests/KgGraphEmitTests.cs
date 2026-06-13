using Xunit;

// Tests for KgGraphEmit: the --emit kggraph wire shape (trusty-tools ADR-0009).
//
// Covers: edge dedup + provenance merge, node derivation, dynamic_sql -> facts
// spillover, relation -> coarse-kind mapping, linked-server retention, and
// deterministic (input-order-independent) output.
//
// Naming convention: <Behavior>_<Scenario>_<Expected>.
public class KgGraphEmitTests
{
    // ------------------------------------------------------------------ helpers

    private static Edge E(
        string file, string from, string fromKind, string to, string toKind,
        string relation, string? linkedServer = null) =>
        new(file, from, fromKind, to, toKind, relation, $"custom:{relation}", linkedServer);

    // ==========================================================================
    // A) Dedup + provenance
    // ==========================================================================

    [Fact]
    public void Build_SameEdgeFromTwoFiles_DedupesAndMergesProvenance()
    {
        var doc = KgGraphEmit.Build(new[]
        {
            E("b.sql", "dbo.p", "proc", "dbo.t", "table", "reads_table"),
            E("a.sql", "dbo.p", "proc", "dbo.t", "table", "reads_table"),
        });
        var edge = Assert.Single(doc.Edges);
        Assert.Equal(new[] { "a.sql", "b.sql" }, edge.Provenance); // sorted, both kept
    }

    [Fact]
    public void Build_SameEndpointsDifferentRelation_RemainSeparateEdges()
    {
        var doc = KgGraphEmit.Build(new[]
        {
            E("a.sql", "dbo.p", "proc", "dbo.t", "table", "reads_table"),
            E("a.sql", "dbo.p", "proc", "dbo.t", "table", "writes_table"),
        });
        Assert.Equal(2, doc.Edges.Count);
    }

    [Fact]
    public void Build_DuplicateOccurrenceSameFile_SingleProvenanceEntry()
    {
        var doc = KgGraphEmit.Build(new[]
        {
            E("a.sql", "dbo.p", "proc", "dbo.t", "table", "reads_table"),
            E("a.sql", "dbo.p", "proc", "dbo.t", "table", "reads_table"),
        });
        var edge = Assert.Single(doc.Edges);
        Assert.Equal(new[] { "a.sql" }, edge.Provenance);
    }

    // ==========================================================================
    // B) Node derivation
    // ==========================================================================

    [Fact]
    public void Build_ProcReadsTable_YieldsBothNodesWithKinds()
    {
        var doc = KgGraphEmit.Build(new[]
        {
            E("a.sql", "dbo.p", "proc", "dbo.t", "table", "reads_table"),
        });
        Assert.Equal(2, doc.Nodes.Count);
        Assert.Contains(doc.Nodes, n => n.Id == "dbo.p" && n.Kind == "proc");
        Assert.Contains(doc.Nodes, n => n.Id == "dbo.t" && n.Kind == "table");
    }

    [Fact]
    public void Build_SharedEndpoint_EmitsOneNode()
    {
        var doc = KgGraphEmit.Build(new[]
        {
            E("a.sql", "dbo.p1", "proc", "dbo.t", "table", "reads_table"),
            E("a.sql", "dbo.p2", "proc", "dbo.t", "table", "writes_table"),
        });
        Assert.Single(doc.Nodes, n => n.Id == "dbo.t");
        Assert.Equal(3, doc.Nodes.Count);
    }

    // ==========================================================================
    // C) dynamic_sql -> facts spillover
    // ==========================================================================

    [Fact]
    public void Build_DynamicSql_GoesToFactsNotEdges()
    {
        var doc = KgGraphEmit.Build(new[]
        {
            E("a.sql", "dbo.p", "proc", "<dynamic>", "unresolved", "dynamic_sql"),
        });
        Assert.Empty(doc.Edges);
        var fact = Assert.Single(doc.Facts);
        Assert.Equal("dbo.p", fact.Subject);
        Assert.Equal("dynamic_sql", fact.Predicate);
        Assert.Equal("<dynamic>", fact.Object);
        Assert.Equal(new[] { "a.sql" }, fact.Provenance);
    }

    [Fact]
    public void Build_DynamicSql_DoesNotMintNodes()
    {
        var doc = KgGraphEmit.Build(new[]
        {
            E("a.sql", "dbo.p", "proc", "<dynamic>", "unresolved", "dynamic_sql"),
        });
        Assert.Empty(doc.Nodes); // neither dbo.p (no graph edge) nor <dynamic>
    }

    [Fact]
    public void Build_DynamicSqlFromTwoFiles_DedupesFactMergesProvenance()
    {
        var doc = KgGraphEmit.Build(new[]
        {
            E("b.sql", "dbo.p", "proc", "<dynamic>", "unresolved", "dynamic_sql"),
            E("a.sql", "dbo.p", "proc", "<dynamic>", "unresolved", "dynamic_sql"),
        });
        var fact = Assert.Single(doc.Facts);
        Assert.Equal(new[] { "a.sql", "b.sql" }, fact.Provenance);
    }

    // ==========================================================================
    // D) Relation -> coarse kind mapping (#817 vocabulary)
    // ==========================================================================

    [Theory]
    [InlineData("reads_table", "reads")]
    [InlineData("writes_table", "writes")]
    [InlineData("references", "references")]
    [InlineData("calls_proc", "calls_function")]
    [InlineData("calls_function", "calls_function")]
    [InlineData("something_new", "custom")]
    public void KindOf_MapsRelationToCoarseKind(string relation, string expected) =>
        Assert.Equal(expected, KgGraphEmit.KindOf(relation));

    [Fact]
    public void Build_EdgeCarriesKindRelationAndTag()
    {
        var doc = KgGraphEmit.Build(new[]
        {
            E("a.sql", "dbo.p", "proc", "dbo.t", "table", "writes_table"),
        });
        var edge = Assert.Single(doc.Edges);
        Assert.Equal("writes", edge.Kind);
        Assert.Equal("writes_table", edge.Relation);
        Assert.Equal("custom:writes_table", edge.Tag);
    }

    // ==========================================================================
    // E) Linked server metadata
    // ==========================================================================

    [Fact]
    public void Build_LinkedServer_RetainedOnDedupedEdge()
    {
        var doc = KgGraphEmit.Build(new[]
        {
            E("a.sql", "dbo.p", "proc", "mydb.dbo.t", "table", "reads_table", linkedServer: null),
            E("b.sql", "dbo.p", "proc", "mydb.dbo.t", "table", "reads_table", linkedServer: "srv"),
        });
        var edge = Assert.Single(doc.Edges);
        Assert.Equal("srv", edge.LinkedServer); // first non-null wins
    }

    // ==========================================================================
    // F) Determinism
    // ==========================================================================

    [Fact]
    public void Build_InputOrderDoesNotChangeOutput()
    {
        var edges = new[]
        {
            E("z.sql", "dbo.p2", "proc", "dbo.t2", "table", "writes_table"),
            E("a.sql", "dbo.p1", "proc", "dbo.t1", "table", "reads_table"),
            E("m.cs", "Repo.Save", "csharp_method", "dbo.p1", "proc", "calls_proc"),
            E("a.sql", "dbo.p1", "proc", "<dynamic>", "unresolved", "dynamic_sql"),
        };
        var forward = System.Text.Json.JsonSerializer.Serialize(
            KgGraphEmit.Build(edges), KgGraphEmit.JsonOpts);
        var reversed = System.Text.Json.JsonSerializer.Serialize(
            KgGraphEmit.Build(edges.Reverse().ToArray()), KgGraphEmit.JsonOpts);
        Assert.Equal(forward, reversed);
    }

    [Fact]
    public void Build_EdgesSortedByFromToRelation()
    {
        var doc = KgGraphEmit.Build(new[]
        {
            E("a.sql", "dbo.zz", "proc", "dbo.t", "table", "reads_table"),
            E("a.sql", "dbo.aa", "proc", "dbo.t", "table", "reads_table"),
        });
        Assert.Equal("dbo.aa", doc.Edges[0].From);
        Assert.Equal("dbo.zz", doc.Edges[1].From);
    }

    // ==========================================================================
    // G) Document envelope
    // ==========================================================================

    [Fact]
    public void Build_EmitsSchemaId()
    {
        var doc = KgGraphEmit.Build(Array.Empty<Edge>());
        Assert.Equal("navigatsql/kggraph@2", doc.Schema);
        Assert.Empty(doc.Nodes);
        Assert.Empty(doc.Edges);
        Assert.Empty(doc.Facts);
    }

    [Fact]
    public void Serialize_UsesCamelCasePropertyNames()
    {
        var doc = KgGraphEmit.Build(new[]
        {
            E("a.sql", "dbo.p", "proc", "dbo.t", "table", "reads_table"),
        });
        var json = System.Text.Json.JsonSerializer.Serialize(doc, KgGraphEmit.JsonOpts);
        Assert.Contains("\"schema\"", json);
        Assert.Contains("\"fromKind\"", json);
        Assert.Contains("\"provenance\"", json);
        Assert.DoesNotContain("\"FromKind\"", json);
    }

    [Fact]
    public void Serialize_OmitsNullLinkedServer()
    {
        var doc = KgGraphEmit.Build(new[]
        {
            E("a.sql", "dbo.p", "proc", "dbo.t", "table", "reads_table"),
        });
        var json = System.Text.Json.JsonSerializer.Serialize(doc, KgGraphEmit.JsonOpts);
        Assert.DoesNotContain("linkedServer", json);
    }

    // ==========================================================================
    // H) Producer envelope (#2)
    // ==========================================================================

    [Fact]
    public void Build_EmitsProducerEnvelope()
    {
        var doc = KgGraphEmit.Build(Array.Empty<Edge>());
        Assert.Equal("navigatsql", doc.Producer);
        Assert.False(string.IsNullOrEmpty(doc.ProducerVersion)); // assembly version present
    }

    [Fact]
    public void Build_GitShaProvided_AppearsInDoc()
    {
        var sha = "0123456789abcdef0123456789abcdef01234567";
        var doc = KgGraphEmit.Build(Array.Empty<Edge>(), sha);
        Assert.Equal(sha, doc.GitSha);
    }

    [Fact]
    public void Build_NoGitSha_OmittedFromJson_ButProducerPresent()
    {
        var doc = KgGraphEmit.Build(new[]
        {
            E("a.sql", "dbo.p", "proc", "dbo.t", "table", "reads_table"),
        }); // no gitSha argument
        Assert.Null(doc.GitSha);
        var json = System.Text.Json.JsonSerializer.Serialize(doc, KgGraphEmit.JsonOpts);
        Assert.DoesNotContain("gitSha", json);   // omitted-when-null, like linkedServer
        Assert.Contains("\"producer\"", json);   // required field always present
    }

    [Fact]
    public void Serialize_EnvelopeUsesCamelCaseAndCarriesGitSha()
    {
        var doc = KgGraphEmit.Build(
            new[] { E("a.sql", "dbo.p", "proc", "dbo.t", "table", "reads_table") },
            "deadbeefdeadbeefdeadbeefdeadbeefdeadbeef");
        var json = System.Text.Json.JsonSerializer.Serialize(doc, KgGraphEmit.JsonOpts);
        Assert.Contains("\"producer\"", json);
        Assert.Contains("\"producerVersion\"", json);
        Assert.Contains("\"gitSha\"", json);
        Assert.Contains("deadbeefdeadbeefdeadbeefdeadbeefdeadbeef", json);
    }

    [Fact]
    public void Build_WithEnvelope_IsByteIdenticalAcrossRuns()
    {
        var edges = new[]
        {
            E("z.sql", "dbo.p2", "proc", "dbo.t2", "table", "writes_table"),
            E("a.sql", "dbo.p1", "proc", "dbo.t1", "table", "reads_table"),
            E("a.sql", "dbo.p1", "proc", "<dynamic>", "unresolved", "dynamic_sql"),
        };
        const string sha = "feedface00000000feedface00000000feedface";
        var first = System.Text.Json.JsonSerializer.Serialize(
            KgGraphEmit.Build(edges, sha), KgGraphEmit.JsonOpts);
        var second = System.Text.Json.JsonSerializer.Serialize(
            KgGraphEmit.Build(edges.Reverse().ToArray(), sha), KgGraphEmit.JsonOpts);
        Assert.Equal(first, second); // envelope + body both deterministic
    }

    // ==========================================================================
    // I) Cross-pass proc-identity resolution (#6)
    //
    // The C# pass mints a bare proc target (`qry_X`); the T-SQL pass mints the
    // definition schema-qualified (`dbo.qry_X`). The emitter re-points the bare
    // target onto its unique definition so method -> proc -> table traverses.
    // ==========================================================================

    [Fact]
    public void Build_BareCsTarget_ResolvesToUniqueSchemaQualifiedDefinition()
    {
        var doc = KgGraphEmit.Build(new[]
        {
            // C# tier: bare proc target.
            E("m.cs", "Repo.AddUser", "csharp_method", "usp_AddUser", "proc", "calls_proc"),
            // T-SQL tier: schema-qualified definition with two table edges.
            E("p.sql", "dbo.usp_AddUser", "proc", "salesdb.dbo.accounts", "table", "reads_table"),
            E("p.sql", "dbo.usp_AddUser", "proc", "salesdb.dbo.orders", "table", "writes_table"),
        });

        // The calls_proc edge now points at the schema-qualified node, not the bare one.
        var call = Assert.Single(doc.Edges, e => e.Relation == "calls_proc");
        Assert.Equal("dbo.usp_AddUser", call.To);
        Assert.Equal("proc", call.ToKind);

        // The bare sink node is gone; the qualified node carries every edge.
        Assert.DoesNotContain(doc.Nodes, n => n.Id == "usp_AddUser");
        Assert.Contains(doc.Nodes, n => n.Id == "dbo.usp_AddUser" && n.Kind == "proc");

        // 2-hop method -> proc -> {both tables} is now reachable through one node.
        var outFromProc = doc.Edges
            .Where(e => e.From == "dbo.usp_AddUser")
            .Select(e => e.To).OrderBy(t => t).ToArray();
        Assert.Equal(new[] { "salesdb.dbo.accounts", "salesdb.dbo.orders" }, outFromProc);
    }

    [Fact]
    public void Build_BareCsTarget_ResolutionCountReportedInStats()
    {
        KgGraphEmit.Build(new[]
        {
            E("m.cs", "Repo.Save", "csharp_method", "qry_X", "proc", "calls_proc"),
            E("p.sql", "dbo.qry_X", "proc", "dbo.t", "table", "reads_table"),
        }, null, out var stats);
        Assert.Equal(1, stats.Resolved);
        Assert.Equal(0, stats.Unresolved);
        Assert.Equal(0, stats.Ambiguous);
    }

    [Fact]
    public void Build_BareTargetDefinedUnderTwoSchemas_StaysBareAndCountsAmbiguous()
    {
        var doc = KgGraphEmit.Build(new[]
        {
            E("m.cs", "Repo.Save", "csharp_method", "qry_X", "proc", "calls_proc"),
            E("a.sql", "dbo.qry_X", "proc", "dbo.t", "table", "reads_table"),
            E("b.sql", "sales.qry_X", "proc", "dbo.t", "table", "reads_table"),
        }, null, out var stats);

        var call = Assert.Single(doc.Edges, e => e.Relation == "calls_proc");
        Assert.Equal("qry_X", call.To); // never guessed between dbo and sales
        Assert.Contains(doc.Nodes, n => n.Id == "qry_X");
        Assert.Equal(0, stats.Resolved);
        Assert.Equal(1, stats.Ambiguous);
        Assert.Equal(0, stats.Unresolved);
    }

    [Fact]
    public void Build_BareTargetWithNoDefinition_StaysBareAndCountsUnresolved()
    {
        var doc = KgGraphEmit.Build(new[]
        {
            E("m.cs", "Repo.Save", "csharp_method", "qry_Missing", "proc", "calls_proc"),
            E("p.sql", "dbo.qry_Other", "proc", "dbo.t", "table", "reads_table"),
        }, null, out var stats);

        var call = Assert.Single(doc.Edges, e => e.Relation == "calls_proc");
        Assert.Equal("qry_Missing", call.To);
        Assert.Equal(0, stats.Resolved);
        Assert.Equal(1, stats.Unresolved);
        Assert.Equal(0, stats.Ambiguous);
    }

    [Fact]
    public void Build_AlreadyQualifiedCsTarget_LeftUntouched()
    {
        var doc = KgGraphEmit.Build(new[]
        {
            E("m.cs", "Repo.Save", "csharp_method", "dbo.qry_X", "proc", "calls_proc"),
            E("p.sql", "dbo.qry_X", "proc", "dbo.t", "table", "reads_table"),
        }, null, out var stats);

        var call = Assert.Single(doc.Edges, e => e.Relation == "calls_proc");
        Assert.Equal("dbo.qry_X", call.To);
        Assert.Equal(0, stats.Resolved); // nothing bare to resolve
        Assert.Equal(0, stats.Unresolved);
        Assert.Equal(0, stats.Ambiguous);
    }

    [Fact]
    public void Build_BareTargetWithBareDefinition_NotTreatedAsUnresolved()
    {
        // Definition itself is bare (`CREATE PROCEDURE qry_X`, no schema): the chain
        // already joins on the bare id, so there is nothing to resolve or count.
        var doc = KgGraphEmit.Build(new[]
        {
            E("m.cs", "Repo.Save", "csharp_method", "qry_X", "proc", "calls_proc"),
            E("p.sql", "qry_X", "proc", "dbo.t", "table", "reads_table"),
        }, null, out var stats);

        Assert.Single(doc.Nodes, n => n.Id == "qry_X");
        Assert.Equal(0, stats.Resolved);
        Assert.Equal(0, stats.Unresolved);
        Assert.Equal(0, stats.Ambiguous);
    }

    [Fact]
    public void Build_ResolutionMergesProvenanceAcrossBareAndQualifiedAssertions()
    {
        // Two C# call sites assert the same bare target; after resolution the
        // calls_proc edge keeps both files in provenance.
        var doc = KgGraphEmit.Build(new[]
        {
            E("b.cs", "Repo.Save", "csharp_method", "qry_X", "proc", "calls_proc"),
            E("a.cs", "Repo.Save", "csharp_method", "qry_X", "proc", "calls_proc"),
            E("p.sql", "dbo.qry_X", "proc", "dbo.t", "table", "reads_table"),
        });
        var call = Assert.Single(doc.Edges, e => e.Relation == "calls_proc");
        Assert.Equal("dbo.qry_X", call.To);
        Assert.Equal(new[] { "a.cs", "b.cs" }, call.Provenance);
    }

    [Fact]
    public void Build_FunctionDefinition_ResolvesBareTargetAndCarriesFunctionKind()
    {
        var doc = KgGraphEmit.Build(new[]
        {
            E("m.cs", "Repo.Calc", "csharp_method", "fn_Rate", "proc", "calls_proc"),
            E("p.sql", "dbo.fn_Rate", "function", "dbo.t", "table", "reads_table"),
        });
        var call = Assert.Single(doc.Edges, e => e.Relation == "calls_proc");
        Assert.Equal("dbo.fn_Rate", call.To);
        Assert.Equal("function", call.ToKind); // kind follows the definition
    }

    [Fact]
    public void Build_Resolution_IsInputOrderIndependent()
    {
        var edges = new[]
        {
            E("m.cs", "Repo.Save", "csharp_method", "qry_X", "proc", "calls_proc"),
            E("a.sql", "dbo.qry_X", "proc", "dbo.t1", "table", "reads_table"),
            E("b.sql", "dbo.qry_X", "proc", "dbo.t2", "table", "writes_table"),
        };
        var forward = System.Text.Json.JsonSerializer.Serialize(
            KgGraphEmit.Build(edges), KgGraphEmit.JsonOpts);
        var reversed = System.Text.Json.JsonSerializer.Serialize(
            KgGraphEmit.Build(edges.Reverse().ToArray()), KgGraphEmit.JsonOpts);
        Assert.Equal(forward, reversed);
    }
}
