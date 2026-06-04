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
}
