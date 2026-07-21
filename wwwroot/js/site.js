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
    "poc-screen": "bi-window-stack",
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

    // Nhóm sidebar: mở/gập khi bấm header + nhớ trạng thái theo từng nhóm (localStorage).
    // Nếu nhóm đang chứa màn hình active thì luôn để mở, không cho trạng thái đã lưu ghi đè
    // (nếu không, item của trang hiện tại sẽ bị ẩn). Nhóm 1-con (--single) không có header nên bỏ qua.
    document.querySelectorAll('.nav-group').forEach(function (group) {
        var head = group.querySelector('.nav-group-head');
        if (!head) return;
        var key = 'nav-group:' + (group.getAttribute('data-group') || '');
        var hasActive = !!group.querySelector('.nav-item.active');
        if (!hasActive) {
            try {
                var saved = localStorage.getItem(key);
                if (saved === 'open') group.classList.add('open');
                else if (saved === 'closed') group.classList.remove('open');
            } catch (e) { /* localStorage không khả dụng: dùng trạng thái server render */ }
        }
        head.setAttribute('aria-expanded', group.classList.contains('open') ? 'true' : 'false');
        head.addEventListener('click', function () {
            var open = group.classList.toggle('open');
            head.setAttribute('aria-expanded', open ? 'true' : 'false');
            try { localStorage.setItem(key, open ? 'open' : 'closed'); } catch (e) { /* bỏ qua */ }
        });
    });

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

/* ============================================================
   Bosch standard table pager (client-side).
   Enhances any element marked [data-paginate] that wraps a <table>:
     • adds an "Elements per page" selector (data-page-sizes, default 10,50,100)
     • adds a numbered page strip (chevron ‹ ›, round buttons, active = blue circle)
   Rows stay in the DOM; only the current page is shown, so command-bar
   search/sort (which reorder or hide rows) keep working. site.js is loaded on
   every view, so tagging a table with [data-paginate] is all a screen needs.

   Integration: command-bar.js marks filter-hidden rows with .cbar-filtered and
   calls window.BoschPager.refresh(target) after search/sort so the visible page
   is recomputed from the rows that currently pass the filter.
   ============================================================ */
