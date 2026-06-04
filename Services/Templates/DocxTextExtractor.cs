using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;

namespace ICOGenerator.Services.Templates;

public static class DocxTextExtractor
{
    public static string Extract(string path)
    {
        if (!File.Exists(path))
            return "";

        using var doc = WordprocessingDocument.Open(path, false);

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
