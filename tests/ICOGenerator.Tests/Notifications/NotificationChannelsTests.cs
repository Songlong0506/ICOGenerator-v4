using System.Net;
using System.Text.Json;
using ICOGenerator.Domain.Enums;
using ICOGenerator.Services.Notifications;
using ICOGenerator.Services.Notifications.Channels;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ICOGenerator.Tests.Notifications;

// Kênh ngoài (Teams/email): logic bật/tắt opt-in, dựng payload đúng, và POST tới webhook cấu hình sẵn.
public class NotificationChannelsTests
{
    private static NotificationMessage Msg(string? url = "https://app.example/AgentDashboard?projectId=1") =>
        new(NotificationType.GateAwaitingApproval, "Chờ duyệt", "Một bước đã xong.", "Cổng thanh toán", url);

    // ---------------- Teams ----------------

    [Fact]
    public void Teams_IsEnabled_RequiresFlagAndWebhookUrl()
    {
        var off = new TeamsNotificationChannel(new HttpClient(), new NotificationOptions(), NullLogger<TeamsNotificationChannel>.Instance);
        Assert.False(off.IsEnabled);

        var noUrl = new TeamsNotificationChannel(new HttpClient(), new NotificationOptions { Teams = { Enabled = true } }, NullLogger<TeamsNotificationChannel>.Instance);
        Assert.False(noUrl.IsEnabled);

        var on = new TeamsNotificationChannel(new HttpClient(), new NotificationOptions { Teams = { Enabled = true, WebhookUrl = "https://x/hook" } }, NullLogger<TeamsNotificationChannel>.Instance);
        Assert.True(on.IsEnabled);
    }

    [Fact]
    public void Teams_BuildMessageCard_HasTitleTextThemeAndAction()
    {
        var json = TeamsNotificationChannel.BuildMessageCard(Msg());
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.Equal("MessageCard", root.GetProperty("@type").GetString());
        Assert.Equal("Chờ duyệt", root.GetProperty("title").GetString());
        Assert.Equal("F58220", root.GetProperty("themeColor").GetString()); // cam = chờ duyệt
        Assert.Contains("Cổng thanh toán", root.GetProperty("text").GetString());

        var action = root.GetProperty("potentialAction")[0];
        Assert.Equal("OpenUri", action.GetProperty("@type").GetString());
        Assert.Equal("https://app.example/AgentDashboard?projectId=1",
            action.GetProperty("targets")[0].GetProperty("uri").GetString());
    }

    [Fact]
    public void Teams_BuildMessageCard_OmitsAction_WhenNoUrl()
    {
        var json = TeamsNotificationChannel.BuildMessageCard(Msg(url: null));
        using var doc = JsonDocument.Parse(json);
        Assert.False(doc.RootElement.TryGetProperty("potentialAction", out _));
    }

    [Theory]
    [InlineData(NotificationType.WorkflowCompleted, "00884A")]
    [InlineData(NotificationType.WorkflowFailed, "E20015")]
    public void Teams_ThemeColor_VariesByType(NotificationType type, string expected)
    {
        var json = TeamsNotificationChannel.BuildMessageCard(new NotificationMessage(type, "t", "m", null, null));
        using var doc = JsonDocument.Parse(json);
        Assert.Equal(expected, doc.RootElement.GetProperty("themeColor").GetString());
    }

    [Fact]
    public async Task Teams_SendAsync_PostsPayloadToWebhook()
    {
        var handler = new CapturingHandler(HttpStatusCode.OK);
        var options = new NotificationOptions { Teams = { Enabled = true, WebhookUrl = "https://teams.example/hook/abc" } };
        var channel = new TeamsNotificationChannel(new HttpClient(handler), options, NullLogger<TeamsNotificationChannel>.Instance);

        await channel.SendAsync(Msg());

        Assert.Equal("https://teams.example/hook/abc", handler.RequestUri?.ToString());
        Assert.Contains("MessageCard", handler.Body);
        Assert.Contains("Chờ duyệt", handler.Body);
    }

