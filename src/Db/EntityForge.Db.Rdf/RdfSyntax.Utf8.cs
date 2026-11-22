using System.Text;

namespace EntityForge.Db.Rdf;

public static partial class RdfSyntax
{
    private static string ReadRdfUtf8(Stream input)
    {
        // Text RDF formats use UTF-8. Do not silently replace malformed bytes
        // or switch to UTF-16/UTF-32 based on a byte-order mark.
        using var reader = new StreamReader(input, new UTF8Encoding(false, true),
            detectEncodingFromByteOrderMarks: false, leaveOpen: true);
        string source = reader.ReadToEnd();
        return source.StartsWith('\uFEFF') ? source[1..] : source;
    }
}
