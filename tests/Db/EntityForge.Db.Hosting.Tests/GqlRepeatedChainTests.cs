using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;

namespace EntityForge.Db.Hosting.Tests;

public sealed partial class EntityForgeDbHostTests
{
    [TestMethod]
    public async Task Gql_multiple_repeated_segments_enforce_whole_path_modes()
    {
        _directory = NewDirectory();
        await using var host = EntityForgeDbServer.CreateBuilder().UseLocalStorage(_directory).UsePropertyGraphProfile()
            .ConfigureWebHost(web => web.UseUrls("http://127.0.0.1:0")).Build();
        await host.StartAsync();
        using var client = Client(host);
        using var seed = await client.PostAsJsonAsync("/v1/graph/gql/update", new
        {
            statement = "INSERT (a:SegmentStart)-[:r]->(b)-[:s]->(c)-[:r]->(d)-[:s]->(e)-[:t]->(a)"
        });
        Assert.AreEqual(HttpStatusCode.OK, seed.StatusCode, await seed.Content.ReadAsStringAsync());
        string halves = "(a:SegmentStart)((x)-[r1:r]->(y)-[s1:s]->(z)){1}(b)((u)-[r2:r]->(v)-[s2:s]->(w)){1}(c)-[closing:t]->(a)";
        string cycle = "(()-[rs:r]->()-[ss:s]->()-[:r]->()-[:s]->()-[:t]->()){1}";
        string doubled = "(a:SegmentStart)" + cycle + "(b)" + cycle.Replace("rs", "rt", StringComparison.Ordinal).Replace("ss", "st", StringComparison.Ordinal) + "(c)";
        foreach (var (matchMode, mode, pattern, count) in new[]
        {
            ("", "WALK", halves, 1),
            ("", "TRAIL", halves, 1),
            ("", "SIMPLE", halves, 1),
            ("", "ACYCLIC", halves, 0),
            ("", "WALK", doubled, 0),
            ("REPEATABLE ELEMENTS ", "WALK", doubled, 1),
            ("REPEATABLE ELEMENTS ", "TRAIL", doubled, 0),
            ("REPEATABLE ELEMENTS ", "SIMPLE", doubled, 0)
        })
        {
            string query = "MATCH " + matchMode + "p=" + mode + " " + pattern + " RETURN COUNT(*) AS total";
            using var response = await client.PostAsJsonAsync("/v1/graph/gql/query", new
            {
                query
            });
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, query + ": " + await response.Content.ReadAsStringAsync());
            var rows = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("matches");
            Assert.AreEqual(count, rows[0].GetProperty("bindings").GetProperty("total").GetInt32(), query);
        }
        foreach (string query in new[]
        {
            "MATCH p=(a:SegmentStart)-[r1:r]->{1}(b)-[s1:s]->{1}(c)((u)-[r2:r]->(v)-[s2:s]->(w)){1}(d)-[:t]->(a) RETURN PATH_LENGTH(p) AS answer",
            "MATCH p=" + halves.Replace("(b)", "", StringComparison.Ordinal).Replace("(c)", "", StringComparison.Ordinal) + " RETURN PATH_LENGTH(p) AS answer",
            "MATCH p=" + halves.Replace("(a:SegmentStart)", "", StringComparison.Ordinal).Replace("(x)", "(x:SegmentStart)", StringComparison.Ordinal).Replace("(a)", "", StringComparison.Ordinal) + " RETURN PATH_LENGTH(p) AS answer",
            "MATCH p=ALL SHORTEST " + halves + " RETURN PATH_LENGTH(p) AS answer",
            "MATCH p=" + halves + " RETURN SIZE(r1)+SIZE(s1)+SIZE(r2)+SIZE(s2)+1 AS answer",
            "MATCH (a:SegmentStart) CALL (a) { MATCH p=" + halves + " RETURN p } RETURN PATH_LENGTH(p) AS answer",
            "MATCH p=" + halves.Replace("(b)", "(b WHERE SIZE(r1)=1 AND SIZE(r2)=1)", StringComparison.Ordinal) + " RETURN PATH_LENGTH(p) AS answer",
            "MATCH p=" + halves.Replace("[closing:t]", "[closing:t WHERE SIZE(r1)=1 AND SIZE(r2)=1]", StringComparison.Ordinal) + " RETURN PATH_LENGTH(p) AS answer",
            "MATCH p=" + halves.Replace("(w)){1}", "(w) WHERE SIZE(r1)=1){1}", StringComparison.Ordinal) + " RETURN PATH_LENGTH(p) AS answer",
            "MATCH p=" + halves.Replace("(z)){1}", "(z) WHERE x IS LABELED SegmentStart){1}", StringComparison.Ordinal) + " RETURN PATH_LENGTH(p) AS answer"
        })
        {
            using var response = await client.PostAsJsonAsync("/v1/graph/gql/query", new
            {
                query
            });
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, query + ": " + await response.Content.ReadAsStringAsync());
            var rows = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("matches");
            Assert.AreEqual(1, rows.GetArrayLength(), query);
            Assert.AreEqual(5, rows[0].GetProperty("bindings").GetProperty("answer").GetInt32(), query);
        }
        string anonymous = "MATCH (:SegmentStart)(()-[:r]->()-[:s]->()){1}(()-[:r]->()-[:s]->()){1}";
        foreach (var (prefix, column) in new[] { ("LET retained=7 ", "retained"), ("RETURN 7 AS \"order\" NEXT ", "order") })
        {
            using var response = await client.PostAsJsonAsync("/v1/graph/gql/query", new
            {
                query = prefix + anonymous + " RETURN *"
            });
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, prefix + anonymous + ": " + await response.Content.ReadAsStringAsync());
            var rows = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("matches");
            Assert.AreEqual(1, rows.GetArrayLength());
            var bindings = rows[0].GetProperty("bindings");
            CollectionAssert.AreEqual(new[] { column }, bindings.EnumerateObject().Select(item => item.Name).ToArray());
            Assert.AreEqual(7, bindings.GetProperty(column).GetInt32());
        }
        var update = new
        {
            statement = "MATCH p=" + halves + " SET a.length=PATH_LENGTH(p) RETURN a.length AS answer",
            transactionId = Guid.NewGuid()
        };
        using var changed = await client.PostAsJsonAsync("/v1/graph/gql/update", update);
        Assert.AreEqual(HttpStatusCode.OK, changed.StatusCode, await changed.Content.ReadAsStringAsync());
        Assert.AreEqual(5, (await changed.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("result")
            .GetProperty("matches")[0].GetProperty("bindings").GetProperty("answer").GetInt32());
        using var replay = await client.PostAsJsonAsync("/v1/graph/gql/update", update);
        Assert.AreEqual(await changed.Content.ReadAsStringAsync(), await replay.Content.ReadAsStringAsync());
        var anonymousUpdate = new
        {
            statement = "MATCH (:SegmentStart)(()-[:r]->()-[:s]->()){0,2} INSERT (:AnonymousResult) RETURN COUNT(*) AS total",
            transactionId = Guid.NewGuid()
        };
        using var inserted = await client.PostAsJsonAsync("/v1/graph/gql/update", anonymousUpdate);
        Assert.AreEqual(HttpStatusCode.OK, inserted.StatusCode, await inserted.Content.ReadAsStringAsync());
        Assert.AreEqual(3, (await inserted.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("result")
            .GetProperty("matches")[0].GetProperty("bindings").GetProperty("total").GetInt32());
        using var insertedAgain = await client.PostAsJsonAsync("/v1/graph/gql/update", anonymousUpdate);
        Assert.AreEqual(await inserted.Content.ReadAsStringAsync(), await insertedAgain.Content.ReadAsStringAsync());
        using var counted = await client.PostAsJsonAsync("/v1/graph/gql/query", new
        {
            query = "MATCH (:AnonymousResult) RETURN COUNT(*) AS total"
        });
        Assert.AreEqual(HttpStatusCode.OK, counted.StatusCode, await counted.Content.ReadAsStringAsync());
        Assert.AreEqual(3, (await counted.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("matches")[0]
            .GetProperty("bindings").GetProperty("total").GetInt32());
        var productUpdate = new
        {
            statement = "MATCH p=(a:SegmentStart WHERE c IS NOT NULL)(()-[:r]->()-[:s]->()){1}(b),q=(b)(()-[:r]->()-[:s]->()){1}(c) SET a.productLength=PATH_LENGTH(p)+PATH_LENGTH(q) RETURN a.productLength AS answer",
            transactionId = Guid.NewGuid()
        };
        using var productChanged = await client.PostAsJsonAsync("/v1/graph/gql/update", productUpdate);
        Assert.AreEqual(HttpStatusCode.OK, productChanged.StatusCode, await productChanged.Content.ReadAsStringAsync());
        Assert.AreEqual(4, (await productChanged.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("result")
            .GetProperty("matches")[0].GetProperty("bindings").GetProperty("answer").GetInt32());
        using var productReplay = await client.PostAsJsonAsync("/v1/graph/gql/update", productUpdate);
        Assert.AreEqual(await productChanged.Content.ReadAsStringAsync(), await productReplay.Content.ReadAsStringAsync());
    }

    [TestMethod]
    [DataRow("(startNode)", "(endNode)")]
    [DataRow("", "")]
    public async Task Gql_repeated_groups_concatenate_fixed_path_factors(string start, string end)
    {
        _directory = NewDirectory();
        await using var host = EntityForgeDbServer.CreateBuilder().UseLocalStorage(_directory).UsePropertyGraphProfile()
            .ConfigureWebHost(web => web.UseUrls("http://127.0.0.1:0")).Build();
        await host.StartAsync();
        using var client = Client(host);
        using var seed = await client.PostAsJsonAsync("/v1/graph/gql/update", new
        {
            statement = "INSERT (a:FixedPrefix)-[:enter]->(b)-[:hop]->(c:Shortcut)-[:hop]->(d)-[:exit]->(e:FixedSuffix)"
        });
        Assert.AreEqual(HttpStatusCode.OK, seed.StatusCode, await seed.Content.ReadAsStringAsync());
        string pattern = "(before:FixedPrefix)-[incoming:enter]->" + start +
            "((x)-[links:hop]->(y)){1,3}" + end + "-[outgoing:exit]->(after:FixedSuffix)";
        foreach (var (query, length) in new[]
        {
            ("MATCH p=" + pattern + " RETURN PATH_LENGTH(p) AS length,SIZE(links) AS repetitions", 4),
            ("MATCH (before:FixedPrefix) MATCH p=" + pattern + " RETURN PATH_LENGTH(p) AS length,SIZE(links) AS repetitions", 4),
            ("MATCH p=TRAIL " + pattern + " RETURN PATH_LENGTH(p) AS length,SIZE(links) AS repetitions", 4),
            ("MATCH p=" + pattern.Replace("{1,3}", "+", StringComparison.Ordinal) + " RETURN PATH_LENGTH(p) AS length,SIZE(links) AS repetitions", 4),
            ("MATCH p=ALL SHORTEST " + pattern.Replace("{1,3}", "*", StringComparison.Ordinal) + " RETURN PATH_LENGTH(p) AS length,SIZE(links) AS repetitions", 4),
            ("MATCH p=(before:FixedPrefix)-[:enter]->(((x)-[links:hop]->(y))){1,3}-[:exit]->(after:FixedSuffix) RETURN PATH_LENGTH(p) AS length,SIZE(links) AS repetitions", 4),
            ("MATCH p=(before:FixedPrefix)-[:enter]->((x)-[links:hop]->((y)))+-[:exit]->(after:FixedSuffix) RETURN PATH_LENGTH(p) AS length,SIZE(links) AS repetitions", 4),
            ("MATCH p=(before:FixedPrefix)-[:enter]->((x)-[firstEdges:hop]->(middle)-[secondEdges:hop]->(y) WHERE middle IS LABELED Shortcut)+-[:exit]->(after:FixedSuffix) RETURN PATH_LENGTH(p) AS length,SIZE(firstEdges)+SIZE(secondEdges) AS repetitions", 4),
            ("MATCH p=(before:FixedPrefix)-[:enter]->(x)-[links:hop WHERE links IS LABELED hop]->+(y)-[:exit]->(after:FixedSuffix) RETURN PATH_LENGTH(p) AS length,SIZE(links) AS repetitions", 4),
            ("MATCH p=ALL SHORTEST (before:FixedPrefix)-[:enter]->(x)-[links IS hop&!missing]->{1,3}(y)-[:exit]->(after:FixedSuffix) RETURN PATH_LENGTH(p) AS length,SIZE(links) AS repetitions", 4),
            ("MATCH p=ALL SHORTEST " + pattern + " RETURN PATH_LENGTH(p) AS length,SIZE(links) AS repetitions", 4),
            ("MATCH p=ANY SHORTEST " + pattern + " RETURN PATH_LENGTH(p) AS length,SIZE(links) AS repetitions", 4),
            ("MATCH p=SHORTEST 1 GROUPS " + pattern + " RETURN PATH_LENGTH(p) AS length,SIZE(links) AS repetitions", 4),
            ("MATCH p=ALL SHORTEST (:FixedPrefix)-[:enter]->((x)-[links:hop]->(y)){1,3}-[:exit]->(:FixedSuffix) RETURN PATH_LENGTH(p) AS length,SIZE(links) AS repetitions", 4),
            ("MATCH p=(before:FixedPrefix)-[incoming:enter]->((x)-[links:hop]->(y)){2} RETURN PATH_LENGTH(p) AS length,SIZE(links) AS repetitions", 3),
            ("MATCH p=((x)-[links:hop]->(y)){2}-[outgoing:exit]->(after:FixedSuffix) RETURN PATH_LENGTH(p) AS length,SIZE(links) AS repetitions", 3)
        })
        {
            using var response = await client.PostAsJsonAsync("/v1/graph/gql/query", new
            {
                query
            });
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, query + ": " + await response.Content.ReadAsStringAsync());
            var rows = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("matches");
            Assert.AreEqual(1, rows.GetArrayLength());
            var row = rows[0].GetProperty("bindings");
            Assert.AreEqual(length, row.GetProperty("length").GetInt32());
            Assert.AreEqual(2, row.GetProperty("repetitions").GetInt32());
        }
        using var all = await client.PostAsJsonAsync("/v1/graph/gql/query", new
        {
            query = "MATCH p=" + pattern + " RETURN *"
        });
        Assert.AreEqual(HttpStatusCode.OK, all.StatusCode, await all.Content.ReadAsStringAsync());
        var columns = (await all.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("matches")[0].GetProperty("bindings")
            .EnumerateObject().Select(property => property.Name).ToArray();
        CollectionAssert.AreEquivalent(("p,before,incoming,outgoing,after,x,y,links" + (start.Length == 0 ? "" : ",startNode,endNode")).Split(','), columns);
        foreach (var (query, expected) in new[]
        {
            ("MATCH (:FixedPrefix)(()-[:enter]->()){1}()(()-[:hop]->()){2}() RETURN COUNT(*) AS total", 1),
            ("LET retained=7 MATCH (:FixedPrefix)(()-[:enter]->()){1}()(()-[:hop]->()){1,2}() RETURN SUM(retained) AS total", 14),
            ("OPTIONAL MATCH (:MissingPrefix)(()-[:enter]->()){1}()(()-[:hop]->()){2}() RETURN COUNT(*) AS total", 1),
            ("MATCH p=(a:FixedPrefix)(()-[entryEdges:enter]->()){1}(b)(()-[hopEdges:hop]->()){2}(c) RETURN PATH_LENGTH(p)+SIZE(entryEdges)+SIZE(hopEdges) AS total", 6),
            ("MATCH p=(a:FixedPrefix)(()-[entryEdges:enter]->()){1}(b)(()-[hopEdges:hop]->())+(c)(()-[exitEdges:exit]->()){1}(d:FixedSuffix) RETURN PATH_LENGTH(p)+SIZE(entryEdges)+SIZE(hopEdges)+SIZE(exitEdges) AS total", 8),
            ("MATCH p=(a:FixedPrefix)(()-[entryEdges:enter]->()){1}(b)-[fixedHop:hop]->(c)(()-[hopEdges:hop]->()){1}(d)-[fixedExit:exit]->(e:FixedSuffix) RETURN PATH_LENGTH(p)+SIZE(entryEdges)+SIZE(hopEdges) AS total", 6),
            ("MATCH p=ALL SHORTEST (a:FixedPrefix)-[fixedEntry:enter]->(b)(()-[hopEdges:hop]->())+(c)(()-[exitEdges:exit]->()){1}(d:FixedSuffix) RETURN PATH_LENGTH(p)+SIZE(hopEdges)+SIZE(exitEdges) AS total", 7)
        })
        {
            using var composed = await client.PostAsJsonAsync("/v1/graph/gql/query", new
            {
                query
            });
            Assert.AreEqual(HttpStatusCode.OK, composed.StatusCode, await composed.Content.ReadAsStringAsync());
            var rows = (await composed.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("matches");
            Assert.AreEqual(1, rows.GetArrayLength());
            Assert.AreEqual(expected, rows[0].GetProperty("bindings").GetProperty("total").GetInt32());
        }
        var update = new
        {
            statement = "MATCH p=" + pattern + " SET after.checked=TRUE RETURN after.checked AS answer",
            transactionId = Guid.NewGuid()
        };
        using var changed = await client.PostAsJsonAsync("/v1/graph/gql/update", update);
        Assert.AreEqual(HttpStatusCode.OK, changed.StatusCode, await changed.Content.ReadAsStringAsync());
        Assert.IsTrue((await changed.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("result")
            .GetProperty("matches")[0].GetProperty("bindings").GetProperty("answer").GetBoolean());
        using var replay = await client.PostAsJsonAsync("/v1/graph/gql/update", update);
        Assert.AreEqual(await changed.Content.ReadAsStringAsync(), await replay.Content.ReadAsStringAsync());
        using var shortcut = await client.PostAsJsonAsync("/v1/graph/gql/update", new
        {
            statement = "MATCH (c:Shortcut),(e:FixedSuffix) INSERT (c)-[:exit]->(e)"
        });
        Assert.AreEqual(HttpStatusCode.OK, shortcut.StatusCode, await shortcut.Content.ReadAsStringAsync());
        foreach (var (selector, expected) in new[] { ("ALL SHORTEST", new[] { 3 }), ("SHORTEST 2 GROUPS", new[] { 3, 4 }) })
        {
            // The repeated section has different endpoints for the two paths,
            // but selection groups by the complete path's common endpoints.
            using var selected = await client.PostAsJsonAsync("/v1/graph/gql/query", new
            {
                query = "MATCH p=" + selector + " " + pattern + " RETURN PATH_LENGTH(p) AS length ORDER BY length"
            });
            Assert.AreEqual(HttpStatusCode.OK, selected.StatusCode, await selected.Content.ReadAsStringAsync());
            int[] lengths = (await selected.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("matches").EnumerateArray()
                .Select(row => row.GetProperty("bindings").GetProperty("length").GetInt32()).ToArray();
            CollectionAssert.AreEqual(expected, lengths);
            using var open = await client.PostAsJsonAsync("/v1/graph/gql/query", new
            {
                query = "MATCH p=" + selector + " " + pattern.Replace("{1,3}", "+", StringComparison.Ordinal) + " RETURN PATH_LENGTH(p) AS length ORDER BY length"
            });
            Assert.AreEqual(HttpStatusCode.OK, open.StatusCode, await open.Content.ReadAsStringAsync());
            int[] openLengths = (await open.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("matches").EnumerateArray()
                .Select(row => row.GetProperty("bindings").GetProperty("length").GetInt32()).ToArray();
            CollectionAssert.AreEqual(expected, openLengths);
            using var sequence = await client.PostAsJsonAsync("/v1/graph/gql/query", new
            {
                query = "MATCH p=" + selector + " (:FixedPrefix)(()-[entryEdges:enter]->()){1}(b)(()-[hopEdges:hop]->())+(c)(()-[exitEdges:exit]->()){1}(:FixedSuffix) RETURN PATH_LENGTH(p) AS length ORDER BY length"
            });
            Assert.AreEqual(HttpStatusCode.OK, sequence.StatusCode, await sequence.Content.ReadAsStringAsync());
            int[] sequenceLengths = (await sequence.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("matches").EnumerateArray()
                .Select(row => row.GetProperty("bindings").GetProperty("length").GetInt32()).ToArray();
            CollectionAssert.AreEqual(expected, sequenceLengths);
            using var unnamed = await client.PostAsJsonAsync("/v1/graph/gql/query", new
            {
                query = "MATCH " + selector + " (:FixedPrefix)(()-[entryEdges:enter]->()){1}(b)(()-[hopEdges:hop]->())+(c)(()-[exitEdges:exit]->()){1}(:FixedSuffix) RETURN SIZE(entryEdges)+SIZE(hopEdges)+SIZE(exitEdges) AS length ORDER BY length"
            });
            Assert.AreEqual(HttpStatusCode.OK, unnamed.StatusCode, await unnamed.Content.ReadAsStringAsync());
            int[] unnamedLengths = (await unnamed.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("matches").EnumerateArray()
                .Select(row => row.GetProperty("bindings").GetProperty("length").GetInt32()).ToArray();
            CollectionAssert.AreEqual(expected, unnamedLengths);
        }
        await host.StopAsync();
    }

    [TestMethod]
    [DataRow("->", "<-", 2)]
    [DataRow("<-", "->", 2)]
    [DataRow("->", "->", 0)]
    [DataRow("<-", "<-", 0)]
    [DataRow("-", "-", 4)]
    [DataRow("<->", "<->", 4)]
    public async Task Gql_standard_repeated_chains_preserve_abbreviated_edge_directions(string first, string second, int expected)
    {
        _directory = NewDirectory();
        await using var host = EntityForgeDbServer.CreateBuilder().UseLocalStorage(_directory).UsePropertyGraphProfile()
            .ConfigureWebHost(web => web.UseUrls("http://127.0.0.1:0")).Build();
        await host.StartAsync();
        using var client = Client(host);
        using var seed = await client.PostAsJsonAsync("/v1/graph/gql/update", new
        {
            statement = "INSERT ()-[:abbreviated]->()"
        });
        Assert.AreEqual(HttpStatusCode.OK, seed.StatusCode, await seed.Content.ReadAsStringAsync());
        using var response = await client.PostAsJsonAsync("/v1/graph/gql/query", new
        {
            query = "MATCH REPEATABLE ELEMENTS p=WALK ((a)" + first + "()" + second + "(b)){1,2} RETURN COUNT(*) AS total"
        });
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());
        var rows = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("matches");
        Assert.AreEqual(expected, rows[0].GetProperty("bindings").GetProperty("total").GetInt32());
        await host.StopAsync();
    }

    [TestMethod]
    [DataRow("((starts)-[:anonFirst]->()-[:anonSecond]->(ends))", "", "p,starts,ends")]
    [DataRow("(()-[:anonFirst]->()-[:anonSecond]->())", "", "p")]
    [DataRow("(()-[:anonFirst]->()-[:anonSecond]->())", "ALL SHORTEST", "p")]
    [DataRow("((starts)-[:anonFirst WHERE starts.mark IS TRUE]->()-[:anonSecond]->(ends))", "ALL SHORTEST", "p,starts,ends")]
    [DataRow("((starts)-[:anonFirst {allowed:TRUE}]->()-[back:anonSecond]->(ends))", "", "p,starts,ends,back")]
    public async Task Gql_standard_repeated_chains_allow_anonymous_relationships(string pattern, string selector, string columns)
    {
        _directory = NewDirectory();
        await using var host = EntityForgeDbServer.CreateBuilder().UseLocalStorage(_directory).UsePropertyGraphProfile()
            .ConfigureWebHost(web => web.UseUrls("http://127.0.0.1:0")).Build();
        await host.StartAsync();
        using var client = Client(host);
        using var seed = await client.PostAsJsonAsync("/v1/graph/gql/update", new
        {
            statement = "INSERT (a {mark:TRUE})-[:anonFirst {allowed:TRUE}]->(b)-[:anonSecond]->(a)"
        });
        Assert.AreEqual(HttpStatusCode.OK, seed.StatusCode, await seed.Content.ReadAsStringAsync());
        using var response = await client.PostAsJsonAsync("/v1/graph/gql/query", new
        {
            query = "MATCH REPEATABLE ELEMENTS p=" + selector + " WALK " + pattern + "{1,2} RETURN *"
        });
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());
        var rows = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("matches");
        Assert.AreEqual(selector.Length == 0 ? 2 : 1, rows.GetArrayLength());
        foreach (var row in rows.EnumerateArray())
        {
            CollectionAssert.AreEquivalent(columns.Split(','), row.GetProperty("bindings").EnumerateObject().Select(property => property.Name).ToArray());
        }
        await host.StopAsync();
    }

    [TestMethod]
    [DataRow("ANY SHORTEST", "{1,3}", "", "", 1, 2)]
    [DataRow("SHORTEST 0x3", "{0b1,0o3}", "", "", 3, 8)]
    [DataRow("SHORTEST 0_2 GROUPS", "{0_1,0_3}", "", "", 6, 20)]
    [DataRow("ALL SHORTEST", "{,0x3}", "", "", 2, 0)]
    [DataRow("ALL SHORTEST", "{1,3}", "", "", 2, 4)]
    [DataRow("SHORTEST 2", "{1,3}", "", "", 2, 4)]
    [DataRow("SHORTEST 3", "{1,3}", "", "", 3, 8)]
    [DataRow("SHORTEST 2 GROUPS", "{1,3}", "", "", 6, 20)]
    [DataRow("ANY 2", "{1,3}", "", "", 2, -1)]
    [DataRow("ANY 0", "{1,3}", "", "", 0, 0)]
    [DataRow("SHORTEST 0 GROUPS", "{1,3}", "", "", 0, 0)]
    [DataRow("ALL SHORTEST", "{0,3}", "", "", 2, 0)]
    [DataRow("ANY SHORTEST", "{1,3}", "", "WHERE SIZE(outEdges)>1", 0, 0)]
    [DataRow("ALL SHORTEST", "{1,3}", "WHERE outEdges.allowed IS TRUE", "", 1, 2)]
    [DataRow("ALL SHORTEST", "{1,3}", "WHERE FALSE", "", 0, 0)]
    public async Task Gql_standard_repeated_chain_selection_combines_lengths_before_selecting(string selector, string quantifier, string prefilter, string postfilter, int expectedCount, int expectedHops)
    {
        _directory = NewDirectory();
        await using var host = EntityForgeDbServer.CreateBuilder().UseLocalStorage(_directory).UsePropertyGraphProfile()
            .ConfigureWebHost(web => web.UseUrls("http://127.0.0.1:0")).Build();
        await host.StartAsync();
        using var client = Client(host);
        using var seed = await client.PostAsJsonAsync("/v1/graph/gql/update", new
        {
            statement = "INSERT (a)-[:tieFirst {allowed:TRUE}]->(b)-[:tieSecond]->(a), (a)-[:tieFirst {allowed:FALSE}]->(b)"
        });
        Assert.AreEqual(HttpStatusCode.OK, seed.StatusCode, await seed.Content.ReadAsStringAsync());
        string search = selector.EndsWith(" GROUPS", StringComparison.Ordinal) ? selector[..^7] + " WALK GROUPS" : selector + " WALK";
        string query = "MATCH REPEATABLE ELEMENTS p=" + search + " ((starts)-[outEdges:tieFirst]->(middles)-[backEdges:tieSecond]->(ends) " + prefilter + ")" +
            quantifier + " " + postfilter + " RETURN COUNT(*) AS total,COALESCE(SUM(PATH_LENGTH(p)),0) AS hops";
        using var response = await client.PostAsJsonAsync("/v1/graph/gql/query", new
        {
            query
        });
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());
        var rows = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("matches");
        Assert.AreEqual(1, rows.GetArrayLength());
        var bindings = rows[0].GetProperty("bindings");
        Assert.AreEqual(expectedCount, bindings.GetProperty("total").GetInt32());
        if (expectedHops >= 0)
        {
            Assert.AreEqual(expectedHops, bindings.GetProperty("hops").GetInt32());
        }
        if (expectedCount > 0)
        {
            string visible = query[..query.IndexOf(" RETURN COUNT(*)", StringComparison.Ordinal)] + " RETURN *";
            using var projected = await client.PostAsJsonAsync("/v1/graph/gql/query", new
            {
                query = visible
            });
            Assert.AreEqual(HttpStatusCode.OK, projected.StatusCode, await projected.Content.ReadAsStringAsync());
            var projectedRows = (await projected.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("matches");
            Assert.AreEqual(expectedCount, projectedRows.GetArrayLength());
            foreach (var row in projectedRows.EnumerateArray())
            {
                CollectionAssert.AreEquivalent(new[] { "starts", "middles", "ends", "outEdges", "backEdges", "p" },
                    row.GetProperty("bindings").EnumerateObject().Select(property => property.Name).ToArray());
            }
        }
        await host.StopAsync();
    }

    [TestMethod]
    public async Task Gql_standard_repeated_chain_updates_preserve_order_and_rollback()
    {
        _directory = NewDirectory();
        await using var host = EntityForgeDbServer.CreateBuilder().UseLocalStorage(_directory).UsePropertyGraphProfile()
            .ConfigureWebHost(web => web.UseUrls("http://127.0.0.1:0")).Build();
        await host.StartAsync();
        using var client = Client(host);
        using var seed = await client.PostAsJsonAsync("/v1/graph/gql/update", new
        {
            statement = "INSERT ()-[:repeatFirst {amount:1}]->()-[:repeatSecond {amount:2}]->()-[:repeatFirst {amount:3}]->()-[:repeatSecond {amount:4}]->()"
        });
        Assert.AreEqual(HttpStatusCode.OK, seed.StatusCode, await seed.Content.ReadAsStringAsync());
        const string match = "MATCH (()-[firstEdges:repeatFirst]->()-[secondEdges:repeatSecond]->() WHERE firstEdges.amount<secondEdges.amount){2} ";
        foreach (var (binding, expected) in new[] { ("firstEdges", new[] { 1, 3 }), ("secondEdges", new[] { 2, 4 }) })
        {
            using var query = await client.PostAsJsonAsync("/v1/graph/gql/query", new
            {
                query = match + "FOR e IN " + binding + " RETURN e.amount AS answer"
            });
            Assert.AreEqual(HttpStatusCode.OK, query.StatusCode, await query.Content.ReadAsStringAsync());
            var rows = (await query.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("matches");
            CollectionAssert.AreEqual(expected, rows.EnumerateArray().Select(row => row.GetProperty("bindings").GetProperty("answer").GetInt32()).ToArray());
        }
        using var rejected = await client.PostAsJsonAsync("/v1/graph/gql/update", new
        {
            statement = match + "FOR e IN firstEdges SET e.checked=TRUE RETURN e NEXT SET e.amount=1/0"
        });
        Assert.AreEqual(HttpStatusCode.BadRequest, rejected.StatusCode, await rejected.Content.ReadAsStringAsync());
        using var unchanged = await client.PostAsJsonAsync("/v1/graph/gql/query", new
        {
            query = "MATCH ()-[e:repeatFirst]->() RETURN COUNT(e.checked) AS marked,SUM(e.amount) AS total"
        });
        Assert.AreEqual(HttpStatusCode.OK, unchanged.StatusCode, await unchanged.Content.ReadAsStringAsync());
        var bindings = (await unchanged.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("matches")[0].GetProperty("bindings");
        Assert.AreEqual(0, bindings.GetProperty("marked").GetInt32());
        Assert.AreEqual(4, bindings.GetProperty("total").GetInt32());
        using var updated = await client.PostAsJsonAsync("/v1/graph/gql/update", new
        {
            statement = match + "FOR e IN firstEdges SET e.checked=TRUE"
        });
        Assert.AreEqual(HttpStatusCode.OK, updated.StatusCode, await updated.Content.ReadAsStringAsync());
        using var verified = await client.PostAsJsonAsync("/v1/graph/gql/query", new
        {
            query = "MATCH ()-[e:repeatFirst]->() WHERE e.checked IS TRUE RETURN COUNT(*) AS answer"
        });
        Assert.AreEqual(HttpStatusCode.OK, verified.StatusCode, await verified.Content.ReadAsStringAsync());
        Assert.AreEqual(2, (await verified.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("matches")[0].GetProperty("bindings").GetProperty("answer").GetInt32());
        await host.StopAsync();
    }

    [TestMethod]
    [DataRow("MATCH p=(()-[firstEdges:repeatFirst]->()-[secondEdges:repeatSecond]->()){1,2} RETURN COUNT(*) AS answer", 3)]
    [DataRow("MATCH p=(a:ChainNode {name:'A'})((s1)-[e1:repeatFirst]->(m1)-[f1:repeatSecond]->(t1)){1}(b)((s2)-[e2:repeatFirst]->(m2)-[f2:repeatSecond]->(t2)){1}(c) RETURN PATH_LENGTH(p) AS answer", 4)]
    [DataRow("MATCH p=(a:ChainNode {name:'A'})((s1)-[e1:repeatFirst]->(m1)-[f1:repeatSecond]->(t1)){1}(b)((s2)-[e2:repeatFirst]->(m2)-[f2:repeatSecond]->(t2))+(c) RETURN PATH_LENGTH(p) AS answer", 4)]
    [DataRow("MATCH p=(a:ChainNode {name:'A'})((s1)-[e1:repeatFirst]->(m1)-[f1:repeatSecond]->(t1)){1,2}(b)((s2)-[e2:repeatFirst]->(m2)-[f2:repeatSecond]->(t2))+(c) RETURN PATH_LENGTH(p) AS answer", 4)]
    [DataRow("MATCH p=(a:ChainNode {name:'A'})((s1)-[e1:repeatFirst]->(m1)-[f1:repeatSecond]->(t1)){0,1}(b)((s2)-[e2:repeatFirst]->(m2)-[f2:repeatSecond]->(t2)){0,1}(c) RETURN COUNT(*) AS answer", 4)]
    [DataRow("OPTIONAL MATCH p=(a:Missing)((s1)-[e1:repeatFirst]->(m1)-[f1:repeatSecond]->(t1)){1}(b)((s2)-[e2:repeatFirst]->(m2)-[f2:repeatSecond]->(t2)){1}(c) RETURN CASE WHEN p IS NULL THEN 1 ELSE 0 END AS answer", 1)]
    [DataRow("MATCH (:ChainNode {name:'A'})(()-[:repeatFirst]->()-[:repeatSecond]->()){1} RETURN COUNT(*) AS answer", 1)]
    [DataRow("MATCH (:ChainNode {name:'A'})(()-[:repeatFirst]->()-[:repeatSecond]->())+ RETURN COUNT(*) AS answer", 2)]
    [DataRow("MATCH (:ChainNode {name:'A'})(()-[:repeatFirst]->()-[:repeatSecond]->()){1}(()-[:repeatFirst]->()-[:repeatSecond]->()){1} RETURN COUNT(*) AS answer", 1)]
    [DataRow("MATCH (:ChainNode {name:'A'})(()-[:repeatFirst]->()-[:repeatSecond]->()){0,1}(()-[:repeatFirst]->()-[:repeatSecond]->()){0,1} RETURN COUNT(*) AS answer", 4)]
    [DataRow("LET retained=7 MATCH (:ChainNode {name:'A'})(()-[:repeatFirst]->()-[:repeatSecond]->()){0,2} RETURN SUM(retained) AS answer", 21)]
    [DataRow("LET retained=7 OPTIONAL MATCH (:Missing)(()-[:repeatFirst]->()-[:repeatSecond]->()){1} RETURN retained AS answer", 7)]
    [DataRow("MATCH (a:ChainNode {name:'A'}) MATCH (a)(()-[:repeatFirst]->()-[:repeatSecond]->()){0,2} RETURN COUNT(a) AS answer", 3)]
    [DataRow("LET retained=7 RETURN VALUE { MATCH (:ChainNode {name:'A'})(()-[:repeatFirst]->()-[:repeatSecond]->()){1} RETURN retained AS valueColumn } AS answer", 7)]
    [DataRow("MATCH p=(a:ChainNode {name:'A'})(()-[:repeatFirst]->()-[:repeatSecond]->()){1}(b),q=(b)(()-[:repeatFirst]->()-[:repeatSecond]->()){1}(c) RETURN PATH_LENGTH(p)+PATH_LENGTH(q) AS answer", 4)]
    [DataRow("MATCH p=(a:ChainNode WHERE a.name='A' AND c.name='E')(()-[:repeatFirst]->()-[:repeatSecond]->()){1}(b),q=(b)(()-[:repeatFirst]->()-[:repeatSecond]->()){1}(c) RETURN PATH_LENGTH(p)+PATH_LENGTH(q) AS answer", 4)]
    [DataRow("MATCH p=(a:ChainNode WHERE a.name='A' AND c.name='E')(()-[:repeatFirst]->()-[:repeatSecond]->()){1}(b),q=(b)(()-[:repeatFirst]->()-[:repeatSecond]->()){1}(c WHERE a.name='A') RETURN PATH_LENGTH(p)+PATH_LENGTH(q) AS answer", 4)]
    [DataRow("LET retained=7 OPTIONAL MATCH p=(a:ChainNode WHERE a.name='A' AND c.name='E')(()-[:repeatFirst]->()-[:repeatSecond]->()){1}(b),q=(b)(()-[:repeatFirst]->()-[:repeatSecond]->()){1}(c WHERE a.name='missing') RETURN CASE WHEN p IS NULL AND q IS NULL THEN retained ELSE 0 END AS answer", 7)]
    [DataRow("MATCH p=(a:ChainNode {name:'A'})((starts)-[:repeatFirst]->()-[:repeatSecond]->() WHERE c.name='E'){1}(b),q=(b)(()-[:repeatFirst]->()-[:repeatSecond]->()){1}(c) RETURN PATH_LENGTH(p)+PATH_LENGTH(q) AS answer", 4)]
    [DataRow("MATCH p=(a:ChainNode {name:'A'})((starts)-[:repeatFirst]->()-[:repeatSecond]->() WHERE c.name='E'){1}(b),q=(b)(()-[:repeatFirst]->()-[:repeatSecond]->()){1}(c WHERE a.name='A') RETURN PATH_LENGTH(p)+PATH_LENGTH(q) AS answer", 4)]
    [DataRow("MATCH p=(a:ChainNode {name:'A'})(()-[:repeatFirst]->()-[:repeatSecond]->()){1}(b)-[tailEdge:repeatFirst WHERE c.name='E']->(d),q=(d)-[:repeatSecond]->(c WHERE a.name='A') RETURN PATH_LENGTH(p)+PATH_LENGTH(q) AS answer", 4)]
    [DataRow("MATCH p=(a:ChainNode {name:'A'})((starts)-[:repeatFirst]->()-[:repeatSecond]->() WHERE SIZE(laterEdges)=1){1}(b),q=(b)(()-[laterEdges:repeatFirst]->()-[:repeatSecond]->()){1}(c) RETURN PATH_LENGTH(p)+PATH_LENGTH(q) AS answer", 4)]
    [DataRow("MATCH p=(a:ChainNode {name:CASE WHEN c.name='E' THEN 'A' ELSE 'missing' END})(()-[:repeatFirst]->()-[:repeatSecond]->()){1}(b),q=(b)(()-[:repeatFirst]->()-[:repeatSecond]->()){1}(c) RETURN PATH_LENGTH(p)+PATH_LENGTH(q) AS answer", 4)]
    [DataRow("OPTIONAL MATCH p=(a:ChainNode WHERE a.name='A' AND c.name='missing')(()-[:repeatFirst]->()-[:repeatSecond]->()){1}(b),q=(b)(()-[:repeatFirst]->()-[:repeatSecond]->()){1}(c) RETURN CASE WHEN a IS NULL AND p IS NULL AND q IS NULL THEN 1 ELSE 0 END AS answer", 1)]
    [DataRow("MATCH p=(a:ChainNode {name:'A'})(()-[:repeatFirst]->()-[:repeatSecond]->()){1}(b),q=(a)(()-[:repeatFirst]->()-[:repeatSecond]->()){1}(c) RETURN COUNT(*) AS answer", 0)]
    [DataRow("MATCH REPEATABLE ELEMENTS p=(a:ChainNode {name:'A'})(()-[:repeatFirst]->()-[:repeatSecond]->()){1}(b),q=(a)(()-[:repeatFirst]->()-[:repeatSecond]->()){1}(c) RETURN COUNT(*) AS answer", 1)]
    [DataRow("OPTIONAL MATCH p=(a:ChainNode {name:'A'})(()-[:repeatFirst]->()-[:repeatSecond]->()){1}(b),q=(a)(()-[:repeatFirst]->()-[:repeatSecond]->()){1}(c) RETURN CASE WHEN p IS NULL AND q IS NULL THEN 1 ELSE 0 END AS answer", 1)]
    [DataRow("MATCH (:ChainNode {name:'A'})(()-[:repeatFirst]->()-[:repeatSecond]->()){1},(:ChainNode {name:'A'})(()-[:repeatFirst]->()-[:repeatSecond]->()){1} RETURN COUNT(*) AS answer", 0)]
    [DataRow("MATCH REPEATABLE ELEMENTS (:ChainNode {name:'A'})(()-[:repeatFirst]->()-[:repeatSecond]->()){1},(:ChainNode {name:'A'})(()-[:repeatFirst]->()-[:repeatSecond]->()){1} RETURN COUNT(*) AS answer", 1)]
    [DataRow("LET retained=7 MATCH (a:ChainNode {name:'A'})(()-[:repeatFirst]->()-[:repeatSecond]->()){1}(b),(b)(()-[:repeatFirst]->()-[:repeatSecond]->()){1}(c) RETURN SUM(retained) AS answer", 7)]
    [DataRow("MATCH (a:ChainNode {name:'A'}) CALL (a) { MATCH p=(a)(()-[:repeatFirst]->()-[:repeatSecond]->()){1}(b),q=(b)-[:repeatFirst]->(c) RETURN PATH_LENGTH(p)+PATH_LENGTH(q) AS lengthValue } RETURN lengthValue AS answer", 3)]
    [DataRow("MATCH (a:ChainNode {name:'A'}) RETURN a AS origin NEXT MATCH p=(origin)(()-[:repeatFirst]->()-[:repeatSecond]->()){1}(b),q=(b)-[:repeatFirst]->(c) RETURN PATH_LENGTH(p)+PATH_LENGTH(q) AS answer", 3)]
    [DataRow("LET retained=7 OPTIONAL MATCH p=(a:ChainNode {name:'A'})(()-[:repeatFirst]->()-[:repeatSecond]->()){1}(b),q=(b)-[:missing]->(c) RETURN CASE WHEN p IS NULL AND q IS NULL AND a IS NULL AND b IS NULL AND c IS NULL THEN retained ELSE 0 END AS answer", 7)]
    [DataRow("MATCH p=(a:ChainNode {name:'A'})(()-[:repeatFirst]->()-[:repeatSecond]->()){0,1}(b),q=(b)-[:repeatFirst]->(c) RETURN COUNT(*) AS answer", 2)]
    [DataRow("MATCH p=(a:ChainNode {name:'A'})(()-[:repeatFirst]->()-[:repeatSecond]->())+(b),q=(b)-[:repeatFirst]->(c) WHERE PATH_LENGTH(p)=2 RETURN COUNT(*) AS answer", 1)]
    [DataRow("MATCH (a:ChainNode {name:'A'}) CALL (a) { MATCH (a)((starts)-[firstEdges:repeatFirst]->(middleNodes)-[secondEdges:repeatSecond]->(ends)){1,2}(z) RETURN COUNT(*) AS answer } RETURN answer", 2)]
    [DataRow("MATCH (a:ChainNode {name:'A'}) CALL { MATCH (a)((starts)-[firstEdges:repeatFirst]->(middleNodes)-[secondEdges:repeatSecond]->(ends)){1,2}(z) RETURN COUNT(*) AS answer } RETURN answer", 2)]
    [DataRow("MATCH (a:ChainNode {name:'A'}) RETURN VALUE { MATCH (a)((starts)-[firstEdges:repeatFirst]->(middleNodes)-[secondEdges:repeatSecond]->(ends))+(z) RETURN COUNT(*) AS total } AS answer", 2)]
    [DataRow("MATCH (a:ChainNode {name:'A'}) RETURN CASE WHEN EXISTS { MATCH (a)((starts)-[firstEdges:repeatFirst]->(middleNodes)-[secondEdges:repeatSecond]->(ends)){2}(z) } THEN 1 ELSE 0 END AS answer", 1)]
    [DataRow("MATCH (a:ChainNode {name:'A'}) LET anchor=a MATCH (anchor)((starts)-[firstEdges:repeatFirst]->(middleNodes)-[secondEdges:repeatSecond]->(ends)){1,2}(z) RETURN COUNT(*) AS answer", 2)]
    [DataRow("MATCH (a:ChainNode {name:'A'}) RETURN a NEXT MATCH (a)((starts)-[firstEdges:repeatFirst]->(middleNodes)-[secondEdges:repeatSecond]->(ends)){1,2}(z) RETURN COUNT(*) AS answer", 2)]
    [DataRow("MATCH (a:ChainNode {name:'A'}) RETURN a AS anchor NEXT MATCH (anchor)((starts)-[firstEdges:repeatFirst]->(middleNodes)-[secondEdges:repeatSecond]->(ends))+(z) RETURN COUNT(*) AS answer", 2)]
    [DataRow("MATCH (a:ChainNode {name:'A'}) RETURN * NEXT MATCH (a)((starts)-[firstEdges:repeatFirst]->(middleNodes)-[secondEdges:repeatSecond]->(ends)){1,2}(z) RETURN COUNT(*) AS answer", 2)]
    [DataRow("MATCH (a:ChainNode {name:'A'}) RETURN a NEXT YIELD a AS anchor MATCH (anchor)((starts)-[firstEdges:repeatFirst]->(middleNodes)-[secondEdges:repeatSecond]->(ends))+(z) RETURN COUNT(*) AS answer", 2)]
    [DataRow("MATCH (a:ChainNode {name:'A'}) RETURN a NEXT RETURN a AS anchor NEXT MATCH (anchor)((starts)-[firstEdges:repeatFirst]->(middleNodes)-[secondEdges:repeatSecond]->(ends)){1,2}(z) RETURN COUNT(*) AS answer", 2)]
    [DataRow("MATCH (a:ChainNode {name:'A'}) RETURN a UNION ALL MATCH (a:ChainNode {name:'C'}) RETURN a NEXT MATCH (a)((starts)-[firstEdges:repeatFirst]->(middleNodes)-[secondEdges:repeatSecond]->(ends))+(z) RETURN COUNT(*) AS answer", 3)]
    [DataRow("MATCH (a:ChainNode {name:'A'}) RETURN 1 AS retained NEXT MATCH (a)((starts)-[firstEdges:repeatFirst]->(middleNodes)-[secondEdges:repeatSecond]->(ends)){1,2}(z) RETURN COUNT(*) AS answer", 3)]
    [DataRow("MATCH (a:ChainNode {name:'A'}) FOR anchor IN [a] MATCH (anchor)((starts)-[firstEdges:repeatFirst]->(middleNodes)-[secondEdges:repeatSecond]->(ends))+(z) RETURN COUNT(*) AS answer", 2)]
    [DataRow("LET anchor=VALUE { MATCH (anchor)((starts)-[firstEdges:repeatFirst]->(middleNodes)-[secondEdges:repeatSecond]->(ends)){2}(z) RETURN anchor } RETURN CASE WHEN anchor.name='A' THEN 1 ELSE 0 END AS answer", 1)]
    [DataRow("FOR anchor IN VALUE { MATCH (anchor)((starts)-[firstEdges:repeatFirst]->(middleNodes)-[secondEdges:repeatSecond]->(ends)){2}(z) RETURN [anchor] AS items } RETURN CASE WHEN anchor.name='A' THEN 1 ELSE 0 END AS answer", 1)]
    [DataRow("MATCH (a:ChainNode {name:'A'}) CALL (a) { MATCH (a)((starts)-[firstEdges:repeatFirst]->(middleNodes)-[secondEdges:repeatSecond]->(ends))+(z) RETURN COUNT(*) AS answer } RETURN answer", 2)]
    [DataRow("MATCH (a:ChainNode {name:'A'}) CALL () { MATCH (a)((starts)-[firstEdges:repeatFirst]->(middleNodes)-[secondEdges:repeatSecond]->(ends)){1,2}(z) RETURN COUNT(*) AS answer } RETURN answer", 3)]
    [DataRow("MATCH (a:ChainNode {name:'A'}) CALL (a) { MATCH (a)((starts)-[firstEdges:repeatFirst]->(middleNodes)-[secondEdges:repeatSecond]->(ends)){0,2}(z) RETURN COUNT(*) AS answer } RETURN answer", 3)]
    [DataRow("MATCH (a:ChainNode {name:'E'}) CALL (a) { OPTIONAL MATCH p=(a)((starts)-[firstEdges:repeatFirst]->(ends))+(z) RETURN CASE WHEN p IS NULL THEN 1 ELSE 0 END AS answer } RETURN answer", 1)]
    [DataRow("MATCH (a:ChainNode {name:'A'}) CALL (a) { CALL () { MATCH (a)((starts)-[firstEdges:repeatFirst]->(middleNodes)-[secondEdges:repeatSecond]->(ends)){1,2}(z) RETURN COUNT(*) AS answer } RETURN answer } RETURN answer", 3)]
    [DataRow("MATCH p=(()-[firstEdges:repeatFirst]->()-[secondEdges:repeatSecond]->()){0,2} RETURN COUNT(*) AS answer", 10)]
    [DataRow("MATCH p=((startNodes)-[firstEdges:repeatFirst]->(middleNodes)-[secondEdges:repeatSecond]->(endNodes)){0,1} WHERE SIZE(firstEdges)=0 RETURN SUM(CASE WHEN startNodes=[] AND middleNodes=[] AND endNodes=[] AND secondEdges=[] AND PATH_LENGTH(p)=0 THEN 1 ELSE 0 END) AS answer", 7)]
    [DataRow("OPTIONAL MATCH ((startNodes:Absent)-[firstEdges:missing]->(endNodes:Absent)){0,1} RETURN COUNT(*) AS answer", 7)]
    [DataRow("MATCH p=(()-[firstEdges:repeatFirst]->()-[secondEdges:repeatSecond]->()){2} RETURN PATH_LENGTH(p) AS answer", 4)]
    [DataRow("MATCH p=((startNodes)-[firstEdges:repeatFirst]->(middleNodes)-[secondEdges:repeatSecond]->(endNodes)){2} RETURN SIZE(startNodes)+SIZE(middleNodes)+SIZE(endNodes) AS answer", 6)]
    [DataRow("MATCH ((startNodes)-[firstEdges:repeatFirst]->(middleNodes)-[secondEdges:repeatSecond]->(endNodes)){2} FOR n IN middleNodes MATCH (n)-[:repeatSecond]->() RETURN COUNT(*) AS answer", 2)]
    [DataRow("MATCH ((startNodes)-[firstEdges:repeatFirst]->(middleNodes)-[secondEdges:repeatSecond]->(endNodes)){1,2} RETURN SUM(CASE WHEN startNodes IS TYPED LIST<NODE> AND endNodes IS TYPED LIST<NODE> THEN 1 ELSE 0 END) AS answer", 3)]
    [DataRow("MATCH ((startNodes)-[edges:repeatFirst]->(endNodes)){1} RETURN SUM(SIZE(startNodes)+SIZE(endNodes)) AS answer", 4)]
    [DataRow("MATCH ((startNodes:ChainNode)-[firstEdges:repeatFirst]->(middleNodes:ChainNode)-[secondEdges:repeatSecond]->(endNodes:ChainNode)){2} RETURN COUNT(*) AS answer", 1)]
    [DataRow("MATCH ((startNodes {name:'A'})-[firstEdges:repeatFirst]->(middleNodes)-[secondEdges:repeatSecond]->(endNodes)){2} RETURN COUNT(*) AS answer", 0)]
    [DataRow("MATCH ((startNodes)-[firstEdges:repeatFirst]->(middleNodes {name:'B'})-[secondEdges:repeatSecond]->(endNodes)){2} RETURN COUNT(*) AS answer", 0)]
    [DataRow("LET allowed='B' MATCH ((startNodes)-[firstEdges:repeatFirst]->(middleNodes {name:allowed})-[secondEdges:repeatSecond]->(endNodes)){1} RETURN COUNT(*) AS answer", 1)]
    [DataRow("MATCH ()-[:repeatFirst WHERE TRUE]->{1}() RETURN COUNT(*) AS answer", 2)]
    [DataRow("MATCH ()-[IS repeatFirst|repeatSecond]->{1}() RETURN COUNT(*) AS answer", 4)]
    [DataRow("MATCH (a:ChainNode {name:'A'}) CALL (a) { MATCH (a)-[:repeatFirst WHERE TRUE]->{1}() RETURN COUNT(*) AS amount } RETURN amount AS answer", 1)]
    [DataRow("MATCH ()-[:missing WHERE FALSE]->{0,1}() RETURN COUNT(*) AS answer", 7)]
    [DataRow("LET retained=7 MATCH ()-[:repeatFirst WHERE TRUE]->{1}() RETURN SUM(retained) AS answer", 14)]
    [DataRow("OPTIONAL MATCH ()-[:repeatFirst WHERE FALSE]->{1}() RETURN COUNT(*) AS answer", 1)]
    [DataRow("MATCH ((:ChainNode)-[edges:repeatFirst]->(:ChainNode)){1} RETURN COUNT(*) AS answer", 2)]
    [DataRow("MATCH ((starts)-[outEdges:repeatFirst]->(middles)-[backEdges:repeatSecond]->(ends) WHERE outEdges.amount<backEdges.amount){2} RETURN COUNT(*) AS answer", 1)]
    [DataRow("MATCH ((starts)-[outEdges:repeatFirst]->(middles)-[backEdges:repeatSecond]->(ends) WHERE middles.name='B'){2} RETURN COUNT(*) AS answer", 0)]
    [DataRow("LET upperBoundValue=2 MATCH ((starts)-[outEdges:repeatFirst]->(middles)-[backEdges:repeatSecond]->(ends) WHERE outEdges.amount<=upperBoundValue){1,2} RETURN COUNT(*) AS answer", 1)]
    [DataRow("MATCH ((starts)-[outEdges:repeatFirst]->(middles)-[backEdges:repeatSecond]->(ends) WHERE starts IS SOURCE OF outEdges AND ends IS DESTINATION OF backEdges){2} RETURN COUNT(*) AS answer", 1)]
    [DataRow("MATCH ((starts WHERE starts.name='A')-[outEdges:repeatFirst]->(middles)-[backEdges:repeatSecond]->(ends)){2} RETURN COUNT(*) AS answer", 0)]
    [DataRow("MATCH ((starts)-[outEdges:repeatFirst WHERE starts.name='A']->(middles)-[backEdges:repeatSecond]->(ends)){2} RETURN COUNT(*) AS answer", 0)]
    [DataRow("LET required=1 MATCH (()-[outEdges:repeatFirst {amount:required}]->()-[backEdges:repeatSecond]->()){1,2} RETURN COUNT(*) AS answer", 1)]
    [DataRow("MATCH (()-[outEdges:repeatFirst {amount:backEdges.amount-1}]->()-[backEdges:repeatSecond]->()){2} RETURN COUNT(*) AS answer", 1)]
    [DataRow("MATCH p=((starts)-[outEdges:missing]->(ends) WHERE 1/0=0){0,1} RETURN SUM(CASE WHEN starts=[] AND ends=[] AND outEdges=[] AND PATH_LENGTH(p)=0 THEN 1 ELSE 0 END) AS answer", 7)]
    [DataRow("OPTIONAL MATCH ((starts)-[outEdges:repeatFirst]->(middles)-[backEdges:repeatSecond]->(ends) WHERE outEdges.amount>backEdges.amount){1,2} RETURN CASE WHEN starts IS NULL AND outEdges IS NULL THEN 1 ELSE 0 END AS answer", 1)]
    [DataRow("MATCH p=(()-[firstEdges:repeatFirst]->()-[secondEdges:repeatSecond]->()){1,2} WHERE SIZE(firstEdges)=2 RETURN COUNT(*) AS answer", 1)]
    [DataRow("LET required=2 MATCH p=(()-[firstEdges:repeatFirst]->()-[secondEdges:repeatSecond]->()){1,2} WHERE SIZE(firstEdges)=required RETURN SIZE(secondEdges) AS answer", 2)]
    [DataRow("LET __repeatedGroup0_Node0=7 MATCH (()-[firstEdges:repeatFirst]->()-[secondEdges:repeatSecond]->()){1} RETURN SUM(__repeatedGroup0_Node0) AS answer", 14)]
    [DataRow("OPTIONAL MATCH p=(()-[firstEdges:repeatFirst]->()-[secondEdges:repeatSecond]->()){1,2} WHERE SIZE(firstEdges)>10 RETURN CASE WHEN firstEdges IS NULL AND secondEdges IS NULL AND p IS NULL THEN 1 ELSE 0 END AS answer", 1)]
    [DataRow("MATCH (()-[firstEdges:repeatFirst]->()-[secondEdges:repeatSecond]->()){2} FOR e IN firstEdges RETURN SUM(e.amount) AS answer", 4)]
    [DataRow("MATCH (()-[firstEdges:repeatFirst]->()-[secondEdges:repeatSecond]->()){2} FOR e IN secondEdges RETURN SUM(e.amount) AS answer", 6)]
    [DataRow("MATCH REPEATABLE ELEMENTS p=WALK (()-[firstEdges:cycleFirst]->()-[secondEdges:cycleSecond]->()){2} RETURN PATH_LENGTH(p) AS answer", 4)]
    [DataRow("MATCH REPEATABLE ELEMENTS ANY SHORTEST WALK ((starts)-[outEdges:cycleFirst]->(middles)-[backEdges:cycleSecond]->(ends)){1,3} RETURN COUNT(*) AS answer", 1)]
    [DataRow("MATCH REPEATABLE ELEMENTS p=TRAIL (()-[firstEdges:cycleFirst]->()-[secondEdges:cycleSecond]->()){2} RETURN COUNT(*) AS answer", 0)]
    [DataRow("MATCH p=(()-[firstEdges:cycleFirst]->()-[secondEdges:cycleSecond]->()){2} RETURN COUNT(*) AS answer", 0)]
    public async Task Gql_standard_repeated_chains_preserve_lists_modes_scope_and_optional_rows(string query, int expected)
    {
        _directory = NewDirectory();
        await using var host = EntityForgeDbServer.CreateBuilder().UseLocalStorage(_directory).UsePropertyGraphProfile()
            .ConfigureWebHost(web => web.UseUrls("http://127.0.0.1:0")).Build();
        await host.StartAsync();
        using var client = Client(host);
        using var seed = await client.PostAsJsonAsync("/v1/graph/gql/update", new
        {
            statement = "INSERT (:ChainNode {name:'A'})-[:repeatFirst {amount:1}]->(:ChainNode {name:'B'})-[:repeatSecond {amount:2}]->(:ChainNode {name:'C'})-[:repeatFirst {amount:3}]->(:ChainNode {name:'D'})-[:repeatSecond {amount:4}]->(:ChainNode {name:'E'}), (a)-[:cycleFirst]->()-[:cycleSecond]->(a)"
        });
        Assert.AreEqual(HttpStatusCode.OK, seed.StatusCode, await seed.Content.ReadAsStringAsync());
        using var response = await client.PostAsJsonAsync("/v1/graph/gql/query", new
        {
            query
        });
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());
        var matches = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("matches");
        Assert.AreEqual(1, matches.GetArrayLength());
        Assert.AreEqual(expected, matches[0].GetProperty("bindings").GetProperty("answer").GetInt32());
        await host.StopAsync();
    }
}
