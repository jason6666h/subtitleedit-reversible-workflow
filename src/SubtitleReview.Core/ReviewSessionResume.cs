using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SubtitleReview.Core;

public static class ReviewSessionResume
{
    public static string GetStableDocumentFingerprint(ReviewDocumentSnapshot document)
    {
        ArgumentNullException.ThrowIfNull(document);
        var positionById = document.Rows
            .Where(row => row.Id != Guid.Empty)
            .GroupBy(row => row.Id)
            .ToDictionary(group => group.Key, group => group.First().Position);
        var canonical = new
        {
            source_path = document.SourcePath,
            format = document.Format,
            rows = document.Rows.Select(row => RowForFingerprint(row, positionById)).ToArray(),
        };
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(canonical));
        return Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    }

    public static string GetSessionFileName(
        ReviewDocumentSnapshot document,
        ReviewSelection selection)
    {
        ArgumentNullException.ThrowIfNull(selection);
        var positionById = document.Rows.ToDictionary(row => row.Id, row => row.Position);

        var mapping = selection.Rows.Select(row =>
        {
            if (!positionById.TryGetValue(row.RowId, out var position))
                throw new InvalidDataException("Review selection does not belong to the document.");
            return new { review_id = row.ReviewId, position };
        }).ToArray();

        var canonical = JsonSerializer.Serialize(new
        {
            document = GetStableDocumentFingerprint(document),
            selection = mapping,
        });
        var hash = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
        return $"review-{hash[..32]}.json";
    }

    public static bool TryRebind(
        ReviewSession session,
        ReviewDocumentSnapshot current,
        string backendFingerprint,
        string engineFingerprint,
        out ReviewSession rebound,
        out string? blockingReason)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(current);

        rebound = session;
        blockingReason = null;
        if (!string.Equals(session.BackendFingerprint, backendFingerprint, StringComparison.Ordinal) ||
            !string.Equals(session.EngineFingerprint, engineFingerprint, StringComparison.Ordinal))
        {
            blockingReason = "Review engine fingerprint changed";
            return false;
        }

        if (!string.Equals(
                GetStableDocumentFingerprint(session.Document),
                GetStableDocumentFingerprint(current),
                StringComparison.Ordinal))
        {
            blockingReason = "Document content or metadata changed";
            return false;
        }

        var oldRowsById = session.Document.Rows.ToDictionary(row => row.Id);
        var reboundRows = new List<ReviewSelectionRow>();
        foreach (var selected in session.Decisions.Selection.Rows)
        {
            if (!oldRowsById.TryGetValue(selected.RowId, out var oldRow) ||
                oldRow.Position <= 0 ||
                oldRow.Position > current.Rows.Count)
            {
                blockingReason = "Stored review selection is invalid";
                return false;
            }

            var currentRow = current.Rows[oldRow.Position - 1];
            if (currentRow.IsReferenceOnly || string.IsNullOrWhiteSpace(currentRow.Text))
            {
                blockingReason = "Stored review target is no longer editable";
                return false;
            }

            reboundRows.Add(new ReviewSelectionRow(selected.ReviewId, currentRow.Id));
        }

        var selection = new ReviewSelection(reboundRows);
        rebound = session with
        {
            Document = current,
            Decisions = new ReviewDecisions(selection, session.Decisions.Items),
        };
        return true;
    }

    private static object RowForFingerprint(
        ReviewRowSnapshot row,
        IReadOnlyDictionary<Guid, int> positionById)
    {
        var referencePosition = row.ReferenceParagraphId is { } referenceId &&
                                positionById.TryGetValue(referenceId, out var position)
            ? position
            : (int?)null;
        return new
        {
        row.Position,
        row.Number,
        row.Text,
        row.OriginalText,
        row.StartTicks,
        row.EndTicks,
        row.Style,
        row.Actor,
        row.Bookmark,
        row.Forced,
        row.IsReferenceOnly,
        HasReferenceParagraph = row.ReferenceParagraphId.HasValue,
        ReferenceParagraphPosition = referencePosition,

        row.Layer,
        row.Language,
        row.Region,
        row.Extra,
        row.Effect,
        row.IsComment,
        row.MarginL,
        row.MarginR,
        row.MarginV,
        row.NewSection,
        paragraph = row.Paragraph is null ? null : new
        {
            row.Paragraph.Number,
            row.Paragraph.Text,
            row.Paragraph.StartTicks,
            row.Paragraph.EndTicks,
            row.Paragraph.Forced,
            row.Paragraph.Extra,
            row.Paragraph.IsComment,
            row.Paragraph.Actor,
            row.Paragraph.Region,
            row.Paragraph.MarginL,
            row.Paragraph.MarginR,
            row.Paragraph.MarginV,
            row.Paragraph.Effect,
            row.Paragraph.Layer,
            row.Paragraph.Language,

            row.Paragraph.Style,
            row.Paragraph.NewSection,
            row.Paragraph.Bookmark,
        },
        };
    }
}
