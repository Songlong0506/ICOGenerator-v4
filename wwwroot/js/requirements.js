const chatForm = document.getElementById("chatForm");
const messageInput = document.getElementById("messageInput");
const chatMessages = document.getElementById("chatMessages");
const thinkingBox = document.getElementById("thinkingBox");
const suggestionList = document.getElementById("suggestionList");

// escapeHtml dùng chung ở site.js (nạp qua _Layout trước file này).

if (chatForm && messageInput && chatMessages && thinkingBox) {
    const maxInputHeight = 180;

    function resizeMessageInput() {
        messageInput.style.height = "auto";

        const nextHeight = Math.min(messageInput.scrollHeight, maxInputHeight);
        messageInput.style.height = `${nextHeight}px`;
        messageInput.classList.toggle("is-scrollable", messageInput.scrollHeight > maxInputHeight);
    }

    resizeMessageInput();

    messageInput.addEventListener("input", resizeMessageInput);

    // ==== Ô tự nhập TỰ CAO theo nội dung ====
    // Ô này (cả trên thẻ hỏi gộp lẫn hàng chip lượt-đơn) nằm trong khung nhãn-nổi gọn, khởi điểm chỉ cao
    // chừng một dòng. Nhưng câu trả lời thật ở đây thường dài hơn thế — "tầm 1500 người, và tần suất sử
    // dụng không cố định" — và ô cố định bắt người dùng cuộn để đọc lại chính thứ mình sắp gửi, đúng lúc họ
    // cần thấy nó trọn vẹn nhất. Bản phục hồi nháp cũng đổ thẳng vào đây, nên ô có thể mang sẵn một câu dài
    // ngay khi người dùng chưa gõ chữ nào trong phiên này.
    // Trần chiều cao là bắt buộc: không có nó, một câu trả lời dài đẩy nút "Gửi N câu trả lời" ra khỏi màn
    // hình và thẻ hỏi gộp thành cụt đường.
    const OTHER_BOX_MAX_HEIGHT = 200;

    function autoGrowOtherBox(box) {
        // Ô nằm trong khối chưa hiện có scrollHeight = 0 → đo lúc này sẽ ghim height 0px thành một ô dẹt.
        if (!box || box.hidden) return;

        box.style.height = "auto";
        const next = Math.min(box.scrollHeight, OTHER_BOX_MAX_HEIGHT);
        box.style.height = `${next}px`;
        box.style.overflowY = box.scrollHeight > OTHER_BOX_MAX_HEIGHT ? "auto" : "hidden";
    }

    messageInput.addEventListener("keydown", function (e) {
        if (e.key === "Enter" && !e.shiftKey && !e.isComposing) {
            e.preventDefault();
            chatForm.requestSubmit();
        }
    });

    // ==== Chat BA dạng streaming ====
    // Submit được chặn lại và gửi qua POST /Requirements/ChatStream (Server-Sent Events): trạng thái
    // ("BA đang soạn…") cập nhật dòng thinking, token "đang gõ" đổ dần vào một bubble BA, frame done
    // mang bản chốt (reply + suggestions + cờ mời Write Requirement) để render tại chỗ — KHÔNG reload.
    // Đây là đường ghi DUY NHẤT của khung chat — stream hỏng kiểu gì cũng reload, không gửi lại (gửi
    // lại sẽ nhân đôi lượt vì server vẫn chạy trọn lượt đã nhận).
    const STREAM_URL = "/Requirements/ChatStream";
    // Ngưỡng coi stream là ĐÃ CHẾT: server gửi frame "ping" mỗi 10s trong suốt lượt (xem
    // HeartbeatInterval ở RequirementsController), nên im lặng quá lâu nghĩa là kết nối đứt chứ không
    // phải BA đang nghĩ lâu. Bắt buộc phải có: khi mạng rớt giữa chừng, fetch/ReadableStream có thể KHÔNG
    // bao giờ reject — promise treo vĩnh viễn, chatBusy kẹt ở true và màn hình đứng mãi ở "BA đang soạn
    // câu trả lời…", gửi tin mới cũng không được.
    const STREAM_IDLE_TIMEOUT_MS = 45000;
    let chatBusy = false;
    let liveBubble = null;

    function appendUserBubble(text) {
        thinkingBox.insertAdjacentHTML("beforebegin", `
            <div class="req-msg you">
                <p>${escapeHtml(text)}</p>
            </div>
        `);
    }

    // Bong bóng "lạc quan" cho lượt gửi ĐÍNH KÈM: hiện ngay ảnh (từ objectURL đang xem trước) / chip tên
    // file + ghi chú như một lượt user thật, để user thấy tin đã gửi trong lúc BA đọc — thay vì chỉ có
    // spinner rồi reload. Markup khớp bản server render (.req-msg you > .chat-attachments) để nhìn giống
    // hệt sau khi reload. Trả về phần tử vừa chèn để có thể gỡ đi (hoàn tác) nếu upload thất bại.
    function appendUserImageBubble(note, files) {
        const thumbs = files.map(f => f.url
            ? `
            <span class="chat-attachment-img" title="${escapeHtml(f.file.name || "ảnh")}">
                <img src="${f.url}" alt="${escapeHtml(f.file.name || "ảnh đính kèm")}" />
            </span>
        `
            : `
            <span class="chat-attachment-file" title="${escapeHtml(f.file.name || "tệp")}">📄 ${escapeHtml(f.file.name || "tệp")}</span>
        `).join("");
        const noteHtml = note ? `<p>${escapeHtml(note)}</p>` : "";
        thinkingBox.insertAdjacentHTML("beforebegin", `
            <div class="req-msg you">
                <div class="chat-attachments">${thumbs}</div>
                ${noteHtml}
            </div>
        `);
        return thinkingBox.previousElementSibling;
    }

    function ensureLiveBubble() {
        if (liveBubble) return liveBubble;

        // BA đã có nội dung để "gõ" vào bubble → ẩn ngay dòng thinking ("BA đang soạn…"): nếu không,
        // bubble đang stream và khung thinking hiển thị CÙNG LÚC thành 2 khu vực BA trùng nhau.
        thinkingBox.style.display = "none";

        // Nhãn "BA" đứng NGOÀI bong bóng (kiểu Teams) → chèn nhãn rồi tới bong bóng; previousElementSibling
        // của thinkingBox vẫn là bong bóng (phần tử chèn sau cùng), nên liveBubble trỏ đúng.
        thinkingBox.insertAdjacentHTML("beforebegin", `
            <b class="req-who">BA</b>
            <div class="req-msg ba streaming">
                <p style="white-space: pre-wrap;"></p>
            </div>
        `);
        liveBubble = thinkingBox.previousElementSibling;
        return liveBubble;
    }

    function setThinkingText(text) {
        const el = document.getElementById("thinkingText");
        if (el) el.textContent = text;
    }

    function scrollToBottom() {
        chatMessages.scrollTop = chatMessages.scrollHeight;
    }

    // Ẩn HẲN danh sách gợi ý: không chỉ display:none mà còn XÓA các chip. Bắt buộc phải xóa vì CSS gộp
    // câu hỏi + chips (.req-msg.ba:has(+ .suggestion-list .suggestion-option)) chỉ dựa vào SỰ TỒN TẠI của
    // .suggestion-option trong DOM — nếu chỉ display:none mà giữ chip, bong bóng BA phía trên vẫn bị bỏ
    // margin đáy nên tin nhắn tiếp theo (lượt user vừa gửi) DÍNH sát vào nó trong lúc BA đang "suy nghĩ".
    function hideSuggestions() {
        if (!suggestionList) return;
        suggestionList.style.display = "none";
        suggestionList.innerHTML = "";
        suggestionList.dataset.multi = "false";
    }

    // Render lại các chip gợi ý cho lượt BA mới nhất (markup khớp bản server render trong Index.cshtml);
    // dời #suggestionList xuống dưới bubble mới nhất vì các lượt streaming được chèn vào sau nó trong DOM.
    // multiSelect = true: chip chuyển sang chế độ TOGGLE (chọn nhiều) + nút "Gửi các lựa chọn" — dùng cho
    // câu hỏi kiểu "gồm những vai trò nào?" mà một đáp án là không đủ.
    function renderSuggestions(suggestions, multiSelect) {
        if (!suggestionList) return;

        if (!Array.isArray(suggestions) || suggestions.length === 0) {
            hideSuggestions();
            return;
        }

        suggestionList.dataset.multi = multiSelect ? "true" : "false";
        // Checkbox chỉ hiển thị ở chế độ chọn nhiều (CSS ẩn nó khi data-multi != "true"),
        // nên vẫn render span trong mọi trường hợp để markup JS/server đồng nhất.
        const ariaSelected = multiSelect ? ` aria-selected="false"` : "";
        suggestionList.innerHTML = suggestions.map((s, i) => `
            <button type="button" class="suggestion-option" role="option"${ariaSelected} data-suggestion="${escapeHtml(s)}">
                <span class="suggestion-option-check" aria-hidden="true"></span>
                <span class="suggestion-option-text">${escapeHtml(s)}</span>
                <span class="suggestion-option-key">${i + 1}</span>
            </button>
        `).join("");
        ensureOtherControls();
        ensureMultiControls();
        updateOtherSendState();
        thinkingBox.before(suggestionList);
        suggestionList.style.display = "";
    }

    function isMultiSelect() {
        return suggestionList && suggestionList.dataset.multi === "true";
    }

    // Lượt hỏi MỘT câu MỞ (BA xin một lời kể): không có chip nào, nên ô nhập là chỗ trả lời DUY NHẤT —
    // đổi placeholder thành lời mời kể để khoảng trắng dưới câu hỏi đọc như "tới lượt anh/chị nói", chứ
    // không như một lượt BA quên đưa gợi ý. Chỉ là một dòng nhắc và KHÔNG được lưu (xem
    // BAChatTurnResult.OpenEnded): tải lại trang thì placeholder về mặc định, câu hỏi vẫn còn nguyên
    // trong hội thoại và vẫn không có chip nào để bấm nhầm.
    const COMPOSER_PLACEHOLDER_DEFAULT = messageInput ? messageInput.placeholder : "";
    function setComposerOpenEnded(openEnded) {
        if (!messageInput) return;
        messageInput.placeholder = openEnded
            ? "Anh/chị kể tự do giúp mình, càng chi tiết càng tốt…"
            : COMPOSER_PLACEHOLDER_DEFAULT;
    }

    // Chế độ chọn nhiều: thêm nút gửi vào cuối danh sách chip (chỉ khi data-multi="true").
    // Checkbox ở đầu mỗi option (xem renderSuggestions + CSS) đã báo rõ đây là chọn nhiều,
    // nên không cần dòng chữ hint nữa.
    function ensureMultiControls() {
        if (!suggestionList) return;

        const existing = suggestionList.querySelector(".suggestion-multi-send");
        if (!isMultiSelect()) {
            if (existing) existing.remove();
            return;
        }
        if (existing) return;

        suggestionList.insertAdjacentHTML("beforeend", `
            <div class="suggestion-multi-send">
                <button type="button" class="btn primary small" id="suggestionMultiSendBtn" disabled>Gửi các lựa chọn</button>
            </div>
        `);
    }

    function selectedSuggestionValues() {
        return Array.from(suggestionList.querySelectorAll(".suggestion-option.selected"))
            .map(o => (o.dataset.suggestion || "").trim())
            .filter(Boolean);
    }

    function updateMultiSendState() {
        const btn = document.getElementById("suggestionMultiSendBtn");
        if (btn) btn.disabled = selectedSuggestionValues().length === 0 && otherAnswerText().length === 0;
    }

    // ==== Ô "Ý khác" của hàng chip lượt-đơn ====
    // Ở lượt hỏi MỘT câu, bấm chip là GỬI NGAY (selectSuggestion) — không có bước xác nhận, không có chỗ
    // viết thêm. Với chip BẤT ĐỒNG ("Không, tính khác", "Tôi muốn khác", "Tôi muốn sửa lại" — prompt
    // requirement-chat.v4.md kê sẵn cả ba, ở đúng các lượt đắt nhất: chốt ví dụ số, chốt kịch bản luồng,
    // nhịp tóm tắt kiểm chứng) thì cú bấm đó gửi đi một lượt user RỖNG NỘI DUNG: phủ định mà không kèm cái
    // đúng. Người dùng phải chờ hết một vòng LLM chỉ để được hỏi "vậy anh/chị tính thế nào?", trong khi câu
    // trả lời đã có sẵn trong đầu họ đúng giây bấm "Không". Nhóm bị đụng tới cũng rớt khỏi [RÕ] mà không có
    // thông tin nào thay thế, nên BA phải quay lại — tiêu mất lượt quay lại DUY NHẤT mà prompt cho phép mỗi
    // nhóm.
    //
    // Thẻ hỏi GỘP đã có đúng lối thoát này từ trước (ô .batchq-answer dưới mỗi hàng gợi ý); phần dưới đây
    // mang nó sang hàng chip lượt-đơn. Không có endpoint mới: thứ gửi đi vẫn là một tin nhắn user bình
    // thường.
    const OTHER_PLACEHOLDER = "Anh/chị nói rõ giúp mình — càng cụ thể càng tốt…";

    // Dựng khối "Ý khác" bằng JS cho CẢ HAI đường render (server lúc tải trang, JS ở frame done) thay vì
    // nhân đôi markup sang Index.cshtml như thẻ gộp phải làm: khối này không mang dữ liệu của lượt nào nên
    // không có gì để server render. Chỉ gắn khi hàng chip THỰC SỰ có chip — lượt câu MỞ không có chip, và ở
    // đó ô nhập của khung chat đã là chỗ trả lời duy nhất (setComposerOpenEnded), thêm một ô thứ hai là tạo
    // hai chỗ trả lời cho cùng một câu.
    //
    // Ô MỞ SẴN, không còn nút "✎ Ý khác" phải bấm mới ra. Nút là một bước thừa đứng đúng chỗ đắt nhất: nó
    // KHÔNG nói được gì mà cái ô mở sẵn không tự nói (một ô nhập kèm nhãn "Ý khác" đã là lời mời rõ ràng),
    // nhưng nó bắt người dùng phải NGHĨ RA rằng còn lối thoát ở đó rồi mới bấm — và người dùng nghiệp vụ
    // đang rà một hàng đáp án thì đọc lướt chứ không đi tìm nút. Bỏ nút đi thì lối thoát luôn hiện diện
    // bằng đúng thứ nó là: chỗ để gõ.
    function ensureOtherControls() {
        if (!suggestionList) return;
        if (!suggestionList.querySelector(".suggestion-option")) return;
        if (suggestionList.querySelector(".suggestion-other")) return;

        // Nhãn nổi khoét trên viền ô là thứ nói cho người dùng biết ô này là gì (trước đây việc đó do viên
        // nút gánh). Nó là nhãn NHÌN nên `aria-hidden`; chỗ trình đọc màn hình lấy tên ô là `aria-label`.
        //
        // Chế độ chọn NHIỀU đã có nút "Gửi các lựa chọn" ở cuối danh sách và text tự nhập được gộp vào đó
        // như một lựa chọn nữa — thêm nút gửi thứ hai là hai nút cùng một việc, cách nhau hai dòng.
        suggestionList.insertAdjacentHTML("beforeend", `
            <div class="suggestion-other">
                <div class="suggestion-other-field">
                    <textarea class="suggestion-other-input" rows="1" aria-label="Ý khác — câu trả lời anh/chị tự nhập" placeholder="${OTHER_PLACEHOLDER}"></textarea>
                    <span class="suggestion-other-cap" aria-hidden="true">Ý khác</span>
                </div>
                <div class="suggestion-other-bar"${isMultiSelect() ? " hidden" : ""}>
                    <button type="button" class="btn primary small suggestion-other-send" disabled>Gửi câu trả lời</button>
                    <span class="suggestion-other-hint"></span>
                </div>
            </div>
        `);
    }

    function otherInput() {
        return suggestionList ? suggestionList.querySelector(".suggestion-other-input") : null;
    }

    function otherAnswerText() {
        const box = otherInput();
        return box ? (box.value || "").trim() : "";
    }

    // Mồi ô tự nhập cho một chip BẤT ĐỒNG vừa bấm: chip đó thành LƯỚI AN TOÀN — để trống ô rồi bấm gửi thì
    // tin nhắn đi ra đúng bằng chip như hôm nay. Ô KHÔNG được là bắt buộc: bắt gõ mới đi tiếp được sẽ đẩy
    // một phần người dùng sang bấm "Đúng rồi" cho xong, tức đổi một lượt cụt lấy một xác nhận GIẢ, thứ đắt
    // hơn nhiều vì mọi tầng sau tin là thật.
    function primeOtherInput(chipText) {
        const box = otherInput();
        if (!box) return;

        const wrap = box.closest(".suggestion-other");
        const hint = wrap.querySelector(".suggestion-other-hint");

        box.dataset.fallback = chipText || "";
        hint.textContent = chipText ? `Để trống rồi gửi = gửi nguyên “${chipText}”.` : "";

        box.focus();
        box.setSelectionRange(box.value.length, box.value.length);
        autoGrowOtherBox(box);
        updateOtherSendState();
        updateMultiSendState();
        scrollToBottom();
    }

    function updateOtherSendState() {
        const box = otherInput();
        if (!box) return;
        const btn = box.closest(".suggestion-other").querySelector(".suggestion-other-send");
        btn.disabled = chatBusy || (otherAnswerText().length === 0 && !(box.dataset.fallback || ""));
    }

    // Tin nhắn gửi đi = chip đã bấm + lời người dùng viết thêm. Giữ lại chip vì nó là VẾ PHỦ ĐỊNH: bỏ đi
    // thì "làm tròn xuống" đứng trơ trọi, và các tầng chắt lọc phía sau không còn biết nó đang bác lại cách
    // tính nào. Người dùng không gõ gì ⇒ đúng hành vi hôm nay, không hơn không kém.
    function otherAnswerMessage() {
        const box = otherInput();
        if (!box) return "";
        const typed = otherAnswerText();
        const fallback = box.dataset.fallback || "";
        if (!typed) return fallback;
        return fallback ? `${fallback} — ${typed}` : typed;
    }

    function sendOtherAnswer() {
        if (chatBusy) return;
        const text = otherAnswerMessage();
        if (!text) return;

        messageInput.value = text;
        chatForm.requestSubmit();
    }

    // ==== Nhận diện chip BẤT ĐỒNG ====
    // Thuần giao diện: nó chỉ quyết định cú bấm MỒI Ô NHẬP hay GỬI NGAY, không đụng gì tới nội dung được
    // lưu — nên nó ở đây (một chỗ, dùng chung cho cả hai đường render) chứ không phải ở BAChatReplyParser,
    // nơi dành cho các chốt chặn làm ĐỔI câu trả lời trước khi nó lên màn hình.
    //
    // Sửa MỘT CHIỀU, và giá của hai kiểu sai lệch nhau hẳn: nhận nhầm ⇒ người dùng tốn thêm một cú bấm
    // "Gửi" (ô để trống vẫn gửi nguyên chip); bỏ sót ⇒ đúng bằng hành vi hôm nay. Không có chiều ngược lại
    // nào — không cú bấm nào bị chặn, không chip nào bị xoá.
    const DISSENT_CHIP_CUES = [
        "tính khác", "cách khác", "cách tính khác", "phương án khác", "phương án nào khác",
        "muốn khác", "làm khác", "ý khác", "khác cơ", "khác với",
        "sửa lại", "chỉnh lại", "tính lại", "chưa đúng", "không đúng", "không phải vậy", "chưa chính xác"
    ];

    function isDissentChip(text) {
        const t = (text || "").trim().toLowerCase().replace(/[.…!?,;:\s]+$/, "");
        if (!t) return false;
        // "Không, tính khác" / "Không, khác" — bắt cả các biến thể mà cụm cố định ở trên không phủ hết.
        if (t.startsWith("không") && t.includes("khác")) return true;
        // Chip KẾT BẰNG "khác" — bắt theo HÌNH DẠNG, vì "Quy tắc khác", "Trạng thái khác", "Cách xử lý
        // khác" là cùng một chip đội ba cái tên và danh sách cụm cố định ở trên không bao giờ phủ hết.
        // BAChatReplyParser.DropBareOtherChips đã xoá phần lớn chúng, nhưng nó CỐ Ý dừng lại khi xoá xong
        // còn dưới 2 chip — tức bộ hai chip prompt kê sẵn ở lượt xin chốt (["Đồng ý", "Tôi muốn khác"])
        // lên màn hình nguyên vẹn, và đó đúng là bộ mà cú bấm "khác" tốn kém nhất. Ở đây bắt RỘNG hơn
        // parser được: nhận nhầm một chip có nội dung thật ("Chuyển sang phòng ban khác") chỉ tốn thêm một
        // cú bấm "Gửi", còn parser thì xoá hẳn chip nên phải hẹp.
        if (/(^|\s)khác$/.test(t)) return true;
        return DISSENT_CHIP_CUES.some(cue => t.includes(cue));
    }

    // ==== Thẻ hỏi GỘP (2–4 câu hỏi độc lập trong cùng một lượt BA) ====
    // Thay cho cổng "chốt nhanh" đã bỏ. Khác biệt cốt tử, và là lý do cả khối này tồn tại: cổng cũ ghi
    // PHƯƠNG ÁN DO BA TỰ SOẠN vào hội thoại như lời của chính người dùng — bản đồ bao phủ đầy lên mà
    // không ai thật sự trả lời câu nào, rồi mọi tầng phía sau (Product Brief, spec, POC) tin đó là điều
    // người dùng đã nói. Ở đây thứ được ghi luôn là CÂU TRẢ LỜI của người dùng cho câu hỏi của BA; cái
    // được rút ngắn chỉ là số vòng đi-về.
    //
    // Cả cụm được gửi qua đúng đường chat thường (soạn thành một tin nhắn "- câu hỏi: trả lời" rồi
    // requestSubmit) — không có endpoint riêng, nên không có đường ghi thứ hai nào lệch khỏi luồng chính
    // và mọi thứ đã đúng ở lượt chat (cổng readiness, chắt lọc bản đồ, decision log) tự khắc đúng ở đây.
    const batchPanel = document.getElementById("batchQuestions");

    // Ô tự nhập của MỘT câu trên thẻ luôn MỞ, kể cả câu có hàng gợi ý, và nó là ô "Ý KHÁC" — đúng bằng ô
    // cùng tên ở hàng chip lượt-đơn, cùng nhãn, cùng cách gộp vào tin nhắn gửi đi.
    //
    // Trước đây ô này bị dùng làm NƠI LƯU câu trả lời của cả hàng gợi ý: bấm chip = chép nguyên văn chip
    // vào ô. Màn hình vì thế nói một điều HAI LẦN — chip sáng ngay trên, y hệt câu chữ đó lại nằm trong ô
    // ngay dưới — mà chẳng thêm được gì: người dùng không sửa được câu gợi ý bằng cách đó (sửa một chữ là
    // chip tắt, thành một câu tự nhập khác hẳn), chỉ còn cảm giác mình phải xoá đi thứ vừa được điền hộ.
    // Nó cũng chiếm mất chỗ của việc mà ô này sinh ra để làm: nói thêm một ý mà không gợi ý nào phủ.
    //
    // Nay hai vai tách hẳn: CHIP giữ lựa chọn (trạng thái nằm trên chính chip, `.is-on`), Ô giữ phần người
    // dùng tự nói. Câu trả lời gửi đi là hai vế ghép lại (batchAnswerOf) — bấm chip rồi gõ thêm thì cả hai
    // cùng đi, đúng như hàng chip lượt-đơn ghép "chip — lời viết thêm".
    const BATCH_ANSWER_LABEL = "Ý khác — câu trả lời anh/chị tự nhập";
    const BATCH_ANSWER_PLACEHOLDER = "Không gợi ý nào đúng, hoặc muốn nói thêm? Anh/chị gõ vào đây…";

    // Lựa chọn của một câu = các chip đang sáng, theo đúng thứ tự chúng nằm trên thẻ (không theo thứ tự
    // bấm): thứ tự hiển thị là thứ tự người dùng vừa đọc, nên tin nhắn gửi đi khớp với thứ họ thấy.
    function batchPicks(li) {
        return Array.from(li.querySelectorAll(".batchq-choice.is-on"))
            .map(chip => (chip.dataset.value || "").trim())
            .filter(Boolean);
    }

    function batchOtherText(li) {
        const box = li.querySelector(".batchq-answer");
        return box ? (box.value || "").trim() : "";
    }

    // Câu trả lời THẬT của một dòng = chip đã bấm + lời tự nhập. Giữ CẢ HAI vế: bỏ chip đi thì phần viết
    // thêm ("nhưng chỉ với đơn trên 10 triệu") đứng trơ trọi, các tầng chắt lọc phía sau không còn biết nó
    // đang nói thêm cho lựa chọn nào; bỏ phần tự nhập đi thì ô "Ý khác" thành ô trang trí.
    // Câu MỞ không có chip nào ⇒ rơi về đúng nội dung ô, như trước.
    function batchAnswerOf(li) {
        const picks = batchPicks(li).join(", ");
        const typed = batchOtherText(li);
        if (!typed) return picks;
        return picks ? `${picks} — ${typed}` : typed;
    }

    // Bấm chip: chọn-nhiều thì mỗi chip là một công tắc riêng; chọn-một thì chip vừa bấm sáng và tắt các
    // chip còn lại, bấm lại chính nó = bỏ chọn (một cú bấm nhầm luôn có đường lùi).
    function toggleBatchChip(li, chip) {
        const on = !chip.classList.contains("is-on");
        if (li.dataset.multi !== "true") {
            li.querySelectorAll(".batchq-choice").forEach(c => c.classList.remove("is-on"));
        }
        chip.classList.toggle("is-on", on);
    }

    // Các câu hỏi đang nằm trên thẻ, dựng thành dấu vết CHỈ-ĐỌC. Không có phần này, các câu hỏi biến mất
    // ngay khi người dùng trả lời (câu dẫn của lượt gộp không chứa câu hỏi nào), nên lịch sử chat còn lại
    // đúng một câu "mình hỏi 4 điểm sau" vô nghĩa — và người dùng không có gì để đối chiếu khi BA lỡ hỏi
    // lại điều họ vừa trả lời. Markup khớp bản server render cho một lượt gộp CŨ (.batchq-history).
    function batchQuestionsHistoryHtml() {
        const rows = Array.from(batchPanel.querySelectorAll(".batchq-item"))
            .map(li => ({
                group: ((li.querySelector(".batchq-group") || {}).textContent || "").trim(),
                question: li.dataset.question || ""
            }))
            .filter(x => x.question)
            .map(x => `
                <li>
                    ${x.group ? `<span class="batchq-history-group">${escapeHtml(x.group)}</span>` : ""}
                    <span class="batchq-history-question">${escapeHtml(x.question)}</span>
                </li>`)
            .join("");

        return rows ? `<ul class="batchq-history">${rows}</ul>` : "";
    }

    function hideBatchQuestions() {
        if (!batchPanel || batchPanel.hidden) return;

        // Thẻ giờ CHỞ LUÔN câu dẫn của lượt (xem renderBatchQuestions), nên xóa trắng thẻ là xóa luôn
        // lượt BA đó khỏi màn hình — chưa kể nhãn "BA" phía trên thành mồ côi. Xếp thẻ lại thành một bong
        // bóng BA thường mang câu dẫn KÈM các câu vừa hỏi: đúng bằng thứ server render cho một lượt gộp
        // CŨ sau khi F5, nên hai đường không lệch nhau.
        const lead = batchPanel.querySelector(".batchq-lead");
        const label = batchPanel.previousElementSibling;
        const leadText = lead ? (lead.textContent || "").trim() : "";
        const history = batchQuestionsHistoryHtml();
        if (leadText || history) {
            batchPanel.insertAdjacentHTML("beforebegin", `
                <div class="req-msg ba">
                    ${leadText ? `<p style="white-space: pre-wrap;">${escapeHtml(leadText)}</p>` : ""}
                    ${history}
                </div>
            `);
        } else if (label && label.classList.contains("req-who")) {
            label.remove();
        }

        batchPanel.hidden = true;
        batchPanel.innerHTML = "";

        // Thẻ không còn trên màn hình ⇒ nháp các ô trả lời của nó cũng hết chỗ để đổ về (đã gửi, hoặc
        // người dùng chọn đường gõ tay). Giữ lại thì lần mở trang sau nó đổ vào một thẻ hỏi KHÁC.
        draftBatchClear();
    }

    // Các câu ĐÃ có câu trả lời, theo đúng thứ tự hỏi. Câu để trống đơn giản không có mặt — BA hỏi tiếp
    // ở lượt sau, đúng như lời hứa dưới nút gửi.
    function answeredBatchQuestions() {
        if (!batchPanel || batchPanel.hidden) return [];
        return Array.from(batchPanel.querySelectorAll(".batchq-item"))
            .map(li => ({
                question: li.dataset.question || "",
                answer: batchAnswerOf(li)
            }))
            .filter(x => x.answer.length > 0);
    }

    // Nhãn nút đếm LIVE: một nút ghi cứng "Gửi 3 câu" trên thẻ mà người dùng mới trả lời 1 câu là lời
    // hứa sai — họ không biết mình sắp gửi đi những gì.
    function updateBatchSendButton() {
        const btn = document.getElementById("batchQuestionsSendBtn");
        if (!btn) return;
        const count = answeredBatchQuestions().length;
        btn.disabled = count === 0 || chatBusy;
        btn.textContent = count === 0 ? "Chưa trả lời câu nào" : `Gửi ${count} câu trả lời`;
    }

    // GỘP CÂU DẪN VÀO THẺ: ở lượt gộp, `message` chỉ là câu dẫn ngắn, nên bong bóng vừa stream xong và
    // thẻ hỏi ngay dưới nó là hai khung liền nhau nói cùng một ý. Gỡ bong bóng đó và trả text về cho
    // renderBatchQuestions đặt làm dòng đầu của thẻ — thẻ TRỞ THÀNH bong bóng của lượt, nên nhãn "BA"
    // của lượt được dời xuống ngay trên thẻ (thay vì xóa theo bong bóng, rồi thẻ đứng trơ không nhãn).
    function absorbLeadBubble(bubble) {
        if (!bubble || bubble.classList.contains("chat-error")) return "";

        const label = bubble.previousElementSibling;
        const p = bubble.querySelector("p");
        const text = p ? (p.textContent || "").trim() : "";
        bubble.remove();
        if (label && label.classList.contains("req-who")) thinkingBox.before(label);
        return text;
    }

    // Markup phải khớp bản server render trong Index.cshtml (đường tải lại trang).
    function renderBatchQuestions(questions, leadBubble) {
        if (!batchPanel) return;
        if (!Array.isArray(questions) || questions.length === 0) {
            hideBatchQuestions();
            return;
        }

        // Lượt gộp MỚI: các câu hỏi cũ biến mất nên nháp trả lời của chúng cũng phải đi theo.
        draftBatchClear();

        // Rỗng (lượt lỗi giữ bong bóng riêng, hoặc BA trả `message` trống) → rơi về câu dẫn tĩnh: thẻ mở
        // đầu bằng câu hỏi trần thì mất mạch hội thoại.
        const lead = absorbLeadBubble(leadBubble) || "Anh/chị trả lời giúp mình mấy điểm sau nhé.";

        batchPanel.innerHTML = `
            <p class="batchq-lead">${escapeHtml(lead)}</p>
            <div class="batchq-howto">Bấm gợi ý, muốn nói thêm thì gõ vào ô "Ý khác"; điểm nào chưa nghĩ tới thì để trống.</div>
            <ul class="batchq-list">
                ${questions.map(q => {
                    // Câu MỞ: không có gợi ý nào để bấm, nên chỉ còn ô tự nhập — một dòng chỉ có mỗi câu
                    // hỏi mà không có chỗ trả lời đọc như một dòng bị lỗi.
                    const open = q.openEnded === true || !(Array.isArray(q.suggestions) && q.suggestions.length > 0);
                    return `
                <li class="batchq-item" data-question="${escapeHtml(q.question || "")}" data-multi="${q.multiSelect ? "true" : "false"}" data-open="${open ? "true" : "false"}">
                    ${q.group ? `<div class="batchq-group">${escapeHtml(q.group)}</div>` : ""}
                    <div class="batchq-question">${escapeHtml(q.question || "")}</div>
                    ${open ? "" : `
                    <div class="batchq-choices">
                        ${(Array.isArray(q.suggestions) ? q.suggestions : []).map(s => `
                        <button type="button" class="batchq-choice" data-value="${escapeHtml(s)}">${escapeHtml(s)}</button>`).join("")}
                    </div>
                    <div class="batchq-other-field">
                        <textarea class="batchq-answer" rows="1" aria-label="${BATCH_ANSWER_LABEL}" placeholder="${BATCH_ANSWER_PLACEHOLDER}"></textarea>
                        <span class="batchq-other-cap" aria-hidden="true">Ý khác</span>
                    </div>`}
                    ${open ? `<textarea class="batchq-answer" rows="3" placeholder="Anh/chị kể giúp mình, càng chi tiết càng tốt…"></textarea>` : ""}
                </li>`;
                }).join("")}
            </ul>
            <div class="batchq-bar">
                <button type="button" class="btn primary" id="batchQuestionsSendBtn" disabled>Chưa trả lời câu nào</button>
                <div class="batchq-hint">Không cần trả lời hết — gửi phần anh/chị đã rõ, BA hỏi tiếp các câu còn lại.</div>
            </div>`;

        // Dời xuống cuối dòng hội thoại: các lượt streaming được chèn vào TRƯỚC thinkingBox, nên một khối
        // render ở vị trí cố định sẽ trôi lên phía trên các lượt mới sau vài lượt chat.
        thinkingBox.before(batchPanel);
        batchPanel.hidden = false;
        scrollToBottom();
    }

    if (batchPanel) {
        batchPanel.addEventListener("click", function (e) {
            const choice = e.target.closest(".batchq-choice");
            if (choice) {
                const li = choice.closest(".batchq-item");

                // Chip KHÔNG đụng tới ô "Ý khác": ô đó chở phần người dùng tự nói, chép lựa chọn vào đó là
                // nói một điều hai lần rồi bắt họ tự xoá (xem chú thích ở BATCH_ANSWER_LABEL).
                toggleBatchChip(li, choice);

                // KHÔNG focus vào ô sau cú bấm: bấm gợi ý là thao tác "câu này xong rồi", mà focus thì trên
                // điện thoại bật bàn phím lên che mất các câu còn lại của thẻ.
                updateBatchSendButton();
                // Bấm chip không đụng vào ô nào nên không có sự kiện input → phải tự hẹn lưu nháp.
                draftBatchSaveSoon();
                return;
            }

            if (!e.target.closest("#batchQuestionsSendBtn")) return;

            const answers = answeredBatchQuestions();
            if (answers.length === 0 || chatBusy) return;

            // Soạn thành MỘT tin nhắn của người dùng, mỗi dòng một cặp câu hỏi–trả lời. Kèm lại câu hỏi
            // (không chỉ câu trả lời) vì mọi tầng chắt lọc đọc hội thoại: một dòng "Dưới 20 người" đứng
            // trơ trọi thì bản đồ bao phủ không biết nó trả lời cho nhóm nào.
            messageInput.value = answers.map(a => `- ${a.question}: ${a.answer}`).join("\n");
            chatForm.requestSubmit();
        });

        // Câu không chip nào sáng và ô cũng rỗng thì KHÔNG được tính, nên nhãn nút phải nhảy theo từng
        // phím gõ.
        batchPanel.addEventListener("input", function (e) {
            if (!e.target.classList.contains("batchq-answer")) return;
            // Chỉ ô trong khung nhãn-nổi mới tự cao: ô của câu MỞ đứng riêng, đã có sẵn 3 dòng và
            // `resize: vertical` để người dùng tự kéo.
            if (e.target.closest(".batchq-other-field")) autoGrowOtherBox(e.target);
            updateBatchSendButton();
            draftBatchSaveSoon();
        });
    }

    // ==== BẢNG CỘT của file bảng tính người dùng vừa gửi ====
    // Bảng do server render (lượt đọc file luôn tới sau một lần upload → tải lại trang), nên ở đây chỉ có
    // phần GỬI. Gửi đi hai bước, và thứ tự là điểm mấu chốt:
    //   1. lưu bảng vào chính file nguồn (ConfirmColumnMap) — đây là thứ SourceContextBuilder và
    //      RealSampleDataReader đọc, tức là thứ quyết định POC seed bằng cột nào;
    //   2. rồi mới gửi tin nhắn người dùng mà SERVER vừa soạn từ bảng đã lưu qua đúng đường chat thường.
    // Bước 2 đi đường chat thường (không có endpoint riêng) nên hội thoại vẫn chỉ có một đường ghi, và mọi
    // thứ đã đúng ở lượt chat (cổng readiness, chắt lọc bản đồ bao phủ, nhật ký điều đã chốt) tự khắc đúng
    // ở đây — cùng lý do với thẻ hỏi gộp bên trên.
    //
    // Tin nhắn lấy từ RESPONSE chứ không ghép ở đây (như bảng phân quyền), vì nó gánh thêm một việc thứ hai:
    // câu mở đầu cố định của nó là dấu hiệu để lượt chat kế tiếp biết mình là lượt BA KỂ LẠI cách hiểu file
    // theo bộ cột vừa chốt (SourceColumnMapBuilder.IsSubmissionMessage). Một bản JS ghép riêng thì chỉ cần
    // lệch một chữ là lượt kể lại không bao giờ diễn ra.
    const columnMapPanel = document.getElementById("columnMap");

    function columnMapRows() {
        if (!columnMapPanel || columnMapPanel.hidden) return [];
        return Array.from(columnMapPanel.querySelectorAll(".colmap-row")).map(tr => ({
            fileName: tr.dataset.file || "",
            column: tr.dataset.column || "",
            meaning: ((tr.querySelector(".colmap-meaning") || {}).value || "").trim(),
            used: !!(tr.querySelector(".colmap-check") || {}).checked
        }));
    }

    function hideColumnMap() {
        if (!columnMapPanel || columnMapPanel.hidden) return;
        columnMapPanel.hidden = true;
        columnMapPanel.innerHTML = "";
    }

    if (columnMapPanel) {
        columnMapPanel.addEventListener("click", async function (e) {
            if (!e.target.closest("#columnMapSendBtn") || chatBusy) return;

            const btn = document.getElementById("columnMapSendBtn");
            const msgEl = document.getElementById("columnMapMsg");
            const rows = columnMapRows();
            if (rows.length === 0) return;

            btn.disabled = true;
            msgEl.textContent = "Đang lưu bảng cột…";

            let message;
            try {
                const fd = new FormData();
                fd.append("projectId", window.REQUIREMENTS_PROJECT_ID || "");
                const tokenEl = document.querySelector('input[name="__RequestVerificationToken"]');
                fd.append("__RequestVerificationToken", tokenEl ? tokenEl.value : "");
                fd.append("mapJson", JSON.stringify(rows));

                const resp = await fetch(columnMapPanel.dataset.confirmUrl, { method: "POST", body: fd });
                const data = await resp.json();
                if (!data.ok || !data.message) throw new Error(data.error || "");
                message = data.message;
            } catch (err) {
                // Lưu hỏng thì DỪNG hẳn, không gửi tin nhắn: gửi mà chưa lưu là trạng thái tệ nhất — hội
                // thoại ghi nhận đã chốt phạm vi cột, còn file nguồn thì vẫn trống nên POC vẫn seed bằng
                // đủ cả cột của hệ cũ, và không ai còn thấy bảng đâu để tích lại.
                btn.disabled = false;
                msgEl.textContent = "Chưa lưu được bảng cột — anh/chị bấm gửi lại giúp mình nhé.";
                return;
            }

            hideColumnMap();
            messageInput.value = message;
            chatForm.requestSubmit();
        });
    }

    // ==== BẢNG PHÂN QUYỀN (lượt chốt nhóm «Phân quyền theo nghiệp vụ», cuối buổi phỏng vấn) ====
    // Khác bảng cột ở một điểm mấu chốt về đường đi: lượt đọc file luôn tới sau một lần upload (redirect ⇒
    // tải lại trang) nên bảng cột chỉ cần bản server render, còn bảng này tới trong MỘT LƯỢT CHAT bình
    // thường qua SSE — nên phải có cả đường JS dựng bảng ở frame done, và markup hai bên phải khớp nhau.
    //
    // Phần gửi thì cùng khuôn hai bước với bảng cột: (1) lưu vào Project.PermissionMatrix, (2) gửi tin nhắn
    // do SERVER soạn qua đúng đường chat thường. Tin nhắn lấy từ response chứ không ghép ở đây, vì nó phải
    // khớp đúng bảng đã được server chuẩn hoá và lưu — hai bản lệch nhau thì hội thoại kể một đằng còn dữ
    // liệu dự án ghi một nẻo, và mọi tầng đọc transcript tin vào bản kể.
    const permMapPanel = document.getElementById("permissionMatrix");
    const PERM_SCOPES = ["của mình", "của đơn vị", "tất cả"];
    const MAX_PERM_ROLES = 8; // = PermissionMatrixBuilder.MaxRoles

    function permMapRows() {
        if (!permMapPanel || permMapPanel.hidden) return [];
        return Array.from(permMapPanel.querySelectorAll(".permmap-row")).map(tr => ({
            screen: tr.dataset.screen || "",
            function: tr.dataset.function || "",
            condition: ((tr.querySelector(".permmap-condition") || {}).value || "").trim(),
            grants: Array.from(tr.querySelectorAll(".permmap-cell")).map(td => ({
                role: td.dataset.role || "",
                scope: ((td.querySelector(".permmap-scope") || {}).value || "").trim()
            }))
        }));
    }

    function hidePermissionMatrix() {
        if (!permMapPanel || permMapPanel.hidden) return;
        permMapPanel.hidden = true;
        permMapPanel.innerHTML = "";
    }

    // MỘT ô quyền. Dùng chung cho lượt dựng cả bảng và cho lúc bảng vai trò thêm một cột — hai bản sao là
    // hai chỗ để một bản quên mất luật "ô có bằng chứng thì khóa".
    function permissionCell(role, fn, grant) {
        const g = grant || {};
        const scope = g.scope || "";
        const inner = g.locked
            ? `<span class="permmap-locked" title="${escapeHtml(g.evidence || "")}">✓ ${escapeHtml(scope)}</span>
               <input type="hidden" class="permmap-scope" value="${escapeHtml(scope)}" />`
            : `<select class="permmap-scope" aria-label="${escapeHtml(role)} — ${escapeHtml(fn)}">
                   <option value=""${scope ? "" : " selected"}>—</option>
                   ${PERM_SCOPES.map(s => `<option value="${s}"${scope === s ? " selected" : ""}>${s}</option>`).join("")}
               </select>`;
        return `<td class="permmap-cell" data-role="${escapeHtml(role)}">${inner}</td>`;
    }

    // MỘT dòng của bảng vai trò. Markup khớp bản server render trong Index.cshtml.
    function permissionRoleRow(value) {
        return `
            <tr class="permrole-row">
                <td><textarea rows="1" class="permmap-cellinput permrole-name" aria-label="Tên vai trò">${escapeHtml(value || "")}</textarea></td>
                <td class="entitymap-delcell">
                    <button type="button" class="entitymap-del permrole-del" title="Xóa vai trò này" aria-label="Xóa vai trò này">×</button>
                </td>
            </tr>`;
    }

    function renderPermissionRoles(roles) {
        return `
            <div class="permmap-howto">
                Đây là các <b>vai trò</b> mình gom được từ những gì anh/chị đã kể — cũng chính là các <b>cột</b>
                của bảng dưới. Thiếu vai nào thì thêm ngay ở đây, sửa chữ hoặc xóa cũng được; các bảng dưới đổi
                cột theo.
            </div>
            <table class="permmap-table permrole-table">
                <thead>
                    <tr>
                        <th>Vai trò</th>
                        <th class="screenmap-th-del"></th>
                    </tr>
                </thead>
                <tbody>${roles.map(permissionRoleRow).join("")}
                    <tr class="permrole-addrow">
                        <td colspan="2"><button type="button" class="entitymap-add permrole-add">+ thêm vai trò</button></td>
                    </tr>
                </tbody>
            </table>`;
    }

    // CỘT của bảng đang có hiệu lực. Bảng vai trò là bản DUY NHẤT đáng tin — nó là thứ người dùng vừa gõ;
    // hàng ô của dòng đầu chỉ là bản dự phòng cho payload/tab dựng từ trước bản này.
    function permissionRoles() {
        if (!permMapPanel) return [];

        const table = permMapPanel.querySelector(".permrole-table");
        if (table) return permRoleValues(table);

        const first = permMapPanel.querySelector(".permmap-row");
        return first ? Array.from(first.querySelectorAll(".permmap-cell")).map(td => td.dataset.role || "") : [];
    }

    // Các mục của bảng vai trò, theo đúng thứ tự trên bảng: bỏ dòng chưa gõ, bỏ trùng, chặn ở trần. Cùng
    // luật với PermissionMatrixBuilder.SanitizeRoles — server chạy lại y hệt trên payload, nên hai bên lệch
    // nhau là người dùng thấy một cột trên màn hình rồi bị server bỏ đúng cột đó.
    function permRoleValues(root) {
        const table = root || (permMapPanel && permMapPanel.querySelector(".permrole-table"));
        if (!table) return [];

        const values = [];
        const seen = Object.create(null);
        Array.from(table.querySelectorAll(".permrole-row")).forEach(row => {
            const value = tableValue(row, ".permrole-name");
            const key = normalizePermRole(value);
            if (value.length === 0 || seen[key] || values.length >= MAX_PERM_ROLES) return;
            seen[key] = true;
            values.push(value);
        });
        return values;
    }

    // Chép phép chuẩn hoá của server (PermissionMatrixBuilder.Normalize) vì nó quyết định thứ TRÙNG NHAU:
    // "HRBP" và "hrbp " là hai dòng khác nhau trên bảng nhưng cùng một cột lúc so khớp.
    function normalizePermRole(value) {
        return (value || "").toLowerCase().split(/\s+/).filter(Boolean).join(" ")
            .replace(/^[.,:;–-]+/, "").replace(/[.,:;–-]+$/, "");
    }

    // Số ô đang CẤP quyền cho một vai — câu hỏi phải trả lời được TRƯỚC khi xóa vai đó.
    function permRoleUsage(value) {
        if (!permMapPanel || !value) return 0;
        return Array.from(permMapPanel.querySelectorAll(".permmap-cell"))
            .filter(td => td.dataset.role === value
                && ((td.querySelector(".permmap-scope") || {}).value || "").trim().length > 0)
            .length;
    }

    // Bảng vai trò vừa đổi ⇒ dựng lại CỘT của mọi bảng màn hình. Đây là chỗ giữ lời hứa của cả tính năng
    // ("sửa một chỗ, mọi bảng đổi theo"), và nó phải làm đủ ba việc, thiếu việc nào cũng là mất dữ liệu im
    // lặng:
    //  • ĐỔI TÊN thì mang theo cả ô đã chọn (`renameFrom` → `renameTo`) — dựng lại ô rỗng là xóa sạch phạm
    //    vi người dùng vừa chọn cho vai đó, ở mọi màn hình, chỉ vì họ sửa một chữ trong tên;
    //  • XÓA thì bỏ hẳn cột đó khỏi mọi bảng;
    //  • THÊM thì chèn một cột rỗng vào mọi dòng, không phải chỉ dòng đầu — cột lỗ chỗ là đúng khiếm khuyết
    //    mà PermissionMatrixBuilder.NormalizeGrants sinh ra để chữa.
    function syncPermissionRoles(renameFrom, renameTo) {
        if (!permMapPanel) return;

        const roles = permRoleValues();

        permMapPanel.querySelectorAll(".permmap-table:not(.permrole-table)").forEach(table => {
            const head = table.querySelector("thead tr");
            const cond = head ? head.querySelector(".permmap-th-cond") : null;
            if (cond) {
                head.querySelectorAll(".permmap-th-role").forEach(th => th.remove());
                roles.forEach(role => cond.insertAdjacentHTML("beforebegin",
                    `<th class="permmap-th-role">${escapeHtml(role)}</th>`));
            }

            table.querySelectorAll(".permmap-row").forEach(row => {
                const fn = row.dataset.function || "";
                const kept = Object.create(null);
                row.querySelectorAll(".permmap-cell").forEach(cell => {
                    const role = (renameFrom && cell.dataset.role === renameFrom) ? renameTo : (cell.dataset.role || "");
                    cell.remove();
                    if (kept[role]) return;
                    cell.dataset.role = role;
                    // Nhãn trợ năng phải đi theo tên mới: nó là thứ DUY NHẤT đọc màn hình đọc ra ở một ô
                    // chọn, nên để nó giữ tên cũ là kể sai cột người dùng đang điền.
                    const select = cell.querySelector("select.permmap-scope");
                    if (select) select.setAttribute("aria-label", `${role} — ${fn}`);
                    kept[role] = cell;
                });

                // Ô điều kiện luôn là ô CUỐI dòng — các cột quyền chèn vào trước nó.
                const anchor = row.lastElementChild;
                roles.forEach(role => {
                    if (kept[role]) row.insertBefore(kept[role], anchor);
                    else anchor.insertAdjacentHTML("beforebegin", permissionCell(role, fn, null));
                });
            });
        });
    }

    // Dựng bảng từ frame done. Markup khớp bản server render trong Index.cshtml — hai đường lệch nhau thì
    // người dùng chọn xong bảng vừa hiện ra rồi F5 và thấy một bảng khác.
    function renderPermissionMatrix(rows) {
        if (!permMapPanel || !Array.isArray(rows) || rows.length === 0) return;

        const roles = (rows[0].grants || []).map(g => g.role);
        const screens = [];
        rows.forEach(r => { if (screens.indexOf(r.screen) < 0) screens.push(r.screen); });

        const tables = screens.map(screen => {
            const body = rows.filter(r => r.screen === screen).map(r => `
                <tr class="permmap-row" data-screen="${escapeHtml(r.screen)}" data-function="${escapeHtml(r.function)}">
                    <td class="permmap-fn">${escapeHtml(r.function)}</td>
                    ${(r.grants || []).map(g => permissionCell(g.role, r.function, g)).join("")}
                    <td><input type="text" class="permmap-condition" value="${escapeHtml(r.condition || "")}" placeholder="vd: chỉ sửa khi chưa submit" /></td>
                </tr>`).join("");

            return `
                <div class="permmap-screen">${escapeHtml(screen)}</div>
                <table class="permmap-table">
                    <thead>
                        <tr>
                            <th class="permmap-th-fn">Chức năng</th>
                            ${roles.map(role => `<th class="permmap-th-role">${escapeHtml(role)}</th>`).join("")}
                            <th class="permmap-th-cond">Điều kiện thêm (nếu có)</th>
                        </tr>
                    </thead>
                    <tbody>${body}</tbody>
                </table>`;
        }).join("");

        permMapPanel.innerHTML = `
            ${renderPermissionRoles(roles)}
            <div class="permmap-howto">
                Ô <b>✓</b> là quyền anh/chị đã nói trong lúc trao đổi (rê chuột để xem lại câu gốc) — mình khóa
                lại, không cần chọn nữa. Các ô còn lại là <b>phỏng đoán của mình</b>: anh/chị chọn phạm vi dữ
                liệu cho đúng, và <b>để trống</b> nghĩa là vai đó không có quyền này.
            </div>
            ${tables}
            <div class="permmap-bar">
                <button type="button" class="btn primary" id="permissionMatrixSendBtn">Gửi bảng phân quyền</button>
                <div class="permmap-hint">
                    Thiếu màn hình nào thì anh/chị cứ gõ vào khung chat — mình bổ sung rồi bày lại bảng.
                </div>
                <div class="permmap-msg" id="permissionMatrixMsg"></div>
            </div>`;
        permMapPanel.hidden = false;
        autoGrowCells(permMapPanel);
        thinkingBox.before(permMapPanel);
    }

    if (permMapPanel) {
        // THÊM / XÓA một vai trò. Ủy quyền trên PANEL vì renderPermissionMatrix thay sạch innerHTML.
        permMapPanel.addEventListener("click", function (e) {
            const add = e.target.closest(".permrole-add");
            const remove = e.target.closest(".permrole-del");
            if (!add && !remove) return;

            const msgEl = document.getElementById("permissionMatrixMsg");
            const note = text => { if (msgEl) msgEl.textContent = text; };

            // Lời hỏi lại "bấm × lần nữa" chỉ sống tới thao tác kế tiếp: người dùng bỏ ngang rồi vài phút
            // sau bấm × một cái là xóa ngay, trong khi họ tưởng cú bấm đó mới là cú thứ nhất.
            permMapPanel.querySelectorAll('.permrole-del[data-confirm="1"]').forEach(el => {
                if (el !== remove) delete el.dataset.confirm;
            });

            if (add) {
                const anchor = permMapPanel.querySelector(".permrole-addrow");
                if (!anchor) return;

                // Trần là giới hạn ĐỌC ĐƯỢC, không phải guard suông: mỗi vai là một cột trên MỌI bảng màn
                // hình, và quá số này thì bảng không rà nổi trên một màn hình thường.
                if (permRoleValues().length >= MAX_PERM_ROLES) {
                    note(`Bảng chỉ hiện được tối đa ${MAX_PERM_ROLES} vai trò.`);
                    return;
                }

                anchor.insertAdjacentHTML("beforebegin", permissionRoleRow(""));
                focusNewRow(anchor.previousElementSibling, ".permrole-name");
                note("");
                return;
            }

            const row = remove.closest(".permrole-row");
            if (!row) return;

            // Bảng không còn cột nào thì không còn ô quyền nào để chọn, và server cũng từ chối lưu — chặn
            // ngay ở đây để người dùng không phải rà cả bảng rồi mới biết lúc bấm gửi.
            if (permRoleValues().length <= 1) {
                note("Bảng cần ít nhất một vai trò — anh/chị sửa chữ dòng này thay vì xóa nhé.");
                return;
            }

            const value = tableValue(row, ".permrole-name");
            const used = permRoleUsage(value);
            if (used > 0 && remove.dataset.confirm !== "1") {
                remove.dataset.confirm = "1";
                note(`“${value}” đang được cấp quyền ở ${used} ô — bấm × lần nữa để xóa cả cột đó.`);
                return;
            }

            row.remove();
            syncPermissionRoles();
            note("");
        });

        // Giá trị TRƯỚC khi sửa của một ô tên vai trò — phải chụp lúc con trỏ vào ô, vì lúc `change` bắn ra
        // thì ô đã mang chữ mới và không còn gì để nối cột cũ về cột mới.
        permMapPanel.addEventListener("focusin", function (e) {
            const nameCell = e.target.closest(".permrole-name");
            if (nameCell) nameCell.dataset.prev = tableValue(nameCell.closest(".permrole-row"), ".permrole-name");
        });

        permMapPanel.addEventListener("change", function (e) {
            const nameCell = e.target.closest(".permrole-name");
            if (nameCell) renamePermissionRole(nameCell);
        });

        permMapPanel.addEventListener("click", async function (e) {
            if (!e.target.closest("#permissionMatrixSendBtn") || chatBusy) return;

            const btn = document.getElementById("permissionMatrixSendBtn");
            const msgEl = document.getElementById("permissionMatrixMsg");
            const rows = permMapRows();
            const roles = permissionRoles();
            if (rows.length === 0) return;

            if (roles.length === 0) {
                msgEl.textContent = "Bảng phải có ít nhất một vai trò — anh/chị thêm một dòng ở bảng Vai trò rồi gửi nhé.";
                return;
            }

            btn.disabled = true;
            msgEl.textContent = "Đang lưu bảng phân quyền…";

            let message = "";
            try {
                const fd = new FormData();
                fd.append("projectId", window.REQUIREMENTS_PROJECT_ID || "");
                const tokenEl = document.querySelector('input[name="__RequestVerificationToken"]');
                fd.append("__RequestVerificationToken", tokenEl ? tokenEl.value : "");
                fd.append("matrixJson", JSON.stringify(rows));
                // Bộ CỘT đi CÙNG CHUYẾN với bảng: để riêng thì server lại chắt cột từ grants như trước, và
                // một vai người dùng vừa thêm nhưng chưa cấp quyền ở dòng nào biến mất khỏi bảng đã lưu.
                fd.append("rolesJson", JSON.stringify(roles));

                const resp = await fetch(permMapPanel.dataset.confirmUrl, { method: "POST", body: fd });
                const data = await resp.json();
                if (!data.ok || !data.message) {
                    // Câu do SERVER soạn đã gọi tên đúng thứ phải sửa — in nguyên văn, vì câu chung chung
                    // "bấm gửi lại" sẽ dẫn người dùng bấm lại đúng cái bảng vừa bị từ chối.
                    btn.disabled = false;
                    msgEl.textContent = data.error || "Chưa lưu được bảng phân quyền — anh/chị bấm gửi lại giúp mình nhé.";
                    return;
                }
                message = data.message;
            } catch (err) {
                // Lưu hỏng thì DỪNG hẳn, không gửi tin nhắn — cùng lý do với bảng cột: hội thoại ghi nhận
                // "đã chốt phân quyền" trong khi dự án vẫn trống là trạng thái tệ nhất, vì bản đồ bao phủ
                // sẽ nâng nhóm này lên [RÕ] dựa trên tin nhắn đó rồi cấm hỏi lại, còn bảng thì không ai
                // còn thấy đâu để chọn lại.
                btn.disabled = false;
                msgEl.textContent = "Chưa lưu được bảng phân quyền — anh/chị bấm gửi lại giúp mình nhé.";
                return;
            }

            hidePermissionMatrix();
            messageInput.value = message;
            chatForm.requestSubmit();
        });
    }

    // SỬA CHỮ một vai trò. Hai giá trị bị từ chối và cùng trả ô về chữ cũ, vì cả hai đều làm hỏng đúng mối
    // nối mà bảng vai trò sinh ra để giữ:
    //  • RỖNG — cột mất tên thì mọi ô của nó bị server bỏ lúc lưu, trong im lặng; muốn bỏ hẳn thì có nút ×
    //    (nó còn hỏi lại khi cột đang có quyền);
    //  • TRÙNG một dòng khác — hai dòng cùng một cột lúc so khớp, nên một trong hai biến mất khỏi bảng và
    //    người dùng không biết cột nào còn hiệu lực.
    function renamePermissionRole(nameCell) {
        const row = nameCell.closest(".permrole-row");
        const msgEl = document.getElementById("permissionMatrixMsg");
        const note = text => { if (msgEl) msgEl.textContent = text; };

        const previous = (nameCell.dataset.prev || "").trim();
        let value = tableValue(row, ".permrole-name");

        const duplicated = value.length > 0
            && Array.from(permMapPanel.querySelectorAll(".permrole-row")).some(other =>
                other !== row && normalizePermRole(tableValue(other, ".permrole-name")) === normalizePermRole(value));

        if (duplicated) {
            note(`“${value}” đã có trong bảng vai trò rồi.`);
            value = previous;
        } else if (value.length === 0 && previous.length > 0) {
            note("Tên vai trò không được để trống — muốn bỏ hẳn thì bấm × ở cuối dòng.");
            value = previous;
        } else {
            note("");
        }

        if (nameCell.value !== value) {
            nameCell.value = value;
            autoGrowCell(nameCell);
        }
        nameCell.dataset.prev = value;

        if (value !== previous) syncPermissionRoles(previous, value);
    }

    // ==== BA BẢNG CHỐT còn lại: LUỒNG → MÀN HÌNH → ĐỐI TƯỢNG ====
    // Cùng khuôn hai bước với bảng cột và bảng phân quyền: (1) POST lưu bảng, (2) gửi tin nhắn mà SERVER
    // soạn qua đúng đường chat thường. Tin nhắn lấy từ response chứ không ghép ở đây, vì nó phải khớp bảng
    // đã được server chuẩn hoá VÀ lưu — hai bản lệch nhau thì hội thoại kể một đằng còn dữ liệu dự án ghi
    // một nẻo, mà mọi tầng chắt lọc tin vào bản kể.
    //
    // Phần gửi giống nhau ở cả ba nên nó là MỘT hàm: ba bản sao của cùng đoạn xử lý lỗi là ba chỗ để một
    // bản quên mất luật "lưu hỏng thì DỪNG hẳn, không gửi tin nhắn".
    //
    // `validate` (tùy chọn) chạy TRƯỚC khi gửi và trả false để chặn lượt gửi. Nó tự lo phần giao diện của
    // mình (bảng thông báo mở một popup bắt chọn người nhận), vì một câu chữ nhỏ cạnh nút thì người dùng
    // đang rà 24 dòng không thấy. Chốt chặn THẬT vẫn ở server — xem ConfirmNotificationMapUseCase.
    // `extras` (tùy chọn): các trường form ĐI CÙNG CHUYẾN với bảng, trả về dạng { tên: chuỗi }. Sinh ra cho
    // bảng thông báo, nơi danh sách người nhận vừa là thứ được lưu vừa là bộ mà server đối chiếu hai ô
    // To/CC — gửi ở một lượt riêng thì có đúng một khoảnh khắc bảng đã lưu mà danh sách thì chưa.
    function initTablePanel(panelId, btnId, msgId, field, collect, savingText, errorText, validate, extras) {
        const panel = document.getElementById(panelId);
        if (!panel) return null;

        panel.addEventListener("click", async function (e) {
            if (!e.target.closest("#" + btnId) || chatBusy) return;

            const btn = document.getElementById(btnId);
            const msgEl = document.getElementById(msgId);
            const rows = panel.hidden ? [] : collect(panel);
            if (rows.length === 0) return;

            if (validate && !validate(panel, rows)) return;

            btn.disabled = true;
            msgEl.textContent = savingText;

            let message = "";
            try {
                const fd = new FormData();
                fd.append("projectId", window.REQUIREMENTS_PROJECT_ID || "");
                const tokenEl = document.querySelector('input[name="__RequestVerificationToken"]');
                fd.append("__RequestVerificationToken", tokenEl ? tokenEl.value : "");
                fd.append(field, JSON.stringify(rows));
                const extraFields = extras ? extras(panel) : null;
                Object.keys(extraFields || {}).forEach(name => fd.append(name, extraFields[name]));

                const resp = await fetch(panel.dataset.confirmUrl, { method: "POST", body: fd });
                const data = await resp.json();
                if (!data.ok || !data.message) throw new Error(data.error || "");
                message = data.message;
            } catch (err) {
                // Lưu hỏng thì DỪNG hẳn, không gửi tin nhắn — cùng lý do với bảng cột và bảng phân quyền:
                // hội thoại ghi nhận "đã chốt" trong khi dự án vẫn trống là trạng thái tệ nhất, vì bản đồ
                // bao phủ nâng nhóm lên [RÕ] dựa trên tin nhắn đó rồi cấm hỏi lại, còn bảng thì không ai
                // còn thấy đâu để rà lại.
                //
                // Server GỌI TÊN được chỗ hỏng (bảng vi phạm bất biến, không phải mạng chập) thì in đúng câu
                // đó: câu "bấm gửi lại giúp mình" ở ca ấy mời người dùng bấm lại đúng cái vừa bị từ chối.
                btn.disabled = false;
                msgEl.textContent = (err && err.message) ? err.message : errorText;
                return;
            }

            panel.hidden = true;
            panel.innerHTML = "";
            messageInput.value = message;
            chatForm.requestSubmit();
        });

        return panel;
    }

    // Cờ giữ/bỏ của một dòng không phải lúc nào cũng là checkbox: dòng ĐÃ KHÓA của bảng phân quyền / đối
    // tượng render thành input hidden value="1" (chỗ của ô tích là dấu ✓), và cả bảng LUỒNG cũng dùng input
    // ẩn vì cột tích ở đó đã được thay bằng nút ×. Đọc mỗi `.checked` thì đúng những dòng ấy gửi đi ở trạng
    // thái BỊ LOẠI — tức người dùng vô tình loại sạch thứ họ chưa đụng tới.
    function tableChecked(el) {
        if (!el) return false;
        return el.type === "checkbox" ? el.checked : (el.value || "") === "1";
    }

    // Ô sửa của ba bảng chốt là TEXTAREA cao theo nội dung, không phải input một dòng — lý do ở
    // requirements.css, mục .permmap-cellinput. Phải tính lại chiều cao SAU khi panel hiện ra: lúc còn
    // `hidden` thì scrollHeight bằng 0 và mọi ô sẽ dẹt lại thành một dòng.
    function autoGrowCell(el) {
        el.style.height = "auto";
        // Cộng bề dày hai viền: scrollHeight KHÔNG tính viền, mà box-sizing của ô là border-box nên gán
        // thẳng scrollHeight là ô luôn hụt đúng 2px và dòng cuối bị cắt mất một vệt chân chữ.
        el.style.height = `${el.scrollHeight + el.offsetHeight - el.clientHeight}px`;
    }

    function autoGrowCells(root) {
        (root || document).querySelectorAll(".permmap-cellinput").forEach(autoGrowCell);
    }

    document.addEventListener("input", function (e) {
        if (e.target.classList && e.target.classList.contains("permmap-cellinput")) autoGrowCell(e.target);
    });

    // Bảng hẹp lại thì chữ xuống thêm dòng, mà chiều cao thì vẫn là chiều cao tính ở bề rộng cũ — tức là
    // chữ bị cắt đúng như hồi còn dùng input. Gom vào một khung hình để kéo cạnh cửa sổ không tính lại
    // hàng chục ô mỗi pixel.
    let autoGrowFrame = 0;
    window.addEventListener("resize", function () {
        if (autoGrowFrame) return;
        autoGrowFrame = requestAnimationFrame(function () {
            autoGrowFrame = 0;
            autoGrowCells();
        });
    });

    autoGrowCells(); // bản server render đã có sẵn trong DOM lúc nạp trang

    // Ô là textarea nên người dùng gõ được xuống dòng, nhưng mọi ô ở đây (trừ "phục vụ bước", xem chỗ gom
    // bảng màn hình) là MỘT giá trị: textarea chỉ để chữ tự xuống dòng cho dễ đọc, không phải để soạn đoạn
    // văn. Nuốt xuống dòng ngay lúc gom để tin nhắn kể lại và tài liệu không lĩnh một đoạn xuống dòng giữa
    // câu.
    function tableValue(root, selector) {
        return tableRawValue(root, selector).replace(/\s*\n\s*/g, " ");
    }

    function tableRawValue(root, selector) {
        const el = root.querySelector(selector);
        return ((el || {}).value || "").trim();
    }

    // ---- BẢNG LUỒNG ----
    // Trần, chép từ FlowMapBuilder. Chặn ở CLIENT chứ không để server cắt — cùng lý do với bảng màn hình,
    // mà ở bảng này còn kín hơn: NormalizeSteps đếm CẢ bước đã bỏ rồi `break` khi chạm trần, nên một bước
    // người dùng vừa gõ mà vượt trần sẽ biến mất lúc lưu không một lời nào nói vì sao. Chặn tại nút bấm thì
    // họ đọc được lý do ngay lúc bấm.
    const MAX_FLOW_STEPS = 10;              // = FlowMapBuilder.MaxStepsPerFlow
    const MIN_FLOW_STEPS = 2;               // = FlowMapBuilder.MinStepsPerFlow
    const MAX_FLOWS = 6;                    // = FlowMapBuilder.MaxFlows
    const MAX_EXCEPTION_FLOWS = 3;          // = FlowMapBuilder.MaxExceptionFlows
    const FLOW_KIND_HAPPY = "luồng chính";  // = FlowKind.Happy
    const FLOW_KIND_EXCEPTION = "ngoại lệ"; // = FlowKind.Exception

    const flowMapPanel = initTablePanel(
        "flowMapPanel", "flowMapSendBtn", "flowMapMsg", "flowJson",
        panel => Array.from(panel.querySelectorAll(".flowmap-block")).map(block => {
            // Luồng BA bày ra chở tên/loại/vai/điều kiện ở `data-*` và không sửa được — sửa tên ở đó là đổi
            // nhãn của một thứ model đề xuất mà vẫn kể lại là đề xuất của nó. Luồng NGƯỜI DÙNG tự thêm thì
            // bốn trường ấy là bốn ô gõ, nên đọc từ ô; cờ `addedByUser` đi kèm để tin nhắn gửi vào hội thoại
            // gọi tên được nó (xem FlowMapRow.AddedByUser).
            const table = block.querySelector(".flowmap-table");
            const added = !!block.querySelector(".flowmap-nameinput");
            return {
                name: added ? tableValue(block, ".flowmap-nameinput") : (table.dataset.flow || ""),
                kind: added ? tableRawValue(block, ".flowmap-kindselect") : (table.dataset.kind || ""),
                role: added ? tableValue(block, ".flowmap-roleinput") : (table.dataset.role || ""),
                trigger: added ? tableValue(block, ".flowmap-triggerinput") : (table.dataset.trigger || ""),
                addedByUser: added,
                steps: Array.from(table.querySelectorAll(".flowmap-row")).map(tr => ({
                    actor: tableValue(tr, ".flowmap-actor"),
                    action: tableValue(tr, ".flowmap-action"),
                    outcome: tableValue(tr, ".flowmap-outcome"),
                    included: tableChecked(tr.querySelector(".flowmap-check")),
                    addedByUser: tr.dataset.added === "1"
                }))
            };
        }),
        "Đang lưu bảng luồng…",
        "Chưa lưu được bảng luồng — anh/chị bấm gửi lại giúp mình nhé.",
        validateFlowMap);

    // Nhãn dùng chung cho cả ba nút cuối dòng. Bước vừa thêm chưa có chữ nào để gọi tên, mà một aria-label
    // cụt ("Bỏ bước ") thì trình đọc màn hình đọc ra đúng như thế.
    function flowStepLabel(action) {
        return action ? ` bước ${action}` : " bước vừa thêm";
    }

    // Dựng nút qua DOM rồi lấy outerHTML: chữ người dùng gõ nằm trong cả `title` lẫn `aria-label`, ghép
    // thẳng vào chuỗi HTML là mở đúng cái lỗ mà escapeHtml sinh ra để bịt.
    function flowIconButton(className, text, label, attrs) {
        const btn = document.createElement("button");
        btn.type = "button";
        btn.className = className;
        btn.textContent = text;
        btn.title = label;
        btn.setAttribute("aria-label", label);
        Object.keys(attrs || {}).forEach(name => btn.setAttribute(name, attrs[name]));
        return btn.outerHTML;
    }

    // Nút BỎ/LẤY LẠI của một bước BA đề xuất. Hai trạng thái dùng chung MỘT nút vì chúng là một thao tác
    // lật; mọc thêm một nút "hoàn tác" riêng ở dòng đã bỏ sẽ làm cột cuối đổi bề rộng theo từng cú bấm.
    function applyFlowStepDropState(btn, included, action) {
        const label = (included ? "Bỏ" : "Lấy lại") + flowStepLabel(action);
        btn.textContent = included ? "×" : "↩";
        btn.title = label;
        btn.setAttribute("aria-label", label);
    }

    // Cột cuối của một dòng: ↑ ↓ rồi tới nút bỏ/xóa.
    //
    // Ba nút chứ không phải một tay cầm KÉO-THẢ, và đó là quyết định chứ không phải bước rút gọn: ô của
    // bảng này là <textarea>, nên đặt `draggable` lên <tr> là cướp mất thao tác bôi đen chữ trong ô — đúng
    // thao tác chính của cả bảng. Tránh nó thì phải đẻ thêm một CỘT tay cầm trên bảng cố ý chỉ có ba cột,
    // mà kéo-thả lại không dùng được bằng bàn phím và không chạy trên cảm ứng nếu không kèm polyfill. Một
    // luồng dài tối đa MAX_FLOW_STEPS bước và thường chỉ lệch một hai vị trí, tức ↑ ↓ là một hai cú bấm.
    function flowStepControls(step, added) {
        const action = (step && step.action) || "";
        const included = !step || step.included !== false;
        const label = flowStepLabel(action);
        const move = flowIconButton("flowmap-move", "↑", "Đưa lên trên" + label, { "data-dir": "up" })
            + flowIconButton("flowmap-move", "↓", "Đưa xuống dưới" + label, { "data-dir": "down" });

        // Bước BA đề xuất: nút LẬT — dòng bị bỏ vẫn phải nằm trong payload để tin nhắn gửi đi gọi tên được
        // nó. Bước NGƯỜI DÙNG vừa gõ: xóa hẳn, vì nó chưa bao giờ là một đề xuất nên không có gì để kể lại,
        // và bắt họ "bỏ" một dòng chính họ vừa tạo ra thì dòng trống ấy nằm lại trên bảng mãi.
        const drop = added
            ? flowIconButton("flowmap-remove", "×", "Xóa" + label)
            : flowIconButton("flowmap-del", included ? "×" : "↩", (included ? "Bỏ" : "Lấy lại") + label);

        return `<input type="hidden" class="flowmap-check" value="${included ? "1" : "0"}" />${move}${drop}`;
    }

    // MỘT bước. `step` null = dòng TRỐNG người dùng vừa thêm bằng nút "+ thêm bước".
    function flowStepRow(step, added) {
        const s = step || {};
        const included = s.included !== false;
        return `
            <tr class="flowmap-row${included ? "" : " flowmap-row-dropped"}"${added ? ' data-added="1"' : ""}>
                <td><textarea rows="1" class="permmap-cellinput flowmap-actor" placeholder="ai làm bước này?">${escapeHtml(s.actor || "")}</textarea></td>
                <td><textarea rows="1" class="permmap-cellinput flowmap-action" placeholder="bước này làm gì?">${escapeHtml(s.action || "")}</textarea></td>
                <td><textarea rows="1" class="permmap-cellinput flowmap-outcome" placeholder="trạng thái sau bước (nếu có)">${escapeHtml(s.outcome || "")}</textarea></td>
                <td class="flowmap-delcell">${flowStepControls(s, added)}</td>
            </tr>`;
    }

    // Dòng cuối mỗi luồng: nút thêm bước. Tên luồng chỉ nằm ở nhãn trợ năng, không nằm trong chữ của nút —
    // cùng lý do với "+ thêm chức năng" của bảng màn hình: mọi ô của .permmap-table là nowrap.
    function flowStepAddRow(name) {
        const label = name ? `Thêm bước cho luồng ${name}` : "Thêm bước cho luồng vừa thêm";
        return `
            <tr class="flowmap-addsteprow">
                <td colspan="4">${flowIconButton("flowmap-add flowmap-addstep", "+ thêm bước", label)}</td>
            </tr>`;
    }

    function flowTable(name, kind, role, trigger, body) {
        return `
                <table class="permmap-table flowmap-table" data-flow="${escapeHtml(name)}" data-kind="${escapeHtml(kind)}"
                       data-role="${escapeHtml(role)}" data-trigger="${escapeHtml(trigger)}">
                    <thead>
                        <tr>
                            <th class="flowmap-th-actor">Ai làm</th>
                            <th class="flowmap-th-action">Làm gì</th>
                            <th class="flowmap-th-outcome">Sau đó</th>
                            <th class="flowmap-th-del"></th>
                        </tr>
                    </thead>
                    <tbody>${body}</tbody>
                </table>`;
    }

    // Luồng BA bày ra: tiêu đề chỉ-đọc + bảng bước.
    function flowBlock(flow) {
        const name = flow.name || "";
        const kind = flow.kind || FLOW_KIND_HAPPY;
        const role = flow.role ? `<span class="flowmap-role">· ${escapeHtml(flow.role)}</span>` : "";
        const trigger = flow.trigger ? `<span class="flowmap-role">· khi ${escapeHtml(flow.trigger)}</span>` : "";
        const body = (flow.steps || []).map(s => flowStepRow(s, false)).join("") + flowStepAddRow(name);
        return `
            <div class="flowmap-block">
                <div class="permmap-screen">${escapeHtml(name)} <span class="flowmap-kind">${escapeHtml(kind)}</span>${role}${trigger}</div>
                ${flowTable(name, kind, flow.role || "", flow.trigger || "", body)}
            </div>`;
    }

    // Luồng NGƯỜI DÙNG tự thêm: bốn trường của tiêu đề thành bốn ô gõ, và bảng được gieo sẵn đúng
    // MIN_FLOW_STEPS dòng trống — luồng ít bước hơn thế bị server loại, nên gieo sẵn là cách nói ra cái
    // luật đó mà không bắt họ khám phá ra nó bằng cách mất một luồng vừa gõ.
    //
    // Ô "kích hoạt khi" chỉ hiện với NGOẠI LỆ: luồng chính không có điều kiện kích hoạt nào ngoài chính
    // việc người dùng bắt đầu nó (server cũng xóa trắng ô này ở luồng chính), nên bày nó ra là bày một ô mà
    // người ta phải đoán xem mình nên điền gì.
    function flowNewBlock() {
        const steps = Array.from({ length: MIN_FLOW_STEPS }, () => flowStepRow(null, true)).join("");
        return `
            <div class="flowmap-block">
                <div class="permmap-screen flowmap-newhead">
                    <textarea rows="1" class="permmap-cellinput flowmap-nameinput" placeholder="tên luồng…" aria-label="Tên luồng vừa thêm"></textarea>
                    <select class="flowmap-kindselect" aria-label="Loại của luồng vừa thêm">
                        <option value="${escapeHtml(FLOW_KIND_HAPPY)}">${escapeHtml(FLOW_KIND_HAPPY)}</option>
                        <option value="${escapeHtml(FLOW_KIND_EXCEPTION)}">${escapeHtml(FLOW_KIND_EXCEPTION)}</option>
                    </select>
                    <textarea rows="1" class="permmap-cellinput flowmap-roleinput" placeholder="ai khởi xướng luồng này?" aria-label="Vai trò khởi xướng luồng vừa thêm"></textarea>
                    <textarea rows="1" class="permmap-cellinput flowmap-triggerinput" placeholder="kích hoạt khi…" aria-label="Điều kiện kích hoạt luồng vừa thêm" hidden></textarea>
                    ${flowIconButton("flowmap-delflow", "×", "Xóa luồng vừa thêm")}
                </div>
                ${flowTable("", FLOW_KIND_HAPPY, "", "", steps + flowStepAddRow(""))}
            </div>`;
    }

    // Markup khớp bản server render trong Index.cshtml — hai đường lệch nhau thì người dùng rà xong bảng
    // vừa hiện ra rồi F5 và thấy một bảng khác.
    function renderFlowMap(rows) {
        if (!flowMapPanel || !Array.isArray(rows) || rows.length === 0) return;

        flowMapPanel.innerHTML = `
            <div class="permmap-howto">
                Đây là các luồng <b>mình ráp lại</b> từ những gì anh/chị đã kể — bước nào sai thì sửa thẳng vào ô,
                bước nào không có thật thì bấm <b>×</b> ở cuối dòng để bỏ (bấm lại để lấy về). Sai thứ tự thì bấm
                <b>↑ ↓</b>, thiếu bước thì bấm <b>+ thêm bước</b> ở cuối luồng, thiếu hẳn một luồng thì bấm
                <b>+ thêm luồng</b> ở cuối bảng.
            </div>
            ${rows.map(flowBlock).join("")}
            <div class="flowmap-addflowrow">
                ${flowIconButton("flowmap-add flowmap-addflow", "+ thêm luồng", "Thêm một luồng mới vào bảng")}
            </div>
            <div class="permmap-bar">
                <button type="button" class="btn primary" id="flowMapSendBtn">Gửi bảng luồng</button>
                <div class="permmap-hint">
                    Muốn mình tự dựng thêm một luồng hay một tình huống hỏng từ đầu, anh/chị cứ gõ vào khung chat — mình bổ sung rồi bày lại bảng.
                </div>
                <div class="permmap-msg" id="flowMapMsg"></div>
            </div>`;
        flowMapPanel.hidden = false;
        thinkingBox.before(flowMapPanel);
        autoGrowCells(flowMapPanel);
        refreshFlowMoves();
    }

    // ↑ của dòng đầu và ↓ của dòng cuối bị KHÓA chứ không để bấm rồi không có gì xảy ra: một nút bấm được
    // mà không làm gì đọc như một lỗi vừa xảy ra. Chạy lại sau MỌI lần thêm/xóa/đổi chỗ.
    function refreshFlowMoves() {
        if (!flowMapPanel) return;
        flowMapPanel.querySelectorAll(".flowmap-table tbody").forEach(function (tbody) {
            const rows = Array.from(tbody.querySelectorAll(".flowmap-row"));
            rows.forEach(function (row, i) {
                const up = row.querySelector('.flowmap-move[data-dir="up"]');
                const down = row.querySelector('.flowmap-move[data-dir="down"]');
                if (up) up.disabled = i === 0;
                if (down) down.disabled = i === rows.length - 1;
            });
        });
    }

    function countExceptionFlows() {
        return Array.from(flowMapPanel.querySelectorAll(".flowmap-block")).filter(function (block) {
            const select = block.querySelector(".flowmap-kindselect");
            const table = block.querySelector(".flowmap-table");
            return (select ? select.value : (table.dataset.kind || "")) === FLOW_KIND_EXCEPTION;
        }).length;
    }

    // Khóa so trùng tên luồng, chép luật của FlowMapBuilder.Normalize. Hai luồng trùng tên thì server giữ
    // cái đầu và bỏ IM LẶNG cái sau — mà cái sau gần như luôn là luồng người dùng vừa gõ.
    function flowNameKey(name) {
        return (name || "").toLowerCase()
            .split(/\s+/).filter(Boolean).join(" ")
            .replace(/^[.,:;\-–]+|[.,:;\-–]+$/g, "");
    }

    function focusFlowField(block, selector) {
        const field = block && block.querySelector(selector);
        if (field) field.focus();
    }

    // Chốt chặn phía CLIENT cho đúng những gì server sẽ lặng lẽ bỏ đi: luồng không tên, luồng chưa đủ
    // MIN_FLOW_STEPS bước, luồng trùng tên. Cả ba đều bị `BuildCore` `continue` qua không một lời nào — mà
    // người dùng thì vừa gõ tay cả luồng đó, và bảng biến mất ngay sau khi gửi nên họ cũng không còn chỗ
    // nào để thấy là mình đã mất gì.
    function validateFlowMap(panel, rows) {
        const msgEl = document.getElementById("flowMapMsg");
        const note = text => { if (msgEl) msgEl.textContent = text; };
        const blocks = Array.from(panel.querySelectorAll(".flowmap-block"));
        const seen = new Set();

        for (let i = 0; i < rows.length; i++) {
            const row = rows[i];
            const block = blocks[i];
            const key = flowNameKey(row.name);

            if (row.addedByUser && key.length === 0) {
                note("Luồng anh/chị vừa thêm chưa có tên — điền tên giúp mình, hoặc bấm × ở đầu luồng để bỏ nó đi.");
                focusFlowField(block, ".flowmap-nameinput");
                return false;
            }

            if (row.addedByUser && row.steps.filter(s => s.action.length > 0).length < MIN_FLOW_STEPS) {
                note(`Luồng "${row.name}" chưa đủ ${MIN_FLOW_STEPS} bước có nội dung — ít hơn thế thì đó là một câu mô tả `
                    + `chứ chưa phải một luồng, và bản demo sẽ không có gì để chấm theo.`);
                focusFlowField(block, ".flowmap-action");
                return false;
            }

            if (key.length > 0 && seen.has(key)) {
                note(`Đang có hai luồng cùng tên "${row.name}" — đặt cho luồng vừa thêm một cái tên khác giúp mình nhé.`);
                focusFlowField(block, ".flowmap-nameinput");
                return false;
            }
            seen.add(key);
        }

        note("");
        return true;
    }

    // Ủy quyền trên PANEL chứ không gắn vào từng nút: renderFlowMap thay sạch innerHTML mỗi lượt BA bày
    // bảng, nên listener gắn vào nút sẽ chết ngay lần render kế. Panel thì sống suốt phiên.
    if (flowMapPanel) {
        flowMapPanel.addEventListener("click", function (e) {
            const toggle = e.target.closest(".flowmap-del");
            const removeStep = e.target.closest(".flowmap-remove");
            const move = e.target.closest(".flowmap-move");
            const addStep = e.target.closest(".flowmap-addstep");
            const addFlow = e.target.closest(".flowmap-addflow");
            const removeFlow = e.target.closest(".flowmap-delflow");
            if (!toggle && !removeStep && !move && !addStep && !addFlow && !removeFlow) return;

            const msgEl = document.getElementById("flowMapMsg");
            const note = text => { if (msgEl) msgEl.textContent = text; };

            // Bấm × ở một bước BA đề xuất là ĐÁNH DẤU bỏ chứ không xóa dòng khỏi bảng: payload vẫn phải chở
            // bước đó thì tin nhắn server soạn mới gọi tên được nó ("(bỏ: …)"), và dòng còn nằm đó — mờ đi,
            // gạch ngang — mới cho người dùng nhìn lướt thấy ngay mình vừa loại những gì. Bấm lần nữa lấy
            // về: một thao tác loại mà không hoàn tác được tại chỗ thì cú bấm nhầm chỉ sửa được bằng cách
            // gõ tay lại cả bước.
            if (toggle) {
                const row = toggle.closest(".flowmap-row");
                const flag = row.querySelector(".flowmap-check");
                const included = flag.value !== "1";
                flag.value = included ? "1" : "0";
                row.classList.toggle("flowmap-row-dropped", !included);
                applyFlowStepDropState(toggle, included, tableValue(row, ".flowmap-action"));
                note("");
                return;
            }

            if (removeStep) {
                removeStep.closest(".flowmap-row").remove();
                refreshFlowMoves();
                note("");
                return;
            }

            if (removeFlow) {
                removeFlow.closest(".flowmap-block").remove();
                note("");
                return;
            }

            if (move) {
                const row = move.closest(".flowmap-row");
                const dir = move.dataset.dir;
                const sibling = dir === "up" ? row.previousElementSibling : row.nextElementSibling;
                // Dòng "+ thêm bước" cũng là một <tr>: đi tới nó là đẩy bước xuống dưới cái nút thêm.
                if (!sibling || !sibling.classList.contains("flowmap-row")) return;

                if (dir === "up") sibling.before(row); else sibling.after(row);
                refreshFlowMoves();
                // Chèn lại một node là mất focus, mà đổi chỗ thì hiếm khi chỉ một nhịp: không trả focus về
                // thì cú bấm thứ hai phải đi tìm lại đúng cái nút vừa bấm ở một dòng vừa nhảy chỗ. Nút vừa
                // bấm mà thành khóa (dòng đã chạm đầu/cuối) thì đưa focus sang nút còn lại của chính dòng đó.
                const back = move.disabled ? row.querySelector(".flowmap-move:not([disabled])") : move;
                if (back) back.focus();
                note("");
                return;
            }

            if (addStep) {
                const table = addStep.closest(".flowmap-table");
                const anchor = addStep.closest(".flowmap-addsteprow");
                // Đếm CẢ bước đã bỏ, đúng như NormalizeSteps đếm — chặn theo một con số khác con số server
                // dùng thì vẫn còn đúng cái đường bị nuốt im lặng mà trần này sinh ra để bịt.
                if (table.querySelectorAll(".flowmap-row").length >= MAX_FLOW_STEPS) {
                    note(`Một luồng chỉ nhận tối đa ${MAX_FLOW_STEPS} bước — dài hơn thì đó là bản mô tả thao tác `
                        + `chứ không còn là luồng nghiệp vụ. Anh/chị bỏ bớt bước không cần, hoặc tách phần sau thành một luồng riêng giúp mình nhé.`);
                    return;
                }

                anchor.insertAdjacentHTML("beforebegin", flowStepRow(null, true));
                focusNewRow(anchor.previousElementSibling, ".flowmap-actor");
                refreshFlowMoves();
                note("");
                return;
            }

            const anchor = addFlow.closest(".flowmap-addflowrow");
            if (flowMapPanel.querySelectorAll(".flowmap-block").length >= MAX_FLOWS) {
                note(`Bảng đã tới trần ${MAX_FLOWS} luồng — nhiều hơn thì không rà nổi trong một lượt. Anh/chị bỏ bớt `
                    + `một luồng vừa thêm, hoặc nhắn vào khung chat để mình gộp lại giúp nhé.`);
                return;
            }

            anchor.insertAdjacentHTML("beforebegin", flowNewBlock());
            const block = anchor.previousElementSibling;
            autoGrowCells(block);
            focusFlowField(block, ".flowmap-nameinput");
            refreshFlowMoves();
            note("");
        });

        flowMapPanel.addEventListener("change", function (e) {
            const select = e.target.closest(".flowmap-kindselect");
            if (!select) return;

            const block = select.closest(".flowmap-block");
            const msgEl = document.getElementById("flowMapMsg");

            // Trần NGOẠI LỆ chặn ngay tại chỗ chọn, không đợi lúc gửi: quá trần thì `BuildCore` bỏ hẳn luồng
            // thứ tư, mà ngoại lệ là phần khó lấy nhất của cả buổi phỏng vấn.
            if (select.value === FLOW_KIND_EXCEPTION && countExceptionFlows() > MAX_EXCEPTION_FLOWS) {
                select.value = FLOW_KIND_HAPPY;
                if (msgEl) {
                    msgEl.textContent = `Bảng chỉ nhận tối đa ${MAX_EXCEPTION_FLOWS} luồng ngoại lệ — anh/chị gộp `
                        + `tình huống này vào một ngoại lệ đã có, hoặc nhắn vào khung chat giúp mình nhé.`;
                }
            } else if (msgEl) {
                msgEl.textContent = "";
            }

            // CSS đọc `data-kind` của BẢNG để đổi viền và nhãn loại, nên nó phải chạy theo ô chọn.
            block.querySelector(".flowmap-table").dataset.kind = select.value;

            const triggerEl = block.querySelector(".flowmap-triggerinput");
            if (triggerEl) {
                triggerEl.hidden = select.value !== FLOW_KIND_EXCEPTION;
                if (!triggerEl.hidden) autoGrowCell(triggerEl);
            }
        });

        // Bản server render đã nằm sẵn trong DOM lúc nạp trang, tức nó chưa đi qua renderFlowMap nào —
        // không gọi ở đây thì ↑ của dòng đầu và ↓ của dòng cuối bấm được cho tới cú bấm đầu tiên.
        refreshFlowMoves();
    }

    // ---- BẢNG MÀN HÌNH ----
    // Trần dòng, chép từ ScreenScopeMapBuilder. Chặn ở đây chứ không để server cắt: một dòng người dùng vừa
    // gõ mà bị nuốt lúc lưu là đúng loại quyết định câm mà cả bảng này sinh ra để chặn — chặn tại nút bấm
    // thì họ đọc được lý do ngay lúc bấm.
    const MAX_SCREEN_ROWS = 40;           // = ScreenScopeMapBuilder.MaxRows
    const MAX_SCREEN_FUNCTIONS = 12;      // = ScreenScopeMapBuilder.MaxFunctionsPerScreen

    const screenScopePanel = initTablePanel(
        "screenScopePanel", "screenScopeSendBtn", "screenScopeMsg", "screensJson",
        panel => Array.from(panel.querySelectorAll(".screenmap-row")).map(tr => {
            // Dòng BA bày ra có tên nằm ở `data-screen` và KHÔNG sửa được: tên là khóa nối sang bảng phân
            // quyền và sang các màn của bản demo, sửa chữ ở đây là làm dòng trượt khỏi chốt chặn ở server.
            // Dòng người dùng TỰ THÊM thì ngược lại — nó chưa có tên nào để nối, nên ô tên là ô gõ, và cờ
            // `addedByUser` là thứ cho phép nó đi qua chốt chặn đó (xem ScreenScopeRow.AddedByUser).
            const nameInput = tr.querySelector(".screenmap-nameinput");
            return {
                screen: nameInput ? tableValue(tr, ".screenmap-nameinput") : (tr.dataset.screen || ""),
                addedByUser: !!nameInput,
                purpose: tableValue(tr, ".screenmap-purpose"),
                // MỖI CHỨC NĂNG MỘT DÒNG CON, mỗi dòng một ô tích. Dòng thêm bằng nút "+ thêm chức năng" mà
                // không gõ gì thì `name` rỗng và server bỏ nó đi.
                functions: Array.from(tr.querySelectorAll(".screenmap-fn-row")).map(fn => ({
                    name: tableValue(fn, ".screenfn-name"),
                    // Ô "phục vụ bước" là MỘT ô text ngăn bằng dấu chấm phẩy, không phải danh sách con: người
                    // dùng gõ tiếp vào đó dễ hơn nhiều so với bấm thêm dòng, và phép kiểm phía server so khớp
                    // theo cụm chứa-nhau nên không cần từng bước là một phần tử riêng. Ô là textarea nên XUỐNG
                    // DÒNG cũng là dấu ngăn: đó là cách gõ danh sách tự nhiên nhất khi ô cao được, và nếu chỉ
                    // tách theo dấu chấm phẩy thì mấy bước gõ mỗi dòng một cái sẽ dính thành một bước dài vô nghĩa.
                    flowSteps: tableRawValue(fn, ".screenfn-steps").split(/[;\n]/).map(s => s.trim()).filter(Boolean),
                    included: tableChecked(fn.querySelector(".screenfn-check"))
                })).filter(fn => fn.name.length > 0),
                // Các mục phạm vi đã được gộp vào màn này. Người dùng không sửa được chúng ở đây (chúng đã
                // hiện thành dòng chú thích dưới tên màn), nhưng payload vẫn phải chở đi: mất chúng là mục đã
                // gộp mọc lại thành một màn hình riêng ở lượt sau. Xem ScreenScopeMapBuilder.EffectiveScreens.
                covers: (tr.dataset.covers || "").split("|").map(s => s.trim()).filter(Boolean),
                included: tableChecked(tr.querySelector(".screenmap-check"))
            };
        // Dòng tự thêm mà bỏ trống tên không phải một màn hình — bỏ ngay ở đây cho payload sạch (server
        // cũng bỏ, nhưng để nó đi hết một vòng chỉ làm khó việc đọc lỗi khi có gì đó lệch).
        }).filter(r => r.screen.length > 0),
        "Đang lưu bảng màn hình…",
        "Chưa lưu được bảng màn hình — anh/chị bấm gửi lại giúp mình nhé.");

    // Một dòng chức năng. `f` null = dòng TRỐNG người dùng vừa thêm bằng nút "+ thêm chức năng".
    //
    // `removable` quyết định dòng có nút xóa hay không, và ranh giới đó là chủ ý: chức năng BA đề xuất thì
    // BỎ TÍCH chứ không xóa — dòng bị loại vẫn phải kể lại được trong tin nhắn gửi đi, nếu không người dùng
    // không có bằng chứng nào cho thấy mình vừa loại đúng thứ định loại. Dòng do CHÍNH HỌ vừa thêm thì
    // không có gì để kể lại: nó chưa bao giờ là một đề xuất, nên xóa hẳn mới là thao tác đúng.
    function screenFunctionRow(f, removable) {
        const name = f ? (f.name || "") : "";
        const steps = f ? (f.flowSteps || []).join("; ") : "";
        const checked = f ? (f.included ? " checked" : "") : " checked";
        // Cột đầu CHỈ có ô tích. Bảng này không mang dấu ✓ bằng chứng như bảng phân quyền — xem
        // requirement-flow.md, mục "Vì sao bảng màn hình không có dấu ✓ bằng chứng".
        return `
            <tr class="screenmap-fn-row">
                <td class="flowmap-use">
                    <input type="checkbox" class="screenfn-check" aria-label="Cần chức năng ${escapeHtml(name)}"${checked} />
                </td>
                <td><textarea rows="1" class="permmap-cellinput screenfn-name" placeholder="chức năng">${escapeHtml(name)}</textarea></td>
                <td><textarea rows="1" class="permmap-cellinput screenfn-steps" placeholder="chức năng này phụ trách bước nào?">${escapeHtml(steps)}</textarea></td>
                <td class="screenmap-delcell">${removable ? screenScopeDeleteButton("Xóa chức năng này") : ""}</td>
            </tr>`;
    }

    // Dòng cuối của mỗi bảng con: nút thêm chức năng. `colspan` phủ cả bốn cột để nút bám mép trái, thẳng
    // hàng với ô tích của các dòng chức năng bên trên.
    //
    // Tên màn hình chỉ nằm ở `aria-label`, không nằm trong chữ của nút: mọi ô của .permmap-table là nowrap,
    // nên một nhãn dài bằng cả tên màn hình sẽ nong bảng con ra và đẩy cả bảng lớn ra ngoài vùng nhìn thấy.
    function screenFunctionAddRow(screen) {
        const label = screen ? `Thêm chức năng cho ${screen}` : "Thêm chức năng cho màn hình vừa thêm";
        return `
            <tr class="screenmap-fn-add">
                <td colspan="4">
                    <button type="button" class="screenmap-add screenmap-addfn" aria-label="${escapeHtml(label)}">+ thêm chức năng</button>
                </td>
            </tr>`;
    }

    function screenScopeDeleteButton(label) {
        return `<button type="button" class="screenmap-del" title="${escapeHtml(label)}" aria-label="${escapeHtml(label)}">×</button>`;
    }

    // MỘT dòng màn hình. `r` null = dòng TRỐNG người dùng vừa thêm bằng nút "+ thêm màn hình" — nó có ô tên
    // gõ được (dòng BA bày ra thì không, xem phần gom bảng) và nút xóa.
    function screenScopeRow(r) {
        const covers = r ? (r.covers || []).filter(Boolean) : [];
        const screen = r ? (r.screen || "") : "";
        const nameCell = r
            ? `<div class="screenmap-name">${escapeHtml(screen)}</div>`
            : `<textarea rows="1" class="permmap-cellinput screenmap-nameinput" placeholder="tên màn hình…"></textarea>`;
        return `
            <tr class="screenmap-row" data-screen="${escapeHtml(screen)}" data-covers="${escapeHtml(covers.join("|"))}">
                <td class="flowmap-use">
                    <input type="checkbox" class="screenmap-check" aria-label="Cần màn hình ${r ? escapeHtml(screen) : "vừa thêm"}"${!r || r.included ? " checked" : ""} />
                </td>
                <td class="permmap-fn">
                    ${nameCell}
                    <textarea rows="1" class="permmap-cellinput screenmap-purpose" placeholder="màn này để làm gì?">${escapeHtml(r ? (r.purpose || "") : "")}</textarea>
                    ${covers.length > 0 ? `<div class="screenmap-covers">gộp vào màn này: ${escapeHtml(covers.join(", "))}</div>` : ""}
                </td>
                <td class="screenmap-fncell">
                    <table class="screenmap-fntable">
                        <tbody>${r ? (r.functions || []).map(f => screenFunctionRow(f, false)).join("") : ""}${screenFunctionAddRow(screen)}</tbody>
                    </table>
                </td>
                <td class="screenmap-delcell">${r ? "" : screenScopeDeleteButton("Xóa màn hình này")}</td>
            </tr>`;
    }

    function renderScreenScope(rows, uncovered) {
        if (!screenScopePanel || !Array.isArray(rows) || rows.length === 0) return;

        const body = rows.map(screenScopeRow).join("");

        const steps = Array.isArray(uncovered) ? uncovered : [];
        screenScopePanel.innerHTML = `
            <div class="permmap-howto">
                Đây là các màn hình mình dự kiến dựng và các chức năng trên từng màn. Màn nào <b>không cần</b>
                thì bỏ tích ở cột đầu; chức năng nào không cần thì bỏ tích ngay dòng của nó. Thiếu chức năng
                nào thì bấm <b>+ thêm chức năng</b> ở cuối màn đó, thiếu cả một màn hình thì bấm
                <b>+ thêm màn hình</b> ở cuối bảng.
            </div>
            <table class="permmap-table screenmap-table">
                <thead>
                    <tr>
                        <th class="flowmap-th-use">Cần</th>
                        <th class="screenmap-th-name">Màn hình</th>
                        <th class="screenmap-th-fn">Chức năng <span class="screenmap-th-note">· cột phải: bước luồng chức năng đó phụ trách</span></th>
                        <th class="screenmap-th-del"></th>
                    </tr>
                </thead>
                <tbody>${body}
                    <tr class="screenmap-addrow">
                        <td colspan="4"><button type="button" class="screenmap-add screenmap-addscreen">+ thêm màn hình</button></td>
                    </tr>
                </tbody>
            </table>
            <div class="screenmap-warn" id="screenScopeWarn"${steps.length > 0 ? "" : " hidden"}>
                Chưa chức năng nào phụ trách các bước: <b>${escapeHtml(steps.join("; "))}</b>.
                Anh/chị điền bước đó vào ô bên phải của chức năng phù hợp, hoặc nhắn cho mình biết nếu thiếu hẳn một màn hình.
            </div>
            <div class="permmap-bar">
                <button type="button" class="btn primary" id="screenScopeSendBtn">Gửi bảng màn hình</button>
                <div class="permmap-hint">
                    Muốn mô tả kỹ hơn một màn hình còn thiếu, anh/chị cứ gõ vào khung chat — mình bổ sung rồi bày lại bảng.
                </div>
                <div class="permmap-msg" id="screenScopeMsg"></div>
            </div>`;
        screenScopePanel.hidden = false;
        thinkingBox.before(screenScopePanel);
        autoGrowCells(screenScopePanel);
    }

    // THÊM/XÓA DÒNG NGAY TRÊN BẢNG. Trước đây bảng chỉ sửa được thứ BA đã bày ra: thiếu hẳn một màn hình thì
    // người dùng phải bỏ bảng đó lại, gõ vào khung chat, rồi chờ BA bày lại bảng khác — một vòng gọi LLM cho
    // một dòng họ đã biết chính xác mình muốn gì, và bảng bày lại thì không chắc giữ nguyên những ô họ vừa
    // điền. Hai nút này cắt vòng đó đi.
    //
    // Ủy quyền trên PANEL chứ không gắn vào từng nút: renderScreenScope thay sạch innerHTML mỗi lượt BA bày
    // bảng, nên listener gắn vào nút sẽ chết ngay lần render kế. Panel thì sống suốt phiên.
    if (screenScopePanel) {
        screenScopePanel.addEventListener("click", function (e) {
            const addScreen = e.target.closest(".screenmap-addscreen");
            const addFunction = e.target.closest(".screenmap-addfn");
            const remove = e.target.closest(".screenmap-del");
            if (!addScreen && !addFunction && !remove) return;

            const msgEl = document.getElementById("screenScopeMsg");
            const note = text => { if (msgEl) msgEl.textContent = text; };

            if (remove) {
                // Nút xóa của dòng chức năng nằm TRONG dòng màn hình chứa nó, nên phải hỏi dòng con trước.
                const row = remove.closest(".screenmap-fn-row") || remove.closest(".screenmap-row");
                if (row) row.remove();
                note("");
                return;
            }

            if (addScreen) {
                const tbody = screenScopePanel.querySelector(".screenmap-table tbody");
                const anchor = tbody && tbody.querySelector(".screenmap-addrow");
                if (!anchor) return;

                if (tbody.querySelectorAll(".screenmap-row").length >= MAX_SCREEN_ROWS) {
                    note(`Bảng đã tới trần ${MAX_SCREEN_ROWS} màn hình — anh/chị bỏ tích bớt một màn không cần trước khi thêm giúp mình nhé.`);
                    return;
                }

                anchor.insertAdjacentHTML("beforebegin", screenScopeRow(null));
                focusNewRow(anchor.previousElementSibling, ".screenmap-nameinput");
                note("");
                return;
            }

            const fnBody = addFunction.closest(".screenmap-fntable").querySelector("tbody");
            const fnAnchor = addFunction.closest(".screenmap-fn-add");
            if (fnBody.querySelectorAll(".screenmap-fn-row").length >= MAX_SCREEN_FUNCTIONS) {
                note(`Một màn hình chỉ nhận tối đa ${MAX_SCREEN_FUNCTIONS} chức năng — quá số đó thường là dấu hiệu đây là hai màn hình bị gộp làm một.`);
                return;
            }

            fnAnchor.insertAdjacentHTML("beforebegin", screenFunctionRow(null, true));
            focusNewRow(fnAnchor.previousElementSibling, ".screenfn-name");
            note("");
        });
    }

    // Ô của dòng vừa chèn chưa qua autoGrowCell nào (chiều cao do JS tính, xem autoGrowCells), và con trỏ
    // phải nhảy thẳng vào ô tên: bấm "thêm" rồi phải đi tìm chỗ gõ là một nhịp thừa ở đúng chỗ người dùng
    // đang gõ liên tục.
    function focusNewRow(row, selector) {
        if (!row) return;
        autoGrowCells(row);
        const field = row.querySelector(selector);
        if (field) field.focus();
    }

    // ---- BẢNG ĐỐI TƯỢNG NGHIỆP VỤ ----
    // Trần dòng, chép từ EntityMapBuilder. Chặn ở đây chứ không để server cắt — cùng lý do với bảng màn hình.
    const MAX_ENTITY_ROWS = 12;      // = EntityMapBuilder.MaxRows
    const MAX_ENTITY_FIELDS = 12;    // = EntityMapBuilder.MaxFieldsPerEntity
    const MAX_ENTITY_STATES = 8;     // = EntityMapBuilder.MaxStatesPerEntity
    const MAX_ENTITY_OPTIONS = 10;   // = EntityMapBuilder.MaxOptionsPerField

    // HAI TRỤC của một thông tin, tách hẳn nhau — xem EntityFieldInput / EntityFieldSource. Gộp chúng vào
    // một dropdown là đẻ ra đúng một ô không ai trả lời: "một danh sách" chưa nói được chọn MỘT hay chọn
    // NHIỀU, mà đó lại là thứ quyết định hình dạng ô nhập của bản demo.
    //
    // Nhãn viết bằng lời NGHIỆP VỤ chứ không phải từ vựng mô hình dữ liệu ("Gõ tay" chứ không phải "Text",
    // "Chọn 1" chứ không phải "Single Select"): cả bảng này dựng ra để người dùng nghiệp vụ rà được, và một
    // dropdown bằng tiếng kỹ thuật là chỗ họ chọn bừa nhanh nhất.
    const ENTITY_INPUTS = [
        { value: "text", label: "Gõ tay" },
        { value: "number", label: "Số" },
        { value: "date", label: "Ngày" },
        { value: "choice-one", label: "Chọn 1" },
        { value: "choice-many", label: "Chọn nhiều" },
        { value: "auto", label: "Ứng dụng tự sinh" }
    ];

    // Ô nguồn bỏ trống là HỢP LỆ và có nghĩa "chưa chốt" — server kể nó ra để BA hỏi nốt thay vì đoán thay
    // người dùng, nên mục đầu KHÔNG phải một giá trị mặc định trá hình.
    const ENTITY_SOURCES = [
        { value: "", label: "— chưa chọn —" },
        { value: "inline", label: "Nhập tại chỗ" },
        { value: "app", label: "Ứng dụng tự quản lý" },
        { value: "external", label: "Lấy từ hệ thống khác" }
    ];

    const isEntityChoice = input => input === "choice-one" || input === "choice-many";

    function entitySelect(cls, items, value, label) {
        const options = items.map(o =>
            `<option value="${escapeHtml(o.value)}"${o.value === value ? " selected" : ""}>${escapeHtml(o.label)}</option>`).join("");
        return `<select class="entityfield-select ${cls}" aria-label="${escapeHtml(label)}">${options}</select>`;
    }

    function entityOptionList(tr) {
        try {
            const parsed = JSON.parse(tr.dataset.options || "[]");
            return Array.isArray(parsed) ? parsed.filter(v => typeof v === "string" && v.trim().length > 0) : [];
        } catch (e) {
            return [];
        }
    }

    function setEntityOptionList(tr, values) {
        tr.dataset.options = JSON.stringify(values.slice(0, MAX_ENTITY_OPTIONS));
    }

    // Ô "danh sách lấy ở đâu" ĐỔI HÌNH theo kiểu nhập, vì phần lớn tổ hợp của hai trục không tồn tại: một
    // nguồn danh sách gắn vào ô gõ tay là ô người dùng phải đọc rồi bỏ qua, còn quy tắc sinh mã chỉ có nghĩa
    // với kiểu tự sinh. Ẩn thứ vô nghĩa đi là cách duy nhất giữ bảng này rà được: nó vốn đã là bảng dài nhất
    // và dễ đọc lướt nhất trong năm bảng.
    //
    // NGUỒN SỰ THẬT nằm ở `tr.dataset`, không ở các ô đang hiển thị: ô nào cũng có thể bị chính hàm này gỡ
    // khỏi DOM khi người dùng đổi dropdown, và đọc giá trị từ một ô vừa bị gỡ là mất đúng chữ họ vừa gõ. Các
    // ô chỉ ghi ngược vào dataset khi người dùng gõ/chọn.
    function renderEntityFieldSource(tr) {
        const cell = tr.querySelector(".entityfield-srccell");
        if (!cell) return;

        const input = tr.dataset.input || "text";
        if (input === "auto") {
            cell.innerHTML = `<textarea rows="1" class="permmap-cellinput entityfield-rule" aria-label="Quy tắc sinh" placeholder="quy tắc sinh, vd HcP-JD-XXX">${escapeHtml(tr.dataset.rule || "")}</textarea>`;
        } else if (!isEntityChoice(input)) {
            // Không phải ô chọn ⇒ không có danh sách nào để hỏi. Một gạch ngang mờ nói rõ "ô này không áp
            // dụng", khác hẳn một ô trống — thứ đọc lên như một câu hỏi chưa ai trả lời.
            cell.innerHTML = `<span class="entityfield-na" aria-hidden="true">—</span>`;
        } else {
            const source = tr.dataset.source || "";
            let html = entitySelect("entityfield-source", ENTITY_SOURCES, source, "Danh sách lấy ở đâu");

            if (source === "inline") {
                const values = entityOptionList(tr);
                const chips = values.map(v =>
                    `<span class="entityfield-chip">${escapeHtml(v)}<button type="button" class="entityfield-optdel" data-value="${escapeHtml(v)}" title="Bỏ giá trị này" aria-label="Bỏ giá trị ${escapeHtml(v)}">×</button></span>`).join("");
                html += `<div class="entityfield-options">${chips}`
                    + (values.length >= MAX_ENTITY_OPTIONS
                        ? `<span class="entityfield-optfull">Dài hơn ${MAX_ENTITY_OPTIONS} giá trị thì nên để ứng dụng tự quản lý.</span>`
                        : `<input type="text" class="entityfield-optadd" aria-label="Thêm một giá trị" placeholder="gõ giá trị rồi Enter…" />`)
                    + `</div>`;
            } else if (source === "external") {
                html += `<textarea rows="1" class="permmap-cellinput entityfield-system" aria-label="Tên hệ thống nguồn" placeholder="lấy từ hệ thống nào?">${escapeHtml(tr.dataset.system || "")}</textarea>`;
            }

            cell.innerHTML = html;
        }

        autoGrowCells(cell);
    }

    // Ô "bắt buộc" KHÓA LẠI khi thông tin bị bỏ tích "Lưu": hai ô tích cạnh nhau với nghĩa khác hẳn là chỗ
    // nhầm rẻ nhất của bảng, và "bắt buộc nhập một thứ ứng dụng không lưu" thì không có nghĩa gì. Cùng lý do
    // với kiểu tự sinh — người dùng không hề nhập ô đó. Server ép lại cả hai luật (EntityMapBuilder), đây
    // chỉ là để họ nhìn thấy điều đó ngay lúc bấm.
    function syncEntityRequired(tr) {
        const used = tr.querySelector(".entityfield-check");
        const required = tr.querySelector(".entityfield-required");
        if (!used || !required) return;

        const off = !tableChecked(used) || (tr.dataset.input || "text") === "auto";
        required.disabled = off;
        if (off) required.checked = false;
    }

    // Dựng phần động của MỘT dòng thông tin. Chạy cho CẢ HAI đường render (bản server dựng lúc nạp trang và
    // bản JS dựng mỗi lượt BA bày bảng) nên logic của hai ô này chỉ tồn tại đúng một chỗ — Razor chỉ chở dữ
    // liệu xuống bằng data-attribute, cùng khuôn với khối "Ý khác" của hàng chip.
    function hydrateEntityField(tr) {
        const inputCell = tr.querySelector(".entityfield-inputcell");
        if (inputCell && !inputCell.querySelector("select")) {
            inputCell.innerHTML = entitySelect(
                "entityfield-input", ENTITY_INPUTS, tr.dataset.input || "text", "Người dùng nhập thế nào");
        }
        renderEntityFieldSource(tr);
        syncEntityRequired(tr);
    }

    function hydrateEntityFields(root) {
        (root || document).querySelectorAll(".entitymap-field").forEach(hydrateEntityField);
    }

    // ---- QUAN HỆ CHA-CON ----
    // Một "thông tin" mà thật ra là nhiều dòng (5 trách nhiệm, mỗi dòng kèm tỷ trọng %) không có chỗ nào
    // trong một ô để đứng — ô đó chở đúng MỘT giá trị. Nó được tách thành một ĐỐI TƯỢNG có cha, và khi đó
    // các cột của một dòng con dùng lại nguyên vẹn hai trục của một thông tin bình thường.
    const MAX_CHILD_ROW_COUNT = 100; // = EntityMapBuilder.MaxChildRowCount

    function entityBlockName(block) {
        const input = block.querySelector(".entitymap-nameinput");
        return (input ? tableValue(block, ".entitymap-nameinput") : (block.dataset.entity || "")).trim();
    }

    // Các đối tượng được phép làm CHA của `block`: mọi khối khác, trừ khối tự nó và trừ những khối ĐÃ CÓ
    // cha — luật "tối đa một cấp" của server, áp luôn ở đây để người dùng không chọn được một thứ sẽ bị hạ
    // xuống lúc lưu mà không lời nào nói vì sao.
    function entityParentChoices(panel, block) {
        return Array.from(panel.querySelectorAll(".entitymap-block"))
            .filter(other => other !== block && !(other.dataset.parent || "").trim())
            .map(entityBlockName)
            .filter(name => name.length > 0);
    }

    function renderEntityRelation(panel, block) {
        const cell = block.querySelector(".entitymap-rel");
        if (!cell) return;

        const choices = entityParentChoices(panel, block);
        // Cha đã chọn mà không còn trong danh sách (bị xóa, bị đổi tên, hoặc vừa nhận cha của chính nó) ⇒
        // rơi về hồ sơ độc lập, đúng như server sẽ làm. Giữ lại một lựa chọn không còn tồn tại là bày ra
        // một quan hệ mà bảng đã lưu không có.
        let parent = (block.dataset.parent || "").trim();
        if (parent && !choices.some(c => c.toLowerCase() === parent.toLowerCase())) {
            parent = "";
            block.dataset.parent = "";
        }

        const options = [{ value: "", label: "Hồ sơ độc lập" }]
            .concat(choices.map(c => ({ value: c, label: `Là các dòng của ${c}` })));

        // Không có đối tượng nào khác để làm cha ⇒ không bày ô: một dropdown chỉ có đúng một lựa chọn là
        // một câu hỏi không có câu trả lời thứ hai.
        if (choices.length === 0 && !parent) {
            cell.innerHTML = "";
            return;
        }

        let html = entitySelect("entityfield-parent", options, parent, "Đối tượng này là gì trong ứng dụng");
        if (parent) {
            html += `<span class="entityrel-count">mỗi <b>${escapeHtml(parent)}</b> có
                <input type="number" min="0" max="${MAX_CHILD_ROW_COUNT}" class="entityrel-min" aria-label="Số dòng tối thiểu" placeholder="—" value="${escapeHtml(block.dataset.min || "")}" />
                đến
                <input type="number" min="0" max="${MAX_CHILD_ROW_COUNT}" class="entityrel-max" aria-label="Số dòng tối đa" placeholder="—" value="${escapeHtml(block.dataset.max || "")}" />
                dòng</span>`;
        }
        cell.innerHTML = html;
    }

    // Dropdown của MỘT khối phụ thuộc tên và quan hệ của MỌI khối khác, nên đổi một chỗ là dựng lại cả cụm.
    function refreshEntityRelations(panel) {
        if (!panel) return;
        panel.querySelectorAll(".entitymap-block").forEach(block => renderEntityRelation(panel, block));
    }

    function hydrateEntityBlocks(root) {
        hydrateEntityFields(root);
        refreshEntityRelations(root);
    }

    hydrateEntityBlocks(document); // bản server render đã có sẵn trong DOM lúc nạp trang

    const entityMapPanel = initTablePanel(
        "entityMapPanel", "entityMapSendBtn", "entityMapMsg", "entitiesJson",
        panel => Array.from(panel.querySelectorAll(".entitymap-block")).map(block => {
            // Đối tượng BA bày ra có tên nằm ở `data-entity` và không sửa được: tên ấy đã đi vào khối ngữ
            // cảnh của các lượt sau, sửa chữ ở đây là làm hội thoại kể một đằng còn bảng ghi một nẻo. Đối
            // tượng người dùng TỰ THÊM thì ô tên là ô gõ, và cờ `addedByUser` là thứ cho phép nó đi qua luật
            // "đối tượng rỗng ruột" ở server (xem EntityMapRow.AddedByUser).
            const nameInput = block.querySelector(".entitymap-nameinput");
            // Quan hệ đọc từ dataset chứ không từ ô đang hiển thị, cùng lý do với hai trục của một thông
            // tin: cả cụm bị dựng lại mỗi khi một khối khác đổi tên hoặc đổi quan hệ.
            const parent = (block.dataset.parent || "").trim();
            return {
                entity: nameInput ? tableValue(block, ".entitymap-nameinput") : (block.dataset.entity || ""),
                addedByUser: !!nameInput,
                description: tableValue(block, ".entitymap-desc"),
                parentEntity: parent,
                // Số dòng chỉ có nghĩa dưới một quan hệ — server cắt lại y hệt.
                minRows: parent ? entityRowCount(block.dataset.min) : null,
                maxRows: parent ? entityRowCount(block.dataset.max) : null,
                // Dòng thêm bằng nút "+ thêm thông tin" mà không gõ tên thì không phải một thông tin — server
                // bỏ nó đi, và bỏ luôn ở đây cho payload sạch.
                fields: Array.from(block.querySelectorAll(".entitymap-field")).map(tr => {
                    // Hai trục đọc từ `tr.dataset` chứ không từ các ô đang hiển thị — xem
                    // renderEntityFieldSource: ô của nhánh không được chọn đã bị gỡ khỏi DOM.
                    const input = tr.dataset.input || "text";
                    const source = isEntityChoice(input) ? (tr.dataset.source || "") : "";
                    return {
                        name: tableValue(tr, ".entityfield-name"),
                        meaning: tableValue(tr, ".entityfield-meaning"),
                        used: tableChecked(tr.querySelector(".entityfield-check")),
                        // Kiểu tự sinh thì người dùng không nhập ô đó, nên "bắt buộc nhập" không có nghĩa —
                        // server ép lại luật này, đây chỉ là để payload nói đúng thứ màn hình đang bày.
                        required: input !== "auto" && tableChecked(tr.querySelector(".entityfield-required")),
                        input: input,
                        source: source,
                        // Các ô ngoài nhánh đang chọn bị cắt cho payload sạch, cùng lý do với dòng thông tin
                        // chưa gõ tên ở trên: server cắt lại y hệt, gửi lên chỉ tổ làm khó đọc lúc soi mạng.
                        options: source === "inline" ? entityOptionList(tr) : [],
                        sourceSystem: source === "external" ? (tr.dataset.system || "").trim() : "",
                        rule: input === "auto" ? (tr.dataset.rule || "").trim() : ""
                    };
                }).filter(f => f.name.length > 0),
                states: Array.from(block.querySelectorAll(".entitymap-state")).map(tr => ({
                    state: tableValue(tr, ".entitystate-name"),
                    entryCondition: tableValue(tr, ".entitystate-entry")
                })).filter(s => s.state.length > 0),
                included: tableChecked(block.querySelector(".entitymap-check"))
            };
        }).filter(r => r.entity.length > 0),
        "Đang lưu bảng đối tượng…",
        "Chưa lưu được bảng đối tượng — anh/chị bấm gửi lại giúp mình nhé.");

    // Ô số dòng để trống là HỢP LỆ và có nghĩa "không ràng buộc" — khác hẳn số 0. Chuỗi không đọc được ra
    // số cũng về null: gửi lên một giá trị rác để server tự cắt là làm payload nói dối về màn hình.
    function entityRowCount(raw) {
        const value = parseInt((raw || "").trim(), 10);
        return Number.isInteger(value) && value >= 0 && value <= MAX_CHILD_ROW_COUNT ? value : null;
    }

    function entityDeleteButton(label) {
        return `<button type="button" class="entitymap-del" title="${escapeHtml(label)}" aria-label="${escapeHtml(label)}">×</button>`;
    }

    // Một dòng thông tin. `f` null = dòng TRỐNG người dùng vừa thêm bằng nút "+ thêm thông tin".
    //
    // `removable` theo đúng ranh giới của bảng màn hình: thông tin BA đề xuất thì BỎ TÍCH chứ không xóa —
    // dòng bị loại vẫn phải kể lại được trong tin nhắn gửi đi ("không cần lưu: …"), nếu không người dùng
    // không có bằng chứng nào cho thấy mình vừa loại đúng thứ định loại. Dòng do CHÍNH HỌ vừa thêm thì không
    // có gì để kể: nó chưa bao giờ là một đề xuất.
    function entityFieldRow(f, removable) {
        const name = f ? (f.name || "") : "";
        // Hai ô cuối để RỖNG và do hydrateEntityField dựng, đúng như bản Razor — xem hàm đó.
        return `
            <tr class="entitymap-field"
                data-input="${escapeHtml(f && f.input ? f.input : "text")}"
                data-source="${escapeHtml(f && f.source ? f.source : "")}"
                data-options="${escapeHtml(JSON.stringify(f && Array.isArray(f.options) ? f.options : []))}"
                data-system="${escapeHtml(f && f.sourceSystem ? f.sourceSystem : "")}"
                data-rule="${escapeHtml(f && f.rule ? f.rule : "")}">
                <td class="flowmap-use"><input type="checkbox" class="entityfield-check" aria-label="Lưu ${escapeHtml(name)}"${!f || f.used ? " checked" : ""} /></td>
                <td class="permmap-fn entityfield-namecell">
                    <textarea rows="1" class="permmap-cellinput entityfield-name" placeholder="thông tin cần lưu">${escapeHtml(name)}</textarea>
                    <textarea rows="1" class="permmap-cellinput entityfield-meaning" placeholder="thông tin này là gì?">${escapeHtml(f ? (f.meaning || "") : "")}</textarea>
                </td>
                <td class="flowmap-use"><input type="checkbox" class="entityfield-required" aria-label="Bắt buộc nhập ${escapeHtml(name)}"${f && f.required ? " checked" : ""} /></td>
                <td class="entityfield-inputcell"></td>
                <td class="entityfield-srccell"></td>
                <td class="entitymap-delcell">${removable ? entityDeleteButton("Xóa thông tin này") : ""}</td>
            </tr>`;
    }

    // Một dòng trạng thái. `s` null = dòng TRỐNG vừa thêm. Trạng thái KHÔNG có ô tích (khác dòng thông tin):
    // một trạng thái không đúng thì sửa hoặc xóa, chứ "có trạng thái này nhưng bỏ tích" không có nghĩa gì
    // trong một vòng đời — nên ở đây MỌI dòng đều xóa được, kể cả dòng BA đề xuất.
    function entityStateRow(s) {
        return `
            <tr class="entitymap-state">
                <td class="permmap-fn"><textarea rows="1" class="permmap-cellinput entitystate-name" placeholder="tên trạng thái">${escapeHtml(s ? (s.state || "") : "")}</textarea></td>
                <td><textarea rows="1" class="permmap-cellinput entitystate-entry" placeholder="điều kiện/hành động đưa vào trạng thái này">${escapeHtml(s ? (s.entryCondition || "") : "")}</textarea></td>
                <td class="entitymap-delcell">${entityDeleteButton("Xóa trạng thái này")}</td>
            </tr>`;
    }

    // MỘT khối đối tượng. `r` null = khối TRỐNG người dùng vừa thêm bằng nút "+ thêm đối tượng".
    //
    // Hai bảng con LUÔN được render, kể cả khi rỗng — khác bản trước, và đó là điều kiện để hai nút thêm có
    // chỗ đứng. Một bảng trạng thái rỗng không phải "mời xác nhận một vòng đời vô nghĩa" (thứ mà luật cắt
    // vòng đời một trạng thái đang chặn); nó là chỗ người dùng nói ra rằng đối tượng này CÓ vòng đời mà BA
    // tưởng là danh mục — trường hợp mà trước đây họ phải rời bảng, gõ vào khung chat và chờ BA bày lại.
    function entityMapBlock(r) {
        const entity = r ? (r.entity || "") : "";
        const nameCell = r
            ? `<span class="entitymap-name">${escapeHtml(entity)}</span>`
            : `<textarea rows="1" class="permmap-cellinput entitymap-nameinput" placeholder="tên đối tượng…"></textarea>`;
        const check = r && r.locked
            ? `<span class="permmap-locked" title="${escapeHtml(r.evidence || "")}">✓</span>
               <input type="hidden" class="entitymap-check" value="1" />`
            : `<input type="checkbox" class="entitymap-check" aria-label="Cần đối tượng ${r ? escapeHtml(entity) : "vừa thêm"}"${!r || r.included ? " checked" : ""} />`;

        const fields = r ? (r.fields || []).map(f => entityFieldRow(f, false)).join("") : "";
        const states = r ? (r.states || []).map(entityStateRow).join("") : "";
        // Tên đối tượng chỉ nằm ở `aria-label`, không nằm trong chữ của nút — cùng lý do với nút thêm chức
        // năng của bảng màn hình: ô của .permmap-table là nowrap, một nhãn dài bằng cả tên đối tượng sẽ nong
        // bảng con ra và đẩy cột cuối ra ngoài vùng nhìn thấy.
        const forEntity = entity ? ` cho ${entity}` : " cho đối tượng vừa thêm";

        return `
            <div class="entitymap-block" data-entity="${escapeHtml(entity)}"
                 data-parent="${escapeHtml(r && r.parentEntity ? r.parentEntity : "")}"
                 data-min="${escapeHtml(r && r.minRows !== null && r.minRows !== undefined ? String(r.minRows) : "")}"
                 data-max="${escapeHtml(r && r.maxRows !== null && r.maxRows !== undefined ? String(r.maxRows) : "")}">
                <div class="permmap-screen entitymap-head">
                    ${check}
                    ${nameCell}
                    <textarea rows="1" class="permmap-cellinput entitymap-desc" placeholder="đối tượng này là gì?">${escapeHtml(r ? (r.description || "") : "")}</textarea>
                    ${r ? "" : entityDeleteButton("Xóa đối tượng này")}
                </div>
                <div class="entitymap-rel"></div>
                <table class="permmap-table entitymap-table entitymap-fieldtable">
                    <thead><tr><th class="flowmap-th-use">Lưu</th><th class="entityfield-th-name">Thông tin</th><th class="flowmap-th-use entityfield-th-req">Bắt buộc</th><th class="entityfield-th-input">Nhập thế nào</th><th class="entityfield-th-src">Danh sách lấy ở đâu</th><th class="screenmap-th-del"></th></tr></thead>
                    <tbody>${fields}
                        <tr class="entitymap-addfieldrow">
                            <td colspan="6"><button type="button" class="entitymap-add entitymap-addfield" aria-label="Thêm thông tin${escapeHtml(forEntity)}">+ thêm thông tin</button></td>
                        </tr>
                    </tbody>
                </table>
                <table class="permmap-table entitymap-table entitymap-statetable">
                    <thead><tr><th class="screenmap-th-name">Trạng thái</th><th class="screenmap-th-purpose">Khi nào chuyển vào</th><th class="screenmap-th-del"></th></tr></thead>
                    <tbody>${states}
                        <tr class="entitymap-addstaterow">
                            <td colspan="3"><button type="button" class="entitymap-add entitymap-addstate" aria-label="Thêm trạng thái${escapeHtml(forEntity)}">+ thêm trạng thái</button></td>
                        </tr>
                    </tbody>
                </table>
            </div>`;
    }

    function renderEntityMap(rows) {
        if (!entityMapPanel || !Array.isArray(rows) || rows.length === 0) return;

        entityMapPanel.innerHTML = `
            <div class="permmap-howto">
                Đây là những thứ ứng dụng cần lưu hồ sơ riêng. Đối tượng nào <b>không cần</b> thì bỏ tích ở tiêu
                đề; thông tin nào không cần lưu thì bỏ tích trong bảng. Thiếu thông tin hay thiếu một trạng thái
                thì bấm <b>+ thêm</b> ở cuối bảng đó, thiếu cả một đối tượng thì bấm <b>+ thêm đối tượng</b> ở
                cuối. Ai được báo ở mỗi trạng thái thì mình hỏi ở bảng cuối buổi.
                <br />Cột <b>Nhập thế nào</b> quyết định hình dạng ô trên màn hình; chọn <b>Chọn 1</b> hay
                <b>Chọn nhiều</b> thì nói thêm giúp mình danh sách lấy ở đâu — <b>ứng dụng tự quản lý</b> nghĩa là
                app sẽ có thêm một màn hình riêng để quản lý danh mục đó.
            </div>
            <div class="entitymap-blocks">${rows.map(entityMapBlock).join("")}</div>
            <div class="entitymap-addrow">
                <button type="button" class="entitymap-add entitymap-addentity">+ thêm đối tượng</button>
            </div>
            <div class="permmap-bar">
                <button type="button" class="btn primary" id="entityMapSendBtn">Gửi bảng đối tượng</button>
                <div class="permmap-hint">
                    Muốn mô tả kỹ hơn một đối tượng còn thiếu, anh/chị cứ gõ vào khung chat — mình bổ sung rồi bày lại bảng.
                </div>
                <div class="permmap-msg" id="entityMapMsg"></div>
            </div>`;
        entityMapPanel.hidden = false;
        thinkingBox.before(entityMapPanel);
        hydrateEntityBlocks(entityMapPanel);
        autoGrowCells(entityMapPanel);
    }

    // THÊM/XÓA DÒNG NGAY TRÊN BẢNG ĐỐI TƯỢNG — cùng lý do và cùng khuôn với bảng màn hình: ủy quyền trên
    // PANEL chứ không gắn vào từng nút, vì renderEntityMap thay sạch innerHTML mỗi lượt BA bày bảng.
    if (entityMapPanel) {
        entityMapPanel.addEventListener("click", function (e) {
            const addEntity = e.target.closest(".entitymap-addentity");
            const addField = e.target.closest(".entitymap-addfield");
            const addState = e.target.closest(".entitymap-addstate");
            const remove = e.target.closest(".entitymap-del");
            if (!addEntity && !addField && !addState && !remove) return;

            const msgEl = document.getElementById("entityMapMsg");
            const note = text => { if (msgEl) msgEl.textContent = text; };

            if (remove) {
                // Nút xóa của dòng con nằm TRONG khối đối tượng chứa nó, nên phải hỏi dòng con trước.
                const target = remove.closest(".entitymap-field")
                    || remove.closest(".entitymap-state")
                    || remove.closest(".entitymap-block");
                if (target) target.remove();
                // Xóa một đối tượng là rút một lựa chọn khỏi mọi dropdown cha, và có thể làm một khối khác
                // rơi về hồ sơ độc lập — dựng lại cả cụm thay vì để một quan hệ trỏ vào khoảng không.
                refreshEntityRelations(entityMapPanel);
                note("");
                return;
            }

            if (addEntity) {
                const blocks = entityMapPanel.querySelector(".entitymap-blocks");
                if (!blocks) return;

                if (blocks.querySelectorAll(".entitymap-block").length >= MAX_ENTITY_ROWS) {
                    note(`Bảng đã tới trần ${MAX_ENTITY_ROWS} đối tượng — nhiều hơn thì không rà nổi trong một lượt, anh/chị bỏ tích bớt một đối tượng không cần trước khi thêm giúp mình nhé.`);
                    return;
                }

                blocks.insertAdjacentHTML("beforeend", entityMapBlock(null));
                refreshEntityRelations(entityMapPanel);
                focusNewRow(blocks.lastElementChild, ".entitymap-nameinput");
                note("");
                return;
            }

            const add = addField || addState;
            const anchor = add.closest("tr");
            const body = anchor.parentElement;
            const cap = addField ? MAX_ENTITY_FIELDS : MAX_ENTITY_STATES;
            const rowClass = addField ? ".entitymap-field" : ".entitymap-state";
            if (body.querySelectorAll(rowClass).length >= cap) {
                note(addField
                    ? `Một đối tượng chỉ nhận tối đa ${cap} thông tin — quá số đó thường là dấu hiệu đây là hai đối tượng bị gộp làm một.`
                    : `Một vòng đời chỉ nhận tối đa ${cap} trạng thái.`);
                return;
            }

            anchor.insertAdjacentHTML("beforebegin", addField ? entityFieldRow(null, true) : entityStateRow(null));
            const added = anchor.previousElementSibling;
            if (addField) hydrateEntityField(added);
            focusNewRow(added, addField ? ".entityfield-name" : ".entitystate-name");
            note("");
        });

        // HAI TRỤC + danh sách giá trị. Tất cả ủy quyền trên panel vì cùng lý do với khối trên: cả bảng bị
        // thay sạch mỗi lượt BA bày bảng, và riêng ô nguồn còn tự dựng lại mỗi lần đổi dropdown.
        entityMapPanel.addEventListener("change", function (e) {
            // Đổi CHA: luật "tối đa một cấp" nghĩa là khối vừa nhận cha không còn được làm cha của ai nữa,
            // nên cả cụm phải dựng lại chứ không riêng khối này.
            if (e.target.classList.contains("entityfield-parent")) {
                const block = e.target.closest(".entitymap-block");
                if (block) {
                    block.dataset.parent = e.target.value;
                    if (!e.target.value) { block.dataset.min = ""; block.dataset.max = ""; }
                    refreshEntityRelations(entityMapPanel);
                }
                return;
            }

            const tr = e.target.closest(".entitymap-field");
            if (!tr) return;

            if (e.target.classList.contains("entityfield-input")) {
                tr.dataset.input = e.target.value;
                renderEntityFieldSource(tr);
                syncEntityRequired(tr);
            } else if (e.target.classList.contains("entityfield-source")) {
                tr.dataset.source = e.target.value;
                renderEntityFieldSource(tr);
            } else if (e.target.classList.contains("entityfield-check")) {
                syncEntityRequired(tr);
            }
        });

        // Ô gõ của nhánh đang chọn ghi ngược vào dataset ngay từng ký tự: dataset là nguồn sự thật mà lúc gom
        // payload đọc, và chính ô này có thể bị gỡ khỏi DOM ngay khi người dùng đổi dropdown.
        entityMapPanel.addEventListener("input", function (e) {
            const block = e.target.closest(".entitymap-block");

            // Hai ô số dòng ghi thẳng vào dataset — chúng bị dựng lại mỗi lần cụm quan hệ đổi.
            if (block && e.target.classList.contains("entityrel-min")) block.dataset.min = e.target.value;
            if (block && e.target.classList.contains("entityrel-max")) block.dataset.max = e.target.value;

            // Đổi TÊN một đối tượng người dùng tự thêm là đổi nhãn của nó trong mọi dropdown cha. Dựng lại
            // sau mỗi ký tự nghe phí, nhưng cụm này chỉ vài phần tử, và để nhãn cũ nằm lại là mời người dùng
            // chọn một cái tên không còn tồn tại.
            if (block && e.target.classList.contains("entitymap-nameinput"))
                refreshEntityRelations(entityMapPanel);

            const tr = e.target.closest(".entitymap-field");
            if (!tr) return;

            if (e.target.classList.contains("entityfield-system")) tr.dataset.system = e.target.value;
            else if (e.target.classList.contains("entityfield-rule")) tr.dataset.rule = e.target.value;
        });

        // Thêm một giá trị bằng Enter. Ô KHÔNG có nút "thêm" riêng: nó nằm ngay sau các chip nên hình dạng đã
        // nói rõ việc phải làm, và một cái nút nữa trong ô hẹp này chỉ chen chỗ của chính danh sách.
        entityMapPanel.addEventListener("keydown", function (e) {
            if (e.key !== "Enter" || !e.target.classList.contains("entityfield-optadd")) return;
            e.preventDefault();
            commitEntityOption(e.target, true);
        });

        // Gõ xong rồi bấm thẳng "Gửi bảng đối tượng" mà không Enter là ca THƯỜNG GẶP, và mất đúng giá trị vừa
        // gõ ở đó là mất im lặng — không dòng nào trên màn hình nói rằng nó đã rơi. Vì vậy rời ô cũng chốt.
        entityMapPanel.addEventListener("focusout", function (e) {
            if (e.target.classList && e.target.classList.contains("entityfield-optadd"))
                commitEntityOption(e.target, false);
        });

        entityMapPanel.addEventListener("click", function (e) {
            const del = e.target.closest(".entityfield-optdel");
            if (!del) return;

            const tr = del.closest(".entitymap-field");
            // Xóa theo GIÁ TRỊ chứ không theo vị trí: ô "thêm giá trị" chốt lúc rời ô, nên một cú bấm vào dấu
            // × vừa kịp chèn thêm một chip trước khi tới đây và mọi chỉ số đã lệch đi một.
            setEntityOptionList(tr, entityOptionList(tr).filter(v => v !== del.dataset.value));
            renderEntityFieldSource(tr);
        });
    }

    // Chốt chữ đang nằm trong ô "thêm giá trị" thành một chip. Trùng thì bỏ qua chứ không báo lỗi: người dùng
    // gõ lại một giá trị đã có là muốn nó có mặt, và nó đang có mặt.
    function commitEntityOption(input, refocus) {
        const tr = input.closest(".entitymap-field");
        const value = (input.value || "").trim();
        if (!tr || value.length === 0) return;

        const values = entityOptionList(tr);
        if (!values.some(v => v.toLowerCase() === value.toLowerCase()) && values.length < MAX_ENTITY_OPTIONS)
            values.push(value);

        setEntityOptionList(tr, values);
        input.value = "";
        renderEntityFieldSource(tr);
        if (refocus) {
            const next = tr.querySelector(".entityfield-optadd");
            if (next) next.focus();
        }
    }

    // ---- BẢNG THÔNG BÁO / NHẮC NHỞ ----
    // Bảng CUỐI CÙNG của buổi phỏng vấn: mỗi sự kiện một dòng, To/CC chọn từ DANH SÁCH NGƯỜI NHẬN của dự án
    // (bảng nhỏ ngay trên đầu panel — thêm/sửa/xóa được). Ô là ô CHỌN NHIỀU chứ không phải ô gõ: gõ thẳng
    // vào từng dòng thì mỗi dòng một cách viết cùng một người, và không tầng nào ghép chúng lại được. Chỗ
    // gõ có đúng MỘT: bảng danh sách người nhận, và sửa ở đó thì mọi ô chọn đổi theo.
    const MAX_NOTIF_ROWS = 24;        // = NotificationMapBuilder.MaxRows
    const MAX_NOTIF_RECIPIENTS = 8;   // = NotificationMapBuilder.MaxRecipientsPerCell
    const MAX_RECIPIENT_OPTIONS = 20; // = NotificationMapBuilder.MaxRecipientOptions

    // ---- BẢNG BÁO CÁO / THỐNG KÊ ----
    // Trần dòng, chép từ ReportMapBuilder. Chặn ở đây chứ không để server cắt — cùng lý do với bảng màn hình:
    // một dòng người dùng vừa gõ mà bị nuốt lúc lưu là đúng loại quyết định câm mà cả bảng này sinh ra để chặn.
    const MAX_REPORT_ROWS = 12;   // = ReportMapBuilder.MaxRows

    const reportMapPanel = initTablePanel(
        "reportMapPanel", "reportMapSendBtn", "reportMapMsg", "reportsJson",
        panel => Array.from(panel.querySelectorAll(".reportmap-row")).map(tr => ({
            report: tableValue(tr, ".reportmap-name"),
            question: tableValue(tr, ".reportmap-question"),
            // Ô "lấy số từ" là danh sách ĐÓNG (các đối tượng đã chốt): server xoá mọi giá trị không khớp
            // đối tượng nào, nên một ô gõ tay là ô mà chữ vừa gõ biến mất lúc lưu, không câu nào giải thích.
            source: tableValue(tr, ".reportmap-source"),
            breakdown: tableValue(tr, ".reportmap-breakdown"),
            included: tableChecked(tr.querySelector(".reportmap-check")),
            // Cờ nằm ở data-attribute chứ không suy ra từ "ô tên có phải input không" như các bảng kia: ở
            // bảng này MỌI dòng đều có ô tên gõ được (tên báo cáo không phải khóa nối sang bảng nào, mà đặt
            // lại tên lại là chỗ người dùng sửa nhiều nhất), nên sự hiện diện của ô không phân biệt được gì.
            addedByUser: tr.dataset.added === "true"
        // Dòng bỏ trống tên không phải một báo cáo — bỏ ngay ở đây cho payload sạch (server cũng bỏ).
        })).filter(r => r.report.length > 0),
        "Đang lưu bảng báo cáo…",
        "Chưa lưu được bảng báo cáo — anh/chị bấm gửi lại giúp mình nhé.");

    // Các ĐỐI TƯỢNG đã chốt đang có hiệu lực — mục chọn của ô "lấy số từ", kể cả ở dòng người dùng vừa thêm.
    // `data-entities` là bản mồi của server (frame done hoặc lượt render lại sau F5).
    function reportEntityOptions() {
        if (!reportMapPanel) return [];
        try {
            const parsed = JSON.parse(reportMapPanel.dataset.entities || "[]");
            return Array.isArray(parsed) ? parsed : [];
        } catch (err) {
            return [];
        }
    }

    function reportSourceCell(selected, options) {
        const chosen = selected || "";
        return `
            <select class="reportmap-source" aria-label="Số liệu lấy từ đối tượng nào">
                <option value=""${chosen ? "" : " selected"}>— chưa rõ —</option>
                ${options.map(o => `<option value="${escapeHtml(o)}"${o === chosen ? " selected" : ""}>${escapeHtml(o)}</option>`).join("")}
            </select>`;
    }

    // MỘT dòng. `r` null = dòng TRỐNG người dùng vừa thêm bằng nút "+ thêm báo cáo" — nút đó tồn tại vì BA
    // chỉ gom được những báo cáo hội thoại đã nhắc tới, còn thứ người dùng chợt nhớ ra khi nhìn danh sách
    // thì không có dòng nào để gieo. Không có chỗ tự thêm thì nó biến mất trong im lặng ngay tại cái bảng
    // sinh ra để chốt nó.
    //
    // `data-added` quyết định dòng có nút xóa hay không, và ranh giới đó là chủ ý: báo cáo BA đề xuất thì
    // BỎ TÍCH chứ không xóa — dòng bị loại vẫn phải kể lại được trong tin nhắn gửi đi, nếu không người dùng
    // không có bằng chứng nào cho thấy mình vừa loại đúng thứ định loại. Dòng do CHÍNH HỌ vừa thêm thì
    // không có gì để kể lại: nó chưa bao giờ là một đề xuất, nên xóa hẳn mới là thao tác đúng.
    function reportRow(r, options) {
        const added = !r;
        return `
            <tr class="reportmap-row" data-added="${added ? "true" : "false"}">
                <td class="flowmap-use">
                    <input type="checkbox" class="reportmap-check" aria-label="Cần báo cáo ${r ? escapeHtml(r.report || "") : "vừa thêm"}"${!r || r.included !== false ? " checked" : ""} />
                </td>
                <td><textarea rows="1" class="permmap-cellinput reportmap-name" aria-label="Tên báo cáo" placeholder="tên báo cáo…">${r ? escapeHtml(r.report || "") : ""}</textarea></td>
                <td><textarea rows="1" class="permmap-cellinput reportmap-question" aria-label="Báo cáo này trả lời câu hỏi gì" placeholder="để biết điều gì?">${r ? escapeHtml(r.question || "") : ""}</textarea></td>
                <td>${reportSourceCell(r ? r.source : "", options)}</td>
                <td><textarea rows="1" class="permmap-cellinput reportmap-breakdown" aria-label="Gộp hoặc lọc theo" placeholder="kỳ, đơn vị, trạng thái…">${r ? escapeHtml(r.breakdown || "") : ""}</textarea></td>
                <td class="entitymap-delcell">${added ? `<button type="button" class="entitymap-del reportmap-del" title="Xóa dòng này" aria-label="Xóa dòng này">×</button>` : ""}</td>
            </tr>`;
    }

    // Markup khớp bản server render trong Index.cshtml — hai đường lệch nhau thì người dùng rà xong bảng
    // vừa hiện ra rồi F5 và thấy một bảng khác.
    function renderReportMap(rows, entities) {
        if (!reportMapPanel || !Array.isArray(rows) || rows.length === 0) return;

        const opts = Array.isArray(entities) && entities.length > 0 ? entities : reportEntityOptions();
        reportMapPanel.dataset.entities = JSON.stringify(opts);
        reportMapPanel.innerHTML = `
            <div class="permmap-howto">
                Đây là các báo cáo <b>mình gom lại</b> từ những gì anh/chị đã kể. Báo cáo nào không cần thì
                <b>bỏ tích</b> cột đầu; ô nào mình hiểu chưa đúng thì sửa thẳng vào ô; thiếu báo cáo nào thì
                bấm <b>+ thêm báo cáo</b> ở cuối bảng. Mỗi báo cáo còn giữ sẽ thành <b>một màn hình</b> của
                ứng dụng, nên ai được xem báo cáo nào sẽ hỏi ở bảng phân quyền ngay sau đây.
            </div>
            <table class="permmap-table reportmap-table">
                <thead>
                    <tr>
                        <th class="flowmap-th-use">Cần</th>
                        <th class="reportmap-th-name">Báo cáo / thống kê</th>
                        <th class="reportmap-th-question">Để trả lời câu hỏi gì</th>
                        <th class="reportmap-th-source">Lấy số từ</th>
                        <th class="reportmap-th-breakdown">Gộp / lọc theo</th>
                        <th class="screenmap-th-del"></th>
                    </tr>
                </thead>
                <tbody>${rows.map(r => reportRow(r, opts)).join("")}
                    <tr class="reportmap-addrow">
                        <td colspan="6"><button type="button" class="entitymap-add reportmap-add">+ thêm báo cáo</button></td>
                    </tr>
                </tbody>
            </table>
            <div class="permmap-bar">
                <button type="button" class="btn primary" id="reportMapSendBtn">Gửi bảng báo cáo</button>
                <div class="permmap-hint">
                    Không cần báo cáo nào thì cứ bỏ tích hết rồi gửi — mình ghi lại là ứng dụng không có
                    phần báo cáo, không hỏi lại nữa.
                </div>
                <div class="permmap-msg" id="reportMapMsg"></div>
            </div>`;
        reportMapPanel.hidden = false;
        thinkingBox.before(reportMapPanel);
        autoGrowCells(reportMapPanel);
        enhanceReportSelects(reportMapPanel);
    }

    // Ô "lấy số từ" là một <select> thường; driver dropdown dùng chung của app (dropdown.js) nâng nó thành
    // .ms-combo để nó trông giống mọi dropdown khác. Driver chỉ quét MỘT LẦN lúc nạp trang, nên bản server
    // render thì đẹp còn bảng do JS dựng (và mọi dòng vừa thêm) lại là select trần — hai đường lệch nhau ngay
    // trên cùng một bảng. Fail-open: chưa có driver thì select trần vẫn gửi đúng giá trị.
    function enhanceReportSelects(root) {
        if (window.CsDropdown && root) window.CsDropdown.enhanceAll(root);
    }

    // THÊM/XÓA DÒNG — ủy quyền trên PANEL vì renderReportMap thay sạch innerHTML.
    if (reportMapPanel) {
        reportMapPanel.addEventListener("click", function (e) {
            const note = text => {
                const msgEl = document.getElementById("reportMapMsg");
                if (msgEl) msgEl.textContent = text;
            };

            const remove = e.target.closest(".reportmap-del");
            if (remove) {
                const row = remove.closest(".reportmap-row");
                if (row) row.remove();
                note("");
                return;
            }

            if (!e.target.closest(".reportmap-add")) return;

            const body = reportMapPanel.querySelector(".reportmap-table tbody");
            const anchor = reportMapPanel.querySelector(".reportmap-addrow");
            if (!body || !anchor) return;

            if (body.querySelectorAll(".reportmap-row").length >= MAX_REPORT_ROWS) {
                note(`Bảng đã tới trần ${MAX_REPORT_ROWS} báo cáo — nhiều hơn thì không rà nổi trong một lượt.`);
                return;
            }

            anchor.insertAdjacentHTML("beforebegin", reportRow(null, reportEntityOptions()));
            enhanceReportSelects(anchor.previousElementSibling);
            focusNewRow(anchor.previousElementSibling, ".reportmap-name");
            note("");
        });
    }

    const notificationMapPanel = initTablePanel(
        "notificationMapPanel", "notificationMapSendBtn", "notificationMapMsg", "notificationsJson",
        panel => Array.from(panel.querySelectorAll(".notifmap-row")).map(row => {
            // Dòng gieo từ bảng đối tượng có tên nằm ở `data-event` và KHÔNG sửa được: nó phải khớp đúng
            // chuyển trạng thái người dùng vừa chốt ở bảng kia, sửa chữ ở đây là cắt luôn mối nối giữa hai
            // bảng ở bước sinh spec. Dòng NHẮC NHỞ người dùng tự thêm thì ô tên là ô gõ.
            const nameInput = row.querySelector(".notifmap-nameinput");
            return {
                entity: row.dataset.entity || "",
                event: nameInput ? tableValue(row, ".notifmap-nameinput") : (row.dataset.event || ""),
                trigger: tableValue(row, ".notifmap-trigger-input") || (row.dataset.trigger || ""),
                to: pickedRecipients(row, "to"),
                cc: pickedRecipients(row, "cc"),
                needed: tableChecked(row.querySelector(".notifmap-check")),
                addedByUser: !!nameInput
            };
        }).filter(r => r.event.length > 0),
        "Đang lưu bảng thông báo…",
        "Chưa lưu được bảng thông báo — anh/chị bấm gửi lại giúp mình nhé.",
        panel => askMissingRecipients(missingRecipientRows(panel)),
        // Danh sách người nhận đi CÙNG CHUYẾN với bảng: server lưu nó và đối chiếu hai ô To/CC theo đúng nó.
        // Gửi ở lượt riêng thì có một khoảnh khắc bảng đã lưu mà danh sách chưa, và ở khoảnh khắc đó mọi
        // người nhận người dùng vừa tự thêm bị bỏ sạch — bảng hiện đủ tên mà server báo "chưa chọn ai".
        () => ({ recipientsJson: JSON.stringify(recipientOptions()) }));

    // Tên sự kiện của MỘT dòng, đúng cách `collect` đọc nó: dòng gieo mang tên ở `data-event` (không sửa
    // được), dòng NHẮC NHỞ người dùng tự thêm thì có ô gõ.
    function notificationRowEvent(row) {
        return row.querySelector(".notifmap-nameinput")
            ? tableValue(row, ".notifmap-nameinput")
            : (row.dataset.event || "");
    }

    // BẤT BIẾN của bảng: một dòng chỉ có HAI trạng thái — bỏ tích ("không gửi email", một quyết định hợp
    // lệ) hoặc còn tích KÈM người nhận chính. Trạng thái thứ ba, "cần gửi mà chưa chọn ai", từng được cho
    // qua và trả giá đúng bằng thứ cái bảng này sinh ra để thay thế: nhóm «Thông báo / nhắc nhở» xuống
    // [MỘT PHẦN], nút "Write Requirement" khóa, và BA phải đi hỏi lại TỪNG sự kiện trong khung chat, mỗi sự
    // kiện hai lượt (To rồi CC). Ca thật: bảng 8 dòng gửi đi với 7 dòng trống ⇒ 14 lượt chat, ở cuối một
    // buổi phỏng vấn đã 78 lượt.
    //
    // Bỏ qua dòng chưa gõ tên đúng như `collect` bỏ qua: nó không được gửi đi, nên chặn vì nó là chặn oan.
    function missingRecipientRows(panel) {
        return Array.from(panel.querySelectorAll(".notifmap-row")).filter(row =>
            notificationRowEvent(row).length > 0
            && tableChecked(row.querySelector(".notifmap-check"))
            && pickedRecipients(row, "to").length === 0);
    }

    // Nhãn người dùng đọc thấy trên bảng — khớp NotificationMapBuilder.EventLabel để popup và câu lỗi của
    // server gọi cùng một sự kiện bằng cùng một tên.
    function notificationRowLabel(row) {
        const entity = (row.dataset.entity || "").trim();
        const trigger = (row.querySelector(".notifmap-trigger-input")
            ? tableValue(row, ".notifmap-trigger-input")
            : (row.dataset.trigger || "")).trim();
        return (entity ? entity + " — " : "") + `"${notificationRowEvent(row)}"`
            + (trigger ? ` (khi ${trigger})` : "");
    }

    // POPUP chặn lượt gửi khi còn dòng trống người nhận. Trả true = bảng hợp lệ, gửi tiếp.
    //
    // Vì sao KHÔNG chỉ nhắc "vui lòng chọn người nhận": ở hệ này một người nhận SAI hại hơn một ô trống —
    // ô trống còn bị hỏi lại, còn giá trị sai được chấm [RÕ] rồi vĩnh viễn không ai soát nữa. Một popup
    // chặn cứng mà chỉ có một đường ra sẽ đẩy người dùng đang mệt tới cú bấm nhanh nhất trong danh sách, và
    // mục nhanh nhất lại là "Toàn bộ <vai>" — nghĩa là cả nhà máy nhận email ở sự kiện đó. Nên popup bày
    // HAI lối đi, và cả hai đều là câu trả lời thật: chọn người nhận, hoặc nói rằng sự kiện này không cần
    // gửi email. "Không biết ai" được đổ về một quyết định hiển thị, không về một người nhận bịa.
    function askMissingRecipients(rows) {
        if (rows.length === 0) return true;

        closeRecipientPickers();
        const backdrop = document.createElement("div");
        backdrop.className = "modal-backdrop notifmap-missing";
        backdrop.innerHTML = `
            <div class="modal" role="dialog" aria-modal="true" aria-labelledby="notifmapMissingTitle">
                <button type="button" class="x" aria-label="Quay lại bảng">×</button>
                <h2 id="notifmapMissingTitle">Còn ${rows.length} sự kiện chưa có người nhận</h2>
                <p class="modal-sub">
                    Mỗi sự kiện dưới đây đang tích <b>Cần</b> nhưng chưa chọn <b>Gửi cho (To)</b>. Anh/chị chọn
                    người nhận, hoặc cho mình biết sự kiện đó <b>không cần gửi email</b>.
                </p>
                <ul class="notifmap-missing-list">
                    ${rows.map((row, i) => `
                        <li data-idx="${i}">
                            <span class="notifmap-missing-event">${escapeHtml(notificationRowLabel(row))}</span>
                            <span class="notifmap-missing-acts">
                                <button type="button" class="btn small" data-act="pick">Chọn người nhận</button>
                                ${row.querySelector("input[type=checkbox].notifmap-check")
                                    ? `<button type="button" class="btn small" data-act="off">Không cần gửi</button>`
                                    : ""}
                            </span>
                        </li>`).join("")}
                </ul>
                <div class="modal-actions"><button type="button" class="btn" data-act="close">Quay lại bảng</button></div>
            </div>`;

        const close = () => {
            backdrop.remove();
            document.removeEventListener("keydown", onKey);
        };
        const onKey = e => { if (e.key === "Escape") close(); };

        backdrop.addEventListener("click", function (e) {
            // Chặn lan lên `document`: handler "bấm ra ngoài thì đóng ô chọn" ở cuối file sẽ chạy SAU handler
            // này và đóng lại đúng ô mà nút "Chọn người nhận" vừa mở — nút nằm trong popup nên nó không thỏa
            // `closest(".notifmap-pick")`. Không có dòng này thì lối đi chính của popup im lặng không làm gì.
            e.stopPropagation();

            if (e.target === backdrop || e.target.closest('.x, [data-act="close"]')) {
                close();
                return;
            }

            const btn = e.target.closest('[data-act="pick"], [data-act="off"]');
            if (!btn) return;

            const item = btn.closest("li");
            const row = rows[Number(item.dataset.idx)];
            if (!row) return;

            // "Chọn người nhận" đóng popup và mở sẵn đúng ô của đúng dòng: bảng tới 24 dòng nên ô trống
            // thường nằm ngoài màn hình, và một popup bảo "hàng nào đó còn thiếu" thì bắt người dùng tự đi
            // tìm — đúng việc mà popup đang thay họ làm.
            if (btn.dataset.act === "pick") {
                close();
                highlightNotificationRow(row);
                const pick = row.querySelector('.notifmap-pick[data-kind="to"]');
                if (pick) {
                    pick.classList.add("open");
                    pick.querySelector(".ms-combo-panel").hidden = false;
                    const first = pick.querySelector("input[type=checkbox]");
                    if (first) first.focus();
                }
                return;
            }

            // "Không cần gửi" = bỏ tích ngay tại đây. Đi qua đúng đường `change` của bảng để nhãn ô chọn và
            // trạng thái dòng không lệch với thứ sẽ được gửi lên.
            const check = row.querySelector("input[type=checkbox].notifmap-check");
            if (check) {
                check.checked = false;
                check.dispatchEvent(new Event("change", { bubbles: true }));
            }
            item.remove();

            const left = backdrop.querySelectorAll(".notifmap-missing-list li").length;
            if (left === 0) {
                close();
                const msgEl = document.getElementById("notificationMapMsg");
                if (msgEl) msgEl.textContent = "Xong — anh/chị bấm “Gửi bảng thông báo” lần nữa nhé.";
                return;
            }
            backdrop.querySelector("#notifmapMissingTitle").textContent =
                `Còn ${left} sự kiện chưa có người nhận`;
        });

        document.addEventListener("keydown", onKey);
        document.body.appendChild(backdrop);
        const firstAction = backdrop.querySelector('[data-act="pick"]');
        if (firstAction) firstAction.focus();
        return false;
    }

    // Vệt sáng tạm trên dòng vừa được popup trỏ tới. Tự tắt khi người dùng chạm vào dòng đó — nó là chỉ
    // đường, không phải trạng thái lỗi cần đọng lại trên bảng.
    function highlightNotificationRow(row) {
        row.scrollIntoView({ block: "center", behavior: "smooth" });
        row.classList.add("is-missing");
        const clear = () => {
            row.classList.remove("is-missing");
            row.removeEventListener("change", clear);
            row.removeEventListener("click", clear);
        };
        row.addEventListener("change", clear);
        row.addEventListener("click", clear);
    }

    function pickedRecipients(row, kind) {
        const pick = row.querySelector('.notifmap-pick[data-kind="' + kind + '"]');
        if (!pick) return [];
        return Array.from(pick.querySelectorAll("input[type=checkbox]:checked")).map(i => i.value);
    }

    // DANH SÁCH NGƯỜI NHẬN đang có hiệu lực. Bảng danh sách (nếu đã dựng) là bản DUY NHẤT đáng tin — nó là
    // thứ người dùng vừa gõ; `data-options` chỉ là bản mồi của server (frame done hoặc lượt render lại sau
    // F5) và bản gương được `syncRecipientOptions` giữ cho khớp, để các dòng thêm sau đọc được cùng một bộ.
    function recipientOptions() {
        if (!notificationMapPanel) return [];

        const table = notificationMapPanel.querySelector(".notifrecip-table");
        if (table) return recipientListValues(table);

        try {
            const parsed = JSON.parse(notificationMapPanel.dataset.options || "[]");
            return Array.isArray(parsed) ? parsed : [];
        } catch (e) {
            return [];
        }
    }

    // Các mục của bảng danh sách người nhận, theo đúng thứ tự trên bảng: bỏ dòng chưa gõ, bỏ trùng, chặn ở
    // trần. Cùng luật với NotificationMapBuilder.SanitizeRecipients — server chạy lại y hệt trên payload,
    // nên hai bên lệch nhau là người dùng chọn được một mục rồi bị server bỏ đúng mục đó.
    function recipientListValues(root) {
        const table = root || (notificationMapPanel && notificationMapPanel.querySelector(".notifrecip-table"));
        if (!table) return [];

        const values = [];
        const seen = Object.create(null);
        Array.from(table.querySelectorAll(".notifrecip-row")).forEach(row => {
            const value = tableValue(row, ".notifrecip-name");
            const key = normalizeRecipient(value);
            if (value.length === 0 || seen[key] || values.length >= MAX_RECIPIENT_OPTIONS) return;
            seen[key] = true;
            values.push(value);
        });
        return values;
    }

    // Chép phép chuẩn hoá của server (NotificationMapBuilder.Normalize) vì nó quyết định thứ TRÙNG NHAU:
    // "HRBP" và "hrbp " là hai dòng khác nhau trên bảng nhưng cùng một mục lúc so khớp, và để cả hai lọt
    // vào là dựng ra hai tùy chọn không phân biệt được mà cùng đích.
    function normalizeRecipient(value) {
        return (value || "").toLowerCase().split(/\s+/).filter(Boolean).join(" ")
            .replace(/^[.,:;–-]+/, "").replace(/[.,:;–-]+$/, "");
    }

    // MỘT dòng của bảng danh sách người nhận. Markup khớp bản server render trong Index.cshtml — hai đường
    // lệch nhau thì người dùng rà bảng vừa hiện ra rồi F5 và thấy một bảng khác.
    function recipientListRow(value) {
        return `
            <tr class="notifrecip-row">
                <td><textarea rows="1" class="permmap-cellinput notifrecip-name" aria-label="Tên người nhận">${escapeHtml(value || "")}</textarea></td>
                <td class="entitymap-delcell">
                    <button type="button" class="entitymap-del notifrecip-del" title="Xóa người nhận này" aria-label="Xóa người nhận này">×</button>
                </td>
            </tr>`;
    }

    function renderRecipientList(options) {
        return `
            <div class="permmap-howto">
                Đây là <b>danh sách người nhận</b> của dự án — mình gom từ những gì anh/chị đã kể. Hai ô
                <b>Gửi cho (To)</b> và <b>Đồng gửi (CC)</b> ở bảng dưới chỉ chọn được trong danh sách này,
                nên thiếu ai thì thêm ngay ở đây, sửa chữ hoặc xóa cũng được.
            </div>
            <table class="permmap-table notifrecip-table">
                <thead>
                    <tr>
                        <th>Người nhận</th>
                        <th class="screenmap-th-del"></th>
                    </tr>
                </thead>
                <tbody>${options.map(recipientListRow).join("")}
                    <tr class="notifrecip-addrow">
                        <td colspan="2"><button type="button" class="entitymap-add notifrecip-add">+ thêm người nhận</button></td>
                    </tr>
                </tbody>
            </table>`;
    }

    // Số ô To/CC đang chọn một người nhận — câu hỏi phải trả lời được TRƯỚC khi xóa hay đổi tên một mục.
    function recipientUsage(value) {
        if (!notificationMapPanel || !value) return 0;
        return Array.from(notificationMapPanel.querySelectorAll(".notifmap-pick input[type=checkbox]:checked"))
            .filter(box => box.value === value).length;
    }

    // Bảng danh sách vừa đổi ⇒ dựng lại danh mục của MỌI ô chọn. Đây là chỗ giữ lời hứa của cả tính năng
    // ("sửa một chỗ, mọi dropdown đổi theo"), và nó phải làm đủ ba việc, thiếu việc nào cũng là mất dữ liệu
    // im lặng:
    //  • ĐỔI TÊN thì kéo theo các ô đang chọn mục cũ (`renameFrom` → `renameTo`) — không thì ô giữ một chuỗi
    //    không còn trong danh sách, và server bỏ nó lúc gửi ⇒ dòng thành "cần gửi mà chưa chọn ai";
    //  • XÓA thì bỏ mục đó khỏi các ô đang chọn nó (lọc theo danh sách mới);
    //  • giữ nguyên trạng thái ĐANG MỞ của ô người dùng đang thao tác, nên chỉ thay phần danh mục bên trong
    //    chứ không dựng lại cả ô.
    function syncRecipientOptions(renameFrom, renameTo) {
        if (!notificationMapPanel) return;

        const opts = recipientOptions();
        notificationMapPanel.dataset.options = JSON.stringify(opts);

        notificationMapPanel.querySelectorAll(".notifmap-pick").forEach(pick => {
            const chosen = Array.from(pick.querySelectorAll("input[type=checkbox]:checked"))
                .map(box => (renameFrom && box.value === renameFrom) ? renameTo : box.value)
                .filter(value => opts.indexOf(value) >= 0);

            const list = pick.querySelector(".ms-combo-list");
            if (list) list.innerHTML = recipientOptionItems(opts, chosen);
            updateRecipientPickLabel(pick);
        });
    }

    // Nhãn của một ô chọn. Đây là dòng chữ DUY NHẤT người dùng đọc khi rà lại cả bảng (panel đóng), nên để
    // nó lệch với thứ đang được chọn là để họ gửi đi một bảng khác với bảng họ tưởng.
    function updateRecipientPickLabel(pick) {
        const chosen = Array.from(pick.querySelectorAll("input[type=checkbox]:checked")).map(box => box.value);
        const text = pick.querySelector(".ms-combo-text");
        if (!text) return;
        text.classList.toggle("is-placeholder", chosen.length === 0);
        text.textContent = chosen.length > 0
            ? chosen.join(", ")
            : (pick.dataset.kind === "to" ? "Chọn người nhận" : "Không đồng gửi");
    }

    // MỘT ô chọn nhiều. Dùng đúng markup .ms-combo dùng chung của app (site.css) nên nó trông y hệt mọi
    // dropdown khác; phần điều khiển nằm ngay dưới vì .ms-combo nhiều-lựa-chọn chưa có driver dùng chung.
    function recipientPicker(kind, selected, options, label) {
        const chosen = Array.isArray(selected) ? selected : [];
        const items = recipientOptionItems(options, chosen);

        return `
            <div class="ms-combo notifmap-pick" data-kind="${kind}">
                <button type="button" class="ms-combo-trigger" aria-label="${escapeHtml(label)}">
                    <span class="ms-combo-trigger-main">
                        <span class="ms-combo-text${chosen.length === 0 ? " is-placeholder" : ""}">${chosen.length === 0 ? (kind === "to" ? "Chọn người nhận" : "Không đồng gửi") : escapeHtml(chosen.join(", "))}</span>
                    </span>
                    <span class="ms-combo-caret">▾</span>
                </button>
                <div class="ms-combo-panel" hidden><ul class="ms-combo-list">${items}</ul></div>
            </div>`;
    }

    // Danh mục BÊN TRONG một ô chọn. Tách riêng vì `syncRecipientOptions` thay đúng phần này khi bảng danh
    // sách người nhận đổi — dựng lại cả ô thì ô đang mở bị đóng sập ngay dưới tay người dùng.
    function recipientOptionItems(options, chosen) {
        const picked = Array.isArray(chosen) ? chosen : [];
        return options.map(o => `
            <li class="ms-combo-option${picked.indexOf(o) >= 0 ? " selected" : ""}">
                <label class="ms-combo-checkbox">
                    <input type="checkbox" value="${escapeHtml(o)}"${picked.indexOf(o) >= 0 ? " checked" : ""} />
                    <span class="ms-combo-box"></span>
                    <span class="ms-combo-option-text">${escapeHtml(o)}</span>
                </label>
            </li>`).join("");
    }

    // MỘT dòng. `r` null = dòng NHẮC NHỞ trống người dùng vừa thêm — nửa "nhắc nhở" của nhóm không phải
    // chuyển trạng thái nào ("trước hạn 3 ngày"), nên không có nút này thì nó biến mất trong im lặng ngay
    // tại cái bảng sinh ra để chốt nó.
    function notificationRow(r, options) {
        const entity = r ? (r.entity || "") : "";
        const evt = r ? (r.event || "") : "";
        const trigger = r ? (r.trigger || "") : "";
        const check = r && r.locked
            ? `<span class="permmap-locked" title="${escapeHtml(r.evidence || "")}">✓</span>
               <input type="hidden" class="notifmap-check" value="1" />`
            : `<input type="checkbox" class="notifmap-check" aria-label="Cần báo khi ${r ? escapeHtml(evt) : "sự kiện vừa thêm"}"${!r || r.needed ? " checked" : ""} />`;

        const nameCell = r
            ? `${entity ? `<span class="notifmap-entity">${escapeHtml(entity)}</span>` : ""}
               <span class="notifmap-name">${escapeHtml(evt)}</span>
               ${trigger ? `<span class="notifmap-trigger">khi ${escapeHtml(trigger)}</span>` : ""}`
            : `<textarea rows="1" class="permmap-cellinput notifmap-nameinput" placeholder="sự kiện / lời nhắc…"></textarea>
               <textarea rows="1" class="permmap-cellinput notifmap-trigger-input" placeholder="khi nào? (vd: trước hạn 3 ngày)"></textarea>`;

        return `
            <tr class="notifmap-row" data-entity="${escapeHtml(entity)}" data-event="${escapeHtml(evt)}" data-trigger="${escapeHtml(trigger)}">
                <td class="flowmap-use">${check}</td>
                <td class="permmap-fn">${nameCell}</td>
                <td>${recipientPicker("to", r ? r.to : [], options, `Người nhận chính của ${evt || "sự kiện vừa thêm"}`)}</td>
                <td>${recipientPicker("cc", r ? r.cc : [], options, `Người đồng gửi của ${evt || "sự kiện vừa thêm"}`)}</td>
                <td class="entitymap-delcell">${r ? "" : `<button type="button" class="entitymap-del" title="Xóa dòng này" aria-label="Xóa dòng này">×</button>`}</td>
            </tr>`;
    }

    function renderNotificationMap(rows, options) {
        if (!notificationMapPanel || !Array.isArray(rows) || rows.length === 0) return;

        const opts = Array.isArray(options) && options.length > 0 ? options : recipientOptions();
        notificationMapPanel.dataset.options = JSON.stringify(opts);
        notificationMapPanel.innerHTML = `
            ${renderRecipientList(opts)}
            <div class="permmap-howto">
                Đây là các sự kiện của ứng dụng. Sự kiện nào <b>không cần gửi email</b> thì bỏ tích cột đầu; sự
                kiện còn tích thì <b>bắt buộc chọn người nhận (To)</b>, còn <b>đồng gửi (CC)</b> có thì chọn,
                không có thì để trống. Thiếu một lời nhắc theo thời hạn thì bấm <b>+ thêm lời nhắc</b> ở cuối bảng.
            </div>
            <table class="permmap-table notifmap-table">
                <thead>
                    <tr>
                        <th class="flowmap-th-use">Cần</th>
                        <th class="notifmap-th-event">Sự kiện</th>
                        <th class="notifmap-th-who">Gửi cho (To)</th>
                        <th class="notifmap-th-cc">Đồng gửi (CC)</th>
                        <th class="screenmap-th-del"></th>
                    </tr>
                </thead>
                <tbody>${rows.map(r => notificationRow(r, opts)).join("")}
                    <tr class="notifmap-addrow">
                        <td colspan="5"><button type="button" class="entitymap-add notifmap-add">+ thêm lời nhắc</button></td>
                    </tr>
                </tbody>
            </table>
            <div class="permmap-bar">
                <button type="button" class="btn primary" id="notificationMapSendBtn">Gửi bảng thông báo</button>
                <div class="permmap-hint">
                    Người nhận là một quan hệ với bản ghi ("Người tạo", "Quản lý trực tiếp của người tạo") hoặc một
                    người/nhóm anh/chị tự thêm ở bảng danh sách người nhận phía trên.
                </div>
                <div class="permmap-msg" id="notificationMapMsg"></div>
            </div>`;
        notificationMapPanel.hidden = false;
        thinkingBox.before(notificationMapPanel);
        autoGrowCells(notificationMapPanel);
    }

    // THÊM/XÓA DÒNG + ĐÓNG/MỞ ô chọn — ủy quyền trên PANEL vì renderNotificationMap thay sạch innerHTML.
    if (notificationMapPanel) {
        notificationMapPanel.addEventListener("click", function (e) {
            const trigger = e.target.closest(".ms-combo-trigger");
            const add = e.target.closest(".notifmap-add");
            const recipientAdd = e.target.closest(".notifrecip-add");
            // Nút xóa của bảng danh sách người nhận phải xét TRƯỚC: nó dùng chung class .entitymap-del với
            // nút xóa dòng của bảng thông báo, nên nhánh dưới sẽ nuốt cú bấm (không tìm ra .notifmap-row rồi
            // lặng lẽ return) nếu nó chạy trước.
            const recipientRemove = e.target.closest(".notifrecip-del");
            const remove = e.target.closest(".entitymap-del");
            const msgEl = document.getElementById("notificationMapMsg");
            const note = text => { if (msgEl) msgEl.textContent = text; };

            // Lời hỏi lại "bấm × lần nữa" chỉ sống tới thao tác kế tiếp: người dùng bỏ ngang rồi vài phút
            // sau bấm × một cái là xóa ngay, trong khi họ tưởng cú bấm đó mới là cú thứ nhất.
            notificationMapPanel.querySelectorAll('.notifrecip-del[data-confirm="1"]').forEach(btn => {
                if (btn !== recipientRemove) delete btn.dataset.confirm;
            });

            if (recipientAdd) {
                const anchor = notificationMapPanel.querySelector(".notifrecip-addrow");
                if (!anchor) return;

                if (recipientListValues().length >= MAX_RECIPIENT_OPTIONS) {
                    note(`Danh sách đã tới trần ${MAX_RECIPIENT_OPTIONS} người nhận.`);
                    return;
                }

                anchor.insertAdjacentHTML("beforebegin", recipientListRow(""));
                focusNewRow(anchor.previousElementSibling, ".notifrecip-name");
                note("");
                return;
            }

            // XÓA một người nhận đang được dùng là xóa nó khỏi cả các ô To/CC đang chọn nó — và một dòng
            // mất người nhận cuối cùng sẽ rơi vào đúng trạng thái mà bất biến của bảng cấm ("cần gửi mà
            // chưa chọn ai"). Nên ca đó phải nói ra trước và đòi một cú bấm THỨ HAI, thay vì lặng lẽ làm
            // rồi để người dùng gặp lại nó ở popup lúc bấm gửi.
            if (recipientRemove) {
                const row = recipientRemove.closest(".notifrecip-row");
                if (!row) return;

                const value = tableValue(row, ".notifrecip-name");
                const used = recipientUsage(value);
                if (used > 0 && recipientRemove.dataset.confirm !== "1") {
                    recipientRemove.dataset.confirm = "1";
                    note(`“${value}” đang được chọn ở ${used} ô người nhận — bấm × lần nữa để xóa khỏi cả các ô đó.`);
                    return;
                }

                row.remove();
                syncRecipientOptions();
                note("");
                return;
            }

            if (trigger) {
                const combo = trigger.closest(".notifmap-pick");
                const wasOpen = combo.classList.contains("open");
                closeRecipientPickers();
                if (!wasOpen) {
                    combo.classList.add("open");
                    combo.querySelector(".ms-combo-panel").hidden = false;
                }
                return;
            }

            if (remove) {
                const row = remove.closest(".notifmap-row");
                if (row) row.remove();
                note("");
                return;
            }

            if (!add) return;

            const body = notificationMapPanel.querySelector(".notifmap-table tbody");
            const anchor = notificationMapPanel.querySelector(".notifmap-addrow");
            if (!body || !anchor) return;

            if (body.querySelectorAll(".notifmap-row").length >= MAX_NOTIF_ROWS) {
                note(`Bảng đã tới trần ${MAX_NOTIF_ROWS} sự kiện — nhiều hơn thì không rà nổi trong một lượt.`);
                return;
            }

            anchor.insertAdjacentHTML("beforebegin", notificationRow(null, recipientOptions()));
            focusNewRow(anchor.previousElementSibling, ".notifmap-nameinput");
            note("");
        });

        // Giá trị TRƯỚC khi sửa của một ô tên người nhận — phải chụp lúc con trỏ vào ô, vì lúc `change` bắn
        // ra thì ô đã mang chữ mới và không còn gì để nối các lựa chọn cũ về mục mới.
        notificationMapPanel.addEventListener("focusin", function (e) {
            const nameCell = e.target.closest(".notifrecip-name");
            if (nameCell) nameCell.dataset.prev = tableValue(nameCell.closest(".notifrecip-row"), ".notifrecip-name");
        });

        // Nhãn của ô chọn phải kể đúng thứ đang được chọn: đây là dòng chữ DUY NHẤT người dùng đọc khi rà
        // lại cả bảng (panel đóng), nên để nó lệch là để họ gửi đi một bảng khác với bảng họ tưởng.
        notificationMapPanel.addEventListener("change", function (e) {
            const nameCell = e.target.closest(".notifrecip-name");
            if (nameCell) {
                renameRecipient(nameCell);
                return;
            }

            const box = e.target.closest(".notifmap-pick input[type=checkbox]");
            if (!box) return;

            const pick = box.closest(".notifmap-pick");
            const boxes = Array.from(pick.querySelectorAll("input[type=checkbox]"));
            if (box.checked && boxes.filter(b => b.checked).length > MAX_NOTIF_RECIPIENTS) {
                box.checked = false;
                const msgEl = document.getElementById("notificationMapMsg");
                if (msgEl) msgEl.textContent = `Một ô chỉ nhận tối đa ${MAX_NOTIF_RECIPIENTS} người nhận.`;
            }

            boxes.forEach(b => b.closest(".ms-combo-option").classList.toggle("selected", b.checked));
            updateRecipientPickLabel(pick);
        });
    }

    // SỬA CHỮ một người nhận. Hai giá trị bị từ chối và cùng trả ô về chữ cũ, vì cả hai đều làm hỏng đúng
    // mối nối mà bảng danh sách sinh ra để giữ:
    //  • RỖNG — các ô đang chọn mục này mất người nhận trong im lặng; muốn bỏ hẳn thì có nút × (nó còn hỏi
    //    lại khi mục đang được dùng);
    //  • TRÙNG một dòng khác — hai dòng cùng một mục lúc so khớp, nên một trong hai biến mất khỏi danh sách
    //    và người dùng không biết dòng nào còn hiệu lực.
    function renameRecipient(nameCell) {
        const row = nameCell.closest(".notifrecip-row");
        const msgEl = document.getElementById("notificationMapMsg");
        const note = text => { if (msgEl) msgEl.textContent = text; };

        const previous = (nameCell.dataset.prev || "").trim();
        let value = tableValue(row, ".notifrecip-name");

        const duplicated = value.length > 0
            && Array.from(notificationMapPanel.querySelectorAll(".notifrecip-row")).some(other =>
                other !== row && normalizeRecipient(tableValue(other, ".notifrecip-name")) === normalizeRecipient(value));

        if (duplicated) {
            note(`“${value}” đã có trong danh sách rồi.`);
            value = previous;
        } else if (value.length === 0 && previous.length > 0) {
            note("Tên người nhận không được để trống — muốn bỏ hẳn thì bấm × ở cuối dòng.");
            value = previous;
        } else {
            note("");
        }

        if (nameCell.value !== value) {
            nameCell.value = value;
            autoGrowCell(nameCell);
        }
        nameCell.dataset.prev = value;

        if (value !== previous) syncRecipientOptions(previous, value);
    }

    // Bấm ra ngoài thì đóng: ô chọn mở đè lên các dòng dưới nó, để mở là che mất đúng phần bảng người dùng
    // đang rà.
    function closeRecipientPickers() {
        if (!notificationMapPanel) return;
        notificationMapPanel.querySelectorAll(".notifmap-pick.open").forEach(c => {
            c.classList.remove("open");
            c.querySelector(".ms-combo-panel").hidden = true;
        });
    }

    document.addEventListener("click", function (e) {
        if (!e.target.closest(".notifmap-pick")) closeRecipientPickers();
    });

    // ==== NHÁP ĐANG GÕ: tự lưu để F5 / mất điện / bấm nhầm không cuốn mất một câu trả lời dài ====
    // Ở trang này người dùng thường gõ những đoạn RẤT DÀI trong một lượt (cả quy trình nghiệp vụ, ai làm
    // bước nào, ràng buộc gì…). Trước đây nội dung đó chỉ sống trong DOM: F5, đóng tab nhầm, máy sập hay
    // một cú bấm vào link là gõ lại từ đầu — và đó đúng là kiểu mất mát khiến người dùng thôi kể chi tiết.
    // Nháp được ghi vào localStorage theo TỪNG project (nhiều project mở song song không đè nhau) và đổ
    // lại vào ô nhập khi mở lại trang.
    //
    // Vì sao KHÔNG xóa nháp ngay lúc bấm gửi: lượt gửi có thể chết dọc đường (stream đứt trước frame đầu,
    // rồi postback dự phòng cũng trượt vì mạng rớt) — xóa sớm là mất bản gõ dở đúng vào lúc cần nó nhất.
    // Nháp chỉ được ĐÁNH DẤU "đã gửi", và bị bỏ khi biết lượt đó tới đích: frame `done` về, hoặc mở lại
    // trang thấy chính nó đã nằm ở lượt user cuối trong hội thoại.
    const DRAFT_PREFIX = "req-chat-draft:";
    const DRAFT_BATCH_PREFIX = "req-batch-draft:";
    // Debounce ngắn: mỗi phím gõ hẹn lại một lần ghi, nên máy sập bất ngờ (không kịp bắn pagehide) cùng
    // lắm mất khoảng này chứ không mất cả đoạn.
    const DRAFT_DEBOUNCE_MS = 400;
    const DRAFT_TTL_MS = 14 * 24 * 60 * 60 * 1000;

    const draftProjectId = (chatForm.querySelector('input[name="projectId"]') || {}).value || "";
    const draftKey = DRAFT_PREFIX + draftProjectId;
    const draftBatchKey = DRAFT_BATCH_PREFIX + draftProjectId;

    // Chế độ riêng tư / storage bị chặn / quota đầy: mọi hàm dưới đây im lặng bỏ qua. Không lưu được nháp
    // thì cùng lắm quay về hành vi cũ — tuyệt đối không được làm hỏng khung chat.
    function draftRead(key) {
        try {
            const raw = localStorage.getItem(key || draftKey);
            if (!raw) return null;
            const obj = JSON.parse(raw);
            return obj && typeof obj === "object" ? obj : null;
        } catch { return null; }
    }

    function draftSave(text) {
        try {
            if (!(text || "").trim()) {
                // Ô nhập rỗng KHÔNG luôn có nghĩa "không còn gì để giữ": ngay sau khi bấm gửi ô nhập được
                // dọn trắng trong lúc dấu "đã gửi" vẫn phải nằm lại chờ xác nhận lượt tới đích — mà đường
                // postback dự phòng thì điều hướng trang, kéo theo pagehide → chính hàm này. Chỉ xóa khi
                // nháp đang giữ là nháp thường (người dùng tự xóa hết chữ).
                const saved = draftRead();
                if (!saved || !saved.submitted) localStorage.removeItem(draftKey);
                return;
            }
            localStorage.setItem(draftKey, JSON.stringify({ text, at: Date.now(), submitted: false }));
        } catch { }
    }

    function draftClear() {
        try { localStorage.removeItem(draftKey); } catch { }
    }

    // Đánh dấu "đã gửi": giữ nguyên nội dung để phục hồi được nếu lượt gửi trượt, nhưng lần mở trang sau
    // sẽ đối chiếu với hội thoại trước khi đổ lại (xem draftRestore).
    function draftMarkSubmitted(text) {
        clearTimeout(draftTimer); // lần ghi đang hẹn sẽ thấy ô nhập rỗng — đừng để nó chạy sau dấu này
        try {
            localStorage.setItem(draftKey, JSON.stringify({ text, at: Date.now(), submitted: true }));
        } catch { }
    }

    // Lượt gửi đã tới đích (frame `done`) → bỏ dấu "đã gửi". Nếu trong lúc chờ người dùng đã gõ tiếp một
    // câu MỚI thì autosave đã ghi đè bằng nháp thường (submitted = false) và nháp đó phải sống.
    function draftClearIfSubmitted() {
        const saved = draftRead();
        if (saved && saved.submitted) draftClear();
    }

    let draftTimer = null;
    function draftSaveSoon() {
        clearTimeout(draftTimer);
        draftTimer = setTimeout(() => draftSave(messageInput.value), DRAFT_DEBOUNCE_MS);
    }

    function draftFlush() {
        clearTimeout(draftTimer);
        draftSave(messageInput.value);
        draftBatchFlush();
    }

    // Nháp của THẺ HỎI GỘP: các ô trả lời trên thẻ cũng là chỗ người dùng gõ dài (câu mở có ô 3 dòng mời
    // "kể chi tiết"), và cũng bay sạch khi F5. Lưu theo map câu-hỏi → câu-trả-lời: thẻ được server render
    // lại nguyên vẹn sau khi tải trang, nên khớp lại bằng chính nội dung câu hỏi là đủ và không phụ thuộc
    // thứ tự.
    //
    // Mỗi câu lưu HAI vế riêng (`picks` = chip đang sáng, `other` = lời tự nhập) chứ không lưu câu trả lời
    // đã ghép: ghép rồi thì lúc đổ về không tách lại được đâu là chip đâu là lời viết thêm, và bản phục hồi
    // sẽ đẩy cả cụm vào ô "Ý khác" — người dùng thấy nguyên văn gợi ý nằm trong ô mình chưa từng gõ.
    function draftBatchAnswers() {
        if (!batchPanel || batchPanel.hidden) return null;
        const map = {};
        batchPanel.querySelectorAll(".batchq-item").forEach(li => {
            const question = li.dataset.question || "";
            if (!question) return;
            const picks = batchPicks(li);
            const other = batchOtherText(li);
            if (picks.length > 0 || other) map[question] = { picks, other };
        });
        return map;
    }

    function draftBatchSave() {
        const map = draftBatchAnswers();
        try {
            if (!map || Object.keys(map).length === 0) {
                localStorage.removeItem(draftBatchKey);
                return;
            }
            localStorage.setItem(draftBatchKey, JSON.stringify({ at: Date.now(), answers: map }));
        } catch { }
    }

    let draftBatchTimer = null;
    function draftBatchSaveSoon() {
        clearTimeout(draftBatchTimer);
        draftBatchTimer = setTimeout(draftBatchSave, DRAFT_DEBOUNCE_MS);
    }

    function draftBatchFlush() {
        clearTimeout(draftBatchTimer);
        draftBatchSave();
    }

    // Thẻ đã gửi / đã xếp lại / bị thay bằng lượt gộp mới → nháp của thẻ cũ vô nghĩa.
    function draftBatchClear() {
        clearTimeout(draftBatchTimer);
        try { localStorage.removeItem(draftBatchKey); } catch { }
    }

    // Nháp của các project khác đã quá cũ: dọn để localStorage không phình mãi theo số project từng mở.
    function draftPruneStale() {
        try {
            const dead = [];
            for (let i = 0; i < localStorage.length; i++) {
                const k = localStorage.key(i);
                if (!k || (k.indexOf(DRAFT_PREFIX) !== 0 && k.indexOf(DRAFT_BATCH_PREFIX) !== 0)) continue;
                let at = 0;
                try { at = (JSON.parse(localStorage.getItem(k)) || {}).at || 0; } catch { at = 0; }
                if (!at || Date.now() - at > DRAFT_TTL_MS) dead.push(k);
            }
            dead.forEach(k => localStorage.removeItem(k));
        } catch { }
    }

    // Nội dung lượt USER cuối cùng đang hiển thị: dùng để biết một nháp "đã gửi" có thật sự tới đích chưa.
    function lastUserMessageText() {
        const bubbles = chatMessages.querySelectorAll(".req-msg.you > p");
        const last = bubbles[bubbles.length - 1];
        return last ? (last.textContent || "").trim() : "";
    }

    // Băng thông báo trên khung soạn: phải NÓI RA việc vừa phục hồi, không thì người dùng gặp một ô nhập
    // tự dưng có chữ và không hiểu vì sao (tưởng đã gửi, hoặc gửi lại lần hai).
    // Nút "Xóa nội dung này" chỉ dọn Ô NHẬP, nên chỉ hiện khi có nội dung được đổ vào đó — phục hồi các ô
    // trên thẻ hỏi thì người dùng sửa/xóa ngay tại từng ô, một nút xóa chung ở đây sẽ mờ nghĩa.
    function showDraftNote(message, withDiscard) {
        const note = document.getElementById("draftRestoredNote");
        if (!note) return;
        const textEl = note.querySelector(".draft-restored-text");
        if (textEl) textEl.textContent = message;
        const discardBtn = note.querySelector("#draftDiscardBtn");
        if (discardBtn) discardBtn.hidden = withDiscard !== true;
        note.hidden = false;
    }

    function hideDraftNote() {
        const note = document.getElementById("draftRestoredNote");
        if (note) note.hidden = true;
    }

    function draftStampText(at) {
        const when = at ? new Date(at) : null;
        if (!when || isNaN(when.getTime())) return "";
        return " (lưu lúc " + when.toLocaleString("vi-VN", {
            hour: "2-digit", minute: "2-digit", day: "2-digit", month: "2-digit"
        }) + ")";
    }

    function draftRestore() {
        draftPruneStale();

        let composerLead = "";
        let stampAt = 0;

        const saved = draftRead();
        if (saved && typeof saved.text === "string" && saved.text.trim()) {
            // Nháp "đã gửi" mà nội dung của nó ĐÃ nằm ở lượt user cuối ⇒ lượt gửi tới đích rồi (F5 giữa
            // lúc BA đang trả lời cũng rơi vào đây) → bỏ đi, không đổ lại kẻo người dùng gửi trùng. Còn
            // nếu KHÔNG khớp thì đúng là lượt gửi đã trượt: giữ lại chính là lý do cơ chế này tồn tại.
            if (saved.submitted && lastUserMessageText() === saved.text.trim()) {
                draftClear();
            } else if (!messageInput.value.trim()) {
                messageInput.value = saved.text;
                resizeMessageInput();
                stampAt = saved.at || 0;
                composerLead = saved.submitted
                    ? "Lượt gửi vừa rồi không tới được máy chủ nên nội dung anh/chị đã gõ được giữ lại"
                    : "Đã phục hồi nội dung anh/chị đang gõ dở";
            }
        }

        const restoredBatch = draftBatchRestore();
        if (restoredBatch > 0 && !stampAt) {
            const batchSaved = draftRead(draftBatchKey);
            stampAt = batchSaved ? (batchSaved.at || 0) : 0;
        }

        if (!composerLead && restoredBatch === 0) return;

        let message;
        if (composerLead && restoredBatch > 0) {
            message = `${composerLead}, kèm ${restoredBatch} câu trả lời trên thẻ hỏi`;
        } else if (composerLead) {
            message = composerLead;
        } else {
            message = `Đã phục hồi ${restoredBatch} câu trả lời anh/chị đang gõ dở trên thẻ hỏi`;
        }

        showDraftNote(message + draftStampText(stampAt) + ". Anh/chị xem lại rồi bấm gửi.",
            composerLead !== "");
    }

    // Một mục nháp, đọc về dạng {picks, other}. Nháp lưu TRƯỚC khi chip tách khỏi ô là một chuỗi câu trả
    // lời đã ghép — đổ nó về ô "Ý khác" là bản phục hồi trung thực nhất còn có thể: chữ người dùng đã gõ
    // không mất, và không có chip nào bị bật lên thay họ.
    function draftBatchEntry(raw) {
        if (typeof raw === "string") return { picks: [], other: raw.trim() };
        if (!raw || typeof raw !== "object") return { picks: [], other: "" };
        return {
            picks: Array.isArray(raw.picks) ? raw.picks.map(x => String(x).trim()).filter(Boolean) : [],
            other: typeof raw.other === "string" ? raw.other.trim() : ""
        };
    }

    // Đổ nháp về các ô trả lời trên thẻ hỏi gộp. Trả về số câu đã phục hồi.
    function draftBatchRestore() {
        if (!batchPanel || batchPanel.hidden) return 0;

        const saved = draftRead(draftBatchKey);
        const answers = saved && saved.answers && typeof saved.answers === "object" ? saved.answers : null;
        if (!answers) return 0;

        let restored = 0;
        batchPanel.querySelectorAll(".batchq-item").forEach(li => {
            const saved = draftBatchEntry(answers[li.dataset.question || ""]);
            const box = li.querySelector(".batchq-answer");
            if (!box || (saved.picks.length === 0 && !saved.other)) return;

            // Câu người dùng đã đụng vào trong phiên NÀY thắng nháp cũ — kể cả khi họ mới chỉ bấm chip.
            if (box.value.trim() || batchPicks(li).length > 0) return;

            li.querySelectorAll(".batchq-choice").forEach(chip => {
                chip.classList.toggle("is-on", saved.picks.includes((chip.dataset.value || "").trim()));
            });
            box.value = saved.other;
            restored++;

            // Câu MỞ không nằm trong khung nhãn-nổi: ô của nó đã sẵn 3 dòng mời "kể chi tiết", tự co lại
            // theo một câu ngắn vừa phục hồi là thu hẹp đúng cái ô đang mời người dùng viết dài.
            if (box.closest(".batchq-other-field")) autoGrowOtherBox(box);
        });

        if (restored > 0) updateBatchSendButton();
        return restored;
    }

    messageInput.addEventListener("input", draftSaveSoon);

    // Ghi NGAY khi trang có nguy cơ biến mất: debounce không kịp nếu người dùng bấm F5 / đóng tab ngay sau
    // ký tự cuối. pagehide phủ cả điều hướng thường lẫn tab bị hệ điều hành thu hồi (Safari/iOS không bắn
    // beforeunload); visibilitychange phủ trường hợp chuyển tab rồi máy sập sau đó.
    window.addEventListener("pagehide", draftFlush);
    document.addEventListener("visibilitychange", function () {
        if (document.visibilityState === "hidden") draftFlush();
    });

    // "Xóa nội dung này": người dùng có thể đã gõ lại câu khác ở nơi khác, hoặc chỉ muốn ô nhập sạch — cho
    // họ một cú bấm để dứt điểm thay vì phải bôi đen xóa cả đoạn dài.
    const draftNoteEl = document.getElementById("draftRestoredNote");
    if (draftNoteEl) {
        draftNoteEl.addEventListener("click", function (e) {
            if (!e.target.closest("#draftDiscardBtn")) return;
            messageInput.value = "";
            resizeMessageInput();
            draftClear();
            hideDraftNote();
            messageInput.focus();
        });
    }

    draftRestore();

    // ==== Panel "Tiến độ khai thác" + "Điều đã chốt" (cột trái) — cập nhật live từ frame done ====
    // Markup phải khớp bản server render trong Index.cshtml.
    const coverageIcons = { "RÕ": "✅", "MỘT PHẦN": "🟡", "KHÔNG ÁP DỤNG": "➖" };

    // Tóm tắt + câu của người dùng mà kết luận dựa vào, gộp vào tooltip của dòng (trước đây trích dẫn
    // đứng thành dòng riêng dưới nhãn — ở bề rộng sidebar nó luôn bị cắt giữa chừng và hay lặp cùng một
    // câu ở nhiều nhóm). Phải khớp CoverageTooltip() bên Index.cshtml.
    const coverageTooltip = x => (x.evidence ? `${x.summary || ""}\nDựa vào: ${x.evidence}` : (x.summary || ""));

    // stale = lượt chắt lọc bản đồ của lượt vừa rồi đã lỗi (server đã thử lại): danh sách dưới đây là bản
    // CŨ, chưa gộp câu trả lời vừa gửi — và BA cũng vừa dẫn lượt bằng đúng bản cũ đó. Phải nói ra: triệu
    // chứng của nó (tiến độ đứng im + BA hỏi lại nhóm vừa trả lời) trông y hệt "BA không nghe mình nói".
    function renderCoverage(items, stale) {
        const panel = document.getElementById("coveragePanel");
        const list = document.getElementById("coverageList");
        if (!panel || !list) return;

        const staleBox = document.getElementById("coverageStale");
        if (staleBox) staleBox.hidden = stale !== true;
        if (stale === true) panel.hidden = false;

        // Bản đồ rỗng (lượt distill hỏng ở ngay lượt đầu) ⇒ giữ nguyên thứ đang hiện: với dự án mới đó là
        // KHUNG 12 nhóm [CHƯA HỎI] server đã render (CoverageChecklist), xoá đi thì panel trống trơn.
        if (!Array.isArray(items) || items.length === 0) return;

        // Mẫu số là TỔNG số nhóm và nhóm "KHÔNG ÁP DỤNG" tính là đã xong cho thanh — khớp CoverageProgress
        // ở server (Index.cshtml render lần đầu bằng đúng công thức này).
        const total = items.length;
        const clear = items.filter(x => x.status === "RÕ").length;
        const notApplicable = items.filter(x => x.status === "KHÔNG ÁP DỤNG").length;

        // Dòng CHỈ ĐỌC — không còn nút "chưa đúng?" cho từng nhóm (xem chú thích ở Index.cshtml): đính
        // chính đi qua chat, lượt chắt lọc kế tiếp hạ nhóm tương ứng xuống [MỘT PHẦN].
        list.innerHTML = items.map(x => `
            <li class="coverage-item ${x.status === "KHÔNG ÁP DỤNG" ? "na" : ""}" title="${escapeHtml(coverageTooltip(x))}">
                <span class="cov-ico">${coverageIcons[x.status] || "⚪"}</span>
                <span class="cov-label">${escapeHtml(x.label)}</span>
            </li>
        `).join("");

        const fill = document.getElementById("coverageBarFill");
        if (fill) fill.style.width = total === 0 ? "0%" : `${Math.round((clear + notApplicable) * 100 / total)}%`;
        const text = document.getElementById("coverageProgressText");
        if (text) text.textContent = `Đã rõ ${clear}/${total} nhóm`;

        // Dòng "N nhóm không áp dụng" (chú thích cho các dòng ➖ và cho việc thanh đầy khi clear < total).
        const na = document.getElementById("coverageNa");
        const naCount = document.getElementById("coverageNaCount");
        if (naCount) naCount.textContent = notApplicable;
        if (na) na.hidden = notApplicable === 0;

        // Dòng .coverage-hint ("chỗ nào chưa đúng thì nói trong chat") do server render và đúng ở MỌI
        // trạng thái bản đồ, nên hàm này không đụng tới nó.

        panel.hidden = false;
    }

    // ==== CỔNG TẠO TÀI LIỆU (#writeReqZone) ====
    // Cụm cuối khung chat: nút tạo tài liệu → cổng soát mâu thuẫn. Đây là chỗ DUY NHẤT có nút sinh Product
    // Brief (sidebar không còn nút nào).
    //
    // KHÔNG còn "bản tổng kết trước khi tạo tài liệu" (danh sách nhật ký quyết định kèm ✎ Sửa / "Gửi đính
    // chính") — xem lý do ở Index.cshtml. Nhật ký vẫn được chắt sau mỗi lượt nhưng chỉ còn người đọc là
    // máy, nên client không nhận frame "decisions" nữa và cổng chỉ còn MỘT thứ để vẽ: trạng thái.
    //
    // Đặt làm khối CUỐI khung chat, cùng chỗ với cổng xác nhận giả định, vì cùng một lý do: quy trình đang
    // đứng chờ người dùng, nên câu hỏi và nút trả lời phải nằm cùng chỗ mắt đang nhìn (chat tự cuộn đáy).
    // Markup phải khớp bản server render trong Index.cshtml.
    //
    // gateState — "waiting" | "ready" | "running" | "done". Suy từ cờ mời của lượt BA mới nhất (frame done),
    // chỉ được sửa qua setWriteReqInvited rồi gọi syncWriteReqGate(). Giá trị đầu lấy thẳng từ bản server
    // vừa render (data-state) chứ không đoán qua "khối đang ẩn hay hiện": server có những trạng thái mà JS
    // không tự suy lại được ("done" — vừa soạn xong và hội thoại chưa có gì mới), và F5 phải giữ nguyên chúng.
    const writeReqZone = document.getElementById("writeReqZone");
    let gateState = writeReqZone?.dataset.state || "waiting";
    // Bản Brief đã tồn tại chưa (server render). Chỉ đổi khi một vòng soạn chạy xong, mà lúc đó
    // requirement-workflow.js tải lại trang ⇒ đọc một lần là đủ.
    const draftExists = writeReqZone?.dataset.draftExists === "true";

    // Cờ mời của lượt BA mới nhất + cờ readiness của bản đồ bao phủ (cả hai từ frame done).
    function setWriteReqInvited(invited, coverageReady) {
        // VÒNG SOẠN ĐANG CHẠY ⇒ giữ nguyên "running", bất kể lượt chat vừa rồi có mời hay không: người dùng
        // vẫn chat được trong lúc tài liệu đang soạn, và mở cổng ở đây sẽ mở lại đúng cửa spam mà trạng thái
        // "running" của server vừa đóng. Run xong thì requirement-workflow.js tải lại trang và server render
        // lại cổng theo trạng thái mới.
        if (gateState === "running") return;

        // Đã có lượt chat mới ⇒ hội thoại không còn "chưa có gì mới kể từ lần soạn gần nhất", nên cổng rời
        // trạng thái "done" và đi theo lượt vừa rồi.
        //
        // ĐƯỜNG LÙI KHI BẢN BRIEF ĐÃ CŨ (khớp nhánh cùng tên ở Index.cshtml): bản Brief đã có, người dùng
        // vừa nhắn một lời đính chính, BA đáp bằng một CÂU HỎI thay vì lời mời ⇒ chỉ xét cờ mời thì cổng
        // đóng và không còn đường nào soạn lại bản Brief đang cũ dần so với hội thoại. Cờ readiness do
        // SERVER tính (BAChatTurnResult.CoverageReady) — luật "mọi dòng áp dụng đã [RÕ]" không được phép
        // có bản sao trong JS. Chưa có draft thì cổng vẫn đi theo đúng lời mời của BA như cũ.
        gateState = (invited === true || (draftExists && coverageReady === true)) ? "ready" : "waiting";
        syncWriteReqGate();
    }

    // Nguồn duy nhất vẽ ra cổng. Viết dạng TOÀN PHẦN (mọi trạng thái đều có nhánh) chứ không vá từng phần:
    // một hàm chỉ vá mẩu trạng thái nó vừa đổi thì kiểu gì cũng có tổ hợp không ai vẽ đúng.
    function syncWriteReqGate() {
        const gate = document.getElementById("summaryGate");
        if (!gate || !writeReqZone) return;

        writeReqZone.dataset.state = gateState;

        // Ghi chú ở sidebar chỉ đúng khi CHƯA đủ thông tin: đủ rồi thì lời mời đã nằm trong chat, nhắc lại
        // ở cột bên kia là thừa.
        const waitingHint = document.getElementById("writeReqWaitingHint");
        if (waitingHint) waitingHint.hidden = gateState !== "waiting";

        const conflictPanel = document.getElementById("conflictPanel");

        // Dời CẢ CỤM xuống cuối dòng hội thoại (cùng cách renderSuggestions dời khay chip): các bong bóng
        // mới được chèn vào ngay trước #thinkingBox, nên một khối đứng yên ở vị trí tĩnh sẽ bị các lượt sau
        // vượt qua — lời mời tạo tài liệu nổi lên phía trên chính câu trả lời vừa sinh ra nó. Dời wrapper
        // chứ không dời từng khối, để panel mâu thuẫn không lạc khỏi nút đã bật nó lên.
        // Dời KỂ CẢ khi cổng đang đóng: "cụm này luôn là khối cuối" phải đúng ở mọi lượt, không chỉ ở lượt
        // mời. Cụm đóng nằm lại giữa hội thoại thì hôm nay vô hại (cả hai con đều ẩn) nhưng biến vị trí của
        // nó thành thứ phụ thuộc vào lịch sử các lượt trước — thêm một khối thấy được vào cụm là lộ ra ngay.
        thinkingBox.before(writeReqZone);

        if (gateState !== "ready") {
            // BA quay lại hỏi tiếp (vd vừa phát hiện mâu thuẫn từ chính đính chính user vừa gửi) ⇒ lời mời
            // cũ không còn đúng nữa, để lại là nói dối. Panel mâu thuẫn đóng theo: nó là câu hỏi phát sinh
            // từ một cú bấm nay đã hết hiệu lực.
            gate.hidden = true;
            if (conflictPanel) conflictPanel.hidden = true;
            return;
        }

        const form = gate.querySelector("form.write-req");
        if (form) form.className = `write-req write-req-${gateState}`;

        const btn = gate.querySelector(".write-req-btn");
        if (btn) {
            // Dựng lại nhãn "nghỉ" từ bản gốc server render: lúc này nhãn có thể đang là "Đang tạo tài
            // liệu…" (submit vừa rồi bị cổng mâu thuẫn chặn) hay "Đang soát…". data-idle-label là nhãn
            // nghỉ hiện hành — cổng soát mâu thuẫn đọc lại nó để khôi phục sau khi soát xong.
            btn.disabled = false;
            btn.textContent = btn.dataset.readyLabel || "Tạo bản mô tả sản phẩm";
            btn.dataset.idleLabel = btn.textContent;
            btn.title = btn.dataset.readyTitle || "";
        }

        gate.hidden = false;
    }

    // KHÔNG còn cơ chế ghi chú/đính chính trên bản tổng kết ở đây (syncSummaryGateBar, summaryGateNotes,
    // nút nổi "✎ Ghi chú đoạn này" khi bôi đen, sendSummaryGateNotes): bản tổng kết đã gỡ, xem lý do ở
    // Index.cshtml. Cổng nay chỉ có MỘT nút và nó là nút submit thật của form.write-req, nên cú bấm đi
    // thẳng qua hộp xác nhận ghi đè (initRegenerateConfirm) rồi cổng soát mâu thuẫn (initConflictGate) —
    // cả hai đều là listener trên chính nút/form đó, không cần listener nào của riêng cổng.
    // Đính chính đi qua khung chat như mọi điều khác; đường ghi chú trên bản xem trước Product Brief
    // (initBriefNotes) vẫn còn và đó mới là chỗ đoạn văn thật sự dài.

    // ==== SỬA lượt vừa gửi ====
    // Bong bóng user CUỐI CÙNG có nút "✎ sửa": nội dung được nạp vào ô nhập, gửi lại sẽ GHI ĐÈ lượt đó
    // (server xóa câu trả lời cũ và kéo lùi các con trỏ gộp) thay vì thêm một lượt mới. Không có đường
    // này, một câu gõ nhầm chỉ sửa được bằng cách nhắn thêm câu đính chính — nhưng bản đồ bao phủ và
    // nhật ký điều đã chốt đã kịp ghi nhận câu sai, và chúng gộp lũy tiến nên câu sai không mất đi.
    let editingBubble = null;

    function exitEditMode() {
        if (editingBubble) editingBubble.classList.remove("is-editing");
        editingBubble = null;
        chatForm.classList.remove("chat-editing");
    }

    // Ghi đè bong bóng đang sửa + gỡ câu trả lời cũ ngay trên màn hình (server sắp xóa đúng lượt đó).
    function applyEditToBubble(text) {
        const bubble = editingBubble;
        exitEditMode();
        if (!bubble) return;

        const p = bubble.querySelector("p");
        if (p) p.textContent = text;

        // Câu trả lời cũ nằm ngay sau bong bóng user (kèm nhãn "BA" phía trước nó).
        let next = bubble.nextElementSibling;
        while (next && !next.classList.contains("req-msg") && !next.classList.contains("req-who")) {
            next = next.nextElementSibling;
        }
        if (next && next.classList.contains("req-who")) {
            const label = next;
            next = next.nextElementSibling;
            label.remove();
        }
        // Câu trả lời cũ là THẺ HỎI GỘP (lượt gộp không có bong bóng riêng — câu dẫn nằm trong thẻ): thẻ
        // là phần tử cố định của trang mà renderBatchQuestions dùng lại, gỡ khỏi DOM là mất luôn chỗ neo.
        if (next === batchPanel) {
            hideBatchQuestions();
            return;
        }
        // Bảng cột cũng là một .req-msg.ba nhưng KHÔNG phải câu trả lời của lượt nào: nó treo cho tới khi
        // file được chốt. Gỡ nó ở đây là vừa mất bảng vừa mất chỗ neo cố định của trang.
        if (next === columnMapPanel) return;
        // Bảng phân quyền: y hệt: treo cho tới khi dự án chốt bảng, và là chỗ neo cố định của trang.
        if (next === permMapPanel) return;
        if (next && next.classList.contains("req-msg") && next.classList.contains("ba")) next.remove();
    }

    chatMessages.addEventListener("click", function (e) {
        const btn = e.target.closest(".chat-edit-btn");
        if (!btn || chatBusy) return;

        editingBubble = btn.closest(".req-msg.you");
        if (editingBubble) editingBubble.classList.add("is-editing");
        chatForm.classList.add("chat-editing");

        messageInput.value = btn.dataset.message || "";
        resizeMessageInput();
        messageInput.focus();
        messageInput.setSelectionRange(messageInput.value.length, messageInput.value.length);
    });

    // Escape = bỏ sửa (ô nhập trở lại trạng thái gửi lượt mới).
    messageInput.addEventListener("keydown", function (e) {
        if (e.key === "Escape" && editingBubble) {
            exitEditMode();
            messageInput.value = "";
            resizeMessageInput();
        }
    });

    // KHÔNG còn handler cho nút "chưa đúng?" của panel bản đồ bao phủ — nút đã gỡ (xem Index.cshtml).
    // Người dùng đính chính bằng câu của họ trong chat; lượt chắt lọc bản đồ hạ nhóm tương ứng xuống
    // [MỘT PHẦN] và cổng tạo tài liệu đóng theo frame `done` của lượt đó, không cần đường riêng.

    // KHÔNG có hàm render nào cho "triển vọng phỏng vấn" (InterviewOutlookService) nữa: cả ba danh sách
    // đều không có panel — openQuestions đi vào ngữ cảnh chat của BA ở lượt sau, plannedScope đi vào ngữ
    // cảnh soát mâu thuẫn, workedExamples đi vào "## 13. Worked Examples" của spec. Vì thế ChatStream
    // cũng thôi gửi frame "outlook".

    // Bấm một "giả định của bản thiết kế" (E) → soạn sẵn tin nhắn đính chính; gửi đi sẽ soạn lại tài liệu
    // và dựng lại POC cho khớp giả định đã sửa (đóng vòng trước khi bản demo bị coi là chốt).
    const assumptionPanelEl = document.getElementById("assumptionPanel");
    if (assumptionPanelEl) {
        assumptionPanelEl.addEventListener("click", function (e) {
            const item = e.target.closest(".assumption-item");
            if (!item) return;
            messageInput.value = `Giả định "${item.dataset.assumption}" chưa đúng. Thực tế là: `;
            resizeMessageInput();
            messageInput.focus();
            messageInput.setSelectionRange(messageInput.value.length, messageInput.value.length);
        });
    }

    // Bấm "chưa đúng?" trên MỘT bước của sơ đồ luồng → soạn sẵn tin nhắn đính chính đúng bước đó vào ô
    // nhập, thay vì bắt user tự mô tả lại cả luồng. Sơ đồ nằm trong bubble BA (thêm động vào chatMessages)
    // nên bắt sự kiện ở mức chatMessages (delegated) để áp cho cả sơ đồ server-render lẫn client-render.
    if (chatMessages) {
        chatMessages.addEventListener("click", function (e) {
            const fix = e.target.closest(".flow-step-fix");
            if (!fix) return;
            messageInput.value = `Bước "${fix.dataset.step}" trong sơ đồ luồng chưa đúng. Ý đúng của tôi là: `;
            resizeMessageInput();
            messageInput.focus();
            messageInput.setSelectionRange(messageInput.value.length, messageInput.value.length);
        });
    }

    // Sơ đồ luồng nghiệp vụ (chỉ ở lượt mời "Write Requirement"): render trong bubble BA để user xác
    // nhận trực quan. Markup khớp bản server render trong Index.cshtml. Xóa sơ đồ của lượt cũ trước khi
    // vẽ để chỉ lượt mới nhất còn hiện (như chip gợi ý).
    function renderFlowDiagram(bubble, steps) {
        chatMessages.querySelectorAll(".flow-diagram").forEach(el => el.remove());
        if (!Array.isArray(steps) || steps.length === 0) return;

        const rows = steps.map(s => `
            <li class="flow-step">
                ${s.actor ? `<span class="flow-actor">${escapeHtml(s.actor)}</span>` : ""}
                <span class="flow-action">${escapeHtml(s.action || "")}</span>
                ${s.outcome ? `<span class="flow-outcome">${escapeHtml(s.outcome)}</span>` : ""}
                <button type="button" class="flow-step-fix" data-step="${escapeHtml(s.action || "")}" title="Bấm nếu bước này chưa đúng để đính chính ngay trong chat">chưa đúng?</button>
            </li>
        `).join("");

        bubble.insertAdjacentHTML("beforeend", `
            <div class="flow-diagram" aria-label="Sơ đồ luồng nghiệp vụ để xác nhận">
                <div class="flow-diagram-title">Luồng nghiệp vụ chính — anh/chị xem giúp đã đúng chưa nhé:</div>
                <ol class="flow-steps">${rows}</ol>
            </div>
        `);
    }

    // Tiền tố lượt BA "lời gọi AI thất bại" — khớp ConversationTranscriptBuilder.LlmFailurePrefix phía
    // server. Lượt như vậy được lưu DB như lượt thường (done ok=true) nên phải nhận diện bằng nội dung.
    const LLM_FAILURE_PREFIX = "⚠️ Lời gọi AI thất bại";

    function finishTurn(data) {
        const bubble = ensureLiveBubble();
        const p = bubble.querySelector("p");
        bubble.classList.remove("streaming");

        // Lượt mới đã chốt ⇒ mọi nút "Thử lại" của các lượt cũ hết hiệu lực (server chỉ retry được lượt CUỐI).
        chatMessages.querySelectorAll(".chat-retry-btn").forEach(b => b.remove());

        if (data.ok) {
            // Bản preview đã stream có thể khác bản chốt (lời mời bị cổng readiness thay bằng câu hỏi)
            // → luôn thay bằng bản chốt.
            p.textContent = data.reply || "";
            renderSuggestions(data.suggestions, data.suggestionsMultiSelect === true);
            setComposerOpenEnded(data.openEnded === true);
            renderCoverage(data.coverage, data.coverageStale === true);
            // Cổng tạo tài liệu chỉ mở ở lượt BA MỜI tạo tài liệu — cùng cờ mời điều khiển cả việc mở cổng
            // lẫn nhãn nút bên trong, nên hai thứ không thể vênh nhau.
            setWriteReqInvited(data.invitesWriteRequirement === true, data.coverageReady === true);
            renderFlowDiagram(bubble, data.flowDiagram);
            // Bảng phân quyền: lượt chốt nhóm phân quyền. Không dựng trong `bubble` mà vào panel cố định
            // của trang (như bảng cột) — bảng treo tới khi dự án chốt nó, sống lâu hơn lượt sinh ra nó.
            renderPermissionMatrix(data.permissionMatrix);
            // Năm bảng còn lại, cùng luật: InterviewTableGate đảm bảo mỗi lượt nhiều nhất MỘT trong sáu
            // danh sách này có nội dung, nên sáu lời gọi liên tiếp không bao giờ dựng hai bảng cùng lúc.
            renderFlowMap(data.flowMap);
            renderScreenScope(data.screenScopeMap, data.uncoveredFlowSteps);
            renderEntityMap(data.entityMap);
            renderReportMap(data.reportMap, data.reportEntityOptions);
            renderNotificationMap(data.notificationMap, data.recipientOptions);

            // Lượt lỗi LLM: tô đỏ + nút "Thử lại" (server xóa lượt lỗi rồi chạy lại, khỏi gõ lại câu hỏi)
            // — markup khớp bản server render trong Index.cshtml.
            if ((data.reply || "").startsWith(LLM_FAILURE_PREFIX)) {
                bubble.classList.add("chat-error");
                bubble.insertAdjacentHTML("beforeend",
                    `<button type="button" class="btn outline small chat-retry-btn" title="Chạy lại lượt trả lời vừa lỗi — không cần gõ lại câu hỏi">↻ Thử lại</button>`);
            }

            // SAU CÙNG vì thẻ hỏi NUỐT bong bóng vừa stream làm câu dẫn của nó (absorbLeadBubble): mọi
            // thứ còn ghi vào `bubble` — sơ đồ luồng, nút "Thử lại" — phải xong trước, không thì ghi vào
            // một node đã rời khỏi DOM.
            renderBatchQuestions(data.questions, bubble);
        } else {
            bubble.classList.add("chat-error");
            p.textContent = data.error || "Có lỗi khi xử lý lượt chat. Vui lòng thử lại.";
        }

        thinkingBox.style.display = "none";
        liveBubble = null;
        chatBusy = false;
        scrollToBottom();
    }

    // Trả về true nếu đây là frame NỘI DUNG (không phải nhịp tim/khung rỗng) — tức server đã thật sự
    // nhận và đang xử lý lượt này. Nhịp tim không tính: nó có thể tới trước khi lượt user kịp lưu.
    function handleFrame(raw) {
        // Frame SSE: các dòng "data: {json}"; bỏ qua comment (": ping") và event end.
        const lines = raw.split("\n");
        let json = "";
        for (const line of lines) {
            if (line.startsWith("data: ")) json += line.slice(6);
        }
        if (!json) return false;

        let ev;
        try { ev = JSON.parse(json); } catch { return false; }

        if (ev.type === "ping") return false; // nhịp tim: chỉ để biết kết nối còn sống

        if (ev.type === "status") {
            setThinkingText(ev.text || "BA đang xử lý…");
        } else if (ev.type === "token") {
            const bubble = ensureLiveBubble();
            bubble.querySelector("p").textContent += ev.text || "";
            scrollToBottom();
        } else if (ev.type === "done") {
            sawDone = true;
            // Lượt đã được server lưu ⇒ bỏ nháp "đã gửi" (nếu trong lúc chờ người dùng đã gõ câu mới thì
            // nháp mới đó không bị xóa — xem draftClearIfSubmitted).
            draftClearIfSubmitted();
            finishTurn(ev);
        }
        // KHÔNG còn nhánh cho frame "decisions": nhật ký quyết định vẫn được gộp ở hậu kỳ lượt chat nhưng
        // không còn mặt UI nào để vá (bản tổng kết đã gỡ), nên server cũng không đẩy frame đó về nữa.
        return true;
    }

    // File đã đính kèm nhưng CHƯA gửi (staged): initSourceDropPaste đổ vào đây khi user đính kèm/dán/kéo-thả.
    // Mỗi phần tử { file, url } — url là objectURL để xem trước (chỉ ảnh; file PDF/bảng tính có url = null).
    // Khi bấm gửi mà mảng này khác rỗng, form ưu tiên upload file (kèm ghi chú trong ô nhập) thay vì chat.
    const stagedImages = [];
    // Do initSourceDropPaste gán: upload các file đang staged kèm ghi chú (text) rồi reload.
    let sendStagedImages = null;

    // true khi lượt đang gửi đã nhận ĐƯỢC ít nhất một frame SSE. Không nhận frame nào mà stream vẫn
    // "kết thúc bình thường" là một kiểu đứt im lặng nữa (proxy đệm rồi đóng, response rỗng): fetch
    // không báo lỗi, nên phải tự coi là hỏng để rơi vào nhánh phục hồi thay vì đứng im.
    // KHÔNG dùng để quyết định có gửi lại hay không — không lượt nào được gửi lại, xem nhánh catch.
    let sawFrame = false;
    // true khi đã nhận frame "done" — tức lượt đã CHỐT. Stream kết thúc mà thiếu nó (proxy đóng kết nối
    // im lặng, server bị kill giữa chừng) là kết thúc GIẢ: đọc xong không lỗi, không exception, nhưng
    // lượt chưa xong. Không kiểm tra cờ này thì chatBusy kẹt ở true và spinner "BA đang soạn câu trả
    // lời…" quay vĩnh viễn — đúng triệu chứng người dùng báo.
    let sawDone = false;

    async function streamChat(text, retry, edit) {
        const fd = new FormData();
        fd.append("projectId", chatForm.querySelector('input[name="projectId"]').value);
        fd.append("message", text);
        if (retry) fd.append("retry", "true");
        // edit: server ghi ĐÈ lượt user cuối rồi trả lời lại, thay vì thêm một lượt mới.
        if (edit) fd.append("edit", "true");
        const token = chatForm.querySelector('input[name="__RequestVerificationToken"]');
        if (token) fd.append("__RequestVerificationToken", token.value);

        // Đồng hồ canh stream "im lặng": mỗi lần có dữ liệu về (kể cả nhịp tim) thì hẹn lại giờ; quá
        // STREAM_IDLE_TIMEOUT_MS không nghe thấy gì ⇒ abort để reader.read() reject và nhánh catch của
        // người gọi phục hồi. Nếu không abort, một kết nối chết âm thầm (mất Wi-Fi, proxy cắt) sẽ giữ
        // promise treo mãi và khóa cứng khung chat.
        const controller = new AbortController();
        let idleTimer = null;
        const armIdleTimer = () => {
            clearTimeout(idleTimer);
            idleTimer = setTimeout(() => controller.abort(), STREAM_IDLE_TIMEOUT_MS);
        };

        try {
            armIdleTimer();
            const response = await fetch(STREAM_URL, {
                method: "POST",
                body: fd,
                headers: { Accept: "text/event-stream" },
                signal: controller.signal
            });
            if (!response.ok || !response.body) throw new Error("stream request failed");

            const reader = response.body.getReader();
            const decoder = new TextDecoder();
            let buffer = "";

            while (true) {
                const { done, value } = await reader.read();
                if (done) break;
                armIdleTimer();

                buffer += decoder.decode(value, { stream: true });
                let idx;
                while ((idx = buffer.indexOf("\n\n")) >= 0) {
                    const frame = buffer.slice(0, idx);
                    buffer = buffer.slice(idx + 2);
                    if (handleFrame(frame)) sawFrame = true;
                }
            }

            return sawFrame;
        } finally {
            clearTimeout(idleTimer);
        }
    }

    chatForm.addEventListener("submit", function (e) {
        e.preventDefault();

        // Băng "đã phục hồi nháp" nói về nội dung đang nằm trong ô nhập — bấm gửi là hết chuyện.
        hideDraftNote();

        // Có ảnh đã đính kèm chờ gửi → gửi ảnh (kèm ghi chú đang gõ trong ô nhập, nếu có) qua luồng
        // UploadSource thay vì gửi tin nhắn chat. Ảnh có thể gửi mà không cần ghi chú.
        if (stagedImages.length > 0) {
            if (chatBusy || !sendStagedImages) return;
            sendStagedImages(messageInput.value.trim());
            return;
        }

        const text = messageInput.value.trim();
        if (!text || chatBusy) return;

        // Người dùng quay ra gõ tay trong khi thẻ hỏi gộp còn mở nghĩa là họ đã chọn đường kia — gỡ thẻ,
        // vì để cả hai cùng sống thì một cú bấm "Gửi" sau đó sẽ gửi lại các câu họ vừa trả lời bằng tay.
        hideBatchQuestions();

        // BẢNG CỘT thì NGƯỢC LẠI: gõ một câu đính chính bản đọc lại ("Item Type mình cũng dùng") không hề
        // thay thế việc chốt phạm vi cột, nên bảng phải sống tiếp — chỉ dời xuống cuối dòng hội thoại để
        // các lượt mới (chèn trước thinkingBox) không đẩy nó trôi lên trên. Server cũng giữ bảng theo đúng
        // luật này: nó treo tới khi FILE được chốt, không phải tới lượt kế tiếp.
        if (columnMapPanel && !columnMapPanel.hidden) thinkingBox.before(columnMapPanel);
        // Bảng phân quyền cùng luật: gõ thêm một câu ("thiếu vai trò Admin") không thay thế việc chốt bảng,
        // nên bảng sống tiếp và chỉ dời xuống cuối dòng hội thoại.
        if (permMapPanel && !permMapPanel.hidden) thinkingBox.before(permMapPanel);

        // Đang SỬA lượt vừa gửi: không thêm bong bóng mới — ghi đè bong bóng cũ và gỡ câu trả lời cũ
        // (server cũng xóa đúng lượt assistant đó), rồi gửi kèm cờ edit.
        const editing = editingBubble !== null;

        chatBusy = true;
        sawFrame = false;
        sawDone = false;
        if (editing) {
            applyEditToBubble(text);
        } else {
            appendUserBubble(text);
        }

        messageInput.value = "";
        resizeMessageInput();

        // Giữ lại nội dung vừa gửi dưới dạng nháp "đã gửi" cho tới khi biết lượt tới đích (frame done, hoặc
        // lần mở trang sau thấy nó đã nằm trong hội thoại): gửi trượt vì mạng thì vẫn còn đường lấy lại.
        draftMarkSubmitted(text);

        // Lượt đã được trả lời → ẩn các gợi ý cũ ngay (gợi ý mới render lại ở frame done nếu có), và trả
        // ô nhập về placeholder mặc định: lời mời "kể tự do" là của câu hỏi vừa được trả lời xong.
        hideSuggestions();
        setComposerOpenEnded(false);

        setThinkingText("BA is analyzing requirements...");
        thinkingBox.style.display = "block";
        scrollToBottom();

        streamChat(text, false, editing).then(function (gotFrame) {
            if (!gotFrame) throw new Error("no frame");
            if (!sawDone) throw new Error("stream ended without done");
        }).catch(function () {
            if (!chatBusy) return; // done đã xử lý xong, lỗi chỉ là đuôi stream — bỏ qua

            // Stream hỏng kiểu gì cũng RELOAD, không bao giờ gửi lại: lượt chat chạy với
            // CancellationToken.None nên server có thể đã nhận và đang chạy trọn lượt này dù client
            // không nghe thấy gì (proxy đệm cả response ⇒ không frame nào về, đồng hồ canh bắn abort
            // sau STREAM_IDLE_TIMEOUT_MS). Gửi lại lúc đó là nhân đôi lượt user + lời gọi LLM.
            // Reload phủ trọn cả hai khả năng: lượt ĐÃ tới đích thì trang hiện bản đã lưu (và
            // ChatReplyStatus lo phần "BA đang soạn…" / mời Thử lại nếu lượt chết); lượt CHƯA tới thì
            // nháp "đã gửi" không khớp lượt user cuối nên draftRestore đổ lại nội dung vào ô nhập.
            location.reload();
        });
    });

    // "Thử lại" một lượt BA bị lỗi LLM: server XÓA lượt lỗi rồi chạy lại lượt chat trên transcript hiện
    // có (không thêm lượt user nào) — cùng đường SSE như một lượt thường. Bubble lỗi được gỡ ngay (server
    // sắp xóa bản ghi tương ứng); stream hỏng thì reload — trang sẽ hiển thị đúng trạng thái đã lưu,
    // KHÔNG re-submit vì retry không có message để post lại.
    chatMessages.addEventListener("click", function (e) {
        const btn = e.target.closest(".chat-retry-btn");
        if (!btn || chatBusy) return;

        chatBusy = true;
        sawFrame = false;
        sawDone = false;

        const failedBubble = btn.closest(".req-msg.ba");
        if (failedBubble) {
            // Gỡ cả nhãn "BA" đứng ngay trước bong bóng lỗi — ensureLiveBubble sẽ chèn nhãn mới cho lượt
            // chạy lại, không thì hai chữ "BA" chồng nhau.
            const label = failedBubble.previousElementSibling;
            if (label && label.classList.contains("req-who")) label.remove();
            failedBubble.remove();
        }
        hideSuggestions();
        setComposerOpenEnded(false);

        setThinkingText("BA đang thử trả lời lại…");
        thinkingBox.style.display = "block";
        scrollToBottom();

        streamChat("", true, false).then(function (gotFrame) {
            if (!gotFrame) throw new Error("no frame");
            if (!sawDone) throw new Error("stream ended without done");
        }).catch(function () {
            if (!chatBusy) return;
            location.reload();
        });
    });

    // Lượt trả lời đang chờ đã CHẾT (server báo stale, hoặc hết hạn chờ): mở khóa khung chat và để lại
    // một bong bóng lỗi có nút "Thử lại" — server chạy lại đúng lượt user còn "cụt" đó, người dùng không
    // phải gõ lại câu hỏi. Trước đây chỗ này chỉ quay spinner vô hạn: màn hình đứng ở "BA đang soạn câu
    // trả lời…", F5 cũng không thoát vì lượt user vẫn nằm cuối hội thoại, và ô nhập bị khóa (chatBusy).
    function recoverFromDeadReply(reason) {
        thinkingBox.style.display = "none";
        setThinkingText("BA is analyzing requirements...");
        chatBusy = false;

        // Chỉ để MỘT bong bóng phục hồi (F5 liên tục không được cộng dồn).
        chatMessages.querySelectorAll(".chat-dead-reply").forEach(el => {
            const label = el.previousElementSibling;
            if (label && label.classList.contains("req-who")) label.remove();
            el.remove();
        });

        thinkingBox.insertAdjacentHTML("beforebegin", `
            <b class="req-who">BA</b>
            <div class="req-msg ba chat-error chat-dead-reply">
                <p style="white-space: pre-wrap;">${escapeHtml(reason)}</p>
                <button type="button" class="btn outline small chat-retry-btn" title="Chạy lại lượt trả lời vừa hỏng — không cần gõ lại câu hỏi">↻ Thử lại</button>
            </div>
        `);
        scrollToBottom();
        messageInput.focus();
    }

    // ==== Khôi phục sau khi F5 GIỮA lúc BA đang trả lời ====
    // Nếu tải lại trang khi lượt hội thoại mới nhất còn là của user (BA chưa kịp lưu câu trả lời — vẫn
    // đang sinh nền với CancellationToken.None), khung chat sẽ THIẾU bong bóng trả lời. Hiện lại dòng
    // "BA đang soạn…" và hỏi server (ChatReplyStatus) theo nhịp cho tới khi câu trả lời đã được lưu, rồi
    // tải lại để render bản chốt (bong bóng BA + gợi ý + các panel). Chặn gửi lượt mới trong lúc chờ để
    // không tạo hai lượt chạy song song.
    // Server còn trả cờ "stale" khi lượt chờ đó KHÔNG bao giờ về đích (không tiến trình nào đang chạy nó
    // — vd server khởi động lại giữa chừng, hoặc lỗi hạ tầng nuốt mất cả lượt ⚠️ đóng lượt): dừng chờ
    // ngay và mời "Thử lại", thay vì khóa khung chat suốt nhiều phút rồi bỏ mặc.
    if (chatMessages.dataset.replyPending === "true") {
        const pendingProjectId = chatForm.querySelector('input[name="projectId"]').value;
        chatBusy = true;
        hideSuggestions();
        setThinkingText("BA đang soạn câu trả lời…");
        thinkingBox.style.display = "block";
        scrollToBottom();

        let pendingAttempts = 0;
        const pendingMaxAttempts = 160; // ~160 × 2.5s ≈ 6-7 phút: dư cho một lượt trả lời dài.
        const pollReply = async function () {
            pendingAttempts++;
            try {
                const res = await fetch(
                    `/Requirements/ChatReplyStatus?projectId=${encodeURIComponent(pendingProjectId)}`,
                    { headers: { Accept: "application/json" } });
                if (res.ok) {
                    const data = await res.json();
                    if (!data.pending) {
                        // Câu trả lời đã được lưu → tải lại để render bản chốt do server dựng.
                        location.reload();
                        return;
                    }
                    if (data.stale) {
                        recoverFromDeadReply(
                            "⚠️ Lượt trả lời trước bị gián đoạn (mất kết nối hoặc server khởi động lại) nên "
                            + "không hoàn tất. Anh/chị bấm \"Thử lại\" để mình trả lời câu vừa rồi, hoặc cứ nhắn tiếp bình thường.");
                        return;
                    }
                }
            } catch (_) {
                // Lỗi mạng tạm thời: thử lại ở nhịp sau.
            }
            if (pendingAttempts < pendingMaxAttempts) {
                setTimeout(pollReply, 2500);
            } else {
                recoverFromDeadReply(
                    "⚠️ Chờ quá lâu mà chưa nhận được câu trả lời. Anh/chị bấm \"Thử lại\" để mình trả lời "
                    + "câu vừa rồi, hoặc cứ nhắn tiếp bình thường.");
            }
        };
        // Hỏi NGAY lần đầu: server tự phân biệt "đang chạy" với "đã chết" nên không cần chờ lấy lệ —
        // trạng thái kẹt được mở khóa trong tích tắc thay vì bắt người dùng nhìn spinner rồi mới biết.
        pollReply();
    }

    // Chọn một đáp án gợi ý: chế độ thường = điền sẵn rồi gửi ngay; chế độ chọn nhiều (multi) =
    // toggle chọn/bỏ, gom lại và gửi MỘT tin nhắn khi bấm "Gửi các lựa chọn".
    function selectSuggestion(option) {
        if (isMultiSelect()) {
            const nowSelected = option.classList.toggle("selected");
            option.setAttribute("aria-selected", nowSelected ? "true" : "false");
            updateMultiSendState();
            return;
        }

        const text = (option?.dataset.suggestion || "").trim();
        if (!text) return;

        // Chip BẤT ĐỒNG: mở ô nhập ngay tại chỗ thay vì gửi đi một lượt "Không, tính khác" trần. Xem khối
        // "Ô Ý khác" phía trên cho lý do đầy đủ.
        if (isDissentChip(text)) {
            primeOtherInput(text);
            return;
        }

        messageInput.value = text;
        chatForm.requestSubmit();
    }

    // Chế độ chọn NHIỀU: text tự nhập là một lựa chọn nữa, nối vào cuối các ô đã tích. Đây là lý do ô này
    // KHÔNG có nút gửi riêng ở chế độ đó — "Gửi các lựa chọn" phải gửi đi trọn vẹn thứ đang hiện trên màn
    // hình, chứ không bỏ rơi ô người dùng vừa gõ.
    function sendSelectedSuggestions() {
        const values = selectedSuggestionValues();
        const typed = otherAnswerText();
        if (typed) values.push(typed);
        if (values.length === 0) return;

        messageInput.value = values.join(", ");
        chatForm.requestSubmit();
    }

    if (suggestionList) {
        // Trang vừa tải với lượt hỏi có chip (server render) → gắn ô "Ý khác" + nút gửi cho danh sách có sẵn.
        ensureOtherControls();
        ensureMultiControls();
        // Ô mở sẵn ⇒ nút "Gửi câu trả lời" của nó cũng có mặt ngay từ đầu: phải khoá đúng trạng thái (ô
        // rỗng, chưa có chip nào đỡ phía sau) chứ không để một nút bấm được mà bấm vào thì không gửi gì.
        updateOtherSendState();

        suggestionList.addEventListener("click", function (e) {
            if (e.target.closest("#suggestionMultiSendBtn")) {
                sendSelectedSuggestions();
                return;
            }

            if (e.target.closest(".suggestion-other-send")) {
                sendOtherAnswer();
                return;
            }

            const option = e.target.closest(".suggestion-option");
            if (!option) return;

            selectSuggestion(option);
        });

        // Ô để trống vẫn gửi được (rơi về chip đã bấm) nên nút chỉ khoá khi KHÔNG có chip nào đỡ phía sau;
        // ở chế độ chọn nhiều thì từng phím gõ còn mở/khoá nút "Gửi các lựa chọn".
        suggestionList.addEventListener("input", function (e) {
            if (!e.target.classList.contains("suggestion-other-input")) return;
            autoGrowOtherBox(e.target);
            updateOtherSendState();
            updateMultiSendState();
        });

        // Enter gửi luôn (Shift+Enter xuống dòng) — cùng phím tắt với ô nhập của khung chat, vì đây đang là
        // chỗ trả lời của lượt. Ở chế độ chọn nhiều thì Enter để dành cho nút "Gửi các lựa chọn".
        suggestionList.addEventListener("keydown", function (e) {
            if (!e.target.classList.contains("suggestion-other-input")) return;
            if (e.key !== "Enter" || e.shiftKey || e.isComposing) return;
            e.preventDefault();
            if (isMultiSelect()) sendSelectedSuggestions();
            else sendOtherAnswer();
        });

        // Phím tắt số (1–9) chọn nhanh đáp án — giống option-select của Claude. Chỉ bắt khi
        // danh sách đang hiện và con trỏ KHÔNG ở ô nhập, để không cướp phím số khi đang soạn tin.
        // Ở chế độ multi, phím số TOGGLE lựa chọn và Enter gửi các lựa chọn đã chọn.
        document.addEventListener("keydown", function (e) {
            if (!suggestionList || suggestionList.style.display === "none") return;
            if (e.ctrlKey || e.metaKey || e.altKey) return;

            const active = document.activeElement;
            if (active && (active.tagName === "TEXTAREA" || active.tagName === "INPUT")) return;

            if (e.key === "Enter" && isMultiSelect()) {
                e.preventDefault();
                sendSelectedSuggestions();
                return;
            }

            if (e.key < "1" || e.key > "9") return;

            const options = suggestionList.querySelectorAll(".suggestion-option");
            const index = Number(e.key) - 1;
            if (index >= options.length) return;

            e.preventDefault();
            selectSuggestion(options[index]);
        });
    }

    // ==== Đính kèm / dán / kéo-thả tài liệu — xem trước trong khung chat rồi mới gửi ====
    // Người dùng nghiệp vụ hay chụp màn hình Excel/biểu mẫu — bắt họ đi qua một form upload riêng ở
    // sidebar là ma sát thừa (form đó đã bị bỏ; đây là lối đính kèm DUY NHẤT). Đính kèm (nút), dán
    // (Ctrl+V) hoặc kéo-thả file vào khung chat sẽ STAGE file ngay trên ô nhập — ảnh thành thumbnail,
    // PDF/bảng tính thành chip tên file: user gõ thêm ghi chú, xoá bớt, rồi bấm gửi mới thật sự upload
    // qua endpoint UploadSource (kèm ghi chú) → BA tóm tắt → reload.

    // Danh sách định dạng phải khớp ProjectSourceIngestor (ảnh PNG/JPG/WebP/GIF, PDF, .docx/.docm, .xlsx/.xlsm/.csv):
    // lọc ngay ở client để user biết file không hỗ trợ TRƯỚC khi upload, thay vì nhận lỗi sau một vòng POST.
    const SUPPORTED_DOC_EXTS = [".pdf", ".docx", ".docm", ".xlsx", ".xlsm", ".csv"];

    function isImageFile(f) {
        return !!(f.type && f.type.startsWith("image/"));
    }

    function isSupportedFile(f) {
        if (isImageFile(f)) return true;
        const name = (f.name || "").toLowerCase();
        return SUPPORTED_DOC_EXTS.some(ext => name.endsWith(ext));
    }

    (function initSourceDropPaste() {
        const token = chatForm.querySelector('input[name="__RequestVerificationToken"]');
        const projectIdInput = chatForm.querySelector('input[name="projectId"]');
        const preview = document.getElementById("attachPreview");
        if (!token || !projectIdInput) return;

        let uploading = false;
        const defaultPlaceholder = messageInput.placeholder;

        const ATTACH_PLACEHOLDER = "Thêm ghi chú cho tài liệu (không bắt buộc) rồi bấm gửi…";

        // Vẽ lại khay xem trước từ stagedImages. Ảnh giữ kèm objectURL để thu hồi khi gỡ (tránh rò bộ
        // nhớ) và hiện thumbnail; PDF/bảng tính không có bản xem trước nên hiện chip tên file.
        // Ẩn khay + trả lại placeholder gốc khi không còn file nào.
        function renderPreview() {
            if (!preview) return;

            if (stagedImages.length === 0) {
                preview.innerHTML = "";
                preview.hidden = true;
                messageInput.placeholder = defaultPlaceholder;
                return;
            }

            preview.innerHTML = stagedImages.map((item, i) => {
                const name = item.file.name || (item.url ? "ảnh" : "tệp");
                const body = item.url
                    ? `<img src="${item.url}" alt="${escapeHtml(name)}" />`
                    : `<span class="attach-thumb-name">📄 ${escapeHtml(name)}</span>`;
                return `
                <div class="attach-thumb ${item.url ? "" : "attach-thumb-file"}" title="${escapeHtml(name)}">
                    ${body}
                    <button type="button" class="attach-thumb-remove" data-i="${i}" aria-label="Gỡ tệp này">×</button>
                </div>
            `;
            }).join("");
            preview.hidden = false;
        }

        // Nhận file từ nút đính kèm / dán / kéo-thả. File không hỗ trợ bị loại và báo ngay tên cụ thể.
        function stageImages(fileList) {
            const all = Array.from(fileList || []);
            if (all.length === 0) return;

            const accepted = all.filter(isSupportedFile);
            const rejected = all.filter(f => !isSupportedFile(f));

            // Ảnh mới có objectURL (xem trước được); PDF/bảng tính để url = null → hiện chip tên file.
            accepted.forEach(file => stagedImages.push({
                file,
                url: isImageFile(file) ? URL.createObjectURL(file) : null
            }));

            if (rejected.length > 0) {
                alert("Không hỗ trợ định dạng của: " + rejected.map(f => f.name || "tệp không tên").join(", ")
                    + ".\nChỉ nhận ảnh (PNG/JPG/WebP/GIF), PDF, Word (.docx) hoặc bảng tính (.xlsx/.xlsm/.csv).");
            }
            if (accepted.length === 0) return;

            renderPreview();
            messageInput.placeholder = ATTACH_PLACEHOLDER;
            messageInput.focus();
        }

        function clearStaged() {
            stagedImages.forEach(item => { if (item.url) URL.revokeObjectURL(item.url); });
            stagedImages.length = 0;
            renderPreview();
        }

        // Gỡ MỘT file khỏi khay (thu hồi objectURL của đúng ảnh đó, nếu có).
        if (preview) {
            preview.addEventListener("click", function (e) {
                const btn = e.target.closest(".attach-thumb-remove");
                if (!btn) return;
                const idx = Number(btn.dataset.i);
                if (Number.isNaN(idx) || idx < 0 || idx >= stagedImages.length) return;
                if (stagedImages[idx].url) URL.revokeObjectURL(stagedImages[idx].url);
                stagedImages.splice(idx, 1);
                renderPreview();
            });
        }

        // Gửi các file đang staged (kèm ghi chú tùy chọn) qua UploadSource → BA tóm tắt → reload.
        // Gán ra ngoài để listener submit của form gọi được.
        sendStagedImages = async function (note) {
            if (stagedImages.length === 0 || uploading) return;

            uploading = true;
            chatBusy = true;

            // Hiện NGAY bong bóng của user (ảnh + ghi chú) rồi mới để BA đọc — trải nghiệm giống chat
            // thường (tin của mình lên khung trước), thay vì đứng nhìn spinner rồi cả trang reload.
            const optimisticBubble = appendUserImageBubble(note, stagedImages);

            // Dọn ô nhập + khay xem trước ngay như vừa gửi tin. KHÔNG revoke objectURL ở đây: bong bóng
            // lạc quan vừa chèn còn đang dùng chúng cho tới khi reload (hoặc bị gỡ khi lỗi).
            messageInput.value = "";
            resizeMessageInput();
            if (preview) {
                preview.innerHTML = "";
                preview.hidden = true;
            }
            messageInput.placeholder = defaultPlaceholder;

            setThinkingText("BA đang đọc tài liệu…");
            thinkingBox.style.display = "block";
            scrollToBottom();

            const fd = new FormData();
            fd.append("projectId", projectIdInput.value);
            fd.append("__RequestVerificationToken", token.value);
            if (note) fd.append("note", note);
            stagedImages.forEach(item => fd.append("files", item.file, item.file.name || "anh-dan.png"));

            try {
                const resp = await fetch("/Requirements/UploadSource", { method: "POST", body: fd });
                // Endpoint trả về redirect→trang Index; reload để hiện tài liệu mới + lượt tóm tắt của BA.
                if (resp.ok || resp.redirected) {
                    clearStaged();
                    // Ghi chú đã đi cùng tài liệu lên server → nháp của nó hết việc. Nhánh lỗi bên dưới thì
                    // KHÔNG xóa: ghi chú được trả lại ô nhập để gửi lại, nháp phải còn đó nếu user F5.
                    draftClear();
                    location.reload();
                    return;
                }
                throw new Error("upload failed");
            } catch {
                // Hoàn tác: gỡ bong bóng lạc quan, khôi phục khay đính kèm + ghi chú để user thử lại.
                if (optimisticBubble) optimisticBubble.remove();
                thinkingBox.style.display = "none";
                uploading = false;
                chatBusy = false;
                if (note) {
                    messageInput.value = note;
                    resizeMessageInput();
                }
                renderPreview();
                messageInput.placeholder = ATTACH_PLACEHOLDER;
                alert("Không tải được tài liệu lên. Anh/chị kiểm tra kết nối rồi bấm gửi lại giúp mình.");
            }
        };

        // Dán (Ctrl+V): chỉ chặn sự kiện khi clipboard có FILE ĐƯỢC HỖ TRỢ — dán một file lạ (vd .docx)
        // không được nuốt mất thao tác dán text kèm theo.
        messageInput.addEventListener("paste", function (e) {
            const items = e.clipboardData && e.clipboardData.files;
            if (items && items.length > 0 && Array.from(items).some(isSupportedFile)) {
                e.preventDefault();
                stageImages(items);
            }
        });

        const chatPanel = chatMessages.closest(".chat-panel") || chatMessages;
        ["dragover", "dragenter"].forEach(ev => chatPanel.addEventListener(ev, function (e) {
            if (e.dataTransfer && Array.from(e.dataTransfer.types || []).includes("Files")) {
                e.preventDefault();
                chatPanel.classList.add("drag-over");
            }
        }));
        ["dragleave", "dragend"].forEach(ev => chatPanel.addEventListener(ev, function (e) {
            if (e.target === chatPanel) chatPanel.classList.remove("drag-over");
        }));
        chatPanel.addEventListener("drop", function (e) {
            if (e.dataTransfer && e.dataTransfer.files && e.dataTransfer.files.length > 0) {
                e.preventDefault();
                chatPanel.classList.remove("drag-over");
                stageImages(e.dataTransfer.files);
            }
        });

        // Nút đính kèm trong khung soạn: mở hộp chọn file rồi STAGE file (xem trước) như dán/kéo-thả.
        // Điểm bấm rõ ràng cho người không biết mẹo dán/kéo-thả — và là lối đính kèm chính sau khi form
        // upload ở sidebar bị bỏ.
        const attachBtn = document.getElementById("attachImageBtn");
        const attachInput = document.getElementById("attachImageInput");
        if (attachBtn && attachInput) {
            attachBtn.addEventListener("click", () => attachInput.click());
            attachInput.addEventListener("change", function () {
                if (attachInput.files && attachInput.files.length > 0) {
                    stageImages(attachInput.files);
                    // Reset để chọn lại đúng file cũ vẫn kích hoạt 'change' lần sau.
                    attachInput.value = "";
                }
            });
        }
    })();

    // ==== Nói thay vì gõ (Web Speech API) ====
    // User nghiệp vụ "kể một mạch" bằng lời nhanh hơn gõ nhiều — đúng lượt mở đầu BA mời kể tự do.
    // Nhận dạng đổ DẦN vào ô nhập (giữ nguyên phần đã gõ trước đó); user vẫn sửa tay rồi tự bấm gửi.
    // Trình duyệt không hỗ trợ (Firefox…) thì nút giữ nguyên hidden — không đổi gì so với trước.
    (function initVoiceInput() {
        const voiceBtn = document.getElementById("voiceInputBtn");
        const SpeechRecognition = window.SpeechRecognition || window.webkitSpeechRecognition;
        if (!voiceBtn || !SpeechRecognition) return;

        voiceBtn.hidden = false;

        let recognition = null; // instance đang ghi âm; null = đang nghỉ
        let baseText = "";      // phần user đã gõ trước khi bấm ghi — luôn giữ nguyên ở đầu ô

        function stopRecording() {
            if (!recognition) return;
            try { recognition.stop(); } catch { /* đã dừng rồi thì thôi */ }
        }

        function setRecordingUi(on) {
            voiceBtn.classList.toggle("recording", on);
            voiceBtn.querySelector("i").className = on ? "bi bi-mic-fill" : "bi bi-mic";
            voiceBtn.title = on ? "Đang nghe… bấm để dừng" : "Nói thay vì gõ — bấm để bắt đầu/dừng ghi âm";
        }

        voiceBtn.addEventListener("click", function () {
            if (recognition) {
                stopRecording();
                return;
            }

            recognition = new SpeechRecognition();
            // Ngôn ngữ nhận dạng theo <html lang>; app nội bộ mặc định tiếng Việt.
            recognition.lang = document.documentElement.lang || "vi-VN";
            recognition.continuous = true;
            recognition.interimResults = true;

            baseText = messageInput.value ? messageInput.value.replace(/\s+$/, "") + " " : "";

            recognition.onresult = function (e) {
                let transcript = "";
                for (let i = 0; i < e.results.length; i++) {
                    transcript += e.results[i][0].transcript;
                }
                messageInput.value = baseText + transcript;
                resizeMessageInput();
            };
            // Lỗi (từ chối mic, mất mạng…) và kết thúc đều đưa nút về trạng thái nghỉ; text đã nhận vẫn ở ô nhập.
            recognition.onerror = stopRecording;
            recognition.onend = function () {
                recognition = null;
                setRecordingUi(false);
                messageInput.focus();
            };

            try {
                recognition.start();
                setRecordingUi(true);
            } catch {
                recognition = null;
                setRecordingUi(false);
            }
        });
    })();


}

