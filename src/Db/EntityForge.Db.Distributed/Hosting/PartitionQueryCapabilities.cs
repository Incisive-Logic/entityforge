using System.Text.Json;
using EntityForge.Db.Rdf;
using EntityForge.Db.Distributed.Sharding;

namespace EntityForge.Db.Distributed.Hosting;

internal sealed partial class PartitionApplicationClient
{
    internal PartitionQueryCapabilities QueryCapabilities => new(Generation, Profile,
        Profile == DatabaseProfileId.PropertyGraph ? ["gql"] : Profile == DatabaseProfileId.Rdf ? ["sparql"] : ["entity", "graphql"],
        Profile == DatabaseProfileId.PropertyGraph ? ["MATCH", "OPTIONAL MATCH"] :
            Profile == DatabaseProfileId.Rdf ? RdfSparql.Capabilities.QueryForms.ToArray() : ["entity-query"],
        _routing.StorageLayout == DatabaseStorageLayout.GraphTopologyProperties ? "graph-topology-property-relation" : "compact-profile-operators-with-bounded-relation-fallback", PartitionQueryLimits.MaximumFacts, PartitionQueryLimits.MaximumExchangeBytes,
        PartitionQueryLimits.ExchangePageBytes, Profile is DatabaseProfileId.PropertyGraph or DatabaseProfileId.Rdf,
        ["Selected input relations must fit the exchange bounds. Unsupported pruning shapes use complete relations.",
            _routing.StorageLayout == DatabaseStorageLayout.GraphTopologyProperties ? "Split Graph exchanges complete topology and property-bag roles at one protected snapshot; joins execute at the query home." : "Graph node/edge predicates, RDF constant patterns and direct scalar Semantic concepts can prune exchange; global joins, ordering and aggregation execute once over the protected union.",
            "RDF non-path SELECT/ASK/CONSTRUCT use native bindings; subject-local SELECT stars execute on owners. DESCRIBE and paths retain relation evaluation.",
            "Direct scalar Semantic projections execute on owners when root support is subject-local; relevant relationship evidence restarts the whole query through relation evaluation. Entailed and relationship queries retain that fallback.",
            "Streaming returns completed projections up to 16 MiB in ordered frames; clients must consume the completion marker. It is not an unbounded query pipeline.",
            "Native scalar snapshot versions and native continuation tokens are not accepted.",
            "Semantic continuation retains an immutable completed projection for 30 seconds; restart invalidates it.",
            "Graph/RDF query updates reserve all owners and require an explicit retained operation identity.",
            "Semantic writes use source sessions; GraphQL is a read-only entity facade, not a mutation language."], PartitionQueryLimits.MaximumStreamBytes);
}
