// Per-project agent dashboard (read-only monitoring): workspace tree, call logs, live activity.
// Razor-provided values come from window.AGENT_DASHBOARD (set inline by the view before this loads).
const DOCS = window.AGENT_DASHBOARD.docs;
const FIRST_DOC_ID = window.AGENT_DASHBOARD.firstDocId;
const PROJECT_ID = window.AGENT_DASHBOARD.projectId;
let activeAgentFilter = null;
// Doc DB-tracked đang xem (nút "Lịch sử" của khung preview trỏ vào id này; file chỉ-trên-đĩa không có lịch sử).
let currentDbDocId = null;

function filterByAgent(agentId, name) {
    if (activeAgentFilter === agentId) { clearAgentFilter(); return; }
    activeAgentFilter = agentId;
    document.querySelectorAll('.agent-row').forEach(c => c.classList.toggle('active', c.dataset.agentId === agentId));
    document.querySelectorAll('#ws-tree .file').forEach(f => {
        f.style.display = (f.dataset.agentId === agentId) ? '' : 'none';
    });
    updateWorkspaceTreeVisibility();
    const label = document.getElementById('ws-filter-label');
    if (label) label.textContent = 'Filtered by: ' + name;
    const clear = document.getElementById('ws-clear');
    if (clear) clear.classList.remove('hidden');
}

function clearAgentFilter() {
    activeAgentFilter = null;
    document.querySelectorAll('.agent-row').forEach(c => c.classList.remove('active'));
    document.querySelectorAll('#ws-tree .file').forEach(f => f.style.display = '');
    updateWorkspaceTreeVisibility();
    const label = document.getElementById('ws-filter-label');
    if (label) label.textContent = '';
    const clear = document.getElementById('ws-clear');
    if (clear) clear.classList.add('hidden');
}

function toggleFolder(button) {
    const folderNode = button.closest('.folder');
    if (!folderNode) return;

    const isCollapsed = folderNode.classList.toggle('collapsed');
    button.setAttribute('aria-expanded', String(!isCollapsed));
}

function updateWorkspaceTreeVisibility() {
    document.querySelectorAll('#ws-tree .version-folder').forEach(versionNode => {
        const hasVisibleFile = Array.from(versionNode.querySelectorAll('.file'))
            .some(file => file.style.display !== 'none');
        versionNode.style.display = hasVisibleFile ? '' : 'none';
    });

    document.querySelectorAll('#ws-tree .phase-folder').forEach(phaseNode => {
        const hasVisibleFile = Array.from(phaseNode.querySelectorAll('.file'))
            .some(file => file.style.display !== 'none');
        const hasStaticEmpty = phaseNode.querySelector(':scope > ul > .empty') !== null;
        phaseNode.style.display = (hasVisibleFile || hasStaticEmpty || !activeAgentFilter) ? '' : 'none';
    });
}

async function showDoc(id) {
    if (!id) return;

    const meta = DOCS[id];
    const titleEl = document.getElementById('doc-title');
    const contentEl = document.getElementById('doc-content');

    if (meta) titleEl.textContent = meta.name;

    // Lịch sử revision chỉ tồn tại cho tài liệu có bản ghi DB (meta.path rỗng); file chỉ-trên-đĩa
    // mang Guid tạm mỗi request nên không tra lịch sử được.
    const isDbDoc = meta && !meta.path;
    currentDbDocId = isDbDoc ? id : null;
    const historyBtn = document.getElementById('doc-history-btn');
    if (historyBtn) historyBtn.classList.toggle('hidden', !isDbDoc);

    document.querySelectorAll('#ws-tree .file').forEach(f => f.classList.remove('selected'));
    const node = document.querySelector('#ws-tree .file[data-doc-id="' + id + '"]');
    if (node) node.classList.add('selected');

    contentEl.innerHTML = '<p class="doc-loading">Loading…</p>';

    try {
        // DB documents resolve by Id; disk-only files (meta.path set) resolve by
        // projectId + relative path, since their Id is a throwaway per-request Guid.
        const url = (meta && meta.path)
            ? '/AgentDashboard/DocumentPreview?projectId=' + encodeURIComponent(PROJECT_ID) + '&path=' + encodeURIComponent(meta.path)
            : '/AgentDashboard/DocumentPreview?id=' + encodeURIComponent(id);
        const response = await fetch(url);
        if (!response.ok) throw new Error('Preview request failed');
        const data = await response.json();
        titleEl.textContent = data.name;
        contentEl.innerHTML = data.html;
    } catch {
        contentEl.innerHTML = '<p class="doc-empty">Unable to load preview.</p>';
    }
}

document.addEventListener('DOMContentLoaded', function () {
    if (FIRST_DOC_ID) showDoc(FIRST_DOC_ID);
    // Render UTC call-log timestamps in the viewer's local timezone (matches the logs modal).
    document.querySelectorAll('.last-activity[data-utc]').forEach(function (el) {
        el.textContent = formatDateTime(el.dataset.utc);
    });
});

