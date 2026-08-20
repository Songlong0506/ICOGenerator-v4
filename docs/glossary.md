# Từ điển thuật ngữ

| Thuật ngữ | Nghĩa trong dự án |
|---|---|
| **Agent** | Một "nhân sự AI" (bản ghi bảng `Agents`): vai + model + tools. Khác **AppUser** (người thật) |
| **AgentRoleKey** | Vai của AI: BusinessAnalyst, TechLead, Developer, Tester, UiUx |
| **UserRole** | Vai của người: SuperAdmin, Admin, TeamDev, User. **Không lưu ở DB** — chỉ tồn tại trong claim của phiên đăng nhập |
| **Product Brief** | Tài liệu yêu cầu ngôn ngữ đời thường cho user duyệt (draft → V{n}) |
| **AI Design Spec** | Bản đặc tả kỹ thuật sinh từ Product Brief đã duyệt — input của POC/Architecture |
| **AC-n (câu nghiệm thu)** | Dòng "Hoàn thành khi: …" người dùng đã duyệt trong Product Brief, chép nguyên văn vào spec § 14 và là đích của bộ kịch bản UAT |
| **POC** | Demo HTML một-file (`poc-demo.html`) có hành vi thật, để user "thấy" trước khi đầu tư code |
| **Technical Docs** | Bộ BRD/SRS/FSD/UserStories — sinh ở bước 2 pipeline, không phải lúc Write Requirement |
| **WorkflowRun / AgentTask** | "Vé" theo dõi một lần chạy quy trình / một đầu việc trong đó |
| **Gate (cổng duyệt)** | Run dừng `WaitingForHuman` chờ người bấm Duyệt/Chỉnh sửa/Từ chối trên Agent Dashboard |
| **Hand-off** | Output bước trước thành Input bước sau khi qua cổng |
| **Revision (cổng)** | "Yêu cầu chỉnh sửa" — agent sửa đúng bước đó theo nhận xét, tối đa 3 vòng/bước |
| **BugFix cycle** | Chu trình tự động Testing↔BugFix khi Tester trả `VERDICT: FAIL`, tối đa 3 vòng |
| **Workspace** | Thư mục file thật của project dưới `AgentWorkspace:RootPath` (5 phase 01→05) |
| **Tool** | Method C# public có `[Description]` mà agent gọi được qua native tool-calling |
| **Prompt key** | Đường dẫn tương đối file prompt dưới `/Prompts` — khóa dùng bởi PromptTemplateService/Studio/Evals |
| **Golden set** | Bộ `EvalScenario` chuẩn để chấm chất lượng prompt/model bằng LLM-judge |
| **Fail-open** | Nguyên tắc thiết kế lặp lại khắp app: tính năng phụ (memory, org context, notification, prompt override) lỗi thì âm thầm rơi về hành vi cơ bản, không bao giờ làm gãy luồng chính |
| **Opt-in** | Nguyên tắc cấu hình: tính năng có phụ thuộc ngoài (Proxy, Otel, Budget limits, Teams/Email) mặc định TẮT; structured output opt-in **theo từng model**, 3 mức (`AiModel.StructuredOutputMode`) |
