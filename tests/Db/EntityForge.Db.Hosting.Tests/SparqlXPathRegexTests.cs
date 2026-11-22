using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using EntityForge.Db.Rdf;
using EntityForge.Db.Rdf.Contracts;

namespace EntityForge.Db.Hosting.Tests;

public sealed partial class RdfDatabaseEngineTests
{
    [TestMethod]
    [DataRow("matches", "REGEX", "ENTITYFORGE_QT3_MATCHES", 2, 3)]
    [DataRow("replace", "REPLACE", "ENTITYFORGE_QT3_REPLACE", 3, 4)]
    public async Task Sparql_regex_matches_upstream_XPath20_literal_cases(string function, string sparql, string variable, int minimum, int maximum)
    {
        string? path = Environment.GetEnvironmentVariable(variable);
        if (String.IsNullOrEmpty(path))
        {
            Assert.Inconclusive("Set " + variable + " to a pinned W3C qt3tests fn/" + function + ".xml file.");
        }
        XNamespace ns = "http://www.w3.org/2010/09/qt-fots-catalog";
        var suite = XDocument.Load(path, LoadOptions.PreserveWhitespace).Root!;
        var results = new List<object>();
        var failures = new List<string>();
        int passed = 0, excluded = 0;
        await using var database = await OpenAsync(CreateDirectory());
        foreach (var test in suite.Elements(ns + "test-case"))
        {
            string name = (string)test.Attribute("name")!;
            var assertion = test.Element(ns + "result")?.Elements().SingleOrDefault();
            // Do not silently run newer-language, collation, XML-version or
            // other dependent cases under SPARQL's XPath 2.0 contract.
            bool dependencies = suite.Elements(ns + "dependency").Concat(test.Elements(ns + "dependency"))
                .Any(dependency => !XPath20Dependency(dependency));
            string? expression = (string?)test.Element(ns + "test");
            string?[] expected = [];
            string[] arguments = [];
            string? exclusion = dependencies || test.Attribute("covers-30") is not null
                ? "dependency outside XPath 2.0 / XML Schema 1.0" :
                assertion is null || !TryXPathRegexExpectations(assertion, out expected) ? "assertion outside adapter" :
                expression is null || !TryXPathRegexLiterals(expression, function, minimum, maximum, out arguments) ? "expression outside constant-argument adapter" : null;
            if (exclusion is not null)
            {
                excluded++;
                results.Add(new
                {
                    name,
                    disposition = exclusion
                });
                continue;
            }
            string query = "SELECT (" + sparql + "(" + String.Join(',', arguments.Select(value => JsonSerializer.Serialize(value))) + ") AS ?value) {}";
            var row = RdfSparql.Execute(database, query).Single();
            string? actual = row.TryGetValue("value", out var term) && term is RdfLiteral literal ? literal.LexicalForm : null;
            bool matches = expected.Contains(actual, StringComparer.Ordinal);
            results.Add(new
            {
                name,
                query,
                expected,
                actual,
                passed = matches
            });
            if (matches)
            {
                passed++;
            }
            else
            {
                failures.Add(name + ": expected " + JsonSerializer.Serialize(expected) + ", got " + actual);
            }
        }
        if (Environment.GetEnvironmentVariable("ENTITYFORGE_QT3_REPORT") is { Length: > 0 } report)
        {
            File.WriteAllText(report + "." + function + ".json", JsonSerializer.Serialize(new
            {
                passed,
                failed = failures.Count,
                excluded,
                results
            }, new JsonSerializerOptions { WriteIndented = true }));
        }
        Assert.IsTrue(passed > 0, "The fixture adapter must execute some cases.");
        Assert.IsEmpty(failures, String.Join(Environment.NewLine, failures));
    }

    private static bool TryXPathRegexExpectations(XElement assertion, out string?[] expected)
    {
        // Some upstream tests explicitly permit either a result or a regex
        // error. Accept only a bounded list of the same literal assertions.
        var alternatives = assertion.Name.LocalName == "any-of" ? assertion.Elements().ToArray() : [assertion];
        expected = [];
        if (alternatives.Length is < 1 or > 16)
        {
            return false;
        }
        var values = new List<string?>();
        foreach (var alternative in alternatives)
        {
            if (!TryXPathRegexExpectation(alternative, out string? value))
            {
                return false;
            }
            values.Add(value);
        }
        expected = values.ToArray();
        return true;
    }

    private static bool TryXPathRegexExpectation(XElement assertion, out string? expected)
    {
        expected = null;
        switch (assertion.Name.LocalName)
        {
            case "assert-true":
                expected = "true";
                return true;
            case "assert-false":
                expected = "false";
                return true;
            case "assert-string-value":
                expected = assertion.Value;
                return true;
            case "error":
                return (string?)assertion.Attribute("code") is "FORX0001" or "FORX0002" or "FORX0003" or "FORX0004";
            case "assert-eq":
                // Only a literal expected value is translated. This does not
                // evaluate arbitrary XPath assertion expressions.
                if (TryXPathRegexLiterals("expected(" + assertion.Value + ")", "expected", 1, 1, out var values))
                {
                    expected = values[0];
                    return true;
                }
                return false;
            default:
                return false;
        }
    }