// Current agent whose call logs the modal is showing (kept so the pager can re-fetch pages).
let logsAgentId = null;
let logsAgentName = null;
// Số dòng mỗi trang cho popup Call Logs ("Elements per page" — mẫu bảng chuẩn Bosch).
let logsPageSize = 10;

function loadAgentLogs(agentId, agentName) {

    logsAgentId = agentId;
    logsAgentName = agentName;

    document.getElementById('logs-modal').style.display = 'flex';

    document.getElementById('logs-subtitle').textContent =
        `Agent: ${agentName}`;

    // Fresh agent → start from an empty filter bar so old selections don't carry over.
    resetLogFilterInputs();

    return loadAgentLogsPage(1);
}

// Convert a <input type="datetime-local"> value (local time) to a UTC wall-clock string
// (yyyy-MM-ddTHH:mm:ss). CreatedAt is stored in UTC, so the backend compares against this directly.
function toUtcWallClock(localValue) {
    if (!localValue) return '';
    const d = new Date(localValue);
    if (isNaN(d.getTime())) return '';
    const pad = n => String(n).padStart(2, '0');
    return `${d.getUTCFullYear()}-${pad(d.getUTCMonth() + 1)}-${pad(d.getUTCDate())}`
        + `T${pad(d.getUTCHours())}:${pad(d.getUTCMinutes())}:${pad(d.getUTCSeconds())}`;
}

function getLogFilters() {
    const val = id => {
        const el = document.getElementById(id);
        return el ? el.value.trim() : '';
    };
    return {
        fromUtc: toUtcWallClock(val('filter-from')),
        toUtc: toUtcWallClock(val('filter-to')),
        purpose: val('filter-purpose'),
        minDurationMs: val('filter-min-duration'),
        maxDurationMs: val('filter-max-duration'),
        status: val('filter-status')
    };
}

function buildLogsUrl(page) {
    const params = new URLSearchParams();
    params.set('projectId', PROJECT_ID);
    params.set('agentId', logsAgentId);
    params.set('page', page);
    params.set('pageSize', logsPageSize);

    const f = getLogFilters();
    if (f.fromUtc) params.set('fromUtc', f.fromUtc);
    if (f.toUtc) params.set('toUtc', f.toUtc);
    if (f.purpose) params.set('purpose', f.purpose);
    if (f.minDurationMs !== '') params.set('minDurationMs', f.minDurationMs);
    if (f.maxDurationMs !== '') params.set('maxDurationMs', f.maxDurationMs);
    if (f.status) params.set('status', f.status);

    return `/AgentDashboard/AgentCallLogs?${params.toString()}`;
}

// Rebuild the Purpose dropdown from the server's distinct list, preserving the current
// selection. Skips the rebuild when the option set is unchanged (avoids flicker on paging).
function populatePurposeFilter(purposes) {
    const select = document.getElementById('filter-purpose');
    if (!select) return;

    const list = Array.isArray(purposes) ? purposes : [];
    const desired = ['', ...list];
    const existing = Array.from(select.options).map(o => o.value);
    const unchanged = existing.length === desired.length
        && existing.every((v, i) => v === desired[i]);
    if (unchanged) return;

    const current = select.value;
    select.innerHTML = '<option value="">All</option>'
        + list.map(p => `<option value="${escapeHtml(p)}">${escapeHtml(p)}</option>`).join('');
    select.value = list.includes(current) ? current : '';
}

function resetLogFilterInputs() {
    ['filter-from', 'filter-to', 'filter-min-duration', 'filter-max-duration', 'filter-status']
        .forEach(id => {
            const el = document.getElementById(id);
            if (el) el.value = '';
        });
    const purpose = document.getElementById('filter-purpose');
    if (purpose) {
        purpose.innerHTML = '<option value="">All</option>';
        purpose.value = '';
    }
}

function applyLogFilters() {
    return loadAgentLogsPage(1);
}

function clearLogFilters() {
    resetLogFilterInputs();
    return loadAgentLogsPage(1);
}

