// C# pass — Roslyn. Emits csharp_method --calls_proc--> proc edges by finding
// proc-name string literals (the data-access call sites) and attributing each to
// its enclosing C# member. This is the cross-tier bridge anchor: chaining these
// with the T-SQL pass's proc->table edges reconstructs method -> proc -> table.
//
// v1 scope: literals whose value matches the stored-procedure naming convention
// (usp_ / sp_ / qry_ / p_ prefix, optionally db/schema-qualified). Convention
// matching was validated at 96% resolution against extracted procs (#580 pilot);
// it deliberately avoids guessing at arbitrary string arguments.

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
                var member = EnclosingMember(lit);
                var proc = val.Replace("[", "").Replace("]", "");
                if (seen.Add((member, proc, "calls_proc")))
                {
                    edges.Add(new Edge(file, member, "csharp_method", proc, "proc", "calls_proc", "custom:calls_proc"));
                    members.Add(member);
                }
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

        return new PassResult(edges, members.Count, 0, false);
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
