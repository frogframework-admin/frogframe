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

    window.frogframe.scrollToTop = function (element, top) {
        if (element) element.scrollTop = top || 0;
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
    var isSyncingTableWidth = false;
    var gridWidthObserver = null;

    function measureColumnFitWidth(table, th, minWidth, maxWidth) {
        if (!table || !th || !th.parentElement) return 0;
        var headerCells = Array.from(th.parentElement.children);
        var index = headerCells.indexOf(th);
        if (index < 0) return 0;

        // A cell's own width says nothing about its content once the column is constrained or 0px,
        // so each cell's content is laid out in a hidden, unconstrained box with the same font and padding.
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

        if (widest <= 0) return 0;
        return Math.min(Math.max(widest + 2, minWidth), maxWidth);
    }

    function syncDataGridTableWidth(table) {
        if (isDraggingColumn || !table || !table.classList.contains('bb:table-fixed')) return;
        var cols = Array.from(table.querySelectorAll('colgroup col[data-column-id]'));
        if (cols.length === 0) return;

        var hasAnyPx = cols.some(function (c) {
            var w = (c.style.width || '').trim();
            return w.endsWith('px') && parseFloat(w) > 0;
        });
        if (!hasAnyPx) {
            if (table.style.width) table.style.width = '';
            return;
        }

        var hadFixedTableWidth = !!(table.style.width && table.style.width.trim());
        var rootFontSize = parseFloat(getComputedStyle(document.documentElement).fontSize) || 16;
        var totalPx = 0;

        for (var i = 0; i < cols.length; i++) {
            var col = cols[i];
            var w = (col.style.width || '').trim();
            var pxVal = 0;
            if (w.endsWith('px') && parseFloat(w) > 0) {
                pxVal = parseFloat(w);
            } else if (w.endsWith('rem') && parseFloat(w) > 0) {
                pxVal = Math.round(parseFloat(w) * rootFontSize);
                if (pxVal > 0) {
                    col.style.width = pxVal + 'px';
                }
            } else {
                var colId = col.getAttribute('data-column-id');
                var th = colId ? table.querySelector('thead th[data-column-id="' + CSS.escape(colId) + '"]') : null;
                if (hadFixedTableWidth) {
                    // Fixed table width is already claimed by existing columns, so a newly shown column
                    // would measure as 0px via getBoundingClientRect(); measure its natural content width instead.
                    pxVal = (th && measureColumnFitWidth(table, th, 100, 420)) || 150;
                } else {
                    pxVal = th ? Math.round(th.getBoundingClientRect().width) : 0;
                    if (pxVal <= 0) {
                        pxVal = (th && measureColumnFitWidth(table, th, 100, 420)) || 150;
                    }
                }
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
        if (isDraggingColumn || isSyncingTableWidth) return;
        isSyncingTableWidth = true;
        try {
            var tables = document.querySelectorAll('table[role="grid"]');
            for (var i = 0; i < tables.length; i++) {
                syncDataGridTableWidth(tables[i]);
            }
        } finally {
            if (gridWidthObserver) gridWidthObserver.takeRecords();
            isSyncingTableWidth = false;
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
                var w = Math.round(th.getBoundingClientRect().width);
                if (w <= 0) {
                    w = measureColumnFitWidth(table, th, 100, 420) || 150;
                }
                return {
                    id: th.getAttribute('data-column-id'),
                    width: w
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

            var target = measureColumnFitWidth(table, th, 50, 640);
            if (target <= 0) return;

            var current = Math.round(th.getBoundingClientRect().width);
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

        gridWidthObserver = new MutationObserver(function () {
            syncAllDataGridTables();
        });

        if (document.body) {
            gridWidthObserver.observe(document.body, { childList: true, subtree: true, attributes: true, attributeFilter: ['style'] });
        } else {
            document.addEventListener('DOMContentLoaded', function () {
                gridWidthObserver.observe(document.body, { childList: true, subtree: true, attributes: true, attributeFilter: ['style'] });
                syncAllDataGridTables();
            });
        }
    }
})();

// Board (Kanban) view: Firefox only starts an HTML5 drag when dataTransfer carries data.
(function () {
    document.addEventListener('dragstart', function (e) {
        var card = e.target && e.target.closest ? e.target.closest('[data-kanban-card]') : null;
        if (!card || !e.dataTransfer) return;
        e.dataTransfer.effectAllowed = 'move';
        try { e.dataTransfer.setData('text/plain', card.getAttribute('data-kanban-card') || ''); } catch (_) { }
    }, true);
})();

// Tree view: arrow keys, Home and End move between tree items instead of scrolling the page.
(function () {
    document.addEventListener('keydown', function (e) {
        if (!e.target || !e.target.hasAttribute || !e.target.hasAttribute('data-tree-item')) return;
        if (['ArrowUp', 'ArrowDown', 'ArrowLeft', 'ArrowRight', 'Home', 'End'].indexOf(e.key) >= 0) e.preventDefault();
    }, true);
})();

// Visual workflow editor (WorkflowDiagram with Editable): drag states, draw transitions from the handle, double-click
// empty space to add a state. Moves happen here without server round trips; C# is called once the pointer is released.
(function () {
    var canvases = new WeakMap();

    function point(svg, e) {
        var p = svg.createSVGPoint();
        p.x = e.clientX; p.y = e.clientY;
        var m = svg.getScreenCTM();
        return m ? p.matrixTransform(m.inverse()) : { x: 0, y: 0 };
    }

    window.frogframe.wfCanvas = {
        init: function (svg, dotnet) {
            if (!svg || canvases.has(svg)) return;
            var state = null; // { kind: 'move'|'connect', name, start, node, line, moved, ox, oy }

            function down(e) {
                if (e.button !== 0) return;
                var handle = e.target.closest('[data-wf-connect]');
                var node = e.target.closest('[data-wf-state]');
                var p = point(svg, e);
                if (handle) {
                    var line = document.createElementNS('http://www.w3.org/2000/svg', 'path');
                    line.setAttribute('class', 'wf-connecting');
                    line.setAttribute('marker-end', 'url(#wf-arrow-sel)');
                    svg.appendChild(line);
                    state = { kind: 'connect', name: handle.getAttribute('data-wf-connect'), start: { x: +handle.getAttribute('cx'), y: +handle.getAttribute('cy') }, line: line };
                } else if (node) {
                    state = { kind: 'move', name: node.getAttribute('data-wf-state'), start: p, node: node, moved: false,
                              ox: +node.getAttribute('data-x'), oy: +node.getAttribute('data-y') };
                } else {
                    return;
                }
                svg.setPointerCapture(e.pointerId);
                e.preventDefault();
            }

            function move(e) {
                if (!state) return;
                var p = point(svg, e);
                if (state.kind === 'move') {
                    var dx = p.x - state.start.x, dy = p.y - state.start.y;
                    if (!state.moved && Math.abs(dx) + Math.abs(dy) < 4) return;
                    state.moved = true;
                    state.node.setAttribute('transform', 'translate(' + dx + ' ' + dy + ')');
                    state.node.classList.add('is-dragging');
                } else {
                    var s = state.start;
                    var mx = (s.x + p.x) / 2;
                    state.line.setAttribute('d', 'M ' + s.x + ' ' + s.y + ' C ' + mx + ' ' + s.y + ', ' + mx + ' ' + p.y + ', ' + p.x + ' ' + p.y);
                    svg.querySelectorAll('.wf-node.is-target').forEach(function (n) { n.classList.remove('is-target'); });
                    var over = document.elementFromPoint(e.clientX, e.clientY);
                    var target = over && over.closest ? over.closest('[data-wf-state]') : null;
                    if (target) target.classList.add('is-target');
                }
            }

            function up(e) {
                if (!state) return;
                var s = state; state = null;
                try { svg.releasePointerCapture(e.pointerId); } catch (_) { }
                if (s.kind === 'move') {
                    if (!s.moved) { dotnet.invokeMethodAsync('SelectState', s.name); return; }
                    var p = point(svg, e);
                    dotnet.invokeMethodAsync('MoveState', s.name, s.ox + p.x - s.start.x, s.oy + p.y - s.start.y)
                        .finally(function () { s.node.removeAttribute('transform'); s.node.classList.remove('is-dragging'); });
                } else {
                    s.line.remove();
                    svg.querySelectorAll('.wf-node.is-target').forEach(function (n) { n.classList.remove('is-target'); });
                    var over = document.elementFromPoint(e.clientX, e.clientY);
                    var target = over && over.closest ? over.closest('[data-wf-state]') : null;
                    if (target) dotnet.invokeMethodAsync('Connect', s.name, target.getAttribute('data-wf-state'));
                }
            }

            function dbl(e) {
                if (!e.target.hasAttribute || !e.target.hasAttribute('data-wf-bg')) return;
                var p = point(svg, e);
                dotnet.invokeMethodAsync('AddStateAt', p.x, p.y);
            }

            function cancel() {
                if (!state) return;
                if (state.line) state.line.remove();
                if (state.node) { state.node.removeAttribute('transform'); state.node.classList.remove('is-dragging'); }
                state = null;
            }

            svg.addEventListener('pointerdown', down);
            svg.addEventListener('pointermove', move);
            svg.addEventListener('pointerup', up);
            svg.addEventListener('pointercancel', cancel);
            svg.addEventListener('dblclick', dbl);
            canvases.set(svg, { down: down, move: move, up: up, cancel: cancel, dbl: dbl });
        },
        dispose: function (svg) {
            var h = svg && canvases.get(svg);
            if (!h) return;
            svg.removeEventListener('pointerdown', h.down);
            svg.removeEventListener('pointermove', h.move);
            svg.removeEventListener('pointerup', h.up);
            svg.removeEventListener('pointercancel', h.cancel);
            svg.removeEventListener('dblclick', h.dbl);
            canvases.delete(svg);
        }
    };
})();
