// poc-share.js — trang xem bản demo dành cho NGƯỜI ĐƯỢC GỬI LINK (không có tài khoản).
// Cùng cơ chế với poc-review.js (annotator chạy trong iframe, nói chuyện với trang cha qua postMessage;
// mọi thao tác GHI đi từ trang cha), nhưng rút gọn còn đúng những gì khách cần: xem, ghim góp ý, đọc góp
// ý của người khác. Không xóa, không gửi cho Dev, không điều khiển quy trình.
(function () {
    "use strict";

    const root = document.getElementById("pocShareRoot");
    const frame = document.getElementById("pocFrame");
    if (!root || !frame) return;

    const commentsUrl = root.dataset.commentsUrl;
    const addUrl = root.dataset.addUrl;

    const pinModeBtn = document.getElementById("pinModeBtn");
    const pinModeHint = document.getElementById("pinModeHint");
    const listEl = document.getElementById("pocCommentList");
    const countEl = document.getElementById("pocCommentCount");
    const formEl = document.getElementById("pocCommentForm");
    const targetLabelEl = document.getElementById("pocTargetLabel");
    const nameEl = document.getElementById("pocGuestName");
    const textEl = document.getElementById("pocCommentText");
    const cancelBtn = document.getElementById("pocCommentCancel");
    const antiForgery = formEl.querySelector('input[name="__RequestVerificationToken"]');

    // Tên người góp ý được nhớ trong máy họ: một buổi nghiệm thu có nhiều góp ý liên tiếp, bắt gõ lại tên
    // mỗi lần là cách nhanh nhất để họ bỏ cuộc giữa chừng.
    const NAME_KEY = "poc-share-guest-name";

    let comments = [];
    let pinMode = false;
    let pendingPick = null;
    let frameReady = false;

    function postToFrame(msg) {
        if (frame.contentWindow) frame.contentWindow.postMessage(msg, "*");
    }

    function numbered() {
        return comments.map((c, i) => Object.assign({}, c, { index: i + 1 }));
    }

    function pushCommentsToFrame() {
        if (frameReady) postToFrame({ type: "poc-comments", items: numbered() });
    }

    function setPinMode(enabled) {
        pinMode = enabled;
        postToFrame({ type: "poc-mode", enabled: pinMode });
        pinModeBtn.classList.toggle("primary", pinMode);
        pinModeBtn.classList.toggle("outline", !pinMode);
        pinModeBtn.textContent = pinMode ? "📌 Đang ghim — bấm để tắt" : "📌 Bật chế độ ghim";
        pinModeHint.textContent = pinMode
            ? "Bấm vào phần tử trong bản demo để góp ý (Esc để thoát)."
            : "Chế độ ghim đang tắt — bản demo thao tác bình thường.";
    }

    function renderList() {
        const items = numbered();
        countEl.textContent = items.length ? `(${items.length})` : "";

        if (!items.length) {
            listEl.innerHTML = '<p class="muted">Chưa có góp ý nào. Bật chế độ ghim và bấm vào phần tử trong bản demo.</p>';
            return;
        }

        listEl.innerHTML = items.map(c => `
            <div class="poc-comment-item" data-id="${c.id}">
                <div class="poc-comment-head">
                    <span class="poc-pin-no">${c.index}</span>
                    <span class="poc-comment-target" title="${escapeHtml(c.elementPath || "")}">${escapeHtml(c.elementLabel || "Vị trí trên trang")}</span>
                </div>
                ${c.pageView ? `<div class="poc-comment-view">Màn hình: ${escapeHtml(c.pageView)}</div>` : ""}
                <div class="poc-comment-text">${escapeHtml(c.comment)}</div>
                <div class="poc-comment-meta">${escapeHtml(c.createdBy || "?")} · ${new Date(c.createdAt).toLocaleString()}</div>
            </div>
        `).join("");
    }

    async function loadComments() {
        try {
            const response = await fetch(commentsUrl);
            comments = response.ok ? await response.json() : [];
        } catch {
            comments = [];
        }
        renderList();
        pushCommentsToFrame();
    }

    function openForm(pick) {
        pendingPick = pick;
        targetLabelEl.textContent = pick.elementLabel || "Vị trí trên trang";
        textEl.value = "";
        formEl.hidden = false;
        (nameEl.value ? textEl : nameEl).focus();
    }

    function closeForm() {
        pendingPick = null;
        formEl.hidden = true;
        textEl.value = "";
    }

    formEl.addEventListener("submit", async function (e) {
        e.preventDefault();
        if (!pendingPick) return;

        const name = nameEl.value.trim();
        const text = textEl.value.trim();
        if (!name) { nameEl.focus(); return; }
        if (!text) { textEl.focus(); return; }

        const fd = new FormData();
        fd.append("guestName", name);
        fd.append("pageView", pendingPick.pageView || "");
        fd.append("elementLabel", pendingPick.elementLabel || "");
        fd.append("elementPath", pendingPick.elementPath || "");
        fd.append("xPercent", pendingPick.xPercent || 0);
        fd.append("yPercent", pendingPick.yPercent || 0);
        fd.append("comment", text);
        if (antiForgery) fd.append("__RequestVerificationToken", antiForgery.value);

        let response;
        try {
            response = await fetch(addUrl, { method: "POST", body: fd });
        } catch {
            alert("Không gửi được góp ý — kiểm tra kết nối rồi thử lại.");
            return;
        }
        if (!response.ok) {
            alert(await response.text() || "Không gửi được góp ý.");
            return;
        }

        try { localStorage.setItem(NAME_KEY, name); } catch { /* chế độ riêng tư: không nhớ tên, không sao */ }
        comments.push(await response.json());
        closeForm();
        renderList();
        pushCommentsToFrame();
    });

    cancelBtn.addEventListener("click", closeForm);

    // Bấm một góp ý trong danh sách → nháy pin tương ứng trong bản demo.
    listEl.addEventListener("click", function (e) {
        const item = e.target.closest(".poc-comment-item");
        if (!item) return;
        const index = comments.findIndex(c => c.id === item.dataset.id);
        if (index >= 0) postToFrame({ type: "poc-focus", id: item.dataset.id, pageView: comments[index].pageView });
    });

    pinModeBtn.addEventListener("click", () => setPinMode(!pinMode));

    window.addEventListener("message", function (e) {
        if (e.source !== frame.contentWindow || !e.data || typeof e.data !== "object") return;

        if (e.data.type === "poc-annotator-ready") {
            frameReady = true;
            postToFrame({ type: "poc-mode", enabled: pinMode });
            pushCommentsToFrame();
            return;
        }
        if (e.data.type === "poc-pick") openForm(e.data);
        if (e.data.type === "poc-mode-off") setPinMode(false);
    });

    try {
        const saved = localStorage.getItem(NAME_KEY);
        if (saved) nameEl.value = saved;
    } catch { /* không đọc được localStorage thì chỉ là phải gõ lại tên */ }

    loadComments();
})();
