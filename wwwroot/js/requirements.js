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
        if (fill) fill.style.width = applicable === 0 ? "0%" : `${Math.round(clear * 100 / applicable)}%`;
        const text = document.getElementById("coverageProgressText");
        if (text) text.textContent = `Đã rõ ${clear}/${applicable} nhóm`;
        panel.hidden = false;

        // Cổng "chốt nhanh" bám cùng bản đồ: còn nhóm trống thì hiện kèm số nhóm, hết thì ẩn hẳn (lúc đó
        // nút "Write Requirement" đã mở — không còn gì để chốt nhanh).
        const quick = document.getElementById("quickClosePanel");
        if (quick) {
            const pending = items.filter(x => x.status === "CHƯA HỎI" || x.status === "MỘT PHẦN").length;
            quick.hidden = pending === 0;
            const countEl = document.getElementById("quickCloseCount");
            if (countEl) countEl.textContent = String(pending);
        }
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

    // "Triển vọng phỏng vấn" (frame "outlook"): điểm cần làm rõ (D), màn hình dự kiến (G), ví dụ tính
    // thử đã xác nhận (A). Mỗi danh sách rỗng thì ẩn panel tương ứng (mục đã được chốt/giải quyết rời đi).
    function renderList(panelId, listId, countId, items, itemHtml) {
        const panel = document.getElementById(panelId);
        const list = document.getElementById(listId);
        if (!panel || !list || !Array.isArray(items)) return;
        // Người dùng đang SỬA TAY panel này (xem initWorkedExamplesEditor): đừng viết đè lên thứ họ đang
        // gõ — bản chắt lọc của lượt chat sẽ được áp ở lần tải trang sau.
        if (panel.dataset.editing === "true") return;
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
            // Frame phụ SAU done: bản "Điều đã chốt" đã gộp lượt vừa rồi (server tách lời gọi LLM này
            // ra khỏi đường trả lời để lượt chat nhanh hơn — panel tự làm tươi trễ vài giây).
            renderDecisions(ev.decisions);
        } else if (ev.type === "outlook") {
            // Frame phụ SAU done: "triển vọng phỏng vấn" (điểm cần làm rõ + màn hình dự kiến + ví dụ
            // tính thử đã xác nhận) đã gộp lượt vừa rồi — làm tươi ba panel bên phải.
            renderOutlook(ev);
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

        // Lượt đã được trả lời → ẩn các gợi ý cũ ngay (gợi ý mới render lại ở frame done nếu có).
        hideSuggestions();

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

    // ==== Cổng "chốt nhanh phần còn lại" ====
    // Đường tăng tốc cho phần phỏng vấn còn lại: BA đề xuất sẵn MỘT phương án cho mỗi nhóm bản đồ bao phủ
    // còn trống, người dùng duyệt một lần thay vì trả lời từng câu qua nhiều lượt chat. Mỗi dòng mặc định
    // "Đồng ý"; bấm "Sửa" mở ô gõ đè (dòng nào bị bỏ trống thì không được chốt). Chốt xong KHÔNG reload:
    // server trả bản đồ đã gộp + cờ ready nên panel tiến độ và nút "Write Requirement" cập nhật tại chỗ,
    // và lượt hội thoại vừa ghi được nối vào khung chat như một lượt bình thường.
    (function initQuickClose() {
        const panel = document.getElementById("quickClosePanel");
        if (!panel) return;

        const startBtn = document.getElementById("quickCloseStartBtn");
        const bodyEl = document.getElementById("quickCloseBody");
        const msgEl = document.getElementById("quickCloseMsg");
        const hintEl = panel.querySelector(".quickclose-hint");
        if (!startBtn || !bodyEl || !msgEl) return;

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

        function renderProposals(proposals) {
            bodyEl.innerHTML = `
                <div class="quickclose-lead">
                    Đây là cách BA hiểu các điểm còn lại. Điểm nào <b>đúng ý</b> thì để nguyên; điểm nào
                    <b>chưa đúng</b> thì bấm "Sửa" và ghi lại theo ý anh/chị. Bấm chốt là mình ghi nhận
                    hết một lượt.
                </div>
                <ul class="quickclose-list">
                    ${proposals.map((p, i) => `
                        <li class="quickclose-item" data-index="${i}" data-group="${escapeHtml(p.group)}" data-question="${escapeHtml(p.question || "")}">
                            <div class="quickclose-group">${escapeHtml(p.group)}</div>
                            ${p.question ? `<div class="quickclose-question">${escapeHtml(p.question)}</div>` : ""}
                            <div class="quickclose-proposal">${escapeHtml(p.proposal)}</div>
                            <textarea class="quickclose-fix" rows="2" hidden>${escapeHtml(p.proposal)}</textarea>
                            <div class="quickclose-actions">
                                <button type="button" class="quickclose-vote ok is-on" data-vote="ok">Đúng ý</button>
                                <button type="button" class="quickclose-vote edit" data-vote="edit">Sửa</button>
                            </div>
                        </li>
                    `).join("")}
                </ul>
                <div class="quickclose-bar">
                    <button type="button" class="btn primary full" id="quickCloseConfirmBtn">
                        ✓ Chốt ${proposals.length} điểm này
                    </button>
                    <button type="button" class="btn outline full" id="quickCloseCancelBtn">Để tôi trả lời trong chat</button>
                </div>`;
            bodyEl.hidden = false;
            startBtn.hidden = true;
            if (hintEl) hintEl.hidden = true;
        }

        // Ghi lượt vừa chốt vào khung chat như một lượt bình thường: không có nó, người dùng bấm xong
        // thấy panel biến mất mà hội thoại không đổi gì — y như thao tác vừa rồi rơi vào khoảng không.
        function appendTurns(count) {
            const you = document.createElement("div");
            you.className = "req-msg you";
            you.innerHTML = `<p>Tôi chốt luôn ${count} điểm còn lại theo phương án BA đề xuất.</p>`;
            chatMessages.insertBefore(you, suggestionList);
            scrollToBottom();
        }

        startBtn.addEventListener("click", async function () {
            const original = startBtn.innerHTML;
            startBtn.disabled = true;
            startBtn.textContent = "BA đang soạn phương án…";
            msgEl.textContent = "";
            try {
                const data = await post(panel.dataset.proposeUrl, null);
                if (data.ok && Array.isArray(data.proposals) && data.proposals.length > 0) {
                    renderProposals(data.proposals);
                    return;
                }
                msgEl.textContent = data.error || "Chưa soạn được phương án.";
            } catch {
                msgEl.textContent = "Không gửi được — kiểm tra kết nối rồi thử lại.";
            }
            startBtn.disabled = false;
            startBtn.innerHTML = original;
        });

        bodyEl.addEventListener("click", async function (e) {
            const vote = e.target.closest(".quickclose-vote");
            if (vote) {
                const li = vote.closest(".quickclose-item");
                const edit = vote.dataset.vote === "edit";
                li.querySelector(".quickclose-vote.ok").classList.toggle("is-on", !edit);
                li.querySelector(".quickclose-vote.edit").classList.toggle("is-on", edit);
                li.querySelector(".quickclose-proposal").hidden = edit;
                const fix = li.querySelector(".quickclose-fix");
                fix.hidden = !edit;
                if (edit) fix.focus();
                return;
            }

            if (e.target.closest("#quickCloseCancelBtn")) {
                bodyEl.hidden = true;
                bodyEl.innerHTML = "";
                startBtn.hidden = false;
                startBtn.disabled = false;
                if (hintEl) hintEl.hidden = false;
                return;
            }

            const confirmBtn = e.target.closest("#quickCloseConfirmBtn");
            if (!confirmBtn) return;

            const items = Array.from(bodyEl.querySelectorAll(".quickclose-item"));
            const decisions = items.map(li => {
                const editing = li.querySelector(".quickclose-vote.edit").classList.contains("is-on");
                const answer = editing
                    ? li.querySelector(".quickclose-fix").value.trim()
                    : li.querySelector(".quickclose-proposal").textContent.trim();
                return { group: li.dataset.group, question: li.dataset.question, answer: answer };
            }).filter(d => d.answer.length > 0);

            if (decisions.length === 0) {
                msgEl.textContent = "Chưa có điểm nào để chốt — ô đã sửa đang để trống.";
                return;
            }

            const original = confirmBtn.textContent;
            confirmBtn.disabled = true;
            confirmBtn.textContent = "Đang ghi nhận…";
            msgEl.textContent = "";
            try {
                const data = await post(panel.dataset.confirmUrl, { decisionsJson: JSON.stringify(decisions) });
                if (data.ok) {
                    appendTurns(decisions.length);
                    renderCoverage(data.coverage);
                    setWriteRequirementReady(!!data.ready);
                    panel.hidden = true;
                    // Tải lại để khung chat có đúng lượt BA vừa lưu (kèm gợi ý/ngữ cảnh) thay vì bản dựng
                    // tạm ở client — chờ một nhịp để người dùng kịp thấy thanh tiến độ nhảy lên.
                    setTimeout(() => location.reload(), 900);
                    return;
                }
                msgEl.textContent = data.error || "Không ghi nhận được.";
            } catch {
                msgEl.textContent = "Không gửi được — kiểm tra kết nối rồi thử lại.";
            }
            confirmBtn.disabled = false;
            confirmBtn.textContent = original;
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

    syncButtons();
})();

// ==== Sửa tay panel "Ví dụ đã xác nhận" ====
// Đây là oracle mà POC bị chấm theo (spec đúc thành "## 13. Worked Examples", POC phải tính lại ra đúng
// kết quả), nên một ví dụ chép sai từ hội thoại làm cả tầng tự kiểm chấm theo chuẩn sai. Editor là một
// textarea "mỗi dòng một ví dụ" — đơn giản nhất cho người nghiệp vụ và khớp đúng dạng lưu (bullet).
(function initWorkedExamplesEditor() {
    const panel = document.getElementById("workedPanel");
    if (!panel) return;

    const listEl = document.getElementById("workedList");
    const editBtn = document.getElementById("workedEditBtn");
    const editor = document.getElementById("workedEditor");
    const textEl = document.getElementById("workedEditorText");
    const saveBtn = document.getElementById("workedSaveBtn");
    const cancelBtn = document.getElementById("workedCancelBtn");
    const msgEl = document.getElementById("workedEditorMsg");
    const countEl = document.getElementById("workedCount");
    if (!listEl || !editBtn || !editor) return;

    function currentItems() {
        return Array.from(listEl.querySelectorAll(".worked-item")).map(li => li.textContent.trim());
    }

    function openEditor() {
        textEl.value = currentItems().join("\n");
        panel.dataset.editing = "true";
        editor.hidden = false;
        listEl.hidden = true;
        editBtn.hidden = true;
        msgEl.textContent = "";
        textEl.focus();
    }

    function closeEditor() {
        delete panel.dataset.editing;
        editor.hidden = true;
        listEl.hidden = false;
        editBtn.hidden = false;
    }

    editBtn.addEventListener("click", openEditor);
    cancelBtn.addEventListener("click", closeEditor);

    saveBtn.addEventListener("click", async function () {
        const examples = textEl.value.split("\n")
            .map(l => l.replace(/^[-*]\s+/, "").trim())
            .filter(l => l.length > 0);

        const token = document.querySelector('input[name="__RequestVerificationToken"]');
        const fd = new FormData();
        fd.append("projectId", window.REQUIREMENTS_PROJECT_ID || "");
        fd.append("examplesJson", JSON.stringify(examples));
        if (token) fd.append("__RequestVerificationToken", token.value);

        saveBtn.disabled = true;
        msgEl.textContent = "Đang lưu…";
        try {
            const resp = await fetch(panel.dataset.updateUrl, { method: "POST", body: fd });
            const data = await resp.json();
            if (data.ok) {
                // Render lại tại chỗ: lượt ghi cũng thêm một lượt hội thoại, nhưng nó chỉ là dấu vết cho
                // bước chắt lọc sau — không có gì mới để user đọc nên khỏi reload cả trang.
                listEl.innerHTML = examples
                    .map(e => `<li class="worked-item">${escapeHtml(e)}</li>`).join("");
                if (countEl) countEl.textContent = `(${examples.length})`;
                panel.hidden = examples.length === 0;
                msgEl.textContent = "";
                closeEditor();
            } else {
                msgEl.textContent = data.error || "Không lưu được.";
            }
        } catch {
            msgEl.textContent = "Không lưu được — kiểm tra kết nối rồi thử lại.";
        }
        saveBtn.disabled = false;
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

        const original = submitBtn.textContent;
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
