// Bộ lái kịch bản nghiệm thu (UAT) chạy TRONG trang POC — PlaywrightPocRuntimeChecker nạp file này bằng
// AddInitScript rồi gọi window.__pocUat.act(...) cho từng thao tác. Xem PocUatDriver.cs cho hợp đồng trả về.
//
// Nguyên tắc: bộ lái chỉ BÁO SỰ KIỆN (đã làm được / không có thứ cần bấm / bấm mà không có gì đổi / không
// làm được). Kết luận "kịch bản gãy" là việc của phía C#, và chỉ khi trang đang ở đúng trạng thái kịch bản
// giả định — tức mọi thao tác trước đó đều đã làm được.
(function () {
  if (window.__pocUat) return;

  // ── Chuẩn hoá chữ ──────────────────────────────────────────────────────────────────────────────────
  // NFC: cùng một chữ tiếng Việt có thể đến ở dạng dựng sẵn hoặc tổ hợp (model sinh, người gõ, trình duyệt).
  const norm = s => String(s || '').normalize('NFC').toLowerCase().replace(/\s+/g, ' ').trim();
  // Nhãn để so: bỏ dấu "*" (trường bắt buộc) và ":" ở cuối — "Name *" và "Tên:" vẫn là nhãn "name"/"tên".
  const normLabel = s => norm(s).replace(/^[\s*]+/, '').replace(/[\s*:：]+$/, '').trim();
  const sleep = ms => new Promise(r => setTimeout(r, ms));

  const visible = el => {
    if (!el || !el.getBoundingClientRect) return false;
    const r = el.getBoundingClientRect();
    if (r.width <= 0 || r.height <= 0) return false;
    const cs = getComputedStyle(el);
    return cs.visibility !== 'hidden' && cs.display !== 'none';
  };
  // Nhãn của một phần tử ĐANG BỊ ẩn (mục menu trong nhóm chưa xổ) có innerText rỗng, nên phải lùi về
  // textContent — nếu không thì đúng các màn hình vừa được gom nhóm lại biến mất khỏi tầm nhìn của lượt lái.
  // aria-label/title: nút chỉ có icon.
  const labelOf = el => normLabel(el.innerText || el.value || el.getAttribute('aria-label') || el.getAttribute('title') || el.textContent || '');

  // ── Vùng đang tương tác ───────────────────────────────────────────────────────────────────────────
  const activeSection = () => document.querySelector('section.page-view.active');
  const currentView = () => { const a = activeSection(); return a ? (a.dataset.view || '') : ''; };
  const openModals = () => Array.from(document.querySelectorAll('.modal.show, dialog[open], .offcanvas.show')).filter(visible);
  // Thứ tự ưu tiên khi tìm: modal đang mở (backdrop chặn phần còn lại) → màn đang mở → cả trang (sidebar…).
  const scopes = () => {
    const out = [];
    const m = openModals();
    if (m.length) out.push(m[m.length - 1]);
    const a = activeSection();
    if (a) out.push(a);
    out.push(document.body);
    return out;
  };
  const screenNames = () => Array.from(document.querySelectorAll('section.page-view')).map(s => norm(s.dataset.view || ''));

  const CLICKABLE = 'button, a, [role=button], [role=tab], [role=menuitem], [role=option], [role=link], '
    + 'input[type=submit], input[type=button], .btn, [data-crud-add], .sidebar-nav .nav-item, .view-as-item, '
    + '[onclick], summary, .list-group-item-action, .nav-link, .dropdown-item, .page-link';
  const isClickable = el => !!(el && el.matches && el.matches(CLICKABLE));

  // MỤC MENU là <div class="nav-item"> (PocTemplate.RenderNav). Tiêu đề NHÓM bị loại vì nó chỉ xổ/thu, không
  // mở màn nào; mục con của nhóm chưa xổ vẫn tính là bấm được (shell bắt click bằng delegation rồi tự mở
  // nhóm). Chỉ vai không được xem mới thật sự nằm ngoài tầm với — applyRoleToNav đặt display:none.
  const navLeaves = () => Array.from(document.querySelectorAll('.sidebar-nav .nav-item'))
    .filter(el => !(el.parentElement && el.parentElement.classList.contains('nav-group')))
    .filter(el => el.style.display !== 'none'
              && !(el.closest('.nav-group') && el.closest('.nav-group').style.display === 'none'))
    .filter(el => labelOf(el).length >= 2);

  // Chữ để phán "nhãn này có tồn tại trên trang không": phần nhìn thấy được, CỘNG nhãn trong sidebar (mục của
  // nhóm chưa xổ không nằm trong innerText). Cố ý KHÔNG dùng body.textContent — nó nuốt luôn mã JS của shell,
  // và khi đó không nhãn nào còn bị coi là thiếu nữa.
  const pageText = () => {
    const nav = document.querySelector('.sidebar-nav');
    return norm((document.body.innerText || '') + ' ' + (nav ? nav.textContent || '' : ''));
  };
  // Chữ của chỗ người dùng đang nhìn: modal đang mở + màn đang mở.
  const focusText = () => {
    let t = '';
    openModals().forEach(m => { t += ' ' + (m.innerText || ''); });
    const a = activeSection();
    if (a) t += ' ' + (a.innerText || '');
    return norm(t);
  };

  // ── Phát hiện "có gì đổi không" ───────────────────────────────────────────────────────────────────
  // "Nút chết" = người dùng bấm mà KHÔNG THẤY gì. Phản ứng nhìn thấy được không chỉ là chữ đổi: tô sáng một
  // khung (đổi class), hiện một khối (đổi style), cuộn tới chỗ cần xem — chỉ so chữ thì mọi nút "cuộn tới /
  // tô sáng" đều bị báo oan là chết. Nên chữ ký gồm: HTML (cả thuộc tính) của màn đang mở + modal đang mở,
  // vị trí cuộn của trang và của mọi khung chứa màn đó, class của <html>/<body> (thu/mở sidebar…).
  // Toast của shell nằm ngoài (#toastHost) nên KHÔNG lọt vào chữ ký — shell tự toast cho mọi .btn, tính vào
  // thì nút chết cũng "có phản ứng". Giá trị gõ trong ô (thuộc tính value) cũng không nằm trong HTML: gõ
  // không phải phản ứng của trang.
  const hashOf = text => { let h = 0; for (let i = 0; i < text.length; i++) { h = ((h * 31) + text.charCodeAt(i)) | 0; } return h; };
  const scrollSignature = () => {
    const parts = [];
    const root = document.scrollingElement;
    if (root) parts.push(root.scrollTop, root.scrollLeft);
    for (let n = activeSection(); n; n = n.parentElement) parts.push(n.scrollTop, n.scrollLeft);
    openModals().forEach(m => { m.querySelectorAll('.modal-body').forEach(b => parts.push(b.scrollTop)); });
    return parts.map(x => Math.round(x)).join(',');
  };
  const snapshot = () => {
    const active = activeSection();
    const modals = openModals();
    let html = active ? active.outerHTML : '';
    modals.forEach(m => { html += '|' + m.outerHTML; });
    html += '|' + document.documentElement.className + '|' + document.body.className;
    return {
      view: active ? (active.dataset.view || '') : '', hash: hashOf(html), modals: modals.length,
      loc: location.hash, scroll: scrollSignature()
    };
  };
  const differs = (a, b) => a.view !== b.view || a.hash !== b.hash || a.modals !== b.modals || a.loc !== b.loc || a.scroll !== b.scroll;
  // Chờ tới khi trang phản ứng (tối đa ~1,2s — đủ cho hiệu ứng fade của Bootstrap), dừng sớm khi đã đổi rồi
  // chờ thêm một nhịp cho hiệu ứng ổn định, để thao tác kế không tìm trong một modal còn đang mở dở.
  const settle = async (before, extra) => {
    for (let t = 0; t < 12; t++) {
      await sleep(100);
      if (differs(before, snapshot()) || (extra && extra())) { await sleep(150); return true; }
    }
    return false;
  };
  const roleNow = () => (typeof window.pocRole === 'function') ? norm(window.pocRole()) : null;

  const out = (status, label, note) => JSON.stringify({ status, label: label || '', note: note || '', view: currentView() });

  // ── Tìm điều khiển ────────────────────────────────────────────────────────────────────────────────
  // Nhãn trùng TÊN MỘT MÀN HÌNH thì điều khiển đúng là mục menu, không phải một nút cùng tên trong nội dung:
  // "bấm 'Duyệt đơn'" ở POC có màn "Duyệt đơn" lẫn nút "Duyệt đơn" trên bảng phải ưu tiên mở màn.
  // Trong từng vùng: trùng nguyên văn trước, rồi nhãn CHỨA chữ cần tìm (ưu tiên nhãn ngắn nhất — dòng bảng
  // chứa cả chục ô, liên kết mang đúng mã bản ghi thì ngắn hơn).
  function findControl(target) {
    const nq = normLabel(target);
    if (nq.length < 1) return null;
    const pools = [];
    if (screenNames().indexOf(nq) >= 0) pools.push(navLeaves());
    for (const sc of scopes()) pools.push(Array.from(sc.querySelectorAll(CLICKABLE)).filter(visible));
    pools.push(navLeaves());
    for (const pool of pools) {
      const exact = pool.find(el => labelOf(el) === nq);
      if (exact) return exact;
    }
    for (const pool of pools) {
      let best = null, bestLen = Infinity;
      for (const el of pool) {
        const l = labelOf(el);
        if (l.length >= nq.length && l.includes(nq) && l.length < bestLen) { best = el; bestLen = l.length; }
      }
      if (best) return best;
    }
    return null;
  }

  // Dòng/thẻ bắt click bằng addEventListener không lộ ra qua selector nào. Chữ định danh (mã bản ghi) có trên
  // màn thì bấm vào phần tử NHỎ NHẤT chứa nó, leo lên dòng/thẻ bao quanh. Đây là phỏng đoán ⇒ bấm mà không
  // đổi gì KHÔNG được chấm là nút chết (có thể dòng đó đúng là không bấm được).
  function rowFallback(target) {
    const nq = normLabel(target);
    if (nq.length < 2) return null;
    for (const sc of scopes()) {
      const hits = Array.from(sc.querySelectorAll('td, th, li, span, div, p, strong, b, small, h5, h6'))
        .filter(el => visible(el) && norm(el.innerText).includes(nq)
          && !Array.from(el.children).some(c => norm(c.innerText).includes(nq)));
      if (hits.length) {
        const leaf = hits[0];
        return leaf.closest('tr, li, .card, .list-group-item, [data-id], [data-key]') || leaf;
      }
    }
    return null;
  }

  // Neo do agent khai báo (data-uat="{kịch bản}.{bước}"). Chỉ nhận phần tử BẤM ĐƯỢC — tự nó, hoặc điều
  // khiển bên trong mang đúng nhãn, hoặc điều khiển DUY NHẤT bên trong. Neo đặt trên một khối chứa nhiều nút
  // (khối gánh neo của nhiều bước) không được hiểu thành "bấm nút đầu tiên trong khối": đó là cách lượt lái
  // từng bấm nhầm một nút bất kỳ rồi đi lạc cả kịch bản.
  function anchoredControl(anchor, target) {
    if (!anchor) return null;
    const nq = normLabel(target || '');
    const nodes = Array.from(document.querySelectorAll('[data-uat]'))
      .filter(n => (n.getAttribute('data-uat') || '').split(/\s+/).indexOf(anchor) >= 0 && visible(n));
    for (const n of nodes) {
      if (isClickable(n)) {
        const l = labelOf(n);
        // Nhãn neo lệch hẳn chữ của thao tác ⇒ neo đang gắn cho một thao tác khác của bước, không dùng.
        if (!nq || !l || l.includes(nq)) return n;
        continue;
      }
      const inner = Array.from(n.querySelectorAll(CLICKABLE)).filter(visible);
      const match = nq ? (inner.find(e => labelOf(e) === nq) || inner.find(e => labelOf(e).includes(nq))) : null;
      if (match) return match;
      if (inner.length === 1 && (!nq || labelOf(inner[0]).includes(nq) || !labelOf(inner[0]))) return inner[0];
    }
    return null;
  }

  // ── Ô nhập ────────────────────────────────────────────────────────────────────────────────────────
  const FIELD = 'input:not([type=hidden]):not([type=submit]):not([type=button]):not([type=reset]):not([type=image]), '
    + 'textarea, select, [contenteditable=true]';

  // Mọi nhãn một ô có thể mang: <label for>, <label> bọc ngoài, aria-label/aria-labelledby, placeholder,
  // name/title, và nhãn của khối nhóm gần nhất chỉ chứa ĐÚNG ô này (mẫu Bootstrap .mb-3 > label + input
  // không có for).
  function fieldLabels(el) {
    const out = [];
    if (el.id) document.querySelectorAll('label[for="' + CSS.escape(el.id) + '"]').forEach(l => out.push(l.innerText));
    const wrap = el.closest('label');
    if (wrap) out.push(wrap.innerText);
    ['aria-label', 'placeholder', 'title', 'name'].forEach(a => { const v = el.getAttribute(a); if (v) out.push(v); });
    const by = el.getAttribute('aria-labelledby');
    if (by) by.split(/\s+/).forEach(id => { const n = document.getElementById(id); if (n) out.push(n.innerText); });
    let p = el.parentElement;
    for (let i = 0; i < 4 && p && p !== document.body; i++, p = p.parentElement) {
      if (p.querySelectorAll(FIELD).length !== 1) break;
      const l = p.querySelector('label, legend, .form-label, .col-form-label, .input-group-text');
      if (l && !l.contains(el)) { out.push(l.innerText); break; }
    }
    return out.map(normLabel).filter(Boolean);
  }

  function findField(target, accept) {
    const nq = normLabel(target);
    if (!nq) return null;
    for (const sc of scopes()) {
      const fields = Array.from(sc.querySelectorAll(FIELD)).filter(f => visible(f) && !f.disabled && (!accept || accept(f)));
      const exact = fields.find(f => fieldLabels(f).some(l => l === nq));
      if (exact) return exact;
      let best = null, bestLen = Infinity;
      for (const f of fields) {
        for (const l of fieldLabels(f)) {
          if ((l.includes(nq) || (l.length >= 2 && nq.includes(l))) && l.length < bestLen) { best = f; bestLen = l.length; }
        }
      }
      if (best) return best;
    }
    return null;
  }

  const fire = (el, type) => el.dispatchEvent(new Event(type, { bubbles: true }));
  // Gán qua setter GỐC của prototype: framework nào ghi đè value trên instance vẫn nhận được sự kiện input.
  function setNativeValue(el, value) {
    const proto = el.tagName === 'TEXTAREA' ? HTMLTextAreaElement.prototype : HTMLInputElement.prototype;
    const desc = Object.getOwnPropertyDescriptor(proto, 'value');
    if (desc && desc.set) desc.set.call(el, value); else el.value = value;
  }

  function chooseOption(sel, value, target) {
    const nv = normLabel(value);
    const opts = Array.from(sel.options);
    const opt = opts.find(o => normLabel(o.text) === nv || normLabel(o.value) === nv)
             || opts.find(o => normLabel(o.text).includes(nv));
    if (!opt) return out('unresolved', target, 'ô chọn không có lựa chọn «' + value + '»');
    sel.value = opt.value;
    fire(sel, 'input'); fire(sel, 'change');
    return out('ok', target);
  }

  const checkable = f => f.type === 'checkbox' || f.type === 'radio';
  function toggleOn(input, label) {
    if (!input.checked) input.click();
    return out(input.checked ? 'ok' : 'unresolved', label, input.checked ? '' : 'không tích được');
  }

  async function fill(target, value) {
    const field = findField(target, f => !checkable(f));
    if (!field) return out('unresolved', target, 'không tìm thấy ô nhập mang nhãn này');
    if (field.tagName === 'SELECT') return chooseOption(field, value, target);
    field.focus && field.focus();
    if (field.isContentEditable) { field.textContent = value; fire(field, 'input'); return out('ok', target); }
    setNativeValue(field, value);
    fire(field, 'input'); fire(field, 'change');
    // Ô không nhận giá trị (ô ngày nhận chữ "hôm nay", ô số nhận chữ) ⇒ không làm được, KHÔNG phải lỗi POC.
    if (String(field.value) !== String(value)) return out('unresolved', target, 'ô không nhận giá trị «' + value + '»');
    return out('ok', target);
  }

  async function select(target, value) {
    if (!value) {
      // Hộp tích đơn: target là nhãn của chính nó.
      const box = findField(target, checkable);
      return box ? toggleOn(box, target) : out('unresolved', target, 'không tìm thấy lựa chọn mang nhãn này');
    }
    const sel = findField(target, f => f.tagName === 'SELECT');
    if (sel) return chooseOption(sel, value, target);
    // Nhóm radio/checkbox: lựa chọn mang nhãn value — ưu tiên cái nằm trong khối có chữ target.
    const nt = normLabel(target);
    const options = [];
    for (const sc of scopes()) {
      Array.from(sc.querySelectorAll('input[type=radio], input[type=checkbox]'))
        .filter(f => !f.disabled && fieldLabels(f).some(l => l === normLabel(value)))
        .forEach(f => options.push(f));
      if (options.length) break;
    }
    const inGroup = options.find(f => { const g = f.closest('fieldset, .form-group, .mb-3, .row, div'); return g && norm(g.innerText).includes(nt); });
    if (inGroup || options[0]) return toggleOn(inGroup || options[0], value);
    // Dropdown tự dựng (không phải <select>): bấm mở theo nhãn rồi bấm lựa chọn.
    const opener = findControl(target);
    if (opener) {
      opener.click();
      await sleep(250);
      const choice = findControl(value);
      if (choice) { const before = snapshot(); choice.click(); await settle(before); return out('ok', value); }
    }
    return out('unresolved', target, 'không tìm thấy ô chọn mang nhãn này');
  }

  // ── Đổi vai, mở màn, bấm, nhìn ────────────────────────────────────────────────────────────────────
  async function role(target) {
    const items = Array.from(document.querySelectorAll('.view-as-item'));
    if (!items.length) return out('unresolved', target, 'POC không có khối VIEW AS');
    const nq = normLabel(target);
    let item = items.find(i => labelOf(i) === nq);
    if (!item) {
      const near = items.filter(i => { const l = labelOf(i); return l.length >= 2 && (l.includes(nq) || nq.includes(l)); });
      if (near.length === 1) item = near[0];
      else if (near.length > 1) return out('unresolved', target, 'nhiều vai gần giống tên này');
    }
    if (!item) return out('missing', target, 'không có vai này ở VIEW AS');
    // Vai đang chọn sẵn (vai demo mở lên): bấm lại là no-op ĐÚNG, không phải nút chết.
    if (item.classList.contains('active')) return out('ok', target, 'vai đang chọn sẵn');
    const before = snapshot();
    const roleBefore = roleNow();
    item.click();
    // Bằng chứng đổi vai là VAI đã đổi, không phải chữ trên màn: hai vai có thể cùng mở một màn hình.
    const changed = await settle(before, () => roleBefore !== null && roleNow() !== roleBefore);
    return out(changed ? 'ok' : 'dead', target);
  }

  async function open(target) {
    const nq = normLabel(target);
    const leaf = navLeaves().find(el => labelOf(el) === nq);
    if (leaf) {
      if (norm(currentView()) === nq) return out('ok', target, 'màn đang mở sẵn');
      const before = snapshot();
      leaf.click();
      await settle(before, () => norm(currentView()) === nq);
      return out(norm(currentView()) === nq ? 'ok' : 'dead', target);
    }
    const section = Array.from(document.querySelectorAll('section.page-view')).find(s => norm(s.dataset.view) === nq);
    if (!section) return out('missing', target, 'POC không có màn hình này');
    // Màn không có trên menu (của vai đang chọn): mở thẳng để còn đi tiếp, nhưng ngữ cảnh (bản ghi đang chọn…)
    // có thể khác khi người dùng tự đi tới — phía C# coi từ đây là không kết luận được.
    try { if (typeof window.pocNavigate === 'function') window.pocNavigate(section.dataset.view); } catch (e) { }
    await sleep(150);
    return out(norm(currentView()) === nq ? 'teleport' : 'unresolved', target, 'màn không có trên menu nên máy mở thẳng');
  }

  async function click(target, anchor) {
    let el = anchoredControl(anchor, target) || findControl(target);
    let guessed = false;
    if (!el) { el = rowFallback(target); guessed = !!el; }
    if (!el) {
      return pageText().includes(normLabel(target))
        ? out('unresolved', target, 'chữ này có trên trang nhưng không thuộc thứ gì bấm được')
        : out('missing', target);
    }
    const stateful = el.closest ? el.closest('.view-as-item, .sidebar-nav .nav-item') : null;
    if (stateful && stateful.classList.contains('active')) return out('ok', target, 'đang chọn sẵn');
    const before = snapshot();
    const roleBefore = roleNow();
    try { el.click(); } catch (e) { return out('unresolved', target, 'bấm lỗi'); }
    const changed = await settle(before, () => !!stateful && stateful.classList.contains('view-as-item') && roleNow() !== roleBefore);
    if (changed) return out('ok', target);
    if (el.getAttribute && el.getAttribute('data-bs-toggle') === 'modal' && !window.bootstrap)
      return out('nobootstrap', target, 'modal cần Bootstrap từ CDN mà môi trường không tải được');
    return guessed
      ? out('unresolved', target, 'đã bấm vào chỗ chứa chữ này nhưng màn không đổi')
      : out('dead', target);
  }

  function check(target) {
    return out(focusText().includes(normLabel(target)) ? 'seen' : 'notseen', target);
  }

  async function act(action, anchor) {
    try {
      const kind = String(action && action.kind || '');
      const target = String(action && action.target || '');
      const value = String(action && action.value || '');
      switch (kind) {
        case 'role': return await role(target);
        case 'open': return await open(target);
        case 'click': return await click(target, anchor);
        case 'fill': return await fill(target, value);
        case 'select': return await select(target, value);
        case 'check': return check(target);
        default: return out('unresolved', target, 'loại thao tác lạ: ' + kind);
      }
    } catch (e) {
      return out('unresolved', '', 'lỗi của bộ lái: ' + (e && e.message ? e.message : e));
    }
  }

  // ── Đường lùi: bộ kịch bản cũ chỉ có câu chữ ──────────────────────────────────────────────────────
  // Đoán điều khiển từ câu: nhãn trong ngoặc ("…", '…', “…”, ‘…’, «…») trước, rồi khớp NGƯỢC (nhãn nút
  // nào xuất hiện nguyên văn trong câu). Trả thêm số nhãn trong ngoặc để phía C# biết câu có đơn nghĩa không:
  // một câu nêu nhiều nhãn ("Nhập Name là 'X', để Status là 'Y', bấm Save") thường là DỮ LIỆU + thao tác,
  // máy chỉ bấm được một thứ nên không được kết luận gì về phần còn lại.
  async function legacy(step, anchor) {
    try {
      const stepText = norm(step);
      const quoted = [];
      const re = /["'“”‘’«»]([^"'“”‘’«»]{2,60})["'“”‘’«»]/g;
      let m;
      while ((m = re.exec(step)) !== null) quoted.push(m[1].trim());
      const res = (status, label, note) => JSON.stringify({ status, label: label || '', note: note || '', view: currentView(), quoted: quoted.length });

      let target = null, label = '';
      if (anchor) {
        for (const q of quoted.concat([''])) { target = anchoredControl(anchor, q); if (target) { label = labelOf(target) || anchor; break; } }
      }
      let quotedTried = 0;
      if (!target) for (const q of quoted) {
        const nq = normLabel(q);
        if (nq.length < 2) continue;
        quotedTried++;
        target = findControl(nq);
        label = q;
        if (target) break;
        if (!pageText().includes(nq)) return res('missing', q);
      }
      // Bước ĐÃ nêu nhãn trong ngoặc, nhãn đó có trên trang nhưng không phải điều khiển ⇒ bước đang nói về
      // DỮ LIỆU. Rơi xuống khớp ngược ở đây là cách cổng này từng bấm bừa một nút khác chỉ vì nhãn nó là
      // khúc con của câu bước.
      if (!target && quotedTried > 0) return res('unresolved', '', 'nhãn có trên trang nhưng không phải điều khiển');
      if (!target) {
        const controls = Array.from(document.querySelectorAll(CLICKABLE)).filter(visible).concat(navLeaves());
        let best = null, bestLen = 0;
        for (const el of controls) {
          const t = labelOf(el);
          if (t.length >= 3 && t.length > bestLen && stepText.includes(t)) { best = el; bestLen = t.length; }
        }
        if (!best) return res('unresolved', '', 'không đoán được điều khiển nào từ câu bước');
        target = best;
        label = labelOf(best);
      }
      const stateful = target.closest ? target.closest('.view-as-item, .sidebar-nav .nav-item') : null;
      if (stateful && stateful.classList.contains('active')) return res('ok', label, 'đang chọn sẵn');
      const before = snapshot();
      const roleBefore = roleNow();
      try { target.click(); } catch (e) { return res('unresolved', label, 'bấm lỗi'); }
      const changed = await settle(before, () => !!stateful && stateful.classList.contains('view-as-item') && roleNow() !== roleBefore);
      if (changed) return res('ok', label);
      if (target.getAttribute && target.getAttribute('data-bs-toggle') === 'modal' && !window.bootstrap)
        return res('nobootstrap', label);
      return res('dead', label);
    } catch (e) {
      return JSON.stringify({ status: 'unresolved', label: '', note: 'lỗi của bộ lái: ' + (e && e.message ? e.message : e), view: currentView(), quoted: 0 });
    }
  }

  window.__pocUat = { act, legacy };
})();
