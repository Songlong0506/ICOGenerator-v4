using ICOGenerator.Domain;
using ICOGenerator.Localization;

namespace ICOGenerator.Services.Requirements;

/// <summary>
/// CẶP HỎI–ĐÁP NGOÀI PHẠM VI: người dùng hỏi điều không dính gì tới dự án (giá vàng, thời tiết, nhờ viết
/// code…), BA từ chối bằng một câu cố định rồi đưa người dùng về đúng chỗ họ đang đứng. Lớp này giữ hai
/// luật của cặp đó, và vì nó là chỗ DUY NHẤT giữ chúng nên mọi tầng đọc hội thoại đều hỏi qua đây.
///
/// <para>
/// <b>Luật 1 — cặp này TRONG SUỐT.</b> Mọi câu hỏi về trạng thái buổi làm việc được trả lời như thể cặp đó
/// chưa từng xảy ra (<see cref="Substantive"/>): lượt BA đang đứng cuối là lời mời hay câu hỏi, người dùng đã
/// nói thêm gì về dự án kể từ lần soạn Brief gần nhất chưa, lượt cuối có được cổng readiness đóng dấu không,
/// transcript gửi bước soạn Brief gồm những gì. Ca thật sinh ra luật này: Brief V1 đã duyệt, POC đã dựng,
/// người dùng hỏi <i>"giá vàng hôm nay là bao nhiêu"</i>. Cổng đang ở trạng thái "đã soạn, chưa có gì mới",
/// nhưng nó đo "có gì mới" bằng "có lượt user nào sau lần soạn không" — nên một câu hỏi giá vàng đủ để mở lại
/// cổng, model (bản đồ vẫn 11/11) khai <c>ready</c>, và người dùng nhận về đúng một cái nút "Write
/// Requirement" kèm câu "mình đã thu thập đủ thông tin" thay cho câu trả lời. Không có gì về dự án đổi
/// cả, nên không có gì để soạn lại.
/// </para>
///
/// <para>
/// <b>Luật 2 — lượt từ chối do CƠ CHẾ dựng, không do model viết</b> (<see cref="Shape"/>). Model chỉ khai cờ
/// (<c>BAChatReply.OffTopic</c>) — phân biệt "câu ngoài phạm vi" với "câu trả lời cụt" là việc về NGHĨA, chỉ
/// model làm được. Còn câu chữ thì cố định: một câu từ chối không bao giờ được vô tình trả lời câu hỏi (một
/// con số giá vàng do model đoán, đặt trong khung làm việc của nhà máy, còn tệ hơn không trả lời), và phần
/// đưa người dùng về chỗ cũ phải chở ĐÚNG chỗ trả lời đang dở — chép từ chính lượt đã hỏi, không để model
/// viết lại một câu gần giống rồi lệch chip.
/// </para>
///
/// <para>
/// ĐỪNG lọc cặp này khỏi ngữ cảnh lượt CHAT (<c>BAChatService.AppendTranscript</c>): model cần thấy mình đã
/// từ chối gì để nhất quán khi người dùng hỏi lại hay hỏi vặn. Nó chỉ bị lọc khỏi các tầng ĐÚC KẾT YÊU CẦU
/// (<see cref="ConversationTurnRenderer.CarriesNoRequirement"/>).
/// </para>
/// </summary>
public static class OffTopicExchange
{
    /// <summary>
    /// Các lượt THUỘC buổi làm việc — bỏ mọi lượt của cặp hỏi–đáp ngoài phạm vi. Giữ nguyên thứ tự đầu vào;
    /// caller tự sắp (CreatedAt rồi Id) như mọi chỗ đọc hội thoại khác.
    /// </summary>
    public static IEnumerable<AgentConversation> Substantive(IEnumerable<AgentConversation> conversations)
        => conversations.Where(c => !c.OffTopic);

