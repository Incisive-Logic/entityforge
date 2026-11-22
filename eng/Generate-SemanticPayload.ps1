param(
    [string]$Protoc = 'protoc',
    [Parameter(Mandatory = $true)][string]$ProtobufInclude
)
$ErrorActionPreference = 'Stop'
$project = Join-Path (Split-Path $PSScriptRoot -Parent) 'src/Shared/EntityForge.Semantics.Protobuf'
$schemaRoot = Join-Path $project 'proto'
& $Protoc "--proto_path=$schemaRoot" "--proto_path=$ProtobufInclude" "--csharp_out=$project" 'entityforge/v1/SemanticPayload.proto'
if ($LASTEXITCODE -ne 0) { throw 'Semantic payload generation failed.' }
