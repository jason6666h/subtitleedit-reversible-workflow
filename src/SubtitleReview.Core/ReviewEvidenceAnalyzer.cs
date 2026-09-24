using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SubtitleReview.Core;

internal static partial class ReviewEvidenceAnalyzer
{
    private static readonly string[] NegationTerms =
        ["不", "無", "未", "非", "莫", "勿", "毋", "袂", "沒有", "不是"];

    private static readonly IReadOnlyDictionary<string, int> Weights =
        new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["missing_utterance"] = 100,
            ["unresolved_deletion"] = 95,
            ["repetition_hallucination"] = 95,
            ["generation_truncated"] = 90,
            ["invalid_generation_text"] = 90,
            ["second_pass_failed"] = 90,
            ["domain_term_disagreement"] = 85,
            ["negation_disagreement"] = 80,
            ["alternative_contains_more_content"] = 75,
            ["uncovered_audio_tail"] = 70,
            ["likely_deletion"] = 70,
            ["number_disagreement"] = 70,
            ["second_pass_pending"] = 60,
            ["low_confidence"] = 45,
            ["candidate_disagreement"] = 40,
        };

    private static readonly IReadOnlyDictionary<string, string> Labels =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["missing_utterance"] = "疑似整段漏辨",
            ["unresolved_deletion"] = "二次辨識仍疑似漏字",
            ["repetition_hallucination"] = "疑似重複幻覺",
            ["generation_truncated"] = "生成疑似截斷",
            ["invalid_generation_text"] = "生成文字異常",
            ["second_pass_failed"] = "二次辨識失敗",
            ["domain_term_disagreement"] = "專有詞候選分歧",
            ["negation_disagreement"] = "否定語義分歧",
            ["alternative_contains_more_content"] = "候選含較多內容",
            ["uncovered_audio_tail"] = "音訊尾端未覆蓋",
            ["likely_deletion"] = "低文字密度疑似漏字",
            ["number_disagreement"] = "數字候選分歧",
            ["second_pass_pending"] = "建議二次辨識但尚未執行",
            ["low_confidence"] = "辨識信心偏低",
            ["candidate_disagreement"] = "候選內容差異大",
        };

    public static IReadOnlyDictionary<int, (string Status, ReviewEvidencePacket Packet)> Match(
        IReadOnlyList<ReviewInputRow> rows,
        string? evidencePath,
        IReadOnlyList<GlossaryRow> glossaryRows)
    {
        if (string.IsNullOrWhiteSpace(evidencePath))
            return rows.ToDictionary(
                row => row.ReviewId,
                _ => ("not_provided", ReviewEvidencePacket.Empty));

        var path = Path.GetFullPath(evidencePath);
        var candidates = ReadEvidenceRows(path);
        var result = new Dictionary<int, (string, ReviewEvidencePacket)>();
        var lastIndex = -1;

        foreach (var row in rows)
        {
            var matches = new List<(int Index, JsonElement Value)>();
            var expectedText = NormalizeMatchText(row.Text);
            for (var index = 0; index < candidates.Count; index++)
            {
                var candidate = candidates[index];
                if (!TryGetDouble(candidate, "start", out var startSeconds) ||
                    !TryGetDouble(candidate, "end", out var endSeconds))
                    continue;

                var candidateText =
                    GetString(candidate, "corrected_prediction") ??
                    GetString(candidate, "prediction") ??
                    GetString(candidate, "text") ??
                    string.Empty;
                if (NormalizeMatchText(candidateText) == expectedText &&
                    Math.Abs(startSeconds * 1000 - row.StartMilliseconds) <= 50.0 &&
                    Math.Abs(endSeconds * 1000 - row.EndMilliseconds) <= 50.0)
                    matches.Add((index, candidate));
            }

            if (matches.Count != 1 || matches[0].Index <= lastIndex)
            {
                result[row.ReviewId] = (
                    matches.Count > 1 ? "ambiguous" : "unmatched",
                    ReviewEvidencePacket.Empty);
                continue;
            }

            var matched = matches[0];
            lastIndex = matched.Index;
            result[row.ReviewId] = (
                "matched",
                BuildPacket(matched.Value, row.Text, glossaryRows));
        }

        return result;
    }

    public static string RenderPromptEvidence(
        IReadOnlyList<ReviewInputRow> rows,
        IReadOnlyDictionary<int, (string Status, ReviewEvidencePacket Packet)> evidence)
    {
        var rendered = new List<string>();
        foreach (var row in rows)
        {
            if (!evidence.TryGetValue(row.ReviewId, out var item))
                continue;
            var packet = item.Packet;
            if (!packet.Available)
                continue;
            if (packet.Risk.Priority == "normal" && packet.Alternatives.Count == 0)
                continue;

            var codes = packet.Risk.FlagCodes.Count == 0
                ? "—"
                : string.Join(", ", packet.Risk.FlagCodes.Select(code =>
                    Labels.TryGetValue(code, out var label) ? label : code));
            var metrics = new List<string>
            {
                $"{FormatCompact(packet.DurationSeconds)}s",
                $"{FormatCompact(packet.CharactersPerSecond)}字每秒",
            };
            if (packet.Confidence is not null)
                metrics.Add($"confidence={FormatCompact(packet.Confidence.Value)}");
            if (!string.IsNullOrWhiteSpace(packet.SecondPassStatus))
                metrics.Add($"second-pass={packet.SecondPassStatus}");

            var alternatives = packet.Alternatives
                .Take(3)
                .Where(alt => !string.IsNullOrWhiteSpace(alt.Text))
                .Select(alt =>
                {
                    var label = !string.IsNullOrWhiteSpace(alt.Engine) && alt.Pass is not null
                        ? $"{alt.Engine}/pass_{alt.Pass}"
                        : !string.IsNullOrWhiteSpace(alt.Engine)
                            ? alt.Engine
                            : alt.Source;
                    return $"{label}:{alt.Text}";
                })
                .ToArray();
            var altText = alternatives.Length == 0
                ? "—"
                : string.Join("；", alternatives)
                    .Replace("|", "｜", StringComparison.Ordinal)
                    .Replace("\n", " ", StringComparison.Ordinal);

            var glossary = packet.GlossaryEvidence.Take(4)
                .Select(match => $"{match.Source}→{match.Target}")
                .ToArray();
            var glossaryText = glossary.Length == 0
                ? "—"
                : string.Join("；", glossary).Replace("|", "｜", StringComparison.Ordinal);

            rendered.Add(
                $"| {row.ReviewId} | {packet.Risk.Priority} {packet.Risk.Score} | " +
                $"{codes} | {string.Join(" / ", metrics)} | {altText} | {glossaryText} |");
        }

        if (rendered.Count == 0)
            return string.Empty;

        return string.Join("\n",
        [
            "## ASR 高成本錯誤證據（只供判斷）",
            "",
            "候選文字不是正確答案，不得直接拼接或據此臆造；它只表示該段可能有漏字、幻覺或關鍵詞分歧。",
            "",
            "| 編號 | 優先級/分數 | 風險訊號 | 音訊/Primary confidence | 未驗證候選 | 詞彙證據 |",
            "| --- | --- | --- | --- | --- | --- |",
            .. rendered,
        ]);
    }

    private static ReviewEvidencePacket BuildPacket(
        JsonElement row,
        string primaryText,
        IReadOnlyList<GlossaryRow> glossaryRows)
    {
        var primary = primaryText.Trim();
        var quality = Object(row, "asr_quality");
        var selection = Object(row, "asr_selection");
        var alternatives = AlternativeCandidates(row, primary, 6);

        var duration = DurationSeconds(row, quality);
        var metrics = Object(quality, "metrics");
        var density = TryGetDouble(metrics, "characters_per_second", out var densityValue)
            ? densityValue
            : duration > 0 ? VisibleText(primary).Length / duration : 0.0;
        double? confidence = TryGetDouble(metrics, "average_word_probability", out var confidenceValue)
            ? confidenceValue
            : null;

        var currentCodes = StringArray(quality, "anomaly_codes").ToHashSet(StringComparer.Ordinal);
        var observedCodes = currentCodes.ToHashSet(StringComparer.Ordinal);
        foreach (var field in new[] { "first_pass_anomaly_codes", "second_pass_anomaly_codes", "anomaly_codes" })
            foreach (var code in StringArray(selection, field))
                observedCodes.Add(code);

        var flags = new List<ReviewEvidenceFlag>();
        if (currentCodes.Contains("empty_decoding") || (primary.Length == 0 && duration >= 1.0))
            AddFlag(flags, "missing_utterance", $"duration={duration:F3}s");
        if (currentCodes.Contains("low_text_density"))
            AddFlag(flags, "likely_deletion", $"density={density:F3} chars/s");

        var reviewReasons = StringArray(selection, "review_reasons");
        if (reviewReasons.Contains("low_text_density_unresolved", StringComparer.Ordinal))
            AddFlag(flags, "unresolved_deletion", "second pass did not resolve low density");
        if (currentCodes.Contains("repetition_loop"))
            AddFlag(flags, "repetition_hallucination");
        if (currentCodes.Contains("generation_token_limit"))
            AddFlag(flags, "generation_truncated");
        if (currentCodes.Contains("invalid_replacement_character"))
            AddFlag(flags, "invalid_generation_text");
        if (currentCodes.Contains("uncovered_span_tail"))
            AddFlag(flags, "uncovered_audio_tail");
        if (currentCodes.Contains("low_confidence"))
            AddFlag(flags, "low_confidence");

        var secondPassStatus = GetString(selection, "second_pass_status") ?? string.Empty;
        if (secondPassStatus == "failed")
            AddFlag(flags, "second_pass_failed", GetString(selection, "failure") ?? string.Empty);
        else if (secondPassStatus == "recommended_not_run")
            AddFlag(flags, "second_pass_pending");

        var primaryKey = ContentKey(primary);
        var primaryLength = primaryKey.Length;
        if (alternatives.Count > 0)
        {
            var similarities = alternatives
                .Select(item => SequenceMatcherRatio(primaryKey, ContentKey(item.Text)))
                .ToArray();
            if (similarities.Length > 0 && similarities.Min() < 0.45)
                AddFlag(flags, "candidate_disagreement");
            if (alternatives.Any(item => ContentKey(item.Text).Length >= primaryLength + 4))
                AddFlag(flags, "alternative_contains_more_content");

            var primaryNumbers = NumberRegex().Matches(primary).Select(m => m.Value).ToArray();
            if (alternatives.Any(item =>
                !NumberRegex().Matches(item.Text).Select(m => m.Value).SequenceEqual(primaryNumbers)))
                AddFlag(flags, "number_disagreement");

            var primaryNegations = NegationTerms.Where(primary.Contains).ToHashSet(StringComparer.Ordinal);
            if (alternatives.Any(item =>
                !NegationTerms.Where(item.Text.Contains).ToHashSet(StringComparer.Ordinal)
                    .SetEquals(primaryNegations)))
                AddFlag(flags, "negation_disagreement");
        }

        var available =
            quality.ValueKind == JsonValueKind.Object && quality.EnumerateObject().Any() ||
            selection.ValueKind == JsonValueKind.Object && selection.EnumerateObject().Any() ||
            Array(row, "asr_observations").GetArrayLength() > 0 ||
            alternatives.Count > 0;

        var glossaryEvidence = new List<ReviewGlossaryEvidence>();
        var disagreement = false;
        foreach (var glossary in glossaryRows.Where(GlossaryRepository.IsEnabled))
        {
            var primaryHits = new[] { glossary.Source, glossary.Target }
                .Where(term => primary.Contains(term, StringComparison.Ordinal))
                .ToArray();
            var alternativeHits = alternatives
                .SelectMany(item => new[] { glossary.Source, glossary.Target }
                    .Where(term => item.Text.Contains(term, StringComparison.Ordinal)))
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)
                .ToArray();
            if (primaryHits.Length == 0 && alternativeHits.Length == 0)
                continue;
            glossaryEvidence.Add(new ReviewGlossaryEvidence(
                glossary.Source,
                glossary.Target,
                glossary.Category,
                primaryHits,
                alternativeHits));
            if ((primary.Contains(glossary.Source, StringComparison.Ordinal) &&
                 alternativeHits.Contains(glossary.Target, StringComparer.Ordinal)) ||
                (primary.Contains(glossary.Target, StringComparison.Ordinal) &&
                 alternativeHits.Contains(glossary.Source, StringComparer.Ordinal)))
                disagreement = true;
        }
        if (disagreement)
            AddFlag(flags, "domain_term_disagreement");

        int? selectedPass = null;
        if (TryGetInt(selection, "selected_pass", out var selectionPass))
            selectedPass = selectionPass;
        else if (TryGetInt(row, "asr_pass", out var asrPass))
            selectedPass = asrPass;

        return new ReviewEvidencePacket(
            available,
            primary,
            selectedPass,
            confidence is null ? null : Math.Round(confidence.Value, 5),
            currentCodes.Order(StringComparer.Ordinal).ToArray(),
            observedCodes.Order(StringComparer.Ordinal).ToArray(),
            alternatives,
            Math.Round(duration, 3),
            Math.Round(density, 4),
            secondPassStatus,
            GetString(selection, "reason") ?? string.Empty,
            GetString(selection, "confidence_tier") ?? string.Empty,
            reviewReasons,
            glossaryEvidence,
            RiskSummary(flags));
    }

    private static List<ReviewEvidenceAlternative> AlternativeCandidates(
        JsonElement row,
        string primaryText,
        int limit)
    {
        var candidates = new List<ReviewEvidenceAlternative>();
        foreach (var observation in Array(row, "asr_observations").EnumerateArray())
        {
            if (observation.ValueKind != JsonValueKind.Object)
                continue;
            var context = Object(observation, "context");
            candidates.Add(Candidate(
                GetString(observation, "text"),
                "asr_observation",
                GetString(observation, "observation_id"),
                GetString(observation, "engine"),
                TryGetInt(observation, "pass", out var pass) && pass != 0 ? pass : null,
                Object(observation, "quality"),
                GetString(context, "decode_mode")));

            foreach (var (field, source) in new[]
            {
                ("candidate_views", "multiwindow_candidate"),
                ("timestamp_candidate_views", "timestamp_candidate"),
                ("candidate_observations", "decode_candidate"),
            })
            {
                var values = Array(context, field);
                var index = 0;
                foreach (var view in values.EnumerateArray())
                {
                    index++;
                    if (view.ValueKind != JsonValueKind.Object)
                        continue;
                    var viewId = GetString(view, "view_id") ??
                                 GetString(view, "observation_id") ??
                                 $"{field}_{index}";
                    candidates.Add(Candidate(
                        GetString(view, "text"),
                        source,
                        viewId,
                        GetString(view, "engine") ?? GetString(observation, "engine"),
                        null,
                        JsonElementFromAnomalyCodes(view),
                        GetString(view, "decode_mode") ?? GetString(context, "decode_mode")));
                }
            }
        }

        var selection = Object(row, "asr_selection");
        var selectionIndex = 0;
        foreach (var view in Array(selection, "candidate_views").EnumerateArray())
        {
            selectionIndex++;
            if (view.ValueKind != JsonValueKind.Object)
                continue;
            candidates.Add(Candidate(
                GetString(view, "text"),
                "selection_candidate",
                GetString(view, "view_id") ?? $"selection_{selectionIndex}",
                GetString(view, "engine"),
                null,
                JsonElementFromAnomalyCodes(view),
                ""));
        }

        var primaryKey = ContentKey(primaryText);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        if (primaryKey.Length > 0)
            seen.Add(primaryKey);
        var output = new List<ReviewEvidenceAlternative>();
        foreach (var candidate in candidates)
        {
            var key = ContentKey(candidate.Text);
            if (key.Length == 0 || !seen.Add(key))
                continue;
            output.Add(candidate);
            if (output.Count >= Math.Max(1, limit))
                break;
        }
        return output;
    }

    private static ReviewEvidenceAlternative Candidate(
        string? text,
        string source,
        string? observationId,
        string? engine,
        int? pass,
        JsonElement quality,
        string? decodeMode)
    {
        var metrics = Object(quality, "metrics");
        double? confidence = TryGetDouble(metrics, "average_word_probability", out var value)
            ? Math.Round(value, 5)
            : null;
        return new ReviewEvidenceAlternative(
            source,
            observationId ?? "",
            engine ?? "",
            pass,
            (text ?? "").Trim(),
            StringArray(quality, "anomaly_codes"),
            confidence,
            decodeMode ?? "");
    }

    private static double DurationSeconds(JsonElement row, JsonElement quality)
    {
        var metrics = Object(quality, "metrics");
        if (TryGetDouble(metrics, "duration_seconds", out var duration))
            return Math.Max(0, duration);
        if (TryGetDouble(row, "duration", out duration))
            return Math.Max(0, duration);
        if (TryTimestamp(row, "start", out var start) && TryTimestamp(row, "end", out var end))
            return Math.Max(0, end - start);
        return 0;
    }

    private static void AddFlag(
        ICollection<ReviewEvidenceFlag> flags,
        string code,
        string detail = "")
    {
        if (flags.Any(item => item.Code == code))
            return;
        var weight = Weights[code];
        var severity = weight >= 90 ? "critical" : weight >= 70 ? "high" : "medium";
        flags.Add(new ReviewEvidenceFlag(code, severity, weight, detail));
    }

    private static ReviewEvidenceRisk RiskSummary(IReadOnlyList<ReviewEvidenceFlag> flags)
    {
        var score = Math.Min(100, flags.Sum(item => item.Weight));
        var priority = score >= 90 ? "critical"
            : score >= 70 ? "high"
            : score >= 40 ? "medium"
            : "normal";
        return new ReviewEvidenceRisk(
            score,
            priority,
            flags.Select(item => item.Code).ToArray(),
            flags.ToArray());
    }

    private static List<JsonElement> ReadEvidenceRows(string path)
    {
        if (!File.Exists(path) ||
            !string.Equals(Path.GetExtension(path), ".jsonl", StringComparison.OrdinalIgnoreCase))
            throw new ReviewEngineException("INVALID_REQUEST", "Evidence 必須是存在的 JSONL 檔案。");

        var output = new List<JsonElement>();
        try
        {
            foreach (var rawLine in File.ReadLines(path, Encoding.UTF8))
            {
                var line = rawLine.TrimStart('\uFEFF');
                if (string.IsNullOrWhiteSpace(line))
                    continue;
                using var document = JsonDocument.Parse(line);
                var record = document.RootElement;
                if (record.ValueKind != JsonValueKind.Object)
                    continue;

                if (record.TryGetProperty("segmentation_plan", out var plan) &&
                    plan.ValueKind == JsonValueKind.Object &&
                    plan.TryGetProperty("events", out var events) &&
                    events.ValueKind == JsonValueKind.Array)
                {
                    foreach (var evt in events.EnumerateArray())
                    {
                        if (evt.ValueKind != JsonValueKind.Object ||
                            !evt.TryGetProperty("compatibility", out var compatibility) ||
                            compatibility.ValueKind != JsonValueKind.Object ||
                            !compatibility.TryGetProperty("flat_event_row", out var flat) ||
                            flat.ValueKind != JsonValueKind.Object)
                            continue;
                        output.Add(flat.Clone());
                    }
                    continue;
                }

                if (record.TryGetProperty("start", out _) &&
                    record.TryGetProperty("end", out _) &&
                    (record.TryGetProperty("prediction", out _) ||
                     record.TryGetProperty("corrected_prediction", out _) ||
                     record.TryGetProperty("text", out _)))
                    output.Add(record.Clone());
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            throw new ReviewEngineException("INVALID_REQUEST", $"Evidence JSONL 無法讀取：{ex.Message}");
        }
        return output;
    }

    private static string NormalizeMatchText(string? value) =>
        (value ?? "").Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace("\r", "\n", StringComparison.Ordinal);

    private static string VisibleText(string value) =>
        WhitespaceRegex().Replace(value, string.Empty);

    private static string ContentKey(string? value) =>
        NonWordRegex().Replace(value ?? string.Empty, string.Empty).ToLowerInvariant();

    private static double SequenceMatcherRatio(string a, string b)
    {
        if (a.Length + b.Length == 0)
            return 1.0;
        var matches = MatchingCharacters(a, 0, a.Length, b, 0, b.Length);
        return 2.0 * matches / (a.Length + b.Length);
    }

    private static int MatchingCharacters(
        string a, int aStart, int aEnd,
        string b, int bStart, int bEnd)
    {
        var bestA = aStart;
        var bestB = bStart;
        var bestSize = 0;
        for (var i = aStart; i < aEnd; i++)
        {
            for (var j = bStart; j < bEnd; j++)
            {
                var size = 0;
                while (i + size < aEnd &&
                       j + size < bEnd &&
                       a[i + size] == b[j + size])
                    size++;
                if (size > bestSize)
                {
                    bestA = i;
                    bestB = j;
                    bestSize = size;
                }
            }
        }
        if (bestSize == 0)
            return 0;

        var left = MatchingCharacters(a, aStart, bestA, b, bStart, bestB);
        var right = MatchingCharacters(
            a, bestA + bestSize, aEnd,
            b, bestB + bestSize, bEnd);
        return left + bestSize + right;
    }

    private static JsonElement Object(JsonElement parent, string property)
    {
        if (parent.ValueKind == JsonValueKind.Object &&
            parent.TryGetProperty(property, out var value) &&
            value.ValueKind == JsonValueKind.Object)
            return value;
        return EmptyObject.RootElement;
    }

    private static JsonElement Array(JsonElement parent, string property)
    {
        if (parent.ValueKind == JsonValueKind.Object &&
            parent.TryGetProperty(property, out var value) &&
            value.ValueKind == JsonValueKind.Array)
            return value;
        return EmptyArray.RootElement;
    }

    private static string? GetString(JsonElement parent, string property)
    {
        if (parent.ValueKind != JsonValueKind.Object ||
            !parent.TryGetProperty(property, out var value) ||
            value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            return null;
        return value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : value.ToString();
    }

    private static bool TryGetDouble(JsonElement parent, string property, out double result)
    {
        result = default;
        if (parent.ValueKind != JsonValueKind.Object ||
            !parent.TryGetProperty(property, out var value))
            return false;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out result))
            return double.IsFinite(result);
        if (value.ValueKind == JsonValueKind.String &&
            double.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out result))
            return double.IsFinite(result);
        return false;
    }

    private static bool TryGetInt(JsonElement parent, string property, out int result)
    {
        result = default;
        if (parent.ValueKind != JsonValueKind.Object ||
            !parent.TryGetProperty(property, out var value))
            return false;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out result))
            return true;
        return value.ValueKind == JsonValueKind.String &&
               int.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out result);
    }

    private static IReadOnlyList<string> StringArray(JsonElement parent, string property)
    {
        var array = Array(parent, property);
        return array.EnumerateArray()
            .Select(value => value.ValueKind == JsonValueKind.String
                ? value.GetString() ?? string.Empty
                : value.ToString())
            .Where(value => value.Length > 0)
            .ToArray();
    }

    private static bool TryTimestamp(JsonElement parent, string property, out double seconds)
    {
        seconds = default;
        if (TryGetDouble(parent, property, out seconds))
            return true;
        var text = GetString(parent, property);
        if (text is null)
            return false;
        var match = TimestampRegex().Match(text.Trim());
        if (!match.Success)
            return false;
        var milliseconds = match.Groups["milliseconds"].Value.PadRight(3, '0')[..3];
        seconds =
            int.Parse(match.Groups["hours"].Value, CultureInfo.InvariantCulture) * 3600 +
            int.Parse(match.Groups["minutes"].Value, CultureInfo.InvariantCulture) * 60 +
            int.Parse(match.Groups["seconds"].Value, CultureInfo.InvariantCulture) +
            int.Parse(milliseconds, CultureInfo.InvariantCulture) / 1000.0;
        return true;
    }

    private static JsonElement JsonElementFromAnomalyCodes(JsonElement source)
    {
        var codes = source.ValueKind == JsonValueKind.Object &&
                    source.TryGetProperty("anomaly_codes", out var value) &&
                    value.ValueKind == JsonValueKind.Array
            ? value.EnumerateArray().Select(item => item.ToString()).ToArray()
            : [];
        return JsonSerializer.SerializeToElement(new
        {
            anomaly_codes = codes,
        });
    }

    private static string FormatCompact(double value) =>
        value.ToString("0.#####", CultureInfo.InvariantCulture);

    private static readonly JsonDocument EmptyObject =
        JsonDocument.Parse("{}");
    private static readonly JsonDocument EmptyArray =
        JsonDocument.Parse("[]");

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();

    [GeneratedRegex(@"[^\w]+", RegexOptions.CultureInvariant)]
    private static partial Regex NonWordRegex();

    [GeneratedRegex(@"\d+(?:[.,]\d+)?")]
    private static partial Regex NumberRegex();

    [GeneratedRegex(@"(?<hours>\d{1,2}):(?<minutes>\d{2}):(?<seconds>\d{2})[,.](?<milliseconds>\d{1,3})")]
    private static partial Regex TimestampRegex();
}
