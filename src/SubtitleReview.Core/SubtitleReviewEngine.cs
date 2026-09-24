using System.Security.Cryptography;

namespace SubtitleReview.Core;

public sealed class SubtitleReviewEngine
{
    public ReviewPromptResult CreateReviewRequest(
        IReadOnlyList<ReviewInputRow> rows,
        int chunkSize,
        string glossaryPath,
        string? evidencePath,
        string? promptTemplate = null,
        string? referenceMaterial = null,
        IReadOnlyList<string>? reviewReferenceFileNames = null)
    {
        ReviewPromptBuilder.ValidateRows(rows);
        var glossary = GlossaryRepository.Read(glossaryPath);
        var protectedPhrases = LoadProtectedPhrases(glossaryPath);
        var evidence = ReviewEvidenceAnalyzer.Match(rows, evidencePath, glossary);
        return ReviewPromptBuilder.Create(
            rows,
            chunkSize,
            glossary,
            protectedPhrases,
            evidence,
            promptTemplate,
            referenceMaterial,
            reviewReferenceFileNames);
    }

    public ParsedReviewResponse ParseAiResponse(
        IReadOnlyList<string> responses,
        IReadOnlyCollection<int> expectedIds) =>
        ReviewMarkdownParser.Parse(responses, expectedIds);

    public ParsedReviewResponseSet ParseAiResponseSet(
        IReadOnlyList<string> responses,
        IReadOnlyCollection<int> expectedIds) =>
        ReviewMarkdownParser.ParseSet(responses, expectedIds);

    public ReviewPreviewResult PreparePreview(
        IReadOnlyList<ReviewInputRow> rows,
        IReadOnlyList<string> responses,
        string glossaryPath,
        string? evidencePath)
    {
        ReviewPromptBuilder.ValidateRows(rows);
        if (responses.Count == 0)
            throw new ReviewEngineException("INVALID_REQUEST", "請貼上 AI 回覆或匯入 MD。");

        var parsed = ReviewMarkdownParser.Parse(
            responses,
            rows.Select(row => row.ReviewId).ToArray());
        var glossary = GlossaryRepository.Read(glossaryPath);
        var evidence = ReviewEvidenceAnalyzer.Match(rows, evidencePath, glossary);
        var preview = new List<ReviewPreviewRow>(rows.Count);

        foreach (var row in rows)
        {
            parsed.Results.TryGetValue(row.ReviewId, out var aiText);
            aiText ??= string.Empty;
            parsed.Conflicts.TryGetValue(row.ReviewId, out var variants);
            var evidenceItem = evidence.TryGetValue(row.ReviewId, out var item)
                ? item
                : ("not_provided", ReviewEvidencePacket.Empty);

            preview.Add(new ReviewPreviewRow(
                row.ReviewId,
                row.Text,
                aiText,
                variants is not null && variants.Count > 0,
                variants ?? [],
                evidenceItem.Item1,
                evidenceItem.Item2));
        }

        return new ReviewPreviewResult(
            preview,
            parsed.GlossaryCandidates,
            parsed.Documents,
            parsed.DuplicateIds,
            parsed.Conflicts,
            parsed.MissingIds,
            parsed.UnknownIds,
            parsed.MissingIds.Count == 0 && parsed.Conflicts.Count == 0);
    }

    public GlossaryLoadResult LoadGlossary(string glossaryPath)
    {
        var path = Path.GetFullPath(glossaryPath);
        return new GlossaryLoadResult(
            path,
            GlossaryRepository.Read(path),
            LoadProtectedPhrases(path));
    }

    public GlossarySaveResult SaveGlossary(
        string glossaryPath,
        IReadOnlyList<GlossarySuggestion> candidates,
        bool confirmed)
    {
        if (!confirmed)
            throw new ReviewEngineException(
                "INVALID_REQUEST",
                "儲存詞彙前必須由使用者明確確認。");
        if (candidates.Count == 0)
            throw new ReviewEngineException(
                "INVALID_REQUEST",
                "沒有要儲存的詞彙候選。");

        var path = Path.GetFullPath(glossaryPath);
        var rows = GlossaryRepository.Read(path).ToList();
        var existing = rows
            .Select(row => (row.Source, row.Target))
            .ToHashSet();
        var added = new List<GlossaryRow>();

        foreach (var candidate in candidates)
        {
            var source = candidate.Source.Trim();
            var target = candidate.Target.Trim();
            if (source.Length == 0 || target.Length == 0)
                throw new ReviewEngineException(
                    "INVALID_REQUEST",
                    "詞彙候選缺少錯誤詞或正確詞。");
            if (!existing.Add((source, target)))
                continue;

            var row = new GlossaryRow(
                "1",
                source,
                target,
                candidate.Category.Trim(),
                candidate.Note.Trim(),
                candidate.EvidenceIds.Trim());
            rows.Add(row);
            added.Add(row);
        }

        if (added.Count > 0)
            GlossaryRepository.Append(path, added);
        return new GlossarySaveResult(path, added.Count, added, rows);
    }

    public static string GetEngineFingerprint()
    {
        var assembly = typeof(SubtitleReviewEngine).Assembly;
        var path = assembly.Location;
        if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
            return Convert.ToHexString(
                SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();

        var name = assembly.GetName();
        return $"{name.Name}:{name.Version}";
    }

    private static IReadOnlyList<string> LoadProtectedPhrases(string glossaryPath)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(glossaryPath)) ?? string.Empty;
        var path = Path.Combine(directory, "protected_phrases.json");
        return GlossaryRepository.LoadProtectedPhrases(path);
    }
}
