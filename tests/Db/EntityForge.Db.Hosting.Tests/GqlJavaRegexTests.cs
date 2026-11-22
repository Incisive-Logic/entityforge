using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;

namespace EntityForge.Db.Hosting.Tests;

public sealed partial class EntityForgeDbHostTests
{
    [TestMethod]
    [DataRow("😀😀", @"\x{1F600}+")]
    [DataRow("😁", @"[\x{1F600}-\x{1F603}]")]
    [DataRow("😁", @"[^\x{1F600}]")]
    [DataRow("aa", @"\x{61}+")]
    [DataRow("++", @"\x{2B}+")]
    [DataRow("aaaa", "a+a")]
    [DataRow("aaaa", "a++a")]
    [DataRow("aa", "a?+a")]
    [DataRow("a", "a?+a")]
    [DataRow("aaaa", "a{2,4}+a")]
    [DataRow("aaaaa", "a{2,4}+a")]
    [DataRow("ab", "(ab|a)++b")]
    [DataRow("ab", "(ab|a)+b")]
    [DataRow("😀😀", "😀++😀")]
    [DataRow("😀😀a", "[😀]++a")]
    [DataRow("aa", @"(a)\1++")]
    [DataRow("aa", @"(a)\1++a")]
    [DataRow("aa", @"\u0061++a")]
    [DataRow("😀😀", "[😀]+")]
    [DataRow("😁", "[😀-😃]")]
    [DataRow("😄", "[😀-😃]")]
    [DataRow("😁", "[^😀]")]
    [DataRow("😀", "[^😀]")]
    [DataRow("😁", "[😀-😃&&[^😂]]")]
    [DataRow("😂", "[😀-😃&&[^😂]]")]
    [DataRow("𐀀", "[a-😀]")]
    [DataRow("𐐀", "[𐀀-𐔀]")]
    [DataRow("a\rb", @"(?m)a$\r^b")]
    [DataRow("a\r\nb", @"(?m)a$\r\n^b")]
    [DataRow("a\u0085b", @"(?m)a$\u0085^b")]
    [DataRow("a\u2028b", @"(?m)a$\u2028^b")]
    [DataRow("a\rb", @"(?dm)a$\r^b")]
    [DataRow("a\n", @"a\Z\n")]
    [DataRow("a\r\n", @"a\Z\r\n")]
    [DataRow("a\r\n", @"a\r\Z\n")]
    [DataRow("a\n", @"(?m)a\n^")]
    [DataRow("", @"(?m)^$")]
    [DataRow("", @"^$")]
    [DataRow("a\r\nb", @"(?m)a\r$\n^b")]
    [DataRow("a\rb", @"(?m:a$\r)^b")]
    [DataRow("é", @"\bé\b")]
    [DataRow("é", @"(?U)\bé\b")]
    [DataRow("𐐀", @"(?U)\b𐐀\b")]
    [DataRow("é", @"\Bé\B")]
    [DataRow("ab", @"\ba\Bb\b")]
    [DataRow("😀😀", "😀+")]
    [DataRow("😀😀", @"\Q😀\E+")]
    [DataRow("𐐀", @"(?U)\w")]
    [DataRow("𐐀", @"(?U)[\w]")]
    [DataRow("𐐀", @"(?U)\W")]
    [DataRow("𝟎", @"(?U)\d")]
    [DataRow("𝟎", @"(?U)[\d]")]
    [DataRow("𝟎", @"(?U)\D")]
    [DataRow("😀", @"(?U)\W")]
    [DataRow("😀", @"(?U)[^\w]")]
    [DataRow("é", @"\w")]
    [DataRow("١", @"\d")]
    [DataRow("\u00A0", @"\s")]
    [DataRow("a_9", @"\w+")]
    [DataRow("é", @"(?U)\w")]
    [DataRow("١", @"(?U)\d")]
    [DataRow("\u00A0", @"(?U)\s")]
    [DataRow("éa", @"(?U:\w)\w")]
    [DataRow("aé", @"(?U:\w)\w")]
    [DataRow("é", @"[\w]")]
    [DataRow("é", @"[\W]")]
    [DataRow("bcd", @"[a-z&&[^aeiou]]+")]
    [DataRow("abc", @"[a-z&&[^aeiou]]+")]
    [DataRow("def", @"[a-z&&[def]]+")]
    [DataRow("amp", @"[a-d[m-p]]+")]
    [DataRow("b", @"[a-z&&[^a-c&&[ac]]]")]
    [DataRow("a", @"[a-z&&[^a-c&&[ac]]]")]
    [DataRow("😀", ".")]
    [DataRow("😀", @"\W")]
    [DataRow("\r", ".")]
    [DataRow("\u2028", ".")]
    [DataRow("\r", "(?s).")]
    [DataRow("\r", "(?d).")]
    [DataRow("\n", "(?d).")]
    [DataRow("\r\n", @"\R")]
    [DataRow("\u00A0", @"\h")]
    [DataRow("\u2029", @"\v")]
    [DataRow("foo.bar", @"\Qfoo.bar\E")]
    [DataRow("(a)aa", @"\Q(a)\E(a)\1")]
    [DataRow("ab", "(?x)a # comment\nb")]
    [DataRow("a", "(?x)[ a b ]")]
    [DataRow(" ", "(?x)[ a b ]")]
    public async Task Gql_rejects_vendor_regex_operator_for_all_legacy_patterns(string input, string pattern)
    {
        string query = "RETURN " + JsonSerializer.Serialize(input) + " =~ " + JsonSerializer.Serialize(pattern) + " AS answer";
        _directory = NewDirectory();
        await using var host = EntityForgeDbServer.CreateBuilder().UseLocalStorage(_directory).UsePropertyGraphProfile()
            .ConfigureWebHost(web => web.UseUrls("http://127.0.0.1:0")).Build();
        await host.StartAsync();
        using var client = Client(host);
        using var response = await client.PostAsJsonAsync("/v1/graph/gql/query", new
        {
            query
        });
        string body = await response.Content.ReadAsStringAsync();
        Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode, body);
    }
}
