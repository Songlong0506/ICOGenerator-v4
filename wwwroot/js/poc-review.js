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
    const reopenUrl = root.dataset.reopenUrl;
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
        if (status === "Sent") return '<span class="poc-badge sent">đã gửi Dev</span>';
        // "Đã xử lý": vòng chỉnh sửa mang ghi chú này đã chạy xong. Người review cần phân biệt được nó
        // với "đang chờ Dev" — nếu không, vòng review thứ hai nhìn danh sách y hệt vòng đầu.
        if (status === "Addressed") return '<span class="poc-badge done">đã sửa — mời kiểm lại</span>';
        if (status === "RoutedToRequirement") return '<span class="poc-badge routed">đã gửi về Requirement</span>';
        return '<span class="poc-badge open">chờ gửi</span>';
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
                ${c.status === "Addressed" ? `
                    <div class="poc-comment-addressed">
                        <div class="poc-comment-addressed-head">Dev đã sửa lúc ${new Date(c.addressedAt).toLocaleString()}</div>
                        ${c.addressedNote ? `<div class="poc-comment-addressed-note">${escapeHtml(c.addressedNote)}</div>` : ""}
                        <button type="button" class="poc-comment-reopen" data-id="${c.id}"
                                title="Mở lại ghi chú này để nó vào vòng chỉnh sửa tiếp theo">✗ vẫn chưa đạt</button>
                    </div>` : ""}
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

    function openForm(pick, prefill) {
        pendingPick = pick;
        targetLabelEl.textContent = (pick.pageView ? `[${pick.pageView}] ` : "") + (pick.elementLabel || "Vị trí trên trang");
        formEl.hidden = false;
        textEl.value = prefill || "";
        textEl.focus();
        // Prefill: đặt con trỏ ở cuối để người dùng gõ tiếp phần mô tả.
        textEl.setSelectionRange(textEl.value.length, textEl.value.length);
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
        if (item) postToFrame({ type: "poc-focus", id: item.dataset.id });
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

    // ===== Checklist UAT (kịch bản đi-từng-bước) =====
    // Tick từng bước được lưu localStorage theo project để rời trang quay lại vẫn còn; "Báo lỗi" mở
    // form ghi chú với ngữ cảnh kịch bản prefill sẵn — ghi chú đi chung pipeline với pin thường.
    const uatList = document.getElementById("uatList");
    if (uatList) {
        const storageKey = `poc-uat-${projectId}`;

        let checked = {};
        try { checked = JSON.parse(localStorage.getItem(storageKey) || "{}"); } catch { checked = {}; }

        uatList.querySelectorAll(".uat-step").forEach(function (box) {
            box.checked = checked[box.dataset.key] === true;
        });

        uatList.addEventListener("change", function (e) {
            const box = e.target.closest(".uat-step");
            if (!box) return;

            checked[box.dataset.key] = box.checked;
            try { localStorage.setItem(storageKey, JSON.stringify(checked)); } catch { }
        });

        // Guided tour: bấm một bước (hoặc "▶ Hướng dẫn" đi lần lượt) → POC mở đúng màn hình + tô sáng
        // phần tử khớp mô tả bước, để người xem biết bấm vào đâu thay vì tự mò. "Chỉ chỗ" là READ-ONLY:
        // annotator chỉ highlight, không tự thao tác — user vẫn tự bấm để kiểm chứng nghiệp vụ thật.
        function tourStep(screen, text) {
            postToFrame({ type: "poc-tour-step", screen: screen || "", text: text || "" });
        }

        uatList.addEventListener("click", function (e) {
            const fail = e.target.closest(".uat-fail");
            if (fail) {
                const scenario = fail.closest(".uat-scenario");
                const title = scenario?.dataset.title || "";
                openForm({
                    pageView: scenario?.dataset.screen || "",
                    elementLabel: `Kịch bản: ${title}`,
                    elementPath: "",
                    xPercent: 0,
                    yPercent: 0
                }, `Kịch bản "${title}" chưa đạt — `);
                return;
            }

            // Bấm chữ của một bước → chỉ chỗ ngay bước đó.
            const stepText = e.target.closest(".uat-step-text");
            if (stepText) {
                const scenario = stepText.closest(".uat-scenario");
                tourStep(scenario?.dataset.screen || "", stepText.dataset.step || stepText.textContent);
                return;
            }

            // "▶ Hướng dẫn" → đi lần lượt từng bước của kịch bản, mỗi bước dừng ~1.8s để người xem theo kịp.
            const tourBtn = e.target.closest(".uat-tour");
            if (tourBtn) {
                const scenario = tourBtn.closest(".uat-scenario");
                const screen = scenario?.dataset.screen || "";
                const steps = Array.from(scenario.querySelectorAll(".uat-step-text"))
                    .map(s => s.dataset.step || s.textContent);
                let i = 0;
                tourBtn.disabled = true;
                (function walk() {
                    if (i >= steps.length) { tourBtn.disabled = false; return; }
                    tourStep(screen, steps[i]);
                    const el = scenario.querySelectorAll(".uat-step-text")[i];
                    if (el) {
                        el.classList.add("uat-step-active");
                        setTimeout(() => el.classList.remove("uat-step-active"), 1700);
                    }
                    i++;
                    setTimeout(walk, 1800);
                })();
            }
        });
    }

    loadComments();
})();

