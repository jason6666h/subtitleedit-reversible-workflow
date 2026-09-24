namespace SubtitleReview.Core;

public sealed record ReviewInputRow(
    int ReviewId,
    string Text,
    double StartMilliseconds,
    double EndMilliseconds);

public sealed record ReviewPromptChunk(string Markdown);

public sealed record ReviewPromptResult(
    IReadOnlyList<ReviewPromptChunk> Prompts);

public sealed record GlossaryRow(
    string Enabled,
    string Source,
    string Target,
    string Category = "",
    string Note = "",
    string Examples = "");

public sealed record GlossarySuggestion(
    string Source,
    string Target,
    string Category,
    string EvidenceIds,
    string Confidence,
    string Note);

public sealed record GlossarySaveResult(
    string Path,
    int AddedCount,
    IReadOnlyList<GlossaryRow> Added,
    IReadOnlyList<GlossaryRow> Rows);

public sealed record GlossaryLoadResult(
    string Path,
    IReadOnlyList<GlossaryRow> Rows,
    IReadOnlyList<string> ProtectedPhrases);

public sealed record ParsedReviewResponse(
    IReadOnlyDictionary<int, string> Results,
    int Documents,
    IReadOnlyList<int> DuplicateIds,
    IReadOnlyDictionary<int, IReadOnlyList<string>> Conflicts,
    IReadOnlyList<int> MissingIds,
    IReadOnlyList<int> UnknownIds,
    IReadOnlyList<GlossarySuggestion> GlossaryCandidates);

public sealed record ParsedReviewDocument(
    IReadOnlyDictionary<int, string> Results,
    string? Error);

public sealed record ParsedReviewResponseSet(
    ParsedReviewResponse Combined,
    IReadOnlyList<ParsedReviewDocument> Documents);

public sealed record ReviewEvidenceFlag(
    string Code,
    string Severity,
    int Weight,
    string Detail);

public sealed record ReviewEvidenceAlternative(
    string Source,
    string ObservationId,
    string Engine,
    int? Pass,
    string Text,
    IReadOnlyList<string> AnomalyCodes,
    double? Confidence,
    string DecodeMode);

public sealed record ReviewGlossaryEvidence(
    string Source,
    string Target,
    string Category,
    IReadOnlyList<string> PrimaryHits,
    IReadOnlyList<string> AlternativeHits);

public sealed record ReviewEvidenceRisk(
    int Score,
    string Priority,
    IReadOnlyList<string> FlagCodes,
    IReadOnlyList<ReviewEvidenceFlag> Flags);

public sealed record ReviewEvidencePacket(
    bool Available,
    string PrimaryText,
    int? SelectedPass,
    double? Confidence,
    IReadOnlyList<string> AnomalyCodes,
    IReadOnlyList<string> ObservedAnomalyCodes,
    IReadOnlyList<ReviewEvidenceAlternative> Alternatives,
    double DurationSeconds,
    double CharactersPerSecond,
    string SecondPassStatus,
    string SelectionReason,
    string ConfidenceTier,
    IReadOnlyList<string> ReviewReasons,
    IReadOnlyList<ReviewGlossaryEvidence> GlossaryEvidence,
    ReviewEvidenceRisk Risk)
{
    public static ReviewEvidencePacket Empty { get; } = new(
        false, "", null, null, [], [], [], 0, 0, "", "", "", [], [],
        new ReviewEvidenceRisk(0, "normal", [], []));
}

public sealed record ReviewPreviewRow(
    int ReviewId,
    string OriginalText,
    string AiReviewText,
    bool ResponseConflict,
    IReadOnlyList<string> ResponseVariants,
    string EvidenceStatus,
    ReviewEvidencePacket Evidence);

public sealed record ReviewPreviewResult(
    IReadOnlyList<ReviewPreviewRow> Rows,
    IReadOnlyList<GlossarySuggestion> GlossaryCandidates,
    int Documents,
    IReadOnlyList<int> DuplicateIds,
    IReadOnlyDictionary<int, IReadOnlyList<string>> Conflicts,
    IReadOnlyList<int> MissingIds,
    IReadOnlyList<int> UnknownIds,
    bool Complete);

public sealed class ReviewEngineException : Exception
{
    public string Code { get; }

    public ReviewEngineException(string code, string message) : base(message) =>
        Code = code;
}
