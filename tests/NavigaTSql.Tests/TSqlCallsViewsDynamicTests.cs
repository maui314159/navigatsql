using Xunit;

// Focused suite covering calls_proc, calls_function, dynamic_sql, view/function
// scopes, Units counting, and EdgeKindTag — derived from TSqlPass.cs source.
public class TSqlCallsViewsDynamicTests
{
    // -----------------------------------------------------------------------
    // helpers
    // -----------------------------------------------------------------------

    private static PassResult Proc(string body, string procName = "dbo.usp_Parent") =>
        TSqlPass.Run("test.sql",
            $"CREATE PROCEDURE {procName} AS BEGIN {body} END");

    private static PassResult View(string selectBody, string viewName = "dbo.MyView") =>
        TSqlPass.Run("test.sql",
            $"CREATE VIEW {viewName} AS {selectBody}");

    private static PassResult Function(string body, string funcName = "dbo.fn_Calc") =>
        TSqlPass.Run("test.sql",
            $"CREATE FUNCTION {funcName}(@x INT) RETURNS INT AS BEGIN {body} END");

    // -----------------------------------------------------------------------
    // 1. calls_proc — EXEC dbo.usp_Child inside a proc
    // -----------------------------------------------------------------------

    [Fact]
    public void CallsProc_exec_named_proc_emits_calls_proc_edge()
    {
        var r = Proc("EXEC dbo.usp_Child;");
        Assert.Contains(r.Edges, e =>
            e.Relation == "calls_proc" &&
            e.To == "dbo.usp_Child" &&
            e.ToKind == "proc");
    }

    [Fact]
    public void CallsProc_From_is_enclosing_proc_name_with_original_case()
    {
        var r = TSqlPass.Run("test.sql",
            "CREATE PROCEDURE dbo.usp_Parent AS BEGIN EXEC dbo.usp_Child; END");
        var edge = Assert.Single(r.Edges, e => e.Relation == "calls_proc");
        Assert.Equal("dbo.usp_Parent", edge.From);
        Assert.Equal("proc", edge.FromKind);
    }

    [Fact]
    public void CallsProc_To_preserves_original_case()
    {
        // Proc names go through Naming.Of which does NOT lowercase.
        var r = Proc("EXEC dbo.Usp_MixedCase;");
        var edge = Assert.Single(r.Edges, e => e.Relation == "calls_proc");
        Assert.Equal("dbo.Usp_MixedCase", edge.To);
    }

    [Fact]
    public void CallsProc_EdgeKindTag_is_custom_calls_proc()
    {
        var r = Proc("EXEC dbo.usp_Child;");
        var edge = Assert.Single(r.Edges, e => e.Relation == "calls_proc");
        Assert.Equal("custom:calls_proc", edge.EdgeKindTag);
    }

    [Fact]
    public void CallsProc_multiple_distinct_procs_produce_separate_edges()
    {
        var r = Proc("EXEC dbo.usp_A; EXEC dbo.usp_B;");
        Assert.Contains(r.Edges, e => e.Relation == "calls_proc" && e.To == "dbo.usp_A");
        Assert.Contains(r.Edges, e => e.Relation == "calls_proc" && e.To == "dbo.usp_B");
    }

    [Fact]
    public void CallsProc_duplicate_exec_is_deduped_to_single_edge()
    {
        var r = Proc("EXEC dbo.usp_A; EXEC dbo.usp_A;");
        Assert.Equal(1, r.Edges.Count(e => e.Relation == "calls_proc" && e.To == "dbo.usp_A"));
    }

    // -----------------------------------------------------------------------
    // 2. calls_function — scalar UDF, built-ins excluded
    // -----------------------------------------------------------------------

    [Fact]
    public void CallsFunction_schema_qualified_udf_emits_calls_function()
    {
        var r = Proc("DECLARE @v INT = dbo.fn_Calc(1);");
        Assert.Contains(r.Edges, e =>
            e.Relation == "calls_function" &&
            e.To == "dbo.fn_Calc" &&
            e.ToKind == "function");
    }

    [Fact]
    public void CallsFunction_builtin_without_schema_does_NOT_emit_edge()
    {
        // GETDATE() has no MultiPartIdentifierCallTarget — no schema prefix.
        var r = Proc("DECLARE @d DATETIME = GETDATE();");
        Assert.DoesNotContain(r.Edges, e => e.Relation == "calls_function");
    }

