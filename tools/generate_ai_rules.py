"""Katalog aturan bisnis untuk AI Assistant, diambil dari kode hook C# (src/Apps.Erp).

AI di aplikasi tidak membaca source .cs. Skrip ini mengekstrak yang hanya ada di badan method:
  - pesan validasi/error (Errors.One, errors.Add, FieldError, InvalidOperationException, FormActionResult.Fail) + field + syarat `if`
  - peringatan (c.Messages.Add)
  - tombol aksi (Label, syarat IsVisible: state, docstatus, role)
  - ringkasan /// <summary> class dan DocType + event tempat aturan berlaku (helper bersama dipetakan ke DocType pemakainya)

Keluaran (ikut dipaket ke KnowledgeBase/rules/ oleh Apps.Erp.csproj):
  docs/rules/<DocType>.md        satu file per DocType, dicari oleh search_knowledge
  docs/rules/_shared-<Class>.md  aturan helper bersama (periode, faktur, stok, ...)
  docs/rules/error-catalog.json  dipakai alat explain_error (pola regex per pesan)

Pemakaian (dari root repo):
  python3 tools/generate_ai_rules.py           # tulis ulang docs/rules/
  python3 tools/generate_ai_rules.py --check   # exit 1 bila docs/rules/ tidak sesuai kode (untuk CI)
"""
import glob, json, os, re, sys
from collections import defaultdict

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
SRC = os.path.join(ROOT, "src", "Apps.Erp")
META = os.path.join(SRC, "Metadata")
OUT = os.path.join(ROOT, "docs", "rules")
SCAN = ["Hooks", "Accounting"]

titles = {}
for p in glob.glob(os.path.join(META, "*.doctype.json")):
    d = json.load(open(p, encoding="utf-8"))
    titles[d["name"]] = d.get("label") or d.get("title") or d["name"]

EVENT = {
    "BeforeValidateAsync": "Saat simpan/submit (validasi)", "BeforeInsertAsync": "Saat dokumen baru disimpan",
    "BeforeUpdateAsync": "Saat perubahan disimpan", "AfterSaveAsync": "Setelah simpan/submit (posting, efek ke dokumen lain)",
    "OnCancelAsync": "Saat cancel", "BeforeDeleteAsync": "Saat hapus", "BeforeTransitionAsync": "Saat aksi workflow (approve/reject/...)",
    "OnLoadAsync": "Saat dokumen dibuka", "ExecuteAsync": "Saat tombol aksi diklik",
}

# ------------------------------------------------------------------------------------------------ lexer kecil C#
def skip_string(s, i):
    """i menunjuk awal literal ($"...", @"...", $@"...", "...", '...'). Kembalikan indeks setelah literal."""
    j = i
    interp = verbatim = False
    while s[j] in "$@":
        interp |= s[j] == "$"; verbatim |= s[j] == "@"; j += 1
    if s[j] == "'":
        j += 1
        while s[j] != "'":
            j += 2 if s[j] == "\\" else 1
        return j + 1
    if s.startswith('"""', j):
        end = s.index('"""', j + 3)
        return end + 3
    j += 1
    while True:
        ch = s[j]
        if ch == "\\" and not verbatim: j += 2; continue
        if ch == '"':
            if verbatim and s[j + 1:j + 2] == '"': j += 2; continue
            return j + 1
        if interp and ch == "{":
            if s[j + 1] == "{": j += 2; continue
            j = skip_braces(s, j)
            continue
        j += 1

def skip_braces(s, i):
    """i menunjuk '{'. Kembalikan indeks setelah '}' pasangannya (mengabaikan isi string & komentar)."""
    depth = 0; j = i
    while j < len(s):
        ch = s[j]
        if ch in "\"'" or (ch in "$@" and s[j + 1:j + 2] in ('"', "@", "$")):
            j = skip_string(s, j); continue
        if s.startswith("//", j): j = s.index("\n", j); continue
        if s.startswith("/*", j): j = s.index("*/", j) + 2; continue
        if ch in "{([": depth += 1
        elif ch in "})]":
            depth -= 1
            if depth == 0: return j + 1
        j += 1
    return j

