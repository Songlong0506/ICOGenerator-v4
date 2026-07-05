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
