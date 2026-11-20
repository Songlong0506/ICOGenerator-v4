// Mở modal chi tiết cho một dòng System Logs. Dữ liệu đọc từ data-* trên nút (Razor đã HTML-encode),
// đổ vào modal bằng textContent nên nội dung log không bao giờ được hiểu là HTML.
let systemLogDetailText = "";

function openSystemLogDetail(btn) {
    const d = btn.dataset;
    const set = (id, text) => { document.getElementById(id).textContent = text || "—"; };
    const block = (blockId, preId, text) => {
        document.getElementById(blockId).classList.toggle("hidden", !text);
        document.getElementById(preId).textContent = text || "";
    };

    const level = document.getElementById("syslog-detail-level");
    level.textContent = d.level;
    level.className = "badge " + (d.levelClass || "");

    set("syslog-detail-id", "#" + d.id);
    set("syslog-detail-time", d.time);
    set("syslog-detail-source", d.source);
    set("syslog-detail-path", d.path);
    set("syslog-detail-message", d.message);

    let properties = d.properties || "";
    try { if (properties) properties = JSON.stringify(JSON.parse(properties), null, 2); } catch { /* giữ nguyên chuỗi */ }
    block("syslog-detail-exception-block", "syslog-detail-exception", d.exception);
    block("syslog-detail-properties-block", "syslog-detail-properties", properties);

    // Bản chép ra clipboard: đủ để dán vào issue / chat nhờ người khác xem.
    systemLogDetailText = [
        `[${d.time}] ${d.level} ${d.source || ""}`.trim(),
        d.path ? `Request: ${d.path}` : "",
        d.message || "",
        d.exception || "",
        properties ? `Properties: ${properties}` : ""
    ].filter(Boolean).join("\n\n");

    openModal("systemLogDetail");
}

document.getElementById("syslog-detail-copy")?.addEventListener("click", async function () {
    try {
        await navigator.clipboard.writeText(systemLogDetailText);
        this.textContent = "Copied";
        setTimeout(() => { this.textContent = "Copy"; }, 1500);
    } catch { /* clipboard bị chặn (HTTP không an toàn) — người dùng vẫn bôi đen được */ }
});

// Đóng modal khi bấm ra vùng nền tối bên ngoài.
document.getElementById("systemLogDetail")?.addEventListener("click", function (e) {
    if (e.target === this) closeModal("systemLogDetail");
});
