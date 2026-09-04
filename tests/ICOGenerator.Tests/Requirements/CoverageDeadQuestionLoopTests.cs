using ICOGenerator.Contracts.Requirements;
using ICOGenerator.Domain;
using ICOGenerator.Services.Requirements;
using Xunit;

namespace ICOGenerator.Tests.Requirements;

// Vòng lặp câu hỏi chết — ca thật đã gặp trên màn hình, ba lượt liên tiếp giống hệt nhau:
//
//   BA:          "Trước khi viết tài liệu, mình cần làm rõ thêm nhóm thông tin «Dữ liệu / danh mục
//                 chính». Trước tiên về «Dữ liệu / danh mục chính» (…) — bạn chia sẻ giúp mình nhé."
//   Người dùng:  "mình không hiểu câu hỏi của bạn, hãy giải thích rõ hơn"
//
// Hai khiếm khuyết độc lập chồng lên nhau, và test này giữ cả hai:
//
// 1. NGUỒN: dòng «Dữ liệu / danh mục chính» kẹt [MỘT PHẦN] với "còn thiếu: chốt bộ cột chính thức" trong
//    khi người dùng đã chốt bộ cột đó bằng BẢNG CỘT từ lượt thứ ba. Lượt distill bản đồ không hề được
//    đưa bảng cột — SourceContextBuilder gắn nó cho lượt chat, còn RequirementCoverageService thì chỉ gửi
//    ExtractedText. Bằng chứng nằm ngay trong DB mà "giám khảo" không được nhìn.
// 2. TRIỆU CHỨNG: cổng readiness thay lời mời "Write Requirement" của BA bằng một câu dựng sẵn kết thúc
//    bằng "bạn chia sẻ giúp mình nhé" và gọi nhóm bằng NHÃN NỘI BỘ của bản đồ. Người dùng nghiệp vụ
//    không có cách nào trả lời câu đó, nên vòng lặp không tự thoát được.
//
// Nhãn nội bộ nay đã đi khỏi HẲN màn hình — câu chặn chỉ chở CÂU HỎI, không nhãn nhóm, không đếm số nhóm
// còn lại ("cứ hỏi thẳng luôn, không cần phải nói nhóm gì hết"). Vì vậy sổ "đã hỏi chỗ nào" của cổng cũng
// đổi khóa: nó dò chính CÂU HỎI sắp phát trong các lượt đã lưu, thay vì đọc nhãn trong cặp «…».
public class CoverageDeadQuestionLoopTests
{
    // Bảng cột đã chốt là câu trả lời của người dùng, chỉ khác là họ trả lời bằng cách tích từng dòng.
    // Distiller phải nhìn thấy nó, nếu không dòng bản đồ không bao giờ lên [RÕ] được.
    [Fact]
    public void ConfirmedColumnTable_IsRenderedForTheDistiller()
    {
        const string columnMap = """
            [
              { "column": "Global ID", "meaning": "Mã định danh nhân viên", "used": true },
              { "column": "Revision Number", "meaning": "Số phiên bản hệ cũ", "used": false }
            ]
            """;

        var block = SourceColumnMapBuilder.RenderConfirmedBlock("LearningPlanTemplate.xlsx", columnMap);

        Assert.NotNull(block);
        Assert.Contains("đã được NGƯỜI DÙNG CHỐT", block, StringComparison.Ordinal);
        Assert.Contains("Global ID", block, StringComparison.Ordinal);
        Assert.Contains("Revision Number", block, StringComparison.Ordinal);
    }