// Sau khi gửi chat, server redirect và tải lại trang Index. Mặc định trình duyệt đặt
// khung hội thoại ở đầu, khiến user phải tự cuộn xuống để đọc câu trả lời mới của BA.
// Vì vậy luôn đưa khung chat xuống tin nhắn mới nhất ngay khi trang vừa tải.
if (chatMessages) {
    function scrollChatToBottom() {
        chatMessages.scrollTop = chatMessages.scrollHeight;
    }

    scrollChatToBottom();
    // Cuộn lại sau khi layout/asset (font, ảnh) ổn định để chắc chắn ở đáy.
    requestAnimationFrame(scrollChatToBottom);
    window.addEventListener("load", scrollChatToBottom);
}

async function loadDocPreview(previewEl) {
    if (!previewEl) return;

    const render = previewEl.querySelector(".doc-render");
    if (!render || render.dataset.loaded === "true") return;

    // Mark as loaded up-front so concurrent shows don't double-fetch.
    render.dataset.loaded = "true";

    const id = render.dataset.docId;

    try {
        const response = await fetch("/Requirements/DocumentPreview?id=" + encodeURIComponent(id));
        if (!response.ok) throw new Error("Preview request failed");
        const data = await response.json();
        render.innerHTML = data.html;
    } catch {
        render.dataset.loaded = "false";
        render.innerHTML = '<p class="doc-empty">Unable to load preview.</p>';
    }
}

