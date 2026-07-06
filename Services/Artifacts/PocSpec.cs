using System.Text.RegularExpressions;

namespace ICOGenerator.Services.Artifacts;

/// <summary>
/// Deterministic reading of the AI Design Spec (markdown) into the checklist PocAudit compares the
/// generated demo against: the screen names of "Screens To Generate" and the bullets of
/// "Business Rules". The BA prompt pins the shape (one "### 6.n. Tên màn hình" heading per screen,
/// one "- BR-n: …" bullet per rule) precisely so this parser can stay a dumb scan; it is still
/// deliberately tolerant — a spec that predates that contract simply yields an empty checklist and
/// the audit skips the coverage checks instead of chasing ghosts.
/// </summary>
public sealed partial class PocSpec
{
    public static readonly PocSpec Empty = new([], []);

    // Defensive caps: a runaway spec must not turn the audit report into a novel.
    private const int MaxScreens = 30;
    private const int MaxRules = 40;

    public IReadOnlyList<string> Screens { get; }
    public IReadOnlyList<string> Rules { get; }

    private PocSpec(IReadOnlyList<string> screens, IReadOnlyList<string> rules)
    {
        Screens = screens;
        Rules = rules;
    }

    public static PocSpec Parse(string? specMarkdown)
    {
        if (string.IsNullOrWhiteSpace(specMarkdown))
            return Empty;

        var lines = specMarkdown.Replace("\r\n", "\n").Split('\n');

        var screens = new List<string>();
        var rules = new List<string>();
        var section = CurrentSection.Other;

        foreach (var raw in lines)
        {
            var line = raw.TrimEnd();

            var level2 = Level2HeadingRegex().Match(line);
            if (level2.Success)
            {
                section = ClassifySection(level2.Groups[1].Value);
                continue;
            }

            switch (section)
            {
                case CurrentSection.Screens:
                    var screenHeading = Level3HeadingRegex().Match(line);
                    if (screenHeading.Success)
                        AddUnique(screens, CleanScreenName(screenHeading.Groups[1].Value), MaxScreens);
                    break;

                case CurrentSection.Rules:
                    // Only TOP-LEVEL bullets/numbered items are rules — column 0, since any
                    // indentation (2+ spaces) is how markdown nests a detail under the rule above.
                    var bullet = TopLevelBulletRegex().Match(line);
                    if (bullet.Success)
                        AddUnique(rules, CleanRuleText(bullet.Groups[1].Value), MaxRules);
                    break;
            }
        }

        return screens.Count == 0 && rules.Count == 0 ? Empty : new PocSpec(screens, rules);
    }

    /// <summary>
    /// Whether a spec screen name and a POC label (nav item / section data-view) refer to the same
    /// screen: equal after normalization, or one contains the other so "Màn hình Đăng nhập" still
    /// matches a section labelled "Đăng nhập". Very short labels must match exactly — containment on
    /// 1–2 characters would pair unrelated names.
    /// </summary>
    public static bool Matches(string specScreen, string pocLabel)
    {
        var a = Key(specScreen);
        var b = Key(pocLabel);
        if (a.Length == 0 || b.Length == 0)
            return false;
        if (a == b)
            return true;

        var shorter = a.Length <= b.Length ? a : b;
        return shorter.Length >= 3 && (a.Contains(b, StringComparison.Ordinal) || b.Contains(a, StringComparison.Ordinal));
    }

    /// <summary>Normalization shared by both sides of the match (mirrors the shell's case-insensitive view routing).</summary>
    public static string Key(string label) =>
        WhitespaceRegex().Replace((label ?? string.Empty).Trim(), " ").ToLowerInvariant();

    private enum CurrentSection { Other, Screens, Rules }

