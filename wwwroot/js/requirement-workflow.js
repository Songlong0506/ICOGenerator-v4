(function () {
    const PID = window.REQUIREMENTS_PROJECT_ID;
    const panels = Array.from(document.querySelectorAll('.workflow-progress[data-run-id]'));

    if (!panels.length) return;

    // Nút "Duyệt & tiếp tục" nằm ở composer (cạnh "Write Requirement"), ẩn cho tới
    // khi có workflow chờ duyệt. Giữ tham chiếu để bật/tắt theo trạng thái poll.
    const approveForm = document.getElementById('approveStageForm');
    const approveRunInput = document.getElementById('approveStageRunId');

    // Form ẩn để chạy lại bước thất bại; banner Failed sẽ điền runId rồi submit.
    const retryForm = document.getElementById('retryWorkflowForm');
    const retryRunInput = document.getElementById('retryWorkflowRunId');

    const COLOR = {
        Queued: '#64748B',
        Running: '#2563EB',
        Completed: '#16A34A',
        Failed: '#DC2626',
        Canceled: '#64748B',
        WaitingForHuman: '#D97706',
        Blocked: '#D97706',
        NeedsReview: '#D97706',
        Retrying: '#2563EB'
    };

    function badge(status) {
        const color = COLOR[status] || '#64748B';
        return `<span class="wf-badge" style="background:${color}1A;color:${color};border:1px solid ${color}55;">${status}</span>`;
    }

    // escapeHtml dùng chung ở site.js (nạp qua _Layout trước file này).

    const EVENT_ICON = {
        start: '🚀',
        setup: '⚙️',
        thinking: '🤔',
        tool: '🔧',
        observation: '📥',
        final: '✅',
        completed: '🎉',
        error: '❌'
    };

    function maybeScrollPanel(panel, data) {
        if (panel.dataset.autoscroll !== 'true') return;

        panel.scrollIntoView({ block: 'nearest' });

        if (data.isTerminal) {
            panel.dataset.autoscroll = 'false';
        }
    }

    function ensureSkeleton(panel) {
        const bodyEl = panel.querySelector('.wf-body');
        if (bodyEl.querySelector('.wf-feed')) return;

        bodyEl.innerHTML =
            '<div class="wf-feed"></div>' +
            '<div class="wf-activity" style="display:none;">' +
                '<span class="wf-typing"><span></span><span></span><span></span></span>' +
                '<span class="wf-activity-text"></span>' +
            '</div>' +
            // Khu "đang gõ": hiển thị token model sinh ra theo thời gian thực (giống xem agent làm việc).
            '<div class="wf-stream" style="display:none;"></div>' +
            '<div class="wf-banner-slot"></div>';
    }

    // Nối token vào khu stream, giữ phần đuôi để DOM không phình theo cả lần sinh dài.
    function renderToken(panel, text) {
        const stream = panel.querySelector('.wf-stream');
        if (!stream) return;

        let buffer = (panel._stream || '') + text;
        if (buffer.length > 4000) buffer = buffer.slice(buffer.length - 4000);
        panel._stream = buffer;

        stream.textContent = buffer;
        stream.style.display = 'block';
        stream.scrollTop = stream.scrollHeight;

        const activity = panel.querySelector('.wf-activity');
        if (activity && activity.style.display === 'none') {
            const textEl = panel.querySelector('.wf-activity-text');
            if (textEl && !textEl.textContent) textEl.textContent = 'Agent đang soạn nội dung…';
            activity.style.display = 'flex';
        }
    }

    // Một milestone mới = đoạn sinh trước đã chốt (đã ghi vào feed) → xóa preview để chờ đoạn kế.
    function resetStream(panel) {
        panel._stream = '';
        const stream = panel.querySelector('.wf-stream');
        if (stream) {
            stream.textContent = '';
            stream.style.display = 'none';
        }
    }

    function appendEvents(panel, events) {
        if (!events.length) return;

        const feed = panel.querySelector('.wf-feed');

        for (const ev of events) {
            const icon = EVENT_ICON[ev.kind] || '•';
            const time = new Date(ev.at).toLocaleTimeString();
            const item = document.createElement('div');
            item.className = `wf-event wf-event-${escapeHtml(ev.kind)}`;

            let html =
                `<span class="wf-event-icon">${icon}</span>` +
                `<div class="wf-event-main">` +
                    `<div class="wf-event-msg">${escapeHtml(ev.message)}` +
                        `<span class="wf-event-time">${escapeHtml(time)}</span></div>`;

            if (ev.detail) {
                html += `<details class="wf-event-detail"><summary>chi tiết</summary><pre>${escapeHtml(ev.detail)}</pre></details>`;
            }

            html += '</div>';
            item.innerHTML = html;
            feed.appendChild(item);
        }

        feed.scrollTop = feed.scrollHeight;
    }

    function updateActivity(panel, data) {
        const activity = panel.querySelector('.wf-activity');
        const textEl = panel.querySelector('.wf-activity-text');
        const running = data.runStatus === 'Running' || data.runStatus === 'Queued';

        if (!running) {
            activity.style.display = 'none';
            return;
        }

        const lastMsg = panel.dataset.lastMsg ||
            (data.runStatus === 'Queued' ? 'Đang chờ agent nhận task…' : 'Agent đang xử lý…');

        textEl.textContent = lastMsg;
        activity.style.display = 'flex';
    }

    function showComposerApprove(data) {
        if (!approveForm) return;
        approveRunInput.value = data.runId || '';
        approveForm.dataset.runId = data.runId || '';
        approveForm.style.display = '';
    }

    function hideComposerApprove(runId) {
        if (!approveForm) return;
        // Chỉ ẩn khi nút đang gắn với chính run này, tránh một run đã kết thúc
        // che mất nút duyệt của run khác đang chờ.
        if (approveForm.dataset.runId && approveForm.dataset.runId !== String(runId)) return;
        approveForm.style.display = 'none';
        approveForm.dataset.runId = '';
    }

    function updateBanner(panel, data) {
        const slot = panel.querySelector('.wf-banner-slot');

        // Chỉ reset "gate" key khi KHÔNG còn ở trạng thái chờ duyệt.
        if (data.runStatus !== 'WaitingForHuman') slot.dataset.gate = '';

        // Cổng duyệt giờ là nút cố định ở composer (cạnh "Write Requirement"),
        // chỉ bật khi bước hiện tại đã xong và đang chờ duyệt.
        if (data.runStatus === 'WaitingForHuman') showComposerApprove(data);
        else hideComposerApprove(data.runId);

        if (data.isCompleted) {
            if (data.runKind === 'Requirement') {
                slot.innerHTML = data.needsMoreInfo
                    ? `<div class="wf-banner wf-wait">❓ Cần bổ sung thông tin trước khi sinh tài liệu — xem câu hỏi BA trong khung chat.</div>`
                    : `<div class="wf-banner wf-ok">✓ Đã tạo/cập nhật tài liệu requirement.</div>`;

                // Reload đúng 1 lần để hiển thị tài liệu draft + tin nhắn BA mới.
                const reloadKey = 'wf-reloaded-' + panel.dataset.runId;
                if (!sessionStorage.getItem(reloadKey)) {
                    sessionStorage.setItem(reloadKey, '1');
                    setTimeout(() => location.reload(), 1200);
                }
            } else {
                slot.innerHTML = `<div class="wf-banner wf-ok">✓ Hoàn tất tất cả các bước. <a href="/Projects/Mockup?projectId=${PID}" target="_blank">Xem demo POC</a> · <a href="/Projects/DownloadSource?projectId=${PID}">⬇ Tải source code</a></div>`;
            }
        } else if (data.runStatus === 'WaitingForHuman') {
            // Tránh dựng lại banner mỗi nhịp poll: chỉ render lại khi sang cổng mới.
            if (slot.dataset.gate === data.currentStage) return;
            slot.dataset.gate = data.currentStage;

            const pocLink = data.pocReady
                ? ` <a href="/Projects/Mockup?projectId=${PID}" target="_blank">Xem POC</a>`
                : '';
            const nextHint = data.nextStageTitle
                ? ` Bước kế: <b>${escapeHtml(data.nextStageTitle)}</b>.`
                : '';

            slot.innerHTML =
                `<div class="wf-banner wf-wait">⏸️ Bước hiện tại đã xong — chờ duyệt.${nextHint}${pocLink}</div>`;
        } else if (data.runStatus === 'Canceled') {
            slot.innerHTML = `<div class="wf-banner wf-fail">✗ Đã hủy. Hãy bổ sung requirement với BA, bấm “Write Requirement” rồi “Approve” để chạy lại.</div>`;
        } else if (data.runStatus === 'Failed') {
            const err = (data.tasks || []).map(t => t.error).filter(Boolean).join('\n');
            slot.innerHTML =
                `<div class="wf-banner wf-fail">✗ Workflow thất bại.${err ? `<pre class="wf-err">${escapeHtml(err)}</pre>` : ''}` +
                `<div class="wf-fail-actions"><button type="button" class="btn retry-wf-btn">↻ Thử lại bước này</button></div></div>`;

            // Nút chạy lại: điền runId vào form ẩn rồi submit (reload trang, worker nhặt task đã re-queue).
            const retryBtn = slot.querySelector('.retry-wf-btn');
            if (retryBtn && retryForm && retryRunInput) {
                retryBtn.addEventListener('click', function () {
                    retryBtn.disabled = true;
                    retryBtn.textContent = 'Đang khởi động lại…';
                    retryRunInput.value = data.runId || '';
                    retryForm.submit();
                });
            }
        } else {
            slot.innerHTML = '';
        }
    }

    // Cập nhật banner/activity/cổng-duyệt từ trạng thái tổng thể của run. KHÔNG nạp event ở đây
    // (SSE đã lo feed); afterSeq cực lớn để status trả về mảng event rỗng, tránh đụng feed.
    async function refreshStatus(panel) {
        const runId = panel.dataset.runId;
        const sub = panel.querySelector('.wf-sub');
        let data;

        try {
            const response = await fetch(`/Requirements/WorkflowStatus?projectId=${PID}&runId=${runId}&afterSeq=999999999`);
            data = await response.json();
        } catch (e) {
            return;
        }

        if (!data.hasWorkflow) return;

        if (sub) sub.innerHTML = `${escapeHtml(data.runName)} · ${badge(data.runStatus)}`;

        ensureSkeleton(panel);

        const running = data.runStatus === 'Running' || data.runStatus === 'Queued';
        if (!running) resetStream(panel);

        updateActivity(panel, data);
        updateBanner(panel, data);
        maybeScrollPanel(panel, data);
    }

    // Xử lý một sự kiện SSE: token thì gõ live, milestone thì ghi vào feed + làm tươi trạng thái.
    function handleEvent(panel, ev) {
        if (ev.kind === 'token') {
            renderToken(panel, ev.message);
            return;
        }

        // Chống trùng khi EventSource tự reconnect (nó phát lại backlog có seq); token có seq 0 luôn qua.
        if (ev.seq && ev.seq <= (panel._maxSeq || 0)) return;

        resetStream(panel);
        appendEvents(panel, [ev]);

        if (ev.seq) {
            panel._maxSeq = ev.seq;
            panel.dataset.afterSeq = String(ev.seq);
        }
        panel.dataset.lastMsg = ev.message;

        const textEl = panel.querySelector('.wf-activity-text');
        const activity = panel.querySelector('.wf-activity');
        if (textEl) textEl.textContent = ev.message;
        if (activity && ev.kind !== 'completed' && ev.kind !== 'error') activity.style.display = 'flex';

        // Các mốc này đổi trạng thái run (bắt đầu / chờ duyệt / xong / lỗi) → đồng bộ lại banner.
        if (ev.kind === 'start' || ev.kind === 'setup' || ev.kind === 'completed' || ev.kind === 'error') {
            refreshStatus(panel);
        }
    }

    function connectStream(panel) {
        if (panel._connected) return;
        panel._connected = true;

        ensureSkeleton(panel);
        refreshStatus(panel);

        // Không có EventSource (trình duyệt cũ) → dùng polling như trước.
        if (typeof EventSource === 'undefined') {
            pollFallback(panel);
            return;
        }

        const runId = panel.dataset.runId;
        let es;
        try {
            es = new EventSource(`/Requirements/WorkflowStream?projectId=${PID}&runId=${runId}&afterSeq=0`);
        } catch (e) {
            pollFallback(panel);
            return;
        }
        panel._es = es;

        es.onmessage = function (e) {
            let ev;
            try { ev = JSON.parse(e.data); } catch (err) { return; }
            handleEvent(panel, ev);
        };

        // Server báo đã hết (run kết thúc): tự đóng để EventSource không reconnect vô ích.
        es.addEventListener('end', function () {
            es.close();
            refreshStatus(panel);
        });

        es.onerror = function () {
            // CONNECTING (0) = đang tự thử lại, cứ để yên. CLOSED (2) = hỏng hẳn → quay về polling.
            if (es.readyState === EventSource.CLOSED) {
                pollFallback(panel);
            }
        };
    }

    // Dự phòng khi SSE không dùng được: poll trạng thái + event theo nhịp (hành vi cũ).
    function pollFallback(panel) {
        if (panel._polling) return;
        panel._polling = true;
        if (panel._es) { try { panel._es.close(); } catch (e) { } }

        async function load() {
            const runId = panel.dataset.runId;
            const afterSeq = panel.dataset.afterSeq || '0';
            const sub = panel.querySelector('.wf-sub');
            let data;

            try {
                const response = await fetch(`/Requirements/WorkflowStatus?projectId=${PID}&runId=${runId}&afterSeq=${afterSeq}`);
                data = await response.json();
            } catch (e) {
                setTimeout(load, 3000);
                return;
            }

            if (!data.hasWorkflow) return;

            if (sub) sub.innerHTML = `${escapeHtml(data.runName)} · ${badge(data.runStatus)}`;

            ensureSkeleton(panel);

            const events = data.events || [];
            appendEvents(panel, events);

            if (events.length) {
                panel.dataset.afterSeq = String(data.lastEventSeq);
                panel._maxSeq = data.lastEventSeq;
                panel.dataset.lastMsg = events[events.length - 1].message;
            }

            updateActivity(panel, data);
            updateBanner(panel, data);
            maybeScrollPanel(panel, data);

            if (!data.isTerminal) setTimeout(load, 1500);
        }

        load();
    }

    panels.forEach(connectStream);
})();
