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

// Bản đồ icon dùng chung cho feed hoạt động agent/workflow. Dùng Bootstrap Icons (font đã nạp ở
// _Layout) thay cho emoji để đồng bộ với phần Views và cho phép chỉnh màu/kích thước theo CSS
// (emoji render theo font hệ điều hành nên không kiểm soát được). site.js nạp ở mọi view nên cả
// agent-dashboard.js lẫn requirement-workflow.js đều dùng được helper này.
const EVENT_ICON_CLASS = {
    start: "bi-rocket-takeoff",
    setup: "bi-gear",
    thinking: "bi-lightbulb",
    tool: "bi-tools",
    observation: "bi-inbox",
    final: "bi-check-circle",
    completed: "bi-check-circle-fill",
    error: "bi-x-circle"
};

// Trả về markup <i> Bootstrap Icons cho một loại event; fallback là dấu chấm (bi-dot) tương đương '•'.
// Class name là hằng số nên an toàn để chèn qua innerHTML.
function eventIconHtml(kind) {
    const cls = EVENT_ICON_CLASS[kind] || "bi-dot";
    return `<i class="bi ${cls}" aria-hidden="true"></i>`;
}

// Khung app dùng chung (_Layout): thu/mở sidebar và các modal cấp shell (user, imprint).
// Nạp ở mọi view qua _Layout nên chỉ gắn handler khi phần tử tương ứng tồn tại.
(function () {
    var shell = document.getElementById('appShell');
    var toggle = document.getElementById('sbToggle');
    if (toggle) toggle.addEventListener('click', function () { shell.classList.toggle('collapsed'); });

    function open(id) { var m = document.getElementById(id); if (m) m.classList.add('open'); }
    var u = document.getElementById('navUser'); if (u) u.addEventListener('click', function () { open('userModal'); });
    var i = document.getElementById('navImprint'); if (i) i.addEventListener('click', function () { open('imprintModal'); });
    document.querySelectorAll('[data-close]').forEach(function (b) {
        b.addEventListener('click', function () { var m = document.getElementById(b.getAttribute('data-close')); if (m) m.classList.remove('open'); });
    });
    document.querySelectorAll('.shell-modal').forEach(function (o) {
        o.addEventListener('click', function (e) { if (e.target === o) o.classList.remove('open'); });
    });
})();
