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
    let chatBusy = false;
    let liveBubble = null;

    function appendUserBubble(text) {
        thinkingBox.insertAdjacentHTML("beforebegin", `
            <div class="req-msg you">
                <p>${escapeHtml(text)}</p>
            </div>
        `);
    }

    // Bong bóng "lạc quan" cho lượt gửi ẢNH: hiện ngay ảnh (từ objectURL đang xem trước) + ghi chú như
    // một lượt user thật, để user thấy tin đã gửi trong lúc BA đọc ảnh — thay vì chỉ có spinner rồi reload.
    // Markup khớp bản server render (.req-msg you > .chat-attachments) để nhìn giống hệt sau khi reload.
    // Trả về phần tử vừa chèn để có thể gỡ đi (hoàn tác) nếu upload thất bại.
    function appendUserImageBubble(note, images) {
        const thumbs = images.map(img => `
            <span class="chat-attachment-img" title="${escapeHtml(img.file.name || "ảnh")}">
                <img src="${img.url}" alt="${escapeHtml(img.file.name || "ảnh đính kèm")}" />
            </span>
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

    // Render lại các chip gợi ý cho lượt BA mới nhất (markup khớp bản server render trong Index.cshtml);
    // dời #suggestionList xuống dưới bubble mới nhất vì các lượt streaming được chèn vào sau nó trong DOM.
    // multiSelect = true: chip chuyển sang chế độ TOGGLE (chọn nhiều) + nút "Gửi các lựa chọn" — dùng cho
    // câu hỏi kiểu "gồm những vai trò nào?" mà một đáp án là không đủ.
    function renderSuggestions(suggestions, multiSelect) {
        if (!suggestionList) return;

        if (!Array.isArray(suggestions) || suggestions.length === 0) {
            suggestionList.style.display = "none";
            suggestionList.innerHTML = "";
            suggestionList.dataset.multi = "false";
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

    // ==== Panel "Tiến độ khai thác" + "Điều đã chốt" (cột trái) — cập nhật live từ frame done ====
    // Markup phải khớp bản server render trong Index.cshtml.
    const coverageIcons = { "RÕ": "✅", "MỘT PHẦN": "🟡", "KHÔNG ÁP DỤNG": "➖" };

    function renderCoverage(items) {
        const panel = document.getElementById("coveragePanel");
        const list = document.getElementById("coverageList");
        if (!panel || !list || !Array.isArray(items) || items.length === 0) return;

        const applicable = items.filter(x => x.status !== "KHÔNG ÁP DỤNG").length;
        const clear = items.filter(x => x.status === "RÕ").length;

        list.innerHTML = items.map(x => `
            <li class="coverage-item ${x.status === "KHÔNG ÁP DỤNG" ? "na" : ""}" title="${escapeHtml(x.summary || "")}">
                <span class="cov-ico">${coverageIcons[x.status] || "⚪"}</span>
                <span class="cov-label">${x.isCore ? "★ " : ""}${escapeHtml(x.label)}</span>
            </li>
        `).join("");

        const fill = document.getElementById("coverageBarFill");
        if (fill) fill.style.width = applicable === 0 ? "0%" : `${Math.round(clear * 100 / applicable)}%`;
        const text = document.getElementById("coverageProgressText");
        if (text) text.textContent = `Đã rõ ${clear}/${applicable} nhóm`;
        panel.hidden = false;
    }

    function renderDecisions(items) {
        const panel = document.getElementById("decisionPanel");
        const list = document.getElementById("decisionList");
        if (!panel || !list || !Array.isArray(items) || items.length === 0) return;

        list.innerHTML = items.map(d => `
            <li>
                <button type="button" class="decision-item" data-decision="${escapeHtml(d)}" title="Bấm để yêu cầu sửa lại">
                    ${escapeHtml(d)}
                </button>
            </li>
        `).join("");
        const count = document.getElementById("decisionCount");
        if (count) count.textContent = `(${items.length})`;
        panel.hidden = false;
    }

    // Bấm một "điều đã chốt" → soạn sẵn tin nhắn đính chính vào ô nhập để user chỉ việc mô tả ý mới.
    const decisionPanelEl = document.getElementById("decisionPanel");
    if (decisionPanelEl) {
        decisionPanelEl.addEventListener("click", function (e) {
            const item = e.target.closest(".decision-item");
            if (!item) return;

            messageInput.value = `Tôi muốn sửa lại điều đã chốt: "${item.dataset.decision}". Ý đúng của tôi là: `;
            resizeMessageInput();
            messageInput.focus();
            messageInput.setSelectionRange(messageInput.value.length, messageInput.value.length);
        });
    }

    // "Triển vọng phỏng vấn" (frame "outlook"): điểm cần làm rõ (D), màn hình dự kiến (G), ví dụ tính
    // thử đã xác nhận (A). Mỗi danh sách rỗng thì ẩn panel tương ứng (mục đã được chốt/giải quyết rời đi).
    function renderList(panelId, listId, countId, items, itemHtml) {
        const panel = document.getElementById(panelId);
        const list = document.getElementById(listId);
        if (!panel || !list || !Array.isArray(items)) return;
        if (items.length === 0) { panel.hidden = true; return; }
        list.innerHTML = items.map(itemHtml).join("");
        const count = document.getElementById(countId);
        if (count) count.textContent = `(${items.length})`;
        panel.hidden = false;
    }

    function renderOutlook(data) {
        renderList("scopePanel", "scopeList", "scopeCount", data.plannedScope,
            s => `<li class="scope-item">${escapeHtml(s)}</li>`);
        renderList("openQPanel", "openQList", "openQCount", data.openQuestions,
            q => `<li><button type="button" class="open-q-item" data-question="${escapeHtml(q)}" title="Bấm để trả lời điểm này">${escapeHtml(q)}</button></li>`);
        renderList("workedPanel", "workedList", "workedCount", data.workedExamples,
            ex => `<li class="worked-item">${escapeHtml(ex)}</li>`);
    }

    // Bấm một "điểm cần làm rõ" → soạn sẵn tin nhắn trả lời điểm đó vào ô nhập.
    const openQPanelEl = document.getElementById("openQPanel");
    if (openQPanelEl) {
        openQPanelEl.addEventListener("click", function (e) {
            const item = e.target.closest(".open-q-item");
            if (!item) return;
            messageInput.value = `Về điểm "${item.dataset.question}": `;
            resizeMessageInput();
            messageInput.focus();
            messageInput.setSelectionRange(messageInput.value.length, messageInput.value.length);
        });
    }

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

        form.classList.toggle("write-req-ready", ready);
        form.classList.toggle("write-req-waiting", !ready);

        const btn = form.querySelector(".write-req-btn");
        if (btn) {
            // Nút CHỈ enable khi đủ thông tin (mọi nhóm áp dụng [RÕ]) — khớp thuộc tính disabled
            // server render trong Index.cshtml. Panel "Tiến độ khai thác" đầy ⇔ ready ⇔ nút mở khóa.
            btn.disabled = !ready;
            btn.classList.toggle("primary", ready);
            btn.classList.toggle("outline", !ready);
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
            setWriteRequirementReady(data.invitesWriteRequirement === true);
            renderCoverage(data.coverage);
            renderDecisions(data.decisions);
            renderFlowDiagram(bubble, data.flowDiagram);

            // Lượt lỗi LLM: tô đỏ + nút "Thử lại" (server xóa lượt lỗi rồi chạy lại, khỏi gõ lại câu hỏi)
            // — markup khớp bản server render trong Index.cshtml.
            if ((data.reply || "").startsWith(LLM_FAILURE_PREFIX)) {
                bubble.classList.add("chat-error");
                bubble.insertAdjacentHTML("beforeend",
                    `<button type="button" class="btn outline small chat-retry-btn" title="Chạy lại lượt trả lời vừa lỗi — không cần gõ lại câu hỏi">↻ Thử lại</button>`);
            }
        } else {
            bubble.classList.add("chat-error");
            p.textContent = data.error || "Có lỗi khi xử lý lượt chat. Vui lòng thử lại.";
        }

        thinkingBox.style.display = "none";
        liveBubble = null;
        chatBusy = false;
        scrollToBottom();
    }

    function handleFrame(raw) {
        // Frame SSE: các dòng "data: {json}"; bỏ qua comment (": ping") và event end.
        const lines = raw.split("\n");
        let json = "";
        for (const line of lines) {
            if (line.startsWith("data: ")) json += line.slice(6);
        }
        if (!json) return;

        let ev;
        try { ev = JSON.parse(json); } catch { return; }

        if (ev.type === "status") {
            setThinkingText(ev.text || "BA đang xử lý…");
        } else if (ev.type === "token") {
            const bubble = ensureLiveBubble();
            bubble.querySelector("p").textContent += ev.text || "";
            scrollToBottom();
        } else if (ev.type === "done") {
            finishTurn(ev);
        } else if (ev.type === "decisions") {
            // Frame phụ SAU done: bản "Điều đã chốt" đã gộp lượt vừa rồi (server tách lời gọi LLM này
            // ra khỏi đường trả lời để lượt chat nhanh hơn — panel tự làm tươi trễ vài giây).
            renderDecisions(ev.decisions);
        } else if (ev.type === "outlook") {
            // Frame phụ SAU done: "triển vọng phỏng vấn" (điểm cần làm rõ + màn hình dự kiến + ví dụ
            // tính thử đã xác nhận) đã gộp lượt vừa rồi — làm tươi ba panel bên phải.
            renderOutlook(ev);
        }
    }

    // Ảnh đã đính kèm nhưng CHƯA gửi (staged): initSourceDropPaste đổ vào đây khi user đính kèm/dán/kéo-thả.
    // Khi bấm gửi mà mảng này khác rỗng, form ưu tiên upload ảnh (kèm ghi chú trong ô nhập) thay vì chat.
    const stagedImages = [];
    // Do initSourceDropPaste gán: upload các ảnh đang staged kèm ghi chú (text) rồi reload.
    let sendStagedImages = null;

    // true khi lượt đang gửi đã nhận ĐƯỢC ít nhất một frame SSE — quyết định cách phục hồi khi lỗi:
    // đã nhận frame nghĩa là server ĐANG xử lý lượt này (và sẽ lưu DB dù stream đứt) → chỉ reload;
    // chưa nhận frame nào mới được phép re-submit theo đường postback cổ điển.
    let sawFrame = false;

    async function streamChat(text, retry) {
        const fd = new FormData();
        fd.append("projectId", chatForm.querySelector('input[name="projectId"]').value);
        fd.append("message", text);
        if (retry) fd.append("retry", "true");
        const token = chatForm.querySelector('input[name="__RequestVerificationToken"]');
        if (token) fd.append("__RequestVerificationToken", token.value);

        const response = await fetch(STREAM_URL, { method: "POST", body: fd, headers: { Accept: "text/event-stream" } });
        if (!response.ok || !response.body) throw new Error("stream request failed");

        const reader = response.body.getReader();
        const decoder = new TextDecoder();
        let buffer = "";

        while (true) {
            const { done, value } = await reader.read();
            if (done) break;

            buffer += decoder.decode(value, { stream: true });
            let idx;
            while ((idx = buffer.indexOf("\n\n")) >= 0) {
                const frame = buffer.slice(0, idx);
                buffer = buffer.slice(idx + 2);
                sawFrame = true;
                handleFrame(frame);
            }
        }

        return sawFrame;
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

        chatBusy = true;
        sawFrame = false;
        appendUserBubble(text);

        messageInput.value = "";
        resizeMessageInput();

        // Lượt đã được trả lời → ẩn các gợi ý cũ ngay (gợi ý mới render lại ở frame done nếu có).
        if (suggestionList) suggestionList.style.display = "none";

        setThinkingText("BA is analyzing requirements...");
        thinkingBox.style.display = "block";
        scrollToBottom();

        streamChat(text, false).then(function (gotFrame) {
            if (!gotFrame) throw new Error("no frame");
        }).catch(function () {
            if (!chatBusy) return; // done đã xử lý xong, lỗi chỉ là đuôi stream — bỏ qua

            if (sawFrame) {
                // Stream đứt giữa chừng NHƯNG server đã nhận lượt này và vẫn chạy trọn
                // (CancellationToken.None) → reload để hiển thị bản đã lưu; re-submit sẽ nhân đôi lượt.
                location.reload();
                return;
            }

            // Hỏng từ trước khi nhận frame nào (mạng/proxy không stream được):
            // quay về postback cổ điển — submit native để không đi lại listener này.
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

        const failedBubble = btn.closest(".req-msg.ba");
        if (failedBubble) failedBubble.remove();
        if (suggestionList) suggestionList.style.display = "none";

        setThinkingText("BA đang thử trả lời lại…");
        thinkingBox.style.display = "block";
        scrollToBottom();

        streamChat("", true).then(function (gotFrame) {
            if (!gotFrame) throw new Error("no frame");
        }).catch(function () {
            if (!chatBusy) return;
            location.reload();
        });
    });

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

    // ==== Đính kèm / dán / kéo-thả ảnh — xem trước trong khung chat rồi mới gửi ====
    // Người dùng nghiệp vụ hay chụp màn hình Excel/biểu mẫu — bắt họ đi qua form "Tài liệu nguồn" ở
    // sidebar là ma sát thừa. Đính kèm (nút), dán (Ctrl+V) hoặc kéo-thả ảnh vào khung chat sẽ STAGE ảnh
    // thành thumbnail nhỏ ngay trên ô nhập: user có thể gõ thêm ghi chú/thông tin, xóa bớt ảnh, rồi bấm
    // gửi mới thật sự upload qua endpoint UploadSource (kèm ghi chú) → BA tóm tắt → reload.
    (function initSourceDropPaste() {
        const token = chatForm.querySelector('input[name="__RequestVerificationToken"]');
        const projectIdInput = chatForm.querySelector('input[name="projectId"]');
        const preview = document.getElementById("attachPreview");
        if (!token || !projectIdInput) return;

        let uploading = false;
        const defaultPlaceholder = messageInput.placeholder;

        // Vẽ lại khay xem trước từ stagedImages. Mỗi ảnh giữ kèm objectURL để thu hồi khi gỡ (tránh rò
        // bộ nhớ). Ẩn khay + trả lại placeholder gốc khi không còn ảnh nào.
        function renderPreview() {
            if (!preview) return;

            if (stagedImages.length === 0) {
                preview.innerHTML = "";
                preview.hidden = true;
                messageInput.placeholder = defaultPlaceholder;
                return;
            }

            preview.innerHTML = stagedImages.map((img, i) => `
                <div class="attach-thumb" title="${escapeHtml(img.file.name || "ảnh")}">
                    <img src="${img.url}" alt="${escapeHtml(img.file.name || "ảnh đính kèm")}" />
                    <button type="button" class="attach-thumb-remove" data-i="${i}" aria-label="Gỡ ảnh này">×</button>
                </div>
            `).join("");
            preview.hidden = false;
        }

        function stageImages(fileList) {
            const images = Array.from(fileList || []).filter(f => f.type && f.type.startsWith("image/"));
            if (images.length === 0) return;

            images.forEach(file => stagedImages.push({ file, url: URL.createObjectURL(file) }));
            renderPreview();
            messageInput.placeholder = "Thêm ghi chú cho ảnh (không bắt buộc) rồi bấm gửi…";
            messageInput.focus();
        }

        function clearStaged() {
            stagedImages.forEach(img => URL.revokeObjectURL(img.url));
            stagedImages.length = 0;
            renderPreview();
        }

        // Gỡ MỘT ảnh khỏi khay (thu hồi objectURL của đúng ảnh đó).
        if (preview) {
            preview.addEventListener("click", function (e) {
                const btn = e.target.closest(".attach-thumb-remove");
                if (!btn) return;
                const idx = Number(btn.dataset.i);
                if (Number.isNaN(idx) || idx < 0 || idx >= stagedImages.length) return;
                URL.revokeObjectURL(stagedImages[idx].url);
                stagedImages.splice(idx, 1);
                renderPreview();
            });
        }

        // Gửi các ảnh đang staged (kèm ghi chú tùy chọn) qua UploadSource → BA tóm tắt → reload.
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

            setThinkingText("BA đang đọc ảnh…");
            thinkingBox.style.display = "block";
            scrollToBottom();

            const fd = new FormData();
            fd.append("projectId", projectIdInput.value);
            fd.append("__RequestVerificationToken", token.value);
            if (note) fd.append("note", note);
            stagedImages.forEach(img => fd.append("files", img.file, img.file.name || "anh-dan.png"));

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
                // Hoàn tác: gỡ bong bóng lạc quan, khôi phục khay ảnh + ghi chú để user thử lại.
                if (optimisticBubble) optimisticBubble.remove();
                thinkingBox.style.display = "none";
                uploading = false;
                chatBusy = false;
                if (note) {
                    messageInput.value = note;
                    resizeMessageInput();
                }
                renderPreview();
                messageInput.placeholder = "Thêm ghi chú cho ảnh (không bắt buộc) rồi bấm gửi…";
                alert("Không tải được ảnh lên. Anh/chị thử lại hoặc dùng nút Upload ở mục 'Tài liệu nguồn'.");
            }
        };

        messageInput.addEventListener("paste", function (e) {
            const items = e.clipboardData && e.clipboardData.files;
            if (items && items.length > 0 && Array.from(items).some(f => f.type.startsWith("image/"))) {
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

        // Nút đính kèm ảnh trong khung soạn: mở hộp chọn file rồi STAGE ảnh (xem trước) như dán/kéo-thả.
        // Điểm bấm rõ ràng cho người không biết mẹo dán/kéo-thả.
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
