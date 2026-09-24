using System.Text;
using System.Text.RegularExpressions;

namespace AudioWorkflow;

/// <summary>
/// Normalizes subtitle text punctuation without touching cue identity or timing.
/// </summary>
public static partial class SubtitlePunctuationNormalizer
{
    private const string TwoSpaces = "  ";

    [GeneratedRegex(@"[ \t]*([?!？！]+)[ \t]*", RegexOptions.CultureInvariant)]
    private static partial Regex QuestionExclamationRegex();

    [GeneratedRegex(@"[^\S\r\n]+", RegexOptions.CultureInvariant)]
    private static partial Regex HorizontalWhitespaceRegex();

    [GeneratedRegex(@"<[^<>]*>|\{[^{}]*\}", RegexOptions.CultureInvariant)]
    private static partial Regex SubtitleTagRegex();

    [GeneratedRegex("\\uE000[0-9A-F]+\\uE001", RegexOptions.CultureInvariant)]
    private static partial Regex ProtectedTagTokenRegex();

    public static string NormalizeText(string? value)
    {
        var text = (value ?? string.Empty).Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n');
        return string.Join('\n', text.Split('\n').Select(NormalizeLine));
    }

    private static string NormalizeLine(string line)
    {
        var tags = new List<string>();
        line = SubtitleTagRegex().Replace(line, match =>
        {
            var token = $"\uE000{tags.Count:X}\uE001";
            tags.Add(match.Value);
            return token;
        });

        line = QuestionExclamationRegex().Replace(line, match =>
        {
            var punctuation = new StringBuilder(match.Groups[1].Length);
            foreach (var character in match.Groups[1].Value)
                punctuation.Append(character is '?' or '？' ? '？' : '！');

            var before = HasVisibleText(line[..match.Index]) ? TwoSpaces : string.Empty;
            var afterIndex = match.Index + match.Length;
            var after = HasVisibleText(line[afterIndex..]) ? TwoSpaces : string.Empty;
            return before + punctuation + after;
        });

        var output = new StringBuilder(line.Length);
        for (var index = 0; index < line.Length; index++)
        {
            var character = line[index];
            var numericSeparator = index > 0 && index < line.Length - 1 &&
                                   char.IsDigit(line[index - 1]) && char.IsDigit(line[index + 1]);
            output.Append(character switch
            {
                ',' when !numericSeparator => '，',
                '.' when !numericSeparator => '。',
                ':' when !numericSeparator => '：',
                '．' when !numericSeparator => '。',
                ';' => '；',
                '(' => '（',
                ')' => '）',
                '[' => '【',
                ']' => '】',
                _ => character,
            });
        }

        line = HorizontalWhitespaceRegex().Replace(output.ToString(), TwoSpaces).Trim();
        for (var index = 0; index < tags.Count; index++)
            line = line.Replace($"\uE000{index:X}\uE001", tags[index], StringComparison.Ordinal);
        return line;
    }

    private static bool HasVisibleText(string value) =>
        !string.IsNullOrWhiteSpace(ProtectedTagTokenRegex().Replace(value, string.Empty));
}
