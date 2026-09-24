using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SubtitleReview.Core;

public sealed record ReviewSession(
    int SchemaVersion,
    ReviewDocumentSnapshot Document,
    string BackendFingerprint,
    string EngineFingerprint,
    ReviewDecisions Decisions,
    IReadOnlyList<string> PromptChunkIds,
    IReadOnlyList<string> ResponseIds,
    DateTimeOffset SavedAtUtc)
{
    public IReadOnlyList<string> PromptChunks { get; init; } = [];
    public IReadOnlyList<string> Responses { get; init; } = [];
}

public static class ReviewSessionJson
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        Converters = { new JsonStringEnumConverter<ReviewDecisionKind>(JsonNamingPolicy.SnakeCaseLower, allowIntegerValues: false) },
    };

    public static string Serialize(ReviewSession session)
    {
        Validate(session);
        return JsonSerializer.Serialize(session, Options);
    }

    public static ReviewSession Deserialize(string json)
    {
        try
        {
            var session = JsonSerializer.Deserialize<ReviewSession>(json, Options);
            Validate(session);
            return session!;
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("Invalid review session JSON", ex);
        }
    }

    private static void Validate(ReviewSession? session)
    {
        if (session?.SchemaVersion != 1 || session.Document?.Rows is null ||
            session.Decisions?.Selection?.Rows is null || session.Decisions.Items is null ||
            session.PromptChunkIds is null || session.ResponseIds is null ||
            session.PromptChunks is null || session.Responses is null ||
            string.IsNullOrWhiteSpace(session.EngineFingerprint) ||
            string.IsNullOrWhiteSpace(session.BackendFingerprint))
            throw new InvalidDataException("Unsupported or incomplete review session");
    }
}


public sealed class ReviewSessionStore
{
    private readonly string _root;

    public ReviewSessionStore(string rootDirectory)
    {
        if (string.IsNullOrWhiteSpace(rootDirectory))
            throw new ArgumentException("Session directory is required.", nameof(rootDirectory));

        _root = Path.GetFullPath(rootDirectory);
        Directory.CreateDirectory(_root);
    }

    public bool Exists(string fileName) => File.Exists(Resolve(fileName));

    public ReviewSession Load(string fileName)
    {
        var path = Resolve(fileName);
        try
        {
            return ReviewSessionJson.Deserialize(File.ReadAllText(path));
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("Invalid review session JSON", ex);
        }
    }

    public void Save(string fileName, ReviewSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        var path = Resolve(fileName);
        var tempPath = Path.Combine(
            _root,
            $".{Path.GetFileNameWithoutExtension(fileName)}.{Guid.NewGuid():N}.tmp");
        try
        {
            var json = ReviewSessionJson.Serialize(session);
            var bytes = new UTF8Encoding(false).GetBytes(json);
            using (var stream = new FileStream(
                       tempPath,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None,
                       16 * 1024,
                       FileOptions.WriteThrough))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }

            File.Move(tempPath, path, overwrite: true);
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new IOException("Unable to replace review session atomically.", ex);
        }
        finally
        {
            if (File.Exists(tempPath))
                File.Delete(tempPath);
        }
    }

    private string Resolve(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName) ||
            Path.IsPathRooted(fileName) ||
            !string.Equals(Path.GetFileName(fileName), fileName, StringComparison.Ordinal) ||
            !string.Equals(Path.GetExtension(fileName), ".json", StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(Path.GetFileNameWithoutExtension(fileName)))
            throw new ArgumentException("Session file name must be a simple .json file name.", nameof(fileName));

        var path = Path.GetFullPath(Path.Combine(_root, fileName));
        if (!string.Equals(Path.GetDirectoryName(path), _root, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Session path escapes the session directory.", nameof(fileName));
        return path;
    }
}
