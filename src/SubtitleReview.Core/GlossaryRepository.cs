using System.Text;
using System.Text.Json;

namespace SubtitleReview.Core;

public static class GlossaryRepository
{
    private static readonly string[] FieldNames =
        ["enabled", "source", "target", "category", "note", "examples"];

    private static readonly HashSet<string> SourceAliases = new(StringComparer.OrdinalIgnoreCase)
        { "source", "wrong", "wrong_term", "from", "mistake", "asr", "錯誤詞", "錯字", "原詞" };
    private static readonly HashSet<string> TargetAliases = new(StringComparer.OrdinalIgnoreCase)
        { "target", "correct", "correct_term", "to", "replacement", "正確詞", "正字", "替換詞" };
    private static readonly HashSet<string> EnabledAliases = new(StringComparer.OrdinalIgnoreCase)
        { "enabled", "enable", "on", "啟用" };
    private static readonly HashSet<string> CategoryAliases = new(StringComparer.OrdinalIgnoreCase)
        { "category", "type", "分類", "類別" };
    private static readonly HashSet<string> NoteAliases = new(StringComparer.OrdinalIgnoreCase)
        { "note", "notes", "reason", "memo", "備註", "說明" };
    private static readonly HashSet<string> ExamplesAliases = new(StringComparer.OrdinalIgnoreCase)
        { "examples", "example", "sample", "例句", "範例" };

    public static IReadOnlyList<GlossaryRow> Read(string path)
    {
        if (!File.Exists(path))
            return [];

        var text = File.ReadAllText(path, Encoding.UTF8);
        if (text.Length > 0 && text[0] == '﻿')
            text = text[1..];
        var records = ParseCsv(text);
        if (records.Count == 0)
            return [];

        var firstDataRow = 0;
        var sourceIndex = 0;
        var targetIndex = 1;
        int? enabledIndex = null;
        int? categoryIndex = null;
        int? noteIndex = null;
        int? examplesIndex = null;

        var header = records[0];
        var namedSource = FindColumn(header, SourceAliases);
        var namedTarget = FindColumn(header, TargetAliases);
        if (namedSource is not null && namedTarget is not null)
        {
            firstDataRow = 1;
            sourceIndex = namedSource.Value;
            targetIndex = namedTarget.Value;
            enabledIndex = FindColumn(header, EnabledAliases);
            categoryIndex = FindColumn(header, CategoryAliases);
            noteIndex = FindColumn(header, NoteAliases);
            examplesIndex = FindColumn(header, ExamplesAliases);
        }

        var result = new List<GlossaryRow>();
        foreach (var record in records.Skip(firstDataRow))
        {
            if (record.Count == 0 || record.All(string.IsNullOrWhiteSpace))
                continue;
            if (Math.Max(sourceIndex, targetIndex) >= record.Count)
                continue;

            var source = record[sourceIndex].Trim();
            var target = record[targetIndex].Trim();
            if (source.Length == 0 || source.StartsWith('#'))
                continue;

            result.Add(new GlossaryRow(
                Value(record, enabledIndex, "1"),
                source,
                target,
                Value(record, categoryIndex),
                Value(record, noteIndex),
                Value(record, examplesIndex)));
        }
        return result;
    }

