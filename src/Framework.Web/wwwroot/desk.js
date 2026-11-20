// Global desk helpers for keyboard shortcuts and interactions in FrogFrame
window.frogframe = window.frogframe || {};

(function () {
    var globalDotNetHelper = null;

    // Apply saved theme preference as early as possible
    try {
        var savedTheme = localStorage.getItem('frogframe-theme');
        if (savedTheme && savedTheme !== 'blue') {
            document.documentElement.setAttribute('data-theme', savedTheme);
        }
    } catch (e) {}

    window.frogframe.initGlobalShortcuts = function (dotNetHelper) {
        globalDotNetHelper = dotNetHelper;

        if (window.frogframe._shortcutHandler) {
            window.removeEventListener('keydown', window.frogframe._shortcutHandler);
        }

        window.frogframe._shortcutHandler = function (e) {
            var isCtrlK = (e.ctrlKey || e.metaKey) && (e.key === 'k' || e.key === 'K');
            var isAltQ = e.altKey && (e.key === 'q' || e.key === 'Q');

            if (isCtrlK || isAltQ) {
                e.preventDefault();
                e.stopPropagation();
                if (globalDotNetHelper) {
                    globalDotNetHelper.invokeMethodAsync('OpenAwesomebarFromJs');
                }
            }
        };

        window.addEventListener('keydown', window.frogframe._shortcutHandler);
    };

    window.frogframe.focusInput = function (selector) {
        setTimeout(function () {
            var el = document.querySelector(selector);
            if (el) {
                el.focus();
                if (typeof el.select === 'function') {
                    el.select();
                }
            }
        }, 50);
    };

    window.frogframe.scrollToBottom = function (element) {
        if (element) element.scrollTop = element.scrollHeight;
    };

    window.frogframe.setLanguage = function (code) {
        if (!code) return;
        document.documentElement.lang = code;
        fetch('/ui/language/' + encodeURIComponent(code), {
            method: 'POST',
            credentials: 'same-origin'
        }).catch(function () {});
    };

    window.frogframe.toggleTheme = function () {
        var toggleBtn = document.querySelector('.theme-toggle button');
        if (toggleBtn) {
            toggleBtn.click();
        } else {
            var html = document.documentElement;
            var currentBb = {};
            try { currentBb = JSON.parse(localStorage.getItem('bb-theme') || '{}') || {}; } catch(e){}
            if (html.classList.contains('dark')) {
                html.classList.remove('dark');
                currentBb.isDarkMode = false;
                try { localStorage.setItem('bb-theme', JSON.stringify(currentBb)); } catch(e){}
            } else {
                html.classList.add('dark');
                currentBb.isDarkMode = true;
                try { localStorage.setItem('bb-theme', JSON.stringify(currentBb)); } catch(e){}
            }
        }
    };

    window.frogframe.setTheme = function (themeName) {
        var html = document.documentElement;
        if (themeName && themeName !== 'blue') {
            html.setAttribute('data-theme', themeName);
            try { localStorage.setItem('frogframe-theme', themeName); } catch (e) {}
        } else {
            html.removeAttribute('data-theme');
            try { localStorage.setItem('frogframe-theme', 'blue'); } catch (e) {}
        }
    };

    window.frogframe.getTheme = function () {
        return document.documentElement.getAttribute('data-theme') || 'blue';
    };

    window.frogframe.downloadUrl = function (url, fileName) {
        if (!url) return;
        var a = document.createElement('a');
        a.href = url;
        if (fileName) a.download = fileName;
        a.style.display = 'none';
        document.body.appendChild(a);
        a.click();
        setTimeout(function () {
            if (a.parentNode) a.parentNode.removeChild(a);
        }, 500);
    };

    // Fix BbDataGrid column resize:
    // 1. Pre-measure all header cells in a single read pass and lock table.style.width
    //    in the capture phase BEFORE table-columns.js mutates <col> widths one by one.
    // 2. Keep table.style.width synced with the sum of <col> pixel widths when all columns
    //    have explicit pixel widths so table-fixed + w-full never rescales other columns.
    var isDraggingColumn = false;

    function syncDataGridTableWidth(table) {
        if (isDraggingColumn || !table || !table.classList.contains('bb:table-fixed')) return;
        var cols = Array.from(table.querySelectorAll('colgroup col[data-column-id]'));
        if (cols.length === 0) return;

        var hasAnyPx = cols.some(function (c) {
            return (c.style.width || '').trim().endsWith('px');
        });
        if (!hasAnyPx) return;

        var rootFontSize = parseFloat(getComputedStyle(document.documentElement).fontSize) || 16;
        var totalPx = 0;

        for (var i = 0; i < cols.length; i++) {
            var col = cols[i];
            var w = (col.style.width || '').trim();
            var pxVal = 0;
            if (w.endsWith('px')) {
                pxVal = parseFloat(w);
            } else if (w.endsWith('rem')) {
                pxVal = Math.round(parseFloat(w) * rootFontSize);
                col.style.width = pxVal + 'px';
            } else {
                var colId = col.getAttribute('data-column-id');
                var th = colId ? table.querySelector('thead th[data-column-id="' + CSS.escape(colId) + '"]') : null;
                pxVal = th ? Math.round(th.getBoundingClientRect().width) : 150;
                if (pxVal > 0) {
                    col.style.width = pxVal + 'px';
                }
            }
            if (!isNaN(pxVal) && pxVal > 0) {
                totalPx += pxVal;
            }
        }

        if (totalPx > 0) {
            var targetWidth = Math.round(totalPx) + 'px';
            if (table.style.width !== targetWidth) {
                table.style.width = targetWidth;
            }
        }
    }

    function syncAllDataGridTables() {
        if (isDraggingColumn) return;
        var tables = document.querySelectorAll('table[role="grid"]');
        for (var i = 0; i < tables.length; i++) {
            syncDataGridTableWidth(tables[i]);
        }
    }

    if (!window.frogframe._resizeCaptureInstalled) {
        window.frogframe._resizeCaptureInstalled = true;

        document.addEventListener('pointerdown', function (e) {
            var handle = e.target && e.target.closest ? e.target.closest('[data-resize-handle]') : null;
            if (!handle) return;
            var table = handle.closest('table[role="grid"]');
            if (!table) return;

            isDraggingColumn = true;

            // Keep the header highlighted while resizing so both column edges stay visible.
            var activeTh = handle.closest('th');
            if (activeTh) activeTh.classList.add('ff-col-resizing');

            var ths = Array.from(table.querySelectorAll('thead th[data-column-id]'));
            if (ths.length === 0) return;

            // Read pass: measure all columns before any style mutation
            var measurements = ths.map(function (th) {
                return {
                    id: th.getAttribute('data-column-id'),
                    width: Math.round(th.getBoundingClientRect().width)
                };
            });

            var totalWidth = measurements.reduce(function (sum, m) { return sum + m.width; }, 0)
                || Math.round(table.getBoundingClientRect().width);

            // Lock table width first so setting <col> widths never triggers proportional rescaling
            table.style.width = totalWidth + 'px';

            // Write pass: lock every <col> to its exact measured pixel width
            measurements.forEach(function (m) {
                if (!m.id) return;
                var col = table.querySelector('colgroup col[data-column-id="' + CSS.escape(m.id) + '"]');
                if (col) {
                    col.style.width = m.width + 'px';
                }
            });

            var onEnd = function () {
                isDraggingColumn = false;
                if (activeTh) activeTh.classList.remove('ff-col-resizing');
                window.removeEventListener('pointerup', onEnd, true);
                window.removeEventListener('pointercancel', onEnd, true);
                setTimeout(syncAllDataGridTables, 0);
            };
            window.addEventListener('pointerup', onEnd, true);
            window.addEventListener('pointercancel', onEnd, true);
        }, true);

        // Double-click on a column's resize handle: fit the column to its widest header or cell content.
        // The new width goes through the regular resize drag of table-columns.js, so it is saved like a
        // manual resize.
        document.addEventListener('dblclick', function (e) {
            var handle = e.target && e.target.closest ? e.target.closest('[data-resize-handle]') : null;
            if (!handle) return;
            var table = handle.closest('table[role="grid"]');
            var th = handle.closest('th[data-column-id]');
            if (!table || !th) return;
            e.preventDefault();
            e.stopPropagation();

            var headerCells = Array.from(th.parentElement.children);
            var index = headerCells.indexOf(th);
            if (index < 0) return;

            // A cell's own width says nothing about its content once the column is wider or narrower,
            // so each cell's content is laid out again in a hidden, unconstrained box with the same font
            // and padding.
            var probe = document.createElement('div');
            probe.setAttribute('aria-hidden', 'true');
            probe.style.cssText = 'position:absolute;left:-10000px;top:0;visibility:hidden;white-space:nowrap;width:auto;max-width:none;display:inline-block';
            document.body.appendChild(probe);
            var widest = 0;
            var measure = function (cell) {
                if (!cell) return;
                var cs = getComputedStyle(cell);
                probe.style.font = cs.font;
                probe.style.letterSpacing = cs.letterSpacing;
                probe.style.paddingLeft = cs.paddingLeft;
                probe.style.paddingRight = cs.paddingRight;
                probe.style.borderLeft = cs.borderLeftWidth + ' solid transparent';
                probe.style.borderRight = cs.borderRightWidth + ' solid transparent';
                probe.innerHTML = cell.innerHTML;
                // The resize handle is absolutely positioned and takes no room.
                probe.querySelectorAll('[data-resize-handle]').forEach(function (h) { h.remove(); });
                widest = Math.max(widest, Math.ceil(probe.getBoundingClientRect().width));
            };
            measure(th);
            table.querySelectorAll('tbody tr').forEach(function (tr) {
                // Rows with merged cells (group headers, detail rows) do not line up with the columns.
                if (tr.children.length === headerCells.length) measure(tr.children[index]);
            });
            probe.remove();
            var current = Math.round(th.getBoundingClientRect().width);
            if (widest <= 0) return;

            var target = Math.min(Math.max(widest + 2, 50), 640);
            var delta = target - current;
            if (Math.abs(delta) < 1) return;

            var box = handle.getBoundingClientRect();
            var x = box.left + box.width / 2;
            var y = box.top + box.height / 2;
            var init = { bubbles: true, cancelable: true, pointerId: 9001, pointerType: 'mouse', isPrimary: true, button: 0, buttons: 1, clientY: y };
            handle.dispatchEvent(new PointerEvent('pointerdown', Object.assign({}, init, { clientX: x })));
            document.dispatchEvent(new PointerEvent('pointermove', Object.assign({}, init, { clientX: x + delta })));
            handle.dispatchEvent(new PointerEvent('pointerup', Object.assign({}, init, { clientX: x + delta, buttons: 0 })));
        }, true);

        var observer = new MutationObserver(function () {
            syncAllDataGridTables();
        });

        if (document.body) {
            observer.observe(document.body, { childList: true, subtree: true, attributes: true, attributeFilter: ['style'] });
        } else {
            document.addEventListener('DOMContentLoaded', function () {
                observer.observe(document.body, { childList: true, subtree: true, attributes: true, attributeFilter: ['style'] });
                syncAllDataGridTables();
            });
        }
    }
})();
