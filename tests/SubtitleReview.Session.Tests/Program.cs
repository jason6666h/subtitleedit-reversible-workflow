using System.Text;
using SubtitleReview.Core;

var failures = 0;
var checks = 0;
void Check(string name, Action test)
{
    checks++;
    var directory = Path.Combine(Path.GetTempPath(), "subtitle-review-session-tests", Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(directory);
    try
    {
        testWithDirectory(test, directory);
        Console.WriteLine("PASS " + name);
    }
    catch (Exception ex)
    {
        failures++;
        Console.WriteLine("FAIL " + name + ": " + ex);
    }
    finally { Directory.Delete(directory, recursive: true); }
}

// Each check receives an isolated directory without a shared mutable session file.
string? activeDirectory = null;
void testWithDirectory(Action test, string directory)
{
    activeDirectory = directory;
    test();
    activeDirectory = null;
}
string Dir() => activeDirectory ?? throw new InvalidOperationException("No active test directory");
void Assert(bool condition, string message) { if (!condition) throw new Exception(message); }
void Throws<T>(Action action) where T : Exception
{
    try { action(); }
    catch (T) { return; }
    throw new Exception($"Expected {typeof(T).Name}");
}

var rowId = Guid.Parse("00000000-0000-0000-0000-000000000001");
var row = new ReviewRowSnapshot { Id = rowId, Position = 1, Number = 1, Text = "Sample subtitle.", Style = "Default" };
var document = new ReviewDocumentSnapshot("sample.srt", "SubRip", [row], "undo-hash");
var decisions = new ReviewDecisions(new ReviewSelection([new(1, rowId)]), [new(1, ReviewDecisionKind.Manual, "Suggested subtitle", "Manual edit")]);
var session = new ReviewSession(1, document, "backend-sha", "engine-sha", decisions,
    ["prompt-1"], ["response-1"], new DateTimeOffset(2026, 9, 18, 12, 0, 0, TimeSpan.Zero))
{
    PromptChunks = ["Complete prompt"],
    Responses = ["Complete response"],
};

Check("Unicode session survives save and load with complete v1 data", () =>
{
    var store = new ReviewSessionStore(Dir());
    store.Save("session-1.json", session);
    var loaded = store.Load("session-1.json");
    Assert(loaded.SchemaVersion == 1 && loaded.Document.Rows[0].Text == "Sample subtitle." &&
        loaded.Document.SourcePath == "sample.srt" && loaded.Document.UndoHash == "undo-hash" &&
        loaded.Decisions.Selection.Rows[0] == new ReviewSelectionRow(1, rowId) &&
        loaded.Decisions.Items[0].ManualText == "Manual edit" &&
        loaded.BackendFingerprint == "backend-sha" && loaded.EngineFingerprint == "engine-sha" &&
        loaded.PromptChunkIds.Single() == "prompt-1" && loaded.ResponseIds.Single() == "response-1" &&
        loaded.PromptChunks.Single() == "Complete prompt" && loaded.Responses.Single() == "Complete response" &&
        loaded.SavedAtUtc == session.SavedAtUtc, "Round trip lost session state");
    Assert(Directory.GetFiles(Dir()).Select(Path.GetFileName).SequenceEqual(["session-1.json"]), "Temporary file remained");
});

Check("restart resume rebinds volatile row IDs and process-local undo hash", () =>
{
    var newId = Guid.Parse("00000000-0000-0000-0000-000000000099");
    var reopenedRow = row with { Id = newId, Paragraph = row.Paragraph };
    var reopened = new ReviewDocumentSnapshot("sample.srt", "SubRip", [reopenedRow], "different-process-hash");
    Assert(ReviewSessionResume.GetStableDocumentFingerprint(document) ==
           ReviewSessionResume.GetStableDocumentFingerprint(reopened),
        "Stable fingerprint depended on volatile identity");

    Assert(ReviewSessionResume.TryRebind(
        session, reopened, "backend-sha", "engine-sha", out var rebound, out var reason),
        reason ?? "Rebind failed");
    Assert(rebound.Document == reopened, "Rebound session did not adopt the current snapshot");
    Assert(rebound.Decisions.Selection.Rows.Single() == new ReviewSelectionRow(1, newId),
        "Selection was not rebound to the reopened row ID");
    Assert(rebound.Decisions.Items.Single().ManualText == "Manual edit",
        "Human decision was lost during rebind");
});

Check("restart keeps the same session file name when row IDs change", () =>
{
    var reopenedId = Guid.Parse("00000000-0000-0000-0000-000000000088");
    var reopened = document with { Rows = [row with { Id = reopenedId }] };
    var reopenedSelection = new ReviewSelection([new ReviewSelectionRow(1, reopenedId)]);
    Assert(
        ReviewSessionResume.GetSessionFileName(document, session.Decisions.Selection) ==
        ReviewSessionResume.GetSessionFileName(reopened, reopenedSelection),
        "Session file name depended on volatile row IDs");
});

Check("restart resume rejects changed content metadata or engine", () =>
{
    var changedText = document with { Rows = [row with { Text = "Changed content" }] };
    Assert(!ReviewSessionResume.TryRebind(
        session, changedText, "backend-sha", "engine-sha", out _, out _),
        "Changed text resumed as writable");
    var changedStyle = document with { Rows = [row with { Style = "Other" }] };
    Assert(!ReviewSessionResume.TryRebind(
        session, changedStyle, "backend-sha", "engine-sha", out _, out _),
        "Changed metadata resumed as writable");
    Assert(!ReviewSessionResume.TryRebind(
        session, document, "backend-sha", "different-engine", out _, out _),
        "Changed engine resumed as writable");
});

Check("restart resume ignores volatile paragraph IDs but keeps semantic metadata strict", () =>
{
    var referenceId = Guid.Parse("00000000-0000-0000-0000-000000000010");
    var paragraphId = Guid.Parse("00000000-0000-0000-0000-000000000011");
    var identifiedRow = row with
    {
        ReferenceParagraphId = referenceId,
        Paragraph = new ReviewParagraphSnapshot { Id = paragraphId, Text = row.Text },
    };
    var referenceRow = row with
    {
        Id = referenceId,
        Position = 2,
        Number = 2,
        Text = "Reference row",
        IsReferenceOnly = true,
        ReferenceParagraphId = null,
    };
    var identifiedDocument = document with { Rows = [identifiedRow, referenceRow] };
    var identifiedSession = session with { Document = identifiedDocument };

    var reloadedRowId = Guid.NewGuid();
    var reloadedReferenceId = Guid.NewGuid();
    var reloadedIdentity = identifiedDocument with
    {
        Rows =
        [
            identifiedRow with
            {
                Id = reloadedRowId,
                ReferenceParagraphId = reloadedReferenceId,
                Paragraph = identifiedRow.Paragraph! with { Id = Guid.NewGuid() },
            },
            referenceRow with { Id = reloadedReferenceId },
        ],
    };
    Assert(ReviewSessionResume.TryRebind(
        identifiedSession, reloadedIdentity, "backend-sha", "engine-sha", out _, out var reason),
        reason ?? "Reload-local paragraph IDs incorrectly invalidated the session");

    var removedReference = identifiedDocument with
    {
        Rows = [identifiedRow with { ReferenceParagraphId = null }, referenceRow],
    };
    Assert(!ReviewSessionResume.TryRebind(
        identifiedSession, removedReference, "backend-sha", "engine-sha", out _, out _),
        "Removing reference metadata resumed as writable");

    var changedParagraphText = identifiedDocument with
    {
        Rows =
        [
            identifiedRow with { Paragraph = identifiedRow.Paragraph! with { Text = "Changed paragraph content" } },
            referenceRow,
        ],
    };
    Assert(!ReviewSessionResume.TryRebind(
        identifiedSession, changedParagraphText, "backend-sha", "engine-sha", out _, out _),
        "Changed paragraph content resumed as writable");
});

Check("malformed and future-schema files are rejected without modification", () =>
{
    var store = new ReviewSessionStore(Dir());
    foreach (var (name, content) in new[] { ("broken.json", "{"), ("future.json", ReviewSessionJson.Serialize(session).Replace("\"schema_version\":1", "\"schema_version\":2")) })
    {
        var path = Path.Combine(Dir(), name);
        File.WriteAllText(path, content, Encoding.UTF8);
        var original = File.ReadAllBytes(path);
        Throws<InvalidDataException>(() => store.Load(name));
        Assert(File.ReadAllBytes(path).SequenceEqual(original), "Load changed rejected bytes");
    }
});

Check("traversal, rooted paths and non-json names cannot be loaded or saved", () =>
{
    var store = new ReviewSessionStore(Dir());
    var outside = Path.Combine(Path.GetDirectoryName(Dir())!, "outside.json");
    foreach (var name in new[] { "../outside.json", "..\\outside.json", "sub/session.json", "sub\\session.json", outside, "session.txt", ".json" })
    {
        Throws<ArgumentException>(() => store.Load(name));
        Throws<ArgumentException>(() => store.Save(name, session));
    }
    Assert(Directory.GetFiles(Dir()).Length == 0 && !File.Exists(outside), "Rejected name caused a write");
});

Check("failed overwrite preserves previous bytes and removes its temporary file", () =>
{
    var store = new ReviewSessionStore(Dir());
    store.Save("session.json", session);
    var path = Path.Combine(Dir(), "session.json");
    var original = File.ReadAllBytes(path);
    using (var lockFile = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
        Throws<IOException>(() => store.Save("session.json", session with { EngineFingerprint = "new-engine" }));
    Assert(File.ReadAllBytes(path).SequenceEqual(original), "Failed overwrite changed previous bytes");
    Assert(Directory.GetFiles(Dir()).Length == 1, "Failed overwrite left a temporary file");
});

Console.WriteLine($"RESULT {checks - failures}/{checks} passed");
return failures == 0 ? 0 : 1;
