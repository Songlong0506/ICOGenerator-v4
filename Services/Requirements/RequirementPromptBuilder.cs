using ICOGenerator.Domain;

namespace ICOGenerator.Services.Requirements;

public class RequirementPromptBuilder
{
    // Lượt "Write Requirement" phía user: chỉ sinh Product Brief (dễ hiểu). AI Design Spec được
    // sinh ở bước Approve (xem BuildAiDesignSpec).
    public string BuildProductBrief(
        Project project,
        string userMessage,
        string currentProductBrief)
    {
        return $$"""
Project:
{{project.Name}}

Project Description:
{{project.Description}}

User latest message:
{{userMessage}}

Current Product Brief preview:
{{currentProductBrief}}

Your task:
- Write/update the Product Brief in plain, non-technical Vietnamese for a normal end user.
- Return JSON only.
""";
    }

    // Bước Approve: sinh AI Design Spec (kỹ thuật, có cấu trúc) từ Product Brief ĐÃ DUYỆT để Developer
    // Agent dựng POC. Bám đúng phạm vi của Product Brief, không thêm tính năng ngoài.
    public string BuildAiDesignSpec(
        Project project,
        string approvedProductBrief,
        string currentAiDesignSpec)
    {
        return $$"""
Project:
{{project.Name}}

Project Description:
{{project.Description}}

Approved Product Brief (source of truth, non-technical):
{{approvedProductBrief}}

Current AI Design Spec preview:
{{currentAiDesignSpec}}

Your task:
- Write the AI Design Spec (technical, structured) so the Developer Agent can build a POC.
- It must describe the SAME product as the approved Product Brief (matching screens/features); only the wording differs.
- Do NOT add features or screens that are not in the approved Product Brief.
- Return JSON only.
""";
    }

    // Lượt team dev trigger ở Agent Dashboard: soạn bộ tài liệu kỹ thuật nặng từ Product Brief +
    // AI Design Spec đã duyệt, bám theo template công ty.
    public string BuildTechnicalDocs(
        Project project,
        string productBrief,
        string aiDesignSpec,
        string currentBrd,
        string currentSrs,
        string currentFsd,
        string currentStories,
        string brdTemplate,
        string srsTemplate,
        string fsdTemplate,
        string userStoriesTemplate)
    {
        return $$"""
Project:
{{project.Name}}

Project Description:
{{project.Description}}

Approved Product Brief (source of truth, non-technical):
{{productBrief}}

Approved AI Design Spec (source of truth, technical):
{{aiDesignSpec}}

Current BRD preview:
{{currentBrd}}

Current SRS preview:
{{currentSrs}}

Current FSD preview:
{{currentFsd}}

Current UserStories preview:
{{currentStories}}

Company BRD Template:
{{brdTemplate}}

Company SRS Template:
{{srsTemplate}}

Company FSD Template:
{{fsdTemplate}}

Company UserStories Template:
{{userStoriesTemplate}}

Your task:
- Update BRD.docx structured data based on Company BRD Template.
- Update SRS.docx structured data based on Company SRS Template.
- Update FSD.docx structured data based on Company FSD Template.
- Update UserStories.docx content.
- Keep everything consistent with the approved Product Brief and AI Design Spec.

General rules:
- Keep the same section order as the templates.
- Fill unknown sections with "TBD" or "Cần làm rõ".
- Do NOT write source code or implementation files.
- Do NOT call tools.
- Return JSON only.
""";
    }
}
