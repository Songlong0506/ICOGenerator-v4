// poc-notes.js — BẢNG GHI CHÚ dùng chung cho POC Review (poc-review.js) và trang khách link chia sẻ
// (poc-share.js): bộ lọc Status/Version, bảng, popup "Add Note". Markup ở Views/Shared/_PocNotesTable.cshtml.
//
// Vì sao một module chung chứ không chép vào hai trang: hai trang phải nói CÙNG một điều về cùng một ghi
// chú (người gửi link và người nhận link nhìn chung một bảng). Hai bản sao thì bản thứ hai không bao giờ
// được sửa cùng bản thứ nhất — đúng chuyện đã xảy ra với danh sách thẻ cũ của trang khách.
//
// Module CHỈ lo hiển thị + nhập liệu. Mọi thao tác ghi (thêm/rút/mở lại) và mọi thứ dính tới iframe (pin,
// chế độ ghim) do trang gọi tự làm qua các callback — module không biết URL nào, không gọi fetch.
(function () {
    "use strict";

    // Ba trạng thái người dùng thấy (server tính sẵn — PocNoteDisplayStatus). Thứ tự = thứ tự trong bộ lọc.
    const DISPLAY_STATUSES = ["Draft", "Waiting", "Apply"];

    // Chi tiết đường đi của ghi chú — tooltip của ô Status. Ba trạng thái là đủ cho câu hỏi "đến đâu rồi",
    // nhưng người hỏi tiếp "ai đang cầm nó" cần chữ này, và nó nằm ngay trên ô họ đang trỏ vào.
    function statusDetail(c) {
        if (c.target === "Brief") {
            return c.displayStatus === "Apply"
                ? "Bản Product Brief chứa góp ý này đã được duyệt"
                : "BA đang sửa Product Brief theo góp ý này";
        }
        switch (c.status) {
            case "Open":
                return c.wasDispatched
                    ? "Đã sửa nhưng bạn báo vẫn chưa đạt — chờ gửi lại cho đội xử lý"
                    : "Chưa gửi cho đội xử lý";
            case "Sent": return "Dev đang sửa bản demo theo ghi chú này";
            case "Deferred": return "Chờ bản demo dựng lại sau khi requirement được duyệt lại — vẫn xoá được";
            case "RoutedToRequirement": return "BA đang sửa tài liệu yêu cầu — áp dụng khi bản demo dựng lại";
            case "Addressed":
                return c.route === "Requirement"
                    ? "Bản demo đã dựng lại từ tài liệu đã sửa — mời kiểm lại"
                    : "Dev đã sửa bản demo — mời kiểm lại";
            default: return "";
        }
    }

    // Neo của ghi chú viết cho NGƯỜI đọc. ElementLabel do annotator sinh có dạng `<loại> “<chữ>”`; với
    // phần tử không có tên thân thiện (main/div/section…) loại là tên thẻ HTML thô — vô nghĩa với người
    // xem demo, nên chỉ giữ phần chữ. Phần tử chứa nhiều khối con thì chữ bị dính liền nhau ("Locker
    // DashboardVIEW AS"): tách lại ở chỗ chữ thường dính chữ hoa — CHỈ cho loại thẻ thô, vì nhãn nút/ô
    // nhập là một chuỗi gốc, tách là làm sai nó. Nhãn nguyên văn vẫn nằm trong tooltip (cùng selector).
    function prettyAnchor(label) {
        label = (label || "").trim();
        if (!label) return "";
        const m = /^(<[a-z0-9-]+>)\s*“([\s\S]*)”$/.exec(label);
        if (m) {
            // \p{Ll}/\p{Lu} chứ không phải dải À-Ỹ: dải đó lẫn cả chữ thường có dấu ("ì", "ạ"), tách theo
            // nó là băm nát tiếng Việt ("t ình tr ạng").
            const text = m[2]
                .replace(/([\p{Ll}\d])(\p{Lu})/gu, "$1 $2")
                .replace(/(\p{Lu})(\p{Lu}\p{Ll})/gu, "$1 $2");
            return `“${text}”`;
        }
        if (/^<[a-z0-9-]+>$/.test(label)) return "Vị trí trên trang";
        return label;
    }

    // "20/11 08:52" — bỏ năm khi là năm hiện tại: buổi review gần như luôn trong năm nay.
    function formatTime(value) {
        const d = new Date(value);
        if (isNaN(d)) return "";
        const p2 = n => String(n).padStart(2, "0");
        const sameYear = d.getFullYear() === new Date().getFullYear();
        return `${p2(d.getDate())}/${p2(d.getMonth() + 1)}${sameYear ? "" : "/" + d.getFullYear()} ${p2(d.getHours())}:${p2(d.getMinutes())}`;
    }

    // Bản mới nhất lên đầu bộ lọc Version: draft (bản đang soạn) > V{n} lớn nhất > … > không rõ.
    function versionSortKey(v) {
        if (v === "draft") return Number.MAX_SAFE_INTEGER;
        const m = /^V(\d+)$/.exec(v || "");
        return m ? Number(m[1]) : -1;
    }

    // Ghi chú này có pin vẽ trên bản demo không: chỉ ghi chú POC neo vào MỘT phần tử. Ghi chú gõ qua popup
    // (chỉ có màn hình) và ghi chú Brief không có chỗ nào để vẽ — vẽ chúng ở toạ độ 0,0 là chồng một cụm
    // pin lên góc header của bản demo.
    function hasPin(c) {
        return c.target !== "Brief" && !!c.elementPath;
    }

    /**
     * @param {object} opts
     *  root        — phần tử bọc partial _PocNotesTable (.poc-notes-table-root)
     *  guest       — trang khách: không có cột Action, popup đòi tên người góp ý
     *  locked      — đã nghiệm thu: không thêm/xoá/mở lại (server vẫn là chỗ chặn thật)
     *  onSubmit(payload)   → Promise<item>; ném Error(message) để hiện lỗi ngay trong popup
     *  onWithdraw(item)    → Promise<boolean> (true = đã rút, bảng bỏ dòng)
     *  onReopen(item)      → Promise<boolean>
     *  onFocus(item)       — bấm một dòng có pin ⇒ trang nháy pin trong bản demo
     *  onChange(numbered)  — sau mỗi lần dữ liệu đổi (trang đẩy pin + cập nhật nút gửi)
     */
    function create(opts) {
        const root = opts.root;
        const guest = !!opts.guest;
        const locked = !!opts.locked;

        const bodyEl = root.querySelector("[data-notes-body]");
        const emptyEl = root.querySelector("[data-notes-empty]");
        const countEl = root.querySelector("[data-notes-count]");
        const statusFilter = root.querySelector("[data-notes-filter-status]");
        const versionFilter = root.querySelector("[data-notes-filter-version]");
        const addBtn = root.querySelector("[data-notes-add]");

        const modal = root.querySelector("[data-notes-modal]");
        const form = modal.querySelector("form");
        const modalTitle = modal.querySelector("[data-notes-modal-title]");
        const targetRow = modal.querySelector("[data-notes-target]");
        const targetText = modal.querySelector("[data-notes-target-text]");
        const screenField = modal.querySelector("[data-notes-screen-field]");
        const screenSelect = modal.querySelector("[data-notes-screen]");
        const nameInput = modal.querySelector("[data-notes-guest-name]");
        const textInput = modal.querySelector("[data-notes-text]");
        const msgEl = modal.querySelector("[data-notes-msg]");
        const saveBtn = modal.querySelector("[data-notes-save]");

        let items = [];
        let pendingPick = null;

        // Số # = thứ tự tạo trên TOÀN bảng, không đánh lại theo bộ lọc: nó là định danh chung với pin vẽ trên
        // bản demo, phải đứng yên khi người dùng lọc hay khi một ghi chú đổi trạng thái.
        function numbered() {
            return items.map((c, i) => Object.assign({}, c, { index: i + 1 }));
        }

        function changed() {
            render();
            if (opts.onChange) opts.onChange(numbered());
        }

        function refreshVersionOptions() {
            const current = versionFilter.value;
            const versions = Array.from(new Set(items.map(c => c.briefVersion || "")))
                .sort((a, b) => versionSortKey(b) - versionSortKey(a));
            const html = ['<option value="">Tất cả</option>']
                .concat(versions.map(v => `<option value="${escapeHtml(v)}">${escapeHtml(v || "Không rõ")}</option>`))
                .join("");
            // Chỉ dựng lại khi tập version đổi: thay options mỗi lần render làm dropdown.js dựng lại panel
            // ngay dưới tay người đang mở nó.
            if (versionFilter.dataset.versions !== versions.join("|")) {
                versionFilter.innerHTML = html;
                versionFilter.dataset.versions = versions.join("|");
            }
            versionFilter.value = versions.includes(current) ? current : "";
        }

        function matches(c) {
            const s = statusFilter.value;
            const v = versionFilter.value;
            return (!s || c.displayStatus === s) && (!v || (c.briefVersion || "") === v);
        }

        function screenCell(c) {
            if (c.target === "Brief") return "Product Brief";
            return c.pageView || "Chung";
        }

        function itemCell(c) {
            if (c.target === "Brief") {
                return c.quote
                    ? `<span class="poc-note-quote">“${escapeHtml(c.quote)}”</span>`
                    : '<span class="muted">Cả bản mô tả</span>';
            }
            const anchor = prettyAnchor(c.elementLabel);
            if (!anchor) return '<span class="muted">—</span>';
            const tooltip = [c.elementLabel, c.elementPath].filter(Boolean).join("\n");
            return `<span class="poc-note-anchor" title="${escapeHtml(tooltip)}">${escapeHtml(anchor)}</span>`;
        }

        function actionCell(c) {
            if (locked) return "";
            const buttons = [];
            // 🗑 chỉ ở dòng chưa tới tay agent nào (server tính CanWithdraw: Draft, hoặc Waiting còn hoãn).
            if (c.canWithdraw) {
                buttons.push(`<button type="button" class="btn outline small danger poc-note-del" data-id="${c.id}"
                    title="Xoá ghi chú này" aria-label="Xoá ghi chú"><i class="bi bi-trash" aria-hidden="true"></i> Delete</button>`);
            }
            // "Vẫn chưa đạt": đường duy nhất để nói một ghi chú đã áp mà chưa đúng ý — đưa nó về Draft để
            // lượt gửi sau mang đi, thay vì gõ lại một ghi chú trùng nội dung.
            if (c.target !== "Brief" && c.status === "Addressed") {
                buttons.push(`<button type="button" class="btn outline small poc-note-reopen" data-id="${c.id}"
                    title="Bản demo vẫn chưa đúng chỗ này — đưa ghi chú về Draft để gửi lại">✗ Vẫn chưa đạt</button>`);
            }
            return buttons.join("");
        }

        function rowHtml(c) {
            const pin = hasPin(c);
            return `
                <tr data-id="${c.id}" class="${pin ? "has-pin" : ""}"${pin ? ' title="Bấm để xem chỗ này trên bản demo"' : ""}>
                    <td class="poc-note-no"><span class="poc-pin-no${pin ? "" : " no-pin"}">${c.index}</span></td>
                    <td class="poc-note-version">${escapeHtml(c.briefVersion || "—")}</td>
                    <td class="poc-note-screen">${escapeHtml(screenCell(c))}</td>
                    <td class="poc-note-item">${itemCell(c)}</td>
                    <td class="poc-note-text">${escapeHtml(c.comment)}</td>
                    <td class="poc-note-date">${escapeHtml(formatTime(c.createdAt))}</td>
                    <td class="poc-note-user">${escapeHtml(c.createdBy || "?")}</td>
                    <td class="poc-note-status"><span class="poc-status ${(c.displayStatus || "").toLowerCase()}" title="${escapeHtml(statusDetail(c))}">${escapeHtml(c.displayStatus || "")}</span></td>
                    ${guest ? "" : `<td class="poc-note-actions">${actionCell(c)}</td>`}
                </tr>`;
        }

        function render() {
            refreshVersionOptions();
            const all = numbered();
            const shown = all.filter(matches);
            const filtered = shown.length !== all.length;

            countEl.textContent = all.length ? (filtered ? `(${shown.length}/${all.length})` : `(${all.length})`) : "";
            bodyEl.innerHTML = shown.map(rowHtml).join("");

            if (!all.length) {
                emptyEl.textContent = locked
                    ? "Chưa có ghi chú nào."
                    : "Chưa có ghi chú nào. Bấm Add Note, hoặc bật chế độ ghim ở góc trên khung demo rồi bấm vào chỗ chưa đúng.";
            } else if (!shown.length) {
                emptyEl.textContent = "Không có ghi chú nào khớp bộ lọc.";
            }
            emptyEl.hidden = shown.length > 0;
            bodyEl.closest("table").hidden = shown.length === 0;
        }

        // ===== Popup Add Note =====
        // MỘT popup cho cả hai đường ghi chú: bấm "Add Note" (chọn màn hình, hoặc "Chung") và ghim một phần
        // tử trên bản demo (màn hình + phần tử đã biết, chỉ còn gõ nội dung).

        function ensureScreenOption(view) {
            if (!view) return;
            if (Array.from(screenSelect.options).some(o => o.value === view)) return;
            const opt = document.createElement("option");
            opt.value = view;
            opt.textContent = view;
            screenSelect.appendChild(opt);
        }

        function showMsg(text) {
            msgEl.textContent = text || "";
            msgEl.hidden = !text;
        }

        function openAdd(pick, currentView) {
            if (locked) return;
            pendingPick = pick || null;
            showMsg("");
            textInput.value = "";

            if (pendingPick) {
                modalTitle.textContent = "Ghi chú cho phần tử";
                const where = pendingPick.pageView ? `${pendingPick.pageView} · ` : "";
                targetText.textContent = where + (prettyAnchor(pendingPick.elementLabel) || "Vị trí trên trang");
                targetRow.hidden = false;
                screenField.hidden = true;
            } else {
                modalTitle.textContent = "Add Note";
                targetRow.hidden = true;
                screenField.hidden = false;
                // Mặc định là màn hình ĐANG MỞ trong bản demo: người dùng thường gõ về đúng chỗ đang nhìn.
                ensureScreenOption(currentView);
                screenSelect.value = currentView && Array.from(screenSelect.options).some(o => o.value === currentView)
                    ? currentView
                    : "";
            }

            modal.classList.remove("hidden");
            const first = nameInput && !nameInput.value.trim() ? nameInput : textInput;
            first.focus();
        }

        function closeAdd() {
            pendingPick = null;
            modal.classList.add("hidden");
        }

        form.addEventListener("submit", async function (e) {
            e.preventDefault();
            const comment = textInput.value.trim();
            const guestName = nameInput ? nameInput.value.trim() : "";
            if (nameInput && !guestName) { nameInput.focus(); return; }
            if (!comment) { textInput.focus(); return; }

            const pick = pendingPick;
            const payload = pick
                ? {
                    pageView: pick.pageView || "",
                    elementLabel: pick.elementLabel || "",
                    elementPath: pick.elementPath || "",
                    xPercent: pick.xPercent || 0,
                    yPercent: pick.yPercent || 0,
                    comment, guestName
                }
                : { pageView: screenSelect.value || "", elementLabel: "", elementPath: "", xPercent: 0, yPercent: 0, comment, guestName };

            saveBtn.disabled = true;
            try {
                const item = await opts.onSubmit(payload);
                items.push(item);
                closeAdd();
                changed();
            } catch (err) {
                showMsg(err && err.message ? err.message : "Không lưu được ghi chú — thử lại sau.");
            } finally {
                saveBtn.disabled = false;
            }
        });

        modal.querySelectorAll("[data-notes-cancel]").forEach(b => b.addEventListener("click", closeAdd));
        modal.addEventListener("click", e => { if (e.target === modal) closeAdd(); });
        document.addEventListener("keydown", e => {
            if (e.key === "Escape" && !modal.classList.contains("hidden")) closeAdd();
        });

        if (addBtn) {
            if (locked) {
                addBtn.disabled = true;
                addBtn.title = "Bản demo đã được nghiệm thu — ghi chú đang khoá";
            }
            addBtn.addEventListener("click", () => openAdd(null, opts.currentView ? opts.currentView() : ""));
        }

        statusFilter.addEventListener("change", render);
        versionFilter.addEventListener("change", render);

        // ===== Thao tác trên dòng =====
        bodyEl.addEventListener("click", async function (e) {
            const del = e.target.closest(".poc-note-del");
            if (del) {
                const item = items.find(c => c.id === del.dataset.id);
                if (item && opts.onWithdraw && await opts.onWithdraw(item)) {
                    items = items.filter(c => c.id !== item.id);
                    changed();
                }
                return;
            }

            const reopen = e.target.closest(".poc-note-reopen");
            if (reopen) {
                const item = items.find(c => c.id === reopen.dataset.id);
                if (item && opts.onReopen && await opts.onReopen(item)) {
                    Object.assign(item, { status: "Open", displayStatus: "Draft", route: null, canWithdraw: true });
                    changed();
                }
                return;
            }

            const row = e.target.closest("tr[data-id]");
            if (row && row.classList.contains("has-pin") && opts.onFocus) {
                const item = numbered().find(c => c.id === row.dataset.id);
                if (item) opts.onFocus(item);
            }
        });

        // Bấm pin trên bản demo ⇒ nháy đúng dòng. Dòng đang bị bộ lọc giấu thì bỏ lọc trước — nháy một dòng
        // không có trên màn hình là cú bấm rơi vào khoảng không.
        function highlight(id) {
            let row = bodyEl.querySelector(`tr[data-id="${id}"]`);
            if (!row && items.some(c => c.id === id)) {
                statusFilter.value = "";
                versionFilter.value = "";
                render();
                row = bodyEl.querySelector(`tr[data-id="${id}"]`);
            }
            if (!row) return;
            row.scrollIntoView({ block: "nearest", behavior: "smooth" });
            row.classList.add("highlight");
            setTimeout(() => row.classList.remove("highlight"), 1600);
        }

        render();

        return {
            setItems(list) { items = Array.isArray(list) ? list.slice() : []; changed(); },
            items: () => items.slice(),
            numbered,
            pins: () => numbered().filter(hasPin),
            openAdd,
            highlight
        };
    }

    window.PocNotes = { create, prettyAnchor, formatTime, DISPLAY_STATUSES };
})();
