using Xunit;

// Focused tests for TSqlPass reads_table / writes_table extraction, canonicalization,
// scope attribution, alias resolution, deduplication, and EdgeKindTag correctness.
// File: tests/NavigaTSql.Tests/TSqlReadsWritesTests.cs
public class TSqlReadsWritesTests
{
    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------

    // Filter edges by relation for brevity in tests that only care about one kind.
    private static List<Edge> Reads(PassResult r) =>
        r.Edges.Where(e => e.Relation == "reads_table").ToList();

    private static List<Edge> Writes(PassResult r) =>
        r.Edges.Where(e => e.Relation == "writes_table").ToList();

    // Assert a single edge with the given To exists in the list.
    private static void HasRead(PassResult r, string to) =>
        Assert.Contains(Reads(r), e => e.To == to);

    private static void HasWrite(PassResult r, string to) =>
        Assert.Contains(Writes(r), e => e.To == to);

    private static void NoRead(PassResult r, string to) =>
        Assert.DoesNotContain(Reads(r), e => e.To == to);

    private static void NoWrite(PassResult r, string to) =>
        Assert.DoesNotContain(Writes(r), e => e.To == to);

    // -----------------------------------------------------------------------
    // 1. reads_table — simple FROM
    // -----------------------------------------------------------------------

    [Fact]
    public void SimpleFrom_schema_qualified_table_is_canonicalized_lowercase()
    {
        // db-from-filename is off by default → no db context → To = "dbo.t1"
        var r = TSqlPass.Run("p.sql", "SELECT * FROM dbo.T1");
        HasRead(r, "dbo.t1");
    }

    [Fact]
    public void SimpleFrom_unqualified_table_defaults_schema_to_dbo()
    {
        // Unqualified T1 with no USE → "dbo.t1"
        var r = TSqlPass.Run("p.sql", "SELECT * FROM T1");
        HasRead(r, "dbo.t1");
    }

    [Fact]
    public void SimpleFrom_MixedCase_table_name_is_lowercased()
    {
        var r = TSqlPass.Run("p.sql", "SELECT * FROM dbo.MyTable");
        HasRead(r, "dbo.mytable");
    }

    [Fact]
    public void SimpleFrom_two_part_name_preserves_schema()
    {
        var r = TSqlPass.Run("p.sql", "SELECT * FROM sales.Orders");
        HasRead(r, "sales.orders");
    }

    // -----------------------------------------------------------------------
    // 1b. reads_table — JOIN
    // -----------------------------------------------------------------------

    [Fact]
    public void Join_both_tables_emit_reads_table()
    {
        var r = TSqlPass.Run("p.sql",
            "SELECT * FROM dbo.Orders o INNER JOIN dbo.Customers c ON o.CustomerID = c.ID");

        HasRead(r, "dbo.orders");
        HasRead(r, "dbo.customers");
    }

    [Fact]
    public void Join_left_outer_both_tables_emit_reads_table()
    {
        var r = TSqlPass.Run("p.sql",
            "SELECT * FROM dbo.T1 LEFT OUTER JOIN dbo.T2 ON dbo.T1.id = dbo.T2.id");

        HasRead(r, "dbo.t1");
        HasRead(r, "dbo.t2");
    }

    [Fact]
    public void Join_three_tables_all_emit_reads_table()
    {
        var r = TSqlPass.Run("p.sql",
            "SELECT * FROM dbo.A JOIN dbo.B ON A.id = B.id JOIN dbo.C ON B.id = C.id");

        HasRead(r, "dbo.a");
        HasRead(r, "dbo.b");
        HasRead(r, "dbo.c");
    }

    // -----------------------------------------------------------------------
    // 1c. reads_table — subquery in FROM
    // -----------------------------------------------------------------------

    [Fact]
    public void Subquery_in_FROM_emits_reads_for_inner_table()
    {
        // The outer query aliases a subquery; the inner FROM dbo.Orders should be read.
        var r = TSqlPass.Run("p.sql",
            "SELECT * FROM (SELECT id FROM dbo.Orders) sub");

        HasRead(r, "dbo.orders");
    }

