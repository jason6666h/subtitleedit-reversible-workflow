using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace SubtitleReview.Core;

internal static partial class ReviewMarkdownParser
{
    public static ParsedReviewResponse Parse(
        IReadOnlyList<string> responses,
        IReadOnlyCollection<int> expectedIds) =>
        ParseSet(responses, expectedIds).Combined;

    public static ParsedReviewResponseSet ParseSet(
        IReadOnlyList<string> responses,
        IReadOnlyCollection<int> expectedIds)
    {
        ArgumentNullException.ThrowIfNull(responses);
        ArgumentNullException.ThrowIfNull(expectedIds);
        if (responses.Count == 0)
            throw new ReviewEngineException("INVALID_REQUEST", "請貼上 AI 回覆或匯入 MD。");

        var known = expectedIds.ToHashSet();
        var merged = new Dictionary<int, string>();
        var occurrenceCounts = new Dictionary<int, int>();
        var variants = new Dictionary<int, List<string>>();
        var glossary = new List<GlossarySuggestion>();
        var documents = new List<ParsedReviewDocument>(responses.Count);

        foreach (var response in responses)
        {
            if (response is null)
                throw new ReviewEngineException("INVALID_REQUEST", "請貼上 AI 回覆或匯入 MD。");

            var rows = ParseReviewRows(response);
            var occurrences = ParseReviewOccurrences(response);
            var suggestions = ParseGlossarySuggestions(response);
            documents.Add(new ParsedReviewDocument(
                rows,
                rows.Count == 0 ? "沒有從 AI 回覆解析到校閱表格。" : null));

            foreach (var pair in rows)
                merged[pair.Key] = pair.Value;

            foreach (var (reviewId, text) in occurrences)
            {
                occurrenceCounts[reviewId] = occurrenceCounts.GetValueOrDefault(reviewId) + 1;
                if (!string.IsNullOrWhiteSpace(text))
                {
                    if (!variants.TryGetValue(reviewId, out var values))
                    {
                        values = [];
                        variants[reviewId] = values;
                    }
                    if (!values.Contains(text, StringComparer.Ordinal))
                        values.Add(text);
                }
            }
            glossary.AddRange(suggestions);
        }

        if (merged.Count == 0)
            throw new ReviewEngineException("PARSE_ERROR", "沒有從 AI 回覆解析到校閱表格。");

        var present = merged.Keys.ToHashSet();
        var duplicateIds = occurrenceCounts
            .Where(pair => pair.Value > 1)
            .Select(pair => pair.Key)
            .Order()
            .ToArray();
        var conflicts = variants
            .Where(pair => pair.Value.Count > 1)
            .ToDictionary(
                pair => pair.Key,
                pair => (IReadOnlyList<string>)pair.Value.ToArray());
        var combined = new ParsedReviewResponse(
            merged,
            responses.Count,
            duplicateIds,
            conflicts,
            known.Except(present).Order().ToArray(),
            present.Except(known).Order().ToArray(),
            glossary);
        return new ParsedReviewResponseSet(combined, documents);
    }

    private static IReadOnlyDictionary<int, string> ParseReviewRows(string responseText)
    {
        var results = new Dictionary<int, string>();
        Dictionary<string, int>? header = null;
        var reviewText = ExtractReviewSection(NormalizeResponse(responseText));

        foreach (var rawLine in reviewText.Split('\n'))
        {
            var line = rawLine.Trim().Replace('｜', '|');
            if (line.Length == 0 || !line.Contains('|'))
                continue;
            var cells = SplitMarkdownRow(line);
            if (cells.Count < 2)
                continue;
            var normalized = cells.Select(NormalizeHeader).ToArray();
            if (IsSeparatorRow(normalized))
                continue;

            var discovered = FindReviewHeaderIndexes(normalized);
            if (discovered is not null)
            {
                header = discovered;
                continue;
            }

            var idIndex = header is null ? 0 : header.GetValueOrDefault("編號", 0);
            var reviewedIndex = header is null
                ? (cells.Count >= 3 ? 2 : cells.Count - 1)
                : header.GetValueOrDefault("校閱後文字", cells.Count - 1);
            if (idIndex >= cells.Count || reviewedIndex >= cells.Count)
                continue;

            var reviewId = ParseReviewId(cells[idIndex]);
            if (reviewId is null)
                continue;
            var reviewedText = CleanAiCell(cells[reviewedIndex]);
            if (reviewedText.Length > 0 || !results.ContainsKey(reviewId.Value))
                results[reviewId.Value] = reviewedText;
        }
        return results;
    }

