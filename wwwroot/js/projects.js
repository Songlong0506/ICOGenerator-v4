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

// Modal "Chỉnh sửa dự án" — MỘT modal dùng chung cho cả bảng: nút bút chì ở mỗi dòng mang dữ liệu dòng đó
// trong data-* và JS đổ vào form. Ba việc modal này lo (ngoài đổ dữ liệu):
//   • Nút "Lưu thay đổi" chỉ bật khi có thay đổi thật (và tên không rỗng).
//   • Sửa tên ⇒ hiện cảnh báo thư mục làm việc sẽ được đổi tên theo — đổi tên là việc CÓ hệ quả trên đĩa,
//     người dùng nên biết trước khi bấm lưu (server vẫn là chốt chặn: xem UpdateProjectUseCase).
//   • Dự án đang chạy workflow ⇒ ô Name chuyển chỉ-đọc kèm lý do, thay vì để người dùng gõ xong mới bị
//     server từ chối. Vẫn dùng readonly (không phải disabled) để Name còn được submit — server cần nó.
(function () {
    var modal = document.getElementById('editProject');
    if (!modal) return;

    var nameInput = modal.querySelector('[data-edit-name]');
    var descInput = modal.querySelector('[data-edit-description]');
    var orgSelect = modal.querySelector('[data-edit-orgunit]');
    var idInput = modal.querySelector('[data-edit-id]');
    var subtitle = modal.querySelector('[data-edit-subtitle]');
    var renameNote = modal.querySelector('[data-edit-rename-note]');
    var lockedNote = modal.querySelector('[data-edit-locked-note]');
    var saveBtn = modal.querySelector('[data-edit-save]');

    var original = { name: '', description: '', orgUnit: '' };
    var locked = false;

    function orgValue() { return orgSelect ? orgSelect.value : ''; }

    function refresh() {
        var name = nameInput.value.trim();
        var renamed = name !== original.name;

        saveBtn.disabled = name === '' || !(renamed
            || descInput.value.trim() !== original.description
            || orgValue() !== original.orgUnit);

        // Không cảnh báo khi tên đang bị khóa: lúc đó tên không thể đổi nên cảnh báo chỉ gây hoang mang.
        renameNote.classList.toggle('hidden', locked || !renamed);
    }

    function open(button) {
        var data = button.dataset;

        original.name = (data.projectName || '').trim();
        original.description = (data.projectDescription || '').trim();
        original.orgUnit = data.projectOrgunit || '';
        locked = data.projectRunning === 'true';

        idInput.value = data.projectId || '';
        nameInput.value = original.name;
        descInput.value = original.description;
        if (orgSelect) {
            // Mã đơn vị không còn trong dữ liệu HR (đã xóa/đồng bộ lại) thì không có <option> tương ứng —
            // gán không ăn, select rơi về "— Chưa chọn —". Đồng bộ lại mốc so sánh theo giá trị THẬT của
            // select để nút Lưu không bật lên vì một khác biệt người dùng không hề tạo ra.
            orgSelect.value = original.orgUnit;
            original.orgUnit = orgSelect.value;
        }

        nameInput.readOnly = locked;
        lockedNote.classList.toggle('hidden', !locked);
        renameNote.classList.add('hidden');

        // Tên hiện tại của dự án luôn hiển thị ở đầu modal (textContent nên không dựng HTML từ dữ liệu):
        // sau khi sửa ô Name, đây là mốc để biết mình đang sửa đúng dòng nào.
        subtitle.textContent = 'Dự án: ' + original.name;

        refresh();
        openModal('editProject');
        // Đang khóa tên thì con trỏ vào ô đầu tiên còn sửa được, khỏi bắt người dùng tự tab qua.
        (locked ? descInput : nameInput).focus();
    }

    document.querySelectorAll('[data-edit-project]').forEach(function (button) {
        button.addEventListener('click', function () { open(button); });
    });

    nameInput.addEventListener('input', refresh);
    descInput.addEventListener('input', refresh);
    // Combo tùy biến của dropdown.js vẫn phát 'change' trên <select> gốc nên chỉ cần nghe ở đây.
    if (orgSelect) orgSelect.addEventListener('change', refresh);

    // Esc để đóng — chỉ gắn cho modal này (không đụng các modal khác trong app) và chỉ khi nó đang mở.
    // Combo Org Unit đang mở thì Esc thuộc về combo (dropdown.js tự đóng panel), chưa đóng cả modal.
    document.addEventListener('keydown', function (e) {
        if (e.key !== 'Escape' || modal.classList.contains('hidden')) return;
        if (modal.querySelector('.ms-combo.open')) return;
        closeModal('editProject');
    });
})();
