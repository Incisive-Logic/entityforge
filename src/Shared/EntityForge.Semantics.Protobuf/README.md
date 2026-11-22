# Semantic protobuf payloads

Serializes semantic identities, provenance and change-session payloads. The assembly depends on semantic contracts and Google.Protobuf; it contains no pipeline event envelopes or activity interfaces.

The schema in `proto/entityforge/v1/SemanticPayload.proto` is authoritative. Regenerate `SemanticPayload.cs` with `eng/Generate-SemanticPayload.ps1 -ProtobufInclude <protobuf-include-directory>` from the repository root (the checked-in output uses protoc 34.0). Keep field numbers, value encodings and hash domains synchronized with the transport codec. Handwritten conversion and hashing code is maintained separately from generated output.

Author-owned code in this component is licensed under Apache-2.0. Google.Protobuf retains its own licence; see the repository [licensing guide](../../../LICENSING.md) and [third-party notices](../../../THIRD-PARTY-NOTICES.md).
