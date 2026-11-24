// poc-review.js — trang cha của POC Review: nói chuyện với annotator trong iframe POC (poc-annotator.js)
// qua postMessage, và nối nó với BẢNG GHI CHÚ dùng chung (poc-notes.js — bộ lọc, bảng, popup Add Note).
// Mọi thao tác GHI (thêm/xoá/mở lại ghi chú) đều đi từ trang cha (same-origin, có cookie + antiforgery);
// iframe sandbox không gọi được gì.
// Nút Delete: ghi chú CHƯA gửi đi bị xoá hẳn; ghi chú đã gửi nhưng còn hoãn chờ bản demo dựng lại (hoặc
// đã đi một vòng rồi được mở lại) thì thu hồi mềm — rời bảng nhưng dòng còn trong DB.
(function () {
    "use strict";

    const root = document.getElementById("pocReviewRoot");
    const frame = document.getElementById("pocFrame");
    if (!root || !frame || !window.PocNotes) return;

    const commentsUrl = root.dataset.commentsUrl;
    const addUrl = root.dataset.addUrl;
    const withdrawUrl = root.dataset.withdrawUrl;
    const reopenUrl = root.dataset.reopenUrl;
    const projectId = root.dataset.projectId;
    const prettyAnchor = window.PocNotes.prettyAnchor;

    // ĐÃ NGHIỆM THU ⇒ trang chỉ còn đọc. Server mới là chỗ chặn thật (mọi endpoint ghi đều đi qua
    // PocAcceptanceGate); lớp này chỉ để người dùng không bấm vào những nút chắc chắn bị từ chối.
    const locked = root.dataset.locked === "true";

    // Nút "Bật chế độ ghim" là một ICON nổi trên góc khung demo (.poc-frame-actions, cạnh "mở tab riêng"),
    // không có nhãn chữ nhìn thấy — nên trạng thái bật phải đọc được ở những chỗ còn lại: icon rỗng/đặc,
    // nền xanh (CSS bám aria-pressed), aria-pressed + aria-label (screen reader) và title (tooltip).
    const pinModeBtn = document.getElementById("pinModeBtn");
    const pinModeIcon = pinModeBtn.querySelector("i");
    // Chữ của trạng thái TẮT là chữ server đã render lên nút (PocReview.cshtml). Đọc lại từ DOM chứ KHÔNG
    // chép vào đây: một nhãn viết ở hai chỗ thì đổi ở Razor xong, lần bấm tắt đầu tiên đã trả nút về nhãn cũ.
    const pinLabelOff = pinModeBtn.getAttribute("aria-label") || "";
    const pinTitleOff = pinModeBtn.title;
    // Khối chứa nút "Gửi ghi chú cho đội xử lý". Nó chỉ hiện khi CÓ ghi chú Draft — triage/dispatch chỉ
    // quét ghi chú Open, nên với bảng rỗng (hoặc chỉ còn ghi chú Waiting/Apply) nút ấy là một lời mời bấm
    // vào chỗ không có gì để bấm. Razor render sẵn khối này ở trạng thái hidden và refreshSendButton() mới
    // mở ra: bảng nạp bằng fetch SAU khi trang render, và thêm/xoá một ghi chú đổi con số này ngay trong
    // buổi review, không tải lại trang.
    const routeReqEl = root.querySelector(".poc-route-req");
    const sendFeedbackBtn = document.getElementById("pocSendFeedbackBtn");
    const antiForgery = document.querySelector('#pocCommentForm input[name="__RequestVerificationToken"]');
    const uatList = document.getElementById("uatList");

    let pinMode = false;
    let frameReady = false;
    let currentView = ""; // màn hình đang mở trong bản demo — mặc định của dropdown Screen trong popup
    let pendingTour = null; // <li> của bước vừa bấm "chỉ chỗ", chờ annotator trả lời tìm được hay không

    // escapeHtml dùng chung ở site.js (nạp qua _Layout trước file này).

    function postToFrame(msg) {
        if (frame.contentWindow) frame.contentWindow.postMessage(msg, "*");
    }

    function clearTourHint() {
        if (uatList) uatList.querySelectorAll(".uat-step-hint").forEach(el => el.remove());
    }

    // Chỉ chỗ TRƯỢT thì nói ra, đừng im lặng: im lặng để người dùng ngồi soi bản demo tìm một hiệu ứng
    // không bao giờ tới. Mỗi trạng thái là một việc khác nhau họ cần làm tiếp. Ba hàm này ở TOP-LEVEL
    // (không nằm trong khối `if (uatList)` bên dưới) vì listener message gọi tới chúng — khai báo hàm
    // trong khối là block-scoped ở chế độ strict.
    const TOUR_HINT = {
        missing: "Bản demo chưa đánh dấu chỗ cho bước này — bạn tự tìm trên màn hình giúp nhé.",
        unsupported: "Bản demo này dựng trước khi có chỉ dẫn từng bước, nên chưa chỉ chỗ được.",
        hidden: "Chỗ cần thao tác chỉ hiện sau khi bạn làm xong các bước trước."
    };

    function showTourResult(status) {
        const li = pendingTour;
        pendingTour = null;
        clearTourHint();
        if (!li || status === "ok") return;

        const hint = document.createElement("div");
        hint.className = "uat-step-hint";
        hint.textContent = TOUR_HINT[status] || TOUR_HINT.missing;
        li.appendChild(hint);
    }

    function setPinMode(enabled) {
        // Khoá thì không có chế độ ghim: mọi ghi chú mới sẽ bị server từ chối, nên để người dùng đi hết
        // một vòng "bật ghim → click phần tử → gõ ghi chú" rồi mới báo lỗi là tệ nhất trong các cách nói không.
        if (locked) enabled = false;
        pinMode = enabled;
        postToFrame({ type: "poc-mode", enabled: pinMode });
        pinModeBtn.setAttribute("aria-pressed", pinMode ? "true" : "false");
        pinModeBtn.setAttribute("aria-label", pinMode ? "Đang ghim — bấm để tắt" : pinLabelOff);
        pinModeBtn.title = pinMode
            ? "Đang ghim — click vào phần tử trong POC để ghi chú (bấm lại hoặc Esc để tắt)"
            : pinTitleOff;
        if (pinModeIcon) {
            pinModeIcon.classList.toggle("bi-pin-angle-fill", pinMode);
            pinModeIcon.classList.toggle("bi-pin-angle", !pinMode);
        }
    }

    function postForm(url, fields) {
        const fd = new FormData();
        Object.keys(fields).forEach(k => fd.append(k, fields[k]));
        if (antiForgery) fd.append("__RequestVerificationToken", antiForgery.value);
        return fetch(url, { method: "POST", body: fd });
    }

    // Nút gửi mang đúng số ghi chú Draft sẽ đi — triage/dispatch quét MỌI ghi chú Open của project.
    function refreshSendButton(items) {
        const drafts = items.filter(c => c.status === "Open").length;
        if (routeReqEl) routeReqEl.hidden = drafts === 0;
        if (sendFeedbackBtn && !sendFeedbackBtn.disabled) {
            sendFeedbackBtn.textContent = `📤 Gửi ${drafts} ghi chú cho đội xử lý`;
        }
    }

    // ===== Bảng ghi chú =====
    const notes = window.PocNotes.create({
        root: root.querySelector(".poc-notes-table-root"),
        guest: false,
        locked,
        currentView: () => currentView,

        async onSubmit(p) {
            let response;
            try {
                response = await postForm(addUrl, {
                    projectId,
                    pageView: p.pageView,
                    elementLabel: p.elementLabel,
                    elementPath: p.elementPath,
                    xPercent: String(p.xPercent),
                    yPercent: String(p.yPercent),
                    comment: p.comment
                });
            } catch {
                throw new Error("Không gửi được ghi chú — kiểm tra kết nối rồi thử lại.");
            }
            if (!response.ok) throw new Error(await response.text().catch(() => "") || "Không gửi được ghi chú.");
            return await response.json();
        },

        async onWithdraw(item) {
            // Lời nhắc nói ĐÚNG hệ quả của từng ca: ghi chú chưa gửi đi thì mất hẳn; ghi chú đã gửi nhưng
            // còn hoãn thì sẽ KHÔNG được áp vào bản demo dựng lại nữa. Một câu chung là hứa sai một nửa.
            const warning = item.status === "Deferred"
                ? "Xoá ghi chú này? Nó chưa được áp vào bản demo nên bản demo dựng lại sẽ không mang nó nữa."
                : item.wasDispatched
                    ? "Xoá ghi chú này khỏi bảng?"
                    : "Xoá ghi chú này? Ghi chú chưa gửi đi nên sẽ bị xoá hẳn.";
            if (!confirm(warning)) return false;

            try {
                const response = await postForm(withdrawUrl, { id: item.id });
                // 400 = ghi chú đã tới tay agent (không rút được nữa) — nói đúng lý do thay vì "không xoá
                // được", vì người dùng sẽ bấm lại mãi.
                if (!response.ok) throw new Error(response.status === 400 ? await response.text() : "");
            } catch (err) {
                alert(err && err.message ? err.message : "Không xoá được ghi chú.");
                return false;
            }
            return true;
        },

        // "Vẫn chưa đạt": đưa ghi chú đã áp về Draft để nó vào lượt gửi TIẾP THEO, thay vì phải gõ một ghi
        // chú mới trùng nội dung.
        async onReopen(item) {
            try {
                const response = await postForm(reopenUrl, { projectId, id: item.id });
                if (!response.ok) throw new Error();
            } catch {
                alert("Không mở lại được ghi chú.");
                return false;
            }
            return true;
        },

        onFocus(item) {
            // Bảng nằm DƯỚI khung demo: đọc tới dòng thứ mười là khung demo đã trôi lên khỏi màn hình, và
            // cú nháy pin bên trong nó thành ra vô hình. Chỉ cuộn khi khung thật sự đã trôi qua mép trên —
            // còn nhìn thấy thì đừng giật trang dưới tay người dùng.
            if (frame.getBoundingClientRect().top < 0) {
                frame.scrollIntoView({ block: "start", behavior: "smooth" });
            }
            postToFrame({ type: "poc-focus", id: item.id });
        },

        onChange(items) {
            refreshSendButton(items);
            pushCommentsToFrame();
        }
    });

    // Pin chỉ cho ghi chú neo vào một phần tử; số trên pin = số # của dòng trong bảng.
    function pushCommentsToFrame() {
        if (frameReady) postToFrame({ type: "poc-comments", items: notes.pins() });
    }

    async function loadComments() {
        let list = [];
        try {
            const response = await fetch(commentsUrl);
            list = response.ok ? await response.json() : [];
        } catch {
            list = [];
        }
        notes.setItems(list);
    }

    // ===== Nghiệm thu: công tắc HAI CHIỀU trên command bar =====
    //
    // "Approve POC" chốt lại nội dung: từ lúc đó ghi chú trên trang này và chat BA ở màn hình Requirement
    // đều ngừng nhận thay đổi (server chặn thật — xem PocAcceptanceGate, khoá ở giao diện chỉ là lớp
    // ngoài). "Withdraw Approve" là cửa mở khoá, và hai nút không bao giờ cùng có mặt.
    //
    // Xong thì TẢI LẠI TRANG chứ không tự đảo nhãn nút: trạng thái khoá chạm vào gần như mọi thứ trên
    // trang (nút gửi ghi chú, chế độ ghim, nút thu hồi từng dòng, biển trạng thái trong panel ghi chú).
    // Vá từng chỗ bằng JS là dựng bản sao thứ hai của luật khoá — bản sao sẽ trôi lệch khỏi server ngay
    // lần sửa sau.
    const FALLBACK_ERROR = "Something went wrong — please try again.";

    // Hộp xác nhận là hộp CỦA APP (#pocConfirmModal trong PocReview.cshtml), không phải confirm() của
    // trình duyệt — lý do đổi ghi ở chính khối markup đó. Một hộp dùng chung cho cả hai chiều: hai thao
    // tác không bao giờ cùng có mặt (nút kia không được render), nên hai bản sao markup chỉ để trôi lệch.
    // Chữ trong hộp để tiếng Anh cho khớp nhãn nút trên command bar.
    const confirmModal = document.getElementById("pocConfirmModal");
    const confirmTitle = document.getElementById("pocConfirmTitle");
    const confirmNoteText = document.getElementById("pocConfirmNoteText");
    const confirmMsg = document.getElementById("pocConfirmMsg");
    const confirmOk = document.getElementById("pocConfirmOk");

    // Người bấm Cancel/Escape/nút ✕ phải làm ĐÚNG MỘT việc: không chạy thao tác. Nên câu trả lời của hộp
    // đi ra bằng một Promise<boolean> — chỗ gọi giữ nguyên hình dạng "hỏi rồi mới làm" của confirm() cũ.
    let confirmResolve = null;

    function closeConfirm(answer) {
        if (!confirmModal) return;
        confirmModal.classList.add("hidden");
        const resolve = confirmResolve;
        confirmResolve = null;
        if (resolve) resolve(answer);
    }

    function askConfirm(title, note, okLabel) {
        if (!confirmModal) return Promise.resolve(false);

        // Hộp còn đang mở cho một câu hỏi khác (mở bằng bàn phím chẳng hạn) ⇒ trả lời "không" cho câu cũ
        // trước, đừng để lời hứa cũ treo mãi.
        closeConfirm(false);

        confirmTitle.textContent = title;
        confirmNoteText.textContent = note;
        confirmOk.textContent = okLabel;
        confirmOk.disabled = false;
        confirmMsg.textContent = "";
        confirmMsg.classList.add("hidden");
        confirmModal.classList.remove("hidden");
        confirmOk.focus();

        return new Promise(resolve => { confirmResolve = resolve; });
    }

    if (confirmModal) {
        confirmOk.addEventListener("click", () => closeConfirm(true));
        document.getElementById("pocConfirmCancel").addEventListener("click", () => closeConfirm(false));
        document.getElementById("pocConfirmClose").addEventListener("click", () => closeConfirm(false));
        confirmModal.addEventListener("click", e => { if (e.target === confirmModal) closeConfirm(false); });
        document.addEventListener("keydown", e => {
            if (e.key === "Escape" && !confirmModal.classList.contains("hidden")) closeConfirm(false);
        });
    }

    function bindAcceptanceButton(id, url, title, note, okLabel) {
        const btn = document.getElementById(id);
        if (!btn || !url) return;

        btn.addEventListener("click", async function () {
            if (!await askConfirm(title, note, okLabel)) return;

            const label = btn.querySelector(".cbar-label") || btn;
            const original = label.textContent;
            btn.disabled = true;
            label.textContent = "Đang xử lý…";

            const fd = new FormData();
            fd.append("projectId", projectId);
            if (antiForgery) fd.append("__RequestVerificationToken", antiForgery.value);

            let message = null;
            try {
                const response = await fetch(url, { method: "POST", body: fd });
                const data = await response.json().catch(() => null);
                if (data && data.ok) {
                    window.location.reload();
                    return;
                }
                message = (data && data.message) || FALLBACK_ERROR;
            } catch {
                message = FALLBACK_ERROR;
            }

            // Lỗi hiện lại TRONG hộp vừa hỏi (mở lại, nút chính khoá): câu trả lời "không làm được" phải
            // đứng cạnh câu hỏi vừa hỏi, và người dùng còn ngay đó để đóng hoặc thử lại.
            btn.disabled = false;
            label.textContent = original;
            showConfirmError(message);
        });
    }

    function showConfirmError(message) {
        if (!confirmModal) return;
        confirmMsg.textContent = message;
        confirmMsg.classList.remove("hidden");
        confirmOk.disabled = true;
        confirmModal.classList.remove("hidden");
    }

    bindAcceptanceButton(
        "pocAcceptBtn",
        root.dataset.acceptUrl,
        "Approve this demo?",
        "After approval, POC notes and the BA chat on the Requirement screen are locked until you click \"Withdraw Approve\".",
        "Approve POC");

    bindAcceptanceButton(
        "pocWithdrawApproveBtn",
        root.dataset.withdrawAcceptUrl,
        "Withdraw the approval of this demo?",
        "Notes and the BA chat reopen, and the delivery team is told that the approval is no longer valid.",
        "Withdraw Approve");

    // ===== Gửi ghi chú đi xử lý: MỘT nút, hai đường =====
    //
    // Trang này từng bày HAI nút ("nhờ Dev chỉnh bản demo" / "gửi về Requirement") và bắt người xem demo
    // tự phân loại ghi chú của mình — trong khi chính hệ thống làm được phép phân loại ấy, và cả hai nút
    // đều nuốt trọn mọi ghi chú Open nên một buổi review lẫn hai loại thì không nút nào đúng.
    //
    // Nay: bấm gửi → server phân loại từng ghi chú (TriagePocFeedback, KHÔNG đổi trạng thái gì) → hộp xác
    // nhận cho soát và đổi nhóm → gửi một lượt (DispatchPocFeedback), mỗi đường nhận đúng tập con của nó.
    const sendBtn = document.getElementById("pocSendFeedbackBtn");
    const triageUrl = root.dataset.triageUrl;
    const dispatchUrl = root.dataset.dispatchUrl;
    const gateOpen = root.dataset.gateOpen === "true";

    if (sendBtn && triageUrl && dispatchUrl) {
        const modal = document.getElementById("pocDispatchModal");
        const groupsEl = document.getElementById("pocDispatchGroups");
        const noteEl = document.getElementById("pocDispatchNote");
        const msgEl = document.getElementById("pocDispatchMsg");
        const confirmBtn = document.getElementById("pocDispatchConfirm");
        const sendHint = document.getElementById("pocSendFeedbackHint");

        // group: "fix" (chỉnh demo) | "design" (sửa thiết kế màn hình) | "requirement" (sửa tài liệu).
        let triaged = [];          // [{ id, pageView, elementLabel, comment, group, reason }]
        let baConfigured = true;

        const GROUPS = {
            fix: { title: "🛠 Sửa POC", move: "→ chỉ là lỗi trình bày" },
            design: { title: "🧭 Sửa thiết kế màn hình", move: "→ đây là góp ý thiết kế màn hình" },
            requirement: { title: "↩ Sửa requirement", move: "→ đây là hiểu sai yêu cầu" }
        };

        function closeModal() {
            modal.classList.add("hidden");
        }

        function itemHtml(item, i) {
            const where = item.pageView ? `[${escapeHtml(item.pageView)}] ` : "";
            const what = item.elementLabel ? `<b>${escapeHtml(prettyAnchor(item.elementLabel))}</b> — ` : "";
            // Mỗi ghi chú chuyển được sang hai nhóm còn lại. Hai đường đắt (thiết kế, tài liệu) cần agent BA.
            const moves = Object.keys(GROUPS).filter(g => g !== item.group).map(g => {
                const disabled = g !== "fix" && !baConfigured;
                return `<button type="button" class="poc-dispatch-move" data-i="${i}" data-group="${g}"${disabled ? " disabled" : ""}>${GROUPS[g].move}</button>`;
            }).join("");

            return `
                <li class="poc-dispatch-item">
                    <div class="poc-dispatch-item-text">${where}${what}${escapeHtml(item.comment)}</div>
                    ${item.reason ? `<div class="poc-dispatch-reason">${escapeHtml(item.reason)}</div>` : ""}
                    ${moves}
                </li>`;
        }

        function groupHtml(key, indexes) {
            return `
                <div class="poc-dispatch-group">
                    <h4>${GROUPS[key].title}<span class="poc-count">(${indexes.length})</span></h4>
                    ${indexes.length ? `<ul>${indexes.map(i => itemHtml(triaged[i], i)).join("")}</ul>` : '<p class="muted poc-dispatch-empty">— không có —</p>'}
                </div>`;
        }

        function renderGroups() {
            const fix = [];
            const design = [];
            const req = [];
            triaged.forEach((item, i) => ({ fix, design, requirement: req })[item.group].push(i));
            const docs = req.length + design.length;

            const warnings = [];
            // Đường tài liệu/thiết kế ĐÈ đường chỉnh demo trong cùng một lượt (xem DispatchPocFeedbackUseCase):
            // POC sắp dựng lại từ tài liệu đã sửa nên vá HTML bây giờ là phí một vòng trong trần. Ghi chú
            // nhóm kia được HOÃN tới vòng dựng mới — phải nói rõ, không thì người dùng tưởng chúng đã đi.
            if (docs && fix.length) {
                warnings.push(`Lượt này gửi <b>${docs}</b> điểm về sửa tài liệu/thiết kế. <b>${fix.length}</b> ghi chú chỉnh trình bày được <b>hoãn lại</b>: bản demo sẽ dựng lại từ tài liệu mới nên chưa vá bây giờ — khi anh/chị duyệt lại requirement, chúng <b>tự được áp dụng</b> ngay trong bản demo mới.`);
            } else if (fix.length && !gateOpen) {
                warnings.push("Quy trình đã đi qua bước bản demo nên vòng chỉnh sửa demo không còn mở — các ghi chú ở nhóm này chưa gửi đi được. Ghi chú nào thật ra là góp ý thiết kế hay hiểu sai yêu cầu thì chuyển sang nhóm dưới.");
            }

            groupsEl.innerHTML = `
                ${groupHtml("fix", fix)}
                ${groupHtml("design", design)}
                ${groupHtml("requirement", req)}
                ${warnings.map(w => `<p class="poc-dispatch-warn">${w}</p>`).join("")}`;

            // Không còn đường nào chạy được cho lựa chọn hiện tại ⇒ khóa nút gửi thay vì để server từ chối.
            confirmBtn.disabled = docs === 0 && (fix.length === 0 || !gateOpen);
            confirmBtn.textContent = docs
                ? `Gửi ${docs} điểm về sửa tài liệu/thiết kế`
                : (fix.length ? `Gửi ${fix.length} ghi chú cho Dev` : "Gửi");
        }

        groupsEl.addEventListener("click", function (e) {
            const move = e.target.closest(".poc-dispatch-move");
            if (!move) return;
            const item = triaged[Number(move.dataset.i)];
            const target = move.dataset.group;
            if (!item || !GROUPS[target]) return;
            if (target !== "fix" && !baConfigured) return;
            item.group = target;
            msgEl.textContent = "";
            renderGroups();
        });

        sendBtn.addEventListener("click", async function () {
            sendBtn.disabled = true;
            const original = sendBtn.textContent;
            sendBtn.textContent = "Đang phân loại ghi chú…";

            const fd = new FormData();
            fd.append("projectId", projectId);
            if (antiForgery) fd.append("__RequestVerificationToken", antiForgery.value);

            let data = null;
            try {
                const response = await fetch(triageUrl, { method: "POST", body: fd });
                data = await response.json().catch(() => null);
            } catch {
                data = null;
            }

            sendBtn.disabled = false;
            sendBtn.textContent = original;
            refreshSendButton(notes.items()); // nhãn "Gửi n ghi chú" có thể đã đổi trong lúc chờ phân loại

            if (!data || !data.ok) {
                if (sendHint) sendHint.textContent = (data && data.message) || "Không phân loại được ghi chú — thử lại sau.";
                return;
            }

            triaged = data.items.map(i => Object.assign({}, i, {
                group: i.requirement ? "requirement" : (i.design ? "design" : "fix")
            }));
            baConfigured = data.baConfigured !== false;

            // Máy không phân loại được ⇒ mọi ghi chú rơi về nhóm rẻ; nói rõ đó là mặc định an toàn chứ
            // không phải kết luận, để người dùng biết mình phải tự soát.
            // Tên KHÁC `notes` (bảng ghi chú ở ngoài): khai `const notes` trong khối này che biến ngoài ngay từ đầu
            // khối, nên lời gọi `notes.items()` phía trên ném "Cannot access 'notes' before initialization" và
            // hộp xác nhận không bao giờ mở.
            const hints = [];
            if (!data.classified) hints.push("Hệ thống chưa phân loại được lượt này, tạm xếp tất cả vào nhóm chỉnh bản demo — anh/chị soát giúp.");
            if (!baConfigured) hints.push("Chưa cấu hình agent BA nên đường sửa tài liệu và sửa thiết kế đang khóa.");
            noteEl.textContent = hints.join(" ");

            msgEl.textContent = "";
            renderGroups();
            modal.classList.remove("hidden");
        });

        confirmBtn.addEventListener("click", async function () {
            const fd = new FormData();
            fd.append("projectId", projectId);
            if (antiForgery) fd.append("__RequestVerificationToken", antiForgery.value);
            triaged.forEach(item => fd.append(
                item.group === "requirement" ? "requirementIds" : (item.group === "design" ? "designIds" : "fixIds"), item.id));

            confirmBtn.disabled = true;
            const original = confirmBtn.textContent;
            confirmBtn.textContent = "Đang gửi…";

            let data = null;
            try {
                const response = await fetch(dispatchUrl, { method: "POST", body: fd });
                data = await response.json().catch(() => null);
            } catch {
                data = null;
            }

            if (data && data.ok) {
                closeModal();
                if (sendHint) {
                    sendHint.textContent = data.message;
                    sendHint.classList.add("poc-route-ok");
                }
                await loadComments();
                return;
            }

            // Danh sách vừa đổi dưới chân (người khác gửi/xóa) ⇒ bảng phân loại đang cầm đã cũ: đóng hộp
            // và nạp lại thay vì để người dùng bấm gửi tiếp theo một bản không còn đúng.
            if (data && data.reload) {
                closeModal();
                if (sendHint) sendHint.textContent = data.message;
                await loadComments();
                return;
            }

            msgEl.textContent = (data && data.message) || "Không gửi được — thử lại sau.";
            confirmBtn.disabled = false;
            confirmBtn.textContent = original;
        });

        document.getElementById("pocDispatchCancel").addEventListener("click", closeModal);
        document.getElementById("pocDispatchClose").addEventListener("click", closeModal);
        modal.addEventListener("click", e => { if (e.target === modal) closeModal(); });
        document.addEventListener("keydown", e => {
            if (e.key === "Escape" && !modal.classList.contains("hidden")) closeModal();
        });
    }

    // ===== Tin nhắn từ annotator trong iframe =====

    window.addEventListener("message", function (e) {
        if (e.source !== frame.contentWindow || !e.data || typeof e.data !== "object") return;

        // Mọi message từ annotator đều chứng tỏ nó đã sẵn sàng (phòng khi poc-ready bị lỡ do race lúc load).
        frameReady = true;

        // Màn hình đang mở trong bản demo: annotator báo kèm poc-ready và mỗi lần đổi (poc-view).
        if (typeof e.data.view === "string") currentView = e.data.view;

        if (e.data.type === "poc-ready") {
            pushCommentsToFrame();
            postToFrame({ type: "poc-mode", enabled: pinMode });
        } else if (e.data.type === "poc-pick") {
            notes.openAdd(e.data, currentView);
            setPinMode(false); // đã chọn xong phần tử — tắt để không click nhầm khi đang gõ
        } else if (e.data.type === "poc-exit-mode") {
            setPinMode(false);
        } else if (e.data.type === "poc-tour-result") {
            showTourResult(e.data.status);
        } else if (e.data.type === "poc-pin-click") {
            notes.highlight(e.data.id);
        }
    });

    if (locked) {
        pinModeBtn.disabled = true;
        pinModeBtn.title = "Bản demo đã được nghiệm thu — bấm \"Withdraw Approve\" để ghi chú trở lại";
    }

    pinModeBtn.addEventListener("click", () => setPinMode(!pinMode));

    // ===== Toàn màn hình bản demo =====
    // Phóng CẢ THẺ .poc-frame-panel, không phóng riêng iframe: pin ghi chú và khung tô sáng "chỉ chỗ"
    // do trang cha vẽ ĐÈ lên khung demo, phóng mỗi iframe là đẩy chúng xuống dưới lớp toàn màn hình —
    // mất đúng thứ người ta đang review. Hai đường: Fullscreen API thật, và nếu trình duyệt/chính sách
    // từ chối (promise reject) thì rơi về lớp phủ position:fixed bằng class .is-fullscreen. Cả hai
    // thoát bằng Esc (đường thật do trình duyệt lo, đường phủ do handler bên dưới) hoặc bấm lại nút.
    const fullscreenBtn = document.getElementById("pocFullscreenBtn");
    const framePanel = frame.closest(".poc-frame-panel");

    if (fullscreenBtn && framePanel) {
        const fsLabelOff = fullscreenBtn.title;
        const fsLabelOn = "Thoát toàn màn hình (Esc)";

        // Trạng thái đọc từ DOM chứ không giữ biến riêng: người dùng thoát fullscreen bằng Esc hoặc nút
        // của trình duyệt thì không cú click nào chạy qua đây, một biến cờ sẽ lệch ngay lần đầu.
        function isFullscreen() {
            return document.fullscreenElement === framePanel || framePanel.classList.contains("is-fullscreen");
        }

        function syncFullscreenBtn() {
            const on = isFullscreen();
            fullscreenBtn.setAttribute("aria-pressed", on ? "true" : "false");
            fullscreenBtn.title = on ? fsLabelOn : fsLabelOff;
            fullscreenBtn.setAttribute("aria-label", on ? fsLabelOn : fsLabelOff);
            const icon = fullscreenBtn.querySelector("i");
            if (icon) {
                icon.classList.toggle("bi-arrows-fullscreen", !on);
                icon.classList.toggle("bi-fullscreen-exit", on);
            }
        }

        function exitFullscreen() {
            if (document.fullscreenElement === framePanel && document.exitFullscreen) {
                document.exitFullscreen().catch(() => { });
            }
            framePanel.classList.remove("is-fullscreen");
            syncFullscreenBtn();
        }

        function enterFullscreen() {
            if (framePanel.requestFullscreen) {
                framePanel.requestFullscreen()
                    .then(syncFullscreenBtn)
                    .catch(() => {
                        framePanel.classList.add("is-fullscreen");
                        syncFullscreenBtn();
                    });
                return;
            }
            framePanel.classList.add("is-fullscreen");
            syncFullscreenBtn();
        }

        fullscreenBtn.addEventListener("click", () => {
            if (isFullscreen()) exitFullscreen(); else enterFullscreen();
        });

        document.addEventListener("fullscreenchange", syncFullscreenBtn);

        // Chỉ cho đường DỰ PHÒNG: fullscreen thật đã tự nuốt Esc. Không chặn Esc của các hộp thoại khác
        // trên trang vì nhánh này chỉ chạy khi lớp phủ đang bật, mà lúc đó nó che kín mọi hộp thoại.
        document.addEventListener("keydown", e => {
            if (e.key === "Escape" && framePanel.classList.contains("is-fullscreen")) {
                exitFullscreen();
            }
        });

        syncFullscreenBtn();
    }

    // ===== Hướng dẫn sử dụng (kịch bản đi-từng-bước — partial _PocGuide, dùng chung với trang khách) =====
    // Không có checkbox: chỗ nào chưa đúng thì ghi qua Add Note của bảng ghi chú (chọn đúng màn hình).
    if (uatList) {
        // ===== Gập/mở từng kịch bản =====
        // Server mặc định chỉ mở thẻ đầu tiên. Đóng/mở tay được nhớ theo project để rời trang quay lại
        // không mất bố cục vừa dựng; khóa là data-index = chỉ số gốc trong uat-scenarios.json.
        const openKey = `poc-uat-open-${projectId}`;

        let openState = {};
        try { openState = JSON.parse(localStorage.getItem(openKey) || "{}"); } catch { openState = {}; }

        uatList.querySelectorAll(".uat-scenario").forEach(function (card) {
            const stored = openState[card.dataset.index];
            if (typeof stored === "boolean") card.open = stored;
        });

        // `toggle` KHÔNG nổi bọt ⇒ phải bắt ở pha capture, không thì mọi lần đóng/mở đều không được nhớ.
        uatList.addEventListener("toggle", function (e) {
            const card = e.target.closest(".uat-scenario");
            if (!card) return;
            openState[card.dataset.index] = card.open;
            try { localStorage.setItem(openKey, JSON.stringify(openState)); } catch { }
        }, true);

        // "Chỉ chỗ": bấm chữ một bước → POC mở đúng màn hình + tô sáng phần tử của ĐÚNG bước đó, để
        // người xem biết bấm vào đâu thay vì tự mò. READ-ONLY: annotator chỉ highlight, không tự thao
        // tác — user vẫn tự bấm để thấy nghiệp vụ chạy thật.
        //
        // Cái đi xuống iframe là MÃ NEO của bước (data-anchor="2.3", do Razor in ra từ chỉ số gốc của
        // kịch bản/bước), không phải câu chữ của bước: annotator tra [data-uat~="2.3"] — thứ agent dựng
        // POC đã khai báo — thay vì đoán phần tử từ tiếng Việt và khoanh nhầm.
        function tourStep(stepEl, screen) {
            pendingTour = stepEl.closest("li") || stepEl;
            clearTourHint();
            postToFrame({ type: "poc-tour-step", screen: screen || "", anchor: stepEl.dataset.anchor || "" });
        }

        // Bước đang được chỉ chỗ: tô nền để người xem nối "chỗ đang sáng bên kia" với "câu đang đọc bên này".
        function markCurrent(li) {
            uatList.querySelectorAll(".uat-steps li.is-current").forEach(x => x.classList.remove("is-current"));
            if (li) li.classList.add("is-current");
        }

        uatList.addEventListener("click", function (e) {
            // Bấm chữ của một bước → chỉ chỗ ngay bước đó.
            const stepText = e.target.closest(".uat-step-text");
            if (stepText) {
                const card = stepText.closest(".uat-scenario");
                markCurrent(stepText.closest("li"));
                tourStep(stepText, card?.dataset.screen || "");
            }
        });
    }

    loadComments();
})();

