// poc-review.js — trang cha của POC Review: giữ danh sách ghi chú + form ghim, nói chuyện với
// annotator trong iframe POC (poc-annotator.js) qua postMessage. Mọi thao tác GHI (thêm/thu hồi ghi chú)
// đều đi từ trang cha (same-origin, có cookie + antiforgery); iframe sandbox không gọi được gì.
// Ghi chú KHÔNG bị xoá: nút 🗑 thu hồi (dòng rời danh sách này nhưng ở lại bảng lịch sử phía dưới).
(function () {
    "use strict";

    const root = document.getElementById("pocReviewRoot");
    const frame = document.getElementById("pocFrame");
    if (!root || !frame) return;

    const commentsUrl = root.dataset.commentsUrl;
    const addUrl = root.dataset.addUrl;
    const withdrawUrl = root.dataset.withdrawUrl;
    const reopenUrl = root.dataset.reopenUrl;
    const projectId = root.dataset.projectId;
    // Bản Brief mà POC đang phục vụ được dựng từ đó — ghi chú của bản này không cần nhãn, các bản
    // trước thì có.
    const currentBriefVersion = root.dataset.briefVersion || "";

    // Nút "Bật chế độ ghim" nay là nút của command bar (icon <i> + <span class="cbar-label">), không
    // còn là nút chữ trơn: ghi đè textContent của CẢ nút sẽ xoá luôn thẻ icon, nên chỉ đổi chữ trong
    // nhãn và đổi class icon. Trạng thái bật đọc được ở ba chỗ cho ba kiểu người dùng: chữ trên nhãn,
    // class .open (màu), và aria-pressed (screen reader).
    const pinModeBtn = document.getElementById("pinModeBtn");
    const pinModeLabel = pinModeBtn.querySelector(".cbar-label") || pinModeBtn;
    const pinModeIcon = pinModeBtn.querySelector("i");
    const listEl = document.getElementById("pocCommentList");
    const countEl = document.getElementById("pocCommentCount");
    const formEl = document.getElementById("pocCommentForm");
    const targetLabelEl = document.getElementById("pocTargetLabel");
    const textEl = document.getElementById("pocCommentText");
    const cancelBtn = document.getElementById("pocCommentCancel");
    const antiForgery = formEl.querySelector('input[name="__RequestVerificationToken"]');
    const uatList = document.getElementById("uatList");
    // Ghi chú nay nằm ở HAI chỗ, và từ lúc danh sách chung dọn sang cột trái (dưới khung demo) còn ở
    // HAI CỘT KHÁC NHAU: danh sách chung ở .poc-notes-panel, ghi chú của từng kịch bản trong thẻ kịch
    // bản ở cột phải. Tổ tiên chung của cả hai vì thế là gốc trang — delegate từ một cột là mất trắng
    // handler của cột kia (xóa/mở lại/click-nháy-pin im lặng không làm gì).
    const commentsPanel = root;

    // Nhãn máy sinh khi báo lỗi từ một thẻ kịch bản — cũng là thứ DUY NHẤT buộc ghi chú về lại đúng thẻ
    // đó ở lần tải trang sau (elementPath rỗng vì không click phần tử nào trong POC). Đổi chuỗi này là
    // làm mồ côi mọi ghi chú kịch bản đã lưu trong DB, chúng sẽ rơi xuống danh sách chung.
    const SCENARIO_LABEL_PREFIX = "Kịch bản: ";

    let comments = [];
    let pinMode = false;
    let pendingPick = null; // mô tả phần tử vừa click trong POC, chờ người dùng gõ ghi chú
    let frameReady = false;
    let pendingTour = null; // <li> của bước vừa bấm "chỉ chỗ", chờ annotator trả lời tìm được hay không

    // escapeHtml dùng chung ở site.js (nạp qua _Layout trước file này).

    function postToFrame(msg) {
        if (frame.contentWindow) frame.contentWindow.postMessage(msg, "*");
    }

    function clearTourHint() {
        if (uatList) uatList.querySelectorAll(".uat-step-hint").forEach(el => el.remove());
    }

    // Chỉ chỗ TRƯỢT thì nói ra, đừng im lặng: im lặng để người dùng ngồi soi bản demo tìm một hiệu ứng
    // không bao giờ tới. Mỗi trạng thái là một việc khác nhau họ cần làm tiếp. Ba hàm này ở TOP-LEVEL
    // (không nằm trong khối `if (uatList)` bên dưới) vì listener message gọi tới chúng — khai báo hàm
    // trong khối là block-scoped ở chế độ strict.
    const TOUR_HINT = {
        missing: "Bản demo chưa đánh dấu chỗ cho bước này — bạn tự tìm trên màn hình giúp nhé.",
        unsupported: "Bản demo này dựng trước khi có chỉ dẫn từng bước, nên chưa chỉ chỗ được.",
        hidden: "Chỗ cần thao tác chỉ hiện sau khi bạn làm xong các bước trước."
    };

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
        pinModeBtn.classList.toggle("open", pinMode);
        pinModeBtn.setAttribute("aria-pressed", pinMode ? "true" : "false");
        // Nhãn phải đổi ở CẢ hai chỗ: .cbar-label là chữ nhìn thấy, aria-label là thứ screen reader
        // đọc — và trên màn hẹp command bar ẩn .cbar-label đi, lúc đó aria-label là nhãn DUY NHẤT.
        const pinLabel = pinMode ? "Đang ghim — bấm để tắt" : "Bật chế độ ghim";
        pinModeLabel.textContent = pinLabel;
        pinModeBtn.setAttribute("aria-label", pinLabel);
        pinModeBtn.title = pinMode
            ? "Click vào phần tử trong POC để ghi chú (Esc để thoát)"
            : "Bật rồi bấm vào chỗ chưa đúng trong bản demo để ghim ghi chú";
        if (pinModeIcon) {
            pinModeIcon.classList.toggle("bi-pin-angle-fill", pinMode);
            pinModeIcon.classList.toggle("bi-pin-angle", !pinMode);
        }
    }

    // Ghi chú của bản Brief NÀO. Danh sách cố ý giữ cả ghi chú của các bản trước (mất chúng đúng là thứ
    // người dùng phàn nàn), nên phải có nhãn để vòng review thứ hai trở đi không nhầm thế hệ.
    function versionBadge(briefVersion) {
        if (!briefVersion || briefVersion === currentBriefVersion) return "";
        return `<span class="poc-badge version" title="Ghi chú của bản Product Brief ${escapeHtml(briefVersion)}">${escapeHtml(briefVersion)}</span>`;
    }

    function statusBadge(status) {
        if (status === "Sent") return '<span class="poc-badge sent">đã gửi Dev</span>';
        // "Đã xử lý": vòng chỉnh sửa mang ghi chú này đã chạy xong. Người review cần phân biệt được nó
        // với "đang chờ Dev" — nếu không, vòng review thứ hai nhìn danh sách y hệt vòng đầu.
        if (status === "Addressed") return '<span class="poc-badge done">đã sửa — mời kiểm lại</span>';
        if (status === "RoutedToRequirement") return '<span class="poc-badge routed">đã gửi về Requirement</span>';
        return '<span class="poc-badge open">chờ gửi</span>';
    }

    // Thẻ kịch bản mà một ghi chú thuộc về, hoặc null nếu nó là ghi chú ghim trên POC (đường thường).
    // Khớp bằng TIÊU ĐỀ kịch bản: đó là thứ duy nhất ghi chú mang theo qua DB. Hai kịch bản trùng tiêu đề
    // ở hai màn hình khác nhau thì lấy thêm màn hình ra phân giải; kịch bản đã biến mất khỏi vòng POC mới
    // ⇒ không khớp thẻ nào và ghi chú rơi về danh sách chung (fail-open, không nuốt mất ghi chú).
    function scenarioNotesHost(comment) {
        if (!uatList) return null;

        const label = comment.elementLabel || "";
        if (!label.startsWith(SCENARIO_LABEL_PREFIX)) return null;

        const title = label.slice(SCENARIO_LABEL_PREFIX.length);
        const matches = Array.from(uatList.querySelectorAll(".uat-scenario"))
            .filter(el => el.dataset.title === title);
        if (!matches.length) return null;

        const sameScreen = matches.find(el => (el.dataset.screen || "") === (comment.pageView || ""));
        return (sameScreen || matches[0]).querySelector(".uat-scenario-notes");
    }

    function itemHtml(c) {
        return `
            <div class="poc-comment-item" data-id="${c.id}">
                <div class="poc-comment-head">
                    <span class="poc-pin-no${c.status === "Sent" ? " sent" : ""}">${c.index}</span>
                    <span class="poc-comment-target" title="${escapeHtml(c.elementPath || "")}">${escapeHtml(c.elementLabel || "Vị trí trên trang")}</span>
                    ${versionBadge(c.briefVersion)}
                    ${statusBadge(c.status)}
                    ${c.canDelete && c.status === "Open" ? `<button type="button" class="poc-comment-del" data-id="${c.id}" title="Thu hồi ghi chú (vẫn giữ trong lịch sử)">🗑</button>` : ""}
                </div>
                ${c.pageView ? `<div class="poc-comment-view">Màn hình: ${escapeHtml(c.pageView)}</div>` : ""}
                <div class="poc-comment-text">${escapeHtml(c.comment)}</div>
                <div class="poc-comment-meta">${escapeHtml(c.createdBy || "?")} · ${new Date(c.createdAt).toLocaleString()}</div>
                ${c.status === "Addressed" ? `
                    <div class="poc-comment-addressed">
                        <div class="poc-comment-addressed-head">Dev đã sửa lúc ${new Date(c.addressedAt).toLocaleString()}</div>
                        ${c.addressedNote ? `<div class="poc-comment-addressed-note">${escapeHtml(c.addressedNote)}</div>` : ""}
                        <button type="button" class="poc-comment-reopen" data-id="${c.id}"
                                title="Mở lại ghi chú này để nó vào vòng chỉnh sửa tiếp theo">✗ vẫn chưa đạt</button>
                    </div>` : ""}
            </div>
        `;
    }

    // Số ghi chú đang nằm TRONG một thẻ kịch bản, in lên dòng tiêu đề của thẻ. Thẻ gập lại là giấu cả
    // ghi chú bên trong, nên nếu không có badge này thì người review không có cách nào biết mình từng
    // báo lỗi kịch bản đó — trừ khi mở lần lượt từng thẻ ra dò.
    function refreshScenarioNoteBadges() {
        if (!uatList) return;
        uatList.querySelectorAll(".uat-scenario").forEach(function (card) {
            const badge = card.querySelector(".uat-note-count");
            if (!badge) return;
            const n = card.querySelectorAll(".uat-scenario-notes > .poc-comment-item").length;
            badge.textContent = n ? `${n} ghi chú` : "";
            badge.hidden = n === 0;
        });
    }

    function renderList() {
        const items = numbered();
        const open = items.filter(c => c.status === "Open").length;
        countEl.textContent = items.length ? `(${open} chờ gửi / ${items.length})` : "";

        // Chỉ dọn ô ghi chú của các thẻ kịch bản — KHÔNG dọn .uat-scenario-form, vì form đang mở có thể
        // nằm trong đó (xóa/mở lại một ghi chú khác cũng gọi renderList) và xóa nó đi là mất luôn nội
        // dung người dùng đang gõ cùng listener submit.
        if (uatList) {
            uatList.querySelectorAll(".uat-scenario-notes").forEach(el => { el.innerHTML = ""; });
        }

        // Ghi chú của một kịch bản về đúng thẻ kịch bản đó; phần còn lại (ghim trên POC) ở danh sách chung.
        const loose = [];
        const byHost = new Map();
        items.forEach(c => {
            const host = scenarioNotesHost(c);
            if (!host) { loose.push(c); return; }
            if (!byHost.has(host)) byHost.set(host, []);
            byHost.get(host).push(c);
        });
        byHost.forEach((list, host) => { host.innerHTML = list.map(itemHtml).join(""); });
        refreshScenarioNoteBadges();

        if (!loose.length) {
            listEl.innerHTML = items.length
                ? '<p class="muted">Mọi ghi chú đang nằm ngay dưới kịch bản của nó, ở cột kịch bản kiểm thử bên phải.</p>'
                : '<p class="muted">Chưa có ghi chú nào. Bật chế độ ghim và click vào phần tử trong POC.</p>';
            return;
        }

        listEl.innerHTML = loose.map(itemHtml).join("");
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

    // MỘT form duy nhất cho cả hai đường ghi chú (ghim trên POC / báo lỗi một kịch bản) — nó được CHUYỂN
    // CHỖ chứ không nhân bản: hai form đồng thời nghĩa là hai pendingPick, hai listener submit và một
    // antiforgery token bị chia đôi. Chỗ đứng mặc định (cuối cột, trên danh sách ghi chú chung) được giữ
    // bằng một comment node để lúc đóng form còn biết trả nó về đâu.
    const formSlot = document.createComment("poc-comment-form");
    formEl.parentNode.insertBefore(formSlot, formEl);

    function moveFormTo(host) {
        if (host) host.appendChild(formEl);
        else formSlot.parentNode.insertBefore(formEl, formSlot);
    }

    function openForm(pick, prefill, host) {
        pendingPick = pick;
        targetLabelEl.textContent = (pick.pageView ? `[${pick.pageView}] ` : "") + (pick.elementLabel || "Vị trí trên trang");
        moveFormTo(host || null);
        formEl.hidden = false;
        textEl.value = prefill || "";
        textEl.focus();
        // Prefill: đặt con trỏ ở cuối để người dùng gõ tiếp phần mô tả.
        textEl.setSelectionRange(textEl.value.length, textEl.value.length);
    }

    function closeForm() {
        pendingPick = null;
        formEl.hidden = true;
        moveFormTo(null);
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

    commentsPanel.addEventListener("click", async function (e) {
        const del = e.target.closest(".poc-comment-del");
        if (del) {
            if (!confirm("Thu hồi ghi chú này? Nó rời danh sách nhưng vẫn còn trong bảng lịch sử bên dưới.")) return;

            const fd = new FormData();
            fd.append("id", del.dataset.id);
            if (antiForgery) fd.append("__RequestVerificationToken", antiForgery.value);

            try {
                const response = await fetch(withdrawUrl, { method: "POST", body: fd });
                // 400 = ghi chú đã gửi đi xử lý (không thu hồi được nữa) — nói đúng lý do thay vì
                // "không thu hồi được", vì người dùng sẽ bấm lại mãi.
                if (!response.ok) throw new Error(response.status === 400 ? await response.text() : "");
            } catch (err) {
                alert(err && err.message ? err.message : "Không thu hồi được ghi chú.");
                return;
            }

            comments = comments.filter(c => c.id !== del.dataset.id);
            renderList();
            pushCommentsToFrame();
            return;
        }

        // "Vẫn chưa đạt": đưa ghi chú về trạng thái chờ gửi để nó vào yêu cầu chỉnh sửa TIẾP THEO, thay
        // vì phải ghim một ghi chú mới trùng nội dung.
        const reopen = e.target.closest(".poc-comment-reopen");
        if (reopen) {
            const fd = new FormData();
            fd.append("projectId", projectId);
            fd.append("id", reopen.dataset.id);
            if (antiForgery) fd.append("__RequestVerificationToken", antiForgery.value);

            try {
                const response = await fetch(reopenUrl, { method: "POST", body: fd });
                if (!response.ok) throw new Error();
            } catch {
                alert("Không mở lại được ghi chú.");
                return;
            }

            const target = comments.find(c => c.id === reopen.dataset.id);
            if (target) {
                target.status = "Open";
                target.addressedAt = null;
                target.addressedNote = null;
            }
            renderList();
            pushCommentsToFrame();
            return;
        }

        const item = e.target.closest(".poc-comment-item");
        if (item) {
            // Danh sách ghi chú nằm DƯỚI khung demo: đọc tới ghi chú thứ mười là khung demo đã trôi lên
            // khỏi màn hình, và cú nháy pin bên trong nó thành ra vô hình. Chỉ cuộn khi khung thật sự
            // đã trôi qua mép trên — còn nhìn thấy thì đừng giật trang dưới tay người dùng.
            if (frame.getBoundingClientRect().top < 0) {
                frame.scrollIntoView({ block: "start", behavior: "smooth" });
            }
            postToFrame({ type: "poc-focus", id: item.dataset.id });
        }
    });

    // "Bản demo đã đạt — tôi nghiệm thu": đường ĐÓNG hành trình phía người yêu cầu, đối trọng với nút
    // "còn sai chỗ này" bên dưới. Chỉ ghi nhận + báo người có quyền duyệt (không tự đẩy pipeline), nên sau
    // khi thành công chỉ cần thay khối nút bằng dòng xác nhận tại chỗ.
    const acceptBtn = document.getElementById("pocAcceptBtn");
    if (acceptBtn) {
        acceptBtn.addEventListener("click", async function () {
            if (!confirm("Xác nhận bản demo này đã đạt yêu cầu? Đội delivery sẽ được báo để đi tiếp các bước sau.")) return;

            const wrap = acceptBtn.closest(".poc-accept");
            const hint = wrap ? wrap.querySelector(".poc-panel-hint") : null;
            acceptBtn.disabled = true;
            const original = acceptBtn.textContent;
            acceptBtn.textContent = "Đang ghi nhận…";

            const fd = new FormData();
            fd.append("projectId", projectId);
            if (antiForgery) fd.append("__RequestVerificationToken", antiForgery.value);

            try {
                const response = await fetch(acceptBtn.dataset.acceptUrl, { method: "POST", body: fd });
                const data = await response.json().catch(() => null);
                if (data && data.ok) {
                    if (wrap) wrap.innerHTML = '<div class="poc-accepted">✓ <b>Đã nghiệm thu</b> — đội delivery đã được báo.</div>';
                    return;
                }
                if (hint) hint.textContent = (data && data.message) || "Không ghi nhận được — thử lại sau.";
            } catch {
                if (hint) hint.textContent = "Không ghi nhận được — thử lại sau.";
            }
            acceptBtn.disabled = false;
            acceptBtn.textContent = original;
        });
    }

    // ===== Gửi ghi chú đi xử lý: MỘT nút, hai đường =====
    //
    // Trang này từng bày HAI nút ("nhờ Dev chỉnh bản demo" / "gửi về Requirement") và bắt người xem demo
    // tự phân loại ghi chú của mình — trong khi chính hệ thống làm được phép phân loại ấy, và cả hai nút
    // đều nuốt trọn mọi ghi chú Open nên một buổi review lẫn hai loại thì không nút nào đúng.
    //
    // Nay: bấm gửi → server phân loại từng ghi chú (TriagePocFeedback, KHÔNG đổi trạng thái gì) → hộp xác
    // nhận cho soát và đổi nhóm → gửi một lượt (DispatchPocFeedback), mỗi đường nhận đúng tập con của nó.
    const sendBtn = document.getElementById("pocSendFeedbackBtn");
    const triageUrl = root.dataset.triageUrl;
    const dispatchUrl = root.dataset.dispatchUrl;
    const gateOpen = root.dataset.gateOpen === "true";

    if (sendBtn && triageUrl && dispatchUrl) {
        const modal = document.getElementById("pocDispatchModal");
        const groupsEl = document.getElementById("pocDispatchGroups");
        const noteEl = document.getElementById("pocDispatchNote");
        const msgEl = document.getElementById("pocDispatchMsg");
        const confirmBtn = document.getElementById("pocDispatchConfirm");
        const sendHint = document.getElementById("pocSendFeedbackHint");

        let triaged = [];          // [{ id, pageView, elementLabel, comment, requirement, reason }]
        let baConfigured = true;

        function closeModal() {
            modal.classList.add("hidden");
        }

        function itemHtml(item, i) {
            const where = item.pageView ? `[${escapeHtml(item.pageView)}] ` : "";
            const what = item.elementLabel ? `<b>${escapeHtml(item.elementLabel)}</b> — ` : "";
            // Đích của nút chuyển nhóm là nhóm ĐỐI DIỆN nhóm hiện tại của ghi chú.
            const moveLabel = item.requirement ? "→ chỉ là lỗi trình bày" : "→ đây là hiểu sai yêu cầu";
            const moveDisabled = !item.requirement && !baConfigured;

            return `
                <li class="poc-dispatch-item">
                    <div class="poc-dispatch-item-text">${where}${what}${escapeHtml(item.comment)}</div>
                    ${item.reason ? `<div class="poc-dispatch-reason">${escapeHtml(item.reason)}</div>` : ""}
                    <button type="button" class="poc-dispatch-move" data-i="${i}"${moveDisabled ? " disabled" : ""}>${moveLabel}</button>
                </li>`;
        }

        function renderGroups() {
            const fix = [];
            const req = [];
            triaged.forEach((item, i) => (item.requirement ? req : fix).push(i));

            const warnings = [];
            // Đường tài liệu ĐÈ đường chỉnh demo trong cùng một lượt (xem DispatchPocFeedbackUseCase):
            // POC sắp dựng lại từ tài liệu đã sửa nên vá HTML bây giờ là phí một vòng trong trần. Ghi chú
            // nhóm kia được giữ nguyên "chờ gửi" — phải nói rõ, không thì người dùng tưởng chúng đã đi.
            if (req.length && fix.length) {
                warnings.push(`Lượt này gửi <b>${req.length}</b> điểm về BA sửa tài liệu. <b>${fix.length}</b> ghi chú chỉnh trình bày được <b>giữ lại</b> ở trạng thái chờ gửi: bản demo sẽ dựng lại từ tài liệu mới nên chưa cần tốn một vòng chỉnh sửa — anh/chị xem lại chúng ở vòng review tới.`);
            } else if (fix.length && !gateOpen) {
                warnings.push("Quy trình đã đi qua bước bản demo nên vòng chỉnh sửa demo không còn mở — các ghi chú ở nhóm này chưa gửi đi được. Ghi chú nào thật ra là hiểu sai yêu cầu thì chuyển sang nhóm dưới.");
            }

            groupsEl.innerHTML = `
                <div class="poc-dispatch-group">
                    <h4>🛠 Nhờ đội Dev chỉnh bản demo <span class="poc-count">(${fix.length})</span></h4>
                    <p class="muted">Lỗi trình bày: sai nhãn, thiếu nút, bảng trống, canh lệch. Developer vá thẳng bản demo, tài liệu không đụng tới.</p>
                    ${fix.length ? `<ul>${fix.map(i => itemHtml(triaged[i], i)).join("")}</ul>` : '<p class="muted poc-dispatch-empty">— không có —</p>'}
                </div>
                <div class="poc-dispatch-group">
                    <h4>↩ Gửi về Requirement để sửa tài liệu <span class="poc-count">(${req.length})</span></h4>
                    <p class="muted">Tài liệu yêu cầu thiếu/hiểu sai. BA soạn lại bản mô tả, sau đó anh/chị duyệt lại để dựng bản demo mới.</p>
                    ${req.length ? `<ul>${req.map(i => itemHtml(triaged[i], i)).join("")}</ul>` : '<p class="muted poc-dispatch-empty">— không có —</p>'}
                </div>
                ${warnings.map(w => `<p class="poc-dispatch-warn">${w}</p>`).join("")}`;

            // Không còn đường nào chạy được cho lựa chọn hiện tại ⇒ khóa nút gửi thay vì để server từ chối.
            confirmBtn.disabled = req.length === 0 && (fix.length === 0 || !gateOpen);
            confirmBtn.textContent = req.length
                ? `Gửi ${req.length} điểm về Requirement`
                : (fix.length ? `Gửi ${fix.length} ghi chú cho Dev` : "Gửi");
        }

        groupsEl.addEventListener("click", function (e) {
            const move = e.target.closest(".poc-dispatch-move");
            if (!move) return;
            const item = triaged[Number(move.dataset.i)];
            if (!item) return;
            if (!item.requirement && !baConfigured) return;
            item.requirement = !item.requirement;
            msgEl.textContent = "";
            renderGroups();
        });

        sendBtn.addEventListener("click", async function () {
            sendBtn.disabled = true;
            const original = sendBtn.textContent;
            sendBtn.textContent = "Đang phân loại ghi chú…";

            const fd = new FormData();
            fd.append("projectId", projectId);
            if (antiForgery) fd.append("__RequestVerificationToken", antiForgery.value);

            let data = null;
            try {
                const response = await fetch(triageUrl, { method: "POST", body: fd });
                data = await response.json().catch(() => null);
            } catch {
                data = null;
            }

            sendBtn.disabled = false;
            sendBtn.textContent = original;

            if (!data || !data.ok) {
                if (sendHint) sendHint.textContent = (data && data.message) || "Không phân loại được ghi chú — thử lại sau.";
                return;
            }

            triaged = data.items.map(i => Object.assign({}, i));
            baConfigured = data.baConfigured !== false;

            // Máy không phân loại được ⇒ mọi ghi chú rơi về nhóm rẻ; nói rõ đó là mặc định an toàn chứ
            // không phải kết luận, để người dùng biết mình phải tự soát.
            const notes = [];
            if (!data.classified) notes.push("Hệ thống chưa phân loại được lượt này, tạm xếp tất cả vào nhóm chỉnh bản demo — anh/chị soát giúp.");
            if (!baConfigured) notes.push("Chưa cấu hình agent BA nên đường sửa tài liệu đang khóa.");
            if (!notes.length) notes.push("Hệ thống đề xuất như dưới đây — bấm nút bên phải mỗi ghi chú để đổi nhóm.");
            noteEl.textContent = notes.join(" ");

            msgEl.textContent = "";
            renderGroups();
            modal.classList.remove("hidden");
        });

        confirmBtn.addEventListener("click", async function () {
            const fd = new FormData();
            fd.append("projectId", projectId);
            if (antiForgery) fd.append("__RequestVerificationToken", antiForgery.value);
            triaged.forEach(item => fd.append(item.requirement ? "requirementIds" : "fixIds", item.id));

            confirmBtn.disabled = true;
            const original = confirmBtn.textContent;
            confirmBtn.textContent = "Đang gửi…";

            let data = null;
            try {
                const response = await fetch(dispatchUrl, { method: "POST", body: fd });
                data = await response.json().catch(() => null);
            } catch {
                data = null;
            }

            if (data && data.ok) {
                closeModal();
                if (sendHint) {
                    sendHint.textContent = data.message;
                    sendHint.classList.add("poc-route-ok");
                }
                await loadComments();
                return;
            }

            // Danh sách vừa đổi dưới chân (người khác gửi/xóa) ⇒ bảng phân loại đang cầm đã cũ: đóng hộp
            // và nạp lại thay vì để người dùng bấm gửi tiếp theo một bản không còn đúng.
            if (data && data.reload) {
                closeModal();
                if (sendHint) sendHint.textContent = data.message;
                await loadComments();
                return;
            }

            msgEl.textContent = (data && data.message) || "Không gửi được — thử lại sau.";
            confirmBtn.disabled = false;
            confirmBtn.textContent = original;
        });

        document.getElementById("pocDispatchCancel").addEventListener("click", closeModal);
        document.getElementById("pocDispatchClose").addEventListener("click", closeModal);
        modal.addEventListener("click", e => { if (e.target === modal) closeModal(); });
        document.addEventListener("keydown", e => {
            if (e.key === "Escape" && !modal.classList.contains("hidden")) closeModal();
        });
    }

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
        } else if (e.data.type === "poc-tour-result") {
            showTourResult(e.data.status);
        } else if (e.data.type === "poc-pin-click") {
            const item = commentsPanel.querySelector(`.poc-comment-item[data-id="${e.data.id}"]`);
            if (item) {
                item.scrollIntoView({ block: "nearest", behavior: "smooth" });
                item.classList.add("highlight");
                setTimeout(() => item.classList.remove("highlight"), 1600);
            }
        }
    });

    pinModeBtn.addEventListener("click", () => setPinMode(!pinMode));

    // ===== Checklist UAT (kịch bản đi-từng-bước) =====
    // Tick từng bước được lưu localStorage theo project để rời trang quay lại vẫn còn; "Báo lỗi" mở
    // form ghi chú với ngữ cảnh kịch bản prefill sẵn — ghi chú đi chung pipeline với pin thường.
    if (uatList) {
        const storageKey = `poc-uat-${projectId}`;

        let checked = {};
        try { checked = JSON.parse(localStorage.getItem(storageKey) || "{}"); } catch { checked = {}; }

        uatList.querySelectorAll(".uat-step").forEach(function (box) {
            box.checked = checked[box.dataset.key] === true;
        });

        // ===== Gập/mở từng kịch bản =====
        // Đơn vị công việc của người review là MỘT KỊCH BẢN: mở ra, đi hết các bước, đóng lại. Mở hết
        // tám kịch bản cùng lúc là bắt họ cuộn qua những cái đã xong để tới cái đang làm dở — nên mặc
        // định chỉ mở kịch bản CHƯA XONG ĐẦU TIÊN. Đóng/mở tay được nhớ theo project để rời trang quay
        // lại không mất bố cục vừa dựng.
        const cards = Array.from(uatList.querySelectorAll(".uat-scenario"));
        const openKey = `poc-uat-open-${projectId}`;

        let openState = {};
        try { openState = JSON.parse(localStorage.getItem(openKey) || "{}"); } catch { openState = {}; }

        function stepsOf(card) { return Array.from(card.querySelectorAll(".uat-step")); }

        function isDone(card) {
            const boxes = stepsOf(card);
            return boxes.length > 0 && boxes.every(b => b.checked);
        }

        // Tiến độ THEO BƯỚC về đúng chỗ của nó: trên dòng tiêu đề của chính kịch bản đó, chỗ duy nhất
        // còn nhìn thấy khi thẻ đã gập.
        function refreshCard(card) {
            const boxes = stepsOf(card);
            const done = boxes.filter(b => b.checked).length;
            const label = card.querySelector(".uat-step-count");
            const finished = isDone(card);
            card.classList.toggle("done", finished);
            if (label) {
                label.textContent = boxes.length
                    ? (finished ? `✓ ${boxes.length}/${boxes.length} bước` : `${done}/${boxes.length} bước`)
                    : "";
            }
        }

        // Tiến độ ở tiêu đề panel đếm KỊCH BẢN, không đếm bước — cùng đơn vị với các thẻ đang gập bên
        // dưới, nên "3/8" đọc thẳng ra "còn 5 thẻ nữa phải mở". Đếm từ DOM (không từ `checked`) vì
        // localStorage còn giữ khóa của những kịch bản đã biến mất ở các vòng POC trước.
        const progress = document.getElementById("uatProgress");
        function renderProgress() {
            if (!progress) return;
            progress.textContent = `(${cards.filter(isDone).length}/${cards.length})`;
        }

        const firstUndone = cards.find(card => !isDone(card));
        cards.forEach(function (card) {
            refreshCard(card);
            const stored = openState[card.dataset.index];
            card.open = typeof stored === "boolean" ? stored : card === firstUndone;
        });
        renderProgress();

        // `toggle` KHÔNG nổi bọt ⇒ phải bắt ở pha capture, không thì mọi lần đóng/mở đều không được nhớ.
        uatList.addEventListener("toggle", function (e) {
            const card = e.target.closest(".uat-scenario");
            if (!card) return;
            openState[card.dataset.index] = card.open;
            try { localStorage.setItem(openKey, JSON.stringify(openState)); } catch { }
        }, true);

        uatList.addEventListener("change", function (e) {
            const box = e.target.closest(".uat-step");
            if (!box) return;

            checked[box.dataset.key] = box.checked;
            try { localStorage.setItem(storageKey, JSON.stringify(checked)); } catch { }

            const card = box.closest(".uat-scenario");
            if (card) {
                const wasDone = card.classList.contains("done");
                refreshCard(card);
                // Tick nốt bước cuối ⇒ tự gập kịch bản lại. Đây là lúc DUY NHẤT gập hộ là đúng ý người
                // dùng (họ vừa nói xong việc này rồi) và cũng là thứ giữ cho panel NGẮN DẦN trong lúc
                // review thay vì dài mãi. Hai ngoại lệ, vì gập lúc đó là giấu mất thứ đang cần nhìn:
                // thẻ đang giữ ô nhập ghi chú, hoặc thẻ đã có ghi chú ghim bên trong.
                const busy = card.querySelector(".uat-scenario-form > *")
                    || card.querySelector(".uat-scenario-notes > *");
                if (!wasDone && isDone(card) && !busy) card.open = false;
            }
            renderProgress();
        });

        // "Chỉ chỗ": bấm chữ một bước → POC mở đúng màn hình + tô sáng phần tử của ĐÚNG bước đó, để
        // người xem biết bấm vào đâu thay vì tự mò. READ-ONLY: annotator chỉ highlight, không tự thao
        // tác — user vẫn tự bấm để kiểm chứng nghiệp vụ thật. Vì thế chỉ chỗ đi theo NHỊP CỦA NGƯỜI XEM:
        // một lần bấm = một lần chỉ chỗ. Không có lượt tự chạy hết kịch bản theo đồng hồ — người xem còn
        // phải tự thao tác nên luôn chậm hơn nó, và mỗi bước là một lần đổi màn hình + cuộn iframe.
        //
        // Cái đi xuống iframe là MÃ NEO của bước (data-anchor="2.3", do Razor in ra từ chỉ số gốc của
        // kịch bản/bước), không phải câu chữ của bước: annotator tra [data-uat~="2.3"] — thứ agent dựng
        // POC đã khai báo — thay vì đoán phần tử từ tiếng Việt và khoanh nhầm.
        function tourStep(stepEl, screen) {
            pendingTour = stepEl.closest("li") || stepEl;
            clearTourHint();
            postToFrame({ type: "poc-tour-step", screen: screen || "", anchor: stepEl.dataset.anchor || "" });
        }

        uatList.addEventListener("click", function (e) {
            const fail = e.target.closest(".uat-fail");
            if (fail) {
                const scenario = fail.closest(".uat-scenario");
                const title = scenario?.dataset.title || "";
                openForm({
                    pageView: scenario?.dataset.screen || "",
                    elementLabel: SCENARIO_LABEL_PREFIX + title,
                    elementPath: "",
                    xPercent: 0,
                    yPercent: 0
                }, `Kịch bản "${title}" chưa đạt — `, scenario?.querySelector(".uat-scenario-form"));
                return;
            }

            // Bấm chữ của một bước → chỉ chỗ ngay bước đó.
            const stepText = e.target.closest(".uat-step-text");
            if (stepText) {
                tourStep(stepText, stepText.closest(".uat-scenario")?.dataset.screen || "");
            }
        });
    }

    loadComments();
})();