// ==== Panel "Chia sẻ bản demo" ====
// Tạo/thu hồi link cho người KHÔNG có tài khoản. Tách IIFE riêng để phần review cốt lõi không phụ thuộc
// vào panel này (panel chỉ hiện với người có quyền quản lý requirement).
(function () {
    "use strict";

    const panel = document.getElementById("pocSharePanel");
    if (!panel) return;

    const root = document.getElementById("pocReviewRoot");
    const listEl = document.getElementById("pocShareList");
    const msgEl = document.getElementById("pocShareMsg");
    const labelEl = document.getElementById("pocShareLabel");
    const daysEl = document.getElementById("pocShareDays");
    const createBtn = document.getElementById("pocShareCreate");
    const projectId = root.dataset.projectId;
    const antiForgery = document.querySelector('#pocCommentForm input[name="__RequestVerificationToken"]');

    let links = [];

    function shareUrl(token) {
        return `${location.origin}/poc-share/${token}`;
    }

    function render() {
        if (!links.length) {
            listEl.innerHTML = '<p class="muted">Chưa có link nào.</p>';
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
                            <button type="button" class="btn outline small poc-share-copy">Copy</button>
                            <button type="button" class="btn outline small poc-share-revoke" data-id="${l.id}">Thu hồi</button>
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

    createBtn.addEventListener("click", async function () {
        createBtn.disabled = true;
        msgEl.textContent = "";
        try {
            const fd = new FormData();
            fd.append("projectId", projectId);
            fd.append("label", labelEl.value.trim());
            fd.append("days", daysEl.value);
            if (antiForgery) fd.append("__RequestVerificationToken", antiForgery.value);

            const response = await fetch(panel.dataset.createUrl, { method: "POST", body: fd });
            if (!response.ok) {
                msgEl.textContent = await response.text() || "Không tạo được link.";
            } else {
                labelEl.value = "";
                await load();
            }
        } catch {
            msgEl.textContent = "Không tạo được link — kiểm tra kết nối rồi thử lại.";
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
                msgEl.textContent = "Đã copy link.";
            } catch {
                // Trình duyệt chặn clipboard API (http, quyền) — link đã được bôi đen sẵn để Ctrl+C.
                msgEl.textContent = "Không copy tự động được — link đã được bôi đen, bấm Ctrl+C.";
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
        } catch {
            msgEl.textContent = "Không thu hồi được link.";
        }
    });

    load();
})();
