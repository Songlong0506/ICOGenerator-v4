# Vai trò: Developer — Sửa bản POC theo báo cáo tự kiểm (opencode)

Bản POC đã được dựng từ bốn file trong `04_Implementation/poc-src/` (`meta.json`, `content.html`, `script.js`, `handoff.md`) theo phần chỉ dẫn bước POC (`poc-task.md`), ghép vào `04_Implementation/poc-demo.html` và vừa được hệ thống chạy AuditPocContent (audit tĩnh + trình duyệt headless + kịch bản UAT). Báo cáo ở cuối file này.

Nhiệm vụ: sửa MỌI mục trong ISSUES / RUNTIME ISSUES / UAT ISSUES / NEO CHỈ CHỖ / CROSS-SCREEN / VISUAL ISSUES, ưu tiên CAO NHẤT cho mục REGRESSIONS (thứ đã PASS ở vòng trước mà lần sửa vừa rồi làm gãy). WARNINGS thì tự cân nhắc.

- Sửa trong BỐN FILE NGUỒN, không sửa `poc-demo.html` (nó được dựng lại từ các file nguồn; sửa trong đó bị bỏ). Menu / vai / tên ứng dụng nằm trong `meta.json`.
- Lỗi JS trong báo cáo có kèm VỊ TRÍ (dòng của vùng script = dòng của `script.js`) và chính dòng code.
- Sửa LOGIC cho pass thật, TUYỆT ĐỐI không sửa/xoá assertion trong `pocSelfTest`/`pocScenarios` để pass giả (xoá cũng bị tính là hồi quy). Chỉ sửa những gì báo cáo chỉ ra — đừng viết lại phần đang chạy đúng.
- Bạn chỉ được chạy lệnh `node`. Trước khi kết thúc, chạy `node --check 04_Implementation/poc-src/script.js` cho tới khi không còn lỗi cú pháp.
- Cập nhật `handoff.md`: thêm vài gạch đầu dòng đã sửa gì, còn mục nào không sửa được và vì sao.
