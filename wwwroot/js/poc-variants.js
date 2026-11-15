// ==== Hộp thoại "Thử model khác" (biến thể POC thử model — PocVariantsController) ====
// Bảng so sánh: mỗi CỘT là một bản demo (bản chính + các biến thể), mỗi HÀNG là một tiêu chí máy đo được.
// Đọc ngang một hàng là so các model trên đúng một tiêu chí, nên ô tốt nhất của hàng được tô — chỉ xét các
// cột ĐÃ CÓ số đo (lượt đang chờ/hỏng không được "thắng" nhờ ô trống).
// Có lượt đang chờ/chạy thì poll mỗi 5s, nhưng chỉ khi hộp thoại đang mở: đóng lại là ngừng gọi mạng.
(function () {
    "use strict";

    const backdrop = document.getElementById("pocVariantsModal");
    const openBtn = document.getElementById("pocVariantsOpen");
    if (!backdrop || !openBtn) return;

    const dialog = backdrop.querySelector(".poc-variants-modal");
    const select = document.getElementById("pocVariantsModel");
    const harnessField = document.getElementById("pocVariantsHarnessField");
    const harnessSelect = document.getElementById("pocVariantsHarness");
    const startBtn = document.getElementById("pocVariantsStart");
    const msg = document.getElementById("pocVariantsMsg");
    const tableWrap = document.getElementById("pocVariantsTable");
    const antiForgery = dialog.querySelector('input[name="__RequestVerificationToken"]');
    const POLL_MS = 5000;

    let pollTimer = null;
    let lastPage = null;

    function esc(value) {
        return String(value ?? "").replace(/[&<>"']/g, c => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" }[c]));
    }

    function showMessage(text, isError) {
        msg.textContent = text || "";
        msg.classList.toggle("error", !!isError);
    }

    function formatDuration(seconds) {
        if (seconds == null) return "—";
        const s = Math.round(seconds);
        const m = Math.floor(s / 60);
        return m > 0 ? `${m} phút ${s % 60}s` : `${s}s`;
    }

    function formatTokens(n) {
        if (!n) return "0";
        return n >= 1000 ? `${(n / 1000).toFixed(n >= 100000 ? 0 : 1)}k` : String(n);
    }

    function formatCost(usd) {
        if (!usd) return "$0";
        return usd < 0.01 ? `$${usd.toFixed(4)}` : `$${usd.toFixed(2)}`;
    }

    const STATUS = {
        Queued: { label: "Đang chờ", cls: "wait" },
        Running: { label: "Đang chạy", cls: "run" },
        Completed: { label: "Xong", cls: "ok" },
        Failed: { label: "Lỗi", cls: "bad" }
    };

    // Tỉ lệ "x/y" — giá trị so sánh là phần trăm; mẫu số 0 nghĩa là tiêu chí không áp dụng (spec không
    // có kịch bản nào…) nên không tham gia so.
    function ratio(pass, total) {
        return total > 0 ? { text: `${pass}/${total}`, value: pass / total } : { text: "—", value: null };
    }

    // Mỗi tiêu chí: nhãn, cách lấy ô từ một dòng, và chiều "tốt hơn" (cao/thấp). Ô trả value null ⇒ không so.
    const CRITERIA = [
        { label: "Màn hình phủ theo spec", better: "high", cell: r => r.metrics ? ratio(r.metrics.coveredScreens, r.metrics.specScreens) : null },
        { label: "Kịch bản nghiệm thu có mục PASS", better: "high", cell: r => r.metrics ? ratio(r.metrics.uatCovered, r.metrics.uatTotal) : null },
        { label: "Máy bấm thử theo kịch bản", better: "high", cell: r => r.metrics ? ratio(r.metrics.uatDrivePass, r.metrics.uatDriveTotal) : null },
        { label: "Business rule (self-test) PASS", better: "high", cell: r => r.metrics ? ratio(r.metrics.selfTestPass, r.metrics.selfTestTotal) : null },
        { label: "Kịch bản end-to-end PASS", better: "high", cell: r => r.metrics ? ratio(r.metrics.scenarioPass, r.metrics.scenarioTotal) : null },
        {
            label: "Điểm máy báo chưa đạt", better: "low",
            cell: r => r.metrics ? { text: String(r.metrics.openIssues) + (r.metrics.runtimeRan ? "" : " *"), value: r.metrics.openIssues } : null
        },
        { label: "Thời gian dựng", better: "low", cell: r => ({ text: formatDuration(r.durationSeconds), value: r.status === "Completed" ? r.durationSeconds : null }) },
        { label: "Số lời gọi model", better: "low", cell: r => ({ text: String(r.modelCalls || 0), value: r.status === "Completed" && r.modelCalls ? r.modelCalls : null }) },
        {
            label: "Token vào / ra", better: null,
            cell: r => ({ text: `${formatTokens(r.promptTokens)} / ${formatTokens(r.completionTokens)}`, value: null })
        },
        { label: "Chi phí (USD)", better: "low", cell: r => ({ text: formatCost(r.costUsd), value: r.status === "Completed" && r.modelCalls ? r.costUsd : null }) }
    ];

    function bestValue(values, better) {
        const usable = values.filter(v => v != null);
        // Một cột duy nhất có số thì "tốt nhất" là vô nghĩa — không tô.
        if (!better || usable.length < 2) return null;
        const best = better === "high" ? Math.max(...usable) : Math.min(...usable);
        // Mọi cột bằng nhau thì cũng không có ai thắng.
        return usable.every(v => v === best) ? null : best;
    }

    function headerCell(row, index, page) {
        const status = STATUS[row.status] || { label: row.status, cls: "" };
        const title = row.isBaseline
            ? `<span class="poc-variant-tag base">Bản đang review</span>`
            : `<span class="poc-variant-tag">Biến thể #${index}</span>`;
        const meta = row.isBaseline
            ? (page.baselineRevisionRounds > 0
                ? `<div class="muted small">Sau lượt dựng đã qua ${page.baselineRevisionRounds} vòng chỉnh sửa — số đo là của bản hiện tại.</div>`
                : "")
            : `<div class="muted small">${esc(row.requestedBy || "")} · ${new Date(row.createdAt).toLocaleString("vi-VN")}</div>`;
        return `<th scope="col">
            ${title}
            <div class="poc-variant-model" title="${esc(row.modelLabel)}">${esc(row.modelLabel)}</div>
            ${row.harness === "Opencode" ? `<span class="poc-variant-tag harness" title="Dựng bằng agent CLI opencode, gọi model qua relay của app">vỏ opencode</span>` : ""}
            <span class="poc-variant-status ${status.cls}">${status.label}</span>
            ${meta}
        </th>`;
    }

    function actionCell(row) {
        const parts = [];
        if (row.hasDemo) {
            const url = row.isBaseline
                ? dialog.dataset.baselineUrl
                : `${dialog.dataset.demoUrl}&variantId=${encodeURIComponent(row.id)}`;
            parts.push(`<a class="btn outline small" href="${esc(url)}" target="_blank" rel="noopener">Mở demo ↗</a>`);
        }
        if (!row.isBaseline && row.status !== "Running") {
            parts.push(`<button type="button" class="btn outline small poc-variant-delete" data-id="${esc(row.id)}">${row.status === "Queued" ? "Huỷ" : "Xoá"}</button>`);
        }
        return `<td>${parts.join(" ") || "—"}</td>`;
    }

    function render(page) {
        lastPage = page;

        // Dropdown model: giữ lựa chọn đang có qua mỗi lần poll (dropdown.js tự dựng lại combo khi options đổi).
        const chosen = select.value;
        select.innerHTML = page.models.map(m =>
            `<option value="${esc(m.id)}">${esc(m.label)}${m.isCurrent ? " (đang gán cho Developer)" : ""}</option>`).join("");
        if (chosen && page.models.some(m => m.id === chosen)) select.value = chosen;

        // Lựa chọn vỏ agent chỉ có nghĩa khi máy chủ bật opencode; tắt thì luôn gửi vỏ của app.
        if (harnessField) {
            harnessField.hidden = !page.opencodeEnabled;
            if (!page.opencodeEnabled) harnessSelect.value = "Builtin";
        }

        const variantCount = page.rows.filter(r => !r.isBaseline).length;
        startBtn.disabled = !page.hasSource || page.hasActive || page.models.length === 0 || variantCount >= page.limit;
        startBtn.title = !page.hasSource ? "Dự án chưa có lượt dựng POC hoàn tất nào"
            : page.hasActive ? "Đang có một lượt thử chờ/chạy — đợi nó xong"
            : variantCount >= page.limit ? `Đã giữ đủ ${page.limit} biến thể — xoá bớt kết quả cũ`
            : "";

        if (page.rows.length === 0) {
            tableWrap.innerHTML = `<p class="muted">Chưa có bản demo nào để so.</p>`;
            return;
        }

        // Đánh số biến thể theo thứ tự tạo (cũ nhất = #1) dù cột hiển thị mới nhất trước.
        const variants = page.rows.filter(r => !r.isBaseline);
        const numberOf = new Map(variants.map((r, i) => [r.id, variants.length - i]));

        const head = `<tr><th scope="col" class="poc-variant-crit">Tiêu chí</th>${page.rows.map(r => headerCell(r, numberOf.get(r.id), page)).join("")}</tr>`;

        const body = CRITERIA.map(c => {
            const cells = page.rows.map(r => c.cell(r));
            const best = bestValue(cells.map(x => x ? x.value : null), c.better);
            return `<tr><th scope="row" class="poc-variant-crit">${esc(c.label)}</th>${cells.map(x => {
                if (!x) return `<td class="muted">—</td>`;
                const win = best != null && x.value === best;
                return `<td class="${win ? "poc-variant-best" : ""}">${esc(x.text)}</td>`;
            }).join("")}</tr>`;
        }).join("");

        // Hàng trạng thái chữ: tiến độ của lượt đang chạy, lỗi của lượt hỏng.
        const notes = page.rows.some(r => r.progress || r.error)
            ? `<tr><th scope="row" class="poc-variant-crit">Ghi chú</th>${page.rows.map(r =>
                `<td class="poc-variant-note${r.error ? " bad" : ""}">${esc(r.error || r.progress || "")}</td>`).join("")}</tr>`
            : "";

        const actions = `<tr><th scope="row" class="poc-variant-crit"></th>${page.rows.map(actionCell).join("")}</tr>`;

        tableWrap.innerHTML = `<table class="poc-variants-table"><thead>${head}</thead><tbody>${body}${notes}${actions}</tbody></table>
            <p class="muted small poc-variants-foot">Ô tô xanh là tốt nhất trên hàng đó. * = trình duyệt headless không chạy được nên chưa có lỗi runtime.
            Chi phí tính trên lời gọi của agent Developer theo đơn giá ở màn AI Models. Chạy lại cùng một model để biết độ dao động trước khi kết luận.</p>`;
    }

    async function load() {
        try {
            const response = await fetch(dialog.dataset.listUrl, { headers: { "Accept": "application/json" } });
            const data = await response.json();
            if (!data.ok) {
                showMessage(data.error || "Không tải được bảng so sánh.", true);
                return;
            }
            render(data.page);
            schedulePoll(data.page.hasActive);
        } catch {
            showMessage("Không tải được bảng so sánh — kiểm tra kết nối rồi mở lại.", true);
        }
    }

    function schedulePoll(active) {
        clearTimeout(pollTimer);
        pollTimer = null;
        if (active && !backdrop.classList.contains("hidden"))
            pollTimer = setTimeout(load, POLL_MS);
    }

    async function post(url, fields) {
        const fd = new FormData();
        Object.entries(fields).forEach(([k, v]) => fd.append(k, v));
        if (antiForgery) fd.append("__RequestVerificationToken", antiForgery.value);
        const response = await fetch(url, { method: "POST", body: fd });
        return response.json();
    }

    startBtn.addEventListener("click", async () => {
        if (!select.value) return;
        const label = select.options[select.selectedIndex]?.text || "";
        startBtn.disabled = true;
        showMessage("");
        try {
            const harness = harnessSelect && harnessField && !harnessField.hidden ? harnessSelect.value : "Builtin";
            const data = await post(dialog.dataset.startUrl, { projectId: dialog.dataset.projectId, aiModelId: select.value, harness });
            if (!data.ok) {
                showMessage(data.error || "Không bắt đầu được lượt thử.", true);
            } else {
                const via = harnessSelect && harnessSelect.value === "Opencode" && !harnessField.hidden ? " qua opencode" : "";
                showMessage(`Đã xếp hàng lượt dựng bằng ${label}${via}. Một lượt thường mất vài phút — bảng tự cập nhật khi hộp thoại đang mở.`, false);
            }
        } catch {
            showMessage("Không bắt đầu được lượt thử — kiểm tra kết nối rồi thử lại.", true);
        }
        await load();
    });

    tableWrap.addEventListener("click", async e => {
        const btn = e.target.closest(".poc-variant-delete");
        if (!btn) return;
        if (!confirm("Xoá biến thể này cùng bản demo của nó? Chi phí đã tiêu vẫn nằm trong trang Usage.")) return;
        btn.disabled = true;
        try {
            const data = await post(dialog.dataset.deleteUrl, { projectId: dialog.dataset.projectId, variantId: btn.dataset.id });
            if (!data.ok) showMessage(data.error || "Không xoá được.", true);
        } catch {
            showMessage("Không xoá được — kiểm tra kết nối rồi thử lại.", true);
        }
        await load();
    });

    function open() {
        backdrop.classList.remove("hidden");
        openBtn.setAttribute("aria-expanded", "true");
        showMessage("");
        if (!lastPage) tableWrap.innerHTML = `<p class="muted">Đang tải…</p>`;
        load();
        startBtn.focus();
    }

    function close() {
        backdrop.classList.add("hidden");
        openBtn.setAttribute("aria-expanded", "false");
        clearTimeout(pollTimer);
        pollTimer = null;
        openBtn.focus();
    }

    openBtn.addEventListener("click", open);
    document.getElementById("pocVariantsClose").addEventListener("click", close);
    document.getElementById("pocVariantsDone").addEventListener("click", close);
    backdrop.addEventListener("click", e => { if (e.target === backdrop) close(); });
    document.addEventListener("keydown", e => {
        if (e.key === "Escape" && !backdrop.classList.contains("hidden")) close();
    });
})();
