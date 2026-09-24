using System.Text.Json;
using System.Text.Json.Nodes;

namespace AudioWorkflow;

public sealed record PortableMapping(string Source, string Target);
public sealed record PortableConfiguration(int SchemaVersion, PortableMapping[] Mappings);

/// <summary>Resolve locations without rewriting immutable checkpoint bytes or their hashes.</summary>
public sealed class PortablePathMap
{
    public const string Prefix = "portable:/";
    public string Root { get; }
    private readonly PortableMapping[] _mappings;
    public PortablePathMap(string root, IEnumerable<PortableMapping>? mappings = null)
    {
        Root = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        _mappings = (mappings ?? []).OrderByDescending(m => m.Source.Length).ToArray();
        foreach (var mapping in _mappings)
        {
            if (!Path.IsPathFullyQualified(mapping.Source)) throw new InvalidDataException("Portable source must be absolute.");
            _ = Local(mapping.Target);
        }
    }
    private string Local(string relative)
    {
        if (Path.IsPathRooted(relative) || relative.Contains(':')) throw new InvalidDataException("Portable target must be relative.");
        var full = Path.GetFullPath(Path.Combine(Root, relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!full.Equals(Root, StringComparison.OrdinalIgnoreCase) && !full.StartsWith(Root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Portable path escaped the package.");
        return full;
    }
    public string Resolve(string value)
    {
        if (value.StartsWith(Prefix, StringComparison.Ordinal)) return Local(value[Prefix.Length..]);
        foreach (var mapping in _mappings)
        {
            var source = mapping.Source.TrimEnd('\\', '/');
            if (value.Equals(source, StringComparison.OrdinalIgnoreCase)) return Local(mapping.Target);
            if (value.StartsWith(source + "\\", StringComparison.OrdinalIgnoreCase) || value.StartsWith(source + "/", StringComparison.OrdinalIgnoreCase))
                return Local(Path.Combine(mapping.Target, value[(source.Length + 1)..]));
        }
        return value;
    }
    public string Encode(string value)
    {
        if (!Path.IsPathFullyQualified(value)) return value;
        var full = Path.GetFullPath(value);
        if (full.Equals(Root, StringComparison.OrdinalIgnoreCase)) return Prefix;
        return full.StartsWith(Root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            ? Prefix + Path.GetRelativePath(Root, full).Replace('\\', '/') : value;
    }
    // Never transform subtitle text, timeline JSON, IDs or hashes, even if they look like paths.
    public static bool IsPathField(string key) => key.EndsWith("Path", StringComparison.OrdinalIgnoreCase) ||
        key.EndsWith("FileName", StringComparison.OrdinalIgnoreCase) || key.EndsWith("Directory", StringComparison.OrdinalIgnoreCase) ||
        key.Equals("ImmutableRevision", StringComparison.OrdinalIgnoreCase);
    public string TransformJson(string json, bool encode)
    {
        var node = JsonNode.Parse(json) ?? throw new InvalidDataException("Empty portable JSON.");
        void Visit(JsonNode? item)
        {
            if (item is JsonObject obj)
                foreach (var pair in obj.ToArray())
                {
                    if (IsPathField(pair.Key) && pair.Value is JsonValue val && val.TryGetValue<string>(out var text))
                        obj[pair.Key] = encode ? Encode(text) : Resolve(text);
                    else Visit(pair.Value);
                }
            else if (item is JsonArray array) foreach (var child in array) Visit(child);
        }
        Visit(node);
        return node.ToJsonString();
    }
}

public static class PortablePaths
{
    private static readonly Lazy<PortablePathMap?> Map = new(() => Load(AppContext.BaseDirectory));
    public static PortablePathMap? Current => Map.Value;
    public static PortablePathMap? Load(string root)
    {
        var file = Path.Combine(root, "portable-paths.json");
        if (!File.Exists(file)) return null;
        var config = JsonSerializer.Deserialize<PortableConfiguration>(File.ReadAllText(file), JsonStore.Options)
            ?? throw new InvalidDataException("Missing portable configuration.");
        if (config.SchemaVersion != 1 || config.Mappings == null) throw new InvalidDataException("Unsupported portable configuration.");
        return new PortablePathMap(root, config.Mappings);
    }
    public static string DecodeJson(string json) => Current?.TransformJson(json, false) ?? json;
    public static string EncodeJson(string json) => Current?.TransformJson(json, true) ?? json;
    public static string WorkRoot => Current is { } map ? Path.Combine(map.Root, "Data", "AudioWork") :
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ReversibleAudioSync", "AudioWork");
    public static string SettingsRoot => Current is { } map ? Path.Combine(map.Root, "Data", "Settings") :
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SubtitleEditReversibleAudio");
}
