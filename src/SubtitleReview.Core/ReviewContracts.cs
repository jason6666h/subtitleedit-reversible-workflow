namespace SubtitleReview.Core;

public sealed record ReviewRowSnapshot
{
    public required Guid Id { get; init; }
    public required int Position { get; init; }
    public required int Number { get; init; }
    public required string Text { get; init; }
    public string? OriginalText { get; init; }
    public long StartTicks { get; init; }
    public long EndTicks { get; init; }
    public string? Style { get; init; }
    public string? Actor { get; init; }
    public string? Bookmark { get; init; }
    public bool Forced { get; init; }
    public bool IsReferenceOnly { get; init; }
    public Guid? ReferenceParagraphId { get; init; }
    public int Layer { get; init; }
    public string? Language { get; init; }
    public string? Region { get; init; }
    public string? Extra { get; init; }
    public string? Effect { get; init; }
    public bool IsComment { get; init; }
    public string? MarginL { get; init; }
    public string? MarginR { get; init; }
    public string? MarginV { get; init; }
    public bool NewSection { get; init; }
    public ReviewParagraphSnapshot? Paragraph { get; init; }
}

public sealed record ReviewParagraphSnapshot
{
    public Guid? Id { get; init; }
    public int Number { get; init; }
    public string? Text { get; init; }
    public long StartTicks { get; init; }
    public long EndTicks { get; init; }
    public bool Forced { get; init; }
    public string? Extra { get; init; }
    public bool IsComment { get; init; }
    public string? Actor { get; init; }
    public string? Region { get; init; }
    public string? MarginL { get; init; }
    public string? MarginR { get; init; }
    public string? MarginV { get; init; }
    public string? Effect { get; init; }
    public int Layer { get; init; }
    public string? Language { get; init; }
    public string? Style { get; init; }
    public bool NewSection { get; init; }
    public string? Bookmark { get; init; }
}

public sealed record ReviewDocumentSnapshot
{
    private IReadOnlyList<ReviewRowSnapshot> _rows = [];
    public string? SourcePath { get; init; }
    public string Format { get; init; }
    public IReadOnlyList<ReviewRowSnapshot> Rows
    {
        get => _rows;
        init => _rows = Array.AsReadOnly(value.ToArray());
    }
    public string UndoHash { get; init; }

    public ReviewDocumentSnapshot(string? sourcePath, string format, IReadOnlyList<ReviewRowSnapshot> rows, string undoHash)
    {
        SourcePath = sourcePath;
        Format = format;
        Rows = rows;
        UndoHash = undoHash;
    }
}

public sealed record ReviewSelectionRow(int ReviewId, Guid RowId);

public sealed record ReviewSelection
{
    private IReadOnlyList<ReviewSelectionRow> _rows = [];
    public IReadOnlyList<ReviewSelectionRow> Rows
    {
        get => _rows;
        init => _rows = Array.AsReadOnly(value.ToArray());
    }

    public ReviewSelection(IReadOnlyList<ReviewSelectionRow> rows) => Rows = rows;

    public static ReviewSelection Create(ReviewDocumentSnapshot snapshot, IEnumerable<Guid> selectedIds)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(selectedIds);
        var selected = selectedIds.ToHashSet();
        var rows = snapshot.Rows.Where(row => selected.Contains(row.Id) &&
            !row.IsReferenceOnly && !string.IsNullOrWhiteSpace(row.Text))
            .Select((row, index) => new ReviewSelectionRow(index + 1, row.Id)).ToArray();
        return new(rows);
    }
}
