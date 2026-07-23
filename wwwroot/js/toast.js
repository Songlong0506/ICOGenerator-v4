// Toast notifications dùng chung toàn app.
// - Server: các flash message (TempData) được render thành <div class="server-toast" data-toast-type="..">…</div>
//   (xem Views/Shared/_Flash.cshtml + các view). Script này nhặt chúng lúc tải trang và bắn toast.
// - Client: gọi window.toast(message, type) hoặc window.Toast.show(message, { type, duration, html }).
// Toast nổi ở góc trên-phải, tự ẩn sau vài giây, hover thì tạm dừng đếm giờ, có nút đóng.
(function () {
    "use strict";

    var ICONS = {
        success: "bi-check-circle-fill",
        error: "bi-exclamation-octagon-fill",
        warning: "bi-exclamation-triangle-fill",
        info: "bi-info-circle-fill"
    };

    var container = null;
    function ensureContainer() {
        if (container && document.body.contains(container)) return container;
        container = document.querySelector(".toast-container");
        if (!container) {
            container = document.createElement("div");
            container.className = "toast-container";
            container.setAttribute("aria-live", "polite");
            container.setAttribute("aria-atomic", "false");
            document.body.appendChild(container);
        }
        return container;
    }

    function normalizeType(type) {
        if (type === "ok") return "success";
        if (type === "danger") return "error";
        if (type === "warn") return "warning";
        if (ICONS.hasOwnProperty(type)) return type;
        return "info";
    }

    function show(message, opts) {
        if (message == null || message === "") return null;
        opts = opts || {};
        var type = normalizeType(opts.type);
        var duration = typeof opts.duration === "number" ? opts.duration : (type === "error" ? 7000 : 4500);

        var host = ensureContainer();

        var el = document.createElement("div");
        el.className = "toast toast--" + type;
        el.setAttribute("role", type === "error" ? "alert" : "status");

        var icon = document.createElement("i");
        icon.className = "toast-icon bi " + (ICONS[type] || ICONS.info);
        icon.setAttribute("aria-hidden", "true");

        var body = document.createElement("div");
        body.className = "toast-body";
        if (opts.html) { body.innerHTML = message; } else { body.textContent = message; }

        var close = document.createElement("button");
        close.type = "button";
        close.className = "toast-close";
        close.setAttribute("aria-label", "Đóng");
        close.innerHTML = "&times;";

        el.appendChild(icon);
        el.appendChild(body);
        el.appendChild(close);
        host.appendChild(el);

        // Kích hoạt transition vào sau khi node đã ở trong DOM.
        requestAnimationFrame(function () { el.classList.add("toast--in"); });

        var timer = null;
        var removed = false;
        function remove() {
            if (removed) return;
            removed = true;
            if (el.parentNode) el.parentNode.removeChild(el);
        }
        function dismiss() {
            if (timer) { clearTimeout(timer); timer = null; }
            el.classList.remove("toast--in");
            el.classList.add("toast--out");
            el.addEventListener("transitionend", remove, { once: true });
            setTimeout(remove, 400); // fallback nếu transitionend không bắn.
        }

        close.addEventListener("click", dismiss);

        if (duration > 0) {
            timer = setTimeout(dismiss, duration);
            el.addEventListener("mouseenter", function () { if (timer) { clearTimeout(timer); timer = null; } });
            el.addEventListener("mouseleave", function () { if (!removed) timer = setTimeout(dismiss, 2000); });
        }

        return el;
    }

    // Nhặt flash render sẵn từ server, bắn thành toast rồi gỡ node gốc khỏi trang.
    function processServerToasts(root) {
        var scope = root || document;
        var nodes = scope.querySelectorAll(".server-toast");
        Array.prototype.forEach.call(nodes, function (node) {
            var type = node.getAttribute("data-toast-type") || "info";
            var html = (node.innerHTML || "").trim();
            if (node.parentNode) node.parentNode.removeChild(node);
            if (html) show(html, { type: type, html: true });
        });
    }

    window.Toast = { show: show, processServerToasts: processServerToasts };

    // Cú pháp gọn cho client: window.toast("Đã lưu", "success").
    window.toast = function (message, type, opts) {
        opts = opts || {};
        if (type) opts.type = type;
        return show(message, opts);
    };

    if (document.readyState === "loading") {
        document.addEventListener("DOMContentLoaded", function () { processServerToasts(); });
    } else {
        processServerToasts();
    }
})();
