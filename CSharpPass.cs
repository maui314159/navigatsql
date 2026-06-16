// C# pass — Roslyn. Emits csharp_method --calls_proc--> proc edges by finding
// proc-name string literals (the data-access call sites) and attributing each to
// its enclosing C# member. This is the cross-tier bridge anchor: chaining these
// with the T-SQL pass's proc->table edges reconstructs method -> proc -> table.
//
// Two convention-independent signals feed calls_proc:
//   1. Literals whose value matches the stored-procedure naming convention
//      (usp_ / sp_ / qry_ / p_ prefix, optionally db/schema-qualified). Convention
//      matching was validated at 96% resolution against extracted procs (#580 pilot).
//   2. Invocations carrying an explicit CommandType.StoredProcedure marker
//      (Dapper/ADO): the first string-literal argument is the proc name regardless
//      of prefix (#12). The marker is declared intent, not a guess — so this still
//      deliberately avoids guessing at arbitrary string arguments.

using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

static class CSharpPass
{
    // Proc-shaped literal: optional [db].[schema]. prefix, then a leaf whose name
    // starts with a stored-proc convention prefix. Standalone token (no spaces),
    // so embedded SQL strings (which contain whitespace) never match.
    private static readonly Regex ProcShaped = new(
        @"^(\[?\w+\]?\.){0,2}\[?(usp_|sp_|qry_|p_)\w+\]?$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // A string literal whose content looks like a SQL DML statement (e.g. a Dapper
    // query). Bounded quantifiers avoid catastrophic backtracking on large strings.
    private static readonly Regex SqlShaped = new(
        @"\bselect\b[\s\S]{0,4000}\bfrom\b|\binsert\s+into\b|\bupdate\b[\s\S]{0,400}\bset\b|\bdelete\s+from\b|\bmerge\b[\s\S]{0,400}\busing\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// Parse one C# file and extract method -> proc call edges. Roslyn returns a
    /// partial tree on syntax errors, so extraction is best-effort and never throws.
    public static PassResult Run(string file, SyntaxNode root)
    {
        var edges = new List<Edge>();
        var seen = new HashSet<(string, string, string)>();
        var members = new HashSet<string>();

        foreach (var lit in root.DescendantNodes().OfType<LiteralExpressionSyntax>())
        {
            if (!lit.IsKind(SyntaxKind.StringLiteralExpression)) continue;
            var val = lit.Token.ValueText?.Trim();
            if (string.IsNullOrEmpty(val)) continue;

            if (ProcShaped.IsMatch(val))
            {
                // Proc-name literal at a data-access call site -> method -> proc.
                AddProcEdge(file, EnclosingMember(lit), val, edges, seen, members);
            }
            else if (SqlShaped.IsMatch(val))
            {
                // Embedded SQL (e.g. a Dapper query) -> parse it and attribute its
                // table reads/writes to the enclosing C# method (cross-tier, no proc).
                var refs = TSqlPass.ExtractTableRefs(val);
                if (refs.Count == 0) continue;
                var member = EnclosingMember(lit);
                foreach (var (relation, table, linkedServer) in refs)
                {
                    if (seen.Add((member, table, relation)))
                    {
                        edges.Add(new Edge(file, member, "csharp_method", table, "table", relation, $"custom:{relation}", linkedServer));
                        members.Add(member);
                    }
                }
            }
        }

        // Dapper/ADO: an explicit CommandType.StoredProcedure marker names a proc
        // regardless of convention. Walk invocations and lift the first literal arg.
        foreach (var inv in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            var args = inv.ArgumentList.Arguments;
            if (!args.Any(a => IsStoredProcedureMarker(a.Expression))) continue;

            // Dapper's sql/command parameter is positional-first; take the first
            // string literal among the invocation's own arguments as the proc name.
            var nameArg = args.FirstOrDefault(a => a.Expression.IsKind(SyntaxKind.StringLiteralExpression));
            var val = (nameArg?.Expression as LiteralExpressionSyntax)?.Token.ValueText?.Trim();
            if (string.IsNullOrEmpty(val)) continue;

            // Shared seen-set dedups against a convention-named call that also passes
            // the marker, so such a call site emits exactly one calls_proc edge.
            AddProcEdge(file, EnclosingMember(inv), val, edges, seen, members);
        }

        return new PassResult(edges, members.Count, 0, false);
    }

    // Emit one csharp_method --calls_proc--> proc edge: strip identifier brackets,
    // dedup on (member, proc, relation), and record the member. Shared by the
    // convention-literal path and the CommandType.StoredProcedure marker path.
    private static void AddProcEdge(string file, string member, string literal,
        List<Edge> edges, HashSet<(string, string, string)> seen, HashSet<string> members)
    {
        var proc = literal.Replace("[", "").Replace("]", "");
        if (seen.Add((member, proc, "calls_proc")))
        {
            edges.Add(new Edge(file, member, "csharp_method", proc, "proc", "calls_proc", "custom:calls_proc"));
            members.Add(member);
        }
    }

    // True for a `CommandType.StoredProcedure` argument in any qualified form
    // (CommandType.StoredProcedure or System.Data.CommandType.StoredProcedure),
    // matched on identifier text only — no semantic model. Requires the trailing
    // qualifier to be `CommandType`, so a bare `StoredProcedure` or a same-named
    // member of another type does not match, and CommandType.Text never matches.
    private static bool IsStoredProcedureMarker(ExpressionSyntax expr)
    {
        if (expr is not MemberAccessExpressionSyntax ma) return false;
        if (ma.Name.Identifier.Text != "StoredProcedure") return false;
        return ma.Expression switch
        {
            IdentifierNameSyntax id => id.Identifier.Text == "CommandType",
            MemberAccessExpressionSyntax inner => inner.Name.Identifier.Text == "CommandType",
            _ => false,
        };
    }

    // Nearest enclosing member, qualified by its type: "Class.Method".
    private static string EnclosingMember(SyntaxNode node)
    {
        string? typeName = null;
        string? memberName = null;
        for (var n = node.Parent; n is not null; n = n.Parent)
        {
            switch (n)
            {
                case MethodDeclarationSyntax m when memberName is null:
                    memberName = m.Identifier.Text; break;
                case ConstructorDeclarationSyntax when memberName is null:
                    memberName = "ctor"; break;
                case PropertyDeclarationSyntax p when memberName is null:
                    memberName = p.Identifier.Text; break;
                case LocalFunctionStatementSyntax lf when memberName is null:
                    memberName = lf.Identifier.Text; break;
                case TypeDeclarationSyntax t when typeName is null:
                    typeName = t.Identifier.Text; break;
            }
        }
        var member = memberName ?? "<member>";
        return typeName is null ? member : $"{typeName}.{member}";
    }
}
