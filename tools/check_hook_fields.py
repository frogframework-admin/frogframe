"""Cocokkan nama field yang dipakai hook C# (src/Apps.Erp) dengan metadata DocType (Metadata/*.doctype.json).

Diperiksa per DocType bila DocType-nya tertulis literal di kode:
  - m.QueryAsync("DT", [new FilterClause("field", ...)])          field harus ada di DT
  - m.StrAsync("DT", x, "field") / RequireAsync / GetAsync ...     field harus ada di DT
  - InsertAsync(.., "DT", new Dictionary<..> { ["field"] = .. })    semua key harus ada di DT (field tak dikenal ditolak framework)
  - UpdateAsync(.., "DT", .., new Dictionary<..> { ["field"] = .. }) key harus ada dan TIDAK read-only (field readOnly ditolak saat update)
Selain itu setiap literal di .Str/.Dec/.Bool/.Date/.Rows/SetIfEmpty/d["..."] harus ada di minimal satu DocType.
Pemakaian: python3 tools/check_hook_fields.py [folder-repo-erp]
"""
import json, glob, os, re, sys

ROOT = sys.argv[1] if len(sys.argv) > 1 else os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
defs = {}
for p in glob.glob(os.path.join(ROOT, "Metadata", "*.doctype.json")):
    d = json.load(open(p, encoding="utf-8"))
    defs[d["name"]] = {f["name"]: f for f in d["fields"]}
STD = {"name", "owner", "created", "modified", "modified_by", "docstatus", "workflow_state", "version", "amended_from", "idx"}
ALL_FIELDS = set().union(*[set(v) for v in defs.values()]) | STD
# key payload event (bukan field DocType)
EVENT_KEYS = {"docType", "name", "old_position", "new_position", "fiscal_year", "status", "quotation", "opportunity"}

problems = []
def check(dt, field, where, write=False):
    if dt not in defs: problems.append(f"{where}: DocType '{dt}' tidak ada"); return
    if field in STD: return
    f = defs[dt].get(field)
    if f is None: problems.append(f"{where}: field '{field}' tidak ada di {dt}"); return
    if write and f.get("readOnlyWhen") == "true":
        problems.append(f"{where}: field '{field}' di {dt} read-only; UpdateAsync akan menolaknya")
    if write and (f.get("compute") or f.get("fetchFrom")):
        problems.append(f"{where}: field '{field}' di {dt} dihitung server (compute/fetch); nilai payload diabaikan")

def block_after(src, start):
    """Ambil isi { ... } pertama setelah posisi start (seimbang)."""
    i = src.find("{", start)
    if i < 0: return ""
    depth = 0
    for j in range(i, len(src)):
        if src[j] == "{": depth += 1
        elif src[j] == "}":
            depth -= 1
            if depth == 0: return src[i:j + 1]
    return ""

def top_level_keys(block):
    """Key ["x"] = pada level teratas blok (bukan di dalam nested new() child row)."""
    keys, depth = [], 0
    for mo in re.finditer(r'[{}]|\["([a-z_][a-z0-9_]*)"\]\s*=', block):
        tok = mo.group(0)
        if tok == "{": depth += 1
        elif tok == "}": depth -= 1
        elif depth == 1: keys.append(mo.group(1))
    return keys

for path in sorted(glob.glob(os.path.join(ROOT, "src", "Apps.Erp", "**", "*.cs"), recursive=True)):
    src = open(path, encoding="utf-8").read()
    fn = os.path.basename(path)
    line = lambda pos: src.count("\n", 0, pos) + 1

    for mo in re.finditer(r'QueryAsync\("([A-Za-z]+)",\s*\[([^\]]*)\]', src):
        for fm in re.finditer(r'FilterClause\("([a-z_]+)"', mo.group(2)):
            check(mo.group(1), fm.group(1), f"{fn}:{line(mo.start())} QueryAsync")
    for mo in re.finditer(r'StrAsync\("([A-Za-z]+)",[^,]+,\s*"([a-z_]+)"', src):
        check(mo.group(1), mo.group(2), f"{fn}:{line(mo.start())} StrAsync")
    for mo in re.finditer(r'InsertAsync\((?:[^,()]+,\s*)?"([A-Za-z]+)",\s*new Dictionary', src):
        for k in top_level_keys(block_after(src, mo.end())):
            check(mo.group(1), k, f"{fn}:{line(mo.start())} InsertAsync")
    for mo in re.finditer(r'UpdateAsync\((?:[^,()]+,\s*)?"([A-Za-z]+)",[^;]*?new Dictionary<string, object\?>\s*\{', src):
        for k in top_level_keys(block_after(src, mo.end() - 1)):
            check(mo.group(1), k, f"{fn}:{line(mo.start())} UpdateAsync", write=True)
    for mo in re.finditer(r'UpdateAsync\("([A-Za-z]+)",\s*[^,]+,\s*patch', src):
        # patch dibangun dinamis: periksa key literal yang dimasukkan ke variabel patch di file yang sama
        for km in re.finditer(r'patch\["([a-z_]+)"\]|\["([a-z_]+)"\] = (?:d\.Str\("effective_date"\)|jc\.Name|newBoss)', src):
            k = km.group(1) or km.group(2)
            check(mo.group(1), k, f"{fn}:{line(mo.start())} UpdateAsync(patch)", write=True)
    for mo in re.finditer(r'\.(?:Str|Dec|Bool|Date|Rows|SetIfEmpty)\("([a-z_]+)"', src):
        if mo.group(1) not in ALL_FIELDS and mo.group(1) not in EVENT_KEYS:
            problems.append(f"{fn}:{line(mo.start())}: field '{mo.group(1)}' tidak ada di DocType mana pun")
    if os.sep + "Reports" + os.sep in path:
        continue  # kunci baris hasil laporan (row["..."]) bukan field DocType; akses .Data.Str(..) sudah dicek di atas
    for mo in re.finditer(r'\b(?:d|r|l|row|it|lead|q|s)\["([a-z_]+)"\]', src):
        if mo.group(1) not in ALL_FIELDS:
            problems.append(f"{fn}:{line(mo.start())}: field '{mo.group(1)}' tidak ada di DocType mana pun")

uniq = sorted(set(problems))
print(f"{len(glob.glob(os.path.join(ROOT, 'src', 'Apps.Erp', '**', '*.cs'), recursive=True))} file C#, {len(uniq)} masalah")
for p in uniq: print(" -", p)
sys.exit(1 if uniq else 0)