def top_level_head(expr):
    """Ekspresi interpolasi tanpa format (:N0) / perataan (,10) di tingkat atas."""
    depth = 0; i = 0
    while i < len(expr):
        ch = expr[i]
        if ch in "\"'" or (ch in "$@" and expr[i + 1:i + 2] in ('"', "@", "$")):
            try: i = skip_string(expr + " ", i); continue
            except (IndexError, ValueError): return expr.strip()
        if ch in "([{": depth += 1
        elif ch in ")]}": depth -= 1
        elif ch == "?" and depth == 0 and expr[i + 1:i + 2] in (".", "?", "["): i += 2; continue
        elif ch in ":," and depth == 0:
            if ch == ":" and "?" in expr[:i]: i += 1; continue  # operator ternary
            return expr[:i].strip()
        i += 1
    return expr.strip()

def literal_value(s, i):
    """Parse literal string di posisi i → (teks tampilan, regex, indeks akhir). Interpolasi → {…} / .+?"""
    j = i; interp = verbatim = False
    while s[j] in "$@":
        interp |= s[j] == "$"; verbatim |= s[j] == "@"; j += 1
    j += 1
    text, rx = [], []
    while True:
        ch = s[j]
        if ch == "\\" and not verbatim:
            esc = s[j + 1]; c = {"n": "\n", "t": "\t", '"': '"', "\\": "\\", "'": "'"}.get(esc, esc)
            text.append(c); rx.append(re.escape(c)); j += 2; continue
        if ch == '"':
            if verbatim and s[j + 1:j + 2] == '"': text.append('"'); rx.append('"'); j += 2; continue
            return "".join(text), "".join(rx), j + 1
        if interp and ch == "{":
            if s[j + 1] == "{": text.append("{"); rx.append(r"\{"); j += 2; continue
            end = skip_braces(s, j)
            expr = s[j + 1:end - 1]
            expr = top_level_head(expr)
            text.append("{" + expr + "}"); rx.append(".+?"); j = end; continue
        if interp and ch == "}" and s[j + 1:j + 2] == "}": text.append("}"); rx.append(r"\}"); j += 2; continue
        text.append(ch); rx.append(re.escape(ch) if ch not in " " else r"\s+"); j += 1

def expression_message(s, i, end, local_strings):
    """Argumen pesan (literal, gabungan literal + variabel, atau variabel string lokal) → (teks, regex) atau None."""
    parts_t, parts_r = [], []
    j = i
    while j < end:
        while j < end and s[j] in " \t\r\n+": j += 1
        if j >= end: break
        if s[j] in "\"$@":
            t, r, j = literal_value(s, j); parts_t.append(t); parts_r.append(r); continue
        m = re.match(r"[A-Za-z_][A-Za-z0-9_.]*", s[j:end])
        if not m: return None
        name = m.group(0); j += len(name)
        if name in local_strings: t, r = local_strings[name]; parts_t.append(t); parts_r.append(r)
        else: parts_t.append("{" + name + "}"); parts_r.append(".+?")
        if j < end and s[j] == "(":  # pemanggilan method: tidak bisa diresolusi
            return None
    if not parts_t: return None
    return "".join(parts_t), "".join(parts_r)

def split_args(s, i):
    """i menunjuk '(' → daftar (start, end) argumen tingkat atas."""
    args, depth, j, start = [], 0, i, i + 1
    while True:
        ch = s[j]
        if ch in "\"'" or (ch in "$@" and s[j + 1:j + 2] in ('"', "@", "$")): j = skip_string(s, j); continue
        if ch in "([{": depth += 1
        elif ch in ")]}":
            depth -= 1
            if depth == 0: args.append((start, j)); return args, j + 1
        elif ch == "," and depth == 1: args.append((start, j)); start = j + 1
        j += 1

