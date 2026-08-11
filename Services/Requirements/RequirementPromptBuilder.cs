using ICOGenerator.Domain;

namespace ICOGenerator.Services.Requirements;

public class RequirementPromptBuilder
{
    // Lượt "Write Requirement" phía user: chỉ sinh Product Brief (dễ hiểu). AI Design Spec được
    // sinh ở bước Approve (xem BuildAiDesignSpec). conversationTranscript là bản ghi Hỏi–Đáp đầy đủ
    // (BA hỏi / Người dùng trả lời) — giữ cả câu hỏi để câu trả lời ngắn kiểu chip không mất ngữ cảnh.
    // organizationContext (có thể rỗng): bối cảnh tổ chức Bosch + đơn vị yêu cầu, render từ dữ liệu HR
    // thật — để tài liệu dùng đúng tên phòng ban/HoD thay vì "TBD". Xem OrganizationContextService.
    public string BuildProductBrief(
        Project project,
        string conversationTranscript,
        string currentProductBrief,
        string organizationContext = "",
        string distilledState = "")
    {
        return $$"""
Project:
{{project.Name}}

Project Description:
{{project.Description}}
{{OrganizationSection(organizationContext)}}
Hội thoại khai thác yêu cầu (BA hỏi – Người dùng trả lời):
{{conversationTranscript}}
{{DistilledStateSection(distilledState)}}
Current Product Brief preview:
{{currentProductBrief}}

Your task:
- Write/update the Product Brief in plain, non-technical Vietnamese for a normal end user.
- Return JSON only.
""";
    }

    // Vòng TỰ SOÁT bản nháp Product Brief: reviewer đối chiếu bản nháp với hội thoại để tìm vấn đề
    // thực chất (bỏ sót/sai lệch/tự thêm/giả định còn sót/thiếu mục). Xem Prompts/BusinessAnalyst/product-brief-review.v2.md.
    // organizationContext phải TRÙNG với khối đã đưa cho lượt soạn: tên phòng ban/HoD lấy từ đó là dữ
    // liệu hợp lệ, reviewer không được tính là "tự thêm ngoài hội thoại".
    public string BuildProductBriefReview(
        Project project,
        string conversationTranscript,
        string draftProductBrief,
        string organizationContext = "",
        string distilledState = "")
    {
        return $$"""
Project:
{{project.Name}}

Project Description:
{{project.Description}}
{{OrganizationSection(organizationContext)}}
Hội thoại khai thác yêu cầu (BA hỏi – Người dùng trả lời):
{{conversationTranscript}}
{{DistilledStateSection(distilledState)}}
Bản nháp Product Brief cần soát:
{{draftProductBrief}}

Your task:
- Review the draft against the conversation and list substantive issues.
- Organization facts (department names, HoD/manager names) taken from the organization context above are legitimate — do NOT flag them as fabricated.
- Return JSON only.
""";
    }

    // Vòng SỬA sau tự soát (chạy đúng một lần): cùng system prompt với lượt soạn, nhưng kèm bản nháp
    // trước + danh sách vấn đề reviewer đã chỉ ra để model sửa đúng chỗ, giữ nguyên phần không bị chê.
    public string BuildProductBriefRevision(
        Project project,
        string conversationTranscript,
        string draftProductBrief,
        IReadOnlyList<string> reviewIssues,
        string organizationContext = "",
        string distilledState = "")
    {
        return $$"""
Project:
{{project.Name}}

Project Description:
{{project.Description}}
{{OrganizationSection(organizationContext)}}
Hội thoại khai thác yêu cầu (BA hỏi – Người dùng trả lời):
{{conversationTranscript}}
{{DistilledStateSection(distilledState)}}
Bản nháp Product Brief trước (cần sửa):
{{draftProductBrief}}

Kết quả tự soát — các vấn đề PHẢI sửa cho hết:
{{string.Join("\n", reviewIssues.Select(i => "- " + i))}}

Your task:
- Rewrite the Product Brief fixing EVERY listed issue; keep the parts that were not criticized.
- Return JSON only.
""";
    }

