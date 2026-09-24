using Nikse.SubtitleEdit.Logic.Config;
using SubtitleReview.Core;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace Nikse.SubtitleEdit.Features.Main.Layout;

internal sealed record SubtitleReviewSettings(
    string GlossaryPath = "",
    int ChunkSize = 100,
    string EvidencePath = "",
    string PromptTemplate = "",
    string ReferenceMaterial = "",
    string[] ReviewReferenceFiles = null!);

internal sealed class SubtitleReviewSettingsStore
{
    private readonly string _root;
    private readonly string _path;
    private readonly string _referenceRoot;

    public SubtitleReviewSettingsStore()
        : this(Path.Combine(Se.DataFolder, "SubtitleReview"))
    {
    }

    internal SubtitleReviewSettingsStore(string root)
    {
        _root = Path.GetFullPath(root);
        _path = Path.Combine(_root, "settings.json");
        _referenceRoot = Path.Combine(_root, "references");
    }

    public SubtitleReviewSettings Load()
    {
        try
        {
            if (!File.Exists(_path))
                return Normalize(new SubtitleReviewSettings());

            var settings = JsonSerializer.Deserialize<SubtitleReviewSettings>(
                File.ReadAllText(_path, Encoding.UTF8));
            if (settings is null)
                return Normalize(new SubtitleReviewSettings());

            return Normalize(settings);
        }
        catch (JsonException)
        {
            return Normalize(new SubtitleReviewSettings());
        }
        catch (IOException)
        {
            return Normalize(new SubtitleReviewSettings());
        }
    }

    public void Save(SubtitleReviewSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        Directory.CreateDirectory(_root);
        var temp = Path.Combine(_root, $".settings.{Guid.NewGuid():N}.tmp");
        try
        {
            var normalized = Normalize(settings);
            var json = JsonSerializer.Serialize(normalized, new JsonSerializerOptions
            {
                WriteIndented = true,
            });
            File.WriteAllText(temp, json, new UTF8Encoding(false));
            File.Move(temp, _path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp))
                File.Delete(temp);
        }
    }

    private static SubtitleReviewSettings Normalize(SubtitleReviewSettings settings)
    {
        var promptTemplate = (settings.PromptTemplate ?? string.Empty).Trim();
        return settings with
        {
            GlossaryPath = (settings.GlossaryPath ?? string.Empty).Trim(),
            ChunkSize = Math.Clamp(settings.ChunkSize, 1, 500),
            EvidencePath = (settings.EvidencePath ?? string.Empty).Trim(),
            PromptTemplate = promptTemplate.Length == 0
                ? ReviewResources.PromptTemplate.Trim()
                : promptTemplate,
            ReferenceMaterial = (settings.ReferenceMaterial ?? string.Empty).Trim(),
            ReviewReferenceFiles = (settings.ReviewReferenceFiles ?? [])
                .Select(Path.GetFileName)
                .Where(name => !string.IsNullOrWhiteSpace(name) &&
                    string.Equals(Path.GetExtension(name), ".txt", StringComparison.OrdinalIgnoreCase))
                .Select(name => name!)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray(),
        };
    }

    public IReadOnlyList<string> AttachReviewReferenceFiles(IEnumerable<string> sourcePaths)
    {
        ArgumentNullException.ThrowIfNull(sourcePaths);
        var sources = sourcePaths.Select(Path.GetFullPath).ToArray();
        foreach (var source in sources)
        {
            if (!File.Exists(source))
                throw new FileNotFoundException("找不到校閱文本參考檔案。", source);
            if (!string.Equals(Path.GetExtension(source), ".txt", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("校閱文本參考只接受 TXT 檔案。");
        }

        Directory.CreateDirectory(_referenceRoot);
        var settings = Load();
        var names = (settings.ReviewReferenceFiles ?? []).ToList();
        foreach (var source in sources)
        {
            var name = Path.GetFileName(source);
            var destination = Path.Combine(_referenceRoot, name);
            if (!string.Equals(source, destination, StringComparison.OrdinalIgnoreCase))
            {
                var temp = Path.Combine(_referenceRoot, $".{Guid.NewGuid():N}.tmp");
                try
                {
                    File.Copy(source, temp, overwrite: true);
                    File.Move(temp, destination, overwrite: true);
                }
                finally
                {
                    if (File.Exists(temp))
                        File.Delete(temp);
                }
            }

            names.RemoveAll(existing => string.Equals(existing, name, StringComparison.OrdinalIgnoreCase));
            names.Add(name);
        }

        Save(settings with { ReviewReferenceFiles = names.ToArray() });
        return names;
    }

    public IReadOnlyList<string> ResolveReviewReferenceFiles()
    {
        var paths = (Load().ReviewReferenceFiles ?? [])
            .Select(name => Path.Combine(_referenceRoot, Path.GetFileName(name)))
            .ToArray();
        foreach (var path in paths)
        {
            if (!File.Exists(path))
                throw new FileNotFoundException("SE 內保存的校閱文本參考檔案遺失。", path);
        }
        return paths;
    }

    public void ExportReviewReferenceFiles(string outputDirectory)
    {
        Directory.CreateDirectory(outputDirectory);
        foreach (var source in ResolveReviewReferenceFiles())
            File.Copy(source, Path.Combine(outputDirectory, Path.GetFileName(source)), overwrite: true);
    }

    public IReadOnlyList<string> ClearReviewReferenceFiles()
    {
        var settings = Load();
        var paths = (settings.ReviewReferenceFiles ?? [])
            .Select(name => Path.Combine(_referenceRoot, Path.GetFileName(name)))
            .ToArray();
        Save(settings with { ReviewReferenceFiles = [] });
        var undeleted = new List<string>();
        foreach (var path in paths)
        {
            try
            {
                if (File.Exists(path))
                    File.Delete(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                undeleted.Add(Path.GetFileName(path));
            }
        }
        return undeleted;
    }

    public string ResolveGlossaryPath(string? configuredPath)
    {
        var configured = (configuredPath ?? string.Empty).Trim();
        if (configured.Length > 0 && File.Exists(configured))
            return Path.GetFullPath(configured);

        var userRoot = Path.Combine(_root, "glossary");
        Directory.CreateDirectory(userRoot);
        ReviewResources.EnsureGlossarySeeds(userRoot);

        var preferred = Path.Combine(userRoot, "glossary_ai.csv");
        if (File.Exists(preferred) && GlossaryRepository.Read(preferred).Count > 0)
            return preferred;
        return Path.Combine(userRoot, "glossary.csv");
    }
}