// ==== Hộp thoại "Chia sẻ bản demo" ====
// Tạo/thu hồi link cho người KHÔNG có tài khoản. Mở từ nút trên command bar. Tách IIFE riêng để phần
// review cốt lõi không phụ thuộc vào hộp thoại này (chỉ dựng cho người có quyền quản lý requirement).
(function () {
    "use strict";

    const panel = document.getElementById("pocSharePanel");
    if (!panel) return;

    const modal = document.getElementById("pocShareModal");
    const openBtn = document.getElementById("pocShareOpen");
    const root = document.getElementById("pocReviewRoot");
    const listEl = document.getElementById("pocShareList");
    const msgEl = document.getElementById("pocShareMsg");
    const labelEl = document.getElementById("pocShareLabel");
    const daysEl = document.getElementById("pocShareDays");
    const createBtn = document.getElementById("pocShareCreate");
    const suggestEl = document.getElementById("pocShareSuggest");
    const pickEl = document.getElementById("pocSharePick");
    const projectId = root.dataset.projectId;
    const antiForgery = document.querySelector('#pocCommentForm input[name="__RequestVerificationToken"]');

    let links = [];

    function shareUrl(token) {
        return `${location.origin}/poc-share/${token}`;
    }

    function say(text, isError) {
        msgEl.textContent = text;
        msgEl.classList.toggle("error", !!isError);
    }

    function render() {
        if (!links.length) {
            listEl.innerHTML = '<p class="poc-share-empty">Chưa có link nào — tạo link đầu tiên ở trên.</p>';
            return;
        }

        listEl.innerHTML = links.map(l => {
            const expired = new Date(l.expiresAtUtc) <= new Date();
            const dead = !!l.revokedAtUtc || expired;
            const state = l.revokedAtUtc ? "đã thu hồi" : (expired ? "đã hết hạn" : `hết hạn ${new Date(l.expiresAtUtc).toLocaleDateString()}`);
            return `
                <div class="poc-share-item ${dead ? "dead" : ""}" data-id="${l.id}">
                    <div class="poc-share-item-head">
                        <span class="poc-share-label">${escapeHtml(l.label || "Không đặt tên")}</span>
                        <span class="poc-share-state">${escapeHtml(state)}</span>
                    </div>
                    ${dead ? "" : `
                        <div class="poc-share-url-row">
                            <input type="text" class="poc-share-url" readonly value="${escapeHtml(shareUrl(l.token))}" />
                            <button type="button" class="btn small poc-share-copy">
                                <i class="bi bi-clipboard" aria-hidden="true"></i> Copy
                            </button>
                            <button type="button" class="btn danger small poc-share-revoke" data-id="${l.id}">Thu hồi</button>
                        </div>`}
                </div>`;
        }).join("");
    }

    async function load() {
        try {
            const response = await fetch(panel.dataset.listUrl);
            links = response.ok ? await response.json() : [];
        } catch {
            links = [];
        }
        render();
    }

    // ---- Autocomplete "Gửi cho ai" (gợi ý từ danh bạ nhân sự) ----
    // Nhãn link vẫn là text tự do — khách ngoài công ty không có trong danh bạ — nên gợi ý chỉ giúp gõ
    // nhanh và ghi cùng một người theo cùng một cách, không phải ràng buộc bắt buộc chọn.
    const MIN_KEYS = 2;
    let suggestions = [];
    let activeIndex = -1;
    let searchTimer = null;
    let searchSeq = 0;

    function hideSuggest() {
        suggestEl.classList.add("hidden");
        suggestEl.innerHTML = "";
        labelEl.setAttribute("aria-expanded", "false");
        suggestions = [];
        activeIndex = -1;
    }

    function clearPick() {
        pickEl.classList.add("hidden");
        pickEl.innerHTML = "";
    }

    function highlight(index) {
        activeIndex = index;
        Array.from(suggestEl.querySelectorAll(".poc-share-suggest-item")).forEach((el, i) => {
            el.classList.toggle("active", i === index);
            el.setAttribute("aria-selected", i === index ? "true" : "false");
        });
    }

    function pick(person) {
        labelEl.value = person.displayName;
        const detail = [person.email, person.organizationUnit].filter(Boolean).join(" · ");
        pickEl.innerHTML = `<i class="bi bi-person-check" aria-hidden="true"></i> ${escapeHtml(person.displayName)}` +
            (detail ? ` <span class="poc-share-suggest-meta">${escapeHtml(detail)}</span>` : "");
        pickEl.classList.remove("hidden");
        hideSuggest();
    }

    function renderSuggest(people) {
        suggestions = people;
        activeIndex = -1;

        if (!people.length) {
            suggestEl.innerHTML = '<div class="poc-share-suggest-empty">Không có ai khớp — cứ gõ tên tự do cũng được.</div>';
        } else {
            suggestEl.innerHTML = people.map(p => {
                const meta = [p.email, p.organizationUnit, p.position].filter(Boolean).join(" · ");
                return `
                    <div class="poc-share-suggest-item" role="option" aria-selected="false">
                        <span class="poc-share-suggest-name">${escapeHtml(p.displayName)}</span>
                        ${meta ? `<span class="poc-share-suggest-meta">${escapeHtml(meta)}</span>` : ""}
                    </div>`;
            }).join("");

            Array.from(suggestEl.children).forEach((el, i) => {
                // mousedown chứ không phải click: blur của ô nhập đóng danh sách trước khi click kịp bắn.
                el.addEventListener("mousedown", ev => { ev.preventDefault(); pick(people[i]); });
                el.addEventListener("mousemove", () => highlight(i));
            });
        }

        suggestEl.classList.remove("hidden");
        labelEl.setAttribute("aria-expanded", "true");
    }

    async function search(key) {
        // Trả lời chậm của lượt gõ cũ không được đè lên kết quả của lượt mới.
        const seq = ++searchSeq;
        try {
            const url = new URL(panel.dataset.associateUrl, location.origin);
            url.searchParams.set("q", key);
            const response = await fetch(url);
            if (!response.ok) throw new Error();
            const people = await response.json();
            if (seq === searchSeq) renderSuggest(people);
        } catch {
            // Tra cứu danh bạ hỏng thì im lặng bỏ qua: ô này vẫn gõ tay được, không chặn việc tạo link.
            if (seq === searchSeq) hideSuggest();
        }
    }

    labelEl.addEventListener("input", function () {
        clearPick();
        say(""); // báo của lượt tạo link trước không còn đúng với thứ đang gõ.
        const key = labelEl.value.trim();
        if (searchTimer) clearTimeout(searchTimer);
        if (key.length < MIN_KEYS) { hideSuggest(); return; }
        searchTimer = setTimeout(() => search(key), 250);
    });

    labelEl.addEventListener("keydown", function (e) {
        if (suggestEl.classList.contains("hidden")) return;

        if (e.key === "ArrowDown" || e.key === "ArrowUp") {
            if (!suggestions.length) return;
            e.preventDefault();
            const step = e.key === "ArrowDown" ? 1 : -1;
            highlight((activeIndex + step + suggestions.length) % suggestions.length);
        } else if (e.key === "Enter" && activeIndex >= 0) {
            e.preventDefault();
            pick(suggestions[activeIndex]);
        } else if (e.key === "Escape") {
            // Escape đóng danh sách trước, chỉ lượt bấm sau mới đóng cả hộp thoại.
            e.stopPropagation();
            hideSuggest();
        }
    });

    labelEl.addEventListener("blur", () => setTimeout(hideSuggest, 150));

    createBtn.addEventListener("click", async function () {
        // "Send to" bắt buộc: góp ý gửi qua link đứng tên người này, khách không phải tự khai tên.
        if (!labelEl.value.trim()) {
            say("Nhập người nhận (Send to) — góp ý gửi qua link sẽ đứng tên người này.", true);
            labelEl.focus();
            return;
        }
        createBtn.disabled = true;
        say("");
        hideSuggest();
        try {
            const fd = new FormData();
            fd.append("projectId", projectId);
            fd.append("label", labelEl.value.trim());
            fd.append("days", daysEl.value);
            if (antiForgery) fd.append("__RequestVerificationToken", antiForgery.value);

            const response = await fetch(panel.dataset.createUrl, { method: "POST", body: fd });
            if (!response.ok) {
                say(await response.text() || "Không tạo được link.", true);
            } else {
                labelEl.value = "";
                clearPick();
                await load();
                say("Đã tạo link — bấm Copy rồi gửi cho người nhận.");
            }
        } catch {
            say("Không tạo được link — kiểm tra kết nối rồi thử lại.", true);
        }
        createBtn.disabled = false;
    });

    listEl.addEventListener("click", async function (e) {
        const copy = e.target.closest(".poc-share-copy");
        if (copy) {
            const input = copy.closest(".poc-share-url-row").querySelector(".poc-share-url");
            input.select();
            try {
                await navigator.clipboard.writeText(input.value);
                say("Đã copy link.");
            } catch {
                // Trình duyệt chặn clipboard API (http, quyền) — link đã được bôi đen sẵn để Ctrl+C.
                say("Không copy tự động được — link đã được bôi đen, bấm Ctrl+C.", true);
            }
            return;
        }

        const revoke = e.target.closest(".poc-share-revoke");
        if (!revoke) return;
        if (!confirm("Thu hồi link này? Người đang giữ link sẽ không mở được nữa.")) return;

        try {
            const fd = new FormData();
            fd.append("projectId", projectId);
            fd.append("id", revoke.dataset.id);
            if (antiForgery) fd.append("__RequestVerificationToken", antiForgery.value);
            const response = await fetch(panel.dataset.revokeUrl, { method: "POST", body: fd });
            if (!response.ok) throw new Error();
            await load();
            say("Đã thu hồi link.");
        } catch {
            say("Không thu hồi được link.", true);
        }
    });

    // Danh sách nạp khi MỞ hộp thoại (không nạp lúc tải trang): người khác có thể vừa tạo/thu hồi link,
    // và trang review vốn mở rất lâu nên bản nạp lúc đầu phiên gần như chắc chắn đã cũ.
    function openShare() {
        say("");
        clearPick();
        hideSuggest();
        modal.classList.remove("hidden");
        labelEl.focus();
        load();
    }

    function closeShare() {
        hideSuggest();
        modal.classList.add("hidden");
    }

    openBtn.addEventListener("click", openShare);
    document.getElementById("pocShareClose").addEventListener("click", closeShare);
    document.getElementById("pocShareDone").addEventListener("click", closeShare);
    modal.addEventListener("click", e => { if (e.target === modal) closeShare(); });
    document.addEventListener("keydown", e => {
        if (e.key === "Escape" && !modal.classList.contains("hidden")) closeShare();
    });
})();

