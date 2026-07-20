/* Unified dropdown — progressive enhancement for native <select> elements.
 *
 * Every native <select> on the page (unless opted out) is upgraded into the
 * same custom combo used by the multi-select filters (.ms-combo), so all
 * dropdowns in the app share ONE look: a grey trigger with an inline caption
 * (the field label) over the selected value, a chevron that flips when open,
 * and a panel that shows a search box for longer lists. The native <select>
 * stays in the DOM (visually hidden) as the source of truth, so form
 * submission, `onchange="this.form.submit()"`, server binding and any JS that
 * reads `.value` / sets options keep working unchanged.
 *
 * Opt out with `data-no-enhance` on the <select>. Multiple-selects and
 * size>1 list boxes are left alone.
 */
(function () {
    'use strict';

    var SEARCH_THRESHOLD = 7; // show the search box once a list has this many options
    var uid = 0;

    // Inline SVG glyphs so the core dropdown affordances render even if the
    // icon font (bootstrap-icons CDN) is unavailable.
    var SVG_CARET = '<svg width="14" height="14" viewBox="0 0 16 16" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true"><path d="M4 6l4 4 4-4"/></svg>';
    var SVG_SEARCH = '<svg width="15" height="15" viewBox="0 0 16 16" fill="none" stroke="currentColor" stroke-width="1.6" stroke-linecap="round" aria-hidden="true"><circle cx="7" cy="7" r="4.5"/><line x1="10.5" y1="10.5" x2="14.5" y2="14.5"/></svg>';
    var SVG_CLEAR = '<svg width="13" height="13" viewBox="0 0 16 16" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" aria-hidden="true"><line x1="4" y1="4" x2="12" y2="12"/><line x1="12" y1="4" x2="4" y2="12"/></svg>';

    function enhanced(select) {
        return select.dataset.csEnhanced === '1';
    }

    function shouldEnhance(select) {
        if (enhanced(select)) return false;
        if (select.multiple) return false;
        if (select.size && select.size > 1) return false;
        if (select.hasAttribute('data-no-enhance')) return false;
        if (select.closest('[data-ms-combo]')) return false; // hand-authored combo
        return true;
    }

    // Resolve the field label for the inline caption. Prefer an explicit
    // data-caption, then aria-label, then a <label for=id>, then a <label>
    // sibling/ancestor within the field wrapper. The DOM <label> we borrow the
    // text from is hidden so it is not shown twice.
    function resolveCaption(select) {
        if (select.dataset.caption) return { text: select.dataset.caption, node: null };

        var labelNode = null;
        if (select.id) {
            labelNode = document.querySelector('label[for="' + cssEscape(select.id) + '"]');
        }
        if (!labelNode) {
            var prev = select.previousElementSibling;
            if (prev && prev.tagName === 'LABEL') labelNode = prev;
        }
        if (!labelNode) {
            // Tight field wrappers only, and only a direct-child <label>, so we
            // never borrow an unrelated label from a large surrounding container.
            var wrap = select.closest('.filter-field, .filter-group, .shell-field, .logs-filter, .form-field, .field');
            if (wrap) {
                var candidate = wrap.querySelector(':scope > label');
                if (candidate && (!candidate.htmlFor || candidate.htmlFor === select.id)) {
                    labelNode = candidate;
                }
            }
        }
        if (labelNode) {
            var text = (labelNode.textContent || '').trim().replace(/\s*\*\s*$/, '');
            if (text) return { text: text, node: labelNode };
        }
        if (select.getAttribute('aria-label')) {
            return { text: select.getAttribute('aria-label').trim(), node: null };
        }
        return { text: '', node: null };
    }

    function cssEscape(s) {
        if (window.CSS && CSS.escape) return CSS.escape(s);
        return s.replace(/[^a-zA-Z0-9_-]/g, '\\$&');
    }

    function el(tag, cls, html) {
        var node = document.createElement(tag);
        if (cls) node.className = cls;
        if (html != null) node.innerHTML = html;
        return node;
    }

    function enhance(select) {
        select.dataset.csEnhanced = '1';
        var id = 'cs' + (++uid);

        var caption = resolveCaption(select);
        if (caption.node) caption.node.classList.add('cs-enhanced-hidden');

        var combo = el('div', 'ms-combo ms-combo--single');
        if (caption.text) combo.classList.add('ms-combo--has-caption');

        // Trigger
        var trigger = el('button', 'ms-combo-trigger');
        trigger.type = 'button';
        trigger.setAttribute('aria-haspopup', 'listbox');
        trigger.setAttribute('aria-expanded', 'false');
        var main = el('span', 'ms-combo-trigger-main');
        var captionEl = el('span', 'ms-combo-caption');
        captionEl.textContent = caption.text;
        var textEl = el('span', 'ms-combo-text');
        main.appendChild(captionEl);
        main.appendChild(textEl);
        trigger.appendChild(main);
        trigger.appendChild(el('span', 'ms-combo-caret', SVG_CARET));

        // Panel
        var panel = el('div', 'ms-combo-panel hidden');
        panel.setAttribute('role', 'listbox');
        var searchWrap = el('div', 'ms-combo-search');
        searchWrap.innerHTML =
            '<div class="ms-combo-search-box">' +
                '<input type="text" placeholder="Search" autocomplete="off" />' +
                '<button type="button" class="ms-combo-search-clear hidden" aria-label="Clear">' + SVG_CLEAR + '</button>' +
                '<span class="ms-combo-search-icon">' + SVG_SEARCH + '</span>' +
            '</div>';
        var search = searchWrap.querySelector('input');
        var clearBtn = searchWrap.querySelector('.ms-combo-search-clear');
        var list = el('ul', 'ms-combo-list');
        var empty = el('div', 'ms-combo-empty hidden');
        empty.textContent = 'Không có kết quả';
        panel.appendChild(searchWrap);
        panel.appendChild(list);
        panel.appendChild(empty);

        combo.appendChild(trigger);
        combo.appendChild(panel);

        // Place the combo right after the (now hidden) native select and move
        // the select inside the combo so relative lookups still resolve.
        // The native <select> stays as the source of truth but is made
        // invisible. We use an opacity/overlay technique (not display:none) so
        // that `required` selects remain focusable — otherwise the browser's
        // constraint validation throws "not focusable" and silently blocks the
        // form submit. pointer-events:none lets clicks fall through to the trigger.
        select.classList.add('cs-enhanced-select');
        select.setAttribute('tabindex', '-1');
        select.parentNode.insertBefore(combo, select);
        combo.appendChild(select);

        var optionRows = [];

        function buildList() {
            list.innerHTML = '';
            optionRows = [];
            Array.prototype.forEach.call(select.options, function (opt, i) {
                var li = el('li', 'ms-combo-option');
                li.setAttribute('role', 'option');
                var label = el('label', 'ms-combo-checkbox');
                label.appendChild(el('span', 'ms-combo-box'));
                var t = el('span', 'ms-combo-option-text');
                t.textContent = opt.textContent.trim();
                label.appendChild(t);
                li.appendChild(label);
                li.dataset.index = String(i);
                li.dataset.search = opt.textContent.trim().toLowerCase();
                if (opt.disabled) li.classList.add('cs-disabled');
                li.addEventListener('click', function () {
                    if (opt.disabled) return;
                    selectIndex(i);
                    close();
                    trigger.focus();
                });
                list.appendChild(li);
                optionRows.push(li);
            });
            syncSelectedRow();
            applySearchVisibility();
        }

        function applySearchVisibility() {
            searchWrap.style.display = select.options.length >= SEARCH_THRESHOLD ? '' : 'none';
        }

        function renderTrigger() {
            var opt = select.options[select.selectedIndex];
            var text = opt ? opt.textContent.trim() : '';
            var placeholder = !opt || opt.value === '';
            textEl.textContent = text || 'Select';
            textEl.classList.toggle('is-placeholder', placeholder || !text);
        }

        function syncSelectedRow() {
            optionRows.forEach(function (li) {
                var on = Number(li.dataset.index) === select.selectedIndex;
                li.classList.toggle('selected', on);
                li.setAttribute('aria-selected', on ? 'true' : 'false');
            });
        }

        function selectIndex(i) {
            if (select.selectedIndex === i) return;
            select.selectedIndex = i;
            renderTrigger();
            syncSelectedRow();
            select.dispatchEvent(new Event('change', { bubbles: true }));
        }

        function filter() {
            var q = search.value.trim().toLowerCase();
            clearBtn.classList.toggle('hidden', q === '');
            var visible = 0;
            optionRows.forEach(function (li) {
                var match = q === '' || li.dataset.search.indexOf(q) !== -1;
                li.classList.toggle('hidden', !match);
                if (match) visible++;
            });
            empty.classList.toggle('hidden', visible > 0);
        }

        function open() {
            closeAll();
            combo.classList.add('open');
            panel.classList.remove('hidden');
            trigger.setAttribute('aria-expanded', 'true');
            search.value = '';
            filter();
            if (searchWrap.style.display !== 'none') {
                search.focus();
            }
            // bring the selected row into view
            var sel = list.querySelector('.ms-combo-option.selected');
            if (sel) sel.scrollIntoView({ block: 'nearest' });
        }

        function close() {
            combo.classList.remove('open');
            panel.classList.add('hidden');
            trigger.setAttribute('aria-expanded', 'false');
        }

        trigger.addEventListener('click', function () {
            if (combo.classList.contains('open')) close(); else open();
        });
        // Enter/Space activation is left to the button's native click (which
        // toggles open/close); handling them here too would double-toggle.
        trigger.addEventListener('keydown', function (e) {
            if (e.key === 'ArrowDown') {
                e.preventDefault();
                if (!combo.classList.contains('open')) open();
            } else if (e.key === 'Escape') {
                close();
            }
        });

        search.addEventListener('input', filter);
        search.addEventListener('keydown', function (e) {
            if (e.key === 'Escape') { close(); trigger.focus(); }
            else if (e.key === 'Enter') {
                e.preventDefault();
                var first = optionRows.filter(function (li) { return !li.classList.contains('hidden'); })[0];
                if (first) { selectIndex(Number(first.dataset.index)); close(); trigger.focus(); }
            }
        });
        clearBtn.addEventListener('click', function () {
            search.value = '';
            filter();
            search.focus();
        });

        combo._csClose = close;

        // Re-sync when the option list or the value changes programmatically.
        var mo = new MutationObserver(function () { buildList(); renderTrigger(); });
        mo.observe(select, { childList: true, subtree: true });

        select.addEventListener('change', function () { renderTrigger(); syncSelectedRow(); });

        // Intercept direct `select.value = x` assignments so the trigger stays
        // in sync even when code sets the value without dispatching change.
        try {
            var desc = Object.getOwnPropertyDescriptor(HTMLSelectElement.prototype, 'value');
            if (desc && desc.set) {
                Object.defineProperty(select, 'value', {
                    configurable: true,
                    get: function () { return desc.get.call(this); },
                    set: function (v) { desc.set.call(this, v); renderTrigger(); syncSelectedRow(); }
                });
            }
        } catch (_) { /* non-fatal: change/mutation handlers still cover most cases */ }

        buildList();
        renderTrigger();
    }

    function closeAll() {
        document.querySelectorAll('.ms-combo.ms-combo--single.open').forEach(function (c) {
            if (c._csClose) c._csClose();
        });
    }

    document.addEventListener('click', function (e) {
        document.querySelectorAll('.ms-combo.ms-combo--single.open').forEach(function (c) {
            if (!c.contains(e.target) && c._csClose) c._csClose();
        });
    });

    function enhanceAll(root) {
        (root || document).querySelectorAll('select').forEach(function (select) {
            if (shouldEnhance(select)) enhance(select);
        });
    }

    // Expose for views that inject selects after load.
    window.CsDropdown = { enhanceAll: enhanceAll, enhance: function (s) { if (shouldEnhance(s)) enhance(s); } };

    if (document.readyState === 'loading') {
        document.addEventListener('DOMContentLoaded', function () { enhanceAll(); });
    } else {
        enhanceAll();
    }
})();