async function loadAgentLogsPage(page) {

    const tbody = document.getElementById('logs-tbody');
    const pager = document.getElementById('logs-pager');

    pager.innerHTML = '';
    tbody.innerHTML =
        '<tr><td colspan="7">Loading...</td></tr>';

    const url = buildLogsUrl(page);

    let result;
    try {
        const response = await fetch(url);
        if (!response.ok) throw new Error(`HTTP ${response.status}`);
        result = await response.json();
    } catch (err) {
        tbody.innerHTML =
            '<tr><td colspan="7">Failed to load logs. Please try again.</td></tr>';
        return;
    }

    populatePurposeFilter(result.purposes);

    const logs = result.items || [];

    if (!logs.length) {

        const hasActiveFilter = Object.values(getLogFilters()).some(v => v !== '');
        tbody.innerHTML = hasActiveFilter
            ? '<tr><td colspan="7">No AI call logs match the current filters.</td></tr>'
            : '<tr><td colspan="7">No AI call logs found for this agent.</td></tr>';

        renderLogsPager(result);
        return;
    }

    tbody.innerHTML = logs.map(x => `
        <tr>
            <td>${formatDateTime(x.createdAt)}</td>

            <td>
                ${escapeHtml(x.purpose || '-')}
                <br>
                <small>Step ${x.step || 1}</small>
            </td>

            <td>
                ${escapeHtml(x.modelId || '-')}
            </td>

            <td>
                ${Number(x.totalTokens || 0).toLocaleString()}
                <br>
                <small>
                    P:${x.promptTokens || 0}
                    C:${x.completionTokens || 0}
                </small>
            </td>

            <td>${x.durationMs || 0} ms</td>

            <td>
                <span class="badge ${x.isSuccess ? 'badge-success' : 'badge-error'}">
                    ${x.isSuccess ? 'Success' : 'Error'}
                </span>
            </td>

            <td>
                <button class="btn btn-primary"
                        onclick="viewLogDetail('${x.id}')">
                    View
                </button>
            </td>
        </tr>
    `).join('');

    renderLogsPager(result);
}

// Đổi số dòng mỗi trang rồi tải lại từ trang 1.
function setLogsPageSize(value) {
    const n = parseInt(value, 10);
    if (n > 0) logsPageSize = n;
    return loadAgentLogsPage(1);
}

// Chuỗi số trang cần hiển thị, chèn '...' làm dấu ellipsis — khớp mẫu Bosch (1 2 3 4 5 … N).
function logsPageItems(current, total) {
    const items = [];
    if (total <= 7) {
        for (let i = 1; i <= total; i++) items.push(i);
        return items;
    }
    let start = Math.max(2, current - 1);
    let end = Math.min(total - 1, current + 1);
    if (current <= 4) { start = 2; end = 5; }
    if (current >= total - 3) { start = total - 4; end = total - 1; }
    items.push(1);
    if (start > 2) items.push('...');
    for (let i = start; i <= end; i++) items.push(i);
    if (end < total - 1) items.push('...');
    items.push(total);
    return items;
}

// Dựng footer phân trang theo mẫu bảng chuẩn Bosch (dùng chung class .pager* với site.css):
// ô "Elements per page" bên trái + dải số trang tròn bên phải (chevron ‹ ›, trang hiện tại
// là chấm xanh). Select được nâng cấp thành combo Bosch qua dropdown.js.
function renderLogsPager(result) {

    const pager = document.getElementById('logs-pager');
    const totalPages = result.totalPages || 0;
    const page = result.page || 1;

    const options = [10, 50, 100]
        .map(n => `<option value="${n}"${n === logsPageSize ? ' selected' : ''}>${n}</option>`)
        .join('');
    let html =
        '<div class="pager-size">'
        + '<select data-caption="Elements per page" aria-label="Elements per page"'
        + ' onchange="setLogsPageSize(this.value)">' + options + '</select>'
        + '</div>';

    if (totalPages > 1) {
        html += '<div class="pager-pages">';
        html += result.hasPrevious
            ? `<button type="button" class="pager-nav" aria-label="Previous page" onclick="loadAgentLogsPage(${page - 1})"><i class="bi bi-chevron-left" aria-hidden="true"></i></button>`
            : '<span class="pager-nav is-disabled" aria-hidden="true"><i class="bi bi-chevron-left"></i></span>';

        logsPageItems(page, totalPages).forEach(item => {
            if (item === '...') {
                html += '<span class="pager-ellipsis">…</span>';
            } else if (item === page) {
                html += `<span class="pager-page is-active" aria-current="page">${item}</span>`;
            } else {
                html += `<button type="button" class="pager-page" onclick="loadAgentLogsPage(${item})">${item}</button>`;
            }
        });

        html += result.hasNext
            ? `<button type="button" class="pager-nav" aria-label="Next page" onclick="loadAgentLogsPage(${page + 1})"><i class="bi bi-chevron-right" aria-hidden="true"></i></button>`
            : '<span class="pager-nav is-disabled" aria-hidden="true"><i class="bi bi-chevron-right"></i></span>';
        html += '</div>';
    }

    pager.classList.add('pager');
    pager.innerHTML = html;

    // Select dựng động nên dropdown.js (chỉ tự nâng cấp lúc tải trang) chưa xử lý — nâng cấp tại đây.
    const sel = pager.querySelector('select');
    if (sel && window.CsDropdown) window.CsDropdown.enhance(sel);
}

function closeLogsModal() {
    document.getElementById('logs-modal').style.display = 'none';
}

