using System.Globalization;
using System.Net;
using System.Text;

namespace ICOGenerator.Services.Artifacts;

/// <summary>
/// Single source of truth for the POC content region; both workspace seeding (AgentTaskWorker)
/// and in-place editing (WorkspaceTools.SetPocContent) go through here so the markers can't drift
/// apart — that drift was the original cause of the "poc-demo.html identical to template" bug.
/// </summary>
public static class PocTemplate
{
    public const string MockupRelativePath = "03_Implementation/poc-demo.html";

    /// <summary>Must match the literal line in Prompts/Design/poc-template.html.</summary>
    public const string StartMarker = "<!-- POC_CONTENT_START : replace everything below with the feature UI -->";
    public const string EndMarker = "<!-- POC_CONTENT_END -->";

    public const string Placeholder = "<!-- POC_CONTENT -->";

    /// <summary>
    /// Builds the poc-demo.html body by collapsing everything between the markers into a single
    /// placeholder line. Returns null when the markers are missing/malformed (caller falls back to a raw copy).
    /// </summary>
    public static string? SeedFromTemplate(string template)
    {
        if (!TryLocateRegion(template, out var afterStart, out var endIdx))
            return null;

        return template[..afterStart]
            + "\n                    " + Placeholder + "\n                    "
            + template[endIdx..];
    }

    /// <summary>
    /// Replaces everything between the markers (exclusive) with <paramref name="newContent"/>,
    /// keeping the markers intact. Returns null when the markers are missing/malformed.
    /// </summary>
    public static string? ReplaceContent(string current, string newContent)
    {
        if (!TryLocateRegion(current, out var afterStart, out var endIdx))
            return null;

        return current[..afterStart]
            + "\n" + newContent.Trim('\n') + "\n                    "
            + current[endIdx..];
    }

    private static bool TryLocateRegion(string content, out int afterStart, out int endIdx)
    {
        afterStart = 0;
        var startIdx = content.IndexOf(StartMarker, StringComparison.Ordinal);
        endIdx = content.IndexOf(EndMarker, StringComparison.Ordinal);

        if (startIdx < 0 || endIdx <= startIdx)
            return false;

        afterStart = startIdx + StartMarker.Length;
        return true;
    }

    // Shell customization (App Name, browser title, breadcrumb, left nav) — these live OUTSIDE the
    // POC_CONTENT markers, the parts the dev agent never touched (why POCs kept showing "App Name"
    // and the template menu). Anchors are literal markup from poc-template.html; a missing anchor or
    // empty input leaves the document untouched, so a partial template can't throw or wipe the shell.

    private const string AppNameOpen = "<span class=\"app-name\">";
    private const string TitleOpen = "<title>";
    private const string BreadcrumbOpen = "<div class=\"breadcrumb\">";
    private const string NavOpen = "<nav class=\"sidebar-nav\">";
    private const string NavClose = "</nav>";

    /// <summary>Sets the sidebar header name and the browser tab title.</summary>
    public static string ReplaceAppName(string current, string appName)
    {
        var name = WebUtility.HtmlEncode((appName ?? string.Empty).Trim());
        if (name.Length == 0)
            return current;

        current = ReplaceInner(current, AppNameOpen, "</span>", name);
        current = ReplaceInner(current, TitleOpen, "</title>", name);
        return current;
    }

    public static string ReplaceBreadcrumb(string current, string breadcrumb)
    {
        var text = WebUtility.HtmlEncode((breadcrumb ?? string.Empty).Trim());
        return text.Length == 0 ? current : ReplaceInner(current, BreadcrumbOpen, "</div>", text);
    }

    /// <summary>
    /// Rebuilds the left sidebar menu from <paramref name="items"/> using the template's nav classes;
    /// the first entry is active and the first group expanded. Returns input unchanged when there's
    /// nothing renderable or the nav element is missing.
    /// </summary>
    public static string ReplaceNav(string current, IReadOnlyList<PocNavItem> items)
    {
        var rendered = RenderNav(items);
        if (rendered.Length == 0)
            return current;

        var startIdx = current.IndexOf(NavOpen, StringComparison.Ordinal);
        if (startIdx < 0)
            return current;

        var innerStart = startIdx + NavOpen.Length;
        var closeIdx = current.IndexOf(NavClose, innerStart, StringComparison.Ordinal);
        if (closeIdx < 0)
            return current;

        return current[..innerStart] + "\n" + rendered + "                " + current[closeIdx..];
    }

