using EntityForge.Db.Query.Planning;

namespace EntityForge.Db.Rdf;

public static partial class RdfSparql
{
    private static readonly Lazy<UnicodeText.CaseData> UnicodeCaseData = UnicodeText.Data;

    private static string ChangeCase(string text, bool upper) => UnicodeText.ChangeCase(text, upper,
        static () => new RdfSparqlWorkLimitException("Case conversion exceeds 1,048,576 UTF-16 code units."));
}
