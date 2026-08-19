# Các tính năng vệ tinh

## Notifications
- **In-app (chuông)**: luôn chạy. `NotificationService` ghi bảng `Notifications` tại các sự kiện workflow (cổng chờ duyệt / hoàn tất / thất bại); client poll `GET /Notifications/Feed`.
- **Kênh ngoài (Teams webhook, SMTP email, Bosch Email Server API)**: opt-in qua config, fail-open (lỗi gửi chỉ log warning, không gãy workflow). Kiến trúc plugin: hiện thực `INotificationChannel` mới + đăng ký DI là xong. `BoschEmailServerNotificationChannel` gửi qua Email Server API nội bộ (HTTP + header `ApiKey`, giống các app Bosch khác) — dùng khi hạ tầng chỉ mở API thay vì SMTP; kèm chốt an toàn `OnlySendToTesterEmail` lọc người nhận về danh sách tester cho môi trường non-prod.
- **Tùy chọn theo user**: `/Notifications/Preferences` — bật/tắt kênh, chọn loại sự kiện, email cá nhân.

## Budget guard
`IBudgetGuard` chặn **trước** mỗi lời gọi model khi tổng chi phí trong kỳ (`Monthly`/`Daily`/`Total`) chạm trần hệ thống hoặc trần mỗi-project. Chi phí tính y hệt trang Usage (cùng `LlmCost`, kể cả phần token đọc từ cache). Chỉ chính xác khi model khai báo đơn giá. Bản tổng chi phí được **cache 15 giây** (IMemoryCache) và query tổng đi qua index `AgentModelCallLogs(CreatedAt)` — một agent run 40 bước không còn quét bảng log 40 lần; đổi lại trần có thể bị vượt thêm đúng lượng chi tiêu của cửa sổ cache đó (chấp nhận được cho một chốt chặn đo theo kỳ).

