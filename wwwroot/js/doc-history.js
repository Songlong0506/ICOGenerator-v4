// Modal "Lịch sử tài liệu": danh sách revision + diff so với bản liền trước.
// Dùng chung cho trang Requirements (Product Brief) và Agent Dashboard (BRD/SRS/FSD/...):
// trang nào cần chỉ việc nạp file này + doc-history.css rồi gọi openDocHistory(docId).
// Markup modal được tự chèn vào <body> ở lần mở đầu tiên để hai view không phải lặp HTML.
(function () {
    'use strict';

    let modalEl = null;
    let currentDocId = null;

    function escapeHtml(text) {
        const div = document.createElement('div');
        div.textContent = text ?? '';
        return div.innerHTML;
    }

    function formatTime(utc) {
        const d = new Date(utc);
        return isNaN(d.getTime()) ? '' : d.toLocaleString();
    }

    function ensureModal() {
        if (modalEl) return modalEl;

        modalEl = document.createElement('div');
        modalEl.className = 'modal-backdrop hidden';
        modalEl.id = 'dh-modal';
        modalEl.innerHTML =
            '<div class="dh-card">' +
            '  <button class="modal-x" type="button" aria-label="Close">×</button>' +
            '  <h3 class="dh-title">Document history</h3>' +
            '  <div class="dh-body">' +
            '    <aside class="dh-list" id="dh-list"></aside>' +
            '    <section class="dh-diff-panel">' +
            '      <div class="dh-diff-head">' +
            '        <span id="dh-diff-title" class="dh-diff-title"></span>' +
            '        <span class="dh-legend">' +
            '          <span class="dh-legend-item added">+ added</span>' +
            '          <span class="dh-legend-item removed">− removed</span>' +
            '        </span>' +
            '      </div>' +
            '      <div class="dh-input" id="dh-input" hidden></div>' +
            '      <div class="dh-diff" id="dh-diff"><p class="dh-empty">Select a revision to view its changes.</p></div>' +
            '    </section>' +
            '  </div>' +
            '</div>';
        document.body.appendChild(modalEl);

        modalEl.querySelector('.modal-x').addEventListener('click', closeDocHistory);
        modalEl.addEventListener('click', function (e) {
            if (e.target === modalEl) closeDocHistory();
        });
        document.addEventListener('keydown', function (e) {
            if (e.key === 'Escape' && !modalEl.classList.contains('hidden')) closeDocHistory();
        });

        return modalEl;
    }

    function renderRevisionList(data) {
        const list = modalEl.querySelector('#dh-list');
        if (!data.revisions.length) {
            list.innerHTML = '<p class="dh-empty">No history for this document yet (history is only recorded for new generations/edits).</p>';
            return;
        }

        list.innerHTML = data.revisions.map(function (r) {
            return '<button type="button" class="dh-rev" data-rev-id="' + r.id + '">' +
                '<span class="dh-rev-top"><b>#' + r.revisionNumber + '</b>' +
                '<span class="dh-rev-version">' + escapeHtml(r.versionName) + '</span></span>' +
                '<span class="dh-rev-time">' + formatTime(r.createdAt) + '</span>' +
                (r.changeNote ? '<span class="dh-rev-note" title="' + escapeHtml(r.changeNote) + '">' + escapeHtml(r.changeNote) + '</span>' : '') +
                '</button>';
        }).join('');

        list.querySelectorAll('.dh-rev').forEach(function (btn) {
            btn.addEventListener('click', function () { selectRevision(btn.dataset.revId); });
        });
    }

    // Khối ghi chú ngay trên diff: các ghi chú người dùng đã GHIM lên bản xem trước Product Brief khi
    // revision đang chọn còn là bản hiện hành — đúng những gì người dùng chê ở bản này, không phải mọi
    // điều họ từng nhắn trong khung chat (cái đó đổ cả buổi phỏng vấn vào mỗi bản và che mất diff).
    // Nằm TRÊN diff vì phải đổi theo revision đang chọn. Không có ghi chú nào ⇒ ẩn hẳn (tài liệu kỹ
    // thuật sinh trong pipeline không có ghi chú ghim; lúc đó changeNote ở cột trái đã nói đủ).
    function renderInputs(data) {
        const el = modalEl.querySelector('#dh-input');
        const notes = (data && data.notes) || [];

        if (!notes.length) {
            el.hidden = true;
            el.innerHTML = '';
            return;
        }

        el.hidden = false;
        el.innerHTML =
            '<div class="dh-input-head">Notes users pinned on this version (' + notes.length + ')</div>' +
            notes.map(function (n) {
                return '<div class="dh-input-turn">' +
                    '<span class="dh-input-time">' + formatTime(n.createdAt) +
                    (n.createdByUsername ? '<br>' + escapeHtml(n.createdByUsername) : '') + '</span>' +
                    '<span class="dh-input-msg">' +
                    (n.quote ? '<span class="dh-input-quote">“' + escapeHtml(n.quote) + '”</span>' : '') +
                    escapeHtml(n.comment) +
                    '</span>' +
                    '</div>';
            }).join('');
    }

    function clearInputs() {
        const el = modalEl.querySelector('#dh-input');
        el.hidden = true;
        el.innerHTML = '';
    }

    async function selectRevision(revisionId) {
        modalEl.querySelectorAll('.dh-rev').forEach(function (b) {
            b.classList.toggle('selected', b.dataset.revId === revisionId);
        });

        const diffEl = modalEl.querySelector('#dh-diff');
        const diffTitle = modalEl.querySelector('#dh-diff-title');
        diffEl.innerHTML = '<p class="dh-empty">Loading diff…</p>';
        clearInputs();

        try {
            const response = await fetch('/Requirements/DocumentRevisionDiff?id=' + encodeURIComponent(revisionId));
            if (!response.ok) throw new Error('diff failed');
            const data = await response.json();

            diffTitle.textContent = data.previousRevisionNumber
                ? '#' + data.previousRevisionNumber + ' → #' + data.revisionNumber
                : 'First version (#' + data.revisionNumber + ')';

            renderInputs(data);

            if (!data.lines.length) {
                diffEl.innerHTML = '<p class="dh-empty">The document is empty.</p>';
                return;
            }

            const changed = data.lines.some(function (l) { return l.type !== 'same'; });
            diffEl.innerHTML = (changed ? '' : '<p class="dh-empty">No changes from the previous version.</p>') +
                data.lines.map(function (l) {
                    const prefix = l.type === 'added' ? '+' : l.type === 'removed' ? '−' : ' ';
                    return '<div class="dh-line ' + l.type + '"><span class="dh-line-sign">' + prefix + '</span>' +
                        escapeHtml(l.text) + '</div>';
                }).join('');
        } catch {
            diffEl.innerHTML = '<p class="dh-empty">Could not load the diff.</p>';
        }
    }

    window.openDocHistory = async function (docId) {
        if (!docId) return;
        currentDocId = docId;

        const modal = ensureModal();
        modal.classList.remove('hidden');
        modal.querySelector('#dh-list').innerHTML = '<p class="dh-empty">Loading history…</p>';
        modal.querySelector('#dh-diff').innerHTML = '<p class="dh-empty">Select a revision to view its changes.</p>';
        modal.querySelector('#dh-diff-title').textContent = '';
        clearInputs();

        try {
            const response = await fetch('/Requirements/DocumentRevisions?id=' + encodeURIComponent(docId));
            if (!response.ok) throw new Error('history failed');
            const data = await response.json();
            if (currentDocId !== docId) return; // user đã mở doc khác trong lúc chờ

            modal.querySelector('.dh-title').textContent = 'History — ' + data.fileName;
            renderRevisionList(data);

            if (data.revisions.length) selectRevision(data.revisions[0].id);
        } catch {
            modal.querySelector('#dh-list').innerHTML = '<p class="dh-empty">Could not load the history.</p>';
        }
    };

    window.closeDocHistory = function () {
        if (modalEl) modalEl.classList.add('hidden');
    };
})();
