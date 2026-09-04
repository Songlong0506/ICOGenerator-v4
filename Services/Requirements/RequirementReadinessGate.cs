using ICOGenerator.Contracts.Requirements;
using ICOGenerator.Domain;

namespace ICOGenerator.Services.Requirements;

/// <summary>
/// Cổng readiness DUY NHẤT và TẤT ĐỊNH quyết định "đã đủ thông tin cốt lõi để soạn tài liệu chưa":
/// suy trực tiếp từ "Bản đồ bao phủ yêu cầu" (<see cref="Project.RequirementCoverageMap"/>, do
/// <see cref="RequirementCoverageService"/> duy trì) — sẵn sàng ⇔ mọi dòng áp dụng đã <c>[RÕ]</c>.
/// Bản đồ là nguồn chân lý duy nhất nên panel "Tiến độ khai thác" trên UI, lời mời bấm
/// "Write Requirement" của BA và cổng lúc bấm nút KHÔNG THỂ vênh nhau — cả ba đọc cùng một dữ liệu.
/// (Trước đây cổng là một lời gọi LLM riêng chấm lại transcript: hai "giám khảo" lệch nhau tạo cảnh
/// panel báo 9/12 nhưng BA vẫn mời bấm nút, và gate lỗi thì fail-open thành ready.) Không sẵn sàng ⇒
/// trả về CÂU HỎI cho đúng chỗ còn thiếu — lấy từ danh sách câu hỏi (<see cref="OpenQuestionDocument"/>,
/// truyền vào cùng bản đồ vì cả hai được lượt distill ghi ra trong CÙNG một lời gọi). Bản đồ chưa có/lỗi
/// gộp ⇒ CHƯA sẵn sàng (fail-closed): distiller giữ con trỏ cũ và gộp bù ở lượt sau nên trạng thái tự lành.
/// </summary>
public static class RequirementReadinessGate
{
    /// <summary>
    /// Xét độ sẵn sàng từ bản đồ bao phủ: ready ⇔ bản đồ đã có, không còn dòng áp dụng nào
    /// [CHƯA HỎI]/[MỘT PHẦN], và có ít nhất một dòng [RÕ] (bản đồ toàn [KHÔNG ÁP DỤNG] là bản đồ hỏng,
    /// không phải dự án đã rõ). Khi chưa sẵn sàng, Message là CÂU HỎI dựng sẵn cho đúng chỗ còn thiếu —
    /// dùng được ngay như một lượt BA trong khung chat.
    ///
    /// <para>
    /// <paramref name="turns"/> là các lượt hội thoại gần đây, dùng để KHÔNG phát lại đúng câu chặn vừa
    /// phát: cổng dò chính CÂU HỎI mà nó sắp phát (xem <see cref="LastAskedAt"/>) trong các lượt BA đã lưu,
    /// rồi chuyển sang một chỗ còn thiếu khác trước khi quay lại chỗ cũ. Bỏ trống ⇒ giữ nguyên thứ tự cũ
    /// (★ cốt lõi trước) — đúng cho những caller chỉ cần cờ <c>Ready</c>.
    /// </para>
    ///
    /// <para>
    /// <paramref name="relatedTo"/> là CÂU HỎI của BA vừa bị phanh chống-hỏi-lại chặn — chỉ đường này
    /// truyền vào. Nó không đổi được luật xoay vòng ở trên (chỗ chưa hỏi vẫn đi trước), chỉ phá thế hoà
    /// TRONG cùng một bậc: giữa các dòng đều chưa bị cổng hỏi lần nào, chọn dòng gần chủ đề vừa bị chặn
    /// nhất. Không có nó, cổng chọn theo cờ ★ và người dùng đang bàn dở vai trò thì bị hỏi sang một nhóm
    /// xa nhất có thể — ca thật (2026-09-03): lượt hỏi vai Nhân viên bị chặn, cổng phát ngay câu xin ví dụ
    /// tính thử của nhóm «Quy tắc nghiệp vụ». Xem <see cref="TopicOverlap"/> cho ngưỡng và giới hạn.
    /// </para>
    ///
    /// <para>
    /// <b>Vì sao cổng phải tự nhớ.</b> Phanh chống hỏi lại dùng chung
    /// (<see cref="AskedQuestionHistory.Collect"/>) chỉ trả lời được MỘT câu: "câu này hỏi rồi hay chưa".
    /// Cổng cần thứ khác — THỨ TỰ: nó phải dựng câu hỏi của MỌI dòng còn thiếu rồi xếp theo lần cuối mỗi
    /// câu được phát, để chỗ chưa hỏi đi trước và chỗ vừa hỏi lùi lại một vòng. Sổ chung không mang vị
    /// trí, nên cổng dò bằng chính câu hỏi nó sắp phát trong các lượt BA đã lưu.</para>
    ///
    /// <para>
    /// Lượt chặn của cổng KHÔNG có chip (nó là câu MỞ) nhưng luôn kết bằng dấu hỏi, nên nó vẫn vào sổ
    /// chung — model đọc "các câu BẠN ĐÃ HỎI" sẽ không phát lại nó bằng lời của mình.
    /// </para>
    /// </summary>
    public static RequirementReadiness Evaluate(
        string? coverageMap,
        IReadOnlyList<OpenQuestionEntry> openQuestions,
        IEnumerable<AgentConversation>? turns = null,
        string? relatedTo = null)
    {
        // Bản đồ chở TRẠNG THÁI, danh sách kia chở CÂU HỎI: cổng cần cả hai, nên nó nối chúng lại ngay ở
        // đầu đường đọc rồi mọi nhánh dưới chỉ còn làm việc với dòng bản đồ như trước.
        var items = CoverageMapParser.AttachQuestions(CoverageMapParser.Parse(coverageMap), openQuestions);
        if (items.Count == 0)
        {
            return new RequirementReadiness
            {
                Ready = false,
                Message = "Mình chưa tổng hợp được bản đồ khai thác yêu cầu cho dự án này, nên chưa thể viết tài liệu. Bạn trao đổi thêm một lượt trong khung chat rồi thử lại nhé.",
                OpenEnded = true
            };
        }

        var pending = items.Where(x => x.Status is "MỘT PHẦN" or "CHƯA HỎI").ToList();

        if (pending.Count == 0)
            return items.Any(x => x.Status == "RÕ")
                ? new RequirementReadiness { Ready = true }
                // Bản đồ toàn [KHÔNG ÁP DỤNG] — bản đồ hỏng, không có nhóm nào để hỏi cụ thể.
                : new RequirementReadiness
                {
                    Ready = false,
                    Message = "Bản đồ khai thác yêu cầu đang trống thông tin đã rõ, nên chưa thể viết tài liệu. Bạn mô tả thêm về dự án trong khung chat giúp mình nhé.",
                    OpenEnded = true
                };

        // Chỗ CHƯA từng bị cổng hỏi đi trước, rồi mới tới chỗ bị hỏi lâu nhất; trong cùng một bậc thì ★
        // cốt lõi trước — đúng thứ tự ưu tiên mà prompt chat hướng dẫn BA chọn câu hỏi kế tiếp.
        //
        // Vì sao "đã hỏi" thắng cả cờ ★: bản đồ không nhúc nhích thì mọi lượt chặn tiếp theo chọn lại đúng
        // dòng cốt lõi đó và phát lại nguyên văn một câu người dùng vừa không trả lời được. Ca thật đã ghi ở
        // CoverageDeadQuestionLoopTests: ba lượt liên tiếp giống hệt nhau, người dùng đáp "mình không hiểu
        // câu hỏi của bạn" hai lần. Đổi chỗ hỏi thì lượt sau còn cơ hội gỡ, mà chỗ cũ không mất đi đâu — nó
        // quay lại ngay khi các chỗ khác đã được hỏi một vòng.
        //
        // Sổ này dò bằng CHÍNH CÂU HỎI sắp phát, không bằng nhãn nhóm: câu chặn không còn đọc nhãn nhóm ra
        // màn hình nữa (xem BuildPendingQuestion), nên nhãn không còn nằm trong lượt đã lưu để đọc lại. Đổi
        // sang so bằng câu hỏi còn đúng hơn ở đúng chỗ phải đúng: danh sách nhúc nhích thì câu hỏi của
        // nhóm đổi theo — và một câu hỏi KHÁC thì đáng hỏi ngay, không phải đợi hết một vòng.
        var candidates = pending.Select(item => (Item: item, Question: AskFor(item))).ToList();
        var chosen = candidates
            .OrderBy(x => LastAskedAt(turns, x.Question))
            .ThenByDescending(x => TopicOverlap(relatedTo, x.Item))
            .ThenByDescending(x => x.Item.IsCore)
            .First();

        return new RequirementReadiness
        {
            Ready = false,
            Message = BuildPendingQuestion(chosen.Question, LastAskedAt(turns, chosen.Question) >= 0),
            OpenEnded = true
        };
    }

