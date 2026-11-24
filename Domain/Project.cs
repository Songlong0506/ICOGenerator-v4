namespace ICOGenerator.Domain;
public class Project
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string? BackendGitUrl { get; set; }
    public string? FrontendGitUrl { get; set; }
    // Cấu hình Git của bước bàn giao, điền cùng popup "Config delivery" với hai URL trên (xem
    // ProjectGitSettings). GitBaseBranch: nhánh đích của PR — null = "main". GitUsername/GitAccessToken:
    // tài khoản dùng để clone/push repo đích — null = dùng credential sẵn có của máy chủ. Token MÃ HÓA
    // trong DB (cùng IApiKeyProtector với AiModel.ApiKey) và không bao giờ được render ra trang.
    public string? GitBaseBranch { get; set; }
    public string? GitUsername { get; set; }
    public string? GitAccessToken { get; set; }
    // Cấu hình delivery do TeamDev điền ở Agent Dashboard (sau bước POC), không phải end-user lúc tạo project.
    // Mặc định = true (dùng Bosch template); TeamDev có thể đổi sang false (để TechLead tự định kiến trúc) ở
    // Agent Dashboard. Luôn có giá trị rõ ràng — không còn trạng thái "chưa chọn".
    public bool IsUseBoschTemplate { get; set; } = true;
    // Username (claim Name) của người tạo project. Dùng để lọc danh sách: User thường chỉ thấy project
    // do mình tạo; Admin/TeamDev (quyền ProjectsViewAll) thấy tất cả. Nullable để tương thích các project
    // cũ tạo trước khi có cột này — chúng coi như "không có chủ" và chỉ hiện cho người xem-tất-cả.
    public string? CreatedByUsername { get; set; }
    // Mã đơn vị yêu cầu (OrgUnits.OrgUnitCode) — người dùng chọn lúc tạo project (tùy chọn). Chỉ lưu MÃ,
    // tên phòng/manager tra lại từ OrgUnits/Associates lúc cần (tên có thể đổi khi đồng bộ HR). Dùng cho:
    // ghi chú "đơn vị yêu cầu" trong ngữ cảnh BA + tài liệu (OrganizationContextService) và thống kê
    // Usage theo phòng ban. null = chưa gắn — mọi luồng chạy như trước.
    public string? OrgUnitCode { get; set; }
    // Bộ nhớ dài hạn của hội thoại BA: tóm tắt (text) các lượt CŨ đã rơi ra ngoài cửa sổ gần nhất, được
    // gộp DẦN để hội thoại dài vẫn giữ ngữ cảnh mà prompt không phình token. null = chưa có gì để tóm tắt.
    // SummarizedTurnCount = số lượt cũ nhất (xếp theo CreatedAt) đã được gộp vào ConversationSummary, làm
    // con trỏ để biết lượt nào còn phải gửi nguyên văn. Xem ConversationMemoryService.
    public string? ConversationSummary { get; set; }
    public int SummarizedTurnCount { get; set; }
    // Con trỏ "mốc duyệt Brief": số lượt hội thoại đã có tại thời điểm người dùng bấm Approve lần gần
    // nhất (ApproveRequirementUseCase). Mọi thứ TRƯỚC mốc này đã được chở bởi chính Product Brief đã
    // duyệt — bản DUY NHẤT trong dự án có chữ ký người dùng — nên vòng soạn Brief sau đó được phép nén
    // phần transcript trước mốc thay vì gửi lại nguyên văn. 0 = chưa duyệt lần nào (gửi như cũ).
    // Chỉ là TRẦN mong muốn: BriefContextWindow không bao giờ cắt quá SummarizedTurnCount, tức không
    // bao giờ bỏ lượt nào chưa nằm trong ConversationSummary. Xem BriefContextWindow.
    public int BriefApprovedTurnCount { get; set; }
    // Con trỏ riêng cho bộ nhớ CẤP USER (AppUser.UserMemory): số lượt cũ nhất (xếp theo CreatedAt) của
    // project này đã được chắt lọc vào hồ sơ user của người tạo. Tách khỏi SummarizedTurnCount vì hai bộ
    // nhớ tiến theo nhịp/độ trễ khác nhau. Xem UserMemoryService.
    public int UserMemoryHarvestedTurnCount { get; set; }
    // Đánh dấu dự án này ĐÃ được rà soát một lần để rút "khoảng trống checklist" (thông tin người dùng
    // phải tự nêu ra mà BA chưa từng hỏi) vào AgentChecklistItem — dùng chung cho MỌI dự án sau
    // này. Chỉ rà soát MỘT LẦN, ngay sau khi tài liệu được sinh thành công (lúc đó mới có bức tranh Q&A
    // đầy đủ). Xem ChecklistGapMemoryService.
    public bool ChecklistGapHarvested { get; set; }
    // HÀNG ĐỢI học ở mốc DUYỆT Product Brief: tên phiên bản vừa được duyệt ("V1", "V2"…), do
    // ApproveRequirementUseCase ghi; RequirementMemoryHarvester chắt lọc rồi xoá. Vì sao học ở mốc DUYỆT
    // chứ không ngay sau khi sinh Brief: lúc sinh xong, người dùng còn chưa đọc bản nháp, nên bằng chứng
    // duy nhất là suy đoán gián tiếp "chỗ nào user tự nêu mà BA chưa hỏi". Đến lúc duyệt thì các GHI CHÚ
    // họ ghim lên bản nháp đã có mặt — bằng chứng trực tiếp BA viết thiếu/hiểu sai, sắc hơn hẳn.
    // Vì sao là con trỏ chuỗi chứ không phải cờ bool: Brief duyệt được NHIỀU lần (V1→V2→V3), mỗi bản một
    // tập ghi chú riêng; giá trị này chính là bản mà vòng harvest phải đọc ghi chú của nó.
    // Fail-open: harvest lỗi ⇒ giữ nguyên hàng đợi, task sau gộp bù. Xem ChecklistGapMemoryService.
    public string? PendingChecklistHarvestVersion { get; set; }
    // "Bản đồ bao phủ yêu cầu" của dự án: bảng trạng thái (text, 11 nhóm cố định) cho biết nhóm thông tin
    // nào đã khai thác [RÕ]/[MỘT PHẦN]/[CHƯA HỎI]/[KHÔNG ÁP DỤNG], cập nhật sau mỗi lượt chat. NGUỒN CHÂN
    // LÝ DUY NHẤT của độ sẵn sàng: BA chọn câu hỏi kế tiếp từ đây, panel tiến độ render nó, và cổng
    // "Write Requirement" suy ready tất định từ nó (RequirementReadinessGate.Evaluate). null = chưa có
    // lượt chat nào được ghi nhận (cổng coi là CHƯA sẵn sàng — fail-closed).
    // CoverageHarvestedTurnCount = số lượt cũ nhất (xếp theo CreatedAt) đã gộp vào bản đồ — con trỏ để
    // biết lượt nào còn phải gộp tiếp (fail-open: lời gọi lỗi thì con trỏ đứng yên, lần sau gộp bù).
    // Xem RequirementCoverageService.
    public string? RequirementCoverageMap { get; set; }
    public int CoverageHarvestedTurnCount { get; set; }
    // Danh sách còn lại của lượt distill trên — cùng lời gọi, cùng con trỏ CoverageHarvestedTurnCount,
    // cùng một SaveChangesAsync (xem RequirementCoverageService):
    //  • OpenQuestions: điểm còn MƠ HỒ / MÂU THUẪN chưa chốt — TỒN ĐỌNG câu hỏi của BA. KHÔNG hiển thị
    //    thành panel (user chỉ cần trò chuyện; hỏi cho hết là việc của BA): danh sách này được nạp vào
    //    ngữ cảnh mỗi lượt chat làm la bàn ƯU TIÊN cạnh bản đồ bao phủ — bản đồ chỉ phân giải theo NHÓM,
    //    còn đây giữ đúng điểm chưa chốt. Mục được chốt thì rời khỏi danh sách. Xem BAChatService.
    //
    // PHẠM VI MÀN HÌNH không có cột riêng ở đây (cột PlannedScope đã gỡ): phần phạm vi mới lộ ra được lượt
    // chắt lọc ghép THẲNG vào UxBlueprint bên dưới dưới dạng tác vụ chưa xếp chỗ. Một danh sách suy đoán chạy
    // song song với bản thiết kế là hai bản của cùng một thứ, và chúng không bao giờ bằng nhau.
    public string? OpenQuestions { get; set; }
    // ẢNH CHỤP hai cột trên + con trỏ CoverageHarvestedTurnCount ở thời điểm ngay TRƯỚC lượt distill gần
    // nhất (JSON). Chỉ có một người đọc: đường "✎ sửa"/"↻ thử lại" lượt user cuối, để trả bản đồ về
    // trạng thái trước khi câu gốc được gộp thay vì chắt câu đã sửa chồng lên bản còn chứa ý cũ. null =
    // chưa có lượt distill nào, hoặc ảnh vừa được dùng. Xem CoverageUndoSnapshot.
    public string? CoverageUndoSnapshot { get; set; }
    // Mốc "＋ New Chat" gần nhất (UTC). Timeline trang Requirements ẩn các workflow run đã KẾT THÚC tạo
    // trước mốc này — chúng thuộc hội thoại đã lưu trữ; POC/tài liệu của chúng vẫn còn, vào qua POC Review.
    // Phải là cột riêng chứ không suy từ MAX(ArchivedAt): New Chat khi hội thoại hiện hành đang trống thì
    // không có lượt nào được đóng dấu, mà run sinh sau lần New Chat trước vẫn phải ẩn. null = chưa bấm lần nào.
    public DateTime? ConversationStartedAtUtc { get; set; }
    // Số lượt hội thoại mà lượt thiết kế UX gần nhất đã đọc (DesignPreparationService ở bước "Write
    // Requirement"). Bấm lại nút mà hội thoại không thêm lượt nào và bảng luồng/báo cáo không đổi ⇒ bản thiết
    // kế đang lưu vẫn đúng, không gọi model lần nào. 0 = chưa thiết kế lần nào.
    public int UxDesignedTurnCount { get; set; }
    // BẢNG PHÂN QUYỀN (JSON PermissionMatrixRow[]) — TÁC VỤ của bản thiết kế UX (khóa TaskId) × vai trò, mỗi ô
    // kèm PHẠM VI DỮ LIỆU ("của mình" / "của đơn vị" / "tất cả"). BA tự soạn ở cuối buổi phỏng vấn
    // (PermissionMatrixDraftService); người dùng xem/sửa ở popup mở từ panel tiến độ
    // (ConfirmPermissionMatrixUseCase). null = chưa soạn.
    //
    // Vì sao phân quyền có đường riêng thay vì nằm chung trong hội thoại như mọi điều khác: câu hỏi "mỗi
    // vai trò được xem và làm những gì" bắt người dùng nghiệp vụ tự dựng cả ma trận trong đầu, nên nó gần
    // như luôn nhận về một câu đóng cửa ("cứ vậy đã, có gì tôi bổ sung sau") — rồi BA tự soạn phương án,
    // người dùng bấm một chip "Đồng ý", và phương án BA tự nghĩ ra được coi là đã chốt. Nay phần quyết định
    // thật (ai làm bước nào, ai quản lý danh mục nào) được hỏi ở các nhóm luồng/dữ liệu/quy tắc, còn ma trận
    // CRUD là hệ quả BA suy ra từ đó ở bước "Write Requirement" — ô nào người dùng đã tự nói thì mang trích
    // dẫn. Phân quyền KHÔNG là một nhóm của bản đồ bao phủ.
    //
    // Non-null ⇒ khối đã chốt được nạp vào ngữ cảnh chat (BA thôi hỏi lại) và vào prompt sinh AI Design Spec
    // (POC dựng UI theo vai thay vì để phân quyền tan vào văn xuôi). Xem PermissionMatrixBuilder +
    // PermissionMatrixGate.
    public string? PermissionMatrix { get; set; }
    // HAI BẢNG tiếp theo của buổi phỏng vấn (JSON): khối "đã chốt" đi vào ngữ cảnh chat, lượt distill bản đồ
    // bao phủ và prompt sinh AI Design Spec.
    //
    // Hai bảng này chỉ XÁC NHẬN LẠI thứ đã [RÕ] từ hội thoại — nhóm tương ứng KHÔNG phụ thuộc vào bảng. Nếu
    // bắt nhóm phụ thuộc vào bảng thì cổng mở bảng (đòi nhóm đó [RÕ]) và bản đồ (chỉ [RÕ] khi có bảng) khóa
    // chặt lẫn nhau.
    //
    //  • FlowMap (FlowMapRow[]) — các LUỒNG nghiệp vụ theo vai trò: luồng chính + 1–2 ngoại lệ, mỗi luồng
    //    là chuỗi bước (ai làm → làm gì → trạng thái sau đó) sửa được và bỏ được. Đây là đường DUY NHẤT để
    //    chuỗi bước người dùng tự tay duyệt tới được POC (chuyển trạng thái ở "## 8. Business Rules") —
    //    trước đó nó là bản LLM chắt từ transcript, không ai duyệt và cũng không sửa tay được nữa.
    //  • UxBlueprint (UxBlueprint) — BẢN THIẾT KẾ UX: đối tượng + vòng đời, danh mục TÁC VỤ có ID ổn định,
    //    màn hình theo pattern library, điều hướng theo vai, hành trình và chỉ số. Đây là THIẾT KẾ (BA dựng
    //    danh mục, UiUx tổ chức màn hình — UxDesignService), người dùng không rà (họ góp ý ở POC Review).
    //    NGOẠI LỆ của khuôn "null = chưa chốt" ở khối này: cột chở CẢ tác vụ chưa xếp chỗ (Placed = false) vì
    //    nó là nguồn phạm vi DUY NHẤT của dự án — lượt chắt lọc chỉ được THÊM tác vụ chưa xếp chỗ vào đây. Vì
    //    vậy "đã thiết kế" phải hỏi UxBlueprintBuilder.IsDesigned.
    //
    // KHÔNG có bảng đối tượng nghiệp vụ: mô hình dữ liệu không được chốt với người dùng nghiệp vụ, và AI
    // Design Spec cũng không có mục đó — bước đề xuất kiến trúc thiết kế nó sau khi POC được duyệt — xem
    // InterviewTableGate.
    //
    // Thứ tự bày là TẤT ĐỊNH và mỗi lượt chỉ có ĐÚNG MỘT bảng — xem InterviewTableGate.
    public string? FlowMap { get; set; }
    public string? UxBlueprint { get; set; }
    // BẢNG BÁO CÁO / THỐNG KÊ (JSON ReportMapRow[]) — mỗi báo cáo một dòng: tên, nó trả lời câu hỏi gì, lấy
    // số từ bản ghi nào, gộp/lọc theo gì, và nó đứng trên màn hình nào. null = chưa ráp.
    //
    // KHÁC các bảng trên: người dùng KHÔNG rà bảng này. Nhóm «Báo cáo / thống kê» lên [RÕ] bằng chính lựa
    // chọn của họ trong hội thoại, rồi ReportMapDraftService ráp ngầm các báo cáo đã chọn vào cột này và
    // gieo chúng vào bảng màn hình ở trạng thái chưa chốt — MỘT màn tổng quan gom các báo cáo (mỗi báo cáo
    // một chức năng), cộng màn riêng của báo cáo nào cần — → bảng phân quyền → "## 6. Screens To
    // Generate". Xem ReportMapBuilder + ReportMapGate.
    public string? ReportMap { get; set; }
    // ĐỪNG TÌM cột bảng thông báo / danh sách người nhận (NotificationMap, NotificationRecipients): nhóm
    // «Thông báo / nhắc nhở» không chốt ở buổi phỏng vấn (sản phẩm của buổi đó là một POC không gửi email
    // nào) — người nhận email do đội Dev xác nhận với người dùng sau POC. Xem CoverageUnaskedGroupGuard.
    // ĐỪNG TÌM các cột của "cổng xác nhận giả định" (PendingAssumptionsVersion, SpecAssumptionCorrections,
    // ConfirmedAssumptions, PendingAssumptionGaps): cổng chờ người dùng rà mục "## 10. Assumptions" giữa
    // bước sinh AI Design Spec và bước dựng POC đã gỡ hẳn — duyệt Product Brief là chạy một mạch tới bản
    // demo, không còn chỗ nào dừng lại hỏi.
    // Con trỏ học từ ghi chú POC: số PocComment (xếp theo CreatedAt) của dự án đã được chắt lọc vào
    // AgentChecklistItem sau mỗi vòng chỉnh sửa POC — ghi chú kiểu "thiếu màn hình X" chính là
    // câu hỏi BA lẽ ra phải hỏi từ lúc phỏng vấn. Xem PocFeedbackMemoryService.
    public int PocFeedbackHarvestedCount { get; set; }
    // CỜ mở hàng đợi cho con trỏ trên: chỉ mốc DUYỆT POC (ApproveStageUseCase) mới bật. Trước đây harvest
    // chạy ngay sau MỖI vòng chỉnh sửa POC — tức trả một lời gọi LLM cho mỗi vòng, và học từ một bản vá
    // chưa ai xác nhận là đạt. Đợi tới lúc duyệt thì mọi vòng gom vào ĐÚNG MỘT lời gọi, và lúc đó mới
    // biết ghi chú nào thật sự được xử lý xong. Xem PocFeedbackMemoryService.
    public bool PendingPocFeedbackHarvest { get; set; }
    // NGHIỆM THU BẢN DEMO — trạng thái KẾT của hành trình phía người dùng nghiệp vụ. Trước đây người
    // yêu cầu xem POC, ghim ghi chú, nhờ sửa… nhưng không có cách nào nói "bản này được rồi": cổng duyệt
    // nằm ở Agent Dashboard (quyền DeliveryAdvance), nên đội delivery phải đi hỏi miệng xem người yêu
    // cầu đã ưng chưa, và chặng cuối của stepper không bao giờ đóng lại. null = chưa nghiệm thu.
    // PocAcceptedBy giữ username người bấm (ai chịu trách nhiệm cho lời "được rồi" này).
    public DateTime? PocAcceptedAtUtc { get; set; }
    public string? PocAcceptedBy { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public ICollection<ProjectDocument> Documents { get; set; } = new List<ProjectDocument>();
    public ICollection<ProjectSourceFile> SourceFiles { get; set; } = new List<ProjectSourceFile>();
    public ICollection<AgentConversation> Conversations { get; set; } = new List<AgentConversation>();
    public ICollection<AgentModelCallLog> ModelCallLogs { get; set; } = new List<AgentModelCallLog>();
    public ICollection<WorkflowRun> WorkflowRuns { get; set; } = new List<WorkflowRun>();
    public ICollection<AgentTask> AgentTasks { get; set; } = new List<AgentTask>();
}
