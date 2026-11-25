using Microsoft.AspNetCore.Mvc.Filters;

namespace ICOGenerator.Localization;

/// <summary>
/// Đổi ngôn ngữ của <see cref="Loc"/> cho TRỌN một request MVC — model binding, action, và cả lúc render
/// View/JSON. Không gắn ⇒ <see cref="Loc.DefaultLanguage"/> (tiếng Anh).
///
/// <para>
/// Hiện chỉ <c>RequirementsController</c> gắn nó (<c>[UiLanguage(Loc.SourceLanguage)]</c>): màn Requirements là
/// ngoại lệ duy nhất nói tiếng Việt. Đừng gắn nó cho màn mới — màn mới nói tiếng Anh.
/// </para>
///
/// <para>
/// <b>Vì sao là resource filter mà không phải action filter.</b> Action filter chỉ bọc action; View được
/// render SAU đó, ngoài phạm vi <see cref="Loc.Use"/>, nên chữ do View/enum vẽ sẽ quay về tiếng Anh. Resource
/// filter bọc cả result. Ngôn ngữ đi theo <see cref="AsyncLocal{T}"/>, nên task con mà request khởi động (luồng
/// SSE của chat BA) cũng mang theo.
/// </para>
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, Inherited = true)]
public sealed class UiLanguageAttribute : Attribute, IAsyncResourceFilter
{
    public UiLanguageAttribute(string language) => Language = language;

    public string Language { get; }

    public async Task OnResourceExecutionAsync(ResourceExecutingContext context, ResourceExecutionDelegate next)
    {
        using (Loc.Use(Language))
            await next();
    }
}