// ==== Hộp thoại "Chi tiết kỹ thuật" (2 tab: vòng tự kiểm / đối chiếu spec) ====
// Nội dung đã được server dựng sẵn trong DOM — ở đây chỉ có đóng/mở và đổi tab, không gọi mạng.
// IIFE riêng, thoát sớm khi không có markup: hộp thoại chỉ dựng cho người có quyền DeliveryAdvance,
// còn phần review cốt lõi phải chạy bình thường với mọi người xem.
(function () {
    "use strict";

    const modal = document.getElementById("pocTechModal");
    const openBtn = document.getElementById("pocTechOpen");
    if (!modal || !openBtn) return;

    const tabs = Array.from(modal.querySelectorAll(".poc-tech-tab"));

    function selectTab(tab) {
        if (!tab || tab.disabled) return;
        tabs.forEach(t => {
            const on = t === tab;
            t.classList.toggle("active", on);
            t.setAttribute("aria-selected", on ? "true" : "false");
            document.getElementById(t.dataset.panel).classList.toggle("hidden", !on);
        });
        tab.focus();
    }

    tabs.forEach(tab => tab.addEventListener("click", () => selectTab(tab)));

    // Mũi tên trái/phải giữa các tab (mẫu tablist chuẩn), bỏ qua tab bị vô hiệu.
    modal.querySelector(".poc-tech-tabs").addEventListener("keydown", e => {
        if (e.key !== "ArrowRight" && e.key !== "ArrowLeft") return;
        const enabled = tabs.filter(t => !t.disabled);
        if (enabled.length < 2) return;
        const at = enabled.indexOf(document.activeElement);
        if (at < 0) return;
        e.preventDefault();
        const step = e.key === "ArrowRight" ? 1 : -1;
        selectTab(enabled[(at + step + enabled.length) % enabled.length]);
    });

    function openTech() {
        modal.classList.remove("hidden");
        openBtn.setAttribute("aria-expanded", "true");
        const active = tabs.find(t => t.classList.contains("active") && !t.disabled) || tabs.find(t => !t.disabled);
        if (active) active.focus();
    }

    function closeTech() {
        modal.classList.add("hidden");
        openBtn.setAttribute("aria-expanded", "false");
        openBtn.focus();
    }

    openBtn.addEventListener("click", openTech);
    document.getElementById("pocTechClose").addEventListener("click", closeTech);
    document.getElementById("pocTechDone").addEventListener("click", closeTech);
    modal.addEventListener("click", e => { if (e.target === modal) closeTech(); });
    document.addEventListener("keydown", e => {
        if (e.key === "Escape" && !modal.classList.contains("hidden")) closeTech();
    });
})();
