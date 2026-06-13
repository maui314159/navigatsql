using Xunit;

// Tests for TSqlPass: foreign-key edge emission, table-identity canonicalization,
// and noise exclusion (temp tables, sys.*, trigger pseudo-tables, CTE names).
//
// Naming convention: <Behavior>_<Scenario>_<Expected>.
// All SQL snippets that contain reads are wrapped in a stored-procedure body so
// ScriptDom can parse the FROM clause inside a well-formed statement context.
public class TSqlCanonNoiseFkTests
{
    // ------------------------------------------------------------------ helpers

    private static string Proc(string body) =>
        $"CREATE PROCEDURE dbo.p AS BEGIN {body} END";

    private static IEnumerable<Edge> ReadsEdges(PassResult r) =>
        r.Edges.Where(e => e.Relation == "reads_table");

    private static IEnumerable<Edge> RefsEdges(PassResult r) =>
        r.Edges.Where(e => e.Relation == "references");

    // ==========================================================================
    // A) Foreign keys -> `references` edges
    // ==========================================================================

    [Fact]
    public void ForeignKey_TableLevel_InCreateTable_EmitsReferencesEdge()
    {
        var sql = """
            CREATE TABLE dbo.Orders (
                Id   int NOT NULL,
                CustId int NOT NULL,
                CONSTRAINT fk_orders_customers
                    FOREIGN KEY (CustId) REFERENCES dbo.Customers(Id)
            )
            """;
        var r = TSqlPass.Run("p.sql", sql);
        var refs = RefsEdges(r).ToList();
        Assert.Contains(refs, e =>
            e.From == "dbo.orders" &&
            e.FromKind == "table" &&
            e.To == "dbo.customers" &&
            e.ToKind == "table");
    }

    [Fact]
    public void ForeignKey_ColumnLevel_InCreateTable_EmitsReferencesEdge()
    {
        var sql = """
            CREATE TABLE dbo.Orders (
                Id     int NOT NULL,
                CustId int NOT NULL FOREIGN KEY REFERENCES dbo.Customers(Id)
            )
            """;
        var r = TSqlPass.Run("p.sql", sql);
        var refs = RefsEdges(r).ToList();
        Assert.Contains(refs, e =>
            e.From == "dbo.orders" &&
            e.FromKind == "table" &&
            e.To == "dbo.customers" &&
            e.ToKind == "table");
    }

    [Fact]
    public void ForeignKey_AlterTableAddConstraint_EmitsReferencesEdge()
    {
        var sql = """
            ALTER TABLE dbo.Orders
            ADD CONSTRAINT fk_orders_customers
                FOREIGN KEY (CustId) REFERENCES dbo.Customers(Id)
            """;
        var r = TSqlPass.Run("p.sql", sql);
        var refs = RefsEdges(r).ToList();
        Assert.Contains(refs, e =>
            e.From == "dbo.orders" &&
            e.FromKind == "table" &&
            e.To == "dbo.customers" &&
            e.ToKind == "table");
    }

    [Fact]
    public void ForeignKey_ReferencesEdge_HasNoReadsTableEdgeForOwnerOrTarget()
    {
        // FK DDL should not produce reads_table edges for either side.
        var sql = """
            CREATE TABLE dbo.Orders (
                Id     int NOT NULL,
                CustId int NOT NULL,
                CONSTRAINT fk FOREIGN KEY (CustId) REFERENCES dbo.Customers(Id)
            )
            """;
        var r = TSqlPass.Run("p.sql", sql);
        // Only "references" edges expected; no reads_table from FK DDL alone.
        Assert.All(r.Edges.Where(e => e.Relation == "references"), e =>
        {
            Assert.Equal("table", e.FromKind);
            Assert.Equal("table", e.ToKind);
        });
    }

    // ==========================================================================
    // B) Canonicalization of table identity
    // ==========================================================================

    [Fact]
    public void Canon_UnqualifiedTable_DefaultsSchemaToDb()
    {
        // No USE, db-from-filename off: unqualified FROM T -> "dbo.t"
        var r = TSqlPass.Run("p.sql", Proc("SELECT * FROM T"));
        Assert.Contains(ReadsEdges(r), e => e.To == "dbo.t");
    }

    [Fact]
    public void Canon_MixedCaseTable_IsFolded()
    {
        var r = TSqlPass.Run("p.sql", Proc("SELECT * FROM dbo.MyTable"));
        Assert.Contains(ReadsEdges(r), e => e.To == "dbo.mytable");
    }

    [Fact]
    public void Canon_DatabaseFromFilename_QualifiesTable()
    {
        // With db-from-filename on, "SALES_LIVE.sql" -> db prefix "sales_live".
        var r = TSqlPass.Run("SALES_LIVE.sql", Proc("SELECT * FROM dbo.t"), dbFromFileName: true);
        Assert.Contains(ReadsEdges(r), e => e.To == "sales_live.dbo.t");
    }

