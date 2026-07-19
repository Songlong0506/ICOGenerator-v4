// poc-annotator.js — chạy BÊN TRONG poc-demo.html khi được phục vụ ở chế độ review
// (Projects/Mockup?review=1, nhúng trong iframe của trang Projects/PocReview).
//
// Vai trò: cho người xem GHIM ghi chú lên phần tử POC — bật "chế độ ghim" thì click vào phần tử sẽ
// được chặn lại, mô tả phần tử (nhãn + CSS selector + vị trí + màn hình đang mở) được gửi lên trang
// cha; trang cha lưu ghi chú rồi gửi danh sách xuống để script này vẽ pin đánh số lên đúng phần tử.
//
// Ranh giới bảo mật: tài liệu POC là HTML do LLM sinh, chạy trong sandbox origin OPAQUE (không cookie,
// không same-origin request). Script này KHÔNG nới gì — nó chỉ nói chuyện với trang cha qua
// postMessage và chỉ nhận lệnh từ đúng window.parent.
(function () {
    "use strict";

    if (window.__pocAnnotator) return;
    window.__pocAnnotator = true;

    var mode = false;          // chế độ ghim đang bật?
    var comments = [];         // danh sách ghi chú từ trang cha: {id, pageView, elementLabel, elementPath, xPercent, yPercent, status, index}
    var pinHost = null;        // container chứa các pin (position: fixed)
    var hovered = null;        // phần tử đang được rê chuột trong chế độ ghim
    var lastView = null;

    function send(msg) {
        // Origin cha không đọc được từ trong sandbox opaque → '*'; nội dung gửi lên chỉ là mô tả phần
        // tử người dùng vừa click, không có gì nhạy cảm.
        try { window.parent.postMessage(msg, "*"); } catch (e) { /* đứng một mình (không iframe) thì thôi */ }
    }

    // ===== Nhận diện màn hình đang mở (POC nhiều màn hình = các <section class="page-view"> ) =====

    function currentView() {
        var active = document.querySelector(".page-view.active");
        return active ? (active.getAttribute("data-view") || "") : "";
    }

    // ===== Mô tả phần tử được click =====

    var TAG_LABEL = {
        BUTTON: "Nút", A: "Liên kết", INPUT: "Ô nhập", TEXTAREA: "Ô nhập", SELECT: "Ô chọn",
        TH: "Cột bảng", TD: "Ô bảng", IMG: "Ảnh", LABEL: "Nhãn",
        H1: "Tiêu đề", H2: "Tiêu đề", H3: "Tiêu đề", H4: "Tiêu đề", H5: "Tiêu đề"
    };

    function shortText(s, max) {
        s = (s || "").replace(/\s+/g, " ").trim();
        return s.length > max ? s.slice(0, max - 1) + "…" : s;
    }

    function elementLabel(el) {
        var kind = TAG_LABEL[el.tagName] || ("<" + el.tagName.toLowerCase() + ">");
        var text = "";

        if (el.tagName === "INPUT" || el.tagName === "TEXTAREA" || el.tagName === "SELECT") {
            text = el.getAttribute("placeholder") || el.getAttribute("name") || el.getAttribute("aria-label") || "";
        } else {
            text = el.textContent || el.getAttribute("aria-label") || el.getAttribute("title") || "";
        }

        text = shortText(text, 60);
        return text ? kind + " “" + text + "”" : kind;
    }

    // CSS selector "đủ tìm lại" cho pin và cho agent tìm phần tử trong poc-demo.html: đi từ phần tử
    // lên tới body, dừng sớm ở phần tử có id; mỗi nấc là tag:nth-of-type để bền với class động.
    function cssPath(el) {
        var parts = [];
        var node = el;

        while (node && node.nodeType === 1 && node !== document.body && parts.length < 8) {
            if (node.id) {
                parts.unshift("#" + node.id);
                break;
            }

            var tag = node.tagName.toLowerCase();
            var index = 1;
            var sibling = node.previousElementSibling;
            while (sibling) {
                if (sibling.tagName === node.tagName) index++;
                sibling = sibling.previousElementSibling;
            }
            parts.unshift(tag + ":nth-of-type(" + index + ")");
            node = node.parentElement;
        }

        return parts.join(" > ");
    }

    // ===== Chế độ ghim: chặn click, gửi mô tả phần tử lên trang cha =====

    function setMode(enabled) {
        mode = !!enabled;
        document.documentElement.classList.toggle("poc-annotate-mode", mode);
        clearHover();
    }

    function clearHover() {
        if (hovered) {
            hovered.classList.remove("poc-annotate-hover");
            hovered = null;
        }
    }

    function pickTarget(e) {
        var el = e.target;
        if (!el || el.nodeType !== 1) return null;
        if (el.closest(".poc-pin-host")) return null; // pin của chính mình
        if (el === document.body || el === document.documentElement) return null;
        return el;
    }

    document.addEventListener("mouseover", function (e) {
        if (!mode) return;
        clearHover();
        var el = pickTarget(e);
        if (el) {
            hovered = el;
            el.classList.add("poc-annotate-hover");
        }
    }, true);

    document.addEventListener("click", function (e) {
        // Pin luôn click được (kể cả ngoài chế độ ghim) để mở ghi chú tương ứng ở panel cha.
        var pin = e.target && e.target.nodeType === 1 ? e.target.closest(".poc-pin") : null;
        if (pin) {
            e.preventDefault();
            e.stopImmediatePropagation();
            send({ type: "poc-pin-click", id: pin.getAttribute("data-id") });
            return;
        }

        if (!mode) return;

        e.preventDefault();
        e.stopImmediatePropagation();

        var el = pickTarget(e);
        if (!el) return;

        send({
            type: "poc-pick",
            pageView: currentView(),
            elementLabel: elementLabel(el),
            elementPath: cssPath(el),
            xPercent: Math.round((e.clientX / window.innerWidth) * 1000) / 10,
            yPercent: Math.round((e.clientY / window.innerHeight) * 1000) / 10
        });
    }, true);

    document.addEventListener("keydown", function (e) {
        if (mode && e.key === "Escape") send({ type: "poc-exit-mode" });
    }, true);

    // ===== Pin: chấm đánh số neo vào phần tử (position: fixed, tính lại khi scroll/resize/đổi màn hình) =====

    function ensurePinHost() {
        if (pinHost) return pinHost;
        pinHost = document.createElement("div");
        pinHost.className = "poc-pin-host";
        document.body.appendChild(pinHost);
        return pinHost;
    }

    function renderPins() {
        var host = ensurePinHost();
        host.innerHTML = "";
        var view = currentView();

        comments.forEach(function (c) {
            // Chỉ hiện pin của màn hình đang mở (pin không màn hình — POC một trang — hiện luôn).
            if ((c.pageView || "") !== view) return;

            var pin = document.createElement("button");
            pin.type = "button";
            pin.className = "poc-pin" + (c.status === "Sent" ? " sent" : "");
            pin.setAttribute("data-id", c.id);
            pin.title = c.comment || "";
            pin.textContent = String(c.index);
            host.appendChild(pin);

            positionPin(pin, c);
        });
    }

    function positionPin(pin, c) {
        var el = null;
        if (c.elementPath) {
            try { el = document.querySelector(c.elementPath); } catch (e) { /* selector hỏng → dùng % */ }
        }

        var left, top;
        if (el) {
            var rect = el.getBoundingClientRect();
            if (rect.width === 0 && rect.height === 0) {
                pin.style.display = "none";
                return;
            }
            left = rect.right - 9;
            top = rect.top - 9;
        } else {
            left = (c.xPercent / 100) * window.innerWidth - 11;
            top = (c.yPercent / 100) * window.innerHeight - 11;
        }

        pin.style.display = "";
        pin.style.left = Math.max(0, Math.min(left, window.innerWidth - 24)) + "px";
        pin.style.top = Math.max(0, Math.min(top, window.innerHeight - 24)) + "px";
    }

    // Gom mọi nguồn thay đổi layout (scroll container .page, resize, CRUD render lại bảng, đổi màn
    // hình) về một lần vẽ lại mỗi frame.
    var repaintQueued = false;
    function queueRepaint() {
        if (repaintQueued) return;
        repaintQueued = true;
        requestAnimationFrame(function () {
            repaintQueued = false;

            var view = currentView();
            if (view !== lastView) {
                lastView = view;
                send({ type: "poc-view", view: view });
            }

            renderPins();
        });
    }

    window.addEventListener("scroll", queueRepaint, true);
    window.addEventListener("resize", queueRepaint);
    new MutationObserver(queueRepaint).observe(document.body, {
        subtree: true, childList: true, attributes: true, attributeFilter: ["class"]
    });

    function flashPin(id) {
        var pin = pinHost && pinHost.querySelector('.poc-pin[data-id="' + id + '"]');
        if (!pin) return;
        pin.classList.add("flash");
        setTimeout(function () { pin.classList.remove("flash"); }, 1600);
    }

    // ===== Guided tour: dẫn người xem theo từng bước kịch bản UAT =====
    // Trang cha gửi text một bước ("Bấm nút Duyệt ở đơn đang chờ"); annotator tìm phần tử tương tác có
    // chữ khớp nhất trong màn hình đang mở rồi tô sáng + cuộn tới. Không khớp phần tử nào thì nháy cả
    // vùng nội dung để người xem biết đang ở đúng màn hình. Chỉ ĐỌC DOM + tô sáng, không thao tác thay user.

    // Từ dừng (tiếng Việt) bỏ khi so khớp — giữ lại danh từ/động từ mang nghĩa.
    var TOUR_STOPWORDS = { "bam": 1, "nut": 1, "vao": 1, "o": 1, "the": 1, "va": 1, "cua": 1, "mot": 1, "voi": 1, "man": 1, "hinh": 1, "kiem": 1, "tra": 1, "mo": 1, "chon": 1, "nhap": 1, "xem": 1, "dang": 1 };

    function tourTokens(text) {
        // Bỏ dấu tiếng Việt để so khớp không phụ thuộc dấu; tách từ; bỏ stopword & từ quá ngắn.
        var norm = (text || "").toLowerCase().normalize("NFD").replace(/[̀-ͯ]/g, "").replace(/đ/g, "d");
        return norm.split(/[^a-z0-9]+/).filter(function (w) { return w.length >= 2 && !TOUR_STOPWORDS[w]; });
    }

    function elementText(el) {
        if (el.tagName === "INPUT" || el.tagName === "SELECT" || el.tagName === "TEXTAREA") {
            return (el.getAttribute("placeholder") || el.getAttribute("name") || el.getAttribute("aria-label") || "");
        }
        return (el.textContent || el.getAttribute("aria-label") || el.title || "");
    }

    function findBestMatch(stepText) {
        var wanted = tourTokens(stepText);
        if (wanted.length === 0) return null;

        // Ưu tiên phần tử tương tác; nới ra heading/nhãn/ô bảng nếu không có nút khớp.
        var candidates = document.querySelectorAll(
            ".page-view.active button, .page-view.active a, .page-view.active .btn, .page-view.active [role='button']," +
            ".page-view.active input, .page-view.active select, .page-view.active th, .page-view.active label," +
            ".page-view.active h1, .page-view.active h2, .page-view.active h3, .page-view.active td");

        var best = null, bestScore = 0;
        candidates.forEach(function (el) {
            var rect = el.getBoundingClientRect();
            if (rect.width === 0 && rect.height === 0) return; // ẩn

            var have = tourTokens(elementText(el));
            if (have.length === 0) return;
            var haveSet = {};
            have.forEach(function (w) { haveSet[w] = 1; });

            var hits = 0;
            wanted.forEach(function (w) { if (haveSet[w]) hits++; });
            if (hits === 0) return;

            // Ưu tiên khớp nhiều token VÀ phần tử ngắn gọn (nút "Duyệt" hơn cả một đoạn dài chứa "duyệt").
            var score = hits * 10 - Math.min(have.length, 20) * 0.2;
            if (score > bestScore) { bestScore = score; best = el; }
        });
        return best;
    }

    var tourTimer = null;
    function highlightElement(el) {
        el.classList.add("poc-tour-target");
        el.scrollIntoView({ block: "center", behavior: "smooth" });
        if (tourTimer) clearTimeout(tourTimer);
        tourTimer = setTimeout(function () {
            document.querySelectorAll(".poc-tour-target").forEach(function (x) { x.classList.remove("poc-tour-target"); });
        }, 2600);
    }

    function flashMain() {
        var main = document.querySelector(".page-view.active") || document.querySelector("main") || document.body;
        main.classList.add("poc-tour-flash");
        setTimeout(function () { main.classList.remove("poc-tour-flash"); }, 900);
    }

    function runTourStep(screen, text) {
        // Mở đúng màn hình trước (nếu kịch bản chỉ định), rồi tìm & tô sáng sau khi render.
        if (screen && typeof window.pocNavigate === "function" && screen !== currentView()) {
            window.pocNavigate(screen);
        }
        setTimeout(function () {
            document.querySelectorAll(".poc-tour-target").forEach(function (x) { x.classList.remove("poc-tour-target"); });
            var el = findBestMatch(text);
            if (el) highlightElement(el);
            else flashMain();
        }, 120);
    }

    // ===== Lệnh từ trang cha =====

    // Handshake: iframe local load rất nhanh nên poc-ready có thể phát TRƯỚC khi trang cha kịp đăng
    // ký listener (script cha nằm cuối body, chạy sau khi iframe đã parse xong) — gửi lại định kỳ
    // cho tới khi nhận message đầu tiên từ cha (coi như ACK), có trần để không tự spam khi đứng một mình.
    var parentAcked = false;
    var readyAttempts = 0;
    var readyTimer = setInterval(function () {
        if (parentAcked || readyAttempts++ > 40) {
            clearInterval(readyTimer);
            return;
        }
        send({ type: "poc-ready", view: currentView() });
    }, 300);

    window.addEventListener("message", function (e) {
        if (e.source !== window.parent || !e.data || typeof e.data !== "object") return;

        parentAcked = true;

        if (e.data.type === "poc-mode") {
            setMode(e.data.enabled);
        } else if (e.data.type === "poc-comments") {
            comments = Array.isArray(e.data.items) ? e.data.items : [];
            renderPins();
        } else if (e.data.type === "poc-focus") {
            var c = comments.find(function (x) { return x.id === e.data.id; });
            if (!c) return;
            // Mở đúng màn hình chứa ghi chú (hook điều hướng của shell POC), rồi nháy pin.
            if (c.pageView && typeof window.pocNavigate === "function" && c.pageView !== currentView()) {
                window.pocNavigate(c.pageView);
            }
            setTimeout(function () { renderPins(); flashPin(c.id); }, 60);
        } else if (e.data.type === "poc-tour-step") {
            // Guided tour: dẫn tới màn hình + tô sáng phần tử khớp mô tả bước.
            runTourStep(e.data.screen || "", e.data.text || "");
        }
    });

    // ===== Style của annotator (tiêm thẳng — POC là tài liệu tự chứa, không link CSS ngoài được) =====

    var style = document.createElement("style");
    style.textContent =
        ".poc-annotate-mode, .poc-annotate-mode * { cursor: crosshair !important; }" +
        ".poc-annotate-hover { outline: 2px dashed #007bc0 !important; outline-offset: 2px; }" +
        ".poc-pin-host { position: fixed; inset: 0 auto auto 0; width: 0; height: 0; z-index: 99999; }" +
        ".poc-pin { position: fixed; min-width: 22px; height: 22px; padding: 0 4px; border-radius: 50%;" +
        "  border: 2px solid #fff; background: #d97706; color: #fff; font: 700 12px/18px sans-serif;" +
        "  box-shadow: 0 1px 4px rgba(0,0,0,.35); cursor: pointer; z-index: 99999; }" +
        ".poc-pin.sent { background: #64748b; }" +
        ".poc-pin.flash { animation: poc-pin-flash 0.4s ease 3; }" +
        "@keyframes poc-pin-flash { 50% { transform: scale(1.45); background: #e20015; } }" +
        // Guided tour: phần tử đang được "chỉ chỗ" và nháy vùng nội dung khi không tìm ra phần tử.
        ".poc-tour-target { outline: 3px solid #d97706 !important; outline-offset: 2px;" +
        "  box-shadow: 0 0 0 6px rgba(217,119,6,.25) !important; border-radius: 3px; transition: outline .15s; }" +
        ".poc-tour-flash { animation: poc-tour-flash 0.9s ease; }" +
        "@keyframes poc-tour-flash { 0%,100% { background: transparent; } 40% { background: rgba(217,119,6,.12); } }";
    document.head.appendChild(style);

    // Báo trang cha là annotator đã sẵn sàng (trang cha sẽ gửi danh sách ghi chú + trạng thái mode).
    lastView = currentView();
    send({ type: "poc-ready", view: lastView });
})();
