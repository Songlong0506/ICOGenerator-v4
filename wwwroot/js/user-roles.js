// Trang User Roles: gán / thu hồi role cho user trên Bosch IdentityServer.
// - Vào trang => nạp danh sách role (RolesByAPIName) thành bảng "Role name | Action".
// - Bấm một hàng (hoặc icon Action) => popup liệt kê user đang có role đó (IdentityServerUser).
// - Trong popup: ô autocomplete tìm user LDAP + nút Assign (RoleForUser); mỗi hàng user có icon xóa
//   (WithdrawalRole). Gán/thu hồi xong thì nạp lại danh sách user của role đang mở.
// Fail-open: mọi lời gọi lỗi chỉ hiện thông báo, không phá trang.

// Popup "Roles & Permissions" (nút Permissions trên command bar): ô tìm kiếm lọc hàng quyền, nhóm màn
// hình ẩn khi không còn quyền nào khớp; link cũ /Roles chuyển về đây kèm #permissions ⇒ mở sẵn popup.
// Tách khỏi IIFE bên dưới vì popup có thể tồn tại cả khi người dùng không có quyền xem bảng user.
(function () {
    "use strict";

    var modal = document.getElementById("permissionsModal");
    if (!modal) return;

    var search = document.getElementById("rolesSearch");
    var rows = Array.prototype.slice.call(modal.querySelectorAll("#rolesTable tbody tr"));

    function applySearch() {
        var q = (search.value || "").trim().toLowerCase();
        var header = null, headerHasMatch = false;
        function closeGroup() { if (header) header.classList.toggle("hidden", !headerHasMatch); }
        rows.forEach(function (row) {
            if (row.classList.contains("screen-row")) {
                closeGroup();
                header = row;
                // Khớp tên màn hình ⇒ hiện cả nhóm.
                headerHasMatch = q === "" || row.textContent.toLowerCase().indexOf(q) >= 0;
                row.dataset.groupMatch = headerHasMatch ? "1" : "";
                return;
            }
            var match = q === "" || (header && header.dataset.groupMatch === "1")
                || row.textContent.toLowerCase().indexOf(q) >= 0;
            row.classList.toggle("hidden", !match);
            if (match) headerHasMatch = true;
        });
        closeGroup();
    }

    if (search) search.addEventListener("input", applySearch);

    if (window.location.hash === "#permissions") {
        openModal("permissionsModal");
        history.replaceState(null, "", window.location.pathname + window.location.search);
    }
})();