// ==== Hộp thoại "Chia sẻ bản demo" ====
// Tạo/thu hồi link cho người KHÔNG có tài khoản. Mở từ nút trên command bar. Tách IIFE riêng để phần
// review cốt lõi không phụ thuộc vào hộp thoại này (chỉ dựng cho người có quyền quản lý requirement).
(function () {
    "use strict";

    const panel = document.getElementById("pocSharePanel");
    if (!panel) return;

    const modal = document.getElementById("pocShareModal");
    const openBtn = document.getElementById("pocShareOpen");
    const root = document.getElementById("pocReviewRoot");
    const listEl = document.getElementById("pocShareList");
    const msgEl = document.getElementById("pocShareMsg");
    const labelEl = document.getElementById("pocShareLabel");
    const daysEl = document.getElementById("pocShareDays");
    const createBtn = document.getElementById("pocShareCreate");
    const suggestEl = document.getElementById("pocShareSuggest");
    const pickEl = document.getElementById("pocSharePick");
    const projectId = root.dataset.projectId;
    const antiForgery = document.querySelector('#pocCommentForm input[name="__RequestVerificationToken"]');

    let links = [];

    function shareUrl(token) {
        return `${location.origin}/poc-share/${token}`;
    }

    function say(text, isError) {
        msgEl.textContent = text;
        msgEl.classList.toggle("error", !!isError);
    }

    function render() {
        if (!links.length) {
            listEl.innerHTML = '<p class="poc-share-empty">Chưa có link nào — tạo link đầu tiên ở trên.</p>';
            return;
        }

        listEl.innerHTML = links.map(l => {
            const expired = new Date(l.expiresAtUtc) <= new Date();
            const dead = !!l.revokedAtUtc || expired;
            const state = l.revokedAtUtc ? "đã thu hồi" : (expired ? "đã hết hạn" : `hết hạn ${new Date(l.expiresAtUtc).toLocaleDateString()}`);
            return `
                <div class="poc-share-item ${dead ? "dead" : ""}" data-id="${l.id}">
                    <div class="poc-share-item-head">
                        <span class="poc-share-label">${escapeHtml(l.label || "Không đặt tên")}</span>
                        <span class="poc-share-state">${escapeHtml(state)}</span>
                    </div>
                    ${dead ? "" : `
                        <div class="poc-share-url-row">
                            <input type="text" class="poc-share-url" readonly value="${escapeHtml(shareUrl(l.token))}" />
                            <button type="button" class="btn small poc-share-copy">
                                <i class="bi bi-clipboard" aria-hidden="true"></i> Copy
                            </button>
                            <button type="button" class="btn danger small poc-share-revoke" data-id="${l.id}">Thu hồi</button>
                        </div>`}
                </div>`;
        }).join("");
    }

    async function load() {
        try {
            const response = await fetch(panel.dataset.listUrl);
            links = response.ok ? await response.json() : [];
        } catch {
            links = [];
        }
        render();
    }

    // ---- Autocomplete "Gửi cho ai" (gợi ý từ danh bạ nhân sự) ----
    // Nhãn link vẫn là text tự do — khách ngoài công ty không có trong danh bạ — nên gợi ý chỉ giúp gõ
    // nhanh và ghi cùng một người theo cùng một cách, không phải ràng buộc bắt buộc chọn.
    const MIN_KEYS = 2;
    let suggestions = [];
    let activeIndex = -1;
    let searchTimer = null;
    let searchSeq = 0;

    function hideSuggest() {
        suggestEl.classList.add("hidden");
        suggestEl.innerHTML = "";
        labelEl.setAttribute("aria-expanded", "false");
        suggestions = [];
        activeIndex = -1;
    }

    function clearPick() {
        pickEl.classList.add("hidden");
        pickEl.innerHTML = "";
    }

    function highlight(index) {
        activeIndex = index;
        Array.from(suggestEl.querySelectorAll(".poc-share-suggest-item")).forEach((el, i) => {
            el.classList.toggle("active", i === index);
            el.setAttribute("aria-selected", i === index ? "true" : "false");
        });
    }

    function pick(person) {
        labelEl.value = person.displayName;
        const detail = [person.email, person.organizationUnit].filter(Boolean).join(" · ");
        pickEl.innerHTML = `<i class="bi bi-person-check" aria-hidden="true"></i> ${escapeHtml(person.displayName)}` +
            (detail ? ` <span class="poc-share-suggest-meta">${escapeHtml(detail)}</span>` : "");
        pickEl.classList.remove("hidden");
        hideSuggest();
    }

    function renderSuggest(people) {
        suggestions = people;
        activeIndex = -1;

        if (!people.length) {
            suggestEl.innerHTML = '<div class="poc-share-suggest-empty">Không có ai khớp — cứ gõ tên tự do cũng được.</div>';
        } else {
            suggestEl.innerHTML = people.map(p => {
                const meta = [p.email, p.organizationUnit, p.position].filter(Boolean).join(" · ");
                return `
                    <div class="poc-share-suggest-item" role="option" aria-selected="false">
                        <span class="poc-share-suggest-name">${escapeHtml(p.displayName)}</span>
                        ${meta ? `<span class="poc-share-suggest-meta">${escapeHtml(meta)}</span>` : ""}
                    </div>`;
            }).join("");

            Array.from(suggestEl.children).forEach((el, i) => {
                // mousedown chứ không phải click: blur của ô nhập đóng danh sách trước khi click kịp bắn.
                el.addEventListener("mousedown", ev => { ev.preventDefault(); pick(people[i]); });
                el.addEventListener("mousemove", () => highlight(i));
            });
        }

        suggestEl.classList.remove("hidden");
        labelEl.setAttribute("aria-expanded", "true");
    }

    async function search(key) {
        // Trả lời chậm của lượt gõ cũ không được đè lên kết quả của lượt mới.
        const seq = ++searchSeq;
        try {
            const url = new URL(panel.dataset.associateUrl, location.origin);
            url.searchParams.set("q", key);
            const response = await fetch(url);
            if (!response.ok) throw new Error();
            const people = await response.json();
            if (seq === searchSeq) renderSuggest(people);
        } catch {
            // Tra cứu danh bạ hỏng thì im lặng bỏ qua: ô này vẫn gõ tay được, không chặn việc tạo link.
            if (seq === searchSeq) hideSuggest();
        }
    }

    labelEl.addEventListener("input", function () {
        clearPick();
        say(""); // báo của lượt tạo link trước không còn đúng với thứ đang gõ.
        const key = labelEl.value.trim();
        if (searchTimer) clearTimeout(searchTimer);
        if (key.length < MIN_KEYS) { hideSuggest(); return; }
        searchTimer = setTimeout(() => search(key), 250);
    });

    labelEl.addEventListener("keydown", function (e) {
        if (suggestEl.classList.contains("hidden")) return;

        if (e.key === "ArrowDown" || e.key === "ArrowUp") {
            if (!suggestions.length) return;
            e.preventDefault();
            const step = e.key === "ArrowDown" ? 1 : -1;
            highlight((activeIndex + step + suggestions.length) % suggestions.length);
        } else if (e.key === "Enter" && activeIndex >= 0) {
            e.preventDefault();
            pick(suggestions[activeIndex]);
        } else if (e.key === "Escape") {
            // Escape đóng danh sách trước, chỉ lượt bấm sau mới đóng cả hộp thoại.
            e.stopPropagation();
            hideSuggest();
        }
    });

    labelEl.addEventListener("blur", () => setTimeout(hideSuggest, 150));

    createBtn.addEventListener("click", async function () {
        createBtn.disabled = true;
        say("");
        hideSuggest();
        try {
            const fd = new FormData();
            fd.append("projectId", projectId);
            fd.append("label", labelEl.value.trim());
            fd.append("days", daysEl.value);
            if (antiForgery) fd.append("__RequestVerificationToken", antiForgery.value);

            const response = await fetch(panel.dataset.createUrl, { method: "POST", body: fd });
            if (!response.ok) {
                say(await response.text() || "Không tạo được link.", true);
            } else {
                labelEl.value = "";
                clearPick();
                await load();
                say("Đã tạo link — bấm Copy rồi gửi cho người nhận.");
            }
        } catch {
            say("Không tạo được link — kiểm tra kết nối rồi thử lại.", true);
        }
        createBtn.disabled = false;
    });

    listEl.addEventListener("click", async function (e) {
        const copy = e.target.closest(".poc-share-copy");
        if (copy) {
            const input = copy.closest(".poc-share-url-row").querySelector(".poc-share-url");
            input.select();
            try {
                await navigator.clipboard.writeText(input.value);
                say("Đã copy link.");
            } catch {
                // Trình duyệt chặn clipboard API (http, quyền) — link đã được bôi đen sẵn để Ctrl+C.
                say("Không copy tự động được — link đã được bôi đen, bấm Ctrl+C.", true);
            }
            return;
        }

        const revoke = e.target.closest(".poc-share-revoke");
        if (!revoke) return;
        if (!confirm("Thu hồi link này? Người đang giữ link sẽ không mở được nữa.")) return;

        try {
            const fd = new FormData();
            fd.append("projectId", projectId);
            fd.append("id", revoke.dataset.id);
            if (antiForgery) fd.append("__RequestVerificationToken", antiForgery.value);
            const response = await fetch(panel.dataset.revokeUrl, { method: "POST", body: fd });
            if (!response.ok) throw new Error();
            await load();
            say("Đã thu hồi link.");
        } catch {
            say("Không thu hồi được link.", true);
        }
    });

    // Danh sách nạp khi MỞ hộp thoại (không nạp lúc tải trang): người khác có thể vừa tạo/thu hồi link,
    // và trang review vốn mở rất lâu nên bản nạp lúc đầu phiên gần như chắc chắn đã cũ.
    function openShare() {
        say("");
        clearPick();
        hideSuggest();
        modal.classList.remove("hidden");
        labelEl.focus();
        load();
    }

    function closeShare() {
        hideSuggest();
        modal.classList.add("hidden");
    }

    openBtn.addEventListener("click", openShare);
    document.getElementById("pocShareClose").addEventListener("click", closeShare);
    document.getElementById("pocShareDone").addEventListener("click", closeShare);
    modal.addEventListener("click", e => { if (e.target === modal) closeShare(); });
    document.addEventListener("keydown", e => {
        if (e.key === "Escape" && !modal.classList.contains("hidden")) closeShare();
    });
})();