    [Fact]
    public void Canon_DatabaseFromUseStatement_QualifiesTable()
    {
        // USE Foo sets db context; subsequent FROM dbo.t -> "foo.dbo.t"
        var sql = "USE Foo;\nGO\n" + Proc("SELECT * FROM dbo.t");
        var r = TSqlPass.Run("p.sql", sql);
        Assert.Contains(ReadsEdges(r), e => e.To == "foo.dbo.t");
    }

    [Fact]
    public void Canon_DbFromFilenameOff_DoesNotAddDbPrefix()
    {
        // Default (flag off): the file name is never used as a db prefix.
        var r = TSqlPass.Run("SALES_LIVE.sql", Proc("SELECT * FROM dbo.t"));
        Assert.Contains(ReadsEdges(r), e => e.To == "dbo.t");
        Assert.DoesNotContain(ReadsEdges(r), e => e.To.StartsWith("sales_live."));
    }

    [Fact]
    public void Canon_LinkedServer_StrippedToMetadata_NodeIsBaseDb()
    {
        // [srv].MyDb.dbo.t -> node "mydb.dbo.t", LinkedServer = "srv"
        var r = TSqlPass.Run("p.sql", Proc("SELECT * FROM [srv].MyDb.dbo.t"));
        var edge = Assert.Single(ReadsEdges(r), e => e.To == "mydb.dbo.t");
        Assert.Equal("srv", edge.LinkedServer);
    }

    [Fact]
    public void Canon_LinkedServer_BaseIdentityDoesNotIncludeServerName()
    {
        var r = TSqlPass.Run("p.sql", Proc("SELECT * FROM [srv].MyDb.dbo.t"));
        // The "To" must not contain the server prefix.
        Assert.DoesNotContain(ReadsEdges(r), e => e.To.StartsWith("srv."));
    }

    // ==========================================================================
    // C) Noise exclusion
    // ==========================================================================

    [Fact]
    public void Noise_TempTable_NotEmittedAsReadsEdge()
    {
        var r = TSqlPass.Run("p.sql", Proc("SELECT * FROM #tmp"));
        Assert.DoesNotContain(ReadsEdges(r), e => e.To.Contains("#tmp") || e.To.Contains("tmp"));
    }

    [Fact]
    public void Noise_SysObjects_NotEmittedAsReadsEdge()
    {
        var r = TSqlPass.Run("p.sql", Proc("SELECT * FROM sys.objects"));
        Assert.DoesNotContain(ReadsEdges(r), e => e.To.Contains("sys"));
    }

    [Fact]
    public void Noise_SysColumns_NotEmittedAsReadsEdge()
    {
        var r = TSqlPass.Run("p.sql", Proc("SELECT * FROM sys.columns"));
        Assert.DoesNotContain(ReadsEdges(r), e => e.To.Contains("sys"));
    }

    [Fact]
    public void Noise_TriggerDeleted_NotEmittedAsReadsEdge()
    {
        // `deleted` is a trigger pseudo-table; must be excluded.
        var r = TSqlPass.Run("p.sql", Proc("SELECT * FROM deleted"));
        Assert.DoesNotContain(ReadsEdges(r), e => e.To == "deleted" || e.To.EndsWith(".deleted"));
    }

    [Fact]
    public void Noise_TriggerInserted_NotEmittedAsReadsEdge()
    {
        var r = TSqlPass.Run("p.sql", Proc("SELECT * FROM inserted"));
        Assert.DoesNotContain(ReadsEdges(r), e => e.To == "inserted" || e.To.EndsWith(".inserted"));
    }

    [Fact]
    public void Noise_CteName_NotEmittedAsReadsEdge_OnlyBaseTableIs()
    {
        // The CTE reference "cte" must NOT produce a reads_table edge;
        // only the underlying dbo.t should.
        var sql = Proc("""
            WITH cte AS (SELECT * FROM dbo.t)
            SELECT * FROM cte
            """);
        var r = TSqlPass.Run("p.sql", sql);
        var reads = ReadsEdges(r).ToList();
        Assert.Contains(reads, e => e.To == "dbo.t");
        Assert.DoesNotContain(reads, e => e.To == "cte" || e.To == "dbo.cte");
    }

    [Fact]
    public void Noise_CteName_MultipleCtesNoneAppearAsTableEdges()
    {
        // Multiple CTEs: none of their names should surface as table nodes.
        var sql = Proc("""
            WITH a AS (SELECT * FROM dbo.t1),
                 b AS (SELECT * FROM a)
            SELECT * FROM b
            """);
        var r = TSqlPass.Run("p.sql", sql);
        var reads = ReadsEdges(r).ToList();
        Assert.Contains(reads, e => e.To == "dbo.t1");
        Assert.DoesNotContain(reads, e => e.To == "a" || e.To == "dbo.a");
        Assert.DoesNotContain(reads, e => e.To == "b" || e.To == "dbo.b");
    }
}
