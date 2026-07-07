// Trang Ma trận truy vết: nút "Phân tích (lại)" POST đồng bộ (một lời gọi LLM — có thể tới vài phút),
// hiện trạng thái chờ, xong thì reload để server render ma trận mới. Giờ hiển thị theo local time.
(function () {
    'use strict';

    function formatTime(utc) {
        const d = new Date(utc);
        return isNaN(d.getTime()) ? '' : d.toLocaleString();
    }

    document.addEventListener('DOMContentLoaded', function () {
        document.querySelectorAll('.trace-time[data-utc]').forEach(function (el) {
            el.textContent = formatTime(el.dataset.utc);
        });

        const form = document.getElementById('trace-build-form');
        if (!form) return;

        form.addEventListener('submit', async function (e) {
            e.preventDefault();

            const btn = document.getElementById('trace-build-btn');
            const status = document.getElementById('trace-build-status');
            const errorBox = document.getElementById('trace-build-error');
            const token = form.querySelector('input[name="__RequestVerificationToken"]').value;

            btn.disabled = true;
            errorBox.hidden = true;
            status.hidden = false;
            status.textContent = 'Đang phân tích… AI đang đối chiếu tài liệu, code và test (có thể mất vài phút).';

            try {
                const response = await fetch(window.TRACE.buildUrl, {
                    method: 'POST',
                    headers: { 'Content-Type': 'application/x-www-form-urlencoded' },
                    body: new URLSearchParams({ projectId: window.TRACE.projectId, __RequestVerificationToken: token })
                });
                if (!response.ok) throw new Error('HTTP ' + response.status);
                const result = await response.json();

                if (result.ok) {
                    location.reload();
                    return;
                }
                errorBox.textContent = result.error || 'Phân tích thất bại — hãy thử lại.';
                errorBox.hidden = false;
            } catch {
                errorBox.textContent = 'Không gọi được phân tích (mạng/timeout) — hãy thử lại.';
                errorBox.hidden = false;
            } finally {
                btn.disabled = false;
                status.hidden = true;
            }
        });
    });
})();
