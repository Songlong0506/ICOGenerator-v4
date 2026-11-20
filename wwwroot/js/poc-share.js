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
    const pinModeIcon = pinModeBtn.querySelector("i");
    // Nhãn/tooltip trạng thái TẮT đọc từ chữ server render (PocShare/Open.cshtml), không chép lại ở đây.
    const pinLabelOff = pinModeBtn.getAttribute("aria-label") || "";
    const pinTitleOff = pinModeBtn.title;
    const listEl = document.getElementById("pocCommentList");
    const countEl = document.getElementById("pocCommentCount");
    const formEl = document.getElementById("pocCommentForm");
    const targetLabelEl = document.getElementById("pocTargetLabel");
    const nameEl = document.getElementById("pocGuestName");
    const textEl = document.getElementById("pocCommentText");
    const cancelBtn = document.getElementById("pocCommentCancel");
    const antiForgery = formEl.querySelector('input[name="__RequestVerificationToken"]');
    const uatList = document.getElementById("uatList");

    // Tên người góp ý được nhớ trong máy họ: một buổi nghiệm thu có nhiều góp ý liên tiếp, bắt gõ lại tên
    // mỗi lần là cách nhanh nhất để họ bỏ cuộc giữa chừng.
    const NAME_KEY = "poc-share-guest-name";

    let comments = [];
    let pinMode = false;
    let pendingPick = null;
    let frameReady = false;
    let pendingTour = null; // <li> của bước vừa bấm "chỉ chỗ", chờ annotator trả lời tìm được hay không

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
        // Nút chỉ có icon (cùng kiểu POC Review): trạng thái nằm ở icon rỗng/đặc, nền xanh (CSS bám
        // aria-pressed), aria-label cho screen reader và title cho tooltip.
        pinModeBtn.setAttribute("aria-pressed", pinMode ? "true" : "false");
        pinModeBtn.setAttribute("aria-label", pinMode ? "Đang ghim — bấm để tắt" : pinLabelOff);
        pinModeBtn.title = pinMode
            ? "Đang ghim — bấm vào phần tử trong bản demo để góp ý (bấm lại hoặc Esc để tắt)"
            : pinTitleOff;
        if (pinModeIcon) {
            pinModeIcon.classList.toggle("bi-pin-angle-fill", pinMode);
            pinModeIcon.classList.toggle("bi-pin-angle", !pinMode);
        }
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
        if (index < 0) return;
        // Danh sách nằm DƯỚI khung demo: cuộn xuống đọc góp ý là đẩy khung khỏi màn hình, và cú nháy pin
        // bên trong nó thành vô hình. Chỉ kéo khung về khi nó đã trôi qua mép trên (như poc-review.js).
        if (frame.getBoundingClientRect().top < 0) {
            frame.scrollIntoView({ block: "start", behavior: "smooth" });
        }
        postToFrame({ type: "poc-focus", id: item.dataset.id, pageView: comments[index].pageView });
    });

    pinModeBtn.addEventListener("click", () => setPinMode(!pinMode));

    // ===== "Chỉ chỗ" — cùng giao thức với poc-review.js =====
    // Bấm chữ một bước → gửi MÃ NEO của bước (data-anchor, Razor in ra từ chỉ số gốc) xuống annotator, nó
    // mở đúng màn hình rồi tô sáng [data-uat~="..."]. Không tìm được thì nói bằng chữ ngay dưới bước đó —
    // im lặng là để khách ngồi soi bản demo tìm một khung vàng không bao giờ tới. Câu chữ ba trạng thái
    // trùng với POC Review để người gửi link và người nhận link nghe cùng một lời giải thích.
    const TOUR_HINT = {
        missing: "Bản demo chưa đánh dấu chỗ cho bước này — bạn tự tìm trên màn hình giúp nhé.",
        unsupported: "Bản demo này dựng trước khi có chỉ dẫn từng bước, nên chưa chỉ chỗ được.",
        hidden: "Chỗ cần thao tác chỉ hiện sau khi bạn làm xong các bước trước."
    };

    function clearTourHint() {
        if (uatList) uatList.querySelectorAll(".uat-step-hint").forEach(el => el.remove());
    }

    function showTourResult(status) {
        const li = pendingTour;
        pendingTour = null;
        clearTourHint();
        if (!li || status === "ok") return;

        const hint = document.createElement("div");
        hint.className = "uat-step-hint";
        hint.textContent = TOUR_HINT[status] || TOUR_HINT.missing;
        li.appendChild(hint);
    }

    if (uatList) {
        uatList.addEventListener("click", function (e) {
            const stepText = e.target.closest(".uat-step-text");
            if (!stepText) return;
            const li = stepText.closest("li");
            const card = stepText.closest(".uat-scenario");

            // Nền nhạt trên bước vừa bấm, cùng màu khung tô sáng trong bản demo: nối "chỗ đang sáng bên
            // kia" với "câu đang đọc bên này".
            uatList.querySelectorAll(".uat-steps li.is-current").forEach(x => x.classList.remove("is-current"));
            if (li) li.classList.add("is-current");

            // Cột hướng dẫn có thể dài hơn màn hình: cuộn xuống đọc bước sau là đẩy khung demo khỏi tầm
            // mắt, và khung vàng bên trong thành vô hình. Chỉ kéo khung về khi nó đã trôi qua mép trên.
            if (frame.getBoundingClientRect().top < 0) {
                frame.scrollIntoView({ block: "start", behavior: "smooth" });
            }

            pendingTour = li || stepText;
            clearTourHint();
            postToFrame({ type: "poc-tour-step", screen: card?.dataset.screen || "", anchor: stepText.dataset.anchor || "" });
        });
    }

    window.addEventListener("message", function (e) {
        if (e.source !== frame.contentWindow || !e.data || typeof e.data !== "object") return;

        // Tên message phải khớp ĐÚNG những gì poc-annotator.js gửi (poc-ready, poc-exit-mode…) — lệch một
        // chữ là trang khách im lặng mất tính năng mà không có lỗi nào. Mọi message từ annotator đều chứng
        // tỏ nó đã sẵn sàng (phòng khi poc-ready bị lỡ do race lúc load), như poc-review.js.
        frameReady = true;

        if (e.data.type === "poc-ready") {
            postToFrame({ type: "poc-mode", enabled: pinMode });
            pushCommentsToFrame();
        } else if (e.data.type === "poc-pick") {
            openForm(e.data);
        } else if (e.data.type === "poc-exit-mode") {
            setPinMode(false);
        } else if (e.data.type === "poc-tour-result") {
            showTourResult(e.data.status);
        }
    });

    try {
        const saved = localStorage.getItem(NAME_KEY);
        if (saved) nameEl.value = saved;
    } catch { /* không đọc được localStorage thì chỉ là phải gõ lại tên */ }

    loadComments();
})();
