// ==== THẺ "BẢNG ĐÃ CHỐT" TRONG BONG BÓNG NGƯỜI DÙNG ====
// Khi người dùng bấm "Gửi bảng …", trình duyệt gửi vào khung chat một tin nhắn CHỮ do server soạn
// (*Builder.RenderUserMessage). Chữ đó là bản chép ĐÚNG của bảng đã lưu và là thứ đi vào transcript cho
// BA/bản đồ bao phủ/bộ nhớ đọc — nên nó phải giữ nguyên là chữ. Nhưng để người dùng ĐỌC LẠI thì một khối
// 50 dòng "- chức năng: vai (phạm vi)" là thứ khó soát nhất: không nhìn ra được vai nào bị thiếu, dòng
// nào không ai được làm, dòng nào có điều kiện.
//
// File này chỉ đổi CÁCH HIỂN THỊ: đọc <p> của bong bóng (nguồn duy nhất, ô "✎ sửa" cũng đọc nó), dựng
// một thẻ có cấu trúc ngay sau, rồi ẩn <p>. Không nhận ra khuôn ⇒ không làm gì, bong bóng vẫn là chữ như
// trước — phía an toàn. Khuôn chữ ở đây phải khớp các RenderUserMessage:
//   PermissionMatrixBuilder · ReportMapBuilder · ScreenScopeMapBuilder · NotificationMapBuilder.
// Đổi câu chữ ở đó mà không đổi ở đây thì thẻ không dựng (rơi về chữ thường), không vỡ gì.
(function () {
    "use strict";

    const PREFIX = {
        permission: "Mình đã rà bảng phân quyền",
        report: "Mình đã rà bảng báo cáo / thống kê",
        screen: "Mình đã rà bảng màn hình",
        notification: "Mình đã rà bảng thông báo"
    };

    // Thứ tự hẹp → rộng, khớp PermissionScope.Granted. Lấy từ khối từ vựng server gửi xuống nếu có.
    const SCOPES = ((window.REQUIREMENTS_VOCAB || {}).permScopes || []).length
        ? window.REQUIREMENTS_VOCAB.permScopes
        : ["của mình", "của đơn vị", "tất cả"];
    const NO_ROLE = "không vai nào được làm";

    // Trên ngưỡng này các nhóm (màn hình) mặc định thu gọn — đủ để thấy toàn cảnh trong một màn hình.
    const COLLAPSE_AFTER = 3;

    function esc(s) {
        return String(s ?? "")
            .replace(/&/g, "&amp;").replace(/</g, "&lt;").replace(/>/g, "&gt;")
            .replace(/"/g, "&quot;").replace(/'/g, "&#39;");
    }

    function stripDot(s) { return s.replace(/\.\s*$/, ""); }

    function splitList(s, sep) {
        return stripDot(s).split(sep).map(x => x.trim()).filter(Boolean);
    }

    // ---------- Bảng phân quyền ----------

    const SCOPE_ALT = SCOPES.map(s => s.replace(/[.*+?^${}()|[\]\\]/g, "\\$&")).join("|");
    const GRANTS_RE = new RegExp(`^(?:.+? \\((?:${SCOPE_ALT})\\))(?:, .+? \\((?:${SCOPE_ALT})\\))*$`);
    const GRANT_RE = new RegExp(`(.+?) \\((${SCOPE_ALT})\\)(?:, |$)`, "g");

    function parseWho(who) {
        if (who === NO_ROLE) return [];
        if (!GRANTS_RE.test(who)) return null;
        const grants = [];
        GRANT_RE.lastIndex = 0;
        let m;
        while ((m = GRANT_RE.exec(who)) !== null) grants.push({ role: m[1], scope: m[2] });
        return grants;
    }

    // "- Chức năng: Vai A (tất cả), Vai B (của mình) [điều kiện: …]" — tên chức năng có thể tự chứa ": "
    // ("Sửa: thông tin"), nên thử từng dấu ": " từ PHẢI sang, lấy chỗ đầu tiên mà phần sau khớp trọn khuôn
    // danh sách vai. Từ phải vì tên chức năng hay chứa ": " hơn tên vai trò.
    function parsePermissionLine(body) {
        let condition = "";
        const cm = body.match(/ \[điều kiện: ([\s\S]*)\]$/);
        if (cm) { condition = cm[1]; body = body.slice(0, cm.index); }

        let idx = body.lastIndexOf(": ");
        while (idx > 0) {
            const grants = parseWho(body.slice(idx + 2));
            if (grants) return { fn: body.slice(0, idx), grants, condition };
            idx = body.lastIndexOf(": ", idx - 1);
        }
        return null;
    }

    function parsePermission(lines) {
        const groups = [];
        let current = null;
        for (const raw of lines) {
            const line = raw.trimEnd();
            if (!line.trim()) continue;
            if (line.startsWith("- ")) {
                if (!current) return null;
                const row = parsePermissionLine(line.slice(2));
                if (!row) return null;
                current.rows.push(row);
            } else if (line.endsWith(":")) {
                current = { screen: line.slice(0, -1), rows: [] };
                groups.push(current);
            } else {
                return null;
            }
        }
        return groups.length ? groups : null;
    }

    function scopeClass(scope) {
        const i = SCOPES.indexOf(scope);
        return i >= 0 ? `tsub-scope-${i}` : "";
    }

    function grantChip(g) {
        return `<span class="tsub-chip tsub-role ${scopeClass(g.scope)}"><b>${esc(g.role)}</b><i>${esc(g.scope)}</i></span>`;
    }

    function renderPermission(groups) {
        const roles = new Set();
        let fnCount = 0, nobody = 0, conditional = 0;
        groups.forEach(g => g.rows.forEach(r => {
            fnCount++;
            if (!r.grants.length) nobody++;
            if (r.condition) conditional++;
            r.grants.forEach(x => roles.add(x.role));
        }));
        const collapse = groups.length > COLLAPSE_AFTER;

        const meta = [
            `${groups.length} màn hình`,
            `${fnCount} chức năng`,
            `${roles.size} vai trò`
        ];
        if (conditional) meta.push(`${conditional} có điều kiện`);
        if (nobody) meta.push(`<span class="tsub-warn">${nobody} không ai được làm</span>`);

        const legend = SCOPES.map(s =>
            `<span class="tsub-legend-item"><span class="tsub-dot ${scopeClass(s)}"></span>${esc(s)}</span>`).join("");

        const sections = groups.map(g => {
            const groupRoles = [...new Set(g.rows.flatMap(r => r.grants.map(x => x.role)))];
            const rows = g.rows.map(r => `
                <tr class="${r.grants.length ? "" : "is-nobody"}">
                    <td class="tsub-fn">${esc(r.fn)}${r.condition
                        ? `<div class="tsub-cond" title="Điều kiện"><span aria-hidden="true">⚑</span> ${esc(r.condition)}</div>` : ""}</td>
                    <td class="tsub-who">${r.grants.length
                        ? r.grants.map(grantChip).join("")
                        : `<span class="tsub-chip tsub-nobody">Không vai nào</span>`}</td>
                </tr>`).join("");
            return `
                <details class="tsub-group"${collapse ? "" : " open"}>
                    <summary>
                        <span class="tsub-group-name">${esc(g.screen)}</span>
                        <span class="tsub-group-meta">${g.rows.length} chức năng${groupRoles.length
                            ? " · " + groupRoles.map(esc).join(", ") : ""}</span>
                    </summary>
                    <table class="tsub-table">
                        <thead><tr><th>Chức năng</th><th>Vai trò được làm (phạm vi dữ liệu)</th></tr></thead>
                        <tbody>${rows}</tbody>
                    </table>
                </details>`;
        }).join("");

        return frame("🔐", "Bảng phân quyền đã chốt", meta, `
            <div class="tsub-legend"><span>Phạm vi:</span>${legend}</div>
            ${sections}`, groups.length > 1 ? { expanded: !collapse } : null);
    }

    // ---------- Bảng báo cáo ----------

    function parseReport(lines) {
        const kept = [];
        let dropped = [];
        for (const raw of lines) {
            const line = raw.trim();
            if (!line) continue;
            if (!line.startsWith("- ")) return null;
            const body = line.slice(2);
            const dm = body.match(/^\(bỏ: ([\s\S]*)\)$/);
            if (dm) { dropped = splitList(dm[1], "; "); continue; }

            const parts = body.split(" · ");
            const item = { name: parts.shift(), question: "", source: "", breakdown: "" };
            for (const p of parts) {
                if (p.startsWith("lấy số từ: ")) item.source = p.slice("lấy số từ: ".length);
                else if (p.startsWith("gộp/lọc theo: ")) item.breakdown = p.slice("gộp/lọc theo: ".length);
                else item.question = item.question ? `${item.question} · ${p}` : p;
            }
            kept.push(item);
        }
        return { kept, dropped };
    }

    function renderReport(data) {
        const meta = [`${data.kept.length} báo cáo cần có`];
        if (data.dropped.length) meta.push(`${data.dropped.length} đã bỏ`);

        const rows = data.kept.map((r, i) => `
            <tr>
                <td class="tsub-idx">${i + 1}</td>
                <td><b>${esc(r.name)}</b>${r.question ? `<div class="tsub-sub">${esc(r.question)}</div>` : ""}</td>
                <td>${r.source ? esc(r.source) : `<span class="tsub-empty">—</span>`}</td>
                <td>${r.breakdown ? esc(r.breakdown) : `<span class="tsub-empty">—</span>`}</td>
            </tr>`).join("");

        const body = (data.kept.length ? `
            <table class="tsub-table">
                <thead><tr><th>#</th><th>Báo cáo · trả lời câu hỏi</th><th>Lấy số từ</th><th>Gộp / lọc theo</th></tr></thead>
                <tbody>${rows}</tbody>
            </table>` : `<p class="tsub-note">Ứng dụng không cần báo cáo nào trong danh sách BA đề xuất.</p>`)
            + droppedBlock("Báo cáo đã bỏ", data.dropped);

        return frame("📊", "Bảng báo cáo / thống kê đã chốt", meta, body, null);
    }

    // ---------- Bảng màn hình ----------

    function parseScreen(lines) {
        const screens = [];
        let added = [], droppedScreens = [], droppedFns = [];
        for (const raw of lines) {
            const line = raw.trimEnd();
            if (!line.trim()) continue;
            let m;
            if (line.startsWith("- ")) {
                const body = line.slice(2);
                m = body.match(/^([\s\S]*?) \[chức năng: ([\s\S]*)\]$/);
                screens.push(m
                    ? { name: m[1], fns: splitList(m[2], ", "), covers: [] }
                    : { name: body, fns: [], covers: [] });
            } else if ((m = line.match(/^\s+\(mình gộp vào màn này: ([\s\S]*)\)$/))) {
                if (!screens.length) return null;
                screens[screens.length - 1].covers = splitList(m[1], ", ");
            } else if (line.startsWith("Các màn hình mình tự bổ sung vào bảng: ")) {
                added = splitList(line.slice("Các màn hình mình tự bổ sung vào bảng: ".length), ", ");
            } else if (line.startsWith("Các màn hình mình KHÔNG cần: ")) {
                droppedScreens = splitList(line.slice("Các màn hình mình KHÔNG cần: ".length), ", ");
            } else if (line.startsWith("Các chức năng mình KHÔNG cần: ")) {
                droppedFns = splitList(line.slice("Các chức năng mình KHÔNG cần: ".length), ", ");
            } else {
                return null;
            }
        }
        const addedSet = new Set(added);
        screens.forEach(s => { s.added = addedSet.has(s.name); });
        return { screens, droppedScreens, droppedFns };
    }

    function renderScreen(data) {
        const fnCount = data.screens.reduce((n, s) => n + s.fns.length, 0);
        const meta = [`${data.screens.length} màn hình`, `${fnCount} chức năng`];
        const addedCount = data.screens.filter(s => s.added).length;
        if (addedCount) meta.push(`${addedCount} tự bổ sung`);
        if (data.droppedScreens.length) meta.push(`${data.droppedScreens.length} màn đã loại`);

        const rows = data.screens.map((s, i) => `
            <tr>
                <td class="tsub-idx">${i + 1}</td>
                <td class="tsub-fn"><b>${esc(s.name)}</b>${s.added ? ` <span class="tsub-badge">Tự thêm</span>` : ""}${s.covers.length
                    ? `<div class="tsub-sub">Gộp: ${s.covers.map(esc).join(", ")}</div>` : ""}</td>
                <td class="tsub-who">${s.fns.length
                    ? s.fns.map(f => `<span class="tsub-chip">${esc(f)}</span>`).join("")
                    : `<span class="tsub-empty">—</span>`}</td>
            </tr>`).join("");

        const body = `
            <table class="tsub-table">
                <thead><tr><th>#</th><th>Màn hình</th><th>Chức năng</th></tr></thead>
                <tbody>${rows}</tbody>
            </table>`
            + droppedBlock("Màn hình đã loại", data.droppedScreens)
            + droppedBlock("Chức năng đã loại", data.droppedFns);

        return frame("🖥️", "Bảng màn hình đã chốt", meta, body, null);
    }

    // ---------- Bảng thông báo ----------

    // Khuôn của NotificationMapBuilder.RenderEvent: `Entity — "Sự kiện" (khi điều kiện)`.
    function parseEvent(s) {
        const m = s.match(/^(?:([\s\S]*?) — )?"([\s\S]*?)"(?: \(khi ([\s\S]*)\))?$/);
        return m ? { entity: m[1] || "", event: m[2], trigger: m[3] || "", raw: s } : null;
    }

    function parseNotification(lines) {
        const rows = [];
        let silent = [], added = [];
        for (const raw of lines) {
            const line = raw.trim();
            if (!line) continue;
            if (line.startsWith("- ")) {
                const body = line.slice(2);
                const at = body.lastIndexOf(" ⇒ To: ");
                if (at < 0) return null;
                const ev = parseEvent(body.slice(0, at));
                if (!ev) return null;
                const recip = body.slice(at + " ⇒ To: ".length);
                const cc = recip.indexOf("; CC: ");
                ev.to = splitList(cc >= 0 ? recip.slice(0, cc) : recip, ", ");
                ev.cc = cc >= 0 ? splitList(recip.slice(cc + "; CC: ".length), ", ") : [];
                rows.push(ev);
            } else if (line.startsWith("Các sự kiện KHÔNG cần gửi thông báo: ")) {
                silent = splitList(line.slice("Các sự kiện KHÔNG cần gửi thông báo: ".length), "; ")
                    .map(s => parseEvent(s) || { entity: "", event: s, trigger: "", raw: s });
            } else if (line.startsWith("Các sự kiện/lời nhắc mình tự bổ sung vào bảng: ")) {
                added = splitList(line.slice("Các sự kiện/lời nhắc mình tự bổ sung vào bảng: ".length), "; ");
            } else {
                return null;
            }
        }
        const addedSet = new Set(added);
        rows.forEach(r => { r.added = addedSet.has(r.raw); });
        silent.forEach(r => { r.added = addedSet.has(r.raw); });
        return { rows, silent };
    }

    function eventCell(r) {
        return `${r.entity ? `<span class="tsub-entity">${esc(r.entity)}</span>` : ""}<b>${esc(r.event)}</b>${r.added
            ? ` <span class="tsub-badge">Tự thêm</span>` : ""}${r.trigger ? `<div class="tsub-sub">Khi ${esc(r.trigger)}</div>` : ""}`;
    }

    function renderNotification(data) {
        const meta = [`${data.rows.length} sự kiện gửi email`];
        if (data.silent.length) meta.push(`${data.silent.length} không gửi`);

        const rows = data.rows.map(r => `
            <tr>
                <td class="tsub-fn">${eventCell(r)}</td>
                <td class="tsub-who">
                    <div class="tsub-recip"><span class="tsub-recip-label">To</span>${r.to.map(t => `<span class="tsub-chip tsub-to">${esc(t)}</span>`).join("")}</div>
                    ${r.cc.length ? `<div class="tsub-recip"><span class="tsub-recip-label">CC</span>${r.cc.map(t => `<span class="tsub-chip">${esc(t)}</span>`).join("")}</div>` : ""}
                </td>
            </tr>`).join("");

        const body = (data.rows.length ? `
            <table class="tsub-table">
                <thead><tr><th>Sự kiện</th><th>Người nhận</th></tr></thead>
                <tbody>${rows}</tbody>
            </table>` : `<p class="tsub-note">Không sự kiện nào cần gửi email.</p>`)
            + (data.silent.length ? `
            <details class="tsub-group tsub-muted-group">
                <summary><span class="tsub-group-name">Không gửi thông báo</span><span class="tsub-group-meta">${data.silent.length} sự kiện</span></summary>
                <ul class="tsub-plain">${data.silent.map(r => `<li>${eventCell(r)}</li>`).join("")}</ul>
            </details>` : "");

        return frame("✉️", "Bảng thông báo đã chốt", meta, body, null);
    }

    // ---------- khung chung ----------

    function droppedBlock(label, items) {
        if (!items.length) return "";
        return `<div class="tsub-dropped"><span class="tsub-dropped-label">${esc(label)}:</span>${items
            .map(x => `<span class="tsub-chip tsub-struck">${esc(x)}</span>`).join("")}</div>`;
    }

    // `toggle`: null = không có nút; { expanded } = trạng thái mở ban đầu của các nhóm.
    function frame(icon, title, meta, body, toggle) {
        return `
            <div class="tsub-card">
                <div class="tsub-head">
                    <span class="tsub-icon" aria-hidden="true">${icon}</span>
                    <div class="tsub-title">
                        <b>${esc(title)}</b>
                        <span class="tsub-meta">${meta.join(" · ")}</span>
                    </div>
                    ${toggle ? `<button type="button" class="tsub-toggle" data-expanded="${toggle.expanded}">${toggle.expanded ? "Thu gọn" : "Mở tất cả"}</button>` : ""}
                </div>
                <div class="tsub-body">${body}</div>
            </div>`;
    }

    function build(text) {
        const lines = text.split(/\r?\n/);
        const head = lines.shift() || "";
        let data;
        if (head.startsWith(PREFIX.permission)) {
            data = parsePermission(lines);
            return data && renderPermission(data);
        }
        if (head.startsWith(PREFIX.report)) {
            data = parseReport(lines);
            return data && renderReport(data);
        }
        if (head.startsWith(PREFIX.screen)) {
            data = parseScreen(lines);
            return data && data.screens.length ? renderScreen(data) : null;
        }
        if (head.startsWith(PREFIX.notification)) {
            data = parseNotification(lines);
            return data && renderNotification(data);
        }
        return null;
    }

    // Dựng (hoặc dựng lại, sau "✎ sửa") thẻ cho một bong bóng người dùng. <p> vẫn ở nguyên chỗ — chỉ ẩn —
    // vì đó là nguồn ô sửa đọc lại.
    function enhance(bubble) {
        if (!bubble) return;
        const old = bubble.querySelector(":scope > .tsub-card");
        if (old) old.remove();
        bubble.classList.remove("has-table-card");

        const p = bubble.querySelector(":scope > p");
        if (!p) return;
        let html = null;
        try { html = build(p.textContent || ""); } catch (e) { console.error(e); }
        if (!html) return;

        p.insertAdjacentHTML("afterend", html);
        bubble.classList.add("has-table-card");
    }

    function enhanceAll(root) {
        (root || document).querySelectorAll(".req-msg.you").forEach(enhance);
    }

    document.addEventListener("click", function (e) {
        const btn = e.target.closest(".tsub-toggle");
        if (!btn) return;
        const card = btn.closest(".tsub-card");
        const expand = btn.dataset.expanded !== "true";
        card.querySelectorAll(".tsub-body > .tsub-group").forEach(d => { d.open = expand; });
        btn.dataset.expanded = String(expand);
        btn.textContent = expand ? "Thu gọn" : "Mở tất cả";
    });

    window.TableSubmissionCard = { enhance, enhanceAll, build };

    if (document.readyState === "loading")
        document.addEventListener("DOMContentLoaded", () => enhanceAll());
    else
        enhanceAll();
})();
