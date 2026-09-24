using SubtitleReview.Core;

var failures = 0;
var checks = 0;
void Check(string name, Action test)
{
    checks++;
    try { test(); Console.WriteLine("PASS " + name); }
    catch (Exception ex) { failures++; Console.WriteLine("FAIL " + name + ": " + ex.Message); }
}
void Assert(bool condition, string message) { if (!condition) throw new Exception(message); }

var ids = Enumerable.Range(1, 5).Select(i => Guid.Parse($"00000000-0000-0000-0000-{i:000000000000}")).ToArray();
ReviewRowSnapshot Row(int position, string text) => new() { Id = ids[position - 1], Position = position, Number = position, Text = text };
var document = new ReviewDocumentSnapshot("work.srt", "SubRip", [
    Row(1, "outside selection"), Row(2, "first"), Row(3, ""),
    Row(4, "reference") with { IsReferenceOnly = true }, Row(5, "second")], "undo-1");

Check("selection uses consecutive review IDs for editable rows in document order", () =>
{
    var selection = ReviewSelection.Create(document, [ids[4], ids[3], ids[2], ids[1]]);
    Assert(selection.Rows.SequenceEqual([new(1, ids[1]), new(2, ids[4])]),
        "Expected review IDs 1→row 2 and 2→row 5, skipping empty and reference-only rows");
});
Check("snapshot and selection are frozen against caller array edits", () =>
{
    var inputRows = new[] { Row(1, "captured") };
    var snapshot = new ReviewDocumentSnapshot("work.srt", "SubRip", inputRows, "hash");
    var inputTargets = new[] { new ReviewSelectionRow(1, ids[0]) };
    var selection = new ReviewSelection(inputTargets);
    inputRows[0] = Row(1, "changed");
    inputTargets[0] = new ReviewSelectionRow(1, ids[1]);
    Assert(snapshot.Rows[0].Text == "captured" && selection.Rows[0].RowId == ids[0], "Captured state followed mutable input arrays");
});

var selected = ReviewSelection.Create(document, [ids[1], ids[4]]);
ReviewDecisions Batch(params ReviewDecision[] items) => new(selected, items);
var approved = Batch(new(1, ReviewDecisionKind.Apply, "FIRST"), new(2, ReviewDecisionKind.Keep, "second"));

void Blocks(string name, ReviewDocumentSnapshot changed)
    => Check(name, () => Assert(!ReviewSafety.Validate(changed, document, approved).IsValid, "Expected whole batch rejection"));

Blocks("changed text blocks batch", document with { Rows = document.Rows.Select((r, i) => i == 0 ? r with { Text = "edited" } : r).ToArray() });
Blocks("moved row blocks batch", document with { Rows = [document.Rows[1], document.Rows[0], .. document.Rows.Skip(2)] });
Blocks("changed time blocks batch", document with { Rows = document.Rows.Select((r, i) => i == 0 ? r with { StartTicks = 1 } : r).ToArray() });
Blocks("changed style blocks batch", document with { Rows = document.Rows.Select((r, i) => i == 0 ? r with { Style = "new" } : r).ToArray() });
Blocks("changed actor blocks batch", document with { Rows = document.Rows.Select((r, i) => i == 0 ? r with { Actor = "new" } : r).ToArray() });
Blocks("changed bookmark blocks batch", document with { Rows = document.Rows.Select((r, i) => i == 0 ? r with { Bookmark = "new" } : r).ToArray() });
Blocks("changed paragraph output blocks batch", document with { Rows = document.Rows.Select((r, i) => i == 0 ? r with { Paragraph = new() { Text = "hidden change" } } : r).ToArray() });
Blocks("added row blocks batch", document with { Rows = [.. document.Rows, new ReviewRowSnapshot { Id = Guid.NewGuid(), Position = 6, Number = 6, Text = "added" }] });
Blocks("source switch blocks batch", document with { SourcePath = "other.srt" });
Blocks("format switch blocks batch", document with { Format = "ASS" });
Blocks("undo hash switch blocks batch", document with { UndoHash = "undo-2" });

