using EntityForge.Semantics;
using System.Text;
using EntityForge.Db.Semantic.Durability;

namespace EntityForge.Db.Semantic;

/// <summary>Canonical conflict identity for a complete single-entity field projection.</summary>
public static class SemanticReadResources
{
    /// <summary>Returns the same canonical entity resource used by native write footprints.</summary>
    public static string Entity(SemanticEntityIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, new UTF8Encoding(false, true), true);
        SemanticValueBinaryCodec.Identity(writer, identity);
        writer.Flush();
        return "semantic:entity:" + Convert.ToHexString(stream.GetBuffer().AsSpan(0, checked((int)stream.Length)));
    }
}
