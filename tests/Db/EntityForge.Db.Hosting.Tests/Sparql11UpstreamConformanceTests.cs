using System.Text.Json;
using System.Globalization;
using System.Numerics;
using System.Text.RegularExpressions;
using EntityForge.Db.Rdf;
using EntityForge.Db.Rdf.Contracts;
using VDS.RDF;
using VDS.RDF.Parsing;
using VDS.RDF.Query;

namespace EntityForge.Db.Hosting.Tests;

public sealed partial class RdfDatabaseEngineTests
{
    [TestMethod]
    public void Sparql11_external_W3C_query_evaluation_manifest()
    {
        string? configured = Environment.GetEnvironmentVariable("ENTITYFORGE_W3C_TEST_ROOT");
        if (String.IsNullOrWhiteSpace(configured))
        {
            Assert.Inconclusive("Set ENTITYFORGE_W3C_TEST_ROOT to a local w3c/rdf-tests checkout to run upstream evaluation fixtures.");
        }
        string root = Path.GetFullPath(configured).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        const string mf = "http://www.w3.org/2001/sw/DataAccess/tests/test-manifest#";
        const string qt = "http://www.w3.org/2001/sw/DataAccess/tests/test-query#";
        var results = new List<object>();
        int failed = 0, passed = 0, outsideProfile = 0;
        string Local(INode node)
        {
            if (node is not IUriNode uri || !uri.Uri.IsFile)
            {
                throw new InvalidDataException("Upstream fixture resources must be local files.");
            }
            string path = Path.GetFullPath(uri.Uri.LocalPath);
            if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("An upstream fixture escapes its root directory.");
            }
            return path;
        }
        var categories = new[] { "aggregates", "bind", "bindings", "cast", "construct", "exists", "functions", "grouping", "json-res", "negation", "project-expression", "property-path", "subquery" }
            .Select(category => "sparql11/" + category)
            .Concat(new[]
            {
                "basic", "ask", "bound", "boolean-effective-value", "expr-builtin", "expr-ops", "optional", "optional-filter", "distinct", "regex", "triple-match",
                "algebra", "bnode-coreference", "cast", "construct", "dataset", "expr-equals", "graph", "i18n", "open-world", "reduced", "solution-seq", "sort", "type-promotion"
            }
                .Select(category => "sparql10/" + category));
        foreach (string category in categories)
        {
            string manifestPath = Path.Combine(root, "sparql", category, "manifest.ttl");
            using var manifest = new VDS.RDF.Graph();
            FileLoader.Load(manifest, manifestPath);
            IEnumerable<INode> Objects(INode subject, string predicate) => manifest.GetTriplesWithSubjectPredicate(subject,
                manifest.CreateUriNode(new Uri(predicate))).Select(triple => triple.Object);
            // Only run entries in the manifest list. Some files retain obsolete,
            // contradictory alternatives as RDF resources outside that list.
            var entries = new List<INode>();
            foreach (var list in manifest.GetTriplesWithPredicate(manifest.CreateUriNode(new Uri(mf + "entries"))))
            {
                INode current = list.Object;
                var visited = new HashSet<INode>();
                while (current is not IUriNode end || end.Uri.AbsoluteUri != RdfSpecsHelper.RdfListNil)
                {
                    Assert.IsTrue(visited.Add(current), "The upstream manifest list contains a cycle.");
                    entries.Add(Objects(current, RdfSpecsHelper.RdfListFirst).Single());
                    current = Objects(current, RdfSpecsHelper.RdfListRest).Single();
                }
            }
            var cases = entries.Where(entry => Objects(entry, RdfSpecsHelper.RdfType)
                .Any(type => type is IUriNode uri && uri.Uri.AbsoluteUri == mf + "QueryEvaluationTest")).ToArray();
            foreach (var test in cases)
            {
                string name = category + "/" + test;
                // These later additions to the repository's sparql10 directory
                // require XPath 3's q flag. SPARQL 1.1 normatively references
                // XPath 2.0 (2010), whose flags are i/m/s/x. Report them explicitly
                // rather than count them as passing SPARQL 1.1 fixtures.
                // https://www.w3.org/TR/sparql11-query/#func-regex
                string? queryFile = null;
                try
                {
                    var action = Objects(test, mf + "action").Single();
                    queryFile = Local(Objects(action, qt + "query").Single());
                    if (category == "sparql10/regex" && test is IUriNode regexCase &&
                        regexCase.Uri.Fragment is "#regex-no-metacharacters" or "#regex-no-metacharacters-case-insensitive")
                    {
                        string querySha256 = VerifyXPath3RegexFixture(regexCase.Uri.Fragment, File.ReadAllBytes(queryFile));
                        outsideProfile++;
                        results.Add(new
                        {
                            name,
                            queryFile,
                            querySha256,
                            outsideProfile = true,
                            reason = "Requires XPath 3 q flag; outside the SPARQL 1.1 XPath 2.0 profile."
                        });
                        continue;
                    }
                    string resultFile = Local(Objects(test, mf + "result").Single());
                    var quads = new List<RdfQuad>();
                    void Load(INode file, RdfTerm? graph)
                    {
                        string path = Local(file);
                        string scope = path;
                        var namespaces = new Dictionary<string, string>(StringComparer.Ordinal);
                        RdfTerm Term(INode node)
                        {
                            RdfTerm term = RdfSyntax.FromNode(node, scope);
                            string Restore(string value)
                            {
                                foreach (var pair in namespaces)
                                {
                                    if (value.StartsWith(pair.Key, StringComparison.Ordinal))
                                    {
                                        return pair.Value + value[pair.Key.Length..];
                                    }
                                }
                                return value;
                            }
                            return term switch
                            {
                                RdfIri iri => new RdfIri(Restore(iri.Value)),
                                RdfLiteral { Datatype: { } datatype } literal => new RdfLiteral(literal.LexicalForm, Restore(datatype), literal.Language),
                                _ => term
                            };
                        }
                        void Add(VDS.RDF.IGraph data, RdfTerm? name) => quads.AddRange(data.Triples.Select(triple =>
                            new RdfQuad(Term(triple.Subject), (RdfIri)Term(triple.Predicate), Term(triple.Object), name)));
                        if (Path.GetExtension(path) == ".trig")
                        {
                            using var store = new TripleStore();
                            new TriGParser().Load(store, path);
                            foreach (var data in store.Graphs)
                            {
                                Add(data, data.Name is IUriNode name ? new RdfIri(name.Uri.OriginalString) : graph);
                            }
                        }
                        else
                        {
                            using var data = new VDS.RDF.Graph();
                            if (Path.GetExtension(path) == ".ttl")
                            {
                                // Preserve lexical IRIs that System.Uri would normalize, including
                                // namespace declarations. Quoted literal contents are untouched.
                                const string lexical = """"(?s)"""(?:\\.|[^\\])*?"""|'''(?:\\.|[^\\])*?'''|"(?:\\.|[^"\\])*"|'(?:\\.|[^'\\])*'|<(?<iri>[^<>"{}\s]*)>"""";
                                string source = Regex.Replace(File.ReadAllText(path), lexical, match =>
                                {
                                    string iri = match.Groups["iri"].Value;
                                    if (!match.Groups["iri"].Success || !Uri.TryCreate(iri, UriKind.Absolute, out var uri) || uri.AbsoluteUri == iri)
                                    {
                                        return match.Value;
                                    }
                                    string? surrogate = namespaces.FirstOrDefault(pair => pair.Value == iri).Key;
                                    if (surrogate is null)
                                    {
                                        surrogate = "urn:entityforge:test-namespace:" + namespaces.Count + ":";
                                        namespaces.Add(surrogate, iri);
                                    }
                                    return "<" + surrogate + ">";
                                });
                                data.BaseUri = new Uri(path);
                                new TurtleParser().Load(data, new StringReader(source));
                            }
                            else
                            {
                                FileLoader.Load(data, path);
                            }
                            Add(data, graph);
                        }
                    }
                    foreach (var file in Objects(action, qt + "data"))
                    {
                        Load(file, null);
                    }
                    foreach (var file in Objects(action, qt + "graphData"))
                    {
                        if (file is IUriNode iri)
                        {
                            Load(file, new RdfIri(iri.Uri.AbsoluteUri));
                        }
                        else
                        {
                            var resource = Objects(file, qt + "graph").Single();
                            var label = Objects(file, "http://www.w3.org/2000/01/rdf-schema#label").Single();
                            string graph = label is IUriNode graphIri ? graphIri.Uri.AbsoluteUri : ((ILiteralNode)label).Value;
                            Load(resource, new RdfIri(graph));
                        }
                    }
                    string query = "BASE <" + new Uri(queryFile).AbsoluteUri + ">\n" + File.ReadAllText(queryFile);
                    var actual = RdfSparql.ExecuteRelation(quads, query, default);
                    string comparison;
                    if (actual is RdfSparqlGraphResult graphResult)
                    {
                        comparison = "graph-isomorphism";
                        using var expected = new VDS.RDF.Graph();
                        FileLoader.Load(expected, resultFile);
                        using var output = new VDS.RDF.Graph();
                        output.Assert(graphResult.Quads.Select(quad => new Triple(RdfSyntax.ToNode(output, quad.Subject),
                            RdfSyntax.ToNode(output, quad.Predicate), RdfSyntax.ToNode(output, quad.Object))));
                        Assert.IsTrue(expected.Equals(output), "Graph differs from the upstream result.");
                    }
                    else
                    {
                        using var expected = new SparqlResultSet();
                        // These pinned fixtures have a total observable output order:
                        // distinct numeric/IRI/string keys, or identical projected rows
                        // for tied keys (subquery11). Do not infer outer ordering from
                        // an ORDER BY inside a subquery or enforce arbitrary tie order.
                        bool ordered = test is IUriNode orderedCase && (category, orderedCase.Uri.Fragment) switch
                        {
                            ("sparql11/aggregates", "#agg08b") => true,
                            ("sparql11/bindings", "#values-order-by" or "#values-offset-limit" or "#values-group-order-by") => true,
                            ("sparql11/negation", "#full-minuend" or "#partial-minuend") => true,
                            ("sparql11/project-expression", "#projexp04") => true,
                            ("sparql11/property-path", "#pp14" or "#pp16" or "#pp37") => true,
                            ("sparql11/subquery", "#subquery11" or "#subquery13") => true,
                            _ => false
                        };
                        if (Path.GetExtension(resultFile) == ".srx")
                        {
                            new SparqlXmlParser().Load(expected, resultFile);
                        }
                        else if (Path.GetExtension(resultFile) == ".srj")
                        {
                            new SparqlJsonParser().Load(expected, resultFile);
                        }
                        else
                        {
                            using var resultGraph = new VDS.RDF.Graph();
                            FileLoader.Load(resultGraph, resultFile);
                            const string rs = "http://www.w3.org/2001/sw/DataAccess/tests/result-set#";
                            var indexed = resultGraph.GetTriplesWithPredicate(resultGraph.CreateUriNode(new Uri(rs + "index"))).ToArray();
                            if (indexed.Length > 0)
                            {
                                ordered = true;
                                var orderedHandler = new VDS.RDF.Parsing.Handlers.ResultSetHandler(expected);
                                orderedHandler.StartResults();
                                foreach (var variable in resultGraph.GetTriplesWithPredicate(resultGraph.CreateUriNode(new Uri(rs + "resultVariable"))))
                                {
                                    orderedHandler.HandleVariable(((ILiteralNode)variable.Object).Value);
                                }
                                Assert.AreEqual(resultGraph.GetTriplesWithPredicate(resultGraph.CreateUriNode(new Uri(rs + "solution"))).Count(), indexed.Length);
                                foreach (var index in indexed.OrderBy(triple => Int32.Parse(((ILiteralNode)triple.Object).Value, CultureInfo.InvariantCulture)))
                                {
                                    var cells = resultGraph.GetTriplesWithSubjectPredicate(index.Subject, resultGraph.CreateUriNode(new Uri(rs + "binding")))
                                        .Select(binding => new KeyValuePair<string, INode>(
                                            ((ILiteralNode)resultGraph.GetTriplesWithSubjectPredicate(binding.Object, resultGraph.CreateUriNode(new Uri(rs + "variable"))).Single().Object).Value,
                                            resultGraph.GetTriplesWithSubjectPredicate(binding.Object, resultGraph.CreateUriNode(new Uri(rs + "value"))).Single().Object));
                                    orderedHandler.HandleResult(new SparqlResult(cells));
                                }
                                orderedHandler.EndResults(true);
                            }
                            else if (!resultGraph.GetTriplesWithPredicate(resultGraph.CreateUriNode(new Uri(rs + "resultVariable"))).Any() &&
                                !resultGraph.GetTriplesWithPredicate(resultGraph.CreateUriNode(new Uri(rs + "boolean"))).Any())
                            {
                                // dotNetRDF's RDF result parser rejects valid zero-column SELECT results.
                                var emptyHandler = new VDS.RDF.Parsing.Handlers.ResultSetHandler(expected);
                                emptyHandler.StartResults();
                                foreach (var solution in resultGraph.GetTriplesWithPredicate(resultGraph.CreateUriNode(new Uri(rs + "solution"))))
                                {
                                    emptyHandler.HandleResult(new SparqlResult());
                                }
                                emptyHandler.EndResults(true);
                            }
                            else
                            {
                                new SparqlRdfParser().Load(expected, resultFile);
                            }
                        }
                        using var nodeFactory = new VDS.RDF.Graph();
                        bool preserveLexical = Objects(test, mf + "requires").Any(value => value.ToString() == mf + "NoCanonicalizationOfNumerics");
                        INode? Normalize(INode? node)
                        {
                            if (preserveLexical || node is not ILiteralNode literal || literal.DataType is null)
                            {
                                return node;
                            }
                            string type = literal.DataType.AbsoluteUri;
                            const string xsd = "http://www.w3.org/2001/XMLSchema#";
                            string? lexical = type switch
                            {
                                xsd + "integer" when BigInteger.TryParse(literal.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var integer) => integer.ToString(CultureInfo.InvariantCulture),
                                xsd + "decimal" => NormalizeUpstreamDecimal(literal.Value),
                                xsd + "double" => NormalizeUpstreamFloating(literal.Value, false),
                                xsd + "float" => NormalizeUpstreamFloating(literal.Value, true),
                                _ => null
                            };
                            return lexical is null ? node : nodeFactory.CreateLiteralNode(lexical, literal.DataType);
                        }
                        foreach (var row in expected)
                        {
                            foreach (string variable in expected.Variables.ToArray())
                            {
                                row.SetValue(variable, Normalize(row.HasBoundValue(variable) ? row[variable] : null)!);
                            }
                        }
                        using var output = new SparqlResultSet();
                        var handler = new VDS.RDF.Parsing.Handlers.ResultSetHandler(output);
                        handler.StartResults();
                        if (actual is RdfSparqlAskResult ask)
                        {
                            handler.HandleBooleanResult(ask.Value);
                        }
                        else if (actual is RdfSparqlSelectResult select)
                        {
                            var variables = RdfSparql.ResultVariables(query);
                            foreach (string variable in variables)
                            {
                                handler.HandleVariable(variable);
                            }
                            foreach (var row in select.Bindings)
                            {
                                handler.HandleResult(new SparqlResult(variables.Select(variable =>
                                    new KeyValuePair<string, INode>(variable, row.TryGetValue(variable, out var value)
                                        ? Normalize(RdfSyntax.ToNode(nodeFactory, value))! : null!))));
                            }
                        }
                        handler.EndResults(true);
                        bool equal;
                        if (actual is RdfSparqlAskResult)
                        {
                            comparison = "boolean";
                            equal = expected.Result == output.Result;
                        }
                        else
                        {
                            comparison = ordered ? "ordered-result-isomorphism" : "result-bag-isomorphism";
                            // Encode the entire result bag as a graph: one fresh row node per
                            // occurrence, sharing value blank nodes across all rows. This tests
                            // blank-node isomorphism without treating their labels as identities.
                            VDS.RDF.Graph ResultGraph(SparqlResultSet set)
                            {
                                var graph = new VDS.RDF.Graph();
                                var blanks = new Dictionary<INode, INode>();
                                int ordinal = 0;
                                foreach (var row in set)
                                {
                                    var rowNode = graph.CreateBlankNode();
                                    graph.Assert(rowNode, graph.CreateUriNode(new Uri("urn:result:row")), graph.CreateLiteralNode("row"));
                                    if (ordered)
                                    {
                                        graph.Assert(rowNode, graph.CreateUriNode(new Uri("urn:result:ordinal")),
                                            graph.CreateLiteralNode((ordinal++).ToString(CultureInfo.InvariantCulture)));
                                    }
                                    foreach (string variable in set.Variables)
                                    {
                                        if (!row.HasBoundValue(variable))
                                        {
                                            continue;
                                        }
                                        INode value = row[variable];
                                        if (value is IBlankNode)
                                        {
                                            if (!blanks.TryGetValue(value, out var mapped))
                                            {
                                                blanks[value] = mapped = graph.CreateBlankNode();
                                            }
                                            value = mapped;
                                        }
                                        graph.Assert(rowNode, graph.CreateUriNode(new Uri("urn:result:column:" + Uri.EscapeDataString(variable))), value);
                                    }
                                }
                                return graph;
                            }
                            using var expectedGraph = ResultGraph(expected);
                            using var actualGraph = ResultGraph(output);
                            equal = expected.Variables.ToHashSet(StringComparer.Ordinal).SetEquals(output.Variables) && expectedGraph.Equals(actualGraph);
                            if (Objects(test, mf + "resultCardinality").Any(value => value.ToString() == mf + "LaxCardinality") &&
                                expected.Concat(output).All(row => row.Variables.All(variable => !row.HasBoundValue(variable) || row[variable] is not IBlankNode)))
                            {
                                // REDUCED permits any multiplicity from one to the original count.
                                comparison = "reduced-multiplicity-bounds";
                                equal = expected.Variables.ToHashSet(StringComparer.Ordinal).SetEquals(output.Variables) &&
                                    expected.All(wanted => output.Any(row => wanted.Equals(row))) &&
                                    output.GroupBy(row => row).All(group => group.Count() <= expected.Count(row => row.Equals(group.Key)));
                            }
                        }
                        Assert.IsTrue(equal, "Bindings differ. Expected: " + String.Join(";", expected) + " Actual: " + String.Join(";", output) + " Source sample: " + String.Join(";", quads.Take(3)));
                    }
                    passed++;
                    results.Add(new
                    {
                        name,
                        queryFile,
                        resultFile,
                        comparison,
                        passed = true
                    });
                }
                catch (Exception exception)
                {
                    failed++;
                    results.Add(new
                    {
                        name,
                        queryFile,
                        passed = false,
                        error = exception.GetType().Name + ": " + exception.Message
                    });
                }
            }
        }
        string report = JsonSerializer.Serialize(new
        {
            passed,
            failed,
            outsideProfile,
            cases = results
        }, new JsonSerializerOptions { WriteIndented = true });
        if (Environment.GetEnvironmentVariable("ENTITYFORGE_W3C_REPORT") is { Length: > 0 } reportFile)
        {
            File.WriteAllText(reportFile, report);
        }
        Assert.AreEqual(0, failed, $"Upstream W3C evaluation: {passed} passed, {failed} failed. " + report);
    }

    private static string? NormalizeUpstreamFloating(string lexical, bool single)
    {
        string value = lexical.Trim(' ', '\t', '\r', '\n');
        if (value is "INF" or "-INF" or "NaN")
        {
            return value;
        }
        if (!Regex.IsMatch(value, @"\A[+-]?(?:[0-9]+(?:\.[0-9]*)?|\.[0-9]+)(?:[eE][+-]?[0-9]+)?\z",
            RegexOptions.CultureInvariant))
        {
            return null;
        }
        double number;
        string normalized;
        if (single)
        {
            if (!Single.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out float parsed))
            {
                return null;
            }
            number = parsed;
            normalized = parsed.ToString("R", CultureInfo.InvariantCulture);
        }
        else
        {
            if (!Double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out number))
            {
                return null;
            }
            normalized = number.ToString("R", CultureInfo.InvariantCulture);
        }
        return Double.IsPositiveInfinity(number) ? "INF" : Double.IsNegativeInfinity(number) ? "-INF" : normalized;
    }

    private static string? NormalizeUpstreamDecimal(string lexical)
    {
        // Compare decimal values without passing through a finite-precision
        // runtime number. Exponent notation is not an xsd:decimal lexical form.
        string value = lexical.Trim(' ', '\t', '\r', '\n');
        if (!Regex.IsMatch(value, @"\A[+-]?(?:[0-9]+(?:\.[0-9]*)?|\.[0-9]+)\z", RegexOptions.CultureInvariant))
        {
            return null;
        }
        bool negative = value[0] == '-';
        if (value[0] is '+' or '-')
        {
            value = value[1..];
        }
        int point = value.IndexOf('.');
        string integer = (point < 0 ? value : value[..point]).TrimStart('0');
        string fraction = point < 0 ? "" : value[(point + 1)..].TrimEnd('0');
        if (integer.Length == 0)
        {
            integer = "0";
        }
        if (fraction.Length == 0)
        {
            fraction = "0";
        }
        return (negative && (integer != "0" || fraction != "0") ? "-" : "") + integer + "." + fraction;
    }
}