    [Fact]
    public void Subquery_in_FROM_nested_two_levels()
    {
        var r = TSqlPass.Run("p.sql",
            "SELECT * FROM (SELECT * FROM (SELECT id FROM dbo.InnerTbl) a) b");

        HasRead(r, "dbo.innertbl");
    }

    // -----------------------------------------------------------------------
    // 2. writes_table — INSERT INTO
    // -----------------------------------------------------------------------

    [Fact]
    public void Insert_emits_writes_table_for_target()
    {
        var r = TSqlPass.Run("p.sql", "INSERT INTO dbo.Orders (id) VALUES (1)");
        HasWrite(r, "dbo.orders");
    }

    [Fact]
    public void Insert_unqualified_target_defaults_schema_to_dbo()
    {
        var r = TSqlPass.Run("p.sql", "INSERT INTO Orders (id) VALUES (1)");
        HasWrite(r, "dbo.orders");
    }

    // -----------------------------------------------------------------------
    // 2b. writes_table — UPDATE
    // -----------------------------------------------------------------------

    [Fact]
    public void Update_emits_writes_table_for_target()
    {
        var r = TSqlPass.Run("p.sql", "UPDATE dbo.Customers SET Name = 'X' WHERE id = 1");
        HasWrite(r, "dbo.customers");
    }

    [Fact]
    public void Update_does_not_emit_reads_table_for_target_when_no_from()
    {
        // Simple UPDATE with no FROM — target is a write, not also a read.
        var r = TSqlPass.Run("p.sql", "UPDATE dbo.Customers SET Name = 'X' WHERE id = 1");
        // The target itself must not appear as a reads_table edge (no FROM clause).
        NoRead(r, "dbo.customers");
    }

    // -----------------------------------------------------------------------
    // 2c. writes_table — DELETE
    // -----------------------------------------------------------------------

    [Fact]
    public void Delete_emits_writes_table_for_target()
    {
        var r = TSqlPass.Run("p.sql", "DELETE FROM dbo.Logs WHERE id = 1");
        HasWrite(r, "dbo.logs");
    }

    [Fact]
    public void Delete_unqualified_target_defaults_schema_to_dbo()
    {
        var r = TSqlPass.Run("p.sql", "DELETE FROM Logs WHERE id = 1");
        HasWrite(r, "dbo.logs");
    }

    // -----------------------------------------------------------------------
    // 3. INSERT...SELECT emits both write (target) and read (source)
    // -----------------------------------------------------------------------

    [Fact]
    public void InsertSelect_emits_write_for_target_and_read_for_source()
    {
        var r = TSqlPass.Run("p.sql",
            "INSERT INTO dbo.Archive SELECT * FROM dbo.Orders");

        HasWrite(r, "dbo.archive");
        HasRead(r, "dbo.orders");
    }

    [Fact]
    public void InsertSelect_target_is_not_also_a_read()
    {
        // INSERT target should NOT appear in reads_table.
        var r = TSqlPass.Run("p.sql",
            "INSERT INTO dbo.Archive SELECT * FROM dbo.Orders");

        NoRead(r, "dbo.archive");
    }

    [Fact]
    public void InsertSelect_source_is_not_also_a_write()
    {
        // SELECT source should NOT appear in writes_table.
        var r = TSqlPass.Run("p.sql",
            "INSERT INTO dbo.Archive SELECT * FROM dbo.Orders");

        NoWrite(r, "dbo.orders");
    }

    [Fact]
    public void InsertSelect_multi_table_join_in_source_all_read()
    {
        var r = TSqlPass.Run("p.sql",
            "INSERT INTO dbo.Summary SELECT o.id, c.name FROM dbo.Orders o JOIN dbo.Customers c ON o.cid = c.id");

        HasWrite(r, "dbo.summary");
        HasRead(r, "dbo.orders");
        HasRead(r, "dbo.customers");
    }

    // -----------------------------------------------------------------------
    // 4. UPDATE with alias — alias resolves back to real table
    // -----------------------------------------------------------------------

