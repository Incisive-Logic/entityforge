# EntityForge.Semantics

Semantic vocabulary, identity, provenance, value, and change-session contracts used by EntityForge DB and applications that exchange semantic data.

This package deliberately contains portable semantic contracts rather than storage or transport implementations. Most application developers should reference `EntityForge.Db.Semantic`, which exposes the conventional entity-store API while retaining access to the underlying evidence-aware model.

The package is currently prerelease software and follows the EntityForgeDB release cadence.