    // Headings come numbered ("## 6. Screens To Generate"); classification goes by the words so a
    // renumbered spec still parses. English names are pinned by the BA prompt; the Vietnamese
    // fallbacks catch specs written before/despite it.
    private static CurrentSection ClassifySection(string headingText)
    {
        var text = Key(StripNumbering(headingText));
        if (text.Contains("screen", StringComparison.Ordinal) || text.Contains("màn hình", StringComparison.Ordinal))
            return CurrentSection.Screens;
        if (text.Contains("business rule", StringComparison.Ordinal) || text.Contains("quy tắc", StringComparison.Ordinal))
            return CurrentSection.Rules;
        return CurrentSection.Other;
    }

    // "### 6.1. Màn hình: Đăng nhập (/login)" → "Đăng nhập": drop the numbering, a "Screen:"-style
    // label prefix (only when followed by a separator — "Màn hình chính" must survive), a trailing
    // route/parenthetical hint, and markdown emphasis characters.
    private static string CleanScreenName(string raw)
    {
        var name = StripMarkdownEmphasis(StripNumbering(raw));
        name = ScreenLabelPrefixRegex().Replace(name, string.Empty);
        name = TrailingParentheticalRegex().Replace(name, string.Empty);
        return name.Trim();
    }

    private static string CleanRuleText(string raw)
    {
        var rule = StripMarkdownEmphasis(raw).Trim();
        // Placeholder bullets ("N/A", "Không có") are not rules.
        return PlaceholderBulletRegex().IsMatch(rule) ? string.Empty : rule;
    }

    private static string StripNumbering(string text) =>
        LeadingNumberingRegex().Replace(text.Trim(), string.Empty);

    private static string StripMarkdownEmphasis(string text) =>
        text.Replace("**", string.Empty).Replace("`", string.Empty).Trim();

    private static void AddUnique(List<string> list, string value, int cap)
    {
        if (value.Length == 0 || list.Count >= cap)
            return;
        if (!list.Any(x => Key(x) == Key(value)))
            list.Add(value);
    }

    // Heading "## …" (không phải "###") — dòng phân mục cấp 2 của spec markdown.
    [GeneratedRegex("^\\s{0,3}##(?!#)\\s*(.+)$")]
    private static partial Regex Level2HeadingRegex();

    // Heading "### …" — mỗi màn hình một dòng trong mục "Screens To Generate".
    [GeneratedRegex("^\\s{0,3}###\\s*(.+)$")]
    private static partial Regex Level3HeadingRegex();

    // Bullet/numbered item ở CỘT 0 (không thụt đầu dòng) — mỗi business rule một dòng.
    [GeneratedRegex("^(?:[-*+]|\\d{1,2}[.)])\\s+(.+)$")]
    private static partial Regex TopLevelBulletRegex();

    // Gộp mọi cụm khoảng trắng về một dấu cách (chuẩn hoá nhãn để so khớp).
    [GeneratedRegex("\\s+")]
    private static partial Regex WhitespaceRegex();

    // Tiền tố nhãn "Screen:" / "Màn hình:" (chỉ khi có dấu phân cách theo sau).
    [GeneratedRegex("^(?:screen|màn hình)\\s*[:\\-–—]\\s*", RegexOptions.IgnoreCase)]
    private static partial Regex ScreenLabelPrefixRegex();

    // Gợi ý route/chú thích trong ngoặc ở cuối tên màn hình.
    [GeneratedRegex("\\s*[(（][^)）]*[)）]\\s*$")]
    private static partial Regex TrailingParentheticalRegex();

    // Bullet placeholder ("N/A", "none", "Không có") — không phải rule thật.
    [GeneratedRegex("^(?:n/?a|none|không có)\\.?$", RegexOptions.IgnoreCase)]
    private static partial Regex PlaceholderBulletRegex();

    // Đánh số đầu dòng ("6.", "6.1.", "1)"…) cần cắt bỏ.
    [GeneratedRegex("^\\d{1,2}(?:\\.\\d{1,2})*[.)]?\\s*")]
    private static partial Regex LeadingNumberingRegex();
}