// ==== Ghi chú trực tiếp trên bản xem trước Product Brief (bản draft) ====
// Bôi đen một đoạn trong bản mô tả → nút "＋ Ghi chú" nổi lên → nhập điều cần sửa; các ghi chú gom vào
// khay dưới modal, bấm "Gửi ghi chú cho BA sửa" sẽ chạy vòng SỬA CÓ PHẠM VI: bản brief hiện có được giữ
// nguyên, BA chỉ đụng các đoạn được chú (POST /Requirements/ReviseBrief → run "Write Requirement" mang
// theo chính các ghi chú này — xem ReviseBriefFromNotesUseCase). Người dùng chỉ vào chỗ cần sửa thay vì
// mô tả bằng lời cả đoạn, và không phải đọc lại cả tài liệu sau mỗi lần góp ý.
(function initBriefAnnotator() {
    const tray = document.getElementById("briefNotesTray");
    const listEl = document.getElementById("briefNotesList");
    const countEl = document.getElementById("briefNotesCount");
    const sendBtn = document.getElementById("briefNotesSendBtn");
    const content = document.querySelector(".requirement-content");
    if (!tray || !listEl || !sendBtn || !content) return;

    const notes = []; // { quote, note }
    let addBtn = null;
    let notePopover = null;
    let pendingQuote = "";

    function currentDraftRender() {
        // Chỉ cho ghi chú trên vùng preview của bản draft đang hiển thị.
        return Array.from(content.querySelectorAll('.doc-render[data-annotatable="true"]'))
            .find(el => el.offsetParent !== null) || null;
    }

    function renderNotes() {
        countEl.textContent = `(${notes.length})`;
        tray.hidden = notes.length === 0;
        sendBtn.hidden = notes.length === 0;
        listEl.innerHTML = notes.map((n, i) => `
            <li class="brief-note-item">
                ${n.quote ? `<span class="brief-note-quote">“${escapeHtml(n.quote)}”</span>` : ""}
                <span class="brief-note-text">${escapeHtml(n.note)}</span>
                <button type="button" class="brief-note-del" data-i="${i}" title="Xóa ghi chú">🗑</button>
            </li>
        `).join("");
    }

    function removeAddBtn() {
        if (addBtn) { addBtn.remove(); addBtn = null; }
    }

    function removeNotePopover() {
        if (notePopover) { notePopover.remove(); notePopover = null; }
        document.removeEventListener("mousedown", onOutsideMouseDown, true);
        document.removeEventListener("keydown", onPopoverKeyDown, true);
    }

    function onOutsideMouseDown(e) {
        if (notePopover && !notePopover.contains(e.target)) removeNotePopover();
    }

    function onPopoverKeyDown(e) {
        if (e.key === "Escape") { e.preventDefault(); removeNotePopover(); }
    }

    // Popover nhỏ ngay dưới đoạn bôi đen để nhập ghi chú — thay cho window.prompt() của trình duyệt.
    function openNotePopover(anchorRect, quote) {
        removeAddBtn();
        removeNotePopover();

        notePopover = document.createElement("div");
        notePopover.className = "brief-note-popover";
        notePopover.setAttribute("role", "dialog");
        notePopover.setAttribute("aria-label", "Ghi chú cho đoạn");
        notePopover.innerHTML = `
            <p class="brief-note-popover-title">Ghi chú cho đoạn</p>
            <p class="brief-note-popover-quote">“${escapeHtml(quote.slice(0, 160))}${quote.length > 160 ? "…" : ""}”</p>
            <label class="brief-note-popover-label" for="briefNotePopoverInput">Điều cần sửa là gì?</label>
            <textarea id="briefNotePopoverInput" class="brief-note-popover-input" rows="3"
                placeholder="Nhập điều cần sửa…"></textarea>
            <div class="brief-note-popover-actions">
                <button type="button" class="btn small" data-act="cancel">Hủy</button>
                <button type="button" class="btn primary small" data-act="save">Lưu ghi chú</button>
            </div>`;
        notePopover.style.position = "absolute";
        notePopover.style.zIndex = "10001";
        notePopover.style.visibility = "hidden";
        notePopover.style.top = "0";
        notePopover.style.left = "0";
        document.body.appendChild(notePopover);

        // Canh vị trí: mặc định ngay dưới đoạn bôi đen, không tràn mép phải/dưới của khung nhìn.
        const pw = notePopover.offsetWidth;
        const ph = notePopover.offsetHeight;
        const vw = document.documentElement.clientWidth;
        const vh = document.documentElement.clientHeight;
        let left = anchorRect.left;
        if (left + pw > vw - 12) left = vw - pw - 12;
        if (left < 12) left = 12;
        let top = anchorRect.bottom + 8;
        if (top + ph > vh - 12) top = Math.max(12, anchorRect.top - ph - 8); // không đủ chỗ bên dưới → lật lên trên
        notePopover.style.left = `${window.scrollX + left}px`;
        notePopover.style.top = `${window.scrollY + top}px`;
        notePopover.style.visibility = "";

        const input = notePopover.querySelector(".brief-note-popover-input");

        function commit() {
            const val = input.value.trim();
            removeNotePopover();
            window.getSelection().removeAllRanges();
            if (val) {
                notes.push({ quote, note: val });
                renderNotes();
            }
        }

        notePopover.querySelector('[data-act="save"]').addEventListener("click", commit);
        notePopover.querySelector('[data-act="cancel"]').addEventListener("click", function () {
            removeNotePopover();
            window.getSelection().removeAllRanges();
        });
        input.addEventListener("keydown", function (e) {
            if (e.key === "Enter" && (e.ctrlKey || e.metaKey)) { e.preventDefault(); commit(); } // Ctrl/⌘+Enter lưu nhanh
        });

        document.addEventListener("mousedown", onOutsideMouseDown, true);
        document.addEventListener("keydown", onPopoverKeyDown, true);
        input.focus();
    }

    function showAddButton(rect, quote) {
        removeAddBtn();
        pendingQuote = quote;
        addBtn = document.createElement("button");
        addBtn.type = "button";
        addBtn.className = "btn primary small brief-add-note-btn";
        addBtn.textContent = "＋ Ghi chú";
        addBtn.style.position = "absolute";
        addBtn.style.top = `${window.scrollY + rect.top - 38}px`;
        addBtn.style.left = `${window.scrollX + rect.left}px`;
        addBtn.style.zIndex = "10000";
        document.body.appendChild(addBtn);

        addBtn.addEventListener("click", function (e) {
            e.stopPropagation();
            openNotePopover(addBtn.getBoundingClientRect(), pendingQuote);
        });
    }

    document.addEventListener("mouseup", function () {
        // Chờ selection ổn định.
        setTimeout(function () {
            const render = currentDraftRender();
            if (!render) { removeAddBtn(); return; }

            const sel = window.getSelection();
            if (!sel || sel.isCollapsed || sel.rangeCount === 0) { removeAddBtn(); return; }

            const range = sel.getRangeAt(0);
            // Selection phải nằm TRONG vùng preview draft.
            if (!render.contains(range.commonAncestorContainer)) { removeAddBtn(); return; }

            const quote = sel.toString().trim();
            if (quote.length < 3) { removeAddBtn(); return; }

            showAddButton(range.getBoundingClientRect(), quote);
        }, 10);
    });

    listEl.addEventListener("click", function (e) {
        const del = e.target.closest(".brief-note-del");
        if (!del) return;
        notes.splice(Number(del.dataset.i), 1);
        renderNotes();
    });

    sendBtn.addEventListener("click", async function () {
        if (notes.length === 0) return;

        const token = tray.querySelector('input[name="__RequestVerificationToken"]');
        const fd = new FormData();
        fd.append("projectId", window.REQUIREMENTS_PROJECT_ID || "");
        fd.append("notesJson", JSON.stringify(notes));
        if (token) fd.append("__RequestVerificationToken", token.value);

        sendBtn.disabled = true;
        sendBtn.textContent = "Đang gửi…";
        try {
            const resp = await fetch("/Requirements/ReviseBrief", { method: "POST", body: fd });
            const data = await resp.json();
            if (data.ok) {
                // Brief đang được sửa theo ghi chú (workflow nền) — reload để thấy tiến độ + bản mới.
                location.reload();
            } else {
                alert(data.error || "Không gửi được ghi chú.");
                sendBtn.disabled = false;
                sendBtn.textContent = "✎ Gửi ghi chú cho BA sửa";
            }
        } catch {
            alert("Không gửi được ghi chú — kiểm tra kết nối rồi thử lại.");
            sendBtn.disabled = false;
            sendBtn.textContent = "✎ Gửi ghi chú cho BA sửa";
        }
    });
})();

