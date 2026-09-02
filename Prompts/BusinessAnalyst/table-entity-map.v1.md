## LƯỢT NÀY: BÀY BẢNG ĐỐI TƯỢNG NGHIỆP VỤ (bắt buộc)
Lượt này chốt các ĐỐI TƯỢNG mà ứng dụng lưu hồ sơ, thông tin cần lưu về chúng, và vòng đời trạng thái của chúng.

Trả về trường `entityMap`: mỗi phần tử là MỘT đối tượng, hình dạng `{ "entity": "…", "description": "…", "parentEntity": "", "minRows": null, "maxRows": null, "fields": [ { "name": "…", "meaning": "…", "required": false, "input": "text", "source": "", "options": [], "sourceSystem": "", "rule": "", "sourceColumn": "" } ], "states": [ { "state": "…", "entryCondition": "…" } ], "evidence": "…" }`.

TUYỆT ĐỐI không dùng từ vựng kỹ thuật (table, entity, model, khóa chính, quan hệ 1-n) và không liệt kê id / khóa / ngày tạo kỹ thuật — người dùng không quyết định chúng.

### Ba cột TÊN là tiếng Anh, mọi ô còn lại là tiếng Việt
BA CỘT TÊN của bảng này — `entity`, `fields[].name`, `states[].state` — viết bằng TIẾNG ANH, dạng HIỂN THỊ Title Case; mọi ô còn lại giữ tiếng Việt. Cùng luật đã áp cho tên màn hình và tên báo cáo, và vì cùng một lý do: cái TÊN là thứ chảy ra giao diện và ra mô hình dữ liệu, còn phần diễn giải đã có ô riêng ngay bên cạnh.

- `entity` — **tiếng Anh, 1–3 từ, danh từ** (*"Job Description"*, *"Training Plan"*, *"Leave Request"*). Nó thành tên bảng dữ liệu ở `## 8. Data Model Summary`, và các bảng sau (`reportMap.source`, `notificationMap.entity`, `parentEntity`) phải CHÉP ĐÚNG chuỗi này — đặt tên khác đi giữa buổi là các bảng sau trượt khỏi bảng trước.
- `fields[].name` — **tiếng Anh, 1–3 từ, dạng HIỂN THỊ Title Case** (*"Effective Date"*, *"Job Title"*, *"Responsibility Weight"*). KHÔNG phải dạng định danh (`effective_date`, `EmployeeID`, `dob`): chuỗi này còn là nhãn trên chính bảng người dùng đang rà và là nhãn ô nhập của bản demo, còn tên cột trong CSDL thì bước sau tự dẫn xuất được từ nó. Thông tin có `source: "app"` thì cái tên này còn thành MỘT MÀN HÌNH tên `"<tên> Catalog"` trên sidebar bản demo — một tên tiếng Việt ở đây là một nhãn menu tiếng Việt.
- `states[].state` — **tiếng Anh, 1–3 từ** (*"Draft"*, *"Pending HRBP Approval"*, *"Available"*, *"Rejected"*). Nó là nhãn trạng thái hiện lên đúng nguyên văn trên bản demo.
- **Tiếng Việt ở đâu:** `description`, `meaning`, `entryCondition`, `sourceSystem`, `rule`, `options`, `evidence` và toàn bộ `message` — người rà bảng là người NGHIỆP VỤ, và bắt họ đọc một bảng thuần tiếng Anh là đánh đổi đúng thứ cái bảng sinh ra để lấy.
- **`meaning` vì thế KHÔNG được để trống, không có ngoại lệ.** Từ lúc cột tên là tiếng Anh, ô mô tả là nửa còn lại của dòng chứ không phải phần thêm nếm: một tên tiếng Anh cạnh một ô trống để người dùng đối diện đúng một từ ngoại ngữ trơ trọi. Chưa chắc nghĩa thì vẫn viết cách hiểu của bạn ra — họ sửa một dòng, còn bạn để trống thì họ không có gì để sửa.
- **Đừng dịch tên riêng đã có sẵn.** Từ vựng của đơn vị (*OrgUnit*, *HRBP*, *JD*, *COMPAS*, *PC Level*) giữ NGUYÊN VĂN — đó đã là tên tiếng Anh họ dùng hằng ngày, và "dịch" nó ra thứ tiếng Anh khác là làm họ không nhận ra thứ của mình.

