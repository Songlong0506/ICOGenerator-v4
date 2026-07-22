// Phân trang phía client cho các bảng Usage do site.js (BoschPager) đảm nhiệm qua [data-paginate];
// file này chỉ còn lo tooltip cho biểu đồ.

// Tooltip chi tiết cho biểu đồ "Tokens per month". Một hộp nổi duy nhất bám theo con trỏ, đọc dữ liệu
// từ các data-* mà server đã định dạng sẵn (tiền tệ / số) nên nội dung an toàn để gán bằng innerHTML.
(function () {
    var chart = document.querySelector('[data-month-chart]');
    if (!chart) { return; }

    var cols = Array.prototype.slice.call(chart.querySelectorAll('[data-col]'));
    if (!cols.length) { return; }

    var tip = document.createElement('div');
    tip.className = 'chart-tip';
    tip.setAttribute('role', 'tooltip');
    tip.hidden = true;
    document.body.appendChild(tip);

    function esc(s) {
        return String(s == null ? '' : s)
            .replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;');
    }

    function fill(col) {
        var d = col.dataset;
        tip.innerHTML =
            '<div class="chart-tip-title">' + esc(d.label) + '</div>' +
            '<div class="chart-tip-row"><i class="swatch-prompt"></i>' +
                '<span>Prompt</span><b>' + esc(d.prompt) + '</b></div>' +
            '<div class="chart-tip-row"><i class="swatch-completion"></i>' +
                '<span>Completion</span><b>' + esc(d.completion) + '</b></div>' +
            '<div class="chart-tip-total"><span>Total cost</span><b>' + esc(d.total) + '</b></div>' +
            '<div class="chart-tip-note">' + esc(d.tokens) + ' tokens · ' + esc(d.calls) + ' calls</div>';
    }

    function place(e) {
        var pad = 14;
        var w = tip.offsetWidth;
        var h = tip.offsetHeight;
        var x = e.clientX + pad;
        var y = e.clientY + pad;
        if (x + w > window.innerWidth - 8) { x = e.clientX - w - pad; }
        if (y + h > window.innerHeight - 8) { y = e.clientY - h - pad; }
        tip.style.left = Math.max(8, x) + 'px';
        tip.style.top = Math.max(8, y) + 'px';
    }

    cols.forEach(function (col) {
        col.addEventListener('mouseenter', function () { fill(col); tip.hidden = false; });
        col.addEventListener('mousemove', place);
        col.addEventListener('mouseleave', function () { tip.hidden = true; });
    });
})();

// Toggle bảng "Usage by department" giữa hai cách gom: theo Org Unit (mặc định) và theo Department.
// Cả hai bảng đã render sẵn từ server; nút chỉ đổi bảng nào hiển thị, nạp lại phân trang cho bảng vừa hiện
// và cập nhật nhãn nút thành cách gom sẽ chuyển sang.
(function () {
    var section = document.querySelector('[data-dept-usage]');
    if (!section) { return; }

    var btn = section.querySelector('[data-dept-toggle]');
    var panels = {
        orgunit: section.querySelector('[data-dept-panel="orgunit"]'),
        department: section.querySelector('[data-dept-panel="department"]')
    };
    if (!btn || !panels.orgunit || !panels.department) { return; }

    var labels = { orgunit: 'Xem theo Department', department: 'Xem theo Org Unit' };

    function show(mode) {
        var next = mode === 'department' ? 'department' : 'orgunit';
        panels.orgunit.hidden = next !== 'orgunit';
        panels.department.hidden = next !== 'department';
        btn.setAttribute('data-mode', next);
        btn.textContent = labels[next];
        // Bảng vừa hiện cần tính lại trang hiển thị (pager của nó dựng lúc còn ẩn).
        if (window.BoschPager) {
            window.BoschPager.refresh(panels[next]);
        }
    }

    btn.addEventListener('click', function () {
        show(btn.getAttribute('data-mode') === 'orgunit' ? 'department' : 'orgunit');
    });
})();