function openRequirementModal(version) {
    document.getElementById("modalTitle").innerText =
        "Product Brief " + (version.charAt(0).toUpperCase() + version.slice(1));

    document.getElementById("requirementModal").style.display = "flex";

    document.querySelectorAll(".doc-preview")
        .forEach(x => x.style.display = "none");

    const docs = document.querySelectorAll(`.doc-preview[data-version="${version}"]`);
    if (docs.length > 0) {
        docs[0].style.display = "block";
        loadDocPreview(docs[0]);
    }
}

function closeRequirementModal() {
    document.getElementById("requirementModal").style.display = "none";
}

// Mở popup Product Brief cho bản MỚI NHẤT: ưu tiên "draft" (bản BA vừa soạn/sửa, còn ghi chú được),
// nếu không có thì lấy V{n} lớn nhất. Trả về false khi trang chưa có bản brief nào trong DOM để phía
// gọi (link "Xem Product Brief" ở banner tiến độ) biết mà reload trang trước khi mở.
function openLatestProductBrief() {
    const previews = Array.from(document.querySelectorAll(".doc-preview[data-version]"));
    if (!previews.length) return false;

    const rank = v => v === "draft" ? Number.MAX_SAFE_INTEGER : (parseInt(v.replace("V", ""), 10) || 0);
    const target = previews.reduce((best, x) =>
        rank(x.dataset.version) > rank(best.dataset.version) ? x : best);

    openRequirementModal(target.dataset.version);
    return true;
}

