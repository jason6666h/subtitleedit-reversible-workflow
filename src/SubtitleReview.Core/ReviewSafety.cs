using System.Text.RegularExpressions;

namespace SubtitleReview.Core;

public enum ReviewDecisionKind { Pending, Apply, Keep, Manual }

public sealed record ReviewDecision(int ReviewId, ReviewDecisionKind Kind, string? SuggestedText = null, string? ManualText = null);
public sealed record ReviewDecisions(ReviewSelection Selection, IReadOnlyList<ReviewDecision> Items);
public sealed record ReviewTextChange(Guid RowId, string Text);
public sealed record ReviewValidation(string? BlockingReason, IReadOnlyList<ReviewTextChange> Changes)
{
    public bool IsValid => BlockingReason is null;
}

public static class ReviewSafety
{
    private static readonly Regex FormatTagRegex = new(
        @"(\{\\[^}]+\}|</?[^>]+>|\\[Nnh])",
        RegexOptions.Compiled);

    public static bool FormatTagsMatch(string original, string proposed) =>
        FormatTagRegex.Matches(original).Select(match => match.Value)
            .SequenceEqual(FormatTagRegex.Matches(proposed).Select(match => match.Value));

    public static ReviewValidation Validate(ReviewDocumentSnapshot current, ReviewDocumentSnapshot original, ReviewDecisions decisions)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(original);
        ArgumentNullException.ThrowIfNull(decisions);

        static ReviewValidation Block(string reason) => new(reason, []);
        if (current.SourcePath != original.SourcePath || current.Format != original.Format ||
            current.UndoHash != original.UndoHash)
            return Block("Document identity changed");
        if (current.Rows.Count != original.Rows.Count)
            return Block("Document row count changed");
        for (var i = 0; i < original.Rows.Count; i++)
            if (current.Rows[i] != original.Rows[i])
                return Block($"Document row {i + 1} changed");

        var rowsById = new Dictionary<Guid, ReviewRowSnapshot>();
        for (var i = 0; i < original.Rows.Count; i++)
        {
            var row = original.Rows[i];
            if (row.Id == Guid.Empty || row.Position != i + 1 || !rowsById.TryAdd(row.Id, row))
                return Block("Invalid document row identity or position");
        }

        var selected = decisions.Selection.Rows;
        var targets = new Dictionary<int, ReviewRowSnapshot>();
        var lastPosition = 0;
        for (var i = 0; i < selected.Count; i++)
        {
            var target = selected[i];
            if (target.ReviewId != i + 1 || !rowsById.TryGetValue(target.RowId, out var row) ||
                row.Position <= lastPosition || row.IsReferenceOnly || string.IsNullOrWhiteSpace(row.Text))
                return Block("Invalid review ID or target row");
            targets.Add(target.ReviewId, row);
            lastPosition = row.Position;
        }

        if (decisions.Items.Count != selected.Count)
            return Block("Missing or extra review decision");
        var seen = new HashSet<int>();
        var changes = new List<ReviewTextChange>();
        foreach (var decision in decisions.Items)
        {
            if (!seen.Add(decision.ReviewId) || !targets.TryGetValue(decision.ReviewId, out var row))
                return Block("Duplicate or unknown review ID");
            if (decision.SuggestedText is not null && string.IsNullOrWhiteSpace(decision.SuggestedText))
                return Block("Empty AI suggestion");
            string? text = decision.Kind switch
            {
                ReviewDecisionKind.Pending or ReviewDecisionKind.Keep => null,
                ReviewDecisionKind.Apply => decision.SuggestedText,
                ReviewDecisionKind.Manual => decision.ManualText,
                _ => null,
            };
            if ((decision.Kind is ReviewDecisionKind.Apply or ReviewDecisionKind.Manual) &&
                string.IsNullOrWhiteSpace(text))
                return Block("Empty applied text");
            if (!Enum.IsDefined(decision.Kind))
                return Block("Invalid decision kind");
            if (decision.Kind is ReviewDecisionKind.Apply or ReviewDecisionKind.Manual &&
                !FormatTagsMatch(row.Text, text!))
                return Block("Applied text changed subtitle formatting");
            if (text is not null && text != row.Text)
                changes.Add(new(row.Id, text));
        }
        return new(null, changes);
    }
}