    [Fact]
    public void CallsFunction_ISNULL_builtin_does_NOT_emit_edge()
    {
        var r = Proc("DECLARE @v INT = ISNULL(NULL, 0);");
        Assert.DoesNotContain(r.Edges, e => e.Relation == "calls_function");
    }

    [Fact]
    public void CallsFunction_EdgeKindTag_is_custom_calls_function()
    {
        var r = Proc("DECLARE @v INT = dbo.fn_X(1);");
        var edge = Assert.Single(r.Edges, e => e.Relation == "calls_function");
        Assert.Equal("custom:calls_function", edge.EdgeKindTag);
    }

    [Fact]
    public void CallsFunction_To_preserves_original_case()
    {
        var r = Proc("DECLARE @v INT = dbo.fn_MyUdf(@x);");
        var edge = Assert.Single(r.Edges, e => e.Relation == "calls_function");
        Assert.Equal("dbo.fn_MyUdf", edge.To);
    }

    // -----------------------------------------------------------------------
    // 3. Table-valued function in FROM -> calls_function, NOT reads_table
    // -----------------------------------------------------------------------

    [Fact]
    public void TVF_in_FROM_emits_calls_function()
    {
        // SchemaObjectFunctionTableReference -> calls_function
        var r = Proc("SELECT * FROM dbo.tvf_Get(1) t;");
        Assert.Contains(r.Edges, e =>
            e.Relation == "calls_function" &&
            e.To == "dbo.tvf_Get" &&
            e.ToKind == "function");
    }

    [Fact]
    public void TVF_in_FROM_does_NOT_emit_reads_table_for_the_TVF()
    {
        // TableCollector only visits NamedTableReference, not function refs.
        var r = Proc("SELECT * FROM dbo.tvf_Get(1) t;");
        Assert.DoesNotContain(r.Edges, e =>
            e.Relation == "reads_table" && e.To.Contains("tvf_get"));
    }

    [Fact]
    public void TVF_in_FROM_EdgeKindTag_is_custom_calls_function()
    {
        var r = Proc("SELECT * FROM dbo.tvf_Items(42) t;");
        var edge = Assert.Single(r.Edges, e => e.Relation == "calls_function");
        Assert.Equal("custom:calls_function", edge.EdgeKindTag);
    }

    [Fact]
    public void TVF_in_FROM_To_preserves_original_case()
    {
        var r = Proc("SELECT * FROM dbo.tvf_GetItems(@id) AS rows;");
        var edge = Assert.Single(r.Edges, e => e.Relation == "calls_function");
        Assert.Equal("dbo.tvf_GetItems", edge.To);
    }

    // -----------------------------------------------------------------------
    // 4. dynamic_sql — EXEC sp_executesql @sql
    // -----------------------------------------------------------------------

    [Fact]
    public void DynamicSql_sp_executesql_emits_dynamic_sql_edge()
    {
        var r = Proc("DECLARE @s NVARCHAR(MAX) = N'SELECT 1'; EXEC sp_executesql @s;");
        Assert.Contains(r.Edges, e =>
            e.Relation == "dynamic_sql" &&
            e.To == "<dynamic>" &&
            e.ToKind == "unresolved");
    }

    [Fact]
    public void DynamicSql_sp_executesql_increments_DynamicFlags()
    {
        var r = Proc("DECLARE @s NVARCHAR(MAX) = N'SELECT 1'; EXEC sp_executesql @s;");
        Assert.Equal(1, r.DynamicFlags);
    }

    [Fact]
    public void DynamicSql_sp_executesql_EdgeKindTag_is_custom_dynamic_sql()
    {
        var r = Proc("EXEC sp_executesql @s;");
        var edge = Assert.Single(r.Edges, e => e.Relation == "dynamic_sql");
        Assert.Equal("custom:dynamic_sql", edge.EdgeKindTag);
    }

    [Fact]
    public void DynamicSql_sp_executesql_does_NOT_emit_calls_proc()
    {
        var r = Proc("EXEC sp_executesql @s;");
        Assert.DoesNotContain(r.Edges, e => e.Relation == "calls_proc");
    }

    // -----------------------------------------------------------------------
    // 5. dynamic_sql — EXEC(@sql) string-list form
    // -----------------------------------------------------------------------