// Người dùng bấm link "Xem Product Brief" ngay lúc trang sắp tự reload (tài liệu vừa sinh xong) → cờ
// một lần này bảo trang mở popup ngay khi tải lại, để cú bấm không bị "rơi" mất. Cờ có hạn 30s: nếu cú
// bấm lỡ nhịp reload (cờ ghi xong thì trang đã tải lại rồi), nó tự hết hạn thay vì bật popup bất ngờ ở
// lần vào trang sau.
(function openBriefAfterReload() {
    const KEY = "req-open-brief-after-reload";
    const at = parseInt(sessionStorage.getItem(KEY) || "", 10);
    if (!at) return;

    sessionStorage.removeItem(KEY);
    if (Date.now() - at > 30000) return;

    openLatestProductBrief();
})();

// ==== Popup "Tài liệu nguồn" ====
// Chỉ để XEM LẠI/XOÁ các file đã đính kèm cho BA (việc đính kèm nằm ở nút 📎 trong khung chat). Xoá gọi
// DeleteSource bằng fetch rồi gỡ hàng tại chỗ: popup không đóng, người dùng dọn liền mấy file một lúc —
// khác hẳn form POST cũ (mỗi lần xoá là reload cả trang). Sau khi xoá xong KHÔNG reload: các thumbnail
// trong hội thoại trỏ tới nguồn đã xoá sẽ nhận 404 và tự ẩn (onerror) ở lần tải trang sau.
(function initSourceModal() {
    const modal = document.getElementById("sourceModal");
    const openBtn = document.getElementById("sourceOpenBtn");
    if (!modal || !openBtn) return;

    const closeBtn = document.getElementById("sourceModalClose");
    const tbody = document.getElementById("sourceTableBody");
    const table = document.getElementById("sourceTable");
    const emptyEl = document.getElementById("sourceEmpty");
    const badge = document.getElementById("sourceCountBadge");
    const projectIdEl = document.getElementById("sourceModalProjectId");
    const token = modal.querySelector('input[name="__RequestVerificationToken"]');

    function open() { modal.style.display = "flex"; }
    function close() { modal.style.display = "none"; }

    // Đồng bộ bảng/empty-state/badge sau mỗi lần xoá — badge trên sidebar là thứ user thấy khi popup đóng.
    function syncCount() {
        const count = tbody ? tbody.querySelectorAll("tr").length : 0;
        if (table) table.hidden = count === 0;
        if (emptyEl) emptyEl.hidden = count > 0;
        if (badge) {
            badge.textContent = String(count);
            badge.hidden = count === 0;
        }
    }

    openBtn.addEventListener("click", open);
    if (closeBtn) closeBtn.addEventListener("click", close);
    modal.addEventListener("click", function (e) {
        if (e.target === modal) close(); // bấm nền tối để đóng
    });
    document.addEventListener("keydown", function (e) {
        if (e.key === "Escape" && modal.style.display === "flex") close();
    });

    if (tbody) {
        tbody.addEventListener("click", async function (e) {
            const btn = e.target.closest(".source-del");
            if (!btn) return;

            const row = btn.closest("tr");
            const id = btn.dataset.sourceId;
            if (!row || !id) return;
            if (!confirm("Xoá tài liệu nguồn này? BA sẽ không dùng nội dung của nó cho các lượt sau.")) return;

            btn.disabled = true;
            const fd = new FormData();
            fd.append("id", id);
            fd.append("projectId", projectIdEl ? projectIdEl.value : "");
            if (token) fd.append("__RequestVerificationToken", token.value);

            try {
                const resp = await fetch("/Requirements/DeleteSource", { method: "POST", body: fd });
                if (!resp.ok && !resp.redirected) throw new Error("delete failed");
                row.remove();
                syncCount();
            } catch {
                btn.disabled = false;
                alert("Không xoá được tài liệu — kiểm tra kết nối rồi thử lại.");
            }
        });
    }
})();

