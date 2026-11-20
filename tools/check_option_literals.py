"""Cocokkan literal nilai opsi di hook C# dengan opsi Select / state / aksi workflow di metadata.

Literal yang dibandingkan atau di-assign (== "X", != "X", is "X", or "X", => "X", ["f"] = "X", const string = "X")
harus ada di salah satu opsi field Select, nama state workflow, aksi workflow, atau nama DocType. Ini menangkap
nilai yang lupa diubah saat metadata diganti bahasa (mis. "Terbuka" vs "Open").
Pemakaian: python3 tools/check_option_literals.py [folder-repo-erp]
"""
import glob, json, os, re, sys

def metadata_dir(root):
    """Metadata ada di src/Apps.Erp/Metadata (repo) atau Metadata/ (paket lama)."""
    for cand in (os.path.join(root, "src", "Apps.Erp", "Metadata"), os.path.join(root, "Metadata")):
        if os.path.isdir(cand): return cand
    sys.exit(f"Folder metadata tidak ditemukan di {root}")

ROOT = sys.argv[1] if len(sys.argv) > 1 else os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
META = metadata_dir(ROOT)
values, names = set(), set()
for p in glob.glob(os.path.join(META, "*.doctype.json")) + glob.glob(os.path.join(META, "*.workflow.json")):
    d = json.load(open(p, encoding="utf-8"))
    names.add(d["name"])
    for f in d.get("fields", []):
        for o in f.get("options") or [] if isinstance(f.get("options"), list) else []:
            values.add(o if isinstance(o, str) else o.get("value"))
    for s in d.get("states", []): values.add(s["name"])
    for t in d.get("transitions", []): values.add(t["action"])

# Literal yang memang bukan nilai opsi (nama job, role, aksi audit, dsb.).
ALLOW = {"System Manager", "Finance", "HR", "Manager", "Sales", "IT", "General Affairs", "Approver delegated"}
PAT = re.compile(r'(?:==|!=|\bis|\bor|\bnot|=>|\]\s*=|const string \w+\s*=|,\s*\w+\s*=)\s*\(?"([A-Z][^"{}\n]*)"')
problems = []
for path in sorted(glob.glob(os.path.join(ROOT, "src", "Apps.Erp", "**", "*.cs"), recursive=True)):
    src = open(path, encoding="utf-8").read()
    # Laporan: pilihan filter laporan sendiri (ReportFilterDef ... "Select", [..]) bukan nilai metadata.
    local = {v for arr in re.findall(r'"Select",\s*\[([^\]]*)\]', src) for v in re.findall(r'"([^"]+)"', arr)}
    for mo in PAT.finditer(src):
        v = mo.group(1)
        before = src[max(0, mo.start() - 8):mo.start()].rstrip()
        if before.endswith("Label") or before.endswith("Title") or before.endswith("Description"): continue  # label tombol / judul laporan
        if v in local: continue
        if v in values or v in names or v in ALLOW or v.startswith("Erp."): continue
        if " " in v and v[0].isupper() and len(v.split()) > 4: continue  # kalimat pesan, bukan nilai opsi
        problems.append(f"{os.path.basename(path)}:{src.count(chr(10), 0, mo.start()) + 1}: '{v}' tidak ada di opsi metadata")

print(f"{len(values)} nilai opsi/state/aksi, {len(problems)} masalah")
for p in problems: print(" -", p)
sys.exit(1 if problems else 0)
