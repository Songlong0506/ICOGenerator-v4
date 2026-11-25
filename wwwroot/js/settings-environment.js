// Nút "Test Environment" của trang Settings: POST tới SettingsController.TestEnvironment rồi vẽ bảng kết quả
// theo nhóm. Mọi chữ đến từ server (message/fix/detail có thể chứa nguyên văn lỗi của bên ngoài) nên chỉ gán
// bằng textContent, không bao giờ innerHTML.
(function () {
    const STATUS_LABEL = { pass: 'Pass', warning: 'Warning', fail: 'Fail', info: 'Info' };
    const STATUS_ICON = {
        pass: 'bi-check-circle-fill',
        warning: 'bi-exclamation-triangle-fill',
        fail: 'bi-x-octagon-fill',
        info: 'bi-info-circle'
    };

    function el(tag, className, text) {
        const node = document.createElement(tag);
        if (className) node.className = className;
        if (text != null) node.textContent = text;
        return node;
    }

    function token() {
        const input = document.querySelector('#settingsForm input[name="__RequestVerificationToken"]');
        return input ? input.value : '';
    }

    function renderSummary(box, data) {
        box.replaceChildren();
        [['fail', data.failed], ['warning', data.warnings], ['pass', data.passed]].forEach(([status, count]) => {
            const chip = el('span', 'env-chip env-chip--' + status);
            chip.append(el('i', 'bi ' + STATUS_ICON[status]), el('span', null, count + ' ' + STATUS_LABEL[status]));
            box.append(chip);
        });
    }

    function renderItems(container, items) {
        container.replaceChildren();
        const groups = new Map();
        items.forEach(item => {
            if (!groups.has(item.group)) groups.set(item.group, []);
            groups.get(item.group).push(item);
        });

        groups.forEach((rows, group) => {
            const worst = ['fail', 'warning', 'pass', 'info'].find(s => rows.some(r => r.status === s)) || 'info';
            const block = el('div', 'env-group env-group--' + worst);
            const head = el('h3', 'env-group-title');
            head.append(el('i', 'bi ' + STATUS_ICON[worst]), el('span', null, group));
            block.append(head);

            const table = el('table', 'env-table');
            const body = el('tbody');
            rows.forEach(row => {
                const tr = el('tr', 'env-row env-row--' + row.status);
                const statusCell = el('td', 'env-status');
                const badge = el('span', 'env-badge env-badge--' + row.status);
                badge.append(el('i', 'bi ' + STATUS_ICON[row.status]), el('span', null, STATUS_LABEL[row.status] || row.status));
                statusCell.append(badge);

                const nameCell = el('td', 'env-name', row.name);
                const msgCell = el('td', 'env-message');
                msgCell.append(el('div', null, row.message));
                if (row.fix) {
                    const fix = el('div', 'env-fix');
                    fix.append(el('strong', null, 'How to fix: '), document.createTextNode(row.fix));
                    msgCell.append(fix);
                }
                if (row.detail) {
                    const details = el('details', 'env-detail');
                    details.append(el('summary', null, 'Details'), el('pre', null, row.detail));
                    msgCell.append(details);
                }
                tr.append(statusCell, nameCell, msgCell);
                body.append(tr);
            });
            table.append(body);
            block.append(table);
            container.append(block);
        });
    }

    window.runEnvironmentCheck = async function () {
        const panel = document.getElementById('envCheckPanel');
        const button = document.getElementById('testEnvironmentButton');
        const status = document.getElementById('envCheckStatus');
        const summary = document.getElementById('envCheckSummary');
        const results = document.getElementById('envCheckResults');
        if (!panel) return;

        panel.hidden = false;
        panel.classList.add('is-running');
        if (button) button.disabled = true;
        summary.replaceChildren();
        results.replaceChildren();
        status.textContent = 'Running checks… The first run on a new server can take about a minute (headless Chromium is downloaded once).';
        panel.scrollIntoView({ behavior: 'smooth', block: 'start' });

        try {
            const response = await fetch(panel.dataset.url, {
                method: 'POST',
                headers: { 'RequestVerificationToken': token(), 'X-Requested-With': 'XMLHttpRequest' }
            });
            // Hết phiên / thiếu quyền: cookie auth chuyển sang trang HTML nên phản hồi không phải JSON.
            const isJson = (response.headers.get('content-type') || '').includes('application/json');
            if (!response.ok || !isJson) {
                status.textContent = !response.ok
                    ? 'The check could not run (HTTP ' + response.status + ').'
                    : 'The check could not run: missing permission or your session has expired. Please reload the page.';
                return;
            }

            const data = await response.json();
            renderSummary(summary, data);
            renderItems(results, data.items || []);
            const at = new Date(data.checkedAtUtc).toLocaleString();
            status.textContent = 'Checked at ' + at + ' in ' + (data.durationMs / 1000).toFixed(1) + ' s. '
                + (data.failed > 0
                    ? 'Fix the failed items first — features that depend on them will not work.'
                    : data.warnings > 0
                        ? 'No blocking problems; warnings mean a feature is degraded or a setting is risky.'
                        : 'Everything the app needs is in place.');
        } catch (e) {
            status.textContent = 'Could not reach the server: ' + e.message;
        } finally {
            panel.classList.remove('is-running');
            if (button) button.disabled = false;
        }
    };
})();
