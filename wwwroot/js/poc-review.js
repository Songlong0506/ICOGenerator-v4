// poc-review.js — trang cha của POC Review: giữ danh sách ghi chú + form ghim, nói chuyện với
// annotator trong iframe POC (poc-annotator.js) qua postMessage. Mọi thao tác GHI (thêm/xóa ghi chú)
// đều đi từ trang cha (same-origin, có cookie + antiforgery); iframe sandbox không gọi được gì.
(function () {
    "use strict";

    const root = document.getElementById("pocReviewRoot");
    const frame = document.getElementById("pocFrame");
    if (!root || !frame) return;

    const commentsUrl = root.dataset.commentsUrl;
    const addUrl = root.dataset.addUrl;
    const deleteUrl = root.dataset.deleteUrl;
    const projectId = root.dataset.projectId;

    const pinModeBtn = document.getElementById("pinModeBtn");
    const pinModeHint = document.getElementById("pinModeHint");
    const listEl = document.getElementById("pocCommentList");
    const countEl = document.getElementById("pocCommentCount");
    const formEl = document.getElementById("pocCommentForm");
    const targetLabelEl = document.getElementById("pocTargetLabel");
    const textEl = document.getElementById("pocCommentText");
    const cancelBtn = document.getElementById("pocCommentCancel");
    const antiForgery = formEl.querySelector('input[name="__RequestVerificationToken"]');

    let comments = [];
    let pinMode = false;
    let pendingPick = null; // mô tả phần tử vừa click trong POC, chờ người dùng gõ ghi chú
    let frameReady = false;

    // escapeHtml dùng chung ở site.js (nạp qua _Layout trước file này).

    function postToFrame(msg) {
        if (frame.contentWindow) frame.contentWindow.postMessage(msg, "*");
    }

    // Đánh số hiển thị 1..n theo thứ tự tạo — pin trong POC và danh sách bên phải dùng CÙNG số.
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
            ? "Click vào phần tử trong POC để ghi chú (Esc để thoát)."
            : "Chế độ ghim đang tắt — POC thao tác bình thường.";
    }

    function statusBadge(status) {
        return status === "Sent"
            ? '<span class="poc-badge sent">đã gửi Dev</span>'
            : '<span class="poc-badge open">chờ gửi</span>';
    }

    function renderList() {
        const items = numbered();
        const open = items.filter(c => c.status === "Open").length;
        countEl.textContent = items.length ? `(${open} chờ gửi / ${items.length})` : "";

        if (!items.length) {
            listEl.innerHTML = '<p class="muted">Chưa có ghi chú nào. Bật chế độ ghim và click vào phần tử trong POC.</p>';
            return;
        }

        listEl.innerHTML = items.map(c => `
            <div class="poc-comment-item" data-id="${c.id}">
                <div class="poc-comment-head">
                    <span class="poc-pin-no${c.status === "Sent" ? " sent" : ""}">${c.index}</span>
                    <span class="poc-comment-target" title="${escapeHtml(c.elementPath || "")}">${escapeHtml(c.elementLabel || "Vị trí trên trang")}</span>
                    ${statusBadge(c.status)}
                    ${c.canDelete ? `<button type="button" class="poc-comment-del" data-id="${c.id}" title="Xóa ghi chú">🗑</button>` : ""}
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

    // ===== Form ghim =====

    function openForm(pick) {
        pendingPick = pick;
        targetLabelEl.textContent = (pick.pageView ? `[${pick.pageView}] ` : "") + (pick.elementLabel || "Vị trí trên trang");
        formEl.hidden = false;
        textEl.value = "";
        textEl.focus();
    }

    function closeForm() {
        pendingPick = null;
        formEl.hidden = true;
    }

    formEl.addEventListener("submit", async function (e) {
        e.preventDefault();
        if (!pendingPick) return;

        const comment = textEl.value.trim();
        if (!comment) { textEl.focus(); return; }

        const fd = new FormData();
        fd.append("projectId", projectId);
        fd.append("pageView", pendingPick.pageView || "");
        fd.append("elementLabel", pendingPick.elementLabel || "");
        fd.append("elementPath", pendingPick.elementPath || "");
        fd.append("xPercent", String(pendingPick.xPercent || 0));
        fd.append("yPercent", String(pendingPick.yPercent || 0));
        fd.append("comment", comment);
        if (antiForgery) fd.append("__RequestVerificationToken", antiForgery.value);

        let response;
        try {
            response = await fetch(addUrl, { method: "POST", body: fd });
        } catch {
            alert("Không gửi được ghi chú — kiểm tra kết nối rồi thử lại.");
            return;
        }

        if (!response.ok) {
            alert(await response.text().catch(() => "Không gửi được ghi chú."));
            return;
        }

        comments.push(await response.json());
        closeForm();
        renderList();
        pushCommentsToFrame();
    });

    cancelBtn.addEventListener("click", closeForm);

    // ===== Danh sách: click để nháy pin trong POC, nút xóa =====

    listEl.addEventListener("click", async function (e) {
        const del = e.target.closest(".poc-comment-del");
        if (del) {
            if (!confirm("Xóa ghi chú này?")) return;

            const fd = new FormData();
            fd.append("id", del.dataset.id);
            if (antiForgery) fd.append("__RequestVerificationToken", antiForgery.value);

            try {
                const response = await fetch(deleteUrl, { method: "POST", body: fd });
                if (!response.ok) throw new Error();
            } catch {
                alert("Không xóa được ghi chú.");
                return;
            }

            comments = comments.filter(c => c.id !== del.dataset.id);
            renderList();
            pushCommentsToFrame();
            return;
        }

        const item = e.target.closest(".poc-comment-item");
        if (item) postToFrame({ type: "poc-focus", id: item.dataset.id });
    });

    // ===== Tin nhắn từ annotator trong iframe =====

    window.addEventListener("message", function (e) {
        if (e.source !== frame.contentWindow || !e.data || typeof e.data !== "object") return;

        // Mọi message từ annotator đều chứng tỏ nó đã sẵn sàng (phòng khi poc-ready bị lỡ do race lúc load).
        frameReady = true;

        if (e.data.type === "poc-ready") {
            pushCommentsToFrame();
            postToFrame({ type: "poc-mode", enabled: pinMode });
        } else if (e.data.type === "poc-pick") {
            openForm(e.data);
            setPinMode(false); // đã chọn xong phần tử — tắt để không click nhầm khi đang gõ
        } else if (e.data.type === "poc-exit-mode") {
            setPinMode(false);
        } else if (e.data.type === "poc-pin-click") {
            const item = listEl.querySelector(`.poc-comment-item[data-id="${e.data.id}"]`);
            if (item) {
                item.scrollIntoView({ block: "nearest", behavior: "smooth" });
                item.classList.add("highlight");
                setTimeout(() => item.classList.remove("highlight"), 1600);
            }
        }
    });

    pinModeBtn.addEventListener("click", () => setPinMode(!pinMode));

    loadComments();
})();
