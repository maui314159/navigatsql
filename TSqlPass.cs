// T-SQL pass — Microsoft ScriptDom. For each stored procedure / function / view,
// emits resource/data-flow relations: reads_table / writes_table / calls_proc /
// calls_function, foreign-key `references`, and dynamic_sql (flagged).
//
// Table identities are CANONICALIZED from the structured name (server.database.
// schema.table) rather than the raw dotted string, so the same table converges
// to one node: missing schema defaults to `dbo`; the containing database (from a
// USE statement or the dump filename) qualifies 1/2-part names; case is folded;
// linked-server prefixes are stripped to the node identity but preserved as edge
// metadata. Temp tables (#x), table variables, trigger pseudo-tables
// (deleted/inserted), MERGE aliases, system catalog (sys.*), and CTE names are
// excluded from table edges.

using Microsoft.SqlServer.TransactSql.ScriptDom;

static class TSqlPass
{
    /// Parse one T-SQL file and extract relation edges. Recovers from parse errors
    /// by mining ScriptDom's partial tree; never throws on syntax errors.
    public static PassResult Run(string file, string text, bool dbFromFileName = false)
    {
        // 160 = SQL Server 2022 dialect; ScriptDom is backward compatible.
        var parser = new TSql160Parser(initialQuotedIdentifiers: true);
        using var reader = new StringReader(text);
        TSqlFragment? tree = parser.Parse(reader, out IList<ParseError> errors);

        bool hadErrors = errors.Count > 0;
        if (hadErrors)
        {
            var shown = errors.Take(3).Select(e => $"L{e.Line}: {e.Message}");
            Console.Error.WriteLine($"parse errors in {file} ({errors.Count}); recovering — {string.Join(" | ", shown)}");
        }
        if (tree is null) return new PassResult(new(), 0, 0, hadErrors);

        // Pre-pass: collect CTE names so they can be excluded from table reads.
        var cte = new CteNameCollector();
        tree.Accept(cte);

        var v = new RelationVisitor(file, DbFromFileName(file, dbFromFileName), cte.Names);
        tree.Accept(v);
        return new PassResult(v.Edges, v.ProcCount + v.FunctionCount + v.ViewCount, v.DynamicSqlFlags, hadErrors);
    }

    // Opt-in (`--db-from-filename`): treat a whole-database dump's file name as the default
    // database context for 1/2-part names, so `dbo.tbl_X` in `SALES_LIVE.sql` canonicalizes
    // to `sales_live.dbo.tbl_x` (and e.g. SALES_LIVE vs SALES_ARCHIVE stay distinct dbs).
    // Off by default, because routine-per-file layouts (`usp_X.sql`) must NOT treat the file
    // name as a database. A `USE <db>` statement, where present, takes precedence regardless.
    private static string? DbFromFileName(string file, bool enabled) =>
        enabled ? Path.GetFileNameWithoutExtension(file).ToLowerInvariant() : null;

    /// Parse a SQL snippet (e.g. an embedded Dapper query string lifted from C#)
    /// and return its canonicalized table reads/writes. Best-effort: returns empty
    /// on parse failure (no logging). Used by the C# pass to turn raw-SQL-in-C# into
    /// cross-tier method -> table edges. No database context (returns schema.table).
    public static List<(string relation, string table, string? linkedServer)> ExtractTableRefs(string sql)
    {
        var parser = new TSql160Parser(initialQuotedIdentifiers: true);
        using var reader = new StringReader(sql);
        TSqlFragment? tree;
        try { tree = parser.Parse(reader, out _); }
        catch { return new(); }
        if (tree is null) return new();

        var cte = new CteNameCollector();
        tree.Accept(cte);
        var v = new RelationVisitor("<embedded>", null, cte.Names);
        tree.Accept(v);
        return v.Edges
            .Where(e => e.Relation is "reads_table" or "writes_table")
            .Select(e => (e.Relation, e.To, e.LinkedServer))
            .ToList();
    }
}

/// Collects every CTE name in a file (WITH x AS (...)) so x isn't mistaken for a table.
sealed class CteNameCollector : TSqlFragmentVisitor
{
    public readonly HashSet<string> Names = new(StringComparer.OrdinalIgnoreCase);
    public override void Visit(CommonTableExpression n)
    {
        if (n.ExpressionName?.Value is { } v) Names.Add(v);
    }
}

/// Collects NamedTableReference targets (handles joins, nesting).
sealed class TableCollector : TSqlFragmentVisitor
{
    public readonly List<SchemaObjectName> Tables = new();
    public override void Visit(NamedTableReference n) => Tables.Add(n.SchemaObject);
}

