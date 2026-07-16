using System.Text;

namespace ICOGenerator.Services.Requirements;

/// <summary>
/// Lọc luồng token thô của model thành phần text HIỂN THỊ ĐƯỢC của một lượt chat BA, để stream
/// "BA đang gõ" lên UI. BA được nhắc trả JSON {"message": "...", "suggestions": [...]} (xem
/// <see cref="BAChatReplyParser"/>) nên token thô là cú pháp JSON — đẩy nguyên văn lên UI người dùng
/// sẽ thấy dấu ngoặc/escape thay vì câu chữ. Filter chỉ phát phần GIÁ TRỊ của trường "message"
/// (đã unescape); model trả text thuần (đường fallback của parser) thì phát nguyên văn.
///
/// Đây chỉ là bản xem trước: nội dung CHỐT vẫn do parser + cổng readiness quyết định sau khi call
/// xong (lời mời có thể bị thay bằng câu hỏi của gate) — UI luôn thay bản preview bằng bản chốt ở
/// sự kiện done. Vì vậy filter được phép "khoan dung": không nhận ra định dạng thì thà im lặng
/// (không phát gì) chứ không bao giờ ném lỗi giữa luồng stream.
/// </summary>
public class BAChatTokenFilter
{
    private enum State
    {
        /// <summary>Chưa đủ dữ liệu để biết model trả JSON hay text thuần (đang gom mở đầu).</summary>
        Detect,

        /// <summary>Text thuần: phát nguyên văn mọi delta.</summary>
        Plain,

        /// <summary>JSON: đang quét tìm khóa "message".</summary>
        SeekKey,

        /// <summary>Đã thấy "message", chờ dấu ':' (bỏ qua whitespace).</summary>
        SeekColon,

        /// <summary>Đã thấy ':', chờ dấu '"' mở chuỗi giá trị (bỏ qua whitespace).</summary>
        SeekOpenQuote,

        /// <summary>Đang trong chuỗi giá trị message: phát từng ký tự đã unescape.</summary>
        InMessage,

        /// <summary>Đã phát xong message (hoặc xác định không phát được) — nuốt phần còn lại.</summary>
        Done
    }

    private const string MessageKey = "\"message\"";

    private readonly Action<string> _emit;
    private readonly StringBuilder _buffer = new();

    private State _state = State.Detect;

    // Trạng thái unescape trong chuỗi message, giữ được qua ranh giới giữa các delta.
    private bool _pendingEscape;
    private int _pendingUnicodeDigits;
    private readonly StringBuilder _unicodeHex = new(4);

    public BAChatTokenFilter(Action<string> emit)
    {
        _emit = emit;
    }

    /// <summary>Nhận một delta thô từ model; phát phần hiển thị được (nếu có) qua callback.</summary>
    public void Feed(string delta)
    {
        if (string.IsNullOrEmpty(delta) || _state == State.Done)
            return;

        if (_state == State.Plain)
        {
            _emit(delta);
            return;
        }

        _buffer.Append(delta);

        if (_state == State.Detect && !TryDetect())
            return;

        if (_state == State.Plain)
        {
            FlushBufferAsPlain();
            return;
        }

        var output = new StringBuilder();
        ScanJson(output);

        if (output.Length > 0)
            _emit(output.ToString());
    }

    // Xác định định dạng từ phần mở đầu: bỏ whitespace + fence ```/```json, rồi nhìn ký tự đầu.
    // '{' → JSON; ký tự khác → text thuần. Trả false khi chưa đủ dữ liệu để quyết định.
    private bool TryDetect()
    {
        var start = 0;
        while (start < _buffer.Length && char.IsWhiteSpace(_buffer[start]))
            start++;

        if (start >= _buffer.Length)
            return false;

        if (_buffer[start] == '`')
        {
            // Có thể là fence ``` — cần đủ 3 backtick rồi trọn dòng fence mới bỏ được.
            var backticks = 0;
            var i = start;
            while (i < _buffer.Length && _buffer[i] == '`') { backticks++; i++; }

            if (backticks < 3)
            {
                // Mới thấy 1–2 backtick: nếu đã có ký tự khác theo sau thì không phải fence → text thuần;
                // chưa có gì theo sau thì chờ thêm dữ liệu.
                return i < _buffer.Length && SwitchToPlain();
            }

            var newline = -1;
            for (var j = i; j < _buffer.Length; j++)
            {
                if (_buffer[j] == '\n') { newline = j; break; }
            }
            if (newline < 0)
                return false; // fence chưa trọn dòng — chờ thêm

            _buffer.Remove(0, newline + 1);
            return TryDetect(); // sau fence lại dò tiếp (thường gặp '{' ngay)
        }

        _buffer.Remove(0, start);
        if (_buffer[0] == '{')
        {
            _state = State.SeekKey;
            return true;
        }

        return SwitchToPlain();
    }

