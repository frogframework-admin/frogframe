"""Port aturan Framework.Metadata.MetadataCompiler (frogframe, Compiler.cs CompileDocType) ke Python.
Pemakaian: python3 validate.py <folder-metadata> [MODUL,...] [ROLE,...]"""
import json, re, sys, glob, os

FOLDER = sys.argv[1]
MODULES = set(sys.argv[2].split(",")) if len(sys.argv) > 2 else None
ROLES = set(sys.argv[3].split(",")) if len(sys.argv) > 3 else None

TYPES = {"Data", "Text", "Email", "Int", "Float", "Currency", "Check", "Date", "Select", "Link", "Table", "Image",
         "Datetime", "Time", "Duration", "Percent", "Phone", "TextEditor", "Markdown", "Attach", "DynamicLink",
         "TableMultiSelect", "Rating", "Color", "JSON", "Signature", "Barcode", "Geolocation", "ReadOnly", "HTML",
         "Button", "Heading", "Password"}
LAYOUT = {"HTML", "Button", "Heading"}
NOT_QUERYABLE = {"Table", "Image", "Password", "Attach", "Signature", "TableMultiSelect", "JSON", "TextEditor", "Markdown",
                 "HTML", "Button", "Heading"}
NOT_IN_CHILD = {"TextEditor", "Markdown", "JSON", "Signature", "TableMultiSelect", "Password", "HTML", "Button", "Heading"}
SEARCHABLE = {"Data", "Text", "Email", "Link", "Select", "Phone", "Barcode", "DynamicLink", "ReadOnly"}
STD = {"name", "owner", "created", "modified", "modified_by", "docstatus", "workflow_state", "version", "amended_from", "idx", "tenant_id"}
STD_QUERY = {"name", "owner", "created", "modified", "docstatus", "workflow_state"}
ACTIONS = {"read", "create", "write", "delete", "submit", "cancel", "amend", "export", "report"}
DYN_DEFAULTS = {"$today", "$user", "$employee"}
FUNCS = {"empty", "len", "sum", "round", "abs", "coalesce", "today", "has_role"}
KEYWORDS = {"true", "false", "null", "in", "and", "or", "not"}
DT_RX = re.compile(r"^[A-Z][A-Za-z0-9]{0,63}$")
FN_RX = re.compile(r"^[a-z][a-z0-9_]{0,63}$")
SCOPE_RX = re.compile(r"^match:([a-z][a-z0-9_]*)=([a-z][a-z0-9_]*)$")

errors = []
defs = {}
for path in sorted(glob.glob(os.path.join(FOLDER, "*.doctype.json"))):
    d = json.load(open(path, encoding="utf-8"))
    if not DT_RX.match(d["name"]): errors.append((d["name"], "name", "bukan PascalCase"))
    if d["name"] in defs: errors.append((d["name"], "name", "duplikat"))
    if os.path.basename(path) != f"{d['name']}.doctype.json": errors.append((d["name"], "file", "nama file tidak sama"))
    defs[d["name"]] = d


def expr_roots(src):
    s = re.sub(r"'(?:[^'\\]|\\.)*'|\"(?:[^\"\\]|\\.)*\"", "''", src)
    roots = set()
    for m in re.finditer(r"[A-Za-z_$][A-Za-z0-9_.$]*", s):
        tok = m.group(0)
        rest = s[m.end():].lstrip()
        if rest.startswith("("):
            if tok not in FUNCS: errors.append((cur, "expr", f"fungsi '{tok}' tidak diizinkan: {src}"))
            continue
        if tok in KEYWORDS: continue
        if tok.startswith("$") or ".." in tok or tok.endswith("."):
            errors.append((cur, "expr", f"nama tidak valid '{tok}'")); continue
        roots.add(tok.split(".")[0])
    if s.count("(") != s.count(")") or s.count("[") != s.count("]"):
        errors.append((cur, "expr", f"kurung tidak seimbang: {src}"))
    return roots


def check_default(f):
    v = f.get("default")
    if v is None: return
    t = f["type"]
    if isinstance(v, str) and v.startswith("$"):
        if v not in DYN_DEFAULTS: errors.append((cur, f["name"], f"default dinamis {v} tidak dikenal"))
        return
    if t == "Check" and not isinstance(v, bool): errors.append((cur, f["name"], "default Check harus boolean"))
    if t == "Int" and not (isinstance(v, int) and not isinstance(v, bool)): errors.append((cur, f["name"], "default Int"))
    if t in ("Float", "Currency", "Percent") and (isinstance(v, bool) or not isinstance(v, (int, float))):
        errors.append((cur, f["name"], "default angka"))
    if t == "Select" and v not in f.get("options", []): errors.append((cur, f["name"], f"default '{v}' tidak ada di options"))
    if t in ("Data", "Link", "Text") and not isinstance(v, str): errors.append((cur, f["name"], "default teks"))
    if t == "Link" and isinstance(v, str):
        tgt = defs.get(f.get("linkTo"))
        if tgt and tgt.get("naming", {}).get("pattern", "").startswith("field:"):
            pass  # nilai harus ada sebagai dokumen; diperiksa saat data seed


