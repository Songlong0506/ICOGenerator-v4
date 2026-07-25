function openAddModel() {
    // Kết quả test của lần mở trước không còn đúng với dữ liệu sắp gõ — dọn trước khi mở.
    resetTestResult('add-test-result');
    openModal('addModel');
}

function openEditModel(
    id,
    modelId,
    endpoint,
    contextWindow,
    inputPrice,
    outputPrice,
    isActive,
    supportsVision,
    supportsStructuredOutput,
    apiKeyMask
) {
    document.getElementById('edit-id').value = id;
    document.getElementById('edit-model-id').value = modelId;
    document.getElementById('edit-endpoint').value = endpoint;
    // Only a masked preview of the key reaches the browser — never the full secret. Leave the
    // input blank on open; an empty field means "keep the existing key" (see UpdateAiModelUseCase).
    document.getElementById('edit-api-key').value = '';
    document.getElementById('edit-api-key-preview').textContent = apiKeyMask || '(chưa cấu hình)';
    document.getElementById('edit-context-window').value = contextWindow;
    document.getElementById('edit-input-price').value = inputPrice;
    document.getElementById('edit-output-price').value = outputPrice;

    document.getElementById('edit-is-active').checked = isActive === 'true';
    document.getElementById('edit-supports-vision').checked = supportsVision === 'true';
    document.getElementById('edit-supports-structured-output').checked = supportsStructuredOutput === 'true';

    resetTestResult('edit-test-result');
    openModal('editModel');
}

// Nút "Test Connection": gọi thử model bằng ĐÚNG các giá trị đang gõ trong modal (chưa cần Save), rồi hiện
// kết quả ngay tại đó — đỡ phải lưu model rồi đi chạy một agent thật mới biết endpoint/ApiKey sai.
// Trên form Edit, để trống ApiKey vẫn test được: server dùng key đã lưu (key thật không gửi về browser).
async function testModelConnection(button) {
    const form = button.closest('form');
    const box = document.getElementById(button.dataset.result);
    if (!form || !box) return;

    // Nút này là type=button nên không kích hoạt validate của browser — gọi tay để thiếu trường bắt buộc
    // được báo ngay tại ô đó, khỏi mất một vòng gọi server chỉ để nhận "dữ liệu không hợp lệ".
    if (!form.reportValidity()) return;

    // FormData của cả form nên mang theo luôn __RequestVerificationToken → POST qua được antiforgery.
    const body = new FormData(form);

    button.disabled = true;
    showTestResult(box, 'info', 'Đang gọi thử model…');

    try {
        const response = await fetch(button.dataset.testUrl, {
            method: 'POST',
            body: body,
            headers: { 'X-Requested-With': 'XMLHttpRequest' }
        });

        // Thiếu quyền / hết phiên: cookie auth chuyển hướng sang trang HTML (AccessDenied hoặc Login) nên
        // phản hồi tuy 200 nhưng không phải JSON — đừng để res.json() ném lỗi khó hiểu.
        const isJson = (response.headers.get('content-type') || '').includes('application/json');
        if (!response.ok || !isJson) {
            showTestResult(box, 'error', !response.ok
                ? 'Không gọi được máy chủ (HTTP ' + response.status + ').'
                : 'Không thử được kết nối: thiếu quyền hoặc phiên đăng nhập đã hết. Hãy tải lại trang.');
            return;
        }

        const data = await response.json();
        showTestResult(box, data.ok ? 'success' : 'error', data.message, data.detail);
    } catch (e) {
        showTestResult(box, 'error', 'Không gọi được máy chủ: ' + e.message);
    } finally {
        button.disabled = false;
    }
}

// Dùng textContent cho cả message và detail: detail có thể là body lỗi từ endpoint lạ, không được coi là HTML.
function showTestResult(box, kind, message, detail) {
    box.className = 'test-result alert ' + kind;
    box.textContent = '';

    const line = document.createElement('div');
    line.textContent = message;
    box.appendChild(line);

    if (detail) {
        const small = document.createElement('div');
        small.className = 'test-result-detail';
        small.textContent = detail;
        box.appendChild(small);
    }
}

function resetTestResult(id) {
    const box = document.getElementById(id);
    if (!box) return;

    box.className = 'test-result hidden';
    box.textContent = '';
}
