// ==== THẺ "CÁC LỰA CHỌN ĐÃ TÍCH" TRONG BONG BÓNG NGƯỜI DÙNG ====
// Lượt trả lời câu hỏi CHỌN NHIỀU ("Gửi các lựa chọn") là các chip đã tích nối bằng ", " — mà mỗi chip là
// cả một câu "<tên> — để <mục đích>", lại hay tự chứa dấu phẩy, nên ba lựa chọn đọc ra thành một dòng chạy
// dài không thấy đâu là ranh giới. File này dựng chúng thành một bảng ngay sau <p> của bong bóng rồi ẩn <p>.
//
// Chỉ đổi CÁCH HIỂN THỊ: <p> là nguồn duy nhất (ô "✎ sửa" cũng đọc nó), transcript và mọi tầng chắt lọc
// không đổi. Không nhận ra khuôn ⇒ không làm gì, bong bóng vẫn là chữ như trước — phía an toàn.
(function () {
    "use strict";

    function esc(s) {
        return String(s ?? "")
            .replace(/&/g, "&amp;").replace(/</g, "&lt;").replace(/>/g, "&gt;")
            .replace(/"/g, "&quot;").replace(/'/g, "&#39;");
    }

    // ---------- Các lựa chọn đã tích (trả lời câu hỏi chọn nhiều) ----------
    // Tin nhắn tự nó KHÔNG tách ngược được: chip chứa ", " thì ", " không còn là ranh giới. Nên bong bóng
    // chở theo danh sách chip của lượt BA đứng ngay trước (`data-choices`, JSON) và ở đây ăn dần tin nhắn
    // từ đầu, thử chip dài nhất trước — đúng thuật toán của UserTurnOrigin.IsMadeOfChips ở server. Phần
    // đuôi không khớp chip nào là lời người dùng tự gõ ở ô "Ý khác" (requirements.js nối nó vào cuối).
    const PICK_SEP = ", ";
    // Một lựa chọn thì bong bóng chữ đọc vẫn gọn; bảng chỉ đáng dựng khi có thứ để tách ra.
    const PICK_MIN_ROWS = 2;

    function parsePicks(text, choices) {
        const ordered = Array.from(new Set(choices.map(c => String(c ?? "").trim()).filter(Boolean)))
            .sort((a, b) => b.length - a.length);
        if (!ordered.length) return null;

        const picked = [];
        let rest = text.trim(), typed = "";
        while (rest) {
            const chip = ordered.find(c => rest === c || rest.startsWith(c + PICK_SEP));
            if (!chip) { typed = rest; break; }
            picked.push(chip);
            rest = rest === chip ? "" : rest.slice(chip.length + PICK_SEP.length);
        }
        // Không chip nào khớp ⇒ đây là một câu tự gõ (hoặc lượt đã bị "✎ sửa" thành câu khác): để nguyên chữ.
        if (!picked.length || picked.length + (typed ? 1 : 0) < PICK_MIN_ROWS) return null;

        // Thứ tự "không chọn" theo thứ tự chip trên màn hình, không theo độ dài.
        const skipped = choices.map(c => String(c ?? "").trim()).filter(c => c && !picked.includes(c));
        return { picked, typed, skipped: Array.from(new Set(skipped)), total: ordered.length };
    }

    // "<tên> — để <mục đích>": vế đầu là thứ được chọn, vế sau là lý do — tách ra để mắt dò được theo tên.
    function pickCell(s) {
        const i = s.indexOf(" — ");
        if (i <= 0) return `<b>${esc(s)}</b>`;
        return `<b>${esc(s.slice(0, i))}</b><div class="tsub-sub">${esc(s.slice(i + 3))}</div>`;
    }

    function renderPicks(data) {
        const meta = [`${data.picked.length} / ${data.total} gợi ý`];
        if (data.typed) meta.push("1 ý tự nhập");

        const rows = data.picked.map(pickCell);
        if (data.typed) rows.push(`<span class="tsub-badge">Ý khác</span> ${esc(data.typed)}`);

        const body = `
            <table class="tsub-table">
                <thead><tr><th>#</th><th>Lựa chọn · để làm gì</th></tr></thead>
                <tbody>${rows.map((cell, i) => `
                    <tr><td class="tsub-idx">${i + 1}</td><td>${cell}</td></tr>`).join("")}</tbody>
            </table>`
            + droppedBlock("Không chọn", data.skipped.map(s => s.split(" — ")[0]));

        return frame("☑️", "Các lựa chọn đã tích", meta, body, null);
    }

    function bubbleChoices(bubble) {
        try {
            const list = JSON.parse(bubble.dataset.choices || "null");
            return Array.isArray(list) ? list : null;
        } catch (e) { return null; }
    }

    // ---------- khung chung ----------

    function droppedBlock(label, items) {
        if (!items.length) return "";
        return `<div class="tsub-dropped"><span class="tsub-dropped-label">${esc(label)}:</span>${items
            .map(x => `<span class="tsub-chip tsub-struck">${esc(x)}</span>`).join("")}</div>`;
    }

    function frame(icon, title, meta, body) {
        return `
            <div class="tsub-card">
                <div class="tsub-head">
                    <span class="tsub-icon" aria-hidden="true">${icon}</span>
                    <div class="tsub-title">
                        <b>${esc(title)}</b>
                        <span class="tsub-meta">${meta.join(" · ")}</span>
                    </div>
                </div>
                <div class="tsub-body">${body}</div>
            </div>`;
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
        try {
            const choices = bubbleChoices(bubble);
            const picks = choices && parsePicks(p.textContent || "", choices);
            if (picks) html = renderPicks(picks);
        } catch (e) { console.error(e); }
        if (!html) return;

        p.insertAdjacentHTML("afterend", html);
        bubble.classList.add("has-table-card");
    }

    function enhanceAll(root) {
        (root || document).querySelectorAll(".req-msg.you").forEach(enhance);
    }

    window.PickedChoicesCard = { enhance, enhanceAll, parsePicks };

    if (document.readyState === "loading")
        document.addEventListener("DOMContentLoaded", () => enhanceAll());
    else
        enhanceAll();
})();
