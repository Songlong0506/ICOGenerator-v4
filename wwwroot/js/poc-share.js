// poc-share.js — trang xem bản demo dành cho NGƯỜI ĐƯỢC GỬI LINK (không có tài khoản).
// Cùng cơ chế với poc-review.js (annotator chạy trong iframe, nói chuyện với trang cha qua postMessage;
// mọi thao tác GHI đi từ trang cha) và CÙNG bảng ghi chú (poc-notes.js), nhưng rút gọn còn đúng những gì
// khách cần: xem, góp ý (Add Note hoặc ghim) vào bản nháp của riêng mình, xoá bản nháp, Send Note để gửi
// bản nháp cho đội dự án, theo dõi trạng thái góp ý đã gửi. Bảng chỉ có góp ý của CHÍNH link này (server
// lọc). Không gửi thẳng cho Dev, không điều khiển quy trình.
(function () {
    "use strict";

    const root = document.getElementById("pocShareRoot");
    const frame = document.getElementById("pocFrame");
    if (!root || !frame || !window.PocNotes) return;

    const commentsUrl = root.dataset.commentsUrl;
    const addUrl = root.dataset.addUrl;
    const sendUrl = root.dataset.sendUrl;
    // URL xoá dựng từ template Razor (id = Guid rỗng) — không chép cấu trúc route vào JS.
    const deleteUrlTemplate = root.dataset.deleteUrlTemplate || "";
    const EMPTY_GUID = "00000000-0000-0000-0000-000000000000";
    const sendBox = root.querySelector(".poc-route-req");
    const sendBtn = document.getElementById("pocGuestSendBtn");
    const sendHint = document.getElementById("pocGuestSendHint");

    const pinModeBtn = document.getElementById("pinModeBtn");
    const pinModeIcon = pinModeBtn.querySelector("i");
    // Nhãn/tooltip trạng thái TẮT đọc từ chữ server render (PocShare/Open.cshtml), không chép lại ở đây.
    const pinLabelOff = pinModeBtn.getAttribute("aria-label") || "";
    const pinTitleOff = pinModeBtn.title;
    const antiForgery = document.querySelector('#pocCommentForm input[name="__RequestVerificationToken"]');
    const nameEl = document.getElementById("pocGuestName");
    const uatList = document.getElementById("uatList");
    const notesRoot = root.querySelector(".poc-notes-table-root");
    // Add Note bị khoá từ server (bản demo đã nghiệm thu) — cùng cờ partial đã dùng để render nút.
    const locked = !!notesRoot.querySelector("[data-notes-add][disabled]");

    // Link cũ (chưa ghi người nhận) mới hỏi tên. Tên đó được nhớ trong máy họ: một buổi nghiệm thu có nhiều góp ý liên tiếp, bắt gõ lại tên
    // mỗi lần là cách nhanh nhất để họ bỏ cuộc giữa chừng.
    const NAME_KEY = "poc-share-guest-name";

    let pinMode = false;
    let frameReady = false;
    let currentView = "";
    let pendingTour = null; // <li> của bước vừa bấm "chỉ chỗ", chờ annotator trả lời tìm được hay không

    function postToFrame(msg) {
        if (frame.contentWindow) frame.contentWindow.postMessage(msg, "*");
    }

    function setPinMode(enabled) {
        if (locked) enabled = false;
        pinMode = enabled;
        postToFrame({ type: "poc-mode", enabled: pinMode });
        // Nút chỉ có icon (cùng kiểu POC Review): trạng thái nằm ở icon rỗng/đặc, nền xanh (CSS bám
        // aria-pressed), aria-label cho screen reader và title cho tooltip.
        pinModeBtn.setAttribute("aria-pressed", pinMode ? "true" : "false");
        pinModeBtn.setAttribute("aria-label", pinMode ? "Pinning — click to turn off" : pinLabelOff);
        pinModeBtn.title = pinMode
            ? "Pinning — click an element in the demo to leave a note (click again or press Esc to turn off)"
            : pinTitleOff;
        if (pinModeIcon) {
            pinModeIcon.classList.toggle("bi-pin-angle-fill", pinMode);
            pinModeIcon.classList.toggle("bi-pin-angle", !pinMode);
        }
    }

    // ===== Bảng ghi chú — CÙNG module với POC Review (poc-notes.js), chế độ khách =====
    const notes = window.PocNotes.create({
        root: notesRoot,
        guest: true,
        locked,
        currentView: () => currentView,

        async onSubmit(p) {
            const fd = new FormData();
            fd.append("guestName", p.guestName);
            fd.append("pageView", p.pageView);
            fd.append("elementLabel", p.elementLabel);
            fd.append("elementPath", p.elementPath);
            fd.append("xPercent", String(p.xPercent));
            fd.append("yPercent", String(p.yPercent));
            fd.append("comment", p.comment);
            if (antiForgery) fd.append("__RequestVerificationToken", antiForgery.value);

            let response;
            try {
                response = await fetch(addUrl, { method: "POST", body: fd });
            } catch {
                throw new Error("Could not send the note — check your connection and try again.");
            }
            if (!response.ok) throw new Error(await response.text().catch(() => "") || "Could not send the note.");

            if (nameEl) {
                try { localStorage.setItem(NAME_KEY, p.guestName); } catch { /* chế độ riêng tư: không nhớ tên, không sao */ }
            }
            return await response.json();
        },

        // Bấm một dòng có pin → nháy pin tương ứng trong bản demo.
        onFocus(item) {
            // Bảng nằm DƯỚI khung demo: cuộn xuống đọc góp ý là đẩy khung khỏi màn hình, và cú nháy pin
            // bên trong nó thành vô hình. Chỉ kéo khung về khi nó đã trôi qua mép trên (như poc-review.js).
            if (frame.getBoundingClientRect().top < 0) {
                frame.scrollIntoView({ block: "start", behavior: "smooth" });
            }
            postToFrame({ type: "poc-focus", id: item.id });
        },

        // Delete chỉ có ở bản nháp chưa gửi (server tính CanWithdraw) ⇒ luôn là xoá thật.
        async onWithdraw(item) {
            if (!window.confirm("Delete this note? It has not been sent, so the project team will never see it.")) return false;
            const fd = new FormData();
            if (antiForgery) fd.append("__RequestVerificationToken", antiForgery.value);
            try {
                const response = await fetch(deleteUrlTemplate.replace(EMPTY_GUID, item.id), { method: "POST", body: fd });
                if (response.ok) return true;
                showSendHint(await response.text().catch(() => "") || "Could not delete the note.", false);
            } catch {
                showSendHint("Could not delete the note — check your connection and try again.", false);
            }
            return false;
        },

        onChange() {
            pushCommentsToFrame();
            refreshSendButton();
        }
    });

    function draftCount() {
        return notes.items().filter(c => c.status === "GuestDraft").length;
    }

    function showSendHint(text, ok) {
        if (!sendHint) return;
        sendHint.textContent = text || "";
        sendHint.classList.toggle("poc-route-ok", !!ok);
    }

    // Nút chỉ hiện khi còn bản nháp, nhãn mang số góp ý sẽ đi — như nút gửi của POC Review.
    function refreshSendButton() {
        if (!sendBox || !sendBtn) return;
        const n = draftCount();
        sendBox.hidden = n === 0;
        sendBtn.innerHTML = `<i class="bi bi-send" aria-hidden="true"></i> Send Note (${n})`;
    }

    if (sendBtn) {
        sendBtn.addEventListener("click", async function () {
            const n = draftCount();
            if (!n) return;
            if (!window.confirm(`Send ${n} note(s) to the project team? Once sent, they can no longer be deleted.`)) return;

            const fd = new FormData();
            if (antiForgery) fd.append("__RequestVerificationToken", antiForgery.value);
            sendBtn.disabled = true;
            try {
                const response = await fetch(sendUrl, { method: "POST", body: fd });
                if (!response.ok) {
                    showSendHint(await response.text().catch(() => "") || "Could not send the note.", false);
                } else {
                    const result = await response.json();
                    showSendHint(`Sent ${result.sent} note(s) to the project team.`, true);
                }
            } catch {
                showSendHint("Could not send the note — check your connection and try again.", false);
            } finally {
                sendBtn.disabled = false;
            }
            // Nạp lại từ server: trạng thái sau khi gửi là của server (Draft → Waiting), không tự đoán ở đây.
            await loadComments();
        });
    }

    function pushCommentsToFrame() {
        if (frameReady) postToFrame({ type: "poc-comments", items: notes.pins() });
    }

    async function loadComments() {
        let list = [];
        try {
            const response = await fetch(commentsUrl);
            list = response.ok ? await response.json() : [];
        } catch {
            list = [];
        }
        notes.setItems(list);
    }

    if (locked) {
        pinModeBtn.disabled = true;
        pinModeBtn.title = "The demo has been finalized — contact the project team if you have more feedback";
    }

    pinModeBtn.addEventListener("click", () => setPinMode(!pinMode));

    // ===== "Chỉ chỗ" — cùng giao thức với poc-review.js =====
    // Bấm chữ một bước → gửi MÃ NEO của bước (data-anchor, Razor in ra từ chỉ số gốc) xuống annotator, nó
    // mở đúng màn hình rồi tô sáng [data-uat~="..."]. Không tìm được thì nói bằng chữ ngay dưới bước đó —
    // im lặng là để khách ngồi soi bản demo tìm một khung vàng không bao giờ tới. Câu chữ ba trạng thái
    // trùng với POC Review để người gửi link và người nhận link nghe cùng một lời giải thích.
    const TOUR_HINT = {
        missing: "The demo has not marked a spot for this step — please look for it on the screen yourself.",
        unsupported: "This demo was built before step-by-step guidance existed, so it cannot point to the spot.",
        hidden: "The spot for this step appears only after you finish the previous steps."
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
        if (typeof e.data.view === "string") currentView = e.data.view;

        if (e.data.type === "poc-ready") {
            postToFrame({ type: "poc-mode", enabled: pinMode });
            pushCommentsToFrame();
        } else if (e.data.type === "poc-pick") {
            notes.openAdd(e.data, currentView);
            setPinMode(false);
        } else if (e.data.type === "poc-exit-mode") {
            setPinMode(false);
        } else if (e.data.type === "poc-tour-result") {
            showTourResult(e.data.status);
        } else if (e.data.type === "poc-pin-click") {
            notes.highlight(e.data.id);
        }
    });

    // Ô tên chỉ có với link cũ chưa ghi người nhận — link mới đã mang sẵn tên ("Send to").
    if (nameEl) {
        try {
            const saved = localStorage.getItem(NAME_KEY);
            if (saved) nameEl.value = saved;
        } catch { /* không đọc được localStorage thì chỉ là phải gõ lại tên */ }
    }

    loadComments();
})();
