// Prompt Studio: đổi các mốc thời gian UTC server render sẵn (data-utc) sang giờ địa phương.
(function () {
    'use strict';

    document.addEventListener('DOMContentLoaded', function () {
        document.querySelectorAll('.prompt-time[data-utc]').forEach(function (el) {
            const d = new Date(el.dataset.utc);
            el.textContent = isNaN(d.getTime()) ? '' : d.toLocaleString();
        });
    });
})();
