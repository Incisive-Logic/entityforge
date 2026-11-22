using System.Text.Json;

namespace EntityForge.Db.Prototypes.Pages;

internal static class Program
{
    private const int SuccessExitCode = 0;
    private const int FailureExitCode = 1;
    private const string OutputOption = "--output";
    private const string BufferPoolExperimentName = "buffer-pool";
    private const string PageLayoutExperimentName = "page-layout";
    private const string LogicalReadExperimentName = "logical-read";
    private const string DurabilityExperimentName = "durability";
    private const string CompactRedoExperimentName = "compact-redo";
    private const string RetentionExperimentName = "retention";
    private const string FilesystemProbeName = "filesystem";
    private const string ConcurrentDurabilityName = "concurrency";
    private const string IndexAlternativesName = "indexes";
    private const string AppendMergeRealIoName = "append-merge-io";
    private const string AppendMergeFocusedTierMName = "append-merge-m";
    private const string PropertyTemperaturesName = "properties";
    private const string StatisticsName = "statistics";
    private const string SemanticProjectionCandidatesName = "semantic-projection-candidates";

    public static async Task<int> Main(string[] args)
    {
        try
        {
            ParsedArguments parsed = Parse(args);
            string json = parsed.Experiment switch
            {
                BufferPoolExperimentName => (await new BufferPoolExperiment().RunAsync().ConfigureAwait(false)).ToJson(),
                LogicalReadExperimentName => (await new LogicalReadExperiment().RunAsync().ConfigureAwait(false)).ToJson(),
                DurabilityExperimentName => (await new DurabilityExperiment().RunAsync().ConfigureAwait(false)).ToJson(),
                CompactRedoExperimentName => (await new CompactRedoExperiment().RunAsync().ConfigureAwait(false)).ToJson(),
                RetentionExperimentName => (await new RetentionExperiment().RunAsync().ConfigureAwait(false)).ToJson(),
                FilesystemProbeName => (await new FilesystemPublicationProbe().RunAsync().ConfigureAwait(false)).ToJson(),
                ConcurrentDurabilityName => JsonSerializer.Serialize(
                    await new ConcurrentDurabilityExperiment().RunAsync().ConfigureAwait(false),
                    new JsonSerializerOptions { WriteIndented = true }),
                IndexAlternativesName => new IndexAlternativeExperiment().Run().ToJson(),
                AppendMergeRealIoName => (await new AppendMergeRealIoExperiment().RunAsync().ConfigureAwait(false)).ToJson(),
                AppendMergeFocusedTierMName => (await new AppendMergeRealIoExperiment(
                    AppendMergeCompactionPolicy.SizeTiered, AppendMergeRealIoOptions.FocusedTierM)
                    .RunAsync().ConfigureAwait(false)).ToJson(),
                PropertyTemperaturesName => new PropertyTemperatureExperiment().Run().ToJson(),
                StatisticsName => new StatisticsPlanningExperiment().Run().ToJson(),
                SemanticProjectionCandidatesName => (await new SemanticProjectionCandidateExperiment()
                    .RunAsync().ConfigureAwait(false)).ToJson(),
                PageLayoutExperimentName => new PageLayoutExperiment().Run().ToJson(),
                _ => throw new ArgumentException($"Unknown experiment '{parsed.Experiment}'.")
            };
            string? outputPath = parsed.OutputPath;
            if (outputPath is null)
            {
                Console.WriteLine(json);
            }
            else
            {
                await File.WriteAllTextAsync(outputPath, json).ConfigureAwait(false);
            }

            return SuccessExitCode;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Console.Error.WriteLine($"Page-layout experiment failed: {exception.Message}");
            return FailureExitCode;
        }
    }

    private static ParsedArguments Parse(IReadOnlyList<string> args)
    {
        if (args.Count == 0)
        {
            return new(PageLayoutExperimentName, null);
        }

        string experiment = args[0];
        if (args.Count == 1)
        {
            return new(experiment, null);
        }

        if (args.Count != 3 || !string.Equals(args[1], OutputOption, StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(args[2]))
        {
            throw new ArgumentException(
                $"Usage: EntityForge.Db.Prototypes.Pages [{PageLayoutExperimentName}|{BufferPoolExperimentName}|{LogicalReadExperimentName}|{DurabilityExperimentName}|{CompactRedoExperimentName}|{RetentionExperimentName}|{FilesystemProbeName}|{ConcurrentDurabilityName}|{IndexAlternativesName}|{AppendMergeRealIoName}|{AppendMergeFocusedTierMName}|{PropertyTemperaturesName}|{StatisticsName}|{SemanticProjectionCandidatesName}] [{OutputOption} <path>]");
        }

        return new(experiment, args[2]);
    }

    private sealed record ParsedArguments(string Experiment, string? OutputPath);
}
