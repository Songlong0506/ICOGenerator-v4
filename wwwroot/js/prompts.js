// Prompt Studio: format thời gian + nạp file .md vào editor (import client-side).
(function () {
    'use strict';

    document.addEventListener('DOMContentLoaded', function () {
        document.querySelectorAll('.prompt-time[data-utc]').forEach(function (el) {
            const d = new Date(el.dataset.utc);
            el.textContent = isNaN(d.getTime()) ? '' : d.toLocaleString();
        });

        // Import: đọc file .md người dùng chọn rồi đổ vào textarea — CHƯA lưu gì; người dùng soát lại
        // nội dung rồi bấm "Lưu & kích hoạt" như một lần sửa bình thường (đối xứng với nút Download).
        const importInput = document.getElementById('promptImportInput');
        const editor = document.querySelector('textarea.prompt-editor');
        if (importInput && editor) {
            importInput.addEventListener('change', function () {
                const file = importInput.files && importInput.files[0];
                if (!file) return;
                const reader = new FileReader();
                reader.onload = function () {
                    editor.value = String(reader.result || '');
                    editor.focus();
                };
                reader.readAsText(file);
                importInput.value = ''; // chọn lại cùng file lần nữa vẫn bắn change
            });
        }
    });
})();
