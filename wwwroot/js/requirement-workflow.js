(function () {
    const PID = window.REQUIREMENTS_PROJECT_ID;
    // Mỗi panel nay gom NHIỀU run liền kề (data-run-ids) thành một timeline thống nhất. Run "lead"
    // (data-lead-run-id, mới nhất trong nhóm) quyết định badge/banner/activity; các run còn lại chỉ
    // góp event vào feed chung.
    const panels = Array.from(document.querySelectorAll('.workflow-progress[data-run-ids]'));

    if (!panels.length) return;

    // Cổng "Duyệt & tiếp tục"/Retry đã chuyển sang Agent Dashboard. Trang này chỉ hiển thị tiến độ;
    // chỉ người có quyền DeliveryAdvance (TeamDev/Admin) mới được dẫn sang dashboard để thao tác.
    const CAN_ADVANCE = window.REQUIREMENTS_CAN_ADVANCE === true || window.REQUIREMENTS_CAN_ADVANCE === 'true';
    const DASHBOARD_URL = `/AgentDashboard/Index?projectId=${PID}`;

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

    // Icon feed dùng eventIconHtml() (Bootstrap Icons) khai báo ở site.js.

    function runIdsOf(panel) {
        return (panel.dataset.runIds || '')
            .split(',')
            .map(s => s.trim())
            .filter(Boolean);
    }

    function leadRunId(panel) {
        return panel.dataset.leadRunId || runIdsOf(panel).slice(-1)[0];
    }

    // Trạng thái theo từng run (seq đã thấy, con trỏ poll, kết nối SSE) — seq là TOÀN CỤC giữa các run,
    // nên chống trùng phải tính riêng từng run để event run này không "nuốt" event run kia.
    function runState(panel, runId) {
        panel._runs = panel._runs || {};
        return panel._runs[runId] || (panel._runs[runId] = { maxSeq: 0, afterSeq: 0, es: null, polling: false });
    }

    function maybeScrollPanel(panel, data) {
        if (panel.dataset.autoscroll !== 'true') return;

        panel.scrollIntoView({ block: 'nearest' });

        if (data.isTerminal) {
            panel.dataset.autoscroll = 'false';
        }
    }

    // Feed dùng chung cho cả nhóm; mỗi run có một "segment" (display:contents) giữ đúng thứ tự
    // run → seq, nhưng hiển thị liền mạch như một dòng thời gian duy nhất.
    function ensureSkeleton(panel) {
        const bodyEl = panel.querySelector('.wf-body');
        if (bodyEl.querySelector('.wf-feed')) return;

        const segments = runIdsOf(panel)
            .map(id => `<div class="wf-seg" data-run-id="${id}"></div>`)
            .join('');

        bodyEl.innerHTML =
            `<div class="wf-feed">${segments}</div>` +
            '<div class="wf-activity" style="display:none;">' +
                '<span class="wf-typing"><span></span><span></span><span></span></span>' +
                '<span class="wf-activity-text"></span>' +
            '</div>' +
            // Khu "đang gõ": hiển thị token model sinh ra theo thời gian thực (giống xem agent làm việc).
            '<div class="wf-stream" style="display:none;"></div>' +
            '<div class="wf-banner-slot"></div>';
    }

    function segOf(panel, runId) {
        return panel.querySelector(`.wf-seg[data-run-id="${runId}"]`);
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

    function appendEvents(panel, runId, events) {
        if (!events.length) return;

        const seg = segOf(panel, runId);
        const feed = panel.querySelector('.wf-feed');
        if (!seg || !feed) return;

        for (const ev of events) {
            const icon = eventIconHtml(ev.kind);
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
            seg.appendChild(item);
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

    function updateBanner(panel, data) {
        const slot = panel.querySelector('.wf-banner-slot');

        // Chỉ reset "gate" key khi KHÔNG còn ở trạng thái chờ duyệt.
        if (data.runStatus !== 'WaitingForHuman') slot.dataset.gate = '';

        if (data.isCompleted) {
            if (data.runKind === 'Requirement') {
                // Run sinh AI Design Spec (sau Approve) cũng là "Requirement" kind nhưng báo khác: nó dẫn
                // sang delivery (POC). Reload bên dưới sẽ hiện run delivery mà worker vừa tạo, gộp chung panel.
                const isSpec = (data.tasks || []).some(t => t.type === 'AiDesignSpec');
                slot.innerHTML = data.needsMoreInfo
                    ? `<div class="wf-banner wf-wait">❓ Cần bổ sung thông tin trước khi sinh tài liệu — xem câu hỏi BA trong khung chat.</div>`
                    : isSpec
                        ? `<div class="wf-banner wf-ok">✓ Đã tạo AI Design Spec — đang khởi động quy trình dựng POC…</div>`
                        : `<div class="wf-banner wf-ok">✓ Đã tạo/cập nhật tài liệu requirement.</div>`;

                // Reload đúng 1 lần để hiển thị tài liệu draft + tin nhắn BA mới (và gộp run delivery vừa tạo
                // vào cùng panel). Key theo lead run để mỗi hành trình chỉ reload một lần.
                const reloadKey = 'wf-reloaded-' + leadRunId(panel);
                if (!sessionStorage.getItem(reloadKey)) {
                    sessionStorage.setItem(reloadKey, '1');
                    setTimeout(() => location.reload(), 1200);
                }
            } else {
                slot.innerHTML = `<div class="wf-banner wf-ok">✓ Hoàn tất tất cả các bước. <a href="/Projects/PocReview?projectId=${PID}" target="_blank">Xem demo POC</a> · <a href="/Projects/DownloadSource?projectId=${PID}">⬇ Tải source code</a></div>`;
            }
        } else if (data.runStatus === 'WaitingForHuman') {
            // Tránh dựng lại banner mỗi nhịp poll: chỉ render lại khi sang cổng mới.
            if (slot.dataset.gate === data.currentStage) return;
            slot.dataset.gate = data.currentStage;

            // Dẫn sang trang review (xem POC + ghim ghi chú lên phần tử) thay vì trang Mockup trần —
            // ghi chú ghim ở đây sẽ được gom vào "Yêu cầu chỉnh sửa" tại cổng POC.
            const pocLink = data.pocReady
                ? ` <a href="/Projects/PocReview?projectId=${PID}" target="_blank">Xem POC</a>`
                : '';

            if (data.currentStage === 'PocPreview') {
                // Cổng POC là điểm dừng của trang Requirements: chỉ báo POC đã tạo xong + link Xem POC cho
                // MỌI vai trò. KHÔNG nêu bước kế (tài liệu kỹ thuật…) hay dẫn sang duyệt — việc tạo tiếp
                // hay không do đội Dev xử lý ở Agent Dashboard, không đẩy sang quy trình delivery ở đây.
                slot.innerHTML =
                    `<div class="wf-banner wf-ok">✓ POC đã tạo xong. Đội ngũ Dev sẽ tiếp nhận các bước tiếp theo.${pocLink}</div>`;
            } else if (CAN_ADVANCE) {
                // TeamDev/Admin: cổng duyệt sống ở Agent Dashboard → dẫn sang đó để bấm "Duyệt & tiếp tục".
                const nextHint = data.nextStageTitle
                    ? ` Bước kế: <b>${escapeHtml(data.nextStageTitle)}</b>.`
                    : '';
                slot.innerHTML =
                    `<div class="wf-banner wf-wait">⏸️ Bước hiện tại đã xong — chờ duyệt.${nextHint}` +
                    ` <a href="${DASHBOARD_URL}">Mở Agent Dashboard để duyệt</a>${pocLink}</div>`;
            } else {
                // User thường ở các cổng sau POC (hiếm — flow của họ dừng ở POC): báo bàn giao cho đội Dev.
                slot.innerHTML =
                    `<div class="wf-banner wf-ok">✓ Bước hiện tại đã xong. Đội ngũ Dev sẽ tiếp nhận các bước tiếp theo.${pocLink}</div>`;
            }
        } else if (data.runStatus === 'Canceled') {
            slot.innerHTML = `<div class="wf-banner wf-fail">✗ Đã hủy. Hãy bổ sung requirement với BA, bấm “Write Requirement” rồi “Approve” để chạy lại.</div>`;
        } else if (data.runStatus === 'Failed') {
            const err = (data.tasks || []).map(t => t.error).filter(Boolean).join('\n');
            const errBlock = err ? `<pre class="wf-err">${escapeHtml(err)}</pre>` : '';

            if (CAN_ADVANCE) {
                // Retry đã chuyển sang Agent Dashboard (quyền DeliveryAdvance) → dẫn sang đó để chạy lại.
                slot.innerHTML =
                    `<div class="wf-banner wf-fail">✗ Workflow thất bại.${errBlock}` +
                    `<div class="wf-fail-actions"><a class="btn" href="${DASHBOARD_URL}">↻ Mở Agent Dashboard để chạy lại</a></div></div>`;
            } else {
                slot.innerHTML =
                    `<div class="wf-banner wf-fail">✗ Có lỗi khi xử lý bước này. Đội ngũ Dev sẽ kiểm tra.${errBlock}</div>`;
            }
        } else {
            slot.innerHTML = '';
        }
    }

    // Cập nhật badge/banner/activity từ trạng thái tổng thể của LEAD run. KHÔNG nạp event ở đây
    // (SSE đã lo feed); afterSeq cực lớn để status trả về mảng event rỗng, tránh đụng feed.
    async function refreshStatus(panel) {
        const runId = leadRunId(panel);
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

    // Xử lý một sự kiện SSE của một run cụ thể: token thì gõ live (chỉ lead run), milestone thì ghi vào
    // segment của run đó; lead run còn làm tươi badge/banner/activity của cả panel.
    function handleEvent(panel, runId, ev) {
        const isLead = runId === leadRunId(panel);

        if (ev.kind === 'token') {
            if (isLead) renderToken(panel, ev.message);
            return;
        }

        // Chống trùng khi EventSource tự reconnect (nó phát lại backlog có seq); token có seq 0 luôn qua.
        const st = runState(panel, runId);
        if (ev.seq && ev.seq <= st.maxSeq) return;

        if (isLead) resetStream(panel);
        appendEvents(panel, runId, [ev]);

        if (ev.seq) {
            st.maxSeq = ev.seq;
            st.afterSeq = ev.seq;
        }

        if (!isLead) return;

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

        runIdsOf(panel).forEach(runId => connectRunStream(panel, runId));
    }

    function connectRunStream(panel, runId) {
        // Không có EventSource (trình duyệt cũ) → dùng polling như trước.
        if (typeof EventSource === 'undefined') {
            pollRun(panel, runId);
            return;
        }

        let es;
        try {
            es = new EventSource(`/Requirements/WorkflowStream?projectId=${PID}&runId=${runId}&afterSeq=0`);
        } catch (e) {
            pollRun(panel, runId);
            return;
        }
        runState(panel, runId).es = es;

        es.onmessage = function (e) {
            let ev;
            try { ev = JSON.parse(e.data); } catch (err) { return; }
            handleEvent(panel, runId, ev);
        };

        // Server báo đã hết (run kết thúc): tự đóng để EventSource không reconnect vô ích.
        es.addEventListener('end', function () {
            es.close();
            if (runId === leadRunId(panel)) refreshStatus(panel);
        });

        es.onerror = function () {
            // CONNECTING (0) = đang tự thử lại, cứ để yên. CLOSED (2) = hỏng hẳn → quay về polling.
            if (es.readyState === EventSource.CLOSED) {
                pollRun(panel, runId);
            }
        };
    }

    // Dự phòng khi SSE không dùng được: poll trạng thái + event của một run theo nhịp (hành vi cũ).
    function pollRun(panel, runId) {
        const st = runState(panel, runId);
        if (st.polling) return;
        st.polling = true;
        if (st.es) { try { st.es.close(); } catch (e) { } }

        const isLead = runId === leadRunId(panel);

        async function load() {
            const afterSeq = st.afterSeq || 0;
            let data;

            try {
                const response = await fetch(`/Requirements/WorkflowStatus?projectId=${PID}&runId=${runId}&afterSeq=${afterSeq}`);
                data = await response.json();
            } catch (e) {
                setTimeout(load, 3000);
                return;
            }

            if (!data.hasWorkflow) return;

            ensureSkeleton(panel);

            const events = data.events || [];
            appendEvents(panel, runId, events);

            if (events.length) {
                st.afterSeq = data.lastEventSeq;
                st.maxSeq = data.lastEventSeq;
                if (isLead) panel.dataset.lastMsg = events[events.length - 1].message;
            }

            if (isLead) {
                const sub = panel.querySelector('.wf-sub');
                if (sub) sub.innerHTML = `${escapeHtml(data.runName)} · ${badge(data.runStatus)}`;

                updateActivity(panel, data);
                updateBanner(panel, data);
                maybeScrollPanel(panel, data);
            }

            if (!data.isTerminal) setTimeout(load, 1500);
        }

        load();
    }

    panels.forEach(connectStream);
})();
