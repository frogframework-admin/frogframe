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
                window.removeEventListener('pointerup', onEnd, true);
                window.removeEventListener('pointercancel', onEnd, true);
                setTimeout(syncAllDataGridTables, 0);
            };
            window.addEventListener('pointerup', onEnd, true);
            window.addEventListener('pointercancel', onEnd, true);
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