void Rejects(string name, ReviewDecisions decisions)
    => Check(name, () => Assert(!ReviewSafety.Validate(document, document, decisions).IsValid, "Expected decision rejection"));
Rejects("empty AI suggestion blocks batch", Batch(new(1, ReviewDecisionKind.Apply, " "), new(2, ReviewDecisionKind.Keep)));
Rejects("duplicate review ID blocks batch", Batch(new(1, ReviewDecisionKind.Apply, "FIRST"), new(1, ReviewDecisionKind.Keep)));
Rejects("unknown review ID blocks batch", Batch(new(1, ReviewDecisionKind.Apply, "FIRST"), new(99, ReviewDecisionKind.Keep)));
Rejects("reference-only target blocks batch", new(new([new(1, ids[3])]), [new(1, ReviewDecisionKind.Apply, "replacement")]));

Check("explicit apply produces one text change", () =>
{
    var result = ReviewSafety.Validate(document, document, approved);
    Assert(result.IsValid && result.Changes.SequenceEqual([new ReviewTextChange(ids[1], "FIRST")]), "Expected only row 2 text change");
});
Check("keep and pending produce no text change", () =>
{
    var result = ReviewSafety.Validate(document, document, Batch(new(1, ReviewDecisionKind.Keep, "different"), new(2, ReviewDecisionKind.Pending, "other")));
    Assert(result.IsValid && result.Changes.Count == 0, "Expected no changes");
});
Check("nonempty manual text changes only its row", () =>
{
    var result = ReviewSafety.Validate(document, document, Batch(new(1, ReviewDecisionKind.Manual, ManualText: "手動"), new(2, ReviewDecisionKind.Keep)));
    Assert(result.IsValid && result.Changes.SequenceEqual([new ReviewTextChange(ids[1], "手動")]), "Expected manual text");
});
Rejects("blank manual text blocks batch", Batch(new(1, ReviewDecisionKind.Manual, ManualText: " "), new(2, ReviewDecisionKind.Keep)));
Check("AI apply must preserve ASS line breaks", () =>
{
    var tagged = document with { Rows = document.Rows.Select((row, index) =>
        index == 1 ? row with { Text = "甲\\N乙" } : row).ToArray() };
    var lostTag = ReviewSafety.Validate(tagged, tagged,
        Batch(new(1, ReviewDecisionKind.Apply, "甲乙"), new(2, ReviewDecisionKind.Keep)));
    var preservedTag = ReviewSafety.Validate(tagged, tagged,
        Batch(new(1, ReviewDecisionKind.Apply, "丙\\N丁"), new(2, ReviewDecisionKind.Keep)));
    Assert(!lostTag.IsValid && preservedTag.IsValid, "ASS line break guard rejected or accepted the wrong AI text");
});
Check("manual text must preserve subtitle formatting tags", () =>
{
    var tagged = document with { Rows = document.Rows.Select((row, index) =>
        index == 1 ? row with { Text = "<i>甲\\N乙</i>" } : row).ToArray() };
    var lostTags = ReviewSafety.Validate(tagged, tagged,
        Batch(new(1, ReviewDecisionKind.Manual, ManualText: "甲乙"), new(2, ReviewDecisionKind.Keep)));
    var preservedTags = ReviewSafety.Validate(tagged, tagged,
        Batch(new(1, ReviewDecisionKind.Manual, ManualText: "<i>丙\\N丁</i>"), new(2, ReviewDecisionKind.Keep)));
    Assert(!lostTags.IsValid && preservedTags.IsValid,
        "Manual text bypassed subtitle formatting protection");
});
Check("unchanged proposal is a no-op", () =>
{
    var result = ReviewSafety.Validate(document, document, Batch(new(1, ReviewDecisionKind.Apply, "first"), new(2, ReviewDecisionKind.Keep)));
    Assert(result.IsValid && result.Changes.Count == 0, "Expected no-op");
});