// ==== Cổng xác nhận giả định (giữa "sinh bản thiết kế" và "dựng POC") ====
// Panel ở chế độ CỔNG (data-pending="true") nghĩa là POC chưa hề được dựng: quy trình đang đứng chờ user
// rà danh sách giả định mà bản thiết kế tự quyết. Mỗi dòng mặc định "Đúng"; bấm "Chưa đúng" mở ô gõ ý
// đúng và đổi nút hành động sang nhánh sửa. Chỉ MỘT nút hiện tại mỗi thời điểm để không có hai đường
// tiếp tục cạnh nhau — nhánh nào cũng dẫn tới một lượt chạy nền nên trang reload sau khi gửi.
(function initAssumptionGate() {
    const panel = document.getElementById("assumptionPanel");
    if (!panel || panel.dataset.pending !== "true") return;

    const confirmBtn = document.getElementById("assumptionConfirmBtn");
    const reviseBtn = document.getElementById("assumptionReviseBtn");
    const msgEl = document.getElementById("assumptionGateMsg");
    const items = Array.from(panel.querySelectorAll(".assumption-gate-item"));

    function markedBad() {
        return items.filter(li => li.querySelector('.assumption-vote.bad').classList.contains("is-on"));
    }

    // Nút hiển thị theo trạng thái đánh dấu: chưa đánh dấu gì ⇒ "tất cả đúng, dựng demo";
    // có ít nhất một điểm sai ⇒ chỉ còn nhánh sửa (dựng POC từ giả định đã biết là sai là phí một lượt).
    function syncButtons() {
        const bad = markedBad().length;
        confirmBtn.hidden = bad > 0;
        reviseBtn.hidden = bad === 0;
        reviseBtn.textContent = `↻ Sửa ${bad} điểm đã đánh dấu rồi dựng lại bản thiết kế`;
    }

    panel.addEventListener("click", function (e) {
        const vote = e.target.closest(".assumption-vote");
        if (!vote) return;
        const li = vote.closest(".assumption-gate-item");
        const bad = vote.dataset.vote === "bad";
        li.querySelector(".assumption-vote.ok").classList.toggle("is-on", !bad);
        li.querySelector(".assumption-vote.bad").classList.toggle("is-on", bad);
        const fix = li.querySelector(".assumption-fix");
        fix.hidden = !bad;
        if (bad) fix.focus();
        syncButtons();
    });

    async function post(url, extra) {
        const token = document.querySelector('input[name="__RequestVerificationToken"]');
        const fd = new FormData();
        fd.append("projectId", window.REQUIREMENTS_PROJECT_ID || "");
        if (token) fd.append("__RequestVerificationToken", token.value);
        Object.keys(extra || {}).forEach(k => fd.append(k, extra[k]));
        const resp = await fetch(url, { method: "POST", body: fd });
        return await resp.json();
    }

    async function run(btn, url, extra, busyText) {
        const original = btn.textContent;
        btn.disabled = true;
        btn.textContent = busyText;
        msgEl.textContent = "";
        try {
            const data = await post(url, extra);
            if (data.ok) {
                location.reload();
                return;
            }
            msgEl.textContent = data.error || "Không gửi được.";
        } catch {
            msgEl.textContent = "Không gửi được — kiểm tra kết nối rồi thử lại.";
        }
        btn.disabled = false;
        btn.textContent = original;
    }

    confirmBtn.addEventListener("click", () =>
        run(confirmBtn, panel.dataset.confirmUrl, null, "Đang khởi động dựng bản demo…"));

    reviseBtn.addEventListener("click", function () {
        const corrections = markedBad().map(li => ({
            assumption: li.dataset.assumption,
            correction: li.querySelector(".assumption-fix").value.trim()
        }));
        if (corrections.length === 0) return;
        run(reviseBtn, panel.dataset.reviseUrl,
            { correctionsJson: JSON.stringify(corrections) }, "Đang gửi đính chính…");
    });

    // Cổng cao hơn một bong bóng chat thường, nên cuộn-xuống-đáy mặc định của khung chat cắt mất phần
    // ĐẦU của nó (nhãn "BA" + tiêu đề + đoạn giải thích "bản demo chưa được dựng") — người dùng rơi
    // thẳng vào giữa danh sách giả định mà không biết mình đang được hỏi gì. Khi cổng đang mở, neo đỉnh
    // cổng lên đầu khung chat thay vì neo đáy. Chạy sau load để không bị scrollChatToBottom ghi đè.
    function scrollGateIntoView() {
        const chat = document.getElementById("chatMessages");
        if (!chat) return;
        chat.scrollTop += panel.getBoundingClientRect().top - chat.getBoundingClientRect().top - 8;
    }

    scrollGateIntoView();
    requestAnimationFrame(scrollGateIntoView);
    window.addEventListener("load", () => requestAnimationFrame(scrollGateIntoView));

    syncButtons();
})();