    // Bước Approve: sinh AI Design Spec (kỹ thuật, có cấu trúc) từ Product Brief ĐÃ DUYỆT để Developer
    // Agent dựng POC. Bám đúng phạm vi của Product Brief, không thêm tính năng ngoài.
    // organizationContext (có thể rỗng): spec là ĐẦU VÀO DUY NHẤT của bước dựng POC, nên tên phòng ban/
    // chức danh/người thật phải vào spec (mục Sample Data) thì dữ liệu mẫu của POC mới "trông như của
    // công ty mình" thay vì "Nguyễn Văn A / Phòng X" chung chung.
    // workedExamples (có thể rỗng): các ví dụ tính thử người dùng ĐÃ xác nhận cho quy tắc định lượng
    // (Project.WorkedExamples, chắt từ hội thoại). Chúng phải đi vào mục "## 13. Worked Examples" của spec
    // để POC dựng từ spec đối chiếu ĐỘC LẬP con số kỳ vọng — xem ai-design-spec.v1.md và PocRuntimeChecker.
    // assumptionCorrections (có thể rỗng): các giả định người dùng đã BÁC ở cổng xác nhận giả định, kèm ý
    // đúng của họ (Project.SpecAssumptionCorrections). Phải nạp vào đây vì spec sinh từ Product Brief chứ
    // không đọc transcript — thiếu khối này thì lượt sinh lại sau khi user báo sai vẫn đẻ ra đúng giả định
    // vừa bị bác, và cổng thành vòng lặp vô nghĩa.
    // acceptanceCriteria (có thể rỗng): các câu "Hoàn thành khi: …" bóc từ chính Product Brief đã duyệt,
    // render sẵn thành các dòng của mục "## 14. Acceptance Criteria" (xem BriefAcceptanceCriteria). Nạp
    // vào đây vì spec là đầu vào DUY NHẤT của bước dựng POC và của bước sinh kịch bản nghiệm thu — không
    // có khối này thì tiêu chí nghiệm thu người dùng đã duyệt dừng lại ở Product Brief.
    public string BuildAiDesignSpec(
        Project project,
        string approvedProductBrief,
        string currentAiDesignSpec,
        string organizationContext = "",
        string? workedExamples = null,
        string? assumptionCorrections = null,
        string? realSampleData = null,
        string? acceptanceCriteria = null)
    {
        return $$"""
Project:
{{project.Name}}

Project Description:
{{project.Description}}
{{OrganizationSection(organizationContext)}}
Approved Product Brief (source of truth, non-technical):
{{approvedProductBrief}}
{{acceptanceCriteria}}{{WorkedExamplesSection(workedExamples)}}{{AssumptionCorrectionsSection(assumptionCorrections)}}{{RealSampleDataSection(realSampleData)}}
Current AI Design Spec preview:
{{currentAiDesignSpec}}

Your task:
- Write the AI Design Spec (technical, structured) so the Developer Agent can build a POC.
- It must describe the SAME product as the approved Product Brief (matching screens/features); only the wording differs.
- Do NOT add features or screens that are not in the approved Product Brief.
- Copy the user-approved acceptance sentences above VERBATIM into "## 14. Acceptance Criteria" (one "- AC-n (<feature>): <sentence>" bullet each) — they are the user's own wording and the target the POC is accepted against.
- When the organization context above names real departments/roles/people relevant to this project, use those REAL names in the spec's sample data (seed records, example approvers, department dropdowns) so the POC demo feels like THIS organization — do NOT invent generic placeholder names for things the context already names.
- Return JSON only.
""";
    }

    // Khối "ví dụ tính thử đã xác nhận" chèn vào prompt sinh spec: rỗng thì biến mất. Có nội dung thì bắt
    // spec đưa NGUYÊN các con số này vào mục "## 13. Worked Examples" — đây là oracle độc lập cho POC.
    private static string WorkedExamplesSection(string? workedExamples)
    {
        if (string.IsNullOrWhiteSpace(workedExamples))
            return string.Empty;

        return $"""

Ví dụ tính thử người dùng ĐÃ XÁC NHẬN trong lúc phỏng vấn (đưa NGUYÊN các con số này vào mục "## 13. Worked Examples" của spec — chúng là chuẩn để POC tự kiểm đối chiếu):
{workedExamples.Trim()}

""";
    }

    // Khối "giả định đã bị bác": rỗng thì biến mất. Có nội dung thì đây là RÀNG BUỘC CỨNG của lượt sinh
    // spec — người dùng đã đích thân nói các điều này sai, nên chúng không còn là chỗ để model tự quyết.
    private static string AssumptionCorrectionsSection(string? assumptionCorrections)
    {
        if (string.IsNullOrWhiteSpace(assumptionCorrections))
            return string.Empty;

        return $"""

Giả định người dùng đã BÁC ở các lượt trước (BẮT BUỘC tuân theo — TUYỆT ĐỐI không đưa lại giả định đã bị bác vào mục "## 12. Assumptions" hay vào bất kỳ mục nào của spec; điều đã có ý đúng kèm theo thì coi như yêu cầu ĐÃ CHỐT của người dùng, không phải giả định nữa):
{assumptionCorrections.Trim()}

""";
    }

