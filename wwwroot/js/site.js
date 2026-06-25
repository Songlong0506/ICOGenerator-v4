function openModal(id){document.getElementById(id)?.classList.remove('hidden')}
function closeModal(id){document.getElementById(id)?.classList.add('hidden')}

// Escape HTML dùng chung cho mọi trang (site.js được nạp ở mọi view qua _Layout).
// Escape cả dấu nháy nên an toàn cho cả nội dung phần tử lẫn giá trị attribute.
function escapeHtml(value) {
    return String(value ?? "")
        .replaceAll("&", "&amp;")
        .replaceAll("<", "&lt;")
        .replaceAll(">", "&gt;")
        .replaceAll('"', "&quot;")
        .replaceAll("'", "&#039;");
}
