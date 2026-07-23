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
    const chips = Array.from(document.querySelectorAll('.tool-filter-chips .chip'));
    const groups = Array.from(scroll.querySelectorAll('.tool-group'));
    const checkboxes = Array.from(scroll.querySelectorAll('.tool-checkbox'));

    let activeFilter = 'all';

    function reflectCard(checkbox) {
        const card = checkbox.closest('.tool-card');
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

    function updateSelectedCount() {
        selectedCount.textContent = checkboxes.filter(c => c.checked).length;
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
            const cards = Array.from(group.querySelectorAll('.tool-card'));
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
        const visibleCards = Array.from(group.querySelectorAll('.tool-card')).filter(c => !c.hidden);
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
            const visibleBoxes = Array.from(group.querySelectorAll('.tool-card'))
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

    chips.forEach(function (chip) {
        chip.addEventListener('click', function () {
            chips.forEach(c => c.classList.remove('active'));
            chip.classList.add('active');
            activeFilter = chip.dataset.filter;
            applyFilters();
        });
    });

    document.querySelectorAll('.tools-bulk .link-btn').forEach(function (btn) {
        btn.addEventListener('click', function () {
            const action = btn.dataset.bulk;
            if (action === 'expand') {
                groups.forEach(g => { g.classList.remove('collapsed'); g.querySelector('.tool-group-toggle').setAttribute('aria-expanded', 'true'); });
            } else if (action === 'collapse') {
                groups.forEach(g => { g.classList.add('collapsed'); g.querySelector('.tool-group-toggle').setAttribute('aria-expanded', 'false'); });
            } else if (action === 'clear') {
                checkboxes.forEach(function (b) { if (b.checked) { b.checked = false; reflectCard(b); } });
                updateSelectedCount();
                applyFilters();
            }
        });
    });

    groups.forEach(syncGroupCheckbox);
    updateSelectedCount();
})();
