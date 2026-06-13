// EF pass — Roslyn. For Entity Framework codebases, the data-access relationships
// live in the C# entity model, not in SQL. This pass recovers them:
//   - references : entity -> entity (reference navigation property) = table -> table
//   - reads_table / writes_table : csharp_method -> table, from DbSet usage in bodies
//
// Two phases: BuildModel scans all .cs to map entity types -> tables (from DbSet<T>
// and [Table]); Extract then emits edges per file using that model. Table ids are
// `dbo.<entity-or-[Table]-name>` (lowercased) to align with the SQL pass's canonical
// `database.schema.table`. Purely syntactic (no semantic model) — heuristic but cheap.

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

/// The EF model: entity type -> table id, and DbSet property name -> entity type.
sealed class EfModel
{
    public readonly Dictionary<string, string> EntityToTable = new(StringComparer.Ordinal);
    public readonly Dictionary<string, string> DbSetToEntity = new(StringComparer.Ordinal);
    public bool IsEmpty => EntityToTable.Count == 0;
}

static class EfPass
{
    private static readonly HashSet<string> WriteOps = new(StringComparer.Ordinal)
    { "Add", "AddRange", "AddAsync", "AddRangeAsync", "Update", "UpdateRange", "Remove", "RemoveRange", "Attach" };

    /// Phase 1: scan all C# files to build the entity↔table / DbSet model.
    public static EfModel BuildModel(IEnumerable<string> csFiles)
    {
        var m = new EfModel();
        var entities = new HashSet<string>(StringComparer.Ordinal);

        foreach (var file in csFiles)
        {
            string text;
            try { text = File.ReadAllText(file); } catch { continue; }
            SyntaxNode root;
            try { root = CSharpSyntaxTree.ParseText(text).GetRoot(); } catch { continue; }

            foreach (var cls in root.DescendantNodes().OfType<ClassDeclarationSyntax>())
            {
                if (IsDbContext(cls))
                    foreach (var prop in cls.Members.OfType<PropertyDeclarationSyntax>())
                        if (DbSetEntity(prop.Type) is { } ent)
                        {
                            m.DbSetToEntity[prop.Identifier.Text] = ent;
                            entities.Add(ent);
                        }

                if (TableAttr(cls) is { } tbl)
                {
                    m.EntityToTable[cls.Identifier.Text] = NormTable(tbl);
                    entities.Add(cls.Identifier.Text);
                }
            }
        }

        // Entities discovered via DbSet<T> but without a [Table] default to dbo.<name>.
        foreach (var ent in entities)
            if (!m.EntityToTable.ContainsKey(ent))
                m.EntityToTable[ent] = NormTable(ent);

        return m;
    }

    /// Phase 2: emit EF relationship + access edges for one file using the model.
    public static PassResult Extract(string file, SyntaxNode root, EfModel m)
    {
        var edges = new List<Edge>();
        var seen = new HashSet<(string, string, string)>();
        var members = new HashSet<string>();

        // entity -> entity reference navigations => table -> table references
        foreach (var cls in root.DescendantNodes().OfType<ClassDeclarationSyntax>())
        {
            if (!m.EntityToTable.TryGetValue(cls.Identifier.Text, out var fromTable)) continue;
            foreach (var prop in cls.Members.OfType<PropertyDeclarationSyntax>())
            {
                if (RefEntityType(prop.Type) is not { } pt) continue;        // single-entity nav only
                if (!m.EntityToTable.TryGetValue(pt, out var toTable)) continue;
                if (toTable == fromTable) continue;
                if (seen.Add((fromTable, toTable, "references")))
                    edges.Add(new Edge(file, fromTable, "table", toTable, "table", "references", "custom:references"));
            }
        }

        // method -> table from DbSet usage (db.Accounts.Add(x) / db.Accounts.Where(...))
        foreach (var acc in root.DescendantNodes().OfType<MemberAccessExpressionSyntax>())
        {
            if (!m.DbSetToEntity.TryGetValue(acc.Name.Identifier.Text, out var ent)) continue;
            if (!m.EntityToTable.TryGetValue(ent, out var table)) continue;
            var rel = IsWriteAccess(acc) ? "writes_table" : "reads_table";
            var member = EnclosingMember(acc);
            if (seen.Add((member, table, rel)))
            {
                edges.Add(new Edge(file, member, "csharp_method", table, "table", rel, $"custom:{rel}"));
                members.Add(member);
            }
        }

        return new PassResult(edges, members.Count, 0, false);
    }

