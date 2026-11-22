# Semantic standards interoperability

`EntityForge.Semantics` is the representation-neutral runtime contract used by Entity Forge.
It adopts established semantic meanings where they fit, while keeping storage, RDF processing, and
source-expression execution outside the core model.

## SKOS

`Exact`, `Close`, `Broader`, `Narrower`, and `Related` mappings correspond to the similarly named
SKOS mapping relations. Exact mappings are symmetric and transitive. Close and related mappings are
symmetric but not transitive. Broader and narrower mappings are directional inverses.

`Transforming` is an Entity Forge extension. It requires an explicit processor and cannot be exported
as a SKOS alias without losing behavior. The Entity Forge mapping model also applies to properties and
relationships, whereas SKOS mapping properties formally relate concepts.

## PROV

Semantic provenance distinguishes source entities, transformation activities, responsible agents,
generation time, and referenced templates. These concepts can be serialized using PROV Entity,
Activity, Agent, generation, use, derivation, and association relations.

The compact runtime object is not a complete PROV graph. Systems requiring qualified PROV relations
should export an expanded provenance document linked to the semantic change identifier.

The RDF interoperability adapter imports the unqualified PROV-O core: Entity (including Collection,
Bundle, and Plan), Activity, Agent (including Person, Organization, and SoftwareAgent), lifecycle
times, generation, use, derivation, attribution, association, delegation, communication, alternates,
and specialization. Qualified influence nodes and role-bearing relations are not yet translated and
must not be silently collapsed into their unqualified forms.

## RDF and XML Schema

Native semantic values have canonical RDF/XSD datatype mappings through `SemanticValueDatatypes`.
Language strings map to `rdf:langString`; JSON maps to `csvw:JSON`. GUID uses an Entity Forge datatype
because XML Schema has no GUID datatype. `TypedLiteralSemanticValue` preserves external datatypes that
do not have a native semantic value kind.

The RDF interoperability adapter imports a named-term RDFS/OWL vocabulary profile. It preserves named
classes; object, datatype, annotation, and generic RDF properties; labels and comments; class and
property hierarchies; domain and range; named equivalence, disjointness, and inverse properties;
deprecation; and common OWL property characteristics. The Semantic database executes the bounded
named-class entailment profile; the
adapter itself remains a parser and performs no reasoning.
Anonymous class expressions, restrictions, property chains, keys, and qualified axioms are diagnosed
as unsupported rather than flattened into weaker meanings.

Named RDFS/OWL structure is normalized into `SemanticAxiomSet`, separately from SKOS-shaped
`SemanticMappingSet`. The initial axiom profile covers concept and property subsumption, named
equivalence, inverse relationships, concept disjointness, and functional, inverse-functional,
symmetric, asymmetric, transitive, reflexive, and irreflexive characteristics. Every axiom retains
the source vocabulary version and RDF predicate. `SemanticAxiomIndex` exposes bounded hierarchy,
equivalence, inverse, disjointness, and characteristic lookup for catalogs and query planners; it is
not a general-purpose OWL reasoner.

## SHACL

Field and relationship cardinality, datatypes, concepts, and relationship endpoints can be represented
by a SHACL profile. Semantic validation issues expose stable codes, severity, paths, focus terms and
values, and constraint identifiers suitable for conversion to SHACL validation results.

Arbitrary SHACL-SPARQL constraints are not executed by the neutral core. An adapter must report when a
shape cannot be represented by native validation.

## JSON-LD

`SemanticTermContext` supports case-sensitive terms, prefixes, a default vocabulary, compact IRIs, and
absolute IRIs. These rules are intentionally compatible with the common JSON-LD context use case but
do not constitute a JSON-LD processor. Scoped contexts, protected terms, containers, remote loading,
base direction, and JSON-LD expansion algorithms belong in an adapter using a conforming processor.

## Source mapping standards

CSVW, R2RML, and RML belong at the source-authoring boundary. Importers may compile compatible mappings
to Entity Forge semantic templates. They do not define source-scoped replacement, idempotent semantic
changes, competing assertions, or EntityForgeDB physical promotion.

All adapters must distinguish lossless translation, supported translation with Entity Forge extensions,
and unsupported or lossy translation. Loss must never be silent.
