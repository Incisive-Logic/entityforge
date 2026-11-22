using EntityForge.Db.Query.Syntax.Gql;

namespace EntityForge.Db.Gql;

internal static partial class GraphGqlStandardSyntax
{
    internal static string ProcedureKey(string spelling)
    {
        if (String.IsNullOrWhiteSpace(spelling) || spelling.Length > 1024 || spelling != spelling.Trim())
        {
            throw new ArgumentException("A procedure name must contain at most 1,024 characters without surrounding whitespace.");
        }
        string source = "CALL " + spelling + "()";
        var calls = Descendants<GQLParser.NamedProcedureCallContext>(GqlSyntaxParser.Parse(source, out _)).ToArray();
        if (calls.Length != 1 || calls[0].procedureReference().Start.StartIndex != 5 ||
            calls[0].procedureReference().Stop.StopIndex != 4 + spelling.Length || calls[0].Stop.StopIndex != source.Length - 1)
        {
            throw new ArgumentException("A catalog name must be one procedure reference.");
        }
        return ProcedureKey(calls[0].procedureReference());
    }

    private static string ProcedureKey(GQLParser.ProcedureReferenceContext reference)
    {
        var named = reference.catalogProcedureParentAndName();
        if (named is null || named.catalogObjectParentReference()?.schemaReference() is not null)
        {
            throw new ArgumentException("Schema paths and parameterized procedure references are not supported by this catalog.");
        }
        // Encode components separately: `tools.increment` is one name, whereas
        // tools.increment has an object qualifier. Quoting does not change identity.
        var parts = (named.catalogObjectParentReference()?.objectName() ?? [])
            .Select(part => GraphGqlIdentifiers.DecodeIdentifier(part.GetText()))
            .Append(GraphGqlIdentifiers.DecodeIdentifier(named.procedureName().GetText()));
        return System.Text.Json.JsonSerializer.Serialize(parts.ToArray());
    }

    internal static IEnumerable<string> ProcedureReferences(string source)
        => Descendants<GQLParser.NamedProcedureCallContext>(GqlSyntaxParser.Parse(source, out _))
            .Select(call => ProcedureKey(call.procedureReference())).Distinct(StringComparer.Ordinal);

    internal static string[] ProcedureColumns(GqlProcedureDefinition procedure)
    {
        var tree = GqlSyntaxParser.Parse(procedure.Query, out _);
        bool TopLevelResult(GQLParser.PrimitiveResultStatementContext result)
        {
            for (var parent = result.Parent; parent is not null; parent = parent.Parent)
            {
                if (parent is GQLParser.NestedQuerySpecificationContext or GQLParser.NestedProcedureSpecificationContext)
                {
                    return false;
                }
            }
            return true;
        }
        var finalResult = Descendants<GQLParser.PrimitiveResultStatementContext>(tree)
            .Where(TopLevelResult).OrderBy(result => result.Start.StartIndex).LastOrDefault();
        if (finalResult?.GetText().Equals("FINISH", StringComparison.OrdinalIgnoreCase) == true)
        {
            // FINISH lowers to a private empty projection. That implementation
            // column is not a declared procedure result.
            throw new ArgumentException("A registered procedure must end with explicit named result columns.");
        }
        if (!procedure.ModifiesData && Descendants<GQLParser.PrimitiveDataModifyingStatementContext>(tree).Any() ||
            Descendants<GQLParser.NamedProcedureCallContext>(tree).Any() ||
            Descendants<GQLParser.ReturnStatementBodyContext>(tree).Any(body => body.ASTERISK() is not null))
        {
            throw new ArgumentException("Procedure bodies require explicit results and resolved dependencies; read procedures cannot modify data.");
        }
        string inputs = procedure.Parameters.Count == 0 ? "" :
            "LET " + String.Join(',', procedure.Parameters.Select(name => name + "=NULL")) + " ";
        string prepared = Prepare("CALL () { " + inputs + procedure.Query + " } RETURN *");
        var plan = procedure.ModifiesData ?
            GraphGqlMutationCompiler.TryCompileProjectionShape(prepared, new HashSet<string>(StringComparer.Ordinal)) ??
                new GraphGqlQueryCompiler().Compile(new(prepared)) :
            new GraphGqlQueryCompiler().Compile(new(prepared));
        string[] columns = GraphGqlQueryCompiler.CallColumns(plan);
        if (columns.Length == 0)
        {
            throw new ArgumentException("A registered procedure must return named columns.");
        }
        return columns;
    }