    private static IReadOnlyList<(int ReviewId, string Text)> ParseReviewOccurrences(string responseText)
    {
        Dictionary<string, int>? header = null;
        var output = new List<(int, string)>();
        var section = ExtractReviewSection(NormalizeResponse(responseText));

        foreach (var rawLine in section.Split('\n'))
        {
            var line = rawLine.Trim().Replace('｜', '|');
            if (line.Length == 0 || !line.Contains('|'))
                continue;
            var cells = SplitMarkdownRow(line);
            if (cells.Count < 2)
                continue;
            var normalized = cells.Select(NormalizeHeader).ToArray();
            if (IsSeparatorRow(normalized))
                continue;

            var discovered = FindReviewHeaderIndexes(normalized);
            if (discovered is not null)
            {
                header = discovered;
                continue;
            }

            var idIndex = header?.GetValueOrDefault("編號", 0) ?? 0;
            var textIndex = header?.GetValueOrDefault("校閱後文字", cells.Count - 1)
                ?? (cells.Count >= 3 ? 2 : cells.Count - 1);
            if (idIndex >= cells.Count || textIndex >= cells.Count)
                continue;
            var reviewId = ParseReviewId(cells[idIndex]);
            if (reviewId is not null)
                output.Add((reviewId.Value, CleanAiCell(cells[textIndex])));
        }
        return output;
    }

    private static IReadOnlyList<GlossarySuggestion> ParseGlossarySuggestions(string responseText)
    {
        var suggestions = new List<GlossarySuggestion>();
        Dictionary<string, int?>? header = null;

        foreach (var rawLine in NormalizeResponse(responseText).Split('\n'))
        {
            var line = rawLine.Trim().Replace('｜', '|');
            if (line.Length == 0 || !line.Contains('|'))
                continue;
            var cells = SplitMarkdownRow(line);
            if (cells.Count < 2)
                continue;
            var normalized = cells.Select(NormalizeHeader).ToArray();
            if (IsSeparatorRow(normalized))
                continue;

            var sourceHeader = HeaderIndexContaining(normalized, "錯誤詞")
                ?? HeaderIndexContaining(normalized, "source");
            var targetHeader = HeaderIndexContaining(normalized, "建議", "正確詞")
                ?? HeaderIndexContaining(normalized, "正確詞")
                ?? HeaderIndexContaining(normalized, "target");
            if (sourceHeader is not null && targetHeader is not null)
            {
                header = new Dictionary<string, int?>
                {
                    ["source"] = sourceHeader,
                    ["target"] = targetHeader,
                    ["category"] = HeaderIndexContaining(normalized, "類別")
                        ?? HeaderIndexContaining(normalized, "category"),
                    ["evidence"] = HeaderIndexContaining(normalized, "證據", "編號")
                        ?? HeaderIndexContaining(normalized, "evidence", "id"),
                    ["confidence"] = HeaderIndexContaining(normalized, "信心")
                        ?? HeaderIndexContaining(normalized, "confidence"),
                    ["note"] = HeaderIndexContaining(normalized, "備註")
                        ?? HeaderIndexContaining(normalized, "note"),
                };
                continue;
            }

            if (FindReviewHeaderIndexes(normalized) is not null)
            {
                header = null;
                continue;
            }
            if (header is null)
                continue;

            string Cell(string name, string fallback = "")
            {
                if (!header.TryGetValue(name, out var index) ||
                    index is null ||
                    index.Value >= cells.Count)
                    return fallback;
                return CleanAiCell(cells[index.Value]);
            }

            var source = Cell("source");
            var target = Cell("target");
            if (source.Length == 0 || target.Length == 0)
                continue;
            var category = Cell("category");
            suggestions.Add(new GlossarySuggestion(
                source,
                target,
                category.Length > 0 ? category : "AI review suggestion",
                Cell("evidence"),
                Cell("confidence"),
                Cell("note")));
        }
        return suggestions;
    }

    internal static List<string> SplitMarkdownRow(string line)
    {
        var text = line.Trim().Replace('｜', '|');
        if (text.StartsWith('|'))
            text = text[1..];
        if (text.EndsWith('|'))
            text = text[..^1];

        var cells = new List<string>();
        var current = new StringBuilder();
        var escaped = false;
        foreach (var ch in text)
        {
            if (ch == '|' && !escaped)
            {
                cells.Add(current.ToString().Trim());
                current.Clear();
            }
            else
            {
                current.Append(ch);
            }
            escaped = ch == '\\' && !escaped;
        }
        cells.Add(current.ToString().Trim());
        return cells;
    }

    internal static string CleanAiCell(string value)
    {
        var text = value.Trim();
        text = LeadingFenceRegex().Replace(text, string.Empty);
        text = TrailingFenceRegex().Replace(text, string.Empty);
        return text
            .Replace("<br>", "\n", StringComparison.Ordinal)
            .Replace("<br/>", "\n", StringComparison.Ordinal)
            .Replace("<br />", "\n", StringComparison.Ordinal)
            .Replace("\\|", "|", StringComparison.Ordinal)
            .Replace("\\\\", "\\", StringComparison.Ordinal)
            .Trim();
    }

