using EntityForge.Db.Rdf;
using EntityForge.Db.Rdf.Contracts;

namespace EntityForge.Db.Hosting.Tests;

public sealed partial class RdfDatabaseEngineTests
{
    [TestMethod]
    [DataRow("[0-9-.]", false)]
    [DataRow("[a-z-0-9]", false)]
    [DataRow("[--0]", false)]
    [DataRow("[!--]", false)]
    [DataRow("[-a]", true)]
    [DataRow("[a-]", true)]
    [DataRow("[--]", true)]
    [DataRow(@"[a-z\-0-9]", true)]
    [DataRow(@"[\--0]", true)]
    [DataRow(@"[!-\-]", true)]
    [DataRow("[-a-z-[b]]", true)]
    [DataRow("[a--[b]]", true)]
    [DataRow("[a-z--[b]]", true)]
    [DataRow("[--[b]]", true)]
    public async Task Sparql_regex_hyphens_follow_XMLSchema10_character_ranges(string pattern, bool valid)
    {
        await using var database = await OpenAsync(CreateDirectory());
        string quoted = System.Text.Json.JsonSerializer.Serialize(pattern);
        foreach (string flags in new[] { "", "i" })
        {
            var row = RdfSparql.Execute(database, "SELECT (REGEX('-'," + quoted + ",'" + flags + "') AS ?matched) " +
                "(REPLACE('-'," + quoted + ",'X','" + flags + "') AS ?replaced) {}").Single();
            Assert.AreEqual(valid, row.ContainsKey("matched"), pattern);
            Assert.AreEqual(valid, row.ContainsKey("replaced"), pattern);
            if (valid)
            {
                Assert.AreEqual("true", ((RdfLiteral)row["matched"]).LexicalForm, pattern);
                Assert.AreEqual("X", ((RdfLiteral)row["replaced"]).LexicalForm, pattern);
            }
        }
    }

    [TestMethod]
    public async Task Sparql_backreference_work_exhaustion_fails_the_query()
    {
        await using var database = await OpenAsync(CreateDirectory());
        string pattern = "^(a|aa)+\\1b$";
        string input = new('a', 40);
        string query = "SELECT (REGEX(" + System.Text.Json.JsonSerializer.Serialize(input) + "," +
            System.Text.Json.JsonSerializer.Serialize(pattern) + ",'i') AS ?value) {}";
        Assert.ThrowsExactly<RdfSparqlWorkLimitException>(() => RdfSparql.Execute(database, query));
    }

    [TestMethod]
    [DataRow(@"^(a*)\1$")]
    [DataRow(@"^(a*?)\1$")]
    [DataRow(@"^(a|ab)\1$")]
    [DataRow(@"^(ab|a)\1$")]
    [DataRow(@"^(a|b)+\1$")]
    [DataRow(@"^(a|b)*?\1$")]
    [DataRow(@"^(a?){2,3}\1$")]
    [DataRow(@"^((a|b)*)\1$")]
    [DataRow(@"^((a|b)*)\2$")]
    [DataRow(@"^((a)?b)*\2$")]
    [DataRow(@"^(a)?b\1$")]
    [DataRow(@"^(a*)(b*)\2\1$")]
    [DataRow(@"^([a-b]+)\1$")]
    [DataRow(@"^([^b]*)\1$")]
    [DataRow(@"^(a|)*\1$")]
    [DataRow(@"^(|a)*\1$")]
    [DataRow(@"^(a?)*\1$")]
    [DataRow(@"^(a?)+\1$")]
    [DataRow(@"^(a?)*?\1$")]
    [DataRow(@"^((a?)*)\2\1$")]
    [DataRow(@"^((a)|(b))*\2\3$")]
    [DataRow(@"^(a|b){0,3}\1$")]
    [DataRow(@"^(a|b){1,3}?\1$")]
    [DataRow(@"^(a|(b))*\2$")]
    public async Task Sparql_backreference_backtracking_matches_independent_ASCII_reference(string pattern)
    {
        await using var database = await OpenAsync(CreateDirectory());
        string referencePattern = System.Text.RegularExpressions.Regex.Replace(pattern, @"\\([1-9])", match => "(?(" + match.Groups[1].Value + ")\\" + match.Groups[1].Value + "|)");
        var reference = new System.Text.RegularExpressions.Regex(referencePattern, System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);
        string patternLiteral = System.Text.Json.JsonSerializer.Serialize(pattern);
        for (int length = 0; length <= 5; length++)
        {
            for (int bits = 0; bits < 1 << length; bits++)
            {
                string input = new(Enumerable.Range(0, length).Select(index => (bits & 1 << index) == 0 ? index % 2 == 0 ? 'a' : 'A' : index % 2 == 0 ? 'b' : 'B').ToArray());
                string literal = System.Text.Json.JsonSerializer.Serialize(input);
                var row = RdfSparql.Execute(database, $"SELECT (REGEX({literal},{patternLiteral},'i') AS ?value) {{}}").Single();
                Assert.AreEqual(reference.IsMatch(input) ? "true" : "false", ((RdfLiteral)row["value"]).LexicalForm, pattern + " / " + input);
            }
        }
    }

