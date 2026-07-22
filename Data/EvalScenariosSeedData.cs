using ICOGenerator.Domain;

namespace ICOGenerator.Data;

// Golden set mặc định cho trang Prompt Evals — tách riêng khỏi DbInitializer cho gọn (cùng mẫu OrgUnitsSeedData).
// Mỗi prompt template "đánh giá được bằng text" (system + 1 user message → text) có các scenario phủ những quy tắc
// quan trọng nhất của nó: happy path, thiếu thông tin, và các bẫy hay vi phạm (tự giả định, sai định dạng, hỏi dồn…).
//
// KHÔNG seed scenario cho các template chỉ có nghĩa khi agent có tool/workspace thật (Developer/poc-preview,
// bugfix, implementation*, pull-request; TechLead/code-review; Tester/testing; các instruction.md;
// Shared/revision + tool-agent-native — file có placeholder {{...}}; organization-context — khối ngữ cảnh render
// từ DB, không phải prompt độc lập): harness eval chạy text-only nên điểm số cho chúng sẽ gây hiểu lầm.
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

        // ================= BusinessAnalyst/requirement-chat.v3.md =================

        Add(
            "Chat BA — mở đầu mơ hồ: một câu hỏi nghiệp vụ + JSON đúng format",
            "BusinessAnalyst/requirement-chat.v3.md",
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
            "BusinessAnalyst/requirement-chat.v3.md",
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
            "BusinessAnalyst/requirement-chat.v3.md",
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
            "BusinessAnalyst/requirement-chat.v3.md",
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
            "BusinessAnalyst/requirement-chat.v3.md",
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
            "BusinessAnalyst/requirement-chat.v3.md",
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

        // ================= BusinessAnalyst/requirement-coverage.v2.md =================
        // Bản đồ bao phủ là NGUỒN CHÂN LÝ DUY NHẤT của cổng "Write Requirement" (ready suy tất định:
        // mọi dòng áp dụng [RÕ]/[KHÔNG ÁP DỤNG]) nên các scenario phủ cả hai chiều sai: chấm [RÕ] non
        // (suy diễn) và giữ [MỘT PHẦN]/[CHƯA HỎI] oan (tra khảo nhóm không áp dụng, bỏ qua điều đã chốt).

        Add(
            "Coverage map — dựng mới từ hội thoại: đúng 12 dòng, không suy diễn",
            "BusinessAnalyst/requirement-coverage.v2.md",
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
            "BusinessAnalyst/requirement-coverage.v2.md",
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
            "BusinessAnalyst/requirement-coverage.v2.md",
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
            "BusinessAnalyst/requirement-coverage.v2.md",
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

        // ================= BusinessAnalyst/checklist-gap.v1.md =================

        Add(
            "Checklist gap — phát hiện thông tin người dùng tự nêu và khái quát hoá",
            "BusinessAnalyst/checklist-gap.v1.md",
            """
            ## Checklist bổ sung hiện có (kết quả rút kinh nghiệm từ các dự án trước)
            - Hỏi thêm về ràng buộc an toàn khi đăng nhập/tài khoản (khóa tài khoản, giới hạn số lần thử…) nếu ứng dụng có đăng nhập.

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
            - Chỉ xuất phần văn bản checklist (gạch đầu dòng), không lời dẫn, không giải thích, không liệt kê lại hội thoại.
            - Giữ nguyên mục checklist hiện có về an toàn đăng nhập/tài khoản.
            - Bổ sung được ~2 khoảng trống mới đã KHÁI QUÁT HOÁ: (1) hỏi về quy tắc làm tròn/định dạng hiển thị số liệu (tiền tệ, số lượng) khi ứng dụng xử lý số liệu; (2) hỏi về nhu cầu lưu vết thao tác (ai làm, lúc nào, không xóa được) phục vụ kiểm tra/kiểm toán với nghiệp vụ duyệt/chi.
            - Mục mới viết ở mức CHUNG áp dụng cho dự án khác — KHÔNG nhắc chi tiết đặc thù như "phòng kế toán", "đề nghị thanh toán", "nghìn đồng".
            - KHÔNG thêm mục cho thông tin mà BA ĐÃ hỏi trong hội thoại (luồng từ chối, báo cáo).
            - Danh sách gọn, không lặp ý.
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

        return scenarios.ToArray();
    }
}