Check("v1 session JSON preserves Unicode, mapping, decisions and fingerprints", () =>
{
    var session = new ReviewSession(1,
        document with { Rows = document.Rows.Select((r, i) => i == 1 ? r with { Text = "測試字幕。" } : r).ToArray() },
        "backend-sha", "engine-sha",
        Batch(new(1, ReviewDecisionKind.Manual, ManualText: "人工修訂"), new(2, ReviewDecisionKind.Keep)),
        ["chunk-1"], ["reply-1"], new DateTimeOffset(2026, 9, 18, 12, 0, 0, TimeSpan.Zero));
    var json = ReviewSessionJson.Serialize(session);
    var restored = ReviewSessionJson.Deserialize(json);
    Assert(json.Contains("\"schema_version\":1") && json.Contains("\"manual\""), "Expected v1 wire shape");
    Assert(restored.SchemaVersion == 1 && restored.Document.Rows[1].Text == "測試字幕。" &&
        restored.Decisions.Selection.Rows[0] == new ReviewSelectionRow(1, ids[1]) &&
        restored.Decisions.Items[0].ManualText == "人工修訂" &&
        restored.BackendFingerprint == "backend-sha" && restored.EngineFingerprint == "engine-sha" &&
        restored.PromptChunkIds.Single() == "chunk-1" && restored.ResponseIds.Single() == "reply-1" &&
        restored.SavedAtUtc == session.SavedAtUtc, "Session round-trip lost data");
});
Check("future and invalid session versions are rejected", () =>
{
    var session = new ReviewSession(1, document, "backend", "engine", approved, [], [], DateTimeOffset.UtcNow);
    var json = ReviewSessionJson.Serialize(session);
    foreach (var version in new[] { 0, 2, -1 })
    {
        try { ReviewSessionJson.Deserialize(json.Replace("\"schema_version\":1", $"\"schema_version\":{version}")); }
        catch (InvalidDataException) { continue; }
        throw new Exception($"Expected version {version} rejection");
    }
    try { ReviewSessionJson.Deserialize("{}"); }
    catch (InvalidDataException) { return; }
    throw new Exception("Expected missing version rejection");
});


var fixtureRoot = Path.Combine(AppContext.BaseDirectory, "fixtures");
var engine = new SubtitleReviewEngine();
var engineRows = new[]
{
    new ReviewInputRow(1, "Open Caption", 0, 1200),
    new ReviewInputRow(2, "Acme Studo", 1400, 2600),
    new ReviewInputRow(3, "Open Captin enabled", 2800, 4200),
    new ReviewInputRow(4, "Unanswered subtitle", 4400, 5500),
};

Check("default public prompt contains generic rules and the review table", () =>
{
    var root = Path.Combine(Path.GetTempPath(), "subtitle-review-core-" + Guid.NewGuid().ToString("N"));
    try
    {
        ReviewResources.EnsureGlossarySeeds(root);
        var actual = engine.CreateReviewRequest(engineRows, 100, Path.Combine(root, "glossary.csv"), null)
            .Prompts.Single().Markdown;
        Assert(actual.Contains("Open Caption", StringComparison.Ordinal), "Generic public glossary was not included");
        Assert(actual.Contains("| 1 | Open Caption | |", StringComparison.Ordinal), "Review table was not appended");
    }
    finally
    {
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }
});

Check("saved complete prompt template is reused without being overwritten", () =>
{
    var root = Path.Combine(Path.GetTempPath(), "subtitle-review-template-" + Guid.NewGuid().ToString("N"));
    try
    {
        ReviewResources.EnsureGlossarySeeds(root);
        const string customTemplate = "# 我的完整校閱指令\n\n請依照我保存的規則處理。";
        var actual = engine.CreateReviewRequest(
                engineRows,
                100,
                Path.Combine(root, "glossary.csv"),
                null,
                customTemplate)
            .Prompts.Single().Markdown;
        Assert(actual.StartsWith(customTemplate + "\n\n", StringComparison.Ordinal),
            "Generated prompt did not start with the saved complete template");
        Assert(!actual.Contains(ReviewResources.PromptTemplate.Trim(), StringComparison.Ordinal),
            "Default template replaced the saved complete template");
        Assert(actual.Contains("| 1 | Open Caption | |", StringComparison.Ordinal),
            "Dynamic subtitle table was not appended");
    }
    finally
    {
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }
});

