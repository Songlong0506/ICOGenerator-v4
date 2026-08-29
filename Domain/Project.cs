namespace ICOGenerator.Domain;
public class Project
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string? BackendGitUrl { get; set; }
    public string? FrontendGitUrl { get; set; }
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
    // "Bản đồ bao phủ yêu cầu" của dự án: bảng trạng thái (text, 12 nhóm cố định) cho biết nhóm thông tin
    // nào đã khai thác [RÕ]/[MỘT PHẦN]/[CHƯA HỎI]/[KHÔNG ÁP DỤNG], cập nhật sau mỗi lượt chat. NGUỒN CHÂN
    // LÝ DUY NHẤT của độ sẵn sàng: BA chọn câu hỏi kế tiếp từ đây, panel tiến độ render nó, và cổng
    // "Write Requirement" suy ready tất định từ nó (RequirementReadinessGate.Evaluate). null = chưa có
    // lượt chat nào được ghi nhận (cổng coi là CHƯA sẵn sàng — fail-closed).
    // CoverageHarvestedTurnCount = số lượt cũ nhất (xếp theo CreatedAt) đã gộp vào bản đồ — con trỏ để
    // biết lượt nào còn phải gộp tiếp (fail-open: lời gọi lỗi thì con trỏ đứng yên, lần sau gộp bù).
    // Xem RequirementCoverageService.
    public string? RequirementCoverageMap { get; set; }
    public int CoverageHarvestedTurnCount { get; set; }
    // "Nhật ký điều đã chốt" của dự án: danh sách bullet (text) các QUYẾT ĐỊNH người dùng đã xác nhận
    // trong chat (vai trò, luồng, quy tắc, phương án đã "Đồng ý"), cập nhật sau mỗi lượt như bản đồ bao
    // phủ. KHÔNG còn panel sidebar: nhật ký nay là ngữ cảnh của MÁY (BA soát mâu thuẫn ngay trong lượt +
    // cổng soát trước lúc soạn tài liệu), và người dùng chỉ đọc lại nó một lần ở cổng tổng kết cuối khung
    // chat. DecisionHarvestedTurnCount là con trỏ số lượt đã gộp (fail-open như
    // CoverageHarvestedTurnCount). Xem DecisionLogService.
    public string? DecisionLog { get; set; }
    public int DecisionHarvestedTurnCount { get; set; }
    // "Triển vọng phỏng vấn" — ba danh sách bullet (text) chắt lọc từ hội thoại trong CÙNG một lời gọi
    // (InterviewOutlookService), cập nhật ở hậu kỳ lượt chat như DecisionLog (không cộng vào độ chờ):
    //  • OpenQuestions: điểm còn MƠ HỒ / MÂU THUẪN chưa chốt — TỒN ĐỌNG câu hỏi của BA. KHÔNG hiển thị
    //    thành panel (user chỉ cần trò chuyện; hỏi cho hết là việc của BA): danh sách này được nạp vào
    //    ngữ cảnh mỗi lượt chat làm la bàn ƯU TIÊN cạnh bản đồ bao phủ — bản đồ chỉ phân giải theo NHÓM,
    //    còn đây giữ đúng điểm chưa chốt. Mục được chốt thì rời khỏi danh sách. Xem BAChatService.
    //  • PlannedScope: các MÀN HÌNH/TÍNH NĂNG dự kiến dựng dần theo hội thoại. Không có panel sidebar nào
    //    hiển thị nó (một danh sách SUY ĐOÁN mà user không sửa được tại chỗ là nhiễu, không đóng được vòng
    //    "bắt hiểu nhầm sớm"). Nó tới tay người dùng ở dạng SỬA ĐƯỢC — bảng màn hình (ScreenScopeMap) —
    //    rồi từ đó thành DÒNG của bảng phân quyền; ngoài ra vẫn dùng làm ngữ cảnh soát mâu thuẫn. Xem
    //    ScreenScopeGate + RequirementConflictService. Đây là cột DUY NHẤT của ba cột này có chỗ ghi thứ
    //    hai ngoài lượt chắt lọc: chốt xong bảng màn hình, ConfirmScreenScopeUseCase ghi ngược phạm vi đã
    //    duyệt lên đây để lượt chắt lọc sau gộp tiếp từ bản người dùng gật, không diễn đạt lại từ đầu.
    //  • WorkedExamples: các VÍ DỤ TÍNH THỬ người dùng ĐÃ XÁC NHẬN (input → kết quả kỳ vọng) cho quy tắc
    //    định lượng — nguồn để bước sinh AI Design Spec đúc thành "## 13. Worked Examples" và POC tự kiểm
    //    (window.pocWorkedExamples) đối chiếu ĐỘC LẬP: kỳ vọng do user chốt, giá trị do POC tự tính.
    // InterviewOutlookHarvestedTurnCount là con trỏ số lượt đã gộp (fail-open như các bản đồ khác).
    public string? OpenQuestions { get; set; }
    public string? PlannedScope { get; set; }
    public string? WorkedExamples { get; set; }
    public int InterviewOutlookHarvestedTurnCount { get; set; }
    // BẢNG PHÂN QUYỀN người dùng ĐÃ CHỐT (JSON PermissionMatrixRow[]) — màn hình × chức năng × vai trò,
    // mỗi ô kèm PHẠM VI DỮ LIỆU ("của mình" / "của đơn vị" / "tất cả"). null = chưa chốt.
    //
    // Vì sao phân quyền có đường riêng thay vì nằm chung trong hội thoại như mọi điều khác: câu hỏi "mỗi
    // vai trò được xem và làm những gì" bắt người dùng nghiệp vụ tự dựng cả ma trận trong đầu, nên nó gần
    // như luôn nhận về một câu đóng cửa ("cứ vậy đã, có gì tôi bổ sung sau") — rồi BA tự soạn phương án,
    // người dùng bấm một chip "Đồng ý", và nhóm «Phân quyền theo nghiệp vụ» của bản đồ bao phủ được chấm
    // [RÕ] với bằng chứng đúng bằng bốn chữ ấy. Đó là kiểu [RÕ] oan tệ nhất hệ thống mắc được: BA bị cấm
    // hỏi lại nhóm đã [RÕ] nên thông tin đó vĩnh viễn không được lấy. Bảng đảo chiều chi phí (tích ô rẻ
    // hơn kể) và để lại bằng chứng trên TỪNG ô.
    //
    // Non-null ⇒ khối đã chốt được nạp vào ngữ cảnh chat (BA thôi hỏi lại), vào lượt distill bản đồ bao
    // phủ (nhóm phân quyền [RÕ] có căn cứ thật), và vào prompt sinh AI Design Spec (POC dựng UI theo vai
    // thay vì để phân quyền tan vào văn xuôi). Xem PermissionMatrixBuilder + PermissionMatrixGate.
    public string? PermissionMatrix { get; set; }
    // BA BẢNG CHỐT còn lại của buổi phỏng vấn (JSON), cùng khuôn với PermissionMatrix ở trên: BA điền sẵn
    // theo hội thoại → người dùng sửa/bỏ tích → chốt một lần → khối "đã chốt" đi vào ngữ cảnh chat, lượt
    // distill bản đồ bao phủ và prompt sinh AI Design Spec. null = chưa chốt.
    //
    // KHÁC PermissionMatrix ở một điểm sống còn, và điểm đó là thứ giữ cho hệ thống không tự khóa: nhóm
    // «Phân quyền theo nghiệp vụ» KHÔNG BAO GIỜ được [RÕ] khi chưa có bảng, còn ba bảng dưới đây chỉ XÁC
    // NHẬN LẠI thứ đã [RÕ] từ hội thoại. Nếu bắt các nhóm tương ứng phụ thuộc vào bảng thì cổng mở bảng
    // (đòi nhóm đó [RÕ]) và bản đồ (chỉ [RÕ] khi có bảng) khóa chặt lẫn nhau — đúng cái bẫy mà
    // PermissionMatrixGate đã phải né bằng cách bỏ qua chính dòng phân quyền khi xét.
    //
    //  • FlowMap (FlowMapRow[]) — các LUỒNG nghiệp vụ theo vai trò: luồng chính + 1–2 ngoại lệ, mỗi luồng
    //    là chuỗi bước (ai làm → làm gì → trạng thái sau đó) sửa được và bỏ được. Đây là đường DUY NHẤT để
    //    chuỗi bước người dùng tự tay duyệt tới được oracle chấm POC ("## 13. Worked Examples" định tính) —
    //    trước đó nó là bản LLM chắt từ transcript, không ai duyệt và cũng không sửa tay được nữa.
    //  • ScreenScopeMap (ScreenScopeRow[]) — các MÀN HÌNH dự kiến, kèm việc của từng màn và các BƯỚC LUỒNG
    //    nó phục vụ. Vá một lỗ hổng đang mở: các DÒNG của bảng phân quyền lấy từ PlannedScope, một danh
    //    sách do LLM chắt mà người dùng chưa bao giờ nhìn thấy (panel sidebar đã gỡ) — tức cả phần phân
    //    quyền đang đứng trên một nền chưa ai duyệt.
    //  • EntityMap (EntityMapRow[]) — các ĐỐI TƯỢNG nghiệp vụ: thông tin cần lưu + vòng đời trạng thái.
    //    Đi vào "## 8. Data Model Summary" của spec, mục mà bước sinh spec vốn phải TỰ NGHĨ RA từ văn xuôi
    //    Product Brief. Vòng đời của nó còn là nguồn DÒNG của bảng thông báo ngay dưới.
    //
    // Thứ tự bày là TẤT ĐỊNH và mỗi lượt chỉ có ĐÚNG MỘT bảng — xem InterviewTableGate.
    public string? FlowMap { get; set; }
    public string? ScreenScopeMap { get; set; }
    public string? EntityMap { get; set; }
    // BẢNG BÁO CÁO / THỐNG KÊ (JSON ReportMapRow[]) — mỗi báo cáo một dòng: tên, nó trả lời câu hỏi gì, lấy
    // số từ đối tượng nào, gộp/lọc theo gì. null = chưa chốt.
    //
    // Cùng luật MỀM với ba bảng trên (bảng chỉ XÁC NHẬN LẠI thứ hội thoại đã trả lời), và cổng của nó còn
    // ĐÒI nhóm «Báo cáo / thống kê» đã [RÕ] trước khi bày: một bảng báo cáo TRỐNG bắt người dùng nghiệp vụ
    // tự chẻ câu chuyện của họ thành bốn cột trước khi gõ được chữ nào, tức thu về ít hơn cả ô kể tự do nó
    // thay thế. Mỗi dòng còn tích là một MÀN HÌNH: ConfirmReportMapUseCase gieo nó vào PlannedScope nên nó
    // đi tiếp vào bảng màn hình → bảng phân quyền → "## 6. Screens To Generate". Đó cũng là lý do bảng này
    // đứng TRƯỚC bảng phân quyền và không có cột "ai xem" riêng. Xem ReportMapBuilder + ReportMapGate.
    public string? ReportMap { get; set; }
    // BẢNG THÔNG BÁO / NHẮC NHỞ (JSON NotificationMapRow[]) — bảng CUỐI CÙNG của buổi phỏng vấn: mỗi sự
    // kiện một dòng, người nhận chính (To) và đồng gửi (CC) chọn từ một danh sách đóng. null = chưa chốt.
    //
    // Cùng luật KHẮT KHE MỘT CHIỀU với PermissionMatrix, và vì cùng lý do: nhóm «Thông báo / nhắc nhở»
    // KHÔNG được hỏi bằng câu hỏi nữa nên nó không bao giờ [RÕ] khi chưa có bảng. Chuẩn [RÕ] của nhóm đòi
    // hai vế GHÉP ĐƯỢC với nhau (mỗi sự kiện biết người nhận của riêng nó), trong khi câu hỏi tự nhiên lại
    // tách chúng làm hai câu rời — người dùng bấm bốn chip vai trò là tài liệu đóng băng thành "mọi thay
    // đổi trạng thái gửi cho cả bốn nhóm". Xem NotificationMapBuilder + NotificationMapGate.
    public string? NotificationMap { get; set; }
    // DANH SÁCH NGƯỜI NHẬN của dự án (JSON string[]) — nguồn DUY NHẤT của hai ô To/CC ở bảng thông báo,
    // và là một bảng người dùng tự sửa được ngay trên bảng đó (thêm / sửa chữ / xóa). null = chưa chốt
    // lần nào ⇒ danh sách bày ra là bản gieo tất định (NotificationMapBuilder.SeedRecipients: bốn quan hệ
    // với bản ghi + các vai trò của bảng phân quyền đã chốt).
    //
    // Vì sao là một CỘT chứ không phải dựng lại từ bảng phân quyền mỗi lần: đường gửi cố ý không tin bộ
    // tùy chọn trình duyệt gửi kèm (xem ConfirmNotificationMapUseCase), nên một người nhận người dùng tự
    // thêm mà không được lưu ở đâu cả sẽ bị NormalizeRecipients bỏ sạch ngay lúc bấm gửi — bảng hiện rõ
    // tên người nhận ở từng dòng mà server lại báo "chưa chọn người nhận", và không ai gỡ được ca đó.
    // Lưu cùng lượt với NotificationMap, trong cùng một SaveChanges.
    public string? NotificationRecipients { get; set; }
    // CỔNG XÁC NHẬN GIẢ ĐỊNH (giữa "sinh AI Design Spec" và "dựng POC"). Spec được phép tự đưa giả định
    // (mục "## 12. Assumptions") cho những điều Product Brief không nói; trước đây các giả định đó đi
    // THẲNG vào POC và user chỉ phát hiện sai sau khi ngồi chờ cả lượt dựng POC. Nay spec sinh xong mà
    // có giả định thì worker DỪNG lại: PendingAssumptionsVersion = phiên bản V{n} đang chờ user rà —
    // non-null ⇔ trang Requirements hiện cổng "Xác nhận & dựng bản demo". Xác nhận ⇒ về null rồi mới
    // khởi động delivery workflow; báo sai ⇒ về null, ghi đính chính vào SpecAssumptionCorrections và
    // sinh lại spec (rồi cổng dựng lại). null trên dự án cũ = không có gì chờ, luồng chạy như trước.
    public string? PendingAssumptionsVersion { get; set; }
    // CỔNG SOÁT MÂU THUẪN (ngay trước khi soạn Product Brief — xem RequirementConflictService). Bản đồ bao
    // phủ chỉ trả lời "đã rõ hết chưa", không trả lời "những điều đã rõ có chọi nhau không": người dùng nói
    // ở lượt 3 rằng quản lý duyệt xong là hết, lượt 12 lại kể thêm HR duyệt — cả hai lần nhóm đó vẫn [RÕ].
    // PendingConflicts = JSON các cặp mâu thuẫn đang chờ người dùng chốt lại (null = không có gì chờ);
    // ConflictCheckedTurnCount = số lượt hội thoại tại thời điểm soát, để không gọi lại LLM khi hội thoại
    // chưa đổi. Cả hai null/0 trên dự án cũ = luồng chạy y như trước.
    public string? PendingConflicts { get; set; }
    public int ConflictCheckedTurnCount { get; set; }
    // Các đính chính giả định người dùng đã gửi ở cổng trên, gom tích lũy (text bullet). Được nạp vào
    // prompt sinh AI Design Spec (RequirementPromptBuilder.BuildAiDesignSpec) để lượt sinh lại KHÔNG
    // lặp lại đúng giả định vừa bị bác. Giữ lại sau khi đã xác nhận: các lần sinh spec sau (phiên bản
    // mới) vẫn phải tôn trọng điều user đã đính chính.
    public string? SpecAssumptionCorrections { get; set; }
    // Vế ĐỐI XỨNG của cột trên: các giả định user đã bấm "Đúng" ở cổng (mỗi dòng một giả định). Không có
    // cột này thì mỗi lượt sinh lại spec, cổng hỏi lại NGUYÊN VĂN cả những điểm vừa được duyệt — user
    // thấy "đã trả lời rồi mà BA cứ hỏi". Có nó thì cổng chỉ hỏi phần mới, và lượt sinh lại không đẻ ra
    // giả định mới nào sẽ tự xác nhận chạy thẳng sang dựng POC. Xem AssumptionMemory.
    public string? ConfirmedAssumptions { get; set; }
    // HÀNG ĐỢI học từ giả định bị bác: khối đính chính user vừa gửi ở cổng, CHƯA được chắt lọc thành bài
    // học cho bộ câu hỏi của BA. ReviseSpecAssumptionsUseCase ghi vào đây; AgentTaskWorker gọi
    // SpecAssumptionMemoryService ở lượt sinh lại spec ngay sau đó rồi xoá. Vì sao cần một hàng đợi chứ
    // không đọc thẳng SpecAssumptionCorrections: cột đó tích lũy và bị cắt vòng, không có cách nào biết
    // phần nào đã học. Fail-open: harvest lỗi ⇒ giữ nguyên hàng đợi, lượt sau gộp bù. Xem
    // SpecAssumptionMemoryService.
    public string? PendingAssumptionGaps { get; set; }
    // Con trỏ học từ ghi chú POC: số PocComment (xếp theo CreatedAt) của dự án đã được chắt lọc vào
    // AgentChecklistItem sau mỗi vòng chỉnh sửa POC — ghi chú kiểu "thiếu màn hình X" chính là
    // câu hỏi BA lẽ ra phải hỏi từ lúc phỏng vấn. Xem PocFeedbackMemoryService.
    public int PocFeedbackHarvestedCount { get; set; }
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
