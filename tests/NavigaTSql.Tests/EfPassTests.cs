using Xunit;
using Microsoft.CodeAnalysis.CSharp;
using System.IO;

public class EfPassTests : IDisposable
{
    private readonly string _tmpDir;

    public EfPassTests()
    {
        _tmpDir = Path.Combine(Path.GetTempPath(), "EfPassTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tmpDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tmpDir, recursive: true); } catch { /* best-effort */ }
    }

    // Write a temp .cs file and return its absolute path.
    private string WriteTempFile(string fileName, string content)
    {
        var path = Path.Combine(_tmpDir, fileName);
        File.WriteAllText(path, content);
        return path;
    }

    // -------------------------------------------------------------------------
    // BuildModel tests — require real files on disk
    // -------------------------------------------------------------------------

    [Fact]
    public void BuildModel_maps_DbSet_to_entity_and_defaults_table_to_dbo_lowercase()
    {
        // DbSet<Account> Accounts -> DbSetToEntity["Accounts"]="Account"
        //                         -> EntityToTable["Account"]="dbo.account"
        var file = WriteTempFile("Ctx1.cs", @"
class MyContext : DbContext
{
    public DbSet<Account> Accounts { get; set; }
}
class Account { }
");
        var m = EfPass.BuildModel(new[] { file });

        Assert.Equal("Account", m.DbSetToEntity["Accounts"]);
        Assert.Equal("dbo.account", m.EntityToTable["Account"]);
        Assert.False(m.IsEmpty);
    }

    [Fact]
    public void BuildModel_Table_attribute_overrides_default_table_name()
    {
        // [Table("acct")] on entity class -> EntityToTable["Account"] = "dbo.acct"
        var file = WriteTempFile("Ctx2.cs", @"
class MyContext : DbContext
{
    public DbSet<Account> Accounts { get; set; }
}
[Table(""acct"")]
class Account { }
");
        var m = EfPass.BuildModel(new[] { file });

        Assert.Equal("Account", m.DbSetToEntity["Accounts"]);
        Assert.Equal("dbo.acct", m.EntityToTable["Account"]);
    }

    [Fact]
    public void BuildModel_no_EF_classes_yields_empty_model()
    {
        // A plain C# file with no DbContext or [Table] -> IsEmpty == true
        var file = WriteTempFile("Plain.cs", @"
class Helper
{
    public void DoStuff() { }
}
");
        var m = EfPass.BuildModel(new[] { file });

        Assert.True(m.IsEmpty);
    }

    [Fact]
    public void BuildModel_Table_attribute_name_is_lowercased_and_prefixed_with_dbo()
    {
        // [Table("Orders")] -> "dbo.orders" (NormTable lowercases)
        var file = WriteTempFile("Ctx3.cs", @"
class MyContext : DbContext
{
    public DbSet<Order> Orders { get; set; }
}
[Table(""Orders"")]
class Order { }
");
        var m = EfPass.BuildModel(new[] { file });

        Assert.Equal("dbo.orders", m.EntityToTable["Order"]);
    }

    // -------------------------------------------------------------------------
    // Extract tests — populate EfModel directly to avoid temp files
    // -------------------------------------------------------------------------

    // Helper: build a minimal EfModel with two entities and one DbSet mapping.
    private static EfModel TwoEntityModel()
    {
        var m = new EfModel();
        m.EntityToTable["Order"] = "dbo.order";
        m.EntityToTable["Customer"] = "dbo.customer";
        m.DbSetToEntity["Orders"] = "Order";
        m.DbSetToEntity["Customers"] = "Customer";
        return m;
    }

    [Fact]
    public void Extract_single_reference_nav_emits_references_edge()
    {
        // Order.Customer (non-collection) -> references from dbo.order -> dbo.customer
        var code = @"
class Order
{
    public Customer Customer { get; set; }
}
";
        var root = CSharpSyntaxTree.ParseText(code).GetRoot();
        var m = TwoEntityModel();

        var result = EfPass.Extract("Order.cs", root, m);

        var edge = Assert.Single(result.Edges, e => e.Relation == "references");
        Assert.Equal("dbo.order", edge.From);
        Assert.Equal("table", edge.FromKind);
        Assert.Equal("dbo.customer", edge.To);
        Assert.Equal("table", edge.ToKind);
        Assert.Equal("custom:references", edge.EdgeKindTag);
    }

    [Fact]
    public void Extract_ICollection_nav_does_NOT_emit_references_edge()
    {
        // public ICollection<Item> Items -> no references edge (collection nav skipped)
        var code = @"
class Order
{
    public ICollection<Item> Items { get; set; }
}
";
        var m = new EfModel();
        m.EntityToTable["Order"] = "dbo.order";
        m.EntityToTable["Item"] = "dbo.item";
        m.DbSetToEntity["Items"] = "Item";

        var root = CSharpSyntaxTree.ParseText(code).GetRoot();
        var result = EfPass.Extract("Order.cs", root, m);

        Assert.DoesNotContain(result.Edges, e => e.Relation == "references");
    }

    [Fact]
    public void Extract_List_nav_does_NOT_emit_references_edge()
    {
        // public List<Item> Items -> no references edge (List<T> is generic, skipped)
        var code = @"
class Order
{
    public List<Item> Items { get; set; }
}
";
        var m = new EfModel();
        m.EntityToTable["Order"] = "dbo.order";
        m.EntityToTable["Item"] = "dbo.item";
        m.DbSetToEntity["Items"] = "Item";

        var root = CSharpSyntaxTree.ParseText(code).GetRoot();
        var result = EfPass.Extract("Order.cs", root, m);

        Assert.DoesNotContain(result.Edges, e => e.Relation == "references");
    }

    [Fact]
    public void Extract_DbSet_access_in_Where_emits_reads_table()
    {
        // _ctx.Accounts.Where(...) -> reads_table dbo.account, FromKind=csharp_method
        var code = @"
class AccountRepo
{
    AppDbContext _ctx;
    void GetActive()
    {
        var a = _ctx.Accounts.Where(x => x.Id == 1);
    }
}
";
        var m = new EfModel();
        m.EntityToTable["Account"] = "dbo.account";
        m.DbSetToEntity["Accounts"] = "Account";

        var root = CSharpSyntaxTree.ParseText(code).GetRoot();
        var result = EfPass.Extract("AccountRepo.cs", root, m);

        var edge = Assert.Single(result.Edges, e => e.Relation == "reads_table");
        Assert.Equal("dbo.account", edge.To);
        Assert.Equal("table", edge.ToKind);
        Assert.Equal("csharp_method", edge.FromKind);
        Assert.Equal("custom:reads_table", edge.EdgeKindTag);
    }

    [Fact]
    public void Extract_DbSet_Add_emits_writes_table()
    {
        // _ctx.Accounts.Add(a) -> writes_table dbo.account
        var code = @"
class AccountRepo
{
    AppDbContext _ctx;
    void Save(Account a)
    {
        _ctx.Accounts.Add(a);
    }
}
";
        var m = new EfModel();
        m.EntityToTable["Account"] = "dbo.account";
        m.DbSetToEntity["Accounts"] = "Account";

        var root = CSharpSyntaxTree.ParseText(code).GetRoot();
        var result = EfPass.Extract("AccountRepo.cs", root, m);

        var edge = Assert.Single(result.Edges, e => e.Relation == "writes_table");
        Assert.Equal("dbo.account", edge.To);
        Assert.Equal("table", edge.ToKind);
        Assert.Equal("csharp_method", edge.FromKind);
        Assert.Equal("custom:writes_table", edge.EdgeKindTag);
    }

    [Fact]
    public void Extract_reads_and_writes_in_same_file_both_emitted()
    {
        // One method reads, another writes -> two separate edges
        var code = @"
class OrderService
{
    AppDbContext _ctx;
    void Read()  { var o = _ctx.Orders.FirstOrDefault(); }
    void Write() { _ctx.Orders.Add(new Order()); }
}
";
        var m = TwoEntityModel();
        var root = CSharpSyntaxTree.ParseText(code).GetRoot();
        var result = EfPass.Extract("OrderService.cs", root, m);

        Assert.Contains(result.Edges, e => e.Relation == "reads_table" && e.To == "dbo.order");
        Assert.Contains(result.Edges, e => e.Relation == "writes_table" && e.To == "dbo.order");
    }

    [Fact]
    public void Extract_enclosing_member_is_qualified_with_class_name()
    {
        // From for a method edge should be "ClassName.MethodName"
        var code = @"
class MyRepo
{
    AppDbContext _ctx;
    void Fetch() { var x = _ctx.Customers.ToList(); }
}
";
        var m = TwoEntityModel();
        var root = CSharpSyntaxTree.ParseText(code).GetRoot();
        var result = EfPass.Extract("MyRepo.cs", root, m);

        var edge = Assert.Single(result.Edges, e => e.Relation == "reads_table" && e.To == "dbo.customer");
        Assert.Equal("MyRepo.Fetch", edge.From);
    }

    [Fact]
    public void Extract_Units_counts_distinct_members_not_edges()
    {
        // Two DbSet accesses in the same method -> 1 unit, but deduplication means 1 edge
        var code = @"
class Svc
{
    AppDbContext _ctx;
    void Work()
    {
        var o = _ctx.Orders.ToList();
        _ctx.Orders.Add(new Order());
    }
}
";
        var m = TwoEntityModel();
        var root = CSharpSyntaxTree.ParseText(code).GetRoot();
        var result = EfPass.Extract("Svc.cs", root, m);

        // reads_table and writes_table for the same table from the same member are distinct
        // (different relation string), so we get two edges but only one member counted.
        Assert.Equal(1, result.Units);
    }

    [Fact]
    public void Extract_HadParseErrors_is_always_false_for_valid_code()
    {
        var code = @"class Stub { }";
        var m = new EfModel();
        var root = CSharpSyntaxTree.ParseText(code).GetRoot();
        var result = EfPass.Extract("Stub.cs", root, m);

        Assert.False(result.HadParseErrors);
    }

    [Fact]
    public void Extract_empty_model_produces_no_edges()
    {
        var code = @"
class Repo
{
    AppDbContext _ctx;
    void Go() { _ctx.Accounts.Add(null); }
}
";
        var m = new EfModel(); // no entity mappings
        var root = CSharpSyntaxTree.ParseText(code).GetRoot();
        var result = EfPass.Extract("Repo.cs", root, m);

        Assert.Empty(result.Edges);
    }
}