def clean(code):
    return re.sub(r"\s+", " ", code).strip()

def summary_before(s, pos):
    """Ringkasan /// <summary> tepat sebelum deklarasi pada posisi pos."""
    lines = s[:pos].rstrip().split("\n")
    doc = []
    for line in reversed(lines[:-0 or None]):
        t = line.strip()
        if t.startswith("///"): doc.append(t[3:].strip()); continue
        if t.startswith("[") or not t: continue
        break
    text = " ".join(reversed(doc))
    text = re.sub(r'<see cref="(?:[A-Z]:)?([^"]+)"\s*/>', lambda m: m.group(1).split(".")[-1], text)
    text = re.sub(r"<[^>]+>", "", text)
    return clean(text)

# ------------------------------------------------------------------------------------------------ ekstraksi
CLASS_RX = re.compile(r"\b(?:public|internal|private)?\s*(?:sealed\s+|static\s+|abstract\s+|partial\s+)*(?:class|record)\s+([A-Za-z0-9_]+)(?:<[^>]*>)?\s*(\([^)]*\))?\s*(?::\s*([^{]+))?\{")
SITE_RX = re.compile(r"Errors\.One\(|\b(?:errors|Errors|plan\.Errors)\.Add\(|new FieldError\(|FormActionResult\.Fail\(|throw new InvalidOperationException\(|new LedgerException\(|\bc\.Messages\.Add\(")

classes = []  # dict(name, file, start, end, docTypes, summary, text)
for folder in SCAN:
    for path in sorted(glob.glob(os.path.join(SRC, folder, "*.cs"))):
        s = open(path, encoding="utf-8").read()
        rel = os.path.relpath(path, ROOT).replace("\\", "/")
        for m in CLASS_RX.finditer(s):
            body_start = m.end() - 1
            end = skip_braces(s, body_start)
            bases = m.group(3) or ""
            dts = sorted(set(re.findall(r"I(?:BeforeValidate|AfterSave|OnCancel|BeforeDelete|BeforeInsert|BeforeUpdate|BeforeTransition|OnLoad|FormAction)<([A-Za-z0-9_]+)>", bases)))
            classes.append(dict(name=m.group(1), file=rel, src=s, start=m.start(), body=body_start, end=end, docTypes=dts,
                                summary=summary_before(s, m.start()), bases=clean(bases)))
# Kelas bersarang: pakai kelas terdalam yang memuat posisi
def owner(path, pos):
    best = None
    for c in classes:
        if c["file"] == path and c["body"] <= pos < c["end"] and (best is None or c["body"] > best["body"]): best = c
    return best

DECL_RX = re.compile(r"^[ \t]+(?:(?:public|private|internal|protected|static|async|override|sealed|virtual)\s+)+[\w<>\[\],.?() ]*?\b([A-Z][A-Za-z0-9_]*)\s*\(", re.M)

def enclosing_method(s, cls, pos):
    """Nama method yang dideklarasikan paling dekat sebelum pos di dalam kelas."""
    best = None
    for m in DECL_RX.finditer(s, cls["body"], pos):
        best = m.group(1)
    return best

