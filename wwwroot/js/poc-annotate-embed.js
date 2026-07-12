// Chạy BÊN TRONG iframe mockup (Projects/Mockup?annotate=1 — sandbox, opaque origin). Nhiệm vụ duy nhất:
// khi trang cha (PocReview) bật chế độ "chọn phần tử", highlight phần tử dưới con trỏ và gửi MÔ TẢ phần
// tử được click về trang cha qua postMessage. Không gọi mạng, không đọc cookie — sandbox chặn sẵn; và
// đây là ranh giới an toàn: HTML mockup do LLM sinh, script này chỉ được nói chuyện một chiều với cha.
(function () {
    "use strict";

    var selecting = false;
    var hoverEl = null;
    var savedOutline = "";
    var savedCursor = "";

    function setHover(el) {
        if (hoverEl === el) return;
        if (hoverEl) hoverEl.style.outline = savedOutline;
        hoverEl = el;
        if (hoverEl) {
            savedOutline = hoverEl.style.outline;
            hoverEl.style.outline = "2px solid #e11d48";
        }
    }

    function setSelecting(enabled) {
        selecting = !!enabled;
        if (selecting) {
            savedCursor = document.documentElement.style.cursor;
            document.documentElement.style.cursor = "crosshair";
        } else {
            document.documentElement.style.cursor = savedCursor;
            setHover(null);
        }
    }

    function shortText(s, max) {
        s = (s || "").replace(/\s+/g, " ").trim();
        return s.length > max ? s.slice(0, max - 1) + "…" : s;
    }

    // Nhãn đọc được cho phần tử: ưu tiên chữ người dùng nhìn thấy, kèm loại phần tử tiếng Việt.
    function labelFor(el) {
        var tag = (el.tagName || "").toLowerCase();
        var kind =
            tag === "button" ? "Nút" :
            tag === "a" ? "Liên kết" :
            tag === "input" || tag === "textarea" ? "Ô nhập" :
            tag === "select" ? "Ô chọn" :
            tag === "img" ? "Ảnh" :
            tag === "th" || tag === "td" ? "Ô bảng" :
            /^h[1-6]$/.test(tag) ? "Tiêu đề" :
            "Phần tử <" + tag + ">";

        var text = "";
        if (tag === "input" || tag === "textarea" || tag === "select") {
            text = el.getAttribute("placeholder") || el.getAttribute("aria-label") || el.getAttribute("name") || "";
            // Ô nhập thường có <label> đứng ngay trước — dùng nó nếu chưa có gì.
            if (!text && el.id) {
                var lbl = document.querySelector('label[for="' + el.id + '"]');
                if (lbl) text = lbl.textContent;
            }
        } else if (tag === "img") {
            text = el.getAttribute("alt") || "";
        } else {
            text = el.textContent || "";
        }

        text = shortText(text, 60);
        return text ? kind + ' "' + text + '"' : kind;
    }

    // Đường dẫn CSS gần đúng (tối đa 5 tầng) — chỉ để tham khảo, không dùng làm neo cứng.
    function cssPath(el) {
        var parts = [];
        var node = el;
        while (node && node.nodeType === 1 && parts.length < 5 && node.tagName.toLowerCase() !== "html") {
            var tag = node.tagName.toLowerCase();
            var part = tag;
            if (node.id) {
                parts.unshift(tag + "#" + node.id);
                break;
            }
            var parent = node.parentElement;
            if (parent) {
                var siblings = Array.prototype.filter.call(parent.children, function (c) {
                    return c.tagName === node.tagName;
                });
                if (siblings.length > 1) part += ":nth-of-type(" + (siblings.indexOf(node) + 1) + ")";
            }
            parts.unshift(part);
            node = parent;
        }
        return parts.join(" > ");
    }

    window.addEventListener("message", function (e) {
        // Chỉ nghe lệnh từ trang cha chứa iframe này.
        if (e.source !== window.parent) return;
        var data = e.data || {};

        // Handshake: trang cha poll "hello" tới khi nhận "ready" — cần vì mockup nhỏ có thể load
        // XONG trước khi trang cha kịp gắn listener, làm bản tin ready bắn một lần bị rơi.
        if (data.type === "poc-annotate-hello") {
            window.parent.postMessage({ type: "poc-annotate-ready" }, "*");
            return;
        }

        if (data.type === "poc-annotate-mode") setSelecting(data.enabled);
    });

    document.addEventListener("mouseover", function (e) {
        if (selecting) setHover(e.target);
    }, true);

    // capture=true + preventDefault để cú click chọn-phần-tử KHÔNG kích hoạt hành vi demo của POC.
    document.addEventListener("click", function (e) {
        if (!selecting) return;
        e.preventDefault();
        e.stopPropagation();

        var el = e.target;
        window.parent.postMessage({
            type: "poc-annotate-pick",
            label: labelFor(el),
            path: cssPath(el)
        }, "*");

        setSelecting(false);
    }, true);

    // Báo sẵn sàng ngay khi chạy; nếu bản tin này rơi (trang cha chưa gắn listener) thì vòng poll
    // hello/ready ở trên sẽ bắt lại.
    window.parent.postMessage({ type: "poc-annotate-ready" }, "*");
})();