    // Khối "dữ liệu thật của người dùng": trích từ chính file Excel/CSV/Word họ đính kèm khi phỏng vấn.
    // Spec là đầu vào DUY NHẤT của bước dựng POC, nên đây là đường duy nhất để bản demo hiện lên đúng
    // danh mục/tên/con số của đơn vị yêu cầu thay vì "Sản phẩm A / Nguyễn Văn B" — thứ khiến người xem
    // demo mất niềm tin ngay dòng đầu tiên. Rỗng thì biến mất (dự án không đính kèm file nào).
    private static string RealSampleDataSection(string? realSampleData)
    {
        if (string.IsNullOrWhiteSpace(realSampleData))
            return string.Empty;

        return $"""

Dữ liệu THẬT trích từ tài liệu người dùng đính kèm (bảng tính/tài liệu của chính họ). Dùng các giá trị này làm DỮ LIỆU MẪU của spec (mục Data Model Summary và các bản ghi seed ở mục Screens To Generate): lấy đúng tên cột/tên danh mục/giá trị có thật ở đây thay vì bịa tên chung chung. Chỉ lấy phần LIÊN QUAN tới phạm vi Product Brief; đây là dữ liệu để demo, KHÔNG phải yêu cầu mới — TUYỆT ĐỐI không vì thấy một cột lạ mà thêm màn hình/tính năng ngoài Product Brief:
{realSampleData.Trim()}

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
        string userStoriesTemplate,
        string organizationContext = "",
        string? revisionFeedback = null)
    {
        return $$"""
Project:
{{project.Name}}

Project Description:
{{project.Description}}
{{OrganizationSection(organizationContext)}}
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
{{RevisionSection(revisionFeedback)}}
Your task:
- Update BRD.docx structured data based on Company BRD Template.
- Update SRS.docx structured data based on Company SRS Template.
- Update FSD.docx structured data based on Company FSD Template.
- Update UserStories.docx content.
- Keep everything consistent with the approved Product Brief and AI Design Spec.

General rules:
- Keep the same section order as the templates.
- Fill unknown sections with "TBD" or "Cần làm rõ".
- When the organization context above names real departments/HoD/managers relevant to this project, use those REAL names in stakeholder/scope sections instead of "TBD".
- Do NOT write source code or implementation files.
- Do NOT call tools.
- Return JSON only.
""";
    }

    // Khối "yêu cầu chỉnh sửa" khi người duyệt từ cổng duyệt gửi nhận xét về bộ tài liệu kỹ thuật
    // (RequestStageRevisionUseCase): BA cập nhật bộ tài liệu hiện có theo đúng nhận xét thay vì
    // soạn lại như mới. Rỗng thì biến mất, cùng cơ chế với OrganizationSection.
    private static string RevisionSection(string? revisionFeedback)
    {
        if (string.IsNullOrWhiteSpace(revisionFeedback))
            return string.Empty;

        return $"""

Reviewer change request (bản "Current ... preview" ở trên là kết quả lần trước — người duyệt yêu cầu CHỈNH SỬA; xử lý TRỌN VẸN từng ý dưới đây, giữ nguyên những phần không bị nhắc tới):
{revisionFeedback.Trim()}

""";
    }

    // Khối "bối cảnh tổ chức" chèn vào giữa prompt: rỗng thì biến mất không để lại dòng thừa; có nội dung
    // thì tự mang đúng một dòng trống đệm trên/dưới để khớp nhịp các section xung quanh.
    // Trạng thái máy đã chắt từ chính hội thoại này — "Điều đã chốt", "Ví dụ đã xác nhận" và "Điểm cần
    // làm rõ còn tồn đọng" (xem DecisionLogService, InterviewOutlookService). Transcript thô KHÔNG thay
    // được cho khối này ở bước soạn/soát Brief:
    //
    // - Một quyết định được chốt ở lượt 38 rồi không ai nhắc lại tới lượt 71 vẫn là yêu cầu phải có trong
    //   tài liệu, nhưng đọc transcript dài thì nó chìm. Ca thật: người dùng chốt nhân viên được HỦY ĐĂNG
    //   KÝ, Brief bỏ hẳn tính năng đó trong khi vẫn giữ hai quy tắc phụ thuộc vào nó (Admin reject ticket
    //   waitlist "khi nhân viên đã hủy đăng ký"), và vòng tự soát không bắt được vì nó cũng chỉ có
    //   transcript để đối chiếu.
    // - "Điểm cần làm rõ còn tồn đọng" là thứ cổng readiness KHÔNG xét (cổng suy tất định từ bản đồ bao
    //   phủ). Đưa nó vào đây để van "không giả định" của bước soạn (needsClarification) có cơ sở dừng lại
    //   thay vì tự lấp chỗ trống.
    private static string DistilledStateSection(string distilledState)
    {
        if (string.IsNullOrWhiteSpace(distilledState))
            return string.Empty;

        return $"""

Trạng thái đã chắt từ hội thoại trên (đối chiếu MÁY MÓC với tài liệu: mỗi mục ở đây phải tìm được chỗ tương ứng trong tài liệu; đây KHÔNG phải nguồn thông tin mới, chỉ là chỉ mục của chính hội thoại):
{distilledState.Trim()}

""";
    }

    private static string OrganizationSection(string organizationContext)
    {
        if (string.IsNullOrWhiteSpace(organizationContext))
            return string.Empty;

        return $"""

Bối cảnh tổ chức Bosch (dữ liệu thật từ HR — khi nhắc tới phòng ban/chức danh/người phụ trách, dùng ĐÚNG tên trong này; nếu có mục "Đơn vị yêu cầu", ghi nhận nó là đơn vị chủ quản của dự án):
{organizationContext.Trim()}

""";
    }
}