    [Fact]
    public void Update_alias_in_target_resolves_to_real_table()
    {
        // UPDATE p SET ... FROM dbo.T AS p — target "p" must resolve to dbo.t.
        var r = TSqlPass.Run("p.sql",
            "UPDATE p SET p.Name = 'X' FROM dbo.T AS p WHERE p.id = 1");

        HasWrite(r, "dbo.t");
    }

    [Fact]
    public void Update_alias_target_does_not_emit_write_for_alias_name()
    {
        // "p" (the alias) must NOT appear as a writes_table target.
        var r = TSqlPass.Run("p.sql",
            "UPDATE p SET p.Name = 'X' FROM dbo.T AS p WHERE p.id = 1");

        NoWrite(r, "dbo.p");
        Assert.DoesNotContain(Writes(r), e => e.To == "p");
    }

    [Fact]
    public void Update_alias_MixedCase_alias_still_resolves()
    {
        var r = TSqlPass.Run("p.sql",
            "UPDATE Alias SET Alias.Val = 1 FROM dbo.RealTable AS Alias WHERE Alias.id = 42");

        HasWrite(r, "dbo.realtable");
        Assert.DoesNotContain(Writes(r), e => e.To == "alias" || e.To == "dbo.alias");
    }

    // -----------------------------------------------------------------------
    // 5. MERGE — target is writes_table, USING source is reads_table
    // -----------------------------------------------------------------------