def condition_before(s, pos, lo):
    """Teks syarat `if (...)` yang langsung mengatur pernyataan error di pos (tanpa ';' di antaranya)."""
    window_start = max(lo, pos - 1200)
    cands = [m.start() for m in re.finditer(r"\bif\s*\(", s[window_start:pos])]
    for off in reversed(cands):
        i = window_start + off
        par = s.index("(", i)
        _, close = split_args(s, par)
        between = s[close:pos]
        if close <= pos and ";" not in between and between.count("{") - between.count("}") <= 1:
            direct = clean(s[par + 1:close - 1])
            direct_at = i
            break
    else:
        direct, direct_at = None, pos
    # guard clause di blok yang sama: `if (x) continue;` / `if (y) throw ...;` sebelum pesan → berlaku bila BUKAN x dan BUKAN y
    depth, k = 0, pos
    while k > lo:
        k -= 1
        if s[k] == "}": depth += 1
        elif s[k] == "{":
            if depth == 0: break
            depth -= 1
    guards = []
    for g in re.finditer(r"\bif\s*\(", s[k:direct_at]):
        par = k + g.end() - 1
        try: _, close = split_args(s, par)
        except IndexError: continue
        tail = s[close:pos]
        stmt = tail.split(";", 1)[0]
        if ";" in tail and re.match(r"\s*(continue|break|return\b|throw\b)", stmt) and "{" not in stmt:
            guards.append("!(" + clean(s[par + 1:close - 1]) + ")")
    if direct: guards.append(direct)
    if guards: return " && ".join(guards)
    # pola `cond ? null : Errors.One(...)` / `?? throw Errors.One(...)`
    stmt_start = max(s.rfind(";", lo, pos), s.rfind("{", lo, pos), s.rfind("}", lo, pos)) + 1
    stmt = clean(s[stmt_start:pos])
    if "?? throw" in stmt: return clean(stmt.split("?? throw")[0].split("=", 1)[-1]) + " tidak ditemukan/kosong"
    return None

def local_strings(s, lo, pos):
    out = {}
    for m in re.finditer(r"\b(?:var|string)\s+([a-z][A-Za-z0-9_]*)\s*=\s*(\$?@?\"|@\$\")", s[lo:pos]):
        st = lo + m.start(2)
        try:
            t, r, e = literal_value(s, st)
            nxt = s[e:e + 40].lstrip()
            if nxt.startswith(";") or nxt.startswith("+"): out[m.group(1)] = (t, r)
        except (IndexError, ValueError):
            pass
    return out

entries = []
actions = []
for c in classes:
    s = c["src"]
    # tombol aksi
    if "IFormAction<" in c["bases"]:
        body = s[c["body"]:c["end"]]
        lab = re.search(r"Label\s*=>\s*\"([^\"]+)\"", body)
        vis = re.search(r"IsVisible\([^)]*\)\s*=>\s*(.+?);\s*\n", body, re.S)
        if lab:
            actions.append(dict(docType=c["docTypes"][0] if c["docTypes"] else None, label=lab.group(1),
                                visibleWhen=clean(vis.group(1)) if vis else None, className=c["name"], summary=c["summary"],
                                file=c["file"], line=s.count("\n", 0, c["start"]) + 1))
for path in sorted({c["file"] for c in classes}):
    s = open(os.path.join(ROOT, path), encoding="utf-8").read()
    for m in SITE_RX.finditer(s):
        call = m.group(0)
        if "Errors.Add(" in call and "plan." not in call and s[m.start() - 1:m.start()] == ".":
            pass
        cls = owner(path, m.start())
        if cls is None or cls["name"] == "Errors": continue
        par = m.end() - 1
        try:
            args, _ = split_args(s, par)
        except IndexError:
            continue
        kind = "Warning" if "Messages.Add" in call else "ActionFailure" if "Fail(" in call else "Error"
        field = None
        if ".Add(" in call and len(args) == 1 and re.match(r"\s*new(?:\s+FieldError)?\s*\(", s[args[0][0]:args[0][1]]):
            inner = s.index("(", args[0][0])
            args, _ = split_args(s, inner)  # errors.Add(new(path, message))
        if "Errors.One" in call or ".Add(" in call and kind == "Error" or "FieldError" in call:
            if len(args) < 2: continue
            fa = s[args[0][0]:args[0][1]].strip()
            field = literal_value(s, args[0][0] + (len(s[args[0][0]:args[0][1]]) - len(s[args[0][0]:args[0][1]].lstrip())))[0] if fa[:1] in ('"', "$", "@") else "{" + fa + "}"
            msg_arg = args[1]
        else:
            msg_arg = args[0]
        method_start = max(s.rfind("\n    public", cls["body"], m.start()), s.rfind("\n    private", cls["body"], m.start()),
                           s.rfind("\n    internal", cls["body"], m.start()), cls["body"])
        msg = expression_message(s, msg_arg[0], msg_arg[1], local_strings(s, method_start, m.start()))
        if msg is None: continue
        text, rx = msg
        literal = re.sub(r"\{[^}]*\}", "", text)
        if len(literal.strip()) < 6: continue
        method = enclosing_method(s, cls, m.start())
        entries.append(dict(kind=kind, message=text, pattern=rx, field=field, condition=condition_before(s, m.start(), method_start),
                            className=cls["name"], method=method, event=EVENT.get(method, f"Pemeriksaan {method}" if method else None), docTypes=list(cls["docTypes"]),
                            file=path, line=s.count("\n", 0, m.start()) + 1))