/// Maps FROM-clause aliases back to their real table (`dbo.T AS p` -> p -> dbo.T).
sealed class AliasCollector : TSqlFragmentVisitor
{
    public readonly Dictionary<string, SchemaObjectName> AliasToTable = new(StringComparer.OrdinalIgnoreCase);
    public override void Visit(NamedTableReference n)
    {
        if (n.Alias?.Value is { } a) AliasToTable[a] = n.SchemaObject;
    }
}

sealed class RelationVisitor : TSqlFragmentVisitor
{
    public readonly List<Edge> Edges = new();
    public int ProcCount { get; private set; }
    public int FunctionCount { get; private set; }
    public int ViewCount { get; private set; }
    public int DynamicSqlFlags { get; private set; }

    private readonly string _file;
    private readonly HashSet<string> _cte;
    private readonly HashSet<(string, string, string)> _seen = new();
    private string _scope = "<script-level>";
    private string _scopeKind = "proc";
    private string? _db;

    private static readonly HashSet<string> SysTables =
        new(new[] { "sysobjects", "syscolumns", "sysindexes", "sysusers", "sysfiles", "sysprocesses" },
            StringComparer.OrdinalIgnoreCase);

    public RelationVisitor(string file, string? db, HashSet<string> cteNames)
    {
        _file = file; _db = db; _cte = cteNames;
    }

    private void EmitEdge(string from, string fromKind, string to, string toKind, string relation, string? linkedServer)
    {
        if (_seen.Add((from, to, relation)))
            Edges.Add(new Edge(_file, from, fromKind, to, toKind, relation, $"custom:{relation}", linkedServer));
    }

    // proc/function/view body relations (calls_proc, calls_function, dynamic_sql).
    private void Emit(string to, string toKind, string relation) =>
        EmitEdge(_scope, _scopeKind, to, toKind, relation, null);

    // A table read/write attributed to the current scope, canonicalized + filtered.
    private void EmitTable(SchemaObjectName son, string relation)
    {
        var (id, server) = Canon(son);
        if (id is not null) EmitEdge(_scope, _scopeKind, id, "table", relation, server);
    }

    // database context follows USE statements within a script.
    public override void Visit(UseStatement node)
    {
        if (node.DatabaseName?.Value is { } v) _db = v.ToLowerInvariant();
    }

    // ---- scopes: procedures, functions, views ----
    public override void Visit(CreateProcedureStatement node)
    { _scope = Naming.Of(node.ProcedureReference.Name); _scopeKind = "proc"; ProcCount++; }
    public override void Visit(AlterProcedureStatement node)
    { _scope = Naming.Of(node.ProcedureReference.Name); _scopeKind = "proc"; ProcCount++; }
    public override void Visit(CreateOrAlterProcedureStatement node)
    { _scope = Naming.Of(node.ProcedureReference.Name); _scopeKind = "proc"; ProcCount++; }
    public override void Visit(CreateFunctionStatement node)
    { _scope = Naming.Of(node.Name); _scopeKind = "function"; FunctionCount++; }
    public override void Visit(AlterFunctionStatement node)
    { _scope = Naming.Of(node.Name); _scopeKind = "function"; FunctionCount++; }
    public override void Visit(CreateOrAlterFunctionStatement node)
    { _scope = Naming.Of(node.Name); _scopeKind = "function"; FunctionCount++; }
    public override void Visit(CreateViewStatement node)
    { _scope = Naming.Of(node.SchemaObjectName); _scopeKind = "view"; ViewCount++; }
    public override void Visit(AlterViewStatement node)
    { _scope = Naming.Of(node.SchemaObjectName); _scopeKind = "view"; ViewCount++; }
    public override void Visit(CreateOrAlterViewStatement node)
    { _scope = Naming.Of(node.SchemaObjectName); _scopeKind = "view"; ViewCount++; }

    // ---- reads ----
    public override void Visit(FromClause node)
    {
        var c = new TableCollector();
        node.Accept(c);
        foreach (var son in c.Tables) EmitTable(son, "reads_table");
    }

    // ---- writes ----
    public override void Visit(InsertStatement node) => EmitTargetWrite(node.InsertSpecification?.Target, null);
    public override void Visit(UpdateStatement node) =>
        EmitTargetWrite(node.UpdateSpecification?.Target, node.UpdateSpecification?.FromClause);
    public override void Visit(DeleteStatement node) =>
        EmitTargetWrite(node.DeleteSpecification?.Target, node.DeleteSpecification?.FromClause);
    public override void Visit(MergeStatement node)
    {
        EmitTargetWrite(node.MergeSpecification?.Target, null);
        if (node.MergeSpecification?.TableReference is { } src)
        {
            var c = new TableCollector();
            src.Accept(c);
            foreach (var son in c.Tables) EmitTable(son, "reads_table");
        }
    }