    // x.Accounts.Add(...) — the DbSet access is the receiver of a write-op invocation.
    private static bool IsWriteAccess(MemberAccessExpressionSyntax dbset) =>
        dbset.Parent is MemberAccessExpressionSyntax outer && WriteOps.Contains(outer.Name.Identifier.Text);

    private static bool IsDbContext(ClassDeclarationSyntax cls) =>
        cls.BaseList?.Types.Any(t => SimpleName(t.Type).EndsWith("DbContext", StringComparison.Ordinal)) ?? false;

    // DbSet<T> -> T's simple name; null otherwise.
    private static string? DbSetEntity(TypeSyntax type) =>
        type is GenericNameSyntax g && g.Identifier.Text == "DbSet" && g.TypeArgumentList.Arguments.Count == 1
            ? SimpleName(g.TypeArgumentList.Arguments[0])
            : null;

    // First positional string literal of a [Table("...")] attribute; null otherwise.
    private static string? TableAttr(ClassDeclarationSyntax cls)
    {
        foreach (var list in cls.AttributeLists)
            foreach (var attr in list.Attributes)
            {
                var n = SimpleName(attr.Name);
                if (n is not ("Table" or "TableAttribute")) continue;
                var arg = attr.ArgumentList?.Arguments.FirstOrDefault();
                if (arg?.Expression is LiteralExpressionSyntax lit && lit.IsKind(SyntaxKind.StringLiteralExpression))
                    return lit.Token.ValueText;
            }
        return null;
    }

    // A single-entity (reference) navigation type: unwrap nullable; ignore collections.
    private static string? RefEntityType(TypeSyntax type)
    {
        if (type is NullableTypeSyntax nt) type = nt.ElementType;
        return type switch
        {
            IdentifierNameSyntax id => id.Identifier.Text,
            QualifiedNameSyntax q => q.Right.Identifier.Text,
            _ => null, // GenericName (ICollection<>/List<>) and predefined types skipped
        };
    }

    private static string NormTable(string name)
    {
        var t = name.Trim().ToLowerInvariant();
        return t.Contains('.') ? t : $"dbo.{t}";
    }

    private static string SimpleName(TypeSyntax t) => t switch
    {
        IdentifierNameSyntax id => id.Identifier.Text,
        GenericNameSyntax g => g.Identifier.Text,
        QualifiedNameSyntax q => q.Right.Identifier.Text,
        _ => t.ToString(),
    };
    private static string SimpleName(NameSyntax n) => n switch
    {
        IdentifierNameSyntax id => id.Identifier.Text,
        GenericNameSyntax g => g.Identifier.Text,
        QualifiedNameSyntax q => q.Right.Identifier.Text,
        _ => n.ToString(),
    };

    // Nearest enclosing member, qualified by its type: "Class.Method".
    private static string EnclosingMember(SyntaxNode node)
    {
        string? typeName = null, memberName = null;
        for (var n = node.Parent; n is not null; n = n.Parent)
        {
            switch (n)
            {
                case MethodDeclarationSyntax me when memberName is null: memberName = me.Identifier.Text; break;
                case ConstructorDeclarationSyntax when memberName is null: memberName = "ctor"; break;
                case PropertyDeclarationSyntax p when memberName is null: memberName = p.Identifier.Text; break;
                case LocalFunctionStatementSyntax lf when memberName is null: memberName = lf.Identifier.Text; break;
                case TypeDeclarationSyntax t when typeName is null: typeName = t.Identifier.Text; break;
            }
        }
        var member = memberName ?? "<member>";
        return typeName is null ? member : $"{typeName}.{member}";
    }
}
