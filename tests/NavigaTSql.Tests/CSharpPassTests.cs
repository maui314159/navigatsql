using Xunit;
using Microsoft.CodeAnalysis.CSharp;

// Focused tests for CSharpPass (proc-name bridge and embedded SQL) and
// TSqlPass.ExtractTableRefs (snippet parsing used by the C# bridge).
public class CSharpPassTests
{
    // ------------------------------------------------------------------ helpers

    private static PassResult Run(string code) =>
        CSharpPass.Run("Test.cs", CSharpSyntaxTree.ParseText(code).GetRoot());

    private static Edge? FindEdge(PassResult r, string relation, string to) =>
        r.Edges.FirstOrDefault(e => e.Relation == relation && e.To == to);

    // ================================================================== 1.
    // Method with a SqlCommand proc-name literal -> calls_proc edge.
    [Fact]
    public void Method_with_proc_name_literal_emits_calls_proc_edge()
    {
        const string code = @"
class Repo {
    void GetData() {
        var cmd = new SqlCommand(""dbo.usp_GetX"");
    }
}";
        var r = Run(code);

        var edge = FindEdge(r, "calls_proc", "dbo.usp_GetX");
        Assert.NotNull(edge);
        Assert.Equal("csharp_method", edge!.FromKind);
        Assert.Equal("Repo.GetData", edge.From);
        Assert.Equal("proc", edge.ToKind);
    }

    // ================================================================== 2.
    // Bracketed proc name: "[dbo].[usp_GetX]" -> To="dbo.usp_GetX" (brackets stripped).
    [Fact]
    public void Bracketed_proc_name_is_stripped_of_brackets()
    {
        const string code = @"
class Repo {
    void GetData() {
        var cmd = new SqlCommand(""[dbo].[usp_GetX]"");
    }
}";
        var r = Run(code);

        var edge = FindEdge(r, "calls_proc", "dbo.usp_GetX");
        Assert.NotNull(edge);
        Assert.Equal("Repo.GetData", edge!.From);
        // Brackets must not appear in the To value.
        Assert.DoesNotContain("[", edge.To);
        Assert.DoesNotContain("]", edge.To);
    }

    // ================================================================== 3.
    // Embedded SELECT statement -> reads_table edge, table lowercased.
    [Fact]
    public void Method_with_embedded_select_emits_reads_table_edge()
    {
        const string code = @"
class Repo {
    void LoadOrders() {
        var rows = conn.Query(""SELECT Id FROM dbo.Orders"");
    }
}";
        var r = Run(code);

        var edge = FindEdge(r, "reads_table", "dbo.orders");
        Assert.NotNull(edge);
        Assert.Equal("csharp_method", edge!.FromKind);
        Assert.Equal("Repo.LoadOrders", edge.From);
        Assert.Equal("table", edge.ToKind);
    }

    // ================================================================== 4.
    // Embedded INSERT statement -> writes_table edge.
    [Fact]
    public void Method_with_embedded_insert_emits_writes_table_edge()
    {
        const string code = @"
class Repo {
    void LogEvent() {
        conn.Execute(""INSERT INTO dbo.Logs(Msg, Ts) VALUES(@m, @t)"");
    }
}";
        var r = Run(code);

        var edge = FindEdge(r, "writes_table", "dbo.logs");
        Assert.NotNull(edge);
        Assert.Equal("csharp_method", edge!.FromKind);
        Assert.Equal("Repo.LogEvent", edge.From);
    }

    // ================================================================== 5.
    // Embedded UPDATE statement -> writes_table edge.
    [Fact]
    public void Method_with_embedded_update_emits_writes_table_edge()
    {
        const string code = @"
class Repo {
    void UpdateAccount() {
        conn.Execute(""UPDATE dbo.Acct SET x=1 WHERE id=@i"");
    }
}";
        var r = Run(code);

        var edge = FindEdge(r, "writes_table", "dbo.acct");
        Assert.NotNull(edge);
        Assert.Equal("Repo.UpdateAccount", edge!.From);
    }

    // ================================================================== 6a.
    // Enclosing member attribution: From = "Class.Method".
    [Fact]
    public void Enclosing_method_name_is_qualified_with_class_name()
    {
        const string code = @"
class CustomerService {
    void FetchCustomer() {
        var cmd = new SqlCommand(""dbo.usp_FetchCustomer"");
    }
}";
        var r = Run(code);

        var edge = FindEdge(r, "calls_proc", "dbo.usp_FetchCustomer");
        Assert.NotNull(edge);
        Assert.Equal("CustomerService.FetchCustomer", edge!.From);
    }

    // ================================================================== 6b.
    // Constructor literal -> From = "Class.ctor".
    [Fact]
    public void Constructor_literal_attributed_as_ctor()
    {
        const string code = @"
class Repo {
    public Repo() {
        var cmd = new SqlCommand(""usp_Init"");
    }
}";
        var r = Run(code);

        var edge = FindEdge(r, "calls_proc", "usp_Init");
        Assert.NotNull(edge);
        Assert.Equal("Repo.ctor", edge!.From);
    }

    // ================================================================== 7.
    // A plain non-SQL, non-proc string produces NO edge.
    [Fact]
    public void Plain_string_literal_produces_no_edge()
    {
        const string code = @"
class Helper {
    void Greet() {
        var s = ""hello world"";
    }
}";
        var r = Run(code);

        Assert.Empty(r.Edges);
    }

    // ================================================================== 7b.
    // Confirm proc-shaped and SQL-shaped literals are mutually exclusive:
    // a proc-name literal must NOT also generate a reads_table / writes_table edge.
    [Fact]
    public void Proc_shaped_literal_goes_calls_proc_route_not_sql_route()
    {
        const string code = @"
class Repo {
    void Go() {
        var cmd = new SqlCommand(""dbo.usp_DoWork"");
    }
}";
        var r = Run(code);

        // Exactly one edge and it must be calls_proc.
        Assert.Single(r.Edges);
        Assert.Equal("calls_proc", r.Edges[0].Relation);
    }