    [Fact]
    public void Merge_target_emits_writes_table()
    {
        var r = TSqlPass.Run("p.sql", @"
            MERGE dbo.Target AS t
            USING dbo.Source AS s ON t.id = s.id
            WHEN MATCHED THEN UPDATE SET t.val = s.val
            WHEN NOT MATCHED THEN INSERT (id, val) VALUES (s.id, s.val);");

        HasWrite(r, "dbo.target");
    }

    [Fact]
    public void Merge_source_emits_reads_table()
    {
        var r = TSqlPass.Run("p.sql", @"
            MERGE dbo.Target AS t
            USING dbo.Source AS s ON t.id = s.id
            WHEN MATCHED THEN UPDATE SET t.val = s.val
            WHEN NOT MATCHED THEN INSERT (id, val) VALUES (s.id, s.val);");

        HasRead(r, "dbo.source");
    }

    [Fact]
    public void Merge_target_is_not_also_a_read()
    {
        // The MERGE target table must not also appear as reads_table.
        var r = TSqlPass.Run("p.sql", @"
            MERGE dbo.Target AS t
            USING dbo.Source AS s ON t.id = s.id
            WHEN MATCHED THEN DELETE;");

        NoRead(r, "dbo.target");
    }

    [Fact]
    public void Merge_source_is_not_also_a_write()
    {
        // The MERGE source table must not also appear as writes_table.
        var r = TSqlPass.Run("p.sql", @"
            MERGE dbo.Target AS t
            USING dbo.Source AS s ON t.id = s.id
            WHEN MATCHED THEN DELETE;");

        NoWrite(r, "dbo.source");
    }

    [Fact]
    public void Merge_pseudo_tables_target_source_are_excluded()
    {
        // The unqualified bare words "target" / "source" used as aliases inside MERGE
        // are filtered by Canon (line 291 in TSqlPass.cs); they must not appear as edges.
        var r = TSqlPass.Run("p.sql", @"
            MERGE dbo.RealTarget AS target
            USING dbo.RealSource AS source ON target.id = source.id
            WHEN MATCHED THEN UPDATE SET target.val = source.val;");

        // "target" and "source" as raw strings must never appear as edge To values.
        Assert.DoesNotContain(r.Edges, e => e.To == "target" || e.To == "source");
    }

    // -----------------------------------------------------------------------
    // 6. Scope attribution
    // -----------------------------------------------------------------------

    [Fact]
    public void Read_inside_proc_has_From_set_to_proc_name_with_original_case()
    {
        var r = TSqlPass.Run("p.sql",
            "CREATE PROCEDURE dbo.usp_GetOrders AS SELECT * FROM dbo.Orders");

        var edge = Assert.Single(Reads(r), e => e.To == "dbo.orders");
        // From preserves original case of the proc name as parsed by Naming.Of.
        Assert.Equal("dbo.usp_GetOrders", edge.From);
    }

    [Fact]
    public void Read_inside_proc_has_FromKind_proc()
    {
        var r = TSqlPass.Run("p.sql",
            "CREATE PROCEDURE dbo.MyProc AS SELECT * FROM dbo.T1");

        var edge = Assert.Single(Reads(r), e => e.To == "dbo.t1");
        Assert.Equal("proc", edge.FromKind);
    }

    [Fact]
    public void Read_at_script_level_has_From_script_level_sentinel()
    {
        // A bare SELECT with no wrapping CREATE PROCEDURE uses the sentinel.
        var r = TSqlPass.Run("p.sql", "SELECT * FROM dbo.T1");

        var edge = Assert.Single(Reads(r), e => e.To == "dbo.t1");
        Assert.Equal("<script-level>", edge.From);
    }

    [Fact]
    public void Read_at_script_level_has_FromKind_proc()
    {
        // _scopeKind initial value is "proc"; it never changes for script-level statements.
        var r = TSqlPass.Run("p.sql", "SELECT * FROM dbo.T1");

        var edge = Assert.Single(Reads(r), e => e.To == "dbo.t1");
        Assert.Equal("proc", edge.FromKind);
    }

    [Fact]
    public void Read_inside_proc_scope_not_attributed_to_script_level()
    {
        var r = TSqlPass.Run("p.sql",
            "CREATE PROCEDURE dbo.Proc1 AS SELECT * FROM dbo.Tbl");

        var edge = Assert.Single(Reads(r), e => e.To == "dbo.tbl");
        Assert.NotEqual("<script-level>", edge.From);
    }

    [Fact]
    public void Proc_name_MixedCase_preserved_exactly()
    {
        // Naming.Of joins Identifier values as-is; case must not be folded.
        var r = TSqlPass.Run("p.sql",
            "CREATE PROCEDURE dbo.usp_X AS SELECT * FROM dbo.T");

        var edge = Assert.Single(Reads(r), e => e.To == "dbo.t");
        Assert.Equal("dbo.usp_X", edge.From);
    }

    // -----------------------------------------------------------------------
    // 7. Deduplication — same (proc, table, relation) appears only once
    // -----------------------------------------------------------------------

    [Fact]
    public void Reads_are_deduped_within_same_scope()
    {
        // T1 is read twice inside the same proc; only one reads_table edge must appear.
        var r = TSqlPass.Run("p.sql", @"
            CREATE PROCEDURE dbo.P AS
            BEGIN
                SELECT * FROM dbo.T1;
                SELECT id FROM dbo.T1;
            END");

        var reads = Reads(r).Where(e => e.To == "dbo.t1").ToList();
        Assert.Single(reads);
    }

    [Fact]
    public void Reads_dedup_is_case_insensitive_on_table_name()
    {
        // dbo.T1 and dbo.t1 canonicalize to the same thing; must appear once.
        var r = TSqlPass.Run("p.sql", @"
            CREATE PROCEDURE dbo.P AS
            BEGIN
                SELECT * FROM dbo.T1;
                SELECT * FROM dbo.t1;
            END");

        var reads = Reads(r).Where(e => e.To == "dbo.t1").ToList();
        Assert.Single(reads);
    }

    [Fact]
    public void Writes_are_deduped_within_same_scope()
    {
        // Two INSERT INTO the same table inside one proc → single writes_table edge.
        var r = TSqlPass.Run("p.sql", @"
            CREATE PROCEDURE dbo.P AS
            BEGIN
                INSERT INTO dbo.Log (msg) VALUES ('a');
                INSERT INTO dbo.Log (msg) VALUES ('b');
            END");

        var writes = Writes(r).Where(e => e.To == "dbo.log").ToList();
        Assert.Single(writes);
    }

    [Fact]
    public void Read_and_write_of_same_table_are_not_deduped_across_relations()
    {
        // reads_table and writes_table are different relations; both edges must appear.
        var r = TSqlPass.Run("p.sql", @"
            CREATE PROCEDURE dbo.P AS
            BEGIN
                INSERT INTO dbo.T1 SELECT * FROM dbo.T1;
            END");

        Assert.Single(Reads(r), e => e.To == "dbo.t1");
        Assert.Single(Writes(r), e => e.To == "dbo.t1");
    }

    // -----------------------------------------------------------------------
    // 8. EdgeKindTag correctness
    // -----------------------------------------------------------------------

    [Fact]
    public void EdgeKindTag_for_reads_table_is_custom_reads_table()
    {
        var r = TSqlPass.Run("p.sql", "SELECT * FROM dbo.T1");

        var edge = Assert.Single(Reads(r), e => e.To == "dbo.t1");
        Assert.Equal("custom:reads_table", edge.EdgeKindTag);
    }

    [Fact]
    public void EdgeKindTag_for_writes_table_is_custom_writes_table()
    {
        var r = TSqlPass.Run("p.sql", "INSERT INTO dbo.T1 (id) VALUES (1)");

        var edge = Assert.Single(Writes(r), e => e.To == "dbo.t1");
        Assert.Equal("custom:writes_table", edge.EdgeKindTag);
    }

    [Fact]
    public void ToKind_is_always_table_for_table_edges()
    {
        var r = TSqlPass.Run("p.sql",
            "SELECT * FROM dbo.T1; INSERT INTO dbo.T2 (id) VALUES (1)");

        Assert.All(r.Edges.Where(e => e.Relation is "reads_table" or "writes_table"),
            e => Assert.Equal("table", e.ToKind));
    }

    // -----------------------------------------------------------------------
    // 9. Database context from filename (opt-in: --db-from-filename)
    // -----------------------------------------------------------------------

    [Fact]
    public void Filename_db_context_adds_db_prefix_to_canonical_name()
    {
        // With db-from-filename on, "SALES_LIVE.sql" → db context "sales_live";
        // dbo.T1 → "sales_live.dbo.t1".
        var r = TSqlPass.Run("SALES_LIVE.sql", "SELECT * FROM dbo.T1", dbFromFileName: true);

        HasRead(r, "sales_live.dbo.t1");
    }

    [Fact]
    public void Filename_db_context_keeps_distinct_databases_distinct()
    {
        var rLive = TSqlPass.Run("SALES_LIVE.sql", "SELECT * FROM dbo.T1", dbFromFileName: true);
        var rArchive = TSqlPass.Run("SALES_ARCHIVE.sql", "SELECT * FROM dbo.T1", dbFromFileName: true);

        var liveEdge = Assert.Single(Reads(rLive), e => e.Relation == "reads_table");
        var archiveEdge = Assert.Single(Reads(rArchive), e => e.Relation == "reads_table");

        Assert.NotEqual(liveEdge.To, archiveEdge.To);
        Assert.Equal("sales_live.dbo.t1", liveEdge.To);
        Assert.Equal("sales_archive.dbo.t1", archiveEdge.To);
    }

    [Fact]
    public void Filename_db_context_off_by_default_adds_no_db_context()
    {
        // Default (flag off): the file name is never treated as a database.
        var r = TSqlPass.Run("SALES_LIVE.sql", "SELECT * FROM dbo.T1");

        HasRead(r, "dbo.t1");
        NoRead(r, "sales_live.dbo.t1");
    }

    // -----------------------------------------------------------------------
    // 10. USE statement overrides db context
    // -----------------------------------------------------------------------

    [Fact]
    public void Use_statement_sets_db_context_for_subsequent_statements()
    {
        var r = TSqlPass.Run("p.sql", "USE MyDb; SELECT * FROM dbo.T1");

        HasRead(r, "mydb.dbo.t1");
    }

    [Fact]
    public void Use_statement_db_name_is_lowercased()
    {
        var r = TSqlPass.Run("p.sql", "USE MYDB; SELECT * FROM dbo.T1");

        HasRead(r, "mydb.dbo.t1");
        NoRead(r, "MYDB.dbo.t1");
    }

    // -----------------------------------------------------------------------
    // 11. Exclusions — temp tables, CTEs, sys.*, pseudo-tables, table vars
    // -----------------------------------------------------------------------

    [Fact]
    public void Temp_tables_are_not_emitted()
    {
        var r = TSqlPass.Run("p.sql",
            "SELECT * FROM #TempResults");

        Assert.DoesNotContain(r.Edges, e => e.To.Contains("#"));
        Assert.Empty(Reads(r));
    }

    [Fact]
    public void Sys_schema_tables_are_excluded()
    {
        var r = TSqlPass.Run("p.sql", "SELECT * FROM sys.objects");

        Assert.DoesNotContain(r.Edges, e => e.Relation == "reads_table");
    }

    [Fact]
    public void Information_schema_tables_are_excluded()
    {
        var r = TSqlPass.Run("p.sql", "SELECT * FROM information_schema.columns");

        Assert.DoesNotContain(r.Edges, e => e.Relation == "reads_table");
    }

    [Fact]
    public void CTE_names_are_not_emitted_as_table_reads()
    {
        // "cte" is a CTE name; only dbo.Orders should appear as a read.
        var r = TSqlPass.Run("p.sql", @"
            WITH cte AS (SELECT * FROM dbo.Orders)
            SELECT * FROM cte");

        HasRead(r, "dbo.orders");
        Assert.DoesNotContain(Reads(r), e => e.To == "dbo.cte" || e.To == "cte");
    }

    // -----------------------------------------------------------------------
    // 12. Units count and HadParseErrors
    // -----------------------------------------------------------------------

    [Fact]
    public void Units_counts_stored_procedures()
    {
        var r = TSqlPass.Run("p.sql",
            "CREATE PROCEDURE dbo.P1 AS SELECT 1;\nGO\nCREATE PROCEDURE dbo.P2 AS SELECT 2;");

        Assert.Equal(2, r.Units);
    }

    [Fact]
    public void HadParseErrors_false_for_valid_sql()
    {
        var r = TSqlPass.Run("p.sql", "SELECT * FROM dbo.T1");

        Assert.False(r.HadParseErrors);
    }

    [Fact]
    public void HadParseErrors_true_for_invalid_sql()
    {
        var r = TSqlPass.Run("p.sql", "THIS IS NOT VALID SQL @@@@");

        Assert.True(r.HadParseErrors);
    }

    // -----------------------------------------------------------------------
    // 13. File field on edges
    // -----------------------------------------------------------------------

    [Fact]
    public void Edge_File_field_matches_the_file_argument()
    {
        var r = TSqlPass.Run("my_proc.sql", "SELECT * FROM dbo.T1");

        var edge = Assert.Single(Reads(r), e => e.To == "dbo.t1");
        Assert.Equal("my_proc.sql", edge.File);
    }

    // -----------------------------------------------------------------------
    // 14. Combined scenario — proc with multiple DML types
    // -----------------------------------------------------------------------

    [Fact]
    public void Proc_with_select_insert_update_delete_emits_correct_edges()
    {
        const string sql = @"
            CREATE PROCEDURE dbo.usp_Reconcile AS
            BEGIN
                SELECT * FROM dbo.Source;
                INSERT INTO dbo.Target SELECT id FROM dbo.Source;
                UPDATE t SET t.processed = 1 FROM dbo.Target AS t WHERE t.id > 0;
                DELETE FROM dbo.Logs WHERE created < GETDATE();
            END";

        var r = TSqlPass.Run("p.sql", sql);

        // All reads attributed to the proc.
        var reads = Reads(r);
        Assert.All(reads, e => Assert.Equal("dbo.usp_Reconcile", e.From));

        // reads_table: Source appears (twice in SQL but deduped to one edge).
        var sourceReads = reads.Where(e => e.To == "dbo.source").ToList();
        Assert.Single(sourceReads);

        // writes_table: Target, Logs.
        HasWrite(r, "dbo.target");
        HasWrite(r, "dbo.logs");

        // Target also appears as write (from UPDATE resolved alias) — already covered.
        var writes = Writes(r);
        Assert.All(writes, e => Assert.Equal("dbo.usp_Reconcile", e.From));
    }
}
