// Trang POC Review: nói chuyện với script annotation trong iframe (postMessage) + CRUD annotation
// qua các endpoint JSON của ProjectsController. escapeHtml dùng chung ở site.js.
(function () {
    "use strict";

    var projectId = window.POC_PROJECT_ID;
    var canComment = !!window.POC_CAN_COMMENT;
    var canAdvance = !!window.POC_CAN_ADVANCE;

    var frame = document.getElementById("pocFrame");
    var list = document.getElementById("pocAnnotationList");
    if (!frame || !list) return;

    var pickBtn = document.getElementById("pocPickBtn");
    var generalBtn = document.getElementById("pocGeneralBtn");
    var pickHint = document.getElementById("pocPickHint");
    var form = document.getElementById("pocAnnotationForm");
    var pickedLabel = document.getElementById("pocPickedLabel");
    var commentInput = document.getElementById("pocCommentInput");
    var cancelBtn = document.getElementById("pocCancelBtn");
    var counts = document.getElementById("pocCounts");
    var submitBtn = document.getElementById("pocSubmitBtn");
    var applyBtn = document.getElementById("pocApplyBtn");
    var actionMessage = document.getElementById("pocActionMessage");

    var pendingPick = null; // { label, path } — phần tử vừa chọn, chờ nhập nhận xét.

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
        if (!actionMessage) return;
        actionMessage.textContent = text;
        actionMessage.className = "poc-action-message " + (ok ? "ok" : "err");
        actionMessage.style.display = "block";
        setTimeout(function () { actionMessage.style.display = "none"; }, 6000);
    }

    // ----- Chế độ chọn phần tử trong iframe -----

    function setPicking(enabled) {
        if (frame.contentWindow)
            frame.contentWindow.postMessage({ type: "poc-annotate-mode", enabled: enabled }, "*");
        if (pickHint) pickHint.style.display = enabled ? "" : "none";
    }

    // Handshake với script trong iframe: poll "hello" tới khi nhận "ready" (bản tin ready đầu tiên
    // có thể rơi nếu mockup load xong trước khi listener ở đây kịp gắn). Dừng sau ~20s cho an toàn.
    var helloAttempts = 0;
    var helloTimer = setInterval(function () {
        if (++helloAttempts > 50) { clearInterval(helloTimer); return; }
        if (frame.contentWindow)
            frame.contentWindow.postMessage({ type: "poc-annotate-hello" }, "*");
    }, 400);

    window.addEventListener("message", function (e) {
        if (e.source !== frame.contentWindow) return;
        var data = e.data || {};

        if (data.type === "poc-annotate-ready") {
            clearInterval(helloTimer);
            if (pickBtn) pickBtn.disabled = false;
            return;
        }

        if (data.type === "poc-annotate-pick") {
            pendingPick = { label: String(data.label || ""), path: String(data.path || "") };
            openComposer(pendingPick.label);
        }
    });

    function openComposer(label) {
        if (!form) return;
        if (pickHint) pickHint.style.display = "none";
        pickedLabel.textContent = label || "(góp ý chung cho cả trang)";
        form.style.display = "";
        commentInput.value = "";
        commentInput.focus();
    }

    function closeComposer() {
        if (!form) return;
        form.style.display = "none";
        pendingPick = null;
        setPicking(false);
    }

    if (pickBtn) pickBtn.addEventListener("click", function () {
        if (form) form.style.display = "none";
        setPicking(true);
    });

    if (generalBtn) generalBtn.addEventListener("click", function () {
        pendingPick = null;
        setPicking(false);
        openComposer("");
    });

    if (cancelBtn) cancelBtn.addEventListener("click", closeComposer);

    if (form) form.addEventListener("submit", function (e) {
        e.preventDefault();
        var comment = commentInput.value.trim();
        if (!comment) { commentInput.focus(); return; }

        postForm("/Projects/AddPocAnnotation", {
            projectId: projectId,
            elementLabel: pendingPick ? pendingPick.label : "",
            elementPath: pendingPick ? pendingPick.path : "",
            comment: comment
        }).then(function (res) {
            if (!res.ok) { showMessage(res.message || "Không lưu được góp ý.", false); return; }
            closeComposer();
            load();
        }).catch(function () { showMessage("Không lưu được góp ý.", false); });
    });

    // ----- Danh sách annotation -----

    var BADGE = {
        Open: { cls: "open", text: "Mới" },
        Submitted: { cls: "submitted", text: "Đã gửi đội Dev" },
        Processed: { cls: "processed", text: "Đã đưa vào chỉnh sửa" }
    };

    function render(data) {
        if (!data.annotations || data.annotations.length === 0) {
            list.innerHTML = '<div class="poc-empty-list">Chưa có góp ý nào. '
                + (canComment ? "Bấm \"🎯 Chọn phần tử\" rồi click vào demo để bắt đầu." : "")
                + "</div>";
        } else {
            list.innerHTML = data.annotations.map(function (a) {
                var badge = BADGE[a.status] || BADGE.Open;
                var author = a.authorDisplayName || a.authorUsername;
                var del = a.canDelete
                    ? '<button type="button" class="poc-del" data-id="' + a.id + '" title="Xóa góp ý">🗑</button>'
                    : "";
                return '<div class="poc-annotation">'
                    + '<div class="poc-el">' + escapeHtml(a.elementLabel) + "</div>"
                    + '<div class="poc-comment">' + escapeHtml(a.comment || "") + "</div>"
                    + '<div class="poc-meta">'
                    + "<span>" + escapeHtml(author) + " · " + new Date(a.createdAt).toLocaleString() + "</span>"
                    + '<span><span class="poc-badge ' + badge.cls + '">' + badge.text + "</span>" + del + "</span>"
                    + "</div></div>";
            }).join("");
        }

        var pendingTotal = data.openCount + data.submittedCount;
        if (counts) counts.textContent = data.openCount + " mới · " + data.submittedCount + " đã gửi";
        if (submitBtn) {
            submitBtn.style.display = data.openCount > 0 ? "" : "none";
            submitBtn.textContent = "📨 Gửi phản hồi cho đội Dev (" + data.openCount + ")";
        }
        if (applyBtn) {
            applyBtn.style.display = pendingTotal > 0 ? "" : "none";
            applyBtn.textContent = "🛠 Yêu cầu agent chỉnh sửa POC (" + pendingTotal + ")";
        }
    }

    function load() {
        fetch("/Projects/PocAnnotations?projectId=" + encodeURIComponent(projectId))
            .then(function (r) { if (!r.ok) throw new Error(); return r.json(); })
            .then(render)
            .catch(function () {
                list.innerHTML = '<div class="poc-empty-list">Không tải được danh sách góp ý.</div>';
            });
    }

    list.addEventListener("click", function (e) {
        var btn = e.target.closest(".poc-del");
        if (!btn) return;
        if (!confirm("Xóa góp ý này?")) return;

        postForm("/Projects/DeletePocAnnotation", { id: btn.dataset.id })
            .then(function (res) {
                if (!res.ok) showMessage(res.message || "Không xóa được.", false);
                load();
            })
            .catch(function () { showMessage("Không xóa được.", false); });
    });

    if (submitBtn) submitBtn.addEventListener("click", function () {
        postForm("/Projects/SubmitPocAnnotations", { projectId: projectId })
            .then(function (res) { showMessage(res.message, res.ok); load(); })
            .catch(function () { showMessage("Không gửi được phản hồi.", false); });
    });

    if (applyBtn) applyBtn.addEventListener("click", function () {
        if (!confirm("Gom mọi góp ý chưa xử lý thành MỘT yêu cầu chỉnh sửa POC và để agent sửa lại?")) return;

        postForm("/Projects/ApplyPocAnnotationsRevision", { projectId: projectId })
            .then(function (res) { showMessage(res.message, res.ok); load(); })
            .catch(function () { showMessage("Không gửi được yêu cầu chỉnh sửa.", false); });
    });

    load();
})();