    internal static void Append(string path, IReadOnlyList<GlossaryRow> added)
    {
        ArgumentNullException.ThrowIfNull(added);
        var fullPath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        var existing = File.Exists(fullPath) ? File.ReadAllText(fullPath, Encoding.UTF8) : string.Empty;
        var records = ParseCsv(existing.TrimStart('\uFEFF'));
        IReadOnlyList<string> header = records.Count == 0 ? FieldNames : records[0];
        var namedSource = FindColumn(header, SourceAliases);
        var namedTarget = FindColumn(header, TargetAliases);
        var namedHeader = namedSource is not null && namedTarget is not null;
        var sourceIndex = namedHeader ? namedSource!.Value : 0;
        var targetIndex = namedHeader ? namedTarget!.Value : 1;
        var enabledIndex = namedHeader ? FindColumn(header, EnabledAliases) : null;
        var categoryIndex = namedHeader ? FindColumn(header, CategoryAliases) : null;
        var noteIndex = namedHeader ? FindColumn(header, NoteAliases) : null;
        var examplesIndex = namedHeader ? FindColumn(header, ExamplesAliases) : null;
        var temp = Path.Combine(
            Path.GetDirectoryName(fullPath)!,
            $".{Path.GetFileName(fullPath)}.{Guid.NewGuid():N}.tmp");
        var backup = fullPath + ".bak";

        try
        {
            using (var writer = new StreamWriter(temp, false, new UTF8Encoding(true)))
            {
                writer.NewLine = existing.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
                if (existing.Length == 0)
                    WriteRecord(writer, FieldNames);
                else
                {
                    writer.Write(existing.TrimStart('\uFEFF'));
                    if (!existing.EndsWith('\n') && !existing.EndsWith('\r'))
                        writer.WriteLine();
                }
                foreach (var row in added)
                {
                    var fields = new string[Math.Max(2, header.Count)];
                    Array.Fill(fields, string.Empty);
                    fields[sourceIndex] = row.Source;
                    fields[targetIndex] = row.Target;
                    if (enabledIndex is int enabled) fields[enabled] = row.Enabled;
                    if (categoryIndex is int category) fields[category] = row.Category;
                    if (noteIndex is int note) fields[note] = row.Note;
                    if (examplesIndex is int examples) fields[examples] = row.Examples;
                    WriteRecord(writer, fields);
                }
            }

            if (File.Exists(fullPath))
                File.Copy(fullPath, backup, overwrite: true);
            File.Move(temp, fullPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp))
                File.Delete(temp);
        }
    }

    public static IReadOnlyList<string> LoadProtectedPhrases(string path)
    {
        if (!File.Exists(path))
            return [];

        using var document = JsonDocument.Parse(
            File.ReadAllText(path, Encoding.UTF8).TrimStart('﻿'));
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("不可切片語 JSON 根節點必須是物件。");
        if (!root.TryGetProperty("schema_name", out var schemaName) ||
            schemaName.GetString() != "subtitle-review-protected-phrases")
            throw new InvalidDataException("Protected phrase JSON schema_name is not supported.");
        if (!root.TryGetProperty("schema_version", out var schemaVersion) ||
            schemaVersion.ToString() != "1.0.0")
            throw new InvalidDataException("不可切片語 JSON schema_version 不支援。");
        if (!root.TryGetProperty("config_version", out var configVersion) ||
            string.IsNullOrWhiteSpace(configVersion.GetString()))
            throw new InvalidDataException("不可切片語 JSON 缺少 config_version。");
        if (!root.TryGetProperty("rules", out var rules) ||
            rules.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("不可切片語 JSON rules 必須是清單。");

        var result = new List<string>();
        foreach (var rule in rules.EnumerateArray())
        {
            if (rule.ValueKind == JsonValueKind.String)
            {
                var phraseText = rule.GetString()?.Trim();
                if (!string.IsNullOrWhiteSpace(phraseText))
                    result.Add(phraseText);
                continue;
            }

            if (rule.ValueKind != JsonValueKind.Object)
                continue;
            if (rule.TryGetProperty("enabled", out var enabled) && !Enabled(enabled, true))
                continue;
            if (rule.TryGetProperty("keep_together", out var keepTogether) &&
                !Enabled(keepTogether, true))
                continue;

            var phrase = rule.TryGetProperty("text", out var text)
                ? text.GetString()
                : rule.TryGetProperty("phrase", out var alias)
                    ? alias.GetString()
                    : null;
            if (!string.IsNullOrWhiteSpace(phrase))
                result.Add(phrase.Trim());
        }
        return result;
    }

    internal static bool IsEnabled(GlossaryRow row)
    {
        var value = (row.Enabled ?? "1").Trim().ToLowerInvariant();
        return value is not ("0" or "false" or "no" or "off" or "停用" or "否") &&
               !string.IsNullOrWhiteSpace(row.Source) &&
               !string.IsNullOrWhiteSpace(row.Target);
    }

    private static bool Enabled(JsonElement value, bool defaultValue)
    {
        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Number => value.TryGetInt32(out var number) ? number != 0 : defaultValue,
            JsonValueKind.String => ParseEnabled(value.GetString(), defaultValue),
            JsonValueKind.Null or JsonValueKind.Undefined => defaultValue,
            _ => defaultValue,
        };
    }

    private static bool ParseEnabled(string? value, bool defaultValue)
    {
        if (string.IsNullOrWhiteSpace(value))
            return defaultValue;
        return value.Trim().ToLowerInvariant() switch
        {
            "0" or "false" or "no" or "off" or "停用" or "否" => false,
            "1" or "true" or "yes" or "on" or "啟用" or "是" => true,
            _ => defaultValue,
        };
    }

    private static int? FindColumn(IReadOnlyList<string> headers, HashSet<string> aliases)
    {
        for (var index = 0; index < headers.Count; index++)
        {
            var normalized = headers[index].Trim().ToLowerInvariant().Replace(" ", "_");
            if (aliases.Contains(normalized))
                return index;
        }
        return null;
    }

    private static string Value(IReadOnlyList<string> record, int? index, string fallback = "")
    {
        if (index is null || index.Value < 0 || index.Value >= record.Count)
            return fallback;
        return record[index.Value].Trim();
    }

    private static List<List<string>> ParseCsv(string text)
    {
        var rows = new List<List<string>>();
        var row = new List<string>();
        var field = new StringBuilder();
        var quoted = false;

        for (var i = 0; i < text.Length; i++)
        {
            var ch = text[i];
            if (quoted)
            {
                if (ch == '"')
                {
                    if (i + 1 < text.Length && text[i + 1] == '"')
                    {
                        field.Append('"');
                        i++;
                    }
                    else
                    {
                        quoted = false;
                    }
                }
                else
                {
                    field.Append(ch);
                }
                continue;
            }

            if (ch == '"' && field.Length == 0)
            {
                quoted = true;
            }
            else if (ch == ',')
            {
                row.Add(field.ToString());
                field.Clear();
            }
            else if (ch is '\r' or '\n')
            {
                if (ch == '\r' && i + 1 < text.Length && text[i + 1] == '\n')
                    i++;
                row.Add(field.ToString());
                field.Clear();
                rows.Add(row);
                row = [];
            }
            else
            {
                field.Append(ch);
            }
        }

        if (field.Length > 0 || row.Count > 0)
        {
            row.Add(field.ToString());
            rows.Add(row);
        }
        return rows;
    }

    private static void WriteRecord(TextWriter writer, IEnumerable<string?> fields)
    {
        writer.WriteLine(string.Join(",", fields.Select(EscapeCsv)));
    }

    private static string EscapeCsv(string? value)
    {
        var text = value ?? string.Empty;
        if (text.IndexOfAny([',', '"', '\r', '\n']) < 0)
            return text;
        return "\"" + text.Replace("\"", "\"\"") + "\"";
    }
}
