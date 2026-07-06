// Trang Prompt Evals: form scenario (thêm/sửa dùng chung một modal), poll tiến độ run đang chạy,
// modal chi tiết run và so sánh 2 run. openModal/closeModal là helper toàn cục của site.js.
(function () {
    'use strict';

    const POLL_INTERVAL_MS = 3000;

    function escapeHtml(text) {
        const div = document.createElement('div');
        div.textContent = text ?? '';
        return div.innerHTML;
    }

    function formatTime(utc) {
        const d = new Date(utc);
        return isNaN(d.getTime()) ? '' : d.toLocaleString();
    }

    function scoreClass(score) {
        if (score == null) return '';
        if (score >= 4.5) return 'score-great';
        if (score >= 3.5) return 'score-good';
        if (score >= 2.5) return 'score-mid';
        return 'score-bad';
    }

    function statusBadgeClass(status) {
        switch (status) {
            case 'Completed': return 'green';
            case 'Failed': return 'red';
            case 'Running': return 'blue';
            default: return 'gray';
        }
    }

    // ---------- Scenario modal (một form cho cả thêm lẫn sửa) ----------

    window.openCreateScenario = function () {
        const form = document.getElementById('scenarioForm');
        form.action = window.EVALS.createUrl;
        form.reset();
        document.getElementById('scenario-id').value = '';
        document.getElementById('scenarioModalTitle').textContent = 'Thêm Scenario';
        document.getElementById('scenarioSubmitBtn').textContent = 'Thêm Scenario';
        document.getElementById('scenario-active-line').style.display = 'none';
        openModal('scenarioModal');
    };

    window.openEditScenario = function (id) {
        const data = window.EVALS.scenarios[id];
        if (!data) return;

        const form = document.getElementById('scenarioForm');
        form.action = window.EVALS.updateUrl;
        document.getElementById('scenario-id').value = id;
        document.getElementById('scenario-name').value = data.name;
        document.getElementById('scenario-prompt-key').value = data.promptKey;
        document.getElementById('scenario-user-input').value = data.userInput;
        document.getElementById('scenario-criteria').value = data.criteria;
        document.getElementById('scenario-is-active').checked = data.isActive;
        document.getElementById('scenario-active-line').style.display = '';
        document.getElementById('scenarioModalTitle').textContent = 'Sửa Scenario';
        document.getElementById('scenarioSubmitBtn').textContent = 'Lưu thay đổi';
        openModal('scenarioModal');
    };

    // ---------- Poll tiến độ run đang Queued/Running ----------

    async function pollLiveRuns() {
        const liveRows = Array.from(document.querySelectorAll('tr[data-run-id][data-live="true"]'));
        if (!liveRows.length) return;

        await Promise.all(liveRows.map(async function (row) {
            try {
                const response = await fetch('/Evals/RunStatus?id=' + encodeURIComponent(row.dataset.runId));
                if (!response.ok) return;
                const s = await response.json();

                row.querySelector('.run-progress').textContent = s.completedCount + '/' + s.scenarioCount;

                const scoreEl = row.querySelector('.run-score .eval-score');
                scoreEl.textContent = s.averageScore != null ? s.averageScore.toFixed(2) : '–';
                scoreEl.className = 'eval-score ' + scoreClass(s.averageScore);

                const badge = row.querySelector('.run-status .badge');
                badge.textContent = s.status;
                badge.className = 'badge ' + statusBadgeClass(s.status);
                if (s.error) badge.title = s.error;

                if (s.status === 'Completed' || s.status === 'Failed') row.dataset.live = 'false';
            } catch { /* lượt poll lỗi thì thử lại ở nhịp sau */ }
        }));
    }

    document.addEventListener('DOMContentLoaded', function () {
        document.querySelectorAll('.eval-time[data-utc]').forEach(function (el) {
            el.textContent = formatTime(el.dataset.utc);
        });

        if (document.querySelector('tr[data-run-id][data-live="true"]')) {
            pollLiveRuns();
            setInterval(pollLiveRuns, POLL_INTERVAL_MS);
        }
    });

    // ---------- Chi tiết run ----------

    window.openRunDetail = async function (runId) {
        openModal('runDetailModal');
        const body = document.getElementById('runDetailBody');
        body.innerHTML = '<p class="muted">Đang tải…</p>';

        try {
            const response = await fetch('/Evals/RunDetail?id=' + encodeURIComponent(runId));
            if (!response.ok) throw new Error('detail failed');
            const run = await response.json();

            document.getElementById('runDetailTitle').textContent =
                'Chi tiết run — ' + run.targetModelName + (run.note ? ' · ' + run.note : '');

            const header =
                '<div class="eval-detail-meta">' +
                '<span>Judge: <b>' + escapeHtml(run.judgeModelName) + '</b></span>' +
                '<span>Prompt: <b>' + escapeHtml(run.promptKey || 'tất cả') + '</b></span>' +
                '<span>Điểm TB: <b class="eval-score ' + scoreClass(run.averageScore) + '">' +
                    (run.averageScore != null ? run.averageScore.toFixed(2) : '–') + '</b></span>' +
                '<span>Tokens: <b>' + run.totalTokens.toLocaleString() + '</b></span>' +
                (run.error ? '<span class="eval-run-error">' + escapeHtml(run.error) + '</span>' : '') +
                '</div>';

            if (!run.results.length) {
                body.innerHTML = header + '<p class="muted">Chưa có kết quả nào (run đang chạy hoặc lỗi sớm).</p>';
                return;
            }

            body.innerHTML = header + run.results.map(function (r, i) {
                const scoreHtml = r.score != null
                    ? '<span class="eval-score ' + scoreClass(r.score) + '">' + r.score + '/5</span>'
                    : '<span class="badge red" title="' + escapeHtml(r.errorMessage || '') + '">lỗi</span>';
                return '<details class="eval-result"' + (i === 0 ? ' open' : '') + '>' +
                    '<summary><span class="eval-result-name">' + escapeHtml(r.scenarioName) + '</span>' + scoreHtml +
                    '<span class="eval-result-meta">' + (r.targetTokens + r.judgeTokens).toLocaleString() + ' tok · ' +
                    Math.round(r.durationMs / 1000) + 's</span></summary>' +
                    (r.errorMessage ? '<p class="eval-run-error">' + escapeHtml(r.errorMessage) + '</p>' : '') +
                    (r.judgeReasoning ? '<p class="eval-reasoning"><b>Judge:</b> ' + escapeHtml(r.judgeReasoning) + '</p>' : '') +
                    '<pre class="eval-output">' + escapeHtml(r.output || '(không có output)') + '</pre>' +
                    '</details>';
            }).join('');
        } catch {
            body.innerHTML = '<p class="muted">Không tải được chi tiết run.</p>';
        }
    };

    // ---------- So sánh 2 run ----------

    window.onCompareCheckChanged = function () {
        const checked = document.querySelectorAll('.cmp-check:checked');
        // Chỉ cho chọn tối đa 2: chọn cái thứ 3 thì bỏ cái cũ nhất.
        if (checked.length > 2) checked[0].checked = false;
        document.getElementById('compareBtn').disabled =
            document.querySelectorAll('.cmp-check:checked').length !== 2;
    };

    window.openCompare = async function () {
        const checked = Array.from(document.querySelectorAll('.cmp-check:checked'));
        if (checked.length !== 2) return;

        openModal('compareModal');
        const body = document.getElementById('compareBody');
        body.innerHTML = '<p class="muted">Đang tải…</p>';

        try {
            // Run A = run CŨ hơn (checkbox nằm dưới trong bảng vì bảng sort mới nhất trước) để delta đọc là "mới - cũ".
            const response = await fetch('/Evals/Compare?runA=' + encodeURIComponent(checked[1].value) +
                '&runB=' + encodeURIComponent(checked[0].value));
            if (!response.ok) throw new Error('compare failed');
            const cmp = await response.json();

            const head = function (run, label) {
                return '<div class="eval-cmp-run"><span class="eval-cmp-label">' + label + '</span>' +
                    '<b>' + escapeHtml(run.targetModelName) + '</b>' +
                    (run.note ? ' · ' + escapeHtml(run.note) : '') +
                    '<span class="muted"> (' + formatTime(run.createdAt) + ')</span>' +
                    ' — TB: <b class="eval-score ' + scoreClass(run.averageScore) + '">' +
                    (run.averageScore != null ? run.averageScore.toFixed(2) : '–') + '</b></div>';
            };

            const rows = cmp.rows.map(function (r) {
                const delta = r.delta == null ? '–'
                    : (r.delta > 0 ? '+' + r.delta : String(r.delta));
                const deltaClass = r.delta == null ? '' : r.delta > 0 ? 'delta-up' : r.delta < 0 ? 'delta-down' : 'delta-flat';
                return '<tr><td>' + escapeHtml(r.scenarioName) + '</td>' +
                    '<td>' + (r.scoreA != null ? r.scoreA : '–') + '</td>' +
                    '<td>' + (r.scoreB != null ? r.scoreB : '–') + '</td>' +
                    '<td class="' + deltaClass + '">' + delta + '</td></tr>';
            }).join('');

            body.innerHTML = head(cmp.runA, 'A (cũ)') + head(cmp.runB, 'B (mới)') +
                '<div class="table-wrap"><table class="data-table eval-cmp-table">' +
                '<thead><tr><th>Scenario</th><th>A</th><th>B</th><th>Δ (B−A)</th></tr></thead>' +
                '<tbody>' + rows + '</tbody></table></div>';
        } catch {
            body.innerHTML = '<p class="muted">Không tải được so sánh.</p>';
        }
    };
})();