    internal static string NormalizeResponse(string response)
    {
        var text = (response ?? string.Empty)
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace("\r", "\n", StringComparison.Ordinal)
            .Replace('｜', '|')
            .Trim();
        text = ResponseLeadingFenceRegex().Replace(text, string.Empty);
        text = ResponseTrailingFenceRegex().Replace(text, string.Empty);
        return text;
    }

    private static string ExtractReviewSection(string text)
    {
        if (text.Contains("## Review Results", StringComparison.OrdinalIgnoreCase))
            return ExtractLastSection(text, "## Review Results", "## Suggested Glossary Additions");
        return ExtractLastSection(text, "## 校閱結果", "## 建議新增詞彙校正");
    }

    internal static string ExtractLastSection(
        string text,
        string startHeading,
        string? endHeading = null)
    {
        var lines = text.Split('\n');
        int? startIndex = null;
        for (var index = 0; index < lines.Length; index++)
            if (lines[index].Trim() == startHeading)
                startIndex = index + 1;
        if (startIndex is null)
            return text;

        var endIndex = lines.Length;
        if (endHeading is not null)
        {
            for (var index = startIndex.Value; index < lines.Length; index++)
            {
                if (lines[index].Trim() != endHeading)
                    continue;
                endIndex = index;
                break;
            }
        }

        var section = string.Join("\n", lines[startIndex.Value..endIndex]).Trim();
        return section.Length == 0 ? text : section;
    }

    private static Dictionary<string, int>? FindReviewHeaderIndexes(IReadOnlyList<string> normalized)
    {
        var idHeader = HeaderIndexContaining(normalized, "編號")
            ?? HeaderIndexContaining(normalized, "序號")
            ?? HeaderIndexContaining(normalized, "id");
        var reviewedHeader =
            HeaderIndexContaining(normalized, "校閱後", "文字") ??
            HeaderIndexContaining(normalized, "校正後", "文字") ??
            HeaderIndexContaining(normalized, "修正後", "文字") ??
            HeaderIndexContaining(normalized, "修改後", "文字") ??
            HeaderIndexContaining(normalized, "校閱後文字") ??
            HeaderIndexContaining(normalized, "校正後文字") ??
            HeaderIndexContaining(normalized, "修正後文字") ??
            HeaderIndexContaining(normalized, "reviewed", "text") ??
            HeaderIndexContaining(normalized, "corrected", "text");
        return idHeader is null || reviewedHeader is null
            ? null
            : new Dictionary<string, int>
            {
                ["編號"] = idHeader.Value,
                ["校閱後文字"] = reviewedHeader.Value,
            };
    }

    private static int? HeaderIndexContaining(IReadOnlyList<string> cells, params string[] parts)
    {
        for (var index = 0; index < cells.Count; index++)
            if (parts.All(part => cells[index].Contains(part, StringComparison.Ordinal)))
                return index;
        return null;
    }

    private static int? ParseReviewId(string value)
    {
        var normalized = CleanAiCell(value).Normalize(NormalizationForm.FormKC);
        var match = DigitsRegex().Match(normalized);
        return match.Success && int.TryParse(
            match.Value,
            NumberStyles.None,
            CultureInfo.InvariantCulture,
            out var result)
            ? result
            : null;
    }

    private static string NormalizeHeader(string value) =>
        WhitespaceRegex().Replace(value.Trim().ToLowerInvariant(), string.Empty);

    private static bool IsSeparatorRow(IEnumerable<string> cells)
    {
        var nonEmpty = cells.Where(cell => cell.Length > 0).ToArray();
        return nonEmpty.Length > 0 &&
               nonEmpty.All(cell => cell.All(ch => ch is '-' or ':'));
    }

    [GeneratedRegex(@"^\x60{3}(?:markdown|md|csv)?\s*", RegexOptions.IgnoreCase)]
    private static partial Regex LeadingFenceRegex();

    [GeneratedRegex(@"\s*\x60{3}$", RegexOptions.IgnoreCase)]
    private static partial Regex TrailingFenceRegex();

    [GeneratedRegex(@"^\x60{3}(?:markdown|md)?\s*\n", RegexOptions.IgnoreCase)]
    private static partial Regex ResponseLeadingFenceRegex();

    [GeneratedRegex(@"\n\x60{3}\s*$")]
    private static partial Regex ResponseTrailingFenceRegex();

    [GeneratedRegex(@"\d+")]
    private static partial Regex DigitsRegex();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();
}
