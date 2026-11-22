using System.Text.Json;

namespace EntityForge.Db.Studio.Modules;

/// <summary>Describes one independently composed Studio panel without referencing Avalonia.</summary>
public sealed record StudioModuleDescriptor(
    string Key,
    string DisplayName,
    string Icon,
    string Description,
    StudioModuleCategory Category,
    bool RequiresConnection);

public enum StudioModuleCategory
{
    Data, Operations, Security
}

/// <summary>Optional stateful behavior implemented by a module view model.</summary>
public interface IStudioModule
{
    string InstanceId
    {
        get;
    }

    StudioModuleDescriptor Descriptor
    {
        get;
    }

    JsonElement? CaptureState();

    void RestoreState(JsonElement state);
}

/// <summary>Explicit registry of module capabilities; discovery never depends on UI reflection.</summary>
public sealed class StudioModuleRegistry
{
    private readonly IReadOnlyDictionary<string, StudioModuleDescriptor> _descriptors;

    public StudioModuleRegistry(IEnumerable<StudioModuleDescriptor> descriptors)
    {
        StudioModuleDescriptor[] items = descriptors?.ToArray()
            ?? throw new ArgumentNullException(nameof(descriptors));
        if (items.Select(item => item.Key).Distinct(StringComparer.Ordinal).Count() != items.Length)
        {
            throw new ArgumentException("Studio module keys must be unique.", nameof(descriptors));
        }

        _descriptors = items.ToDictionary(item => item.Key, StringComparer.Ordinal);
    }

    public IReadOnlyCollection<StudioModuleDescriptor> Descriptors => _descriptors.Values.ToArray();

    public StudioModuleDescriptor Get(string key) => _descriptors.TryGetValue(key, out StudioModuleDescriptor? value)
        ? value : throw new KeyNotFoundException($"Studio module '{key}' is not registered.");
}
