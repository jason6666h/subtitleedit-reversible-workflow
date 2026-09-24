using System.Globalization;
using System.Reflection;
using System.Text;

namespace SubtitleReview.Core;

public static class ReviewResources
{
    private const string ResourcePrefix = "SubtitleReview.Core.";

    public static string PromptTemplate
    {
        get
        {
            var culture = CultureInfo.CurrentUICulture.Name;
            var resource = culture.StartsWith("zh", StringComparison.OrdinalIgnoreCase)
                ? "PromptTemplate.zh-TW.md"
                : "PromptTemplate.en.md";
            return ReadTextResource(resource)
                .Replace("\r\n", "\n", StringComparison.Ordinal)
                .Replace("\r", "\n", StringComparison.Ordinal);
        }
    }

    public static void EnsureGlossarySeeds(string destinationDirectory)
    {
        Directory.CreateDirectory(destinationDirectory);
        EnsureSeed("Resources.glossary.csv", Path.Combine(destinationDirectory, "glossary.csv"));
        EnsureSeed("Resources.glossary_ai.csv", Path.Combine(destinationDirectory, "glossary_ai.csv"));
        EnsureSeed("Resources.protected_phrases.json", Path.Combine(destinationDirectory, "protected_phrases.json"));
    }

    private static byte[] ReadSeedBytes(string resourceName)
    {
        using var stream = Open(resourceName);
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        return memory.ToArray();
    }

    private static string ReadTextResource(string name)
    {
        using var stream = Open(name);
        using var reader = new StreamReader(stream, new UTF8Encoding(false, true), detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }

    private static void EnsureSeed(string resourceName, string path)
    {
        if (File.Exists(path))
            return;
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);
        File.WriteAllBytes(path, ReadSeedBytes(resourceName));
    }

    private static Stream Open(string name)
    {
        var assembly = typeof(ReviewResources).Assembly;
        var fullName = ResourcePrefix + name.Replace('/', '.').Replace('\\', '.');
        return assembly.GetManifestResourceStream(fullName)
            ?? throw new InvalidDataException($"Missing embedded SubtitleReview resource: {fullName}");
    }
}
