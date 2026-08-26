# Luồng yêu cầu — chat với BA

> Đây là "động cơ 1" của hệ thống: một request HTTP xử lý trọn một lượt chat và stream kết quả về.
> Động cơ còn lại (pipeline nền) nằm ở [delivery-pipeline.md](delivery-pipeline.md).

## Đường chat SSE và bốn chốt chặn "không lượt nào được treo"

Đường chat chính là `POST /Requirements/ChatStream` — cùng một request xử lý trọn lượt chat và trả
**Server-Sent Events**: frame `status` ("BA đang soạn câu trả lời…"), frame `token` (BA "đang gõ" —
đã lọc cú pháp JSON qua `BAChatTokenFilter`, chỉ phần `message` hiển thị được stream), và frame `done`
mang bản chốt (reply + suggestions + cờ mời Write Requirement) để client render tại chỗ **không reload
trang**. Client dùng `fetch` + đọc `ReadableStream` (EventSource không POST được).
Lượt chat chạy với `CancellationToken.None` — người dùng đóng tab giữa chừng thì turn vẫn hoàn tất và lưu
DB, chỉ việc ghi response dừng lại.

Đây là **đường ghi duy nhất** của khung chat, và stream hỏng kiểu gì `requirements.js` cũng **reload chứ
không gửi lại**. Chính vì lượt chạy với `CancellationToken.None`: khi client không nghe thấy gì (proxy đệm
cả response nên không frame nào về, đồng hồ canh bắn abort sau 45s) thì server **vẫn đang chạy trọn lượt
đó** — POST lần hai cho cùng câu hỏi sẽ nhân đôi lượt user lẫn lời gọi LLM, vì `BAChatService.ChatAsync`
ghi lượt user vô điều kiện và `BAChatTurnTracker` chỉ loại trừ nhánh `retry`. Reload phủ trọn cả hai khả
năng: lượt đã tới đích thì trang hiện bản đã lưu (và `ChatReplyStatus` lo phần còn lại), lượt chưa tới thì
nháp "đã gửi" không khớp lượt user cuối nên `draftRestore` đổ lại nội dung vào ô nhập kèm lời giải thích.

**Không lượt nào được phép "treo"** — hội thoại luôn kết thúc bằng một lượt assistant, và UI luôn có
đường thoát. Lượt user được lưu TRƯỚC khi gọi LLM, nên nếu phần sau vỡ mà không ai đóng lượt thì hội
thoại nằm lại ở "lượt cuối là user" và trang kẹt vĩnh viễn ở "BA đang soạn câu trả lời…" (F5 cũng không
thoát, không gửi được tin mới). Bốn chốt chặn:

- **Đóng lượt kiểu gì cũng đóng**: mọi ngoại lệ trong một lượt (`BAChatService.RunTurnGuaranteedAsync`,
  và nhánh catch của `AcknowledgeSourcesAsync`) đều ghi một lượt assistant ⚠️ có nút "Thử lại".
- **Nhịp tim SSE**: frame `ping` mỗi 10s trong lúc lượt chạy — client phân biệt được "BA đang nghĩ lâu"
  với "kết nối đã chết". Không có nó, một lời gọi structured-output dài trông y hệt stream đứt.
- **Đồng hồ canh phía client** (`STREAM_IDLE_TIMEOUT_MS`, 45s không nghe thấy gì ⇒ abort) + kiểm tra
  "stream kết thúc mà THIẾU frame `done`": hai kiểu đứt im lặng mà `fetch` không hề báo lỗi.
- **`GET /Requirements/ChatReplyStatus` trả `{pending, stale}`**: `stale` = lượt đang chờ đã chết —
  không tiến trình nào đang chạy nó (`BAChatTurnTracker`, sổ singleton trong bộ nhớ) và lượt user đã cũ
  hơn `BAChatService.ReplyStaleAfter` (3 phút). UI mở khóa ô nhập và mời "Thử lại"; retry lúc này chạy
  lại đúng lượt user còn "cụt" (`RetryLastTurnAsync`) nên người dùng không phải gõ lại câu hỏi.

```
Browser POST /Requirements/ChatStream (SSE)
  └► RequirementsController.ChatStream               [Controllers]
       └► ChatWithBAUseCase.ExecuteAsync             [Application/Requirements]
            └► BAChatService.ChatAsync               [Services/Requirements]
                 ├► OrganizationContextService       → system message: hằng số sản phẩm (phạm vi + nền tảng) + "bức tranh tổ chức" (cache 1h)
                 ├► UserMemoryService                → hồ sơ user (học dần, xuyên project)
                 ├► ConversationMemoryService        → 20 lượt gần nhất nguyên văn + tóm tắt lượt cũ
                 ├► RequirementCoverageService       → bản đồ bao phủ 12 nhóm thông tin
                 ├► SourceContextBuilder             → ngữ cảnh từ tài liệu user upload (text + ảnh/ảnh trang scan)
                 ├► RequirementPromptBuilder         → dựng prompt (template Prompts/BusinessAnalyst/*)
                 ├► ILlmClient                       → gọi LLM  [Services/Llm]
                 └► BAChatReplyParser                → parse trả lời (+ cổng readiness tất định từ bản đồ bao phủ)
       └► AppDbContext.SaveChanges                   [Data] — lưu lượt hội thoại
```