    /// <summary>
    /// Lượt BA mà người dùng ĐANG NỢ câu trả lời ngay trước câu ngoài phạm vi, hoặc null khi không có.
    /// <paramref name="priorTurns"/> là các lượt ĐỨNG TRƯỚC lượt user vừa gửi, đã sắp thứ tự.
    ///
    /// <para>
    /// Lượt thuộc buổi làm việc ĐỨNG CUỐI phải là lượt BA, và lượt đó phải đang chờ trả lời: hỏi (dấu hỏi
    /// hoặc có chip/thẻ hỏi — cùng phép thử <see cref="AskedQuestionHistory.IsAskingTurn"/>) hoặc xin file.
    /// Không tính: lời mời (cổng tạo tài liệu là chỗ trả lời của nó, và cổng vẫn đứng nguyên), lượt bày bảng
    /// (bảng treo theo dự án nên nó vẫn nằm trên màn hình), lượt ⚠️ lỗi. Lượt cuối là lượt USER (đã trả lời
    /// nhưng chưa có lượt BA nối tiếp) thì BA không còn câu nào đang chờ.
    /// </para>
    /// </summary>
    public static AgentConversation? PendingAsk(IEnumerable<AgentConversation> priorTurns)
    {
        var last = Substantive(priorTurns).LastOrDefault();
        if (last == null
            || !ConversationTurnRenderer.IsAssistant(last)
            || ConversationTurnRenderer.IsFailureTurn(last)
            || last.ReadinessVerified
            || !string.IsNullOrWhiteSpace(last.FlowMap))
            return null;

        var hasAnswerPlace = ConversationTurnRenderer.ParseSuggestions(last.Suggestions).Count > 0
                             || ConversationTurnRenderer.ParseQuestions(last.Questions).Count > 0;
        return last.SourceRequested || AskedQuestionHistory.IsAskingTurn(last.Message, hasAnswerPlace)
            ? last
            : null;
    }

    /// <summary>
    /// Dựng TRỌN lượt từ chối lên <paramref name="draft"/>: câu từ chối cố định, rồi hoặc chép lại câu hỏi
    /// đang dở (<paramref name="pendingAsk"/> — kèm đúng chip / thẻ hỏi của nó), hoặc một câu mời quay lại
    /// dự án. Lượt không bao giờ là lời mời bấm nút: cổng tạo tài liệu tự giữ trạng thái của nó.
    /// </summary>
    internal static void Shape(BAChatTurnDraft draft, AgentConversation? pendingAsk)
    {
        var decline = Loc.T("OffTopicExchange.Decline");
        draft.Replace(decline + " " + Loc.T("OffTopicExchange.Steer"), openEnded: false);
        draft.OffTopic = true;

        if (pendingAsk == null)
            return;

        // Lượt GỘP: câu dẫn của lượt này làm dòng đầu của thẻ hỏi (thẻ nuốt bong bóng câu dẫn — xem
        // batchLeadTurn ở Index.cshtml), nên câu từ chối + câu nối CHÍNH LÀ câu dẫn.
        var questions = ConversationTurnRenderer.ParseQuestions(pendingAsk.Questions);
        if (questions.Count > 0)
        {
            draft.Reply = decline + " " + Loc.T("OffTopicExchange.BackToQuestions");
            draft.Questions = questions;
            return;
        }

        // Lượt MỘT CÂU (hoặc lời xin file): Message chở câu hỏi, chép nguyên văn để chip đi kèm vẫn khớp.
        draft.Reply = decline + "\n\n" + Loc.T("OffTopicExchange.BackToQuestion") + "\n" + (pendingAsk.Message ?? string.Empty).Trim();
        if (ConversationTurnRenderer.ParseSuggestions(pendingAsk.Suggestions).Count > 0)
        {
            draft.SuggestionsJson = pendingAsk.Suggestions;
            draft.SuggestionsMultiSelect = pendingAsk.SuggestionsMultiSelect;
        }
        else
        {
            // Câu không chip là câu mở hoặc lời xin file — cả hai đều trả lời bằng ô nhập.
            draft.OpenEnded = true;
        }
    }
}
