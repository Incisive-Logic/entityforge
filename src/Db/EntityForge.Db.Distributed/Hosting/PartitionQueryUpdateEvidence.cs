using System.Text.Json;
using EntityForge.Db.Kernel;
using EntityForge.Db.Rdf;
using EntityForge.Db.Rdf.Contracts;
using EntityForge.Db.Distributed.Sharding;

namespace EntityForge.Db.Distributed.Hosting;

internal static class PartitionQueryUpdateEvidence
{
    internal static PartitionApplicationBinding Narrow(PartitionApplicationBinding application,
        DistributedRoutingBinding routing, IReadOnlyList<PartitionTransactionMember> members)
    {
        // Only the compiler's complete same-subject dependency proof permits
        // projection. Unsupported joins and writes outside candidates keep the
        // full relation. Empty candidate sets still retain the anchor predicate
        // in the native payload, protecting against subsequently inserted matches.
        if (members.Any(m => m.Domain.Profile != DatabaseProfileId.Rdf ||
            !PartitionQueryUpdate.HasCurrentStateDependencies(m.Domain.Profile, application.Operation, m.Payload.Span)))
        {
            return application;
        }

        var evidence = PartitionQueryUpdate.Read(application, routing);
        var facts = evidence.Facts.Select(fact =>
        {
            var member = members.Single(m => m.Domain.Identity == fact.Owner);
            var reads = RdfTransactionCodec.DecodeTransaction(application.Operation, member.Payload.Span)
                .ReadPredicates!.Cast<RdfQueryStateRead>();
            var subjects = reads.SelectMany(r => r.Subjects).ToHashSet();
            using var document = JsonDocument.Parse(fact.Relation);
            using var stream = new MemoryStream();
            using (var writer = new Utf8JsonWriter(stream))
            {
                writer.WriteStartObject();
                foreach (var property in document.RootElement.EnumerateObject())
                {
                    if (property.Name != "quads")
                    {
                        property.WriteTo(writer);
                        continue;
                    }
                    writer.WriteStartArray("quads");
                    foreach (var item in property.Value.EnumerateArray())
                    {
                        if (subjects.Contains(RdfWireJson.Quad(item).Subject))
                        {
                            item.WriteTo(writer);
                        }
                    }

                    writer.WriteEndArray();
                }
                writer.WriteEndObject();
            }
            return new PartitionQueryUpdate.Fact(fact.Owner, stream.ToArray());
        }).ToArray();
        return new(application.Operation, application.Owners, application.Requests,
            planningEvidence: JsonSerializer.SerializeToUtf8Bytes(evidence with
            {
                Facts = facts
            }, PartitionParticipantWire.Json));
    }
}
