using Xunit;
using Microsoft.CodeAnalysis.CSharp;

// Verifies the test project is wired to the extractor's internal API across all
// three passes. The swarm's focused suites build on these patterns.
public class SmokeTests
{
    [Fact]
    public void TSqlPass_extracts_reads_and_writes()
    {
        var r = TSqlPass.Run("p.sql",
            "CREATE PROCEDURE dbo.p AS BEGIN SELECT * FROM dbo.t1; INSERT INTO dbo.t2 SELECT * FROM dbo.t1; END");
        Assert.Contains(r.Edges, e => e.Relation == "reads_table" && e.To == "dbo.t1");
        Assert.Contains(r.Edges, e => e.Relation == "writes_table" && e.To == "dbo.t2");
    }

    [Fact]
    public void CSharpPass_extracts_embedded_sql_method_to_table()
    {
        var root = CSharpSyntaxTree.ParseText(
            "class Repo { void M(){ var x = conn.Query(\"SELECT a FROM dbo.Orders WHERE id=@id\"); } }").GetRoot();
        var r = CSharpPass.Run("Repo.cs", root);
        Assert.Contains(r.Edges, e => e.FromKind == "csharp_method" && e.Relation == "reads_table" && e.To == "dbo.orders");
    }

    [Fact]
    public void TSqlPass_ExtractTableRefs_parses_a_snippet()
    {
        var refs = TSqlPass.ExtractTableRefs("SELECT * FROM dbo.Customers");
        Assert.Contains(refs, t => t.relation == "reads_table" && t.table == "dbo.customers");
    }
}