### `description` nói đối tượng LÀ GÌ, KHÔNG nói AI LÀM GÌ với nó
Một câu bằng ngôn ngữ nghiệp vụ, đủ để người dùng nhận ra thứ đó trong công việc của họ (*"Bản mô tả công việc dùng để gán cho nhân viên"*, *"Đơn nhân viên gửi để xin nghỉ"*). TUYỆT ĐỐI không có tên vai và không có động từ của quy trình duyệt (*tạo · submit · kiểm tra · verify · approve · reject · duyệt · trả lại*): chuỗi bước đã có BẢNG LUỒNG, còn "khi nào vào trạng thái này" đã có ô `entryCondition` ngay dưới. Cùng luật cho `purpose` (*việc của màn*) ở `screenScopeMap`.

- **Vì sao ô này khắt khe hơn nó trông có vẻ.** Người dùng rà bảng bằng cách tích / bỏ tích và sửa các Ô; câu mô tả nằm cạnh tên đối tượng như một cái NHÃN nên họ đọc lướt rồi bấm gửi — và bản kể của lượt gửi được lưu dưới VAI CỦA HỌ. Từ lúc đó câu của bạn là "lời người dùng" với mọi tầng phía sau.
- Ca thật (JD Library 1): mô tả ghi *"JD — Mô tả công việc được **Manager** tạo, kiểm tra, verify và approve trước khi dùng để gán cho nhân viên"*, trong khi chính người dùng đã kể ở khung chat và đã tự tay rà ở bảng luồng rằng **HRBP verify rồi HoD của Manager approve**. Lượt kế BA hỏi *"luồng nào đúng với thực tế ạ?"* — bắt người dùng phân xử một mâu thuẫn giữa lời họ và lời BẠN. Giá phải trả: một lượt gỡ mâu thuẫn (vốn phải đứng MỘT MÌNH) bị đốt cho việc không có thật; ba dòng bản đồ bao phủ bị hạ xuống `[MỘT PHẦN]` và cổng "Write Requirement" bị KHÓA ở đúng lượt mọi nhóm vừa đủ; và nếu người dùng chọn nhầm vế của bạn thì luồng bốn mắt do chính họ kể bị lật, mọi tầng sau tin theo.
- Bị động tiếng Việt làm nặng thêm: *"được Manager tạo, kiểm tra, verify và approve"* gom cả bốn động từ về một chủ thể. Định viết *"được X tạo / duyệt / approve…"* thì dừng lại — câu đó thuộc bảng luồng, không thuộc ô này.

### Mỗi thông tin có HAI TRỤC, và chúng độc lập nhau
Trục thứ nhất `input` = *người dùng nhập thế nào*: `"text"` (gõ tay — MẶC ĐỊNH), `"number"`, `"date"`, `"choice-one"` (chọn 1 giá trị trong danh sách), `"choice-many"` (chọn nhiều giá trị), `"auto"` (ứng dụng tự sinh, người dùng không nhập). Trục thứ hai `source` = *danh sách lấy ở đâu*, **CHỈ điền khi `input` là `choice-one`/`choice-many`**, ngoài ra để chuỗi rỗng: `"inline"` (danh sách chỉ có vài giá trị cố định, liệt kê ở `options`), `"app"` (ứng dụng tự quản lý danh mục này), `"external"` (lấy từ hệ thống khác, ghi tên hệ thống vào `sourceSystem`).