    [Fact]
    public async Task Teams_SendAsync_NoOp_WhenDisabled()
    {
        var handler = new CapturingHandler(HttpStatusCode.OK);
        var channel = new TeamsNotificationChannel(new HttpClient(handler), new NotificationOptions(), NullLogger<TeamsNotificationChannel>.Instance);

        await channel.SendAsync(Msg());

        Assert.False(handler.WasCalled);
    }

    [Fact]
    public async Task Teams_SendAsync_Swallows_HttpFailure()
    {
        var handler = new CapturingHandler(HttpStatusCode.InternalServerError);
        var options = new NotificationOptions { Teams = { Enabled = true, WebhookUrl = "https://teams.example/hook" } };
        var channel = new TeamsNotificationChannel(new HttpClient(handler), options, NullLogger<TeamsNotificationChannel>.Instance);

        // Không ném dù webhook trả 500 (fail-open).
        await channel.SendAsync(Msg());
        Assert.True(handler.WasCalled);
    }

    // ---------------- Email ----------------

    [Fact]
    public void Email_IsEnabled_RequiresFlagHostAndFrom()
    {
        // Người nhận có thể đến per-message (opt-in cá nhân) nên IsEnabled KHÔNG còn đòi To.
        Assert.False(new EmailNotificationChannel(new NotificationOptions(), NullLogger<EmailNotificationChannel>.Instance).IsEnabled);

        var missingFrom = new NotificationOptions { Email = { Enabled = true, Host = "smtp" } };
        Assert.False(new EmailNotificationChannel(missingFrom, NullLogger<EmailNotificationChannel>.Instance).IsEnabled);

        var ok = new NotificationOptions { Email = { Enabled = true, Host = "smtp", From = "a@b" } };
        Assert.True(new EmailNotificationChannel(ok, NullLogger<EmailNotificationChannel>.Instance).IsEnabled);
    }

    [Fact]
    public void Email_BuildMail_SetsFromRecipientsSubjectAndBody()
    {
        var email = new EmailChannelOptions { Enabled = true, Host = "smtp", From = "noreply@bosch.com", To = new[] { "a@bosch.com", "b@bosch.com" } };
        using var mail = EmailNotificationChannel.BuildMail(email, Msg("https://app/x"));

        Assert.Equal("noreply@bosch.com", mail.From!.Address);
        Assert.Equal(2, mail.To.Count);
        Assert.Contains("Chờ duyệt", mail.Subject);
        Assert.Contains("Cổng thanh toán", mail.Subject);
        Assert.Contains("Một bước đã xong.", mail.Body);
        Assert.Contains("https://app/x", mail.Body);
    }

    [Fact]
    public void Email_BuildMail_MergesConfigToWithPerUserRecipients_Deduped()
    {
        var email = new EmailChannelOptions { Enabled = true, Host = "smtp", From = "noreply@bosch.com", To = new[] { "team@bosch.com" } };
        var message = new NotificationMessage(NotificationType.WorkflowFailed, "T", "M", "P", null,
            new[] { "u1@bosch.com", "TEAM@bosch.com" }); // trùng To (khác hoa/thường) phải bị khử

        using var mail = EmailNotificationChannel.BuildMail(email, message);

        var addrs = mail.To.Select(a => a.Address).ToList();
        Assert.Equal(2, addrs.Count);
        Assert.Contains("team@bosch.com", addrs);
        Assert.Contains("u1@bosch.com", addrs);
    }

    // Bắt request cuối cùng để kiểm tra URL + payload, trả về status cấu hình sẵn.
    private sealed class CapturingHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _status;
        public CapturingHandler(HttpStatusCode status) => _status = status;

        public bool WasCalled { get; private set; }
        public Uri? RequestUri { get; private set; }
        public string Body { get; private set; } = string.Empty;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            WasCalled = true;
            RequestUri = request.RequestUri;
            Body = request.Content == null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(_status);
        }
    }
}
