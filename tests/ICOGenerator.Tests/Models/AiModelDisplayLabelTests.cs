using ICOGenerator.Domain;
using Xunit;

namespace ICOGenerator.Tests.Models;

// Nhãn "ModelId (endpoint)" ở các dropdown chọn model (Agents, Evals, thử model cho POC). Mục đích duy nhất là
// phân biệt hai model cùng ModelId khai báo trên hai endpoint — nên phần endpoint phải ngắn mà vẫn giữ đoạn
// phân biệt được (host, tên deployment), và bỏ phần lặp lại ở mọi model (scheme, ?api-version=…).
public class AiModelDisplayLabelTests
{
    [Theory]
    [InlineData("http://127.0.0.1:1234/v1", "127.0.0.1:1234/v1")]
    [InlineData("https://api.deepseek.com/", "api.deepseek.com")]
    [InlineData("https://aoai-farm.bosch-temp.com/api/google/v1/publishers/anthropic",
        "aoai-farm.bosch-temp.com/api/google/v1/publishers/anthropic")]
    [InlineData("https://aoai-farm.bosch-temp.com/api/openai/deployments/gpt-6-luna-2026-09-22?api-version=2025-04-01-preview",
        "aoai-farm.bosch-temp.com/…/gpt-6-luna-2026-09-22")]
    [InlineData("not a url", "not a url")]
    [InlineData("", "")]
    [InlineData(null, "")]
    public void ShortEndpoint_bo_scheme_query_va_rut_gon_khi_qua_dai(string? endpoint, string expected)
    {
        Assert.Equal(expected, AiModel.ShortEndpoint(endpoint));
    }

    [Fact]
    public void DisplayLabel_ghep_ModelId_voi_endpoint_rut_gon()
    {
        Assert.Equal("claude-haiku-5-5 (api.example.com/v1)",
            AiModel.DisplayLabel("claude-haiku-5-5", "https://api.example.com/v1"));
        Assert.Equal("claude-haiku-5-5", AiModel.DisplayLabel("claude-haiku-5-5", ""));
    }
}