    [Fact]
    public void DynamicSql_exec_string_list_emits_dynamic_sql_edge()
    {
        var r = Proc("DECLARE @q NVARCHAR(200) = N'SELECT 1'; EXEC(@q);");
        Assert.Contains(r.Edges, e =>
            e.Relation == "dynamic_sql" &&
            e.To == "<dynamic>" &&
            e.ToKind == "unresolved");
    }

    [Fact]
    public void DynamicSql_exec_string_list_increments_DynamicFlags()
    {
        var r = Proc("DECLARE @q NVARCHAR(200) = N'SELECT 1'; EXEC(@q);");
        Assert.Equal(1, r.DynamicFlags);
    }

    [Fact]
    public void DynamicSql_two_dynamic_execs_DynamicFlags_equals_two()
    {
        // Two separate EXEC(@var) calls, but dedup on (from, to, relation) means
        // only one edge; DynamicFlags still increments per call.
        var r = Proc(
            "DECLARE @a NVARCHAR(100) = N'x'; " +
            "DECLARE @b NVARCHAR(100) = N'y'; " +
            "EXEC(@a); EXEC(@b);");
        Assert.Equal(2, r.DynamicFlags);
    }

    [Fact]
    public void DynamicSql_exec_paren_does_NOT_emit_calls_proc()
    {
        var r = Proc("DECLARE @q NVARCHAR(200) = N'SELECT 1'; EXEC(@q);");
        Assert.DoesNotContain(r.Edges, e => e.Relation == "calls_proc");
    }

    // -----------------------------------------------------------------------
    // 6. CREATE VIEW — reads_table edge with From=view-name, FromKind="view"
    // -----------------------------------------------------------------------

    [Fact]
    public void CreateView_reads_table_edge_From_is_view_name()
    {
        var r = View("SELECT id FROM dbo.tbl_Orders");
        var edge = Assert.Single(r.Edges, e => e.Relation == "reads_table");
        Assert.Equal("dbo.MyView", edge.From);
    }

    [Fact]
    public void CreateView_reads_table_edge_FromKind_is_view()
    {
        var r = View("SELECT id FROM dbo.tbl_Orders");
        var edge = Assert.Single(r.Edges, e => e.Relation == "reads_table");
        Assert.Equal("view", edge.FromKind);
    }

    [Fact]
    public void CreateView_reads_table_edge_To_is_canonicalized_table()
    {
        var r = View("SELECT id FROM dbo.tbl_Orders");
        var edge = Assert.Single(r.Edges, e => e.Relation == "reads_table");
        Assert.Equal("dbo.tbl_orders", edge.To);
    }

    [Fact]
    public void CreateView_reads_table_edge_ToKind_is_table()
    {
        var r = View("SELECT id FROM dbo.tbl_Orders");
        var edge = Assert.Single(r.Edges, e => e.Relation == "reads_table");
        Assert.Equal("table", edge.ToKind);
    }

    [Fact]
    public void CreateView_From_preserves_original_case_of_view_name()
    {
        // Naming.Of preserves case; schema-qualified view name keeps casing.
        var r = TSqlPass.Run("test.sql",
            "CREATE VIEW dbo.MyView AS SELECT id FROM dbo.t");
        var edge = Assert.Single(r.Edges, e => e.Relation == "reads_table");
        Assert.Equal("dbo.MyView", edge.From);
    }

    [Fact]
    public void CreateView_EdgeKindTag_is_custom_reads_table()
    {
        var r = View("SELECT id FROM dbo.tbl_Orders");
        var edge = Assert.Single(r.Edges, e => e.Relation == "reads_table");
        Assert.Equal("custom:reads_table", edge.EdgeKindTag);
    }

    [Fact]
    public void CreateView_multi_table_join_emits_reads_table_for_each()
    {
        var r = View("SELECT a.id FROM dbo.tbl_A a JOIN dbo.tbl_B b ON a.id = b.id");
        Assert.Contains(r.Edges, e => e.Relation == "reads_table" && e.To == "dbo.tbl_a");
        Assert.Contains(r.Edges, e => e.Relation == "reads_table" && e.To == "dbo.tbl_b");
    }

    // -----------------------------------------------------------------------
    // 7. CREATE FUNCTION — FromKind="function" on body edges
    // -----------------------------------------------------------------------

