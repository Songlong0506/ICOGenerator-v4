using ICOGenerator.Contracts.Requirements;
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

    // Vòng SỬA CÓ PHẠM VI: người dùng ghim ghi chú thẳng lên bản xem trước Product Brief. Khác hẳn lượt
    // soạn ở trên — bản Brief hiện tại là BẢN GỐC phải chép nguyên văn, chỉ các đoạn được ghi chú mới
    // được đụng tới. Xem Prompts/BusinessAnalyst/product-brief-note-revision.v1.md.
    //
    // CỐ Ý KHÔNG có khối "Trạng thái đã chắt từ hội thoại": khối đó là danh sách kiểm bắt model rà lại
    // MỌI điều đã chốt và bổ sung thứ còn thiếu vào tài liệu — đúng việc của lượt "Write Requirement",
    // và đúng thứ biến một ghi chú một dòng thành một bản Brief đổi hàng chục dòng. Hội thoại vẫn đi kèm,
    // nhưng chỉ với vai trò TRA CỨU cho những ghi chú cần thông tin đã trao đổi.
    public string BuildProductBriefNoteRevision(
        Project project,
        string conversationTranscript,
        string currentProductBrief,
        IReadOnlyList<BriefNote> notes,
        string organizationContext = "")
    {
        return $$"""
Project:
{{project.Name}}

Project Description:
{{project.Description}}
{{OrganizationSection(organizationContext)}}
Hội thoại khai thác yêu cầu (CHỈ để tra cứu khi một ghi chú cần thông tin đã trao đổi — KHÔNG phải danh sách kiểm, KHÔNG bổ sung yêu cầu nào từ đây):
{{conversationTranscript}}

Bản Product Brief HIỆN TẠI (BẢN GỐC — chép nguyên văn mọi đoạn không bị ghi chú):
{{currentProductBrief}}

Ghi chú người dùng ghim trên bản xem trước — sửa cho hết, và CHỈ sửa những chỗ này:
{{RenderNotes(notes)}}

Your task:
- Apply every note to the current Product Brief; copy every other part verbatim.
- Return JSON only.
""";
    }

    // Mỗi ghi chú một dòng: đoạn được bôi đen (nếu có) + điều cần sửa. Đoạn trích cắt ở 200 ký tự —
    // đủ để định vị chỗ cần sửa, không kéo cả tài liệu vào prompt lần thứ hai.
    private static string RenderNotes(IReadOnlyList<BriefNote> notes)
    {
        var lines = notes.Select((n, i) =>
        {
            var quote = (n.Quote ?? "").Trim();
            if (quote.Length > 200)
                quote = quote[..200] + "…";
            var note = (n.Note ?? "").Trim();
            return quote.Length > 0
                ? $"{i + 1}. Ở đoạn “{quote}”: {note}"
                : $"{i + 1}. (không gắn đoạn nào) {note}";
        });

        return string.Join("\n", lines);
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
        string? acceptanceCriteria = null,
        string? permissionMatrix = null,
        string? flowMap = null,
        string? screenScopeMap = null,
        string? entityMap = null,
        string? reportMap = null,
        string? notificationMap = null)
    {
        return $$"""
Project:
{{project.Name}}

Project Description:
{{project.Description}}
{{OrganizationSection(organizationContext)}}
Approved Product Brief (source of truth, non-technical):
{{approvedProductBrief}}
{{acceptanceCriteria}}{{WorkedExamplesSection(workedExamples)}}{{AssumptionCorrectionsSection(assumptionCorrections)}}{{FlowMapSection(flowMap)}}{{ScreenScopeSection(screenScopeMap)}}{{EntityMapSection(entityMap)}}{{ReportMapSection(reportMap)}}{{NotificationMapSection(notificationMap)}}{{PermissionMatrixSection(permissionMatrix)}}{{RealSampleDataSection(realSampleData)}}
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

    // Khối "bảng phân quyền đã chốt": người dùng đã tự chọn từng ô (vai trò × chức năng × màn hình, kèm
    // PHẠM VI DỮ LIỆU) ở cuối buổi phỏng vấn. Rỗng thì biến mất (dự án cũ, hoặc chưa chốt bảng).
    //
    // Đây là đường DUY NHẤT để phân quyền tới được POC ở dạng máy đọc được. Không có nó, phân quyền tan
    // vào văn xuôi của Product Brief và bản demo hiện đúng một bộ màn hình cho mọi vai — người xem demo
    // không có cách nào nghiệm thu "manager chỉ thấy nhân viên thuộc quyền", mà đó thường lại là ràng
    // buộc họ quan tâm nhất.
    private static string PermissionMatrixSection(string? permissionMatrix)
    {
        if (string.IsNullOrWhiteSpace(permissionMatrix))
            return string.Empty;

        return $"""

Bảng phân quyền người dùng ĐÃ CHỐT (họ tự chọn từng ô ở cuối buổi phỏng vấn — đây là YÊU CẦU, không phải giả định):
{permissionMatrix.Trim()}

Bắt buộc với bảng này: đưa nguyên nó vào mục "## 6b. Permission Matrix" của spec; ở mục "## 6. Screens To Generate" ghi rõ mỗi màn hình vai nào vào được và nút/hành động nào ẩn với vai nào; và PHẠM VI DỮ LIỆU ("của mình"/"của đơn vị"/"tất cả") phải thành điều kiện lọc thật trong "## 9. API Expectations" chứ không chỉ là một câu mô tả. TUYỆT ĐỐI không thêm vai trò hay quyền nào ngoài bảng.

""";
    }

    // Khối "bảng luồng nghiệp vụ đã chốt": người dùng đã rà từng bước của từng luồng (chính + ngoại lệ).
    // Rỗng thì biến mất (dự án cũ, hoặc chưa chốt bảng).
    //
    // Đây là đường DUY NHẤT để chuỗi bước người dùng TỰ TAY duyệt tới được oracle chấm POC. Trước bước
    // này, mục "## 13. Worked Examples" định tính được dựng từ Project.WorkedExamples — một danh sách do
    // LLM chắt từ transcript, không ai duyệt và cũng không còn đường sửa tay (UpdateWorkedExamplesUseCase
    // đã gỡ). Nghĩa là POC đang bị chấm theo bản BA hiểu, không theo bản người dùng xác nhận.
    private static string FlowMapSection(string? flowMap)
    {
        if (string.IsNullOrWhiteSpace(flowMap))
            return string.Empty;

        return $"""

Bảng luồng nghiệp vụ người dùng ĐÃ CHỐT (họ rà từng bước — đây là YÊU CẦU, không phải giả định):
{flowMap.Trim()}

Bắt buộc với bảng này: mỗi luồng ở trên phải thành MỘT ví dụ ĐỊNH TÍNH của mục "## 13. Worked Examples" theo đúng dạng "- WE-n (BR-m): <chuỗi hành động> => <trạng thái/nhãn cuối>", lấy chuỗi hành động từ các bước và kết quả từ `outcome` của bước cuối. Các luồng NGOẠI LỆ cũng phải có ví dụ riêng — đó là phần POC hay bỏ sót nhất. Điều kiện chuyển trạng thái trong các bước phải xuất hiện ở "## 10. Business Rules". TUYỆT ĐỐI không thêm bước nào ngoài bảng.

""";
    }

    // Khối "bảng màn hình đã chốt": phạm vi màn hình người dùng đã tự rà. Rỗng thì biến mất.
    //
    // Vì sao nó cần ở đây dù mục "## 6. Screens To Generate" vẫn sinh được từ Product Brief: Brief là văn
    // xuôi, nên số màn hình của spec là thứ model tự đếm lại mỗi lần sinh (SpecBriefParityChecker tồn tại
    // đúng vì việc đếm đó hay lệch). Bảng này là danh sách có ranh giới, do người dùng chốt.
    private static string ScreenScopeSection(string? screenScopeMap)
    {
        if (string.IsNullOrWhiteSpace(screenScopeMap))
            return string.Empty;

        return $"""

Bảng màn hình người dùng ĐÃ CHỐT (phạm vi màn hình của ứng dụng — đây là YÊU CẦU):
{screenScopeMap.Trim()}

Bắt buộc với bảng này: mục "## 6. Screens To Generate" phải có ĐÚNG các màn hình trên, mỗi màn một heading "### 6.n." — không thêm màn hình nào khác, không bỏ màn hình nào. Màn hình người dùng đã LOẠI thì TUYỆT ĐỐI không dựng lại.

""";
    }

    // Khối "bảng đối tượng nghiệp vụ đã chốt": thông tin cần lưu + vòng đời trạng thái. Rỗng thì biến mất.
    //
    // Mục "## 8. Data Model Summary" vốn là mục spec TỰ NGHĨ RA từ văn xuôi Brief, không có gì đối chiếu —
    // và mô hình dữ liệu sai thì mọi màn hình của POC sai theo.
    private static string EntityMapSection(string? entityMap)
    {
        if (string.IsNullOrWhiteSpace(entityMap))
            return string.Empty;

        return $"""

Bảng đối tượng nghiệp vụ người dùng ĐÃ CHỐT (đây là YÊU CẦU, không phải giả định):
{entityMap.Trim()}

Bắt buộc với bảng này: mục "## 8. Data Model Summary" phải dựng từ đúng các đối tượng và thông tin trên — không thêm entity nào chưa có ở đây, không bỏ thông tin nào đã tích. Vòng đời trạng thái phải thành các quy tắc chuyển trạng thái ở "## 10. Business Rules".

Phần trong ngoặc vuông sau mỗi thông tin là RÀNG BUỘC người dùng đã tự tay chọn, mỗi loại có chỗ đi riêng:
- "bắt buộc" ⇒ một quy tắc validation ở "## 10. Business Rules"; các thông tin còn lại để trống được.
- "chọn 1 giá trị" / "chọn nhiều giá trị" ⇒ ô nhập là dropdown (đơn/đa trị), không phải ô gõ tay.
- "danh sách cố định: …" ⇒ dùng ĐÚNG các giá trị đó, không tự thêm.
- "danh sách do ứng dụng tự quản lý" ⇒ danh mục ấy cần một MÀN HÌNH CRUD riêng ở "## 6. Screens To Generate" và các endpoint tương ứng ở "## 9. API Expectations".
- "lấy từ <hệ thống>" ⇒ ghi thành một nguồn dữ liệu ngoài ở "## 9. API Expectations"; POC dùng dữ liệu mẫu tĩnh và nói rõ điều đó.
- "ứng dụng tự sinh theo quy tắc …" ⇒ KHÔNG dựng ô nhập cho nó; quy tắc sinh thành một dòng ở "## 10. Business Rules".
- "CHƯA rõ …" ⇒ đây là chỗ người dùng chưa chốt: xử như một GIẢ ĐỊNH ở "## 12. Assumptions" thay vì im lặng chọn thay họ.

Đối tượng ghi "là các DÒNG của X" là một QUAN HỆ MỘT-NHIỀU, không phải một hồ sơ độc lập:
- "## 8. Data Model Summary" phải nêu quan hệ đó tường minh (khóa ngoại trỏ về X), và số dòng mỗi cha — nếu có nêu — thành một quy tắc validate ở "## 10. Business Rules".
- "## 6. Screens To Generate": KHÔNG dựng màn hình CRUD riêng cho nó. Nó là một BẢNG NHÚNG ngay trong màn hình của X, thêm/xóa dòng tại chỗ, lưu cùng lúc với bản ghi cha. Dựng cho nó một màn hình riêng là bắt người dùng rời hồ sơ đang nhập để đi tạo từng dòng một.
- "## 9. API Expectations": các dòng con đi cùng payload của bản ghi cha, không cần bộ endpoint riêng.

""";
    }

    // Khối "bảng báo cáo đã chốt": mỗi báo cáo một dòng, kèm câu hỏi nó trả lời, đối tượng nó lấy số và các
    // chiều gộp/lọc. Rỗng thì biến mất (dự án cũ, dự án không cần báo cáo, hoặc chưa chốt bảng).
    //
    // Vì sao nó cần ở đây dù các màn hình báo cáo đã có mặt ở phạm vi: PlannedScope chỉ chở được cái TÊN.
    // Phần quyết định một màn hình báo cáo có dùng được hay không — nó trả lời câu hỏi gì, lấy số từ đối
    // tượng nào, gộp/lọc theo chiều nào — nằm ở đây, và nếu không nạp vào thì bước sinh spec dựng ra một
    // bảng đổ toàn bộ dữ liệu ra màn hình rồi gọi đó là báo cáo.
    private static string ReportMapSection(string? reportMap)
    {
        if (string.IsNullOrWhiteSpace(reportMap))
            return string.Empty;

        return $"""

Bảng báo cáo / thống kê người dùng ĐÃ CHỐT (họ tự rà từng dòng — đây là YÊU CẦU, không phải giả định):
{reportMap.Trim()}

Bắt buộc với bảng này: mỗi báo cáo ở trên phải có MỘT màn hình riêng ở mục "## 6. Screens To Generate" (chúng đã nằm trong phạm vi màn hình, không được gộp hai báo cáo làm một và không được bỏ báo cáo nào). Phần "lấy số từ" phải trỏ về đúng entity đó ở "## 8. Data Model Summary" — TUYỆT ĐỐI không dựng thêm bảng dữ liệu riêng cho báo cáo. Phần "gộp/lọc theo" phải thành BỘ LỌC THẬT của màn hình và thành tham số truy vấn ở "## 9. API Expectations", không chỉ là một câu mô tả. Quyền xem của từng báo cáo lấy theo bảng phân quyền, không tự suy ra từ tên báo cáo. Báo cáo nào người dùng đã BỎ thì TUYỆT ĐỐI không dựng lại.

""";
    }

    // Khối "bảng thông báo đã chốt": mỗi sự kiện một dòng, người nhận chính (To) và đồng gửi (CC) do người
    // dùng tự chọn từ danh sách người nhận của dự án. Rỗng thì biến mất (dự án cũ, hoặc chưa chốt bảng).
    //
    // Đây là đường DUY NHẤT để "ai được báo khi nào" tới được spec ở dạng máy đọc được. Không có nó, thông
    // báo tan vào văn xuôi của Product Brief và bước sinh spec tự viết ra một quy tắc gửi mail — mà mặc
    // định im lặng của mọi tầng phía sau là gửi cho tất cả, đúng thứ bảng này sinh ra để chặn.
    private static string NotificationMapSection(string? notificationMap)
    {
        if (string.IsNullOrWhiteSpace(notificationMap))
            return string.Empty;

        return $"""

Bảng thông báo / nhắc nhở người dùng ĐÃ CHỐT (họ tự chọn người nhận cho từng sự kiện — đây là YÊU CẦU, không phải giả định):
{notificationMap.Trim()}

Bắt buộc với bảng này: mỗi dòng "gửi" ở trên phải thành MỘT quy tắc ở "## 10. Business Rules" theo dạng "khi <sự kiện> ⇒ gửi email cho <To>, CC <CC>". Người nhận là một QUAN HỆ với bản ghi ("Người tạo", "Quản lý trực tiếp của người tạo") thì quy tắc phải nói rõ quan hệ đó được tra ra từ trường nào của đối tượng. Sự kiện nằm trong danh sách "KHÔNG gửi" thì TUYỆT ĐỐI không sinh thông báo nào, và không được tự thêm người nhận hay sự kiện nào ngoài bảng. Kênh gửi duy nhất là email.

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
