using System.Collections.Concurrent;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;

namespace ICOGenerator.Services.Requirements.Templates;

public class RequirementTemplateService
{
    private readonly IWebHostEnvironment _env;

    // File template .docx là tĩnh và không đổi lúc chạy, nhưng parse OpenXML khá nặng và
    // được gọi mỗi lần sinh draft (3 file/lần). Cache phần text đã trích theo tên file.
    private static readonly ConcurrentDictionary<string, string> TemplateTextCache = new(StringComparer.OrdinalIgnoreCase);

    public RequirementTemplateService(IWebHostEnvironment env)
    {
        _env = env;
    }

    public string GetBrdTemplate() => GetTemplateText("BRD_Template.docx");

    public string GetSrsTemplate() => GetTemplateText("SRS_Template.docx");

    public string GetFsdTemplate() => GetTemplateText("FSD_Template.docx");

    private string GetTemplateText(string fileName) =>
        TemplateTextCache.GetOrAdd(fileName, name => ReadDocx(EnsureTemplateDocx(name)));

    public string EnsureTemplateDocx(string fileName)
    {
        var templatePath = Path.Combine(_env.ContentRootPath, "Templates", fileName);

        if (File.Exists(templatePath))
            return templatePath;

        var base64Path = templatePath + ".base64";
        if (!File.Exists(base64Path))
            throw new FileNotFoundException($"Template file not found: {templatePath}. Also missing base64 fallback: {base64Path}");

        Directory.CreateDirectory(Path.GetDirectoryName(templatePath)!);
        var base64 = File.ReadAllText(base64Path).Trim();
        File.WriteAllBytes(templatePath, Convert.FromBase64String(base64));
        return templatePath;
    }

    public string GetUserStoriesTemplate()
    {
        return """
# User Stories

## 1. Overview
[Mô tả tổng quan user stories]

## 2. Actors
- [Actor 1]
- [Actor 2]

## 3. User Stories

### US-001: [Tên user story]
As a [role],
I want [goal],
so that [benefit].

Acceptance Criteria:
- Given ...
- When ...
- Then ...

Priority: Must Have
Notes: ...

## 4. Open Questions
- [Câu hỏi cần làm rõ]
""";
    }

    private static string ReadDocx(string path)
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
                .Where(x => !string.IsNullOrWhiteSpace(x))
        );
    }
}