for cur, d in defs.items():
    is_child = d.get("isChild", False)
    fields = d.get("fields", [])
    names = [f["name"] for f in fields]
    by = {f["name"]: f for f in fields}
    if not fields: errors.append((cur, "fields", "kosong"))
    if len(set(names)) != len(names): errors.append((cur, "fields", "field duplikat"))
    for n in names:
        if not FN_RX.match(n): errors.append((cur, n, "bukan snake_case"))
        if n in STD: errors.append((cur, n, "nama dicadangkan"))
    roots_ok = set(names) | STD | {"user"} | ({"parent"} if is_child else set())

    def check_expr(src, where, ctx_fields=None):
        if not src: return
        for r in expr_roots(src):
            if r not in roots_ok: errors.append((cur, where, f"ekspresi merujuk '{r}' yang tidak dikenal"))
        # path ke child/parent: periksa segmen kedua
        for m in re.finditer(r"\b([a-z_][a-z0-9_]*)\.([a-z_][a-z0-9_]*)", re.sub(r"'[^']*'", "''", src)):
            a, b = m.groups()
            if a == "parent":
                parents = [p for p in defs.values() if any(x.get("childDocType") == cur for x in p.get("fields", []))]
                for p in parents:
                    if b not in {x["name"] for x in p["fields"]} | STD: errors.append((cur, where, f"parent {p['name']} tidak punya '{b}'"))
            elif a in by and by[a]["type"] == "Table":
                ch = defs.get(by[a].get("childDocType"), {})
                if b not in {x["name"] for x in ch.get("fields", [])}: errors.append((cur, where, f"child {a} tidak punya '{b}'"))
            elif a == "user":
                pass
            elif a in by:
                errors.append((cur, where, f"'{a}.{b}': navigasi hanya untuk Table/parent/user"))

    for f in fields:
        t, fn = f["type"], f["name"]
        if t not in TYPES: errors.append((cur, fn, f"tipe {t} tidak terdaftar")); continue
        if t == "Link":
            tgt = defs.get(f.get("linkTo"))
            if not tgt: errors.append((cur, fn, f"linkTo {f.get('linkTo')} tidak ada"))
            elif tgt.get("isChild"): errors.append((cur, fn, "Link ke child"))
            else:
                for lf in f.get("linkFilters", []):
                    if lf["op"] not in ("=", "!=", "in"): errors.append((cur, fn, "op filter"))
                    if lf["field"] not in STD and lf["field"] not in {x["name"] for x in tgt["fields"]}:
                        errors.append((cur, fn, f"filter field {lf['field']} tidak ada di {tgt['name']}"))
                    if lf["op"] == "in" and not isinstance(lf["value"], list): errors.append((cur, fn, "in butuh array"))
                    tf = next((x for x in tgt["fields"] if x["name"] == lf["field"]), None)
                    if tf and tf["type"] == "Select":
                        vals = lf["value"] if isinstance(lf["value"], list) else [lf["value"]]
                        for v in vals:
                            if v not in tf["options"]: errors.append((cur, fn, f"nilai filter '{v}' bukan opsi {tgt['name']}.{tf['name']}"))
                    if tf and tf["type"] == "Check" and not isinstance(lf["value"], bool):
                        errors.append((cur, fn, "filter Check harus boolean"))
        if t == "Table":
            if is_child: errors.append((cur, fn, "child bertingkat"))
            ch = defs.get(f.get("childDocType"))
            if not ch: errors.append((cur, fn, "childDocType tidak ada"))
            elif not ch.get("isChild"): errors.append((cur, fn, "child belum isChild"))
            if f.get("compute"): errors.append((cur, fn, "Table compute"))
            add = f.get("addFromLookup")
            if add and ch:
                lk = next((x for x in ch["fields"] if x["name"] == add.get("linkField")), None)
                if not lk or lk["type"] != "Link": errors.append((cur, fn, "addFromLookup.linkField harus Link di child"))
                else:
                    src = defs.get(lk["linkTo"], {"fields": []})
                    for cfld, sfld in add.get("map", {}).items():
                        if cfld not in {x["name"] for x in ch["fields"]}: errors.append((cur, fn, f"addFromLookup: {cfld} tidak ada di child"))
                        if sfld not in {x["name"] for x in src["fields"]}: errors.append((cur, fn, f"addFromLookup: {sfld} tidak ada di {lk['linkTo']}"))
        if t == "Select" and not f.get("options"): errors.append((cur, fn, "Select tanpa options"))
        if t == "TableMultiSelect":
            tgt = defs.get(f.get("linkTo"))
            if not tgt or tgt.get("isChild"): errors.append((cur, fn, "TableMultiSelect linkTo"))
        if t == "DynamicLink":
            tf = by.get(f.get("linkToField") or "")
            if not tf: errors.append((cur, fn, "linkToField"))
            elif tf["type"] not in ("Select", "Data", "ReadOnly"): errors.append((cur, fn, "linkToField tipe"))
            elif tf["type"] == "Select":
                for o in tf["options"]:
                    if o not in defs or defs[o].get("isChild"): errors.append((cur, fn, f"opsi {o} bukan DocType"))
        if t == "Button" and not f.get("buttonAction"): errors.append((cur, fn, "buttonAction"))
        if is_child and t in NOT_IN_CHILD: errors.append((cur, fn, f"{t} tidak boleh di child"))
        if t in ("Password", "Attach", "Signature") or t in LAYOUT:
            if f.get("compute") or f.get("fetchFrom") or f.get("default") is not None: errors.append((cur, fn, f"{t} tak boleh compute/fetch/default"))
        if t in LAYOUT and f.get("requiredWhen"): errors.append((cur, fn, "layout wajib"))
        if f.get("pattern"):
            try: re.compile(f["pattern"])
            except re.error: errors.append((cur, fn, "regex"))
        if f.get("fetchFrom") and is_child:
            errors.append((cur, fn, "fetchFrom di child table tidak diterapkan runtime (DocumentService.ApplyFetchFromAsync)"))
        if f.get("fetchFrom"):
            parts = f["fetchFrom"].split(".")
            lf = by.get(parts[0]) if len(parts) == 2 else None
            if not lf or lf["type"] != "Link": errors.append((cur, fn, "fetchFrom format"))
            elif lf.get("linkTo") in defs and parts[1] not in {x["name"] for x in defs[lf["linkTo"]]["fields"]}:
                errors.append((cur, fn, f"fetchFrom: {parts[1]} tidak ada di {lf['linkTo']}"))
        check_default(f)
        if f.get("addFromLookup") and t != "Table": errors.append((cur, fn, "addFromLookup hanya Table"))
        if f.get("lookupDialog") and t != "Link": errors.append((cur, fn, "lookupDialog"))
        for k in ("requiredWhen", "visibleWhen", "readOnlyWhen", "compute"):
            check_expr(f.get(k), f"{fn}.{k}")
        if f.get("compute") and f.get("fetchFrom"): errors.append((cur, fn, "compute + fetchFrom"))

    for r in d.get("rules", []):
        check_expr(r["expr"], f"rules.{r['name']}")
        if r.get("field") and r["field"] not in by: errors.append((cur, r["name"], "rule field"))

    if d.get("isSingle") and is_child: errors.append((cur, "isSingle", "child+single"))
    if d.get("allowAutoRepeat") and (is_child or d.get("isSingle")): errors.append((cur, "autoRepeat", "tidak boleh"))
    if MODULES is not None and not is_child:
        if not d.get("module"): errors.append((cur, "module", "wajib"))
        elif d["module"] not in MODULES: errors.append((cur, "module", f"{d['module']} belum di master modul"))
    for p in d.get("permissions", []):
        if ROLES is not None and p["role"] not in ROLES: errors.append((cur, "permissions", f"role {p['role']} belum ada"))
        for a in p["actions"]:
            if a not in ACTIONS: errors.append((cur, "permissions", f"aksi {a}"))
        sc = p.get("scope", "all")
        if sc not in ("all", "owner"):
            m = SCOPE_RX.match(sc)
            if not m or m.group(1) not in by: errors.append((cur, "permissions", f"scope {sc}"))
        if "submit" in p["actions"] and not d.get("document", {}).get("allowSubmit"):
            print(f"   peringatan: {cur} memberi aksi submit tapi allowSubmit false")
    lv = d.get("listView", {})
    cols = lv.get("columns") or [f["name"] for f in fields if f.get("inListView")]
    for c in cols + lv.get("filters", []) + lv.get("searchFields", ["name"]) + [s["field"] for s in lv.get("defaultSort", [])]:
        if c in STD_QUERY: continue
        f = by.get(c)
        if not f or f["type"] == "Table" or (f["type"] != "Image" and f["type"] in NOT_QUERYABLE):
            errors.append((cur, "listView", f"field {c} tidak bisa dipakai"))
    for s in lv.get("searchFields", []):
        if s in by and by[s]["type"] not in SEARCHABLE: errors.append((cur, "listView.searchFields", f"{s} bukan teks"))
    if lv.get("titleField") and lv["titleField"] not in by and lv["titleField"] not in STD:
        errors.append((cur, "listView.titleField", "tidak ada"))
    for op in d.get("api", {}).get("operations", []):
        if op not in ("list", "read", "create", "update", "delete", "action"): errors.append((cur, "api", op))
    pat = d.get("naming", {}).get("pattern", "{doctype}-{#####}")
    if not is_child:
        if pat.startswith("field:"):
            nf = by.get(pat[6:])
            if not nf or nf["type"] != "Data" or nf.get("requiredWhen") != "true": errors.append((cur, "naming", "field: butuh Data wajib"))
        elif "{#" not in pat: errors.append((cur, "naming", "tanpa nomor seri"))