Các cơ chế trí nhớ (chi tiết đầy đủ ở [phần dưới](#các-cơ-chế-trí-nhớ)):

- **Bộ nhớ hội thoại 2 tầng**: 20 lượt gần nhất gửi nguyên văn; lượt cũ gộp dần vào `Project.ConversationSummary` **theo lô ≥10 lượt** (không tóm tắt mỗi lượt — đó là chỗ tiết kiệm token). Fail-open: gọi tóm tắt lỗi thì giữ summary cũ, không mất lượt nào. Vòng soạn Product Brief dùng lại đúng bộ nhớ này (cửa sổ riêng, rộng hơn — xem [Ngữ cảnh gửi lên model ở vòng soạn Brief](#ngữ-cảnh-gửi-lên-model-ở-vòng-soạn-brief)).
- **Bộ nhớ cấp user** (`AppUser.UserMemory`): BA chắt lọc sự thật bền về user (vai trò, lĩnh vực, văn phong...) theo lô, dùng lại ở mọi project của họ.
- **Bản đồ bao phủ yêu cầu** (`Project.RequirementCoverageMap`): 12 nhóm thông tin đánh dấu [RÕ]/[MỘT PHẦN]/[CHƯA HỎI]/[KHÔNG ÁP DỤNG] — NGUỒN CHÂN LÝ DUY NHẤT của độ sẵn sàng: BA chọn câu hỏi kế tiếp dựa vào đây, panel "Tiến độ khai thác" render nó, và cổng "Write Requirement" suy ready TẤT ĐỊNH từ nó (`RequirementReadinessGate.Evaluate`: mọi dòng áp dụng [RÕ] ⇔ cho phép) — không có lời gọi LLM nào chấm lại, nên panel/nút/lời mời không thể vênh nhau.
- **Checklist học được** (`AgentChecklistItem`): sau khi tài liệu sinh thành công, sau mỗi vòng sửa POC, và **mỗi khi người dùng bác một giả định ở cổng xác nhận**, hệ thống rà "user phải tự nêu thông tin gì mà BA chưa từng hỏi" và ghi nhớ **cho mọi project sau**. Ba đường harvest, sắc dần: hội thoại (`ChecklistGapMemoryService`) → ghi chú POC (`PocFeedbackMemoryService`) → giả định bị bác (`SpecAssumptionMemoryService`, xem [Cổng xác nhận giả định](#cổng-xác-nhận-giả-định-giữa-spec-và-poc)). Mỗi bài học là MỘT DÒNG có định danh, kèm **lý do rút ra + trích dẫn bằng chứng + dự án nguồn**, bật/tắt được ở trang `Agents/Checklist`. Chỉ phần `Text` của mục đang bật đi vào prompt; mục bị tắt được gửi cho vòng harvest sau như **danh sách cấm** nên bài học sai không quay lại.
- **Bối cảnh tổ chức**: render từ OrgUnits/Associates, chỉ dữ liệu GỘP (không PII), cache 1h. Fail-open toàn tuyến. Đi kèm hai khối TĨNH "hằng số của sản phẩm" luôn được đính kể cả khi bảng OrgUnits trống: **ranh giới phạm vi** (chỉ nhà máy Đồng Nai) và **nền tảng đã chốt** (chỉ có kênh thông báo email; chỉ đăng nhập bằng SSO qua IdentityServer; danh sách orgUnit + nhân sự đồng bộ từ hệ thống COMPAS).

## Tài liệu nguồn, ảnh và call log

**Tài liệu nguồn** (`ProjectSourceIngestor`) — người dùng nghiệp vụ mô tả yêu cầu bằng thứ họ đang có, nên đường vào này quyết định chất lượng phỏng vấn:

| Định dạng | Cách đọc |
|---|---|
| Ảnh (PNG/JPG/WebP/GIF) | gửi thẳng cho model vision |
| PDF có text | bóc text từng trang (PdfPig), **cộng các hình nhúng đủ lớn trong trang** (sơ đồ, ảnh chụp màn hình phần mềm cũ) lấy ra `figure-{n}.png` kèm mốc `[Hình n]` dưới đúng trang chứa nó (`PdfFigureExtractor`, ngưỡng ~50k pixel, tối đa 12 hình/file, bản lặp như logo/header bị loại theo nội dung bytes) — không có bước này thì cùng một tài liệu lưu `.docx` được gửi kèm hình còn xuất `.pdf` thì mất trắng phần đó |
| PDF **scan** | trang không có text ⇒ lấy ảnh nhúng lớn nhất của trang ra `page-{n}.png` (`PdfScanPageRenderer`, ngưỡng ~200k pixel vì giả định "một trang scan = một ảnh phủ kín trang", tối đa 10 trang), gửi cho model vision theo đúng thứ tự trang. Không lấy được ảnh nào mới cảnh báo "không đọc được" |
| Word `.docx`/`.docm` | đoạn văn + bảng (render `ô \| ô`) theo đúng thứ tự tài liệu, **cộng các hình nhúng đủ lớn** (screenshot phần mềm cũ, sơ đồ nghiệp vụ) lấy ra `figure-{n}.png` kèm mốc `[Hình n]` đúng vị trí trong text (`WordDocumentTextExtractor`, tối đa 12 hình/file) — quy trình/biểu mẫu phòng ban gần như luôn ở dạng này, và phần quý nhất của nó thường nằm trong ảnh |
| Excel `.xlsx`/`.xlsm` / CSV | tiêu đề cột + 29 dòng mẫu, **cộng khối `#### Thống kê cột` quét TOÀN BỘ bảng** (`SpreadsheetTextExtractor`) — xem dưới |

Một PDF có thể góp cả hai loại ảnh (trang bản scan + hình nhúng trong trang có chữ) — hai tập trang rời nhau, cùng đếm vào một con số `ProjectSourceFile.ScannedPageImageCount`, vì `SourceContextBuilder` chỉ cần TỔNG để nói đúng số ảnh thực sự gửi kèm. Ảnh đi theo thứ tự: trang scan trước (theo số trang), rồi hình nhúng (theo số hình) — tên file không tới model, nên mốc `[Hình n]` trong text tự nói ra nó thuộc trang nào.

**Bảng tính: danh mục lấy từ thống kê, không lấy từ dòng mẫu** (`SpreadsheetTextExtractor`). Dòng mẫu chỉ để BA thấy hình dạng dữ liệu; **danh mục của mỗi cột** — thứ sẽ thành enum/danh mục trong mô hình dữ liệu ở các bước sau — phải lấy từ khối `#### Thống kê cột`, quét cả bảng và ghi cho từng cột: bao nhiêu dòng có giá trị, bao nhiêu giá trị phân biệt, các giá trị đó là gì kèm số dòng (liệt kê **đủ** khi ≤ 12 giá trị, còn lại nêu 5 giá trị hay gặp nhất). Vì sao không thể chỉ gửi dòng mẫu: các dòng đầu của một bản xuất thường được sắp theo người/đơn vị nên không đại diện cho cả bảng — ca thật, file 262 dòng mà 29 dòng đầu chỉ chứa `REQ`/`MAN` nên bản đọc lại của BA bỏ sót `OPT`, đúng giá trị mã hóa "khóa học **tự chọn**" mà người dùng đã nói ngay câu đầu tiên; cùng cửa sổ đó cột `Required Date` trống sạch trong khi cả bảng có 12 dòng mang hạn hoàn thành. Ba chi tiết đi kèm, cả ba đều là lỗi đã gặp:

- Ô được đặt theo **`CellReference`** chứ không theo thứ tự xuất hiện. OpenXML lược bỏ hẳn phần tử của ô rỗng, nên đọc tuần tự thì mọi giá trị sau một chỗ trống trượt sang cột khác — BA từng đi báo với người dùng rằng bảng "bị lệch giữa hàng tiêu đề và các giá trị", trong khi bảng gốc không lệch, chỉ text ta dựng ra mới lệch.
- **Thống kê đứng TRƯỚC các dòng mẫu — nhưng `RealSampleDataReader` cắt ngược lại.** Hai consumer của cùng chuỗi text muốn hai nửa ngược nhau, nên text mang hai mốc hằng số (`ColumnStatsHeading`, `DataRowsHeading`). `SourceContextBuilder.Truncate` cắt ở `Llm:SourceUpload:MaxTextCharsPerFile` (mặc định 20.000) giữ **phần đầu** — một sheet rộng (tới 40 cột, ô tới 200 ký tự) ăn hết ngân sách chỉ bằng 29 dòng mẫu, bảng nhiều sheet thì các sheet sau mất sạch ⇒ thống kê phải đứng trước để sống sót. Ngược lại `RealSampleDataReader` (trần **3.000** ký tự) chỉ lấy từ `DataRowsHeading` trở đi, vì nó cần **bản ghi thật**: đó là dữ liệu POC seed lên màn hình, và là chuẩn để `PocSampleDataCheck` rút token đối chiếu. Để nguyên cả khối thì cái tới POC là mấy dòng *"có giá trị 262/262 · ĐỦ 5 giá trị"* — token đặc trưng thành từ vựng thống kê, POC seed sai mà cổng kiểm cũng mù theo.
- Phần bảng mẫu tự nói rõ nó chỉ là dòng đầu và không được dùng để suy ra danh mục.

Chốt bằng `SpreadsheetTextExtractorTests` + `SourceAckReadbackRuleTests`, và chấm điểm bằng các scenario `source-ack` / `source-readback` trong golden set.

**Cột nào được hỏi, và cột nào là của hệ cũ.** Lượt **kể lại** file (`source-readback.v1.md` — với bảng tính nó tới sau bảng cột, xem mục dưới) **chọn** cột đáng nêu thay vì trải cả bảng ra xin giải nghĩa (18 cột thành 18 việc tồn thì cuộc phỏng vấn hết sạch lượt, mà bị hỏi *"Last Name nghĩa là gì"* thì người dùng hiểu là BA chưa mở file). Tiêu chí nêu: cột phân loại ít giá trị, header là mã/viết tắt, **cột chở một quy tắc người dùng đã nói** (ưu tiên cao nhất — `Assignment Type` với `REQ/MAN/OPT` chính là "bắt buộc / tự chọn" họ nói ở câu đầu), hoặc giá trị bất thường. Nêu dưới dạng **đề xuất cách hiểu** để người dùng gật/lắc, không phải câu hỏi trống. Sang lượt chat, các điểm đó được gom vào **một** lượt `questions` thay vì hỏi lẻ từng cột. Riêng **phạm vi cột** thì không hỏi trong chat nữa — nó được chốt bằng bảng cột ngay tại lượt đọc file, xem mục dưới.

**Các cột được đọc CẠNH NHAU, không đọc rời từng cột.** Khối thống kê có số dòng có giá trị của mọi cột, nên đặt vài con số cạnh nhau bắt được cách hiểu sai với giá gần bằng không — trong khi cùng cái sai đó nếu lọt qua sẽ được người dùng bấm "Đúng rồi" đóng dấu rồi chảy vào Product Brief. Ba dấu hiệu prompt bắt soát, cả ba lấy từ cùng một bản đọc thật: **hai cột cùng số dòng có giá trị** (`Item Status` có `Active (219)` và `Complete Date` có giá trị ở đúng 219/262 dòng ⇒ `Active` nhiều khả năng là *đã học xong* chứ không phải *nội dung còn hiệu lực* — cột này quyết định file kể "ai đã học" hay "nội dung nào còn dùng", tức quyết định nó có suy ra được nhu cầu học hay không); **cột mã và cột tên lệch số giá trị phân biệt** (`Item ID` 134 mã nhưng `Item Title` 136 tiêu đề ⇒ mã không phải khóa như tưởng); **một cột chỉ có giá trị ở đúng những dòng mà cột khác có giá trị** ⇒ cột dẫn xuất, xem ngay dưới.

**Hệ cũ có hai loại cột, loại thứ hai khó thấy hơn hẳn.** Cột **hạ tầng** (`Revision Number`, `Preferred Time zone`) lộ ra ngay từ cái tên. Cột **dẫn xuất** — giá trị tính sẵn từ một cột khác tại thời điểm hệ cũ xuất file, như `Days Rem` = `Required Date` trừ ngày xuất — thì đọc lên *như một dữ kiện nghiệp vụ thật* nên trôi qua rất êm, và bản đọc thật đã tích nó là cột của app mới. App mới tính lại được bất cứ lúc nào, còn con số trong file thì đông cứng từ ngày xuất ⇒ POC seed lên màn hình một giá trị vĩnh viễn sai. Phép thử của prompt: *giá trị này có tự đổi theo thời gian mà không ai sửa gì không?* — có thì giữ cột **gốc**, bỏ cột tính sẵn.

### Bảng cột: chốt phạm vi cột của file bảng tính

File người dùng gửi gần như luôn là bản **xuất của hệ thống họ đang dùng**, nên nó chở cả cột nghiệp vụ lẫn cột hạ tầng của hệ cũ. Toàn bộ text của file đi tiếp vào **dữ liệu mẫu thật** của bước sinh spec và POC seed màn hình bằng đúng các cột đó: không chốt thì người dùng mở demo ra thấy `Revision Number`, `Preferred Time zone` nằm như trường của app mới.

Cách chốt: ở **chính lượt BA đọc file** (`source-ack.v3.md`), BA trả thêm `columns` — mỗi cột của file một dòng, kèm **cách hiểu viết sẵn** và đề xuất cột đó có thuộc ứng dụng mới hay không. Người dùng thấy nó thành một **bảng** ngay dưới lời giới thiệu file (`#columnMap`), tích/bỏ tích, sửa ô ý nghĩa nào lệch, rồi gửi trong một lượt.

**Bảng ĐỨNG TRƯỚC bản đọc lại, và với bảng tính lượt upload không kể lại gì cả.** Đây là thứ tự, không phải chi tiết trình bày: lượt đọc file cũ vừa bày bảng vừa kể lại cả file kèm cụm "Chỗ chưa chắc", tức là làm ba việc sai cùng lúc. Nó dựng **việc tồn** trên những cột người dùng sắp bỏ tích ngay bên dưới (mỗi mục ở "Chỗ chưa chắc" đốt một lượt phỏng vấn thật, và `requirement-chat.v4.md` bắt hỏi cho hết chúng trước khi mở nhóm mới). Nó đọc nhầm cả file khi người dùng **gửi nhầm file** hoặc gửi bản xuất sai kỳ — mà bản đọc vẫn đủ hợp lý để được bấm "Đúng rồi". Và nó đặt một bức tường chữ về 18 cột **ngay trên** một cái bảng 18 dòng chở cùng nội dung ở dạng **sửa được**, nên ai cũng đọc lướt phần trên. Nay lượt upload chỉ còn: file này là gì, quy mô thật, mời rà bảng — tối đa năm câu, không gạch đầu dòng, không "Chỗ chưa chắc" (ngoại lệ đúng một câu khi file rõ ràng không phải thứ BA vừa xin, vì họ cần biết ngay để gửi lại). Word/PDF/ảnh không có bảng nên vẫn được đọc lại đầy đủ ngay tại lượt đó.

Hình dạng của lượt do **cơ chế** chọn chứ không để model đoán (`BAChatService.BuildSourceAckTurnShape`, cùng khuôn với cổng bảng phân quyền): còn bảng tính nào `ColumnMap == null` ⇒ khối `## LƯỢT NÀY: CHỐT PHẠM VI CỘT` gọi đích danh các file đó; không còn ⇒ khối `## LƯỢT NÀY: BẢN ĐỌC LẠI`. Model nhìn thấy text của **mọi** nguồn trong project (kể cả file đã chốt cột từ lần upload trước) nên nó không tự suy ra được file nào đang chờ.

**Cùng lý do đó, phạm vi KỂ LẠI cũng phải do cơ chế nói ra** (`BAChatService.BuildReadbackScope` → khối `## PHẠM VI KỂ LẠI CỦA LƯỢT NÀY`). Lượt đọc file nạp lại **toàn bộ** nguồn của project và điều đó là cố ý — nguồn cũ là thứ duy nhất để **đối chiếu**, mà chỗ **nối** giữa file mới và file cũ thường là điểm chưa rõ đắt nhất của cả lô upload (*"biểu mẫu vừa gửi lấy danh sách người học từ file kia, hay người dùng tự nhập?"*). Nhưng "đính kèm để đối chiếu" khác hẳn "phải kể lại", và trước đây không có gì phân biệt hai việc đó: câu dẫn của lượt user nói *"đây là các tài liệu nguồn tôi vừa đính kèm"* rồi đứng trên text của **mọi** nguồn, còn `source-ack.v3.md` bắt *"MỌI file vừa gửi đều phải được nhắc tới"*. Ca thật: người dùng chốt bảng cột cho một file Excel ở đầu buổi, mười mấy lượt sau gửi một ảnh chụp biểu mẫu để trả lời một câu hỏi — bản đọc lại mở đầu bằng gần nửa số dòng nói lại đúng bộ cột họ đã tích tay (chép từ chính khối *"Bảng cột … đã được NGƯỜI DÙNG CHỐT"*), rồi mới tới cái ảnh. Model không sai luật nào nó được cho; cơ chế nói dối về chữ "vừa gửi". Nay lô vừa upload đi từ controller xuống dưới dạng `attachments`, và:

- câu dẫn của lượt user **gọi tên** đúng các file vừa gửi thay vì gộp chung;
- khối phạm vi liệt kê file vừa gửi, liệt kê các nguồn cũ và cấm kể lại chúng — trừ đúng một chỗ: một điểm chưa rõ nằm ở chỗ **nối** giữa hai nguồn;
- bảng tính cũ **chưa chốt cột** không nằm trong danh sách bị cấm đó: bảng của nó được bày lại ngay lượt này, nên nó vẫn cần lời dẫn — cấm nhắc tới nó là mời rà một cái bảng không câu nào giới thiệu;
- không biết lô nào vừa gửi (`attachments` rỗng) ⇒ giữ nguyên hành vi cũ, coi mọi nguồn là vừa gửi. Kể lại thừa còn đỡ hơn để một file vừa gửi rơi khỏi phạm vi — lượt bắt lỗi đọc-nhầm-file của chính nó sẽ biến mất trong im lặng.

`sourceNotes` **không** bị bó theo phạm vi này: nguồn nào có ảnh thật sự đi kèm lượt này đều phải có một mục, kể cả nguồn cũ lần đầu được gửi ảnh — đó là chỗ cất ảnh thành chữ (`VisionSummary`), không phải chỗ người dùng đọc.

Sáu quyết định của thiết kế này, mỗi cái vá một đường hỏng đã gặp:

- **Bảng chứ không phải hàng chip.** Chip chỉ nêu được tập con do BA tự chọn; các cột không lên chip bị coi là "của hệ cũ" mà người dùng không bao giờ nhìn thấy để phản đối. Bảng phơi **đủ** cột của file — `SourceColumnMapBuilder.Build` luôn bổ sung các cột model bỏ sót vào cuối, ở trạng thái chưa tích.
- **Ô ý nghĩa do BA ĐIỀN SẴN.** Một bảng 18 dòng trống là bắt người dùng nghiệp vụ giải nghĩa 18 cột — đúng thái cực mà cả hai prompt đang cấm, và đọc lên như "tôi chưa mở file của anh/chị". BA có tên cột, mọi giá trị phân biệt và số dòng từ khối `#### Thống kê cột` nên đoán được gần hết; đoán sai thì người dùng sửa một dòng.
- **Mọi tên cột phải khớp hàng tiêu đề THẬT.** `SourceColumnMapBuilder` đọc tên cột từ khối thống kê rồi loại mọi dòng không khớp, và luôn dùng chính tả của **file** chứ không của model — ở cả đường model đề xuất lẫn đường trình duyệt gửi lên (server không tin payload nó vừa render ra). Một dòng bịa lọt qua là POC đi lọc dữ liệu mẫu theo một cột không tồn tại.
- **Bảng treo theo FILE, không theo lượt.** Nó còn đó tới khi `ProjectSourceFile.ColumnMap` được ghi, nên người dùng gõ một câu hỏi lại trước rồi mới ngồi tích cũng không mất bảng.
- **Bảng nhận luôn phần giải nghĩa cột, và bản đọc lại NHẢ nó ra.** Bảng đã đi qua đủ mọi cột kèm ô ý nghĩa **sửa được**, nên một `message` đi qua nốt 18 cột nữa trong văn xuôi là lặp cùng nội dung ở dạng **không sửa được** — và cái người dùng nhận về là một bức tường số (*"Revision Number có 3 giá trị: 1 (218), 3 (21), 2 (18)"*), thứ mà ai cũng đọc lướt rồi bấm "Đúng rồi". Bản đọc thật đã trượt đúng kiểu đó. Luật này áp cho **cả hai** lượt: lượt bày bảng không kể lại gì, và lượt kể lại sau đó chỉ giữ những phần bảng cột không chở được — tổng quan + quy mô, các cột **chở một quy tắc nghiệp vụ**, các cột đọc cạnh nhau, và phần đối chiếu với lời kể. Cùng lý do, phạm vi cột chỉ được xử ở **một** chỗ: nêu lại "cột này trông như của hệ cũ" ở bất kỳ lượt nào là tạo một việc tồn trùng với thứ người dùng vừa (hoặc sắp) bỏ tích, rồi lượt phỏng vấn sau đi hỏi lại đúng điều `requirement-chat.v4.md` cấm.
- **Bảng là chỗ trả lời của lượt, nên câu kết phải chỉ vào nó.** Lượt có bảng thì `BAChatService` **bỏ** hàng chip "Đúng rồi / Chưa đúng" — chip bấm là gửi NGAY, để cả hai cùng sống thì một cú bấm nhầm gửi mất lượt trước khi người dùng kịp tích xong bảng, và bảng không bao giờ được chốt (cùng luật với "lượt gộp có `questions` ⇒ bỏ `suggestions`"). Chip đã đi thì câu kết cũng phải đi theo: kết bằng câu hỏi đóng *"Mình hiểu vậy đã đúng chưa ạ?"* là bày ra một câu hỏi **không có nút trả lời**, người dùng đi tìm nút "Đúng rồi" không thấy trong khi việc thật sự phải làm nằm ở bảng. `source-ack.v3.md` vì vậy chia hai ca: có bảng ⇒ mời rà bảng rồi bấm **Gửi bảng cột**; Word/PDF/ảnh ⇒ câu hỏi đóng như cũ vì lúc đó hai chip là đường trả lời duy nhất. Chiều ngược lại cũng phải chặn: model được lệnh mời rà bảng mà rốt cuộc không trả nổi dòng `columns` nào dùng được ⇒ `BAChatService.ColumnMapMissingNotice` nói thẳng là chưa dựng được bảng và mời người dùng gõ các cột họ dùng, thay vì để câu mời trỏ vào khoảng không.

Gửi bảng đi **hai bước**: `POST Requirements/ConfirmColumnMap` lưu vào `ProjectSourceFile.ColumnMap` (`ConfirmSourceColumnMapUseCase`, không gọi LLM), rồi trình duyệt gửi tiếp **một tin nhắn người dùng** qua đúng đường chat thường — hội thoại vẫn chỉ có một đường ghi, như thẻ hỏi gộp. Lưu hỏng thì dừng hẳn, không gửi tin nhắn: hội thoại ghi "đã chốt phạm vi cột" trong khi file vẫn trống là trạng thái tệ nhất.

Tin nhắn đó do **server** soạn (`SourceColumnMapBuilder.RenderUserMessage`, từ bảng đã chuẩn hoá và đã lưu) chứ không do JS ghép từ payload — như bảng phân quyền, vì hai bản lệch nhau thì hội thoại kể một đằng còn file nguồn ghi một nẻo, mà mọi tầng chắt lọc tin vào bản kể. Ở đây nó gánh thêm việc thứ hai: câu mở đầu cố định của nó (`SourceColumnMapBuilder.SubmissionLead`) là **dấu hiệu tất định** để lượt chat kế tiếp biết mình là lượt kể lại.

### Lượt kể lại: bản đọc file, sau khi phạm vi cột đã chốt

Lượt chat ngay sau khi bảng được gửi được đính thêm khối `source-readback.v1.md` (`BAChatService`, cổng đọc `SourceColumnMapBuilder.IsSubmissionMessage` + có ít nhất một bảng tính đã chốt cột). Đây là bản đọc lại bị dời từ lượt upload xuống — vẫn là **cơ hội duy nhất bắt lỗi đọc file ở đầu vào**, chỉ khác là giờ nó nói về đúng bộ cột người dùng thật sự dùng:

- Chỉ nói về **cột đã tích**. Cột bị bỏ tích không được nhắc, không được hỏi thêm — nhắc lại là mở lại thứ họ vừa đóng, và mục đó nằm lại trong danh sách tồn đọng.
- Giữ bốn thứ bảng cột không chở được: file kể chuyện gì + quy mô thật, các cột chở một quy tắc nghiệp vụ (chép đủ giá trị kèm số dòng, lấy từ khối thống kê), các cột **đọc cạnh nhau**, và phần **đối chiếu với lời kể** + cụm "Chỗ chưa chắc".
- Lượt này **không hỏi khai thác**: kết bằng câu hỏi đóng, hai chip trả lời. Code cắt `questions` của lượt và trả lại hai chip (`SourceReadbackSuggestions`) nếu model lỡ kèm thẻ hỏi — `BAChatReplyParser.Normalize` đã dọn `suggestions` khi thấy `questions`, để nguyên là bày ra một câu hỏi đóng không có nút trả lời. Các câu hỏi phỏng vấn quay lại từ lượt kế tiếp, lấy từ chính cụm "Chỗ chưa chắc" vừa được chắt.
- Cổng bảng phân quyền nhường một lượt (`askPermissionMatrix` tắt khi đang ở lượt kể lại): hai khối `## LƯỢT NÀY:` cùng lúc là hai mệnh lệnh chọi nhau, và cổng kia mở lại ngay lượt sau.

Chốt xong, bản đồ cột được **tiêu thụ ở hai đầu** — đây mới là chỗ bảng trả tiền cho chính nó:

| Đầu đọc | Việc |
|---|---|
| `SourceContextBuilder` | gắn khối *"Bảng cột của … đã được NGƯỜI DÙNG CHỐT"* ngay sau text của nguồn, ở **mọi** lượt chat sau ⇒ BA thôi hỏi lại nghĩa cột, thôi hỏi lại phạm vi, thôi dựng yêu cầu trên cột đã loại |
| `RequirementCoverageService` | gắn **cùng khối đó** vào lượt distill bản đồ bao phủ: bảng cột là câu trả lời của người dùng cho phần "bộ cột chính thức", chỉ khác là họ trả lời bằng cách tích chứ không gõ. Thiếu nó thì dòng *Dữ liệu / danh mục chính* kẹt `[MỘT PHẦN]` với *"còn thiếu: chốt bộ cột"* trong khi bằng chứng nằm ngay trong DB — và vì cổng readiness đọc đúng bản đồ đó, lời mời "Write Requirement" bị thay bằng một câu hỏi mà người dùng đã trả lời rồi, lặp lại mỗi lần họ bấm nút |
| `RealSampleDataReader` | **lọc** các dòng dữ liệu mẫu xuống đúng tập cột đã tích, trước khi chúng vào prompt AI Design Spec và làm chuẩn cho `PocSampleDataCheck` |

Chưa chốt (file không phải bảng tính, model không đề xuất được dòng nào, hoặc người dùng chưa gửi) ⇒ không có bảng, không có khối ngữ cảnh, không lọc gì — luồng chạy đúng như trước. Bảng cột không khớp hàng tiêu đề nào cũng không lọc: cắt sạch dữ liệu mẫu tệ hơn nhiều so với để lọt vài cột thừa.

## Sáu bảng chốt của buổi phỏng vấn

Buổi phỏng vấn kết thúc bằng **sáu bảng**, không phải một. Cả sáu cùng một cơ chế, và cơ chế đó sinh ra từ
cùng một quan sát: có những thứ **BA ráp lại từ hội thoại** mà người dùng chưa bao giờ nhìn thấy để bác —
chuỗi bước của một luồng, danh sách màn hình, mô hình dữ liệu, ma trận quyền. Chúng vẫn đi thẳng vào tài
liệu, mang chữ ký của người dùng. Bảng là chỗ họ nhìn thấy và sửa được, và bằng chứng thu về là **một thao
tác trên từng dòng** thay vì một chip trả lời thay cho tất cả.

| Bảng | Cột trên `Project` | Chốt cái gì | Đường tiêu thụ ngoài chat |
|---|---|---|---|
| Luồng nghiệp vụ | `FlowMap` | luồng chính + 1–2 ngoại lệ, mỗi luồng là chuỗi bước *ai làm → làm gì → trạng thái sau đó* | `## 13. Worked Examples` định tính (oracle chấm POC) + `## 10. Business Rules` |
| Đối tượng nghiệp vụ | `EntityMap` | thông tin cần lưu (kèm **cách nhập** và **danh sách lấy ở đâu**), **quan hệ cha-con** + vòng đời trạng thái | `## 8. Data Model Summary` + `## 10. Business Rules` + **màn hình danh mục** gieo vào `PlannedScope` |
| Báo cáo / thống kê | `ReportMap` | mỗi báo cáo một dòng: tên, nó **trả lời câu hỏi gì** (lời người dùng), **lấy số từ** đối tượng nào, **gộp/lọc** theo gì | mỗi dòng còn giữ gieo một MÀN HÌNH vào `PlannedScope` ⇒ `## 6. Screens To Generate` + `## 9. API Expectations` (bộ lọc thật) |
| Màn hình | `ScreenScopeMap` | phạm vi màn hình, việc của từng màn, **các chức năng** trên màn (mỗi chức năng một dòng tích riêng) và **bước luồng** từng chức năng phục vụ | DÒNG của bảng phân quyền + `## 6. Screens To Generate` |
| Phân quyền | `PermissionMatrix` | quyền CRUD theo màn hình, kèm phạm vi dữ liệu | `## 6b. Permission Matrix` + điều kiện lọc ở `## 9. API Expectations` |
| Thông báo / nhắc nhở | `NotificationMap` | mỗi **sự kiện** một dòng: có gửi email không, **To** và **CC** chọn từ danh sách người nhận của dự án | quy tắc gửi mail ở `## 10. Business Rules` |

### Một cổng, đúng một bảng mỗi lượt

`InterviewTableGate.Select` là chỗ DUY NHẤT quyết định lượt này bày bảng nào. Không thể để mỗi cổng tự
quyết: mỗi cổng bơm một khối `## LƯỢT NÀY:` vào ngữ cảnh, và hai khối như thế cùng lúc là hai mệnh lệnh
chọi nhau — model trả một bảng lai hoặc bỏ cả hai. Repo đã gặp đúng chuyện này ở quy mô nhỏ hơn (cổng bảng
phân quyền phải nhường một lượt cho lượt kể lại file bảng tính); với sáu bảng thì việc nhường không còn
viết tay được nữa.

**Thứ tự là thứ tự PHỤ THUỘC, không phải thứ tự tiện tay:**

```
luồng → đối tượng → báo cáo → màn hình → phân quyền → thông báo
```

Câu hỏi xếp ra thứ tự này chỉ có một: **bảng nào ĐẺ RA màn hình thì phải đứng trước bảng chốt phạm vi màn
hình.**

Luồng trước, vì mọi bảng sau đều trỏ về bước luồng — bảng màn hình có ô *"chức năng này phục vụ bước nào"*,
còn cột *"khi nào chuyển vào"* của bảng đối tượng lấy điều kiện từ chính các bước. **Đối tượng rồi báo cáo
đứng TRƯỚC màn hình**, vì cả hai là NGUỒN màn hình: mỗi thông tin kiểu chọn có nguồn *"ứng dụng tự quản lý"*
đẻ ra một màn hình quản lý danh mục (`EntityMapBuilder.ManagedListScreens`), và mỗi báo cáo còn giữ là một
màn hình (`ReportMapBuilder.ReportScreens`) — cả hai gieo thẳng vào `PlannedScope` lúc chốt, tức vào chính
các DÒNG của bảng màn hình. Báo cáo sau đối tượng vì ô *"lấy số từ"* của nó trỏ về một đối tượng đã chốt.
**Màn hình sau đó là chỗ người dùng rà TRỌN phạm vi đúng một lần.** Phân quyền gần cuối, vì
các DÒNG của nó là màn hình — hỏi trước khi phạm vi màn hình đứng yên thì bảng thiếu nửa số dòng, mà quyền
của một màn hình chưa tồn tại thì không ai trả lời được. **Thông báo cuối cùng**, vì nó vay cả hai chiều:
các DÒNG là chuyển trạng thái của bảng đối tượng, còn danh sách người nhận cần các VAI TRÒ của bảng phân
quyền — vai trò của ứng dụng đang thiết kế chỉ tồn tại trong hội thoại, không bảng nào trong DB liệt kê
chúng (vai trò của chính ICOGenerator không nằm trong DB, và cũng không liên quan).

#### Vì sao thứ tự cũ (màn hình trước đối tượng) là một lỗi

Lý do cũ ghi ở đây là *"cái người dùng nhìn thấy trên màn hình quyết định thông tin nào thật sự cần lưu"* —
nhưng chiều đó chưa bao giờ tồn tại trong code: `ScreenScopeMapBuilder` không đọc `EntityMap`, khối
`## LƯỢT NÀY` của bảng đối tượng không nhắc tới bảng màn hình, và một dòng của bảng màn hình còn không chở
nổi một trường thông tin nào để mà quyết định. Chiều CÓ THẬT chạy ngược lại, và chạy tất định qua hai hàm
gieo màn hình nêu trên.

Cái giá của thứ tự cũ, đo được trên dự án thật (JD Libary): người dùng chốt bảng màn hình như một phạm vi
trọn vẹn — bảng tự giới thiệu đúng như vậy (*"thiếu cả một màn hình thì bấm + thêm màn hình"*) — rồi mấy
lượt sau bảng đối tượng gieo thêm năm màn hình quản lý danh mục, bảng màn hình phải mở lại, và cổng
[KHÔNG MÂU THUẪN](#hai-cổng-chất-lượng-phía-yêu-cầu-đủ-và-không-mâu-thuẫn) bắn một mâu thuẫn
(*"trước đây anh/chị xác nhận đây là toàn bộ màn hình…"*). Tức một suất trong tối đa 5 mâu thuẫn tiêu cho
một xung đột do chính thứ tự đẻ ra, cộng một lượt rà lặp. Đường mở lại của cổng màn hình vẫn còn nguyên,
nhưng nay nó lùi về đúng vai **lưới an toàn** cho phần phạm vi trôi THẬT (một màn hình lộ ra từ hội thoại
sau lượt chốt), thay vì là đường chính của một luồng biết trước là sẽ trôi.

Điều kiện mở của từng cổng suy từ chính bản đồ bao phủ, và **cố ý rải ra chứ không dồn xuống cuối buổi**:
cổng luồng mở khi «Chức năng & luồng nghiệp vụ chính» + «Đối tượng người dùng & vai trò» đã `[RÕ]` và
«Luồng ngoại lệ» đã được chạm tới; cổng đối tượng mở khi «Dữ liệu / danh mục chính» `[RÕ]`, «Vòng đời &
trạng thái» đã được chạm tới và cổng luồng đã đủ điều kiện mở; cổng báo cáo mở khi **bảng đối tượng đã
chốt** và nhóm «Báo cáo / thống kê» đã `[RÕ]`; cổng màn hình mở khi `PlannedScope` có mục và — ở **lần bày
đầu** — cổng luồng đã đủ điều kiện mở, hai nhóm của cổng đối tượng («Dữ liệu / danh mục chính», «Vòng đời &
trạng thái») đã được chạm tới, và nhóm «Báo cáo / thống kê» cũng đã được chạm tới; cổng phân quyền giữ
nguyên điều kiện cũ (mọi nhóm áp dụng KHÁC đã `[RÕ]`); cổng thông báo mở khi **bảng phân quyền đã chốt** và
bảng đối tượng gieo ra được ít nhất một sự kiện. Hai vế mượn dùng chung hai hàm —
`FlowMapGate.CoverageReady` và `EntityMapGate.CoverageDecided` (hàm sau đã bao hàm hàm trước), xem
[dưới](#thứ-tự-ưu-tiên-không-thay-được-điều-kiện-mở). Các bảng điền sẵn nối đuôi nhau ở cuối
buổi chính là cái chip *"Đồng ý phương án này"* phóng to nhiều lần — người dùng nghiệp vụ bận sẽ bấm "Đúng
rồi" cho xong từ bảng thứ hai.

Hai cổng cuối xét theo **bảng đã chốt** chứ không theo bản đồ, và với cổng thông báo đó là chủ ý: nó phải
đứng sau bảng phân quyền THẬT SỰ, không chỉ đứng sau trong danh sách ưu tiên — một lượt bày bảng phân quyền
hỏng (model không trả nổi bảng dùng được) mà để bảng thông báo chen lên trước thì danh sách người nhận gieo
ra mất sạch phần vai trò, chỉ còn bốn mục quan hệ.

Hệ quả cần biết: khi cổng phân quyền mở thì điều kiện của các cổng kia đương nhiên cũng đúng (điều kiện của
chúng là tập CON — cổng phân quyền đòi mọi nhóm áp dụng khác `[RÕ]`), nên bảng nào chưa chốt sẽ lần lượt
được hỏi TRƯỚC nó — và cổng phân quyền lại là thứ duy nhất mở nút "Write Requirement". Không có đường nào
soạn tài liệu mà bỏ qua bốn bảng đầu. `InterviewTableGateTests` giữ bất biến này.

#### Thứ tự ưu tiên không thay được điều kiện mở

Danh sách ưu tiên ở `Select` chỉ phân xử được khi **hai cổng cùng mở**; nó không nói gì về ca cổng đứng
trước còn ĐÓNG vì bản đồ chưa đủ. Điều kiện đời đầu của cổng màn hình chỉ đòi «Chức năng & luồng nghiệp vụ
chính» `[RÕ]` — nhóm lên `[RÕ]` ngay ở lượt người dùng kể luồng, trong khi vai trò và ngoại lệ còn phải hỏi
thêm vài lượt — nên cổng màn hình mở TRƯỚC cổng luồng và thứ tự phụ thuộc bị đảo trong im lặng.

Ca thật (dự án JD Libary 1): bảng màn hình bày ở lượt 12, bảng luồng mãi lượt 20. Thiệt hại nằm đúng ở ô
*"chức năng này phục vụ bước nào"* — lượt 12 chưa có bước nào tồn tại để gắn nên cả cột ra rỗng; luồng chốt
xong thì `UncoveredActions` báo gần như MỌI bước chưa ai phụ trách, và người dùng phải rà bảng màn hình lần
thứ hai chỉ vì lần đầu bày quá sớm. Vì vậy lần bày đầu của cổng màn hình **mượn nguyên điều kiện bản đồ của
cổng đứng trước**: nay là `EntityMapGate.CoverageDecided` (chính nó đã bao `FlowMapGate.CoverageReady`) cộng
vế nhóm «Báo cáo / thống kê» đã được CHẠM TỚI. Các cổng cùng mở một lượt, rồi để `Select` phân xử.

Ba ranh giới của cách vá này:

- **Không đòi bảng đứng trước đã CHỐT**, chỉ đòi điều kiện bản đồ. Treo cổng này vào một artifact do model
  sinh ra là biến một lượt bày bảng hỏng thành chuỗi kẹt vĩnh viễn.
- **Chờ cổng đứng trước NGÃ NGŨ, không chờ nó SẴN SÀNG** — mọi vế mượn chỉ đòi *chạm tới*
  (`[RÕ]` hoặc `[KHÔNG ÁP DỤNG]`), không đòi `[RÕ]`. Một nhóm ở `[KHÔNG ÁP DỤNG]` nghĩa là bảng của nó sẽ
  KHÔNG BAO GIỜ tới (dự án không có danh mục nào, hoặc không cần báo cáo nào); chờ nó là xoá luôn bảng màn
  hình khỏi buổi phỏng vấn — trong khi cổng phân quyền vẫn coi `[KHÔNG ÁP DỤNG]` là đã trả lời và cứ thế
  mở, nên bảng phân quyền quay về đứng trên `PlannedScope` thô. Nhóm còn `[MỘT PHẦN]`/`[CHƯA HỎI]` thì bảng
  màn hình chờ thật — nhưng đó không phải chỗ kẹt MỚI: cổng phân quyền vốn đã đòi mọi nhóm áp dụng ngã ngũ.
- **Chỉ áp cho lần bày ĐẦU.** Đường mở lại (phạm vi trôi sau lúc chốt) giữ điều kiện cũ: tới đó thì mọi điều
  kiện của lần bày đầu đã từng đúng, và đòi lại cả bộ là để một nhóm bị lượt distill hạ xuống `[MỘT PHẦN]`
  chặn mất đường thu hồi phần phạm vi trôi.

**Cổng đối tượng mượn điều kiện của cổng luồng** theo đúng khuôn đó, vì nó hở theo cùng một kiểu: hai nhóm
của nó («Dữ liệu / danh mục chính», «Vòng đời & trạng thái») rời hẳn nhóm vai trò, nên có ca dữ liệu và vòng
đời đã rõ trong khi vai trò còn `[MỘT PHẦN]` — cổng luồng đóng, và bảng ĐỐI TƯỢNG bày ra đầu tiên trong khi
cột *"khi nào chuyển vào"* của nó lấy điều kiện từ chính các bước luồng chưa tồn tại.

Chỗ hai cổng tách nhau, cố ý không chặn: `PlannedScope` rỗng ⇒ cổng màn hình đóng vì không có DÒNG nào để
hỏi, còn cổng đối tượng vẫn mở (nó không lấy dòng từ phạm vi màn hình). Bắt cổng đối tượng chờ một danh sách
có thể không bao giờ đến là dựng thêm một chỗ kẹt để đổi lấy một thứ tự đẹp — mà thứ tự ở đây vốn đã đúng.

### Vì sao bốn bảng GIỮA không được là điều kiện để một nhóm lên `[RÕ]`

Hai nhóm cuối — «Phân quyền theo nghiệp vụ» và «Thông báo / nhắc nhở» — có luật khắt khe một chiều: chưa có
bảng thì không bao giờ `[RÕ]`. Luật đó đúng vì cả hai **không được hỏi bằng câu hỏi**. Bốn nhóm của các bảng
giữa (luồng, màn hình, đối tượng, báo cáo) thì có: chúng được hỏi suốt buổi, và bảng chỉ **xác nhận lại**
thứ hội thoại đã trả lời. Với bảng báo cáo thì đó còn là **điều kiện mở cổng**, không chỉ một lựa chọn ghi
trong bản đồ — xem mục riêng của nó bên dưới.

Áp luật một chiều cho chúng là dựng một vòng khóa kín: cổng đòi nhóm `[RÕ]` mới mở, bản đồ đòi có bảng mới
`[RÕ]`, không bên nào đi trước được. Đó chính là cái bẫy mà `PermissionMatrixGate` né bằng cách cố ý **bỏ
qua hai dòng chốt-bằng-bảng** khi xét. Triệu chứng nếu làm sai rất khó truy: nút "Write Requirement" không
bao giờ sáng, panel tiến độ đứng ở 11/12 vĩnh viễn.

Cùng bất biến đó buộc hai thay đổi khi nhóm «Thông báo / nhắc nhở» chuyển sang chốt bằng bảng, và cả hai
đều được `InterviewTableGateTests` giữ: `EntityMapGate` **bỏ** điều kiện "nhóm thông báo đã chạm tới" (nhóm
ấy nay đứng ở `[CHƯA HỎI]` suốt buổi, mà bảng đối tượng lại là nguồn DÒNG của bảng thông báo), và
`PermissionMatrixGate` thêm dòng thông báo vào danh sách bỏ qua (bảng thông báo đứng SAU bảng phân quyền).
Bỏ sót một trong hai là khóa chết cả ba bảng cuối.

### Ba chốt chặn tất định dùng chung

Cả bốn builder áp cùng bộ luật, vì cả bốn hỏng theo cùng một kiểu:

- **Luật bằng chứng.** Server chỉ khóa một ô/dòng khi model kèm được TRÍCH DẪN. Cờ suông bị bỏ. Không
  có ranh giới này thì một bảng điền sẵn toàn bộ trông như đã chốt, và người dùng bấm gửi trong ba giây.
  Ở **bảng thông báo**, khóa còn đòi thêm một điều kiện — dòng phải CÓ người nhận — vì dòng khóa không có
  checkbox: xem [ngõ chết của dòng khóa](#bảng-thông-báo-bảng-cuối-cùng).
  Luật áp cho các bảng có ô KHÓA được — bảng phân quyền, bảng đối tượng, bảng thông báo. Bảng luồng và
  bảng màn hình **không** nằm trong đó vì mọi dòng của chúng đều ra ở trạng thái được giữ, nên một trích
  dẫn ở đó không đổi được trạng thái nào — xem
  [Vì sao bảng luồng và bảng màn hình không có dấu ✓ bằng chứng](#vì-sao-bảng-luồng-và-bảng-màn-hình-không-có-dấu--bằng-chứng).
- **Cờ tích ở lượt BÀY BẢNG luôn là TÍCH SẴN**, bất kể model trả gì. Cờ đó là chỗ NGƯỜI DÙNG loại bớt,
  không phải chỗ model tự phủ nhận đề xuất của mình — mà structured output buộc điền đủ trường, nên một
  model điền `false` cho có sẽ âm thầm bỏ tích sạch bảng và người dùng gửi đi một phạm vi RỖNG trong khi
  tưởng mình vừa xác nhận cả ứng dụng. Chỉ đường GỬI (`Sanitize`) mới tôn trọng lựa chọn của họ.
- **Bản kể chỉ chở các Ô, không chở văn xuôi của BA.** Tin nhắn `RenderUserMessage` được lưu dưới **vai
  NGƯỜI DÙNG**, nên chữ nào lọt vào đó cũng thành lời của họ với mọi tầng phía sau: bộ chắt bản đồ bao phủ
  trích làm `{nguồn: …}`, bộ chắt "điểm cần làm rõ" lấy làm một vế mâu thuẫn, BA đem ra chất vấn ở lượt kế.
  Nhưng thứ người dùng thật sự QUYẾT chỉ là các ô tích/sửa được. Hai ô **mô tả đối tượng** và **việc của
  màn** là văn xuôi BA điền sẵn, nằm cạnh tên như một cái nhãn xám và đi cùng chuyến gửi mà không ai rà —
  nên chúng **không** vào bản kể, và trong khối ngữ cảnh chúng đứng ở dòng riêng gắn nhãn *(BA tự đặt, chưa
  ai rà)* kèm một câu cấm trích chúng làm bằng chứng. Ca thật (JD Library 1): ô mô tả ghi *"JD — Mô tả công
  việc được Manager tạo, kiểm tra, verify và approve…"* trong khi người dùng đã kể ở chat VÀ tự tay rà bảng
  luồng rằng HRBP verify rồi HoD approve; câu đó quay về trong vai họ, BA hỏi *"luồng nào đúng với thực tế
  ạ?"*, và mục tồn đọng sinh ra từ đó hạ ba dòng bản đồ xuống `[MỘT PHẦN]` — khóa cổng "Write Requirement" ở
  đúng lượt mọi nhóm vừa đủ. Mô tả vẫn được **lưu** (nó là văn xuôi cho `## 8. Data Model Summary`), chỉ
  không được đóng dấu là lời người dùng. Phần prompt của cùng chốt chặn này: BA phải viết ô mô tả nói đối
  tượng **là gì**, không nói **ai làm gì** (`requirement-chat.v4.md`), và ba bộ đọc — chat, bản đồ bao phủ,
  triển vọng phỏng vấn — đều bị cấm coi ô đó là lời người dùng.
- **Dòng bịa bị loại, dòng bị bỏ quên vẫn phải có mặt** — và có mặt ở trạng thái TÍCH SẴN. "BA quên nêu"
  không phải "người dùng đã loại"; đưa vào ở trạng thái bỏ tích là ra quyết định thay họ ở đúng chỗ họ
  không nhìn thấy để phản đối. Chốt chặn này chặn MODEL, nên bảng màn hình có đúng một ngoại lệ cho dòng
  người dùng TỰ THÊM — xem [dưới](#thêm-dòng-ngay-trên-bảng-và-chỗ-chốt-chặn-màn-hình-bịa-phải-nhường).

Gửi đi vẫn **hai bước** như bảng cột và bảng phân quyền: `POST Requirements/ConfirmFlowMap` /
`ConfirmScreenScope` / `ConfirmEntityMap` / `ConfirmNotificationMap` lưu vào cột tương ứng (không gọi LLM), rồi trình duyệt gửi tiếp
**một tin nhắn người dùng** do SERVER soạn qua đúng đường chat thường — hội thoại vẫn chỉ có một đường ghi.
Lưu hỏng thì dừng hẳn, không gửi tin nhắn. Fail-open toàn tuyến: model không trả bảng dùng được ⇒ lượt chạy
như một lượt chat thường và cổng mở lại ở lượt sau.

**Mỗi bảng phải có mặt ở CẢ HAI đường render, và frame `done` là đường dễ quên.** Bảng được LƯU vào lượt hội thoại nên sau F5 nó luôn hiện đúng — điều đó che mất ca một bảng không được chở qua frame `done`: lượt bày bảng về tới client mà không có bảng, `render*` thấy mảng `undefined` nên bỏ qua, panel vẫn ẩn, và người dùng đọc đúng một câu BA mời *"rà bảng bên dưới rồi bấm Gửi bảng …"* trỏ vào chỗ trống. Đây chính là hình dạng "câu hỏi không có chỗ trả lời" mà lượt đọc bảng tính đã vấp một lần. Bảng báo cáo đã hỏng đúng như vậy (`reportMap`/`reportEntityOptions` thiếu trong frame), và triệu chứng trên màn hình còn tệ hơn: bảng chỉ hiện ra vì cú bấm "Write Requirement" tải lại trang — tức người dùng nhìn thấy nó SAU khi đã lỡ soạn tài liệu thiếu nó. `ChatStreamFrameCoverageTests` giữ bất biến này: mọi trường của `BAChatTurnResult` phải được frame `done` đọc tới.

`ConfirmNotificationMap` là đường gửi duy nhất còn **từ chối** một bảng người dùng đã bấm gửi: bảng còn dòng
tích "Cần" mà chưa chọn người nhận thì không lưu gì, và câu lỗi (gọi tên đúng các sự kiện còn thiếu) hiện
ngay cạnh nút — xem [bất biến của bảng thông báo](#bảng-thông-báo-bảng-cuối-cùng).

Ba bảng đều **treo theo DỰ ÁN** (cột còn null) chứ không theo lượt — riêng bảng màn hình treo theo **bảng
server vừa bày** vì nó mở lại được sau khi đã chốt (xem [Bảng màn hình](#bảng-màn-hình-vá-cái-nền-mà-bảng-phân-quyền-đang-đứng-lên)) — và lượt có bảng thì **bỏ** chip và thẻ hỏi
gộp — chip bấm là GỬI NGAY, để cả hai cùng sống thì một cú bấm nhầm cuốn mất lượt trước khi
người dùng rà xong. Cùng luật với bảng cột và bảng phân quyền.

### Bảng luồng: chuỗi bước người dùng tự tay duyệt, và đường của nó tới POC

Bảng này đã **thay hẳn** sơ đồ luồng chỉ-đọc mà BA từng vẽ ở lượt mời "Write Requirement" (`flowDiagram`,
đã gỡ — xem [Vì sao sơ đồ luồng ở lượt mời bị gỡ](#vì-sao-sơ-đồ-luồng-ở-lượt-mời-bị-gỡ)). Sơ đồ đó chỉ **vẽ
ra để nhìn**: một luồng chính, không ngoại lệ, đính chính bằng lời trong khung chat rồi chờ BA vẽ lại. Bảng
luồng khác ở chỗ quyết định — bước **sửa được và bỏ được** tại chỗ.

Phần trả tiền lớn nhất nằm ở đường ra: mỗi luồng đã chốt trở thành một ví dụ ĐỊNH TÍNH của
`## 13. Worked Examples`, tức **POC bị chấm theo đúng chuỗi bước người dùng gật**. Trước đó `WorkedExamples`
là danh sách do LLM chắt từ transcript, không ai duyệt và cũng không còn đường sửa tay
(`UpdateWorkedExamplesUseCase` đã gỡ) — nghĩa là oracle chấm POC đang là bản BA hiểu, không phải bản người
dùng xác nhận.

Bảng bắt buộc chở NGOẠI LỆ vì chuẩn `[RÕ]` của nhóm «Luồng ngoại lệ» đòi một tình huống hỏng cụ thể kèm
cách xử lý, mà đó là loại thông tin không bao giờ tự nhiên xuất hiện trong phỏng vấn — người dùng kể đường
đi thuận, còn đường hỏng thì họ coi là hiển nhiên. `FlowMapBuilder.Build` vì vậy **không bao giờ cắt ngoại
lệ trước** khi chạm trần: prompt xin ngoại lệ đứng sau luồng chính, nên cắt tuần tự sẽ luôn vứt đúng phần
khó lấy nhất đi đầu tiên. Luồng một bước bị loại — đó là một câu mô tả, không kiểm được bằng oracle và cũng
không cho người dùng chỗ nào để bắt lỗi thứ tự.

**Bỏ bước bằng nút ×, không bằng cột tích.** Bảng từng có một cột đầu tên *"Đúng"*: bước có bằng chứng hiện
dấu ✓ khóa cứng, bước còn lại là một ô tích để bỏ. Cột ấy chết trên bảng thật — model kèm trích dẫn cho mọi
bước nên **mọi dòng đều ra dấu ✓**, không dòng nào bấm được, và một cột 44px chỉ để bày ra một hàng ✓ giống
hệt nhau thì người dùng đọc nó là trang trí (lý do đầy đủ ở [mục dưới](#vì-sao-bảng-luồng-và-bảng-màn-hình-không-có-dấu--bằng-chứng)).
Nay mỗi dòng có nút **×** ở cột cuối, và nó là một cái LẬT chứ không phải xóa thật: dòng bị bỏ vẫn nằm
nguyên trên bảng — mờ đi, gạch ngang, nút đổi thành **↩** để lấy về — và vẫn đi trong payload gửi lên. Hai
điều kiện đó không phải chi tiết trình bày. Dòng còn nằm đó là cách người dùng nhìn lướt thấy ngay mình vừa
loại những gì; còn dòng còn trong payload là cách `RenderUserMessage` gọi tên được nó (`- (bỏ: …)`) trong
tin nhắn đi vào hội thoại — im lặng bỏ đi thì họ không có bằng chứng nào cho thấy mình vừa loại đúng thứ
định loại, đúng lỗi mà bảng cột đã cấm. Cờ đi theo dòng nằm ở một `input` ẩn chứ không ở class, để phép gom
bảng của trình duyệt vẫn đọc đúng một chỗ (`tableChecked`) cho cả sáu bảng.

### Vì sao sơ đồ luồng ở lượt mời bị gỡ

Ở lượt BA mời bấm **"Write Requirement"**, code từng đính thêm một **sơ đồ luồng** vẽ từ `flowDiagram` của
lượt đó: luồng chính dựng thành các bước dọc, mỗi bước một nút *"chưa đúng?"* soạn sẵn câu đính chính vào ô
nhập. Cả đường đó đã gỡ — trường `flowDiagram` ra khỏi schema trả lời, `renderFlowDiagram` và khối server
render ra khỏi khung chat, và `BAConversationLog` thôi ghi cột `AgentConversation.FlowDiagram`.

Ba lý do, theo thứ tự nặng dần:

1. **Nó không bày ra được gì người dùng chưa duyệt.** `InterviewTableGate.Select` trả bảng luồng ĐẦU TIÊN
   và `FlowMapGate.ShouldAsk` chỉ tắt khi bảng đã chốt, nên **không bảng nào khác được bày trước khi bảng
   luồng chốt xong**; mà lời mời thì đòi bản đồ bao phủ đủ, tức đòi hai bảng cuối (phân quyền, thông báo)
   đã chốt. Tới lúc sơ đồ hiện ra, chuỗi bước ấy đã được người dùng tự tay duyệt từng dòng.
2. **Đường đính chính của nó là ngõ cụt.** `Project.FlowMap` chỉ được ghi bởi `ConfirmFlowMapUseCase`, và
   `FlowMapGate` **không có đường mở lại** (khác `ScreenScopeGate`). Bấm *"chưa đúng?"* rồi gõ đính chính
   chỉ đẩy một câu vào transcript: bảng đã chốt không đổi, `## 13. Worked Examples` không đổi, **oracle
   chấm POC vẫn giữ chuỗi bước cũ**. Người dùng tưởng mình vừa sửa, thực tế không. Sơ đồ lại do model vẽ
   lại từ transcript chứ không dựng từ `FlowMap` đã chốt, nên nó còn lệch được với bản người dùng đã gật.
3. **Nó là cổng rà soát bị bấm qua** — đúng thứ đã gỡ *"bản tổng kết trước khi tạo tài liệu"*: một cổng rà
   soát bị bấm qua còn tệ hơn không có cổng nào, vì nó tạo cảm giác ĐÃ rà, và nó được bày ra đúng lúc người
   dùng chỉ còn muốn bấm nút.

**Cái mất: gần như không có.** Dòng transcript `(Sơ đồ luồng nghiệp vụ đã trình bày…)` từng nuôi bản đồ bao
phủ và bước soạn Product Brief, nhưng khối *"bảng đã chốt"* của bảng luồng (`FlowMapBuilder.RenderConfirmedBlock`)
đã vào đúng ba chỗ đó — ngữ cảnh chat của BA, ngữ cảnh soát mâu thuẫn, và tài liệu — với nội dung giàu hơn
hẳn (nhiều luồng, có ngoại lệ, đã được người dùng duyệt).

**Cái còn lại có chủ đích:** cột `AgentConversation.FlowDiagram` và `ConversationTurnRenderer.ParseFlowDiagram`.
Các dự án chạy trước lần gỡ này có sơ đồ đã THẬT SỰ trình bày cho người dùng; xoá đường đọc là làm hỏng bản
xuất hội thoại và transcript của chúng. Lượt mới không bao giờ ghi vào cột đó nữa
(`RequirementReadinessGateTests.ChatAsync_InviteAndMapClear_StoresNoFlowDiagram` giữ điều này).

**Luồng có trôi sau lúc chốt bảng không?** Có — hội thoại còn chạy qua bảng đối tượng, báo cáo, màn hình,
phân quyền, thông báo. Nhưng một bản vẽ chỉ-đọc chỉ *cho thấy* trôi mà không sửa được (lý do 2), còn phần
bắt trôi đã có [cổng soát mâu thuẫn](#hai-cổng-chất-lượng-phía-yêu-cầu-đủ-và-không-mâu-thuẫn) ngay dưới
nút tạo tài liệu. Nếu đo được rằng
trôi là rủi ro thật, cách đúng là mở **đường mở lại cho bảng luồng** như `ScreenScopeGate`, không phải dựng
lại một bức tranh không bấm được.

### Thêm bước, thêm luồng, và đổi thứ tự bước

Bảng luồng là bảng **cuối cùng trong sáu bảng** được mở đường thêm dòng ngay trên bảng; năm bảng kia đã có
từ trước (xem [Thêm dòng ngay trên bảng](#thêm-dòng-ngay-trên-bảng-và-chỗ-chốt-chặn-màn-hình-bịa-phải-nhường)).
Ba nút:

- **+ thêm bước** ở cuối mỗi luồng — dòng trống, ba ô gõ, xóa hẳn được bằng **×**;
- **+ thêm luồng** ở cuối bảng — một khối mới với bốn ô tiêu đề (tên · loại · vai khởi xướng · điều kiện
  kích hoạt) và **đúng `MinStepsPerFlow` dòng bước gieo sẵn**. Ô *"kích hoạt khi"* chỉ hiện khi loại là
  **ngoại lệ**: luồng chính không có điều kiện kích hoạt nào ngoài chính việc người dùng bắt đầu nó, và
  server cũng xoá trắng ô đó ở luồng chính;
- **↑ ↓** ở cuối mỗi dòng — đổi chỗ bước, giới hạn **trong một luồng**.

Lý do là lý do cũ, áp nguyên xi: đường duy nhất trước đó là gõ vào khung chat rồi chờ BA bày lại bảng — một
vòng gọi LLM cho một bước họ đã biết chính xác mình muốn gì, và bảng bày lại thì không có gì bảo đảm giữ
nguyên các ô họ vừa điền.

**Ở bảng này nó rẻ hơn hẳn bảng màn hình.** Bước là chữ tự do và tên luồng không phải khóa nối sang đâu cả
(khác `ScreenScopeRow.Screen`, thứ là khóa ở bốn chỗ độc lập), nên không có chốt chặn nào phải nhường đường:
`FlowMapBuilder.Sanitize` không đối chiếu bước với danh sách cho phép nào. `FlowMapStep.AddedByUser` và
`FlowMapRow.AddedByUser` vì thế chỉ còn **đúng một việc** — để `RenderUserMessage` gọi tên chúng: *"Các luồng
mình tự bổ sung vào bảng: …"* / *"Các bước mình tự bổ sung: …"*. Việc đó vẫn bắt buộc, và ở bảng này còn nặng
hơn các bảng khác: mỗi bước được giữ là một mục `IncludedActions` mà bảng màn hình sau đó **buộc phải** có
chức năng phụ trách (`UncoveredActions`) — thêm một bước ở lượt này là siết một cổng ở lượt sau, và câu kể
kia là chỗ duy nhất nối hai lượt ấy lại. Cờ vẫn bị ép về `false` ở đường **BÀY BẢNG**, cùng lý do với
`ScreenScopeRow.AddedByUser`: ở đó nó là cờ của model, tức một chỗ để model gán chữ ký của người dùng lên
luồng chính nó vừa bịa.

**Vì sao ↑ ↓ chứ không phải kéo-thả.** Đây là đánh đổi có chủ ý, không phải bản rút gọn:

| | kéo-thả | ↑ ↓ |
|---|---|---|
| Ô của bảng là `<textarea>` | `draggable` trên `<tr>` cướp mất thao tác bôi đen chữ trong ô — đúng thao tác chính của cả bảng. Tránh nó thì phải đẻ thêm một **cột tay cầm** trên bảng cố ý chỉ có ba cột | không đụng vào ô |
| Bàn phím | không thao tác được | là `<button>`, có `aria-label` như mọi nút khác của panel |
| Cảm ứng | HTML5 DnD không chạy nếu không kèm polyfill; repo không có thư viện kéo thả nào | chạy sẵn |
| Quy mô | một luồng tối đa `MaxStepsPerFlow` bước và thường chỉ lệch một hai vị trí | một hai cú bấm |

`↑` của dòng đầu và `↓` của dòng cuối bị **khóa chứ không giấu**: giấu đi thì cột cuối đổi bề rộng theo từng
lần đổi chỗ và hai nút còn lại nhảy chỗ ngay dưới con trỏ vừa bấm. Sau mỗi lần đổi chỗ, focus được trả về
đúng nút vừa bấm — đổi chỗ hiếm khi chỉ một nhịp, và một cú bấm phải đi tìm lại nút ở một dòng vừa nhảy chỗ
là một nhịp thừa ở đúng chỗ người ta đang bấm liên tục.

**Thứ tự bước là dữ liệu, không phải cách bày** — đó mới là lý do nút đổi chỗ tồn tại. Nó đi thẳng vào khối
*"bảng đã chốt"* của mọi lượt chat sau đó và vào `## 13. Worked Examples`, tức **oracle chấm POC bị chấm theo
đúng thứ tự này**. BA ráp sai thứ tự mà chỉ sửa được bằng cách gõ đè chữ của hai dòng (sáu ô) là một đường
sửa đắt tới mức không ai đi, và cái sai thì đi tiếp vào tài liệu.

**Cả bốn trần đều chặn ở TRÌNH DUYỆT, ngay tại nút bấm** — cùng luật với bảng màn hình, và ở đây có một
đường hỏng riêng: `NormalizeSteps` đếm **cả bước đã bỏ** rồi `break` khi chạm `MaxStepsPerFlow`, nên phép
đếm phía trình duyệt cũng phải đếm cả dòng đã bỏ; đếm theo con số khác con số server dùng là vẫn còn đúng
cái đường bị nuốt im lặng mà trần này sinh ra để bịt. `MaxFlows` chặn ở nút **+ thêm luồng**;
`MaxExceptionFlows` chặn ngay **tại ô chọn loại** (chọn quá trần thì ô bật về *luồng chính* kèm một câu
giải thích) chứ không đợi lúc gửi, vì quá trần thì `BuildCore` bỏ hẳn luồng ngoại lệ thứ tư — mà ngoại lệ
là phần khó lấy nhất của cả buổi. `MinStepsPerFlow`, tên rỗng và trùng tên thì chặn ở **nút Gửi**
(`validateFlowMap`): cả ba đều bị `BuildCore` `continue` qua không một lời nào, mà bảng thì biến mất ngay
sau khi gửi nên người dùng cũng không còn chỗ nào để thấy mình vừa mất gì.

**Ranh giới giữ nguyên ở một chỗ:** tiêu đề của luồng **BA đề xuất** vẫn chỉ-đọc (tên/loại/vai/điều kiện
nằm ở `data-*`). Sửa được tên ở đó là đổi nhãn của một thứ model đề xuất trong khi tin nhắn gửi đi vẫn kể
lại nó là đề xuất của model. Chỉ luồng người dùng tự thêm mới có bốn ô gõ đó.

### Bảng màn hình: vá cái nền mà bảng phân quyền đang đứng lên

Các DÒNG của bảng phân quyền lấy từ `Project.PlannedScope` — một danh sách do LLM chắt sau mỗi lượt chat mà
**người dùng chưa bao giờ nhìn thấy** kể từ khi panel sidebar bị gỡ. Tức toàn bộ phần phân quyền, thứ đã
được dựng cẩn thận để có bằng chứng trên từng ô, lại đứng trên một nền chưa ai duyệt: một màn hình LLM chắt
nhầm sẽ được người dùng tích quyền cho, còn một màn hình bị bỏ quên thì không bao giờ có mặt để họ phản đối.

Chốt xong, `PermissionMatrixGate.EffectiveScreens` đọc bảng thay cho `PlannedScope` thô: các dòng người dùng
GIỮ, cộng những mục phạm vi mới lộ ra SAU lúc chốt. Mục mới phải được thêm vào (buổi phỏng vấn còn tiếp tục,
và một màn hình lộ ra ở lượt sau mà không vào được bảng phân quyền thì mặc nhiên "không ai được xem"); còn
mục đã BỎ TÍCH thì không bao giờ quay lại, và mở lại thứ họ vừa đóng là đúng lỗi bảng cột đã cấm.

Bảng đứng **thứ tư** trong chuỗi, sau cả hai bảng gieo ra màn hình (đối tượng và báo cáo), đúng để nó là
chỗ rà TRỌN phạm vi một lần duy nhất — xem [thứ tự phụ thuộc](#một-cổng-đúng-một-bảng-mỗi-lượt).

**Bảng màn hình là cổng DUY NHẤT mở lại được sau khi đã chốt** — vì nó là cổng duy nhất mà phạm vi còn trôi
tiếp sau lượt chốt. `ScreenScopeGate` mở lại khi `ScreenScopeMapBuilder.NewScreens` còn mục: màn hình có
trong `PlannedScope` mà bảng đã chốt không đứng tên và cũng không khai là đã gộp vào một dòng nào. Ca thật:
bảng chốt ở lượt 23, tới lượt 33 người dùng mới nói sĩ số tối thiểu/tối đa lấy từ *"danh sách khóa học được
quản lý ở một màn hình riêng"*, và Admin đã được chốt là người quản lý cả phòng học lẫn người dạy — ba màn
hình vào `PlannedScope` mà không bao giờ đi qua bảng. Đường duy nhất còn lại cho chúng là bù vào bảng phân
quyền ở dạng **trắng** (không việc, không chức năng, không bước luồng), trong khi khối ngữ cảnh của bảng đã
chốt lại **cấm** BA hỏi lại việc của từng màn; chúng đi thẳng vào tài liệu và vào bản demo mà không ai biết
để làm gì. Hai điều kiện đi kèm, cả hai đều bắt buộc:

- **Bày lại không được xóa phần đã duyệt.** `Build` dựng bảng từ đề xuất TƯƠI của model, nên lượt bày lại
  được **gieo** bằng `ScreenScopeMapBuilder.SeedRows` (chỉ màn hình còn tích, trong mỗi màn chỉ chức năng
  còn tích) đứng TRƯỚC phần model đề xuất — `Build` giữ dòng đầu tiên của mỗi màn hình nên bản người dùng
  đã rà luôn thắng, và phần tươi chỉ lấp vào màn hình mới. Không gieo thì lần bày lại thay sạch việc của
  từng màn, danh sách chức năng và ô "phục vụ bước nào" bằng bản model vừa đoán lại.
- **Vòng lặp có đáy.** Giữ màn hình mới ⇒ nó thành một dòng của bảng ⇒ hết "mới". Bỏ tích ⇒
  `ConfirmScreenScopeUseCase` ghi ngược `PlannedScope` nên nó rời phạm vi ⇒ cũng hết "mới". Bảng chốt mà
  không dòng nào được giữ (bảng hỏng) ⇒ `NewScreens` trả rỗng, cùng luật fail-open với `EffectiveScreens`.
- **Lượt bày lại phải NÓI RA rằng nó là lượt bổ sung.** Hai điều kiện trên đúng ở tầng dữ liệu nhưng người
  dùng không nhìn thấy tầng đó: họ thấy một bảng màn hình hiện ra lần thứ hai. Model không có cách nào biết
  lượt này khác lượt trước — nó nhận cùng khối lệnh và viết ra cùng một câu dẫn — nên ca thật (JD Libary 1,
  lượt 22) là *"anh/chị rà soát bảng màn hình dưới đây rồi bấm Gửi bảng màn hình"*, đọc lên đúng như BA quên
  mất bảng vừa gửi. Vì vậy lượt bày lại là **ngoại lệ duy nhất** của luật "câu dẫn model thắng":
  `BAChatService.ScreenScopeReshowIntro` ép câu dẫn của cơ chế, gọi tên các màn hình mới (quá 4 thì gộp
  phần dư thành *"và N mục khác"*) và nói rõ phần đã chốt được giữ nguyên. Khối `## LƯỢT NÀY:` cũng đổi
  theo: đầu khối nói rõ đây là lượt BỔ SUNG và thêm mục *"Màn hình MỚI"*, để model khỏi mô tả lại những
  dòng mà `SeedRows` sẽ bỏ đi.
- **Bảng bày lại phải sống sót qua F5.** Panel được view dựng lại từ lượt hội thoại, và điều kiện treo của
  ba bảng kia là *"cột tương ứng trên `Project` còn null"* — đúng với bảng chốt MỘT lần, sai với đúng bảng
  này: ở lượt bày lại thì `Project.ScreenScopeMap` đã khác null từ lần chốt trước, nên điều kiện ấy kết
  luận "bảng đã trả lời xong" cho một bảng người dùng còn chưa kịp rà. Ca thật: BA bày bảng bổ sung 8 màn
  hình, người dùng F5 rồi bảng biến mất — và không còn đường nào để gửi, tức 8 màn hình đó quay lại đúng
  chỗ mà đường mở lại sinh ra để dọn: một dòng **trắng** trong bảng phân quyền. `ScreenScopeMapBuilder`
  `.PendingRows` vì vậy so bản ĐÃ CHỐT với **chính bảng server vừa bày** (`AgentConversation.ScreenScopeMap`
  của lượt gần nhất) chứ không với `PlannedScope` — cùng lý do với đường GỬI ngay dưới: `PlannedScope` bị
  lượt chắt lọc ghi đè ở hậu kỳ, treo panel vào nó là để một lời gọi LLM chạy sau lưng quyết định bảng còn
  hay mất. Vòng lặp vẫn có đáy: gửi xong thì mọi màn hình của bảng vừa bày đều có mặt trong bản chốt (kể cả
  dòng bỏ tích và mục khai gộp), nên panel tự đóng.

**Đường GỬI đối chiếu với BẢNG SERVER ĐÃ RENDER, không với `PlannedScope` đọc lại lúc gửi.** Hai thứ đó
không bằng nhau, và chỗ lệch là một lỗi câm: lượt chắt lọc "triển vọng phỏng vấn" chạy ở HẬU KỲ ngay chính
lượt bày bảng (`RequirementsController` gọi `UpdateInterviewOutlookAsync` sau frame done) và nó **ghi đè cả
danh sách**. Tới lúc người dùng bấm gửi, bảng trên màn hình đã dựng từ một bản `PlannedScope` không còn tồn
tại. Chỉ cần lượt chắt lọc diễn đạt lại một mục — *"…trong nhà máy"* thành *"…theo orgUnit"* — là
`MatchScreen` trượt, chốt chặn "màn hình bịa" quay ra bắn vào chính bảng của server: mọi dòng người dùng vừa
điền bị bỏ, chỗ của chúng là các mục phạm vi mới bù vào ở dạng **trắng**. Họ gửi một bảng đã rà và nhận lại
một danh sách tên suông — không việc, không chức năng, không bước luồng — trong khi khối ngữ cảnh của bảng
đã chốt lại cấm BA hỏi lại việc của từng màn. Không nút nào báo lỗi, và lượt chắt lọc thì **luôn** chạy giữa
lúc bày bảng và lúc bấm gửi. Vì vậy `ConfirmScreenScopeUseCase` lấy danh sách đối chiếu từ
`AgentConversation.ScreenScopeMap` của lượt BA bày bảng — đúng lượt mà view dùng để dựng lại panel sau F5 —
và chỉ quay về `PlannedScope` khi không còn lượt nào (fail-open: mất chốt chặn tên màn hình rẻ hơn nhiều so
với một nút gửi không bao giờ lưu được gì).

Chốt xong, use case **ghi ngược** phạm vi đã duyệt lên `Project.PlannedScope`. Người dùng vừa tự tay rà, nên
bản đó thay cho bản LLM đoán: lượt chắt lọc kế tiếp nhận nó làm gốc để gộp tiếp thay vì diễn đạt lại từ đầu,
và `EffectiveScreens` không còn bù vào những mục chỉ khác CHỮ so với dòng vừa giữ — nếu không, bảng phân
quyền ngay sau đó mọc thêm một loạt dòng trùng nghĩa mà không dòng nào có việc của màn hình. Ghi ngược cũng
là chỗ vá nốt nửa còn lại của luật "mục đã bỏ tích không bao giờ quay lại": trước đây lượt chắt lọc không
đọc bảng nên nó giữ mãi mục người dùng đã đóng, và `EffectiveScreens` phải một mình chắn. Bỏ tích SẠCH bảng
thì **không** ghi ngược — một `PlannedScope` rỗng cắt luôn đường fail-open của `EffectiveScreens` và khóa
chết cổng phân quyền trong im lặng.

#### Ô "phục vụ bước", và lượt xếp chỗ cho bước mồ côi

**Ô "phục vụ bước" cho một phép kiểm TẤT ĐỊNH chạy bằng code, không cần lời gọi LLM nào**
(`ScreenScopeMapBuilder.UncoveredActions`): mọi bước của bảng luồng đã chốt phải được ít nhất một **chức
năng còn tích** nhận phụ trách. Hai danh sách đọc riêng đều "đạt" — bảng luồng đầy đủ, bảng màn hình đầy đủ
— còn chỗ hỏng nằm ở **mối nối**, đúng loại lỗi đắt nhất của cả dây chuyền. Một bước không ai phụ trách
nghĩa là hoặc người dùng sẽ không có chỗ nào để làm bước đó, hoặc bước đó không có thật; cả hai đều phải
xử, và xử lúc bảng còn trên màn hình rẻ hơn hẳn hỏi lại ở POC. So khớp bằng CHỨA-NHAU sau chuẩn hoá chứ
không nguyên văn — người dùng sửa ô bằng lời của họ, và một cảnh báo luôn sai thì lần thứ hai không ai đọc
nữa. Ô là MỘT ô text ngăn bằng dấu chấm phẩy **hoặc xuống dòng** (ô cao theo nội dung nên gõ mỗi bước một
dòng là cách tự nhiên nhất), không phải một danh sách con — người dùng gõ tiếp vào đó dễ hơn bấm thêm dòng,
và phép so khớp chứa-nhau ở trên không cần từng bước là một phần tử riêng.

Bước gắn ở **cấp chức năng**, không phải cấp màn hình, và đó là điều kiện để phép kiểm còn nói được sự
thật: bỏ tích một chức năng là bỏ luôn phần việc nó gánh, nên bước của nó phải lập tức hiện ra là chưa ai
làm. Bản gắn ở cấp màn hình không nói được điều đó — người dùng bỏ đúng chức năng chở bước ấy mà cả bảng
vẫn báo "đủ".

**Bắt được lỗ hổng thì BA TỰ LẤP, không hỏi ngược người dùng.** Phép kiểm nói ra chỗ hỏng, nhưng phần việc
sau đó — bước này là việc của chức năng nào trên màn nào — là phần người dùng đi thuê BA để làm. Trước đây
nó bị đẩy ngược lại thành một dòng nhắc: *"Chưa chức năng nào phụ trách các bước: … Anh/chị điền bước đó
vào ô bên phải của chức năng phù hợp, hoặc nhắn cho mình biết nếu thiếu hẳn một màn hình."* Ca thật (JD
Library 2): bước mồ côi là *"Xem danh sách nhân viên trực tiếp dưới quyền"* — bước 4 của luồng chính chính
người dùng vừa tự tay chốt — và nó hiện ra như một câu đố ngay dưới một bảng mười bảy màn hình, trong khi
chỗ đúng của nó là một chức năng trên `JD Assignment` đang nằm ngay trên bảng đó. Người dùng nghiệp vụ
không có từ vựng để trả lời câu đó, và càng không có cách nào tự nhận ra "phạm vi màn hình còn thiếu một
chỗ".

Hai lớp chặn nó, theo thứ tự:

1. **Bảng kê các bước, đính vào khối `## LƯỢT NÀY:` của lượt bày bảng** (`BAChatService.FlowStepChecklist`).
   Các bước đã có sẵn trong ngữ cảnh qua khối *"bảng luồng đã chốt"*, nhưng ở đó chúng là một câu chuyện
   kể theo từng luồng, trộn với vai trò và kết quả sau mỗi bước. Ở đây chúng là một danh sách phẳng để
   ĐỐI CHIẾU, đúng hình dạng mà `UncoveredActions` sẽ chấm ngay sau đó. Khối này cũng nói thẳng: bước nào
   không màn nào trong phạm vi làm được thì **để trống**, đừng gán bừa — gán sai là dựng một chức năng
   không có thật lên một màn hình có thật, và người dùng đọc lướt qua nó như phần đã đúng.
2. **Lượt XẾP CHỖ** (`ScreenStepPlacementService` + prompt `screen-step-placement.v1.md`), chạy khi vẫn còn
   bước mồ côi sau lượt bày bảng và **trước khi bảng ra tới màn hình**. Phân vai giữ nguyên nguyên tắc của
   cả tính năng: **code** quyết định có lỗ hổng không (`UncoveredActions`) và lời xếp chỗ nào được nhận
   (`ScreenScopeMapBuilder.ApplyPlacements`); **model** chỉ trả lời đúng một câu ngữ nghĩa mà không phép so
   chuỗi nào làm thay được — *bước này là việc của chức năng nào*. Ba nhánh, theo thứ tự ưu tiên: gắn bước
   vào một chức năng đã có · thêm một chức năng mới lên màn hình đúng (ca thường gặp nhất) · dựng hẳn một
   màn hình mới.

`ApplyPlacements` là chốt chặn của lượt đó, và nó chỉ nhận đúng ba thứ: lời xếp chỗ **trỏ vào một bước
trong danh sách mồ côi** (không thì lượt vá lỗ hổng thành đường vòng cho model viết lại cả bảng — kể cả
phần người dùng đã tự tay rà ở lần chốt trước), thao tác **chỉ THÊM** (không dòng nào bị xóa, không cờ tích
nào bị đổi, không câu "việc của màn" nào bị viết đè), và các **trần** sẵn có (`MaxRows`,
`MaxFunctionsPerScreen`, `MaxFlowStepsPerFunction`). Kết quả ra bảng ở dạng **tích sẵn** như mọi đề xuất
khác của BA, `AddedByUser` vẫn `false` — đây là đề xuất của BA, mượn cờ đó là gán chữ ký người dùng lên một
dòng họ chưa nhìn thấy.

Ô "phục vụ bước" nhận **chữ của bảng luồng**, không chữ model vừa gõ lại — dù phép so chứa-nhau vẫn nhận một
bản diễn đạt lại. Ghi bản diễn đạt ấy vào bảng thì hỏng hai chỗ: `UncoveredActions` so bảng với chính danh
sách bước, nên một dòng khác chữ là một báo động giả chực chờ; và người dùng đang đọc đúng các bước họ vừa
tự tay rà ở bảng trước, thấy chúng hiện ra bằng chữ khác là mất đường đối chiếu.

Dòng mới **được nói ra** ở câu dẫn của lượt (`BAChatService.ScreenScopePlacementNotice`): bước nào vừa được
xếp vào màn nào · chức năng nào, và màn hình nào là màn mới thêm. Câu này dựng từ **bảng sau khi xếp** chứ
không từ lời model trả về — chỗ ở thật của một bước là chỗ `ApplyPlacements` đã ghi nó vào, và mục bị chốt
chặn bỏ đi thì không được kể như đã làm. Cùng luật với `RenderUserMessage` kể lại các dòng người dùng tự
thêm: thứ vào phạm vi bằng một đường khác thường phải được gọi tên ở đúng lượt nó vào.

Màn hình mới ở nhánh 3 là **ngoại lệ thứ hai** của chốt chặn "màn hình bịa" (ngoại lệ thứ nhất: dòng người
dùng tự thêm). Nó hẹp đúng bằng lý do sinh ra nó — dòng ấy phải chở một bước NGƯỜI DÙNG đã chốt ở bảng
luồng mà không màn hình nào đang có phụ trách nổi — nên thứ bảo lãnh cho nó là phép kiểm tất định vừa chạy,
không phải một danh sách cho phép. Dòng đi qua đường GỬI bình thường: `ConfirmScreenScopeUseCase` đối chiếu
payload với **bảng server đã render**, mà dòng mới nằm trong chính bảng đó.

**Dòng nhắc dưới bảng ở lại làm CHỖ RƠI CUỐI CÙNG**, không còn là lối ra mặc định: tới đó chỉ còn những bước
mà chính BA cũng không xếp nổi — bước làm ngoài phần mềm, hoặc một màn hình còn thiếu hẳn — và đó đúng là ca
duy nhất đáng hỏi. Lượt xếp chỗ **fail-open** toàn phần (lời gọi lỗi, model trả rác, không xếp được mục
nào ⇒ bảng nguyên trạng, dòng nhắc hiện ra như trước), nên dòng nhắc cũng là thứ nói thật khi lượt phụ ấy
hỏng. Nó vẫn **không chặn** nút gửi: một câu hỏi, không phải một lỗi.

### Ba cột của bảng màn hình, và vì sao cột "Màn hình" chỉ được chứa màn hình

Bảng có đúng ba cột: **Cần · Màn hình · Chức năng** (cột thứ tư chỉ chứa nút xóa của các dòng người dùng tự
thêm, xem dưới). Việc của màn (`Purpose`) là dòng phụ dưới tên màn chứ
không chiếm một cột, để nửa bảng bên phải dành cho phần người dùng phải rà kỹ nhất. Mỗi chức năng là **một
dòng con có ô tích riêng** kèm ô "phục vụ bước". Trước đây cả cụm chức năng nằm trong MỘT ô text: muốn loại đúng một chức năng thì
phải sửa tay giữa một chuỗi chữ, và thao tác đó không để lại quyết định nào máy đọc được — còn bỏ tích cả
màn hình thì mất luôn những chức năng vẫn cần. Danh sách chức năng đã chốt cũng là thứ bảng phân quyền lấy
làm vế `function`, thay vì để model tự nghĩ ra một danh sách khác ngay tại lượt bày bảng.

**Dòng của bảng là MÀN HÌNH — không phải tính năng, không phải luồng.** Ranh giới này là chốt chặn: cột
`Screen` là khóa nối sang bảng phân quyền và sang các màn của bản demo, nên một mục kiểu *"Tính năng
Generate Training Implement từ Training Plan Detail"* lọt vào sẽ thành một dòng phân quyền và một trang
trống trong POC, trong khi nó vốn là **một cái nút trên Training Plan Detail**. Nguồn của lỗi này nằm ở
lượt chắt lọc `PlannedScope` (prompt `interview-outlook.v1.md`), nên luật "chỉ màn hình, chức năng thì gộp
vào màn chứa nó" sống ở đó. Tầng bảng dọn nốt phần lọt lưới bằng `ScreenScopeRow.Covers`: dòng khai nguyên
văn các mục phạm vi mà nó đã gộp vào mình, và chốt chặn "màn hình bị bỏ quên" thôi bổ sung đúng những mục
ấy — không có `Covers` thì mục vừa gộp vào cột chức năng sẽ mọc lại thành một dòng trắng ngay bên dưới.
Hai điều kiện đi kèm, cả hai đều tất định: mục đã gộp **hiện trên bảng** (dòng *"gộp vào màn này: …"*) vì
một mục rời khỏi phạm vi mà người dùng không nhìn thấy là đúng loại quyết định thay họ mà cả bảng sinh ra
để chặn; và **dòng luôn thắng lời khai gộp** — một màn hình có dòng của chính nó thì không lời khai nào làm
nó biến mất được, nếu không thì chỉ cần model khai bừa một tên là mất trắng một màn hình.

### Tên màn hình là nhãn menu của bản demo, nên nó ngắn và bằng tiếng Anh

Cột `Screen` chở **một chuỗi làm hai việc**: nó là khóa nối của cả buổi phỏng vấn (dòng bảng phân quyền,
`Covers`, phép đối chiếu độ phủ POC ở `PocSpec.Matches`) **và** là nhãn hiển thị — bước sinh POC chép nguyên
văn tên ở `## 6. Screens To Generate` ra `navItems` của sidebar, không dịch, không rút gọn
(`Prompts/Developer/poc-preview.v1.md`). Luật đặt tên vì thế phải thoả cả hai đầu cùng lúc:

> **Tiếng Anh, 2–4 từ, là một DANH TỪ CHỈ NƠI CHỐN — không phải một câu mô tả.**
> `JD Library` · `Standard JD` · `HRBP Approval` · `JD Assignment` · `My JD` · `Skill Catalog` ·
> `Remaining Leave Report`.

Hai vế của luật, mỗi vế chặn một lỗi khác nhau:

- **Ngắn và tiếng Anh** — chặn đầu HIỂN THỊ. Tên kiểu *"Trang tạo và chỉnh sửa JD của Manager"* hiện lên
  sidebar của POC đúng nguyên văn như thế, trong khi các ứng dụng nội bộ nơi bản demo được đem đi trình bày
  đều dùng tiếng Anh. Phần mô tả không mất đi: nó đã có ô riêng ngay dưới tên trong bảng màn hình
  (`Purpose`, [ba cột](#ba-cột-của-bảng-màn-hình-và-vì-sao-cột-màn-hình-chỉ-được-chứa-màn-hình)) và thành
  các bullet dưới heading `### 6.n` của spec. Tên gánh thêm phần mô tả là lặp lại một thứ đã có chỗ.
- **Danh từ chỉ nơi chốn (thường là hậu tố `Library`/`List`/`Approval`/`Assignment`/`Detail`/`Catalog`/`Report`/`Dashboard`)**
  — chặn đầu KHÓA NỐI, và nó là lý do bản rút gọn không được rút tới mức tên trần. Một dòng tên `Skill` trùng
  nguyên văn tên đối tượng `Skill` ở bảng đối tượng vừa chốt ở lượt trước: trong cùng một tài liệu sẽ có
  "Skill" là thực thể và "Skill" là màn hình, và không chốt chặn nào phân biệt nổi hai thứ đó — cùng đúng
  cái lỗi mà [luật "cột Màn hình chỉ được chứa màn hình"](#ba-cột-của-bảng-màn-hình-và-vì-sao-cột-màn-hình-chỉ-được-chứa-màn-hình)
  sinh ra để chặn.

**Luật sống ở ba chỗ, theo đúng ba nguồn sinh ra tên** — sửa một chỗ mà bỏ hai chỗ kia là để phạm vi chạy ra
hai kiểu tên:

| Nguồn tên | Nơi giữ luật | Hình dạng |
|---|---|---|
| LLM chắt từ hội thoại | `Prompts/BusinessAnalyst/interview-outlook.v1.md`, mục `plannedScope` | tự đặt theo luật trên |
| Danh mục `app` của bảng đối tượng | `EntityMapBuilder.ManagedListScreens` (**tất định**) | `<tên danh mục> Catalog` — nửa đầu là TÊN THÔNG TIN, nên vế "tiếng Anh" của luật này đứng được là nhờ [luật đặt tên của bảng đối tượng](#ba-cột-tên-của-bảng-đối-tượng-cũng-là-tiếng-anh) |
| Dòng của bảng báo cáo | `ReportMapBuilder.ReportScreens` (**tất định**) + luật `report` trong `requirement-chat.v4.md` | `<tên> Report`, trừ tên đã tự đọc được như màn hình |

Hai bước sau chỉ CHÉP: `ai-design-spec.v1.md` lấy cột `Screen` của bảng đã chốt làm heading `### 6.n`, rồi
`poc-preview.v1.md` lấy heading đó làm nhãn `navItems` và `data-view` của section.

Hai nguồn tất định ở trên sinh màn hình theo **lô** (5–8 danh mục, 3–5 báo cáo là bình thường), nên ở bản
demo chúng được gom vào một mục menu xổ xuống thay vì rải phẳng — cơ chế và ngưỡng ở
[workspace-and-poc.md](workspace-and-poc.md#poc-demo). Phạm vi màn hình thì không đổi: mỗi danh mục/báo cáo
vẫn là một dòng riêng của bảng màn hình và một heading `### 6.n` riêng của spec.

**Tên ngắn làm phép so khớp bù gần như hết tác dụng, và đó là đánh đổi có chủ ý.** `MatchScreen` chỉ chấp
nhận cụm chứa nhau khi tên dài từ 8 ký tự (`MinContainsLength`), nên với `My JD` hay `Standard JD` thì chỉ
còn đường khớp ĐÚNG CHỮ — model thêm một chữ dẫn là dòng trượt khỏi danh sách cho phép rồi mọc lại thành
dòng trắng. Bù lại, chính vì thế mà không tên ngắn nào bị ghép nhầm vào một màn hình khác. Phần gánh nằm ở
prompt: khối "LƯỢT NÀY" nói thẳng *chép đúng, không thêm chữ dẫn, không dịch*.

**Chỉ CỘT TÊN đổi sang tiếng Anh — và đó là NĂM cột trên toàn bộ bộ bảng**: `Screen` (bảng màn hình),
`Report` (bảng báo cáo), và ba cột tên của bảng đối tượng — tên đối tượng, tên thông tin, tên trạng thái
([luật riêng](#ba-cột-tên-của-bảng-đối-tượng-cũng-là-tiếng-anh)). Việc của màn, danh sách chức năng, bước
luồng, ô "để trả lời câu hỏi gì" của bảng báo cáo, ô mô tả và ô "khi nào chuyển vào" của bảng đối tượng —
tất cả vẫn là ngôn ngữ nghiệp vụ của người dùng. Người rà bảng là người nghiệp vụ, và bắt họ đọc một bảng
thuần tiếng Anh là đánh đổi đúng thứ mấy bảng này sinh ra để lấy.

**Vì sao không tách thành hai trường (tên khóa + nhãn hiển thị).** Vì `Screen` đang là khóa ở bốn chỗ độc
lập (dòng bảng phân quyền, `Covers`, `PocSpec.Matches`, danh sách cho phép của `ScreenScopeMapBuilder`), nên
hai cái tên nghĩa là hai khóa phải đồng bộ ở cả bốn — và một cột thứ tư cho bảng này đi ngược đúng lý do nó
chỉ có ba cột. Một chuỗi thoả cả hai đầu rẻ hơn hẳn.

### Vì sao bảng luồng và bảng màn hình không có dấu ✓ bằng chứng

Bảng phân quyền, bảng đối tượng và bảng thông báo đều có dấu **✓** cho phần người dùng đã tự nói: ở đó nó
**thay** ô tích, tức là một trạng thái thật — ô ấy đã chốt, không phải đề xuất còn phải chọn. Hai bảng còn
lại chép dấu ✓ ấy về mà **không** có trạng thái nào cho nó thay, mỗi bảng hỏng một kiểu.

Bảng màn hình đặt nó **cạnh** ô tích chứ không thay, vì "anh/chị từng nhắc tới màn này" không đồng nghĩa
"màn này phải có trong bản đầu". Hệ quả là ở bảng này dấu ✓ không đổi trạng thái của ô nào: `Build`
**tích sẵn mọi dòng và mọi chức năng** bất kể có trích dẫn hay không, nên phần duy nhất nó còn làm là một
tooltip chở câu gốc.

Bảng luồng thì hỏng nặng hơn, vì ở đó dấu ✓ **thay** ô tích thật — và ô nó thay là ô KHÓA. Mọi bước ra khỏi
`FlowMapBuilder.Build` đều được giữ sẵn, nên trích dẫn không thêm được trạng thái nào mà chỉ lấy đi một
thao tác: bước có ✓ không còn bỏ được nữa. Mà model thì luôn kèm được trích dẫn cho mọi bước, và nó làm
đúng thế — bảng thật chạy ra **toàn dấu ✓**, tức cả bảng thành chỉ-đọc ở đúng chiều người dùng cần bác.
Một chốt chặn sinh ra để chặn cái chip "Đồng ý phương án này" tự biến thành chính nó.

Tooltip cũng không kiểm được thứ nó hứa, ở cả hai bảng. Người dùng rê chuột và gặp lại đúng câu của chính
mình từ mấy chục lượt trước — thường là không nhớ đã nói trong ngữ cảnh nào, và muốn xác nhận thì phải rời
bảng lăn ngược hội thoại đi tìm. Một dấu hiệu đòi rời màn hình mới hiểu được, trên một cột mà mọi dòng đều
đã ở trạng thái được giữ, là nhiễu chứ không phải bằng chứng.

Nên **cả hai bảng bỏ hẳn** cờ `locked`/`evidence`: prompt không sinh, contract không chở. Bảng màn hình còn
lại cột ô tích; bảng luồng bỏ luôn cột đó — xem [Bỏ bước bằng nút ×](#bảng-luồng-chuỗi-bước-người-dùng-tự-tay-duyệt-và-đường-của-nó-tới-poc).
Ranh giới dừng ở hai bảng này — ba bảng kia giữ nguyên dấu ✓ vì ở đó nó là trạng thái ô, không phải chú thích.

### Thêm dòng ngay trên bảng, và chỗ chốt chặn "màn hình bịa" phải nhường

Nút **+ thêm màn hình** ở cuối bảng và **+ thêm chức năng** ở cuối mỗi màn cho người dùng bổ sung thứ BA
không nghĩ tới mà không phải rời bảng. Trước đó đường duy nhất là gõ vào khung chat rồi chờ BA bày lại bảng
— một vòng gọi LLM cho một dòng họ đã biết chính xác mình muốn gì, và bảng bày lại thì không có gì bảo đảm
giữ nguyên những ô họ vừa điền. Cả **sáu** bảng chốt nay đều thêm dòng được ngay trên bảng; bảng luồng là
bảng cuối cùng được mở đường, và nó rẻ hơn hẳn vì không có chốt chặn nào phải nhường — xem
[Thêm bước, thêm luồng, và đổi thứ tự bước](#thêm-bước-thêm-luồng-và-đổi-thứ-tự-bước).

Thêm được thì phải **xóa** được, nhưng chỉ đúng những dòng người dùng tự thêm. Dòng BA đề xuất vẫn **bỏ
tích chứ không xóa**: dòng bị loại còn phải kể lại được trong tin nhắn gửi đi, nếu không họ không có bằng
chứng nào cho thấy mình vừa loại đúng thứ định loại. Dòng do chính họ vừa gõ thì không có gì để kể — nó
chưa bao giờ là một đề xuất — nên xóa hẳn mới là thao tác đúng.

Phần đắt nhất nằm ở server: chốt chặn **"màn hình bịa"** loại mọi dòng không khớp danh sách cho phép, nên
nếu không phân biệt được nguồn thì nó chặn luôn màn hình người dùng vừa tự gõ — họ thêm một dòng, bấm gửi,
và dòng ấy biến mất không một lời nào nói vì sao. `ScreenScopeRow.AddedByUser` là ngoại lệ **duy nhất** của
chốt chặn đó, và nó hẹp có chủ ý:

- chỉ đường **GỬI** (`Sanitize`) đọc cờ. Ở lượt **BÀY BẢNG** (`Build`) mọi dòng đều do model soạn, nên đọc
  cờ ở đó là dựng cho model một cửa sau đi vòng chốt chặn bằng cách tự khai là người dùng;
- mọi giới hạn khác vẫn áp: tên rỗng bị bỏ, trùng tên bị bỏ, và cả bảng vẫn không vượt `MaxRows`. Trần được
  chặn ngay **tại nút bấm** ở trình duyệt chứ không để server cắt lặng — một dòng vừa gõ mà bị nuốt lúc lưu
  là đúng loại quyết định câm mà cả bảng này sinh ra để chặn;
- cờ **không** bị xoá lúc lưu (khác cờ khóa), vì tin nhắn kể lại phải gọi tên chúng: *"Các màn hình mình tự
  bổ sung vào bảng: …"*. Một màn hình chưa từng có trong đề xuất mà lặng lẽ đi vào phạm vi — rồi từ đó vào
  `PlannedScope`, vào bảng phân quyền, vào POC — là đúng loại thay đổi phải nói ra, cùng luật với các dòng
  bị bỏ tích.

### Bảng đối tượng: mô hình dữ liệu, và nguồn dòng của bảng thông báo

Bảng này đứng SAU hai bảng kia chứ không mở đầu chuỗi như trực giác mách bảo, vì ba lý do: người dùng nghiệp
vụ **kể được quy trình, không kể được đối tượng nào có thông tin nào**; vòng đời trạng thái phụ thuộc luồng
(chuẩn `[RÕ]` của nhóm «Vòng đời & trạng thái» đòi gọi tên trạng thái VÀ điều kiện chuyển, mà điều kiện
chuyển chính là "ai làm bước nào"); và phần lớn thông tin đắt giá đã được chốt ở **bảng cột** nếu người dùng
có gửi file — `EntityMapBuilder` vì vậy đánh dấu các thông tin trùng cột đã tích là đã có nguồn, thay vì bày
ra như đề xuất mới chờ duyệt lần hai (bắt duyệt lại đúng thứ họ vừa tự tay tích là hình dạng vòng lặp câu
hỏi chết mà `CoverageDeadQuestionLoopTests` đã phải dựng lưới một lần).

**Mỗi trạng thái ở đây là một DÒNG của bảng thông báo** ngay sau đó — đó là chỗ "ai được báo" được chốt, và
bảng đối tượng **không** hỏi nó: người nhận thật gần như luôn là một QUAN HỆ với bản ghi (*"người gửi đơn"*,
*"sếp của người đó"*) chứ không phải một vai trò, mà muốn bày ra một danh sách quan hệ/vai trò ĐÓNG thì phải
có bảng phân quyền đã chốt trước. Vì vậy dòng `SeedRows` gieo ra có cột To/CC **rỗng**: một phỏng đoán điền
sẵn ở đó là ký tên người dùng vào danh sách người nhận mà họ chưa chọn.

Vòng đời một trạng thái bị cắt sạch (đối tượng vẫn giữ — nó là đối tượng danh mục): "vòng đời" một trạng
thái là không có vòng đời, và giữ lại là mời người dùng xác nhận một điều vô nghĩa. Luật này chỉ áp ở lượt
BÀY BẢNG — xem ngay dưới.

#### Ba cột TÊN của bảng đối tượng cũng là tiếng Anh

Cùng luật với [tên màn hình](#tên-màn-hình-là-nhãn-menu-của-bản-demo-nên-nó-ngắn-và-bằng-tiếng-anh) và tên
báo cáo, áp cho ba cột tên của bảng này:

> **`Entity`, `Field.Name`, `State` — tiếng Anh, 1–3 từ, dạng HIỂN THỊ Title Case.**
> `Job Description` · `Effective Date` · `Job Title` · `Pending HRBP Approval` · `Available`.
> Mọi ô còn lại — mô tả đối tượng, ý nghĩa thông tin, "khi nào chuyển vào", tên hệ thống nguồn, quy tắc
> sinh, các giá trị của danh sách — **giữ tiếng Việt**.

**Ba đường tiêu thụ, và cả ba đọc cái TÊN chứ không đọc ô mô tả:**

| Đường | Cái tên thành gì |
|---|---|
| `Field.Name` + `source: app` → `ManagedListScreens` | một MÀN HÌNH `"<tên> Catalog"` → `## 6. Screens To Generate` → **nhãn mục menu** của bản demo, Developer chép nguyên văn |
| `Entity` / `Field.Name` → `## 8. Data Model Summary` | tên bảng và tên cột của mô hình dữ liệu ở mọi bước sau |
| `Field.Name` / `State` → POC | **nhãn ô nhập**, tiêu đề cột bảng, **chip trạng thái** và giá trị bộ lọc |

Đường thứ nhất là chỗ luật này vá một lỗi CÓ SẴN chứ không chỉ đổi quy ước: `ManagedListScreens` ghép
`"<tên thông tin> Catalog"` và bắt buộc tên phải ngắn + tiếng Anh (nó đi thẳng ra sidebar), trong khi
prompt lại bảo tên thông tin viết bằng lời nghiệp vụ. Một danh mục tên *"Chức danh"* vì thế đẻ ra đúng mục
menu *"Chức danh Catalog"* trên bản demo, và không chốt chặn nào chặn được.

**Dạng hiển thị chứ KHÔNG phải dạng định danh** (`Effective Date`, không phải `effective_date`): cùng chuỗi
đó còn là nhãn trên bảng mà người dùng nghiệp vụ đang rà và trong bản kể gửi vào hội thoại
(`RenderField` cho ra *"Effective Date (ngày JD bắt đầu có hiệu lực)"*). Tên cột CSDL thì bước sinh spec
dẫn xuất được từ dạng hiển thị, chiều ngược lại thì không.

**Hai cái giá, và chỗ trả:**

- **Ô ý nghĩa hết là phần thêm nếm.** Một tên tiếng Anh cạnh một ô mô tả trống để người rà đối diện đúng
  một từ ngoại ngữ trơ trọi — mất đúng thứ cái bảng sinh ra để lấy. Prompt vì vậy cấm để `meaning` trống,
  không ngoại lệ.
- **Mối nối tới bảng cột đứt.** `EntityMapBuilder` nhận ra một thông tin ĐÃ chốt ở bảng cột bằng cách so tên
  với các cột đã tích; hai đầu nay không cùng ngôn ngữ nên *"Effective Date"* không bao giờ khớp cột
  *"Ngày hiệu lực"*, và ô ý nghĩa mất dấu xuất xứ đúng ở chỗ người dùng cần nhận ra thứ họ vừa tự tay tích.
  Nối lại bằng một ô MÁY: model chép nguyên văn tên cột vào `EntityFieldNote.SourceColumn` (người dùng không
  thấy, không sửa), và giá trị không khớp cột đã tích nào thì bị xoá — ô đó chở dấu *"đã chốt rồi"* nên một
  cái tên bịa sẽ dán dấu ấy lên một thông tin chưa ai duyệt, cùng luật với `evidence`.

**Luật chỉ ràng buộc MODEL, không ràng buộc người dùng.** Nút *"+ thêm thông tin"* cho họ tự gõ tên và
không có cổng nào chặn tiếng Việt ở đó — dựng một cổng như thế là đi ngược đúng lý do mấy cái nút thêm dòng
tồn tại. Nên bảng vẫn trộn ngôn ngữ trong thực tế, và đó là ca CHẤP NHẬN ĐƯỢC: không tầng nào phía sau được
phép giả định cái tên là một định danh tiếng Anh hợp lệ.

**Luật sống ở bốn chỗ** — sửa một chỗ mà bỏ ba chỗ kia là để mỗi lượt ra một kiểu tên:

| Nơi giữ luật | Giữ vế nào |
|---|---|
| `Prompts/BusinessAnalyst/requirement-chat.v4.md`, mục `entityMap` | luật đặt tên đầy đủ + luật `sourceColumn` |
| `BAChatService`, khối `## LƯỢT NÀY: BÀY BẢNG ĐỐI TƯỢNG` | bản rút gọn cho đúng lượt bày bảng |
| `Prompts/BusinessAnalyst/ai-design-spec.v1.md`, mục `## 8` | **chép đúng chữ**, không dịch, không đổi cách viết |
| `Prompts/Developer/poc-preview.v1.md` + `Prompts/UiUx/poc-visual-review.v1.md` | ngoại lệ của luật ngôn ngữ UI: các nhãn TÊN tiếng Anh trong một UI tiếng Việt là hình dạng ĐÚNG, không phải lỗi "lẫn lộn ngôn ngữ" |

#### Hai TRỤC của một thông tin, và vì sao không gộp làm một

Mỗi thông tin có thêm ba ô, và chúng trả lời đúng những gì `## 8. Data Model Summary` trước đây phải TỰ ĐOÁN
từ một cái tên: **bắt buộc nhập hay không**, **người dùng nhập thế nào**, và — chỉ với ô chọn — **danh sách
lấy ở đâu**.

| Trục | Giá trị | Ô kèm theo |
|---|---|---|
| `Input` — nhập thế nào | `text` (mặc định) · `number` · `date` · `choice-one` · `choice-many` · `auto` | `auto` ⇒ ô **quy tắc sinh** (*"HcP-JD-XXX"*) |
| `Source` — danh sách lấy ở đâu (**chỉ** với `choice-*`) | `inline` · `app` · `external` | `inline` ⇒ các **giá trị** gõ tại chỗ; `external` ⇒ **tên hệ thống** nguồn |

**Hai trục chứ không phải một dropdown sáu giá trị.** Trực giác đầu tiên là gộp: *Text / List / Single Select
/ MultiSelect*. Nhưng "List" không cùng loại với hai cái sau — một danh sách tự nhập **vẫn** phải nói rõ chọn
MỘT hay chọn NHIỀU, và đó chính là thứ quyết định hình dạng ô nhập trong bản demo. Gộp lại là đẻ ra một ô mà
không ai trả lời được, rồi POC dựng bừa một trong hai.

**Ba luật tất định**, cùng họ với các chốt chặn khác của bảng và đều nằm ở `EntityMapBuilder.NormalizeFields`:

- **Ô ngoài nhánh đang chọn bị CẮT**, không phải chỉ ẩn đi ở UI. Người dùng đổi *"chọn nhiều"* sang *"gõ
  tay"* thì các giá trị họ gõ lúc trước vẫn còn trong payload, và một danh sách treo dưới một ô gõ tay sẽ
  được cả spec lẫn POC đọc như thật.
- **`Required` ép về `false`** khi thông tin bị bỏ tích *"cần lưu"* hoặc khi kiểu là `auto`. Cả hai là "ô này
  không có nghĩa" chứ không phải một lựa chọn bị bác: bắt buộc nhập một ô người dùng **không hề nhập** là một
  ràng buộc mà POC dựng ra sẽ chặn đúng cái biểu mẫu nó vừa dựng. UI khóa ô ngay lúc bấm để họ nhìn thấy điều
  đó, server ép lại vì payload không đáng tin.
- **Giá trị lạ rơi về MẶC ĐỊNH AN TOÀN** (`text` / nguồn rỗng), không rơi về một giá trị nào đó cho có.

**Nguồn rỗng là HỢP LỆ và có nghĩa "chưa chốt" — và nó KHÔNG chặn nút gửi.** Đây là chỗ bảng này cố tình khác
[bảng thông báo](#bảng-thông-báo-bảng-cuối-cùng), đường gửi duy nhất được phép từ chối một bảng đã bấm gửi: ở
đó dòng khóa không có checkbox nên người dùng không có đường nào thoát ra ngoài việc điền, còn ở đây họ luôn
bỏ tích được cả dòng. Đổi lại, **cả hai bản kể phải gọi tên đúng các ô còn thiếu** — tin nhắn gửi đi và khối
ngữ cảnh (nơi ghi *"⇒ hỏi nốt"*, cùng hình dạng ngoại lệ với đối tượng rỗng ruột) — vì im lặng ở đây là để
một dropdown không ai dựng được đi thẳng vào spec. Ba ca được gọi tên: chưa chọn nguồn, `external` mà chưa
nói hệ thống nào, `inline` mà chưa nêu giá trị nào.

**Từ vựng của hai trục là của HỆ THỐNG, không phải của người dùng.** Prompt vẫn cấm từ vựng kỹ thuật ở `entity`
và `meaning` như cũ; hai trục chỉ sống trong JSON và trong hai dropdown có nhãn nghiệp vụ (*"Gõ tay"*, *"Chọn
1"* — không phải *"Text"*, *"Single Select"*). BA **không** được hỏi *"trường này kiểu gì"* trong khung chat:
bảng đã là chỗ họ chọn.

#### Một "thông tin" là NHIỀU DÒNG: quan hệ cha-con

*"Mỗi JD có 5 trách nhiệm, mỗi cái kèm tỷ trọng %"* không có chỗ nào trong bảng để đứng: một ô `fields` chở
đúng MỘT giá trị, nên nhét cả danh sách vào đó là làm biến mất mọi thứ trên từng dòng. Đây là mẫu phổ biến
nhất của app nghiệp vụ — Đơn hàng → Dòng hàng, Đánh giá → Mục tiêu có trọng số, Phiếu chi → Khoản mục.

Cách chốt: dòng con là **một đối tượng nữa của chính bảng này**, mang `ParentEntity` trỏ về cha, cộng
`MinRows`/`MaxRows` là số dòng mỗi bản ghi cha. Trên giao diện nó là một dòng ngay dưới tiêu đề khối:
*"Là các dòng của ‹JD› — mỗi ‹JD› có [5] đến [5] dòng"*.

**Vì sao là một ô PHẲNG chứ không phải một bảng lồng trong ô "thông tin".** Trực giác đầu tiên là thêm một
kiểu nhập thứ bảy (`list-of-rows`) rồi cho ô nguồn đổi hình thành một bảng con định nghĩa các cột. Nó thua ở
đúng một điểm: **các cột của dòng con cần đúng bộ ràng buộc mà một thông tin đã có** — một cột con rất hay là
dropdown lấy từ danh mục ứng dụng tự quản lý. Dựng chúng thành cấu trúc lồng là nhân đôi cả hai trục xuống
một tầng thứ ba, cộng một tầng bảng nữa trong bảng dài nhất của buổi phỏng vấn. Để dòng con **là** một đối
tượng thì hai trục, cột *Bắt buộc*, chip giá trị — tất cả dùng lại nguyên vẹn.

**Ba chốt chặn** (`EntityMapBuilder.NormalizeParents`), và cả ba **hạ dòng về hồ sơ độc lập** chứ không loại
dòng — một quan hệ sai là một ô điền sai, còn nuốt cả dòng là làm biến mất một đối tượng người dùng đã tích:

- **Cha phải tồn tại và còn được giữ** trong chính bảng này; chính tả lấy của BẢNG, không của model (cùng
  luật `MatchScreen`). Cha đã bị bỏ tích cũng không được: quan hệ sẽ trỏ vào một đối tượng không vào ứng dụng.
- **Không tự làm cha của mình.**
- **Tối đa MỘT cấp** — cha của một dòng con thì không được có cha nữa. POC dựng grid lồng grid là thứ không ai
  duyệt nổi. Luật xét trên ảnh chụp TRƯỚC khi sửa và áp đúng một lượt, nên nó tất định (thứ tự dòng trong
  payload không đổi được kết quả) và làm **mọi chu trình tự vỡ**: `A→B→C` giữ `B→C` và cắt `A`; `A→B→A` thì cả
  hai về độc lập.

Giao diện áp cùng luật một cấp ngay tại dropdown — khối đã có cha không xuất hiện trong danh sách cha của
khối khác — để người dùng không chọn được một thứ sẽ bị hạ xuống lúc lưu mà không lời nào nói vì sao. Không
có đối tượng nào đủ điều kiện làm cha ⇒ **không bày ô**: một dropdown chỉ có đúng một lựa chọn là một câu hỏi
không có câu trả lời thứ hai.

Đường tiêu thụ: `## 8. Data Model Summary` nêu quan hệ 1-n tường minh, `## 6. Screens To Generate` dựng nó
thành **bảng nhúng trong màn hình của cha** chứ KHÔNG phải màn hình CRUD riêng (ngược hẳn với `Source = app`),
và `## 9. API Expectations` cho dòng con đi cùng payload của bản ghi cha.

#### Bảng chốt CẤU TRÚC, hội thoại chốt RÀNG BUỘC

Ví dụ trên còn hai mảnh nữa mà bảng **cố ý không chở**: *"tổng tỷ trọng phải bằng 100%"* và *"luôn có sẵn một
dòng mặc định người dùng không sửa được"*. Phép thử để quyết định một thứ đáng được cấp một ô:

> **Ràng buộc này có xuất hiện ở ít nhất ba dự án khác nhau với CÙNG MỘT hình dạng không?**

`MinRows`/`MaxRows` qua được — mọi quan hệ cha-con đều có số dòng. *"Tổng = 100%"* thì không: app khác là
*tổng ≤ ngân sách*, app khác nữa không ràng buộc gì. Cấp cho mỗi ràng buộc một ô là đẻ ra một ô rác cho mọi
dự án không cần tới nó, và một bảng đầy ô rác là bảng không ai rà.

Chỗ của chúng đã có sẵn: nhóm «Quy tắc nghiệp vụ & ràng buộc» của bản đồ bao phủ, với chuẩn `[RÕ]` đòi **điều
kiện và hệ quả** — đúng hình dạng của cả hai. Vì vậy khối ngữ cảnh của bảng đã chốt phải nói rõ giới hạn của
lệnh *"đừng hỏi lại"*: nó phủ **cấu trúc**, không phủ ràng buộc. Thiếu câu ấy thì model hiểu rộng lệnh sang cả
quy tắc, hai thứ đắt nhất của ví dụ này vĩnh viễn không được hỏi, và POC dựng ra một biểu mẫu cộng lại không
ra gì cả.

#### `app` đẻ ra một MÀN HÌNH, và nó phải chảy ngược lên phạm vi

Chọn *"ứng dụng tự quản lý"* nghĩa là ứng dụng phải có một màn hình CRUD riêng cho danh mục đó. Để quyết
định nằm lại trong cột `EntityMap` là để một màn hình không có mục nào ở `## 6. Screens To Generate` và không
có DÒNG nào trong bảng phân quyền — tức **mặc nhiên "không ai được xem"** một màn hình người dùng vừa đặt
hàng, và không có gì trên màn hình nói vì sao.

`ConfirmEntityMapUseCase` vì vậy gieo mỗi danh mục `app` thành một mục `<tên> Catalog` vào
`Project.PlannedScope` (hậu tố `Catalog` là bắt buộc, không phải trang trí — xem
[Tên màn hình là nhãn menu của bản demo](#tên-màn-hình-là-nhãn-menu-của-bản-demo-nên-nó-ngắn-và-bằng-tiếng-anh)). **Chính hàm gieo này là lý do
[thứ tự phụ thuộc](#một-cổng-đúng-một-bảng-mỗi-lượt) đặt bảng đối tượng TRƯỚC bảng màn hình:** gieo trước
lần bày đầu thì các màn hình danh mục là những dòng bình thường của bảng màn hình, người dùng tích/bỏ tích
ngay tại đó. Đường **mở lại** của `ScreenScopeGate`
([trên](#bảng-màn-hình-vá-cái-nền-mà-bảng-phân-quyền-đang-đứng-lên)) vẫn là lưới an toàn cho ca bảng màn
hình chốt trước bảng này — sau khi sửa thứ tự thì ca đó chỉ còn tới được khi cổng đối tượng mở muộn (nhóm
«Dữ liệu / danh mục chính» lên `[RÕ]` sau lúc bảng màn hình đã chốt). Ba ràng buộc:

- **Ghép thêm, không ghi đè.** Ở ca mở muộn ấy `PlannedScope` chính là danh sách người dùng đã tự tay rà ở
  bảng màn hình (`ConfirmScreenScopeUseCase` ghi ngược lên đây); thay nó bằng mấy dòng danh mục là xoá sạch
  phạm vi đã duyệt.
- **Mục trùng bị bỏ**, theo cùng phép chuẩn hoá mà `ScreenScopeMapBuilder` dùng để nhận ra "màn hình mới" —
  nếu không, mỗi lần gửi lại bảng là thêm một dòng trùng nghĩa mà không dòng nào có việc của màn hình.
- **Tên gieo ra phải đọc được như một MÀN HÌNH**, vì cột "Màn hình" chỉ được chứa màn hình: một mục tên
  `OrgUnit` trần sẽ được rà như một màn hình mà không ai biết nó để làm gì.

Người dùng chỉ tích một ô nhỏ trong một bảng dài, nên `RenderUserMessage` **gọi tên** các danh mục ấy ở cuối
tin nhắn — cùng luật với các dòng bị bỏ tích và các đối tượng tự thêm: bản kể là thứ mọi tầng chắt lọc phía
sau đọc, không phải cột DB.

#### Thêm/xóa dòng ngay trên bảng, và hai chốt chặn phải nhường

Ba nút, cùng lý do với bảng màn hình (một vòng gọi LLM cho một dòng người dùng đã biết chính xác mình muốn
gì, và bảng bày lại thì không có gì bảo đảm giữ nguyên những ô họ vừa điền): **+ thêm đối tượng** ở cuối
bảng, **+ thêm thông tin** ở cuối bảng thông tin của mỗi đối tượng, **+ thêm trạng thái** ở cuối bảng vòng
đời. Trần vẫn là trần của builder (12 đối tượng · 12 thông tin · 8 trạng thái) và bị chặn **tại nút bấm** ở
trình duyệt chứ không để server cắt lặng.

Ranh giới xóa giống bảng màn hình ở phần thông tin: dòng BA đề xuất thì **bỏ tích chứ không xóa** (dòng bị
loại còn phải kể lại được trong tin nhắn gửi đi — *"không cần lưu: …"*), chỉ dòng người dùng tự thêm mới có
nút xóa. **Dòng trạng thái là ngoại lệ: mọi dòng đều xóa được**, vì bảng vòng đời không có cột tích — "có
trạng thái này nhưng bỏ tích" không có nghĩa gì trong một vòng đời, và trước đây cách duy nhất để loại một
trạng thái sai là xóa trắng ô tên (server bỏ dòng không có tên), tức đúng việc này chỉ khác là không ai nhìn
ra để làm.

**Hai bảng con luôn hiện, kể cả khi rỗng** — đó là chỗ đứng của hai nút thêm, và một bảng trạng thái rỗng
không phải cái mà luật cắt vòng đời một trạng thái đang chặn: nó là chỗ người dùng nói ra rằng đối tượng này
CÓ vòng đời mà BA tưởng là danh mục.

Phần đắt nhất nằm ở server: **hai chốt chặn tất định của bảng này đều nhắm vào MODEL, nên cả hai phải nhường
ở đường GỬI** — không phân biệt được nguồn thì chúng chặn luôn thứ người dùng vừa tự gõ, và dòng ấy biến mất
không một lời nào nói vì sao (`EntityMapRow.AddedByUser`, ngoại lệ duy nhất, chỉ đọc ở `Sanitize` — ở lượt
bày bảng mọi dòng đều do model soạn nên đọc cờ ở đó là dựng cho model một cửa sau tự cấp phép):

- **"Đối tượng rỗng ruột"** loại dòng không có thông tin nào và cũng không có trạng thái nào. Một dòng người
  dùng vừa gõ tên thì lại là một câu nói *"ứng dụng còn phải lưu thứ này"* — họ thêm đối tượng vì biết nó cần
  có, phần "lưu gì" chính là thứ họ đang chờ được hỏi. Vì vậy nó đi qua, và **cả hai bản kể phải nói ra rằng
  nó còn trống**: tin nhắn gửi đi thêm dòng *"mình chưa rõ cần lưu những gì cho đối tượng này"*, còn khối ngữ
  cảnh — thứ đứng dưới lệnh *"đừng hỏi lại"* — mang một **ngoại lệ ghi ngay tại dòng của nó** bảo BA hỏi tiếp.
  Thiếu chỗ này thì tính năng tự mở đúng cái lỗ mà cả bảng sinh ra để bịt, chỉ khác là lần này do người dùng
  mở ra.
- **"Vòng đời một trạng thái"** không cắt ở đường gửi. Một trạng thái người dùng tự gõ (hoặc còn lại sau khi
  họ xóa bớt) là một quyết định; cắt nó vừa mất chữ họ vừa gõ, vừa làm **cả dòng rơi khỏi bảng** theo luật
  "rỗng ruột" khi đó là đối tượng danh mục vừa được thêm đúng một trạng thái.

Cờ `AddedByUser` **không** bị xoá lúc lưu (khác cờ khóa) vì `RenderUserMessage` phải gọi tên chúng: *"Các đối
tượng mình tự bổ sung vào bảng: …"*. Một đối tượng chưa từng có trong đề xuất mà lặng lẽ đi vào mô hình dữ
liệu là đúng loại thay đổi phải nói ra, cùng luật với các dòng bị bỏ tích.

### Bảng báo cáo: mỗi báo cáo là một màn hình

Nhóm «Báo cáo / thống kê» trước đây được hỏi bằng **một ô kể tự do** ở thẻ hỏi gộp. Hình dạng đó sai với
hình dạng câu trả lời: người dùng không có *một* báo cáo, họ có một **danh sách** — và một ô text gom cả
danh sách vào một đoạn, nên mỗi mục mất đi phần *"lấy số từ đâu"* và *"gộp theo gì"*, rồi bước sinh spec
phải đoán lại cả hai. Kết quả điển hình: spec dựng một màn hình đổ toàn bộ bảng dữ liệu ra rồi gọi đó là
báo cáo.

Bảng có bốn cột, và mỗi cột có một đường đi riêng ngoài chat:

| Cột | Là gì | Đi đâu |
|---|---|---|
| **Báo cáo / thống kê** | tên, đọc được như MỘT màn hình và theo [luật đặt tên màn hình](#tên-màn-hình-là-nhãn-menu-của-bản-demo-nên-nó-ngắn-và-bằng-tiếng-anh) (*"Remaining Leave Report"*) | gieo thẳng vào `PlannedScope` ⇒ bảng màn hình ⇒ `## 6. Screens To Generate` |
| **Để trả lời câu hỏi gì** | mục đích, viết bằng **lời người dùng** (*"để biết tháng này ai chưa đi học"*) | phần mô tả màn hình ở `## 6` |
| **Lấy số từ** | một đối tượng của bảng đối tượng đã chốt | nối về `## 8. Data Model Summary` — không dựng bảng dữ liệu riêng cho báo cáo |
| **Gộp / lọc theo** | kỳ, đơn vị, trạng thái, người phụ trách… | **bộ lọc thật** của màn hình + tham số truy vấn ở `## 9. API Expectations` |

**Ba ranh giới của bảng này, cả ba đều là chỗ dễ làm hỏng nếu sửa mà không biết lý do:**

- **Cổng ĐÒI nhóm đã `[RÕ]` trước khi bày bảng** (`ReportMapGate`) — khác hẳn năm bảng kia. Cám dỗ ở đây là
  thật: câu trả lời có hình dạng một danh sách nên một cái bảng **trống** trông như đã sẵn sàng cho người
  dùng tự điền. Nhưng bảng trống bắt người dùng nghiệp vụ tự chẻ câu chuyện của họ thành bốn cột **trước
  khi** gõ được chữ nào — khó hơn hẳn kể tự do, nên thu về ÍT hơn cả cái ô text nó thay thế. Đó đúng là
  thái cực mà luật *"ô ý nghĩa do BA điền sẵn"* của [bảng cột](#bảng-cột-chốt-phạm-vi-cột-của-file-bảng-tính)
  đã bỏ đi một lần rồi. Vì vậy hội thoại vẫn hỏi nhóm này như thường (câu mở đầu ở `CoverageGroupOpeners`),
  và bảng chỉ **chốt lại cho có ranh giới** khi nhóm đã `[RÕ]`.
- **`[KHÔNG ÁP DỤNG]` ⇒ bảng KHÔNG BAO GIỜ bày.** Người dùng nói rõ không cần báo cáo nào thì đó là câu trả
  lời xong; không có lối thoát này thì một ứng dụng thuần nhập liệu vẫn bị bày ra một bảng rỗng ở cuối buổi.
  Cùng hình dạng với điều kiện thứ ba của `NotificationMapGate` (dự án không có vòng đời nào).
- **KHÔNG có cột "ai xem".** Một báo cáo LÀ một màn hình, nên quyền xem của nó được chốt ở bảng phân quyền
  cùng với mọi màn hình khác, kèm cả PHẠM VI DỮ LIỆU (*"của mình"* / *"của đơn vị"* / *"tất cả"*) — thứ mà
  một cột vai trò ở đây không chở nổi. Thêm cột đó là dựng danh sách vai trò **thứ hai** trong cùng một buổi
  phỏng vấn, và hai danh sách lệch nhau thì không tầng nào phía sau biết tin bên nào. Đây cũng chính là lý
  do bảng báo cáo phải đứng **trước** bảng màn hình rồi bảng phân quyền chứ không phải sau.

**Đường ra `PlannedScope` là chỗ bảng trả tiền cho chính nó** (`ConfirmReportMapUseCase`, cùng khuôn với màn
hình danh mục của bảng đối tượng): nằm lại trong cột `ReportMap` thì báo cáo không có DÒNG nào ở bảng phân
quyền và không có mục nào ở `## 6` — mặc nhiên *"không ai được xem"* một màn hình người dùng vừa đặt hàng.
Và đó là lý do thứ hai bảng này đứng **trước** bảng màn hình chứ không chỉ trước bảng phân quyền: gieo trước
lần bày đầu thì mỗi báo cáo là một dòng bình thường của bảng màn hình, không phải một mục lộ ra sau lưng một
phạm vi vừa được chốt là *"toàn bộ"*. Ghép **thêm** chứ không ghi đè, và bỏ mục trùng — ở ca cổng báo cáo mở
muộn (nhóm chỉ `[RÕ]` sau lúc bảng màn hình đã chốt) thì `PlannedScope` chính là danh sách người dùng đã rà,
và đường **mở lại** của `ScreenScopeGate` là lưới an toàn đưa các màn hình báo cáo qua bảng màn hình ở lượt
kế rồi mới tới bảng phân quyền.

**Bỏ tích SẠCH vẫn được lưu, và vẫn ra khối "đã chốt".** *"Ứng dụng không cần báo cáo nào"* là một quyết
định, không phải một bảng rỗng: không lưu thì cổng mở lại và người dùng bị bày đúng cái bảng vừa tắt ở lượt
sau; không nói ra thì lượt sau BA thấy một nhu cầu họ từng nhắc mà bảng không có, rồi đề xuất lại đúng thứ
vừa bị bỏ. Đây là chỗ bảng này khác `FlowMapBuilder.RenderConfirmedBlock` (ở đó, không bước nào được giữ
nghĩa là chẳng có gì để khẳng định).

Bảng **không có** dấu ✓ bằng chứng, cùng lý do với bảng luồng và bảng màn hình — xem
[mục riêng](#vì-sao-bảng-luồng-và-bảng-màn-hình-không-có-dấu--bằng-chứng).

### Bảng thông báo: bảng cuối cùng

Nhóm «Thông báo / nhắc nhở» là nhóm THỨ HAI không được hỏi bằng câu hỏi, và vì đúng lý do của nhóm phân
quyền — chỉ khác chỗ hỏng. Chuẩn `[RÕ]` của nó đòi **ai nhận** và **khi nào** phải *ghép được với nhau*:
mỗi loại sự kiện biết ai là người nhận của riêng nó. Nhưng hình dạng tự nhiên của câu hỏi lại **tách hai vế
đó ra thành hai câu rời** — *"vai trò nào cần nhận email?"* và *"sự kiện nào cần gửi thông báo?"* — và không
gì nối chúng lại. Ca thật: người dùng bấm bốn chip vai trò ở câu đầu, dòng được nâng `[RÕ]`, và tài liệu
đóng băng thành *"mọi thay đổi trạng thái đều gửi cho cả bốn nhóm"*, tức mỗi lần một bản kế hoạch đổi trạng
thái thì **toàn bộ nhân viên nhà máy** nhận email. Không ai nói thế, và không cổng nào bắt được nữa.

Bảng ghép sẵn hai vế trên **cùng một dòng**, và bằng chứng thu về là một thao tác trên TỪNG sự kiện thay vì
một hàng chip trả lời thay cho tất cả.

**Dòng do CƠ CHẾ gieo, không do model liệt kê.** `NotificationMapBuilder.SeedRows` lấy mọi trạng thái của
mọi đối tượng còn tích trong bảng đối tượng đã chốt; model chỉ điền người nhận vào các dòng có sẵn. Sự kiện
model quên nêu vẫn có mặt ở trạng thái chưa chọn người nhận — im lặng bỏ nó đi là biến "chưa hỏi" thành
"không báo cho ai" mà người dùng không bao giờ nhìn thấy để phản đối. Chiều ngược lại cũng chặn: một dòng
model tự nghĩ thêm chỉ đi qua khi có **trích dẫn**, và đó là đường dành cho nửa "NHẮC NHỞ" của nhóm
(*"trước hạn 3 ngày"*, *"quá hạn mà chưa ai duyệt"*) — thứ không phải chuyển trạng thái nào nên bảng đối
tượng không gieo ra được. Người dùng còn có nút **+ thêm lời nhắc** cho đúng phần đó; không có nó thì nửa
"nhắc nhở" biến mất trong im lặng ngay tại cái bảng sinh ra để chốt nó.

**Người nhận chọn từ DANH SÁCH NGƯỜI NHẬN của dự án — một bảng người dùng tự sửa, đứng ngay trên bảng
thông báo.** Ô To/CC là ô chọn nhiều, không phải ô gõ: gõ thẳng vào từng dòng thì mỗi dòng một cách viết
cùng một người, và không tầng nào ghép chúng lại được. Chỗ gõ có đúng **một**, và sửa ở đó thì mọi ô chọn
đổi theo — đó là toàn bộ lý do bảng danh sách tồn tại.

- Dự án chưa chốt lần nào ⇒ danh sách là bản **gieo** tất định (`NotificationMapBuilder.SeedRecipients`):
  **bốn mục QUAN HỆ với bản ghi** trước, vì đây là ca thường gặp — *Người tạo* · *Người được phân công* ·
  *Quản lý trực tiếp của người tạo* · *HOD của đơn vị liên quan* — rồi mỗi vai trò của **bảng phân quyền đã
  chốt**, nguyên tên.
- Đã chốt ⇒ bộ đã lưu ở `Project.NotificationRecipients` **thắng** bản gieo, kể cả khi bảng phân quyền sau
  đó đổi: nó là thứ người dùng đã tự tay rà, bản gieo chỉ là phỏng đoán.

Danh sách **mở cho người dùng, không mở cho model**: giá trị model (hoặc payload) đưa lên vẫn phải kéo về
đúng một mục theo hai nấc hẹp dần — khớp chính xác → khớp chứa-nhau và chỉ khi có đúng một mục dài nhất
khớp — còn lại thì BỎ. Nấc thứ hai còn gánh thêm việc kéo các giá trị `"Toàn bộ <vai>"` của bản trước về
đúng mục vai trò trần.

Không còn tiền tố **"Toàn bộ …"** trong danh sách: không thao tác nào của các ứng dụng ở đây cần gửi email
cho cả một vai trò, nên mục đó chỉ là một cái bẫy bấm nhầm. Đổi lại, một mục vai trò trần (`HRBP`) tự nó
không phân biệt được *một người* với *cả nhóm*, nên khối "đã chốt" (`RenderConfirmedBlock`) mang theo một
lệnh **cấm mọi tầng sau tự suy rộng** một mục thành "mọi người mang vai đó" — đó là chỗ ca "cả nhà máy nhận
email" có thể sống lại nếu để trống.

**Danh sách đi CÙNG CHUYẾN với bảng lúc gửi** (`recipientsJson` cạnh `notificationsJson`), và bộ server đối
chiếu hai ô To/CC chính là danh sách vừa gửi lên đã chuẩn hoá. Giữ nó ở riêng trình duyệt thì mọi mục người
dùng tự thêm bị bỏ sạch ngay lúc lưu — bảng hiện đủ tên người nhận ở từng dòng mà server lại trả về *"còn N
sự kiện chưa chọn người nhận"*. Danh sách rỗng (tab mở từ trước bản này, hoặc người dùng xóa sạch) rơi về
đúng bộ mà lượt bày bảng đã dùng, chứ không được hiểu là "dự án không còn người nhận nào".

Trên trình duyệt, hai thao tác phá được mối nối giữa hai bảng nên bị chặn tại chỗ: **đổi tên** một mục kéo
theo mọi ô đang chọn nó (và tên rỗng / trùng một dòng khác thì trả lại chữ cũ kèm câu giải thích), còn
**xóa** một mục đang được dùng thì đòi một cú bấm **thứ hai** sau khi nói rõ nó đang nằm ở mấy ô — im lặng
xóa là đẩy các dòng đó vào đúng trạng thái mà bất biến của bảng cấm.

**HAI trạng thái của một dòng, và không trạng thái nào được hỏi lại.** Bỏ tích cột "Cần" = KHÔNG gửi thông
báo ở sự kiện đó (một quyết định hợp lệ, và khối ngữ cảnh gọi tên chúng ra vì mặc định im lặng của các tầng
sau là gửi cho tất cả). Còn tích ⇒ **bắt buộc có người nhận chính (To)**; CC vẫn tùy chọn. Nhóm coi như
xong ngay khi bảng được chốt, và khối "đã chốt" là một lệnh cấm hỏi **tuyệt đối**, không ngoại lệ — dòng
bản đồ của nhóm được `CoverageConfirmedTableGuard` nâng lên `[RÕ]` bằng máy (xem
[Lượt hỏi GỘP, chuẩn `[RÕ]` và phanh chống hỏi lại](#lượt-hỏi-gộp-chuẩn-rõ-và-phanh-chống-hỏi-lại)) chứ
không chờ lượt distill chấm: cấm hỏi mà dòng vẫn kẹt `[MỘT PHẦN]` thì không ai còn đường trả lời.

Trạng thái thứ ba — *"cần báo nhưng chưa chốt được ai"* — từng được cho phép, và nó dẫn ngược về đúng vòng
hỏi lẻ mà cả cái bảng này sinh ra để thay thế. Ca thật ở dự án JD Library: bảng 8 dòng gửi đi với **7 dòng
trống người nhận**, nhóm xuống `[MỘT PHẦN]`, nút "Write Requirement" khóa, và BA — đúng luật, theo ngoại lệ
mà khối "đã chốt" tự chở — phải đi hỏi lại từng sự kiện trong khung chat, mỗi sự kiện hai lượt (To rồi CC):
**14 lượt**, ở cuối một buổi phỏng vấn đã 78 lượt. Tệ hơn: tin nhắn kể lại mở đầu bằng *"đây là các sự kiện
cần gửi email và người nhận"* rồi mới đính chính ở đoạn dưới, nên người dùng đọc tiêu đề, tin là mình đã trả
lời xong, và mọi câu BA hỏi tiếp trông như hỏi lại điều vừa nói.

**Bất biến được chặn ở đường GỬI, không ở trình duyệt.** `ConfirmNotificationMapUseCase` gọi
`NotificationMapBuilder.MissingRecipients` và **không lưu gì** khi còn một dòng tích "Cần" mà To rỗng; câu
trả về gọi tên đúng các sự kiện còn thiếu. Lưu một phần thì tệ hơn không lưu: cột có dữ liệu ⇒
`NotificationMapGate` coi như đã chốt và không bao giờ bày lại bảng, nên các dòng còn dở **không còn màn
hình nào để sửa**. Trình duyệt là phanh phụ — nó không thấy được payload sửa tay, tab mở từ trước bản này,
hay lần bấm gửi lại sau khi mất mạng.

**Popup của trình duyệt bày HAI lối đi, và cả hai đều là câu trả lời thật.** Bấm "Gửi bảng thông báo" khi
còn dòng trống thì một popup liệt kê đúng các sự kiện đó, mỗi dòng hai nút: *Chọn người nhận* (đóng popup,
cuộn tới đúng dòng, tô sáng và mở sẵn ô To — bảng tới 24 dòng nên ô trống thường nằm ngoài màn hình) và
*Không cần gửi* (bỏ tích ngay tại popup). Lý do không chỉ nhắc một câu "vui lòng chọn người nhận": ở hệ này
một người nhận **sai** hại hơn một ô trống — ô trống còn bị hỏi lại, còn giá trị sai được chấm `[RÕ]` rồi
vĩnh viễn không ai soát nữa. Một popup chặn cứng mà chỉ có một đường ra sẽ đẩy người dùng đang mệt tới cú
bấm nhanh nhất trong danh sách, mà cú bấm nhanh nhất thì chẳng liên quan gì tới sự kiện đang hỏi. Có lối
thứ hai thì *"tôi không biết ai"* đổ về một quyết định hiển thị, không về một người nhận bịa.

**Một ngõ chết đi kèm phải đóng cùng lúc.** Dòng có trích dẫn được KHÓA (`Locked`), và dòng khóa không có
checkbox — ô "Cần" là một input hidden. Nếu chỉ cần có `evidence` là khóa thì ca *model kèm trích dẫn thật
nhưng viết người nhận không khớp mục nào của danh sách chọn* (⇒ `NormalizeRecipients` bỏ sạch To) sinh ra
một dòng người dùng **không được bỏ tích mà cũng chẳng có ai để gửi** — kẹt vĩnh viễn dưới chốt chặn mới.
Vì vậy `Locked` đòi **cả** trích dẫn **và** To không rỗng.

Không có cột **kênh gửi**: hằng số nền tảng đã chốt chỉ có email (xem `organization-platform.v1.md`), nên
thêm cột đó là mời người dùng chọn một thứ không tồn tại.

**Đường thoát cho dự án không có vòng đời nào.** Không đối tượng nào có trạng thái ⇒ `SeedRows` rỗng ⇒ cổng
không bao giờ mở. Lúc đó lệnh cấm hỏi lẻ trong ngữ cảnh chat **tự tắt** (điều kiện khớp đúng điều kiện thứ
ba của cổng) và nhóm quay về đường hỏi bằng câu hỏi như trước. Thiếu đường thoát đó thì ứng dụng danh mục
thuần kẹt vĩnh viễn: không bảng nào bày ra, không câu hỏi nào được phép, nhóm không bao giờ `[RÕ]`, nút
"Write Requirement" không bao giờ sáng.

## Bảng phân quyền: chốt nhóm phân quyền ở cuối buổi

«Phân quyền theo nghiệp vụ» là nhóm **duy nhất** của bản đồ bao phủ không được hỏi bằng câu hỏi. Lý do nằm ở một
buổi phỏng vấn thật, 94 lượt: BA hỏi mở *"từng vai trò còn được xem những dữ liệu nào?"*, người dùng đáp *"hiện
tại cứ vậy đã, có gì tôi bổ sung sau"*, BA tự soạn phương án cho cả năm vai trò rồi xin gật, người dùng bấm một
chip *"Đồng ý phương án này"* — và dòng phân quyền lên `[RÕ]` với bằng chứng đúng bằng bốn chữ ấy. Từ đó BA bị
cấm hỏi lại nhóm đã `[RÕ]`, nên **toàn bộ phân quyền của sản phẩm là thứ BA tự nghĩ ra, ký tên người dùng**.

Câu hỏi đó không hỏng vì cách viết mà vì hình dạng: nó bắt một người dùng nghiệp vụ tự dựng cả ma trận
(màn hình × chức năng × vai trò) trong đầu rồi đọc ra thành lời. Bảng đảo chiều chi phí — chọn vài chục ô có
sẵn rẻ hơn hẳn việc kể ra chừng ấy quyền — và đổi bằng chứng từ *một chip trả lời thay cho tất cả* thành *một
thao tác trên từng ô*.

**Cái bảng KHÔNG gánh.** Quyền định hình LUỒNG (*"HOD duyệt từng quý"*, *"manager trực tiếp duyệt ticket"*,
*"Admin xử waitlist theo FIFO"*) vẫn phải hỏi trong hội thoại đúng lúc nó phát sinh: câu trả lời của chúng làm
ĐỔI câu hỏi kế tiếp của BA, nên hoãn xuống cuối là tự bịt mắt suốt cả buổi. Chúng thuộc nhóm «Chức năng & luồng
nghiệp vụ chính» và «Dữ liệu / danh mục chính». Bảng chỉ gánh phần quyền CRUD theo màn hình — phần mà hỏi lúc
nào cũng cho ra cùng một câu trả lời.

**Thời điểm do CƠ CHẾ chọn, không để model tự đoán** (`PermissionMatrixGate`). Bản đồ bao phủ ghi nhóm phân
quyền là `[CHƯA HỎI]` suốt cả buổi, nên một câu dặn "để cuối" trong prompt không đủ: model vẫn thấy một nhóm
chưa hỏi nằm đó và sớm muộn cũng hỏi. Cổng mở khi cả ba điều kiện cùng đúng — chưa chốt bảng nào, phạm vi màn hình
đã có mục, và **mọi nhóm áp dụng KHÁC** đã `[RÕ]`. Phạm vi đó nay lấy từ **bảng màn hình đã chốt**
(`PermissionMatrixGate.EffectiveScreens`) chứ không từ `PlannedScope` thô — xem
[Sáu bảng chốt của buổi phỏng vấn](#sáu-bảng-chốt-của-buổi-phỏng-vấn).
Cổng cố tình **bỏ qua đúng dòng phân quyền** khi xét: `RequirementReadinessGate` đòi mọi dòng `[RÕ]` mới mở nút
"Write Requirement", mà dòng phân quyền chỉ lên `[RÕ]` sau khi bảng được chốt — không bỏ qua thì hai cổng khóa
lẫn nhau và không cổng nào mở được. Ba trạng thái của cổng thành ba khối lệnh khác nhau trong ngữ cảnh chat:
chưa mở ⇒ *cấm hỏi lẻ quyền CRUD*; mở ⇒ *lượt này bày bảng*; đã chốt ⇒ *khối bảng đã chốt, đừng hỏi lại*.

Bảy quyết định của thiết kế này:

- **Ô là PHẠM VI DỮ LIỆU, không phải dấu tích.** Quyết định thật gần như luôn có mệnh đề phạm vi —
  *"Assistant xem và chỉnh Training Plan **do mình lập**"*, *"manager xem ticket **của nhân viên thuộc quyền**"*.
  Một ma trận nhị phân chỉ ghi được "có xem" và bước soạn tài liệu phải tự đoán xem của ai, tức là bảng sẽ
  **nghèo hơn chính khung chat** nó thay thế. Bốn nấc: rỗng (không có quyền) / `của mình` / `của đơn vị` /
  `tất cả`, và `PermissionMatrixBuilder` kéo mọi cách viết của model về đúng bốn nấc đó.
- **Chỉ ô có TRÍCH DẪN mới được khóa.** Ô khóa hiện thành dấu ✓ kèm tooltip câu gốc; ô còn lại vẫn mang đề xuất
  của BA nhưng ở dạng chọn được và được nói thẳng là phỏng đoán. Server không nhận lời tuyên bố "người dùng đã
  nói điều này" từ một lá cờ — phải có `evidence` đi kèm. Thiếu ranh giới này thì một bảng điền sẵn trông như
  đã chốt chính là cái chip *"Đồng ý phương án này"* phóng to: người dùng bấm gửi trong ba giây và ta quay về
  đúng chỗ cũ, chỉ khác là tốn thêm một màn hình.
- **Dòng phải trỏ vào màn hình CÓ THẬT, và không màn hình nào được vắng mặt.** Mọi `screen` phải khớp một mục
  `PlannedScope` (khớp chính xác, hoặc một bên chứa bên kia khi model rút gọn tên) và luôn lấy lại **chữ của
  PlannedScope**; dòng không khớp bị bỏ (một tính năng ngoài phạm vi đi vào tài liệu mang chữ ký người dùng),
  còn mục phạm vi mà model quên nhắc tới được **bổ sung vào bảng** ở trạng thái chưa ai có quyền — vắng mặt thì
  nó mặc nhiên "không ai được xem" mà người dùng không bao giờ nhìn thấy để phản đối. Cùng luật với bảng cột.
- **Mọi dòng có ĐỦ mọi vai trò.** Vai chỉ được model nêu ở vài dòng thì các dòng còn lại không có ô cho vai đó —
  và trên màn hình, "không có quyền" với "không hỏi" trông giống hệt nhau.
- **Bộ CỘT là một bảng người dùng tự sửa được** (bảng *"Vai trò"* đứng ngay trên các bảng màn hình, cùng khuôn
  với danh sách người nhận của [bảng thông báo](#bảng-thông-báo-bảng-cuối-cùng)).
  Thêm / sửa chữ / xóa một dòng ở đó là thêm / đổi tên / bỏ **một cột trên MỌI bảng màn hình**, và ô đã chọn đi
  theo tên mới — không thì sửa một chữ trong tên vai là xóa sạch phạm vi vừa chọn cho vai đó, ở mọi màn hình.
  Trước đó cột được chắt ngầm từ chính grants model trả về, nên một vai có thật mà model quên chỉ thêm lại được
  bằng cách gõ vào khung chat cho BA bày lại cả bảng: một lượt LLM cho một việc tất định, và bảng đang tích dở
  thì bị thay bằng bảng mới. Trần **8 vai** (`PermissionMatrixBuilder.MaxRoles`) là giới hạn đọc được, không
  phải guard suông — mỗi vai là một cột trên mọi bảng. Xóa một vai đang có quyền đòi **cú bấm × thứ hai** kèm số
  ô sẽ mất; xóa vai cuối cùng bị chặn, và đường gửi cũng **từ chối lưu** một bảng không còn cột nào (`PermissionMatrix`
  có dữ liệu ⇒ cổng coi như đã chốt và không bao giờ bày lại bảng, tức mất luôn đường sửa).
- **Có cột ĐIỀU KIỆN.** Ràng buộc mà bốn nấc phạm vi không chở nổi (*"chỉ đăng ký được khóa nằm trong danh sách
  bắt buộc của mình"*, *"chỉ sửa khi chưa submit"*) có chỗ riêng ở mức dòng. Đây là loại ràng buộc đổi ngược lại
  cả luồng: ca thật là nhu cầu mở lớp được tính từ danh sách "ai phải học khóa nào" nhưng không ai hỏi nhân viên
  có bị giới hạn chỉ đăng ký khóa của mình không ⇒ tài liệu để đăng ký mở tự do, và con số kế hoạch không còn
  liên quan gì tới người thật sự vào lớp.
- **Bảng treo theo DỰ ÁN, không theo lượt.** Nó còn đó tới khi `Project.PermissionMatrix` được ghi, nên người
  dùng gõ thêm một câu (*"thiếu màn hình đăng ký khóa học"*) rồi mới ngồi chọn cũng không mất bảng. Lượt có bảng thì **bỏ**
  hàng chip và thẻ hỏi gộp — chip bấm là gửi NGAY, để cả hai cùng sống thì một cú bấm nhầm cuốn mất
  lượt trước khi người dùng chọn xong. Cùng luật với lượt có bảng cột.

Gửi bảng đi **hai bước**, như bảng cột: `POST Requirements/ConfirmPermissionMatrix` (payload mang **cả bảng vai
trò**, `rolesJson` — để riêng thì server lại chắt cột từ grants và một vai vừa thêm nhưng chưa cấp quyền ở dòng
nào sẽ biến mất khỏi bảng đã lưu; payload không có trường đó — tab mở từ trước — rơi về đúng hành vi cũ) lưu vào
`Project.PermissionMatrix` (`ConfirmPermissionMatrixUseCase`, không gọi LLM), rồi trình duyệt gửi tiếp **một tin
nhắn người dùng** qua đúng đường chat thường — hội thoại vẫn chỉ có một đường ghi. Tin nhắn do **server** soạn
(`PermissionMatrixBuilder.RenderUserMessage`) từ bảng đã chuẩn hoá chứ không do JS ghép từ payload: hai bản lệch
nhau thì hội thoại kể một đằng còn dữ liệu dự án ghi một nẻo, mà mọi tầng đọc transcript tin vào bản kể. Lưu
hỏng thì dừng hẳn, không gửi tin nhắn.

Chốt xong, bảng được **tiêu thụ ở ba đầu**:

| Đầu đọc | Việc |
|---|---|
| `BAChatService` | gắn khối *"Bảng phân quyền đã được NGƯỜI DÙNG CHỐT"* vào **mọi** lượt chat sau ⇒ BA thôi hỏi lại, thôi bắt xác nhận lần nữa, thôi dựng yêu cầu trái với bảng |
| `RequirementCoverageService` | gắn **cùng khối đó** vào lượt distill: đây là nguồn bằng chứng RIÊNG của dòng phân quyền. `requirement-coverage.v3.md` khắt khe một chiều — có khối ⇒ `[RÕ]`; **chưa có ⇒ không bao giờ `[RÕ]`**, kể cả khi hội thoại nghe có vẻ đã nói đủ, vì đó chính là đường mà một chip "Đồng ý" đã đi qua một lần |
| `RequirementPromptBuilder.BuildAiDesignSpec` | đưa bảng vào mục `## 6b. Permission Matrix` của spec, và bắt phạm vi dữ liệu thành **điều kiện lọc thật** ở `## 9. API Expectations` chứ không phải một câu mô tả. Đây là đường DUY NHẤT để phân quyền tới được POC ở dạng máy đọc được — không có nó, phân quyền tan vào văn xuôi và bản demo hiện đúng một bộ màn hình cho mọi vai |

Chưa chốt (cổng chưa mở, model không trả bảng dùng được, hoặc người dùng chưa gửi) ⇒ không có bảng, không có
khối ngữ cảnh — luồng chạy đúng như trước và cổng mở lại ở lượt sau. Fail-open toàn tuyến: một lượt hỏi thừa rẻ
hơn nhiều so với một lượt câm.

**Ảnh đi một lần, chữ đi mãi** (`SourceContextBuilder` + `ProjectSourceFile.VisionSummary`). Ảnh nguồn được đính vào lượt user của MỖI lời gọi model, mà mỗi lượt chat là một request mới ⇒ nếu không chặn, một cuộc chat 20 lượt trả tiền upload lại cùng bộ ảnh 20 lần (12 screenshot full-width ≈ 20–40k token/lượt). Chặn bằng cách: lượt BA **mở tài liệu** (`source-ack.v3.md`, chạy ngay sau upload — kể cả khi nó chỉ bày bảng cột) vừa đọc ảnh vừa ghi lại nội dung từng `[Hình n]` thành chữ vào `VisionSummary`; từ lượt sau builder gửi phần chữ đó thay cho ảnh. Hai rào an toàn đi kèm:

- Chỉ khóa lại khi **TOÀN BỘ** hình của nguồn thật sự đã đi kèm lượt đó — mô tả dựa trên nửa số hình rồi khóa là mất trắng nửa còn lại. Nguồn chưa đủ vẫn được ưu tiên hạn mức ảnh ở lượt sau, nên project nhiều tài liệu tự cuốn chiếu.
- Câu ghi chú gắn kèm mỗi nguồn nói **đúng số ảnh thực sự đi trên dây** (tính sau khi đã chốt danh sách). Nói "kèm 12 hình" rồi chỉ gửi 6 là mời model bịa nội dung 6 hình còn lại — trần `Llm:SourceUpload:MaxImagesPerCall` mặc định **12** để khớp trần bóc hình của Word, hạ xuống 4–6 nếu chạy model vision context nhỏ.

**Ảnh trong call log** (`ModelCallImageStore` + `ModelCallRequestPreview`). `RequestJson` của `AgentModelCallLogs` là một bản DỰNG LẠI để hiển thị, không phải body thật, và trước đây nó chỉ lấy phần text của message ⇒ mọi ảnh đã gửi biến mất khỏi log. Khi truy lỗi, ba tình huống khác hẳn nhau — model không bật vision, ảnh bị cắt vì chạm trần, đọc file ảnh lỗi nên bỏ qua — để lại log **giống hệt nhau**. Nay:

- Message có ảnh thì `content` là **mảng part**: `{"type":"image","index":n,"name":"tai-lieu.docx › Hình 3","mediaType":"image/png","bytes":78138}`. Message toàn chữ vẫn là chuỗi như cũ (UI "Dễ đọc" và log cũ dựa vào dạng đó). **Không nhúng base64** — 12 screenshot là ~1.3MB base64 mỗi dòng, trong bảng mà `BudgetGuard` quét trước mỗi lời gọi.
- Bytes ghi ra **đĩa** trong workspace project (`99_CallLogs/{logId}/image-{n}.{ext}`), mở lại bằng `GET /AgentDashboard/CallLogImage?id=…&index=…` (cùng cổng quyền với `CallLogDetail`; đường dẫn dựng hoàn toàn phía server từ log id — endpoint không bao giờ nhận path). Modal Model Invocation Detail hiện dải thumbnail ở tab Request. Không thể chỉ lưu đường dẫn tới file gốc: ảnh chụp POC do Playwright chụp trong RAM rồi thả, không hề có trên đĩa — mà đó lại là loại ảnh cần xem lại nhất.
- Tắt bằng `Llm:CallLog:StoreRequestImages=false`. Ảnh chết cùng workspace khi xóa project, không cần cơ chế dọn riêng. Log cũ / ảnh đã xóa ⇒ ô "ảnh không còn" thay vì icon vỡ, vì "không xem lại được" khác hẳn "không gửi ảnh nào".

Text bóc từ **Excel/Word** còn được nạp vào prompt sinh AI Design Spec làm **dữ liệu mẫu THẬT** (`RequirementDocsService.BuildRealSampleDataAsync`), để POC demo bằng đúng danh mục/tên của đơn vị yêu cầu thay vì "Sản phẩm A / Nguyễn Văn B".

## Sidebar đã gỡ: mọi cổng chờ người dùng chuyển vào khung chat

**Sidebar không còn panel nào của `InterviewOutlookService`.** Ba danh sách chắt sau mỗi lượt chat — `OpenQuestions`, `PlannedScope`, `WorkedExamples` — nay đều đi thẳng vào đường tiêu thụ của máy (và hai trong ba quay lại với người dùng ở dạng SỬA ĐƯỢC — `PlannedScope` thành bảng màn hình, `WorkedExamples` được bảng luồng thay thế ở phần định tính; xem [Sáu bảng chốt](#sáu-bảng-chốt-của-buổi-phỏng-vấn)): ngữ cảnh chat của BA (`BAChatService`), ngữ cảnh soát mâu thuẫn (`RequirementConflictService`), và mục `## 13. Worked Examples` của AI Design Spec. Panel **"Ví dụ đã xác nhận"** là cái cuối cùng bị bỏ vì nó lặp lại đúng thứ BA vừa nói trong chat: ví dụ ĐỊNH TÍNH trùng gần nguyên văn **bảng luồng** mà người dùng tự tay duyệt từng bước — đúng chỗ để đính chính, ví dụ ĐỊNH LƯỢNG thì đến từ chính câu người dùng vừa chốt. Cái mất kèm theo là đường **sửa tay** danh sách oracle (`UpdateWorkedExamplesUseCase`, đã gỡ): đính chính nay đi qua chat như mọi điều khác, và `WorkedExamples` vẫn là oracle mà POC bị chấm theo (`PocWorkedExampleOracle`) — chỉ khác là nó chỉ được sửa qua lượt chắt lọc chứ không sửa trực tiếp được nữa.
**Stepper 5 chặng ở đầu trang đã bỏ.** Quy trình thực tế không chạy một chiều — người dùng sửa tới sửa lui (chat thêm → sinh lại brief → duyệt lại → dựng lại POC), nên một thanh tuyến tính vừa chiếm chỗ đầu trang vừa mô tả sai việc đang diễn ra. Trạng thái thật vẫn ở đúng chỗ cần đọc: cổng xác nhận giả định và tiến trình workflow nằm trong khung chat, các bản mô tả nằm ở panel tài liệu.

**Sidebar không còn panel "Điều đã chốt" — soát mâu thuẫn chuyển từ NGƯỜI DÙNG sang BA.** Đây là panel cuối cùng của sidebar bị gỡ, và vì đúng cái lý do đã gỡ ba panel trước nó. Panel hiển thị nhật ký `DecisionLogService` (tới 40 dòng) cạnh khung chat để người dùng tự rà, tức bắt họ **vừa kể chuyện nghiệp vụ vừa làm QA cho BA** — hai chế độ tư duy song song, đúng lúc cần tập trung nhất. Nó cũng đặt việc soát mâu thuẫn nhầm vai: người dùng không có nghĩa vụ nhớ mình đã nói gì ở lượt thứ ba, còn BA thì đọc được cả hội thoại. Và "bấm để sửa" không phải công cụ sửa thật — nó chỉ soạn sẵn một câu vào ô chat.

Nhật ký **vẫn được chắt sau mỗi lượt** (không đổi chi phí: lời gọi `BADecisionLog` vốn đã chạy), chỉ đổi người đọc. Nó nay đi vào **ngữ cảnh chat của BA** (`BAChatService`) kèm chỉ dẫn bắt buộc: trước khi soạn câu hỏi kế tiếp, đối chiếu câu người dùng vừa trả lời với danh sách; chọi nhau ⇒ lượt này PHẢI là lượt gỡ mâu thuẫn (nêu cả hai vế, hỏi vế nào đúng, tối đa một mâu thuẫn mỗi lượt, hỏi MỘT MÌNH); không chọi nhau ⇒ coi là điều đã biết, không hỏi lại. Trước đây prompt đã dặn "mâu thuẫn thì nêu lại" nhưng BA **không có gì để đối chiếu**: ngữ cảnh không nạp nhật ký, mà các lượt cũ thì bị `ConversationMemoryService` nén thành tóm tắt — chi tiết đã chốt bị bào mòn đúng ở hội thoại dài, nơi mâu thuẫn dễ xảy ra nhất. `RequirementConflictService` (soát một cục lúc bấm "Write Requirement") **vẫn giữ** làm lưới an toàn, nhưng nay hiếm khi bắt được gì — bắt tại lượt rẻ hơn nhiều so với bắt ở cuối, khi người dùng phải chọn A/B cho một câu đã nói từ rất lâu trước.

**Mỗi dòng nhật ký phải TỰ ĐỨNG ĐƯỢC — và điều đó cần lô gộp CHỜM về trước con trỏ.** Người đọc một dòng ("Điều đã chốt") không nhìn thấy câu hỏi đã sinh ra nó: BA ở lượt sau chỉ có danh sách, và `RequirementConflictService` lúc soát cũng vậy. Mà người dùng nghiệp vụ trả lời chủ yếu bằng cách **bấm chip**, và chip cố ý chỉ mang phần *khác nhau* giữa các phương án — chủ ngữ, đối tượng, điều kiện đều nằm trong câu hỏi. Chép chip vào nhật ký là chép cái vỏ: `- Chỉ Assistant HR.`, `- Có trên 100 người.`, `- Duyệt toàn bộ quý.`. Thiệt hại không dừng ở khó đọc — nó **đổi nghĩa**: một câu trả lời cho *"vai trò nào cần nhận email?"* mà ghi thành `- Các vai trò gồm Assistant HR, HOD HR, Manager trực tiếp và Nhân viên.` biến quyết định về **người nhận email** thành quyết định về **danh sách vai trò**, đủ để `RequirementConflictService` bắt nhầm mâu thuẫn với vai trò Admin đã chốt và chất vấn người dùng một câu thừa.

Hai tầng cùng chặn. Prompt (`decision-log.v1.md`) chốt "trung thành với NGHĨA, không phải với CÂU CHỮ": lấy mệnh đề BA hỏi/đề xuất ghép phần người dùng đã gật, viết thành câu có đủ *ai làm / cái gì được quyết / trong điều kiện nào*, kèm bảng đối chiếu chép-chip ⇄ dòng đúng. Cơ chế (`DecisionLogService.ContextTurnCount`) lo phần prompt không tự lo được: hàm chạy CUỐI lượt chat, sau khi lượt BA đã lưu, nên mỗi lô là `[câu trả lời của người dùng, câu hỏi MỚI của BA]` — **câu hỏi mà họ đang trả lời nằm ở lô TRƯỚC**, tức model không có gì để dựng lại nghĩa. Lô gộp vì vậy kéo thêm 2 lượt đã gộp về trước con trỏ, dán nhãn "KHÔNG chắt lại" (chắt lại là ghi trùng dòng đã có) và **không dời con trỏ** — chỉ phần delta được tính, đúng như trước.

**CỔNG TẠO TÀI LIỆU (`#writeReqZone`) — chỗ DUY NHẤT có nút sinh Product Brief.** Cụm cuối khung chat, gồm hai nhịp của cùng một quyết định: **nút tạo tài liệu** → **cổng soát mâu thuẫn** (nếu có). Đặt trong chat vì cùng lý do đã chuyển cổng xác nhận giả định vào đây: quy trình đang ĐỨNG CHỜ người dùng, câu hỏi và nút trả lời phải nằm cùng chỗ mắt đang nhìn. Là **một wrapper** chứ không phải hai khối rời vì `syncWriteReqGate` phải dời cả cụm xuống cuối dòng hội thoại sau mỗi lượt (các bong bóng mới chèn vào trước `#thinkingBox`) — dời lẻ thì panel mâu thuẫn lạc khỏi cái nút vừa bật nó lên; wrapper cũng giữ cụm không kề trực tiếp bong bóng BA nên quy tắc gộp "câu hỏi + chip gợi ý" (`.req-msg.ba:has(+ .suggestion-list …)`) không bị chen vào giữa.

**Cổng KHÔNG còn "bản tổng kết trước khi tạo tài liệu".** Nó từng là nhịp đầu của cụm: toàn bộ nhật ký `DecisionLogService` bày thành danh sách (thường ~40 dòng) kèm nút **✎ Sửa** cho từng ý, nút nổi "✎ Ghi chú đoạn này" khi bôi đen, và một nút "Gửi N đính chính cho BA" loại trừ với nút tạo tài liệu. Gỡ vì đúng cái lý do đã gỡ panel "Điều đã chốt" ở sidebar, chỉ muộn hơn một nhịp: **một cổng rà soát bị bấm qua còn tệ hơn không có cổng nào** — nó tạo cảm giác đã rà. Bốn mươi dòng phẳng, không thứ tự ưu tiên, bày ra đúng lúc người dùng chỉ còn muốn bấm nút thì bị đọc lướt; và kể cả đọc kỹ, danh sách chỉ kể lại **những gì đã nói** nên nó không giúp thấy thứ đắt nhất ở bước này là điều còn **THIẾU**.

Việc rà soát không mất — nó đã nằm ở những chỗ **sửa được** và đúng lúc hơn, nên bản tổng kết là lần thứ hai (có chỗ là lần thứ ba) nói cùng một điều:

| rà cái gì | ở đâu |
|---|---|
| cách hiểu chung, theo từng chặng phỏng vấn | nhịp BA **chủ động đọc lại** sau mỗi ~5–7 câu đã trả lời (`requirement-chat.v4.md`) |
| các bước quy trình | **bảng luồng** — sửa/bỏ/đổi thứ tự từng bước rồi bấm chốt ([Bảng luồng](#bảng-luồng-chuỗi-bước-người-dùng-tự-tay-duyệt-và-đường-của-nó-tới-poc)) |
| cột dữ liệu, màn hình, thực thể, báo cáo, thông báo, luồng | [sáu bảng chốt](#sáu-bảng-chốt-của-buổi-phỏng-vấn) + bảng phân quyền — người dùng sửa trực tiếp trong ô rồi gửi |
| những điều đã rõ có chọi nhau không | **cổng soát mâu thuẫn** ngay dưới nút này, với lựa chọn A/B thật |
| toàn văn tài liệu | ghim ghi chú thẳng trên bản xem trước Product Brief (`ReviseBriefFromNotesUseCase`) |

Cái mất kèm theo: đường **✎ Sửa → Gửi đính chính** một-cú-bấm. Đính chính nay đi qua khung chat như mọi điều khác (nó vốn đã đi qua chat — nút cũ chỉ soạn hộ câu vào ô nhập). Nhật ký quyết định thì **không đổi gì**: vẫn được gộp sau mỗi lượt, chỉ mất mặt UI cuối cùng, nên nay hoàn toàn là dữ liệu cho máy (ngữ cảnh chat của BA, soát mâu thuẫn, `ProductBriefDraftService`, `ChatExportBuilder`) — client không nhận frame `decisions` nữa và `BAChatTurnResult` không mang danh sách này.

**Năm trạng thái, chỉ MỘT trạng thái có nút** (`writeReqState`, suy tất định ở đầu `Index.cshtml`, ghi vào `data-state` của wrapper để `requirements.js` khởi tạo từ đúng bản server render):

| trạng thái | điều kiện | trên màn hình |
|---|---|---|
| `waiting` | lượt BA mới nhất chưa mời (và chưa có draft nào để soạn lại) | cổng ĐÓNG, không có nút nào; panel "Tiến độ khai thác" bên cạnh là chỗ nói điều còn thiếu |
| `table` | còn một [bảng chốt](#sáu-bảng-chốt-của-buổi-phỏng-vấn) đang chờ người dùng bấm gửi (`PendingConfirmTableGate`) | cổng ĐÓNG; `#tableGate` nói ra **bảng nào** + **nút nào**, nhưng chỉ khi cổng lẽ ra đã mở — xem [dưới](#cổng-đóng-khi-còn-một-bảng-chờ-chốt) |
| `ready` | lượt BA mới nhất mời tạo tài liệu — **hoặc** draft đã có và cổng readiness đang đủ; và không còn bảng nào chờ chốt | cổng MỞ, nút "Write Requirement" |
| `running` | vòng soạn đang xếp hàng/đang chạy | cổng ĐÓNG; tiến độ đã có panel `.workflow-progress` trong chat, xong thì `requirement-workflow.js` tải lại trang |
| `done` | draft đã có và hội thoại chưa có gì mới kể từ vòng soạn gần nhất | cổng ĐÓNG hẳn; người dùng nhắn thêm một câu là nó mở lại |

**Soạn xong thì cổng ĐÓNG, không phải mở ra một nút "tạo lại".** Trạng thái `done` từng là `regenerate`: bày lại cả bản tổng kết (nay đã gỡ) kèm nút "🔄 Tạo lại tài liệu" và một hộp xác nhận GHI ĐÈ. Cả cụm đó là nhiễu ở đúng chỗ người dùng cần tập trung nhất. Panel workflow ngay phía trên đã nói *"Tài liệu đã sẵn sàng · Xem Product Brief"* và BA cũng vừa mời xem lại rồi bấm Approve, nên bong bóng này là lần **thứ ba** nói cùng một điều — mà lại đẩy hành động thật (đọc Brief → Approve) xuống dưới hàng chục dòng. Soạn xong rồi thì thứ đáng rà là chính Product Brief, và đường đó đã có, chính xác hơn hẳn: ghim ghi chú ngay trên bản xem trước (`ReviseBriefFromNotesUseCase`) hoặc nhắn thẳng trong khung chat. Còn cái nút thì tự nó vô nghĩa: bấm khi chưa bổ sung gì tốn 2–3 lời gọi LLM để ra gần đúng bản cũ rồi ghi đè bản đang có, mà model chạy ở `temperature > 0` nên bản mới có thể tệ hơn — chính lời dẫn cũ của cổng cũng khuyên *"nhắn thêm trong khung chat rồi tạo lại"*, tức một cái nút mà dòng chữ ngay trên nó bảo hãy làm việc khác. Đường soạn lại không mất: nhắn một câu là cổng mở lại ở `ready`, và lúc đó nút soạn từ hội thoại ĐÃ có thông tin mới.

### Cổng đóng khi còn một bảng chờ chốt

Vế thứ hai của `ready` — *draft đã có và cổng readiness đang đủ* — **không đọc lượt cuối**. Nó có mặt để cứu ca "bản Brief đang cũ dần" ở trên, nhưng đúng vì thế nó cũng mở cổng ở lượt mà BA vừa bày một BẢNG ra và vừa nói *"rà lại rồi bấm Gửi bảng … giúp mình"*. Hai việc chọi nhau nằm cách nhau vài dòng, và người dùng bấm cái nút.

Cái giá đo được (dự án JD Libary): Brief đã có, người dùng nhắn thêm hai báo cáo, `ReportMapGate` mở và BA bày bảng báo cáo — người dùng bấm "Write Requirement". Vòng soạn chạy trên một hội thoại mà bảng báo cáo còn chưa chốt, nên `Project.ReportMap` vẫn null ⇒ `ConfirmReportMapUseCase` chưa gieo màn hình báo cáo nào vào `PlannedScope` ⇒ tài liệu ra đời **thiếu hẳn phần báo cáo** ở `## 6. Screens To Generate`; rồi họ vẫn phải gửi bảng, và tin nhắn chốt bảng lại mở cổng lần nữa ⇒ một vòng soạn thứ hai ghi đè bản vừa sinh. Hai lần gọi LLM cho một tài liệu, lần đầu chắc chắn sai.

`PendingConfirmTableGate.Select` là hàm tất định trả lời *"còn bảng nào đang chờ gửi không"*, và **ba** chỗ đọc nó — nên không có bản chép tay nào: `Index.cshtml` (trạng thái `table`), `requirements.js` (frame `done`), và `ProductBriefDraftService` (chặn TRƯỚC mọi lời gọi LLM, cho các đường không đi qua cái nút: tab mở từ trước, đường POC-feedback, đường ghi chú trên bản xem trước).

Bốn ranh giới:

- **Xét "bảng còn treo", không xét "lượt này có bày bảng".** Lượt bày bảng đã tự dọn lời mời rồi (`TakeOverTurn`), và chốt chặn đó KHÔNG đủ: bảng treo theo DỰ ÁN nên nó còn nguyên trên màn hình qua F5 và qua các lượt sau. Xét đúng câu hỏi mà chính panel dùng để tự ẩn/hiện còn phủ luôn ca fail-open — lượt sau model không trả nổi bảng dùng được nên lượt đó chạy như chat thường và mời bấm nút, trong khi bảng của lượt TRƯỚC vẫn nằm đó.
- **Khi cổng lấy đi một cái nút thì phải GỌI TÊN bảng và nút.** `#tableGate` là một bong bóng BA xám, không nút, ngay dưới bảng: *"Mình chưa mở nút tạo tài liệu vì bảng báo cáo ngay phía trên còn đang chờ anh/chị chốt…"*. Nó chỉ dựng khi cổng **lẽ ra đã mở** (`tableGateSpeaks` ở `Index.cshtml`, `writeReqWouldOpen` ở `requirements.js`) — tức đúng hai đường mở cổng mà không đọc lượt cuối: fail-open (bảng của lượt TRƯỚC còn treo, đã cuộn khỏi tầm mắt, lượt này model mời bấm nút) và đường lùi *"draft đã có + bản đồ đã đủ"*. Ở đó im lặng đọc lên thành "hệ thống hỏng": người dùng vừa đọc một lời mời rồi nhìn xuống không thấy nút, mà cũng không có gì để bấm để biết vì sao. Tên bảng + nhãn nút đi từ chính cổng chặn ra `data-table-name`/`data-send-label` của từng panel, nên JS không chép lại danh sách bảng lần thứ hai.
- **Ở lượt vừa bày bảng thì KHÔNG nói gì.** Trạng thái `table` nuốt cả ca lẽ ra là `waiting`: lượt BA vừa bày bảng ra và vừa nói *"rà lại rồi bấm Gửi bảng … giúp mình"* thì chưa có lời mời tạo tài liệu nào, cổng vốn đã đóng im lặng. Bong bóng ở đó là lần **thứ hai** nói cùng một việc — ngay dưới một cái bảng đã có sẵn nút gửi và câu dẫn của chính nó — mà lại còn kéo khái niệm *"nút tạo tài liệu"* vào đầu người dùng đúng lúc họ đang rà từng dòng, để giải thích vì sao thiếu một cái nút chưa ai hứa. Cổng **chặn** thì không đổi gì: trạng thái vẫn là `table`, nút vẫn không được dựng, `ProductBriefDraftService` vẫn chặn trước mọi lời gọi LLM. Đây thuần là chuyện nói hay không nói.
- **Không có ngõ cụt.** Mọi bảng đều gửi được ngay: bỏ tích sạch vẫn là một quyết định hợp lệ và vẫn được lưu, bảng thông báo còn dòng trống người nhận thì popup của nó bày sẵn lối *"Không cần gửi"*. Nên "cổng đóng" ở đây luôn kèm đúng một việc bấm một cái là xong — khác hẳn cái nút mờ-và-khóa mà repo đã cố ý bỏ.

Chuỗi tự nhiên sau khi vá: chốt bảng báo cáo ⇒ các màn hình báo cáo gieo vào `PlannedScope` ⇒ đường mở lại của `ScreenScopeGate` bày bảng màn hình ⇒ cổng vẫn đóng nhưng nay gọi tên *bảng màn hình* ⇒ chốt xong mới tới nút tạo tài liệu. Đúng thứ tự phụ thuộc, và tài liệu chỉ được soạn MỘT lần.

**Đường lùi khi bản Brief đã cũ.** Cờ mời đọc chữ trong lượt BA mới nhất, nên có một ca kẹt: Brief đã tồn tại, người dùng nhắn một lời đính chính, BA đáp bằng một **câu hỏi** thay vì lời mời ⇒ cổng đóng và không còn đường nào soạn lại bản Brief đang cũ dần so với hội thoại. Vì vậy `ready` xét thêm cổng readiness tất định, và **chỉ khi đã có draft** — trước lần soạn đầu tiên cổng vẫn đi đúng theo lời mời của BA như cũ. Cờ này do **server** tính ở cả hai đường (`Index.cshtml` lúc tải trang, `BAChatTurnResult.CoverageReady` → frame `done` lúc chat): luật *"mọi dòng áp dụng đã [RÕ]"* không được phép có bản sao trong JS.

**Không có nút mờ-và-khóa nào nữa.** Nút "Write Requirement" từng sống ở sidebar với cả bốn trạng thái, trong đó ba là nhiễu: `waiting` bày ra một nút mời bấm mà bấm không được, `running` lặp lại đúng điều panel tiến độ workflow đang nói, và ở `ready` thì người dùng đã có nút thật ngay dưới câu BA vừa mời — hai nút cùng một việc, cách nhau nửa màn hình. Nút nay là **nút submit thật** của `form.write-req` nằm trong cổng, nên không còn đường "bấm hộ" nào: cổng soát mâu thuẫn (`initConflictGate`) là listener trên chính nút/form đó, và mọi cú bấm đều đi qua nó.

**Nhãn nút là "Write Requirement", đúng cái tên mọi chỗ khác đã gọi nó.** Prompt BA mời người dùng bấm nút *"Write Requirement"* (`requirement-chat.v4.md`), cổng readiness dò đúng cụm đó trong lượt cuối để suy `requirementReady`, thông báo lỗi lúc Approve thiếu Brief cũng gọi vậy — chỉ riêng cái nhãn trên màn hình từng được dịch thành *"Tạo bản mô tả sản phẩm"*, để lại một khoảng vênh mà người dùng phải tự bắc cầu: BA bảo bấm một nút không tồn tại trên màn hình. Tên nút ở đây là **danh tính của một bước quy trình** (như Approve), không phải một câu tiếng Việt cần dịch; lời dẫn và tooltip quanh nó vẫn tiếng Việt và đã nói đủ nút này làm gì.

**Nút rộng theo nhãn, không trải hết cổng.** `.summary-gate-bar .btn { width: 100% }` có từ thời thanh nút chứa một CẶP nút loại trừ xếp chồng ("tạo tài liệu" / "gửi đính chính") và cần hai nút bằng nhau. Nút thứ hai đã gỡ cùng bản tổng kết, nên luật đó chỉ còn tác dụng kéo nút duy nhất còn lại thành một dải xanh đặc chạy gần hết bề rộng cổng (~92% khung chat) — đọc lên như một băng thông báo chứ không như thứ bấm được, và vì nó ăn đứt `.write-req-btn` về độ ưu tiên nên kích cỡ thật của nút không sửa được ở chỗ khai báo nút. Luật đã bỏ; nút nay có sàn `min-width`, đệm rộng hơn, bo góc 10px theo khuôn các điều khiển khác trong khung chat, neo trái thẳng hàng với lời dẫn, và chỉ trải hết bề rộng ở khung hẹp (≤ 640px) nơi vùng bấm to mới là đúng.

Cổng chỉ còn **một** thứ để vẽ — trạng thái — và nó đến từ đúng **một** frame SSE (`gateState`: cờ mời + cờ readiness ở frame `done`), nhưng `syncWriteReqGate()` vẫn viết dạng **toàn phần** (mọi trạng thái đều có nhánh): một hàm chỉ vá mẩu nó vừa đổi thì thêm một mẩu trạng thái nữa là có ngay tổ hợp không ai vẽ đúng — đúng lỗi đã gặp thời cổng bị hai frame điều khiển.

Đính chính đi qua **một lượt chat bình thường**, không qua endpoint riêng: BA đọc và xác nhận lại cách hiểu mới, nhật ký gộp lượt đó, cổng tự mở lại ở lượt mời kế tiếp. Đây cũng là điều kiện để bước soạn tài liệu (vốn đọc transcript) thấy được đính chính — ghi chú nằm ngoài transcript thì chỉ là trang trí. **Ranh giới với cổng "chốt nhanh" đã bỏ:** mọi dòng trong nhật ký đều là điều người dùng ĐÃ nói hoặc đã bấm đồng ý (`decision-log.v1.md` cấm suy diễn); BA không bao giờ điền hộ ô trống rồi ghi vào hội thoại như lời người dùng — chỗ trống vẫn phải hỏi tiếp trong chat, và cổng readiness vẫn là thứ quyết định khi nào cổng mở.

## Lượt hỏi GỘP, chuẩn `[RÕ]` và phanh chống hỏi lại

**Lượt hỏi GỘP (2–4 câu hỏi độc lập một lượt).** Phỏng vấn được thiết kế "mỗi lượt một câu hỏi" và cổng readiness chỉ mở khi MỌI nhóm áp dụng đã `[RÕ]` — hai điều đúng về chất lượng nhưng cộng lại thành hàng chục lượt chat, và người dùng nghiệp vụ bận thì bỏ dở chứ không có cách nào rút ngắn. Bản trước rút ngắn bằng cổng **"chốt nhanh phần còn lại"**: BA tự soạn một phương án cho mỗi nhóm còn trống, người dùng duyệt một lần. Cổng đó **đã bỏ**, vì nó rút ngắn ở sai chỗ — phương án do BA soạn được ghi vào hội thoại **như lời của chính người dùng**, nên bản đồ bao phủ đầy lên mà không ai thật sự trả lời câu nào, và mọi tầng phía sau (Product Brief, spec, POC, UAT) tin đó là điều người dùng đã nói. Với hội thoại còn ngắn thì phần lớn phương án là BA phỏng đoán theo thông lệ, tức là tài liệu của BA đoán, ký tên người dùng.

Nay thứ được rút ngắn là **số vòng đi-về**, không phải độ sâu khai thác: BA vẫn là người HỎI, người dùng vẫn là người TRẢ LỜI, nhưng một lượt chở được nhiều câu hỏi.

- **Phép thử để được gộp** (`BusinessAnalyst/requirement-chat.v4.md`): *câu trả lời của câu này có làm ĐỔI câu hỏi kế tiếp không?* Không đổi ⇒ được gộp (các nhóm rời nhau: quy mô sử dụng, thông báo, báo cáo, dữ liệu & danh mục, phân quyền). Có đổi ⇒ **phải hỏi một mình**: xin câu chuyện thật, đào ngoại lệ, chốt ví dụ số, chốt kịch bản luồng, gỡ mâu thuẫn, nhịp tóm tắt kiểm chứng. Gộp mấy câu đó là mất đúng cái phễu mở → đào sâu → chốt.
- **Trần cứng 4 câu/lượt, chặn TẤT ĐỊNH ở `BAChatReplyParser`** — không chỉ dặn trong prompt. Model luôn có xu hướng gộp tối đa để "xong sớm", và một lượt 12 câu hỏi chính là cổng chốt nhanh đội lốt phỏng vấn. Trần áp ở **cả hai** đường vào: `Parse` (model trả text) và `Normalize` (structured output trả thẳng `BAChatReply` — đường mặc định của các model tốt, nếu chỉ chặn trong `Parse` thì đúng những model đó không bị chặn).
- **Hình dạng bộ chip phải khớp cờ `multiSelect`, chặn TẤT ĐỊNH ở `BAChatReplyParser`.** Một bộ gợi ý chỉ thuộc đúng một trong hai kiểu: **phương án thay thế** (mỗi chip là câu trả lời trọn vẹn, chọn cái này loại cái kia ⇒ chọn MỘT) hoặc **liệt kê thành phần** (câu trả lời thật là một danh sách, mỗi chip là một MẢNH ⇒ chọn NHIỀU). Model hay trộn hai kiểu: hỏi *"gồm những vai trò nào?"* — đúng kiểu liệt kê nên bật `multiSelect` — nhưng chip vẫn giữ dạng GÓI lồng nhau và phủ định nhau (`["Nhân viên và HR/đào tạo", "Nhân viên, quản lý và HR", "Thêm HoD phòng ban", "Chỉ bộ phận HR/đào tạo"]`). UI cho tích ô 1 + ô 4 cùng lúc, và thứ gửi đi là một câu trả lời **tự mâu thuẫn** được chắt thẳng vào bản đồ bao phủ với "Điều đã chốt" như lời người dùng — từ đó không tầng nào phía sau phân biệt được nữa. Parser nhận diện ba dấu hiệu "chip này là một PHƯƠNG ÁN, không phải một mảnh" (gói nhiều thứ trong một dòng; mở đầu bằng *"Chỉ…"*/*"Tất cả…"*/*"Không…"*; không tự đứng một mình như *"Thêm HoD…"*) rồi **hạ `multiSelect` về `false`** — áp ở cả hai đường vào và cho cả chip lượt-đơn lẫn chip từng câu của lượt gộp. Sửa **chỉ một chiều**, không bao giờ tự bật: hạ nhầm thì người dùng mất tiện ích tích nhiều ô (vẫn bấm được một chip, vẫn tự nhập được), bật nhầm thì sinh ra dữ liệu sai mà mọi bước sau tin là thật — hai cái giá không cùng hạng. Prompt (`requirement-chat.v4.md`, mục *"HAI KIỂU BỘ GỢI Ý"*) dạy cách viết chip nguyên tử; parser chỉ là cái phanh.
- **Câu ĐÓNG mới có chip; câu MỞ thì KHÔNG, chặn TẤT ĐỊNH ở `BAChatReplyParser`.** Luật trước bắt *"mỗi khi bạn HỎI bất cứ điều gì thì PHẢI kèm gợi ý"*, nên BA xin một câu chuyện rồi vẫn dựng ra một hàng chip. Lỗi thật đã gặp trên màn hình: *"Anh/chị kể giúp một lần gần nhất lập kế hoạch cho các lớp học trong năm: bắt đầu từ đâu, thực hiện những bước nào, và kết quả cuối cùng cần có là gì?"* với `["Đã có danh sách khóa học", "Bắt đầu từ nhu cầu đào tạo", "Đang theo dõi bằng Excel", "Chưa có quy trình cố định"]`. Bốn chip chỉ chạm vế *"bắt đầu từ đâu"*, mà ở lượt hỏi một câu **bấm chip là GỬI NGAY** — nên *các bước* và *kết quả cuối cùng*, đúng hai thứ đắt nhất, không bao giờ được kể; rồi mẩu bốn chữ đó được chắt vào bản đồ bao phủ với "Điều đã chốt" **như câu trả lời thật của người dùng**, và nhóm coi như đã hỏi xong. Chip ở đó không phải tiện ích mà là một cái bẫy. Phép thử của prompt (`requirement-chat.v4.md`, mục *"CÂU ĐÓNG hay CÂU MỞ"*): *viết được 2–5 đáp án mà MỖI đáp án là câu trả lời TRỌN VẸN không?* — được ⇒ câu đóng, bắt buộc kèm chip; các đáp án chỉ trả lời được một MẨU ⇒ câu mở, `suggestions: []` + `openEnded: true`. Parser áp cờ đó ở cả hai đường vào và cho cả câu lượt-đơn lẫn từng câu của lượt gộp: `openEnded` ⇒ **xóa chip** (không bao giờ có hai chỗ trả lời cho một câu), cộng một nhận diện hẹp theo CỤM TỪ (*"kể giúp"*, *"mô tả"*, *"nói rõ hơn"*…) tự chuyển câu xin-lời-kể sang mở. Sửa **chỉ một chiều** (đóng → mở), không bao giờ tắt cờ BA đã đặt: bật nhầm thì người dùng phải gõ thay vì bấm, bỏ sót thì sinh ra một câu trả lời cụt mà mọi tầng sau tin là lời người dùng — hai cái giá không cùng hạng. Mặc định vẫn là câu đóng có chip: bỏ chip ở câu đóng là bắt người dùng nghiệp vụ gõ tay đúng thứ đáng lẽ bấm một cái là xong.
- **Chip "KHÁC" TRẦN bị XÓA, chặn TẤT ĐỊNH ở `BAChatReplyParser`.** Chip mà toàn bộ nội dung chỉ là *"không phải mấy cái kia"* — *"Khác"*, *"Tự nhập"*, và các bản đội lốt nghiệp vụ *"Quy tắc khác"*, *"Trạng thái khác"*, *"Cách xử lý khác"* — nói **đúng bằng** ô *"Ý khác"* nằm ngay dưới mọi hàng chip, chỉ thiếu đúng phần đắt nhất: nội dung. Mà ở lượt một câu **bấm chip là GỬI NGAY**, nên cú bấm đó gửi đi một lượt user rỗng (*"Quy tắc khác"* — quy tắc gì thì không ai biết) trong khi bản đồ bao phủ tính là nhóm đó đã hỏi VÀ đã trả lời: đúng ca *"câu trả lời rỗng"* mà prompt cảnh báo, chỉ khác là lần này chính bộ chip bày sẵn cái bẫy. Prompt cấm chip này từ lâu nhưng cấm theo **mặt chữ** (*"Khác"*, *"Tự nhập"*), nên model né được chỉ bằng cách thêm một danh từ vào trước — ca thật đã gặp trên màn hình. `DropBareOtherChips` cấm theo **hình dạng**: đuôi *"khác"* + phần đầu là một danh từ mê-ta (`MetaChipHeads`), áp cho **mọi** câu chứ không riêng câu liệt kê (khác chip chốt hạ, thứ chỉ vô nghĩa ở câu liệt kê). Đây là chip thứ hai và cuối cùng được phép **xóa**, cùng lý lẽ với chốt hạ: xóa không mất gì, vì lối thoát vẫn còn nguyên ở cái ô. Hai chốt giữ nó không xóa quá tay — danh sách đầu mê-ta cố ý **hẹp** (*"Chuyển sang phòng ban khác"* chở nội dung thật ⇒ giữ; lọt lưới thì mất tiện ích, không mất dữ liệu), và **xóa xong phải còn ≥ 2 chip**, thứ giữ nguyên vẹn bộ HAI chip mà prompt kê sẵn ở lượt xin chốt (`["Đồng ý", "Tôi muốn khác"]`) — ở đó vế *"khác"* không phải lối thoát mà là một trong hai **nhánh trả lời**, xóa đi là biến câu hỏi thành cái gật bắt buộc. Đúng bộ chip đó lại là bộ mà cú bấm *"khác"* tốn kém nhất, nên tầng dưới đỡ tiếp: `isDissentChip` mở ô nhập tại chỗ thay vì gửi (mục kế).
- **Chip BẤT ĐỒNG mở ô tự nhập TẠI CHỖ, không gửi ngay.** Prompt kê sẵn ba bộ chip có vế từ chối — `["Đúng rồi", "Không, tính khác"]` (chốt ví dụ số / kịch bản luồng), `["Đồng ý", "Tôi muốn khác"]` (xin chốt một phương án), `["Đúng rồi, tiếp tục", "Tôi muốn sửa lại"]` (nhịp tóm tắt kiểm chứng) — và cả ba đều thuộc nhóm **bắt buộc hỏi một mình**, tức các lượt đắt nhất của cuộc phỏng vấn. Nhưng ở lượt hỏi một câu, **bấm chip là GỬI NGAY**, nên vế từ chối gửi đi một lượt user RỖNG NỘI DUNG: phủ định mà không kèm cái đúng. Giá phải trả là trọn một vòng LLM chỉ để BA hỏi lại *"vậy anh/chị tính thế nào?"*, trong khi nhóm bị đụng tới đã rớt khỏi `[RÕ]` mà không có thông tin nào thay thế — và **lượt quay lại duy nhất** mà mỗi nhóm được phép (xem mục trên) bị tiêu đúng vào đó; câu trả lời thật thì đang nằm sẵn trong đầu người dùng đúng giây họ bấm "Không". Nay `requirements.js` nhận diện chip bất đồng (`isDissentChip`) rồi **mở ô nhập ngay trong hàng chip** thay vì gửi. Bốn điều ràng buộc thiết kế này:
  - **Tin nhắn đi ra là `chip — lời viết thêm`**, giữ lại vế phủ định: bỏ đi thì *"làm tròn xuống"* đứng trơ trọi và các tầng chắt lọc không còn biết nó đang bác lại cách tính nào.
  - **Ô KHÔNG bắt buộc** — để trống rồi bấm gửi thì tin nhắn đúng bằng chip như trước, và dòng nhắc dưới nút nói rõ điều đó. Bắt gõ mới đi tiếp được sẽ đẩy một phần người dùng sang bấm "Đúng rồi" cho xong: đổi một lượt cụt lấy một **xác nhận giả**, thứ đắt hơn hẳn vì mọi tầng sau tin nó là thật.
  - **Hàng chip luôn có ô "Ý khác", và ô đó MỞ SẴN** — không nấp sau một cái nút. Một hàng chip đọc như tập đáp án ĐÓNG: không có ô này thì người dùng có ý riêng chỉ còn cách bỏ qua chip rồi tự tìm xuống khung chat, thao tác mà phần lớn người dùng nghiệp vụ không nghĩ ra. Một viên nút *"✎ Ý khác"* cũng **không** sửa được điều đó — nó không nói được gì mà cái ô mở sẵn không tự nói, nhưng vẫn bắt người ta NGHĨ RA là còn lối thoát ở đó rồi mới bấm, trong khi người đang rà một hàng đáp án thì đọc lướt chứ không đi tìm nút. Khối này do JS dựng cho **cả hai** đường render (`ensureOtherControls`, như `ensureMultiControls`) chứ không nhân đôi markup sang `Index.cshtml`: nó không mang dữ liệu của lượt nào nên server không có gì để render. Lượt câu MỞ không có chip nên cũng không có ô này — ở đó khung chat đã là chỗ trả lời duy nhất.
  - **Nhãn khoét trên viền, không phải một dòng chữ phía trên ô.** Ô mang **nhãn nổi** — *"Ý khác"* ở **cả hai** chỗ (`.suggestion-other-cap` ở hàng chip lượt-đơn, `.batchq-other-cap` trên thẻ gộp) vì hai ô làm đúng một việc và ghép vào tin nhắn theo đúng một luật; hai tên gọi cho một thứ chỉ bắt người dùng học lại từ đầu ở màn hình thứ hai. Nó giữ danh tính của ô mà không tốn thêm một dòng nào trong một khung chat vốn đã chật. Ô là `textarea` **tự cao theo nội dung** (`autoGrowOtherBox`, trần 200px) chứ không phải ô một dòng: câu trả lời thật ở đây thường dài hơn một dòng, và cuộn ngang để đọc lại thứ mình sắp gửi là đúng lúc không được phép bắt họ làm. Ở chế độ chọn NHIỀU, hàng nút gửi riêng của ô ẩn đi: chữ tự nhập được gộp vào nút *"Gửi các lựa chọn"* như một lựa chọn nữa, hai nút cùng một việc cách nhau hai dòng là mời người dùng bấm nhầm. **Viền xanh của ô là dấu hiệu ĐANG chọn, không phải trang trí**: lúc ô còn rỗng nó mang viền xám của gợi ý chưa chọn (`.suggestion-option` / `.batchq-choice`) và chỉ chuyển xanh khi con trỏ đang ở trong ô hoặc trong ô đã có chữ (`:focus` / `:not(:placeholder-shown)` — không cần JS gắn class theo từng phím gõ). Tô xanh sẵn thì ô đọc như một lựa chọn đang được chọn, và người dùng bấm một gợi ý xong vẫn thấy hai thứ cùng xanh — không còn nhìn ra mình đã chọn cái nào. Nhãn nổi đổi màu theo viền, vì một ô nửa xám nửa xanh lại thành một trạng thái thứ ba không có thật.
  - **Nhận diện bắt theo HÌNH DẠNG, không chỉ theo cụm cố định.** Ngoài danh sách cụm (`DISSENT_CHIP_CUES`) và biến thể *"Không, … khác"*, mọi chip **kết bằng "khác"** đều tính là bất đồng: *"Quy tắc khác"*, *"Trạng thái khác"*, *"Cách xử lý khác"* là cùng một chip đội ba cái tên, danh sách cụm không bao giờ phủ hết. Ở đây bắt **rộng hơn** parser được, vì hai tầng trả giá khác nhau cho cùng một lần nhận nhầm: JS chỉ tốn thêm một cú bấm *"Gửi"* (ô để trống vẫn gửi nguyên chip), còn parser thì **xóa hẳn** chip nên phải hẹp.
  - **Nhận diện đặt ở JS, không ở `BAChatReplyParser`.** Nó chỉ quyết định cú bấm MỞ Ô hay GỬI NGAY, không đổi nội dung được lưu — khác hẳn các chốt chặn tất định của parser (`multiSelect`, `openEnded`) vốn sửa chính câu trả lời trước khi nó lên màn hình. Vẫn giữ luật **sửa một chiều**: nhận nhầm ⇒ tốn thêm một cú bấm "Gửi"; bỏ sót ⇒ đúng bằng hành vi cũ. Không cú bấm nào bị chặn, không chip nào bị xoá.
- **Lượt XIN FILE cũng phải đứng một mình.** Xin file không phải câu hỏi nên nó không lọt vào danh sách "hỏi một mình" ở trên, nhưng nó hỏng đúng cùng một kiểu: người dùng đọc xong thì đi tìm file, và vế còn lại của lượt bị nuốt mất. Ca thật, BA vừa xin file Master List vừa hỏi *"hiện nay việc lập kế hoạch và tính số lớp được thực hiện như thế nào và điểm khó chịu nhất là gì?"* — người dùng đính kèm file rồi đáp đúng một dòng (*"làm thủ công, tự tính tay thường bị sai sót, data không đồng bộ"*), tức chỉ chạm vế *điểm khó chịu*; **các bước** của quy trình hiện tại không bao giờ được kể, mà nhóm *Quy trình hiện tại & điểm khó* vẫn được chắt là đã hỏi xong nên BA không quay lại. Prompt tách làm hai lượt: lượt này chỉ xin file (`suggestions` rỗng, `openEnded: true`), đọc xong rồi mới xin lời kể — file thường trả lời hộ một phần câu định hỏi, nên hỏi trước khi đọc file còn là tự bỏ mất lợi thế đó. Không chặn được bằng máy (phân biệt "lời nhờ đính kèm" với "câu hỏi" là việc của model), nên lưới an toàn là điểm chấm trong golden set.
- **NGUỒN của dữ liệu: hỏi *từ đâu ra*, không hỏi *nối bằng gì*.** Danh sách cấm hỏi kỹ thuật từng gộp luôn *"tích hợp hệ thống ngoài"*, tức cấm cả vế nghiệp vụ — và chỗ hỏng không lộ ra trong hội thoại mà ở cuối đường: tài liệu im lặng về nguồn ⇒ bước soạn tài liệu mặc định là nhập tay ⇒ POC seed một màn hình CRUD đầy nút Thêm/Sửa/Xóa cho danh sách nhân viên mà thực tế HR đổ sang hằng tháng (cùng loại thiệt hại với cột `Revision Number` của hệ cũ nằm lại trong app mới, chỉ khác là sai cả một màn hình). Ranh giới nay tách đôi trong `requirement-chat.v4.md` (mục *"NGUỒN của dữ liệu"*): **nghiệp vụ** = dữ liệu vào ứng dụng bằng đường nào (có người tải file lên / nhập tay / app tự lấy về), cập nhật khi nào, và trong app còn sửa được không; **kỹ thuật, vẫn cấm** = API/webhook/đọc thẳng DB/real-time hay chạy lô/định dạng trao đổi. Quy tắc có **điều kiện kích hoạt**: chỉ hỏi khi CHÍNH người dùng nhắc tới một hệ thống/file đang dùng — cùng câu nói kích hoạt luật xin file, nên thứ tự bắt buộc là lượt đó chỉ xin file, đọc xong mới hỏi nguồn. Phía coverage khoá luôn chiều ngược lại: người dùng chưa hề nhắc tới nguồn nào ⇒ mặc định dữ liệu do chính app quản lý, TUYỆT ĐỐI không giữ dòng ở `[MỘT PHẦN]` với *"còn thiếu: nguồn dữ liệu"* — đó đúng là hình dạng vòng lặp câu hỏi chết mà `CoverageDeadQuestionLoopTests` đã phải dựng lưới một lần. Chốt bằng `BAChatDataSourceRuleTests` + điểm chấm golden set.
- **Câu hỏi kép mà chip chỉ trả lời được một nửa** (*"những vai trò nào sẽ dùng ứng dụng **và mỗi vai trò chịu trách nhiệm gì**?"* với chip là danh sách vai trò) bị cấm trong prompt — người dùng bấm chip là hết lượt, nửa sau rơi mất trong khi BA tưởng đã hỏi. Chỗ này KHÔNG chặn được bằng máy (tách một câu hỏi làm đôi là việc chỉ model làm đúng), nên lưới an toàn nằm ở tầng chấm điểm: `requirement-coverage.v3.md` nay có chuẩn `[RÕ]` riêng cho **Đối tượng người dùng & vai trò** — phải rõ **mỗi vai trò làm gì**, một danh sách tên vai trò trần chỉ được `[MỘT PHẦN]` kèm *còn thiếu: mỗi vai trò làm/xem được gì*. Nhờ vậy nửa câu trả lời bị chấm là thiếu và BA buộc phải hỏi nốt ở lượt sau, thay vì dựa vào việc BA không bao giờ hỏi câu kép.
- **Contract**: `BAChatReply.Questions` (`BAChatQuestion[]`: nhóm + câu hỏi + gợi ý riêng + cờ chọn-nhiều + cờ `openEnded`), lưu ở cột `AgentConversation.Questions` (mã hóa at rest như `Message`/`Suggestions`). Lượt hỏi một câu vẫn đi đường cũ (`message` + `suggestions`) — đó là ca thường gặp nhất VÀ là ca bắt buộc của mọi câu hỏi đào sâu, nên nó không đổi gì. `Normalize` giữ hai đường **loại trừ nhau**: có thẻ hỏi thì không có chip lượt-đơn (chip bấm là GỬI NGAY, để cả hai cùng sống thì một cú bấm cướp lượt trước khi người dùng kịp trả lời các câu còn lại), và một lượt "gộp" chỉ có một câu bị **hạ về** đường một-câu với câu hỏi nối vào `message`.
- **UI**: thẻ nhiều dòng trong khung chat (`.batchq`), mỗi dòng là một câu hỏi + hàng gợi ý bấm + **một ô "Ý khác" luôn mở** ở dưới (dòng `openEnded` thì không có hàng gợi ý, chỉ còn ô — một dòng chỉ có câu hỏi mà không có chỗ trả lời đọc như dòng bị lỗi); nút gửi đếm live số câu đã trả lời và nói rõ **không cần trả lời hết** (câu để trống thì BA hỏi tiếp ở lượt sau). Không dòng nào in **nhãn nhóm** của bản đồ lên đầu câu hỏi — xem [Câu chặn không nói nhóm](#hai-cổng-chất-lượng-phía-yêu-cầu-đủ-và-không-mâu-thuẫn). Render ở CẢ hai đường — server lúc tải trang, JS ở frame `done` — vì F5 giữa chừng mà thẻ biến mất thì người dùng mất các câu chưa trả lời, và `message` của lượt gộp chỉ là câu dẫn.
  - **Chip giữ lựa chọn, ô giữ lời tự nói — hai vai TÁCH HẲN.** Trạng thái chọn nằm trên chính chip (`.is-on`, `batchPicks`); ô bên dưới là ô *"Ý khác"* đúng như ở hàng chip lượt-đơn, và câu trả lời gửi đi của dòng đó là hai vế ghép lại: `chip đã bấm — lời viết thêm` (`batchAnswerOf`, cùng luật ghép với `otherAnswerMessage`). Trước đây bấm chip **chép nguyên văn chip vào ô**: màn hình nói một điều hai lần (chip sáng ngay trên, y hệt câu chữ đó nằm trong ô ngay dưới) mà không đổi lại được gì — sửa một chữ trong ô là chip tắt, tức không hề "sửa lời gợi ý" như hình thức của nó hứa; và chỗ duy nhất để nói thêm một ý nằm ngoài mọi gợi ý thì bị chiếm mất. Đây cũng là lý do nhãn ô quay lại là **"Ý khác"**: nó lại đúng là thứ nó chứa.
  - **Chọn-nhiều: mỗi chip là một công tắc riêng; chọn-một: chip vừa bấm sáng và tắt các chip còn lại**, bấm lại chính chip đang chọn = bỏ chọn, để một cú bấm nhầm có đường lùi (`toggleBatchChip`). **Bấm chip KHÔNG focus vào ô**: bấm gợi ý là thao tác "câu này xong rồi", focus thì trên điện thoại bật bàn phím lên che mất các câu còn lại của thẻ.
  - **Nháp của thẻ lưu HAI vế riêng** (`{picks, other}` theo từng câu hỏi) chứ không lưu câu trả lời đã ghép: ghép rồi thì lúc F5 đổ về không tách lại được đâu là chip đâu là lời viết thêm, và cả cụm sẽ rơi vào ô *"Ý khác"* — người dùng thấy nguyên văn gợi ý nằm trong ô mình chưa từng gõ. Nháp lưu theo dạng CŨ (một chuỗi) vẫn đổ về được, vào đúng ô *"Ý khác"*: chữ họ đã gõ không mất, và không chip nào bị bật lên thay họ.
- **Không có endpoint riêng**: cả cụm được soạn thành MỘT tin nhắn `- câu hỏi: trả lời` rồi gửi qua đúng đường chat thường. Nhờ vậy không có đường ghi thứ hai nào lệch khỏi luồng chính, và mọi thứ đã đúng ở lượt chat (cổng readiness, chắt lọc bản đồ bao phủ, decision log) tự khắc đúng ở đây. `ConversationTurnRenderer` render cả các câu hỏi vào transcript — thiếu nó thì reader chỉ thấy câu trả lời mà không biết nó trả lời cho câu nào.

**Chuẩn `[RÕ]` được siết ở `BusinessAnalyst/requirement-coverage.v3.md`.** Lượt gộp làm người dùng dễ trả lời ngắn hơn, nên "giám khảo" của cổng phải khắt khe hơn ở đúng chỗ một câu khẳng định chung chung có thể trôi qua: ngoại lệ phải có **một tình huống hỏng cụ thể kèm cách xử lý**; quy tắc nghiệp vụ phải có **điều kiện và hệ quả**; vòng đời phải **gọi tên các trạng thái** và điều kiện chuyển; thông báo phải rõ **ai nhận, khi nào** và hai vế phải **ghép được với nhau** (một danh sách vai trò trần trả lời cho câu hỏi gộp nhiều loại sự kiện chỉ `[MỘT PHẦN]` — nếu không, tài liệu đóng băng thành "mọi thay đổi trạng thái gửi cho cả bốn nhóm", tức mỗi lần một bản kế hoạch đổi trạng thái thì toàn bộ nhân viên nhà máy nhận email); phân quyền phải rõ **vai nào làm/xem được gì** ("phân quyền theo vai trò" là nhắc lại tên nhóm, không phải câu trả lời) và các thao tác của **người dùng cuối** còn phải rõ **ai đủ điều kiện làm**; *Dữ liệu / danh mục chính* có thêm một chuẩn **CÓ ĐIỀU KIỆN KÍCH HOẠT** — người dùng đã nêu một hệ thống/file mà dữ liệu đang nằm sẵn ở đó thì phải rõ **vào app bằng đường nào** và **cập nhật khi nào**, còn chưa ai nhắc tới nguồn thì mặc định app tự quản lý và dòng KHÔNG được giữ `[MỘT PHẦN]` vì chuyện đó. Thêm ba điều **không được tính là căn cứ**: (1) lời của BA mà người dùng chưa xác nhận — trích dẫn `{nguồn: …}` phải lấy từ lượt của NGƯỜI DÙNG hoặc tài liệu nguồn, vì một dòng `[RÕ]` sai thì BA sẽ không bao giờ hỏi lại nhóm đó nữa; (2) một tiếng "có/không" trả lời cho một câu hỏi MỞ; (3) lượt người dùng nói họ **không hiểu câu hỏi** — lượt đó không chứa dữ kiện nào, và lượt BA kế tiếp mở đầu bằng *"giờ mình đã rõ: …"* là BA tự trả lời hộ. Hai chuẩn cũ (định lượng phải có ví dụ số, luồng/trạng thái phải có chuỗi bước xác nhận) giữ nguyên.

**Ba chuẩn cắt ngang** (áp cho mọi dòng, không riêng nhóm nào) chặn đúng loại lỗ hổng mà tài liệu vẫn trông đầy đủ: **tham số của một quy tắc phải có nguồn** (biết công thức mà không biết sĩ số tối đa được nhập ở đâu ⇒ bản kỹ thuật tự đẻ ra một màn hình cấu hình chưa ai yêu cầu); **danh mục dùng để kiểm tra dữ liệu phải có người quản lý** (bộ cột của file upload KHÔNG thay được cho câu hỏi này); **dữ kiện mồ côi thì chưa xong** — một trường/tham số được nhắc tới mà không quy tắc nào dùng tới là dấu hiệu còn một luật chưa được hỏi, không phải chi tiết thừa.

**Chốt chặn `[RÕ]` ⇄ điểm tồn đọng (`CoveragePendingGuard`).** Bản đồ bao phủ và "Điểm cần làm rõ còn tồn
đọng" được chắt bởi **hai** lời gọi LLM khác nhau, đọc cùng một hội thoại nhưng không bao giờ nhìn thấy
nhau — nên chúng nói ngược nhau mà không tầng nào biết. Ca thật: bản đồ ghi «Luồng ngoại lệ», «Vòng đời &
trạng thái» và «Dữ liệu / danh mục chính» là `[RÕ]` trong khi hệ thống đang giữ đúng bảy điểm tồn đọng
thuộc ba nhóm ấy (*"đăng ký lại được sau khi ticket bị Reject không"*, *"kết quả Complete/Not Complete/No
Show dùng để xử lý bước nào"*, *"Item ID và Item Title có tạo thành cặp duy nhất không"*). `[RÕ]` không
phải một nhãn trạng thái mà là một **lệnh cấm BA hỏi lại**, nên bảy điểm đó vĩnh viễn không được lấy, và
bước soạn tài liệu — vốn bị cấm giả định — nhận một khoảng trống mà không cổng nào báo. Nay
`interview-outlook.v1.md` gắn mỗi mục tồn đọng một **thẻ nhóm** (`[Vòng đời & trạng thái] …`, chép đúng
một trong 12 nhãn), và guard chạy ngay sau lượt distill hạ mọi dòng `[RÕ]` còn mục của nhóm đó xuống
`[MỘT PHẦN]`, ghi chính mục ấy vào phần `còn thiếu:` — tức điểm tồn đọng trở thành câu chặn của cổng
readiness thay vì một ghi chú không ai đọc. Bốn ràng buộc của thiết kế:

- **Một chiều, chỉ hạ không nâng.** Hạ nhầm thì BA hỏi thêm một câu; bỏ sót thì sinh ra một khoảng trống
  mà mọi tầng sau tin là đã đủ — cùng cách cân giá với các chốt chặn của `BAChatReplyParser`.
- **Chạy ở đường GHI, không ở đường đọc.** Bản đồ là nguồn chân lý mà cổng readiness, panel tiến độ và bốn
  cổng bảng cùng đọc; lọc lúc đọc ở một chỗ là dựng lại đúng cảnh hai giám khảo lệch nhau mà thiết kế này
  đã bỏ đi.
- **Thẻ nhóm bị GỠ trước khi vào ngữ cảnh chat** (`CoveragePendingGuard.StripGroupTag`) — nhãn nhóm là từ
  vựng nội bộ, để nguyên là mời BA chép nó vào câu hỏi kế tiếp.
- **Trễ một lượt, có chủ ý.** Bản đồ gộp ngay trong lượt chat còn danh sách tồn đọng chắt ở hậu kỳ, nên
  guard của lượt N đọc danh sách tính tới lượt N−1: điểm vừa được trả lời vẫn hạ dòng một lượt rồi tự lên
  lại. Lưới đỡ đã có sẵn — prompt chat bắt BA tin HỘI THOẠI khi bản đồ chưa kịp cập nhật, và
  `AskedQuestionHistory` loại thẳng câu hỏi trùng. Đồng bộ hai nhịp thì phải dời distill xuống hậu kỳ, tức
  bản đồ dẫn lượt hỏi kế tiếp luôn cũ một lượt — đắt hơn nhiều.

**Chốt chặn bảng-đã-chốt ⇒ `[RÕ]` (`CoverageConfirmedTableGuard`).** Guard thứ hai của đường ghi, chạy
**sau** guard trên và đi ngược chiều nó — chỉ cho đúng hai nhóm chốt bằng bảng: «Phân quyền theo nghiệp vụ»
và [«Thông báo / nhắc nhở»](#bảng-thông-báo-bảng-cuối-cùng). Bảng của nhóm đã nằm trong DB ⇒ dòng bản đồ
của nhóm đó bị viết lại thành `[RÕ]`, và mẩu `còn thiếu:` — nếu còn sót — bị xóa.

Vì sao phải là máy chứ không phải prompt: `requirement-coverage.v3.md` đã ghi luật một chiều cho cả hai
nhóm (*"có khối bảng đã chốt ⇒ `[RÕ]`, **không có ngoại lệ nào**"*), và lượt distill được đính đúng khối
đó. Nhưng nó cũng được đính **bản đồ hiện có**, và bản đồ ấy thường mang sẵn một mẩu `còn thiếu:` từ lúc
bảng chưa chốt — do chính distiller viết, hoặc do `CoveragePendingGuard` ghi vào từ một điểm tồn đọng.
Model cập nhật phần tóm tắt theo bảng mới nhưng **giữ nguyên mẩu cũ**. Ca thật (dự án *JD Libary 7*, ba
lượt cuối của buổi 102 lượt): người dùng gửi bảng thông báo với đủ To/CC cho 4 sự kiện và tắt sự kiện thứ
5, bảng đã lưu, mà dòng bản đồ là *«[MỘT PHẦN] … đã chốt To/CC riêng từng sự kiện … còn thiếu: Chưa rõ
người nhận cho từng sự kiện thông báo»* — một dòng vừa nói đã chốt vừa nói chưa rõ. Cổng readiness lấy
nguyên mẩu ấy làm câu chặn, nên lượt kế tiếp của BA hỏi lại đúng điều người dùng vừa trả lời; và không lối
thoát nào còn lại: `NotificationMapGate` không bày lại bảng đã chốt, khối "đã chốt" cấm BA hỏi lẻ nhóm này.
Nút "Write Requirement" khóa vĩnh viễn.

- **Được phép NÂNG cấp, khác luật một chiều ở trên** — vì bằng chứng ở đây không do LLM chắt: nó là bảng
  người dùng tự tay bấm từng ô, và đường gửi đã bảo đảm nó ĐỦ (`NotificationMapBuilder.MissingRecipients`
  chặn mọi lần lưu còn dòng tích "Cần" mà chưa chọn người nhận). Guard không đoán thêm gì, nó đọc thẳng một
  dữ kiện tất định thay vì trông chờ model đọc hộ. Một bảng ghi từ TRƯỚC bất biến đó mà còn dòng thiếu
  người nhận thì guard im — chỗ đó thiếu thật.
- **Thắng cả điểm tồn đọng gắn vào hai nhóm này**, và đó là lý do nó chạy sau `CoveragePendingGuard`: BA bị
  cấm hỏi lẻ hai nhóm ấy, bảng thì không bày lại — nên một mục tồn đọng ở đây là **câu hỏi chết**, không ai
  hỏi được và không ai trả lời được.
- **Tóm tắt dòng được dựng lại TỪ BẢNG**, không giữ chữ của model: số đếm lấy từ chính bảng vừa lưu (*"4 sự
  kiện gửi email kèm người nhận riêng; 1 sự kiện người dùng chọn không gửi"*). Đây là hai nhóm mà một câu
  tóm tắt sai gây thiệt hại nặng nhất (*"mọi thay đổi trạng thái gửi cho cả bốn nhóm"*), mà dòng bản đồ thì
  đi thẳng vào ngữ cảnh mọi lượt chat sau — dựng bằng số đếm thì dòng không nói được điều bảng không chứa,
  và người dùng kiểm lại được bằng cách đếm.
- **Hai chỗ cố ý không đụng vào**: dòng đã `[RÕ]`/`[KHÔNG ÁP DỤNG]` (không trạng thái nào trong hai cái đó
  chặn cổng), và dòng mang cụm `AskedQuestionHistory.ReopenNote` — đó là lần duy nhất đường hỏi lại được mở
  ra, và do chính người dùng mở.
- **Chạy cả ở lượt không có gì mới** (và cả trên đường fail-open khi distill hỏng): người bị kẹt không gõ
  thêm gì cả, họ bấm gửi lại hoặc tải lại trang — bắt bản đồ chờ một lượt chat mới là bắt nó chờ đúng thứ
  đang bị chặn. Chỉ ghi DB khi bản đồ thật sự đổi.

Cùng họ với nó, `requirement-coverage.v3.md` thêm một điều **không được tính là căn cứ để `[RÕ]`**: câu trả
lời chỉ chạm được MỘT VẾ của một câu hỏi NHIỀU VẾ. BA bị cấm hỏi câu nhiều vế nhưng luật đó chỉ định
hướng, nên việc của distiller là **đếm vế**. Ca thật: *"từ lúc nhận file đến lúc lập kế hoạch, anh/chị làm
bằng công cụ nào, và điểm khó chịu nhất ở đâu?"* — ba vế, câu đáp 32 token chạm hai vế, **các bước** của
quy trình Excel không bao giờ được kể, mà dòng *Quy trình hiện tại & điểm khó* vẫn lên `[RÕ]` với đúng câu
đó làm bằng chứng.

**Phanh chống HỎI LẠI (`AskedQuestionHistory`).** Chuẩn `[RÕ]` càng khắt khe thì càng lộ ra một lỗ hổng của thiết kế: thứ DUY NHẤT ngăn BA hỏi lại là bản đồ bao phủ, mà bản đồ chỉ có độ phân giải theo **NHÓM** (12 dòng). Một dòng chưa `[RÕ]` nghĩa là "ưu tiên hỏi nhóm này", và vì mỗi câu hỏi của lượt gộp được gắn `group` = tên dòng bản đồ, model sinh lại đúng **câu hỏi mở đầu** của nhóm đó — người dùng vừa trả lời xong đã bị hỏi lại nguyên văn, chip gợi ý chính là câu họ vừa gõ. Cùng triệu chứng khi lượt chắt lọc bản đồ hỏng (fail-open giữ bản cũ): cả cụm câu hỏi lượt trước được phát lại y nguyên. Prompt đã cấm, nhưng prompt chỉ định hướng — nên có ba lớp:

- **Ngữ cảnh**: system message *"Các câu hỏi BẠN ĐÃ HỎI ở những lượt trước"* dựng từ chính hội thoại (câu của lượt gộp + `message` của lượt hỏi một câu), nạp cạnh bản đồ. Đây là thứ duy nhất phân biệt được "hỏi tiếp phần còn thiếu" với "hỏi lại điều vừa được trả lời" — bản đồ theo nhóm thì không.
- **Chặn tất định**: câu hỏi trùng (khoá chuẩn hoá, hoặc bao phủ tập từ ≥ 0.8 **và** Jaccard ≥ 0.5 — bắt được câu cũ sửa vài chữ mà không chặn oan câu đào sâu mới) bị **loại khỏi lượt trả lời trước khi nó lên màn hình**. Còn ≥ 2 câu ⇒ thẻ hỏi rút gọn; còn 1 ⇒ hạ về đường một-câu; còn 0 ⇒ thay bằng bước kế tiếp suy tất định từ bản đồ (`RequirementReadinessGate`) — nêu đúng nhóm còn thiếu, hoặc mời bấm "Write Requirement" khi bản đồ đã đủ. Không bao giờ để lại một lượt câm hay một câu dẫn cụt. Phanh này chỉ thấy các lượt CÓ hỏi — lượt không chở câu hỏi nào lọt qua nó, và được chặn riêng ở [lượt câm](#hai-cổng-chất-lượng-phía-yêu-cầu-đủ-và-không-mâu-thuẫn).
- **Ngoại lệ đúng chỗ**: nhóm mà người dùng vừa **đính chính trong chat** được MIỄN phanh. Nhận diện qua cụm `AskedQuestionHistory.ReopenNote` (*"người dùng báo phần này chưa đúng"*) mà lượt chắt lọc ghi vào phần `còn thiếu:` của dòng bị đụng tới — xem [Đính chính một nhóm](#đính-chính-một-nhóm-đường-thoát-khỏi-một-dòng-rõ-oan). Không có ngoại lệ này thì lời đính chính rơi vào im lặng: bản đồ đã hạ nhóm xuống nhưng câu hỏi của BA lại bị lọc mất vì trùng câu cũ.

Prompt `requirement-chat.v4.md` cũng tách rõ hai việc mà trước đây bị gộp làm một: `[CHƯA HỎI]` ⇒ hỏi câu mở đầu của nhóm; `[MỘT PHẦN]` ⇒ hỏi **đúng phần ghi sau `còn thiếu:`**, bằng câu hỏi khác hẳn, và mỗi nhóm chỉ được quay lại **tối đa một lần** trước khi phải đề xuất phương án xin chốt.

**Bản đồ chắt lọc lỗi thì KHÔNG còn câm.** `RequirementCoverageService` thử lại một lần rồi trả `CoverageUpdate.DistillFailed`; cờ này đi tới `BAChatTurnResult.CoverageStale` → frame `done` → dải cảnh báo trên panel "Tiến độ khai thác". Bản đồ đứng im là chuyện người dùng phải thấy: BA vừa dẫn lượt bằng bản CŨ nên có thể hỏi lại nhóm vừa được trả lời, và triệu chứng đó trông hệt "BA không nghe mình nói". Các lượt gộp CŨ cũng để lại **dấu vết chỉ-đọc** (`.batchq-history`) trong bong bóng đã hỏi chúng — `message` của lượt gộp chỉ là câu dẫn, không có dấu vết này thì lịch sử hội thoại nuốt mất chính các câu hỏi và người dùng không có gì để đối chiếu.

## Tải trọn gói để nhờ một AI khác rà soát

`GET /Requirements/DownloadReviewPackage` (`ExportReviewPackageQuery` → `ReviewPackageBuilder`) xuất **cả
chuỗi dẫn xuất** của dự án thành một file `.zip` để người dùng đem sang một công cụ AI ngoài hệ thống
(Claude Code, ChatGPT…) hỏi *"thông tin có bị rơi mất qua từng tầng không"*. Nút mang nhãn
**"Download Context"**, nằm ở đầu sidebar trang Requirements cạnh "New Chat" và **"Context"** (cửa xem lại
tài liệu nguồn đã đính kèm); là thẻ `<a download>` chứ không phải form vì đây là thao tác chỉ đọc và một cú
bấm nhầm không được phép làm mất nội dung đang gõ dở trong ô chat. Gói mang **phiên bản Product Brief đang
chọn**, nên danh sách Product Brief ngay dưới sidebar đánh dấu bản đó bằng thẻ nổi bật (`.brief-card.current`)
và nói rõ trong tooltip của thẻ, chứ không in thêm nhãn chữ: mỗi thẻ chỉ mang tên bản (`Draft` / `V1` /
`V2`…) và mốc thời gian, vì tên bản đã chở sẵn trạng thái — `draft` chỉ được đổi thành `V{n}` lúc duyệt.

**Ai được tải: quyền `RequirementsDownloadPackage`.** Action đòi quyền này *chồng lên* `RequirementsView`
của controller (AND), và nút cũng ẩn khi thiếu — cùng một quyền ở cả hai chỗ nên nút hiện ra không bao giờ
dẫn tới trang Access Denied. Đây là quyền riêng chứ không dùng lại `RequirementsView` vì gói này là đường
**đem cả chuỗi tài liệu của dự án ra ngoài hệ thống** thành một file mang đi được: cho phép ai làm việc đó
là quyết định của admin trên ma trận Roles & Permissions, không phải hệ quả của việc được xem trang này.
Mặc định seed: Admin và TeamDev có, vai trò `User` không — xem
[screens-and-permissions.md](screens-and-permissions.md#phân-quyền-chiều-dọc--role--quyền-mức-hành-động).

| File trong gói | Nội dung |
|---|---|
| `00-README.md` | Chỉ dẫn chấm (prompt `Eval/delivery-review.v1.md`) + gói thực sự có gì + khai báo phiên bản |
| `01-chat-ba.md` | Bản xuất hội thoại (kèm prompt hệ thống BA + khối bối cảnh tổ chức) — do `ChatExportBuilder` dựng, mô tả ngay bên dưới |
| `02-product-brief.md` | `ProjectDocument.Content` của Product Brief (phiên bản đang chọn trên màn hình) |
| `03-ai-design-spec.md` | `ProjectDocument.Content` của AI Design Spec |
| `04-poc-demo.html` | `04_Implementation/poc-demo.html`, đã gỡ khối chỉ dẫn cho agent (`PocTemplate.StripDeveloperGuide`) |

**Vì sao gói này tồn tại.** Từng tầng đã có cổng kiểm riêng (`PocAudit` đối chiếu POC với spec,
`ProductBriefReviewParser` soi bản mô tả), nhưng **không cổng nào đặt cả bốn tầng cạnh nhau** — mà lớp lỗi
đắt nhất của dây chuyền lại nằm đúng ở các mối nối: điều người dùng nói bị Product Brief bỏ, Brief bị
Design Spec diễn dịch lệch, Spec bị POC hiện thực thiếu. Mỗi tầng nhìn riêng đều "đạt".

Bốn quyết định thiết kế đáng biết:

- **`.zip` nhiều file chứ không phải một `.md`.** Bản demo là HTML vài trăm KB mà phần lớn là khung dùng
  chung của `poc-template.html` — nhét nguyên vào một file Markdown thì thứ AI ít cần nhất chiếm hết cửa
  sổ ngữ cảnh, và bản demo mất luôn khả năng mở bằng trình duyệt. README chỉ cho người chấm biết phần do
  agent sinh nằm giữa các mốc `POC_CONTENT` / `POC_SCRIPT`, phần còn lại đừng chấm.
- **Gói CO LẠI theo quyền người tải, không theo quyền của endpoint.** Trang Requirements cố ý không hiển
  thị AI Design Spec (thuộc Agent Dashboard) và POC (thuộc Projects), nên quyền tải gói không được biến
  thành quyền đọc cả hai: controller hỏi `IPermissionService` cho `AgentsView` /
  `ProjectsView` rồi truyền xuống dưới dạng `ReviewPackageAccess`. Phần bị bỏ ra **luôn được README nói rõ
  kèm lý do** — im lặng thì mọi phát hiện "tầng sau bỏ mất X" là kết luận về một file người chấm chưa từng thấy.
- **README cảnh báo lệch pha giữa các tầng.** Hai phép so tất định: bản mô tả và bản kỹ thuật khác phiên
  bản (Design Spec chỉ sinh lúc Approve nên nó luôn tụt lại sau bản nháp đang sửa), và bản demo sửa lần
  cuối TRƯỚC khi bản kỹ thuật hiện tại được sinh. Không có cảnh báo này, một gói xuất bản mô tả nháp cạnh
  POC dựng từ V1 sẽ sinh ra cả một danh sách "sai lệch" hoàn toàn giả.
- **Khối chỉ dẫn cho agent bị gỡ khỏi POC.** Cùng phép gỡ với đường phục vụ POC, nhưng ở đây lý do mạnh
  hơn: để nguyên là thả một tập mệnh lệnh lạ vào giữa dữ liệu mà công cụ AI kia sắp đọc.

Phần vắng mặt không làm hỏng cả gói: chưa "Write Requirement" / chưa Approve / chưa dựng POC / workspace
chưa cấu hình đều chỉ làm mất đúng file đó, các tầng còn lại vẫn đủ để soi các mối nối phía trước.

### `01-chat-ba.md` — bản xuất hội thoại

**Vì sao không chỉ chép các bong bóng chat.** Phần lớn thứ quyết định chất lượng buổi phỏng vấn KHÔNG nằm
trong text các bong bóng: câu "Đúng rồi" chỉ có nghĩa khi biết BA vừa bày ra bảng cột nào; `Message` của
lượt hỏi GỘP chỉ là câu dẫn còn các câu hỏi thật nằm ở cột `Questions`; và thứ hệ thống thật sự TIN không
phải transcript mà là **bản đồ bao phủ** — nên lỗi nặng nhất của cả tuyến (một nhóm bị chấm `[RÕ]` oan ⇒
BA vĩnh viễn không hỏi lại) chỉ lộ ra khi đặt bản đồ CẠNH transcript. File vì vậy chở bảy mục:

| Mục | Nội dung | Vì sao có mặt |
|---|---|---|
| 0 | Chỉ dẫn chấm (prompt `Eval/chat-review.v1.md`) | AI kia không biết luật của buổi phỏng vấn thì chỉ chấm được văn phong. Là file prompt ⇒ sửa được ở Prompt Studio, không cần deploy |
| 1–2 | Dự án + agent/model BA đang chạy | Model KHÔNG vision đổi hẳn cách chấm: BA "không thấy" ảnh trong tài liệu nguồn nên một câu hỏi trông như hỏi lại điều file đã nói lại là bắt buộc |
| 3 | Bản đồ bao phủ (nguyên văn, kèm `{nguồn: …}`), cổng sẵn sàng, "Điều đã chốt", điểm còn tồn đọng, phạm vi dự kiến, ví dụ đã chốt, bộ nhớ hội thoại, hồ sơ user | Đây là thứ hệ thống tin — đối chiếu với mục 5 để bắt kết luận không có căn cứ |
| 4 | Tài liệu nguồn: loại, bảng cột đã chốt, mô tả hình, trích text (cắt ở `ChatExportBuilder.SourceExcerptChars`) | Nhiều lỗi nặng nằm ở chỗ BA hỏi lại đúng thứ file đã trả lời |
| 5 | Toàn văn hội thoại, ĐÁNH SỐ LƯỢT, kèm chip + cờ chọn-một/chọn-nhiều, thẻ hỏi gộp + cờ `openEnded`, **cả sáu bảng chốt BA bày ra** (kể cả bảng phân quyền), sơ đồ luồng của hội thoại CŨ, file đính kèm | Các cột phụ chở đúng phần mà `Message` cố ý không chứa; thiếu chúng thì bản xuất trông vẫn bình thường nhưng người chấm mất chính cái để đối chiếu |
| A | Prompt hệ thống của BA (bản đang chạy, đã tính override Prompt Studio) | "BA làm vậy có sai không" không trả lời được nếu không biết BA được dặn gì |
| B | Khối bối cảnh tổ chức `OrganizationContextService.BuildCombinedContextAsync` đính vào mọi lượt gọi BA | Xem ngay bên dưới — đây là NGUỒN THỨ HAI của mọi dữ kiện trong tài liệu |

**Vì sao phụ lục B bắt buộc phải có mặt.** Người chấm được dặn rằng mọi dữ kiện phải truy ngược được về
lời người dùng hoặc tài liệu nguồn. Nhưng BA còn một nguồn thứ ba: khối ngữ cảnh này, đính vào **mọi** lời
gọi BA — cả lượt chat lẫn bước soạn Product Brief — và chứa các hằng số mà người dùng **không nhìn thấy
nên không bao giờ nói ra**: nhà máy Đồng Nai, "chỉ có kênh email", tên department/HoD thật. Thiếu nó, bản
xuất khiến người chấm kết luận NGƯỢC HẲN sự thật. Ca đã xảy ra: một AI rà soát chấm "toàn nhà máy Bosch
Đồng Nai", "email là kênh thông báo duy nhất" và tên HoD trong Product Brief là **bịa thêm mức NẶNG** —
cả ba đều đến từ khối này và đều là hành vi ĐÚNG; đi "sửa" theo báo cáo đó là biến dữ kiện đang đúng
thành sai. Vì vậy hai prompt chấm (`Eval/chat-review.v1.md`, `Eval/delivery-review.v1.md`) đều nêu phụ lục
B như nguồn hợp lệ thứ ba, kèm hướng lỗi thật sự đáng báo ở khu vực này: BA **kể lại hằng số như lời người
dùng** (chèn vào "mình ghi nhận…", vào "Điều đã chốt", hay dựng thành mâu thuẫn bắt người dùng phân xử).
Khối không dựng được thì mục vẫn in kèm câu "không dựng được khối ngữ cảnh nào" — im lặng thì người chấm
không phân biệt được "BA chạy trần" với "bản xuất quên chở phần này đi".

**Lượt BÀY BẢNG phải in cả BẢNG, không chỉ câu dẫn.** Cả sáu bảng của một lượt được in ra (🧾 cột · 🔐 phân
quyền · 🧭 luồng · 🗂 màn hình · 🧱 đối tượng · 🔔 thông báo), vì `Message` của lượt đó cố ý chỉ là một câu
mời rà bảng — không in bảng thì tin nhắn *"mình đã rà bảng…"* ở lượt ngay sau **không chấm được**: người dùng
tự chọn từng dòng, hay chỉ bấm gửi một bảng BA điền sẵn? Ca thật (dự án JD Library, lượt 68): bản xuất không
có dòng nào cho bảng thông báo, nên không cách nào phân biệt hai thứ đó — mà dòng *"To: HOD của đơn vị"* đã
vào "Điều đã chốt" như một quyết định của người dùng, và về nghiệp vụ nó còn đáng ngờ (JD chờ **HRBP** verify
mà email lại gửi cho HOD).

Ba dấu chở đúng ba trạng thái người chấm cần: **✓** = dòng BA khóa vì khai có trích dẫn — in kèm luôn chính
trích dẫn đó dưới dạng `{nguồn: …}` để soi được nó có thật trong hội thoại hay là bịa cho ô trông như đã
chốt; **✗** = dòng bị bỏ tích (im lặng bỏ nó khỏi bản xuất là xoá đúng bằng chứng cho thấy người dùng vừa
loại một thứ); *(người dùng tự thêm)* = dòng chưa từng có trong đề xuất của BA. Riêng bảng thông báo còn in
`To: *chưa chọn*` — ở lượt BÀY thì ô To trống là trạng thái **thật** và là thứ đáng soi nhất, nó nói rằng BA
không có trích dẫn nào để điền nên người dùng phải tự chọn (đường GỬI mới là chỗ không cho lưu ô trống, xem
[bất biến của bảng thông báo](#bảng-thông-báo-bảng-cuối-cùng)). Bảng luồng và bảng màn hình không có dấu ✓ vì
chúng không có ô khóa được — xem
[Vì sao bảng luồng và bảng màn hình không có dấu ✓ bằng chứng](#vì-sao-bảng-luồng-và-bảng-màn-hình-không-có-dấu--bằng-chứng).

Ba chi tiết đi kèm:

- **Chỉ hội thoại đang dùng.** Các lượt đã bị "New Chat" lưu trữ không vào file (BA cũng không còn dùng
  chúng làm ngữ cảnh) nhưng **số lượng thì phải nêu** — đếm chúng cần `IgnoreQueryFilters` vì
  `AgentConversation` có global filter `ArchivedAt == null`; quên là bản xuất im lặng khẳng định hội thoại
  này là tất cả những gì đã diễn ra, và người chấm đổ lỗi nhầm cho BA vì "không hỏi từ đầu".
- **Hàng rào ``` được nới theo nội dung.** Bản đồ bao phủ và text bóc từ tài liệu hoàn toàn có thể chứa
  ```` ``` ````; hàng rào ba dấu sẽ đóng sớm và phần còn lại tràn ra ngoài đúng ở chỗ cần đọc kỹ nhất.
- **Tên file bỏ dấu tiếng Việt** (kể cả `đ`, thứ mà `NormalizationForm.FormD` không tách) — chuỗi này đi
  qua header `Content-Disposition` và làm tên file trên đĩa. Dùng chung `ExportFileName` với gói `.zip`.

## Từ hội thoại ra tài liệu: Write Requirement → Approve

**"Write Requirement"** chỉ sinh **Product Brief** (ngôn ngữ đời thường, dạng draft — user sửa đi sửa lại không đốt token bản kỹ thuật). Chạy dưới dạng workflow run một-bước loại `RequirementAnalysis` với tiến độ live (xem [delivery-pipeline.md](delivery-pipeline.md#tiến-độ-realtime)).

**"Approve"** (`ApproveRequirementUseCase`): promote Product Brief lên `V{n}`, rồi khởi động run nền **AiDesignSpec** (một bước, BA sinh bản kỹ thuật từ Product Brief đã duyệt — chạy nền để màn hình không treo chờ LLM).

### File .docx sinh ra: Markdown thành tài liệu Word thật

LLM trả nội dung ở dạng **Markdown**, còn thứ người dùng tải về và **gửi cấp trên duyệt** là file `.docx`.
`RequirementDocumentGenerator` không đổ thẳng từng dòng vào từng paragraph nữa mà đi qua
`Templates/MarkdownDocxWriter` — áp cho cả ba tài liệu sinh từ Markdown: **Product Brief**, **AI Design
Spec** và **User Stories** (BRD/SRS/FSD đi đường khác: điền vào template `.docx` sẵn có bằng
`DocxTemplateWriter`).

Bản đổ thô để lại nguyên `#`, `**`, `` ` ``, `|` trên mặt giấy, mọi dòng cùng một cỡ chữ, không mục lục,
không số trang — file đúng nội dung nhưng không ai đem đi họp được. `MarkdownDocxWriter` dịch sang cấu
trúc Word thật:

| Trong Markdown | Trong .docx |
|---|---|
| `#` … `######` | style `Heading1`–`Heading4` (Word tự dựng mục lục & khung điều hướng; `DocxTemplateWriter.ExtractHtml` render đúng cấp cho khung xem trước) |
| `-` / `*` / `1.` (kể cả thụt lề nhiều bậc) | numbering thật 3 bậc; **mỗi danh sách đánh số một instance riêng** nên danh sách sau không đếm tiếp danh sách trước |
| `**đậm**`, `*nghiêng*`, `` `mã` ``, `~~gạch~~`, `[chữ](url)` | định dạng run + hyperlink thật |
| bảng `\| … \|` (kể cả `:---:` canh lề) | bảng Word: dòng đầu là dòng tiêu đề lặp lại khi tràn trang, các dòng chẵn tô nền |
| ` ``` ` , `>` , `---` | khối mã có nền, khối trích dẫn có vạch lề, đường kẻ ngang |

Thêm vào phần khung, không lấy từ nội dung: **trang bìa** (tên dự án, loại tài liệu, phiên bản — `draft`
hiện là *"Bản nháp (chưa duyệt)"*, ngày lập, người soạn), **mục lục**, **header** và **footer có số
trang**. Trang bìa đứng riêng (`titlePg`) nên không đeo header/footer.

Hai chi tiết dễ làm sai nếu sửa lớp này:

- **Dòng `#` mở đầu đi lên trang bìa** làm phụ đề, không lặp lại ở thân bài — và các mục còn lại được
  **nâng một bậc** để mục cấp cao nhất thành `Heading1`. Prompt Product Brief đặt tên sản phẩm ở `#` và
  các mục ở `##`; giữ nguyên bậc thì cả tài liệu không có `Heading1` nào, mục lục và khung điều hướng của
  Word thụt vào một cấp vô cớ. Tên sản phẩm trùng tên dự án ⇒ bìa chỉ in một lần.
- **Mục lục là field `TOC` thật** (Word cập nhật số trang khi mở nhờ `updateFields`) nhưng kết quả field
  được điền sẵn danh sách heading, để công cụ không cập nhật field (Google Docs, LibreOffice, khung xem
  trước) không hiển thị một trang trắng. Dưới 3 heading thì bỏ hẳn mục lục.

File sai lược đồ OOXML thì Word **từ chối mở** chứ không báo lỗi lúc sinh, nên `MarkdownDocxWriterTests`
chạy `OpenXmlValidator` trên một tài liệu có đủ heading/danh sách/bảng/mã/trích dẫn/liên kết. Thứ tự phần
tử con trong OOXML là **bắt buộc** (vd `w:tblBorders` phải là top → left → bottom → right): đây là lỗi
validator bắt được mà mắt thường không.

### Chỉ mục của chính hội thoại đi kèm lượt soạn/soát/sửa Brief

Cả ba lượt LLM của bước này (`ProductBriefDraftService`) nhận thêm khối **"Trạng thái đã chắt từ hội
thoại"**: `Project.DecisionLog`, `Project.WorkedExamples`, `Project.OpenQuestions` —
`RequirementPromptBuilder.DistilledStateSection`. Không phải nguồn thông tin mới (mọi dòng đều đã có
trong transcript), mà là thứ biến *"đừng bỏ sót yêu cầu nào"* từ một lời dặn thành **phép đối chiếu
đếm được**: mỗi dòng phải tìm được chỗ tương ứng trong tài liệu.

Lý do nó cần tồn tại: transcript thô là chỗ yêu cầu đi lạc. Một quyết định chốt ở giữa buổi rồi không ai
nhắc lại tới cuối vẫn là yêu cầu, nhưng trong 70 lượt chat nó chìm — và vòng tự soát cũng không cứu được
vì reviewer đọc đúng transcript ấy, tức hai lượt LLM cùng bỏ sót một chỗ. Ca thật: người dùng chốt nhân
viên **được hủy đăng ký**, Brief bỏ hẳn tính năng đó nhưng vẫn giữ hai quy tắc dựa vào nó.

`OpenQuestions` đi kèm còn vì một lý do khác: cổng readiness **không** xét danh sách tồn đọng (nó suy tất
định từ bản đồ bao phủ — xem [Hai cổng chất lượng phía yêu cầu](#hai-cổng-chất-lượng-phía-yêu-cầu-đủ-và-không-mâu-thuẫn)).
Đưa nó vào đây để van `needsClarification` của bước soạn có cơ sở dừng lại, thay vì tự chọn một cách hiểu
cho điểm còn treo rồi viết ra như điều đã chốt. Dự án chưa chắt được gì ⇒ khối vắng mặt hoàn toàn, prompt
trở về đúng hình dạng cũ (`BriefTraceabilityRuleTests`).

## Cổng xác nhận giả định (giữa spec và POC)

**Cổng xác nhận giả định** (giữa spec và POC): spec được phép tự quyết những điều Product Brief không nói (mục `## 12. Assumptions`). Nếu có giả định nào, worker **KHÔNG** khởi động Delivery Pipeline mà đánh dấu `Project.PendingAssumptionsVersion` — trang Requirements đổi panel giả định thành cổng có nút bấm:

- **"Tất cả đúng — dựng bản demo"** → `ConfirmSpecAssumptionsUseCase`: gỡ cổng rồi `StartDeliveryWorkflowAsync` (đúng lời gọi worker vẫn tự chạy trước đây).
- **"Sửa các điểm đã đánh dấu"** → `ReviseSpecAssumptionsUseCase`: ghi đính chính vào **cả** hội thoại BA (nguồn sự thật cho bản đồ bao phủ/decision log) **lẫn** `Project.SpecAssumptionCorrections` (đường tất định nạp vào prompt sinh spec — spec sinh từ Brief chứ không đọc transcript), rồi sinh LẠI spec; cổng dựng lại ở lượt sinh mới.

**Cổng chỉ hỏi phần MỚI.** Sinh lại spec là sinh lại cả mục `## 12. Assumptions`, và LLM thường chép lại gần như nguyên văn các giả định cũ — nên bản đầu của cổng bắt người dùng duyệt lại đúng những điểm họ vừa bấm "Đúng" ở vòng trước, sửa một điểm là bị hỏi lại cả sáu. Vế đối xứng của cột đính chính vá chỗ này: cả hai nhánh đều ghi phần user để nguyên "Đúng" vào `Project.ConfirmedAssumptions`, và worker chỉ dựng cổng cho những giả định **chưa** có trong trí nhớ đó (`AssumptionMemory.SelectUnconfirmed`) — lượt sinh lại không đẻ ra giả định mới nào thì **tự xác nhận**, đi thẳng sang dựng POC. So khớp bằng khoá chuẩn hoá (bỏ hoa/thường, gộp khoảng trắng, bỏ dấu câu cuối) chứ không nguyên văn: một dấu chấm LLM thêm vào không được phép biến câu đã duyệt thành câu hỏi mới; ngược lại câu bị viết lại thật sự thì cố ý **không** khớp — đó là giả định mới, phải hỏi. Phần đã duyệt vẫn hiện trong cổng nhưng gấp lại (`<details>`), bấm "Chưa đúng" ở đó thì nó rời trí nhớ và được hỏi lại như thường — đổi ý được, chỉ là không bị bắt trả lời lại.

Lý do đặt cổng ở đây chứ không sau POC: một giả định sai chỉ lộ ra khi xem POC là đã tốn trọn lượt dựng đắt nhất tuyến (5–15 phút), trong khi rà vài dòng chữ mất vài giây. Spec không có giả định nào ⇒ chạy thẳng sang [Delivery Pipeline](delivery-pipeline.md) như trước.

**Cổng đọc mục 12 theo kiểu "lỏng vào, chặt ra".** Cổng bật/tắt theo đúng số bullet `SpecAssumptionsParser` bóc được, nên mọi kiểu trình bày parser không nhận đều biến thành "spec không có giả định nào": cổng **tắt im lặng** trong khi các giả định vẫn nằm trong spec và vẫn lái POC. Nhìn từ phía người dùng, cùng một buổi phỏng vấn mà đổi model BA thì model này "ra POC ngay" còn model kia "cứ hỏi giả định" — khác biệt nằm ở chỗ mục 12 được viết ra sao, không phải ở chỗ model nào tự quyết ít hơn. Vì hỏng theo chiều đó đắt hơn hẳn hỏi thừa một dòng, parser nhận cả các kiểu lệch thật đã gặp: heading ở **bất kỳ cấp nào** (`### 12. Assumptions`), heading in đậm mang số mục (`**12. Assumptions**`), tiểu mục bên trong mục (`### 12.1. …` **không** đóng mục — chỉ heading cùng cấp hoặc nông hơn mới đóng), và **danh sách đánh số** (`1.`, `1)`, `(1)`) ngang hàng với gạch đầu dòng. Vế chặt giữ nguyên: một dòng in đậm **không** mang số mục (`**Giả định chung: …**` nằm giữa mục Business Rules) không được coi là heading, nếu không mọi bullet phía sau bị kéo vào danh sách giả định.

**Cổng chỉ HỎI nhóm nghiệp vụ.** Mục 12 của spec gắn nhãn cho từng bullet — `[NGHIỆP VỤ]` (bạn tự quyết một điều về cách người dùng làm việc: ai được làm gì, cái gì bắt buộc, trạng thái đi tiếp về đâu) và `[MÔ PHỎNG]` (bạn tự quyết cách bản demo dàn dựng hạ tầng thật: đăng nhập, đồng bộ hệ thống ngoài, gửi email, định dạng file xuất). Cổng dựng câu hỏi Đúng/Chưa đúng **chỉ cho nhóm nghiệp vụ**; nhóm mô phỏng hiện trong một khối gấp lại "bản demo sẽ giả lập — không cần trả lời". Lý do không hỏi: đó đúng là những thứ [`requirement-chat.v4.md`](../Prompts/BusinessAnalyst/requirement-chat.v4.md) **cấm BA hỏi** người dùng nghiệp vụ suốt buổi phỏng vấn (SSO, cách nối hệ thống, cấu hình email) — bắt họ phán xét "POC mô phỏng SSO bằng user mẫu" là hỏi một câu họ không có thẩm quyền trả lời, và nó làm loãng đúng mấy điểm nghiệp vụ cần đọc kỹ. Vẫn phải hiện chứ không được giấu: không có khối đó thì người xem demo tưởng POC đã nối SSO/COMPAS thật. Nhãn thiếu hoặc lạ ⇒ tính là nghiệp vụ (`SpecAssumptionsParser`): hỏi thừa một dòng mất vài giây, xếp nhầm một quyết định nghiệp vụ vào nhóm "chỉ để biết" là tự quyết thay người dùng đúng thứ cổng sinh ra để chặn. Nhãn chỉ bị CẮT khỏi câu khi nhận ra được là nhãn phân loại — một giả định mở đầu bằng ngoặc vuông của chính nội dung (`[Xuất báo cáo] dùng định dạng CSV`) mà bị cắt thì hiện lên cụt nghĩa. Nhãn KHÔNG đi vào `Project.ConfirmedAssumptions` — trí nhớ giả định khớp theo chính câu chữ, đổi hình dạng chuỗi là mọi điểm đã duyệt thành "mới" và bị hỏi lại một lượt.

**Điểm bị bác trở thành câu hỏi của buổi phỏng vấn SAU.** Người dùng bấm "Chưa đúng" nghĩa là: Product Brief không nói gì về điểm đó (nên spec phải tự quyết) và cách tự quyết đó sai — tức đúng một câu hỏi BA lẽ ra phải hỏi, kèm sẵn cách hiểu đúng do chính họ gõ. `ReviseSpecAssumptionsUseCase` xếp khối đính chính vào `Project.PendingAssumptionGaps`; ở lượt sinh lại spec ngay sau đó, `AgentTaskWorker` gọi `SpecAssumptionMemoryService` khái quát hoá nó thành bài học rồi ghi vào bucket miền của dự án trong `AgentChecklistItem` (nguồn `SpecAssumption`) và dọn hàng đợi. Đây là **đường harvest sắc nhất** trong ba đường: hội thoại phải suy ra "chỗ nào user tự nêu mà BA chưa hỏi", ghi chú POC chỉ có sau khi đã tốn một lượt dựng demo, còn ở đây thì chỗ hỏng được chỉ thẳng, trước cả khi POC tồn tại. Hàng đợi là cột RIÊNG chứ không đọc lại `SpecAssumptionCorrections`: cột đính chính tích lũy và bị cắt vòng nên không có cách nào biết phần nào đã học. Fail-open: harvest lỗi ⇒ giữ nguyên hàng đợi, lượt sinh lại sau gộp bù; bản sao dự án không chép hàng đợi (bài học thuộc về dự án gốc).

**Khung dự phòng cũng phải qua cổng.** Lượt sinh spec trả JSON không đọc được sẽ rơi vào khung dự phòng của `RequirementResponseParser.ParseAiDesignSpec` — một bản spec không ai viết: Project Goal là nguyên văn Brief, các mục còn lại là "Cần làm rõ". Khung đó mang sẵn một bullet ở mục `## 12. Assumptions` nói đúng tình trạng ("bản thiết kế chi tiết chưa lập được từ bản mô tả sản phẩm…") để cổng bật lên. Không có bullet đó thì khung này đi thẳng vào lượt dựng POC mà không cổng nào hé một chữ: `SpecBriefParityChecker` fail-open với chính nó (không bóc ra màn hình/rule/AC nào để so), nên cổng là chốt duy nhất còn lại. Bấm "Chưa đúng" ở bullet đó chính là đường sinh lại spec.

---

## Các cơ chế trí nhớ

### Bộ nhớ hội thoại BA (summarization memory — hai tầng nhớ)
Hội thoại BA (`ChatWithBAUseCase` → `BAChatService.ChatAsync`) dùng **hai tầng nhớ** để giữ ngữ
cảnh khi chat dài mà prompt không phình token, do `ConversationMemoryService` lo:

- **Ngắn hạn (working memory):** `RecentWindowSize` (=20) lượt gần nhất luôn gửi **nguyên văn** cho model.
- **Dài hạn:** các lượt **cũ** rơi ra ngoài cửa sổ được **gộp dần** thành một đoạn tóm tắt (text) lưu bền
  trên `Project.ConversationSummary`; `Project.SummarizedTurnCount` là con trỏ "đã gộp tới lượt nào". Đoạn
  tóm tắt được đính vào prompt như một `System` message nền (prompt `BusinessAnalyst/conversation-summary.v1.md`).

Việc tóm tắt **gom theo lô**: chỉ gọi LLM khi đã có ít nhất `SummarizeBatchThreshold` (=10) lượt cũ chưa
gộp — nên KHÔNG tóm tắt trên mỗi lượt chat (đây mới là chỗ tiết kiệm token). Trong lúc chờ đủ lô, các lượt
đó vẫn gửi nguyên văn nên **không mất ngữ cảnh**; cửa sổ verbatim chỉ phình tạm tới `20 + (10-1)` rồi co lại
sau mỗi lần gộp. **Fail-open:** lời gọi tóm tắt lỗi ⇒ giữ nguyên summary cũ, KHÔNG dời con trỏ (các lượt
chưa gộp vẫn được gửi nguyên văn) — không bao giờ fail trắng, không mất lượt nào.

**Gộp lũy tiến ⇒ thứ đã viết ra ở lại MÃI trừ khi lượt chắt lọc chủ động gỡ nó**, và luật đó áp cho cả ba
tầng cùng hình dạng: bộ nhớ hội thoại, "Điều đã chốt" (`decision-log.v1.md`), ví dụ vàng
(`interview-outlook.v1.md`). Người dùng đổi ý bằng cách nói một câu MỚI, không bằng cách chỉ vào dòng cũ —
nên cả ba prompt đều phải **thu hồi vế đã bị bác**, không để nó nằm cạnh bản mới cho bước sau tự chọn. Ca
thật: BA dựng ví dụ *"23 người, sĩ số 8–12 ⇒ mở 2 lớp, phân bổ 12 và 11 người"*, người dùng gật bằng một
chip 4 token ở lượt 15; tới lượt 35 họ nói *"1 lớp có bao nhiêu học viên thì không cần quan tâm, nhân viên
tự đăng ký"* — vế phân bổ vừa bị bác, nhưng bộ nhớ vẫn chở nguyên nó cạnh một dòng mới nói ngược lại, và
ví dụ vàng là **oracle chấm POC** nên bản demo bị chấm theo đúng cái sai đó. Gốc của nó nằm ở lượt 14 và
được chặn ở đó: `requirement-chat.v4.md` nay bắt **mỗi ví dụ tính thử chốt ĐÚNG MỘT quy tắc** — một cú bấm
"Đúng rồi" là **một** chữ ký, nên hai quy tắc trong một ví dụ là xin chữ ký cho cả hai bằng bằng chứng của
một. Phép thử: *bỏ đi một nửa ví dụ thì nửa còn lại có còn hỏi trọn vẹn một điều không?* Cùng họ, nhật ký
"Điều đã chốt" bị cấm để một câu đáp **bao trùm** cho câu hỏi gộp nhiều đối tượng ghi đè lên một dòng đã
chốt trước đó (ca thật: lượt 19 chốt *Assistant* chấm điểm, lượt 25 một tiếng *"admin sẽ quản lý"* trả lời
cho câu hỏi gộp bốn danh mục — trong đó BA nhét sẵn *"kết quả học tập"* — và nhật ký nhận về cả hai dòng).

### Bộ nhớ cấp người dùng (personalization — "càng nói càng hiểu user")
Song song với bộ nhớ theo dự án ở 5.11, `UserMemoryService` lo một tầng nhớ **gắn theo NGƯỜI DÙNG** chứ
không theo dự án — đây là thứ tạo cảm giác giống Claude/ChatGPT: trò chuyện càng nhiều, BA càng hiểu user.

- **Lưu ở đâu:** một hồ sơ ngắn gọn các sự thật **bền** về user (vai trò, lĩnh vực, tổ chức, văn phong/định
  dạng ưa dùng, thuật ngữ hay dùng…) lưu trên `AppUser.UserMemory`, dùng lại **xuyên suốt mọi dự án** của họ.
  Hồ sơ được quy về **người tạo dự án** (`Project.CreatedByUsername`); dự án không có chủ thì bỏ qua.
- **Chắt lọc khi nào:** `BAChatService.ChatAsync` gọi `UserMemoryService.UpdateAndLoadAsync` mỗi lượt;
  việc gọi LLM chắt lọc **gom theo lô** — chỉ chạy khi đã có ≥ `HarvestBatchThreshold` (=10) lượt mới chưa
  chắt lọc (con trỏ riêng `Project.UserMemoryHarvestedTurnCount`, tách khỏi `SummarizedTurnCount` vì hai bộ
  nhớ tiến theo nhịp khác nhau). Prompt: `BusinessAnalyst/user-memory.v1.md`.
- **Nạp lại:** hồ sơ user (nếu có) được đính vào prompt BA như một `System` message nền — nên BA "đã biết
  user là ai" ngay từ lượt đầu, kể cả ở dự án mới.
- **Fail-open:** lời gọi chắt lọc lỗi ⇒ giữ hồ sơ cũ, KHÔNG dời con trỏ; lần sau gặp ngưỡng sẽ thử lại.

### Bối cảnh tổ chức Bosch (OrgUnits/Associates → prompt BA + tài liệu + Usage)
Hai bảng **`OrgUnits`/`Associates`** (đồng bộ từ HR_Portal, seed một lần khi trống — xem `DbInitializer`)
được khai thác qua **`OrganizationContextService`** (Services/Requirements):

- **`BuildBaContextAsync`** render một "bức tranh tổ chức" gọn (~3–4KB): danh sách department + HoD
  (tra `TrgtManagerLId` → `Associates.PersonalNumber`), số orgUnit trực thuộc + headcount **roll-up cả cây
  con** (đi theo `TargetResponsible`, chống chu trình), chức danh phổ biến và quy mô. Phần chữ tĩnh nằm ở
  template `Prompts/BusinessAnalyst/organization-context.v2.md` (thay thế bản điền tay v1; comment HTML đầu file bị cắt
  trước khi render); dữ liệu chỉ ở dạng GỘP — **không đưa PII của Associates** (ngày sinh/điện thoại/email)
  vào prompt, tên người thật chỉ xuất hiện ở vai trò HoD/manager. Bản render **cache trong IMemoryCache 1h**.
- **`BuildScopeNote`** đính khối **ranh giới phạm vi** (template tĩnh `BusinessAnalyst/organization-scope.v1.md`)
  vào ĐẦU khối ngữ cảnh: sản phẩm chỉ phục vụ **nhà máy Bosch Đồng Nai**, nên BA bị cấm đưa phương án vượt
  khỏi nhà máy ("Toàn Bosch Việt Nam", "toàn tập đoàn"…) và được cho sẵn thang phạm vi hợp lệ (một orgUnit →
  một department → vài department → toàn nhà máy) để gợi ý bằng tên đơn vị CÓ THẬT. Khối này là sự thật
  nghiệp vụ của sản phẩm chứ không suy ra từ dữ liệu HR ⇒ đính **kể cả khi `OrgUnits` còn trống**, và tách
  khỏi `organization-context.v2.md` để vẫn còn hiệu lực khi khối ngữ cảnh render bị override ở Prompt Studio.
  Vì khối này là **hằng số của sản phẩm chứ không phải lời người dùng**, prompt chốt thêm hai chiều dùng
  sai: người dùng nói *"toàn công ty"/"tất cả nhân viên Bosch"* thì hiểu ngầm là toàn nhà máy (ghi nhận rồi
  đi tiếp, KHÔNG hỏi xác nhận điều đã chốt), và BA không được chèn *"Đồng Nai"* vào câu *"mình ghi nhận…"*
  rồi lượt sau đem chính câu đó ra chất vấn như một mâu thuẫn — xem `BAChatScopeConflictRuleTests`.
- **`BuildPlatformNote`** đính khối **nền tảng đã chốt** (template tĩnh `BusinessAnalyst/organization-platform.v1.md`)
  ngay sau khối ranh giới phạm vi: nhà máy chỉ có **DUY NHẤT một kênh thông báo là email**, nên nhóm "Thông
  báo / nhắc nhở" chỉ còn hỏi *ai nhận* + *khi nào*, còn "muốn báo qua kênh nào" là hỏi đúng điều ĐÃ CHỐT và
  mọi gợi ý Teams/SMS/Zalo/push đều là phương án không có thật — người dùng bấm nhầm một chip là yêu cầu ghi
  sai kênh từ lượt đầu rồi chảy thẳng vào tài liệu và bản thiết kế. Cùng hạng "hằng số của sản phẩm" với khối
  phạm vi (⇒ đính kể cả khi `OrgUnits` trống, và cũng KHÔNG được chèn "qua email" vào câu "mình ghi nhận…"),
  nhưng tách file vì hai khối được sửa vì hai lý do khác nhau. Chốt bằng `BAChatNotificationChannelRuleTests`.
  Ràng buộc này KHÔNG tới được TechLead/Developer qua đây (pipeline chỉ đọc tài liệu), nên bốn prompt
  `architecture-design[-bosch]` / `implementation[-bosch]` mang bản nhắc lại của riêng chúng.
  Cùng file còn chở ràng buộc thứ hai cùng hạng: **mọi ứng dụng đăng nhập bằng SSO qua IdentityServer** với
  tài khoản Bosch sẵn có, nên BA không được hỏi cách đăng nhập và không được gợi ý tài khoản nội bộ / đăng ký
  mới / Google / tài khoản dùng chung. Khối này hỏng khác khối kênh thông báo ở một điểm: prompt chat TỪNG
  nêu *"Người dùng cần đăng nhập riêng cho mỗi người không?"* làm **ví dụ mẫu của câu hỏi đúng tầm nghiệp
  vụ**, tức BA được chính prompt mời đi hỏi — mà một tiếng *"cả tổ dùng chung một tài khoản"* thì không hiện
  thực được và vẫn chảy thẳng vào tài liệu. Chốt được cách đăng nhập KHÔNG đóng luôn nhóm này: **ai được vào
  ứng dụng** (nhất là nhân viên **external** — người của công ty khác được Bosch thuê) và **vai trò được gán
  từ đâu** (suy từ dữ liệu HR hay admin gán tay) vẫn là câu hỏi nghiệp vụ phải hỏi. Lưu ý đúng chỗ hay bị
  hiểu ngược: SSO phủ **cả internal lẫn external** — external có tài khoản Bosch và đăng nhập y hệt, nên BA
  không được dựng họ thành ngoại lệ của đăng nhập; thứ họ thiếu là **bản ghi trong dữ liệu HR**, nên phạm vi
  người dùng và nguồn vai trò của riêng nhóm đó phải hỏi. Chốt bằng `BAChatLoginRuleTests`.
  Ràng buộc thứ ba cùng file, cùng hạng: **danh sách orgUnit và danh sách nhân sự của MỌI ứng dụng trong nhà
  máy đồng bộ tự động từ hệ thống COMPAS** — ứng dụng tự lấy về, không ai upload, không ai nhập tay, không
  ứng dụng nào được sửa. Khối này hỏng khác hai khối trên ở chỗ nguồn của lỗi là một luật ĐÚNG: mục "NGUỒN
  của dữ liệu" bắt BA hỏi *dữ liệu từ đâu ra* và *ai quản lý từng danh mục* để POC thôi dựng màn hình CRUD
  cho dữ liệu do nơi khác đổ sang — áp lên hai danh mục đã có nguồn cố định thì nó gây ra đúng cái nó sinh
  ra để chặn. Ca thật, ba lượt liền: *"Ai quản lý và cập nhật danh sách orgUnit trong ứng dụng?"*, *"Ai quản
  lý và cập nhật thông tin nhân viên được dùng để gán JD?"*, *"Danh sách OrgUnit để Manager chọn khi tạo JD
  được đưa vào ứng dụng bằng cách nào?"* — người dùng phải tự gõ vào ô "Ý khác" rằng app tự đồng bộ từ
  COMPAS, còn ai bấm chip cho xong thì tài liệu ghi một quy trình nhập tay không có thật và POC dựng màn
  hình *"Quản lý OrgUnit"* đầy nút Thêm/Sửa/Xóa. Vì vậy ngoại lệ sống ở **hai chỗ**: khối hằng số (cấm hỏi,
  cấm gợi ý, cấm dựng màn hình quản lý), và ngay TRONG mục "NGUỒN của dữ liệu" của `requirement-chat.v4.md`
  — model đọc tới đó rồi thì không quay ngược lên khối ngữ cảnh nữa. Phía coverage phải chừa hai danh mục
  này khỏi cả dòng *Dữ liệu / danh mục chính* lẫn chuẩn cắt ngang "danh mục dùng để KIỂM TRA dữ liệu phải có
  người quản lý": BA bị CẤM hỏi những câu đó, nên một dòng bị hạ vì chúng sẽ kẹt `[MỘT PHẦN]` vĩnh viễn.
  Chốt được nguồn KHÔNG đóng luôn nhóm dữ liệu: thứ ứng dụng **tự gắn thêm** lên một orgUnit/một con người
  (JD do ai soạn và ai duyệt, ai được gán vào lớp nào) vẫn là danh mục bình thường, và nhân viên **external**
  không có trong COMPAS. Chốt bằng `BAChatOrgDirectoryRuleTests`.
- **`BuildProjectUnitNoteAsync`** dựng ghi chú "đơn vị yêu cầu" từ **`Project.OrgUnitCode`** (chọn tùy chọn
  ở modal New Project; `CreateProjectUseCase` chỉ lưu mã có thật trong OrgUnits): orgUnit + manager +
  department cha + HoD.
- Nơi tiêu thụ: `BAChatService.ChatAsync` (system message nền — BA hiểu tên phòng/vai trò, gợi ý
  bằng tên phòng thật, hỏi luồng duyệt đúng ngôn ngữ manager/HoD, biết external KHÔNG nằm trong dữ liệu HR),
  và các lời gọi soạn/soát/sửa Product Brief + Technical Docs (`RequirementPromptBuilder` — tài liệu dùng
  đúng tên phòng ban/HoD thật thay vì "TBD"; khối context đưa cả vào vòng tự soát để reviewer không coi tên
  thật là "tự thêm"). Trang **Usage** thêm bảng "Usage by department" (roll-up orgUnit của project về
  department gần nhất). **Fail-open toàn tuyến**: bảng trống/lỗi ⇒ mọi luồng chạy như trước.
- **`ExportChatTranscriptQuery` cũng là nơi tiêu thụ** — vì đúng lý do vừa nêu ở gạch đầu dòng trên, chỉ
  đổi người soát: khối context vào **phụ lục B** của `01-chat-ba.md` để AI rà soát NGOÀI hệ thống cũng
  không coi phạm vi nhà máy / kênh email / tên HoD thật là "tự thêm". Vòng tự soát nội bộ đã được vá theo
  hướng này từ trước; đường xuất ra ngoài thì chưa, và đó là lỗ hổng đã sinh ra một báo cáo chấm ba dữ kiện
  ĐÚNG thành "bịa thêm mức NẶNG". Xem [phần bản xuất hội thoại](#01-chat-bamd--bản-xuất-hội-thoại).

---

### Hai cổng chất lượng phía yêu cầu: ĐỦ và KHÔNG MÂU THUẪN
`RequirementReadinessGate` (đã có) chỉ trả lời *đã rõ hết chưa*. `RequirementConflictService` trả lời
*những điều đã rõ có chọi nhau không* — chạy khi bấm nút tạo tài liệu, trước khi tài liệu được
soạn. Người dùng nói ở lượt 3 rằng quản lý duyệt xong là hết, lượt 12 lại kể thêm HR duyệt: bản đồ
bao phủ đánh dấu [RÕ] cả hai lần, còn bước soạn tài liệu (bị cấm tự giả định) sẽ chọn bừa một bên.
Lựa chọn của người dùng được ghi vào **chính hội thoại** nên mọi thứ đọc transcript đều thấy, không
cần biết cổng này tồn tại. Fail-open toàn phần (`Project.PendingConflicts` + con trỏ
`ConflictCheckedTurnCount` để không gọi lại LLM khi hội thoại chưa đổi). Panel `#conflictPanel` nằm
**ngay dưới cái nút đã bật nó lên** (trong `#writeReqZone`), không ở sidebar: nó là câu hỏi phát sinh
từ cú bấm đó, hiện ở nửa màn hình bên kia thì người dùng chỉ thấy nút mình vừa bấm không phản ứng gì.

Cùng tinh thần "người dùng phải kiểm chứng được": bản đồ bao phủ nay mang **bằng chứng**
(`{nguồn: …}` cuối mỗi dòng, `CoverageMapParser.SplitEvidence`), hiện trong **tooltip** của dòng chứ
không phải một hàng riêng dưới nhãn — ở bề rộng sidebar trích dẫn luôn bị cắt giữa chừng và hay lặp
cùng một câu ở nhiều nhóm, làm panel cao gấp đôi mà vẫn không soát được gì. Trích dẫn chỉ được lấy từ
**lời người dùng hoặc tài liệu nguồn**: một câu của hệ thống đem làm bằng chứng (câu dẫn của các bảng
chốt, bối cảnh tổ chức, chính câu "mình ghi nhận…" của BA) khiến dòng đó trông như đã kiểm chứng trong
khi người dùng đọc lại thấy một "lời mình" mình không nhớ đã nói.

**Chốt xong mâu thuẫn thì vòng soạn tài liệu phải chạy TIẾP, không được quay về khung chat.** Cú bấm "Chốt
lại rồi tạo tài liệu" ghi một cặp lượt (user chốt + BA xác nhận) rồi submit ngay form "Write Requirement" —
và chính cặp lượt đó từng làm gãy đường đi. Bước soạn tài liệu có một đường tắt: lượt cuối hội thoại là lời
mời đã được cổng duyệt ⇒ không xét lại (xem [Sinh draft requirement](#sinh-draft-requirement)). Bản trước
nhận diện đường tắt đó bằng cách **dò cụm "Write Requirement" trong lượt cuối**, nên cặp lượt "chốt lại" —
thứ KHÔNG mang thông tin mới, chỉ chọn giữa hai điều đã nói — vô tình xoá mất tín hiệu: vòng soạn xét lại
cổng trên một bản đồ vừa distill lại chính câu *"Mình chốt lại các điểm còn mâu thuẫn như sau…"*, mà
`requirement-coverage.v3.md` § "Người dùng đính chính một nhóm" đọc câu đó **đúng như một lời đính chính** và
hạ nhóm vừa được chốt xuống `[MỘT PHẦN]`. Kết quả: `NeedsMoreInfo`, BA hỏi lại trong chat, không tài liệu
nào được sinh — và vì không đường nào tự khởi động lại vòng soạn (nút "Write Requirement" là caller DUY NHẤT
của `GenerateRequirementDraftUseCase` ở màn hình này), người dùng phải bấm nút lần thứ hai.

Đường tắt nay khoá bằng **cờ `AgentConversation.ReadinessVerified`** — dấu do chính cổng đóng lên lượt nó vừa
cho qua, không phải thứ suy lại từ chữ. `ApplyResolutionsAsync` **chép** cờ của lượt nó vừa đè lên sang lượt
BA đóng cổng; không có cờ để chép (người dùng vào nút bằng đường lùi "đã có draft + bản đồ đủ") thì không tự
dựng, và cổng được xét lại như trước. Fail-closed giữ nguyên: mọi đường ghi khác mặc định `false`, nên một
lượt chat mới / một file vừa đính kèm / một lượt ⚠️ lỗi LLM đều tự động đóng đường tắt lại.

**Lượt chặn của cổng là một câu MỞ.** Khi chưa đủ, `Evaluate` trả về `Message` + `OpenEnded = true`, và
cờ đó đi tiếp ra `BAChatTurnResult.OpenEnded` để khung chat đổi placeholder thành lời mời kể. Cổng
không dựng chip: chip phải là đáp án TRỌN VẸN cho đúng câu đang hỏi, thứ chỉ BA viết ra được. Không có
cờ này, lượt gate lên màn hình vừa không có nút bấm vừa không mời gõ — đúng thứ `requirement-chat.v4.md`
gọi là "một lượt hỏi thiếu chỗ trả lời".

**Câu chặn KHÔNG nói nhóm.** Lượt chặn chỉ chở **câu hỏi**, không nhãn nhóm và không đếm số nhóm còn lại.
Bản trước mở đầu bằng *"Trước khi viết tài liệu, mình còn một chỗ chưa đủ thông tin để khỏi phải tự đoán
(nhóm «Đối tượng người dùng & vai trò», còn 3 nhóm — mình hỏi từng nhóm một)"* rồi mới tới câu hỏi thật: cả
cụm đó là **sổ sách của hệ thống** đọc ra màn hình. Nhãn là từ vựng của bản đồ mà người dùng nghiệp vụ chưa
từng thấy (cùng lý do `CoveragePendingGuard.StripGroupTag` gỡ thẻ nhóm trước khi vào ngữ cảnh chat), còn
*"còn 3 nhóm"* chỉ báo cho họ biết còn phải chịu bao nhiêu lượt nữa — không giúp trả lời câu đang hỏi, mà
làm lượt đó đọc như một bản tin tiến độ. Câu dẫn duy nhất còn lại là *"Mình quay lại chỗ này một chút."* ở
nhánh quay lại (bên dưới). Cùng luật áp cho hai chỗ khác: `requirement-chat.v4.md` cấm BA đọc nhãn nhóm hay
đếm nhóm trong `message`, và **thẻ hỏi gộp thôi in nhãn nhóm lên đầu mỗi câu** — trường `group` vẫn được
lưu và vẫn vào transcript vì nó là thứ nối câu hỏi về đúng dòng bản đồ, chỉ là không nói ra với người dùng.
Ai muốn xem nhãn thì panel "Tiến độ khai thác" bên cạnh vẫn liệt kê đủ 12 dòng.

**Bốn nhánh dựng câu chặn, và không nhánh nào được rỗng nghĩa.** Vế câu hỏi thử bốn nhánh theo lượng thông
tin bản đồ cho, hẹp dần — và vì không còn câu dẫn nào đỡ, mỗi nhánh phải tự đứng một mình được:

1. **Có mẩu `còn thiếu: …`** ⇒ hỏi thẳng nó — thứ duy nhất bước soạn tài liệu còn phải tự đoán.
2. **`[MỘT PHẦN]` mà distiller không viết được mẩu nào** ⇒ **phát lại** phần đã ghi nhận (mọi thứ trước cụm
   `còn thiếu:`, đã lược sạch ghi chú máy) rồi hỏi còn chỗ nào chưa đúng. KHÔNG được rơi xuống nhánh 3 ở ca
   này: `requirement-chat.v4.md` cấm tuyệt đối việc phát lại **câu mở đầu** cho một nhóm `[MỘT PHẦN]` —
   người dùng đã kể phần đó rồi, nghe lại đúng câu cũ là mất lòng tin vào cả buổi phỏng vấn.
3. **`[CHƯA HỎI]`** (và `[MỘT PHẦN]` rỗng ruột) ⇒ **câu mở đầu THẬT của nhóm** — `CoverageGroupOpeners`,
   một câu cho mỗi nhóm, bằng ngôn ngữ công việc của người dùng.
4. **Nhãn không khớp nhóm nào** (distiller tự nghĩ ra một tên) ⇒ không bịa một câu hỏi khai thác về thứ
   không có trong checklist, nhưng cũng không trỏ tới *"phần này"* suông: nhãn được đọc vào câu như một cụm
   **chủ đề** bình thường (*"Về tích hợp hệ thống ngoài, hiện trong công việc thực tế của anh/chị đang diễn
   ra thế nào?"*) — ngôn ngữ tự nhiên, khác hẳn cái ngoặc sổ sách `(nhóm «…»)`.

Nhánh 3 là chỗ đã trả giá. Trước đây nó phát **một câu duy nhất cho cả 12 nhóm** — *"Anh/chị kể giúp mình
phần này trong công việc thực tế hiện đang diễn ra thế nào?"* — không nói được đang hỏi cái gì và trỏ tới
*"phần này"*, đúng cụm **tham chiếu suông** mà prompt cấm BA dùng vì người dùng chỉ thấy ô chat cuối trên
màn hình. Ca thật (dự án JD Library, lượt 76): người dùng vừa trả lời xong người nhận của một sự kiện thông
báo, BA mời bấm "Write Requirement" quá sớm, cổng thay lời mời bằng câu đó, và người dùng đáp *"mình chưa
hiểu câu hỏi, hãy hỏi rõ hơn"* — mất trắng một vòng ở cuối một buổi phỏng vấn đã 78 lượt. Nhánh này
**reachable với bất kỳ nhóm nào**: cụm `còn thiếu:` là định dạng do LLM xuất, không phải bất biến của code,
nên chỉ cần lượt distill quên viết nó đúng một lần.

Hai nhóm chốt bằng **BẢNG** (`Thông báo / nhắc nhở`, `Phân quyền theo nghiệp vụ`) có câu mở đầu đọc khác
hẳn: chúng chỉ **mời người dùng nhắn một tiếng** để phần đó được đưa ra rà, chứ không khai thác bằng câu
hỏi — phát cho chúng một câu hỏi là cổng tự phá đúng chốt chặn mà hai cái bảng sinh ra để dựng (xem
[Bảng phân quyền](#bảng-phân-quyền-chốt-nhóm-phân-quyền-ở-cuối-buổi)). Câu của chúng cũng cố tình **không
hứa hẹn một cái bảng**: dự án không có vòng đời trạng thái nào thì bảng thông báo không bao giờ được bày và
nhóm quay về đường hỏi bằng câu hỏi, mà cổng chỉ có bản đồ bao phủ trong tay nên nó không phân biệt được hai
ca đó.

`CoverageGroupOpenersTests` chốt bảng câu mở đầu khớp **danh sách nhóm của prompt thật**: thêm một nhóm vào
`requirement-coverage.v3.md` mà quên viết câu cho nó thì fail ở test, chứ không âm thầm rơi về nhánh 4 trên
màn hình người dùng.

**Cổng giữ SỔ RIÊNG "đã hỏi câu nào"** (`RequirementReadinessGate.LastAskedAt`), vì phanh chống hỏi lại
dùng chung **không thấy** câu của nó: `AskedQuestionHistory.Collect` chỉ nhận một lượt assistant là câu hỏi
khi lượt đó có **gợi ý**, mà lượt chặn cố tình không có chip nào. Nới luật của `Collect` thì mọi lượt tóm
tắt/thông báo cũng thành "câu hỏi" và chặn oan các lượt xác nhận về sau — nên cổng dò **chính câu hỏi nó sắp
phát** trong các lượt BA đã lưu (so chuẩn hóa hoa/thường + khoảng trắng). Khóa là câu hỏi chứ không phải nhãn
nhóm vì hai lẽ: nhãn không còn nằm trong lượt đã lưu để mà đọc lại, và so bằng câu hỏi đúng hơn ở đúng chỗ
phải đúng — bản đồ nhúc nhích thì mẩu `còn thiếu:` đổi, câu hỏi đổi theo, mà một câu hỏi KHÁC thì đáng hỏi
ngay chứ không phải đợi hết một vòng.

Sổ đó lái việc **chọn chỗ hỏi**: câu cổng chưa hỏi đi trước, rồi tới câu bị hỏi lâu nhất; trong cùng một bậc
thì ★ cốt lõi trước. Cờ "đã hỏi" **thắng cả cờ ★** — bản đồ không nhúc nhích thì mọi lượt chặn tiếp theo chọn
lại đúng dòng cốt lõi đó và phát lại nguyên văn một câu người dùng vừa không trả lời được (ca thật: ba lượt
liên tiếp giống hệt nhau, người dùng đáp *"mình không hiểu câu hỏi của bạn"* hai lần rồi tự dán lại câu trả
lời họ đã gõ từ 60 lượt trước). Đổi chỗ hỏi thì lượt sau còn cơ hội gỡ, mà chỗ cũ không mất đi đâu: nó quay
lại ngay khi các chỗ khác đã được hỏi một vòng — và khi quay lại, lượt đó **nói ra** rằng cổng đang quay lại
(*"Mình quay lại chỗ này một chút."*) nên hai lượt không bao giờ giống hệt nhau, kể cả khi chỉ còn đúng một
dòng thiếu để hỏi. Câu dẫn ấy đứng ở **đầu**, vế hỏi phía sau giữ nguyên — đó là điều kiện để sổ đọc ra được
cả hai biến thể của lượt chặn.

Câu chặn phát ra ở **bốn đường**, và cả bốn đều phải chở hội thoại vào cổng: lượt BA mời bấm nút quá sớm bị
thay (`BAChatService`), lượt mà **mọi câu hỏi của BA đều là câu đã hỏi** (`BuildFollowUpAfterRepeat` — đường
dễ lặp nhất, vì lượt nào cũng rơi vào đó khi bản đồ đứng yên), **lượt câm** (ngay dưới), và cú bấm
"Write Requirement" thật (`ProductBriefDraftService`). `ChatExportBuilder` cũng truyền hội thoại, nếu không bản
xuất in ra một câu chặn khác với câu người dùng sẽ thấy.

**Lượt câm — lượt BA không hỏi gì cả.** Hai phanh trên chỉ soi các lượt CÓ hỏi: `AskedQuestionHistory` so nội
dung *câu hỏi* với các câu đã hỏi, còn cổng readiness chỉ vào cuộc khi lượt đó *nhắc tới* nút. Một lượt chỉ gồm
câu ghi nhận rồi dừng lại lọt qua cả hai — không có câu hỏi để so, không có lời mời để chặn. Ca thật (JD Libary
5, các lượt 82/84/90): một dòng bản đồ kẹt `[MỘT PHẦN]` dù người dùng đã trả lời đúng mẩu `còn thiếu:` của nó,
nên BA hết đường hợp lệ — prompt cấm hỏi lại điều vừa được trả lời, và cấm nhắc tới nút khi bản đồ chưa đủ — rồi
viết *"mình tiếp tục bước rà soát cuối"*, một bước không tồn tại ở chế độ chat. Người dùng đáp *"ok"*, *"tiếp
tục đi"*, nhận lại đúng một lượt như thế, và buổi phỏng vấn 90 lượt kết thúc ở một lượt không ai trả lời được.
Đây là ca bản đồ **không tự lành**: nó chỉ nhúc nhích khi có thông tin mới, mà lượt câm thì không hỏi được gì để
lấy thông tin mới — nên chốt chặn phải nằm ở lượt chat chứ không ở lượt distill sau đó.

`BAChatService` xét **hình dạng của lượt đã chốt**, sau mọi nhánh khác (kể cả các cổng bảng): không chip, không
`openEnded`, không thẻ hỏi, không bảng, không dấu hỏi, không nhắc tới nút ⇒ thay bằng `BuildFollowUpAfterRepeat`.
Dấu hỏi là ranh giới, cùng phép thử mà `BAChatReplyParser.LooksOpenEnded` dùng: một lượt CÓ hỏi mà quên chip vẫn
trả lời được bằng ô nhập (luôn mở), và thay nó đi là cướp mất câu hỏi thật của BA — thường là loại đắt nhất, câu
xin lời kể — để phát một câu khô cứng hơn. Lượt có bảng cũng không câm: bảng chính là chỗ trả lời duy nhất của
lượt, và câu dẫn của nó cố tình không phải câu hỏi. `BAChatSilentTurnTests` chốt cả hai chiều.

Chốt chặn này chỉ chữa **triệu chứng**. Nguyên nhân nằm ở hai lượt chắt lọc, và mỗi cái có một luật riêng:
`requirement-coverage.v3.md` cấm viết mẩu `còn thiếu:` mà **không câu trả lời nào đóng lại được** — dạng loại trừ
(*"chỉ ở A hay chỉ ở B"*, trong khi *"cả hai"* là đáp án hợp lệ), hoặc một mẩu hỏi đúng thứ BA bị cấm hỏi — và
bắt distiller bỏ mẩu `còn thiếu:` mà chính phần tóm tắt của dòng đó đã trả lời; `interview-outlook.v1.md` tính
**một cái gật bằng chip** cho phương án BA vừa nêu là mục đã chốt, vì mục tồn đọng giữ lại quá hạn khoá cổng
chắc chắn như một dòng `[MỘT PHẦN]` thật (`CoveragePendingGuard` hạ dòng tương ứng ở mọi lượt).
`InterviewDeadEndRuleTests` giữ ba luật prompt đó khỏi bị dọn đi.

### Đính chính một nhóm: đường thoát khỏi một dòng [RÕ] oan

Một nhóm bị chấm `[RÕ]` oan là **điểm mù kín** của hệ thống — prompt cấm BA hỏi lại nhóm đã `[RÕ]`, nên
nhóm đó không bao giờ được nhắc tới nữa và cách hiểu sai đi thẳng vào tài liệu. Đường thoát duy nhất là
**chat**, và nó gồm ba mảnh:

1. **BA chủ động đọc lại** — nhịp tóm tắt kiểm chứng sau mỗi ~5–7 câu đã trả lời
   (`requirement-chat.v4.md`), cộng chính sáu bảng chốt. Người dùng nói "chưa đúng" bằng lời của họ, không
   phải bằng tên nhóm.
2. **Lượt chắt lọc hạ dòng bị đụng tới xuống `[MỘT PHẦN]`** kèm **đúng nguyên văn** cụm
   `còn thiếu: người dùng báo phần này chưa đúng — cần hỏi lại và chốt lại.`, giữ ghi nhận cũ trong ngoặc
   (`requirement-coverage.v3.md` § *"Người dùng đính chính một nhóm"*). Cổng "Write Requirement" đóng theo,
   vì nó suy tất định từ chính bản đồ.
3. **Phanh chống hỏi lại nhường đường** cho nhóm mang cụm đó (`AskedQuestionHistory.ReopenNote`), nếu không
   BA hỏi lại mà câu hỏi bị lọc mất vì trùng câu cũ.

Phần `còn thiếu:` của một dòng vừa đính chính vì thế có **ba mảnh, đúng thứ tự**: cụm tín hiệu (cho máy) →
**mẩu còn phải hỏi** (cho người dùng) → `(ghi nhận trước đó: …)` (cho BA). Mảnh giữa là bắt buộc và không
mảnh nào thay được nó: cổng lấy nguyên phần sau `còn thiếu:` làm câu hỏi hiển thị, nên một dòng chỉ có cụm
tín hiệu sẽ lên màn hình thành *"người dùng báo phần này chưa đúng — cần hỏi lại và chốt lại — anh/chị cho
mình xin thông tin này nhé?"* — một lượt hỏi rỗng nghĩa mà người dùng không có cách nào trả lời, và nhóm đó
đứng yên ở `[MỘT PHẦN]` mãi. `RequirementReadinessGate.ExtractMissingPart` cắt hai mảnh dành cho máy/BA ra
khỏi câu hỏi; hết mảnh giữa thì cổng đi tiếp xuống các nhánh dự phòng (phát lại phần đã ghi nhận, rồi câu
mở đầu của nhóm — xem [bốn nhánh dựng câu chặn](#hai-cổng-chất-lượng-phía-yêu-cầu-đủ-và-không-mâu-thuẫn))
thay vì đọc cụm tín hiệu lên. Phần phát lại cũng bị lược sạch hai mảnh đó, cùng một lý do: chúng là ghi chép
của hệ thống dành cho BA, đọc lên là xưng "người dùng" ở ngôi thứ ba với chính người đang đọc.

Cụm ở bước 2 là một giao ước prompt↔code mà compiler không kiểm được, nên `CoverageReopenNoteRuleTests`
giữ hai bên không trôi khỏi nhau.

**Panel "Tiến độ khai thác" KHÔNG có nút "chưa đúng?" nữa** (endpoint `ReopenCoverage` và
`CoverageMapEditor` đã gỡ). Nút cũ hạ nhóm xuống `[MỘT PHẦN]` bằng phép sửa chuỗi tất định ngay khi bấm —
nhanh hơn đường chat một lượt — nhưng nó bắt người dùng **tự quy lời phàn nàn của mình về một trong 12
nhãn**: *"Vòng đời & trạng thái"*, *"Phân quyền theo nghiệp vụ"* là từ vựng nội bộ của BA, người dùng
nghiệp vụ không đọc được chúng để biết mình đang phản đối cái gì. Bấm nhầm nhóm thì họ mở lại một nhóm
đang đúng và nhóm sai vẫn `[RÕ]` — tệ hơn không bấm. Panel nay **chỉ đọc**, và mang một dòng chỉ đường
(`.coverage-hint`) về khung chat: panel người dùng thấy sai mà không biết kêu ở đâu còn tệ hơn panel
không có nút.

---

## Sơ đồ luồng

### Requirement discovery với BA

```mermaid
sequenceDiagram
    autonumber
    participant U as User
    participant UI as Requirements UI
    participant UC as ChatWithBAUseCase
    participant BA as BAChatService
    participant LLM as LLM
    participant DB as DB

    U->>UI: nhập câu trả lời / yêu cầu
    UI->>UC: gửi message
    UC->>DB: lưu AgentConversation role=user
    UC->>BA: tạo prompt gồm transcript + memory + org context + sources
    BA->>LLM: gọi BA model
    LLM-->>BA: reply + suggestions (readiness suy tất định từ coverage map)
    BA->>DB: lưu AgentConversation role=assistant
    BA->>DB: cập nhật conversation summary/memory/coverage nếu cần
    UC-->>UI: assistant reply + next questions
```

BA không chỉ trả lời chat; service còn duy trì ngữ cảnh dài hạn:

| Context | Lưu ở đâu | Mục đích |
|---|---|---|
| Conversation transcript | `AgentConversation` | Lịch sử trao đổi chi tiết |
| Conversation summary | `Project.ConversationSummary` | Rút gọn hội thoại dài (khung chat + vòng soạn Brief) |
| Mốc duyệt Brief | `Project.BriefApprovedTurnCount` | Số lượt hội thoại tại lần Approve gần nhất — cho phép vòng soạn nén phần transcript trước mốc (phần đó đã được bản đã duyệt chở) |
| User memory | `AppUser.UserMemory` | Ghi nhớ preference/đặc thù người dùng |
| Checklist học được | `AgentChecklistItem` | Học các điểm BA thường hỏi thiếu (mỗi bài học một dòng, kèm lý do + nguồn, bật/tắt được). Ba đường vào: hội thoại, ghi chú POC, giả định bị bác |
| Requirement coverage | `Project.RequirementCoverageMap` | Theo dõi coverage requirement |
| Source files | `ProjectSourceFile` | Bối cảnh từ PDF/image user upload |

### Sinh draft requirement

```mermaid
sequenceDiagram
    autonumber
    participant U as User
    participant R as RequirementsController
    participant UC as GenerateRequirementDraftUseCase
    participant O as WorkflowOrchestrator
    participant DB as DB
    participant W as AgentTaskWorker
    participant BA as ProductBriefDraftService

    U->>R: Click Generate/Update Requirement
    R->>UC: execute(projectId)
    UC->>O: StartRequirementDraftWorkflowAsync
    O->>DB: tạo WorkflowRun Write Requirement
    O->>DB: tạo AgentTask RequirementAnalysis Queued
    W->>DB: poll task
    W->>BA: GenerateOrUpdateDraftAsync
    BA->>DB: tạo/cập nhật ProjectDocument + Revision
    W->>DB: task completed, run completed
```

Kết quả có thể là:

- Đủ thông tin: tạo/cập nhật requirement docs.
- Chưa đủ thông tin: worker trả marker `NeedsMoreInfo`, BA đặt câu hỏi tiếp trong chat. **Không có đường tự
  chạy tiếp**: trả lời xong thì vòng soạn chỉ khởi động lại khi người dùng bấm "Write Requirement" lần nữa
  — nên mọi lần bị đá về đây đều là một vòng mất trắng, và đó là lý do đường tắt bên dưới đáng giữ cho đúng.
  Hai mốc người dùng đọc ở nhánh này (mốc `completed` của worker, băng `needsMoreInfo` của panel tiến độ)
  vì vậy phải nói **cả hai việc còn phải làm**: trả lời trong khung chat, RỒI bấm lại nút. Bản trước viết
  *"Đang chờ anh/chị trả lời câu hỏi của BA trong khung chat để viết tiếp tài liệu"* — hứa một bước không
  tồn tại, đúng cái bẫy mà `requirement-chat.v4.md` cấm BA tự đào bằng những câu *"mình sẽ tổng hợp lại rồi
  quay lại"*.

#### Ngữ cảnh gửi lên model ở vòng soạn Brief

Một lượt bấm gửi transcript lên model **ba lần** (soạn → tự soát → sửa), nên đây là chỗ ngữ cảnh đắt nhất
phía yêu cầu. Prompt gồm: bối cảnh tổ chức, **bản Product Brief đã duyệt gần nhất**, **tóm tắt hội thoại
cũ**, transcript nguyên văn, trạng thái đã chắt (điều đã chốt / ví dụ đã xác nhận / điểm tồn đọng), bản
draft hiện hành và text/ảnh tài liệu nguồn.

**Transcript có trần** (`BriefContextWindow`). Trước đây nó là input DUY NHẤT không bị chặn trên — mọi khối
khác đã có trần (bản đồ bao phủ 4000 ký tự, nhật ký/tồn đọng 4000, tóm tắt 6000, text nguồn theo
`Llm:SourceUpload:MaxTextCharsPerFile`) — nên một buổi phỏng vấn dài đủ sức đẩy lượt soạn vượt context
window, và ở đó không có degrade mềm: lời gọi hỏng ⇒ task fail. Cửa sổ lấy cái cắt nhiều nhất trong ba
nguồn: trần **40 lượt** (rộng hơn cửa sổ 20 của khung chat — dẫn một câu hỏi chỉ cần vài lượt gần đây, còn
VIẾT tài liệu thì cần chi tiết), trần **40.000 ký tự** (một lượt chốt bảng dài bằng vài chục lượt hỏi đáp,
nên đếm lượt một mình không chặn được token), và **mốc duyệt Brief** (`Project.BriefApprovedTurnCount`).

**Bất biến, và là thứ dễ làm hỏng nhất nếu sửa sau này: chỉ được cắt phần đã nằm trong
`Project.ConversationSummary`** — không bao giờ cắt quá `SummarizedTurnCount`, và luôn chừa lại ít nhất một
lượt nguyên văn. Cắt xa hơn là làm thông tin bốc hơi: phần bị bỏ không có trong tóm tắt, không có trong
transcript, và vòng tự soát mất luôn thứ nó phải đối chiếu. Vì vậy vòng soạn gọi thẳng
`ConversationMemoryService` (cùng service khung chat dùng) thay vì dựng đường nén riêng: đường ghi chú trên
bản xem trước và đường POC-feedback ghi thêm lượt user rồi gọi vào đây, không qua lượt chat nào để summary
kịp tiến. Fail-open toàn tuyến: tóm tắt lỗi ⇒ con trỏ đứng yên ⇒ không cắt gì, hội thoại đi nguyên văn.

**Brief ĐÃ DUYỆT là mốc nén hợp lệ; bản draft thì không.** Sau `Approve`, chính dòng draft được đổi tên
thành `V{n}`, nên trước đây lượt soạn kế tiếp tra `"draft"` nhận về chuỗi rỗng và **transcript là thứ duy
nhất chở nội dung V1 sang V2**. Nay bản đã duyệt được nạp lại vào prompt: nó là bản duy nhất trong dự án có
chữ ký người dùng (họ đã bấm Approve), nên vừa là mỏ neo chống trôi, vừa là thứ cho phép cắt phần hội thoại
trước mốc duyệt. Vòng tự soát được nói rõ rằng nội dung truy được về bản đã duyệt / tóm tắt là **hợp lệ** —
thiếu câu đó, reviewer chê chính phần người dùng đã ký là "tự thêm ngoài hội thoại" rồi vòng sửa xoá nó đi.

**Đừng đổi thành "chỉ gửi bản draft + ghi chú".** Đề xuất này quay lại đều đặn vì nó rẻ, và nó gãy ở bốn
chỗ: Brief là bản nén MẤT MÁT nên ghi chú kiểu *"đoạn này thiếu ý X"* chỉ sửa được khi X còn ở đâu đó;
`product-brief-review.v2.md` đối chiếu bản nháp **với hội thoại** để bắt bỏ sót, mà bỏ hội thoại đi thì nó
so bản nháp với chính nó; cổng readiness và van `needsClarification` cần phân biệt "điều người dùng nói"
với "điều model tự điền", trong khi đọc từ Brief thì mọi câu đều trông như đã chốt; và ở `temperature > 0`,
patch chồng patch không còn mốc nào để tái neo (cùng lý do đã gỡ nút "🔄 Tạo lại tài liệu"). Nén hội thoại
thì được — thay hội thoại thì không.

**Ba đường được phép khởi động vòng soạn, và không đường nào là một lượt chat.** Nút "Write Requirement",
ghi chú đã ghim trên bản xem trước Brief (`ReviseBriefFromNotesUseCase`), phản hồi POC chuyển về phía yêu cầu
(`RoutePocFeedbackToRequirementUseCase`) — cả ba đều là một cú submit có chủ ý nói đúng một điều: *lấy những
gì đang có mà viết*. `RequirementDraftTriggerCoverageTests` **fail build** khi có đường thứ tư.

Cám dỗ thường trực là nối nó vào lượt chat ("người dùng vừa trả lời xong câu hỏi của cổng thì tự viết tiếp,
đỡ phải bấm"). Ba lý do không làm: một câu trong khung chat KHÔNG phải lệnh viết tài liệu (trả lời xong rồi
kể thêm ba ý nữa là ca thường — tự chạy ở câu đầu là cướp lượt và đốt token cho một bản draft thiếu đúng ba
ý đó); bản đồ bao phủ do LLM chắt nên nó nhấp nháy, một lượt distill lỡ nâng đủ dòng lên `[RÕ]` là một run
tự bay; và prompt chat đang CẤM BA hứa một bước chạy ngầm giữa hai lượt, nên nối vào chat là biến chính điều
prompt dạy BA thành lời nói dối.

Trước khi xét cổng, bước soạn kiểm tra **cờ `AgentConversation.ReadinessVerified` của lượt đang đứng cuối**:
có cờ ⇒ cổng đã pass ở đúng lượt đó và chưa có gì mới kể từ đấy, đi thẳng vào soạn tài liệu (tiết kiệm một
lượt distill). Cờ do lượt chat đóng khi cổng cho lời mời đi qua, và được đường chốt mâu thuẫn chép sang lượt
của nó — xem [Hai cổng chất lượng phía yêu cầu](#hai-cổng-chất-lượng-phía-yêu-cầu-đủ-và-không-mâu-thuẫn).

### Approve requirement và sinh AI Design Spec

```mermaid
flowchart TD
    A[User review Product Brief/Requirement] --> B{Approve?}
    B -- Không --> C[Chat tiếp / sửa draft]
    B -- Có --> D[ApproveRequirementUseCase]
    D --> E[Mark ProjectDocument approved + version]
    E --> F[StartAiDesignSpecWorkflowAsync]
    F --> G[AgentTask AiDesignSpec Queued]
    G --> H[AgentTaskWorker gọi RequirementDocsService.GenerateAiDesignSpecAsync]
    H --> I[Lưu AI Design Spec]
    I --> J[Tự khởi động Delivery Workflow]
```

Điểm quan trọng: sinh AI Design Spec chạy nền để UI không bị treo trong lúc đợi LLM.