    [Fact]
    public void CreateFunction_reads_table_edge_FromKind_is_function()
    {
        var r = TSqlPass.Run("test.sql",
            "CREATE FUNCTION dbo.fn_Get(@id INT) RETURNS TABLE AS " +
            "RETURN SELECT * FROM dbo.tbl_Data WHERE id = @id;");
        var edge = Assert.Single(r.Edges, e => e.Relation == "reads_table");
        Assert.Equal("function", edge.FromKind);
    }

    [Fact]
    public void CreateFunction_reads_table_edge_From_is_function_name()
    {
        var r = TSqlPass.Run("test.sql",
            "CREATE FUNCTION dbo.fn_Get(@id INT) RETURNS TABLE AS " +
            "RETURN SELECT * FROM dbo.tbl_Data WHERE id = @id;");
        var edge = Assert.Single(r.Edges, e => e.Relation == "reads_table");
        Assert.Equal("dbo.fn_Get", edge.From);
    }

    [Fact]
    public void CreateFunction_scalar_body_reads_table_FromKind_is_function()
    {
        // Multi-statement scalar function reading a table.
        var r = TSqlPass.Run("test.sql",
            "CREATE FUNCTION dbo.fn_Scalar(@id INT) RETURNS INT AS " +
            "BEGIN DECLARE @v INT; SELECT @v = val FROM dbo.tbl_Config WHERE id = @id; RETURN @v; END");
        Assert.Contains(r.Edges, e => e.Relation == "reads_table" && e.FromKind == "function");
    }

    // -----------------------------------------------------------------------
    // 8. Units = count of proc + function + view definitions
    // -----------------------------------------------------------------------

    [Fact]
    public void Units_single_proc_equals_one()
    {
        var r = Proc("SELECT 1;");
        Assert.Equal(1, r.Units);
    }

    [Fact]
    public void Units_single_view_equals_one()
    {
        var r = View("SELECT 1 AS x");
        Assert.Equal(1, r.Units);
    }

    [Fact]
    public void Units_single_function_equals_one()
    {
        var r = Function("RETURN @x;");
        Assert.Equal(1, r.Units);
    }

    [Fact]
    public void Units_one_proc_and_one_view_equals_two()
    {
        var sql =
            "CREATE PROCEDURE dbo.usp_P AS BEGIN SELECT * FROM dbo.t; END " +
            "\nGO\n" +
            "CREATE VIEW dbo.vw_V AS SELECT id FROM dbo.t";
        var r = TSqlPass.Run("test.sql", sql);
        Assert.Equal(2, r.Units);
    }

    [Fact]
    public void Units_proc_plus_function_plus_view_equals_three()
    {
        var sql =
            "CREATE PROCEDURE dbo.usp_P AS BEGIN SELECT 1; END " +
            "\nGO\n" +
            "CREATE FUNCTION dbo.fn_F(@x INT) RETURNS INT AS BEGIN RETURN @x; END " +
            "\nGO\n" +
            "CREATE VIEW dbo.vw_V AS SELECT 1 AS c";
        var r = TSqlPass.Run("test.sql", sql);
        Assert.Equal(3, r.Units);
    }

    [Fact]
    public void Units_alter_proc_counts_same_as_create_proc()
    {
        var r = TSqlPass.Run("test.sql",
            "ALTER PROCEDURE dbo.usp_P AS BEGIN SELECT 1; END");
        Assert.Equal(1, r.Units);
    }

    [Fact]
    public void Units_no_definitions_equals_zero()
    {
        var r = TSqlPass.Run("test.sql", "SELECT * FROM dbo.t;");
        Assert.Equal(0, r.Units);
    }

    // -----------------------------------------------------------------------
    // 9. EdgeKindTag general contract
    // -----------------------------------------------------------------------

    [Fact]
    public void EdgeKindTag_is_always_custom_colon_relation()
    {
        var sql =
            "CREATE PROCEDURE dbo.usp_P AS BEGIN " +
            "SELECT * FROM dbo.tbl_R; " +
            "INSERT INTO dbo.tbl_W SELECT id FROM dbo.tbl_R; " +
            "EXEC dbo.usp_Child; " +
            "EXEC sp_executesql @s; " +
            "END";
        var r = TSqlPass.Run("test.sql", sql);
        foreach (var edge in r.Edges)
            Assert.Equal($"custom:{edge.Relation}", edge.EdgeKindTag);
    }

