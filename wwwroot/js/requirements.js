const chatForm = document.getElementById("chatForm");
const messageInput = document.getElementById("messageInput");
const chatMessages = document.getElementById("chatMessages");
const thinkingBox = document.getElementById("thinkingBox");

function escapeHtml(value) {
    return value
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
        thinkingBox.style.display = "block";
        chatMessages.scrollTop = chatMessages.scrollHeight;
    });
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
    }
}

function closeRequirementModal() {
    document.getElementById("requirementModal").style.display = "none";
}

function showDocument(id) {
    document.querySelectorAll(".doc-preview")
        .forEach(x => x.style.display = "none");

    const target = document.getElementById("doc-" + id);

    if (target)
        target.style.display = "block";
}