## Usage & Delivery Quality
- **Usage**: token & USD theo model/project/tháng, kèm "Usage by department" (roll-up `OrgUnitCode` về department gần nhất). Bảng "Cost by model" tách thêm cột **Cached prompt** (số token + % prompt được provider đọc lại từ cache) và đơn giá cache — xem [cached input](llm-and-prompts.md#cached-input-token-prompt-đọc-lại-từ-cache).
- **Delivery Quality**: thông lượng pipeline, tỉ lệ rework (revision/bugfix), độ tin cậy model; có card trỏ sang Prompt Evals.

## Feedback
Người dùng gửi bug/góp ý kèm tối đa 8 file × 50MB (ảnh, PDF, Office, video — whitelist trong `FeedbackAttachmentStore`). TeamDev/Admin triage bằng `FeedbackManage`.

---

## Lịch sử revision tài liệu sinh ra (version history + diff)
Tài liệu sinh ra bị **ghi đè** ở nhiều luồng (bấm lại "Write Requirement" trên draft; vòng "Yêu cầu
chỉnh sửa" sinh lại BRD/SRS/FSD/UserStories cùng phiên bản) — trước đây lịch sử mất sạch. Nay
`RequirementDocumentGenerator.UpsertDocument` là **chốt chặn duy nhất**: mỗi lần Content được ghi
(lần đầu hoặc ghi đè CÓ thay đổi) nó chụp một **`ProjectDocumentRevision`** (nội dung đầy đủ — không
lưu delta — + `ChangeNote` nguồn gốc: "Write Requirement", "Chỉnh sửa theo nhận xét: ..." v.v.; ghi
lại cùng nội dung thì KHÔNG snapshot). Revision chỉ Add vào change tracker — SaveChanges của caller
lưu **atomic** cùng document, không bao giờ có revision mồ côi. Diff giữa revision liền kề tính **lúc
xem** bằng `DocumentDiffService` (LCS theo dòng, trim đầu/cuối chung, quá trần DP thì fallback "thay
cả khối"). UI: nút **Lịch sử** ở modal tài liệu trang Requirements + khung preview Agent Dashboard
(chỉ doc DB-tracked), dùng chung `wwwroot/js/doc-history.js` + endpoint
`Requirements/DocumentRevisions|DocumentRevisionDiff`.

## Prompt Evals — golden set + LLM-judge
Trả lời câu "sửa prompt/đổi model xong, chất lượng LÊN hay XUỐNG?" bằng số thay vì cảm tính:
- **`EvalScenario`** (golden set): một tình huống = (template prompt dưới `/Prompts` + đầu vào mô
  phỏng + tiêu chí chấm). System prompt lấy **nội dung hiện hành** của file template lúc chạy, nên
  cùng bộ scenario đo được các phiên bản prompt khác nhau.
- **`EvalRun`/`EvalResult`**: một run chạy mọi scenario đang bật (lọc được theo template) với model
  MỤC TIÊU rồi để model **JUDGE** chấm 1–5 theo tiêu chí (prompt `Eval/judge.v1.md`, parse bằng
  `EvalJudgeParser`). Run chạy **nền** bởi `EvalRunWorker` (poll Queued như `AgentTaskWorker`; run
  Running mồ côi sau restart → Failed); UI poll tiến độ, xem chi tiết từng scenario và **so sánh 2
  run** theo từng scenario (khớp bằng `EvalScenarioId`).
- Lời gọi eval **tái dùng** middleware `ModelCallLoggingChatClient` (deadline/trần token/map lỗi)
  nhưng với `NullModelCallLogger`: KHÔNG ghi `AgentModelCallLogs` (bảng đó FK cứng Project/Agent) và
  không qua budget guard theo-project — token/lỗi đã nằm trên `EvalResult`.
- Model & scenario tham chiếu bằng **Guid + snapshot tên, không FK** (như `AgentModelCallLog`): xoá
  model/scenario không bị chặn và không mất lịch sử điểm.
- **Chấm theo TỪNG tiêu chí** (`EvalResult.CriteriaJson`): judge trả kèm `criteria[]` — mỗi dòng
  tiêu chí của scenario được đánh đạt/trượt và ghi chỗ trượt — render thành checklist ✓/✗ trong chi
  tiết run. Một điểm tổng chỉ nói "có vấn đề"; danh sách này nói vấn đề nằm ở DÒNG NÀO, nếu không
  mỗi lần điểm tụt lại phải đọc `JudgeReasoning` rồi đoán. Đây là phần **mở rộng, không bắt buộc**:
  judge/kết quả cũ không có nó thì `CriteriaJson` null và điểm vẫn hợp lệ (`EvalJudgeParser` bỏ qua
  phần tử rác từng cái). Lưu nguyên JSON, không dựng bảng con — dữ liệu này chỉ đọc kèm kết quả,
  không bao giờ bị truy vấn/lọc riêng.
- **Huỷ run** (`EvalRun.CancelRequestedAt` + `EvalRunStatus.Cancelled`): controller chỉ ĐẶT CỜ, còn
  `EvalRunnerService` đọc lại cờ giữa hai scenario rồi mới chốt trạng thái — worker chạy ở
  scope/DbContext khác nên giật trạng thái từ ngoài sẽ đụng độ lần `SaveChanges` kế tiếp của nó, và
  huỷ giữa một lời gọi LLM đang bay không cứu được token của lời gọi đó (điểm cắt rẻ nhất là ranh
  giới scenario). Run Queued thì chốt `Cancelled` ngay vì chưa ai đụng tới. Kết quả đã chạy xong
  được GIỮ (đã trả tiền rồi) và vẫn tính vào điểm TB; `Error` ghi rõ dừng ở x/y. Restart giữa chừng:
  run mồ côi CÓ cờ huỷ → `Cancelled` (kết cục người dùng muốn), không cờ → `Failed` như cũ.
- **Chặn chạy trùng** (`StartEvalRunUseCase`): đã có run Queued/Running cùng (target, judge,
  promptKey) thì từ chối — nút chạy nằm sau một modal + redirect nên bấm hai lần là chuyện thường,
  và lần thứ hai không cho thêm thông tin gì ngoài hoá đơn LLM thứ hai.
- **Ước lượng chi phí** trước khi chạy (`GetEvalPageQuery.BuildCostEstimatesAsync`): trung bình chi
  phí THẬT của chính từng scenario ở ≤5 run Completed gần nhất, thiếu thì mượn trung bình cùng
  `Kind` (scenario `Interview` đắt gấp nhiều lần `Prompt` nên không được chia đều). Cộng/chia làm
  trong bộ nhớ, KHÔNG đẩy xuống SQL: Sqlite (Development/test) lưu decimal dạng TEXT nên `AVG`/phép
  cộng phía DB cho ra số sai.
- Bảng Runs lọc + phân trang **phía server** (`_Pager` như Audit/Models/Projects) và xoá được run đã
  kết thúc (`DeleteEvalRunUseCase`; FK Cascade dọn `EvalResults`, run đang chạy phải huỷ trước để
  không xoá dưới chân worker). Bảng Scenarios mang điểm lần chấm gần nhất (truy vấn tương quan —
  golden set chỉ vài chục dòng).
- Phân quyền: `EvalView`/`EvalManage` (màn hình "Prompt Evals" trong `PermissionCatalog`; TeamDev
  được seed mặc định). Trang Delivery Quality có card "Prompt evals gần nhất" trỏ sang.

### Eval tầng PHỎNG VẤN (`EvalScenarioKind.Interview`)
Golden set cũ đo **một lượt**: một đầu vào → một câu trả lời → judge chấm. Chất lượng yêu cầu lại do
CẢ cuộc phỏng vấn quyết định, nên `EvalRunnerService` có thêm nhánh `Interview`: một model đóng vai
người dùng nghiệp vụ theo hồ sơ persona (`Prompts/Eval/persona.v1.md`), BA hỏi và persona trả lời qua
nhiều lượt tới khi BA mời bấm "Write Requirement" hoặc chạm trần lượt. Kết quả gồm **số đo tất định**
(`InterviewTranscript.Measure`: số lượt, có tới đích không, số lượt hỏi dồn nhiều câu, số lượt hỏi mà
quên gợi ý) cộng điểm judge trên toàn transcript. Đây là tầng duy nhất trả lời được "sửa prompt hôm
nay làm cuộc phỏng vấn ngắn đi hay dài ra, có còn tới đích không". Ranh giới hiện tại: eval dừng ở
cuộc phỏng vấn, chưa chạy tiếp Brief/Spec/POC (những bước đó cần project thật + agent + browser).
