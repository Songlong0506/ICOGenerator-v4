// Per-project agent dashboard (read-only monitoring): workspace tree, call logs, live activity.
// Razor-provided values come from window.AGENT_DASHBOARD (set inline by the view before this loads).
const DOCS = window.AGENT_DASHBOARD.docs;
const FIRST_DOC_ID = window.AGENT_DASHBOARD.firstDocId;
const PROJECT_ID = window.AGENT_DASHBOARD.projectId;
let activeAgentFilter = null;

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

async function loadAgentLogs(agentId, agentName) {

    document.getElementById('logs-modal').style.display = 'flex';

    document.getElementById('logs-subtitle').textContent =
        `Agent: ${agentName}`;

    const tbody = document.getElementById('logs-tbody');

    tbody.innerHTML =
        '<tr><td colspan="7">Loading...</td></tr>';

    const url =
        `/AgentDashboard/AgentCallLogs?projectId=${PROJECT_ID}&agentId=${agentId}`;

    let logs;
    try {
        const response = await fetch(url);
        if (!response.ok) throw new Error(`HTTP ${response.status}`);
        logs = await response.json();
    } catch (err) {
        tbody.innerHTML =
            '<tr><td colspan="7">Failed to load logs. Please try again.</td></tr>';
        return;
    }

    if (!logs.length) {

        tbody.innerHTML =
            '<tr><td colspan="7">No AI call logs found for this agent.</td></tr>';

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
                ${escapeHtml(x.modelName || x.modelId || '-')}
                <br>
                <small>${escapeHtml(x.endpoint || '')}</small>
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

    document.getElementById('log-detail-meta').textContent = `${log.agentName} · ${log.modelName || log.modelId} · ${formatDateTime(log.createdAt)} · ${log.totalTokens || 0} tokens · ${log.durationMs || 0} ms`;
    document.getElementById('log-request').textContent = prettyJson(log.requestJson);
    document.getElementById('log-response').textContent = prettyJson(log.responseText);
    document.getElementById('log-content').textContent = log.extractedContent || '';
    document.getElementById('log-error').textContent = log.errorMessage || '';
    document.getElementById('log-modal').style.display = 'flex';
    showLogTab('request', document.querySelector('.tab'));
}

function closeLogsPanel() {
    document.getElementById('logs-panel').style.display = 'none';
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

function showLogTab(name, button) {
    ['request', 'response', 'content', 'error'].forEach(x => document.getElementById(`log-${x}`).classList.add('hidden'));
    document.getElementById(`log-${name}`).classList.remove('hidden');
    document.querySelectorAll('.tab').forEach(x => x.classList.remove('active'));
    button.classList.add('active');
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

// ===== Live agent indicator + activity (debug) popup =====
const ACTIVITY_ICON = {
    start: '🚀', setup: '⚙️', thinking: '🤔', tool: '🔧',
    observation: '📥', final: '✅', completed: '🎉', error: '❌'
};

// -- Indicator: poll which agents are running and mark their cards. --
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

// -- Stats: keep the table's Share / Total Tokens / Calls / Last Activity columns live. --
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

// -- Popup: live operation feed for one agent. --
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
        const icon = ACTIVITY_ICON[ev.kind] || '•';
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
    const runIdInputs = [
        document.getElementById('dg-approve-runid'),
        document.getElementById('dg-reject-runid'),
        document.getElementById('dg-retry-runid')
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

    function setForms(approve, reject, retry) {
        approveForm.style.display = approve ? '' : 'none';
        rejectForm.style.display = reject ? '' : 'none';
        retryForm.style.display = retry ? '' : 'none';
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
        setRunId(data.runId);
        if (statusEl) statusEl.innerHTML = `${escapeHtml(data.runName || 'Delivery')} · ${badge(data.runStatus)}`;
        renderTimeline(data.pipeline);

        if (data.runStatus === 'WaitingForHuman') {
            // Cổng POC: POC chưa đúng = requirement cần điều chỉnh → việc của user (chat BA → Approve lại),
            // không phải TeamDev. Vì vậy ẩn nút "Từ chối" ở bước POC; các bước sau vẫn cho từ chối.
            const isPocGate = data.currentStage === 'PocPreview';
            bannerEl.innerHTML = '';
            setForms(true, !isPocGate, false);
        } else if (data.runStatus === 'Failed') {
            const err = (data.tasks || []).map(t => t.error).filter(Boolean).join('\n');
            bannerEl.innerHTML = `<div class="dg-msg fail">✗ Workflow thất bại.${err ? `<pre class="dg-err">${escapeHtml(err)}</pre>` : ''}</div>`;
            setForms(false, false, true);
        } else if (data.isCompleted) {
            bannerEl.innerHTML = `<div class="dg-msg ok">✓ Hoàn tất tất cả các bước. <a href="/Projects/DownloadSource?projectId=${PROJECT_ID}">⬇ Tải source code</a></div>`;
            setForms(false, false, false);
        } else if (data.runStatus === 'Canceled') {
            bannerEl.innerHTML = `<div class="dg-msg fail">✗ Đã hủy. Quay lại Requirements, bổ sung với BA rồi Approve để chạy phiên bản mới.</div>`;
            setForms(false, false, false);
        } else {
            // Running / Queued / Retrying...
            bannerEl.innerHTML = '';
            setForms(false, false, false);
        }

        setTimeout(poll, data.isTerminal ? 5000 : 2500);
    }

    poll();
})();