// ==== Hộp thoại "Chi tiết kỹ thuật" (2 tab: vòng tự kiểm / đối chiếu spec) ====
// Nội dung đã được server dựng sẵn trong DOM — ở đây chỉ có đóng/mở và đổi tab, không gọi mạng.
// IIFE riêng, thoát sớm khi không có markup: hộp thoại chỉ dựng cho người có quyền DeliveryAdvance,
// còn phần review cốt lõi phải chạy bình thường với mọi người xem.
(function () {
    "use strict";

    const modal = document.getElementById("pocTechModal");
    const openBtn = document.getElementById("pocTechOpen");
    if (!modal || !openBtn) return;

    const tabs = Array.from(modal.querySelectorAll(".poc-tech-tab"));

    function selectTab(tab) {
        if (!tab || tab.disabled) return;
        tabs.forEach(t => {
            const on = t === tab;
            t.classList.toggle("active", on);
            t.setAttribute("aria-selected", on ? "true" : "false");
            document.getElementById(t.dataset.panel).classList.toggle("hidden", !on);
        });
        tab.focus();
    }

    tabs.forEach(tab => tab.addEventListener("click", () => selectTab(tab)));

    // Mũi tên trái/phải giữa các tab (mẫu tablist chuẩn), bỏ qua tab bị vô hiệu.
    modal.querySelector(".poc-tech-tabs").addEventListener("keydown", e => {
        if (e.key !== "ArrowRight" && e.key !== "ArrowLeft") return;
        const enabled = tabs.filter(t => !t.disabled);
        if (enabled.length < 2) return;
        const at = enabled.indexOf(document.activeElement);
        if (at < 0) return;
        e.preventDefault();
        const step = e.key === "ArrowRight" ? 1 : -1;
        selectTab(enabled[(at + step + enabled.length) % enabled.length]);
    });

    function openTech() {
        modal.classList.remove("hidden");
        openBtn.setAttribute("aria-expanded", "true");
        const active = tabs.find(t => t.classList.contains("active") && !t.disabled) || tabs.find(t => !t.disabled);
        if (active) active.focus();
    }

    function closeTech() {
        modal.classList.add("hidden");
        openBtn.setAttribute("aria-expanded", "false");
        openBtn.focus();
    }

    openBtn.addEventListener("click", openTech);
    document.getElementById("pocTechClose").addEventListener("click", closeTech);
    document.getElementById("pocTechDone").addEventListener("click", closeTech);
    modal.addEventListener("click", e => { if (e.target === modal) closeTech(); });
    document.addEventListener("keydown", e => {
        if (e.key === "Escape" && !modal.classList.contains("hidden")) closeTech();
    });
})();