    // Câu hỏi dựng sẵn phải hỏi ĐÚNG phần "còn thiếu: …" — thứ duy nhất bước soạn tài liệu còn phải tự
    // đoán — chứ không đọc lại nhãn nhóm và cả tóm tắt máy.
    [Fact]
    public void PendingQuestion_AsksTheMissingPart_NotTheInternalGroupLabel()
    {
        var readiness = Ask("""
            - ★ Mục tiêu / bài toán: [RÕ] Lập kế hoạch lớp học. {nguồn: "lên kế hoạch các lớp học"}
            - Dữ liệu / danh mục chính: [MỘT PHẦN] Master List gồm 6 cột đã chốt; còn thiếu: ai quản lý danh mục khóa học của ứng dụng.
            """);

        Assert.False(readiness.Ready);

        // Hỏi đúng mẩu còn hụt…
        Assert.Contains("ai quản lý danh mục khóa học của ứng dụng", readiness.Message, StringComparison.Ordinal);
        // …và là một CÂU HỎI, không phải bản tin trạng thái.
        Assert.EndsWith("?", readiness.Message.Trim(), StringComparison.Ordinal);

        // Nhãn nhóm là sổ sách nội bộ ⇒ KHÔNG được lên màn hình…
        Assert.DoesNotContain("Dữ liệu / danh mục chính", readiness.Message, StringComparison.Ordinal);
        // …và cũng KHÔNG phát lại tóm tắt máy (đọc lên tưởng bị hỏi lại điều vừa trả lời); lượt này
        // không được kết thúc bằng một lời mời trống nghĩa như bản cũ.
        Assert.DoesNotContain("Master List gồm 6 cột đã chốt", readiness.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("bạn chia sẻ giúp mình nhé", readiness.Message, StringComparison.Ordinal);
    }

    // Dòng [CHƯA HỎI] không có phần "còn thiếu" nào để bám ⇒ mới được phép hỏi câu mở đầu của nhóm; kể cả
    // khi đó cũng phải kết thúc bằng dấu hỏi và hỏi theo góc nhìn công việc thật.
    [Fact]
    public void PendingQuestion_FallsBackToAnOpeningQuestion_WhenNothingWasAskedYet()
    {
        var readiness = Ask("""
            - ★ Mục tiêu / bài toán: [RÕ] Lập kế hoạch lớp học. {nguồn: "lên kế hoạch các lớp học"}
            - Thông báo / nhắc nhở: [CHƯA HỎI]
            """);

        Assert.False(readiness.Ready);
        Assert.Equal(CoverageGroupOpeners.Find("Thông báo / nhắc nhở"), readiness.Message);
        Assert.EndsWith("?", readiness.Message.Trim(), StringComparison.Ordinal);
    }

    // Ghi chú tái mở "(ghi nhận trước đó: …)" là ghi chép CŨ của hệ thống dành cho BA, không phải điều
    // cần hỏi — đọc nguyên khối vào câu hỏi là kể lại chính lời người dùng rồi bắt họ nghe lại. Cụm
    // ReopenNote đứng ngay trước nó cũng vậy, và còn tệ hơn: nó là TÍN HIỆU MÁY (mở phanh chống-hỏi-lại)
    // nên đọc lên là một lượt hỏi rỗng nghĩa, xưng "người dùng" ở ngôi thứ ba với chính người đang đọc.
    // Ca thật: dự án JD Library lượt 34 — "Anh/chị cho mình hỏi thêm: người dùng báo phần này chưa đúng
    // — cần hỏi lại và chốt lại — anh/chị cho mình xin thông tin này nhé?".
    [Fact]
    public void PendingQuestion_DropsTheReopenBookkeeping()
    {
        var readiness = Ask($"""
            - ★ Đối tượng người dùng & vai trò: [MỘT PHẦN] còn thiếu: {AskedQuestionHistory.ReopenNote} — cần hỏi lại và chốt lại. (ghi nhận trước đó: trưởng phòng duyệt đơn)
            """);

        Assert.False(readiness.Ready);
        Assert.DoesNotContain("ghi nhận trước đó", readiness.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(AskedQuestionHistory.ReopenNote, readiness.Message, StringComparison.OrdinalIgnoreCase);
        // Không còn mẩu nào để hỏi ⇒ rơi về câu mở đầu của nhóm: một câu hỏi rộng vẫn trả lời được, còn
        // cụm tín hiệu thì không.
        Assert.Contains(CoverageGroupOpeners.Find("Đối tượng người dùng & vai trò")!, readiness.Message, StringComparison.Ordinal);
        Assert.EndsWith("?", readiness.Message.Trim(), StringComparison.Ordinal);
    }

    // Distiller viết ĐÚNG theo prompt: cụm tín hiệu, rồi mẩu còn phải hỏi, rồi ghi chép cũ trong ngoặc.
    // Câu hỏi phải lấy mảnh GIỮA — đó là thứ duy nhất người dùng trả lời được.
    [Fact]
    public void PendingQuestion_AsksTheGapWrittenAfterTheReopenMarker()
    {
        var readiness = Ask($"""
            - ★ Chức năng & luồng nghiệp vụ chính: [MỘT PHẦN] còn thiếu: {AskedQuestionHistory.ReopenNote} — cần hỏi lại và chốt lại. MyJD có nằm trong phạm vi màn hình không. (ghi nhận trước đó: phạm vi không có MyJD)
            """);

        Assert.False(readiness.Ready);
        Assert.Contains("MyJD có nằm trong phạm vi màn hình không", readiness.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(AskedQuestionHistory.ReopenNote, readiness.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ghi nhận trước đó", readiness.Message, StringComparison.Ordinal);
    }

    // Lượt chặn của cổng không có chip nào (Evaluate không dựng phương án), nên nó PHẢI là câu mở — nếu
    // không, người dùng nhận một câu hỏi vừa không có nút bấm vừa không mời gõ.
    [Theory]
    [InlineData("- ★ Mục tiêu / bài toán: [MỘT PHẦN] còn thiếu: ứng dụng giải quyết việc gì.")]
    [InlineData("- Thông báo / nhắc nhở: [CHƯA HỎI]")]
    [InlineData(null)]
    public void PendingTurn_IsAlwaysAnOpenQuestion(string? map)
    {
        var readiness = Ask(map);

        Assert.False(readiness.Ready);
        Assert.True(readiness.OpenEnded);
    }

    // Bản đồ chưa có/hỏng ⇒ fail-closed, và câu chặn vẫn phải nói được cho người dùng biết làm gì tiếp.
    [Fact]
    public void EmptyMap_StaysFailClosed()
    {
        var readiness = Ask(null);

        Assert.False(readiness.Ready);
        Assert.False(string.IsNullOrWhiteSpace(readiness.Message));
    }

    // ==== NHÁNH DỰ PHÒNG: không nhóm nào được rơi vào một lượt trống nghĩa ====
    //
    // Ca thật thứ hai của cùng lớp lỗi — dự án JD Library, lượt 76. Người dùng vừa trả lời xong người nhận
    // của một sự kiện thông báo; BA mời bấm "Write Requirement" quá sớm; cổng thay lời mời bằng câu dựng
    // sẵn, và vì dòng «Thông báo / nhắc nhở» lúc đó không có cụm "còn thiếu:" nào, câu phát ra là
    // *"…(nhóm «Thông báo / nhắc nhở»). Anh/chị kể giúp mình phần này trong công việc thực tế hiện đang diễn
    // ra thế nào?"*. Người dùng đáp *"mình chưa hiểu câu hỏi, hãy hỏi rõ hơn"*.
    //
    // Nhánh đó reachable với BẤT KỲ nhóm nào: cụm "còn thiếu:" là định dạng do LLM xuất, không phải bất
    // biến của code.

    // Dòng [CHƯA HỎI] ⇒ câu mở đầu THẬT của nhóm, không phải câu dùng chung cho cả 12 nhóm.
    [Fact]
    public void PendingQuestion_AsksTheGroupsOwnOpeningQuestion_WhenNothingWasAskedYet()
    {
        var readiness = Ask("""
            - ★ Mục tiêu / bài toán: [RÕ] Lập kế hoạch lớp học. {nguồn: "lên kế hoạch các lớp học"}
            - Quy mô sử dụng: [CHƯA HỎI]
            """);

        Assert.Contains(CoverageGroupOpeners.Find("Quy mô sử dụng")!, readiness.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("phần này", readiness.Message, StringComparison.OrdinalIgnoreCase);
    }

    // Dòng [MỘT PHẦN] mà distiller không viết được mẩu còn hụt ⇒ PHÁT LẠI phần đã ghi nhận rồi hỏi còn
    // thiếu gì. KHÔNG được phát lại câu mở đầu của nhóm ở ca này: người dùng đã kể phần đó rồi, nghe lại
    // đúng câu cũ là mất lòng tin vào cả buổi phỏng vấn (prompt chat cấm tuyệt đối).
    [Fact]
    public void PendingQuestion_PlaysBackWhatWasRecorded_WhenTheDistillerWroteNoGap()
    {
        var readiness = Ask("""
            - Thông báo / nhắc nhở: [MỘT PHẦN] Đã chốt To HOD của đơn vị, CC người tạo khi JD chờ HRBP verify.
            """);

        Assert.False(readiness.Ready);
        Assert.Contains("Đã chốt To HOD của đơn vị, CC người tạo khi JD chờ HRBP verify", readiness.Message, StringComparison.Ordinal);
        Assert.Contains("còn chỗ nào chưa đúng hoặc còn thiếu", readiness.Message, StringComparison.Ordinal);
        Assert.EndsWith("?", readiness.Message.Trim(), StringComparison.Ordinal);
        Assert.DoesNotContain(CoverageGroupOpeners.Find("Thông báo / nhắc nhở")!, readiness.Message, StringComparison.Ordinal);
    }

    // Phần phát lại KHÔNG bị cắt giữa chừng. Nhánh này hỏi đúng một câu — "còn chỗ nào chưa đúng hoặc còn
    // thiếu?" — nên phần ghi nhận là thứ DUY NHẤT người dùng có để rà: cắt nó đi là tự vô hiệu câu hỏi.
    //
    // Ca thật đã lên màn hình (dự án khóa học bắt buộc): dòng «Mục tiêu / bài toán» dài 204 ký tự, trần cũ
    // 200 cắt đúng giữa cụm cuối và người dùng đọc được một câu kết bằng "…. Phần này còn chỗ nào chưa
    // đúng…?" — không biết chỗ bị nuốt có sai hay có thiếu gì để mà bổ sung.
    [Fact]
    public void PlaybackReadsBackEverythingRecorded_WhenTheLineIsLongerThanTheOldCap()
    {
        var known = "Quản lý khóa học bắt buộc, theo dõi hạn hiệu lực, gửi email nhắc nhở khi sắp hết hạn. "
            + "Nhân viên xem khóa học bắt buộc và lịch sử học; Manager xem tiến độ học của nhân viên; "
            + "Admin quản lý danh sách vai trò và gán khóa bắt buộc cho từng vai";
        Assert.True(known.Length > 200, "fixture phải vượt trần CŨ, nếu không test này không kiểm gì cả");

        var readiness = Ask($"- ★ Mục tiêu / bài toán: [MỘT PHẦN] {known}");

        Assert.Contains(known, readiness.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("…", readiness.Message, StringComparison.Ordinal);
    }

    // Trần AN TOÀN vẫn còn (một dòng bản đồ hỏng không được đổ nguyên biên bản vào bong bóng chat), nhưng
    // nó cắt ở RANH GIỚI CÂU: phần đọc được luôn là những câu TRỌN VẸN, không phải một câu cụt.
    [Fact]
    public void PlaybackCutsOnlyAtASentenceBoundary_WhenTheLineIsAbsurdlyLong()
    {
        var sentence = "Người dùng kể một ý dài chừng năm mươi ký tự ở đây. ";
        var known = string.Concat(Enumerable.Repeat(sentence, 40)) + "Câu cuối cùng bị bỏ lại.";

        var readiness = Ask($"- ★ Mục tiêu / bài toán: [MỘT PHẦN] {known}");

        var playback = readiness.Message["Mình đang ghi nhận: ".Length..];
        playback = playback[..playback.IndexOf(". Phần này", StringComparison.Ordinal)];

        Assert.True(playback.Length < known.Length, "dòng vượt trần an toàn thì phải bị cắt");
        Assert.EndsWith("ký tự ở đây", playback, StringComparison.Ordinal);
        Assert.DoesNotContain("Câu cuối cùng bị bỏ lại", readiness.Message, StringComparison.Ordinal);
    }

    // Phần phát lại phải sạch ghi chú MÁY: cụm ReopenNote và "(ghi nhận trước đó: …)" là ghi chép của hệ
    // thống dành cho BA, đọc lên là xưng "người dùng" ở ngôi thứ ba với chính người đang đọc.
    [Fact]
    public void PlaybackDropsTheMachineBookkeeping()
    {
        var readiness = Ask($"""
            - Vòng đời & trạng thái: [MỘT PHẦN] Đơn đi qua Chờ duyệt và Đã duyệt. {AskedQuestionHistory.ReopenNote} (ghi nhận trước đó: chỉ có hai trạng thái)
            """);

        Assert.Contains("Đơn đi qua Chờ duyệt và Đã duyệt", readiness.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("ghi nhận trước đó", readiness.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(AskedQuestionHistory.ReopenNote, readiness.Message, StringComparison.OrdinalIgnoreCase);
    }

    // Không nhóm nào của bản đồ THẬT còn phát ra câu dùng chung — cả ở [CHƯA HỎI] lẫn [MỘT PHẦN] rỗng ruột
    // (dòng chỉ còn ghi chú máy, đã bị lược sạch trước khi phát lại).
    [Theory]
    [InlineData("[CHƯA HỎI]")]
    [InlineData("[MỘT PHẦN]")]
    public void NoRealGroupFallsBackToTheSharedSentence(string status)
    {
        foreach (var group in CoverageChecklist.Parse(CoveragePromptFixture.Read()))
        {
            var readiness = Ask($"- {group.Label}: {status}");

            Assert.False(readiness.Ready);
            Assert.Contains(CoverageGroupOpeners.Find(group.Label)!, readiness.Message, StringComparison.Ordinal);
            Assert.DoesNotContain("phần này", readiness.Message, StringComparison.OrdinalIgnoreCase);
        }
    }

    // Nhãn model tự nghĩ ra: không có câu mở đầu nào, và cổng KHÔNG được bịa một câu hỏi khai thác về một
    // nhóm không có trong checklist. Nhãn vẫn được đọc vào câu như một cụm CHỦ ĐỀ bình thường — đó là ngôn
    // ngữ tự nhiên, khác hẳn cái ngoặc sổ sách "(nhóm «…»)" của bản trước.
    [Fact]
    public void AnUnknownGroupLabelStillGetsAnAnswerableTurn()
    {
        var readiness = Ask("- Tích hợp hệ thống ngoài: [CHƯA HỎI]");

        Assert.False(readiness.Ready);
        Assert.Contains("tích hợp hệ thống ngoài", readiness.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("nhóm", readiness.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("«", readiness.Message, StringComparison.Ordinal);
        Assert.EndsWith("?", readiness.Message.Trim(), StringComparison.Ordinal);
    }

    // ==== SỔ RIÊNG CỦA CỔNG: không phát lại đúng câu vừa phát ====
    //
    // Đây là nửa còn lại của vòng lặp câu hỏi chết ở đầu file — ba lượt liên tiếp GIỐNG HỆT nhau. Nguồn của
    // nó là một lỗ trong cơ chế: AskedQuestionHistory.Collect chỉ nhận một lượt assistant là "câu hỏi" khi
    // lượt đó có GỢI Ý, mà lượt chặn của cổng cố tình không có chip nào — nên câu của cổng vô hình với đúng
    // cái phanh dựng ra để chặn hỏi lại. Cổng vì thế giữ sổ riêng, dò bằng chính CÂU HỎI nó dựng ra.

    private const string TwoPendingGroups = """
        - ★ Chức năng & luồng nghiệp vụ chính: [CHƯA HỎI]
        - Quy mô sử dụng: [CHƯA HỎI]
        """;

    // Cổng đọc HAI cột: bản đồ (trạng thái) và danh sách câu hỏi. Một dòng bullet của fixture chở cả hai,
    // nên helper này dựng đủ cặp — truyền mỗi bản đồ là dựng ra một trạng thái không có thật.
    private static RequirementReadiness Ask(
        string? bullets, IEnumerable<AgentConversation>? turns = null, string? relatedTo = null)
        => RequirementReadinessGate.Evaluate(
            bullets == null ? null : CoverageMapFixture.Map(bullets),
            bullets == null ? Array.Empty<OpenQuestionEntry>() : CoverageMapFixture.Questions(bullets),
            turns,
            relatedTo);

    private static AgentConversation BaTurn(string message) => new() { Role = "assistant", Message = message };

    // Không có hội thoại ⇒ thứ tự cũ: ★ cốt lõi trước.
    [Fact]
    public void WithoutHistory_TheCoreGroupGoesFirst()
    {
        var readiness = Ask(TwoPendingGroups);

        Assert.Equal(CoverageGroupOpeners.Find("Chức năng & luồng nghiệp vụ chính"), readiness.Message);
    }

    // Đã hỏi nhóm cốt lõi mà bản đồ không nhúc nhích ⇒ chuyển sang nhóm CHƯA hỏi, kể cả khi nó không phải ★.
    // Cờ "đã hỏi" thắng cả cờ cốt lõi: phát lại đúng câu người dùng vừa không trả lời được thì lượt sau cũng
    // không trả lời được, còn đổi nhóm thì còn cơ hội gỡ — mà nhóm cũ không mất đi đâu.
    [Fact]
    public void AfterAskingTheCoreGroup_ItMovesOnToTheOneNotAskedYet()
    {
        var first = Ask(TwoPendingGroups);

        var second = Ask(TwoPendingGroups, new[] { BaTurn(first.Message) });

        Assert.Equal(CoverageGroupOpeners.Find("Quy mô sử dụng"), second.Message);
        Assert.NotEqual(first.Message, second.Message);
    }

    // Hỏi hết một vòng ⇒ quay lại nhóm bị hỏi LÂU NHẤT, và phải NÓI RA rằng mình đang quay lại. Phát lại y
    // nguyên câu dẫn cũ đọc lên như thể hệ thống không nhớ mình vừa hỏi gì.
    [Fact]
    public void AfterAFullRound_ItComesBackToTheOldestAskAndSaysSo()
    {
        var first = Ask(TwoPendingGroups);
        var second = Ask(TwoPendingGroups, new[] { BaTurn(first.Message) });

        var third = Ask(TwoPendingGroups, new[] { BaTurn(first.Message), BaTurn(second.Message) });

        Assert.Contains(CoverageGroupOpeners.Find("Chức năng & luồng nghiệp vụ chính")!, third.Message, StringComparison.Ordinal);
        Assert.StartsWith("Mình quay lại", third.Message, StringComparison.Ordinal);
        Assert.NotEqual(first.Message, third.Message);
    }

    // Chỉ còn MỘT nhóm thiếu thì không có nhóm nào để đổi sang — lượt sau vẫn phải khác lượt trước, nếu
    // không người dùng nhận đúng hai tin nhắn giống nhau và thôi trả lời.
    [Fact]
    public void WithASinglePendingGroup_TheSecondAskIsStillWordedDifferently()
    {
        const string oneGroup = "- Quy mô sử dụng: [CHƯA HỎI]";
        var first = Ask(oneGroup);

        var second = Ask(oneGroup, new[] { BaTurn(first.Message) });

        Assert.NotEqual(first.Message, second.Message);
        Assert.Contains(CoverageGroupOpeners.Find("Quy mô sử dụng")!, second.Message, StringComparison.Ordinal);
        Assert.EndsWith("?", second.Message.Trim(), StringComparison.Ordinal);
    }

    // ==== LIỀN MẠCH: giữa các chỗ ĐỀU chưa hỏi, chọn chỗ gần chủ đề vừa bị chặn ====
    //
    // Ca thật (dự án quản lý khóa học bắt buộc — 2026-09-03). BA đang hỏi dở về các vai trò thì lượt hỏi vai
    // Nhân viên bị phanh chống-hỏi-lại chặn; cổng nhận việc và — theo thứ tự cũ, ★ cốt lõi trước — phát ngay
    // câu xin ví dụ tính thử của nhóm «Quy tắc nghiệp vụ». Người dùng đang kể vai trò thì bị hỏi sang một
    // chủ đề xa nhất có thể, còn vai Nhân viên thì không ai hỏi nữa.
    private const string RolesAndRulesPending = """
        - ★ Quy tắc nghiệp vụ & ràng buộc: [MỘT PHẦN] Thời hạn hiệu lực 12 tháng, nhắc trước 30 ngày. còn thiếu: với quy tắc có con số ở trên, anh/chị cho mình một ví dụ cụ thể tính ra kết quả thế nào?
        - Đối tượng người dùng & vai trò: [MỘT PHẦN] Có Admin và Quản lý trực tiếp. còn thiếu: vai Nhân viên xem được những gì trong ứng dụng
        """;

    // Câu BA vừa bị chặn (nguyên văn lượt thật).
    private const string BlockedRoleQuestion =
        "Anh/chị cho mình biết: Nhân viên sẽ dùng ứng dụng để làm những việc gì? Ví dụ: xem khóa học bắt "
        + "buộc của mình, xem lịch sử học, hay còn thao tác nào khác?";

    [Fact]
    public void WithABlockedQuestion_TheGateStaysOnTheTopicBeingDiscussed()
    {
        var readiness = Ask(RolesAndRulesPending, turns: null, relatedTo: BlockedRoleQuestion);

        Assert.Contains("vai Nhân viên xem được những gì", readiness.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("ví dụ cụ thể tính ra kết quả", readiness.Message, StringComparison.Ordinal);
    }

    // Không có câu bị chặn ⇒ thước đo trả 0 cho mọi dòng và cờ ★ lại là thứ phân định, y như trước.
    [Fact]
    public void WithoutABlockedQuestion_TheCoreGroupStillGoesFirst()
    {
        var readiness = Ask(RolesAndRulesPending);

        Assert.Contains("ví dụ cụ thể tính ra kết quả", readiness.Message, StringComparison.Ordinal);
    }

    // …nhưng độ gần chủ đề KHÔNG được nới luật xoay vòng: nó chỉ phá thế hoà trong cùng một bậc "đã hỏi".
    // Hỏi rồi mà bản đồ không nhúc nhích thì vẫn phải đổi chỗ hỏi — nếu không, đúng cái vòng lặp câu hỏi
    // chết mà cả file này sinh ra để cắt sẽ quay lại, chỉ khác là lần này do độ tương đồng giữ nó ở đó.
    [Fact]
    public void TopicContinuityNeverOverridesTheRotation()
    {
        var first = Ask(RolesAndRulesPending, turns: null, relatedTo: BlockedRoleQuestion);

        var second = Ask(RolesAndRulesPending, new[] { BaTurn(first.Message) }, BlockedRoleQuestion);

        Assert.Contains("ví dụ cụ thể tính ra kết quả", second.Message, StringComparison.Ordinal);
        Assert.NotEqual(first.Message, second.Message);
    }

    // Nhận diện lượt chặn là giao ước code↔code mà compiler không kiểm được: CẢ HAI biến thể của lượt chặn
    // phải đọc ra được. Lượt "quay lại" chỉ thêm một câu dẫn ở ĐẦU, vế hỏi phía sau giữ nguyên — sổ dò trên
    // vế hỏi nên phải thấy cả hai. Thêm câu dẫn vào GIỮA hay đổi vế hỏi ở nhánh "quay lại" là cổng mất sổ và
    // lặng lẽ quay về phát lại một câu ba lượt liền; không test nào khác bắt được.
    [Fact]
    public void LastAskedAt_ReadsBothWordingsOfTheGateTurn()
    {
        const string oneGroup = "- Quy mô sử dụng: [CHƯA HỎI]";
        var firstAsk = Ask(oneGroup).Message;
        var comingBack = Ask(oneGroup, new[] { BaTurn(firstAsk) }).Message;

        Assert.StartsWith("Mình quay lại", comingBack, StringComparison.Ordinal);
        Assert.Equal(0, RequirementReadinessGate.LastAskedAt(new[] { BaTurn(firstAsk) }, firstAsk));
        Assert.Equal(1, RequirementReadinessGate.LastAskedAt(
            new[] { BaTurn("Cảm ơn anh/chị."), BaTurn(comingBack) }, firstAsk));
    }

    // Sổ chỉ đếm lượt của BA. Lượt của người dùng (họ dán lại câu hỏi để hỏi ngược), lượt BA nói chuyện
    // khác và lượt ⚠️ báo lỗi gọi AI không phải câu chặn — tính chúng vào là cổng bỏ qua một chỗ chưa ai hỏi.
    [Fact]
    public void LastAskedAt_IgnoresEverythingThatIsNotAnAssistantTurnAskingIt()
    {
        var question = Ask(TwoPendingGroups).Message;

        Assert.Equal(-1, RequirementReadinessGate.LastAskedAt(new[]
        {
            new AgentConversation { Role = "user", Message = question },
            BaTurn("Anh/chị cho mình hỏi thêm: ai chịu trách nhiệm cập nhật danh sách khóa học?"),
            BaTurn("⚠️ Lời gọi AI thất bại, lượt trả lời bị gián đoạn."),
        }, question));
    }

    // ==== KHÔNG NÓI NHÓM: lượt chặn chỉ chở CÂU HỎI ====
    //
    // Bản trước mở đầu bằng *"Trước khi viết tài liệu, mình còn một chỗ chưa đủ thông tin để khỏi phải tự
    // đoán (nhóm «Đối tượng người dùng & vai trò», còn 3 nhóm — mình hỏi từng nhóm một)"* rồi mới tới câu
    // hỏi thật. Cả cụm đó là sổ sách của hệ thống đọc ra màn hình: nhãn nhóm là từ vựng của bản đồ mà người
    // dùng nghiệp vụ chưa từng thấy, còn "còn 3 nhóm" chỉ báo cho họ biết còn phải chịu bao nhiêu lượt nữa.
    // Yêu cầu của người dùng repo: "BA có câu hỏi nào thì cứ hỏi thẳng luôn, không cần phải nói nhóm gì hết".
    [Theory]
    [InlineData("[CHƯA HỎI]")]
    [InlineData("[MỘT PHẦN] còn thiếu: ai chịu trách nhiệm cập nhật danh sách")]
    [InlineData("[MỘT PHẦN] Đã chốt duyệt hai cấp")]
    public void ThePendingTurnNeverNamesOrCountsCoverageGroups(string status)
    {
        foreach (var group in CoverageChecklist.Parse(CoveragePromptFixture.Read()))
        {
            var map = CoverageMapFixture.Map($"- {group.Label}: {status}\n- Quy mô sử dụng: [CHƯA HỎI]");
            var message = Ask(map).Message;

            Assert.DoesNotContain("nhóm", message, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("«", message, StringComparison.Ordinal);
            Assert.DoesNotContain(group.Label, message, StringComparison.OrdinalIgnoreCase);
        }
    }
}
