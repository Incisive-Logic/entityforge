using System.Globalization;
using System.Text.RegularExpressions;
using VDS.RDF.Parsing;
using VDS.RDF.Update;

namespace EntityForge.Db.Rdf;

public static partial class RdfSparqlUpdate
{
    internal static SparqlUpdateCommandSet ParseCommandSet(string source)
    {
        string prolog = Regex.Match(source, UpdateProlog, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant).Groups["prolog"].Value;
        source = RdfSparql.NormalizeDependencyPrefixNames(source, prolog);
        // The dependency tokeniser uses the current culture when folding
        // keywords. Scope invariant culture to this synchronous parse only;
        // RDF term spelling and the caller's execution culture stay intact.
        CultureInfo previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
            return new SparqlUpdateParser().ParseFromString(source);
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

}