    [TestMethod]
    [DataRow("""REGEX('𐐀𐐨','^(.)\\1$','i')""", "true")]
    [DataRow("""REGEX('𐐨𐐀','^(.)\\1$','i')""", "true")]
    [DataRow("""REGEX('𐐀𐐩','^(.)\\1$','i')""", "false")]
    [DataRow("""REGEX('𐐀𐐨','^(.)\\1$')""", "false")]
    [DataRow("""REGEX('Σς','^(.)\\1$','i')""", "true")]
    [DataRow("""REGEX('ﬅﬆ','^(.)\\1$','i')""", "true")]
    [DataRow("""REGEX('ﬆﬅ','^(.)\\1$','i')""", "true")]
    [DataRow("""REGEX('İi','^(.)\\1$','i')""", "false")]
    [DataRow("""REPLACE('ﬅﬆ','(.)\\1','[$1]','i')""", "[ﬅ]")]
    [DataRow("""REGEX('ıI','^(.)\\1$','i')""", "true")]
    [DataRow("""REGEX('ſS','^(.)\\1$','i')""", "true")]
    [DataRow("""REGEX('😀😀','^(.)\\1$','i')""", "true")]
    [DataRow("""REGEX('a𐐀A𐐨','^(.+)\\1$','i')""", "true")]
    [DataRow("""REGEX('a𐐀A𐐨','^(.+?)\\1$','i')""", "true")]
    [DataRow("""REGEX('𐐀𐐨','^([\\p{Lu}])\\1$','i')""", "true")]
    [DataRow("""REGEX('𐐨𐐀','^([\\p{Lu}])\\1$','i')""", "false")]
    [DataRow("""REGEX('bB','^([a-z-[aeiou]])\\1$','i')""", "true")]
    [DataRow("""REGEX('aA','^([a-z-[aeiou]])\\1$','i')""", "false")]
    [DataRow("""REGEX('B','^(a)?b\\1$','i')""", "true")]
    [DataRow("""REGEX('aBa','^(a)?b\\1$','i')""", "true")]
    [DataRow("""REGEX('abcABC','^(a|abc)\\1$','i')""", "true")]
    [DataRow("""REGEX('abcABC','^(abc|a)\\1$','i')""", "true")]
    [DataRow("""REGEX('aB','^(a|(b))\\2$','i')""", "false")]
    [DataRow("""REGEX('AbB','^(a)(b)\\2$','i')""", "true")]
    [DataRow("""REGEX('aaA','^(a){2}\\1$','i')""", "true")]
    [DataRow("""REGEX('aA','^(a){1,3}?\\1$','i')""", "true")]
    [DataRow("""REGEX('','^(a?){3}\\1$','i')""", "true")]
    [DataRow("""REGEX('x\n𐐀𐐨\ny','^(.)\\1$','im')""", "true")]
    [DataRow("""REGEX('𐐀𐐨','^ ( . ) \\1 $','ix')""", "true")]
    [DataRow("""REPLACE('𐐀𐐨!𐐨𐐀','(.)\\1','[$1]','i')""", "[𐐀]![𐐨]")]
    [DataRow("""REPLACE('aaAA','(a+)\\1','[$1]','i')""", "[aa]")]
    [DataRow("""REPLACE('aaAA','(a+?)\\1','[$1]','i')""", "[a][A]")]
    [DataRow("""REPLACE('abcABC','(a|abc)\\1','$1-$0-$9','i')""", "abc-abcABC-")]
    public async Task Sparql_regex_backreferences_compare_Unicode_scalars_and_preserve_captures(string expression, string expected)
    {
        await using var database = await OpenAsync(CreateDirectory());
        var row = RdfSparql.Execute(database, $"SELECT ({expression} AS ?value) {{}}").Single();
        Assert.AreEqual(expected, ((RdfLiteral)row["value"]).LexicalForm);
    }

