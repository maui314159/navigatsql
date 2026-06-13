using System.Text.Json.Serialization;

/// One emitted graph edge: source --relation--> target, tagged with its source file.
///
/// FromKind is "proc"/"function"/"view" (T-SQL scope), "table" (FK source), or
/// "csharp_method" (C#). ToKind is "table"/"proc"/"function"/"unresolved".
/// EdgeKindTag is the trusty-tools ingest key (`custom:<relation>`).
///
/// LinkedServer is set only when a table was referenced through a linked server
/// (e.g. `[srv].DB.dbo.tbl`); the node identity is canonicalized to the base
/// `database.schema.table` and the server is preserved here as metadata (a
/// cross-server modernization signal). Omitted from JSON when null.
record Edge(
    string File, string From, string FromKind, string To, string ToKind,
    string Relation, string EdgeKindTag,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? LinkedServer = null);

/// Result of extracting one source file.
/// Units = stored procedures/functions/views (T-SQL) or C# members (C#).
record PassResult(List<Edge> Edges, int Units, int DynamicFlags, bool HadParseErrors);