// KHÔNG còn hộp xác nhận "tạo lại tài liệu" ở đây. Trạng thái sinh ra nó — draft đã có mà hội thoại chưa
// có gì mới — nay ĐÓNG cổng hẳn (trạng thái "done" ở Index.cshtml) thay vì bày ra một nút ghi đè kèm lời
// khuyên đừng bấm. Muốn bản khác thì nhắn thêm trong khung chat: cổng mở lại ở "ready" và nút lúc đó soạn
// từ hội thoại ĐÃ có thông tin mới, không còn là cú ghi đè bằng một bản gần y hệt.

// ==== Cổng soát MÂU THUẪN (chạy khi bấm "Write Requirement") ====
// Panel "Tiến độ khai thác" chỉ trả lời *đã rõ hết chưa*. Cổng này trả lời *những điều đã rõ có chọi nhau
// không*: người dùng nói ở lượt 3 rằng quản lý duyệt xong là hết, tới lượt 12 lại kể thêm HR duyệt — bản
// đồ bao phủ đánh dấu [RÕ] cả hai lần, còn bước soạn tài liệu (bị cấm tự giả định) sẽ chọn bừa một bên.
// Không có mâu thuẫn ⇒ người dùng không thấy gì cả, form submit như trước (fail-open cả khi soát lỗi).
(function initConflictGate() {
    const panel = document.getElementById("conflictPanel");
    const form = document.querySelector("form.write-req");
    if (!panel || !form) return;

    const bodyEl = document.getElementById("conflictBody");
    const msgEl = document.getElementById("conflictMsg");
    const submitBtn = form.querySelector("button");

    // Đã soát xong (không còn mâu thuẫn / vừa chốt xong) ⇒ lần submit sau đi thẳng, không soát lại.
    let cleared = false;

    function token() {
        const el = document.querySelector('input[name="__RequestVerificationToken"]');
        return el ? el.value : "";
    }

    async function post(url, extra) {
        const fd = new FormData();
        fd.append("projectId", window.REQUIREMENTS_PROJECT_ID || "");
        fd.append("__RequestVerificationToken", token());
        Object.keys(extra || {}).forEach(k => fd.append(k, extra[k]));
        const resp = await fetch(url, { method: "POST", body: fd });
        return await resp.json();
    }

    function submitForRealNow() {
        cleared = true;
        submitBtn.disabled = true;
        submitBtn.textContent = "Đang tạo tài liệu…";
        // form.submit() không chạy handler onsubmit nên trạng thái nút phải tự đặt ở trên.
        form.submit();
    }

    function render(conflicts) {
        bodyEl.innerHTML = conflicts.map((c, i) => `
            <div class="conflict-item" data-index="${i}" data-question="${escapeHtml(c.question || "")}">
                ${c.topic ? `<div class="conflict-topic">${escapeHtml(c.topic)}</div>` : ""}
                <div class="conflict-sides">
                    <div class="conflict-side"><span class="conflict-side-tag">Anh/chị từng nói</span> ${escapeHtml(c.sideA || "")}</div>
                    <div class="conflict-side"><span class="conflict-side-tag">Nhưng cũng nói</span> ${escapeHtml(c.sideB || "")}</div>
                </div>
                <div class="conflict-question">${escapeHtml(c.question || "")}</div>
                <div class="conflict-options">
                    ${(c.options || []).map(o => `
                        <button type="button" class="conflict-option" data-value="${escapeHtml(o)}">${escapeHtml(o)}</button>
                    `).join("")}
                </div>
                <input type="text" class="conflict-other" placeholder="Hoặc gõ cách hiểu đúng của anh/chị…" />
            </div>`).join("") + `
            <div class="conflict-bar">
                <button type="button" class="btn primary full" id="conflictConfirmBtn">✓ Chốt lại rồi tạo tài liệu</button>
            </div>`;
        panel.hidden = false;
        panel.scrollIntoView({ behavior: "smooth", block: "nearest" });
    }

    // Lựa chọn của một mục = nút đang bật, hoặc ô tự nhập nếu người dùng gõ (ô gõ được ưu tiên).
    function choiceOf(item) {
        const typed = item.querySelector(".conflict-other").value.trim();
        if (typed.length > 0) return typed;
        const on = item.querySelector(".conflict-option.is-on");
        return on ? on.dataset.value : "";
    }

    form.addEventListener("submit", async function (e) {
        if (cleared) return;
        e.preventDefault();

        // Nhãn để khôi phục lấy từ data-idle-label chứ KHÔNG đọc textContent tại đây: handler onsubmit
        // inline chạy trước listener này và đã đổi nhãn thành "Đang tạo tài liệu…", nên đọc ở đây sẽ ghim
        // luôn chữ đó làm nhãn "nghỉ" — phát hiện có mâu thuẫn, nút enable lại mà vẫn ghi "Đang tạo tài liệu…".
        const original = submitBtn.dataset.idleLabel || submitBtn.textContent;
        submitBtn.disabled = true;
        submitBtn.textContent = "Đang soát mâu thuẫn…";
        try {
            const data = await post(panel.dataset.checkUrl, null);
            if (!data.ok || !Array.isArray(data.conflicts) || data.conflicts.length === 0) {
                submitForRealNow();
                return;
            }
            render(data.conflicts);
        } catch {
            // Soát lỗi (mất mạng, LLM chết) KHÔNG được chặn người dùng — đây là cổng chất lượng, không
            // phải cổng bảo mật: đi tiếp và soạn tài liệu như trước khi có cổng này.
            submitForRealNow();
            return;
        }
        submitBtn.disabled = false;
        submitBtn.textContent = original;
    });

    bodyEl.addEventListener("click", async function (e) {
        const option = e.target.closest(".conflict-option");
        if (option) {
            const item = option.closest(".conflict-item");
            item.querySelectorAll(".conflict-option").forEach(b => b.classList.toggle("is-on", b === option));
            item.querySelector(".conflict-other").value = "";
            return;
        }

        const confirmBtn = e.target.closest("#conflictConfirmBtn");
        if (!confirmBtn) return;

        const items = Array.from(bodyEl.querySelectorAll(".conflict-item"));
        const resolutions = items
            .map(item => ({ question: item.dataset.question, choice: choiceOf(item) }))
            .filter(r => r.choice.length > 0);

        if (resolutions.length < items.length) {
            msgEl.textContent = "Còn điểm chưa chọn — chọn một phương án (hoặc gõ cách hiểu đúng) cho từng điểm nhé.";
            return;
        }

        confirmBtn.disabled = true;
        confirmBtn.textContent = "Đang ghi nhận…";
        msgEl.textContent = "";
        try {
            const data = await post(panel.dataset.resolveUrl, { resolutionsJson: JSON.stringify(resolutions) });
            if (data.ok) {
                panel.hidden = true;
                submitForRealNow();
                return;
            }
            msgEl.textContent = data.error || "Không ghi nhận được lựa chọn.";
        } catch {
            msgEl.textContent = "Không gửi được — kiểm tra kết nối rồi thử lại.";
        }
        confirmBtn.disabled = false;
        confirmBtn.textContent = "✓ Chốt lại rồi tạo tài liệu";
    });
})();
