using System.Net;
using System.Text;
using System.Xml;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;

namespace ICOGenerator.Services.Requirements.Templates;

public class DocxTemplateWriter
{
    public string CreateFromTemplate(
        string templatePath,
        string outputPath,
        Dictionary<string, string> replacements)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);

        // Build into a temp file and move into place only after Save() succeeds, so a mid-way failure can't leave a half-written .docx that downstream code treats as valid.
        var tempPath = outputPath + ".tmp";
        try
        {
            File.Copy(templatePath, tempPath, overwrite: true);

            using (var doc = WordprocessingDocument.Open(tempPath, true))
            {
                var texts = doc.MainDocumentPart!
                    .Document
                    .Descendants<Text>()
                    .ToList();

                // Replace longest keys first so a short marker can't clobber part of a longer one sharing its prefix; sanitize each value because XML-illegal chars would make Save() throw and corrupt the file.
                foreach (var item in replacements.OrderByDescending(r => r.Key.Length))
                {
                    var value = SanitizeXmlText(item.Value);

                    foreach (var text in texts)
                    {
                        if (text.Text.Contains(item.Key))
                            text.Text = text.Text.Replace(item.Key, value);
                    }
                }

                doc.MainDocumentPart.Document.Save();
            }

            // Atomic on the same volume: outputPath is only touched once the document is
            // fully written and the package is closed.
            File.Move(tempPath, outputPath, overwrite: true);
        }
        catch
        {
            TryDelete(tempPath);
            throw;
        }

        return outputPath;
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
            // Best-effort cleanup; the original failure is the one worth surfacing.
        }
    }

    /// <summary>
    /// Removes XML 1.0-illegal chars (control chars, lone surrogates) that would otherwise make <c>Document.Save()</c> throw and corrupt the document; valid surrogate pairs are preserved.
    /// </summary>
    public static string SanitizeXmlText(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return string.Empty;

        var sb = new StringBuilder(value.Length);

        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];

            if (char.IsHighSurrogate(c) && i + 1 < value.Length && char.IsLowSurrogate(value[i + 1]))
            {
                sb.Append(c);
                sb.Append(value[i + 1]);
                i++;
            }
            else if (XmlConvert.IsXmlChar(c))
            {
                sb.Append(c);
            }
        }

        return sb.ToString();
    }

    public string ExtractText(string docxPath)
    {
        using var doc = WordprocessingDocument.Open(docxPath, false);

        var body = doc.MainDocumentPart?.Document.Body;

        if (body == null)
            return "";

        return string.Join(
            Environment.NewLine,
            body.Descendants<Paragraph>()
                .Select(p => p.InnerText)
                .Where(x => !string.IsNullOrWhiteSpace(x)));
    }

    /// <summary>
    /// Converts a .docx into HTML, preserving headings, tables and bold/italic so the preview resembles the Word document instead of a flat block of text.
    /// </summary>
    public string ExtractHtml(string docxPath)
    {
        using var doc = WordprocessingDocument.Open(docxPath, false);

        var body = doc.MainDocumentPart?.Document.Body;

        if (body == null)
            return "";

        var sb = new StringBuilder();

        foreach (var element in body.Elements())
        {
            switch (element)
            {
                case Paragraph paragraph:
                    AppendParagraph(sb, paragraph);
                    break;
                case Table table:
                    AppendTable(sb, table);
                    break;
            }
        }

        return sb.ToString();
    }

    private static void AppendParagraph(StringBuilder sb, Paragraph paragraph)
    {
        var inner = BuildRunsHtml(paragraph);

        if (string.IsNullOrWhiteSpace(inner))
            return;

        var styleId = paragraph.ParagraphProperties?.ParagraphStyleId?.Val?.Value ?? "";
        var headingLevel = GetHeadingLevel(styleId);

        if (headingLevel > 0)
            sb.Append($"<h{headingLevel}>").Append(inner).Append($"</h{headingLevel}>");
        else
            sb.Append("<p>").Append(inner).Append("</p>");
    }

    private static void AppendTable(StringBuilder sb, Table table)
    {
        sb.Append("<table class=\"doc-table\">");

        foreach (var row in table.Elements<TableRow>())
        {
            sb.Append("<tr>");

            foreach (var cell in row.Elements<TableCell>())
            {
                var cellHtml = new StringBuilder();

                foreach (var paragraph in cell.Elements<Paragraph>())
                {
                    var inner = BuildRunsHtml(paragraph);
                    if (string.IsNullOrWhiteSpace(inner))
                        continue;

                    if (cellHtml.Length > 0)
                        cellHtml.Append("<br>");

                    cellHtml.Append(inner);
                }

                sb.Append("<td>").Append(cellHtml).Append("</td>");
            }

            sb.Append("</tr>");
        }

        sb.Append("</table>");
    }

    private static string BuildRunsHtml(OpenXmlElement container)
    {
        var sb = new StringBuilder();

        foreach (var run in container.Descendants<Run>())
        {
            var text = string.Concat(run.Descendants<Text>().Select(t => t.Text));

            if (text.Length == 0)
            {
                if (run.Descendants<Break>().Any())
                    sb.Append("<br>");
                continue;
            }

            var encoded = WebUtility.HtmlEncode(text);

            if (IsOn(run.RunProperties?.Bold))
                encoded = "<strong>" + encoded + "</strong>";

            if (IsOn(run.RunProperties?.Italic))
                encoded = "<em>" + encoded + "</em>";

            sb.Append(encoded);
        }

        return sb.ToString();
    }

    private static int GetHeadingLevel(string styleId)
    {
        if (string.IsNullOrEmpty(styleId))
            return 0;

        var normalized = styleId.Replace(" ", "");

        if (normalized.Equals("Title", StringComparison.OrdinalIgnoreCase))
            return 1;

        if (normalized.StartsWith("Heading", StringComparison.OrdinalIgnoreCase)
            && int.TryParse(normalized["Heading".Length..], out var level))
            return Math.Clamp(level, 1, 6);

        return 0;
    }

    private static bool IsOn(OnOffType? toggle)
    {
        if (toggle == null)
            return false;

        return toggle.Val == null || toggle.Val.Value;
    }
}