Check("reference material and multiple review attachments are listed in every prompt", () =>
{
    var previousUiCulture = System.Globalization.CultureInfo.CurrentUICulture;
    var root = Path.Combine(Path.GetTempPath(), "subtitle-review-references-" + Guid.NewGuid().ToString("N"));
    try
    {
        System.Globalization.CultureInfo.CurrentUICulture = new System.Globalization.CultureInfo("zh-TW");
        ReviewResources.EnsureGlossarySeeds(root);
        var actual = engine.CreateReviewRequest(
                engineRows,
                2,
                Path.Combine(root, "glossary.csv"),
                null,
                "# 自訂提示詞",
                "Acme Studio terminology reference",
                ["reviewed-session-b.txt", "reviewed-session-a.txt"])
            .Prompts.Select(prompt => prompt.Markdown).ToArray();

        Assert(actual.Length == 2, "Expected two prompt chunks");
        foreach (var prompt in actual)
        {
            Assert(prompt.Contains("## 參考資料（使用者提供）", StringComparison.Ordinal) &&
                   prompt.Contains("Acme Studio terminology reference", StringComparison.Ordinal),
                "Reference material was not added to every prompt");
            Assert(prompt.Contains("## 先前校閱文本參考（附件）", StringComparison.Ordinal) &&
                   prompt.Contains("`reviewed-session-a.txt`", StringComparison.Ordinal) &&
                   prompt.Contains("`reviewed-session-b.txt`", StringComparison.Ordinal),
                "All review reference attachments were not listed in every prompt");
            Assert(prompt.IndexOf("`reviewed-session-a.txt`", StringComparison.Ordinal) <
                   prompt.IndexOf("`reviewed-session-b.txt`", StringComparison.Ordinal),
                "Attachment names were not rendered in stable order");
            Assert(prompt.Contains("不得把附件中的句子新增到「校閱結果」", StringComparison.Ordinal),
                "Attachment safety instruction was omitted");
        }
    }
    finally
    {
        System.Globalization.CultureInfo.CurrentUICulture = previousUiCulture;
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }
});

Check("English prompt and response format round-trip", () =>
{
    var previous = System.Globalization.CultureInfo.CurrentUICulture;
    var root = Path.Combine(Path.GetTempPath(), "subtitle-review-en-" + Guid.NewGuid().ToString("N"));
    try
    {
        System.Globalization.CultureInfo.CurrentUICulture = new System.Globalization.CultureInfo("en-US");
        ReviewResources.EnsureGlossarySeeds(root);
        var prompt = engine.CreateReviewRequest(engineRows, 100, Path.Combine(root, "glossary.csv"), null)
            .Prompts.Single().Markdown;
        Assert(prompt.Contains("## Review Results", StringComparison.Ordinal), "English review heading missing");
        var response = """
## Review Results
| ID | Original | Reviewed Text |
| --- | --- | --- |
| 1 | Open Caption | Open Caption |

## Suggested Glossary Additions
| Source | Target | Category | Evidence IDs | Confidence | Notes |
| --- | --- | --- | --- | --- | --- |
| Open Captin | Open Caption | product | 1 | high | verified |
""";
        var parsed = engine.ParseAiResponse([response], [1]);
        Assert(parsed.Results[1] == "Open Caption", "English review table did not parse");
        Assert(parsed.GlossaryCandidates.Single().Target == "Open Caption", "English glossary table did not parse");
    }
    finally
    {
        System.Globalization.CultureInfo.CurrentUICulture = previous;
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }
});