async function viewLogDetail(id) {
    let log;
    try {
        const response = await fetch(`/AgentDashboard/CallLogDetail?id=${id}`);
        if (!response.ok) throw new Error(`HTTP ${response.status}`);
        log = await response.json();
    } catch (err) {
        alert('Failed to load log detail. Please try again.');
        return;
    }

    document.getElementById('log-detail-meta').textContent = `${log.agentName} · ${log.modelId} · ${formatDateTime(log.createdAt)} · ${log.totalTokens || 0} tokens · ${log.durationMs || 0} ms`;
    document.getElementById('log-request').textContent = prettyJson(log.requestJson);
    document.getElementById('log-request-readable').innerHTML = buildReadableRequest(log.requestJson);
    document.getElementById('log-response').textContent = prettyJson(log.responseText);
    document.getElementById('log-error').textContent = log.errorMessage || '';
    requestReadableMode = false;
    document.getElementById('log-modal').style.display = 'flex';
    showLogTab('request', document.querySelector('.tab'));
}

function closeLogModal() {
    document.getElementById('log-modal').style.display = 'none';
}

function openDeliveryConfig() {
    const modal = document.getElementById('delivery-config-modal');
    if (modal) modal.style.display = 'flex';
}

function closeDeliveryConfig() {
    const modal = document.getElementById('delivery-config-modal');
    if (modal) modal.style.display = 'none';
}

function openReviseModal() {
    const modal = document.getElementById('revise-modal');
    if (!modal) return;
    modal.style.display = 'flex';
    const feedback = document.getElementById('revise-feedback');
    if (feedback) feedback.focus();

    loadRevisePocComments();
}

function closeReviseModal() {
    const modal = document.getElementById('revise-modal');
    if (modal) modal.style.display = 'none';
}

// Cổng POC: nạp các ghi chú người xem đã GHIM trực tiếp trên POC (Projects/PocReview) vào popup
// "Yêu cầu chỉnh sửa" — mặc định gửi kèm cho agent (checkbox). Khi có ghi chú được gửi kèm, phần
// nhận xét gõ tay được phép trống (required của textarea bật/tắt theo đó). Bước hiện tại do vòng
// poll cổng ghi vào #delivery-gate (data-stage); không phải cổng POC thì ẩn cả khối.
async function loadRevisePocComments() {
    const block = document.getElementById('revise-poc-comments');
    const feedback = document.getElementById('revise-feedback');
    if (!block || !feedback) return;

    const gate = document.getElementById('delivery-gate');
    const isPocGate = gate && gate.dataset.stage === 'PocPreview';

    const listEl = document.getElementById('revise-poc-list');
    const summaryEl = document.getElementById('revise-poc-summary');
    const checkbox = document.getElementById('revise-include-poc');

    function syncRequired(hasComments) {
        feedback.required = !(hasComments && checkbox && checkbox.checked);
    }

    if (!isPocGate) {
        block.style.display = 'none';
        syncRequired(false);
        return;
    }

    let comments = [];
    try {
        const response = await fetch(`/Projects/PocComments?projectId=${PROJECT_ID}`);
        if (response.ok) comments = await response.json();
    } catch { /* lỗi mạng: coi như không có ghi chú, chỉ mất tiện ích phụ */ }

    const open = comments.filter(c => c.status === 'Open');
    if (open.length === 0) {
        block.style.display = 'none';
        syncRequired(false);
        return;
    }

    block.style.display = '';
    summaryEl.textContent = `Gửi kèm ${open.length} ghi chú đã ghim trên POC`;
    listEl.innerHTML = open.map(c => `
        <li>
            <b>${escapeHtml(c.elementLabel || 'Vị trí trên trang')}</b>
            ${c.pageView ? `<span class="muted">· ${escapeHtml(c.pageView)}</span>` : ''}
            — ${escapeHtml(c.comment)}
        </li>
    `).join('');

    syncRequired(true);
    if (checkbox && !checkbox.dataset.wired) {
        checkbox.dataset.wired = '1';
        checkbox.addEventListener('change', () => syncRequired(block.style.display !== 'none'));
    }
}

let requestReadableMode = false;

function showLogTab(name, button) {
    ['request', 'response', 'error'].forEach(x => document.getElementById(`log-${x}`).classList.add('hidden'));
    document.getElementById('log-request-readable').classList.add('hidden');
    document.getElementById('log-request-toggle').classList.add('hidden');

    if (name === 'request') {
        document.getElementById('log-request-toggle').classList.remove('hidden');
        applyRequestFormat();
    } else {
        document.getElementById(`log-${name}`).classList.remove('hidden');
    }

    document.querySelectorAll('.tab').forEach(x => x.classList.remove('active'));
    button.classList.add('active');
}

function toggleRequestFormat() {
    requestReadableMode = !requestReadableMode;
    applyRequestFormat();
}

// Chuyển đổi hiển thị tab Request giữa JSON gốc và dạng dễ đọc.
function applyRequestFormat() {
    applyReadableToggle('log-request', 'log-request-readable', 'log-request-toggle', requestReadableMode);
}

