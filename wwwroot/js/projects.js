// Combo "Đơn vị yêu cầu" ở thanh lọc — chọn NHIỀU. Mỗi option là 1 checkbox name="orgUnit" sẵn trong
// form GET; JS chỉ lo giao diện (nhãn nút, mở/đóng panel, tìm kiếm, chọn tất cả) và đồng bộ lớp
// .selected. Không tự submit theo từng tick — người dùng chọn xong rồi bấm "Lọc".
(function () {
    var combo = document.querySelector('[data-ms-combo]');
    if (!combo) return;

    var trigger = combo.querySelector('[data-ms-trigger]');
    var panel = combo.querySelector('[data-ms-panel]');
    var label = combo.querySelector('[data-ms-label]');
    var search = combo.querySelector('[data-ms-search]');
    var empty = combo.querySelector('[data-ms-empty]');
    var allBox = combo.querySelector('[data-ms-all]');
    var allText = combo.querySelector('[data-ms-all-text]');
    var clearBtn = combo.querySelector('[data-ms-clear]');
    var placeholder = label.getAttribute('data-ms-placeholder') || '';
    var options = Array.prototype.slice.call(combo.querySelectorAll('.ms-combo-option'));

    function checkboxOf(opt) { return opt.querySelector('[data-ms-option]'); }
    function isVisible(opt) { return !opt.classList.contains('hidden'); }

    // Nhãn nút: 0 chọn → placeholder; 1–2 → ghép tên; >2 → "N mục đã chọn".
    function renderLabel() {
        var names = options
            .filter(function (opt) { return checkboxOf(opt).checked; })
            .map(function (opt) { return opt.querySelector('.ms-combo-option-text').textContent.trim(); });
        if (names.length === 0) {
            label.textContent = placeholder;
            label.classList.add('is-placeholder');
        } else {
            label.textContent = names.length <= 2 ? names.join(', ') : (names.length + ' mục đã chọn');
            label.classList.remove('is-placeholder');
        }
    }

    // Trạng thái "Chọn tất cả" bám theo các option ĐANG hiển thị (sau khi lọc tìm kiếm).
    function renderAll() {
        var visible = options.filter(isVisible);
        var checked = visible.filter(function (opt) { return checkboxOf(opt).checked; });
        allBox.checked = visible.length > 0 && checked.length === visible.length;
        allBox.indeterminate = checked.length > 0 && checked.length < visible.length;
        if (allText) allText.textContent = 'Chọn tất cả (' + options.length + ')';
    }

    function syncOption(opt) {
        var on = checkboxOf(opt).checked;
        opt.classList.toggle('selected', on);
        opt.setAttribute('aria-selected', on ? 'true' : 'false');
    }

    function open() {
        combo.classList.add('open');
        panel.classList.remove('hidden');
        trigger.setAttribute('aria-expanded', 'true');
        search.value = '';
        filter();
        search.focus();
    }

    function close() {
        combo.classList.remove('open');
        panel.classList.add('hidden');
        trigger.setAttribute('aria-expanded', 'false');
    }

    function filter() {
        var q = search.value.trim().toLowerCase();
        if (clearBtn) clearBtn.classList.toggle('hidden', q === '');
        var visible = 0;
        options.forEach(function (opt) {
            var match = q === '' || opt.getAttribute('data-search').indexOf(q) !== -1;
            opt.classList.toggle('hidden', !match);
            if (match) visible++;
        });
        empty.classList.toggle('hidden', visible > 0);
        renderAll();
    }

    trigger.addEventListener('click', function () {
        if (combo.classList.contains('open')) close(); else open();
    });

    options.forEach(function (opt) {
        checkboxOf(opt).addEventListener('change', function () {
            syncOption(opt);
            renderLabel();
            renderAll();
        });
    });

    allBox.addEventListener('change', function () {
        var on = allBox.checked;
        options.filter(isVisible).forEach(function (opt) {
            var cb = checkboxOf(opt);
            if (cb.checked !== on) {
                cb.checked = on;
                syncOption(opt);
            }
        });
        renderLabel();
        renderAll();
    });

    search.addEventListener('input', filter);

    search.addEventListener('keydown', function (e) {
        if (e.key === 'Escape') { close(); trigger.focus(); }
    });

    if (clearBtn) {
        clearBtn.addEventListener('click', function () {
            search.value = '';
            filter();
            search.focus();
        });
    }

    document.addEventListener('click', function (e) {
        if (!combo.contains(e.target)) close();
    });

    renderLabel();
    renderAll();
})();
