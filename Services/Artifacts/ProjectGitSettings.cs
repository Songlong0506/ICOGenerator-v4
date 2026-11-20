using System.Text.RegularExpressions;
using ICOGenerator.Domain;

namespace ICOGenerator.Services.Artifacts;

/// <summary>
/// Cấu hình Git CỦA MỘT DỰ ÁN cho bước bàn giao: nhánh đích của PR và tài khoản Git (username + token)
/// dùng để clone/push repo đích. Người vận hành điền ở popup "Config delivery" của Agent Dashboard, cùng
/// chỗ với Backend/Frontend Git URL — không còn section <c>PullRequest</c> nào trong appsettings.
/// <para>
/// Vì sao theo dự án chứ không toàn cục: mỗi dự án đẩy lên repo riêng, có thể ở máy chủ Git khác nhau
/// (GitHub, Bitbucket Bosch…) với nhánh chính khác nhau (<c>main</c>/<c>master</c>/<c>develop</c>) và
/// tài khoản khác nhau. Một giá trị trong appsettings chỉ đúng cho đúng một trong số đó.
/// </para>
/// <para>
/// TOKEN KHÔNG BAO GIỜ ĐI VÀO ĐỐI SỐ LỆNH GIT — đối số của lệnh đi vào log gọi tool, và một token nằm
/// trong log là một token đã lộ. Nó tới git qua BIẾN MÔI TRƯỜNG của tiến trình con
/// (<see cref="CredentialEnvironment"/>): một credential helper khai báo bằng <c>GIT_CONFIG_*</c> đọc
/// lại username/token từ biến môi trường lúc git cần. Đòi git ≥ 2.31 (03/2021); git cũ hơn bỏ qua các
/// biến này và lùi về credential sẵn có của máy chủ.
/// </para>
/// </summary>
public sealed record ProjectGitSettings(string BaseBranch, string? Username, string? AccessToken)
{
    /// <summary>
    /// Tên remote của mọi repo đích. Cố định vì chính <see cref="ProjectRepositorySeeder"/> gắn remote
    /// này vào repo — không có ai khác đặt tên remote để mà phải cấu hình.
    /// </summary>
    public const string RemoteName = "origin";

    public const string DefaultBaseBranch = "main";

    // Tên biến môi trường chở thông tin đăng nhập tới credential helper. Không phải bí mật cần giấu —
    // giá trị của chúng mới là bí mật, và giá trị chỉ sống trong môi trường của đúng một tiến trình git.
    public const string UsernameVariable = "ICOGEN_GIT_USERNAME";
    public const string TokenVariable = "ICOGEN_GIT_TOKEN";

    // Username mặc định khi chỉ nhập token: GitHub chấp nhận một username bất kỳ đi kèm PAT. Bitbucket
    // Server (Bosch) thì KHÔNG — ở đó phải nhập đúng username của tài khoản.
    public const string FallbackUsername = "x-access-token";

    // Helper chạy qua sh (Git for Windows có sẵn sh), chỉ trả lời thao tác "get". Đối số "$1" do git
    // truyền (get/store/erase); "store"/"erase" bị bỏ qua nên không có gì được ghi xuống đĩa.
    internal const string CredentialHelper =
        "!f() { test \"$1\" = get || return 0; echo \"username=${" + UsernameVariable + "}\"; echo \"password=${" + TokenVariable + "}\"; }; f";

    /// <summary>Không có cấu hình nào: nhánh đích <c>main</c>, dùng credential sẵn có của máy chủ.</summary>
    public static ProjectGitSettings Default { get; } = new(DefaultBaseBranch, null, null);

    // Cùng luật với GitTools.SafeRef: tên nhánh thành đối số vị trí của git (`git init -b <nhánh>`,
    // `git checkout <nhánh>`), mà git hiểu đối số mở đầu bằng '-' là TUỲ CHỌN.
    private static readonly Regex SafeBranch = new(@"^[A-Za-z0-9][A-Za-z0-9._/-]*$", RegexOptions.Compiled);

    public static bool IsValidBranchName(string? value) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= 200 && !value.Contains("..") && SafeBranch.IsMatch(value);

    // Nhánh không hợp lệ (chỉ có thể là sửa thẳng DB — UpdateDeliveryConfigUseCase đã chặn ở form) lùi
    // về mặc định thay vì để nó tới dòng lệnh git.
    public static ProjectGitSettings From(Project project) => new(
        IsValidBranchName(project.GitBaseBranch?.Trim()) ? project.GitBaseBranch!.Trim() : DefaultBaseBranch,
        string.IsNullOrWhiteSpace(project.GitUsername) ? null : project.GitUsername.Trim(),
        string.IsNullOrWhiteSpace(project.GitAccessToken) ? null : project.GitAccessToken.Trim());

    public bool HasCredential => !string.IsNullOrWhiteSpace(AccessToken);

    /// <summary>
    /// Biến môi trường cần đặt cho tiến trình git để nó đăng nhập bằng tài khoản của dự án. Rỗng khi dự
    /// án không có token — git dùng credential sẵn có của máy chủ (SSH key, credential manager).
    /// <para>
    /// Mục đầu tiên đặt <c>credential.helper</c> về RỖNG để xoá danh sách helper của máy chủ: không thì
    /// một helper đã lưu tài khoản KHÁC (ví dụ Git Credential Manager của người cài máy) được hỏi trước
    /// và thắng.
    /// </para>
    /// </summary>
    public IReadOnlyDictionary<string, string> CredentialEnvironment()
    {
        if (!HasCredential)
            return new Dictionary<string, string>();

        return new Dictionary<string, string>
        {
            ["GIT_CONFIG_COUNT"] = "2",
            ["GIT_CONFIG_KEY_0"] = "credential.helper",
            ["GIT_CONFIG_VALUE_0"] = "",
            ["GIT_CONFIG_KEY_1"] = "credential.helper",
            ["GIT_CONFIG_VALUE_1"] = CredentialHelper,
            [UsernameVariable] = string.IsNullOrWhiteSpace(Username) ? FallbackUsername : Username,
            [TokenVariable] = AccessToken!
        };
    }
}
