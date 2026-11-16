"""Periksa data seed SQL (deploy/sql/seed-phase-*.sql) terhadap metadata DocType.

Yang dicek per dokumen (termasuk baris child table):
  - DocType ada; field tidak dikenal (framework menolaknya saat dokumen disimpan ulang)
  - nilai Select ada di daftar pilihan; Check bertipe boolean; angka bertipe angka
  - field wajib (requiredWhen "true") terisi
  - Link menunjuk dokumen yang ada di seed (Link ke DocType yang tidak di-seed dilewati)
  - naming "field:x": nama dokumen = nilai field x
  - jurnal seimbang & akun kontrol hanya di jurnal pembukaan berpihak; root type akun = induk; akun piutang/utang = akun kontrol
  - dokumen ber-docstatus 1 untuk DocType yang memposting ke buku besar: seed SQL tidak membuat baris buku besar,
    jadi saldo & open item akan kosong (peringatan)
Pemakaian: python3 tools/check_seed.py [file.sql ...]   (default: semua deploy/sql/seed-phase-*.sql)
"""
import glob, json, os, re, sys

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
META = os.path.join(ROOT, "src", "Apps.Erp", "Metadata")
defs = {}
for p in glob.glob(os.path.join(META, "*.doctype.json")):
    d = json.load(open(p, encoding="utf-8"))
    defs[d["name"]] = d
STD = {"name", "owner", "created", "modified", "modified_by", "docstatus", "workflow_state", "version", "amended_from", "idx"}
POSTING = {"JournalEntry", "SalesInvoice", "SalesCreditNote", "PurchaseInvoice", "PurchaseDebitNote", "PaymentEntry", "ExpenseClaim",
           "EmployeeAdvance", "Asset", "DepreciationRun", "AssetDisposal", "PeriodClosingVoucher", "PaymentReconciliation", "PayrollRun",
           "CommissionStatement", "StockEntry", "StockReconciliation", "GoodsReceipt"}
NUM = {"Int", "Float", "Currency", "Percent"}

files = sys.argv[1:] or sorted(glob.glob(os.path.join(ROOT, "deploy", "sql", "seed-phase-*.sql")))
ROW = re.compile(r"VALUES\s*\(\s*'([^']*)'\s*,\s*'([^']*)'\s*,\s*'((?:[^']|'')*)'\s*,\s*'[^']*'\s*,\s*(\d+)\s*,.*?'((?:[^']|'')*)'::jsonb", re.S)
docs = []
for f in files:
    for m in ROW.finditer(open(f, encoding="utf-8").read()):
        tenant, dt, name, status, data = m.groups()
        try: payload = json.loads(data.replace("''", "'"))
        except json.JSONDecodeError as e: docs.append((os.path.basename(f), dt, name.replace("''", "'"), int(status), None, str(e))); continue
        docs.append((os.path.basename(f), dt, name.replace("''", "'"), int(status), payload, None))

existing = {(dt, n) for _, dt, n, _, _, _ in docs}
by_key = {(dt, n): data for _, dt, n, _, data, _ in docs if data is not None}

def filters_ok(f, target):
    """linkFilters sederhana (=, !=) dicek terhadap data dokumen tujuan."""
    for flt in f.get("linkFilters") or []:
        field, op, val = (flt.get("field"), flt.get("op") or flt.get("operator"), flt.get("value")) if isinstance(flt, dict) else flt
        if field in ("docstatus",) or op not in ("=", "!="): continue
        actual = target.get(field)
        if isinstance(val, bool): actual = bool(actual)
        if (op == "=" and actual != val) or (op == "!=" and actual == val): return f"{field} {op} {val!r} (sekarang {actual!r})"
    return None
problems, warnings = [], []
missing_defaults = {}