    // Replaces the text between the first `open` tag and the next `close` after it.
    private static string ReplaceInner(string content, string open, string close, string newInner)
    {
        var openIdx = content.IndexOf(open, StringComparison.Ordinal);
        if (openIdx < 0)
            return content;

        var innerStart = openIdx + open.Length;
        var closeIdx = content.IndexOf(close, innerStart, StringComparison.Ordinal);
        if (closeIdx < 0)
            return content;

        return content[..innerStart] + newInner + content[closeIdx..];
    }

    // Sidebar icons come from Bootstrap Icons, which the shell loads once via a <link> in <head>, so
    // the menu can use any of the ~2000 icons without us hand-defining SVGs. Each item renders an
    // <i class="bi bi-NAME">: the agent may name the icon per navItem (PocNavItem.Icon); when it
    // doesn't, we infer one from the label by keyword, falling back to a neutral dot. The chevron that
    // marks an expandable group stays an inline SVG (its rotate animation is tied to .nav-chevron).
    private const string Chevron = "<svg class=\"ico nav-chevron\" viewBox=\"0 0 24 24\"><path d=\"M6 9l6 6 6-6\" /></svg>";

    private const string DefaultIcon = "circle";

    // Keyword → Bootstrap Icons name, matched on the normalized label via Contains, so order from
    // specific to generic: e.g. "Quản lý sản phẩm" hits "san pham" (box-seam) before "quan ly" (list).
    // This is only a fallback for items whose icon the agent didn't specify.
    private static readonly (string[] Keywords, string Icon)[] IconRules =
    {
        (new[] { "dang nhap", "login", "sign in", "log in", "signin" }, "box-arrow-in-right"),
        (new[] { "dang ky", "dang ki", "register", "sign up", "signup", "tao tai khoan" }, "person-plus"),
        (new[] { "dang xuat", "logout", "log out", "sign out", "thoat" }, "box-arrow-right"),
        (new[] { "trang chu", "trang chinh", "home page", "home", "main" }, "house"),
        (new[] { "gio hang", "cart", "basket" }, "cart3"),
        (new[] { "thanh toan", "payment", "checkout", "credit card" }, "credit-card"),
        (new[] { "chi tiet", "detail", "thong tin", "mo ta", "gioi thieu", "about" }, "file-earmark-text"),
        (new[] { "don hang", "don dat", "order", "purchase", "mua hang" }, "bag"),
        (new[] { "hoa don", "invoice", "bill", "receipt" }, "receipt"),
        (new[] { "san pham", "product", "hang hoa", "mat hang", "ton kho", "inventory", "kho hang" }, "box-seam"),
        (new[] { "danh muc", "category", "categories", "the loai", "phan loai", "thuong hieu", "nhan hieu", "brand" }, "tags"),
        (new[] { "khuyen mai", "giam gia", "voucher", "coupon", "ma giam", "discount", "promotion", "uu dai" }, "percent"),
        (new[] { "doanh thu", "doanh so", "revenue", "tai chinh", "finance", "thu chi", "cong no", "gia ban" }, "cash-coin"),
        (new[] { "bao cao", "report", "thong ke", "statistic", "analytic", "bieu do", "chart", "phan tich" }, "bar-chart"),
        (new[] { "nguoi dung", "tai khoan", "khach hang", "thanh vien", "user", "account", "customer", "member", "nhan vien", "staff", "employee" }, "people"),
        (new[] { "ho so", "profile", "ca nhan", "my account" }, "person"),
        (new[] { "dashboard", "tong quan", "overview", "bang dieu khien", "tong hop" }, "speedometer2"),
        (new[] { "quan tri", "admin", "he thong", "system", "phan quyen", "vai tro", "role", "permission", "bao mat", "security" }, "shield-lock"),
        (new[] { "cai dat", "setting", "cau hinh", "config", "tuy chon", "preference", "thiet lap" }, "gear"),
        (new[] { "tim kiem", "search", "tra cuu", "loc ", "filter" }, "search"),
        (new[] { "lich su", "history", "nhat ky", "hoat dong", "activity", "audit" }, "clock-history"),
        (new[] { "lich", "calendar", "schedule", "dat lich", "appointment", "booking" }, "calendar3"),
        (new[] { "thong bao", "notification", "bell", "alert", "canh bao" }, "bell"),
        (new[] { "tin nhan", "message", "chat", "lien he", "contact", "ho tro", "support", "phan hoi", "feedback", "binh luan", "comment", "danh gia", "review" }, "chat-dots"),
        (new[] { "yeu thich", "wishlist", "favorite", "favourite", "da luu", "saved", "bookmark" }, "heart"),
        (new[] { "danh sach", "list", "quan ly", "manage", "management" }, "list-ul"),
    };

