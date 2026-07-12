// Panel góp ý trên Product Brief (trong modal Requirement của trang Requirements).
// - Bôi đen một đoạn trong .doc-render → đoạn trích thành "anchor" của góp ý.
// - "Chèn vào chat": gom các góp ý còn mở thành một tin nhắn gửi BA (người dùng vẫn duyệt trước khi gửi).
// escapeHtml dùng chung ở site.js.
(function () {
    "use strict";

    var panel = document.getElementById("briefCommentsPanel");
    if (!panel) return;

    var projectId = window.REQUIREMENTS_PROJECT_ID;
    var list = document.getElementById("briefCommentList");
    var countEl = document.getElementById("briefCommentCount");
    var form = document.getElementById("briefCommentForm");
    var input = document.getElementById("briefCommentInput");
    var anchorPreview = document.getElementById("briefAnchorPreview");
    var anchorTextEl = document.getElementById("briefAnchorText");
    var anchorClear = document.getElementById("briefAnchorClear");
    var insertBtn = document.getElementById("briefInsertToChatBtn");

    var currentAnchor = "";
    var openComments = [];
    var loaded = false;

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

    function setAnchor(text) {
        currentAnchor = (text || "").replace(/\s+/g, " ").trim().slice(0, 500);
        if (!anchorPreview) return;
        if (currentAnchor) {
            anchorTextEl.textContent = currentAnchor.length > 120 ? currentAnchor.slice(0, 119) + "…" : currentAnchor;
            anchorPreview.style.display = "";
        } else {
            anchorPreview.style.display = "none";
        }
    }

    if (anchorClear) anchorClear.addEventListener("click", function () { setAnchor(""); });

    // Bôi đen trong phần render tài liệu → bắt đoạn trích làm anchor cho góp ý kế tiếp.
    document.addEventListener("mouseup", function () {
        if (!form) return;
        var sel = window.getSelection();
        if (!sel || sel.isCollapsed) return;

        var node = sel.anchorNode && sel.anchorNode.nodeType === 1 ? sel.anchorNode : sel.anchorNode && sel.anchorNode.parentElement;
        if (!node || !node.closest(".doc-render")) return;

        var text = sel.toString();
        if (text && text.trim()) setAnchor(text);
    });

    function render(data) {
        openComments = data.comments.filter(function (c) { return !c.resolvedAt; });

        if (countEl) countEl.textContent = data.openCount > 0 ? "(" + data.openCount + " mở)" : "";
        if (insertBtn) insertBtn.style.display = data.openCount > 0 ? "" : "none";

        if (!data.comments.length) {
            list.innerHTML = '<div class="brief-comment-empty">Chưa có góp ý nào.</div>';
            return;
        }

        list.innerHTML = data.comments.map(function (c) {
            var author = c.authorDisplayName || c.authorUsername;
            var resolved = !!c.resolvedAt;
            var anchor = c.anchorText
                ? '<div class="brief-quote">“' + escapeHtml(c.anchorText) + '”</div>'
                : "";
            var resolveBtn = c.canResolve
                ? '<button type="button" class="brief-resolve" data-id="' + c.id + '" title="Đánh dấu đã xử lý">✓ Resolve</button>'
                : "";
            var state = resolved
                ? '<span class="brief-resolved-badge">✓ đã xử lý bởi ' + escapeHtml(c.resolvedByUsername || "") + "</span>"
                : resolveBtn;

            return '<div class="brief-comment' + (resolved ? " resolved" : "") + '">'
                + anchor
                + '<div class="brief-comment-content">' + escapeHtml(c.content) + "</div>"
                + '<div class="brief-comment-meta">'
                + "<span>" + escapeHtml(author) + " · " + new Date(c.createdAt).toLocaleString() + "</span>"
                + "<span>" + state + "</span>"
                + "</div></div>";
        }).join("");
    }

    function load() {
        fetch("/Requirements/BriefComments?projectId=" + encodeURIComponent(projectId))
            .then(function (r) { if (!r.ok) throw new Error(); return r.json(); })
            .then(function (data) { loaded = true; render(data); })
            .catch(function () {
                list.innerHTML = '<div class="brief-comment-empty">Không tải được góp ý.</div>';
            });
    }

    if (form) form.addEventListener("submit", function (e) {
        e.preventDefault();
        var content = input.value.trim();
        if (!content) { input.focus(); return; }

        postForm("/Requirements/AddBriefComment", {
            projectId: projectId,
            content: content,
            anchorText: currentAnchor
        }).then(function (res) {
            if (!res.ok) { alert(res.message || "Không gửi được góp ý."); return; }
            input.value = "";
            setAnchor("");
            load();
        }).catch(function () { alert("Không gửi được góp ý."); });
    });

    list.addEventListener("click", function (e) {
        var btn = e.target.closest(".brief-resolve");
        if (!btn) return;

        postForm("/Requirements/ResolveBriefComment", { id: btn.dataset.id })
            .then(function (res) {
                if (!res.ok && res.message) alert(res.message);
                load();
            })
            .catch(function () { alert("Không resolve được góp ý."); });
    });

    // Gom các góp ý còn mở thành một tin nhắn cho BA — điền vào ô chat để NGƯỜI DÙNG duyệt rồi tự gửi,
    // thay vì tự động bắn thẳng (giữ human-in-the-loop, không đốt token ngoài ý muốn).
    if (insertBtn) insertBtn.addEventListener("click", function () {
        if (!openComments.length) return;

        var lines = ["Reviewer đã góp ý về Product Brief như sau, hãy cập nhật tài liệu theo các ý này:"];
        openComments.forEach(function (c, i) {
            var quote = c.anchorText ? ' (về đoạn: "' + c.anchorText + '")' : "";
            lines.push((i + 1) + ". " + c.content + quote + " — " + (c.authorDisplayName || c.authorUsername));
        });

        var messageInput = document.getElementById("messageInput");
        if (!messageInput) return;

        messageInput.value = lines.join("\n");
        messageInput.dispatchEvent(new Event("input")); // để textarea tự giãn chiều cao
        if (typeof closeRequirementModal === "function") closeRequirementModal();
        messageInput.focus();
    });

    // Chỉ tải khi modal Requirement mở lần đầu (panel nằm trong modal — tránh một request thừa mỗi lần vào trang).
    var origOpen = window.openRequirementModal;
    if (typeof origOpen === "function") {
        window.openRequirementModal = function (version) {
            origOpen(version);
            if (!loaded) load();
        };
    } else {
        load();
    }
})();
