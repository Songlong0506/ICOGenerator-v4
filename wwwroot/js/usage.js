// Phân trang phía client cho các bảng Usage (giữ nguyên style pager của trang Projects).
// Tất cả dòng vẫn nằm trong DOM nên phần trăm "Share" (tính theo tổng của mọi dòng) không bị lệch;
// chỉ ẩn/hiện dòng theo trang hiện tại.
(function () {
    function initPager(container) {
        var pageSize = parseInt(container.getAttribute('data-page-size'), 10) || 10;
        var noun = container.getAttribute('data-noun') || 'rows';
        var tbody = container.querySelector('tbody');
        var summary = container.querySelector('[data-pager-summary]');
        var controls = container.querySelector('[data-pager-controls]');
        if (!tbody || !summary || !controls) return;

        var rows = Array.prototype.slice.call(tbody.children);
        var total = rows.length;
        var totalPages = Math.max(1, Math.ceil(total / pageSize));
        var page = 1;

        function button(label, opts) {
            opts = opts || {};
            var el = document.createElement(opts.disabled || opts.current ? 'span' : 'button');
            if (el.tagName === 'BUTTON') { el.type = 'button'; }
            el.className = 'btn' + (opts.className ? ' ' + opts.className : '');
            if (opts.disabled) { el.className += ' disabled'; }
            // opts.html = true khi label chứa markup icon (Bootstrap Icons). Chỉ dùng cho chuỗi
            // hằng số trong file này nên an toàn với innerHTML; nhãn số trang vẫn dùng textContent.
            if (opts.html) { el.innerHTML = label; } else { el.textContent = label; }
            if (!opts.disabled && !opts.current && typeof opts.onClick === 'function') {
                el.addEventListener('click', opts.onClick);
            }
            return el;
        }

        function goTo(p) {
            page = Math.min(Math.max(1, p), totalPages);
            render();
        }

        function render() {
            var start = (page - 1) * pageSize;
            var end = Math.min(start + pageSize, total);
            rows.forEach(function (row, i) {
                row.style.display = (i >= start && i < end) ? '' : 'none';
            });

            var first = total === 0 ? 0 : start + 1;
            summary.textContent = 'Showing ' + first + ' to ' + end + ' of ' + total + ' ' + noun;

            controls.innerHTML = '';
            if (totalPages <= 1) { return; }

            controls.appendChild(button('<i class="bi bi-chevron-left" aria-hidden="true"></i> Prev', {
                html: true,
                className: 'outline',
                disabled: page === 1,
                onClick: function () { goTo(page - 1); }
            }));

            for (var i = 1; i <= totalPages; i++) {
                (function (p) {
                    controls.appendChild(button(String(p), {
                        className: p === page ? 'primary' : '',
                        current: p === page,
                        onClick: function () { goTo(p); }
                    }));
                })(i);
            }

            controls.appendChild(button('Next <i class="bi bi-chevron-right" aria-hidden="true"></i>', {
                html: true,
                className: 'outline',
                disabled: page === totalPages,
                onClick: function () { goTo(page + 1); }
            }));
        }

        render();
    }

    Array.prototype.slice.call(document.querySelectorAll('[data-paginate]')).forEach(initPager);
})();

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