    /// <summary>
    /// CHỈ cờ sẵn sàng, không dựng câu chặn — nên không cần danh sách câu hỏi. "Đã đủ vốn chưa" là một
    /// tính chất của TRẠNG THÁI 12 dòng và chỉ của nó (bất biến "nhóm còn câu hỏi MỞ thì không được [RÕ]"
    /// đã được áp ở ĐƯỜNG GHI bởi <see cref="CoveragePendingGuard"/>); danh sách câu hỏi chỉ quyết định
    /// lượt chặn NÓI GÌ khi chưa đủ. Các chỗ chỉ cần bật/tắt nút gọi hàm này để không phải nặn ra một
    /// danh sách rỗng cho có.
    /// </summary>
    public static bool IsReady(string? coverageMap)
        => Evaluate(coverageMap, Array.Empty<OpenQuestionEntry>()).Ready;

    /// <summary>
    /// Vị trí lượt CUỐI mà <paramref name="question"/> đã được phát trong <paramref name="turns"/>, hoặc
    /// <c>-1</c> khi chưa phát lần nào. Sắp TĂNG theo giá trị này cho ra đúng thứ tự cần: chưa hỏi (-1)
    /// trước, rồi tới câu bị hỏi lâu nhất.
    ///
    /// <para>
    /// Đây là sổ RIÊNG của cổng, không dùng <see cref="AskedQuestionHistory.Collect"/>: sổ chung chỉ nói
    /// "đã hỏi hay chưa", còn cổng cần VỊ TRÍ để xoay vòng (xem <see cref="Evaluate"/>). Dò trên VẾ CÂU HỎI nên cả
    /// hai biến thể của câu chặn đều được đọc ra — lượt "quay lại" chỉ thêm một câu dẫn ở ĐẦU, vế hỏi phía
    /// sau giữ nguyên. Chuẩn hóa hoa/thường + khoảng trắng để một lượt bị xuống dòng khác đi không làm sổ
    /// này câm trong im lặng.
    /// </para>
    /// </summary>
    public static int LastAskedAt(IEnumerable<AgentConversation>? turns, string question)
    {
        var needle = Normalize(question);
        if (needle.Length == 0)
            return -1;

        var last = -1;
        var index = 0;
        foreach (var turn in turns ?? Enumerable.Empty<AgentConversation>())
        {
            if (ConversationTurnRenderer.IsAssistant(turn)
                && Normalize(turn.Message).Contains(needle, StringComparison.Ordinal))
                last = index;
            index++;
        }

        return last;
    }

