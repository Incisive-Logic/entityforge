using System.Security.Cryptography;
using System.Text;
using EntityForge.Db.Query.Planning;

namespace EntityForge.Db.Hosting.Tests;

[TestClass]
public sealed class QueryContinuationBoundaryTests
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void Query_continuation_encoder_respects_decoder_byte_budget(bool unicode)
    {
        string fingerprint = Convert.ToHexString(SHA256.HashData("query"u8));
        string position = unicode ? new string('雪', 998) + "x" : new string('x', 2995);
        var original = new EntityQueryContinuation(42, fingerprint, position);
        string token = EntityQueryContinuationCodec.Encode(original);
        Assert.AreEqual(4096, token.Length);
        Assert.AreEqual(original, EntityQueryContinuationCodec.Decode(token));
        Assert.ThrowsExactly<ArgumentException>(() => EntityQueryContinuationCodec.Encode(original with { Position = position + "x" }));
    }

    [TestMethod]
    public void Query_continuation_roundtrips_supplementary_unicode_without_normalization()
    {
        var original = new EntityQueryContinuation(17, new string('A', 64), "\U0001F680/e\u0301/é/\0");
        Assert.AreEqual(original, EntityQueryContinuationCodec.Decode(EntityQueryContinuationCodec.Encode(original)));
    }

    [TestMethod]
    public void Query_continuation_encoder_rejects_unpaired_surrogates()
    {
        Assert.ThrowsExactly<EncoderFallbackException>(() =>
            EntityQueryContinuationCodec.Encode(new(1, new string('A', 64), "\uD800")));
    }

    [TestMethod]
    public void Query_continuation_decoder_rejects_invalid_utf8_even_with_valid_checksum()
    {
        string token = EntityQueryContinuationCodec.Encode(new(1, new string('A', 64), "x"));
        string padded = token.Replace('-', '+').Replace('_', '/');
        byte[] bytes = Convert.FromBase64String(padded.PadRight((padded.Length + 3) / 4 * 4, '='));
        bytes[45] = 0xff;
        SHA256.HashData(bytes.AsSpan(0, bytes.Length - 32)).CopyTo(bytes.AsSpan(bytes.Length - 32));
        string corrupt = Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var error = Assert.ThrowsExactly<EntityQueryContinuationException>(() => EntityQueryContinuationCodec.Decode(corrupt));
        Assert.AreEqual(EntityQueryContinuationFailure.Malformed, error.Failure);
    }
}