# Helper bersama → DocType pemakai, transitif (hook → Stock → StockPlan): kelas yang menyebut nama helper di badannya
by_name = {c["name"]: c for c in classes}
mentions = defaultdict(set)  # helper -> kelas yang menyebutnya
names = [c["name"] for c in classes if c["end"] - c["body"] > 10]
for c in classes:
    body = c["src"][c["body"]:c["end"]]
    for n in names:
        if n != c["name"] and re.search(r"\b" + re.escape(n) + r"\b", body): mentions[n].add(c["name"])
users = {}
def users_of(name, seen=()):
    if name in users: return users[name]
    out = set()
    for caller in mentions.get(name, ()):
        if caller in seen: continue
        cc = by_name[caller]
        out |= set(cc["docTypes"]) if cc["docTypes"] else users_of(caller, seen + (name,))
    if not seen: users[name] = out
    return out
for e in entries:
    if not e["docTypes"]:
        e["docTypes"] = sorted(users_of(e["className"]))
        e["shared"] = True

# ------------------------------------------------------------------------------------------------ keluaran
def esc(t): return t.replace("|", "\\|")
HEADER = "<!-- Dihasilkan otomatis oleh tools/generate_ai_rules.py dari kode hook. Jangan diedit manual; jalankan ulang skripnya. -->\n"
files = {}
per_dt = defaultdict(list)
for e in entries:
    if not e.get("shared"):
        for dt in e["docTypes"]: per_dt[dt].append(e)
act_dt = defaultdict(list)
for a in actions:
    if a["docType"]: act_dt[a["docType"]].append(a)
shared_by_dt = defaultdict(set)
for e in entries:
    if e.get("shared"):
        for dt in e["docTypes"]: shared_by_dt[dt].add(e["className"])
hook_summaries = defaultdict(list)
for c in classes:
    if c["summary"]:
        for dt in c["docTypes"]: hook_summaries[dt].append(c)

def entry_line(e):
    where = f"`{e['field']}`" if e["field"] else ""
    cond = f" — terjadi bila `{e['condition']}`" if e["condition"] else ""
    return f"- {where}{': ' if where else ''}\"{e['message']}\"{cond} ({e['className']}, {os.path.basename(e['file'])}:{e['line']})"