    private bool SwitchToPlain()
    {
        _state = State.Plain;
        return true;
    }

    private void FlushBufferAsPlain()
    {
        if (_buffer.Length == 0)
            return;

        _emit(_buffer.ToString());
        _buffer.Clear();
    }

    // Chạy máy trạng thái JSON trên phần buffer hiện có; ký tự đã xử lý được cắt khỏi buffer.
    private void ScanJson(StringBuilder output)
    {
        var pos = 0;

        while (pos < _buffer.Length && _state != State.Done)
        {
            switch (_state)
            {
                case State.SeekKey:
                    var idx = IndexOfMessageKey(pos);
                    if (idx < 0)
                    {
                        // Giữ lại đuôi có thể là nửa đầu của "message" bị cắt giữa hai delta.
                        var keep = Math.Min(MessageKey.Length - 1, _buffer.Length - pos);
                        _buffer.Remove(0, _buffer.Length - keep);
                        return;
                    }
                    pos = idx + MessageKey.Length;
                    _state = State.SeekColon;
                    break;

                case State.SeekColon:
                    pos = SkipWhitespace(pos);
                    if (pos >= _buffer.Length) { _buffer.Clear(); return; }
                    if (_buffer[pos] == ':') { pos++; _state = State.SeekOpenQuote; }
                    else _state = State.SeekKey; // "message" nằm trong một chuỗi khác — quét tiếp
                    break;

                case State.SeekOpenQuote:
                    pos = SkipWhitespace(pos);
                    if (pos >= _buffer.Length) { _buffer.Clear(); return; }
                    if (_buffer[pos] == '"') { pos++; _state = State.InMessage; }
                    else _state = State.Done; // giá trị không phải chuỗi (null/số…) — bỏ preview
                    break;

                case State.InMessage:
                    pos = EmitMessageChars(pos, output);
                    break;
            }
        }

        _buffer.Clear();
    }

    private int IndexOfMessageKey(int from)
    {
        var limit = _buffer.Length - MessageKey.Length;
        for (var i = from; i <= limit; i++)
        {
            var match = true;
            for (var j = 0; j < MessageKey.Length; j++)
            {
                if (char.ToLowerInvariant(_buffer[i + j]) != MessageKey[j])
                {
                    match = false;
                    break;
                }
            }
            if (match)
                return i;
        }
        return -1;
    }

    private int SkipWhitespace(int pos)
    {
        while (pos < _buffer.Length && char.IsWhiteSpace(_buffer[pos]))
            pos++;
        return pos;
    }

    // Phát nội dung chuỗi message (unescape chuẩn JSON) cho tới dấu '"' đóng chuỗi.
    private int EmitMessageChars(int pos, StringBuilder output)
    {
        while (pos < _buffer.Length)
        {
            var c = _buffer[pos];
            pos++;

            if (_pendingUnicodeDigits > 0)
            {
                _unicodeHex.Append(c);
                _pendingUnicodeDigits--;
                if (_pendingUnicodeDigits == 0)
                {
                    if (int.TryParse(_unicodeHex.ToString(), System.Globalization.NumberStyles.HexNumber, null, out var code))
                        output.Append((char)code);
                    _unicodeHex.Clear();
                }
                continue;
            }

            if (_pendingEscape)
            {
                _pendingEscape = false;
                switch (c)
                {
                    case 'n': output.Append('\n'); break;
                    case 't': output.Append('\t'); break;
                    case 'r': output.Append('\r'); break;
                    case 'b': output.Append('\b'); break;
                    case 'f': output.Append('\f'); break;
                    case 'u': _pendingUnicodeDigits = 4; break;
                    default: output.Append(c); break; // \" \\ \/ và escape lạ: giữ ký tự
                }
                continue;
            }

            if (c == '\\')
            {
                _pendingEscape = true;
                continue;
            }

            if (c == '"')
            {
                _state = State.Done; // hết message — suggestions không stream
                return pos;
            }

            output.Append(c);
        }

        return pos;
    }
}
