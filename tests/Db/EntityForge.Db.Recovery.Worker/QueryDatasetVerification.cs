using System.Diagnostics;
using System.Text.Json;
using EntityForge.Db.Graph;
using EntityForge.Db.Rdf;

namespace EntityForge.Db.Recovery.Worker;

internal sealed record QueryDatasetVerificationRequest(string Directory, string ReportPath, Guid Nonce,
    bool Rdf, int Size, int FanOut, bool Aggregate, bool Hubs, int Remainder);

internal sealed record QueryDatasetVerificationResult(Guid Nonce, int ProcessId, bool Rdf,
    long Entities, long Edges, double OpenSeconds, double VerificationSeconds);

internal static class QueryDatasetVerification
{
    internal static async Task RunAsync(string requestPath)
    {
        if (new FileInfo(requestPath).Length > 65536)
        {
            throw new InvalidDataException("Dataset verification request is too large.");
        }
        var request = JsonSerializer.Deserialize<QueryDatasetVerificationRequest>(await File.ReadAllTextAsync(requestPath))
            ?? throw new InvalidDataException("Dataset verification request is missing.");
        if (request.Nonce == Guid.Empty || request.Size is < 1 or > 10_000_000 ||
            request.FanOut < 0 || request.FanOut > Math.Min(5, request.Size) || request.Remainder is < 0 or > 6)
        {
            throw new InvalidDataException("Dataset verification dimensions are invalid.");
        }
        int quads = checked(request.Size * (1 + request.FanOut + (request.Aggregate ? 1 : 0)) + request.Remainder);
        if (quads > 10_000_000 || !request.Rdf && request.Size > 1_000_000)
        {
            throw new InvalidDataException("Dataset verification exceeds the supported scale.");
        }
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(30));
        var oracle = new QueryWorkloadDatasetOracle(request.Size, request.FanOut, request.Aggregate, request.Hubs, request.Remainder);
        long started = Stopwatch.GetTimestamp();
        double opened, verified;
        long entities = 0, edges = 0;
        if (request.Rdf)
        {
            await using var database = (RdfDatabaseEngine)await RdfDatabase.OpenLocalAsync(new(request.Directory), deadline.Token);
            opened = Stopwatch.GetElapsedTime(started).TotalSeconds;
            long verification = Stopwatch.GetTimestamp();
            await foreach (var page in database.ReadScanPagesAsync(new(), 1000, quads + 1, deadline.Token))
            {
                foreach (var quad in page.Quads)
                {
                    oracle.Observe(quad);
                }
                entities += page.Quads.Count;
            }
            oracle.Complete();
            verified = Stopwatch.GetElapsedTime(verification).TotalSeconds;
        }
        else
        {
            await using var database = (GraphDatabaseEngine)await GraphDatabase.OpenLocalAsync(new(request.Directory), deadline.Token);
            opened = Stopwatch.GetElapsedTime(started).TotalSeconds;
            long verification = Stopwatch.GetTimestamp();
            using var lease = await database.AcquireSnapshotAsync(cancellationToken: deadline.Token);
            var snapshot = await database.CaptureScanSnapshotAsync(lease, deadline.Token);
            oracle.VerifyGraph(snapshot.View.Vertices, snapshot.View.Edges, deadline.Token);
            entities = snapshot.View.Vertices.Count;
            edges = snapshot.View.Edges.Count;
            verified = Stopwatch.GetElapsedTime(verification).TotalSeconds;
        }
        // Publish success only after the independently opened database has closed.
        await File.WriteAllTextAsync(request.ReportPath, JsonSerializer.Serialize(new QueryDatasetVerificationResult(
            request.Nonce, Environment.ProcessId, request.Rdf, entities, edges, opened, verified)), deadline.Token);
    }
}