for dt in sorted(set(per_dt) | set(act_dt) | set(hook_summaries) | set(shared_by_dt)):
    title = titles.get(dt, dt)
    out = [HEADER, f"# Aturan bisnis {title} ({dt})", "",
           f"Aturan yang dijalankan kode saat dokumen {title} ({dt}) disimpan, di-submit, dibatalkan, atau diproses. "
           "Gunakan untuk menjelaskan kenapa dokumen ditolak, kenapa muncul peringatan, atau kenapa tombol tidak tampil.", ""]
    if hook_summaries.get(dt):
        out += ["## Ringkasan perilaku", ""]
        for c in hook_summaries[dt]: out.append(f"- **{c['name']}**: {c['summary']}")
        out.append("")
    groups = defaultdict(list)
    for e in per_dt.get(dt, []): groups[(e["kind"], e["event"])].append(e)
    for kind, head in (("Error", "Validasi (dokumen ditolak)"), ("ActionFailure", "Tombol aksi gagal"), ("Warning", "Peringatan (dokumen tetap tersimpan)")):
        evs = sorted({ev for k, ev in groups if k == kind}, key=lambda x: x or "")
        for ev in evs:
            out += [f"## {head}: {ev or 'lainnya'}", ""]
            out += [entry_line(e) for e in groups[(kind, ev)]]
            out.append("")
    for a in act_dt.get(dt, []):
        out += [f"## Tombol aksi: {a['label']}", "",
                f"Tombol **{a['label']}** di form {title} hanya tampil bila `{a['visibleWhen']}` "
                "(WorkflowState = state workflow, DocStatus = Draft/Submitted, HasRole = peran user)." + (f" {a['summary']}" if a["summary"] else ""), ""]
    if shared_by_dt.get(dt):
        out += ["## Aturan bersama yang juga berlaku", ""]
        out += [f"- {h}: lihat `_shared-{h}.md`" for h in sorted(shared_by_dt[dt])]
        out.append("")
    files[f"{dt}.md"] = "\n".join(out).rstrip() + "\n"

shared = defaultdict(list)
for e in entries:
    if e.get("shared"): shared[e["className"]].append(e)
for helper, es in sorted(shared.items()):
    c = by_name[helper]
    used = ", ".join(es[0]["docTypes"]) or ("semua dokumen yang memposting ke buku besar" if "Ledger" in helper else "beberapa proses (job/event)")
    out = [HEADER, f"# Aturan bersama {helper}", "", f"Dipakai oleh: {used}.", ""]
    if c["summary"]: out += [c["summary"], ""]
    for kind, head in (("Error", "Validasi (dokumen ditolak)"), ("Warning", "Peringatan"), ("ActionFailure", "Aksi gagal")):
        rows = [e for e in es if e["kind"] == kind]
        if rows: out += [f"## {head}", ""] + [entry_line(e) for e in rows] + [""]
    files[f"_shared-{helper}.md"] = "\n".join(out).rstrip() + "\n"

catalog = [dict(id=f"{os.path.basename(e['file'])}:{e['line']}", **{k: e[k] for k in
           ("kind", "message", "pattern", "field", "condition", "docTypes", "event", "className", "method", "file", "line")},
           shared=bool(e.get("shared"))) for e in entries]
files["error-catalog.json"] = json.dumps({"generatedBy": "tools/generate_ai_rules.py", "entries": catalog}, ensure_ascii=False, indent=1) + "\n"

check = "--check" in sys.argv
existing = {os.path.basename(p) for p in glob.glob(os.path.join(OUT, "*")) if os.path.basename(p) != "README.md"}
stale = sorted(n for n, t in files.items() if not os.path.exists(os.path.join(OUT, n)) or open(os.path.join(OUT, n), encoding="utf-8").read() != t)
extra = sorted(existing - set(files))
print(f"{len(entries)} aturan ({sum(e['kind'] == 'Error' for e in entries)} error, {sum(e['kind'] == 'Warning' for e in entries)} peringatan, "
      f"{sum(e['kind'] == 'ActionFailure' for e in entries)} aksi gagal), {len(actions)} tombol, {len(files) - 1} file Markdown")
if check:
    if stale or extra:
        print("docs/rules/ tidak sesuai kode. Jalankan: python3 tools/generate_ai_rules.py")
        for n in stale: print("  berubah:", n)
        for n in extra: print("  usang:", n)
        sys.exit(1)
    print("docs/rules/ sesuai kode"); sys.exit(0)
os.makedirs(OUT, exist_ok=True)
for n in extra: os.remove(os.path.join(OUT, n))
for n, t in files.items():
    open(os.path.join(OUT, n), "w", encoding="utf-8", newline="\n").write(t)
print(f"Ditulis ke docs/rules/ ({len(stale)} berubah, {len(extra)} dihapus)")
