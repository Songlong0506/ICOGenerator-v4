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
            '  <h3 class="dh-title">Lịch sử tài liệu</h3>' +
            '  <div class="dh-body">' +
            '    <aside class="dh-list" id="dh-list"></aside>' +
            '    <section class="dh-diff-panel">' +
            '      <div class="dh-diff-head">' +
            '        <span id="dh-diff-title" class="dh-diff-title"></span>' +
            '        <span class="dh-legend">' +
            '          <span class="dh-legend-item added">+ thêm</span>' +
            '          <span class="dh-legend-item removed">− xóa</span>' +
            '        </span>' +
            '      </div>' +
            '      <div class="dh-input" id="dh-input" hidden></div>' +
            '      <div class="dh-diff" id="dh-diff"><p class="dh-empty">Chọn một revision để xem thay đổi.</p></div>' +
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
            list.innerHTML = '<p class="dh-empty">Chưa có lịch sử cho tài liệu này (lịch sử chỉ được ghi từ các lần sinh/sửa mới).</p>';
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

    // Khối "vì sao đổi" ngay trên diff: các lượt user đã dẫn tới revision đang chọn (câu chat, các ghi
    // chú ghim trên bản xem trước Product Brief, phản hồi POC chuyển về). Nằm TRÊN diff chứ không phải
    // cuối popup vì lý do phải đọc trước cái đã đổi — và phải đổi theo từng revision, không phải một
    // khối chung của cả tài liệu. Không có lượt nào ⇒ ẩn hẳn (tài liệu kỹ thuật sinh trong pipeline
    // thường không có lượt user nào xen giữa hai bản; lúc đó changeNote ở cột trái đã nói đủ).
    function renderInputs(data) {
        const el = modalEl.querySelector('#dh-input');
        const turns = (data && data.inputs) || [];

        if (!turns.length) {
            el.hidden = true;
            el.innerHTML = '';
            return;
        }

        el.hidden = false;
        el.innerHTML =
            '<div class="dh-input-head">Người dùng đã gửi gì trước bản này' +
            (data.inputsTruncated ? ' <span class="dh-input-more">(chỉ hiện ' + turns.length + ' lượt gần nhất)</span>' : '') +
            '</div>' +
            turns.map(function (t) {
                return '<div class="dh-input-turn">' +
                    '<span class="dh-input-time">' + formatTime(t.createdAt) + '</span>' +
                    '<span class="dh-input-msg">' + escapeHtml(t.message) + '</span>' +
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
        diffEl.innerHTML = '<p class="dh-empty">Đang tải diff…</p>';
        clearInputs();

        try {
            const response = await fetch('/Requirements/DocumentRevisionDiff?id=' + encodeURIComponent(revisionId));
            if (!response.ok) throw new Error('diff failed');
            const data = await response.json();

            diffTitle.textContent = data.previousRevisionNumber
                ? '#' + data.previousRevisionNumber + ' → #' + data.revisionNumber
                : 'Bản đầu tiên (#' + data.revisionNumber + ')';

            renderInputs(data);

            if (!data.lines.length) {
                diffEl.innerHTML = '<p class="dh-empty">Tài liệu rỗng.</p>';
                return;
            }

            const changed = data.lines.some(function (l) { return l.type !== 'same'; });
            diffEl.innerHTML = (changed ? '' : '<p class="dh-empty">Không có thay đổi so với bản trước.</p>') +
                data.lines.map(function (l) {
                    const prefix = l.type === 'added' ? '+' : l.type === 'removed' ? '−' : ' ';
                    return '<div class="dh-line ' + l.type + '"><span class="dh-line-sign">' + prefix + '</span>' +
                        escapeHtml(l.text) + '</div>';
                }).join('');
        } catch {
            diffEl.innerHTML = '<p class="dh-empty">Không tải được diff.</p>';
        }
    }

    window.openDocHistory = async function (docId) {
        if (!docId) return;
        currentDocId = docId;

        const modal = ensureModal();
        modal.classList.remove('hidden');
        modal.querySelector('#dh-list').innerHTML = '<p class="dh-empty">Đang tải lịch sử…</p>';
        modal.querySelector('#dh-diff').innerHTML = '<p class="dh-empty">Chọn một revision để xem thay đổi.</p>';
        modal.querySelector('#dh-diff-title').textContent = '';
        clearInputs();

        try {
            const response = await fetch('/Requirements/DocumentRevisions?id=' + encodeURIComponent(docId));
            if (!response.ok) throw new Error('history failed');
            const data = await response.json();
            if (currentDocId !== docId) return; // user đã mở doc khác trong lúc chờ

            modal.querySelector('.dh-title').textContent = 'Lịch sử — ' + data.fileName;
            renderRevisionList(data);

            if (data.revisions.length) selectRevision(data.revisions[0].id);
        } catch {
            modal.querySelector('#dh-list').innerHTML = '<p class="dh-empty">Không tải được lịch sử.</p>';
        }
    };

    window.closeDocHistory = function () {
        if (modalEl) modalEl.classList.add('hidden');
    };
})();
