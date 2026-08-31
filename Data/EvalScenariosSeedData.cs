using ICOGenerator.Domain;

namespace ICOGenerator.Data;

// Golden set mặc định cho trang Prompt Evals — tách riêng khỏi DbInitializer cho gọn (cùng mẫu OrgUnitsSeedData).
// Mỗi prompt template "đánh giá được bằng text" (system + 1 user message → text) có các scenario phủ những quy tắc
// quan trọng nhất của nó: happy path, thiếu thông tin, và các bẫy hay vi phạm (tự giả định, sai định dạng, hỏi dồn…).
//
// KHÔNG seed scenario cho các template chỉ có nghĩa khi agent có tool/workspace thật (Developer/poc-preview,
// bugfix, implementation*, pull-request; TechLead/code-review; Tester/testing; các instruction.md;
// Shared/revision + tool-agent-native — file có placeholder {{...}};
// organization-context/organization-scope/organization-platform — khối ngữ cảnh đính kèm prompt khác, không
// phải prompt độc lập): harness eval chạy text-only nên điểm số cho chúng sẽ gây hiểu lầm.
// Ngoại lệ: TechLead/architecture-design.v1 vẫn đo được vì sản phẩm chính là NỘI DUNG bản thiết kế.
//
// UserInput của từng scenario mô phỏng đúng ĐỊNH DẠNG đầu vào thật mà service dựng lúc runtime
// (ConversationTranscriptBuilder "BA:/Người dùng:", RequirementPromptBuilder, các khối "## Bản đồ hiện có"…)
// để điểm eval phản ánh sát hành vi production.
public static class EvalScenariosSeedData
{
    public static EvalScenario[] Build()
    {
        var scenarios = new List<EvalScenario>();

        void Add(string name, string promptKey, string userInput, string criteria) =>
            scenarios.Add(new EvalScenario
            {
                Name = name,
                PromptKey = promptKey,
                UserInput = userInput.Trim(),
                Criteria = criteria.Trim(),
                CreatedByUsername = "seed"
            });

        // Phỏng vấn MÔ PHỎNG: UserInput là hồ sơ vai diễn, một model đóng vai người dùng nghiệp vụ và
        // BA phải tự đào cho ra thông tin qua nhiều lượt. Đây là tầng đo thứ mà scenario một-lượt không
        // chạm tới: cả cuộc phỏng vấn có tới đích không và tốn bao nhiêu lượt của người dùng.
        void AddInterview(string name, string persona, string criteria) =>
            scenarios.Add(new EvalScenario
            {
                Name = name,
                PromptKey = "BusinessAnalyst/requirement-chat.v4.md",
                Kind = Domain.Enums.EvalScenarioKind.Interview,
                UserInput = persona.Trim(),
                Criteria = criteria.Trim(),
                CreatedByUsername = "seed"
            });

        // ================= BusinessAnalyst/requirement-chat.v4.md =================

        Add(
            "Chat BA — mở đầu mơ hồ: một câu hỏi nghiệp vụ + JSON đúng format",
            "BusinessAnalyst/requirement-chat.v4.md",
            """
            Tôi muốn làm một ứng dụng quản lý kho vật tư cho phòng MFG.
            """,
            """
            - Trả về DUY NHẤT một object JSON hợp lệ có đủ 3 trường message, suggestions, ready — không có bất kỳ chữ nào ngoài JSON.
            - ready phải là false (mới lượt đầu, chưa khai thác được gì).
            - message chỉ chứa ĐÚNG MỘT câu hỏi, ở góc nhìn nghiệp vụ (mục tiêu, người dùng, quy trình…), KHÔNG hỏi chi tiết kỹ thuật (SSO, API, database…).
            - message KHÔNG nhắc tới nút "Write Requirement".
            - suggestions có 2–5 đáp án ngắn (~2–6 từ) sát ngữ cảnh quản lý kho; không có lựa chọn kiểu "Khác"/"Tự nhập".
            - message KHÔNG liệt kê lại các đáp án đã nằm trong suggestions.
            - Toàn bộ bằng tiếng Việt.
            """);

        Add(
            "Chat BA — người dùng nói 'sao cũng được': đề xuất MỘT phương án và xin chốt",
            "BusinessAnalyst/requirement-chat.v4.md",
            """
            Hội thoại trước đó:
            BA: Nếu phiếu xuất kho bị quản lý từ chối thì xử lý tiếp thế nào?
               (Các lựa chọn gợi ý đã đưa cho người dùng: [1] Nhân viên sửa rồi gửi lại; [2] Hủy hẳn phiếu; [3] Chuyển cấp cao hơn duyệt)
            Người dùng: Sao cũng được, bạn quyết giúp tôi.
            """,
            """
            - Trả về DUY NHẤT một object JSON hợp lệ {message, suggestions, ready}, không chữ nào ngoài JSON.
            - message đề xuất ĐÚNG MỘT phương án cụ thể cho việc xử lý phiếu bị từ chối và xin người dùng chốt — KHÔNG bỏ qua điểm này, KHÔNG tự coi như đã chốt, KHÔNG chuyển sang hỏi chủ đề khác.
            - Không hỏi thêm câu khai thác nào khác trong cùng lượt (mỗi lượt một câu).
            - suggestions dạng chốt phương án (2–5 mục), ví dụ "Đồng ý phương án này" / "Tôi muốn khác".
            - ready = false và KHÔNG nhắc tới nút "Write Requirement".
            """);

        Add(
            "Chat BA — mọi nhóm đã rõ: ready=true, mời Write Requirement, suggestions rỗng",
            "BusinessAnalyst/requirement-chat.v4.md",
            """
            ## Bản đồ bao phủ yêu cầu
            - ★ Mục tiêu / bài toán: [RÕ] Đặt phòng họp nội bộ thay cho ghi sổ giấy, hết trùng lịch.
            - ★ Đối tượng người dùng & vai trò: [RÕ] Nhân viên đặt phòng; admin hành chính quản lý danh mục phòng.
            - ★ Chức năng & luồng nghiệp vụ chính: [RÕ] Xem lịch trống theo ngày, đặt phòng theo khung giờ, hủy lượt đặt; admin thêm/sửa phòng.
            - Quy trình hiện tại & điểm khó: [RÕ] Đang ghi sổ giấy, hay trùng giờ.
            - Luồng ngoại lệ & trường hợp đặc biệt: [RÕ] Trùng khung giờ thì hệ thống từ chối; chỉ được hủy trước giờ họp.
            - Dữ liệu / danh mục chính: [RÕ] Danh mục phòng họp (tên, sức chứa, máy chiếu) do admin quản lý.
            - Quy tắc nghiệp vụ & ràng buộc: [RÕ] Không cho đặt trùng khung giờ cùng phòng; chỉ người đặt mới được hủy.
            - Vòng đời & trạng thái: [RÕ] Lượt đặt: đã đặt → đã hủy / đã diễn ra.
            - Thông báo / nhắc nhở: [KHÔNG ÁP DỤNG] Người dùng xác nhận không cần thông báo.
            - Báo cáo / thống kê: [KHÔNG ÁP DỤNG] Người dùng xác nhận không cần báo cáo.
            - Phân quyền theo nghiệp vụ: [RÕ] Nhân viên chỉ đặt/hủy lượt của mình; admin quản lý danh mục.
            - Quy mô sử dụng: [RÕ] Khoảng 80 nhân viên, vài chục lượt đặt mỗi ngày.

            Lượt mới nhất của người dùng (trả lời câu hỏi cuối cùng về quy mô):
            Người dùng: Khoảng 80 người dùng thôi.
            """,
            """
            - Trả về DUY NHẤT một object JSON hợp lệ {message, suggestions, ready}, không chữ ngoài JSON.
            - ready PHẢI là true — mọi nhóm đã [RÕ] hoặc [KHÔNG ÁP DỤNG], không còn gì phải giả định.
            - message tóm tắt ngắn cách hiểu yêu cầu VÀ nói rõ: nếu không bổ sung gì thêm thì bấm nút "Write Requirement" để tạo tài liệu.
            - message KHÔNG đặt thêm câu hỏi khai thác thông tin nào.
            - suggestions PHẢI là mảng rỗng [].
            """);

        Add(
            "Chat BA — bị giục viết tài liệu khi còn thiếu: giữ ready=false và hỏi tiếp nhóm ★",
            "BusinessAnalyst/requirement-chat.v4.md",
            """
            ## Bản đồ bao phủ yêu cầu
            - ★ Mục tiêu / bài toán: [RÕ] Quản lý đơn đề nghị mua vật tư của phòng.
            - ★ Đối tượng người dùng & vai trò: [MỘT PHẦN] Có nhân viên đề nghị và "sếp" duyệt; còn thiếu: ai là người duyệt (trưởng nhóm hay trưởng phòng), có mấy cấp duyệt.
            - ★ Chức năng & luồng nghiệp vụ chính: [MỘT PHẦN] Tạo đơn, duyệt đơn; còn thiếu: sau khi duyệt thì ai đi mua, quy trình kết thúc ở bước nào.
            - Quy trình hiện tại & điểm khó: [RÕ] Đang xin duyệt qua email, hay thất lạc.
            - Luồng ngoại lệ & trường hợp đặc biệt: [CHƯA HỎI]
            - Dữ liệu / danh mục chính: [CHƯA HỎI]
            - Quy tắc nghiệp vụ & ràng buộc: [CHƯA HỎI]
            - Vòng đời & trạng thái: [CHƯA HỎI]
            - Thông báo / nhắc nhở: [CHƯA HỎI]
            - Báo cáo / thống kê: [CHƯA HỎI]
            - Phân quyền theo nghiệp vụ: [CHƯA HỎI]
            - Quy mô sử dụng: [CHƯA HỎI]

            Lượt mới nhất của người dùng:
            Người dùng: Thôi đủ rồi đó, bạn viết tài liệu luôn đi, tôi đang vội.
            """,
            """
            - Trả về DUY NHẤT một object JSON hợp lệ {message, suggestions, ready}, không chữ ngoài JSON.
            - ready PHẢI là false — còn nhiều nhóm [CHƯA HỎI]/[MỘT PHẦN], không được chiều theo lời giục.
            - message KHÔNG nhắc tới nút "Write Requirement"; có thể giải thích rất ngắn vì sao cần làm rõ thêm.
            - message hỏi ĐÚNG MỘT câu, nhắm vào nhóm ★ còn thiếu (ai duyệt / mấy cấp duyệt, hoặc luồng sau khi duyệt) — không nhảy sang nhóm phụ.
            - suggestions có 2–5 đáp án ngắn phù hợp với câu hỏi.
            """);

        Add(
            "Chat BA — bẫy kỹ thuật: nhu cầu đăng nhập không được biến thành câu hỏi SSO/API",
            "BusinessAnalyst/requirement-chat.v4.md",
            """
            Hội thoại trước đó:
            BA: Ứng dụng chấm công này dành cho những ai sử dụng?
            Người dùng: Toàn bộ nhân viên nhà máy, và mỗi người phải đăng nhập bằng tài khoản riêng để chỉ xem được công của chính mình thôi.
            """,
            """
            - Trả về DUY NHẤT một object JSON hợp lệ {message, suggestions, ready}, không chữ ngoài JSON; ready = false.
            - Câu hỏi tiếp theo ở góc nhìn nghiệp vụ; TUYỆT ĐỐI KHÔNG chứa các chủ đề kỹ thuật như SSO, OAuth, SAML, LDAP, SMTP, API, webhook, database, server.
            - KHÔNG hỏi cách hiện thực việc đăng nhập (dùng tài khoản nội bộ hay hệ thống ngoài…) — nhu cầu "mỗi người đăng nhập riêng, chỉ xem công của mình" đã rõ ở mức nghiệp vụ.
            - Chỉ MỘT câu hỏi; có suggestions 2–5 đáp án ngắn.
            """);

        Add(
            "Chat BA — bám bản đồ bao phủ: hỏi đúng chỗ thiếu, không hỏi lại nhóm đã [RÕ]",
            "BusinessAnalyst/requirement-chat.v4.md",
            """
            ## Bản đồ bao phủ yêu cầu
            - ★ Mục tiêu / bài toán: [RÕ] Theo dõi đơn nghỉ phép thay cho form giấy.
            - ★ Đối tượng người dùng & vai trò: [RÕ] Nhân viên nộp đơn; quản lý trực tiếp duyệt; nhân sự xem tổng hợp.
            - ★ Chức năng & luồng nghiệp vụ chính: [MỘT PHẦN] Nộp đơn và duyệt đã rõ; còn thiếu: đơn bị từ chối thì đi tiếp thế nào.
            - Quy trình hiện tại & điểm khó: [RÕ] Form giấy, hay thất lạc, không biết còn bao nhiêu ngày phép.
            - Luồng ngoại lệ & trường hợp đặc biệt: [MỘT PHẦN] Còn thiếu: xử lý khi đơn bị từ chối, khi muốn hủy đơn đã duyệt.
            - Dữ liệu / danh mục chính: [RÕ] Loại phép (phép năm, ốm, việc riêng) do nhân sự quản lý.
            - Quy tắc nghiệp vụ & ràng buộc: [RÕ] Trừ vào quỹ phép năm; không cho nộp quá số ngày còn lại.
            - Vòng đời & trạng thái: [RÕ] Chờ duyệt → đã duyệt / bị từ chối.
            - Thông báo / nhắc nhở: [RÕ] Báo quản lý khi có đơn mới; báo nhân viên khi có kết quả.
            - Báo cáo / thống kê: [RÕ] Nhân sự xem tổng hợp ngày phép theo phòng ban.
            - Phân quyền theo nghiệp vụ: [RÕ] Nhân viên chỉ thấy đơn của mình; quản lý thấy đơn của nhóm mình.
            - Quy mô sử dụng: [RÕ] Khoảng 200 nhân viên.

            Lượt mới nhất của người dùng:
            Người dùng: Ừ đúng rồi, loại phép thì để nhân sự quản lý.
            """,
            """
            - Trả về DUY NHẤT một object JSON hợp lệ {message, suggestions, ready}, không chữ ngoài JSON; ready = false.
            - Câu hỏi tiếp theo PHẢI nhắm vào điểm còn thiếu trên bản đồ: đơn bị từ chối xử lý tiếp thế nào (hoặc hủy đơn đã duyệt).
            - KHÔNG hỏi lại bất kỳ nhóm nào đã [RÕ] (mục tiêu, vai trò, loại phép, thông báo, báo cáo, quy mô…).
            - Chỉ MỘT câu hỏi; suggestions 2–5 đáp án ngắn sát ngữ cảnh (vd "Nhân viên sửa rồi gửi lại").
            - KHÔNG nhắc tới nút "Write Requirement".
            """);

        Add(
            "Chat BA — hỏi bổ sung phải PHÁT LẠI danh sách đã ghi nhận, không tham chiếu suông",
            "BusinessAnalyst/requirement-chat.v4.md",
            """
            ## Bản đồ bao phủ yêu cầu
            - ★ Mục tiêu / bài toán: [RÕ] Lập kế hoạch các lớp học cả năm cho nhân viên và đánh giá kết quả học.
            - ★ Đối tượng người dùng & vai trò: [RÕ] Assistant phòng HR lập kế hoạch; HoD HR duyệt; nhân viên đăng ký; quản lý trực tiếp duyệt ticket; admin xử lý waitlist; giáo viên chấm kết quả.
            - ★ Chức năng & luồng nghiệp vụ chính: [RÕ] Tạo project → upload Master List → tạo version plan → chốt số lớp theo tháng → generate training implement → HoD HR duyệt theo quý → nhân viên đăng ký → quản lý duyệt → enroll/waitlist.
            - Quy trình hiện tại & điểm khó: [RÕ] Đang làm bằng file Excel gửi đầu năm.
            - Luồng ngoại lệ & trường hợp đặc biệt: [RÕ] HoD HR reject thì assistant làm lại; lớp đầy thì ticket chuyển waitlist cho admin xét; quản lý có thể reject ticket.
            - Dữ liệu / danh mục chính: [MỘT PHẦN] Đã rõ thông tin của LỚP HỌC; còn thiếu: mỗi KHÓA HỌC cần quản lý những thông tin nào.
            - Quy tắc nghiệp vụ & ràng buộc: [RÕ] Mỗi lớp có sĩ số tối thiểu – tối đa; duyệt kế hoạch theo từng quý.
            - Vòng đời & trạng thái: [RÕ] Ticket: pending → enroll / waitlist / reject.
            - Thông báo / nhắc nhở: [CHƯA HỎI]
            - Báo cáo / thống kê: [CHƯA HỎI]
            - Phân quyền theo nghiệp vụ: [RÕ] Assistant lập kế hoạch, HoD HR duyệt, quản lý duyệt ticket nhân viên mình, admin xử lý waitlist.
            - Quy mô sử dụng: [CHƯA HỎI]

            Hội thoại trước đó:
            BA: Anh/chị kể giúp mình một lần gần nhất lập kế hoạch các lớp học trong năm?
            Người dùng: … từ trang training plan detail sẽ có 1 button generate training implement, khi click vào button này thì sẽ chuyển qua trang training implement và sẽ tự generate các khóa học và các lớp cần mở vào trang này, trang này sẽ có những thông tin chi tiết hơn như lớp dạy ngày nào, phòng nào, ai là người dạy, ngôn ngữ, dạy trong bao lâu, mã lớp học, link lớp học để cho nhân viên có thể vào đăng ký…
            BA: Mình cần chốt chu kỳ duyệt: Assistant lập kế hoạch và gửi HoD HR duyệt theo từng quý, hay duyệt một lần cho toàn bộ năm?
               (Các lựa chọn gợi ý đã đưa cho người dùng: [1] Duyệt theo từng quý; [2] Duyệt một lần cả năm)
            Người dùng: Duyệt theo từng quý
            """,
            """
            - Trả về DUY NHẤT một object JSON hợp lệ, không chữ nào ngoài JSON; ready = false; KHÔNG nhắc tới nút "Write Requirement".
            - message PHẢI CHÉP LẠI danh sách thông tin của LỚP HỌC mà người dùng đã kể (ngày học, phòng học, người dạy, ngôn ngữ, thời lượng, mã lớp, link đăng ký — đủ phần lớn các mục này) TRƯỚC khi hỏi tiếp.
            - TUYỆT ĐỐI KHÔNG thay việc liệt kê đó bằng cụm tham chiếu suông: "đã nêu", "ở trên", "như đã đề cập", "những thông tin trên" — người dùng chỉ nhìn thấy ô chat cuối cùng, không nhìn thấy cuộn hội thoại.
            - Hỏi ĐÚNG MỘT thứ: mỗi KHÓA HỌC cần quản lý thêm những thông tin nào. Không kèm vế thứ hai trong cùng message.
            - KHÔNG bắt người dùng "mô tả các trường thông tin và mối liên hệ giữa khóa học, nhân viên, nhu cầu học và lớp học" — đó là vẽ mô hình dữ liệu, không phải câu hỏi nghiệp vụ.
            - suggestions là các trường cụ thể của khóa học, mỗi chip ĐÚNG MỘT trường (vd "Mã khóa học", "Đối tượng áp dụng", "Thời lượng chuẩn", "Chi phí đào tạo"); không chip gói nhiều thứ, không chip kiểu "Tất cả các ý trên".
            - Câu hỏi dạng liệt kê ⇒ multiSelect = true.
            """);

        Add(
            "Chat BA — quy tắc tính số lớp: tự dựng ví dụ SỐ để chốt, không bắt người dùng mô tả công thức",
            "BusinessAnalyst/requirement-chat.v4.md",
            """
            Hội thoại trước đó:
            BA: Ở trang Training Plan detail, số lớp gợi ý cho mỗi khóa học được tính ra từ đâu?
            Người dùng: Dựa trên data từ master list, nó sẽ có 1 table chứa các khóa học cần được tổ chức và số lượng lớp cần mở cho mỗi khóa học — dựa trên nhu cầu học và số lượng học viên tối thiểu và tối đa của từng lớp. Assistant xem gợi ý đó hợp lý chưa, có thể chỉnh sửa lại, rồi chia số lớp đó ra các tháng trong năm.
            """,
            """
            - Trả về DUY NHẤT một object JSON hợp lệ, không chữ nào ngoài JSON; ready = false; KHÔNG nhắc tới nút "Write Requirement".
            - Đây là quy tắc ĐỊNH LƯỢNG: message phải tự dựng MỘT ví dụ bằng SỐ cụ thể theo cách BA hiểu (một khóa học có N nhân viên phải học, sĩ số tối đa M mỗi lớp ⇒ gợi ý mở K lớp) rồi xin người dùng xác nhận.
            - KHÔNG hỏi lại kiểu "công thức tính thế nào?", "anh/chị mô tả giúp mối liên hệ giữa nhu cầu học và số lớp" — người dùng vừa mô tả xong, việc còn lại là BA chốt cách hiểu bằng ví dụ.
            - Chỉ MỘT câu hỏi trong lượt này; không gộp thêm câu nào khác (quy tắc định lượng phải hỏi một mình).
            - suggestions dạng chốt đúng/sai, 2–3 mục (vd "Đúng rồi" / "Không, tính khác"); multiSelect = false.
            """);

        Add(
            "Chat BA — quy trình hiện tại đã kể xong: đi tiếp sang HƯỚNG CẢI TIẾN, không hỏi lại",
            "BusinessAnalyst/requirement-chat.v4.md",
            """
            ## Bản đồ bao phủ yêu cầu
            - ★ Mục tiêu / bài toán: [MỘT PHẦN] App quản lý danh sách JD trong nhà máy và gán JD cho nhân viên.
            - ★ Đối tượng người dùng & vai trò: [MỘT PHẦN] Có vai trò HRBP thực hiện tạo và gán JD.
            - ★ Chức năng & luồng nghiệp vụ chính: [MỘT PHẦN] HRBP tự thêm/sửa/xóa JD trong file Excel danh sách JD và file Excel quản lý JD đã gán.
            - Quy trình hiện tại & điểm khó: [MỘT PHẦN] HRBP làm bằng 2 file Excel; còn thiếu: điểm khó chịu nhất của cách làm hiện tại và mong muốn cải tiến.
            - Luồng ngoại lệ & trường hợp đặc biệt: [CHƯA HỎI]
            - Dữ liệu / danh mục chính: [CHƯA HỎI]
            - Quy tắc nghiệp vụ & ràng buộc: [CHƯA HỎI]
            - Vòng đời & trạng thái: [CHƯA HỎI]
            - Thông báo / nhắc nhở: [CHƯA HỎI]
            - Báo cáo / thống kê: [CHƯA HỎI]
            - Phân quyền theo nghiệp vụ: [CHƯA HỎI]
            - Quy mô sử dụng: [CHƯA HỎI]

            ## Các câu hỏi BẠN ĐÃ HỎI ở những lượt trước (TUYỆT ĐỐI KHÔNG phát lại)
            - Anh/chị kể giúp mình một lần gần nhất khi tạo và gán một JD cho nhân viên: bắt đầu từ đâu, làm những bước nào, và ai tham gia vào quy trình đó?

            Hội thoại trước đó:
            BA: Anh/chị kể giúp mình một lần gần nhất khi tạo và gán một JD cho nhân viên: bắt đầu từ đâu, làm những bước nào, và ai tham gia vào quy trình đó?
            Người dùng: hiện tại việc tạo và gán JD cho nhân viên được HRBP thực hiện trong file excel, có 1 file excel danh sách JD được dùng trong nhà máy, HRBP vào đó tự thêm, sửa, xóa JD, và 1 file excel khác để quản lý JD được gán cho nhân viên, HRBP cũng tự vào đó để thêm, sửa, xóa
            Người dùng: mình nói ở trên rồi đó, hiện tại chỉ có mỗi HRBP chỉnh sửa thông tin trên 2 file excel thôi
            """,
            """
            - Trả về DUY NHẤT một object JSON hợp lệ, không chữ nào ngoài JSON; ready = false; KHÔNG nhắc tới nút "Write Requirement".
            - TUYỆT ĐỐI KHÔNG hỏi lại quy trình hiện tại dưới bất kỳ hình thức nào: không "kể giúp mình một lần gần nhất…", không "các bước khi tạo và gán JD là gì", không xin xác nhận lại chuỗi thao tác trên 2 file Excel. Người dùng đã kể, và đã phải nhắc "mình nói ở trên rồi đó".
            - Lượt này phải đi sang chặng kế: hỏi ĐIỂM ĐAU của cách làm bằng 2 file Excel, hoặc hỏi MONG MUỐN CẢI TIẾN ở ứng dụng mới.
            - Chỉ MỘT câu hỏi trong message.
            - Nếu hỏi mong muốn cải tiến (câu MỞ, xin lời kể) thì suggestions phải RỖNG và openEnded = true; nếu hỏi điểm đau bằng câu ĐÓNG thì suggestions có 2–5 chip rút từ chính bối cảnh 2 file Excel (vd "Phải sửa tay ở 2 file", "Không biết JD nào đang gán cho ai", "Người khác muốn xem phải hỏi HRBP").
            - KHÔNG tự khẳng định một điểm đau mà người dùng chưa nói ("dữ liệu khó đồng bộ", "khó truy vết") như thể đó là lời họ.
            """);

        // Hai scenario dưới đây đo cùng một ca hỏng thật: khối ranh giới phạm vi (hằng số của sản phẩm,
        // OrganizationContextService đính vào MỌI lời gọi BA) bị BA đối xử như lời người dùng — trước là
        // chèn "Đồng Nai" vào câu "mình ghi nhận", sau là đem chính câu đó ra chất vấn người dùng như một
        // mâu thuẫn. UserInput vì vậy phải mang theo khối ranh giới đúng như runtime đính kèm.
        Add(
            "Chat BA — 'tất cả nhân viên Bosch' KHÔNG phải mâu thuẫn với ranh giới nhà máy",
            "BusinessAnalyst/requirement-chat.v4.md",
            """
            ## Ranh giới phạm vi (BẮT BUỘC — áp cho câu hỏi, phương án gợi ý và tài liệu)

            Mọi ứng dụng khai thác ở đây phục vụ DUY NHẤT nhà máy Bosch tại Đồng Nai, Việt Nam. Đây là điều ĐÃ CHỐT của sản phẩm, không phải điểm cần người dùng xác nhận: đừng hỏi ứng dụng có dùng cho nơi khác không. Phạm vi rộng nhất có thể của một ứng dụng là toàn nhà máy Đồng Nai.

            ## Điều đã chốt
            - Ứng dụng lập kế hoạch các lớp học cả năm và đánh giá kết quả học tập của nhân viên
            - Gồm cả khóa học bắt buộc và khóa học tùy chọn

            Hội thoại trước đó:
            BA: Anh/chị cho mình biết những vai trò nào sẽ sử dụng ứng dụng và trách nhiệm chính của từng vai trò là gì?
            Người dùng: Cứ mỗi đầu năm, các bạn có role Assistant trong phòng HR sẽ nhận được file excel chứa thông tin tất cả nhân viên trong Bosch cùng các khóa học mà tất cả nhân viên đó phải học trong năm. Đầu tiên Assistant sẽ tạo 1 project mới và upload file excel đó vào trang Master List, bước tiếp theo là qua trang Training Plan để tạo 1 version plan, rồi click vào plan để vào trang Training Plan detail — trang này dựa vào data từ master list để gợi ý số lớp cần mở cho mỗi khóa học, assistant xem hợp lý chưa, có thể chỉnh sửa lại, và sau đó chia chi tiết ra số lớp đó cần mở vào các tháng nào trong năm cho phù hợp, rồi bấm button generate training implement để sang trang training implement xem chi tiết lớp (dạy ngày nào, phòng nào, ai dạy, ngôn ngữ, dạy trong bao lâu, mã lớp học, link đăng ký). Làm xong training implement thì assistant submit lên cho HoD của phòng HR duyệt kế hoạch cho quý đó; HoD HR duyệt thì các lớp available và nhân viên click link đăng ký, HoD HR reject thì assistant phải làm lại. Nhân viên click link đăng ký thì ticket ở trạng thái pending, cần manager trực tiếp của nhân viên đó approve thì ticket mới chuyển qua enroll; mỗi lớp đều có min-max học viên nên khi manager approve, lớp chưa đầy thì ticket thành enroll, lớp đầy rồi thì ticket chuyển qua waitlist và admin xem xét để chuyển qua enroll hay reject. Manager cũng có thể reject ticket nếu muốn.
            """,
            """
            - Trả về DUY NHẤT một object JSON hợp lệ, không chữ nào ngoài JSON; ready = false; KHÔNG nhắc tới nút "Write Requirement".
            - TUYỆT ĐỐI KHÔNG hỏi xác nhận phạm vi áp dụng. Người dùng nói "tất cả nhân viên trong Bosch" mà không gọi tên nơi nào khác ⇒ hiểu là toàn nhà máy Đồng Nai, ghi nhận và đi tiếp.
            - KHÔNG được dựng câu kiểu "anh/chị vừa mô tả tất cả nhân viên Bosch, trong khi phạm vi đang được ghi nhận là nhà máy Đồng Nai — phạm vi nào đúng?": ranh giới phạm vi là hằng số của sản phẩm, không phải một vế mâu thuẫn do người dùng nói ra.
            - KHÔNG đưa ra bất kỳ phương án phạm vi nào vượt khỏi nhà máy ("Toàn Bosch Việt Nam", "Các nhà máy Bosch khác", "Toàn tập đoàn"…), dù trong message hay trong suggestions.
            - message PHẢI ghi nhận lại điều người dùng vừa kể (các vai trò Assistant HR / HoD HR / manager trực tiếp / admin / nhân viên, hoặc các bước chính của luồng) trước khi hỏi tiếp — không được bỏ trắng câu trả lời dài vừa nhận.
            - Lượt này phải ĐI TỚI: hoặc chốt luồng ticket bằng MỘT kịch bản cụ thể (pending → manager approve → enroll nếu còn chỗ / waitlist nếu đầy → admin xét) để xin xác nhận, hoặc hỏi một điểm còn mờ khác. Không quay lại hỏi vai trò.
            - suggestions 2–5 mục ngắn, sát câu hỏi thật sự được đặt ra.
            """);

        Add(
            "Chat BA — câu 'mình ghi nhận' không được chèn dữ kiện từ khối ngữ cảnh hệ thống",
            "BusinessAnalyst/requirement-chat.v4.md",
            """
            ## Ranh giới phạm vi (BẮT BUỘC — áp cho câu hỏi, phương án gợi ý và tài liệu)

            Mọi ứng dụng khai thác ở đây phục vụ DUY NHẤT nhà máy Bosch tại Đồng Nai, Việt Nam. Đây là điều ĐÃ CHỐT của sản phẩm, không phải điểm cần người dùng xác nhận: đừng hỏi ứng dụng có dùng cho nơi khác không.

            ## Bối cảnh tổ chức Bosch
            - Department HcP/HRL — Human Resources Learning (HoD: Nguyễn Văn A), 3 orgUnit trực thuộc, 24 nhân sự.
            - Department HcP/MFG1 — Manufacturing 1, 7 orgUnit trực thuộc, 410 nhân sự.

            Hội thoại trước đó:
            BA: Anh/chị cứ kể thoải mái một mạch mọi điều đang hình dung về ứng dụng.
            Người dùng: Đây là app để lên kế hoạch các lớp học cần tổ chức và đánh giá kết quả học tập của tất cả nhân viên Bosch trong 1 năm, bao gồm cả khóa học bắt buộc và khóa học tùy chọn.
            """,
            """
            - Trả về DUY NHẤT một object JSON hợp lệ, không chữ nào ngoài JSON; ready = false; KHÔNG nhắc tới nút "Write Requirement".
            - Nếu message có câu ghi nhận, câu đó chỉ được chứa điều người dùng THẬT SỰ đã nói (lập kế hoạch lớp học cả năm, đánh giá kết quả học tập, khóa bắt buộc + khóa tùy chọn).
            - TUYỆT ĐỐI KHÔNG gán cho người dùng những dữ kiện chỉ có trong khối ngữ cảnh hệ thống: không viết "mình ghi nhận … nhân viên Bosch Đồng Nai", "… tại nhà máy Đồng Nai", không nhét tên department (HcP/HRL, HcP/MFG1) vào phần phát lại như thể họ đã nêu.
            - KHÔNG hỏi ứng dụng có dùng cho nơi khác ngoài nhà máy không, và KHÔNG đưa phương án phạm vi vượt nhà máy vào suggestions.
            - Hỏi tiếp ĐÚNG MỘT câu ở góc nhìn nghiệp vụ, nhắm nhóm ★ chưa khai thác (vd ai dùng và vai trò của họ, hoặc hiện tại việc này đang làm bằng gì).
            - suggestions 2–5 đáp án ngắn sát câu hỏi đó.
            """);

        // Cùng loại chốt chặn với hai scenario trên, cho khối hằng số thứ hai: nhà máy chỉ có DUY NHẤT kênh
        // thông báo email (Prompts/BusinessAnalyst/organization-platform.v1.md). Nhóm "Thông báo / nhắc nhở"
        // là chỗ BA hay tự đẻ ra phương án không có thật — một chip "Thông báo qua Teams" người dùng bấm nhầm
        // là yêu cầu ghi sai kênh từ lượt đầu, rồi chảy thẳng vào tài liệu và bản thiết kế.
        Add(
            "Chat BA — hỏi thông báo: chỉ hỏi AI nhận/KHI NÀO, không gợi ý kênh ngoài email",
            "BusinessAnalyst/requirement-chat.v4.md",
            """
            ## Nền tảng đã chốt của nhà máy (BẮT BUỘC — áp cho câu hỏi, phương án gợi ý và tài liệu)

            ### Kênh thông báo: CHỈ CÓ EMAIL

            Mọi ứng dụng chạy ở nhà máy này đều chỉ có DUY NHẤT một kênh thông báo: EMAIL. KHÔNG có Microsoft Teams, KHÔNG SMS, KHÔNG Zalo, KHÔNG thông báo đẩy / app di động. Đây là điều ĐÃ CHỐT của sản phẩm, không phải điểm cần người dùng chọn: đừng hỏi họ muốn nhận thông báo qua kênh nào. Nhóm "Thông báo / nhắc nhở" chỉ còn hai điều cần làm rõ: AI nhận và KHI NÀO.

            ## Điều đã chốt
            - Ứng dụng quản lý đơn xin nghỉ phép của công nhân xưởng
            - Nhân viên tạo đơn, quản lý trực tiếp duyệt hoặc từ chối

            Hội thoại trước đó:
            BA: Sau khi nhân viên gửi đơn thì ai là người xử lý tiếp, và các trạng thái của đơn gồm những gì?
            Người dùng: Đơn gửi lên là ở trạng thái chờ duyệt, quản lý trực tiếp vào duyệt hoặc từ chối. Nếu nghỉ quá 3 ngày thì sau quản lý trực tiếp còn phải qua HR duyệt nữa mới xong. Ai cũng cần được báo khi đơn của mình được duyệt hay bị từ chối.
            """,
            """
            - Trả về DUY NHẤT một object JSON hợp lệ, không chữ nào ngoài JSON; ready = false; KHÔNG nhắc tới nút "Write Requirement".
            - Người dùng vừa mở nhóm "Thông báo / nhắc nhở" ⇒ lượt này được phép đào tiếp nhóm đó, nhưng CHỈ ở hai chiều: AI nhận (người tạo đơn, quản lý trực tiếp, HR…) và KHI NÀO (đơn được duyệt, bị từ chối, chờ duyệt quá lâu…).
            - TUYỆT ĐỐI KHÔNG hỏi muốn nhận thông báo qua KÊNH nào: kênh đã chốt là email, hỏi lại là hỏi đúng điều ĐÃ CHỐT.
            - TUYỆT ĐỐI KHÔNG đưa vào message/questions/suggestions bất kỳ kênh nào ngoài email — "Thông báo qua Teams", "Nhắn tin SMS", "Gửi Zalo", "Thông báo đẩy trên điện thoại", "Thông báo trong ứng dụng di động" đều là phương án không có thật.
            - KHÔNG hỏi chuyện kỹ thuật của email (SMTP, máy chủ mail, tài khoản gửi, template HTML).
            - Nếu message có câu ghi nhận, câu đó chỉ được chứa điều người dùng THẬT SỰ đã nói — KHÔNG chèn "qua email" vào như thể họ đã nói ra, và KHÔNG đem ràng buộc kênh ra chất vấn họ như một mâu thuẫn.
            - suggestions 2–5 mục ngắn, sát câu hỏi thật sự được đặt ra (vd các nhóm người nhận, hoặc các mốc sự kiện) — không phải danh sách kênh.
            """);

        // Khối hằng số thứ ba: mọi app đăng nhập bằng SSO qua IdentityServer. Chỗ này hỏng khác hai khối
        // trên — prompt chat từng nêu "Người dùng cần đăng nhập riêng cho mỗi người không?" như VÍ DỤ MẪU
        // của câu hỏi đúng tầm nghiệp vụ, nên BA không phải tự nghĩ ra mà được mời hỏi. Một tiếng "cả tổ
        // dùng chung một tài khoản" là yêu cầu không hiện thực được, và không cổng nào bắt lại.
        Add(
            "Chat BA — không hỏi cách đăng nhập, nhưng vẫn hỏi ai được vào app",
            "BusinessAnalyst/requirement-chat.v4.md",
            """
            ## Nền tảng đã chốt của nhà máy (BẮT BUỘC — áp cho câu hỏi, phương án gợi ý và tài liệu)

            ### Đăng nhập: CHỈ CÓ SSO qua IdentityServer

            Mọi ứng dụng chạy ở nhà máy này đều đăng nhập bằng SSO qua IdentityServer, dùng tài khoản Bosch sẵn có của nhân viên. Nhân viên external (người của công ty khác được Bosch thuê) cũng đăng nhập bằng chính SSO đó — họ có tài khoản Bosch y như internal, chỉ khác là KHÔNG nằm trong dữ liệu HR. KHÔNG có màn hình đăng ký tài khoản, KHÔNG username/password riêng của ứng dụng, KHÔNG đăng nhập bằng Google, KHÔNG tài khoản dùng chung, KHÔNG cấp tài khoản riêng cho nhóm external. Đây là điều ĐÃ CHỐT của sản phẩm: đừng hỏi họ muốn đăng nhập kiểu gì, và đừng hỏi "mỗi người có cần tài khoản riêng không?". Vẫn phải hỏi: AI được vào ứng dụng (kể cả nhân viên external), và vào rồi thì hệ thống biết họ là vai nào bằng cách gì.

            ## Điều đã chốt
            - Ứng dụng đặt suất ăn ca cho công nhân xưởng

            Hội thoại trước đó:
            BA: Anh/chị kể giúp mình việc đặt suất ăn hiện nay đang làm thế nào.
            Người dùng: Mỗi tổ có một người tổng hợp số suất rồi báo xuống bếp trước 9 giờ sáng. Bên nhà thầu vệ sinh với bảo vệ cũng ăn chung căng tin nhưng họ không thuộc biên chế Bosch.
            """,
            """
            - Trả về DUY NHẤT một object JSON hợp lệ, không chữ nào ngoài JSON; ready = false; KHÔNG nhắc tới nút "Write Requirement".
            - TUYỆT ĐỐI KHÔNG hỏi bất cứ điều gì về cách đăng nhập — kể cả câu nghe rất nghiệp vụ "mỗi người có cần tài khoản riêng không?", "nhân viên đăng nhập bằng gì". Đăng nhập đã chốt là SSO, hỏi lại là hỏi đúng điều ĐÃ CHỐT.
            - TUYỆT ĐỐI KHÔNG đưa vào message/questions/suggestions bất kỳ phương án đăng nhập nào — "Tài khoản nội bộ", "Đăng ký tài khoản mới", "Đăng nhập bằng Google", "Tài khoản dùng chung cho cả tổ", "Nhập mã nhân viên để vào" đều là phương án không có thật.
            - KHÔNG hỏi chuyện kỹ thuật của đăng nhập (OAuth/SAML/LDAP, client id, token, đồng bộ tài khoản).
            - Người dùng vừa tự nêu nhân viên external (nhà thầu vệ sinh, bảo vệ không thuộc biên chế Bosch) ⇒ đây là câu hỏi HỢP LỆ và đáng hỏi: họ có dùng ứng dụng này không / suất của họ ai đặt. ĐẠT nếu lượt này đào đúng vế đó hoặc một nhóm ★ chưa khai thác; TRƯỢT nếu coi external là chuyện kỹ thuật của đăng nhập rồi bỏ qua.
            - TRƯỢT nếu dựng nhóm external thành ngoại lệ của ĐĂNG NHẬP — hỏi "nhóm này vào ứng dụng bằng cách nào", nói họ "không đăng nhập được", hay gợi ý cấp tài khoản riêng cho họ. External cũng đăng nhập bằng SSO như internal; thứ họ thiếu là bản ghi trong dữ liệu HR, không phải tài khoản.
            - Nếu message có câu ghi nhận, câu đó chỉ được chứa điều người dùng THẬT SỰ đã nói — KHÔNG chèn "đăng nhập bằng SSO" vào như thể họ đã nói ra.
            """);

        // Nguồn dữ liệu: hỏi DỮ LIỆU TỪ ĐÂU RA là nghiệp vụ, hỏi HAI HỆ THỐNG NỐI NHAU BẰNG GÌ là kỹ thuật.
        // Ranh giới này mảnh, và trượt về phía nào cũng đắt: cấm quá tay thì tài liệu im lặng về nguồn và
        // POC dựng màn hình nhập tay cho dữ liệu do nơi khác đổ sang; nới quá tay thì BA hỏi người dùng
        // nghiệp vụ về webhook. Kịch bản đặt BA vào đúng lượt SAU khi đã có file, tức lượt được phép hỏi.
        Add(
            "Chat BA — hỏi dữ liệu từ đâu ra, không hỏi hai hệ thống nối nhau bằng gì",
            "BusinessAnalyst/requirement-chat.v4.md",
            """
            ## Điều đã chốt
            - Ứng dụng lập kế hoạch lớp học cho nhân viên trong năm
            - Người dùng đã đính kèm file MasterList.xlsx ở lượt trước

            Hội thoại trước đó:
            BA: Anh/chị gửi giúp mình file Master List đang dùng nhé, mình đọc rồi hỏi tiếp cho đỡ mất công gõ lại.
            Người dùng: [đã đính kèm MasterList.xlsx] Đây, file này bên HR bên em xuất từ hệ thống SAP ra rồi gửi qua cho tụi em hằng tháng, trong đó có danh sách nhân viên và các khóa học mà từng người phải học trong năm.
            """,
            """
            - Trả về DUY NHẤT một object JSON hợp lệ, không chữ nào ngoài JSON; ready = false; KHÔNG nhắc tới nút "Write Requirement".
            - Người dùng vừa tự nêu một nguồn dữ liệu nằm ngoài ứng dụng (HR xuất từ SAP, gửi qua hằng tháng) và file đã có ⇒ lượt này ĐƯỢC hỏi nguồn, ở góc nhìn nghiệp vụ: dữ liệu đó vào ứng dụng bằng đường nào (có người tải file lên / nhập tay / ứng dụng tự lấy về), và cập nhật khi nào (một lần, mỗi lần HR gửi bản mới, định kỳ hằng tháng).
            - TUYỆT ĐỐI KHÔNG hỏi cách hai hệ thống NỐI với nhau: không hỏi tích hợp qua API hay đọc thẳng database của SAP, không hỏi webhook, không hỏi đồng bộ real-time hay chạy lô, không hỏi định dạng file trao đổi hay lịch chạy job. Người dùng nghiệp vụ không trả lời được, và đó là việc của bước sinh tài liệu.
            - group của câu hỏi (nếu dùng questions) phải là "Dữ liệu / danh mục chính", chép đúng nhãn.
            - Câu hỏi này là câu ĐÓNG ⇒ phải kèm suggestions 2–5 mục ngắn, sát nhịp người dùng vừa kể (vd "Có người tải file lên", "Hằng tháng khi HR gửi bản mới").
            - KHÔNG hỏi lại nghĩa từng cột của file vừa gửi, và KHÔNG hỏi người dùng có cần tài khoản riêng để đăng nhập không.
            """);

        // Mặt kia của cùng ranh giới, và là chỗ luật "hỏi nguồn" quay ra cắn chính nó: orgUnit + nhân sự
        // đồng bộ tự động từ COMPAS cho MỌI ứng dụng trong nhà máy (organization-platform.v1.md), nên hỏi
        // nguồn/ai cập nhật cho hai danh mục đó là hỏi đúng điều ĐÃ CHỐT. Ca thật đã gặp trên màn hình: ba
        // lượt liền hỏi "ai quản lý danh sách orgUnit", "ai cập nhật thông tin nhân viên", "danh sách orgUnit
        // đưa vào ứng dụng bằng cách nào" — người dùng phải tự gõ vào ô "Ý khác" rằng app tự đồng bộ từ
        // COMPAS. Ai bấm chip cho xong thì tài liệu ghi một quy trình nhập tay không có thật, và POC dựng
        // một màn hình "Quản lý OrgUnit" đầy nút Thêm/Sửa/Xóa.
        Add(
            "Chat BA — không hỏi nguồn/người quản lý của orgUnit và nhân sự (đã đồng bộ từ COMPAS)",
            "BusinessAnalyst/requirement-chat.v4.md",
            """
            ## Nền tảng đã chốt của nhà máy (BẮT BUỘC — áp cho câu hỏi, phương án gợi ý và tài liệu)

            ### Dữ liệu tổ chức: orgUnit và nhân sự ĐỀU lấy từ hệ thống COMPAS

            Danh sách orgUnit (cây tổ chức, ai là HoD/manager) và danh sách nhân sự (họ tên, mã nhân viên, chức danh, thuộc orgUnit nào, quản lý trực tiếp là ai) của MỌI ứng dụng trong nhà máy đều được đồng bộ tự động từ hệ thống COMPAS. Ứng dụng tự lấy về; không ai tải file lên, không ai nhập tay, và không ứng dụng nào được sửa hai danh mục đó. Đây là điều ĐÃ CHỐT của sản phẩm: đừng hỏi ai quản lý/cập nhật chúng, đừng hỏi chúng vào ứng dụng bằng đường nào, và đừng dựng màn hình quản lý orgUnit/nhân viên. Thứ ứng dụng TỰ gắn thêm lên một orgUnit hay một con người (JD của orgUnit do ai soạn, ai duyệt…) thì vẫn phải hỏi như thường.

            ## Điều đã chốt
            - Ứng dụng quản lý JD (bản mô tả công việc) cho các vị trí trong nhà máy
            - Manager tạo JD cho vị trí thuộc orgUnit của mình

            Hội thoại trước đó:
            BA: Anh/chị kể giúp mình một lần gần nhất cần tới bản JD thì làm những bước nào.
            Người dùng: Manager vào chọn orgUnit của mình rồi tạo JD cho vị trí cần tuyển, xong gửi HRBP xem lại. JD duyệt rồi thì gán cho nhân viên đang giữ vị trí đó.
            """,
            """
            - Trả về DUY NHẤT một object JSON hợp lệ, không chữ nào ngoài JSON; ready = false; KHÔNG nhắc tới nút "Write Requirement".
            - TUYỆT ĐỐI KHÔNG hỏi ai quản lý / ai cập nhật danh sách orgUnit, ai quản lý thông tin nhân viên, danh sách orgUnit được đưa vào ứng dụng bằng cách nào, hay dữ liệu nhân sự cập nhật khi nào. Hai danh mục đó đồng bộ từ COMPAS — hỏi lại là hỏi đúng điều ĐÃ CHỐT.
            - TUYỆT ĐỐI KHÔNG đưa vào message/questions/suggestions các phương án không có thật cho hai danh mục đó: "HR", "HRBP", "HR và manager orgUnit", "Manager trực tiếp", "Có người tải file lên", "Nhập tay trong ứng dụng", "Ứng dụng tự lấy về".
            - KHÔNG đề xuất màn hình quản lý orgUnit / quản lý nhân viên / quản lý người dùng.
            - KHÔNG hỏi chuyện kỹ thuật của việc đồng bộ (API, webhook, đọc thẳng database COMPAS, lịch chạy job, tần suất).
            - ĐẠT khi lượt này đào đúng phần dữ liệu của CHÍNH ứng dụng hoặc một nhóm ★ chưa khai thác — vd JD gồm những thông tin gì, HRBP xem lại rồi có được sửa không, JD bị trả về thì manager làm gì, một vị trí có nhiều bản JD theo thời gian không.
            - Nếu message có câu ghi nhận, câu đó chỉ được chứa điều người dùng THẬT SỰ đã nói — KHÔNG chèn "đồng bộ từ COMPAS" vào như thể họ đã nói ra.
            """);

        // Xin file là lời nhờ HÀNH ĐỘNG, không phải câu hỏi: người dùng đọc xong thì đi tìm file, nên mọi thứ
        // khác trong lượt bị nuốt mất. Ca thật: BA vừa xin Master List vừa hỏi quy trình hiện tại và điểm đau
        // — người dùng đính kèm file rồi đáp đúng một dòng về điểm đau, còn CÁC BƯỚC của quy trình hiện tại
        // không bao giờ được kể, mà nhóm đó vẫn được tính là đã hỏi xong.
        Add(
            "Chat BA — xin file phải đứng một mình, không kèm câu hỏi khai thác",
            "BusinessAnalyst/requirement-chat.v4.md",
            """
            Người dùng: Đây là app để lên kế hoạch các lớp học cần tổ chức cho nhân viên trong 1 năm, gồm khóa bắt buộc và khóa tự chọn. Mỗi năm bên HR nhận được file excel chứa thông tin tất cả nhân viên cùng các khóa học mà từng người phải học trong năm, tụi mình gọi là Master List. Assistant sẽ tạo project mới rồi upload file đó lên, dựa vào đó tính ra số lớp cần mở cho mỗi khóa.
            """,
            """
            - Trả về DUY NHẤT một object JSON hợp lệ; ready = false; KHÔNG nhắc tới nút "Write Requirement".
            - Người dùng vừa nhắc tới file Master List đang dùng ⇒ lượt này PHẢI xin họ đính kèm file đó ngay (nút 📎 dưới ô nhập / kéo thả vào khung chat). TRƯỢT nếu để dành việc xin file sang lượt sau.
            - Lượt xin file phải ĐỨNG MỘT MÌNH: message KHÔNG được chứa thêm câu hỏi khai thác nào. TRƯỢT nếu vừa xin file vừa hỏi quy trình hiện tại đang làm thế nào, điểm khó chịu nhất là gì, ai dùng ứng dụng, hay bất kỳ câu hỏi nghiệp vụ nào khác — người dùng đi tìm file thì vế còn lại rơi mất, nhưng bản đồ bao phủ vẫn tính là đã hỏi.
            - questions là mảng rỗng (không phải lượt hỏi gộp).
            - Không có câu hỏi đóng nào để bấm ⇒ suggestions là mảng RỖNG và openEnded = true. TRƯỢT nếu kèm chip kiểu ["Đang theo dõi bằng Excel", "Chưa có quy trình cố định"].
            - Nếu message có câu ghi nhận thì chỉ được chứa điều người dùng THẬT SỰ đã nói, và KHÔNG nhét "Đồng Nai" hay tên department từ khối ngữ cảnh hệ thống vào đó.
            """);

        // BẢN KỂ CỦA BẢNG KHÔNG PHẢI LỜI NGƯỜI DÙNG. Ca thật (JD Library 1): BA điền ô mô tả của đối tượng
        // JD là "Mô tả công việc được Manager tạo, kiểm tra, verify và approve…", trong khi chính người dùng
        // đã kể ở lượt 7 và tự tay rà ở BẢNG LUỒNG rằng HRBP verify rồi HoD của Manager approve. Người dùng
        // đọc ô đó như một cái nhãn nên bấm gửi luôn — và lượt kế BA hỏi "luồng nào đúng với thực tế ạ?",
        // tức bắt họ phân xử một mâu thuẫn giữa lời họ và lời BA. Ba tầng thiệt hại: lượt gỡ mâu thuẫn phải
        // đứng MỘT MÌNH nên cả lượt bị đốt cho việc không có thật; mục tồn đọng sinh ra từ đó hạ ba dòng bản
        // đồ xuống [MỘT PHẦN] và KHÓA cổng "Write Requirement" ở đúng lượt mọi nhóm vừa đủ; và nếu người dùng
        // chọn nhầm vế của BA thì luồng bốn mắt do chính họ kể bị lật. Cơ chế đã bỏ ô mô tả khỏi bản kể và
        // gắn nhãn xuất xứ cho nó trong khối ngữ cảnh (EntityMapBuilder); scenario này chấm phần còn lại —
        // BA có thôi lấy câu của mình làm một vế mâu thuẫn hay không.
        Add(
            "Chat BA — mô tả BA tự đặt trong bảng KHÔNG phải một vế mâu thuẫn",
            "BusinessAnalyst/requirement-chat.v4.md",
            """
            ## Bản đồ bao phủ yêu cầu (trạng thái khai thác từng nhóm thông tin — dùng để chọn câu hỏi kế tiếp)
            Nhóm đã [RÕ]: KHÔNG hỏi lại. Nhóm [MỘT PHẦN]: chỉ hỏi ĐÚNG phần ghi sau "còn thiếu:", KHÔNG phát lại câu hỏi mở đầu của nhóm đó (người dùng đã trả lời phần còn lại rồi).
            - ★ Đối tượng người dùng & vai trò: [RÕ] Manager tạo/gán JD; HRBP verify; HoD approve; Admin quản lý Skill.
            - ★ Chức năng & luồng nghiệp vụ chính: [RÕ] Manager tạo JD → submit → HRBP verify → HoD approve → available.
            - Vòng đời & trạng thái: [RÕ] Đang chỉnh sửa → Chờ HRBP verify → Chờ HoD approve → Available.
            - Quy tắc nghiệp vụ & ràng buộc: [MỘT PHẦN] Mỗi JD có 5 Responsibility, mỗi dòng có tỷ lệ. còn thiếu: tổng tỷ lệ của 5 Responsibility có phải bằng 100% không.

            ## Bảng luồng nghiệp vụ người dùng ĐÃ CHỐT (tự tay rà từng bước — coi như điều đã biết)
            KHÔNG hỏi lại thứ tự bước, ai làm bước nào, hay kết quả của bước; KHÔNG dựng yêu cầu trái với các luồng này.

            --- Bảng luồng đã được NGƯỜI DÙNG CHỐT ---
            * Tạo và phê duyệt JD [luồng chính] — Manager
              1. Manager: Submit JD ⇒ JD được chuyển cho HRBP phòng Nhân sự verify
              2. HRBP phòng Nhân sự: Verify JD ⇒ JD được chuyển cho HoD của Manager approve
              3. HoD của Manager: Approve JD ⇒ JD trở thành available để assign cho nhân viên

            ## Bảng đối tượng nghiệp vụ người dùng ĐÃ CHỐT (coi như điều đã biết)
            KHÔNG hỏi lại thông tin nào cần lưu hay các trạng thái đi qua. Bảng này chốt CẤU TRÚC, không chốt RÀNG BUỘC.

            --- Bảng đối tượng nghiệp vụ đã được NGƯỜI DÙNG CHỐT (đừng hỏi lại) ---
            Mỗi đối tượng: thông tin cần lưu, rồi vòng đời trạng thái kèm ĐIỀU KIỆN chuyển vào.
            Dòng "mô tả" là câu CHÍNH BẠN đặt lúc bày bảng, KHÔNG phải lời người dùng: đừng trích nó làm bằng chứng, đừng lấy nó làm một vế mâu thuẫn với điều họ nói, và thấy nó lệch với hội thoại thì tự sửa im lặng chứ không hỏi.
            * JD
              - mô tả (BA tự đặt, chưa ai rà): Mô tả công việc được Manager tạo, kiểm tra, verify và approve trước khi dùng để gán cho nhân viên
              - thông tin: Job Title [bắt buộc · chọn 1 giá trị · ứng dụng tự quản lý danh mục]; Degree [bắt buộc · chọn nhiều giá trị · ứng dụng tự quản lý danh mục]
              - trạng thái "Chờ HRBP verify" khi Manager submit JD và JD đạt kiểm tra tự động
            * Responsibility — mỗi JD có 5 dòng
              - thông tin: Task [bắt buộc]; Tỷ lệ [bắt buộc · nhập số]

            Hội thoại trước đó:
            BA: Các nhóm thông tin chính đã đủ. Anh/chị rà soát bảng đối tượng bên dưới, sau đó bấm "Gửi bảng đối tượng" giúp mình nhé.

            Mình đã rà bảng đối tượng nghiệp vụ:

            JD:
            - thông tin cần lưu: Job Title [bắt buộc · chọn 1 giá trị · ứng dụng tự quản lý danh mục]; Degree [bắt buộc · chọn nhiều giá trị · ứng dụng tự quản lý danh mục]
            - "Chờ HRBP verify" khi Manager submit JD và JD đạt kiểm tra tự động

            Responsibility:
            - là các DÒNG của "JD" — mỗi "JD" có 5 dòng như thế
            - thông tin cần lưu: Task [bắt buộc]; Tỷ lệ [bắt buộc · nhập số]
            """,
            """
            - Trả về DUY NHẤT một object JSON hợp lệ; ready = false; KHÔNG nhắc tới nút "Write Requirement"; mọi trường bảng (flowMap, screenScopeMap, entityMap, reportMap, permissionMatrix, notificationMap) là mảng RỖNG.
            - TRƯỢT NẶNG nếu lượt này nêu một mâu thuẫn về ai verify / ai approve JD — vd "trong bảng luồng anh/chị đã chốt HRBP verify và HoD approve, nhưng phần mô tả JD lại ghi Manager thực hiện verify và approve; luồng nào đúng với thực tế ạ?". Vế "Manager verify và approve" đến từ ô mô tả do CHÍNH BA điền sẵn, không phải lời người dùng: hai vế không cùng nguồn nên không có mâu thuẫn nào, và câu mô tả lệch là lỗi câu chữ của BA để tự sửa im lặng.
            - TRƯỢT nếu hỏi lại bất kỳ điều gì thuộc bốn dòng bản đồ đang [RÕ]: ai tạo JD, ai verify, ai approve, JD đi qua những trạng thái nào, thông tin nào cần lưu cho JD.
            - ĐẠT khi lượt này đi tiếp bằng đúng phần "còn thiếu" của nhóm «Quy tắc nghiệp vụ & ràng buộc»: tổng tỷ lệ của 5 Responsibility trong một JD (bằng 100% hay không), tốt nhất là dựng sẵn một ví dụ số cụ thể rồi xin chốt.
            - Câu hỏi ĐÓNG ⇒ suggestions có 2–5 phương án thật; không có chip kiểu "Quy tắc khác" / "Ý khác".
            """);

        // ================= BusinessAnalyst/source-ack.v3.md + source-readback.v1.md =================
        // Đường vào tài liệu nguồn là CỬA VÀO của mọi thứ phía sau: đọc sai ở đây thì cái sai được người
        // dùng bấm "Đúng rồi" đóng dấu, rồi chảy thẳng vào Product Brief. Với BẢNG TÍNH nó đi HAI lượt —
        // lượt upload chỉ bày BẢNG CỘT kèm lời giới thiệu ngắn, lượt sau (khi người dùng đã chốt cột) mới
        // kể lại cách hiểu file — nên mỗi lượt có scenario riêng, và cái bẫy của hai lượt ngược nhau: lượt
        // đầu TRƯỢT vì kể quá nhiều, lượt sau TRƯỢT vì kể quá ít.
        //
        // UserInput mô phỏng đúng khối mà runtime dựng: SourceContextBuilder ("=== TÀI LIỆU NGUỒN…",
        // "[Nguồn: …]", khối bảng cột đã chốt) + phần text bảng tính do SpreadsheetTextExtractor bóc ra
        // ("#### Thống kê cột" trên cả bảng, rồi các dòng mẫu) + khối "## LƯỢT NÀY:" do BAChatService chọn.

        Add(
            "Source-ack — bảng cột: mỗi cột một dòng, ý nghĩa điền sẵn; message CHƯA kể lại file",
            "BusinessAnalyst/source-ack.v3.md",
            """
            ## LƯỢT NÀY: CHỐT PHẠM VI CỘT (bắt buộc)
            File bảng tính CHƯA chốt bảng cột: LearningPlan.xlsx.
            Với các file đó, lượt này CHỈ làm hai việc: điền `columns` phủ đủ mọi cột của file (ý nghĩa viết sẵn, tích sẵn cột nghiệp vụ), và viết `message` NGẮN — tối đa năm câu: file này là gì, quy mô thật, rồi mời người dùng rà bảng bên dưới và bấm "Gửi bảng cột".
            TUYỆT ĐỐI KHÔNG kể lại chi tiết từng cột và KHÔNG viết cụm "Chỗ chưa chắc" cho các file đó: bản đọc lại của bảng tính là lượt SAU, sau khi người dùng chốt xong cột.

            ## PHẠM VI KỂ LẠI CỦA LƯỢT NÀY
            File người dùng VỪA GỬI ở lượt này: LearningPlan.xlsx. Chỉ những file này mới là thứ lượt này phải kể lại và xin xác nhận.

            Tôi vừa đính kèm: LearningPlan.xlsx. Bạn đọc kỹ và kể lại cụ thể những gì rút được từ đó để tôi xác nhận nhé.

            === TÀI LIỆU NGUỒN DO NGƯỜI DÙNG CUNG CẤP (tham khảo khi phân tích yêu cầu) ===

            [Nguồn: LearningPlan.xlsx]
            ### Sheet: Sheet1
            Tổng: 262 dòng dữ liệu, 10 cột.

            #### Thống kê cột (trên 262 dòng)
            - Global ID: có giá trị 262/262 · 13 giá trị phân biệt · hay gặp nhất: 10151719 (60), 10540911 (54)
            - Ten dem: TRỐNG ở toàn bộ 262 dòng
            - Active User: có giá trị 262/262 · CHỈ MỘT giá trị duy nhất: Yes
            - Organization: có giá trị 262/262 · ĐỦ 3 giá trị: HcP/MSE2 (120), PS/QMM3-HcP (92), HcP/PC (50)
            - Item Title: có giá trị 257/262 · 136 giá trị phân biệt · hay gặp nhất: [QM-QM001] Quality at Bosch-B (8)
            - Item Type: có giá trị 257/262 · ĐỦ 5 giá trị: WBT (114), COURSE (91), DOC (41), WEBINAR (9), EUNIVERSITY (2)
            - Assignment Type: có giá trị 136/262 · ĐỦ 3 giá trị: REQ (78), MAN (53), OPT (5)
            - Required Date: có giá trị 12/262 · ĐỦ 7 giá trị: 31/Dec/2023 (4), 31/Oct/2023 (3), 15/Jan/2026 (1), 30/Apr/2023 (1), 30/Jun/2023 (1), 31/Dec/2025 (1), 31/Jul/2023 (1)
            - Days Rem: có giá trị 12/262 · ĐỦ 8 giá trị: 0 (5), 61 (1), 92 (1), 184 (1), 245 (1), 976 (1), 991 (1), 1204 (1)
            - Revision Number: có giá trị 262/262 · ĐỦ 3 giá trị: 1 (218), 3 (21), 2 (18)

            #### Dòng dữ liệu (2 dòng đầu làm mẫu — chỉ để thấy hình dạng dữ liệu; ĐỪNG suy ra danh mục của cột từ đây, dùng "Thống kê cột" bên trên)
            Global ID | Ten dem | Active User | Organization | Item Title | Item Type | Assignment Type | Required Date | Days Rem | Revision Number
            11054396 |  | Yes | HcP/MSE2 | [QM-QM001] Quality at Bosch-B | DOC | REQ |  |  | 1
            11227524 |  | Yes | PS/QMM3-HcP | [LG-ATL] Compliance - Antitrust Law-A | COURSE | MAN |  |  | 1
            """,
            """
            - Trả về DUY NHẤT một object JSON hợp lệ; ready = false; message KHÔNG nhắc tới nút "Write Requirement".
            - columns có ĐÚNG 10 dòng, mỗi cột của file một dòng, fileName = "LearningPlan.xlsx" và column chép đúng tên cột trong hàng tiêu đề. TRƯỢT nếu bỏ sót cột nào, hoặc bịa thêm cột không có trong hàng tiêu đề.
            - Mọi dòng đều phải có meaning ĐIỀN SẴN dạng chú giải ngắn (vd "mã số nhân viên", "tên khóa học", "REQ/MAN là bắt buộc, OPT là tự chọn"). TRƯỢT nếu meaning để trống ở phần lớn các dòng, hoặc viết thành câu hỏi ("cột này là gì?").
            - meaning của Assignment Type phải gọi đủ CẢ 3 giá trị kèm cả OPT (chỉ có trong "Thống kê cột", không xuất hiện ở dòng mẫu) — đây là cột mã hóa "khóa bắt buộc / khóa tự chọn".
            - meaning của "Ten dem" và "Active User" phải nói thẳng cột đang trống toàn bộ / chỉ có một giá trị, và used = false cho cả hai.
            - used = true cho Global ID, Organization, Item Title, Item Type, Assignment Type, Required Date; used = false cho Revision Number.
            - used = false cho "Days Rem": nó là cột DẪN XUẤT (số ngày còn lại tính sẵn từ Required Date lúc hệ cũ xuất file, có giá trị ở đúng 12 dòng như Required Date), app mới tính lại được. TRƯỢT nếu tích nó là cột của ứng dụng mới.
            - message phải NGẮN (tối đa năm câu, viết liền mạch): file này là gì, quy mô thật (262 dòng nhưng chỉ 13 người), rồi mời rà bảng và bấm "Gửi bảng cột".
            - TRƯỢT nếu message có gạch đầu dòng kể lại nội dung file, hoặc đi qua từng cột, hoặc liệt kê phân bố giá trị của các cột — bảng cột ngay bên dưới đã chở đúng nội dung đó ở dạng người dùng SỬA ĐƯỢC.
            - TRƯỢT nếu message có cụm "Chỗ chưa chắc": lượt này chưa biết người dùng dùng cột nào, nên mỗi mục ở đó là một việc tồn dựng trên cột họ sắp bỏ tích.
            - TRƯỢT nếu message kết bằng câu hỏi đóng kiểu "mình hiểu vậy đúng chưa ạ": lượt này hệ thống ẩn hai chip đi, nút duy nhất trên màn hình là "Gửi bảng cột".
            """);

        // PHẠM VI KỂ LẠI. Lượt đọc file nạp lại TOÀN BỘ nguồn của project (nguồn cũ là thứ duy nhất để đối
        // chiếu), nhưng chỉ file VỪA GỬI mới phải kể lại. Ca thật: người dùng chốt bảng cột cho file Excel ở
        // đầu buổi, mười mấy lượt sau gửi một ảnh chụp biểu mẫu để trả lời một câu hỏi — bản đọc lại mở đầu
        // bằng gần nửa số dòng nói lại đúng bộ cột họ đã tích tay, rồi mới tới cái ảnh. Cơ chế đã gọi đích
        // danh file vừa gửi; scenario này chấm xem model có tuân hay không, kể cả khi nguồn cũ nằm ngay đó.
        Add(
            "Source-ack — chỉ kể lại file VỪA GỬI, nguồn cũ chỉ để đối chiếu",
            "BusinessAnalyst/source-ack.v3.md",
            """
            ## LƯỢT NÀY: BẢN ĐỌC LẠI
            Không có file bảng tính nào đang chờ chốt bảng cột ⇒ `columns` là mảng RỖNG, và lượt này là bản đọc lại đầy đủ: kể lại thứ bạn đọc được, nêu cụm "Chỗ chưa chắc", kết bằng câu hỏi đóng để người dùng bấm một trong hai chip.

            ## PHẠM VI KỂ LẠI CỦA LƯỢT NÀY
            File người dùng VỪA GỬI ở lượt này: bieu-mau-lop.png. Chỉ những file này mới là thứ lượt này phải kể lại và xin xác nhận.
            Các nguồn còn lại — LearningPlan.xlsx — đã gửi từ TRƯỚC và người dùng đã xác nhận cách bạn hiểu chúng rồi; chúng đính kèm ở đây CHỈ để bạn đối chiếu. TUYỆT ĐỐI KHÔNG kể lại chúng: không mô tả lại nội dung/cột/quy mô của chúng, không dựng cụm "Chỗ chưa chắc" cho riêng chúng.
            Được phép nhắc tên chúng đúng MỘT trường hợp: nêu một điểm chưa rõ nằm ở chỗ NỐI giữa file vừa gửi và chúng (dữ liệu bên này lấy từ bên kia hay nhập tay?).

            Tôi vừa đính kèm: bieu-mau-lop.png, kèm ghi chú của tôi: "tôi có gửi cho bạn 1 hình ảnh những field cần có của 1 lớp học". Bạn đọc kỹ và kể lại cụ thể những gì rút được từ đó để tôi xác nhận nhé.

            === TÀI LIỆU NGUỒN DO NGƯỜI DÙNG CUNG CẤP (tham khảo khi phân tích yêu cầu) ===

            [Nguồn: LearningPlan.xlsx]
            ### Sheet: Sheet1
            Tổng: 262 dòng dữ liệu, 4 cột.

            #### Thống kê cột (trên 262 dòng)
            - Global ID: có giá trị 262/262 · 13 giá trị phân biệt · hay gặp nhất: 10151719 (60), 10540911 (54)
            - Organization: có giá trị 262/262 · ĐỦ 3 giá trị: HcP/MSE2 (120), PS/QMM3-HcP (92), HcP/PC (50)
            - Item ID: có giá trị 257/262 · 134 giá trị phân biệt
            - Item Title: có giá trị 257/262 · 136 giá trị phân biệt · hay gặp nhất: [QM-QM001] Quality at Bosch-B (8)

            --- Bảng cột của "LearningPlan.xlsx" đã được NGƯỜI DÙNG CHỐT (đừng hỏi lại nghĩa các cột này) ---
            Cột DÙNG trong ứng dụng mới:
            - Global ID: mã số nhân viên
            - Organization: đơn vị của nhân viên
            - Item ID: mã khóa học
            - Item Title: tên khóa học

            [Nguồn: bieu-mau-lop.png] (kèm 1 ảnh dưới dạng ẢNH — xem nội dung ảnh đính kèm)
            """,
            """
            - Trả về DUY NHẤT một object JSON hợp lệ; ready = false; columns là mảng RỖNG; message KHÔNG nhắc tới nút "Write Requirement".
            - message chỉ kể lại bieu-mau-lop.png — biểu mẫu vừa gửi. TRƯỢT nếu có một cụm riêng kể lại LearningPlan.xlsx: quy mô 262 dòng / 13 nhân viên, danh sách cột đã chốt, hay danh sách cột không dùng. Người dùng đã tích tay bảng cột đó ở lượt trước; kể lại là bắt họ duyệt lần thứ hai đúng thứ vừa duyệt, còn thứ họ thật sự vừa gửi bị đẩy xuống nửa dưới của lượt.
            - TRƯỢT nếu message mở đầu bằng kiểu "Mình đọc được 2 nguồn: …" rồi tách hai cụm ngang hàng cho hai file.
            - Vẫn ĐƯỢC nhắc tên LearningPlan.xlsx ở "Chỗ chưa chắc" khi điểm chưa rõ nằm ở chỗ NỐI giữa hai nguồn (biểu mẫu lấy danh sách người học từ file kia hay người dùng tự nhập) — đó là câu hỏi chỉ lộ ra khi đặt hai nguồn cạnh nhau.
            - Ảnh KHÔNG đọc được nội dung ⇒ nói thẳng là chưa đọc được gì và mời người dùng mô tả bằng lời; TUYỆT ĐỐI không bịa tên trường của biểu mẫu.
            - Kết bằng câu hỏi đóng xin xác nhận, suggestions có ĐÚNG 2 đáp án (một xác nhận, một đính chính).
            """);

        // NỬA SAU: người dùng đã chốt bảng cột, giờ mới tới lượt BA kể lại cách hiểu file. Bẫy ngược lại
        // với lượt trên — kể quá ít, hoặc kể lại chính những cột người dùng vừa bỏ tích.
        Add(
            "Source-readback — kể lại file theo ĐÚNG bộ cột đã chốt, không đụng cột đã bỏ",
            "BusinessAnalyst/source-readback.v1.md",
            """
            Mình đã rà xong bảng cột.

            Trong file LearningPlan.xlsx, các cột mình thật sự dùng khi làm việc:
            - Global ID: mã số nhân viên
            - Organization: đơn vị của nhân viên
            - Item Title: tên khóa học
            - Item Type: hình thức học
            - Assignment Type: REQ/MAN là bắt buộc, OPT là tự chọn
            - Required Date: hạn phải hoàn thành
            Các cột còn lại mình không dùng, đó là dữ liệu của hệ thống cũ: Ten dem, Active User, Days Rem, Revision Number

            === TÀI LIỆU NGUỒN DO NGƯỜI DÙNG CUNG CẤP (tham khảo khi phân tích yêu cầu) ===

            [Nguồn: LearningPlan.xlsx]
            ### Sheet: Sheet1
            Tổng: 262 dòng dữ liệu, 10 cột.

            #### Thống kê cột (trên 262 dòng)
            - Global ID: có giá trị 262/262 · 13 giá trị phân biệt · hay gặp nhất: 10151719 (60), 10540911 (54)
            - Ten dem: TRỐNG ở toàn bộ 262 dòng
            - Active User: có giá trị 262/262 · CHỈ MỘT giá trị duy nhất: Yes
            - Organization: có giá trị 262/262 · ĐỦ 3 giá trị: HcP/MSE2 (120), PS/QMM3-HcP (92), HcP/PC (50)
            - Item Title: có giá trị 257/262 · 136 giá trị phân biệt · hay gặp nhất: [QM-QM001] Quality at Bosch-B (8)
            - Item Type: có giá trị 257/262 · ĐỦ 5 giá trị: WBT (114), COURSE (91), DOC (41), WEBINAR (9), EUNIVERSITY (2)
            - Assignment Type: có giá trị 136/262 · ĐỦ 3 giá trị: REQ (78), MAN (53), OPT (5)
            - Required Date: có giá trị 12/262 · ĐỦ 7 giá trị: 31/Dec/2023 (4), 31/Oct/2023 (3), 15/Jan/2026 (1), 30/Apr/2023 (1), 30/Jun/2023 (1), 31/Dec/2025 (1), 31/Jul/2023 (1)
            - Days Rem: có giá trị 12/262 · ĐỦ 8 giá trị: 0 (5), 61 (1), 92 (1), 184 (1), 245 (1), 976 (1), 991 (1), 1204 (1)
            - Revision Number: có giá trị 262/262 · ĐỦ 3 giá trị: 1 (218), 3 (21), 2 (18)

            #### Dòng dữ liệu (2 dòng đầu làm mẫu — chỉ để thấy hình dạng dữ liệu; ĐỪNG suy ra danh mục của cột từ đây, dùng "Thống kê cột" bên trên)
            Global ID | Ten dem | Active User | Organization | Item Title | Item Type | Assignment Type | Required Date | Days Rem | Revision Number
            11054396 |  | Yes | HcP/MSE2 | [QM-QM001] Quality at Bosch-B | DOC | REQ |  |  | 1
            11227524 |  | Yes | PS/QMM3-HcP | [LG-ATL] Compliance - Antitrust Law-A | COURSE | MAN |  |  | 1

            --- Bảng cột của "LearningPlan.xlsx" đã được NGƯỜI DÙNG CHỐT (đừng hỏi lại nghĩa các cột này) ---
            Cột DÙNG trong ứng dụng mới:
            - Global ID: mã số nhân viên
            - Organization: đơn vị của nhân viên
            - Item Title: tên khóa học
            - Item Type: hình thức học
            - Assignment Type: REQ/MAN là bắt buộc, OPT là tự chọn
            - Required Date: hạn phải hoàn thành
            Cột KHÔNG dùng — dữ liệu của hệ thống cũ. Đừng đưa vào yêu cầu, màn hình hay dữ liệu mẫu, và đừng hỏi thêm về chúng: Ten dem, Active User, Days Rem, Revision Number
            """,
            """
            - Trả về DUY NHẤT một object JSON hợp lệ; ready = false; message KHÔNG nhắc tới nút "Write Requirement".
            - message phải là bản KỂ LẠI thật sự: file này kể chuyện gì, và quy mô thật (262 dòng nhưng chỉ 13 người).
            - message phải nêu ĐỦ CẢ 3 giá trị của Assignment Type, gồm cả OPT — TRƯỢT nếu chỉ kể REQ và MAN (OPT chỉ có trong "Thống kê cột", không xuất hiện ở các dòng mẫu).
            - message phải nêu ĐỦ CẢ 5 giá trị của Item Type, gồm cả WEBINAR và EUNIVERSITY.
            - KHÔNG được kết luận Required Date "để trống"/"chưa có dữ liệu": thống kê ghi rõ 12/262 dòng có giá trị.
            - TRƯỢT nếu message nhắc tới Days Rem, Revision Number, Ten dem hay Active User: người dùng vừa bỏ tích chúng, nhắc lại là mở lại thứ họ vừa đóng.
            - TRƯỢT nếu có bất kỳ mục nào hỏi lại phạm vi cột ("cột này có nên đưa vào ứng dụng mới không").
            - Có cụm "Chỗ chưa chắc" nêu điểm còn mờ trong các cột ĐÃ TÍCH, kèm cách hiểu của BA để người dùng gật/lắc; chỉ NÊU RA, không hỏi thành câu hỏi bắt trả lời ngay.
            - Kết bằng câu hỏi đóng xin xác nhận, suggestions có ĐÚNG 2 đáp án (một xác nhận, một đính chính), questions là mảng RỖNG.
            """);

        Add(
            "Source-readback — đối chiếu file với điều người dùng đã kể (thiếu gì so với lời kể)",
            "BusinessAnalyst/source-readback.v1.md",
            """
            Người dùng đã nói lúc đầu: "đây là file Master List — danh sách nhân viên và các khóa học họ phải học trong năm, tụi em dựa vào đó để tính số lớp cần mở".

            Mình đã rà xong bảng cột.

            Trong file MasterList.xlsx, các cột mình thật sự dùng khi làm việc:
            - Global ID: mã số nhân viên
            - Organization: đơn vị của nhân viên
            - Item Title: tên khóa học
            - Item Status: trạng thái
            - Complete Date: ngày hoàn thành

            === TÀI LIỆU NGUỒN DO NGƯỜI DÙNG CUNG CẤP (tham khảo khi phân tích yêu cầu) ===

            [Nguồn: MasterList.xlsx]
            ### Sheet: Sheet1
            Tổng: 262 dòng dữ liệu, 5 cột.

            #### Thống kê cột (trên 262 dòng)
            - Global ID: có giá trị 262/262 · 13 giá trị phân biệt · hay gặp nhất: 10151719 (60), 10540911 (54), 10150054 (50), 10481461 (49), 10504807 (38)
            - Organization: có giá trị 262/262 · ĐỦ 3 giá trị: HcP/MSE2 (120), PS/QMM3-HcP (92), HcP/PC (50)
            - Item Title: có giá trị 257/262 · 136 giá trị phân biệt · hay gặp nhất: [QM-QM001] Quality at Bosch-B (8), [LG-ATL] Compliance - Antitrust Law-A (7)
            - Item Status: có giá trị 262/262 · ĐỦ 2 giá trị: Active (219), Inactive (43)
            - Complete Date: có giá trị 219/262 · 161 giá trị phân biệt · hay gặp nhất: 44707 (6), 44700 (5), 44683 (4)

            #### Dòng dữ liệu (2 dòng đầu làm mẫu — chỉ để thấy hình dạng dữ liệu; ĐỪNG suy ra danh mục của cột từ đây, dùng "Thống kê cột" bên trên)
            Global ID | Organization | Item Title | Item Status | Complete Date
            11054396 | HcP/MSE2 | [QM-QM001] Quality at Bosch-B | Active | 44330
            11227524 | PS/QMM3-HcP | [LG-ATL] Compliance - Antitrust Law-A | Active | 42506

            --- Bảng cột của "MasterList.xlsx" đã được NGƯỜI DÙNG CHỐT (đừng hỏi lại nghĩa các cột này) ---
            Cột DÙNG trong ứng dụng mới:
            - Global ID: mã số nhân viên
            - Organization: đơn vị của nhân viên
            - Item Title: tên khóa học
            - Item Status: trạng thái
            - Complete Date: ngày hoàn thành
            """,
            """
            - Trả về DUY NHẤT một object JSON hợp lệ; ready = false; suggestions có ĐÚNG 2 đáp án (một xác nhận, một đính chính); questions là mảng RỖNG.
            - Cụm "Chỗ chưa chắc" PHẢI nêu được rằng file không có cột nào chở "số lượng lớp cần mở" hay "nhu cầu học" — thứ người dùng vừa nói là mục đích dùng file.
            - PHẢI nêu chỗ lệch giữa file và lời kể: người dùng gọi đây là danh sách khóa học PHẢI HỌC trong năm, nhưng cột Complete Date (219/262 dòng đã có ngày hoàn thành) cho thấy đây là dữ liệu ĐÃ HỌC.
            - PHẢI nối hai cột lại với nhau: Item Status có Active (219) và Complete Date có giá trị ở đúng 219/262 dòng, nên "Active" nhiều khả năng nghĩa là ĐÃ HỌC XONG chứ không phải "nội dung còn hiệu lực" — nêu kèm phỏng đoán đó để người dùng gật/lắc. TRƯỢT nếu chỉ kể rời hai con số, hoặc chốt Item Status là "trạng thái nội dung" mà không nhắc tới chỗ trùng khít này.
            - PHẢI nêu quy mô đáng ngờ: 262 dòng nhưng chỉ 13 người, trong khi người dùng mô tả đây là danh sách nhân viên của cả đơn vị.
            - Complete Date dạng số (44330, 42506): PHẢI tự quy đổi thành ngày kiểu Excel rồi nêu kèm cách hiểu để người dùng xác nhận; TRƯỢT nếu để thành một câu hỏi trống bắt người dùng nghiệp vụ giải thích định dạng.
            - KHÔNG bịa thêm cột hay quy tắc không có trong tài liệu.
            """);

        Add(
            "Chat BA — hỏi về cột file: gom một lượt, đề xuất cách hiểu để chốt, không giải nghĩa cả bảng",
            "BusinessAnalyst/requirement-chat.v4.md",
            """
            ## Điều đã chốt
            - Ứng dụng lập kế hoạch lớp học cả năm cho nhân viên, gồm khóa bắt buộc và khóa tự chọn
            - Các vai trò dùng ứng dụng: nhân viên, HR – Đào tạo, Manager orgUnit, Quản trị ứng dụng

            ## Điểm cần làm rõ còn tồn đọng
            - Assignment Type: mình hiểu REQ và MAN đều là khóa bắt buộc (78 và 53 dòng), OPT là khóa tự chọn (5 dòng) — chưa được xác nhận
            - Curriculum ID xuất hiện ở 139/262 dòng, chưa rõ là mã chương trình đào tạo hay mã nhóm khóa học
            - Revision Number và Preferred Time zone trông như thông tin của hệ thống đang dùng chứ không phải thứ ứng dụng mới cần quản lý

            ## Hội thoại
            BA: Mình đã đọc file KeHoachDaoTao.xlsx và kể lại nội dung, anh/chị đã xác nhận là đúng.
            Người dùng: Đúng rồi
            """,
            """
            - Trả về DUY NHẤT một object JSON hợp lệ; ready = false; KHÔNG nhắc tới nút "Write Requirement".
            - Phải hỏi các điểm tồn đọng về cột, KHÔNG mở một nhóm mới trong bản đồ bao phủ.
            - Câu hỏi về Assignment Type phải là ĐỀ XUẤT cách hiểu để người dùng chốt (nêu REQ/MAN là bắt buộc, OPT là tự chọn) kèm gợi ý dạng xác nhận/đính chính — TRƯỢT nếu chỉ hỏi trống "Assignment Type nghĩa là gì?".
            - TUYỆT ĐỐI KHÔNG hỏi giải nghĩa những cột đã tự nói ra được (Last Name, Item Title, Complete Date) và không liệt kê cả bảng ra để xin giải nghĩa từng cột.
            - Nếu hỏi từ 2 câu trở lên thì dùng questions với suggestions riêng cho từng câu, và suggestions ở cấp ngoài phải rỗng.
            - Tối đa 4 câu trong lượt này.
            """);

        // PHẠM VI CỘT nay được chốt bằng BẢNG CỘT ngay ở lượt đọc file, và kết quả đi vào ngữ cảnh dưới
        // tiêu đề "Bảng cột của … đã được NGƯỜI DÙNG CHỐT". Rủi ro còn lại không phải "BA quên hỏi" mà là
        // NGƯỢC LẠI: BA hỏi lại đúng thứ người dùng vừa ngồi tích từng dòng — cách nhanh nhất để cả bảng
        // trông như một màn bấm vô ích.
        Add(
            "Chat BA — bảng cột đã chốt: không hỏi lại nghĩa cột, không hỏi lại phạm vi cột",
            "BusinessAnalyst/requirement-chat.v4.md",
            """
            ## Tài liệu nguồn
            [Nguồn: KeHoachDaoTao.xlsx]

            --- Bảng cột của "KeHoachDaoTao.xlsx" đã được NGƯỜI DÙNG CHỐT (đừng hỏi lại nghĩa các cột này) ---
            Cột DÙNG trong ứng dụng mới:
            - Global ID: mã số nhân viên global
            - Organization: tên orgUnit của nhân viên
            - Item Title: tên lớp học
            - Assignment Type: REQ/MAN là khóa bắt buộc, OPT là khóa tự chọn
            - Required Date: hạn phải hoàn thành khóa học
            Cột KHÔNG dùng — dữ liệu của hệ thống cũ. Đừng đưa vào yêu cầu, màn hình hay dữ liệu mẫu, và đừng hỏi thêm về chúng: Active User, Last Name, First Name, Item ID, Revision Date, Revision Number, Item Status, Preferred Time zone, Curriculum ID, Complete Date

            ## Điều đã chốt
            - Ứng dụng lập kế hoạch lớp học cả năm cho nhân viên, gồm khóa bắt buộc và khóa tự chọn
            - Các vai trò dùng ứng dụng: nhân viên, HR – Đào tạo, Manager orgUnit, Quản trị ứng dụng

            ## Hội thoại
            BA: Mình đã đọc file KeHoachDaoTao.xlsx và kể lại nội dung, anh/chị đã xác nhận là đúng.
            Người dùng: Trong file KeHoachDaoTao.xlsx, các cột mình thật sự dùng khi làm việc: Global ID, Organization, Item Title, Assignment Type, Required Date. Các cột còn lại mình không dùng, đó là dữ liệu của hệ thống cũ.
            """,
            """
            - Trả về DUY NHẤT một object JSON hợp lệ; ready = false.
            - TRƯỢT nếu hỏi lại nghĩa của bất kỳ cột nào đã có mô tả trong bảng cột đã chốt (Global ID, Organization, Item Title, Assignment Type, Required Date).
            - TRƯỢT nếu hỏi lại phạm vi cột dưới bất kỳ dạng nào ("cột nào anh/chị dùng", "cột nào cần đưa vào ứng dụng"), kể cả bằng một câu multiSelect gọn.
            - TRƯỢT nếu hỏi thêm về các cột người dùng đã bỏ tích (Revision Number, Preferred Time zone, Complete Date…).
            - Lượt này phải đi tiếp sang một nhóm còn thiếu của bản đồ bao phủ, hoặc đào sâu QUY TẮC NGHIỆP VỤ đằng sau một cột đã chốt (vd Required Date quá hạn thì xử lý thế nào) — đào quy tắc thì KHÔNG tính là hỏi lại nghĩa cột.
            """);

        // ================= BusinessAnalyst/decision-log.v1.md =================
        // Nhật ký "Điều đã chốt" là bộ nhớ dài hạn của cuộc phỏng vấn: BA đọc nó ở MỌI lượt sau để soát mâu
        // thuẫn, và bước soạn tài liệu — vốn bị CẤM tự giả định — coi mỗi dòng ở đây là điều người dùng đã
        // duyệt. Một dòng ghi dư vì BA gộp hai điều vào một lượt sẽ không còn cổng nào chặn lại.

        Add(
            "Decision log — BA gộp hai điều, người dùng chỉ trả lời một: chỉ ghi điều đã trả lời",
            "BusinessAnalyst/decision-log.v1.md",
            """
            ## Nhật ký hiện có
            - Ứng dụng lập kế hoạch lớp học và đánh giá kết quả học tập cho nhân viên, gồm khóa bắt buộc và tự chọn
            - Các vai trò dùng ứng dụng: nhân viên, HR – Đào tạo, Manager orgUnit, Quản trị ứng dụng

            ## Các lượt hội thoại MỚI
            BA: Vậy giáo viên sẽ cập nhật trạng thái Complete/Not Complete/No Show, sau đó chấm điểm từ 1 đến 4 cho riêng học viên Complete. Mình chốt phạm vi duyệt như sau có đúng không: Assistant lập và submit kế hoạch theo từng quý → HoD phòng HR duyệt kế hoạch của quý đó → các lớp trong quý được mở đăng ký?
            Người dùng: Đúng, duyệt theo quý
            """,
            """
            - Xuất CHỈ danh sách bullet "- ", không lời dẫn, không heading, không giải thích.
            - Giữ nguyên 2 dòng của nhật ký hiện có.
            - PHẢI thêm một dòng ghi việc duyệt kế hoạch theo từng quý (Assistant submit → HoD phòng HR duyệt) — đó là điều người dùng đã xác nhận.
            - TUYỆT ĐỐI KHÔNG thêm dòng nào về việc giáo viên cập nhật Complete/Not Complete/No Show hay chấm điểm 1–4: người dùng chỉ đáp "Đúng, duyệt theo quý", phần đó mới là cách BA hiểu và chưa được xác nhận.
            - Không bịa thêm quyết định nào khác.
            """);

        // ================= BusinessAnalyst/requirement-coverage.v4.md =================
        // Bản đồ bao phủ là NGUỒN CHÂN LÝ DUY NHẤT của cổng "Write Requirement" (ready suy tất định:
        // mọi dòng áp dụng [RÕ]/[KHÔNG ÁP DỤNG]) nên các scenario phủ cả hai chiều sai: chấm [RÕ] non
        // (suy diễn) và giữ [MỘT PHẦN]/[CHƯA HỎI] oan (tra khảo nhóm không áp dụng, bỏ qua điều đã chốt).

        Add(
            "Coverage map — dựng mới từ hội thoại: đúng 12 dòng, không suy diễn",
            "BusinessAnalyst/requirement-coverage.v4.md",
            """
            ## Các lượt hội thoại mới cần gộp vào bản đồ
            - BA: Anh/chị muốn ứng dụng giải quyết việc gì?
            - Người dùng: Quản lý việc đăng ký suất ăn ca của công nhân, thay cho việc tổ trưởng gom danh sách bằng giấy.
            - BA: Ai sẽ dùng ứng dụng này?
               (Các lựa chọn gợi ý đã đưa cho người dùng: [1] Công nhân tự đăng ký; [2] Tổ trưởng đăng ký giúp cả tổ; [3] Cả hai)
            - Người dùng: Cả hai — công nhân tự đăng ký được, tổ trưởng đăng ký giúp người không có điện thoại.
            - BA: Việc đăng ký chốt vào lúc nào?
            - Người dùng: Trước 9 giờ sáng hằng ngày, sau giờ đó thì khóa không sửa được nữa.
            """,
            """
            - Xuất ĐÚNG 12 dòng gạch đầu dòng theo đúng thứ tự và tên nhóm quy định; không lời dẫn, không giải thích thêm.
            - Mỗi dòng có đúng một trạng thái hợp lệ: [RÕ], [MỘT PHẦN], [CHƯA HỎI] hoặc [KHÔNG ÁP DỤNG].
            - Mục tiêu / bài toán ghi nhận đúng (đăng ký suất ăn ca thay cho gom giấy).
            - Đối tượng người dùng ghi nhận CẢ công nhân lẫn tổ trưởng — câu trả lời "Cả hai" phải được hiểu theo các lựa chọn gợi ý đã đưa.
            - Quy tắc "chốt trước 9 giờ, sau đó khóa" được ghi vào nhóm phù hợp (quy tắc nghiệp vụ / vòng đời).
            - Các nhóm chưa được nhắc tới (thông báo, báo cáo, quy mô…) để [CHƯA HỎI] — KHÔNG tự suy diễn thành [RÕ].
            """);

        Add(
            "Coverage map — gộp lũy tiến khi người dùng đổi ý: ý mới nhất thắng",
            "BusinessAnalyst/requirement-coverage.v4.md",
            """
            ## Bản đồ hiện có (gộp/cập nhật cùng các lượt mới bên dưới)
            - ★ Mục tiêu / bài toán: [RÕ] Quản lý yêu cầu sửa chữa thiết bị trong nhà máy.
            - ★ Đối tượng người dùng & vai trò: [RÕ] Chỉ nhân viên bảo trì dùng, tự ghi nhận và tự xử lý.
            - ★ Chức năng & luồng nghiệp vụ chính: [MỘT PHẦN] Ghi nhận yêu cầu và đóng yêu cầu; còn thiếu: có phân công người xử lý không.
            - Quy trình hiện tại & điểm khó: [RÕ] Gọi điện báo hỏng, không lưu vết.
            - Luồng ngoại lệ & trường hợp đặc biệt: [CHƯA HỎI]
            - Dữ liệu / danh mục chính: [CHƯA HỎI]
            - Quy tắc nghiệp vụ & ràng buộc: [CHƯA HỎI]
            - Vòng đời & trạng thái: [CHƯA HỎI]
            - Thông báo / nhắc nhở: [CHƯA HỎI]
            - Báo cáo / thống kê: [CHƯA HỎI]
            - Phân quyền theo nghiệp vụ: [CHƯA HỎI]
            - Quy mô sử dụng: [CHƯA HỎI]

            ## Các lượt hội thoại mới cần gộp vào bản đồ
            - BA: Ai là người phân công xử lý yêu cầu sửa chữa?
            - Người dùng: À nghĩ lại thì không chỉ bảo trì dùng đâu — công nhân đứng máy sẽ là người tạo yêu cầu khi máy hỏng, còn tổ trưởng bảo trì phân công cho kỹ thuật viên xử lý.
            - BA: Yêu cầu sửa chữa đi qua những trạng thái nào?
            - Người dùng: Mới tạo → đã phân công → đang sửa → hoàn tất. Nếu hỏng nặng phải thuê ngoài thì đánh dấu "chờ nhà cung cấp".
            """,
            """
            - Xuất ĐÚNG 12 dòng đúng thứ tự/tên nhóm, mỗi dòng một trạng thái hợp lệ, không lời dẫn.
            - Đối tượng người dùng & vai trò cập nhật theo ý MỚI NHẤT: công nhân tạo yêu cầu, tổ trưởng bảo trì phân công, kỹ thuật viên xử lý — không còn là "chỉ nhân viên bảo trì".
            - Chức năng & luồng chính được nâng cấp (việc phân công đã rõ).
            - Vòng đời & trạng thái ghi nhận chuỗi trạng thái kèm nhánh "chờ nhà cung cấp".
            - Các nhóm chưa có thông tin mới (thông báo, báo cáo, quy mô…) giữ nguyên [CHƯA HỎI] — không tự suy diễn.
            """);

        Add(
            "Coverage map — hội thoại đủ (kể cả phương án đã chốt): mọi dòng [RÕ]/[KHÔNG ÁP DỤNG] để mở cổng",
            "BusinessAnalyst/requirement-coverage.v4.md",
            """
            ## Các lượt hội thoại mới cần gộp vào bản đồ
            - BA: Anh/chị muốn ứng dụng giải quyết việc gì?
            - Người dùng: Sổ theo dõi mượn trả sách nội bộ của thư viện công ty, thay cho file Excel hay bị ghi đè.
            - BA: Ai sẽ dùng ứng dụng này?
            - Người dùng: Thủ thư là người ghi mượn/trả và quản lý danh mục sách; nhân viên công ty chỉ tra cứu xem sách còn hay đang được mượn.
            - BA: Luồng mượn sách diễn ra thế nào?
            - Người dùng: Nhân viên tới quầy, thủ thư tìm sách theo tên rồi ghi lượt mượn cho người đó, hẹn trả trong 14 ngày. Khi trả thì thủ thư bấm xác nhận đã trả.
            - BA: Nếu quá 14 ngày chưa trả thì sao?
            - Người dùng: Hệ thống đánh dấu quá hạn để thủ thư nhắc, không phạt gì cả.
            - BA: Có trường hợp sách bị mất hoặc hỏng không?
            - Người dùng: Có, thủ thư đánh dấu "mất/hỏng" và sách đó không cho mượn nữa.
            - BA: Danh mục sách gồm những thông tin gì và ai được sửa?
            - Người dùng: Tên sách, tác giả, mã sách, số lượng bản. Chỉ thủ thư được thêm/sửa/xóa.
            - BA: Có cần báo cáo thống kê gì không?
            - Người dùng: Không cần, xem danh sách là đủ.
            - BA: Khoảng bao nhiêu người dùng và bao nhiêu lượt mượn mỗi ngày?
            - Người dùng: Công ty 150 người, chắc 10–20 lượt mượn mỗi ngày.
            - BA: Mình chốt: nhân viên tra cứu không cần đăng nhập, chỉ thủ thư có tài khoản để thao tác nhé?
               (Các lựa chọn gợi ý đã đưa cho người dùng: [1] Đồng ý; [2] Tôi muốn khác)
            - Người dùng: Đồng ý.
            """,
            """
            - Xuất ĐÚNG 12 dòng đúng thứ tự/tên nhóm, mỗi dòng một trạng thái hợp lệ, không lời dẫn.
            - KHÔNG còn dòng nào [CHƯA HỎI]/[MỘT PHẦN] — hội thoại này đã đủ để mở cổng "Write Requirement": mọi dòng phải là [RÕ] hoặc [KHÔNG ÁP DỤNG].
            - Báo cáo / thống kê là [KHÔNG ÁP DỤNG] (người dùng đã nói không cần) — KHÔNG để [CHƯA HỎI].
            - Phân quyền được tính [RÕ]: phương án "tra cứu không cần đăng nhập, chỉ thủ thư có tài khoản" đã được người dùng bấm "Đồng ý" — điều ĐÃ CHỐT, không phải giả định.
            - KHÔNG đòi chi tiết kỹ thuật (cách đăng nhập, database…) để coi một nhóm là thiếu.
            """);

        Add(
            "Coverage map — app cá nhân một người dùng: chủ động [KHÔNG ÁP DỤNG], không tra khảo",
            "BusinessAnalyst/requirement-coverage.v4.md",
            """
            ## Các lượt hội thoại mới cần gộp vào bản đồ
            - BA: Anh/chị muốn ứng dụng giải quyết việc gì?
            - Người dùng: Tôi muốn một app ghi chú việc cần làm cho riêng tôi, dùng một mình, khỏi quên việc.
            - BA: Anh/chị thao tác với ghi chú thế nào?
            - Người dùng: Thêm việc mới, đánh dấu xong, xóa việc. Việc nào có hạn thì ghi kèm ngày hạn.
            - BA: Việc quá hạn thì hiển thị thế nào?
            - Người dùng: Tô đỏ và đưa lên đầu danh sách là được.
            - BA: Có cần nhắc nhở khi sắp tới hạn không?
            - Người dùng: Không cần, tôi tự mở app xem.
            - BA: Một ngày anh/chị có khoảng bao nhiêu việc?
            - Người dùng: Vài việc thôi, 5–10 việc.
            """,
            """
            - Xuất ĐÚNG 12 dòng đúng thứ tự/tên nhóm, mỗi dòng một trạng thái hợp lệ, không lời dẫn.
            - App cá nhân MỘT người dùng: các nhóm hiển nhiên không liên quan (phân quyền theo nghiệp vụ, báo cáo/thống kê, quy trình duyệt nhiều vai trò…) phải được CHỦ ĐỘNG đánh [KHÔNG ÁP DỤNG] kèm lý do ngắn — KHÔNG treo [CHƯA HỎI] để chờ tra khảo.
            - Thông báo / nhắc nhở là [KHÔNG ÁP DỤNG] hoặc [RÕ] (người dùng đã nói không cần) — KHÔNG để [CHƯA HỎI].
            - Mục tiêu, chức năng, vòng đời (việc: chưa xong → xong; quá hạn tô đỏ), quy mô là [RÕ] theo đúng lời người dùng.
            - KHÔNG còn dòng nào [CHƯA HỎI]/[MỘT PHẦN] — hội thoại này đã đủ để mở cổng "Write Requirement".
            """);

        // ================= BusinessAnalyst/product-brief.v3.md =================

        Add(
            "Product Brief — hội thoại đủ: đúng cấu trúc, đủ 'Hoàn thành khi', không rơi rụng",
            "BusinessAnalyst/product-brief.v3.md",
            """
            Project:
            Đặt phòng họp nội bộ

            Project Description:
            Ứng dụng đặt phòng họp cho văn phòng, thay cho ghi sổ giấy.

            Hội thoại khai thác yêu cầu (BA hỏi – Người dùng trả lời):
            BA: Anh/chị muốn ứng dụng giải quyết việc gì?
            Người dùng: Đặt phòng họp nội bộ, hết cảnh hai nhóm tranh nhau một phòng vì ghi sổ giấy.
            BA: Ai sẽ dùng?
            Người dùng: Nhân viên đặt phòng; chị hành chính quản lý danh sách phòng.
            BA: Luồng đặt phòng thế nào?
            Người dùng: Xem lịch trống theo ngày, chọn phòng và khung giờ, điền nội dung họp rồi đặt. Ai đặt trước được trước, trùng giờ thì hệ thống từ chối.
            BA: Muốn hủy lượt đặt thì sao?
            Người dùng: Chỉ người đặt được hủy, và phải hủy trước giờ họp.
            BA: Danh mục phòng gồm gì?
            Người dùng: Tên phòng, sức chứa, có máy chiếu hay không. Chỉ chị hành chính được thêm/sửa.
            BA: Có cần thông báo hay báo cáo gì không?
            Người dùng: Không cần, nhìn lịch là đủ.
            BA: Khoảng bao nhiêu người dùng?
            Người dùng: Tầm 80 người.

            Current Product Brief preview:
            (chưa có)

            Your task:
            - Write/update the Product Brief in plain, non-technical Vietnamese for a normal end user.
            - Return JSON only.
            """,
            """
            - Trả về DUY NHẤT một object JSON có đủ assistantMessage, productBrief.content, needsClarification, clarifyingQuestion, clarifyingSuggestions; needsClarification = false.
            - productBrief.content là Markdown có đủ các mục: tên sản phẩm, "Sản phẩm này là gì?", "Dành cho ai?", "Người dùng làm được những gì?", "Các màn hình chính", "Luồng sử dụng điển hình", "Quy tắc cần nhớ".
            - Mỗi tính năng chính có dòng "Hoàn thành khi: …" ngay bên dưới.
            - Không rơi rụng yêu cầu: có đặt phòng theo khung giờ, hủy trước giờ họp (chỉ người đặt), quản lý danh mục phòng (chỉ hành chính), quy tắc không cho đặt trùng.
            - KHÔNG tự thêm tính năng ngoài hội thoại (không thông báo, không báo cáo, không sửa/xóa lượt đặt của người khác…).
            - Không dùng thuật ngữ kỹ thuật (API, database, schema…); KHÔNG có mục "Điểm cần xác nhận" hay câu chữ giả định/xin xác nhận.
            """);

        Add(
            "Product Brief — hội thoại quá mỏng: dùng van thoát needsClarification",
            "BusinessAnalyst/product-brief.v3.md",
            """
            Project:
            Chấm công

            Project Description:
            (trống)

            Hội thoại khai thác yêu cầu (BA hỏi – Người dùng trả lời):
            Người dùng: Làm cho tôi app chấm công.

            Current Product Brief preview:
            (chưa có)

            Your task:
            - Write/update the Product Brief in plain, non-technical Vietnamese for a normal end user.
            - Return JSON only.
            """,
            """
            - Trả về DUY NHẤT một object JSON đúng format của prompt.
            - needsClarification PHẢI là true — hội thoại chưa đủ để viết tài liệu mà không tự giả định.
            - clarifyingQuestion chứa ĐÚNG MỘT câu hỏi ở góc nhìn nghiệp vụ (không kỹ thuật); clarifyingSuggestions có 2–5 đáp án ngắn.
            - productBrief.content là chuỗi rỗng — KHÔNG viết đại tài liệu bằng giả định.
            - assistantMessage giải thích ngắn gọn rằng cần làm rõ trước khi viết.
            """);

        Add(
            "Product Brief — không tự thêm tính năng 'hiển nhiên' (chỉ thêm mới, không sửa/xóa)",
            "BusinessAnalyst/product-brief.v3.md",
            """
            Project:
            Sổ khách hàng tiềm năng

            Project Description:
            Ghi nhận khách hàng tiềm năng cho nhóm kinh doanh.

            Hội thoại khai thác yêu cầu (BA hỏi – Người dùng trả lời):
            BA: Anh/chị muốn ứng dụng giải quyết việc gì?
            Người dùng: Nhóm kinh doanh 5 người cần một chỗ ghi lại khách hàng tiềm năng gặp ở hội chợ, khỏi ghi mỗi người một file.
            BA: Với mỗi khách hàng cần ghi những gì?
            Người dùng: Tên, số điện thoại, công ty, ghi chú buổi gặp.
            BA: Ai được xem danh sách?
            Người dùng: Cả nhóm xem chung một danh sách.
            BA: Có cần sửa hay xóa khách đã ghi không?
            Người dùng: Không cần, chỉ cần THÊM MỚI thôi. Ghi sai thì ghi chú thêm dòng mới, khỏi sửa.
            BA: Có cần tìm kiếm không?
            Người dùng: Có, tìm theo tên hoặc công ty.
            BA: Một tháng khoảng bao nhiêu khách mới?
            Người dùng: Vài chục.

            Current Product Brief preview:
            (chưa có)

            Your task:
            - Write/update the Product Brief in plain, non-technical Vietnamese for a normal end user.
            - Return JSON only.
            """,
            """
            - Trả về DUY NHẤT một object JSON đúng format; needsClarification = false (thông tin đã đủ, người dùng đã chốt rõ).
            - Tài liệu CHỈ có: thêm khách mới, xem danh sách chung, tìm kiếm theo tên/công ty.
            - TUYỆT ĐỐI KHÔNG xuất hiện tính năng sửa hay xóa khách hàng — người dùng đã nói rõ không cần; tự thêm sửa/xóa là lỗi tự giả định.
            - KHÔNG tự thêm vai trò quản trị, phân quyền, đăng nhập, báo cáo… ngoài hội thoại.
            - Có đủ các mục bắt buộc và dòng "Hoàn thành khi:" cho các tính năng chính.
            """);

        // ================= BusinessAnalyst/product-brief-review.v2.md =================

        Add(
            "Review Brief — bản nháp có lỗi gài: bắt đủ tự-thêm / bỏ sót / giả định còn sót",
            "BusinessAnalyst/product-brief-review.v2.md",
            """
            Project:
            Đăng ký suất ăn ca

            Project Description:
            Đăng ký suất ăn ca cho công nhân.

            Hội thoại khai thác yêu cầu (BA hỏi – Người dùng trả lời):
            BA: Anh/chị muốn ứng dụng giải quyết việc gì?
            Người dùng: Công nhân đăng ký suất ăn ca hằng ngày, thay cho tổ trưởng gom giấy.
            BA: Ai dùng ứng dụng?
            Người dùng: Công nhân tự đăng ký; tổ trưởng đăng ký giúp người không có điện thoại; nhà bếp xem tổng số suất theo ngày.
            BA: Chốt đăng ký lúc nào?
            Người dùng: Trước 9 giờ sáng, sau đó khóa.
            BA: Khi khóa rồi mà công nhân muốn đổi thì sao?
            Người dùng: Báo tổ trưởng, tổ trưởng được sửa giúp tới 10 giờ.
            BA: Nhà bếp cần xem gì?
            Người dùng: Tổng số suất theo ngày và theo tổ, để nấu cho đủ.

            Bản nháp Product Brief cần soát:
            # Đăng ký suất ăn ca
            ## Sản phẩm này là gì?
            Ứng dụng giúp công nhân đăng ký suất ăn ca hằng ngày qua điện thoại, thay cho việc tổ trưởng gom danh sách bằng giấy.
            ## Dành cho ai?
            - Công nhân: tự đăng ký suất ăn.
            - Tổ trưởng: đăng ký giúp người không có điện thoại.
            - Nhà bếp: xem tổng số suất để chuẩn bị.
            ## Người dùng làm được những gì? (các tính năng chính)
            - Công nhân đăng ký / bỏ đăng ký suất ăn trong ngày.
              Hoàn thành khi: đăng ký xong thì suất ăn của mình hiện trong danh sách ngày đó.
            - Tổ trưởng đăng ký giúp thành viên trong tổ.
              Hoàn thành khi: tổ trưởng chọn người và đăng ký được cho người đó.
            - Nhà bếp xem tổng số suất theo ngày.
            - Xuất danh sách đăng ký ra file Excel và gửi qua API cho hệ thống kho.
            ## Các màn hình chính
            - Màn hình đăng ký: chọn ngày, bấm đăng ký.
            - Màn hình của nhà bếp: tổng số suất theo ngày.
            ## Luồng sử dụng điển hình
            Buổi sáng, công nhân mở ứng dụng, bấm đăng ký suất ăn. Đúng 9 giờ hệ thống khóa đăng ký.
            ## Quy tắc cần nhớ
            - Chốt đăng ký trước 9 giờ sáng.
            ## Điểm cần xác nhận
            - Tôi giả định nhà bếp cũng xem được danh sách chi tiết từng người, vui lòng xác nhận.

            Your task:
            - Review the draft against the conversation and list substantive issues.
            """,
            """
            - Trả về DUY NHẤT một object JSON dạng {"issues": [...]}, không chữ ngoài JSON; tối đa 8 vấn đề, xếp theo mức nghiêm trọng.
            - Bắt được lỗi TỰ THÊM: "xuất Excel / gửi qua API cho hệ thống kho" không có trong hội thoại (đồng thời lọt thuật ngữ kỹ thuật) — cách sửa là XÓA.
            - Bắt được lỗi BỎ SÓT: tổ trưởng được sửa giúp tới 10 giờ sau khi khóa.
            - Bắt được lỗi BỎ SÓT/SAI: nhà bếp cần xem tổng suất theo ngày VÀ THEO TỔ (bản nháp chỉ có theo ngày).
            - Bắt được mục "Điểm cần xác nhận" mang tính giả định còn sót — phải xóa.
            - Bắt được tính năng "nhà bếp xem tổng số suất" thiếu dòng "Hoàn thành khi:".
            - Mỗi vấn đề là MỘT câu cụ thể, tự đứng được; không bắt lỗi văn phong vụn vặt.
            """);

        Add(
            "Review Brief — bản nháp đạt: trả issues rỗng, không nặn lỗi",
            "BusinessAnalyst/product-brief-review.v2.md",
            """
            Project:
            Sổ mượn trả sách

            Project Description:
            Theo dõi mượn trả sách thư viện nội bộ.

            Hội thoại khai thác yêu cầu (BA hỏi – Người dùng trả lời):
            BA: Ứng dụng giải quyết việc gì?
            Người dùng: Theo dõi mượn trả sách của thư viện công ty, thay file Excel hay bị ghi đè, để biết sách nào đang được ai mượn.
            BA: Ai dùng?
            Người dùng: Thủ thư ghi mượn/trả và quản lý danh mục sách; nhân viên chỉ tra cứu.
            BA: Luồng mượn thế nào?
            Người dùng: Thủ thư tìm sách, ghi lượt mượn cho nhân viên, hẹn trả 14 ngày; khi trả thì xác nhận đã trả; quá hạn thì đánh dấu để nhắc.
            BA: Danh mục sách gồm gì và ai được sửa?
            Người dùng: Tên sách, tác giả, mã sách, số bản. Chỉ thủ thư được thêm/sửa/xóa.

            Bản nháp Product Brief cần soát:
            # Sổ mượn trả sách
            ## Sản phẩm này là gì?
            Ứng dụng theo dõi mượn trả sách của thư viện công ty, thay cho file Excel dùng chung hay bị ghi đè, giúp biết sách nào đang được ai mượn.
            ## Dành cho ai?
            - Thủ thư: ghi mượn/trả và quản lý danh mục sách.
            - Nhân viên công ty: tra cứu xem sách còn hay đang được mượn.
            ## Người dùng làm được những gì? (các tính năng chính)
            - Thủ thư ghi lượt mượn sách cho nhân viên, hẹn trả trong 14 ngày.
              Hoàn thành khi: ghi xong thì sách hiện trạng thái "đang mượn" kèm tên người mượn.
            - Thủ thư xác nhận trả sách.
              Hoàn thành khi: xác nhận xong thì sách trở về trạng thái "có sẵn".
            - Hệ thống đánh dấu lượt mượn quá 14 ngày là "quá hạn" để thủ thư nhắc.
              Hoàn thành khi: lượt mượn quá 14 ngày tự hiển thị nhãn quá hạn.
            - Thủ thư thêm/sửa/xóa sách trong danh mục.
              Hoàn thành khi: sách mới thêm xuất hiện trong danh mục và tra cứu được.
            - Nhân viên tra cứu sách.
              Hoàn thành khi: gõ tên sách thì thấy sách còn hay đang được mượn.
            ## Các màn hình chính
            - Màn hình tra cứu: tìm sách, xem trạng thái.
            - Màn hình quản lý mượn trả: danh sách lượt mượn, ghi mượn, xác nhận trả.
            - Màn hình danh mục sách: thêm/sửa/xóa sách.
            ## Luồng sử dụng điển hình
            Nhân viên tới quầy hỏi mượn. Thủ thư tìm sách trên màn hình quản lý, ghi lượt mượn với hẹn trả 14 ngày. Khi nhân viên mang trả, thủ thư bấm xác nhận đã trả. Nếu quá 14 ngày chưa trả, lượt mượn hiện nhãn quá hạn để thủ thư nhắc.
            ## Quy tắc cần nhớ
            - Hẹn trả trong 14 ngày; quá hạn thì đánh dấu để nhắc.
            - Chỉ thủ thư được thêm/sửa/xóa danh mục sách.

            Your task:
            - Review the draft against the conversation and list substantive issues.
            """,
            """
            - Trả về DUY NHẤT một object JSON dạng {"issues": [...]}, không chữ ngoài JSON.
            - issues PHẢI là mảng rỗng [] — bản nháp đã đầy đủ và bám sát hội thoại; không được nặn ra vấn đề văn phong/diễn đạt cho có.
            - KHÔNG bắt lỗi "thiếu" thông tin mà hội thoại chưa từng đề cập (thông báo, báo cáo, phạt quá hạn…).
            """);

        // ================= BusinessAnalyst/ai-design-spec.v1.md =================

        Add(
            "AI Design Spec — đúng cấu trúc 11 mục, heading 6.n và BR-n chuẩn định dạng",
            "BusinessAnalyst/ai-design-spec.v1.md",
            """
            Product Brief đã được user duyệt:

            # Đặt phòng họp nội bộ
            ## Sản phẩm này là gì?
            Ứng dụng đặt phòng họp cho văn phòng khoảng 80 người, thay cho ghi sổ giấy, chấm dứt cảnh trùng lịch phòng họp.
            ## Dành cho ai?
            - Nhân viên: xem lịch trống và đặt phòng.
            - Nhân viên hành chính: quản lý danh mục phòng họp.
            ## Người dùng làm được những gì? (các tính năng chính)
            - Xem lịch phòng trống theo ngày.
              Hoàn thành khi: chọn ngày thì thấy phòng nào trống khung giờ nào.
            - Đặt phòng theo khung giờ, điền nội dung họp.
              Hoàn thành khi: đặt xong thì khung giờ đó hiện tên người đặt, người khác không đặt trùng được.
            - Hủy lượt đặt của chính mình trước giờ họp.
              Hoàn thành khi: hủy xong thì khung giờ trống trở lại.
            - Hành chính thêm/sửa phòng họp (tên, sức chứa, có máy chiếu không).
              Hoàn thành khi: phòng mới thêm xuất hiện trong lịch để đặt.
            ## Các màn hình chính
            - Lịch phòng họp: lưới phòng theo khung giờ trong ngày.
            - Đặt phòng: chọn phòng, khung giờ, điền nội dung họp.
            - Danh mục phòng (cho hành chính): danh sách phòng, thêm/sửa.
            ## Luồng sử dụng điển hình
            Nhân viên mở lịch, chọn ngày, thấy phòng trống, bấm đặt, điền nội dung rồi xác nhận. Nếu khung giờ vừa bị người khác đặt thì hệ thống từ chối và mời chọn khung khác. Muốn hủy, mở lượt đặt của mình và bấm hủy trước giờ họp.
            ## Quy tắc cần nhớ
            - Một phòng một khung giờ chỉ một lượt đặt; ai đặt trước được trước.
            - Chỉ người đặt mới được hủy, và phải hủy trước giờ họp.
            - Chỉ hành chính được thêm/sửa danh mục phòng.
            """,
            """
            - Trả về DUY NHẤT một object JSON {assistantMessage, aiDesignSpec.content}, không chữ ngoài JSON.
            - aiDesignSpec.content có đủ 11 mục từ "## 1. Project Goal" tới "## 11. Developer Instructions".
            - Mục 6: MỖI màn hình là một heading cấp 3 dạng "### 6.n. <Tên màn hình>" với tên NGẮN 2–4 từ; route/mục đích/thành phần/field/nút/validation nằm ở bullet BÊN DƯỚI heading, không nhét vào tên.
            - Mục 6 phủ đúng các màn hình của Product Brief (lịch phòng họp, đặt phòng, danh mục phòng); KHÔNG bịa màn hình chức năng mới (báo cáo, dashboard…).
            - Mục 10: mỗi rule là bullet một dòng dạng "- BR-n: <phát biểu>", phủ đủ: không đặt trùng khung giờ, chỉ người đặt được hủy trước giờ họp, chỉ hành chính quản lý danh mục.
            - Spec mô tả CÙNG sản phẩm với Product Brief, không thêm tính năng ngoài phạm vi.
            """);

        Add(
            "AI Design Spec — brief tối giản: không phình phạm vi (không Login/Settings/Report)",
            "BusinessAnalyst/ai-design-spec.v1.md",
            """
            Product Brief đã được user duyệt:

            # Việc của tôi
            ## Sản phẩm này là gì?
            Ứng dụng ghi chú việc cần làm cho MỘT người dùng duy nhất, giúp khỏi quên việc.
            ## Dành cho ai?
            - Chính chủ ứng dụng, dùng một mình, không cần đăng nhập.
            ## Người dùng làm được những gì? (các tính năng chính)
            - Thêm việc mới, kèm ngày hạn nếu có.
              Hoàn thành khi: thêm xong thì việc hiện trong danh sách.
            - Đánh dấu việc đã xong.
              Hoàn thành khi: việc được gạch đi và chuyển sang mục đã xong.
            - Xóa việc.
              Hoàn thành khi: việc biến mất khỏi danh sách.
            ## Các màn hình chính
            - Danh sách việc: việc chưa xong (việc quá hạn tô đỏ, nằm đầu danh sách) và mục việc đã xong.
            ## Luồng sử dụng điển hình
            Mở ứng dụng, thấy danh sách việc chưa xong, việc quá hạn tô đỏ trên đầu. Thêm việc mới khi có việc, bấm đánh dấu xong khi làm xong, xóa việc không cần nữa.
            ## Quy tắc cần nhớ
            - Việc quá ngày hạn thì tô đỏ và đưa lên đầu danh sách.
            """,
            """
            - Trả về DUY NHẤT một object JSON {assistantMessage, aiDesignSpec.content}; đủ 11 mục.
            - Mục 6 CHỈ gồm màn hình bám theo Brief (danh sách việc; được phép tách form thêm việc thành modal của màn hình đó) — KHÔNG tự thêm Login/đăng ký tài khoản, Settings, Dashboard, báo cáo hay phân quyền (ứng dụng một người dùng, không đăng nhập).
            - Mục 10 có rule dạng "- BR-n:" cho quy tắc: việc quá hạn tô đỏ và lên đầu danh sách.
            - Mục 8/9 giữ tối giản đúng bản chất app cá nhân (một entity việc cần làm; không thiết kế đa người dùng).
            - Không thêm tính năng ngoài Brief ở bất kỳ mục nào.
            """);

        // ================= BusinessAnalyst/technical-docs.v1.md =================

        Add(
            "Technical Docs — 4 tài liệu đủ trường, nhất quán, thiếu thì TBD",
            "BusinessAnalyst/technical-docs.v1.md",
            """
            Requirement đã được user duyệt. Soạn bộ tài liệu kỹ thuật dựa trên hai tài liệu sau.

            ## Product Brief đã duyệt
            # Việc của tôi
            Ứng dụng ghi chú việc cần làm cho MỘT người dùng duy nhất, không cần đăng nhập. Tính năng: thêm việc (kèm ngày hạn tùy chọn), đánh dấu xong, xóa việc; việc quá hạn tô đỏ và nằm đầu danh sách. Một màn hình danh sách việc duy nhất.

            ## AI Design Spec đã duyệt
            # AI Design Spec
            ## 1. Project Goal
            Ứng dụng to-do cá nhân một người dùng, quản lý việc cần làm với ngày hạn.
            ## 2. Target Users / Actors
            - Owner (một người dùng duy nhất, không đăng nhập).
            ## 3. MVP Scope
            - Thêm việc, đánh dấu xong, xóa việc; ngày hạn; đánh dấu quá hạn.
            ## 4. Out of Scope
            - Đa người dùng, đăng nhập, thông báo, báo cáo.
            ## 5. Navigation Structure
            - Một màn hình chính "Việc của tôi".
            ## 6. Screens To Generate
            ### 6.1. Danh sách việc
            - Route: /
            - Bảng việc chưa xong (tên việc, ngày hạn, nút Xong/Xóa) + mục việc đã xong; việc quá hạn tô đỏ, xếp lên đầu.
            - Form thêm việc: ô tên việc (bắt buộc), ô ngày hạn (tùy chọn), nút Thêm.
            - Trạng thái empty: "Chưa có việc nào".
            ## 7. UI/UX Direction
            - Một trang đơn giản: danh sách + form, badge trạng thái.
            ## 8. Data Model Summary
            - TodoItem: id, title, dueDate (tùy chọn), isDone, createdAt.
            ## 9. API Expectations
            - API tối giản: GET/POST/PATCH/DELETE /todos.
            ## 10. Business Rules
            - BR-1: Việc có dueDate nhỏ hơn ngày hiện tại và chưa xong được đánh dấu quá hạn, tô đỏ và xếp lên đầu danh sách.
            - BR-2: Tên việc là bắt buộc khi thêm mới.
            ## 11. Developer Instructions
            - Dựng bản chạy được, một màn hình, không cần đăng nhập.
            """,
            """
            - Trả về DUY NHẤT một object JSON đúng schema của prompt: có assistantMessage và đủ 4 khóa brd, srs, fsd, userStories với đầy đủ TỪNG trường con (brd: executiveSummary→openQuestions; srs: purpose→openIssues; fsd: moduleScope→openQuestions; userStories.content).
            - Nội dung nhất quán với Product Brief + AI Design Spec: một người dùng, không đăng nhập — KHÔNG phát minh phạm vi mới (đa người dùng, báo cáo, thông báo, phân quyền).
            - Thông tin đầu vào không có (vd yêu cầu triển khai, ràng buộc hạ tầng) điền "TBD" hoặc "Cần làm rõ" thay vì bịa.
            - userStories.content có các user story bám tính năng: thêm việc, đánh dấu xong, xóa việc, quá hạn tô đỏ.
            - assistantMessage tóm tắt ngắn gọn đã tạo những tài liệu nào.
            """);

        // ================= BusinessAnalyst/conversation-summary.v1.md =================

        Add(
            "Tóm tắt hội thoại — gộp lũy tiến, giữ mọi chốt, ý mới thay ý cũ",
            "BusinessAnalyst/conversation-summary.v1.md",
            """
            ## Tóm tắt hiện có (kết quả nén của các lượt còn cũ hơn)
            - Ứng dụng quản lý đơn nghỉ phép cho công ty ~200 người; thay form giấy hay thất lạc.
            - Vai trò: nhân viên nộp đơn; quản lý trực tiếp duyệt; nhân sự xem tổng hợp.
            - Loại phép: phép năm, ốm, việc riêng — danh mục do nhân sự quản lý.
            - Quy tắc: trừ tự động vào quỹ phép năm; không cho nộp quá số ngày còn lại.
            - Còn chờ làm rõ: đơn bị từ chối thì xử lý tiếp thế nào.

            ## Các lượt hội thoại mới cần gộp vào
            - BA: Nếu đơn bị quản lý từ chối thì tiếp theo xử lý thế nào?
            - Người dùng: Nhân viên sửa lại đơn rồi gửi duyệt lại, không giới hạn số lần.
            - BA: Đơn đã duyệt mà muốn hủy thì sao?
            - Người dùng: À mà khoan, quỹ phép thì thôi đừng trừ tự động — để nhân sự trừ tay cuối tháng, hệ thống chỉ hiển thị số ngày đã nghỉ.
            - BA: Vâng, vậy đơn đã duyệt muốn hủy thì xử lý thế nào?
            - Người dùng: Nhân viên bấm xin hủy, quản lý xác nhận thì đơn mới được hủy.
            """,
            """
            - Chỉ xuất PHẦN VĂN BẢN tóm tắt (gạch ý), không lời mở đầu, không giải thích, không JSON.
            - Là MỘT tóm tắt hợp nhất — không liệt kê lại từng lượt hội thoại, không lặp ý.
            - Giữ đủ các ý cũ còn hiệu lực: mục tiêu, vai trò, loại phép, quy mô ~200 người, không cho nộp quá số ngày còn lại.
            - Cập nhật theo ý MỚI: quỹ phép KHÔNG trừ tự động nữa (nhân sự trừ tay cuối tháng, hệ thống chỉ hiển thị số ngày đã nghỉ) — ý cũ "trừ tự động" phải được thay thế.
            - Ghi nhận 2 chốt mới: đơn bị từ chối → sửa và gửi lại không giới hạn số lần; hủy đơn đã duyệt cần quản lý xác nhận.
            - Tiếng Việt, súc tích.
            """);

        // ================= BusinessAnalyst/user-memory.v1.md =================

        Add(
            "Hồ sơ người dùng — chỉ giữ sự thật bền, không lẫn chi tiết dự án",
            "BusinessAnalyst/user-memory.v1.md",
            """
            ## Hồ sơ người dùng hiện có (gộp/cập nhật cùng các lượt mới bên dưới)
            - Trưởng nhóm kế hoạch sản xuất tại nhà máy, quen làm việc với Excel.
            - Thích trao đổi ngắn gọn, dùng tiếng Việt.

            ## Các lượt hội thoại mới cần chắt lọc vào hồ sơ
            - BA: Anh/chị muốn ứng dụng giải quyết việc gì?
            - Người dùng: Tôi mới chuyển sang làm trưởng phòng QA. Cần app theo dõi thiết bị đo cần hiệu chuẩn định kỳ — cái này gấp nhé.
            - BA: Thiết bị đo gồm những thông tin gì?
            - Người dùng: Mã thiết bị, ngày hiệu chuẩn gần nhất, chu kỳ hiệu chuẩn. À mà tài liệu nhớ làm song ngữ Việt–Anh như mọi khi giùm tôi, sếp tổng người Đức.
            """,
            """
            - Chỉ xuất phần văn bản hồ sơ (gạch ý), không lời dẫn, không giải thích.
            - Cập nhật vai trò theo thông tin MỚI NHẤT: trưởng phòng QA (thay cho trưởng nhóm kế hoạch sản xuất).
            - Ghi nhận sở thích bền mới: tài liệu song ngữ Việt–Anh ("như mọi khi" cho thấy đây là quy ước lặp lại).
            - Giữ các sự thật bền còn hiệu lực: quen Excel, thích ngắn gọn, dùng tiếng Việt.
            - KHÔNG đưa chi tiết đặc thù của dự án vào hồ sơ (app thiết bị đo, mã thiết bị, chu kỳ hiệu chuẩn, mức độ gấp).
            """);

        // ================= BusinessAnalyst/checklist-gap.v2.md =================

        Add(
            "Checklist gap — phát hiện thông tin người dùng tự nêu và khái quát hoá",
            "BusinessAnalyst/checklist-gap.v2.md",
            """
            ## Checklist đang dùng (KHÔNG đề xuất lại các ý này)
            - Hỏi thêm về ràng buộc an toàn khi đăng nhập/tài khoản (khóa tài khoản, giới hạn số lần thử…) nếu ứng dụng có đăng nhập.

            ## Bài học đã bị loại (người dùng đã tắt — TUYỆT ĐỐI không đề xuất lại)
            - Hỏi về nhu cầu xuất dữ liệu ra Excel.

            ## Toàn bộ hội thoại của một dự án VỪA hoàn tất (đã sinh tài liệu thành công)
            BA: Anh/chị muốn ứng dụng giải quyết việc gì?
            Người dùng: Quản lý đề nghị thanh toán của phòng kế toán.
            BA: Ai dùng ứng dụng?
            Người dùng: Nhân viên các phòng tạo đề nghị, kế toán duyệt và chi.
            BA: Luồng xử lý một đề nghị thế nào?
            Người dùng: Tạo đề nghị kèm hóa đơn, kế toán kiểm tra, duyệt rồi chi tiền, xong đánh dấu đã chi. À, các số tiền phải làm tròn đến nghìn đồng và hiển thị kiểu 1.234.000 đ nhé — quy định của kế toán, ai cũng hay quên.
            BA: Đề nghị bị kế toán từ chối thì sao?
            Người dùng: Trả về người tạo kèm lý do, sửa xong gửi lại. Còn nữa — cuối năm kiểm toán hay hỏi, nên mọi thao tác duyệt/chi phải lưu lại ai làm, lúc nào, không được xóa.
            BA: Cần báo cáo gì không?
            Người dùng: Tổng chi theo tháng và theo phòng.
            """,
            """
            - Trả về DUY NHẤT một object JSON {"items":[{"text":..., "rationale":..., "evidence":...}]} — không lời dẫn, không markdown.
            - Chỉ đề xuất bài học MỚI: KHÔNG lặp lại mục an toàn đăng nhập/tài khoản đang dùng, KHÔNG đề xuất lại mục "xuất dữ liệu ra Excel" đã bị loại.
            - Bắt được ~2 khoảng trống mới đã KHÁI QUÁT HOÁ: (1) hỏi về quy tắc làm tròn/định dạng hiển thị số liệu (tiền tệ, số lượng) khi ứng dụng xử lý số liệu; (2) hỏi về nhu cầu lưu vết thao tác (ai làm, lúc nào, không xóa được) phục vụ kiểm tra/kiểm toán với nghiệp vụ duyệt/chi.
            - `text` viết ở mức CHUNG áp dụng cho dự án khác — KHÔNG nhắc chi tiết đặc thù như "phòng kế toán", "đề nghị thanh toán", "nghìn đồng".
            - `rationale` nêu rõ BA đã bỏ sót nhóm thông tin nào; `evidence` trích NGUYÊN VĂN đoạn người dùng tự nêu (làm tròn nghìn đồng / lưu lại ai làm lúc nào).
            - KHÔNG thêm mục cho thông tin mà BA ĐÃ hỏi trong hội thoại (luồng từ chối, báo cáo).
            """);

        // ================= Eval/judge.v1.md =================

        Add(
            "Judge — trừng phạt bịa đặt: trả lời thêm thắt phải bị điểm ≤ 2",
            "Eval/judge.v1.md",
            """
            ## Đầu vào của tình huống
            Người dùng: Tóm tắt lại giúp tôi các loại phép mà tôi ĐÃ liệt kê: phép năm 12 ngày, nghỉ ốm có giấy bác sĩ, nghỉ việc riêng có lương 3 ngày/năm. Ngoài các loại trên tôi chưa cung cấp gì thêm.

            ## Tiêu chí chấm
            - Chỉ tóm tắt đúng 3 loại phép người dùng đã liệt kê, không thêm loại mới.
            - Không bịa con số hay chính sách không có trong đầu vào.
            - Trả lời bằng tiếng Việt, ngắn gọn.

            ## Câu trả lời của AI cần chấm
            Công ty bạn có các loại phép sau: phép năm 12 ngày (cộng thêm 1 ngày cho mỗi 5 năm thâm niên), nghỉ ốm có giấy bác sĩ tối đa 30 ngày/năm, nghỉ việc riêng có lương 3 ngày/năm, nghỉ không lương tối đa 60 ngày, và nghỉ thai sản 6 tháng theo luật.
            """,
            """
            - Trả về DUY NHẤT một object JSON {"score": <số nguyên 1-5>, "reasoning": "..."} — không chữ nào ngoài JSON, không markdown.
            - score phải ≤ 2: câu trả lời BỊA nhiều thông tin không có trong đầu vào (cộng thâm niên, 30 ngày ốm, nghỉ không lương, thai sản) — vi phạm trực tiếp tiêu chí "không bịa".
            - reasoning 2–4 câu tiếng Việt, chỉ rõ lỗi bịa đặt/thêm thắt là lý do trừ điểm.
            """);

        Add(
            "Judge — trả lời đạt chuẩn: chấm cao, đúng format JSON",
            "Eval/judge.v1.md",
            """
            ## Đầu vào của tình huống
            Người dùng: Tóm tắt giúp tôi 3 quy tắc đặt phòng họp: không đặt trùng khung giờ; chỉ người đặt được hủy và phải hủy trước giờ họp; chỉ hành chính được sửa danh mục phòng.

            ## Tiêu chí chấm
            - Nêu đủ và đúng cả 3 quy tắc, không thêm quy tắc mới.
            - Trả lời bằng tiếng Việt, dạng gạch đầu dòng, ngắn gọn.

            ## Câu trả lời của AI cần chấm
            Ba quy tắc đặt phòng họp của bạn:
            - Mỗi phòng trong một khung giờ chỉ có một lượt đặt — không đặt trùng.
            - Chỉ người đã đặt mới được hủy lượt đặt, và phải hủy trước khi cuộc họp bắt đầu.
            - Chỉ nhân viên hành chính được thêm/sửa danh mục phòng họp.
            """,
            """
            - Trả về DUY NHẤT một object JSON {"score": <số nguyên 1-5>, "reasoning": "..."} — không chữ ngoài JSON.
            - score phải ≥ 4: câu trả lời đạt đủ mọi tiêu chí (đủ 3 quy tắc, đúng nội dung, tiếng Việt, gạch đầu dòng, không thêm thắt).
            - reasoning 2–4 câu tiếng Việt nêu rõ các tiêu chí đã đạt.
            """);

        // ================= TechLead/architecture-design.v1.md =================

        Add(
            "Kiến trúc — đủ thành phần cốt lõi, bám spec, không phát minh tính năng",
            "TechLead/architecture-design.v1.md",
            """
            # AI Design Spec
            ## 1. Project Goal
            Ứng dụng đặt phòng họp nội bộ (~80 người dùng), chấm dứt trùng lịch phòng họp.
            ## 2. Target Users / Actors
            - Employee: xem lịch, đặt phòng, hủy lượt đặt của mình.
            - Admin (hành chính): quản lý danh mục phòng.
            ## 3. MVP Scope
            - Lịch phòng theo ngày, đặt phòng theo khung giờ, hủy trước giờ họp, quản lý danh mục phòng.
            ## 4. Out of Scope
            - Thông báo, báo cáo, tích hợp lịch ngoài (Outlook/Google).
            ## 5. Navigation Structure
            - Lịch phòng họp / Đặt phòng / Danh mục phòng (chỉ Admin).
            ## 6. Screens To Generate
            ### 6.1. Lịch phòng họp
            - Route: /calendar — lưới phòng theo khung giờ trong ngày, ô trống bấm để đặt.
            ### 6.2. Đặt phòng
            - Route: /booking — chọn phòng + khung giờ, nội dung họp (bắt buộc), nút Đặt; báo lỗi khi trùng giờ.
            ### 6.3. Danh mục phòng
            - Route: /rooms — bảng phòng (tên, sức chứa, máy chiếu), thêm/sửa; chỉ Admin thấy.
            ## 7. UI/UX Direction
            - Enterprise dashboard, sidebar trái, card + table + modal.
            ## 8. Data Model Summary
            - Room: id, name, capacity, hasProjector.
            - Booking: id, roomId, date, startTime, endTime, topic, bookedBy, status.
            ## 9. API Expectations
            - GET/POST /bookings, DELETE /bookings/{id}; GET/POST/PUT /rooms.
            ## 10. Business Rules
            - BR-1: Một phòng một khung giờ chỉ một lượt đặt (từ chối khi trùng).
            - BR-2: Chỉ người đặt được hủy, và chỉ trước giờ bắt đầu họp.
            - BR-3: Chỉ Admin thao tác danh mục phòng.
            ## 11. Developer Instructions
            - Dựng bản chạy được, stack đơn giản (dotnet hoặc node).
            """,
            """
            - Sản phẩm chính là NỘI DUNG bản kiến trúc Markdown (môi trường eval không có tool thật — nội dung nằm trong lời gọi WriteFile hay trả trực tiếp đều chấp nhận), có cấu trúc rõ ràng.
            - Nêu đủ: tổng quan giải pháp + các thành phần/module chính và trách nhiệm từng phần; các màn hình/route và luồng tương tác; mô hình dữ liệu (Room, Booking với các trường chính); rủi ro/điểm cần lưu ý cho bước Implementation.
            - Cả 3 business rule (chống đặt trùng, quyền hủy trước giờ họp, quyền Admin với danh mục) được phản ánh trong thiết kế (chỗ nào validate, tầng nào chịu trách nhiệm).
            - Bám đúng spec: đủ 3 màn hình; KHÔNG phát minh tính năng ngoài phạm vi (thông báo, báo cáo, tích hợp lịch ngoài đã Out of Scope).
            - Đề xuất stack đơn giản, khả thi để Developer hiện thực dự án nhiều file chạy được.
            """);

        // ================= Phỏng vấn mô phỏng (Kind = Interview) =================

        AddInterview(
            "Phỏng vấn mô phỏng — đơn nghỉ phép (có quy tắc định lượng)",
            """
            Bạn là trưởng nhóm hành chính nhân sự của một công ty 120 người, không rành công nghệ.

            Bài toán: nhân viên xin nghỉ phép đang gửi qua email và Excel, hay thất lạc, cuối tháng chấm công rất mệt.
            Muốn có ứng dụng nội bộ để nhân viên gửi đơn và quản lý duyệt.

            Những điều bạn biết (chỉ nói khi được hỏi đúng chỗ):
            - Vai trò: nhân viên, quản lý trực tiếp, và bạn (HR) xem được tất cả.
            - Luồng: nhân viên gửi đơn → quản lý trực tiếp duyệt → đơn được duyệt thì khóa, không sửa được nữa.
            - Nếu quản lý từ chối, nhân viên sửa lại rồi gửi lại được.
            - Nếu quản lý trực tiếp đang nghỉ thì cấp trên của người đó duyệt thay.
            - Mỗi người có 12 ngày phép/năm, cộng thêm 1 ngày cho mỗi 3 năm thâm niên, tối đa 18 ngày.
            - Ngày phép năm cũ dùng tới hết tháng 3 năm sau, sau đó mất.
            - Nghỉ nửa ngày được tính 0,5 ngày.
            - Cần xem được ai đang nghỉ trong tuần này để xếp việc.
            - HR cần xuất danh sách ngày nghỉ trong tháng để chấm công.
            - Không cần app cho khách hàng, không cần tích hợp gì bên ngoài.
            """,
            """
            - Khai thác được ĐẦY ĐỦ các thông tin trong hồ sơ vai diễn: vai trò, luồng duyệt, xử lý khi từ chối, người duyệt thay, công thức tính ngày phép (12 + thâm niên, trần 18), hạn dùng phép năm cũ, nghỉ nửa ngày, xem ai đang nghỉ, xuất danh sách chấm công.
            - Với quy tắc ĐỊNH LƯỢNG (cách tính ngày phép), BA phải chốt bằng một VÍ DỤ SỐ cụ thể và xin xác nhận, không chỉ ghi lại lời mô tả.
            - Với luồng duyệt, BA phải chốt lại chuỗi bước/trạng thái cụ thể để người dùng xác nhận.
            - KHÔNG hỏi câu kỹ thuật (SSO, API, database, hạ tầng, email/SMTP).
            - Mỗi lượt chỉ hỏi MỘT câu và luôn kèm đáp án gợi ý; càng ít lượt mà vẫn lấy đủ thông tin thì càng tốt.
            - Kết thúc bằng lượt tóm tắt mời bấm "Write Requirement" — cuộc phỏng vấn phải TỚI ĐÍCH, không lan man vô hạn.
            - Không tự giả định thay người dùng; điểm nào người dùng không có ý kiến thì đề xuất một phương án rồi xin chốt.
            """);

        AddInterview(
            "Phỏng vấn mô phỏng — người dùng mơ hồ, hay nói \"sao cũng được\"",
            """
            Bạn là nhân viên kho của một xưởng nhỏ, rất bận và trả lời cộc lốc, hay nói "sao cũng được", "tuỳ bạn".

            Bài toán: đang ghi sổ tay việc nhập/xuất vật tư, hay nhầm số lượng, sếp hỏi tồn kho thì phải đếm tay.

            Những điều bạn biết (chỉ nói khi được hỏi, và trả lời rất ngắn):
            - Chỉ có bạn và một người nữa nhập liệu; sếp thỉnh thoảng vào xem.
            - Mỗi lần nhập hàng ghi: tên vật tư, số lượng, ngày, nhà cung cấp.
            - Xuất hàng ghi: tên vật tư, số lượng, ngày, xuất cho tổ nào.
            - Muốn biết tồn kho hiện tại của từng vật tư.
            - Có lúc nhập nhầm, cần sửa lại phiếu đã ghi.
            - Vật tư có khoảng 200 loại, mỗi ngày chừng 20-30 phiếu.
            - Còn lại "sao cũng được", bạn không có ý kiến gì thêm.
            """,
            """
            - Lấy đủ: hai người nhập liệu + sếp xem, các trường của phiếu nhập và phiếu xuất, nhu cầu xem tồn kho, sửa phiếu đã ghi, quy mô (~200 vật tư, 20-30 phiếu/ngày).
            - Khi người dùng nói "sao cũng được"/"tuỳ bạn", BA phải ĐỀ XUẤT một phương án cụ thể rồi xin chốt (kèm gợi ý dạng Đồng ý / Muốn khác) — TUYỆT ĐỐI không bỏ lửng và cũng không tự ghi nhận như thể người dùng đã quyết.
            - Không tra khảo: không hỏi đi hỏi lại một điểm người dùng đã trả lời hoặc đã nói không quan tâm.
            - Mỗi lượt một câu hỏi, luôn có đáp án gợi ý, ngôn ngữ dễ hiểu với người không rành công nghệ.
            - Cuộc phỏng vấn phải TỚI ĐÍCH (mời bấm "Write Requirement") trong số lượt hợp lý, không kéo dài vô tận vì người dùng trả lời cộc lốc.
            """);

        return scenarios.ToArray();
    }
}