    // -----------------------------------------------------------------------
    // 10. HadParseErrors flag
    // -----------------------------------------------------------------------

    [Fact]
    public void HadParseErrors_false_for_valid_sql()
    {
        var r = Proc("SELECT 1;");
        Assert.False(r.HadParseErrors);
    }

    [Fact]
    public void HadParseErrors_true_for_invalid_sql()
    {
        var r = TSqlPass.Run("bad.sql", "THIS IS NOT VALID SQL @@@@");
        Assert.True(r.HadParseErrors);
    }

    // -----------------------------------------------------------------------
    // 11. LinkedServer — null on non-linked-server edges
    // -----------------------------------------------------------------------

    [Fact]
    public void LinkedServer_is_null_for_local_table_reads()
    {
        var r = View("SELECT id FROM dbo.tbl_Local");
        var edge = Assert.Single(r.Edges, e => e.Relation == "reads_table");
        Assert.Null(edge.LinkedServer);
    }

    [Fact]
    public void LinkedServer_is_null_for_calls_proc()
    {
        var r = Proc("EXEC dbo.usp_Child;");
        var edge = Assert.Single(r.Edges, e => e.Relation == "calls_proc");
        Assert.Null(edge.LinkedServer);
    }

    // -----------------------------------------------------------------------
    // 12. File field propagated to edges
    // -----------------------------------------------------------------------

    [Fact]
    public void Edge_File_matches_file_argument_passed_to_Run()
    {
        var r = TSqlPass.Run("MySchema.sql",
            "CREATE PROCEDURE dbo.usp_P AS BEGIN SELECT * FROM dbo.t; END");
        Assert.All(r.Edges, e => Assert.Equal("MySchema.sql", e.File));
    }

    // -----------------------------------------------------------------------
    // 13. Scope transitions — two procs in one file attribute to correct scope
    // -----------------------------------------------------------------------

    [Fact]
    public void Scope_edges_attributed_to_correct_proc_after_scope_change()
    {
        var sql =
            "CREATE PROCEDURE dbo.usp_First AS BEGIN SELECT * FROM dbo.tbl_A; END " +
            "\nGO\n" +
            "CREATE PROCEDURE dbo.usp_Second AS BEGIN SELECT * FROM dbo.tbl_B; END";
        var r = TSqlPass.Run("test.sql", sql);
        Assert.Contains(r.Edges, e => e.From == "dbo.usp_First" && e.To == "dbo.tbl_a");
        Assert.Contains(r.Edges, e => e.From == "dbo.usp_Second" && e.To == "dbo.tbl_b");
    }

    // -----------------------------------------------------------------------
    // 14. calls_proc — sp_executesql is NOT emitted as calls_proc
    // -----------------------------------------------------------------------

    [Fact]
    public void CallsProc_sp_executesql_is_not_treated_as_a_regular_proc_call()
    {
        var r = Proc("EXEC sp_executesql @s;");
        Assert.DoesNotContain(r.Edges, e => e.Relation == "calls_proc" && e.To == "sp_executesql");
    }

    // -----------------------------------------------------------------------
    // 15. calls_function — From and FromKind match enclosing scope
    // -----------------------------------------------------------------------

    [Fact]
    public void CallsFunction_From_is_enclosing_proc_name()
    {
        var r = TSqlPass.Run("test.sql",
            "CREATE PROCEDURE dbo.usp_Caller AS BEGIN DECLARE @v INT = dbo.fn_Get(1); END");
        var edge = Assert.Single(r.Edges, e => e.Relation == "calls_function");
        Assert.Equal("dbo.usp_Caller", edge.From);
        Assert.Equal("proc", edge.FromKind);
    }

    [Fact]
    public void CallsFunction_From_is_enclosing_view_name()
    {
        // A view that references a scalar UDF in its SELECT list.
        var r = TSqlPass.Run("test.sql",
            "CREATE VIEW dbo.vw_Computed AS SELECT dbo.fn_Format(id) AS label FROM dbo.tbl_X");
        Assert.Contains(r.Edges, e =>
            e.Relation == "calls_function" &&
            e.From == "dbo.vw_Computed" &&
            e.FromKind == "view");
    }
}
