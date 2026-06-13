using System.Net;
using System.Text;
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

        File.Copy(templatePath, outputPath, overwrite: true);

        using var doc = WordprocessingDocument.Open(outputPath, true);

        var texts = doc.MainDocumentPart!
            .Document
            .Descendants<Text>()
            .ToList();

        foreach (var text in texts)
        {
            foreach (var item in replacements)
            {
                if (text.Text.Contains(item.Key))
                {
                    text.Text = text.Text.Replace(item.Key, item.Value ?? "");
                }
            }
        }

        doc.MainDocumentPart.Document.Save();

        return outputPath;
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
    /// Converts a .docx file into formatted HTML, preserving headings, tables
    /// and basic run formatting (bold / italic) so the preview resembles the
    /// document as opened in Word instead of a flat block of text.
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
