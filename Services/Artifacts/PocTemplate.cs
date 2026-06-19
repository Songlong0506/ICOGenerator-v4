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

    // Label-aware sidebar icons. The agent only supplies navItem *text*, so each label is mapped to a
    // meaningful Feather-style glyph by keyword (English + Vietnamese, diacritic-insensitive); unmatched
    // labels fall back to a small solid dot. This replaced the old generic square/circle, which — with
    // the template's .ico styling (fill:none, stroke:currentColor) — rendered as an empty checkbox /
    // radio button, so every menu looked iconless. The chevron still marks expandable groups.
    private const string Chevron = "<svg class=\"ico nav-chevron\" viewBox=\"0 0 24 24\"><path d=\"M6 9l6 6 6-6\" /></svg>";

    private const string IcoHome = "<path d=\"M3 9l9-7 9 7v11a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2z\"/><path d=\"M9 22V12h6v10\"/>";
    private const string IcoLogin = "<path d=\"M15 3h4a2 2 0 0 1 2 2v14a2 2 0 0 1-2 2h-4\"/><path d=\"M10 17l5-5-5-5\"/><path d=\"M15 12H3\"/>";
    private const string IcoRegister = "<path d=\"M16 21v-2a4 4 0 0 0-4-4H6a4 4 0 0 0-4 4v2\"/><circle cx=\"9\" cy=\"7\" r=\"4\"/><path d=\"M20 8v6M23 11h-6\"/>";
    private const string IcoLogout = "<path d=\"M9 21H5a2 2 0 0 1-2-2V5a2 2 0 0 1 2-2h4\"/><path d=\"M16 17l5-5-5-5\"/><path d=\"M21 12H9\"/>";
    private const string IcoUsers = "<path d=\"M17 21v-2a4 4 0 0 0-4-4H5a4 4 0 0 0-4 4v2\"/><circle cx=\"9\" cy=\"7\" r=\"4\"/><path d=\"M23 21v-2a4 4 0 0 0-3-3.87\"/><path d=\"M16 3.13a4 4 0 0 1 0 7.75\"/>";
    private const string IcoUser = "<circle cx=\"12\" cy=\"8\" r=\"4\"/><path d=\"M4 21a8 8 0 0 1 16 0\"/>";
    private const string IcoPackage = "<path d=\"M21 16V8a2 2 0 0 0-1-1.73l-7-4a2 2 0 0 0-2 0l-7 4A2 2 0 0 0 3 8v8a2 2 0 0 0 1 1.73l7 4a2 2 0 0 0 2 0l7-4A2 2 0 0 0 21 16z\"/><path d=\"M3.27 6.96L12 12l8.73-5.04M12 22V12\"/>";
    private const string IcoCart = "<circle cx=\"9\" cy=\"21\" r=\"1\"/><circle cx=\"20\" cy=\"21\" r=\"1\"/><path d=\"M1 1h4l2.7 13.4a2 2 0 0 0 2 1.6h9.7a2 2 0 0 0 2-1.6L23 6H6\"/>";
    private const string IcoPayment = "<rect x=\"1\" y=\"4\" width=\"22\" height=\"16\" rx=\"2\"/><path d=\"M1 10h22\"/>";
    private const string IcoBag = "<path d=\"M6 2L3 6v14a2 2 0 0 0 2 2h14a2 2 0 0 0 2-2V6l-3-4z\"/><path d=\"M3 6h18\"/><path d=\"M16 10a4 4 0 0 1-8 0\"/>";
    private const string IcoShield = "<path d=\"M12 22s8-4 8-10V5l-8-3-8 3v7c0 6 8 10 8 10z\"/>";
    private const string IcoGrid = "<rect x=\"3\" y=\"3\" width=\"7\" height=\"7\"/><rect x=\"14\" y=\"3\" width=\"7\" height=\"7\"/><rect x=\"14\" y=\"14\" width=\"7\" height=\"7\"/><rect x=\"3\" y=\"14\" width=\"7\" height=\"7\"/>";
    private const string IcoSettings = "<circle cx=\"12\" cy=\"12\" r=\"3.2\"/><path d=\"M12 2v3M12 19v3M4.9 4.9l2.1 2.1M17 17l2.1 2.1M2 12h3M19 12h3M4.9 19.1l2.1-2.1M17 7l2.1-2.1\"/>";
    private const string IcoList = "<line x1=\"8\" y1=\"6\" x2=\"21\" y2=\"6\"/><line x1=\"8\" y1=\"12\" x2=\"21\" y2=\"12\"/><line x1=\"8\" y1=\"18\" x2=\"21\" y2=\"18\"/><line x1=\"3\" y1=\"6\" x2=\"3.01\" y2=\"6\"/><line x1=\"3\" y1=\"12\" x2=\"3.01\" y2=\"12\"/><line x1=\"3\" y1=\"18\" x2=\"3.01\" y2=\"18\"/>";
    private const string IcoFile = "<path d=\"M14 2H6a2 2 0 0 0-2 2v16a2 2 0 0 0 2 2h12a2 2 0 0 0 2-2V8z\"/><path d=\"M14 2v6h6\"/><path d=\"M16 13H8M16 17H8M10 9H8\"/>";
    private const string IcoSearch = "<circle cx=\"11\" cy=\"11\" r=\"8\"/><path d=\"M21 21l-4.35-4.35\"/>";
    private const string IcoChart = "<line x1=\"12\" y1=\"20\" x2=\"12\" y2=\"10\"/><line x1=\"18\" y1=\"20\" x2=\"18\" y2=\"4\"/><line x1=\"6\" y1=\"20\" x2=\"6\" y2=\"16\"/>";
    private const string IcoCalendar = "<rect x=\"3\" y=\"4\" width=\"18\" height=\"18\" rx=\"2\"/><line x1=\"16\" y1=\"2\" x2=\"16\" y2=\"6\"/><line x1=\"8\" y1=\"2\" x2=\"8\" y2=\"6\"/><line x1=\"3\" y1=\"10\" x2=\"21\" y2=\"10\"/>";
    private const string IcoBell = "<path d=\"M18 8a6 6 0 0 0-12 0c0 7-3 9-3 9h18s-3-2-3-9\"/><path d=\"M13.73 21a2 2 0 0 1-3.46 0\"/>";
    private const string IcoMessage = "<path d=\"M21 15a2 2 0 0 1-2 2H7l-4 4V5a2 2 0 0 1 2-2h14a2 2 0 0 1 2 2z\"/>";
    private const string IcoTag = "<path d=\"M20.59 13.41l-7.17 7.17a2 2 0 0 1-2.83 0L2 12V2h10l8.59 8.59a2 2 0 0 1 0 2.82z\"/><line x1=\"7\" y1=\"7\" x2=\"7.01\" y2=\"7\"/>";
    private const string IcoClock = "<circle cx=\"12\" cy=\"12\" r=\"10\"/><path d=\"M12 6v6l4 2\"/>";
    private const string IcoMoney = "<line x1=\"12\" y1=\"1\" x2=\"12\" y2=\"23\"/><path d=\"M17 5H9.5a3.5 3.5 0 0 0 0 7h5a3.5 3.5 0 0 1 0 7H6\"/>";
    private const string IcoHeart = "<path d=\"M20.84 4.61a5.5 5.5 0 0 0-7.78 0L12 5.67l-1.06-1.06a5.5 5.5 0 0 0-7.78 7.78l1.06 1.06L12 21.23l7.78-7.78 1.06-1.06a5.5 5.5 0 0 0 0-7.78z\"/>";
    private const string IcoDot = "<circle cx=\"12\" cy=\"12\" r=\"3\" fill=\"currentColor\" stroke=\"none\"/>";

    // Matched top-to-bottom on the normalized label via Contains, so order from specific to generic:
    // e.g. "Quản lý sản phẩm" hits "san pham" (package) before the generic "quan ly" (list).
    private static readonly (string[] Keywords, string Icon)[] IconRules =
    {
        (new[] { "dang nhap", "login", "sign in", "log in", "signin" }, IcoLogin),
        (new[] { "dang ky", "dang ki", "register", "sign up", "signup", "tao tai khoan" }, IcoRegister),
        (new[] { "dang xuat", "logout", "log out", "sign out", "thoat" }, IcoLogout),
        (new[] { "trang chu", "trang chinh", "home page", "home", "main" }, IcoHome),
        (new[] { "gio hang", "cart", "basket" }, IcoCart),
        (new[] { "thanh toan", "payment", "checkout", "credit card" }, IcoPayment),
        (new[] { "chi tiet", "detail", "thong tin", "mo ta", "gioi thieu", "about" }, IcoFile),
        (new[] { "don hang", "don dat", "order", "purchase", "mua hang" }, IcoBag),
        (new[] { "hoa don", "invoice", "bill", "receipt" }, IcoFile),
        (new[] { "san pham", "product", "hang hoa", "mat hang", "ton kho", "inventory", "kho hang" }, IcoPackage),
        (new[] { "danh muc", "category", "categories", "the loai", "phan loai", "thuong hieu", "nhan hieu", "brand" }, IcoTag),
        (new[] { "khuyen mai", "giam gia", "voucher", "coupon", "ma giam", "discount", "promotion", "uu dai" }, IcoTag),
        (new[] { "doanh thu", "doanh so", "revenue", "tai chinh", "finance", "thu chi", "cong no", "gia ban" }, IcoMoney),
        (new[] { "bao cao", "report", "thong ke", "statistic", "analytic", "bieu do", "chart", "phan tich" }, IcoChart),
        (new[] { "nguoi dung", "tai khoan", "khach hang", "thanh vien", "user", "account", "customer", "member", "nhan vien", "staff", "employee" }, IcoUsers),
        (new[] { "ho so", "profile", "ca nhan", "my account" }, IcoUser),
        (new[] { "dashboard", "tong quan", "overview", "bang dieu khien", "tong hop" }, IcoGrid),
        (new[] { "quan tri", "admin", "he thong", "system", "phan quyen", "vai tro", "role", "permission", "bao mat", "security" }, IcoShield),
        (new[] { "cai dat", "setting", "cau hinh", "config", "tuy chon", "preference", "thiet lap" }, IcoSettings),
        (new[] { "tim kiem", "search", "tra cuu", "loc ", "filter" }, IcoSearch),
        (new[] { "lich su", "history", "nhat ky", "hoat dong", "activity", "audit" }, IcoClock),
        (new[] { "lich", "calendar", "schedule", "dat lich", "appointment", "booking" }, IcoCalendar),
        (new[] { "thong bao", "notification", "bell", "alert", "canh bao" }, IcoBell),
        (new[] { "tin nhan", "message", "chat", "lien he", "contact", "ho tro", "support", "phan hoi", "feedback", "binh luan", "comment", "danh gia", "review" }, IcoMessage),
        (new[] { "yeu thich", "wishlist", "favorite", "favourite", "da luu", "saved", "bookmark" }, IcoHeart),
        (new[] { "danh sach", "list", "quan ly", "manage", "management" }, IcoList),
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
            var icon = PickIcon(rawLabel);

            var active = activeUsed ? string.Empty : " active";
            activeUsed = true;

            var children = item!.Children?
                .Select(c => (c ?? string.Empty).Trim())
                .Where(c => c.Length > 0)
                .ToList() ?? new List<string>();

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
                sb.Append("                            <div class=\"nav-item\">").Append(PickIcon(child))
                  .Append("<span class=\"nav-label\">").Append(WebUtility.HtmlEncode(child)).Append("</span></div>\n");
            }
            sb.Append("                        </div>\n");
            sb.Append("                    </div>\n");
        }

        return sb.ToString();
    }

    private static string Svg(string body) => "<svg class=\"ico\" viewBox=\"0 0 24 24\">" + body + "</svg>";

    /// <summary>Picks a sidebar glyph for <paramref name="label"/> by keyword, falling back to a solid dot.</summary>
    private static string PickIcon(string label)
    {
        var key = Normalize(label);
        if (key.Length > 0)
        {
            foreach (var (keywords, icon) in IconRules)
                foreach (var keyword in keywords)
                    if (key.Contains(keyword, StringComparison.Ordinal))
                        return Svg(icon);
        }

        return Svg(IcoDot);
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