    private static string RenderNav(IReadOnlyList<PocNavItem>? items)
    {
        if (items == null)
            return string.Empty;

        var sb = new StringBuilder();
        var activeUsed = false;
        var groupOpened = false;

        foreach (var item in items)
        {
            var rawLabel = (item?.Label ?? string.Empty).Trim();
            if (rawLabel.Length == 0)
                continue;

            var label = WebUtility.HtmlEncode(rawLabel);
            var icon = PickIcon(rawLabel, item!.Icon);

            var active = activeUsed ? string.Empty : " active";
            activeUsed = true;

            var children = item.Children?
                .Where(c => c != null && !string.IsNullOrWhiteSpace(c.Label))
                .ToList() ?? new List<PocNavItem>();

            if (children.Count == 0)
            {
                sb.Append("                    <div class=\"nav-item").Append(active).Append("\" title=\"").Append(label).Append("\">\n");
                sb.Append("                        ").Append(icon).Append('\n');
                sb.Append("                        <span class=\"nav-label\">").Append(label).Append("</span>\n");
                sb.Append("                    </div>\n");
                continue;
            }

            var open = groupOpened ? string.Empty : " open";
            groupOpened = true;

            sb.Append("                    <div class=\"nav-group").Append(open).Append("\">\n");
            sb.Append("                        <div class=\"nav-item").Append(active).Append("\" title=\"").Append(label).Append("\">\n");
            sb.Append("                            ").Append(icon).Append('\n');
            sb.Append("                            <span class=\"nav-label\">").Append(label).Append("</span>\n");
            sb.Append("                            ").Append(Chevron).Append('\n');
            sb.Append("                        </div>\n");
            sb.Append("                        <div class=\"nav-sub\">\n");
            foreach (var child in children)
            {
                var childLabel = child.Label.Trim();
                sb.Append("                            <div class=\"nav-item\">").Append(PickIcon(childLabel, child.Icon))
                  .Append("<span class=\"nav-label\">").Append(WebUtility.HtmlEncode(childLabel)).Append("</span></div>\n");
            }
            sb.Append("                        </div>\n");
            sb.Append("                    </div>\n");
        }

        return sb.ToString();
    }

    // Renders the Bootstrap Icons element for an item: the explicit name wins, otherwise infer from
    // the label by keyword, otherwise a neutral dot.
    private static string PickIcon(string label, string? explicitIcon)
    {
        var name = SanitizeIconName(explicitIcon);
        if (name != null)
            return BiMarkup(name);

        var key = Normalize(label);
        if (key.Length > 0)
        {
            foreach (var (keywords, icon) in IconRules)
                foreach (var keyword in keywords)
                    if (key.Contains(keyword, StringComparison.Ordinal))
                        return BiMarkup(icon);
        }

        return BiMarkup(DefaultIcon);
    }

    private static string BiMarkup(string name) =>
        "<i class=\"bi bi-" + name + "\" aria-hidden=\"true\"></i>";

    // Bootstrap Icons names are [a-z0-9-]; lower-case, drop an optional leading "bi-"/"bi ", and keep
    // only that safe charset so an agent-supplied value can never break out of the class attribute.
    // Returns null when nothing usable remains, so the caller falls back to a keyword/default icon.
    private static string? SanitizeIconName(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return null;

        var s = raw.Trim().ToLowerInvariant();
        if (s.StartsWith("bi-", StringComparison.Ordinal) || s.StartsWith("bi ", StringComparison.Ordinal))
            s = s[3..];

        var sb = new StringBuilder(s.Length);
        foreach (var ch in s)
        {
            if (ch is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '-')
                sb.Append(ch);
        }

        var cleaned = sb.ToString().Trim('-');
        return cleaned.Length == 0 ? null : cleaned;
    }

    // Lowercases and strips diacritics so keyword matching works on labels like "Sản phẩm" → "san pham";
    // đ/Đ collapse to d (FormD decomposition alone doesn't separate them).
    private static string Normalize(string label)
    {
        var lowered = label.Trim().ToLowerInvariant().Replace('đ', 'd');
        var decomposed = lowered.Normalize(NormalizationForm.FormD);

        var sb = new StringBuilder(decomposed.Length);
        foreach (var ch in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) != UnicodeCategory.NonSpacingMark)
                sb.Append(ch);
        }

        return sb.ToString().Normalize(NormalizationForm.FormC);
    }
}
