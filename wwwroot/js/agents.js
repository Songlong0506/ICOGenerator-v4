// Clicking the agent color swatch opens the native color picker and previews the
// chosen color live on both the header swatch and the matching sidebar circle.
(function () {
    const swatch = document.getElementById('agentColorSwatch');
    const input = document.getElementById('agentColorInput');
    if (!swatch || !input) return;

    swatch.addEventListener('click', function () { input.click(); });

    function preview() {
        swatch.style.background = input.value;
        const activeCircle = document.querySelector('.agent-item.active .circle');
        if (activeCircle) activeCircle.style.background = input.value;
    }
    input.addEventListener('input', preview);
    input.addEventListener('change', preview);
})();

(function () {
    const scroll = document.getElementById('toolsScroll');
    if (!scroll) return;

    const searchInput = document.getElementById('toolSearch');
    const selectedCount = document.getElementById('toolsSelectedCount');
    const emptyMsg = document.getElementById('toolsEmpty');
    const groups = Array.from(scroll.querySelectorAll('.tool-group'));
    const checkboxes = Array.from(scroll.querySelectorAll('.tool-checkbox'));
    const selectedBar = document.getElementById('toolsSelected');
    const selectedChips = document.getElementById('toolsSelectedChips');

    let activeFilter = 'all';

    function reflectCard(checkbox) {
        const card = checkbox.closest('.tool-row');
        const badge = card.querySelector('.badge');
        card.dataset.enabled = checkbox.checked ? 'true' : 'false';
        if (checkbox.checked) {
            card.classList.add('enabled');
            badge.classList.remove('gray');
            badge.classList.add('green');
            badge.textContent = 'Enabled';
        } else {
            card.classList.remove('enabled');
            badge.classList.remove('green');
            badge.classList.add('gray');
            badge.textContent = 'Disabled';
        }
    }

    // Rebuild the "selected" summary bar from the currently checked tools.
    function renderSelectedBar() {
        const checked = checkboxes.filter(c => c.checked);
        selectedBar.classList.toggle('is-empty', checked.length === 0);
        selectedChips.innerHTML = '';
        checked
            .map(function (cb) {
                const row = cb.closest('.tool-row');
                return { id: cb.value, name: row.querySelector('.tool-row-name').textContent, key: row.dataset.name || '' };
            })
            .sort(function (a, b) { return a.key.localeCompare(b.key); })
            .forEach(function (item) {
                const chip = document.createElement('button');
                chip.type = 'button';
                chip.className = 'sel-chip';
                chip.dataset.toolId = item.id;
                chip.title = 'Bỏ chọn ' + item.name;
                const label = document.createElement('span');
                label.textContent = item.name;
                const x = document.createElement('span');
                x.className = 'sel-chip-x';
                x.setAttribute('aria-hidden', 'true');
                x.textContent = '✕';
                chip.appendChild(label);
                chip.appendChild(x);
                selectedChips.appendChild(chip);
            });
    }

    function updateSelectedCount() {
        selectedCount.textContent = checkboxes.filter(c => c.checked).length;
        renderSelectedBar();
    }

    // Sort rows inside every group by name.
    function applySort() {
        groups.forEach(function (group) {
            const body = group.querySelector('.tool-group-body');
            const rows = Array.from(body.querySelectorAll('.tool-row'));
            rows.sort(function (a, b) {
                return (a.dataset.name || '').localeCompare(b.dataset.name || '');
            });
            rows.forEach(function (r) { body.appendChild(r); });
        });
    }

    function matchesFilter(card) {
        if (activeFilter === 'enabled') return card.dataset.enabled === 'true';
        if (activeFilter === 'disabled') return card.dataset.enabled !== 'true';
        return true;
    }

    function applyFilters() {
        const term = searchInput.value.trim().toLowerCase();
        let anyVisible = false;

        groups.forEach(function (group) {
            const cards = Array.from(group.querySelectorAll('.tool-row'));
            let visibleInGroup = 0;
            cards.forEach(function (card) {
                const hit = (!term || card.dataset.search.indexOf(term) !== -1) && matchesFilter(card);
                card.hidden = !hit;
                if (hit) visibleInGroup++;
            });
            group.hidden = visibleInGroup === 0;
            if (visibleInGroup > 0) anyVisible = true;
            syncGroupCheckbox(group);
        });

        emptyMsg.hidden = anyVisible;
    }

    function syncGroupCheckbox(group) {
        const groupCheck = group.querySelector('.group-check');
        const visibleCards = Array.from(group.querySelectorAll('.tool-row')).filter(c => !c.hidden);
        const visibleBoxes = visibleCards.map(c => c.querySelector('.tool-checkbox'));
        const checked = visibleBoxes.filter(b => b.checked).length;

        // group enabled/total counters reflect ALL tools in the group, not just filtered
        const allBoxes = Array.from(group.querySelectorAll('.tool-checkbox'));
        group.querySelector('.grp-enabled').textContent = allBoxes.filter(b => b.checked).length;

        if (visibleBoxes.length === 0) {
            groupCheck.checked = false;
            groupCheck.indeterminate = false;
        } else if (checked === 0) {
            groupCheck.checked = false;
            groupCheck.indeterminate = false;
        } else if (checked === visibleBoxes.length) {
            groupCheck.checked = true;
            groupCheck.indeterminate = false;
        } else {
            groupCheck.checked = false;
            groupCheck.indeterminate = true;
        }
    }

    checkboxes.forEach(function (checkbox) {
        checkbox.addEventListener('change', function () {
            reflectCard(checkbox);
            updateSelectedCount();
            syncGroupCheckbox(checkbox.closest('.tool-group'));
            if (activeFilter !== 'all') applyFilters();
        });
    });

    // Group "select all" (applies to currently visible cards in the group)
    groups.forEach(function (group) {
        const groupCheck = group.querySelector('.group-check');
        groupCheck.addEventListener('change', function () {
            const visibleBoxes = Array.from(group.querySelectorAll('.tool-row'))
                .filter(c => !c.hidden)
                .map(c => c.querySelector('.tool-checkbox'));
            visibleBoxes.forEach(function (b) {
                b.checked = groupCheck.checked;
                reflectCard(b);
            });
            updateSelectedCount();
            if (activeFilter !== 'all') applyFilters(); else syncGroupCheckbox(group);
        });

        const toggle = group.querySelector('.tool-group-toggle');
        toggle.addEventListener('click', function () {
            const collapsed = group.classList.toggle('collapsed');
            toggle.setAttribute('aria-expanded', String(!collapsed));
        });
    });

    let searchTimer;
    searchInput.addEventListener('input', function () {
        clearTimeout(searchTimer);
        searchTimer = setTimeout(applyFilters, 120);
    });

    // Removing a tool from the selected bar unchecks its underlying checkbox.
    if (selectedChips) {
        selectedChips.addEventListener('click', function (e) {
            const chip = e.target.closest('.sel-chip');
            if (!chip) return;
            const box = checkboxes.find(c => c.value === chip.dataset.toolId);
            if (!box || !box.checked) return;
            box.checked = false;
            reflectCard(box);
            updateSelectedCount();
            syncGroupCheckbox(box.closest('.tool-group'));
            if (activeFilter !== 'all') applyFilters();
        });
    }

    groups.forEach(syncGroupCheckbox);
    updateSelectedCount();
    applySort();
})();