function applyReadableToggle(preId, readableId, toggleId, readableMode) {
    const pre = document.getElementById(preId);
    const readable = document.getElementById(readableId);
    const toggle = document.getElementById(toggleId);

    if (readableMode) {
        pre.classList.add('hidden');
        readable.classList.remove('hidden');
        toggle.textContent = '{ } JSON gốc';
    } else {
        readable.classList.add('hidden');
        pre.classList.remove('hidden');
        toggle.textContent = '📖 Dễ đọc';
    }
}

// Dựng HTML dạng hội thoại, giải mã nội dung JSON lồng (unicode \uXXXX -> ký tự thật).
function buildReadableRequest(requestJson) {
    let parsed;
    try { parsed = JSON.parse(requestJson); }
    catch { return '<p class="rd-empty">Không thể phân tích nội dung để hiển thị dạng dễ đọc.</p>'; }

    // RequestJson được lưu dạng object { model, messages: [...], ... }; lấy mảng hội thoại bên trong.
    // Vẫn chấp nhận trường hợp requestJson là mảng messages trần để tương thích ngược.
    let messages = parsed;
    if (parsed && !Array.isArray(parsed) && Array.isArray(parsed.messages)) messages = parsed.messages;
    if (!Array.isArray(messages)) messages = [messages];
    if (!messages.length) return '<p class="rd-empty">Không có nội dung.</p>';

    return messages.map(m => {
        const role = (m && m.role) ? String(m.role) : 'message';
        const bodyHtml = renderReadableContent(m ? m.content : m);
        return `<div class="rd-msg rd-msg-${escapeHtml(role)}">
            <div class="rd-role">${escapeHtml(role)}</div>
            <div class="rd-body">${bodyHtml}</div>
        </div>`;
    }).join('');
}

function renderReadableContent(content) {
    if (content === null || content === undefined) return '<span class="rd-muted">(trống)</span>';

    let value = content;
    if (typeof content === 'string') {
        const parsed = tryParseJson(content);
        if (parsed === undefined) return `<div class="rd-text">${escapeHtml(content)}</div>`;
        value = parsed;
    }
    if (value !== null && typeof value === 'object') return renderReadableObject(value);
    return `<div class="rd-text">${escapeHtml(String(value))}</div>`;
}

function renderReadableObject(obj) {
    if (Array.isArray(obj)) {
        if (!obj.length) return '<span class="rd-muted">[]</span>';
        return '<ul class="rd-list">' + obj.map(v => `<li>${renderReadableValue(v)}</li>`).join('') + '</ul>';
    }
    return Object.entries(obj).map(([k, v]) =>
        `<div class="rd-field"><span class="rd-key">${escapeHtml(k)}</span><div class="rd-fieldval">${renderReadableValue(v)}</div></div>`
    ).join('');
}

function renderReadableValue(v) {
    if (v === null || v === undefined) return '<span class="rd-muted">null</span>';
    if (Array.isArray(v)) {
        if (!v.length) return '<span class="rd-muted">[]</span>';
        return '<ul class="rd-list">' + v.map(item => `<li>${renderReadableValue(item)}</li>`).join('') + '</ul>';
    }
    if (typeof v === 'object') return `<pre class="rd-pre">${escapeHtml(JSON.stringify(v, null, 2))}</pre>`;
    return `<span class="rd-text">${escapeHtml(String(v))}</span>`;
}

function tryParseJson(s) {
    try { return JSON.parse(s); }
    catch { return undefined; }
}

function prettyJson(value) {
    if (!value) return '';
    try { return JSON.stringify(JSON.parse(value), null, 2); }
    catch { return value; }
}

function formatDateTime(value) {
    if (!value) return '-';
    return new Date(value).toLocaleString();
}

// escapeHtml dùng chung ở site.js (nạp qua _Layout trước file này).

document.getElementById('logs-modal')
    .addEventListener('click', function (e) {

        if (e.target.id === 'logs-modal') {
            closeLogsModal();
        }
    });

// Dropdowns filter immediately on change; text/date/number fields filter on Enter (or the button).
document.querySelectorAll('#logs-filters .logs-filter-input').forEach(function (el) {
    if (el.tagName === 'SELECT') {
        el.addEventListener('change', applyLogFilters);
    } else {
        el.addEventListener('keydown', function (e) {
            if (e.key === 'Enter') applyLogFilters();
        });
    }
});

// ===== Live agent indicator + activity (debug) popup =====
// Icon feed dùng eventIconHtml() (Bootstrap Icons) khai báo ở site.js.

