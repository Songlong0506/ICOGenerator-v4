# Test & xác minh end-to-end

## Unit test

```bash
dotnet test          # xUnit; EF chạy Sqlite — không cần SQL Server/LLM
```

Bố cục test khớp bố cục code — sửa ở đâu, tìm test ở thư mục cùng tên. Các parser (verdict, judge, chat reply...), cổng readiness tất định, use case cổng duyệt, budget, notification, prompt studio... đều có test.

## Xác minh end-to-end không cần hạ tầng thật — skill `verify`

`.claude/skills/verify/SKILL.md` (dùng được cả như tài liệu chạy tay):

1. Build rồi **chạy DLL trực tiếp** với env Development (Sqlite) — nhớ `Encryption__ApiKeyKey` bất kỳ và `AgentWorkspace__RootPath` hợp lệ.
2. Dựng **LLM stub OpenAI-compatible** — **bắt buộc hỗ trợ SSE streaming** (`stream:true`); stub trả JSON thường thì agent "chạy xong" nhưng Output rỗng. Trỏ model vào stub bằng UPDATE bảng `AiModels` (ApiKey plaintext vẫn đọc được nhờ passthrough).
3. Seed trạng thái workflow bằng SQL nếu cần (enum lưu TEXT; **datetime format EF: `YYYY-MM-DD HH:MM:SS.ffffff`, dấu cách không phải 'T'**).
4. Lái UI bằng Playwright; selector cổng duyệt: `#delivery-gate`, `#dg-approve-form`, `#dg-revise-btn`, `#revise-modal`... Gate poll ~2.5s, worker nhặt task ~2s.
