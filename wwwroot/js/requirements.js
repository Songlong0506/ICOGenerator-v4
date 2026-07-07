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

    chatForm.addEventListener("submit", function (e) {
        const text = messageInput.value.trim();

        if (!text) {
            e.preventDefault();
            return;
        }

        document.getElementById("hiddenMessage").value = text;

        const html = `
            <div class="req-msg you">
                <p>${escapeHtml(text)}</p>
            </div>
        `;

        thinkingBox.insertAdjacentHTML("beforebegin", html);

        messageInput.value = "";
        resizeMessageInput();

        // Lượt đã được trả lời → ẩn các gợi ý cũ ngay (trang sẽ reload với gợi ý mới nếu có).
        if (suggestionList) suggestionList.style.display = "none";

        thinkingBox.style.display = "block";
        chatMessages.scrollTop = chatMessages.scrollHeight;
    });

    // Chọn một đáp án gợi ý = điền sẵn câu trả lời rồi gửi qua đúng pipeline submit ở trên,
    // để người dùng không phải gõ tay từng chữ. Vẫn có thể tự nhập nếu không gợi ý nào khớp.
    function selectSuggestion(option) {
        const text = (option?.dataset.suggestion || "").trim();
        if (!text) return;

        messageInput.value = text;
        chatForm.requestSubmit();
    }

    if (suggestionList) {
        suggestionList.addEventListener("click", function (e) {
            const option = e.target.closest(".suggestion-option");
            if (!option) return;

            selectSuggestion(option);
        });

        // Phím tắt số (1–9) chọn nhanh đáp án — giống option-select của Claude. Chỉ bắt khi
        // danh sách đang hiện và con trỏ KHÔNG ở ô nhập, để không cướp phím số khi đang soạn tin.
        document.addEventListener("keydown", function (e) {
            if (!suggestionList || suggestionList.style.display === "none") return;
            if (e.ctrlKey || e.metaKey || e.altKey) return;

            const active = document.activeElement;
            if (active && (active.tagName === "TEXTAREA" || active.tagName === "INPUT")) return;

            if (e.key < "1" || e.key > "9") return;

            const options = suggestionList.querySelectorAll(".suggestion-option");
            const index = Number(e.key) - 1;
            if (index >= options.length) return;

            e.preventDefault();
            selectSuggestion(options[index]);
        });
    }
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

function openRequirementModal(version) {
    document.getElementById("modalTitle").innerText =
        "Requirement " + version;

    document.getElementById("requirementModal").style.display = "flex";

    document.querySelectorAll(".doc-tab")
        .forEach(x => x.style.display = "none");

    document.querySelectorAll(".doc-preview")
        .forEach(x => x.style.display = "none");

    const tabs = document.querySelectorAll(`.doc-tab[data-version="${version}"]`);
    const docs = document.querySelectorAll(`.doc-preview[data-version="${version}"]`);

    tabs.forEach(x => x.style.display = "inline-block");

    if (docs.length > 0) {
        docs[0].style.display = "block";
        loadDocPreview(docs[0]);
    }
}

function closeRequirementModal() {
    document.getElementById("requirementModal").style.display = "none";
}

function showDocument(id) {
    document.querySelectorAll(".doc-preview")
        .forEach(x => x.style.display = "none");

    const target = document.getElementById("doc-" + id);

    if (target) {
        target.style.display = "block";
        loadDocPreview(target);
    }
}

// ---------- Panel "Dự án tương tự" (tri thức xuyên dự án) ----------
// Đổ danh sách dự án khác có tài liệu ĐÃ DUYỆT khớp nội dung đang trao đổi. Fail-open: endpoint
// lỗi/không có gì khớp thì panel giữ trạng thái ẩn — trang Requirements không phụ thuộc tính năng này.
(function () {
    const panel = document.getElementById("similarPanel");
    const list = document.getElementById("similarList");
    if (!panel || !list) return;

    async function loadSimilarProjects() {
        try {
            const response = await fetch(`/Requirements/SimilarProjects?projectId=${encodeURIComponent(panel.dataset.projectId)}`);
            if (!response.ok) return;
            const items = await response.json();
            if (!Array.isArray(items) || items.length === 0) return;

            // Dựng bằng DOM API (textContent) — tên dự án/snippet là dữ liệu người dùng, không nhét thẳng vào HTML.
            list.replaceChildren(...items.map(function (item) {
                const li = document.createElement("li");
                li.className = "similar-item";

                const name = document.createElement("div");
                name.className = "similar-name";
                name.textContent = item.projectName;
                if (item.orgUnitCode) {
                    const unit = document.createElement("span");
                    unit.className = "similar-unit";
                    unit.textContent = item.orgUnitCode;
                    name.appendChild(unit);
                }

                const docs = document.createElement("div");
                docs.className = "similar-docs";
                docs.textContent = "Khớp: " + item.matchedDocuments.join(", ");

                const snippet = document.createElement("div");
                snippet.className = "similar-snippet";
                snippet.textContent = item.snippet;

                li.append(name, docs, snippet);
                return li;
            }));
            panel.hidden = false;
        } catch { /* fail-open: giữ panel ẩn */ }
    }

    document.addEventListener("DOMContentLoaded", loadSimilarProjects);
})();