    [TestMethod]
    [DataRow("[[]")]
    [DataRow("[]]")]
    [DataRow("a]")]
    [DataRow("a}")]
    [DataRow("a{b}")]
    [DataRow("a{,2}")]
    [DataRow("a{")]
    [DataRow("(?:a)")]
    [DataRow(@"(?:a)(b)\1")]
    [DataRow("[a-z-[b]c]")]
    public async Task Sparql_regex_rejects_engine_extensions_to_XPath_character_classes(string pattern)
    {
        await using var database = await OpenAsync(CreateDirectory());
        string literal = System.Text.Json.JsonSerializer.Serialize(pattern);
        var row = RdfSparql.Execute(database, $"SELECT (REGEX('a[]{{}}',{literal}) AS ?matched) (REPLACE('a[]{{}}',{literal},'x') AS ?replaced) {{}}").Single();
        Assert.IsFalse(row.ContainsKey("matched"));
        Assert.IsFalse(row.ContainsKey("replaced"));
    }

    [TestMethod]
    [DataRow("REGEX('\\U00010D70','^\\U00010D50$','i')", "true")]
    [DataRow("REGEX('\\U00010D50','^\\U00010D70$','i')", "true")]
    [DataRow("REGEX('\\U00010D70','^[\\U00010D50-\\U00010D65]$','i')", "true")]
    [DataRow("REGEX('\\U00010D50','^\\\\p{Lu}$')", "true")]
    [DataRow("REGEX('\\U00010D70','^\\\\p{Ll}$')", "true")]
    [DataRow("REGEX('\\U00010D40','^\\\\d$')", "true")]
    [DataRow("REGEX('\\U00011380','^\\\\w$')", "true")]
    [DataRow("REGEX('\\U0002EBF0','^\\\\p{Lo}$')", "true")]
    [DataRow("REGEX('\\U00010D70','^\\\\p{Cn}$')", "false")]
    public async Task Sparql_regex_uses_pinned_Unicode_categories_and_simple_case(string expression, string expected)
    {
        await using var database = await OpenAsync(CreateDirectory());
        var row = RdfSparql.Execute(database, "SELECT (" + expression + " AS ?value) WHERE {}").Single();
        Assert.AreEqual(new RdfLiteral(expected, "http://www.w3.org/2001/XMLSchema#boolean"), row["value"]);
    }

    [TestMethod]
    public async Task Sparql_regex_class_nesting_fails_with_a_work_limit_instead_of_a_partial_answer()
    {
        await using var database = await OpenAsync(CreateDirectory());
        string pattern = "[a]";
        for (int index = 0; index < 16; index++)
        {
            pattern = "[a-" + pattern + "]";
        }
        Assert.ThrowsExactly<RdfSparqlWorkLimitException>(() => RdfSparql.Execute(database, $"SELECT (REGEX('a','{pattern}') AS ?value) {{}}"));
    }