async function pollActiveAgents() {
    let active = [];
    try {
        const response = await fetch(`/AgentDashboard/ActiveAgents?projectId=${PROJECT_ID}`);
        if (response.ok) active = await response.json();
    } catch { /* transient: keep last state, try again next tick */ }

    const byId = new Map(active.map(a => [a.agentId, a]));
    document.querySelectorAll('.agent-row').forEach(card => {
        const info = byId.get(card.dataset.agentId);
        card.classList.toggle('running', !!info);
        const pill = card.querySelector('.running-indicator');
        if (pill) {
            pill.title = info
                ? `Đang chạy: ${info.taskTitle || info.taskType} — bấm để xem hoạt động`
                : 'Agent đang chạy — bấm để xem hoạt động';
        }
    });

    setTimeout(pollActiveAgents, 3000);
}

async function pollAgentStats() {
    let data;
    try {
        const response = await fetch(`/AgentDashboard/AgentStats?projectId=${PROJECT_ID}`);
        if (response.ok) data = await response.json();
    } catch { /* transient: keep last values, retry next tick */ }

    if (data) updateAgentStats(data);

    setTimeout(pollAgentStats, 3000);
}

function updateAgentStats(data) {
    const totalTokens = Number(data.totalTokens || 0);
    const byId = new Map((data.agents || []).map(a => [a.agentId, a]));

    const totalEl = document.getElementById('total-tokens');
    if (totalEl) totalEl.textContent = totalTokens.toLocaleString();

    document.querySelectorAll('.agent-row').forEach(row => {
        const stat = byId.get(row.dataset.agentId);
        const tokens = Number(stat ? stat.totalTokens : 0);
        const calls = Number(stat ? stat.calls : 0);
        const sharePct = totalTokens > 0 ? (tokens / totalTokens) * 100 : 0;

        const tokensCell = row.querySelector('.agent-tokens');
        if (tokensCell) tokensCell.textContent = tokens.toLocaleString();

        const callsCell = row.querySelector('.agent-calls');
        if (callsCell) callsCell.textContent = calls.toLocaleString();

        const fill = row.querySelector('.share-fill');
        if (fill) fill.style.width = sharePct.toFixed(2) + '%';
        const shareLabel = row.querySelector('.share-label');
        if (shareLabel) shareLabel.textContent = sharePct.toFixed(1) + '%';

        const activityCell = row.querySelector('.agent-last-activity');
        if (activityCell) {
            const lastUtc = stat && stat.lastActivityUtc;
            activityCell.innerHTML = lastUtc
                ? `<span class="last-activity" data-utc="${escapeHtml(lastUtc)}">${escapeHtml(formatDateTime(lastUtc))}</span>`
                : '<span class="muted">—</span>';
        }
    });
}

let activityAgentId = null;
let activityAfterSeq = 0;
let activityTimer = null;

function openAgentActivity(agentId, name) {
    activityAgentId = agentId;
    activityAfterSeq = 0;
    if (activityTimer) { clearTimeout(activityTimer); activityTimer = null; }

    document.getElementById('agent-activity-modal').style.display = 'flex';
    document.getElementById('activity-subtitle').textContent = `Agent: ${name}`;
    document.getElementById('activity-meta').innerHTML = '';
    document.getElementById('activity-feed').innerHTML = '<p class="activity-empty">Đang tải hoạt động…</p>';

    loadAgentActivity();
}

function closeAgentActivity() {
    activityAgentId = null;
    if (activityTimer) { clearTimeout(activityTimer); activityTimer = null; }
    document.getElementById('agent-activity-modal').style.display = 'none';
}

async function loadAgentActivity() {
    const agentId = activityAgentId;
    if (!agentId) return;

    let data;
    try {
        const url = `/AgentDashboard/AgentActivity?projectId=${PROJECT_ID}&agentId=${agentId}&afterSeq=${activityAfterSeq}`;
        const response = await fetch(url);
        if (!response.ok) throw new Error(`HTTP ${response.status}`);
        data = await response.json();
    } catch {
        // Network blip — retry while the popup is still on this agent.
        if (activityAgentId === agentId) activityTimer = setTimeout(loadAgentActivity, 2500);
        return;
    }

    // User may have closed/switched agents while the request was in flight.
    if (activityAgentId !== agentId) return;

    renderActivityMeta(data);

    const feed = document.getElementById('activity-feed');
    const events = data.events || [];

    if (activityAfterSeq === 0 && !events.length) {
        feed.innerHTML = '<p class="activity-empty">'
            + (data.hasActivity
                ? 'Chưa có thao tác nào được ghi nhận cho task này.'
                : 'Agent này hiện không có task nào.')
            + '</p>';
    } else {
        appendActivityEvents(feed, events);
    }

    if (events.length) activityAfterSeq = data.lastEventSeq;

    // Keep polling only while the task is still running and the popup is open.
    if (data.isRunning) {
        activityTimer = setTimeout(loadAgentActivity, 1500);
    }
}

