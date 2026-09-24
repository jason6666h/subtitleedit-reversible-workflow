using SubtitleReview.Core;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace Nikse.SubtitleEdit.Features.Main.Layout;

internal sealed class ReviewWorkflowState : IDisposable
{
    public List<ReviewUiRow> Rows { get; } = [];
    public List<string> Prompts { get; } = [];
    public List<int[]> PromptExpectedIds { get; } = [];
    public List<ResponseDocument> ResponseDocuments { get; } = [];
    public List<ResponseParseStatusRow> ResponseStatuses { get; } = [];

    public int PromptIndex { get; set; }
    public bool SessionInvalidated { get; set; }
    public int UnknownResponseIdCount { get; set; }
    public bool SettingResponseText { get; set; }
    public string? BackendFingerprint { get; set; }
    public string? EngineFingerprint { get; set; }
    public bool IsBusy { get; private set; }
    public CancellationTokenSource? ResponseParseDebounce { get; private set; }

    public bool TryBeginBusy()
    {
        if (IsBusy)
            return false;
        IsBusy = true;
        return true;
    }

    public void EndBusy() => IsBusy = false;

    public void InvalidateFingerprints()
    {
        BackendFingerprint = null;
        EngineFingerprint = null;
    }

    public void ResetForNewSources()
    {
        SessionInvalidated = false;
        UnknownResponseIdCount = 0;
        PromptIndex = 0;
        SettingResponseText = false;
        Prompts.Clear();
        PromptExpectedIds.Clear();
        ResponseDocuments.Clear();
        ResponseStatuses.Clear();
        Rows.Clear();
        CancelResponseParseDebounce();
    }

    public CancellationTokenSource RestartResponseParseDebounce()
    {
        CancelResponseParseDebounce();
        ResponseParseDebounce = new CancellationTokenSource();
        return ResponseParseDebounce;
    }

    public bool OwnsResponseParseDebounce(CancellationTokenSource source) =>
        ReferenceEquals(ResponseParseDebounce, source);

    public void CompleteResponseParseDebounce(CancellationTokenSource source)
    {
        if (ReferenceEquals(ResponseParseDebounce, source))
            ResponseParseDebounce = null;
        source.Dispose();
    }

    public void CancelResponseParseDebounce()
    {
        ResponseParseDebounce?.Cancel();
        ResponseParseDebounce?.Dispose();
        ResponseParseDebounce = null;
    }

    public void Dispose() => CancelResponseParseDebounce();
}

internal sealed record ResponseDocument(string Name, string Content);
internal sealed record ResponseParseStatusRow(
    string Batch,
    string Source,
    string Parsed,
    string Unchanged,
    string Changed,
    string Missing,
    string Conflicts,
    string Unknown,
    string Status)
{
    public static ResponseParseStatusRow NotImported(int batch, IReadOnlyList<int> expectedIds) =>
        new(
            batch.ToString("000"),
            "—",
            $"0/{expectedIds.Count}",
            "0",
            "0",
            FormatIds(expectedIds),
            "—",
            "—",
            "尚未匯入");

    public static ResponseParseStatusRow Unparsed(string source, string message) =>
        new("—", source, "0", "—", "—", message, "—", "—", "無法解析");

    public static ResponseParseStatusRow UnparsedBatch(
        int batch,
        IReadOnlyList<string> sources,
        IReadOnlyList<int> expectedIds,
        string message) =>
        new(
            batch.ToString("000"),
            string.Join("、", sources),
            $"0/{expectedIds.Count}",
            "—",
            "—",
            expectedIds.Count == 0 ? message : FormatIds(expectedIds),
            "—",
            "—",
            "無法解析");

    public static ResponseParseStatusRow UnknownIds(IReadOnlyList<int> ids) =>
        new("—", "全部回覆", "—", "—", "—", "—", "—", FormatIds(ids), "有未知編號");

    public static string FormatIds(IEnumerable<int> ids)
    {
        var values = ids.Order().ToArray();
        return values.Length == 0 ? "—" : string.Join("、", values);
    }

    public override string ToString() =>
        $"第 {Batch} 批｜{Source}｜已解析 {Parsed}｜未修改 {Unchanged}｜已修改 {Changed}｜缺漏 {Missing}｜衝突 {Conflicts}｜未知 {Unknown}｜{Status}";
}

