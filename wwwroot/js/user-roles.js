// Trang User Roles: gán / thu hồi role cho user trên Bosch IdentityServer.
// - Nạp danh sách role vào dropdown filter + dropdown trong popup Add.
// - Chọn role ở filter => nạp danh sách user đang có role đó (mỗi hàng có nút Thu hồi).
// - Ô autocomplete: gõ => gọi SearchUsers lấy gợi ý; chọn một user => giữ UserName để Assign/Withdraw.
// Fail-open: mọi lời gọi lỗi chỉ hiện thông báo, không phá trang.
(function () {
    "use strict";

    var rowsBody = document.getElementById("urUserRows");
    if (!rowsBody) return; // Không có quyền / không phải trang này.

    var canManage = rowsBody.getAttribute("data-can-manage") === "true";
    var columnCount = canManage ? 4 : 3;

    var roleFilter = document.getElementById("urRoleFilter");

    // Các phần tử của popup Add chỉ tồn tại khi có quyền quản lý.
    var assignRole = document.getElementById("urAssignRole");
    var userSearch = document.getElementById("urUserSearch");
    var selectedUser = document.getElementById("urSelectedUser");
    var suggestBox = document.getElementById("urSuggest");
    var selectedChip = document.getElementById("urSelectedChip");
    var assignBtn = document.getElementById("urAssignBtn");
    var withdrawBtn = document.getElementById("urWithdrawBtn");

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

    // ----- Nạp danh sách role vào các dropdown -----
    function loadRoles() {
        getJson("/UserRoles/Roles").then(function (data) {
            if (!data.ok) { showFlash(data.message || "Không lấy được danh sách role.", "error"); return; }
            var items = data.items || [];
            var options = '<option value="">— Chọn role —</option>' + items.map(function (r) {
                return '<option value="' + escapeHtml(r.name) + '">' + escapeHtml(r.name) + "</option>";
            }).join("");
            roleFilter.innerHTML = options;
            if (assignRole) assignRole.innerHTML = options;
        });
    }

    // ----- Bảng người dùng theo role -----
    function renderMessageRow(message) {
        rowsBody.innerHTML = '<tr><td colspan="' + columnCount + '" class="ur-empty">' + escapeHtml(message) + "</td></tr>";
    }

    function renderUsers(users, roleName) {
        if (!users || users.length === 0) {
            renderMessageRow("Chưa có người dùng nào được gán role này.");
            return;
        }
        rowsBody.innerHTML = users.map(function (u) {
            var action = "";
            if (canManage) {
                action = '<td class="actions">' +
                    '<button class="btn danger btn-small ur-row-withdraw" type="button"' +
                    ' data-user="' + escapeHtml(u.userName) + '"' +
                    ' data-role="' + escapeHtml(roleName) + '">Thu hồi</button></td>';
            }
            return "<tr>" +
                "<td><b>" + escapeHtml(u.displayName) + "</b><br><small class=\"ur-muted\">" + escapeHtml(u.userName) + "</small></td>" +
                "<td>" + escapeHtml(u.email || "–") + "</td>" +
                "<td>" + escapeHtml(u.department || "–") + "</td>" +
                action +
                "</tr>";
        }).join("");
    }

    function loadUsersByRole(roleName) {
        if (!roleName) { renderMessageRow("Chọn một role ở trên để xem danh sách người dùng."); return; }
        renderMessageRow("Đang tải…");
        getJson("/UserRoles/UsersByRole?roleName=" + encodeURIComponent(roleName)).then(function (data) {
            if (!data.ok) { renderMessageRow(data.message || "Không tải được danh sách người dùng."); return; }
            renderUsers(data.items || [], roleName);
        });
    }

    roleFilter.addEventListener("change", function () { loadUsersByRole(roleFilter.value); });

    // Nút Thu hồi trên từng hàng (event delegation vì hàng được render động).
    rowsBody.addEventListener("click", function (e) {
        var btn = e.target.closest(".ur-row-withdraw");
        if (!btn) return;
        var user = btn.getAttribute("data-user");
        var role = btn.getAttribute("data-role");
        if (!confirm('Thu hồi role "' + role + '" của ' + user + "?")) return;
        btn.disabled = true;
        postForm("/UserRoles/Withdraw", { roleName: role, userName: user }).then(function (data) {
            showFlash(data.message, data.ok ? "success" : "error");
            if (data.ok) loadUsersByRole(role); else btn.disabled = false;
        });
    });

    // ----- Autocomplete người dùng (chỉ khi có popup Add) -----
    if (canManage && userSearch) {
        var searchTimer = null;

        function clearSelectedUser() {
            selectedUser.value = "";
            selectedChip.hidden = true;
            selectedChip.textContent = "";
        }

        function hideSuggest() {
            suggestBox.classList.add("hidden");
            suggestBox.innerHTML = "";
            userSearch.setAttribute("aria-expanded", "false");
        }

        function pickUser(user) {
            selectedUser.value = user.userName;
            userSearch.value = user.displayName;
            selectedChip.hidden = false;
            selectedChip.innerHTML = '<i class="bi bi-person-check" aria-hidden="true"></i> ' +
                escapeHtml(user.displayName) + " (" + escapeHtml(user.userName) + ")";
            hideSuggest();
        }

        function renderSuggest(users) {
            if (!users || users.length === 0) {
                suggestBox.innerHTML = '<div class="ur-suggest-empty">Không tìm thấy người dùng phù hợp.</div>';
                suggestBox.classList.remove("hidden");
                userSearch.setAttribute("aria-expanded", "true");
                return;
            }
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
            suggestBox.classList.remove("hidden");
            userSearch.setAttribute("aria-expanded", "true");
        }

        userSearch.addEventListener("input", function () {
            clearSelectedUser(); // gõ lại => bỏ lựa chọn cũ, buộc chọn từ gợi ý.
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

        document.addEventListener("click", function (e) {
            if (!e.target.closest(".ur-autocomplete")) hideSuggest();
        });

        // ----- Assign / Withdraw từ popup -----
        function submitFromModal(url) {
            var role = assignRole.value;
            var user = selectedUser.value;
            if (!role) { showFlash("Vui lòng chọn role.", "error"); return; }
            if (!user) { showFlash("Vui lòng chọn người dùng từ danh sách gợi ý.", "error"); return; }

            assignBtn.disabled = true;
            withdrawBtn.disabled = true;
            postForm(url, { roleName: role, userName: user }).then(function (data) {
                assignBtn.disabled = false;
                withdrawBtn.disabled = false;
                showFlash(data.message, data.ok ? "success" : "error");
                if (data.ok) {
                    closeModal("assignModal");
                    resetModal();
                    // Nếu đang xem đúng role vừa thay đổi thì làm mới bảng.
                    if (roleFilter.value && roleFilter.value === role) loadUsersByRole(role);
                }
            });
        }

        function resetModal() {
            assignRole.value = "";
            userSearch.value = "";
            clearSelectedUser();
            hideSuggest();
        }

        assignBtn.addEventListener("click", function () { submitFromModal("/UserRoles/Assign"); });
        withdrawBtn.addEventListener("click", function () { submitFromModal("/UserRoles/Withdraw"); });
    }

    // Nạp role khi vào trang.
    loadRoles();
})();