Check("native Markdown parser matches frozen Python response semantics", () =>
{
    var response = File.ReadAllText(
        Path.Combine(fixtureRoot, "review-response.md"),
        new System.Text.UTF8Encoding(false, true));
    var parsed = engine.ParseAiResponse([response], [1, 2, 3, 4]);
    Assert(parsed.Results[1] == "Open Caption", "ID 1 mismatch");
    Assert(parsed.Results[2] == "Acme Studio!", "Repeated ID should keep last nonempty text");
    Assert(parsed.Results[3] == "Open Caption enabled.", "ID 3 mismatch");
    Assert(parsed.Results[99] == "Do not apply", "Unknown ID parse mismatch");
    Assert(parsed.DuplicateIds.SequenceEqual([2]), "Duplicate ID mismatch");
    Assert(parsed.Conflicts.TryGetValue(2, out var conflict) &&
           conflict.SequenceEqual(["Acme Studio.", "Acme Studio!"]),
        "Conflict variants mismatch");
    Assert(parsed.MissingIds.SequenceEqual([4]), "Missing ID mismatch");
    Assert(parsed.UnknownIds.SequenceEqual([99]), "Unknown ID mismatch");
    var candidate = parsed.GlossaryCandidates.Single();
    Assert(candidate == new GlossarySuggestion(
            "Open Captin", "Open Caption", "product", "3", "高", "verified manually"),
        "Glossary suggestion mismatch");
});
Check("Markdown parser preserves ASS line breaks and escaped pipes", () =>
{
    var response = "## 校閱結果\n| 編號 | 原文 | 校閱後的文字 |\n| --- | --- | --- |\n| 1 | 原文 | 甲\\N乙 \\| 丙 |";
    var parsed = engine.ParseAiResponse([response], [1]);
    Assert(parsed.Results[1] == "甲\\N乙 | 丙", "ASS line break or escaped pipe was lost");
});
Check("response set parses each document once while preserving cross-document conflicts", () =>
{
    var first = "## 校閱結果\n| 編號 | 原文 | 校閱後的文字 |\n| --- | --- | --- |\n| 1 | 原文 | 第一版 |";
    var second = "## 校閱結果\n| 編號 | 原文 | 校閱後的文字 |\n| --- | --- | --- |\n| 1 | 原文 | 第二版 |";
    var parsed = engine.ParseAiResponseSet([first, second, "不是校閱表格"], [1, 2]);
    Assert(parsed.Documents.Count == 3, "Document parse status count mismatch");
    Assert(parsed.Documents[0].Error is null && parsed.Documents[0].Results[1] == "第一版",
        "First document parse result mismatch");
    Assert(parsed.Documents[1].Error is null && parsed.Documents[1].Results[1] == "第二版",
        "Second document parse result mismatch");
    Assert(parsed.Documents[2].Error is not null, "Malformed document was not reported separately");
    Assert(parsed.Combined.Results[1] == "第二版", "Combined parser did not keep last nonempty text");
    Assert(parsed.Combined.Conflicts.TryGetValue(1, out var conflict) &&
           conflict.SequenceEqual(["第一版", "第二版"]),
        "Cross-document conflict was lost");
    Assert(parsed.Combined.MissingIds.SequenceEqual([2]), "Combined missing IDs mismatch");
});

Check("native preview preserves original text and reports conflict/missing/unknown", () =>
{
    var root = Path.Combine(Path.GetTempPath(), "subtitle-review-preview-" + Guid.NewGuid().ToString("N"));
    try
    {
        ReviewResources.EnsureGlossarySeeds(root);
        var response = File.ReadAllText(
            Path.Combine(fixtureRoot, "review-response.md"),
            new System.Text.UTF8Encoding(false, true));
        var preview = engine.PreparePreview(
            engineRows,
            [response],
            Path.Combine(root, "glossary.csv"),
            null);
        Assert(preview.Rows.Count == 4, "Preview row count mismatch");
        Assert(preview.Rows[1].AiReviewText == "Acme Studio!", "AI preview text mismatch");
        Assert(preview.Rows[1].OriginalText == "Acme Studo",
            "Preview did not preserve the original text alongside the AI suggestion");
        Assert(preview.Rows[1].ResponseConflict, "Conflict flag missing");
        Assert(preview.MissingIds.SequenceEqual([4]), "Preview missing ID mismatch");
        Assert(preview.UnknownIds.SequenceEqual([99]), "Preview unknown ID mismatch");
        Assert(!preview.Complete, "Incomplete response marked complete");
    }
    finally
    {
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }
});

