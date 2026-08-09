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
    // Stream hỏng trước khi nhận được frame nào → fallback postback cổ điển (hành vi cũ, reload trang).
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
        ensureMultiControls();
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
        if (btn) btn.disabled = selectedSuggestionValues().length === 0;
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
    }

    // Các câu ĐÃ có câu trả lời, theo đúng thứ tự hỏi. Câu để trống đơn giản không có mặt — BA hỏi tiếp
    // ở lượt sau, đúng như lời hứa dưới nút gửi.
    function answeredBatchQuestions() {
        if (!batchPanel || batchPanel.hidden) return [];
        return Array.from(batchPanel.querySelectorAll(".batchq-item"))
            .map(li => ({
                question: li.dataset.question || "",
                answer: (li.querySelector(".batchq-answer").value || "").trim()
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

        // Rỗng (lượt lỗi giữ bong bóng riêng, hoặc BA trả `message` trống) → rơi về câu dẫn tĩnh: thẻ mở
        // đầu bằng câu hỏi trần thì mất mạch hội thoại.
        const lead = absorbLeadBubble(leadBubble) || "Anh/chị trả lời giúp mình mấy điểm sau nhé.";

        batchPanel.innerHTML = `
            <p class="batchq-lead">${escapeHtml(lead)}</p>
            <div class="batchq-howto">Bấm một gợi ý hoặc tự nhập; điểm nào chưa nghĩ tới thì để trống.</div>
            <ul class="batchq-list">
                ${questions.map(q => {
                    // Câu MỞ: không có gợi ý nào để bấm, nên ô tự nhập mở SẴN (không có nút "Ý khác" để
                    // bấm mở) — một dòng chỉ có mỗi câu hỏi mà không có chỗ trả lời đọc như một dòng bị lỗi.
                    const open = q.openEnded === true || !(Array.isArray(q.suggestions) && q.suggestions.length > 0);
                    return `
                <li class="batchq-item" data-question="${escapeHtml(q.question || "")}" data-multi="${q.multiSelect ? "true" : "false"}" data-open="${open ? "true" : "false"}">
                    ${q.group ? `<div class="batchq-group">${escapeHtml(q.group)}</div>` : ""}
                    <div class="batchq-question">${escapeHtml(q.question || "")}</div>
                    ${open ? "" : `
                    <div class="batchq-choices">
                        ${(Array.isArray(q.suggestions) ? q.suggestions : []).map(s => `
                        <button type="button" class="batchq-choice" data-value="${escapeHtml(s)}">${escapeHtml(s)}</button>`).join("")}
                        <button type="button" class="batchq-choice is-other" data-other="1">Ý khác — tôi tự nhập</button>
                    </div>`}
                    <textarea class="batchq-answer" rows="${open ? 3 : 2}"${open ? "" : " hidden"} placeholder="${open ? "Anh/chị kể giúp mình, càng chi tiết càng tốt…" : "Câu trả lời của anh/chị…"}"></textarea>
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
                const answer = li.querySelector(".batchq-answer");
                const multi = li.dataset.multi === "true";

                if (choice.dataset.other) {
                    // "Ý khác" mồi sẵn nội dung đang chọn: phần lớn lượt tự nhập là CHỈNH vài chữ của một
                    // gợi ý, bắt gõ lại cả câu là thao tác thừa.
                    choice.classList.add("is-on");
                    answer.hidden = false;
                    answer.focus();
                    answer.setSelectionRange(answer.value.length, answer.value.length);
                    updateBatchSendButton();
                    return;
                }

                if (multi) {
                    // Câu chọn-nhiều: chip là toggle, câu trả lời là các lựa chọn nối bằng dấu phẩy.
                    choice.classList.toggle("is-on");
                    const picked = Array.from(li.querySelectorAll(".batchq-choice.is-on:not(.is-other)"))
                        .map(b => b.dataset.value);
                    answer.value = picked.join(", ");
                } else {
                    li.querySelectorAll(".batchq-choice").forEach(b => b.classList.toggle("is-on", b === choice));
                    answer.hidden = true;
                    answer.value = choice.dataset.value || "";
                }
                updateBatchSendButton();
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

        // Ô "Ý khác" đang mở mà rỗng thì câu đó KHÔNG được tính, nên nhãn nút phải nhảy theo từng phím gõ.
        batchPanel.addEventListener("input", function (e) {
            if (e.target.classList.contains("batchq-answer")) updateBatchSendButton();
        });
    }

    // ==== Panel "Tiến độ khai thác" + "Điều đã chốt" (cột trái) — cập nhật live từ frame done ====
    // Markup phải khớp bản server render trong Index.cshtml.
    const coverageIcons = { "RÕ": "✅", "MỘT PHẦN": "🟡", "KHÔNG ÁP DỤNG": "➖" };

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

        list.innerHTML = items.map(x => `
            <li class="coverage-item ${x.status === "KHÔNG ÁP DỤNG" ? "na" : ""}" data-label="${escapeHtml(x.label)}" title="${escapeHtml(x.summary || "")}">
                <span class="cov-ico">${coverageIcons[x.status] || "⚪"}</span>
                <span class="cov-label">${escapeHtml(x.label)}</span>
                ${(x.status === "RÕ" || x.status === "MỘT PHẦN")
                    ? `<button type="button" class="cov-wrong" title="Nhóm này BA hiểu chưa đúng — hỏi lại giúp tôi">chưa đúng?</button>`
                    : ""}
                ${x.evidence ? `<div class="cov-evidence" title="Câu mà kết luận này dựa vào">${escapeHtml(x.evidence)}</div>` : ""}
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

        // Ghi chú của khung rỗng chỉ đúng khi CHƯA nhóm nào được khai thác.
        const hint = document.getElementById("coverageHint");
        if (hint) hint.hidden = !items.every(x => x.status === "CHƯA HỎI");

        panel.hidden = false;
    }

    // ==== CỔNG TỔNG KẾT CUỐI (#summaryGate) ====
    // Thay cho panel "Điều đã chốt" ở sidebar (đã gỡ): cùng dữ liệu, nhưng hiện đúng MỘT lần — khi BA đã
    // khai thác xong và mời bấm "Write Requirement". Trong lúc phỏng vấn, việc soát mâu thuẫn là của BA
    // (nhật ký nay nằm trong ngữ cảnh chat của BA); người dùng chỉ đội mũ kiểm duyệt ở đây, khi họ rảnh trí.
    //
    // Đặt làm khối CUỐI khung chat, cùng chỗ với cổng xác nhận giả định, vì cùng một lý do: quy trình đang
    // đứng chờ người dùng, nên câu hỏi và nút trả lời phải nằm cùng chỗ mắt đang nhìn (chat tự cuộn đáy).
    // Markup phải khớp bản server render trong Index.cshtml.

    // Cờ mời "Write Requirement" của lượt BA MỚI NHẤT. Phải giữ lại vì frame "decisions" tới SAU frame
    // done và KHÔNG mang theo cờ đó — mà nó mới là frame có danh sách đầy đủ để mở cổng.
    // Giá trị đầu = đúng trạng thái server vừa render: F5 ngay ở lượt mời thì frame decisions của lượt
    // kế tiếp vẫn phải biết cổng đang được phép mở.
    let lastTurnInvitedWriteReq = !(document.getElementById("summaryGate")?.hidden ?? true);

    function renderSummaryGate(items, invites) {
        const gate = document.getElementById("summaryGate");
        if (!gate) return;

        // Không phải lượt mời bấm nút ⇒ đóng cổng. BA quay lại hỏi tiếp (vd vừa phát hiện mâu thuẫn từ
        // chính đính chính user vừa gửi) thì bản tổng kết cũ không còn đúng nữa, để lại là nói dối.
        if (invites !== true) {
            gate.hidden = true;
            return;
        }
        if (!Array.isArray(items) || items.length === 0) return;

        const list = gate.querySelector(".summary-gate-list");
        if (!list) return;

        // Giữ nguyên ghi chú người dùng đang gõ dở khi frame "decisions" tới sau done và vẽ lại danh sách.
        const kept = {};
        list.querySelectorAll(".summary-gate-item").forEach(li => {
            const note = li.querySelector(".summary-gate-note");
            if (note && note.value.trim().length > 0) kept[li.dataset.decision] = note.value;
        });

        list.innerHTML = items.map(d => `
            <li class="summary-gate-item" data-decision="${escapeHtml(d)}">
                <div class="summary-gate-text">${escapeHtml(d)}</div>
                <button type="button" class="summary-gate-fix" title="Ý này chưa đúng — ghi chú lại cho BA">✎ Sửa</button>
                <textarea class="summary-gate-note" rows="2" ${kept[d] === undefined ? "hidden" : ""}
                          placeholder="Đúng ra là… (ghi ngắn gọn)">${kept[d] === undefined ? "" : escapeHtml(kept[d])}</textarea>
            </li>
        `).join("");

        const count = gate.querySelector(".summary-gate-count");
        if (count) count.textContent = `${items.length} ý`;

        // Dời cổng xuống CUỐI dòng hội thoại (cùng cách renderSuggestions dời khay chip): các bong bóng
        // mới được chèn vào ngay trước #thinkingBox, nên một khối đứng yên ở vị trí tĩnh sẽ bị các lượt
        // sau vượt qua — bản tổng kết nổi lên phía trên chính câu trả lời vừa sinh ra nó.
        thinkingBox.before(gate);

        gate.hidden = false;
        syncSummaryGateBar();
    }

    // Nút chính đổi nghĩa theo việc user đã ghi chú hay chưa: chưa ghi gì ⇒ "đúng hết, tạo tài liệu";
    // đã ghi ⇒ "gửi đính chính" (tạo tài liệu từ một bản tổng kết user vừa nói là sai thì vô nghĩa).
    function syncSummaryGateBar() {
        const gate = document.getElementById("summaryGate");
        if (!gate || gate.hidden) return;

        const notes = summaryGateNotes();
        const okBtn = gate.querySelector(".summary-gate-ok");
        const fixBtn = gate.querySelector(".summary-gate-send");
        if (okBtn) okBtn.hidden = notes.length > 0;
        if (fixBtn) {
            fixBtn.hidden = notes.length === 0;
            fixBtn.textContent = `Gửi ${notes.length} đính chính cho BA`;
        }
    }

    // Các ghi chú đã gõ, kèm đoạn user bôi đen (nếu có) để BA biết sửa đúng chỗ trong ý đó.
    function summaryGateNotes() {
        const gate = document.getElementById("summaryGate");
        if (!gate) return [];
        return Array.from(gate.querySelectorAll(".summary-gate-item"))
            .map(li => ({
                decision: li.dataset.decision,
                quote: (li.dataset.quote || "").trim(),
                note: (li.querySelector(".summary-gate-note")?.value || "").trim()
            }))
            .filter(n => n.note.length > 0);
    }

    const summaryGateEl = document.getElementById("summaryGate");
    if (summaryGateEl) {
        summaryGateEl.addEventListener("click", function (e) {
            const fix = e.target.closest(".summary-gate-fix");
            if (fix) {
                openSummaryGateNote(fix.closest(".summary-gate-item"));
                return;
            }

            // "Đúng hết rồi" → bấm hộ nút "Write Requirement" thật ở sidebar thay vì tự submit form: cổng
            // soát mâu thuẫn và hộp xác nhận "tạo lại" đều là listener trên nút đó, gọi form.submit() ở
            // đây sẽ đi vòng qua cả hai.
            if (e.target.closest(".summary-gate-ok")) {
                const btn = document.querySelector("form.write-req .write-req-btn");
                if (btn && !btn.disabled) btn.click();
                return;
            }

            if (e.target.closest(".summary-gate-send")) sendSummaryGateNotes();
        });

        // Nhãn nút chính đổi theo việc đã có ghi chú hay chưa — phải nhảy theo từng phím gõ, vì một ô mở
        // ra mà bỏ trống thì không tính là đính chính.
        summaryGateEl.addEventListener("input", function (e) {
            if (e.target.classList.contains("summary-gate-note")) syncSummaryGateBar();
        });

        // BÔI ĐEN một đoạn trong bản tổng kết → hiện nút ghi chú ngay cạnh con trỏ. Đây là tiện ích phụ
        // chứ không phải đường chính: các ý trong bản tổng kết là câu ngắn (~25 từ), nên bôi một cụm
        // trong đó chỉ giúp NÓI RÕ chỗ sai chứ không thay được nút "✎ Sửa" của cả ý. Cùng ý tưởng với
        // popover ghi chú trên bản xem trước Product Brief (initBriefNotes), nơi đoạn văn thật sự dài.
        document.addEventListener("selectionchange", function () {
            const sel = window.getSelection();
            if (!sel || sel.isCollapsed || sel.rangeCount === 0) return hideSummaryQuoteBtn();

            const text = sel.toString().trim();
            const item = sel.anchorNode
                ? (sel.anchorNode.nodeType === 1 ? sel.anchorNode : sel.anchorNode.parentElement)?.closest(".summary-gate-text")
                : null;
            if (!item || text.length < 3) return hideSummaryQuoteBtn();

            showSummaryQuoteBtn(sel.getRangeAt(0).getBoundingClientRect(), item.closest(".summary-gate-item"), text);
        });
        window.addEventListener("scroll", hideSummaryQuoteBtn, true);
    }

    let summaryQuoteBtn = null;

    function hideSummaryQuoteBtn() {
        if (summaryQuoteBtn) summaryQuoteBtn.hidden = true;
    }

    function showSummaryQuoteBtn(rect, item, quote) {
        if (!summaryQuoteBtn) {
            summaryQuoteBtn = document.createElement("button");
            summaryQuoteBtn.type = "button";
            summaryQuoteBtn.className = "summary-quote-btn";
            summaryQuoteBtn.textContent = "✎ Ghi chú đoạn này";
            // mousedown (không phải click): click tới sau khi trình duyệt đã xoá vùng chọn, nên nút biến
            // mất ngay dưới ngón tay người dùng trước khi kịp nhận cú bấm.
            summaryQuoteBtn.addEventListener("mousedown", function (e) {
                e.preventDefault();
                const target = summaryQuoteBtn.targetItem;
                if (!target) return;
                target.dataset.quote = summaryQuoteBtn.quoteText || "";
                openSummaryGateNote(target);
                hideSummaryQuoteBtn();
                window.getSelection().removeAllRanges();
            });
            document.body.appendChild(summaryQuoteBtn);
        }
        summaryQuoteBtn.targetItem = item;
        summaryQuoteBtn.quoteText = quote;
        summaryQuoteBtn.hidden = false;
        summaryQuoteBtn.style.top = `${rect.top + window.scrollY - 38}px`;
        summaryQuoteBtn.style.left = `${rect.left + window.scrollX}px`;
    }

    // Mở ô ghi chú của một ý (và hiện chip đoạn đã bôi, nếu có).
    function openSummaryGateNote(item) {
        if (!item) return;
        const note = item.querySelector(".summary-gate-note");
        if (!note) return;

        const quote = (item.dataset.quote || "").trim();
        let chip = item.querySelector(".summary-gate-quote");
        if (quote.length > 0) {
            if (!chip) {
                chip = document.createElement("div");
                chip.className = "summary-gate-quote";
                note.insertAdjacentElement("beforebegin", chip);
            }
            chip.textContent = `Đoạn cần sửa: “${quote}”`;
        } else if (chip) {
            chip.remove();
        }

        note.hidden = false;
        note.focus();
        syncSummaryGateBar();
    }

    // Gửi các đính chính thành MỘT lượt chat bình thường, thay vì một endpoint riêng: BA đọc, xác nhận
    // lại cách hiểu mới, nhật ký quyết định gộp lượt này, và cổng tự mở lại ở lượt mời bấm nút kế tiếp
    // với bản tổng kết đã sửa. Ghi chú đi qua hội thoại cũng là điều kiện để bước soạn tài liệu (vốn đọc
    // transcript) thấy được chúng — ghi chú nằm ngoài transcript thì chỉ là trang trí.
    function sendSummaryGateNotes() {
        const notes = summaryGateNotes();
        if (notes.length === 0 || chatBusy) return;

        const lines = notes.map(n => n.quote.length > 0
            ? `- Ở ý “${n.decision}”, đoạn “${n.quote}” chưa đúng: ${n.note}`
            : `- Ý “${n.decision}” chưa đúng: ${n.note}`);

        messageInput.value = "Tôi đã xem bản tổng kết. Mấy ý sau cần chỉnh lại:\n"
            + lines.join("\n")
            + "\nCác ý còn lại thì đúng rồi nhé.";
        resizeMessageInput();

        const gate = document.getElementById("summaryGate");
        if (gate) gate.hidden = true;
        chatForm.requestSubmit();
    }

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

    // Bấm "chưa đúng?" trên một nhóm của bản đồ bao phủ → hạ nhóm xuống [MỘT PHẦN] NGAY (server sửa bản
    // đồ tất định, không chờ lượt chat) rồi soạn sẵn tin nhắn để user nói lại cho đúng. Không có đường
    // này thì một nhóm bị chấm [RÕ] oan sẽ không bao giờ được hỏi lại — BA bị cấm hỏi lại nhóm đã [RÕ].
    const coveragePanelEl = document.getElementById("coveragePanel");
    if (coveragePanelEl) {
        coveragePanelEl.addEventListener("click", async function (e) {
            const btn = e.target.closest(".cov-wrong");
            if (!btn) return;

            const item = btn.closest(".coverage-item");
            const label = item ? item.dataset.label : "";
            if (!label) return;

            btn.disabled = true;
            try {
                const fd = new FormData();
                fd.append("projectId", window.REQUIREMENTS_PROJECT_ID || "");
                fd.append("label", label);
                const tokenEl = document.querySelector('input[name="__RequestVerificationToken"]');
                fd.append("__RequestVerificationToken", tokenEl ? tokenEl.value : "");
                const resp = await fetch(coveragePanelEl.dataset.reopenUrl, { method: "POST", body: fd });
                const data = await resp.json();
                if (data.ok) {
                    renderCoverage(data.coverage);
                    setWriteRequirementReady(!!data.ready);
                }
            } catch {
                // Mất mạng: không sửa được bản đồ thì thôi, người dùng vẫn gõ được đính chính vào chat
                // bên dưới — lượt gộp kế tiếp sẽ hạ nhóm đó xuống.
            }

            messageInput.value = `Nhóm "${label}" BA hiểu chưa đúng. Ý đúng của tôi là: `;
            resizeMessageInput();
            messageInput.focus();
            messageInput.setSelectionRange(messageInput.value.length, messageInput.value.length);
        });
    }

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

    // Đồng bộ trạng thái nút "Write Requirement" với cờ mời của lượt BA mới nhất — đúng logic server
    // render (requirementReady trong Index.cshtml), vì trang không reload nữa.
    function setWriteRequirementReady(ready) {
        const form = document.querySelector("form.write-req");
        if (!form) return;

        // VÒNG SOẠN ĐANG CHẠY ⇒ giữ nguyên nút khóa, bất kể lượt chat vừa rồi có mời bấm hay không: người
        // dùng vẫn chat được trong lúc tài liệu đang soạn, và mở khóa ở đây sẽ mở lại đúng cửa spam mà
        // trạng thái "running" của server vừa đóng. Run xong thì requirement-workflow.js tải lại trang,
        // server render lại nút theo trạng thái mới.
        if (form.dataset.runInFlight === "true") return;

        // Vừa có lượt chat mới ⇒ hội thoại KHÔNG còn "chưa có gì mới kể từ lần soạn gần nhất", nên nút
        // thôi đóng vai "Tạo lại tài liệu" (kèm xác nhận) và quay về đúng cờ mời của lượt BA mới nhất.
        // Ghi chú của trạng thái đó cũng hết đúng → xoá luôn thay vì để nó nói sai bên dưới nút.
        form.classList.remove("write-req-regenerate");
        form.querySelectorAll(".write-req-state-hint").forEach(el => el.remove());

        form.classList.toggle("write-req-ready", ready);
        form.classList.toggle("write-req-waiting", !ready);

        const btn = form.querySelector(".write-req-btn");
        if (btn) {
            // Nút CHỈ enable khi đủ thông tin (mọi nhóm áp dụng [RÕ]) — khớp thuộc tính disabled
            // server render trong Index.cshtml. Panel "Tiến độ khai thác" đầy ⇔ ready ⇔ nút mở khóa.
            btn.disabled = !ready;
            btn.classList.toggle("primary", ready);
            btn.classList.toggle("outline", !ready);

            // Nhãn/tooltip cũng phải theo: rời trạng thái "Tạo lại tài liệu" mà giữ nguyên chữ cũ thì nút
            // hứa một đằng làm một nẻo. data-idle-label là nhãn "nghỉ" hiện hành — cổng soát mâu thuẫn
            // đọc lại nó để khôi phục sau khi soát xong.
            btn.textContent = btn.dataset.readyLabel || "📝 Write Requirement";
            btn.dataset.idleLabel = btn.textContent;
            btn.title = (ready ? btn.dataset.readyTitle : btn.dataset.waitingTitle) || "";
        }

        const hint = form.querySelector(".write-req-hint");
        if (hint) hint.style.display = ready ? "none" : "";
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
            setWriteRequirementReady(data.invitesWriteRequirement === true);
            renderCoverage(data.coverage, data.coverageStale === true);
            // Cổng tổng kết chỉ mở ở lượt BA MỜI bấm "Write Requirement" — cùng một cờ điều khiển nút
            // "Write Requirement", nên cổng và nút không thể vênh nhau.
            lastTurnInvitedWriteReq = data.invitesWriteRequirement === true;
            renderSummaryGate(data.decisions, lastTurnInvitedWriteReq);
            renderFlowDiagram(bubble, data.flowDiagram);

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
            finishTurn(ev);
        } else if (ev.type === "decisions") {
            // Frame phụ SAU done: nhật ký quyết định đã gộp lượt vừa rồi (server tách lời gọi LLM này ra
            // khỏi đường trả lời để lượt chat nhanh hơn), tức bản ở frame done còn THIẾU đúng lượt cuối —
            // thiếu chính điều người dùng vừa chốt. Vẽ lại cổng bằng bản đầy đủ.
            //
            // Điều kiện là cờ mời của lượt vừa rồi, KHÔNG phải "cổng đang mở": ở lượt mời đầu tiên mà
            // nhật ký còn rỗng (gộp lỗi ở các lượt trước, hoặc phỏng vấn ngắn chưa kịp có bản nào), frame
            // done không mở được cổng vì không có ý nào để hiện — bám theo trạng thái cổng thì frame này
            // cũng bỏ qua nốt, và cổng KHÔNG BAO GIỜ xuất hiện ở đúng lượt cần nó.
            renderSummaryGate(ev.decisions, lastTurnInvitedWriteReq);
        }
        return true;
    }

    // File đã đính kèm nhưng CHƯA gửi (staged): initSourceDropPaste đổ vào đây khi user đính kèm/dán/kéo-thả.
    // Mỗi phần tử { file, url } — url là objectURL để xem trước (chỉ ảnh; file PDF/bảng tính có url = null).
    // Khi bấm gửi mà mảng này khác rỗng, form ưu tiên upload file (kèm ghi chú trong ô nhập) thay vì chat.
    const stagedImages = [];
    // Do initSourceDropPaste gán: upload các file đang staged kèm ghi chú (text) rồi reload.
    let sendStagedImages = null;

    // true khi lượt đang gửi đã nhận ĐƯỢC ít nhất một frame SSE — quyết định cách phục hồi khi lỗi:
    // đã nhận frame nghĩa là server ĐANG xử lý lượt này (và sẽ lưu DB dù stream đứt) → chỉ reload;
    // chưa nhận frame nào mới được phép re-submit theo đường postback cổ điển.
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

            if (sawFrame) {
                // Stream đứt giữa chừng NHƯNG server đã nhận lượt này và vẫn chạy trọn
                // (CancellationToken.None) → reload để hiển thị bản đã lưu; re-submit sẽ nhân đôi lượt.
                location.reload();
                return;
            }

            // Hỏng từ trước khi nhận frame nào (mạng/proxy không stream được):
            // quay về postback cổ điển — submit native để không đi lại listener này. Trừ lượt SỬA:
            // postback chỉ biết thêm lượt mới, gửi qua đó sẽ nhân đôi câu vừa sửa.
            if (editing) {
                location.reload();
                return;
            }
            document.getElementById("hiddenMessage").value = text;
            HTMLFormElement.prototype.submit.call(chatForm);
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

        messageInput.value = text;
        chatForm.requestSubmit();
    }

    function sendSelectedSuggestions() {
        const values = selectedSuggestionValues();
        if (values.length === 0) return;

        messageInput.value = values.join(", ");
        chatForm.requestSubmit();
    }

    if (suggestionList) {
        // Trang vừa tải với lượt hỏi multi-select (server render) → gắn nút gửi cho danh sách có sẵn.
        ensureMultiControls();

        suggestionList.addEventListener("click", function (e) {
            if (e.target.closest("#suggestionMultiSendBtn")) {
                sendSelectedSuggestions();
                return;
            }

            const option = e.target.closest(".suggestion-option");
            if (!option) return;

            selectSuggestion(option);
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
// khay dưới modal, bấm "Gửi ghi chú cho BA sửa" sẽ nhờ BA soạn lại brief theo đúng các đoạn được chú
// (POST /Requirements/ReviseBrief — tái dùng vòng "Write Requirement"). Người dùng chỉ vào chỗ cần sửa
// thay vì mô tả bằng lời cả đoạn.
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
                // Brief đang được soạn lại (workflow nền) — reload để thấy tiến độ + bản mới.
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

// ==== Xác nhận TẠO LẠI tài liệu ====
// Server đặt lớp write-req-regenerate khi bản draft đã có và hội thoại CHƯA có gì mới kể từ lần soạn gần
// nhất. Bấm lúc đó tốn 2–3 lời gọi LLM để ra gần đúng bản cũ, lại GHI ĐÈ bản đang có — và vì model chạy ở
// temperature > 0, bản mới có thể tệ hơn bản người dùng đang hài lòng. Không cấm (họ có quyền muốn bản
// khác), chỉ bắt xác nhận để nó không còn là cú bấm vô thức.
//
// Chặn ở sự kiện CLICK chứ không phải submit: huỷ ở đây thì form không submit nên cả handler onsubmit lẫn
// cổng soát mâu thuẫn bên dưới đều không chạy. Huỷ ở submit thì cổng mâu thuẫn (một listener khác trên
// cùng form) vẫn nghe thấy và tự gọi form.submit() — tức bấm "Huỷ" mà tài liệu vẫn bị soạn lại.
(function initRegenerateConfirm() {
    const form = document.querySelector("form.write-req");
    if (!form) return;

    const btn = form.querySelector(".write-req-btn");
    if (!btn) return;

    btn.addEventListener("click", function (e) {
        if (!form.classList.contains("write-req-regenerate")) return;

        const ok = confirm(
            "Soạn lại bản mô tả sản phẩm và GHI ĐÈ bản hiện tại?\n\n" +
            "Anh/chị chưa bổ sung thông tin nào kể từ lần tạo trước, nên bản mới sẽ dựa trên đúng những gì " +
            "đã trao đổi — nội dung có thể khác đi mà không chắc tốt hơn.\n\n" +
            "Muốn tài liệu đổi theo ý mình, cách chắc ăn hơn là nhắn thêm cho BA trong khung chat rồi tạo lại.\n\n" +
            "Bản cũ vẫn xem lại được trong Lịch sử phiên bản.");

        if (!ok) e.preventDefault();
    });
})();

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
