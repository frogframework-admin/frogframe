// Monaco-based C# editor for Server Scripts (used by ScriptCodeEditor.razor).
//
// Monaco is served from the BlazorMonaco NuGet package (static files only, under _content/BlazorMonaco) and is
// loaded lazily, the first time a script editor opens, so other pages do not pay for its ~3 MB of JavaScript.

const MONACO_BASE = new URL('_content/BlazorMonaco/lib/monaco-editor/min/vs', document.baseURI).href.replace(/\/$/, '');
const MARKER_OWNER = 'frogframe-script';

/** @type {Map<string, {editor:any, dotnet:any, fields:any[], timer:number, disposables:{dispose():void}[]}>} */
const editors = new Map();
let monacoPromise = null;
let providersRegistered = false;
let themeObserver = null;
let catalog = { globals: [], members: {}, snippets: [] };

function loadScript(src) {
    return new Promise((resolve, reject) => {
        const s = document.createElement('script');
        s.src = src;
        s.async = true;
        s.onload = () => resolve();
        s.onerror = () => reject(new Error('Failed to load ' + src));
        document.head.appendChild(s);
    });
}

// Monaco's AMD loader installs a global define() with define.amd set. UMD bundles loaded afterwards
// (ECharts on dashboards, Quill) would then register as anonymous AMD modules instead of exporting normally,
// which breaks them. Monaco's own loader never reads define.amd, so removing the marker is safe.
function hideAmdMarker() {
    if (typeof window.define === 'function' && window.define.amd) {
        window.__frogframeAmdMarker = window.define.amd;
        try { delete window.define.amd; } catch { window.define.amd = undefined; }
    }
}

function ensureMonaco() {
    if (window.monaco && window.monaco.editor) {
        hideAmdMarker();
        return Promise.resolve(window.monaco);
    }
    if (!monacoPromise) {
        monacoPromise = (async () => {
            if (typeof window.require !== 'function' || typeof window.require.config !== 'function') {
                await loadScript(MONACO_BASE + '/loader.js');
            }
            window.require.config({ paths: { vs: MONACO_BASE } });
            const monaco = await new Promise((resolve, reject) =>
                window.require(['vs/editor/editor.main'], () => resolve(window.monaco), reject));
            hideAmdMarker();
            return monaco;
        })().catch(err => {
            monacoPromise = null;
            throw err;
        });
    }
    return monacoPromise;
}

function isDark() {
    return document.documentElement.classList.contains('dark');
}

function watchTheme(monaco) {
    if (themeObserver) return;
    themeObserver = new MutationObserver(() => monaco.editor.setTheme(isDark() ? 'vs-dark' : 'vs'));
    themeObserver.observe(document.documentElement, { attributes: true, attributeFilter: ['class'] });
}

function stateForModel(model) {
    for (const s of editors.values()) {
        if (s.editor.getModel() === model) return s;
    }
    return null;
}

function kindOf(monaco, kind) {
    const K = monaco.languages.CompletionItemKind;
    switch (kind) {
        case 'method': return K.Method;
        case 'property': return K.Property;
        case 'field': return K.Field;
        case 'variable': return K.Variable;
        case 'snippet': return K.Snippet;
        default: return K.Text;
    }
}

function toSuggestion(monaco, item, range) {
    return {
        label: item.label,
        kind: kindOf(monaco, item.kind),
        detail: item.detail,
        documentation: item.documentation || undefined,
        insertText: item.insertText,
        insertTextRules: item.isSnippet ? monaco.languages.CompletionItemInsertTextRule.InsertAsSnippet : undefined,
        range
    };
}