    private static string Normalize(string? text)
        => string.Join(' ', (text ?? string.Empty)
                .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
            .ToLowerInvariant();

    /// <summary>
    /// Số TỪ NỘI DUNG mà một dòng bản đồ dùng chung với câu hỏi vừa bị phanh chặn — thước đo "cùng chủ đề"
    /// rẻ nhất còn đọc được ở tầng này. <c>0</c> khi không có câu bị chặn, hoặc khi phần chung chưa đủ để
    /// nói lên điều gì.
    ///
    /// <para>
    /// <b>Chỉ là thước phá thế hoà, không phải luật chọn.</b> Nó đứng SAU sổ "đã hỏi" trong
    /// <see cref="Evaluate"/> nên không bao giờ kéo cổng quay lại một câu vừa phát — đúng cái vòng lặp câu
    /// hỏi chết mà thứ tự kia sinh ra để cắt. Chấm sai thì cái giá là cổng hỏi một nhóm khác cũng đang
    /// thiếu, tức vẫn là một câu hỏi hợp lệ; vì vậy phép đo cố ý thô: đếm từ chung sau khi bỏ hư từ và
    /// văn mẫu phỏng vấn (<see cref="TopicStopWords"/>), không đo tỷ lệ.
    /// </para>
    ///
    /// <para>
    /// Sàn <see cref="MinTopicOverlap"/> giữ cho MỘT từ trùng ngẫu nhiên không quyết định được gì: dưới
    /// sàn thì trả 0 và cờ ★ cốt lõi lại là thứ phân định, y như trước khi có tham số này.
    /// </para>
    /// </summary>
    private static int TopicOverlap(string? relatedTo, CoverageMapItem item)
    {
        var topic = ContentWords(relatedTo);
        if (topic.Count == 0)
            return 0;

        var row = ContentWords($"{item.Label} {item.Known} {string.Join(' ', item.Questions)}");
        var shared = topic.Count(row.Contains);
        return shared >= MinTopicOverlap ? shared : 0;
    }

    /// <summary>Số từ chung tối thiểu để hai bên được coi là cùng chủ đề.</summary>
    private const int MinTopicOverlap = 2;

    private static HashSet<string> ContentWords(string? text)
        => new(
            AskedQuestionHistory.Key(text)
                .Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Where(word => !TopicStopWords.Contains(word)),
            StringComparer.Ordinal);

    // Hư từ tiếng Việt + văn mẫu của buổi phỏng vấn. Từ nào cũng có mặt ở gần như MỌI câu hỏi lẫn mọi dòng
    // bản đồ, nên để lại thì dòng nào cũng "cùng chủ đề" với nhau và thước đo mất hết sức phân biệt. Đây là
    // danh sách của tầng ĐO ĐỘ GẦN CHỦ ĐỀ, không phải một bộ tách từ: nó không cần đầy đủ, chỉ cần bỏ đi
    // những từ không nói lên chủ đề nào.
    private static readonly HashSet<string> TopicStopWords = new(StringComparer.Ordinal)
    {
        "anh", "chị", "mình", "bạn", "ai", "họ",
        "của", "cho", "và", "hay", "hoặc", "với", "về", "từ", "theo", "trong", "ở", "vào", "ra", "đến", "tới",
        "là", "có", "không", "còn", "thì", "mà", "nếu", "khi", "vậy", "này", "đó", "các", "những", "một",
        "nào", "gì", "khác", "nữa", "thêm", "sao", "thế", "như", "ví", "dụ", "chẳng", "hạn",
        "sẽ", "đã", "đang", "được", "cần", "phải", "để", "làm", "việc", "biết",
        "ạ", "nhé", "ơi", "xin", "hỏi", "trả", "lời", "ghi", "nhận", "bây", "giờ", "hiện", "nay",
        // Văn mẫu của chính sản phẩm: có trong mọi câu hỏi lẫn mọi nhãn nhóm.
        "ứng", "dụng", "hệ", "thống", "phần", "mềm", "app"
    };

    // Câu hỏi dựng sẵn khi chưa đủ. Đây là lượt BA mà người dùng THẬT SỰ đọc trên màn hình, nên nó phải
    // là một câu hỏi TRẢ LỜI ĐƯỢC, không phải một bản tin trạng thái:
    //
    // - Hỏi ĐÚNG câu hỏi mà distiller gắn vào nhóm [MỘT PHẦN] — đó là thứ duy nhất bước soạn tài liệu còn
    //   phải tự đoán.
    // - KHÔNG đọc cả tóm tắt máy vào câu hỏi khi nhóm đã có câu hỏi: tóm tắt là ghi chép của hệ thống về
    //   điều người dùng đã nói, phát lại nguyên khối trong khi đã hỏi được đúng chỗ hụt chỉ làm người đọc
    //   tưởng bị hỏi lại điều họ vừa trả lời.
    // - KHÔNG nói ra nhãn nhóm, cũng không đếm số nhóm còn lại. Bản trước mở đầu bằng *"Trước khi viết tài
    //   liệu, mình còn một chỗ chưa đủ thông tin để khỏi phải tự đoán (nhóm «Đối tượng người dùng & vai
    //   trò», còn 3 nhóm — mình hỏi từng nhóm một)"* rồi mới tới câu hỏi thật. Cả cụm đó là SỔ SÁCH của hệ
    //   thống đọc ra màn hình: «Dữ liệu / danh mục chính» là từ vựng nội bộ của bản đồ, còn "còn 3 nhóm"
    //   thì báo cho người dùng biết họ còn phải chịu bao nhiêu lượt nữa — không giúp họ trả lời câu đang
    //   hỏi, chỉ làm lượt đó đọc như một bản tin tiến độ. Người dùng của repo yêu cầu đúng điều này: "BA có
    //   câu hỏi nào thì cứ hỏi thẳng luôn, không cần phải nói nhóm gì hết". Vì vậy lượt chặn chỉ chở CÂU
    //   HỎI; nhãn nhóm vẫn ở nguyên panel "Tiến độ khai thác" bên cạnh cho ai muốn xem.
    // - Kết thúc bằng dấu hỏi và chỉ hỏi MỘT chỗ, không hỏi dồn.
    //
    // BỐN NHÁNH, hẹp dần theo lượng thông tin bản đồ cho — và không nhánh nào được rơi về một câu trống
    // nghĩa. Bản trước chỉ có hai nhánh, nhánh dự phòng phát MỘT câu duy nhất cho cả 12 nhóm
    // (*"Anh/chị kể giúp mình phần này…"*): nó không nói được đang hỏi cái gì và trỏ tới "phần này" — đúng
    // cụm tham chiếu suông mà prompt cấm BA dùng. Ca thật ở dự án JD Library lượt 76: người dùng đáp
    // *"mình chưa hiểu câu hỏi, hãy hỏi rõ hơn"*, mất trắng một vòng ở cuối buổi phỏng vấn thứ 78. Nhánh đó
    // reachable với BẤT KỲ nhóm nào, chỉ cần lượt distill quên viết câu hỏi cho nhóm đó đúng một lần — nó
    // là nội dung do LLM xuất, không phải bất biến của code.
    /// <summary>
    /// Câu dẫn DUY NHẤT mà cổng được phép thêm vào trước vế hỏi: nó chỉ dùng khi cổng quay lại một câu đã
    /// phát. Phát lại y nguyên câu cũ đọc lên như thể cổng không nhớ mình vừa hỏi gì — mà đúng cái đó là thứ
    /// làm người dùng thôi trả lời. Đứng ở ĐẦU nên <see cref="LastAskedAt"/> (dò trên vế hỏi phía sau) vẫn
    /// nhận ra cả hai biến thể.
    /// </summary>
    private const string ComingBackLead = "Mình quay lại chỗ này một chút. ";

    // Lần đầu thì KHÔNG có câu dẫn nào: câu hỏi đứng một mình đúng như một lượt BA bình thường.
    private static string BuildPendingQuestion(string question, bool askedBefore)
        => askedBefore ? ComingBackLead + question : question;

    /// <summary>
    /// Vế câu hỏi thật cho một dòng còn thiếu, thử bốn nhánh theo đúng thứ tự thông tin đáng tin cậy giảm
    /// dần. Tách khỏi <see cref="BuildPendingQuestion"/> vì nó còn là KHÓA của sổ "đã hỏi" (xem
    /// <see cref="LastAskedAt"/>): cổng phải dựng được câu hỏi của MỌI dòng còn thiếu để so, rồi mới chọn
    /// dòng nào để phát — nên vế hỏi không được dính câu dẫn của lượt phát.
    /// </summary>
    private static string AskFor(CoverageMapItem item)
    {
        // 1. Câu hỏi còn treo của nhóm — thứ duy nhất bước soạn tài liệu còn phải tự đoán, nên hỏi thẳng nó.
        var missing = ExtractMissingPart(item);
        if (!string.IsNullOrWhiteSpace(missing))
            return $"Anh/chị cho mình hỏi thêm: {ToQuestion(missing)}";

        // 2. [MỘT PHẦN] mà không có mẩu nào ⇒ PHÁT LẠI phần đã ghi nhận rồi hỏi còn hụt gì. Không được rơi
        //    xuống câu mở đầu của nhóm ở ca này: prompt chat cấm tuyệt đối việc phát lại câu mở đầu cho một
        //    nhóm [MỘT PHẦN] — người dùng đã kể phần đó rồi, nghe lại đúng câu cũ là mất lòng tin vào cả
        //    buổi phỏng vấn. Phát lại lời họ thì ngược lại: nó miễn cho họ việc phải cuộn ngược lên tìm.
        var recorded = ExtractRecordedPart(item.Known);
        if (recorded.Length > 0)
            return $"Mình đang ghi nhận: {recorded}. Phần này còn chỗ nào chưa đúng hoặc còn thiếu mà "
                + "anh/chị muốn bổ sung không?";

        // 3. [CHƯA HỎI] (và [MỘT PHẦN] rỗng ruột — dòng chỉ còn ghi chú máy đã bị lược sạch) ⇒ câu mở đầu
        //    THẬT của nhóm, bằng ngôn ngữ công việc của người dùng.
        var opener = CoverageGroupOpeners.Find(item.Label);
        if (opener != null)
            return opener;

        // 4. Nhãn không khớp nhóm nào (model tự nghĩ ra một tên) ⇒ không bịa một câu hỏi khai thác về thứ
        //    không có trong checklist, nhưng cũng KHÔNG được trỏ tới "phần này" suông. Nhãn được đọc vào câu
        //    như một cụm chủ đề bình thường ("Về tích hợp hệ thống ngoài, …") — đó là ngôn ngữ tự nhiên, khác
        //    hẳn cái ngoặc sổ sách "(nhóm «…»)" mà bản trước in ra.
        var topic = item.Label.Trim();
        return topic.Length == 0
            ? "Anh/chị kể giúp mình chỗ này trong công việc thực tế hiện đang diễn ra thế nào?"
            : $"Về {char.ToLowerInvariant(topic[0])}{topic[1..]}, hiện trong công việc thực tế của anh/chị "
              + "đang diễn ra thế nào?";
    }

    // Câu hỏi được phát cho một dòng [MỘT PHẦN]: câu ĐẦU TIÊN dùng được của nhóm, đã lược hai ghi chú máy
    // (cụm tái mở và "(ghi nhận trước đó: …)"). Câu KHÔNG DÙNG ĐƯỢC (rỗng nghĩa, tường thuật trạng thái,
    // hoặc gắn vào một nhóm chốt-bằng-bảng) bị bỏ qua; hết câu dùng được thì trả rỗng để caller rơi về
    // nhánh PHÁT LẠI.
    //
    // Chỉ MỘT câu mỗi lượt dù nhóm có nhiều: lượt chặn của cổng là một lượt BA bình thường, và dội cả cụm
    // vào đó là đúng cái lượt hỏi dồn mà người dùng chỉ trả lời được vế đầu. Các câu còn lại không mất đi
    // đâu — chúng vẫn nằm trong ngữ cảnh chat của BA, và cổng quay lại nhóm này ở vòng sau.
    //
    // Phép thử nằm ở CoverageQuestionGuard và guard đó đã lọc ngay ở đường ghi; cổng vẫn gọi lại nó vì danh
    // sách của một dự án chỉ được lọc từ lượt distill KẾ TIẾP trở đi — thứ đang nằm trong DB lúc người dùng
    // bấm nút thì chưa qua lớp nào cả.
    private static string ExtractMissingPart(CoverageMapItem item)
        => item.Questions
               .Where(q => CoverageQuestionGuard.IsUsable(q, item.Label))
               .Select(CoverageQuestionGuard.StripMachineNotes)
               .FirstOrDefault(q => q.Length > 0)
           ?? string.Empty;

    /// <summary>
    /// Trần AN TOÀN của phần phát lại — chống một dòng bản đồ HỎNG đổ nguyên biên bản vào một bong bóng
    /// chat, KHÔNG phải trần trình bày. Nó cố ý đặt cao hơn hẳn mọi phần <c>known</c> đúng chuẩn:
    /// <c>requirement-coverage.v5.md</c> bắt <c>known</c> "tối đa ~2 câu", nên ở một bản đồ lành trần này
    /// KHÔNG BAO GIỜ chạm tới.
    ///
    /// <para>
    /// <b>Vì sao không còn là 200.</b> Con số cũ chép từ <c>CoveragePendingGuard.MaxQuestionChars</c>, nhưng
    /// hai trần làm hai việc ngược nhau: bên kia cắt thứ được GHI VÀO bản đồ (một mẩu máy đọc, cắt ngắn
    /// vẫn còn nguyên nghĩa), còn ở đây là thứ được ĐỌC RA cho người dùng rà. Mà nhánh phát lại hỏi đúng
    /// một câu: *"phần này còn chỗ nào chưa đúng hoặc còn thiếu không?"* — cắt bản ghi nhận đi thì câu hỏi
    /// đó tự vô hiệu, người dùng không có cách nào biết chỗ bị cắt có sai hay có thiếu gì. Ca thật đã lên
    /// màn hình: một dòng «Mục tiêu / bài toán» dài 204 ký tự bị cắt đúng giữa cụm cuối, người dùng đọc
    /// được một câu kết bằng *"…. Phần này còn chỗ nào chưa đúng…?"* và không rà được gì.
    /// </para>
    /// </summary>
    private const int MaxRecordedChars = 800;

    /// <summary>
    /// Dấu kết CÂU dùng để cắt phần phát lại. Chỉ có ba: dấu chấm, chấm phẩy và xuống dòng — một bản ghi
    /// nhận không kết bằng "!" hay "?", và cắt ở một dấu như thế sẽ để lại câu hỏi giả trong lời phát lại.
    /// </summary>
    private static readonly char[] RecordedSentenceEnders = { '.', ';', '\n' };

    // Phần ĐÃ GHI NHẬN của một dòng [MỘT PHẦN] — trường `known`, một ô riêng nên không phải cắt chuỗi. Dùng
    // để phát lại theo "QUY TẮC PHÁT LẠI" của prompt chat khi nhóm không còn câu hỏi dùng được — người dùng chỉ thấy ô
    // chat cuối trên màn hình, nên một câu hỏi bổ sung không kèm phần phát lại là câu hỏi họ phải cuộn
    // ngược lên mới trả lời được, và phần lớn sẽ không cuộn.
    //
    // Ghi chú máy bị lược SẠCH trước khi phát: cụm ReopenNote và mẩu "(ghi nhận trước đó: …)" là ghi chép
    // của hệ thống dành cho BA, đọc lên là xưng "người dùng" ở ngôi thứ ba với chính người đang đọc. Lược
    // hết mà không còn gì ⇒ trả rỗng để caller rơi về câu mở đầu của nhóm.
    private static string ExtractRecordedPart(string? known)
    {
        if (string.IsNullOrWhiteSpace(known))
            return string.Empty;

        var recorded = known.Trim();

        var note = recorded.IndexOf(CoverageQuestionGuard.RecordedNote, StringComparison.OrdinalIgnoreCase);
        if (note >= 0)
            recorded = recorded[..note].Trim();

        recorded = CoverageQuestionGuard.StripReopenMarker(recorded).Trim();
        return TrimToWholeSentences(recorded).TrimEnd('.', ';', ',', '—', '-');
    }

    /// <summary>
    /// Phần phát lại, cắt ở RANH GIỚI CÂU cuối cùng nằm trong trần — và chỉ khi có một ranh giới như thế.
    /// Dưới trần thì trả về NGUYÊN VĂN: mặc định của nhánh phát lại là đọc đủ điều đã ghi nhận, vì đó là
    /// thứ duy nhất người dùng có để rà.
    ///
    /// <para>
    /// Không còn cắt giữa chừng rồi dán "…". Cắt theo ký tự làm hỏng đúng cái nó phục vụ: câu cụt không rà
    /// được, mà dấu "…" cũng không nói được phần bị nuốt là gì — người dùng chỉ biết mình đang thiếu thông
    /// tin, không biết thiếu gì. Cắt theo câu thì phần đọc được luôn là những câu TRỌN VẸN, nên dù có chạm
    /// trần (chỉ xảy ra với bản đồ hỏng) lời phát lại vẫn là thứ trả lời được.
    /// </para>
    ///
    /// <para>
    /// Không có dấu kết câu nào trong trần (cả phần ghi nhận là MỘT câu chạy dài) ⇒ phát nguyên văn, chấp
    /// nhận một bong bóng dài. Cắt giữa một câu đơn là mất đúng vế cuối — thường là vế chở điều kiện — và
    /// đó là ca tệ nhất chứ không phải ca an toàn.
    /// </para>
    /// </summary>
    private static string TrimToWholeSentences(string recorded)
    {
        if (recorded.Length <= MaxRecordedChars)
            return recorded;

        var cut = recorded.LastIndexOfAny(RecordedSentenceEnders, MaxRecordedChars - 1);
        return cut > 0 ? recorded[..(cut + 1)].TrimEnd() : recorded;
    }

    private static string ToQuestion(string missing)
        => missing.EndsWith('?') ? missing : missing + " — anh/chị cho mình xin thông tin này nhé?";

    // Lượt BA "mời bấm Write Requirement" — cùng tín hiệu mà UI dùng để làm nổi nút (Index.cshtml đọc
    // Contains tương tự trên lượt BA mới nhất) và BuildAssistantContext dùng để echo cờ ready. Từ khi
    // cổng tất định chạy ngay trong lượt chat, một lời mời được LƯU đồng nghĩa bản đồ bao phủ đã đủ
    // (mọi dòng áp dụng [RÕ]) tại thời điểm đó.
    public static bool IsWriteRequirementInvite(string? message) =>
        message?.Contains("Write Requirement", StringComparison.OrdinalIgnoreCase) ?? false;

    /// <summary>
    /// Lượt BA sắp được lưu có phải lượt "cổng readiness đã PASS tại đây" không — tức là nó MỜI bấm
    /// "Write Requirement" VÀ bản đồ bao phủ hiện hành đủ để lời mời đó hợp lệ. Kết quả được đóng dấu
    /// vào <see cref="AgentConversation.ReadinessVerified"/> của chính lượt đó.
    ///
    /// <para>
    /// Phép dò chuỗi nằm ở ĐÂY và chỉ ở đây: "lượt này có mời bấm nút không" là một tính chất của CHỮ mà
    /// model vừa sinh ra, không có tín hiệu nào khác để đọc. Cái đã bỏ đi là việc các tầng SAU (bước soạn
    /// tài liệu) phải suy lại kết luận của cổng bằng cách đọc lại transcript: quyết định được ra MỘT LẦN,
    /// ở nơi biết đủ dữ kiện, rồi được ghi lại.
    /// </para>
    /// </summary>
    public static bool IsReadinessVerifiedTurn(string? message, string? coverageMap)
        => IsWriteRequirementInvite(message) && IsReady(coverageMap);

    /// <summary>
    /// Hội thoại đang ĐỨNG trên một lượt đã được cổng verify ⇒ bước soạn tài liệu được phép bỏ qua lần xét
    /// lại (không có thông tin mới nào kể từ lúc cổng cho qua). Thứ tự CreatedAt rồi Id — như
    /// <c>ConversationTranscriptBuilder</c> — vì CreatedAt có thể trùng.
    ///
    /// <para>
    /// Đọc CỜ chứ không đọc nội dung lượt cuối, và không lọc lượt rỗng: mọi đường ghi thêm một lượt đều
    /// mặc định <c>false</c>, nên bất kỳ thứ gì chen vào sau lời mời (một lượt chat mới, một file vừa đính
    /// kèm, một lượt ⚠️ lỗi LLM) đều tự động đóng đường tắt lại. Một đường ghi muốn giữ đường tắt phải TỰ
    /// KHẲNG ĐỊNH rằng mình không mang thông tin mới, bằng cách chép cờ của lượt nó vừa đè sang lượt của
    /// mình; hiện KHÔNG đường nào làm thế, nên mọi lượt mới đều đóng cổng lại.
    /// </para>
    /// </summary>
    public static bool IsReadinessVerifiedLatestTurn(IEnumerable<AgentConversation> conversations)
        => conversations
            .OrderBy(c => c.CreatedAt)
            .ThenBy(c => c.Id)
            .LastOrDefault()?.ReadinessVerified == true;
}