function renderActivityMeta(data) {
    const meta = document.getElementById('activity-meta');
    const status = data.taskStatus || (data.hasActivity ? '-' : 'Idle');
    const statusHtml = data.isRunning
        ? `<span class="activity-live"><span class="pulse-dot"></span>${escapeHtml(status)}</span>`
        : `<b>${escapeHtml(status)}</b>`;

    const parts = [`<span>Status: ${statusHtml}</span>`];
    if (data.taskTitle) parts.push(`<span>Task: <b>${escapeHtml(data.taskTitle)}</b></span>`);
    if (data.taskType) parts.push(`<span>Type: <b>${escapeHtml(data.taskType)}</b></span>`);
    if (data.attempt) parts.push(`<span>Attempt: <b>${escapeHtml(String(data.attempt))}</b></span>`);
    if (data.startedAt) parts.push(`<span>Started: <b>${formatDateTime(data.startedAt)}</b></span>`);
    if (data.error) parts.push(`<span style="color:#991b1b;">Error: ${escapeHtml(data.error)}</span>`);

    meta.innerHTML = parts.join('');
}

function appendActivityEvents(feed, events) {
    if (!events.length) return;

    // Clear the placeholder on the first batch.
    const placeholder = feed.querySelector('.activity-empty');
    if (placeholder) placeholder.remove();

    for (const ev of events) {
        const icon = eventIconHtml(ev.kind);
        const row = document.createElement('div');
        row.className = 'act-event';

        let html = `<span class="act-icon">${icon}</span><div class="act-main">`
            + `<div class="act-msg"><span>${escapeHtml(ev.message)}</span>`
            + `<span class="act-time">${formatDateTime(ev.at)}</span></div>`;
        if (ev.detail) {
            html += `<details class="act-detail"><summary>chi tiết</summary><pre>${escapeHtml(ev.detail)}</pre></details>`;
        }
        html += '</div>';

        row.innerHTML = html;
        feed.appendChild(row);
    }

    feed.scrollTop = feed.scrollHeight;
}

document.getElementById('agent-activity-modal')
    .addEventListener('click', function (e) {
        if (e.target.id === 'agent-activity-modal') closeAgentActivity();
    });

const deliveryConfigModal = document.getElementById('delivery-config-modal');
if (deliveryConfigModal) {
    deliveryConfigModal.addEventListener('click', function (e) {
        if (e.target.id === 'delivery-config-modal') closeDeliveryConfig();
    });
}

const reviseModal = document.getElementById('revise-modal');
if (reviseModal) {
    reviseModal.addEventListener('click', function (e) {
        if (e.target.id === 'revise-modal') closeReviseModal();
    });
}

pollActiveAgents();
pollAgentStats();

