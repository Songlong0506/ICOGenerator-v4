// Modal "Chia sẻ project" (trang Requirements): danh sách thành viên + thêm/gỡ bằng username.
// escapeHtml dùng chung ở site.js.
(function () {
    "use strict";

    var modal = document.getElementById("shareModal");
    if (!modal) {
        // Người không có quyền chia sẻ vẫn cần openShareModal tồn tại? Không — nút cũng bị ẩn theo cùng cờ.
        return;
    }

    var projectId = window.REQUIREMENTS_PROJECT_ID;
    var list = document.getElementById("shareMemberList");
    var form = document.getElementById("shareAddForm");
    var input = document.getElementById("shareUsernameInput");
    var messageEl = document.getElementById("shareMessage");

    function antiForgeryToken() {
        var el = document.querySelector('input[name="__RequestVerificationToken"]');
        return el ? el.value : "";
    }

    function postForm(url, fields) {
        var body = new FormData();
        body.append("__RequestVerificationToken", antiForgeryToken());
        Object.keys(fields || {}).forEach(function (k) {
            if (fields[k] != null) body.append(k, fields[k]);
        });
        return fetch(url, { method: "POST", body: body }).then(function (r) {
            if (!r.ok) throw new Error("Request failed");
            return r.json();
        });
    }

    function showMessage(text, ok) {
        if (!messageEl) return;
        if (!text) { messageEl.style.display = "none"; return; }
        messageEl.textContent = text;
        messageEl.className = "share-message " + (ok ? "ok" : "err");
        messageEl.style.display = "block";
    }

    function render(data) {
        if (!data.members.length) {
            list.innerHTML = '<div class="brief-comment-empty">Chưa có thành viên nào — thêm username đồng nghiệp ở trên.</div>';
            return;
        }

        list.innerHTML = data.members.map(function (m) {
            var name = m.displayName ? m.displayName + " (" + m.username + ")" : m.username;
            var remove = data.canManage
                ? '<button type="button" class="share-remove" data-id="' + m.id + '" title="Gỡ thành viên">🗑</button>'
                : "";
            return '<div class="share-member">'
                + "<span>👤 " + escapeHtml(name) + "</span>"
                + remove
                + "</div>";
        }).join("");
    }

    function load() {
        fetch("/Projects/Members?projectId=" + encodeURIComponent(projectId))
            .then(function (r) { if (!r.ok) throw new Error(); return r.json(); })
            .then(render)
            .catch(function () {
                list.innerHTML = '<div class="brief-comment-empty">Không tải được danh sách thành viên.</div>';
            });
    }

    window.openShareModal = function () {
        showMessage("");
        modal.style.display = "flex";
        load();
        if (input) input.focus();
    };

    window.closeShareModal = function () {
        modal.style.display = "none";
    };

    modal.addEventListener("click", function (e) {
        if (e.target === modal) window.closeShareModal();
    });

    if (form) form.addEventListener("submit", function (e) {
        e.preventDefault();
        var username = input.value.trim();
        if (!username) { input.focus(); return; }

        postForm("/Projects/AddMember", { projectId: projectId, username: username })
            .then(function (res) {
                showMessage(res.message, res.ok);
                if (res.ok) input.value = "";
                load();
            })
            .catch(function () { showMessage("Không thêm được thành viên.", false); });
    });

    list.addEventListener("click", function (e) {
        var btn = e.target.closest(".share-remove");
        if (!btn) return;
        if (!confirm("Gỡ thành viên này khỏi project?")) return;

        postForm("/Projects/RemoveMember", { id: btn.dataset.id })
            .then(function (res) {
                showMessage(res.message, res.ok);
                load();
            })
            .catch(function () { showMessage("Không gỡ được thành viên.", false); });
    });
})();