(function () {
    'use strict';

    var instances = [];

    function pageList(current, total) {
        var pages = [];
        if (total <= 7) {
            for (var i = 1; i <= total; i++) { pages.push(i); }
            return pages;
        }
        var start = Math.max(2, current - 1);
        var end = Math.min(total - 1, current + 1);
        if (current <= 4) { start = 2; end = 5; }
        if (current >= total - 3) { start = total - 4; end = total - 1; }
        pages.push(1);
        if (start > 2) { pages.push('...'); }
        for (var p = start; p <= end; p++) { pages.push(p); }
        if (end < total - 1) { pages.push('...'); }
        pages.push(total);
        return pages;
    }

    function Pager(container) {
        // [data-paginate] may sit on a wrapping element or directly on the <table>.
        var isTable = container.tagName === 'TABLE';
        var table = isTable ? container : container.querySelector('table');
        var tbody = table && (table.tBodies[0] || table.querySelector('tbody'));
        if (!tbody) { return null; }

        this.container = container;
        this.table = table;
        this.tbody = tbody;
        this.pageSize = parseInt(container.getAttribute('data-page-size'), 10) || 10;
        this.noun = container.getAttribute('data-noun') || '';
        this.page = 1;

        var sizesAttr = container.getAttribute('data-page-sizes') || '10,50,100';
        this.sizes = sizesAttr.split(',').map(function (s) { return parseInt(s.trim(), 10); })
            .filter(function (n) { return n > 0; });
        if (this.sizes.indexOf(this.pageSize) === -1) { this.sizes.unshift(this.pageSize); }

        // Footer placement: after the table's .table-wrap (so it sits outside the
        // horizontal-scroll box) when there is one, otherwise right after the table
        // — or as the last child of a wrapping [data-paginate] div.
        this.footer = isTable ? null : container.querySelector(':scope > .pager');
        if (!this.footer) {
            this.footer = document.createElement('div');
            this.footer.className = 'pager';
            if (isTable) {
                var wrap = table.closest('.table-wrap');
                var anchor = wrap && wrap.parentNode ? wrap : table;
                anchor.parentNode.insertBefore(this.footer, anchor.nextSibling);
            } else {
                container.appendChild(this.footer);
            }
        }
        this.build();
        this.render();
    }

    Pager.prototype.eligibleRows = function () {
        return Array.prototype.filter.call(this.tbody.querySelectorAll(':scope > tr'), function (tr) {
            return !tr.classList.contains('cbar-empty-row')
                && !tr.classList.contains('cbar-filtered')
                && !tr.hasAttribute('data-cbar-skip');
        });
    };

    Pager.prototype.build = function () {
        this.footer.innerHTML = '';

        var sizeWrap = document.createElement('div');
        sizeWrap.className = 'pager-size';
        var select = document.createElement('select');
        // dropdown.js enhances this into the Bosch combo; the caption ("Elements per
        // page") shows inside the box exactly like the reference design.
        select.setAttribute('data-caption', 'Elements per page');
        select.setAttribute('aria-label', 'Elements per page');
        var self = this;
        this.sizes.forEach(function (n) {
            var opt = document.createElement('option');
            opt.value = String(n);
            opt.textContent = String(n);
            if (n === self.pageSize) { opt.selected = true; }
            select.appendChild(opt);
        });
        select.addEventListener('change', function () {
            self.pageSize = parseInt(select.value, 10) || self.pageSize;
            self.page = 1;
            self.render();
        });
        sizeWrap.appendChild(select);
        this.footer.appendChild(sizeWrap);
        // If dropdown.js already ran (e.g. this pager was built after load), enhance now.
        if (window.CsDropdown) { window.CsDropdown.enhance(select); }

        this.pages = document.createElement('div');
        this.pages.className = 'pager-pages';
        this.footer.appendChild(this.pages);
    };

    Pager.prototype.navButton = function (label, disabled, onClick) {
        var el = document.createElement('button');
        el.type = 'button';
        el.className = 'pager-nav' + (disabled ? ' is-disabled' : '');
        el.innerHTML = label;
        if (disabled) { el.disabled = true; }
        else { el.addEventListener('click', onClick); }
        return el;
    };

    Pager.prototype.render = function () {
        var rows = this.eligibleRows();
        var total = rows.length;
        var totalPages = Math.max(1, Math.ceil(total / this.pageSize));
        if (this.page > totalPages) { this.page = totalPages; }
        if (this.page < 1) { this.page = 1; }

        var start = (this.page - 1) * this.pageSize;
        var end = start + this.pageSize;
        rows.forEach(function (row, i) {
            var visible = i >= start && i < end;
            row.style.display = visible ? '' : 'none';
            row.classList.toggle('pgr-stripe', visible && ((i - start) % 2 === 0));
        });

        // Numbered strip (hidden when everything fits on one page).
        this.pages.innerHTML = '';
        if (totalPages <= 1) { return; }

        var self = this;
        this.pages.appendChild(this.navButton(
            '<i class="bi bi-chevron-left" aria-hidden="true"></i>',
            this.page === 1,
            function () { self.page--; self.render(); }));

        pageList(this.page, totalPages).forEach(function (item) {
            if (item === '...') {
                var gap = document.createElement('span');
                gap.className = 'pager-ellipsis';
                gap.textContent = '…';
                self.pages.appendChild(gap);
                return;
            }
            var btn = document.createElement('button');
            btn.type = 'button';
            btn.className = 'pager-page' + (item === self.page ? ' is-active' : '');
            btn.textContent = String(item);
            if (item !== self.page) {
                btn.addEventListener('click', function () { self.page = item; self.render(); });
            }
            self.pages.appendChild(btn);
        });

        this.pages.appendChild(this.navButton(
            '<i class="bi bi-chevron-right" aria-hidden="true"></i>',
            this.page === totalPages,
            function () { self.page++; self.render(); }));
    };

    // Public hook so command-bar can re-run pagination after search/sort.
    window.BoschPager = {
        refresh: function (target) {
            var container = target && target.closest ? target.closest('[data-paginate]') : null;
            instances.forEach(function (inst) {
                if (!target || inst.container === container) {
                    inst.page = 1;
                    inst.render();
                }
            });
        }
    };

    function init() {
        document.querySelectorAll('[data-paginate]').forEach(function (container) {
            var inst = new Pager(container);
            if (inst && inst.tbody) { instances.push(inst); }
        });
    }

    if (document.readyState === 'loading') {
        document.addEventListener('DOMContentLoaded', init);
    } else {
        init();
    }
})();
