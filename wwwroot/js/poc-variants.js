// ==== Tab "Thử model khác" trong hộp thoại "Chi tiết kỹ thuật" (biến thể POC thử model — PocVariantsController) ====
// Bảng so sánh: mỗi CỘT là một bản demo (bản chính + các biến thể), mỗi HÀNG là một tiêu chí máy đo được.
// Đọc ngang một hàng là so các model trên đúng một tiêu chí, nên ô tốt nhất của hàng được tô — chỉ xét các
// cột ĐÃ CÓ số đo (lượt đang chờ/hỏng không được "thắng" nhờ ô trống).
// Có lượt đang chờ/chạy thì poll mỗi 5s, nhưng chỉ khi tab đang hiện: rời tab hoặc đóng hộp thoại là ngừng
// gọi mạng. Đóng/mở và đổi tab thuộc poc-review.js; tab này chỉ nghe "poctech:panelshown"/"poctech:closed".
(function () {
    "use strict";

    const backdrop = document.getElementById("pocTechModal");
    const dialog = document.getElementById("pocVariantsPanel");
    if (!backdrop || !dialog) return;

    const select = document.getElementById("pocVariantsModel");
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
        return m > 0 ? `${m} min ${s % 60}s` : `${s}s`;
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
        Queued: { label: "Queued", cls: "wait" },
        Running: { label: "Running", cls: "run" },
        Completed: { label: "Completed", cls: "ok" },
        Failed: { label: "Failed", cls: "bad" }
    };

    // Tỉ lệ "x/y" — giá trị so sánh là phần trăm; mẫu số 0 nghĩa là tiêu chí không áp dụng (spec không
    // có kịch bản nào…) nên không tham gia so.
    function ratio(pass, total) {
        return total > 0 ? { text: `${pass}/${total}`, value: pass / total } : { text: "—", value: null };
    }

    // Mỗi tiêu chí: nhãn, cách lấy ô từ một dòng, và chiều "tốt hơn" (cao/thấp). Ô trả value null ⇒ không so.
    const CRITERIA = [
        { label: "Screens covered per spec", better: "high", cell: r => r.metrics ? ratio(r.metrics.coveredScreens, r.metrics.specScreens) : null },
        { label: "UAT scenarios with a PASS item", better: "high", cell: r => r.metrics ? ratio(r.metrics.uatCovered, r.metrics.uatTotal) : null },
        { label: "Automated scenario click-through", better: "high", cell: r => r.metrics ? ratio(r.metrics.uatDrivePass, r.metrics.uatDriveTotal) : null },
        { label: "Business rule (self-test) PASS", better: "high", cell: r => r.metrics ? ratio(r.metrics.selfTestPass, r.metrics.selfTestTotal) : null },
        // Chỉ các luồng NGOÀI bộ UAT — luồng khớp UAT đã được đếm ở dòng "UAT scenarios with a PASS item".
        { label: "End-to-end flows outside the UAT set PASS", better: "high", cell: r => r.metrics ? ratio(r.metrics.outsideUatScenarioPass, r.metrics.outsideUatScenarioTotal) : null },
        {
            label: "Issues flagged by the checker", better: "low",
            cell: r => r.metrics ? { text: String(r.metrics.openIssues) + (r.metrics.runtimeRan ? "" : " *"), value: r.metrics.openIssues } : null
        },
        { label: "Build time", better: "low", cell: r => ({ text: formatDuration(r.durationSeconds), value: r.status === "Completed" ? r.durationSeconds : null }) },
        { label: "Model calls", better: "low", cell: r => ({ text: String(r.modelCalls || 0), value: r.status === "Completed" && r.modelCalls ? r.modelCalls : null }) },
        {
            label: "Tokens in / out", better: null,
            cell: r => ({ text: `${formatTokens(r.promptTokens)} / ${formatTokens(r.completionTokens)}`, value: null })
        },
        { label: "Cost (USD)", better: "low", cell: r => ({ text: formatCost(r.costUsd), value: r.status === "Completed" && r.modelCalls ? r.costUsd : null }) }
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
            ? `<span class="poc-variant-tag base">Version under review</span>`
            : `<span class="poc-variant-tag">Variant #${index}</span>`;
        const meta = row.isBaseline
            ? (page.baselineRevisionRounds > 0
                ? `<div class="muted small">The build has since gone through ${page.baselineRevisionRounds} revision round(s) — metrics are for the current version.</div>`
                : "")
            : `<div class="muted small">${esc(row.requestedBy || "")} · ${new Date(row.createdAt).toLocaleString("vi-VN")}</div>`;
        return `<th scope="col">
            ${title}
            <div class="poc-variant-model" title="${esc(row.modelLabel)}">${esc(row.modelLabel)}</div>
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
            parts.push(`<a class="btn outline small" href="${esc(url)}" target="_blank" rel="noopener">Open demo ↗</a>`);
        }
        if (!row.isBaseline && row.status !== "Running") {
            parts.push(`<button type="button" class="btn outline small poc-variant-delete" data-id="${esc(row.id)}">${row.status === "Queued" ? "Cancel" : "Delete"}</button>`);
        }
        return `<td>${parts.join(" ") || "—"}</td>`;
    }

    function render(page) {
        lastPage = page;

        // Dropdown model: giữ lựa chọn đang có qua mỗi lần poll (dropdown.js tự dựng lại combo khi options đổi).
        const chosen = select.value;
        select.innerHTML = page.models.map(m =>
            `<option value="${esc(m.id)}">${esc(m.label)}${m.isCurrent ? " (assigned to Developer)" : ""}</option>`).join("");
        if (chosen && page.models.some(m => m.id === chosen)) select.value = chosen;

        const variantCount = page.rows.filter(r => !r.isBaseline).length;
        startBtn.disabled = !page.hasSource || page.hasActive || page.models.length === 0 || variantCount >= page.limit;
        startBtn.title = !page.hasSource ? "The project has no completed POC build yet"
            : page.hasActive ? "A trial is queued/running — wait for it to finish"
            : variantCount >= page.limit ? `Already holding ${page.limit} variants — delete some old results`
            : "";

        if (page.rows.length === 0) {
            tableWrap.innerHTML = `<p class="muted">No demos to compare yet.</p>`;
            return;
        }

        // Đánh số biến thể theo thứ tự tạo (cũ nhất = #1) dù cột hiển thị mới nhất trước.
        const variants = page.rows.filter(r => !r.isBaseline);
        const numberOf = new Map(variants.map((r, i) => [r.id, variants.length - i]));

        const head = `<tr><th scope="col" class="poc-variant-crit">Criterion</th>${page.rows.map(r => headerCell(r, numberOf.get(r.id), page)).join("")}</tr>`;

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
            ? `<tr><th scope="row" class="poc-variant-crit">Notes</th>${page.rows.map(r =>
                `<td class="poc-variant-note${r.error ? " bad" : ""}">${esc(r.error || r.progress || "")}</td>`).join("")}</tr>`
            : "";

        const actions = `<tr><th scope="row" class="poc-variant-crit"></th>${page.rows.map(actionCell).join("")}</tr>`;

        tableWrap.innerHTML = `<table class="poc-variants-table"><thead>${head}</thead><tbody>${body}${notes}${actions}</tbody></table>
            <p class="muted small poc-variants-foot">Green cells are the best in their row. * = the headless browser could not run, so there are no runtime errors yet.
            Cost is computed on the Developer agent's calls at the prices on the AI Models screen. Re-run the same model to gauge variance before drawing conclusions.</p>`;
    }

    async function load() {
        try {
            const response = await fetch(dialog.dataset.listUrl, { headers: { "Accept": "application/json" } });
            const data = await response.json();
            if (!data.ok) {
                showMessage(data.error || "Could not load the comparison table.", true);
                return;
            }
            render(data.page);
            schedulePoll(data.page.hasActive);
        } catch {
            showMessage("Could not load the comparison table — check your connection and reopen.", true);
        }
    }

    function schedulePoll(active) {
        clearTimeout(pollTimer);
        pollTimer = null;
        if (active && isVisible())
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
            const data = await post(dialog.dataset.startUrl, { projectId: dialog.dataset.projectId, aiModelId: select.value });
            if (!data.ok) {
                showMessage(data.error || "Could not start the trial.", true);
            } else {
                showMessage(`Queued a build with ${label}. A build usually takes a few minutes — the table updates itself while the dialog is open.`, false);
            }
        } catch {
            showMessage("Could not start the trial — check your connection and try again.", true);
        }
        await load();
    });

    tableWrap.addEventListener("click", async e => {
        const btn = e.target.closest(".poc-variant-delete");
        if (!btn) return;
        if (!confirm("Delete this variant and its demo? The cost already spent stays on the Usage page.")) return;
        btn.disabled = true;
        try {
            const data = await post(dialog.dataset.deleteUrl, { projectId: dialog.dataset.projectId, variantId: btn.dataset.id });
            if (!data.ok) showMessage(data.error || "Could not delete.", true);
        } catch {
            showMessage("Could not delete — check your connection and try again.", true);
        }
        await load();
    });

    function isVisible() {
        return !backdrop.classList.contains("hidden") && !dialog.classList.contains("hidden");
    }

    function stopPolling() {
        clearTimeout(pollTimer);
        pollTimer = null;
    }

    function shown() {
        showMessage("");
        if (!lastPage) tableWrap.innerHTML = `<p class="muted">Loading…</p>`;
        load();
    }

    backdrop.addEventListener("poctech:panelshown", e => {
        if (e.detail && e.detail.panelId === dialog.id) shown();
        else stopPolling();
    });
    backdrop.addEventListener("poctech:closed", stopPolling);
})();