    // ================================================================== 8.
    // TSqlPass.ExtractTableRefs with a JOIN: both tables returned as reads_table.
    [Fact]
    public void ExtractTableRefs_returns_both_tables_in_a_join()
    {
        const string sql = "SELECT x.Id, y.Name FROM dbo.X AS x JOIN dbo.Y AS y ON x.Id = y.Id";
        var refs = TSqlPass.ExtractTableRefs(sql);

        Assert.Contains(refs, t => t.relation == "reads_table" && t.table == "dbo.x");
        Assert.Contains(refs, t => t.relation == "reads_table" && t.table == "dbo.y");
    }

    // ================================================================== extra guards

    // sp_ prefix is also proc-shaped.
    [Fact]
    public void Sp_prefix_literal_is_proc_shaped()
    {
        const string code = @"
class Svc {
    void Exec() {
        var cmd = new SqlCommand(""sp_GetConfig"");
    }
}";
        var r = Run(code);
        Assert.Contains(r.Edges, e => e.Relation == "calls_proc" && e.To == "sp_GetConfig");
    }

    // qry_ prefix is proc-shaped.
    [Fact]
    public void Qry_prefix_literal_is_proc_shaped()
    {
        const string code = @"
class Svc {
    void Exec() {
        conn.Execute(""qry_LoadReport"");
    }
}";
        var r = Run(code);
        Assert.Contains(r.Edges, e => e.Relation == "calls_proc" && e.To == "qry_LoadReport");
    }

    // p_ prefix is proc-shaped.
    [Fact]
    public void P_prefix_literal_is_proc_shaped()
    {
        const string code = @"
class Svc {
    void Exec() {
        conn.Execute(""p_SaveRecord"");
    }
}";
        var r = Run(code);
        Assert.Contains(r.Edges, e => e.Relation == "calls_proc" && e.To == "p_SaveRecord");
    }

    // Property body: enclosing member is the property name, not "ctor" or "<member>".
    [Fact]
    public void Property_body_attributed_as_property_name()
    {
        const string code = @"
class Repo {
    string ConnectionName => ""usp_GetConn"";
}";
        var r = Run(code);
        var edge = FindEdge(r, "calls_proc", "usp_GetConn");
        Assert.NotNull(edge);
        Assert.Equal("Repo.ConnectionName", edge!.From);
    }

    // Duplicate proc-name literal in the same method -> only ONE edge (deduplication).
    [Fact]
    public void Duplicate_proc_literal_in_same_method_produces_one_edge()
    {
        const string code = @"
class Repo {
    void Go() {
        var a = new SqlCommand(""dbo.usp_Dup"");
        var b = new SqlCommand(""dbo.usp_Dup"");
    }
}";
        var r = Run(code);
        var edges = r.Edges.Where(e => e.Relation == "calls_proc" && e.To == "dbo.usp_Dup").ToList();
        Assert.Single(edges);
    }

    // Embedded DELETE statement -> writes_table edge.
    [Fact]
    public void Method_with_embedded_delete_emits_writes_table_edge()
    {
        const string code = @"
class Repo {
    void Purge() {
        conn.Execute(""DELETE FROM dbo.Staging WHERE dt < @cutoff"");
    }
}";
        var r = Run(code);
        Assert.Contains(r.Edges, e => e.Relation == "writes_table" && e.To == "dbo.staging");
    }

    // HadParseErrors is false for well-formed input.
    [Fact]
    public void PassResult_HadParseErrors_is_false_for_valid_csharp()
    {
        const string code = @"class C { void M() { } }";
        var r = Run(code);
        Assert.False(r.HadParseErrors);
    }

    // Units counts distinct C# members that produced at least one edge.
    [Fact]
    public void PassResult_Units_counts_emitting_members()
    {
        const string code = @"
class Repo {
    void A() { var cmd = new SqlCommand(""usp_Alpha""); }
    void B() { var cmd = new SqlCommand(""usp_Beta""); }
}";
        var r = Run(code);
        Assert.Equal(2, r.Units);
    }

    // Three-part bracketed name: "[mydb].[dbo].[usp_Foo]" -> "mydb.dbo.usp_Foo".
    [Fact]
    public void Three_part_bracketed_proc_name_is_stripped_correctly()
    {
        const string code = @"
class Repo {
    void Go() {
        var cmd = new SqlCommand(""[mydb].[dbo].[usp_Foo]"");
    }
}";
        var r = Run(code);
        var edge = FindEdge(r, "calls_proc", "mydb.dbo.usp_Foo");
        Assert.NotNull(edge);
        Assert.DoesNotContain("[", edge!.To);
        Assert.DoesNotContain("]", edge.To);
    }

    // ExtractTableRefs on a plain non-SQL string returns empty list.
    [Fact]
    public void ExtractTableRefs_on_non_sql_returns_empty()
    {
        var refs = TSqlPass.ExtractTableRefs("hello world");
        Assert.Empty(refs);
    }

    // ExtractTableRefs: INSERT returns writes_table, not reads_table.
    [Fact]
    public void ExtractTableRefs_insert_returns_writes_table()
    {
        var refs = TSqlPass.ExtractTableRefs("INSERT INTO dbo.AuditLog(Col) VALUES(1)");
        Assert.Contains(refs, t => t.relation == "writes_table" && t.table == "dbo.auditlog");
        Assert.DoesNotContain(refs, t => t.relation == "reads_table" && t.table == "dbo.auditlog");
    }
}