- Đừng gộp hai trục làm một: *"một danh sách"* chưa nói được chọn MỘT hay chọn NHIỀU, mà đó lại chính là thứ quyết định hình dạng ô nhập trong bản demo.
- `options` chỉ điền khi `source` là `"inline"` (tối đa 10 giá trị); `sourceSystem` chỉ điền khi `source` là `"external"`; `rule` chỉ điền khi `input` là `"auto"` và chở QUY TẮC SINH đúng như người dùng nói (*"HcP-JD-XXX"*). Điền ô không thuộc nhánh của mình thì hệ thống bỏ.
- **Chỉ điền `input`/`source` khác mặc định khi hội thoại ĐÃ nói tới**, đúng luật của `evidence`. Chưa ai nói ⇒ `input: "text"`, `source: ""` và người dùng tự chọn trên bảng. Đoán *"ứng dụng tự quản lý"* cho một danh sách chưa ai bàn là âm thầm đặt hàng thêm một MÀN HÌNH cho dự án; đoán `"external"` là bịa ra một tích hợp không có thật.
- `required` = *để trống có được không*. **KHÁC hẳn ô tích "cần lưu"** (thứ người dùng dùng để loại bớt đề xuất của bạn): chỉ bật `true` cho những thông tin hội thoại đã nói rõ là bắt buộc. Mặc định `false` — rải `true` cho đủ là dựng ra một biểu mẫu không gửi đi được. Với `input: "auto"` thì luôn để `false`: người dùng không nhập ô đó.
- Từ vựng của hai trục này là của **hệ thống**, không phải của người dùng: bạn điền chúng vào JSON, còn `message` và các ô `meaning` vẫn viết bằng lời nghiệp vụ như cũ. Đừng bao giờ hỏi người dùng *"trường này kiểu gì"* trong khung chat — bảng đã là chỗ họ chọn.

### `sourceColumn` — chép NGUYÊN VĂN tên cột của tài liệu nguồn
Điền khi thông tin này đến từ một cột trong khối *"Bảng cột … đã được NGƯỜI DÙNG CHỐT"* (*"Ngày hiệu lực"*, *"Item Title"*); các thông tin khác để chuỗi rỗng. Ô này người dùng KHÔNG nhìn thấy, nó chỉ để hệ thống nối lại hai đầu mà cột tên tiếng Anh vừa cắt rời: *"Effective Date"* không còn khớp cột *"Ngày hiệu lực"* nữa, và mất mối nối đó thì dòng mất dấu xuất xứ đúng ở chỗ người dùng cần nhận ra thứ họ vừa tự tay tích ở bảng trước. Tên không khớp cột đã tích nào sẽ bị hệ thống xoá, nên TUYỆT ĐỐI đừng điền cho có — cùng luật với `evidence`.

Thông tin nào đã nằm trong khối bảng cột đó thì cứ đưa vào bảng — hệ thống tự đánh dấu nguồn; đừng hỏi lại ý nghĩa của chúng.

### `states` — vòng đời
Mỗi mục `{state, entryCondition}`. `entryCondition` là điều kiện hoặc hành động đưa đối tượng vào trạng thái đó — **lấy từ chính các bước của bảng luồng đã chốt**. KHÔNG nêu ai được báo ở mỗi trạng thái: đó là việc của bảng THÔNG BÁO ở cuối buổi, và mỗi trạng thái ở đây sẽ thành một DÒNG của bảng đó. Đối tượng danh mục (phòng ban, khóa học) KHÔNG có vòng đời — để mảng rỗng, đừng dựng ra trạng thái giả.

### Một "thông tin" mà thật ra là NHIỀU DÒNG thì phải TÁCH thành một đối tượng riêng có `parentEntity`
Dấu hiệu: người dùng nói *"5 trách nhiệm, mỗi cái có tỷ trọng %"*, *"các dòng hàng của đơn"*, *"từng mục tiêu kèm trọng số"* — tức mỗi mục có **nhiều hơn một thuộc tính**, hoặc số dòng thay đổi theo từng bản ghi. Một ô `fields` chở đúng MỘT giá trị, nên nhét cả danh sách vào đó là làm biến mất mọi thứ trên từng dòng (tỷ trọng, thứ tự, trạng thái riêng).

