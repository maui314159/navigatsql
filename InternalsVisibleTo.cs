// Expose the extractor's internal types (TSqlPass, CSharpPass, EfPass, Edge, …) to
// the test assembly without widening the public API surface.
[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("NavigaTSql.Tests")]
