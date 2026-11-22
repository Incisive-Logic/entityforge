using System.Text;
using System.Text.RegularExpressions;
using EntityForge.Db.Rdf.Contracts;
using VDS.RDF;
using VDS.RDF.Parsing;
using VDS.RDF.Query.Patterns;
using VDS.RDF.Update;
using VDS.RDF.Update.Commands;

namespace EntityForge.Db.Rdf;

public static partial class RdfSparqlUpdate
{
    private static RdfModifyPlan ParseModify(string update)
    {
        // Parse the native WITH scope separately: the reference grammar parser
        // rejects valid WITH + INSERT + USING combinations in its insert-only branch.
        var with = RdfSparql.SeparateModifyWith(update);
        // Preserve native WHERE syntax and expression semantics instead of
        // round-tripping the group through the template parser's formatter.
        var separated = RdfSparql.SeparateModifyWhere(with.Source);
        string template = RdfSparql.NormalizeModifyTemplateQuads(separated.Template);
        template = RdfSparql.IsolateModifyTemplateBlankLabels(template, out var blankLabels);
        var bound = RdfSparql.BindUpdateIris(template);
        template = bound.Source;
        SparqlUpdateCommandSet parsed;
        try
        {
            try
            {
                parsed = ParseCommandSet(template);
            }
            catch (RdfParseException) when (RdfSparql.NormalizeSingleTemplateUsing(template) is not null)
            {
                parsed = ParseCommandSet(RdfSparql.NormalizeSingleTemplateUsing(template)!);
            }
        }
        catch (Exception exception) when (exception is RdfException or SparqlUpdateException)
        {
            throw new InvalidDataException("SPARQL modify syntax is invalid.", exception);
        }
        if (parsed.CommandCount != 1 || parsed.Commands.Single() is not BaseModificationCommand command)
        {
            throw new NotSupportedException("A modify request requires one DELETE/INSERT WHERE operation.");
        }
        GraphPattern? delete = null, insert = null;
        switch (command)
        {
            case ModifyCommand both:
                delete = both.DeletePattern;
                insert = both.InsertPattern;
                break;
            case InsertCommand added:
                insert = added.InsertPattern;
                break;
            case DeleteCommand removed:
                delete = removed.DeletePattern;
                break;
            default:
                throw new NotSupportedException("Unsupported modification command.");
        }
        string prolog = Regex.Match(update, UpdateProlog, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant).Groups["prolog"].Value;
        string? Body(GraphPattern? pattern)
        {
            if (pattern is null)
            {
                return null;
            }
            string text = pattern.ToString().Trim();
            if (!text.StartsWith('{') || !text.EndsWith('}'))
            {
                throw new InvalidDataException("A modification pattern must be a group.");
            }
            return RdfSparql.RestoreModifyTemplateBlankLabels(RdfSparql.RestoreUpdateIris(text[1..^1], bound.Iris), blankLabels);
        }
        var query = new StringBuilder(prolog).Append(" SELECT * ");
        bool usingDataset = false;
        foreach (var graph in command.UsingUris)
        {
            query.Append("FROM <").Append(bound.Iris.GetValueOrDefault(graph.OriginalString, graph.OriginalString)).Append("> ");
            usingDataset = true;
        }
        foreach (var graph in command.UsingNamedUris)
        {
            query.Append("FROM NAMED <").Append(bound.Iris.GetValueOrDefault(graph.OriginalString, graph.OriginalString)).Append("> ");
            usingDataset = true;
        }
        query.Append("WHERE ").Append(separated.Where);
        RdfIri? target = with.Target ?? (command.WithGraphName switch
        {
            null => null,
            IUriNode iri => new(bound.Iris.GetValueOrDefault(iri.Uri.OriginalString, iri.Uri.OriginalString)),
            _ => throw new InvalidDataException("WITH requires an IRI graph name.")
        });
        return new(prolog, query.ToString(), Body(delete), Body(insert), target, target is not null && !usingDataset);
    }
}