    private static bool XPath20Dependency(XElement dependency)
    {
        if ((string?)dependency.Attribute("satisfied") == "false")
        {
            return false;
        }
        string? value = (string?)dependency.Attribute("value");
        return (string?)dependency.Attribute("type") switch
        {
            "spec" => value?.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Any(spec => spec is "XP20" or "XP20+" or "XQ10" or "XQ10+") == true,
            "xsd-version" => value == "1.0",
            _ => false
        };
    }

    private static bool TryXPathRegexLiterals(string expression, string function, int minimum, int maximum, out string[] arguments, int depth = 0)
    {
        arguments = [];
        if (depth > 16)
        {
            return false;
        }
        string text = expression.Trim();
        if (text.StartsWith("fn:", StringComparison.Ordinal))
        {
            text = text[3..];
        }
        if (!text.StartsWith(function + "(", StringComparison.Ordinal) || !text.EndsWith(')'))
        {
            return false;
        }
        text = text[(function.Length + 1)..^1];
        var values = new List<string>();
        int offset = 0;
        while (offset < text.Length)
        {
            while (offset < text.Length && Char.IsWhiteSpace(text[offset]))
            {
                offset++;
            }
            bool stringConstructor = text.AsSpan(offset).StartsWith("xs:string(", StringComparison.Ordinal);
            if (stringConstructor || text.AsSpan(offset).StartsWith("concat(", StringComparison.Ordinal) ||
                text.AsSpan(offset).StartsWith("fn:concat(", StringComparison.Ordinal))
            {
                int end = offset, nesting = 0;
                char delimiter = '\0';
                for (; end < text.Length; end++)
                {
                    char current = text[end];
                    if (delimiter != '\0')
                    {
                        if (current == delimiter)
                        {
                            if (end + 1 < text.Length && text[end + 1] == delimiter)
                            {
                                end++;
                            }
                            else
                            {
                                delimiter = '\0';
                            }
                        }
                    }
                    else if (current is '\'' or '"')
                    {
                        delimiter = current;
                    }
                    else if (current == '(')
                    {
                        nesting++;
                    }
                    else if (current == ')' && --nesting == 0)
                    {
                        break;
                    }
                }
                // xs:string of an already constant string is an identity
                // operation, not permission to evaluate arbitrary XPath casts.
                if (end == text.Length || !TryXPathRegexLiterals(text[offset..(end + 1)],
                    stringConstructor ? "xs:string" : "concat", stringConstructor ? 1 : 2,
                    stringConstructor ? 1 : 32, out var parts, depth + 1))
                {
                    return false;
                }
                values.Add(String.Concat(parts));
                offset = end + 1;
                while (offset < text.Length && Char.IsWhiteSpace(text[offset]))
                {
                    offset++;
                }
                if (offset == text.Length)
                {
                    break;
                }
                if (text[offset++] != ',' || offset == text.Length)
                {
                    return false;
                }
                continue;
            }
            const string codepointCall = "codepoints-to-string(";
            if (text.AsSpan(offset).StartsWith(codepointCall, StringComparison.Ordinal))
            {
                int end = text.IndexOf(')', offset + codepointCall.Length);
                if (end < 0 || !Int32.TryParse(text.AsSpan(offset + codepointCall.Length, end - offset - codepointCall.Length),
                    System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int scalar) ||
                    !Rune.IsValid(scalar) || scalar is < 32 and not (9 or 10 or 13) or 0xFFFE or 0xFFFF)
                {
                    return false;
                }
                values.Add(Char.ConvertFromUtf32(scalar));
                offset = end + 1;
                while (offset < text.Length && Char.IsWhiteSpace(text[offset]))
                {
                    offset++;
                }
                if (offset == text.Length)
                {
                    break;
                }
                if (text[offset++] != ',' || offset == text.Length)
                {
                    return false;
                }
                continue;
            }
            if (offset == text.Length || text[offset] is not ('\'' or '"'))
            {
                return false;
            }
            char quote = text[offset++];
            var literal = new StringBuilder();
            bool closed = false;
            while (offset < text.Length)
            {
                char value = text[offset++];
                if (value != quote)
                {
                    literal.Append(value);
                    continue;
                }
                if (offset < text.Length && text[offset] == quote)
                {
                    literal.Append(quote);
                    offset++;
                    continue;
                }
                closed = true;
                break;
            }
            if (!closed)
            {
                return false;
            }
            // XQuery string literals use XML entity references, not backslash
            // escapes. Decode that layer before JSON/SPARQL quoting.
            try
            {
                values.Add(XElement.Parse("<v>" + literal.ToString().Replace("<", "&lt;", StringComparison.Ordinal) + "</v>", LoadOptions.PreserveWhitespace).Value);
            }
            catch (System.Xml.XmlException)
            {
                return false;
            }
            while (offset < text.Length && Char.IsWhiteSpace(text[offset]))
            {
                offset++;
            }
            if (offset == text.Length)
            {
                break;
            }
            if (text[offset++] != ',' || offset == text.Length)
            {
                return false;
            }
        }
        if (values.Count < minimum || values.Count > maximum)
        {
            return false;
        }
        arguments = values.ToArray();
        return true;
    }
}