// Field names after Doc["  /  OldDoc["  /  Doc.Data["  /  Doc.GetDecimal("  etc.
const FIELD_CONTEXT = /(?:\b(?:Doc|OldDoc)(?:\.Data)?\s*\[\s*|\.(?:GetString|GetDecimal|GetInt|GetBool|GetTable|ContainsKey)\s*\(\s*)"\w*$/;
// Member access: identifier followed by a dot and a partial word.
const MEMBER_CONTEXT = /\b([A-Za-z_]\w*)\s*\.\s*\w*$/;

function registerProviders(monaco) {
    if (providersRegistered) return;
    providersRegistered = true;

    monaco.languages.registerCompletionItemProvider('csharp', {
        triggerCharacters: ['.', '"'],
        provideCompletionItems(model, position) {
            const state = stateForModel(model);
            if (!state) return { suggestions: [] };

            const before = model.getValueInRange({
                startLineNumber: position.lineNumber, startColumn: 1,
                endLineNumber: position.lineNumber, endColumn: position.column
            });
            const word = model.getWordUntilPosition(position);
            const range = {
                startLineNumber: position.lineNumber, endLineNumber: position.lineNumber,
                startColumn: word.startColumn, endColumn: word.endColumn
            };

            if (FIELD_CONTEXT.test(before)) {
                return {
                    suggestions: state.fields.map(f => ({
                        label: f.name,
                        kind: monaco.languages.CompletionItemKind.Field,
                        detail: `${f.label} · ${f.type}`,
                        insertText: f.name,
                        range
                    }))
                };
            }

            const member = MEMBER_CONTEXT.exec(before);
            if (member) {
                const items = catalog.members[member[1]] || [];
                return { suggestions: items.map(i => toSuggestion(monaco, i, range)) };
            }

            return { suggestions: [...catalog.globals, ...catalog.snippets].map(i => toSuggestion(monaco, i, range)) };
        }
    });
}

function notifyChanged(state) {
    state.timer = 0;
    state.dotnet.invokeMethodAsync('OnCodeChanged', state.editor.getValue()).catch(() => { });
}

/**
 * Creates an editor inside `element`. Returns when the editor is ready.
 * options: { value, fields, catalog, height }
 */
export async function create(id, element, dotnet, options) {
    const monaco = await ensureMonaco();
    if (options && options.catalog) catalog = options.catalog;
    registerProviders(monaco);
    watchTheme(monaco);

    const editor = monaco.editor.create(element, {
        value: (options && options.value) || '',
        language: 'csharp',
        theme: isDark() ? 'vs-dark' : 'vs',
        automaticLayout: true,
        minimap: { enabled: false },
        fontSize: 13,
        tabSize: 4,
        insertSpaces: true,
        scrollBeyondLastLine: false,
        lineNumbersMinChars: 3,
        renderWhitespace: 'selection',
        wordBasedSuggestions: 'currentDocument',
        quickSuggestions: { other: true, comments: false, strings: true }
    });

    const state = { editor, dotnet, fields: (options && options.fields) || [], timer: 0, disposables: [] };
    editors.set(id, state);

    state.disposables.push(editor.onDidChangeModelContent(() => {
        clearTimeout(state.timer);
        state.timer = setTimeout(() => notifyChanged(state), 300);
    }));

    // Ctrl/Cmd+S validates instead of opening the browser's "Save page" dialog.
    editor.addCommand(monaco.KeyMod.CtrlCmd | monaco.KeyCode.KeyS, () => {
        clearTimeout(state.timer);
        state.timer = 0;
        state.dotnet.invokeMethodAsync('OnSaveShortcutPressed', editor.getValue()).catch(() => { });
    });

    // Ctrl+K opens the awesomebar (desk.js), but inside the editor it is Monaco's chord prefix
    // (Ctrl+K Ctrl+C comments a selection). Keep it from reaching the window-level handler.
    const stopCtrlK = e => {
        if ((e.ctrlKey || e.metaKey) && (e.key === 'k' || e.key === 'K')) e.stopPropagation();
    };
    element.addEventListener('keydown', stopCtrlK);
    state.disposables.push({ dispose: () => element.removeEventListener('keydown', stopCtrlK) });

    return true;
}

export function getValue(id) {
    const state = editors.get(id);
    if (!state) return null;
    // A pending debounced notification is no longer needed: the caller receives the current value.
    clearTimeout(state.timer);
    state.timer = 0;
    return state.editor.getValue();
}

export function setValue(id, value) {
    const state = editors.get(id);
    if (!state) return;
    const editor = state.editor;
    if (editor.getValue() === value) return;
    const model = editor.getModel();
    // executeEdits keeps the undo history, unlike editor.setValue.
    editor.pushUndoStop();
    editor.executeEdits('frogframe', [{ range: model.getFullModelRange(), text: value ?? '' }]);
    editor.pushUndoStop();
    clearTimeout(state.timer);
    state.timer = 0;
}

/** markers: [{ line, message, severity: 'error' | 'warning' }] */
export function setMarkers(id, markers) {
    const state = editors.get(id);
    if (!state || !window.monaco) return;
    const monaco = window.monaco;
    const model = state.editor.getModel();
    const lineCount = model.getLineCount();
    monaco.editor.setModelMarkers(model, MARKER_OWNER, (markers || []).map(m => {
        const line = Math.min(Math.max(m.line || 1, 1), lineCount);
        return {
            startLineNumber: line,
            startColumn: model.getLineFirstNonWhitespaceColumn(line) || 1,
            endLineNumber: line,
            endColumn: model.getLineMaxColumn(line),
            message: m.message,
            severity: m.severity === 'warning' ? monaco.MarkerSeverity.Warning : monaco.MarkerSeverity.Error
        };
    }));
}

export function setFields(id, fields) {
    const state = editors.get(id);
    if (state) state.fields = fields || [];
}

export function focus(id) {
    const state = editors.get(id);
    if (state) state.editor.focus();
}

export function dispose(id) {
    const state = editors.get(id);
    if (!state) return;
    clearTimeout(state.timer);
    for (const d of state.disposables) {
        try { d.dispose(); } catch { /* ignore */ }
    }
    if (window.monaco) window.monaco.editor.setModelMarkers(state.editor.getModel(), MARKER_OWNER, []);
    state.editor.dispose();
    editors.delete(id);
}
