using System.Text.Json;
using Google.Protobuf;
using Wire = EntityForge.Db.Distributed.Hosting.QueryWire;

namespace EntityForge.Db.Distributed.Hosting;

internal static class PartitionRootIndexWire
{
    private const uint Format = 2;

    // The pending proof already binds the complete physical cut and request nonce.
    internal sealed record Control(string Authority, PartitionRootIndexResponse Response);

    internal static byte[] Encode(Control control)
    {
        var pages = control.Response.NativePages ?? throw new InvalidDataException("Missing typed root pages.");
        if (control.Response.Pages is not { Count: 0 })
        {
            throw new InvalidDataException("Unexpected nested root encoding.");
        }

        var reply = new Wire.RootReply
        {
            Format = Format,
            Control = ByteString.CopyFrom(JsonSerializer.SerializeToUtf8Bytes(
            control with
            {
                Response = control.Response with
                {
                    Pages = new(),
                    NativePages = null
                }
            }, PartitionQueryJson.Options))
        };
        foreach (var page in pages)
        {
            reply.Pages.Add(new Wire.RootPage { Id = PartitionQueryOperatorWire.Id(page.Key), Page = page.Value.OperatorWirePage ?? PartitionQueryOperatorWire.EncodePage(page.Value.Native as PartitionQueryExchangePage ?? throw new InvalidDataException("Invalid root page.")) });
        }

        if (reply.CalculateSize() > PartitionParticipantWire.MaximumResponseBytes)
        {
            throw new InvalidDataException("Root reply exceeds its bound.");
        }

        return reply.ToByteArray();
    }

    internal static Control Decode(byte[] bytes)
    {
        if (bytes.Length > PartitionParticipantWire.MaximumResponseBytes)
        {
            throw new InvalidDataException("Root reply exceeds its bound.");
        }

        var reply = Wire.RootReply.Parser.ParseFrom(bytes);
        if (reply.Format != Format || reply.Control.IsEmpty || reply.Pages.Count > 32)
        {
            throw new InvalidDataException("Invalid root framing.");
        }

        var control = JsonSerializer.Deserialize<Control>(reply.Control.Span, PartitionQueryJson.Options) ?? throw new InvalidDataException("Missing root control.");
        if (control.Response.Pages is not { Count: 0 } || control.Response.NativePages is not null)
        {
            throw new InvalidDataException("Unexpected root data control.");
        }

        var values = new Dictionary<Guid, PartitionProjectionValue>();
        foreach (var page in reply.Pages)
        {
            if (page.Id.Length != 16 || page.Page is null)
            {
                throw new InvalidDataException("Invalid root page.");
            }

            var decoded = PartitionQueryOperatorWire.DecodePage(page.Page);
            long size = PartitionProjectionValue.Measure(decoded, PartitionQueryLimits.ExchangePageBytes, PartitionQueryJson.Options);
            if (!values.TryAdd(new Guid(page.Id.Span), new(decoded, PartitionQueryJson.Options)
            {
                QueryPageBytes = size
            }))
            {
                throw new InvalidDataException("Duplicate root page.");
            }
        }
        return control with
        {
            Response = control.Response with
            {
                NativePages = values
            }
        };
    }
}