    internal static string ExpandProcedures(string source, GqlProcedureCatalog catalog, int depth = 0, string reserved = "")
    {
        if (depth > 16)
        {
            throw new ArgumentException("Named procedure argument nesting exceeds sixteen levels.");
        }
        var tree = GqlSyntaxParser.Parse(source, out _);
        var calls = Descendants<GQLParser.NamedProcedureCallContext>(tree).OrderByDescending(call => call.Start.StartIndex).ToArray();
        if (calls.Length > 64)
        {
            throw new ArgumentException("A request accepts at most 64 named procedure calls.");
        }
        // Replacements use source positions from the original parse. Nested
        // named calls in argument subqueries are expanded recursively as a unit.
        var topCalls = calls.Where(call => !calls.Any(parent => parent != call && parent.Start.StartIndex < call.Start.StartIndex && parent.Stop.StopIndex > call.Stop.StopIndex)).ToArray();
        foreach (var call in topCalls)
        {
            var (procedure, columns) = catalog.Resolve(ProcedureKey(call.procedureReference()));
            var arguments = call.procedureArgumentList()?.procedureArgument() ?? [];
            if (arguments.Length != procedure.Parameters.Count)
            {
                throw new ArgumentException("Procedure argument count does not match its definition.");
            }
            string prefix = "__procedure0_";
            for (int suffix = 1; source.Contains(prefix, StringComparison.Ordinal) || procedure.Query.Contains(prefix, StringComparison.Ordinal) || reserved.Contains(prefix, StringComparison.Ordinal); suffix++)
            {
                prefix = "__procedure" + suffix + "_";
            }
            string[] argumentNames = arguments.Select((_, index) => prefix + index).ToArray();
            string Argument(GQLParser.ProcedureArgumentContext argument)
            {
                const string begin = "RETURN ", end = " AS __argumentResult";
                return ExpandProcedures(begin + source[argument.Start.StartIndex..(argument.Stop.StopIndex + 1)] + end,
                    catalog, depth + 1, reserved + prefix)[begin.Length..^end.Length];
            }
            string argumentBindings = arguments.Length == 0 ? "" : "LET " + String.Join(',', arguments.Select((argument, index) =>
                argumentNames[index] + "=(" + Argument(argument) + ")")) + " ";
            string inputs = arguments.Length == 0 ? "" : "LET " + String.Join(',', procedure.Parameters.Select((name, index) => name + "=" + argumentNames[index])) + " ";
            string[] resultNames = columns.Select((_, index) => prefix + "Result" + index).ToArray();
            string[] exports = columns.Select((name, index) => resultNames[index] + " AS " + GraphGqlIdentifiers.QuoteAlias(name)).ToArray();
            if (call.yieldClause() is { } yield)
            {
                exports = yield.yieldItemList().yieldItem().Select(item =>
                {
                    string name = GraphGqlIdentifiers.DecodeIdentifier(item.yieldItemName().GetText());
                    if (!columns.Contains(name, StringComparer.Ordinal))
                    {
                        throw new ArgumentException("Unknown procedure result column: " + name);
                    }
                    return resultNames[Array.IndexOf(columns, name)] + " AS " + (item.yieldItemAlias() is { } alias ? alias.bindingVariable().GetText() : GraphGqlIdentifiers.QuoteAlias(name));
                }).ToArray();
            }
            string renamedResults = String.Join(',', columns.Select((name, index) => GraphGqlIdentifiers.QuoteAlias(name) + " AS " + resultNames[index]));
            string replacement = "{ " + argumentBindings + "CALL (" + String.Join(',', argumentNames) + ") { " + inputs + procedure.Query +
                " NEXT YIELD " + renamedResults + " RETURN " + String.Join(',', resultNames) + " } RETURN " + String.Join(',', exports) + " }";
            source = source.Remove(call.Start.StartIndex, call.Stop.StopIndex - call.Start.StartIndex + 1).Insert(call.Start.StartIndex, replacement);
            if (source.Length > GraphGqlQueryCompiler.MaximumQueryCharacters)
            {
                throw new ArgumentException("Expanded procedures exceed the query character limit.");
            }
        }
        return source;
    }
}
