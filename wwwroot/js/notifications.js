// Chuông thông báo trên topbar. Poll /Notifications/Feed định kỳ để cập nhật badge + danh sách; bấm một
// thông báo mở /Notifications/Open/{id} (server đánh dấu đã đọc rồi điều hướng). Fail-open: lỗi mạng chỉ
// bỏ qua một nhịp poll, không phá trang.
(function () {
    "use strict";

    var root = document.getElementById("notifRoot");
    if (!root) return;

    var bell = document.getElementById("notifBell");
    var badge = document.getElementById("notifBadge");
    var panel = document.getElementById("notifPanel");
    var list = document.getElementById("notifList");
    var markAllBtn = document.getElementById("notifMarkAll");

    var POLL_MS = 30000;

    var TYPE_META = {
        GateAwaitingApproval: { icon: "bi-hourglass-split", cls: "warn" },
        WorkflowCompleted: { icon: "bi-check-circle", cls: "ok" },
        WorkflowFailed: { icon: "bi-x-circle", cls: "danger" },
        PocFeedbackSubmitted: { icon: "bi-chat-square-text", cls: "warn" }
    };

    function antiForgeryToken() {
        var el = document.querySelector('input[name="__RequestVerificationToken"]');
        return el ? el.value : "";
    }

    function escapeHtml(s) {
        return String(s == null ? "" : s)
            .replace(/&/g, "&amp;").replace(/</g, "&lt;").replace(/>/g, "&gt;")
            .replace(/"/g, "&quot;").replace(/'/g, "&#39;");
    }

    function timeAgo(iso) {
        var then = new Date(iso).getTime();
        if (isNaN(then)) return "";
        var s = Math.max(0, Math.floor((Date.now() - then) / 1000));
        if (s < 60) return "vừa xong";
        var m = Math.floor(s / 60);
        if (m < 60) return m + " phút trước";
        var h = Math.floor(m / 60);
        if (h < 24) return h + " giờ trước";
        var d = Math.floor(h / 24);
        return d + " ngày trước";
    }

    function setBadge(count) {
        if (count > 0) {
            badge.textContent = count > 99 ? "99+" : String(count);
            badge.hidden = false;
        } else {
            badge.hidden = true;
        }
    }

    function render(items) {
        if (!items || items.length === 0) {
            list.innerHTML = '<div class="notif-empty">Chưa có thông báo nào.</div>';
            return;
        }
        list.innerHTML = items.map(function (n) {
            var meta = TYPE_META[n.type] || { icon: "bi-bell", cls: "" };
            var project = n.projectName ? '<span class="notif-project">' + escapeHtml(n.projectName) + "</span>" : "";
            return (
                '<a class="notif-item ' + (n.isRead ? "read" : "unread") + '" href="/Notifications/Open/' + encodeURIComponent(n.id) + '">' +
                '<span class="notif-ico ' + meta.cls + '"><i class="bi ' + meta.icon + '"></i></span>' +
                '<span class="notif-body">' +
                '<span class="notif-title">' + escapeHtml(n.title) + "</span>" +
                '<span class="notif-msg">' + escapeHtml(n.message) + "</span>" +
                '<span class="notif-meta">' + project + '<span class="notif-time">' + timeAgo(n.createdAt) + "</span></span>" +
                "</span></a>"
            );
        }).join("");
    }

    function refresh() {
        fetch("/Notifications/Feed", { headers: { "Accept": "application/json" }, credentials: "same-origin" })
            .then(function (r) { return r.ok ? r.json() : null; })
            .then(function (data) {
                if (!data) return;
                setBadge(data.unreadCount || 0);
                render(data.items || []);
            })
            .catch(function () { /* fail-open: bỏ qua một nhịp poll */ });
    }

    function togglePanel(open) {
        var show = open === undefined ? panel.hidden : open;
        panel.hidden = !show;
        bell.setAttribute("aria-expanded", show ? "true" : "false");
        if (show) refresh();
    }

    bell.addEventListener("click", function (e) {
        e.stopPropagation();
        togglePanel();
    });

    document.addEventListener("click", function (e) {
        if (!panel.hidden && !root.contains(e.target)) togglePanel(false);
    });

    markAllBtn.addEventListener("click", function (e) {
        e.stopPropagation();
        fetch("/Notifications/MarkAllRead", {
            method: "POST",
            headers: { "RequestVerificationToken": antiForgeryToken() },
            credentials: "same-origin"
        })
            .then(function () { refresh(); })
            .catch(function () { /* fail-open */ });
    });

    // Nạp lần đầu + poll định kỳ.
    refresh();
    setInterval(refresh, POLL_MS);
})();
