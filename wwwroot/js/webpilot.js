// Màn hình thử nghiệm WebPilot: POST một task rồi đọc SSE trả về theo dòng.
//
// Dùng fetch + ReadableStream chứ không phải EventSource vì lượt chạy cần POST (task dài + antiforgery
// token). Nút "Dừng" là AbortController.abort() — request đứt ⇒ RequestAborted bên server ⇒ agent huỷ
// và trình duyệt của nó đóng lại. Không có trạng thái nào phải dọn ở phía client.
(function () {
    'use strict';

    var root = document.getElementById('webpilotRoot');
    if (!root) return;

    var form = document.getElementById('webpilotForm');
    var taskInput = document.getElementById('webpilotTask');
    var runBtn = document.getElementById('webpilotRun');
    var stopBtn = document.getElementById('webpilotStop');
    var logEl = document.getElementById('webpilotLog');
    var shotEl = document.getElementById('webpilotShot');
    var resultEl = document.getElementById('webpilotResult');

    var controller = null;

    if (root.dataset.enabled !== 'true') {
        runBtn.disabled = true;
        taskInput.disabled = true;
    }

    root.querySelectorAll('[data-sample]').forEach(function (btn) {
        btn.addEventListener('click', function () {
            taskInput.value = btn.dataset.sample;
            taskInput.focus();
        });
    });

    form.addEventListener('submit', function (e) {
        e.preventDefault();
        if (controller) return;
        start();
    });

    stopBtn.addEventListener('click', function () {
        if (controller) controller.abort();
    });

    function start() {
        var task = taskInput.value.trim();
        if (!task) { taskInput.focus(); return; }

        logEl.innerHTML = '';
        shotEl.className = 'webpilot-shot empty';
        shotEl.textContent = 'Đang chờ ảnh chụp đầu tiên…';
        setResult('Đang chạy…', '');
        setRunning(true);

        controller = new AbortController();

        var body = new FormData(form);
        body.set('task', task);

        fetch(form.action, { method: 'POST', body: body, signal: controller.signal })
            .then(function (res) {
                if (!res.ok) throw new Error('Máy chủ trả về ' + res.status);
                return readStream(res.body.getReader());
            })
            .catch(function (err) {
                if (err.name === 'AbortError') {
                    addLog('error', 'Đã dừng theo yêu cầu.', null);
                    setResult('Đã dừng theo yêu cầu.', 'error');
                } else {
                    setResult(err.message, 'error');
                }
            })
            .finally(function () {
                controller = null;
                setRunning(false);
            });
    }

    // SSE là các frame ngăn nhau bằng dòng trống; mỗi frame có thể tới làm nhiều mảnh nên phải giữ
    // phần dư lại cho lần đọc sau.
    function readStream(reader) {
        var decoder = new TextDecoder();
        var buffer = '';

        function pump() {
            return reader.read().then(function (chunk) {
                if (chunk.done) return;
                buffer += decoder.decode(chunk.value, { stream: true });

                var parts = buffer.split('\n\n');
                buffer = parts.pop();

                parts.forEach(function (frame) {
                    frame.split('\n').forEach(function (line) {
                        if (line.indexOf('data: ') !== 0) return;
                        var payload = line.slice(6);
                        if (!payload || payload === '{}') return;
                        try { handle(JSON.parse(payload)); } catch (_) { /* frame vỡ: bỏ qua */ }
                    });
                });

                return pump();
            });
        }

        return pump();
    }

    function handle(ev) {
        switch (ev.type) {
            case 'ping':
                return;
            case 'token':
                // Token của lượt suy nghĩ: không đổ vào dòng thời gian, nếu không nó dài gấp trăm lần
                // phần đáng đọc. Kết quả đầy đủ về ở frame done.
                return;
            case 'shot':
                showShot(ev.message, ev.detail);
                addLog('shot', ev.message || 'Đã chụp màn hình', null);
                return;
            case 'done':
                setResult(ev.ok ? (ev.output || '(agent không trả về gì)') : (ev.error || 'Lượt chạy thất bại.'),
                    ev.ok ? '' : 'error');
                return;
            default:
                addLog(ev.type, ev.message, ev.detail);
        }
    }

    function showShot(note, dataUrl) {
        if (!dataUrl) return;
        shotEl.className = 'webpilot-shot';
        shotEl.innerHTML = '';
        var img = document.createElement('img');
        img.src = dataUrl;
        img.alt = note || 'Ảnh chụp màn hình của agent';
        shotEl.appendChild(img);
    }

    function addLog(kind, message, detail) {
        if (!message) return;

        var li = document.createElement('li');
        li.className = 'kind-' + (kind || 'info');
        li.textContent = message;

        if (detail && kind !== 'shot') {
            var span = document.createElement('span');
            span.className = 'webpilot-detail';
            span.textContent = detail;
            li.appendChild(span);
        }

        logEl.appendChild(li);
        logEl.scrollTop = logEl.scrollHeight;
    }

    function setResult(text, state) {
        resultEl.className = 'webpilot-result' + (state ? ' ' + state : '');
        resultEl.textContent = text;
    }

    function setRunning(running) {
        runBtn.disabled = running || root.dataset.enabled !== 'true';
        stopBtn.disabled = !running;
        taskInput.disabled = running;
        runBtn.textContent = running ? 'Đang chạy…' : 'Chạy';
    }
})();