internal sealed record ReviewEvidence(
    string Status,
    string Priority,
    int Score,
    IReadOnlyList<string> FlagCodes,
    IReadOnlyList<string> Alternatives)
{
    public bool IsHighRisk =>
        string.Equals(Priority, "high", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(Priority, "critical", StringComparison.OrdinalIgnoreCase);

    public string Summary
    {
        get
        {
            if (Status == "not_provided")
                return "Evidence 未提供；不能據此判定低風險。";
            if (Status == "unmatched")
                return "Evidence 未匹配；不使用任何風險推論。";
            if (Status == "ambiguous")
                return "Evidence 有多重匹配；不使用任何風險推論。";
            if (Status != "matched")
                return $"Evidence 狀態：{Status}";

            var flags = FlagCodes.Count == 0 ? "無硬錯誤訊號" : string.Join("、", FlagCodes);
            var alternatives = Alternatives.Count == 0
                ? string.Empty
                : $"｜候選：{string.Join("；", Alternatives.Take(3))}";
            return $"風險：{Priority} / {Score}｜訊號：{flags}{alternatives}";
        }
    }
}

internal sealed class ReviewUiRow
{
    public int ReviewId { get; }
    public Guid RowId { get; }
    public int SubtitleNumber { get; }
    public string OriginalText { get; }
    public string AiText { get; }
    public bool Conflict { get; }
    public bool MissingResponse { get; }
    public bool Changed { get; }
    public ReviewDecisionKind Decision { get; set; }
    public string? ManualText { get; set; }
    public ReviewEvidence Evidence { get; }

    public ReviewUiRow(
        int reviewId,
        Guid rowId,
        int subtitleNumber,
        string originalText,
        string aiText,
        bool conflict,
        bool missingResponse,
        bool changed,
        ReviewDecisionKind decision,
        ReviewEvidence? evidence = null)
    {
        ReviewId = reviewId;
        RowId = rowId;
        SubtitleNumber = subtitleNumber;
        OriginalText = originalText;
        AiText = aiText;
        Conflict = conflict;
        MissingResponse = missingResponse;
        Changed = changed;
        Decision = decision;
        Evidence = evidence ?? new ReviewEvidence("not_provided", "normal", 0, [], []);
    }

    public bool CanApplyAi =>
        !MissingResponse && !Conflict && ReviewSafety.FormatTagsMatch(OriginalText, AiText);

    public string DecisionLabel => Decision switch
    {
        ReviewDecisionKind.Apply => "套用 AI",
        ReviewDecisionKind.Keep => "保留",
        ReviewDecisionKind.Manual => "手動",
        _ => "待審",
    };

    public string PreviewLabel => Conflict
        ? "衝突"
        : MissingResponse
            ? "缺漏／保留原文"
            : !ReviewSafety.FormatTagsMatch(OriginalText, AiText)
                ? "格式不符"
                : Changed ? "有修改" : "相同";

    public string RiskLabel => Evidence.IsHighRisk
        ? $"高風險 {Evidence.Score}"
        : Evidence.Status == "matched"
            ? $"{Evidence.Priority} {Evidence.Score}"
            : Evidence.Status == "not_provided" ? "未提供" : "未匹配";

    public string Warning
    {
        get
        {
            if (Conflict)
                return "同一編號出現互相衝突的 AI 回覆，禁止直接套用；請保留或手動修正。";
            if (MissingResponse)
                return "AI 回覆缺少這一列；一鍵套用時會自動保留原文。";
            if (!ReviewSafety.FormatTagsMatch(OriginalText, AiText))
                return "AI 結果未保留原有格式標籤或換行控制碼，禁止直接套用；請手動修正。";
            return Changed ? "AI 有修改，請人工核對。" : "AI 結果與原文相同。";
        }
    }

    public override string ToString()
    {
        var flag = Conflict ? "｜衝突" : MissingResponse ? "｜缺漏" : string.Empty;
        if (Evidence.IsHighRisk)
            flag += "｜高風險";
        return $"#{SubtitleNumber}　{DecisionLabel}{flag}　{Compact(OriginalText)}";
    }

    private static string Compact(string text)
    {
        var value = text.Replace("\r", " ").Replace("\n", " ");
        return value.Length <= 42 ? value : value[..39] + "…";
    }
}