# ---------------- Workflow (port CompileWorkflow) ----------------
WF_RX = re.compile(r"^[A-Za-z][A-Za-z0-9_]{1,63}$")
wfs = {}
for path in sorted(glob.glob(os.path.join(FOLDER, "*.workflow.json"))):
    w = json.load(open(path, encoding="utf-8"))
    cur = f"Workflow {w['name']}"
    wfs[w["name"]] = w
    if os.path.basename(path) != f"{w['name']}.workflow.json": errors.append((cur, "file", "nama file tidak sama"))
    if not WF_RX.match(w["name"]): errors.append((cur, "name", "nama workflow tidak valid"))
    dt = defs.get(w.get("docType") or "")
    if w.get("docType") and not dt: errors.append((cur, "docType", "DocType tidak ada")); continue
    if dt and dt.get("isChild"): errors.append((cur, "docType", "child"))
    if dt and dt.get("workflow") != w["name"]: errors.append((cur, "docType", f"DocType {dt['name']} tidak menunjuk workflow ini"))
    seen = set()
    for t in w["transitions"]:
        k = (t["from"], t["action"])
        if k in seen: errors.append((cur, str(k), "aksi duplikat dari state yang sama"))
        seen.add(k)
    states = {}
    for st in w["states"]:
        if not st["name"].strip(): errors.append((cur, "states", "nama kosong"))
        if st["name"] in states: errors.append((cur, st["name"], "state duplikat"))
        if st["docStatus"] not in (0, 1, 2): errors.append((cur, st["name"], "docStatus"))
        states[st["name"]] = st
    init = states.get(w["initialState"])
    if not init or init["docStatus"] != 0: errors.append((cur, "initialState", "harus ada dan docStatus 0"))
    roots_ok = ({f["name"] for f in dt["fields"]} if dt else set()) | STD | {"user"}
    for t in w["transitions"]:
        p = f"{t['from']}->{t['to']}"
        if t["from"] not in states: errors.append((cur, p, "state asal tidak ada"))
        if t["to"] not in states: errors.append((cur, p, "state tujuan tidak ada"))
        if not t.get("action", "").strip(): errors.append((cur, p, "action kosong"))
        if not t.get("allowedRoles"): errors.append((cur, p, "minimal satu role"))
        for r in t.get("allowedRoles", []):
            if ROLES is not None and r not in ROLES: errors.append((cur, p, f"role {r} belum ada"))
        if t["from"] in states and t["to"] in states and states[t["to"]]["docStatus"] < states[t["from"]]["docStatus"]:
            errors.append((cur, p, "menurunkan docStatus"))
        if t.get("condition") and dt:
            for r in expr_roots(t["condition"]):
                if r not in roots_ok: errors.append((cur, p, f"kondisi merujuk '{r}'"))
    # Bukan aturan compiler: state draft (docStatus 0) tanpa transisi keluar membuat dokumen macet
    for sn, st in states.items():
        if st["docStatus"] == 0 and not any(t["from"] == sn for t in w["transitions"]):
            errors.append((cur, sn, "state draft tanpa transisi keluar"))
for cur, d in defs.items():
    if d.get("workflow"):
        if d.get("isSingle"): errors.append((cur, "workflow", "Single tidak mendukung workflow"))
        if d["workflow"] not in wfs: errors.append((cur, "workflow", f"workflow {d['workflow']} tidak ditemukan"))
        elif wfs[d["workflow"]].get("docType") != cur: errors.append((cur, "workflow", "workflow milik DocType lain"))

# Siklus compute sederhana
for cur, d in defs.items():
    comp = {f["name"]: expr_roots(f["compute"]) for f in d["fields"] if f.get("compute")}
    def visit(n, trail):
        if n in trail: errors.append((cur, n, "compute melingkar")); return
        for dep in comp.get(n, ()):
            if dep in comp: visit(dep, trail | {n})
    for n in comp: visit(n, frozenset())

print(f"{len(defs)} DocType, {len(wfs)} workflow diperiksa, {len(errors)} masalah")
for e in errors: print(" -", *e)
sys.exit(1 if errors else 0)