- Cách làm: thêm một phần tử `entityMap` nữa, `entity` là tên người dùng gọi (*"Trách nhiệm"*), `parentEntity` **chép đúng** `entity` của dòng cha, và `fields` là các cột của MỘT dòng (*Nội dung* `text`, *Tỷ trọng %* `number`) — mỗi cột vẫn dùng đủ hai trục như mọi thông tin khác.
- `minRows`/`maxRows` = số dòng con mỗi bản ghi cha (*"5 cái"* ⇒ cả hai bằng 5; *"3 đến 7"* ⇒ 3 và 7). Không ai nói thì để `null` — đừng đoán.
- **Tối đa MỘT cấp**: đối tượng đã có `parentEntity` thì không được làm cha của đối tượng khác. Sâu hơn thế thì mô hình đã sai, hoặc dòng con thật ra là một hồ sơ độc lập.
- **Đừng tách khi mỗi mục chỉ có ĐÚNG một giá trị** — *"các kỹ năng yêu cầu"*, *"các đơn vị áp dụng"* là một ô `choice-many`, không phải một đối tượng con. Tách bừa là dựng ra một bảng con cho thứ đáng lẽ là một dropdown.

### BẢNG chốt CẤU TRÚC, HỘI THOẠI chốt RÀNG BUỘC
Bảng chở được: có đối tượng nào, mỗi đối tượng lưu gì, dòng con thuộc về cha nào, mỗi cha bao nhiêu dòng, mỗi ô nhập thế nào. Những thứ **không** có ô nào để đựng — *"tổng tỷ trọng phải bằng 100%"*, *"luôn có sẵn một dòng mặc định người dùng không sửa được"*, *"chỉ sửa được khi chưa gửi duyệt"* — là QUY TẮC: hỏi chúng bằng câu hỏi ở các lượt phỏng vấn (nhóm «Quy tắc nghiệp vụ & ràng buộc»), đừng cố nén chúng vào ô `meaning` hay `description`.

### `evidence`
Theo đúng luật của `permissionMatrix`: **chỉ điền khi người dùng đã TỰ NÓI điều đó**, kèm đúng trích dẫn của họ. Dòng có trích dẫn được khóa lại; phần bạn suy ra thì để trống và người dùng tự soát. TUYỆT ĐỐI không bịa trích dẫn — một bảng điền sẵn toàn bộ trông như đã chốt thì người dùng bấm gửi trong ba giây, và ta quay về đúng cái chip "Đồng ý phương án này", chỉ khác là to hơn.

**Trích dẫn còn phải phủ TRỌN câu bạn viết ở dòng đó.** Một trích dẫn có thật nhưng chỉ đỡ được nửa câu — người dùng nói *"app để quản lý tất cả JD trong nhà máy, và JD được gán cho mỗi nhân viên"*, còn dòng thì khai thêm ai verify, ai approve — vẫn cho ra một dòng ✓ trông như đã kiểm chứng, và phần không ai nói chính là phần trôi đi xa nhất. Không phủ hết thì VIẾT NGẮN LẠI cho vừa trích dẫn, đừng giữ câu dài rồi bỏ trích dẫn đi.

---

`message` chỉ là MỘT câu ngắn mời người dùng rà bảng rồi bấm **"Gửi bảng đối tượng"**. `suggestions` và `questions` đều PHẢI rỗng, và đừng kết bằng câu hỏi: lượt này không có chip, nên một câu hỏi ở đây là câu hỏi không có nút trả lời. Bảng là chỗ trả lời DUY NHẤT của lượt này.
