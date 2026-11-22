# Third-party notices

## Noda Time and timezone data

The graph runtime depends on [Noda Time](https://nodatime.org/) 3.3.7 for its bundled IANA timezone database and timezone transition resolution. Noda Time retains its Apache-2.0 licence and upstream notice, including the notice for portions derived from Joda Time. Unmodified copies are in `src/Db/EntityForge.Db.Graph/ThirdParty/NodaTime-LICENSE.txt` and `NodaTime-NOTICE.txt`; they accompany build/publish output and the graph package. EntityForge's public value contracts do not expose Noda Time types.

## dotNetRDF JSON-LD expansion sources

`src/Db/EntityForge.Db.Rdf/ThirdParty/DotNetRdfExpansion` contains six expansion and RDF-conversion components derived from dotNetRDF 3.5.2. They retain the upstream MIT licence and source notices; they are not relicensed as Apache-2.0. The adjacent provenance file pins their upstream revision. EntityForge isolates these components in a private namespace and uses its native RDF IRI validator for absolute identifiers, including single-letter schemes that `System.Uri` can interpret as drive paths. Local base resolution uses the native IRI resolver, protected definitions include language values and scoped contexts, and RDF conversion consumes expanded input directly. Upstream portions remain MIT; EntityForge-authored modifications use the root Apache-2.0 licence. Compaction and framing are not included. The licence and notice accompany RDF build/publish output and its NuGet package.

## Unicode character data

`src/Db/EntityForge.Db.Rdf/Unicode/Blocks-16.0.0.txt` is the unmodified Unicode 16.0.0 block table from [the Unicode Character Database](https://www.unicode.org/Public/16.0.0/ucd/Blocks.txt). It supports SPARQL regular-expression block escapes. This data retains the Unicode License V3 in the adjacent `LICENSE.txt`; it is not relicensed under Apache-2.0. Both the data and licence are embedded in the RDF assembly, and the licence is also copied to build and publish output and included in the RDF package.

`src/Db/EntityForge.Db.Query.Planning/Unicode` contains unmodified Unicode 16.0.0 [UnicodeData](https://www.unicode.org/Public/16.0.0/ucd/UnicodeData.txt), [SpecialCasing](https://www.unicode.org/Public/16.0.0/ucd/SpecialCasing.txt), [DerivedCoreProperties](https://www.unicode.org/Public/16.0.0/ucd/DerivedCoreProperties.txt) and [CompositionExclusions](https://www.unicode.org/Public/16.0.0/ucd/CompositionExclusions.txt) tables, with the version included in each filename. The shared query-engine infrastructure embeds these tables for GQL and SPARQL default Unicode case conversion, GQL normalization and SPARQL regex categories. GQL identifier generation also uses the pinned data. They retain the Unicode License V3 in the adjacent `LICENSE.txt`; the licence accompanies build/publish output as `ThirdParty/Unicode-LICENSE.txt` and is included in the planning package. The Unicode data is not relicensed under Apache-2.0.

## Selected RDF query regression fixtures

Files in `tests/Db/EntityForge.Db.Hosting.Tests/Fixtures/QueryCompatibility/upstream` originate from the RDF test repository. Their original dual licence is retained; this development subset uses the W3C 3-clause BSD option. The directory contains the original licence reference, licence text, pinned source revision and file hashes. These fixtures are not relicensed under EntityForge's Apache-2.0 licence. No endorsement or standards-conformance claim is made.

## Wildcard matcher

`src/Shared/EntityForge.Utility/FastWildcard.cs` includes H.A. Sullivan's implementation. Its original source notice is retained. The following permission notice applies to that implementation:

```text
MIT License

Copyright(c) [2016]
[H.A. Sullivan]

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.

```

## Dependencies

The GQL syntax grammar is derived from the Apache-2.0 OpenGQL project. Its
unchanged grammar, licence and source revision are retained in
[GQL grammar](src/Db/EntityForge.Db.Query.Syntax/Gql/NOTICE.txt). Generated C#
lexer and parser files use ANTLR; the runtime dependency is
Antlr4.Runtime.Standard 4.13.1 and retains its BSD licence.

NuGet packages and their runtime/native/font assets retain their respective licences. A binary distribution must carry the notices for its resolved dependency versions. Use the package metadata and bundled licence files, including Google.Protobuf, Avalonia, SkiaSharp, HarfBuzzSharp and Inter font assets, when preparing the distribution. Optional etcd deployment and dotnet-etcd attribution are listed in [the database notice](docs/db/NOTICE).

