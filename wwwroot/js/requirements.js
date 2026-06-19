const chatForm = document.getElementById("chatForm");
const messageInput = document.getElementById("messageInput");
const chatMessages = document.getElementById("chatMessages");
const thinkingBox = document.getElementById("thinkingBox");
const suggestionChips = document.getElementById("suggestionChips");

function escapeHtml(value) {
    return String(value ?? "")
        .replaceAll("&", "&amp;")
        .replaceAll("<", "&lt;")
        .replaceAll(">", "&gt;")
        .replaceAll('"', "&quot;")
        .replaceAll("'", "&#039;");
}

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
            <div class="msg you">
                <p>${escapeHtml(text)}</p>
            </div>
        `;

        thinkingBox.insertAdjacentHTML("beforebegin", html);

        messageInput.value = "";
        resizeMessageInput();

        // Lượt đã được trả lời → ẩn các gợi ý cũ ngay (trang sẽ reload với gợi ý mới nếu có).
        if (suggestionChips) suggestionChips.style.display = "none";

        thinkingBox.style.display = "block";
        chatMessages.scrollTop = chatMessages.scrollHeight;
    });

    // Bấm một "chip" gợi ý = điền sẵn câu trả lời rồi gửi qua đúng pipeline submit ở trên,
    // để người dùng không phải gõ tay từng chữ. Vẫn có thể tự nhập nếu không gợi ý nào khớp.
    if (suggestionChips) {
        suggestionChips.addEventListener("click", function (e) {
            const chip = e.target.closest(".suggestion-chip");
            if (!chip) return;

            const text = (chip.dataset.suggestion || "").trim();
            if (!text) return;

            messageInput.value = text;
            chatForm.requestSubmit();
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