// ===== Delivery gate: duyệt / từ chối / chạy-lại các bước sau POC =====
// Chỉ chạy khi #delivery-gate được render (user có quyền DeliveryAdvance); ngược lại no-op.
(function () {
    const gate = document.getElementById('delivery-gate');
    if (!gate) return;

    const statusEl = document.getElementById('dg-status');
    const timelineEl = document.getElementById('dg-timeline');
    const bannerEl = document.getElementById('dg-banner');
    const approveForm = document.getElementById('dg-approve-form');
    const rejectForm = document.getElementById('dg-reject-form');
    const retryForm = document.getElementById('dg-retry-form');
    const reviseBtn = document.getElementById('dg-revise-btn');
    const reviseNote = document.getElementById('dg-revise-note');
    const reviseRemaining = document.getElementById('revise-remaining');
    const runIdInputs = [
        document.getElementById('dg-approve-runid'),
        document.getElementById('dg-reject-runid'),
        document.getElementById('dg-retry-runid'),
        document.getElementById('dg-revise-runid')
    ];

    const COLOR = {
        Queued: '#64748B', Running: '#2563EB', Completed: '#16A34A',
        Failed: '#DC2626', Canceled: '#64748B', WaitingForHuman: '#D97706', Retrying: '#2563EB'
    };

    function badge(status) {
        const c = COLOR[status] || '#64748B';
        return `<span class="dg-badge" style="background:${c}1A;color:${c};border:1px solid ${c}55;">${escapeHtml(status)}</span>`;
    }

    // Nhãn + biểu tượng cho từng trạng thái bước (khớp lớp CSS .dg-step.<state>).
    // Các trạng thái tĩnh không hiện chữ để timeline gọn hơn; marker/màu vẫn thể hiện tiến độ.
    const STEP_LABEL = {
        done: '', running: 'Đang chạy', next: '',
        failed: 'Thất bại', pending: ''
    };

    function stepMarker(state, ordinal) {
        if (state === 'done') return '✓';
        if (state === 'failed') return '✕';
        if (state === 'running') return '<span class="pulse-dot"></span>';
        if (state === 'next') return '→';
        return String(ordinal);
    }

    function renderTimeline(pipeline) {
        if (!timelineEl) return;
        if (!Array.isArray(pipeline) || pipeline.length === 0) {
            timelineEl.innerHTML = '';
            return;
        }
        timelineEl.innerHTML = pipeline.map((s, i) => {
            const state = Object.prototype.hasOwnProperty.call(STEP_LABEL, s.state) ? s.state : 'pending';
            return `<li class="dg-step ${state}">
                <span class="dg-step-marker">${stepMarker(state, i + 1)}</span>
                <span class="dg-step-body">
                    <span class="dg-step-title">${escapeHtml(s.title)}</span>
                    ${STEP_LABEL[state] ? `<span class="dg-step-state">${STEP_LABEL[state]}</span>` : ''}
                </span>
            </li>`;
        }).join('');
    }

    function setForms(approve, reject, retry, revise) {
        approveForm.style.display = approve ? '' : 'none';
        rejectForm.style.display = reject ? '' : 'none';
        retryForm.style.display = retry ? '' : 'none';
        if (reviseBtn) reviseBtn.style.display = revise ? '' : 'none';
    }

    // Ghi chú số vòng chỉnh sửa đã dùng — hiện cạnh các nút cổng duyệt (và trong popup) để người
    // duyệt biết còn được gửi nhận xét mấy lần trước khi chỉ còn Duyệt/Từ chối.
    function setReviseRounds(used, limit, waiting) {
        const exhausted = used >= limit;
        const text = exhausted
            ? `Đã hết ${used}/${limit} vòng chỉnh sửa cho bước này — chỉ còn Duyệt hoặc Từ chối.`
            : `Đã dùng ${used}/${limit} vòng chỉnh sửa cho bước này.`;
        if (reviseNote) {
            reviseNote.style.display = waiting && used > 0 ? '' : 'none';
            reviseNote.textContent = text;
        }
        if (reviseRemaining) reviseRemaining.textContent = text;
        return !exhausted;
    }

    function setRunId(id) {
        runIdInputs.forEach(input => { if (input) input.value = id || ''; });
    }

    async function poll() {
        let data;
        try {
            const response = await fetch(`/AgentDashboard/WorkflowStatus?projectId=${PROJECT_ID}`);
            data = await response.json();
        } catch {
            setTimeout(poll, 4000);
            return;
        }

        // Cổng chỉ áp cho workflow delivery (sau khi Approve requirement). Run "Requirement" (sinh tài liệu)
        // không có cổng ở đây — user vẫn đang ở màn hình Requirements.
        if (!data.hasWorkflow || data.runKind !== 'Delivery') {
            gate.style.display = 'none';
            setTimeout(poll, 4000);
            return;
        }

        gate.style.display = '';
        // Bước hiện tại cho popup "Yêu cầu chỉnh sửa" (khối ghi chú POC chỉ hiện ở cổng PocPreview).
        gate.dataset.stage = data.currentStage || '';
        setRunId(data.runId);
        if (statusEl) statusEl.innerHTML = `${escapeHtml(data.runName || 'Delivery')} · ${badge(data.runStatus)}`;
        renderTimeline(data.pipeline);

        if (data.runStatus === 'WaitingForHuman') {
            // Cổng POC: POC chưa đúng = requirement cần điều chỉnh → việc của user (chat BA → Approve lại),
            // không phải TeamDev. Vì vậy ẩn nút "Từ chối" ở bước POC; các bước sau vẫn cho từ chối.
            // "Yêu cầu chỉnh sửa" thì được ở MỌI cổng (kể cả POC — sửa cho bám spec, không đổi requirement),
            // tới khi hết số vòng cho phép.
            const isPocGate = data.currentStage === 'PocPreview';
            const canRevise = setReviseRounds(data.revisionRoundsUsed || 0, data.revisionRoundsLimit || 0, true);
            bannerEl.innerHTML = '';
            setForms(true, !isPocGate, false, canRevise);
        } else if (data.runStatus === 'Failed') {
            const err = (data.tasks || []).map(t => t.error).filter(Boolean).join('\n');
            bannerEl.innerHTML = `<div class="dg-msg fail">✗ Workflow thất bại.${err ? `<pre class="dg-err">${escapeHtml(err)}</pre>` : ''}</div>`;
            setReviseRounds(0, 0, false);
            setForms(false, false, true, false);
        } else if (data.isCompleted) {
            bannerEl.innerHTML = `<div class="dg-msg ok">✓ Hoàn tất tất cả các bước. <a href="/Projects/DownloadSource?projectId=${PROJECT_ID}">⬇ Tải source code</a></div>`;
            setReviseRounds(0, 0, false);
            setForms(false, false, false, false);
        } else if (data.runStatus === 'Canceled') {
            bannerEl.innerHTML = `<div class="dg-msg fail">✗ Đã hủy. Quay lại Requirements, bổ sung với BA rồi Approve để chạy phiên bản mới.</div>`;
            setReviseRounds(0, 0, false);
            setForms(false, false, false, false);
        } else {
            // Running / Queued / Retrying...
            bannerEl.innerHTML = '';
            setReviseRounds(0, 0, false);
            setForms(false, false, false, false);
        }

        setTimeout(poll, data.isTerminal ? 5000 : 2500);
    }

    poll();
})();
