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

    // "Gửi về Requirement" (B): các ghi chú hiểu-sai-yêu-cầu được lọc + đưa vào hội thoại BA để soạn lại
    // TÀI LIỆU của chính dự án (không chỉ vá HTML). Server tự bỏ ghi chú thẩm mỹ.
    const routeReqBtn = document.getElementById("pocRouteReqBtn");
    const routeReqUrl = root.dataset.routeReqUrl;
    const routeReqHint = document.getElementById("pocRouteReqHint");
    if (routeReqBtn && routeReqUrl) {
        routeReqBtn.addEventListener("click", async function () {
            if (!confirm("Gửi các điểm HIỂU SAI YÊU CẦU về BA để cập nhật tài liệu? (Ghi chú chỉnh trình bày sẽ được bỏ qua.)")) return;

            routeReqBtn.disabled = true;
            const original = routeReqBtn.textContent;
            routeReqBtn.textContent = "Đang gửi…";

            const fd = new FormData();
            fd.append("projectId", projectId);
            if (antiForgery) fd.append("__RequestVerificationToken", antiForgery.value);

            try {
                const response = await fetch(routeReqUrl, { method: "POST", body: fd });
                const data = await response.json().catch(() => null);
                if (routeReqHint && data && data.message) {
                    routeReqHint.textContent = data.message;
                    routeReqHint.classList.toggle("poc-route-ok", !!data.ok);
                }
                if (data && data.ok) {
                    // Các ghi chú đã chuyển trạng thái (RoutedToRequirement) — làm tươi danh sách.
                    await loadComments();
                }
            } catch {
                if (routeReqHint) routeReqHint.textContent = "Không gửi được — thử lại sau.";
            } finally {
                routeReqBtn.disabled = false;
                routeReqBtn.textContent = original;
            }
        });
    }

    // "Bản demo đã đạt — tôi nghiệm thu": đường ĐÓNG hành trình phía người yêu cầu, đối trọng với hai nút
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

    // "Nhờ đội Dev chỉnh bản demo": gom các ghi chú đang mở thành một vòng chỉnh sửa POC cho Developer.
    // Khác nút bên dưới (gửi về Requirement — sửa TÀI LIỆU rồi dựng lại), đây là đường vá chính bản demo,
    // vốn trước đây chỉ mở cho người có quyền cổng duyệt trên Agent Dashboard.
    const requestFixBtn = document.getElementById("pocRequestFixBtn");
    const requestFixUrl = root.dataset.requestFixUrl;
    if (requestFixBtn && requestFixUrl) {
        const fixHint = requestFixBtn.nextElementSibling;

        requestFixBtn.addEventListener("click", async function () {
            if (requestFixBtn.dataset.limitReached === "true") {
                if (fixHint) fixHint.textContent = "Đã hết số vòng chỉnh sửa cho bản demo này — nếu vẫn chưa đúng thì thường là do tài liệu, hãy dùng nút gửi về Requirement.";
                return;
            }
            if (!confirm("Gửi các ghi chú đang mở cho đội Dev chỉnh bản demo?")) return;

            requestFixBtn.disabled = true;
            const original = requestFixBtn.textContent;
            requestFixBtn.textContent = "Đang gửi…";

            const fd = new FormData();
            fd.append("projectId", projectId);
            if (antiForgery) fd.append("__RequestVerificationToken", antiForgery.value);

            try {
                const response = await fetch(requestFixUrl, { method: "POST", body: fd });
                const data = await response.json().catch(() => null);
                if (fixHint && data && data.message) {
                    fixHint.textContent = data.message;
                    fixHint.classList.toggle("poc-route-ok", !!data.ok);
                }
                if (data && data.ok) {
                    // Ghi chú đã chuyển sang Sent (đang được sửa) — làm tươi danh sách như nút kia.
                    await loadComments();
                }
            } catch {
                if (fixHint) fixHint.textContent = "Không gửi được — thử lại sau.";
            } finally {
                requestFixBtn.disabled = false;
                requestFixBtn.textContent = original;
            }
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
