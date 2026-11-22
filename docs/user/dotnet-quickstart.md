# First application with the embedded database

This walkthrough creates a local property-graph database, saves a typed entity, reads it, and reads it again after reopening the database. It runs inside your .NET application and does not require a server, access token or Studio.

## Reference the library

Install the SDK from [building from source](../developer/building.md). Create a console project outside the EntityForge checkout:

```powershell
dotnet new console -n EntityForgeDemo -f net10.0
```

Add a project reference to `src/Db/EntityForge.Db.Graph/EntityForge.Db.Graph.csproj` in your EntityForge checkout. For example, if the demo and checkout are sibling directories:

```powershell
dotnet add EntityForgeDemo/EntityForgeDemo.csproj reference entityforge/src/Db/EntityForge.Db.Graph/EntityForge.Db.Graph.csproj
```

Alternatively, use the [local package workflow](../developer/distribution.md) and reference an exact version of `EntityForge.Db.Graph` from the resulting package directory. A source checkout is not evidence that a version is available on nuget.org.

## Save, read and reopen

Replace the demo's `Program.cs` with:

```csharp
#nullable enable
using EntityForge.Db;
using EntityForge.Db.Graph;
using EntityForge.Db.Mapping;

string directory = Path.GetFullPath("demo-data");

await using (var database = await GraphDatabase.OpenLocalAsync(
    new LocalGraphDatabaseOptions(directory)))
{
    var people = database.EntityStore<Person>();
    await people.SaveAsync(new Person("p1", "Alice"));
    var person = await people.FindAsync("p1");
    Console.WriteLine($"Saved: {person?.Name}");
}

await using (var database = await GraphDatabase.OpenLocalAsync(
    new LocalGraphDatabaseOptions(directory)))
{
    var people = database.EntityStore<Person>();
    var person = await people.FindAsync("p1");
    Console.WriteLine($"Recovered: {person?.Name}");
}

[EntityType("catalog", "person")]
public sealed record Person(string Id, string Name);
```

Run it from a directory where you want `demo-data` to be created:

```powershell
dotnet run --project EntityForgeDemo/EntityForgeDemo.csproj
```

The expected output includes `Saved: Alice` and `Recovered: Alice`. The directory is relative to the process working directory, not the source file. Keep the same absolute path when reopening. Dispose the first database before opening another owner on its files.

## Update and delete

Within an open database scope, `await people.SaveAsync(new Person("p1", "Alicia"))` replaces this mapping's state for `p1`. It is not a partial patch. `await people.DeleteAsync("p1")` removes the state owned by that mapping; a later `FindAsync("p1")` returns null when the mapped entity is absent.

An entity's stable identity is part of your data model. Do not create a new random identity each time you save an update. Keep mapping identifiers stable between process restarts. Reads require an established mapping; the write in this example establishes it before the read. Read-only clients must use mappings provisioned beforehand.

Convention mapping requires explicit CLR nullability metadata. Keep nullable reference types enabled (`<Nullable>enable</Nullable>` in the project, or `#nullable enable` as above) so the mapper can distinguish required and optional properties.

## Choose the next API

| Requirement | Next step |
| --- | --- |
| Control property names, RDF predicates or semantic terms | [Explicit entity definitions](../db/ENTITY_API_GUIDE.md#2-explicit-definitions-and-values) |
| Repeat a write after a lost response | [Prepared writes and delivery](../db/ENTITY_API_GUIDE.md#3-prepared-writes-editing-and-delivery) |
| Retain work across application crashes | Capture portable intent and use the durable delivery workflow in the entity guide |
| Query through a running server | [Server and HTTP guide](database.md#use-the-http-service) |
| Preserve source-specific facts and evidence | Use the Semantic profile and its contribution model |
| Inspect records in a browser | Run a matching profile server with [Studio](studio.md); stop the embedded owner before opening the same directory |

The same high-level entity-store pattern is available for Semantic and RDF. Their mappings and native semantics differ: Graph labels are not RDF predicate IRIs, and Semantic contributions retain source provenance. Follow the profile-specific examples rather than changing only a namespace.