(function () {
    "use strict";

    var roleRows = document.getElementById("urRoleRows");
    var modal = document.getElementById("roleUsersModal");
    if (!roleRows || !modal) return; // Không có quyền / không phải trang này.

    var canManage = modal.getAttribute("data-can-manage") === "true";
    var userColumnCount = canManage ? 4 : 3;

    var userRows = document.getElementById("urUserRows");
    var roleNameEl = document.getElementById("urUsersRoleName");
    var countEl = document.getElementById("urUsersCount");

    // Các phần tử gán role chỉ tồn tại khi có quyền quản lý.
    var userSearch = document.getElementById("urUserSearch");
    var selectedUser = document.getElementById("urSelectedUser");
    var suggestBox = document.getElementById("urSuggest");
    var assignBtn = document.getElementById("urAssignBtn");

    // Role đang mở trong popup — đích của Assign/Withdraw và của lần nạp lại danh sách user.
    var currentRole = "";
    // Bộ đếm lượt nạp: đóng popup rồi mở role khác nhanh thì kết quả của role cũ về muộn bị bỏ qua.
    var loadSeq = 0;

    function antiForgeryToken() {
        var el = document.querySelector('input[name="__RequestVerificationToken"]');
        return el ? el.value : "";
    }

    function showFlash(message, kind) {
        if (window.toast) { window.toast(message, kind || "info"); }
    }

    var reauthTriggered = false;

    // Phiên SSO hết hạn giữa chừng: server trả { authExpired:true, loginUrl }. Đá người dùng sang endpoint
    // ReAuth để challenge lại OIDC (thường im lặng). Trả về Promise không bao giờ resolve để callback phía
    // sau (hiển thị lỗi / bật lại nút) không chạy — trang sắp điều hướng đi.
    function handleAuthExpired(data) {
        if (!reauthTriggered) {
            reauthTriggered = true;
            showFlash(data.message || "Phiên đăng nhập đã hết hạn. Đang đăng nhập lại…", "info");
            window.location.assign(data.loginUrl || "/Account/Login");
        }
        return new Promise(function () { });
    }

    // Đọc JSON kể cả khi status lỗi để không nuốt mất tín hiệu authExpired/loginUrl; authExpired ⇒ điều hướng.
    function readJson(r) {
        return r.json()
            .catch(function () { return { ok: false, message: "Máy chủ trả về lỗi." }; })
            .then(function (data) { return data && data.authExpired ? handleAuthExpired(data) : data; });
    }

    function getJson(url) {
        return fetch(url, { headers: { "Accept": "application/json" }, credentials: "same-origin" })
            .then(readJson)
            .catch(function () { return { ok: false, message: "Lỗi mạng khi gọi máy chủ." }; });
    }

    function postForm(url, data) {
        var body = Object.keys(data)
            .map(function (k) { return encodeURIComponent(k) + "=" + encodeURIComponent(data[k]); })
            .join("&");
        return fetch(url, {
            method: "POST",
            headers: {
                "Content-Type": "application/x-www-form-urlencoded",
                "RequestVerificationToken": antiForgeryToken(),
                "Accept": "application/json"
            },
            credentials: "same-origin",
            body: body
        })
            .then(readJson)
            .catch(function () { return { ok: false, message: "Lỗi mạng khi gọi máy chủ." }; });
    }

    // ----- Bảng role -----
    // Hàng thông báo gắn data-cbar-skip để ô tìm kiếm của command bar không ẩn/đếm nó.
    function renderRoleMessage(message) {
        roleRows.innerHTML = '<tr data-cbar-skip><td colspan="2" class="ur-empty">' + escapeHtml(message) + "</td></tr>";
    }

    function renderRoles(roles) {
        if (!roles || roles.length === 0) {
            renderRoleMessage("IdentityServer không trả về role nào cho API resource này.");
            return;
        }
        roleRows.innerHTML = roles.map(function (r) {
            var name = escapeHtml(r.name);
            return '<tr class="ur-role-row" data-role="' + name + '">' +
                "<td>" + name + "</td>" +
                '<td class="ur-action-col">' +
                '<button type="button" class="icon-btn icon-btn--accent ur-open-role" data-role="' + name + '"' +
                ' title="Người dùng của role ' + name + '" aria-label="Người dùng của role ' + name + '"' +
                ' aria-haspopup="dialog"><i class="bi bi-person-gear" aria-hidden="true"></i></button>' +
                "</td></tr>";
        }).join("");
    }

    function loadRoles() {
        getJson("/UserRoles/Roles").then(function (data) {
            if (!data.ok) { renderRoleMessage(data.message || "Không lấy được danh sách role."); return; }
            renderRoles(data.items || []);
        });
    }

    // Cả hàng đều bấm được (như ảnh mẫu); nút icon là control thật cho bàn phím/trình đọc màn hình.
    roleRows.addEventListener("click", function (e) {
        var row = e.target.closest(".ur-role-row");
        if (row) openRole(row.getAttribute("data-role"));
    });

    // ----- Popup người dùng của role -----
    function renderUserMessage(message) {
        userRows.innerHTML = '<tr><td colspan="' + userColumnCount + '" class="ur-empty">' + escapeHtml(message) + "</td></tr>";
    }

    function renderUsers(users) {
        countEl.textContent = users.length;
        if (users.length === 0) {
            renderUserMessage("Chưa có người dùng nào được gán role này.");
            return;
        }
        userRows.innerHTML = users.map(function (u) {
            var action = "";
            if (canManage) {
                action = '<td class="ur-action-col">' +
                    '<button class="icon-btn danger ur-row-withdraw" type="button"' +
                    ' data-user="' + escapeHtml(u.userName) + '"' +
                    ' title="Xóa ' + escapeHtml(u.userName) + ' khỏi role" aria-label="Xóa ' + escapeHtml(u.userName) + ' khỏi role">' +
                    '<i class="bi bi-trash" aria-hidden="true"></i></button></td>';
            }
            return "<tr>" +
                "<td><b>" + escapeHtml(u.displayName) + "</b><br><small class=\"ur-muted\">" + escapeHtml(u.userName) + "</small></td>" +
                "<td>" + escapeHtml(u.email || "–") + "</td>" +
                "<td>" + escapeHtml(u.department || "–") + "</td>" +
                action +
                "</tr>";
        }).join("");
    }

    function loadUsers() {
        var seq = ++loadSeq;
        var role = currentRole;
        renderUserMessage("Đang tải…");
        getJson("/UserRoles/UsersByRole?roleName=" + encodeURIComponent(role)).then(function (data) {
            if (seq !== loadSeq) return;
            if (!data.ok) { renderUserMessage(data.message || "Không tải được danh sách người dùng."); return; }
            renderUsers(data.items || []);
        });
    }

    function openRole(roleName) {
        if (!roleName) return;
        currentRole = roleName;
        roleNameEl.textContent = roleName;
        countEl.textContent = "…";
        if (canManage) resetAssign();
        openModal("roleUsersModal");
        loadUsers();
        if (userSearch) userSearch.focus();
    }

    // Icon xóa trên từng hàng (event delegation vì hàng được render động).
    userRows.addEventListener("click", function (e) {
        var btn = e.target.closest(".ur-row-withdraw");
        if (!btn) return;
        var user = btn.getAttribute("data-user");
        var role = currentRole;
        if (!confirm('Xóa ' + user + ' khỏi role "' + role + '"?')) return;
        btn.disabled = true;
        postForm("/UserRoles/Withdraw", { roleName: role, userName: user }).then(function (data) {
            showFlash(data.message, data.ok ? "success" : "error");
            if (data.ok) { if (role === currentRole) loadUsers(); } else btn.disabled = false;
        });
    });

    // ----- Autocomplete + Assign (chỉ khi có quyền quản lý) -----
    var searchTimer = null;

    function hideSuggest() {
        if (!suggestBox) return;
        suggestBox.classList.add("hidden");
        suggestBox.innerHTML = "";
        userSearch.setAttribute("aria-expanded", "false");
    }

    function resetAssign() {
        if (searchTimer) clearTimeout(searchTimer);
        userSearch.value = "";
        selectedUser.value = "";
        hideSuggest();
    }

    if (canManage && userSearch) {
        function pickUser(user) {
            selectedUser.value = user.userName;
            userSearch.value = user.displayName + " (" + user.userName + ")";
            hideSuggest();
        }

        function renderSuggest(users) {
            if (!users || users.length === 0) {
                suggestBox.innerHTML = '<div class="ur-suggest-empty">Không tìm thấy người dùng phù hợp.</div>';
            } else {
                suggestBox.innerHTML = users.map(function (u, i) {
                    return '<div class="ur-suggest-item" role="option" data-idx="' + i + '">' +
                        "<span class=\"ur-suggest-name\">" + escapeHtml(u.displayName) + "</span>" +
                        "<span class=\"ur-suggest-meta\">" + escapeHtml(u.userName) +
                        (u.department ? " · " + escapeHtml(u.department) : "") + "</span></div>";
                }).join("");
                // Gắn dữ liệu user vào từng item để chọn không phải parse lại.
                Array.prototype.forEach.call(suggestBox.children, function (el, i) {
                    el.addEventListener("mousedown", function (ev) {
                        ev.preventDefault(); // giữ focus, tránh blur đóng danh sách trước khi chọn.
                        pickUser(users[i]);
                    });
                });
            }
            suggestBox.classList.remove("hidden");
            userSearch.setAttribute("aria-expanded", "true");
        }

        userSearch.addEventListener("input", function () {
            selectedUser.value = ""; // gõ lại => bỏ lựa chọn cũ, buộc chọn từ gợi ý.
            var key = userSearch.value.trim();
            if (searchTimer) clearTimeout(searchTimer);
            if (key.length < 2) { hideSuggest(); return; }
            searchTimer = setTimeout(function () {
                getJson("/UserRoles/SearchUsers?searchKey=" + encodeURIComponent(key)).then(function (data) {
                    if (!data.ok) { hideSuggest(); showFlash(data.message || "Không tra cứu được người dùng.", "error"); return; }
                    renderSuggest(data.items || []);
                });
            }, 300);
        });

        userSearch.addEventListener("blur", function () { setTimeout(hideSuggest, 150); });

        assignBtn.addEventListener("click", function () {
            var role = currentRole;
            var user = selectedUser.value;
            if (!user) { showFlash("Vui lòng chọn người dùng từ danh sách gợi ý.", "error"); return; }

            assignBtn.disabled = true;
            postForm("/UserRoles/Assign", { roleName: role, userName: user }).then(function (data) {
                assignBtn.disabled = false;
                showFlash(data.message, data.ok ? "success" : "error");
                if (data.ok) {
                    resetAssign();
                    if (role === currentRole) loadUsers();
                }
            });
        });
    }

    // Nạp role khi vào trang.
    loadRoles();
})();
