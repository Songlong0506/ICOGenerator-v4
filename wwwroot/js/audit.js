// Mở modal chi tiết cho một dòng Audit Log. Dữ liệu được đọc từ các data-* attribute
// trên nút "Chi tiết" (Razor đã HTML-encode nên an toàn), rồi đổ vào modal dùng chung.
function openAuditDetail(btn) {
    const d = btn.dataset;

    const setBadge = (id, text, cls) => {
        const el = document.getElementById(id);
        el.textContent = text;
        el.className = "badge " + (cls || "");
    };

    setBadge("audit-detail-category", d.category, d.categoryClass);
    setBadge("audit-detail-action", d.action, d.actionClass);

    document.getElementById("audit-detail-summary").textContent = d.summary || "";
    document.getElementById("audit-detail-actor").textContent = d.actor || "—";
    document.getElementById("audit-detail-time").textContent = d.time || "—";
    document.getElementById("audit-detail-entity").textContent = d.entity || "—";

    const before = d.before || "";
    const after = d.after || "";
    const hasDiff = before !== "" || after !== "";

    const diff = document.getElementById("audit-detail-diff");
    if (hasDiff) {
        document.getElementById("audit-detail-before").textContent = before || "—";
        document.getElementById("audit-detail-after").textContent = after || "—";
        diff.classList.remove("hidden");
    } else {
        diff.classList.add("hidden");
    }

    openModal("auditDetail");
}

// Đóng modal khi bấm ra vùng nền tối bên ngoài.
document.getElementById("auditDetail")?.addEventListener("click", function (e) {
    if (e.target === this) closeModal("auditDetail");
});
