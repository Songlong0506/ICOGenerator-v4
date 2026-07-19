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
