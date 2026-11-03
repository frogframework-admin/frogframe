// Global desk helpers for keyboard shortcuts and interactions in DocFrame
window.docframe = window.docframe || {};

(function () {
    var globalDotNetHelper = null;

    window.docframe.initGlobalShortcuts = function (dotNetHelper) {
        globalDotNetHelper = dotNetHelper;

        if (window.docframe._shortcutHandler) {
            window.removeEventListener('keydown', window.docframe._shortcutHandler);
        }

        window.docframe._shortcutHandler = function (e) {
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

        window.addEventListener('keydown', window.docframe._shortcutHandler);
    };

    window.docframe.focusInput = function (selector) {
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

    window.docframe.toggleTheme = function () {
        var toggleBtn = document.querySelector('.theme-toggle button');
        if (toggleBtn) {
            toggleBtn.click();
        } else {
            var html = document.documentElement;
            if (html.classList.contains('dark')) {
                html.classList.remove('dark');
                try { localStorage.setItem('bb-theme', 'light'); } catch(e){}
            } else {
                html.classList.add('dark');
                try { localStorage.setItem('bb-theme', 'dark'); } catch(e){}
            }
        }
    };
})();