    [TestMethod]
    [DataRow("REGEX('😀😀','^😀+$')", "true")]
    [DataRow("REGEX('abc','^\\\\p{IsBasicLatin}+$')", "true")]
    [DataRow("REGEX('ς','^Σ$','i')", "true")]
    [DataRow("REGEX('ẞ','^ß$','i')", "true")]
    [DataRow("REGEX('ß','^ẞ$','i')", "true")]
    [DataRow("REGEX('ẞ','^[ß]$','i')", "true")]
    [DataRow("REPLACE('ßẞ','ß','x','i')", "xx")]
    [DataRow("REGEX('σ','^ς$','i')", "true")]
    [DataRow("REGEX('ς','^[Σ]$','i')", "true")]
    [DataRow("REGEX('ς','^[^Σ]$','i')", "false")]
    [DataRow("REGEX('ſK','^[A-Z]+$','i')", "true")]
    [DataRow("REGEX('K','^[A-Z-[K]]$','i')", "false")]
    [DataRow("REGEX('K','^k$','i')", "true")]
    [DataRow("REGEX('k','^K$','i')", "true")]
    [DataRow("REGEX('ς','Σ','qi')", null)]
    [DataRow("REPLACE('Σσς','Σ','x','i')", "xxx")]
    [DataRow("REGEX('é','^\\\\p{IsBasicLatin}$')", "false")]
    [DataRow("REGEX('😀','^\\\\P{IsBasicLatin}$')", "true")]
    [DataRow("REGEX('😀','^[\\\\p{IsEmoticons}]$')", "true")]
    [DataRow("REGEX('😀','^[^\\\\p{IsEmoticons}]$')", "false")]
    [DataRow("REGEX('😀','^[\\\\p{IsEmoticons}-[😀]]$')", "false")]
    [DataRow("REGEX('😃','^[\\\\p{IsEmoticons}-[😀]]$')", "true")]
    [DataRow("REGEX('𐐀𐐨','^\\\\p{IsDeseret}{2}$')", "true")]
    [DataRow("REGEX('K','^\\\\p{IsBasicLatin}$','i')", "false")]
    [DataRow("REGEX('Ω','^\\\\p{IsGreek}$')", "true")]
    [DataRow("REGEX('Ω','^\\\\p{IsGreekandCoptic}$')", "true")]
    [DataRow("REGEX('\\U000F0000','^\\\\p{IsPrivateUse}$')", "true")]
    [DataRow("REGEX('😀','^\\\\p{IsHighSurrogates}$')", "false")]
    [DataRow("REGEX('😀','^\\\\P{IsHighSurrogates}$')", "true")]
    [DataRow("REPLACE('a😀😃b','(\\\\p{IsEmoticons}+)','[$1]')", "a[😀😃]b")]
    [DataRow("REGEX('b','^(a)?b\\\\1$')", "true")]
    [DataRow("REGEX('aba','^(a)?b\\\\1$')", "true")]
    [DataRow("REGEX('abb','^(a)?b\\\\1$')", "false")]
    [DataRow("REGEX('','^(a)?\\\\1$')", "true")]
    [DataRow("REGEX('aa1','^(a)\\\\11$')", "true")]
    [DataRow("REGEX('abcdefghijkll','^(a)(b)(c)(d)(e)(f)(g)(h)(i)(j)(k)(l)\\\\12$')", "true")]
    [DataRow("REGEX('abb','^(?:a)(b)\\\\1$')", null)]
    [DataRow("REGEX('abb','^(?:a)(b)\\\\1$','i')", null)]
    [DataRow("REPLACE('a','(?:a)','x')", null)]
    [DataRow("REPLACE('abb','(?:a)(b)\\\\1','x','i')", null)]
    [DataRow("REGEX('aA','^(a)\\\\1$','i')", "true")]
    [DataRow("REGEX('😀😀','^(😀)\\\\1$')", "true")]
    [DataRow("REPLACE('b','(a)?b\\\\1','x')", "x")]
    [DataRow("REPLACE('aba','(a)?b\\\\1','$1$1')", "aa")]
    [DataRow("REPLACE('a.b.c','.','$1','q')", null)]
    [DataRow("REPLACE('a b',' ','_','qx')", null)]
    [DataRow("REPLACE('𐐨𐐀','𐐀','$','qi')", null)]
    [DataRow("REPLACE('x[y','[','(','q')", null)]
    [DataRow("REPLACE('AbA','a','x','qi')", null)]
    [DataRow("REPLACE('abc','z','$1','q')", null)]
    [DataRow("REGEX('😀😀','^😀{2}$')", "true")]
    [DataRow("REGEX('😀😃','^(😀|😃)+$')", "true")]
    [DataRow("REPLACE('😀😀x','😀+','_')", "_x")]
    [DataRow("REGEX('_name:9','^\\\\i\\\\c*$')", "true")]
    [DataRow("REGEX('9name','^\\\\i\\\\c*$')", "false")]
    [DataRow("REGEX('é-name.9','^\\\\i\\\\c*$')", "true")]
    [DataRow("REGEX('😀','^[\\\\i]$')", "true")]
    [DataRow("REGEX('😀','^[\\\\I]$')", "false")]
    [DataRow("REGEX('9','^[\\\\i]$')", "false")]
    [DataRow("REGEX('9','^[\\\\c]$')", "true")]
    [DataRow("REGEX(' ','^\\\\C$')", "true")]
    [DataRow("REGEX(':','^[\\\\i-[:]]$')", "false")]
    [DataRow("REGEX('é','^[\\\\i-[:]]$')", "true")]
    [DataRow("REPLACE('é9','(\\\\i)','$1$1')", "éé9")]
    [DataRow("REGEX('𐐨','^𐐀$','i')", "true")]
    [DataRow("REGEX('𐐨𐐨','^𐐀+$','i')", "true")]
    [DataRow("REGEX('𐐨','^[𐐀-𐐧]$','i')", "true")]
    [DataRow("REGEX('𐐨','^[^𐐀]$','i')", "false")]
    [DataRow("REGEX('𐐨','^[𐐀-𐐧-[𐐀]]$','i')", "false")]
    [DataRow("REGEX('𐐩','^[𐐀-𐐧-[𐐀]]$','i')", "true")]
    [DataRow("REGEX('𐐨','𐐀','qi')", null)]
    [DataRow("REGEX('𐐨','^\\\\p{Lu}$','i')", "false")]
    [DataRow("REGEX('a','^[\\\\p{Lu}A]$','i')", "true")]
    [DataRow("REGEX('b','^[\\\\p{Lu}A]$','i')", "false")]
    [DataRow("REPLACE('𐐨𐐨','𐐀+','X','i')", "X")]
    [DataRow("REGEX('a','^\\\\p{Lu}$','i')", "false")]
    [DataRow("REGEX('A','^\\\\p{Lu}$','i')", "true")]
    [DataRow("REGEX('a','^[\\\\p{Lu}]$','i')", "false")]
    [DataRow("REGEX('a','^\\\\P{Lu}$','i')", "true")]
    [DataRow("REGEX('A','^\\\\P{Lu}$','i')", "false")]
    [DataRow("REGEX('𐐀','^\\\\p{L}$')", "true")]
    [DataRow("REGEX('𐐀','^[\\\\p{Lu}]$')", "true")]
    [DataRow("REGEX('𐐨','^[\\\\p{Ll}]$')", "true")]
    [DataRow("REGEX('𐐨','^[\\\\p{Lu}]$')", "false")]
    [DataRow("REGEX('😀','^[\\\\p{L}\\\\p{S}]$')", "true")]
    [DataRow("REGEX('😀','^\\\\P{L}$')", "true")]
    [DataRow("REGEX('𝟘','^\\\\p{Nd}$')", "true")]
    [DataRow("REGEX('𐐀','^[\\\\p{L}-[A-Z]]$')", "true")]
    [DataRow("REGEX('A','^[\\\\p{L}-[A-Z]]$')", "false")]
    [DataRow("REGEX('😀','^\\\\p{Cs}$')", "false")]
    [DataRow("REGEX('😀','^\\\\P{Cs}$')", "true")]
    [DataRow("REPLACE('𐐀A😀','(\\\\p{L})','$1$1')", "𐐀𐐀AA😀")]
    [DataRow("REGEX('\\U00010000','^[\\uE000-\\U00010001]$')", "true")]
    [DataRow("REGEX('\\U00010002','^[\\uE000-\\U00010001]$')", "false")]
    [DataRow("REGEX('a','^[a-z-[a-z-[aeiou]]]$')", "true")]
    [DataRow("REGEX('b','^[a-z-[a-z-[aeiou]]]$')", "false")]
    [DataRow("REGEX('A','^[a-z-[aeiou]]$','i')", "false")]
    [DataRow("REGEX('B','^[a-z-[aeiou]]$','i')", "true")]
    [DataRow("REGEX('😀','^[a\\\\w]$')", "true")]
    [DataRow("REGEX('😀','^[^a\\\\w]$')", "false")]
    [DataRow("REGEX('😀','^[^a]$')", "true")]
    [DataRow("REGEX('😀','^[😀-🙏]$')", "true")]
    [DataRow("REGEX('🚀','^[😀-🙏]$')", "false")]
    [DataRow("REGEX('😀','^[\\\\w-[a-z]]$')", "true")]
    [DataRow("REGEX('a','^[\\\\w-[a-z]]$')", "false")]
    [DataRow("REGEX('b','^[a-z-[aeiou]]$')", "true")]
    [DataRow("REGEX('a','^[a-z-[aeiou]]$')", "false")]
    [DataRow("REGEX('😀','^[a\\\\S]$')", "true")]
    [DataRow("REGEX(' ','^[a\\\\S]$')", "false")]
    [DataRow("REPLACE('a😀b','([^a-z])','$1$1')", "a😀😀b")]
    [DataRow("REGEX('😀','^[\\\\w]$')", "true")]
    [DataRow("REGEX('😀','^[^\\\\W]$')", "true")]
    [DataRow("REGEX('𝟘','^[\\\\d]$')", "true")]
    [DataRow("REGEX('𝟘','^[^\\\\d]$')", "false")]
    [DataRow("REGEX('_','\\\\w')", "false")]
    [DataRow("REGEX('$','\\\\w')", "true")]
    [DataRow("REGEX('€','[\\\\w]')", "true")]
    [DataRow("REGEX('_','[\\\\W]')", "true")]
    [DataRow("REGEX('😀','^\\\\w$')", "true")]
    [DataRow("REGEX('😀','^\\\\W$')", "false")]
    [DataRow("REGEX('𐐀','^\\\\w$')", "true")]
    [DataRow("REGEX('𝟘','^\\\\d$')", "true")]
    [DataRow("REGEX('𝟘','^\\\\D$')", "false")]
    [DataRow("REGEX('😀','^\\\\D$')", "true")]
    [DataRow("REGEX('$','^[\\\\$]$')", "true")]
    [DataRow("REGEX('[','^[\\\\[]$')", "true")]
    [DataRow("REGEX(']','^[\\\\]]$')", "true")]
    [DataRow("REGEX('aa','^a{ 2 }$','x')", "true")]
    [DataRow("REPLACE('aaa','a{ 2 , }','x','x')", "x")]
    [DataRow("REPLACE('a_€😀','(\\\\w)','$1$1')", "aa_€€😀😀")]
    [DataRow("REGEX('a#b','a # b','x')", "true")]
    [DataRow("REGEX('ﬆ','^ﬅ$','i')", "true")]
    [DataRow("REGEX('ﬅ','^ﬆ$','i')", "true")]
    [DataRow("REGEX('ﬆ','^ﬅ$')", "false")]
    [DataRow("REGEX('st','^ﬅ$','i')", "false")]
    [DataRow("REGEX('ﬆ','^[ﬅ]$','i')", "true")]
    [DataRow("REGEX('ﬆ','^[^ﬅ]$','i')", "false")]
    [DataRow("REGEX('ﬆ','^[ﬅ-[ﬆ]]$','i')", "false")]
    [DataRow("REGEX('İ','^i$','i')", "false")]
    [DataRow("REGEX('i','^İ$','i')", "false")]
    [DataRow("REGEX('İ','^[i]$','i')", "false")]
    [DataRow("REGEX('ı','^i$','i')", "true")]
    [DataRow("REGEX('Ι','^ͅ$','i')", "true")]
    [DataRow("REPLACE('ﬅﬆ','(ﬅ)','$1$1','i')", "ﬅﬅﬆﬆ")]
    [DataRow("""REGEX('hello world','hello\\ sworld','x')""", "true")]
    [DataRow("""REGEX('helloworld','hello\\ sworld','x')""", "false")]
    [DataRow("""REGEX('A','^\\ p { L }$','x')""", "true")]
    [DataRow("""REGEX('a[b]','a\\ [b\\ ]','x')""", "true")]
    [DataRow("""REGEX('𐐀𐐨','^(𐐀)\\ 1$','ix')""", "true")]
    [DataRow("""REPLACE('𐐀𐐨','(𐐀)\\ 1','[$1]','ix')""", "[𐐀]")]
    [DataRow("REGEX(' ','\\\\s')", "false")]
    [DataRow("REGEX(' ','\\\\S')", "true")]
    [DataRow("REGEX(' ','[\\\\s]')", "false")]
    [DataRow("REGEX(' ','[\\\\S]')", "true")]
    [DataRow("REGEX(' ','[a\\\\S]')", "false")]
    [DataRow("REGEX(' ','[^\\\\s]')", "false")]
    [DataRow("REGEX(' ','[^\\\\s]')", "true")]
    [DataRow("REGEX(' ','[\\\\s]','x')", "true")]
    [DataRow("REGEX('😀','^\\\\S$')", "true")]
    [DataRow("REPLACE('a b c','\\\\s','_')", "a b_c")]
    [DataRow("REPLACE('😀','(\\\\S)','$1$1')", "😀😀")]
    [DataRow("REGEX('a\\n','a$')", "false")]
    [DataRow("REGEX('a\\n','a$','m')", "true")]
    [DataRow("REGEX('a\\r','a.')", "true")]
    [DataRow("REGEX('a\\n','a.')", "false")]
    [DataRow("REGEX('a\\n','a.','s')", "true")]
    [DataRow("REGEX('a\\r\\n','^a..$')", "false")]
    [DataRow("REGEX('a\\r\\n','^a..$','s')", "true")]
    [DataRow("REGEX('a\\r','a.','m')", "true")]
    [DataRow("REPLACE('a\\rb','(.)','[$1]')", "[a][\r][b]")]
    [DataRow("REPLACE('a\\nb','(.)','[$1]')", "[a]\n[b]")]
    [DataRow("""REGEX('\r\r','^(.)\\1$','i')""", "true")]
    [DataRow("""REGEX('\n\n','^(.)\\1$','i')""", "false")]
    [DataRow("""REGEX('\n\n','^(.)\\1$','is')""", "true")]
    [DataRow("REGEX('a\\r','a.','s')", "true")]
    [DataRow("REGEX('😀','^.$')", "true")]
    [DataRow("REGEX('😀','^..$')", "false")]
    [DataRow("REGEX('a\\n','a\\n^','m')", "false")]
    [DataRow("REGEX('a\\n','a\\n$','m')", "false")]
    [DataRow("REPLACE('😀','(.)','$1$1')", "😀😀")]
    [DataRow("REPLACE('a\\n','a$','X')", "a\n")]
    [DataRow("REGEX('a','a # b','x')", "false")]
    [DataRow("REGEX('a b','a[ ]b','x')", "true")]
    [DataRow("REGEX('ab','a[ ]b','x')", "false")]
    [DataRow("REGEX('a b','a b','x')", "true")]
    [DataRow("REGEX('ab','a b','x')", "false")]
    [DataRow("REPLACE('a#b','a # b','X','x')", "X")]
    [DataRow("REPLACE('a','a # b','X','x')", "a")]
    [DataRow("REPLACE('a b','a[ ]b','X','x')", "X")]
    [DataRow("REPLACE('ab','a b','X','x')", "ab")]
    public async Task Sparql_regex_x_flag_preserves_hashes_classes_and_non_xml_whitespace(string expression, string? expected)
    {
        await using var database = await OpenAsync(CreateDirectory());
        var row = RdfSparql.Execute(database, $"SELECT ({expression} AS ?value) {{}}").Single();
        if (expected is null)
        {
            Assert.IsFalse(row.ContainsKey("value"), expression);
        }
        else
        {
            Assert.AreEqual(expected, ((RdfLiteral)row["value"]).LexicalForm);
        }
    }
}
