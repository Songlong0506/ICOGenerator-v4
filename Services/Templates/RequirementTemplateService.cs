namespace ICOGenerator.Services.Templates;

public class RequirementTemplateService
{
    private readonly IWebHostEnvironment _env;

    public RequirementTemplateService(IWebHostEnvironment env)
    {
        _env = env;
    }

    public string GetBrdTemplate()
    {
        return DocxTextExtractor.Extract(EnsureTemplateDocx("BRD_Template.docx"));
    }

    public string GetSrsTemplate()
    {
        return DocxTextExtractor.Extract(EnsureTemplateDocx("SRS_Template.docx"));
    }

    public string GetFsdTemplate()
    {
        return DocxTextExtractor.Extract(EnsureTemplateDocx("FSD_Template.docx"));
    }

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

}