Check("native evidence matches frozen Python high-risk result", () =>
{
    var root = Path.Combine(Path.GetTempPath(), "subtitle-review-evidence-" + Guid.NewGuid().ToString("N"));
    try
    {
        ReviewResources.EnsureGlossarySeeds(root);
        var response =
            "## 校閱結果\n\n| 編號 | 原文 | 校閱後的文字 |\n| --- | --- | --- |\n| 1 | Open Caption | Open Caption |\n";
        var preview = engine.PreparePreview(
            [engineRows[0]],
            [response],
            Path.Combine(root, "glossary.csv"),
            Path.Combine(fixtureRoot, "evidence-reference.jsonl"));
        var row = preview.Rows.Single();
        Assert(row.EvidenceStatus == "matched", "Evidence did not match");
        Assert(row.Evidence.Risk.Score == 100 &&
               row.Evidence.Risk.Priority == "critical",
            "Evidence risk score/priority mismatch");
        Assert(row.Evidence.Risk.FlagCodes.SequenceEqual(
                ["likely_deletion", "second_pass_pending"]),
            "Evidence flag codes mismatch");
        Assert(row.Evidence.Alternatives.Count == 0, "Unexpected evidence alternatives");
    }
    finally
    {
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }
});

Check("native glossary writes atomically with backup and deduplicates", () =>
{
    var root = Path.Combine(Path.GetTempPath(), "subtitle-review-glossary-" + Guid.NewGuid().ToString("N"));
    try
    {
        ReviewResources.EnsureGlossarySeeds(root);
        var path = Path.Combine(root, "glossary.csv");
        var first = engine.SaveGlossary(
            path,
            [new GlossarySuggestion("Captin Mode", "Caption Mode", "product", "3", "high", "verified manually")],
            confirmed: true);
        Assert(first.AddedCount == 1, "Expected glossary addition");
        Assert(File.Exists(path + ".bak"), "Glossary backup was not created");
        var second = engine.SaveGlossary(
            path,
            [new GlossarySuggestion("Captin Mode", "Caption Mode", "product", "3", "high", "verified manually")],
            confirmed: true);
        Assert(second.AddedCount == 0, "Duplicate glossary rule was added twice");
    }
    finally
    {
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }
});
Check("adding a glossary rule preserves custom CSV columns and existing rows", () =>
{
    var root = Path.Combine(Path.GetTempPath(), "subtitle-review-custom-glossary-" + Guid.NewGuid().ToString("N"));
    try
    {
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "custom.csv");
        var original = "source,owner,target,enabled\nfoo,Alice,bar,0\n# keep this comment\n";
        File.WriteAllText(path, original);
        engine.SaveGlossary(path,
            [new GlossarySuggestion("新錯", "新正", "", "", "", "")],
            confirmed: true);
        var updated = File.ReadAllText(path);
        Assert(updated.StartsWith(original, StringComparison.Ordinal), "Existing CSV content changed");
        Assert(updated.Contains("新錯,,新正,1", StringComparison.Ordinal), "New rule did not follow custom column order");
        Assert(File.ReadAllText(path + ".bak") == original, "Original CSV backup was not preserved");
        Assert(GlossaryRepository.Read(path).Any(row => row.Source == "新錯" && row.Target == "新正"),
            "New rule cannot be read back");
    }
    finally
    {
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }
});

Console.WriteLine($"RESULT {checks - failures}/{checks} passed");
return failures == 0 ? 0 : 1;