    private void EmitTargetWrite(TableReference? target, FromClause? fromForAlias)
    {
        if (target is null) return;

        Dictionary<string, SchemaObjectName>? aliasMap = null;
        if (fromForAlias is not null)
        {
            var a = new AliasCollector();
            fromForAlias.Accept(a);
            aliasMap = a.AliasToTable;
        }

        var c = new TableCollector();
        target.Accept(c);
        foreach (var son in c.Tables)
        {
            var resolved = son;
            // `UPDATE p ... FROM dbo.T AS p`: the 1-part target is an alias.
            if (aliasMap is not null && son.SchemaIdentifier is null && son.DatabaseIdentifier is null
                && son.BaseIdentifier?.Value is { } b && aliasMap.TryGetValue(b, out var real))
                resolved = real;
            EmitTable(resolved, "writes_table");
        }
    }

    // ---- calls ----
    public override void Visit(ExecuteStatement node)
    {
        switch (node.ExecuteSpecification?.ExecutableEntity)
        {
            case ExecutableProcedureReference p when p.ProcedureReference?.ProcedureReference?.Name is { } name:
                var proc = Naming.Of(name);
                if (IsDynamicExec(proc)) { DynamicSqlFlags++; Emit("<dynamic>", "unresolved", "dynamic_sql"); }
                else Emit(proc, "proc", "calls_proc");
                break;
            case ExecutableStringList: // EXEC(@sql) — direct string exec, unresolvable
                DynamicSqlFlags++; Emit("<dynamic>", "unresolved", "dynamic_sql");
                break;
        }
    }

    // calls_function: scalar UDFs (schema-qualified FunctionCall; built-ins skipped)
    // and table-valued UDFs in FROM.
    public override void Visit(FunctionCall node)
    {
        if (node.CallTarget is MultiPartIdentifierCallTarget t)
        {
            var prefix = string.Join('.', t.MultiPartIdentifier.Identifiers.Select(i => i.Value));
            Emit($"{prefix}.{node.FunctionName.Value}", "function", "calls_function");
        }
    }
    public override void Visit(SchemaObjectFunctionTableReference node) =>
        Emit(Naming.Of(node.SchemaObject), "function", "calls_function");

    private static bool IsDynamicExec(string proc) =>
        proc.Equals("sp_executesql", StringComparison.OrdinalIgnoreCase) ||
        proc.Equals("sp_execute", StringComparison.OrdinalIgnoreCase);

    // ---- foreign keys ----
    public override void Visit(CreateTableStatement node) =>
        EmitForeignKeys(node.SchemaObjectName, node.Definition);
    public override void Visit(AlterTableAddTableElementStatement node) =>
        EmitForeignKeys(node.SchemaObjectName, node.Definition);

    private void EmitForeignKeys(SchemaObjectName ownerSon, TableDefinition? def)
    {
        if (def is null) return;
        var (owner, _) = Canon(ownerSon);
        if (owner is null) return;
        void Fk(ForeignKeyConstraintDefinition fk)
        {
            var (to, server) = Canon(fk.ReferenceTableName);
            if (to is not null) EmitEdge(owner, "table", to, "table", "references", server);
        }
        foreach (var fk in def.TableConstraints.OfType<ForeignKeyConstraintDefinition>()) Fk(fk);
        foreach (var col in def.ColumnDefinitions)
            foreach (var fk in col.Constraints.OfType<ForeignKeyConstraintDefinition>()) Fk(fk);
    }

    // Canonicalize a table name to `database.schema.table` (lowercased), defaulting
    // schema to dbo and database to the current context; strip the linked server to
    // metadata; return (null, _) for non-table noise. Second tuple item = linked server.
    private (string?, string?) Canon(SchemaObjectName son)
    {
        if (son.BaseIdentifier?.Value is not { } rawTable || rawTable.Length == 0) return (null, null);
        var table = rawTable.ToLowerInvariant();
        if (table.StartsWith("#")) return (null, null);                       // temp table

        var schema = son.SchemaIdentifier?.Value?.ToLowerInvariant();
        if (schema is "sys" or "information_schema") return (null, null);     // system catalog
        if (SysTables.Contains(table)) return (null, null);

        bool unqualified = son.SchemaIdentifier is null && son.DatabaseIdentifier is null && son.ServerIdentifier is null;
        if (unqualified)
        {
            if (table is "deleted" or "inserted" or "target" or "source") return (null, null); // pseudo/alias
            if (_cte.Contains(rawTable)) return (null, null);                 // CTE name
        }

        var db = (son.DatabaseIdentifier?.Value?.ToLowerInvariant()) ?? _db;
        var s = schema ?? "dbo";
        var id = db is not null ? $"{db}.{s}.{table}" : $"{s}.{table}";
        return (id, son.ServerIdentifier?.Value?.ToLowerInvariant());
    }
}

static class Naming
{
    public static string Of(SchemaObjectName name) =>
        string.Join('.', name.Identifiers.Select(i => i.Value));
}
