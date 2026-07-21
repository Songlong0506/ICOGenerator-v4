/* Command bar — hành vi client-side dùng chung cho mẫu Bosch (_CommandBar.cshtml).
 *
 * Mỗi <div class="command-bar" data-cbar> có thể trỏ tới một bảng/danh sách qua
 * data-cbar-target (CSS selector). Trên phần tử đó, thanh cung cấp:
 *   • tìm kiếm  — [data-cbar-search]  lọc hàng theo văn bản
 *   • sắp xếp   — [data-cbar-sort]    sắp xếp hàng theo cột / data-key
 * Ngoài ra, độc lập với target:
 *   • nút Filter — [data-cbar-filter-toggle="#panel"] bật/tắt panel lọc server-side
 *   • menu thả   — [data-cbar-menu] (Sort, overflow ⋮) mở/đóng, đóng khi bấm ra ngoài
 *
 * Hàng dữ liệu = <tbody> <tr> (bỏ qua tr có data-cbar-skip) với bảng, hoặc
 * [data-cbar-item] / phần tử con trực tiếp với danh sách. Không đụng tới bộ lọc
 * server: search/sort chỉ tác động lên các hàng đang render ở client.
 */
(function () {
    'use strict';

    function rowsOf(target) {
        if (!target) return [];
        if (target.tagName === 'TABLE') {
            var tbody = target.tBodies && target.tBodies[0] ? target.tBodies[0] : target;
            return Array.prototype.filter.call(
                tbody.querySelectorAll(':scope > tr'),
                function (tr) { return !tr.hasAttribute('data-cbar-skip') && !tr.classList.contains('cbar-empty-row'); });
        }
        var tagged = target.querySelectorAll(':scope [data-cbar-item], :scope > [data-cbar-item]');
        if (tagged.length) return Array.prototype.slice.call(tagged);
        return Array.prototype.filter.call(target.children, function (el) {
            return !el.classList.contains('cbar-empty-row') && !el.hasAttribute('data-cbar-skip');
        });
    }

    function cellText(row, col, key) {
        if (key) {
            var keyed = row.matches('[data-' + key + ']') ? row : row.querySelector('[data-' + key + ']');
            if (keyed) return (keyed.getAttribute('data-' + key) || keyed.textContent || '').trim();
        }
        if (row.tagName === 'TR') {
            var cell = row.cells && row.cells[col];
            return cell ? (cell.getAttribute('data-sort') || cell.textContent || '').trim() : '';
        }
        return (row.textContent || '').trim();
    }

    function ensureEmptyRow(target) {
        var existing = target.querySelector('.cbar-empty-row');
        if (existing) return existing;
        var node;
        if (target.tagName === 'TABLE') {
            var tbody = target.tBodies && target.tBodies[0] ? target.tBodies[0] : target;
            var cols = 1;
            var headRow = target.tHead ? target.tHead.rows[0] : null;
            if (headRow) cols = headRow.cells.length;
            node = document.createElement('tr');
            node.className = 'cbar-empty-row';
            var td = document.createElement('td');
            td.colSpan = cols;
            td.style.textAlign = 'center';
            td.style.color = 'var(--muted)';
            td.style.padding = '24px';
            td.textContent = 'Không tìm thấy kết quả nào.';
            node.appendChild(td);
            tbody.appendChild(node);
        } else {
            node = document.createElement('div');
            node.className = 'cbar-empty-row';
            node.style.textAlign = 'center';
            node.style.color = 'var(--muted)';
            node.style.padding = '24px';
            node.textContent = 'Không tìm thấy kết quả nào.';
            target.appendChild(node);
        }
        return node;
    }

    function initBar(bar) {
        var selector = bar.getAttribute('data-cbar-target');
        var target = selector ? document.querySelector(selector) : null;

        function applyFilter(term) {
            if (!target) return;
            term = (term || '').trim().toLowerCase();
            var rows = rowsOf(target);
            var visible = 0;
            rows.forEach(function (row) {
                var match = !term || (row.textContent || '').toLowerCase().indexOf(term) !== -1;
                row.style.display = match ? '' : 'none';
                if (match) visible++;
            });

            // Dòng "không có kết quả" chỉ khi đang tìm mà rỗng.
            var empty = target.querySelector('.cbar-empty-row');
            if (term && visible === 0) {
                empty = empty || ensureEmptyRow(target);
                empty.classList.add('show');
            } else if (empty) {
                empty.classList.remove('show');
            }
        }

        // ---- Tìm kiếm ----
        var search = bar.querySelector('[data-cbar-search]');
        if (search && target) {
            search.addEventListener('input', function () { applyFilter(search.value); });
        }

        // ---- Sắp xếp ----
        bar.querySelectorAll('[data-cbar-sort]').forEach(function (item) {
            item.addEventListener('click', function () {
                if (!target) return;
                var col = parseInt(item.getAttribute('data-col') || '0', 10);
                var key = item.getAttribute('data-key') || '';
                var dir = item.getAttribute('data-dir') === 'desc' ? -1 : 1;
                var numeric = item.getAttribute('data-numeric') === '1';

                var rows = rowsOf(target);
                rows.sort(function (a, b) {
                    var av = cellText(a, col, key), bv = cellText(b, col, key);
                    if (numeric) {
                        var an = parseFloat(av.replace(/[^0-9.\-]/g, '')) || 0;
                        var bn = parseFloat(bv.replace(/[^0-9.\-]/g, '')) || 0;
                        return (an - bn) * dir;
                    }
                    return av.localeCompare(bv, undefined, { sensitivity: 'base', numeric: true }) * dir;
                });

                var parent = rows.length ? rows[0].parentNode : null;
                if (parent) {
                    rows.forEach(function (r) { parent.appendChild(r); });
                    var empty = parent.querySelector('.cbar-empty-row');
                    if (empty) parent.appendChild(empty);
                }

                // Đánh dấu tuỳ chọn đang chọn trong menu.
                var menu = item.closest('.cbar-menu');
                if (menu) menu.querySelectorAll('[data-cbar-sort]').forEach(function (i) { i.classList.toggle('active', i === item); });
                closeMenu(item.closest('[data-cbar-menu]'));
            });
        });
    }

    // ---- Nút Filter: bật/tắt panel lọc server-side ----
    function initFilterToggles(root) {
        root.querySelectorAll('[data-cbar-filter-toggle]').forEach(function (btn) {
            var panel = document.querySelector(btn.getAttribute('data-cbar-filter-toggle'));
            if (!panel) return;
            // Trạng thái đầu: nếu nút không .open thì ẩn panel.
            if (!btn.classList.contains('open')) panel.hidden = true;
            btn.addEventListener('click', function () {
                var open = btn.classList.toggle('open');
                btn.setAttribute('aria-expanded', open ? 'true' : 'false');
                panel.hidden = !open;
            });
        });
    }

    // ---- Menu thả (Sort, overflow) ----
    function closeMenu(wrap) {
        if (!wrap) return;
        var btn = wrap.querySelector('[data-cbar-menu-toggle]');
        var menu = wrap.querySelector('.cbar-menu');
        if (btn) btn.setAttribute('aria-expanded', 'false');
        if (btn) btn.classList.remove('open');
        if (menu) menu.hidden = true;
    }

    function initMenus(root) {
        root.querySelectorAll('[data-cbar-menu]').forEach(function (wrap) {
            var btn = wrap.querySelector('[data-cbar-menu-toggle]');
            var menu = wrap.querySelector('.cbar-menu');
            if (!btn || !menu) return;
            btn.addEventListener('click', function (e) {
                e.stopPropagation();
                var willOpen = menu.hidden;
                // Đóng mọi menu khác trước.
                document.querySelectorAll('.command-bar [data-cbar-menu]').forEach(closeMenu);
                if (willOpen) {
                    menu.hidden = false;
                    btn.setAttribute('aria-expanded', 'true');
                    btn.classList.add('open');
                }
            });
        });
    }

    function init() {
        document.querySelectorAll('.command-bar[data-cbar]').forEach(initBar);
        initFilterToggles(document);
        initMenus(document);

        // Đóng menu khi bấm ra ngoài hoặc nhấn Escape.
        document.addEventListener('click', function () {
            document.querySelectorAll('.command-bar [data-cbar-menu]').forEach(closeMenu);
        });
        document.addEventListener('keydown', function (e) {
            if (e.key === 'Escape') document.querySelectorAll('.command-bar [data-cbar-menu]').forEach(closeMenu);
        });
    }

    if (document.readyState === 'loading') {
        document.addEventListener('DOMContentLoaded', init);
    } else {
        init();
    }
})();