def check_fields(where, dt, data, child=False):
    d = defs.get(dt)
    if d is None: problems.append(f"{where}: DocType {dt} tidak ada"); return
    fields = {f["name"]: f for f in d["fields"]}
    for k, v in data.items():
        if k in STD: continue
        f = fields.get(k)
        if f is None: problems.append(f"{where}: field '{k}' tidak ada di {dt}"); continue
        t = f["type"]
        if v is None: continue
        if t == "Select" and isinstance(f.get("options"), list):
            opts = [o if isinstance(o, str) else o.get("value") for o in f["options"]]
            if v not in opts: problems.append(f"{where}: {dt}.{k} = '{v}' bukan pilihan {opts}")
        elif t == "Check" and not isinstance(v, bool): problems.append(f"{where}: {dt}.{k} harus true/false, bukan {v!r}")
        elif t in NUM and (isinstance(v, bool) or not isinstance(v, (int, float))): problems.append(f"{where}: {dt}.{k} harus angka, bukan {v!r}")
        elif t == "Link" and f.get("linkTo") in {x for x, _ in existing} and (f["linkTo"], v) not in existing:
            problems.append(f"{where}: {dt}.{k} → {f['linkTo']} '{v}' tidak ada di seed")
        elif t == "Link" and (f.get("linkTo"), v) in by_key and (bad := filters_ok(f, by_key[(f["linkTo"], v)])):
            problems.append(f"{where}: {dt}.{k} → {f['linkTo']} '{v}' tidak memenuhi filter: {bad}")
        elif t == "Table":
            if not isinstance(v, list): problems.append(f"{where}: {dt}.{k} harus array"); continue
            for i, row in enumerate(v): check_fields(f"{where} {k}[{i}]", f["childDocType"], row, True)
    for f in d["fields"]:
        if "default" in f and f["name"] not in data and not str(f["default"]).startswith("$"):
            missing_defaults.setdefault((dt, f["name"], json.dumps(f["default"])), set()).add(where.split(" ")[0])
        if f.get("requiredWhen") == "true" and not f.get("compute") and not f.get("fetchFrom") and data.get(f["name"]) in (None, "", []):
            problems.append(f"{where}: field wajib {dt}.{f['name']} kosong")

for src, dt, name, status, data, err in docs:
    where = f"{src} {dt} {name}"
    if err: problems.append(f"{where}: JSON rusak ({err})"); continue
    check_fields(where, dt, data)
    d = defs.get(dt)
    pattern = (d or {}).get("naming", {}).get("pattern", "")
    if pattern.startswith("field:") and str(data.get(pattern[6:])) != name:
        problems.append(f"{where}: nama dokumen harus sama dengan {pattern[6:]} = {data.get(pattern[6:])!r}")
    if status == 1 and dt in POSTING:
        warnings.append(f"{where}: docstatus 1 lewat SQL tidak membuat baris buku besar/stok — submit lewat aplikasi atau biarkan draft")

# Aturan bisnis yang dijaga hook saat simpan (SQL melewatinya): jurnal seimbang, root type akun = induk, piutang/utang = akun kontrol.
accounts = {n: d for (dt, n), d in by_key.items() if dt == "Account"}
for name, acc in accounts.items():
    parent = accounts.get(acc.get("parent_account") or "")
    if parent and parent.get("root_type") != acc.get("root_type"):
        problems.append(f"Account {name}: root type {acc.get('root_type')} berbeda dengan induk {parent and acc['parent_account']} ({parent.get('root_type')})")
    if acc.get("account_type") in ("Receivable", "Payable") and not acc.get("is_control_account"):
        problems.append(f"Account {name}: akun {acc['account_type']} harus akun kontrol (is_control_account)")
for (dt, n), d in by_key.items():
    if dt != "JournalEntry": continue
    rows = d.get("accounts") or []
    dr, cr = sum(r.get("debit") or 0 for r in rows), sum(r.get("credit") or 0 for r in rows)
    if round(dr - cr, 2) != 0: problems.append(f"JournalEntry {n}: tidak seimbang (debit {dr:,.2f} ≠ kredit {cr:,.2f})")
    for i, r in enumerate(rows):
        acc = accounts.get(r.get("account"))
        if acc and acc.get("is_control_account") and (d.get("voucher_type") != "Opening Balance" or not r.get("party")):
            problems.append(f"JournalEntry {n} accounts[{i}]: akun kontrol {r['account']} hanya untuk jurnal Opening Balance dengan pihak")

# SQL melewati framework: default field tidak diisi. Check bernilai default true yang hilang dibaca false oleh filter & UI.
for (dt, field, default), srcs in sorted(missing_defaults.items()):
    msg = f"{dt}.{field} tidak ada di seed ({', '.join(sorted(srcs))}); default metadata {default} tidak diterapkan oleh SQL"
    (problems if default == "true" else warnings).append(msg)
print(f"{len(docs)} dokumen seed dari {len(files)} file: {len(problems)} masalah, {len(warnings)} peringatan")
for p in problems: print(" -", p)
for w in warnings: print(" !", w)
sys.exit(1 if problems else 0)
