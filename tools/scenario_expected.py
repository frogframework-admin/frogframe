"""Angka yang diharapkan untuk skenario uji docs/ERP_TEST_SCENARIOS.md, dihitung dari data seed (deploy/sql/seed-phase-*.sql).

- Payroll Januari 2026: port aritmetika PayslipHook (BPJS, PPh 21 TER per kategori PTKP, lembur PP 35/2021) dari tabel di Payroll Settings seed.
- Jurnal tiap langkah skenario (port aturan posting hook: faktur, retur, pembayaran, GR/GRNI, HPP rata-rata bergerak, klaim, uang muka,
  payroll, payment run, penyusutan, opname, komisi) → neraca saldo per 31-01-2026 dan setelah langkah Februari.
- Memastikan angka yang ditulis tetap di seed fase 8 (JV-2026-000003, rekening koran BST-2026-00001, PAY-2026-000003) sama dengan hasil hitung.

Pemakaian (dari root repo): python3 tools/scenario_expected.py [--markdown]
"""
import glob, json, os, re, sys
from collections import defaultdict
from decimal import Decimal as Dm, ROUND_FLOOR, ROUND_HALF_UP

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
ROW = re.compile(r"VALUES\s*\(\s*'([^']*)'\s*,\s*'([^']*)'\s*,\s*'((?:[^']|'')*)'\s*,\s*'[^']*'\s*,\s*(\d+)\s*,.*?'((?:[^']|'')*)'::jsonb", re.S)
docs = {}
for f in sorted(glob.glob(os.path.join(ROOT, "deploy", "sql", "seed-phase-*.sql"))):
    for m in ROW.finditer(open(f, encoding="utf-8").read()):
        docs[(m.group(2), m.group(3).replace("''", "'"))] = json.loads(m.group(5).replace("''", "'"), parse_float=Dm, parse_int=Dm)

def of(dt): return {n: d for (t, n), d in docs.items() if t == dt}
rup = lambda v: Dm(v).quantize(Dm(1), rounding=ROUND_HALF_UP)
r2 = lambda v: Dm(v).quantize(Dm("0.01"), rounding=ROUND_HALF_UP)
fmt = lambda v: (lambda v: f"{v:,.0f}".replace(",", ".") if v == v.to_integral_value() else f"{v:,.2f}".replace(",", "X").replace(".", ",").replace("X", "."))(Dm(v))

# ------------------------------------------------------------------------------------------------
# Payroll Januari 2026
# ------------------------------------------------------------------------------------------------
S = docs[("PayrollSettings", "PayrollSettings")]
comps, emps = of("SalaryComponent"), of("Employee")
sas = {d["employee"]: d for d in of("SalaryAssignment").values()}
ptkp = {r["ptkp_status"]: (r["amount"], r["ter_category"]) for r in S["ptkp_rates"]}

def ter(cat, gross):
    rows = sorted([(r.get("up_to") or Dm(0), r["rate"]) for r in S["ter_rates"] if r["ter_category"] == cat], key=lambda t: Dm("1e30") if t[0] == 0 else t[0])
    for up, rate in rows:
        if up == 0 or gross <= up: return rate

def overtime(emp, weighted):
    """Overtime.PayAsync: upah sejam = (pendapatan regular dasar BPJS, minimal 75% pendapatan regular) / 173."""
    reg = fixed = Dm(0)
    for r in sas[emp]["earnings"]:
        c = comps[r["salary_component"]]
        if not c.get("is_regular"): continue
        reg += r["amount"]
        if c.get("in_bpjs_base"): fixed += r["amount"]
    monthly = reg * Dm("0.75") if fixed < reg * Dm("0.75") else fixed
    return monthly / 173, rup(weighted * monthly / 173)

def payslip(emp, extra=()):
    a, e = sas[emp], emps[emp]
    earn = [(r["salary_component"], Dm(r["amount"])) for r in a["earnings"]] + list(extra)
    ded = [(r["salary_component"], Dm(r["amount"])) for r in a.get("deductions") or []]
    gross = sum(v for _, v in earn)
    taxable = sum(v for c, v in earn if comps[c].get("is_taxable", True))
    base = sum(v for c, v in earn if comps[c].get("in_bpjs_base"))
    rate = lambda f: S[f] / 100
    jht_ee, jht_er = rup(base * rate("jht_employee_rate")), rup(base * rate("jht_employer_rate"))
    jkk, jkm = rup(base * rate("jkk_rate")), rup(base * rate("jkm_rate"))
    jpb = min(base, S["jp_wage_cap"]); jp_ee, jp_er = rup(jpb * rate("jp_employee_rate")), rup(jpb * rate("jp_employer_rate"))
    kb = min(base, S["kes_wage_cap"]); kes_ee, kes_er = rup(kb * rate("kes_employee_rate")), rup(kb * rate("kes_employer_rate"))
    _, cat = ptkp[e["ptkp_status"]]
    bruto = taxable + jkk + jkm + kes_er
    ter_rate = ter(cat, bruto)
    pph = (bruto * ter_rate / 100).to_integral_value(rounding=ROUND_FLOOR)
    ee, er, other = jht_ee + jp_ee + kes_ee, jht_er + jp_er + jkk + jkm + kes_er, sum(v for _, v in ded)
    return dict(emp=emp, name=e["full_name"], ptkp=e["ptkp_status"], cat=cat, cc=e["cost_center"], earn=earn, ded=ded, gross=gross, base=base,
                bruto=bruto, ter=ter_rate, pph=pph, ee=ee, er=er, other=other, net=gross - ee - pph - other,
                tk=jht_ee + jht_er + jp_ee + jp_er + jkk + jkm, kes=kes_ee + kes_er)

OT_WEIGHTED = Dm("1.5") + 2 * Dm(2)  # OT-2026-000001: hari kerja 3 jam = jam ke-1 ×1,5 + 2 jam ×2
ot_hourly, ot_amount = overtime("EMP-0010", OT_WEIGHTED)
slips = [payslip(e, [("Uang Lembur", ot_amount)] if e == "EMP-0010" else ()) for e in sorted(sas)]
T = lambda k: sum(s[k] for s in slips)

# ------------------------------------------------------------------------------------------------
# Buku besar skenario
# ------------------------------------------------------------------------------------------------
LEDGER = []
def post(voucher, date, lines):
    dr = sum(l[1] for l in lines if l[1] > 0); cr = sum(-l[1] for l in lines if l[1] < 0)
    assert dr == cr, f"{voucher} tidak seimbang: {dr} vs {cr}"
    for acc, amt, *party in lines:
        LEDGER.append(dict(voucher=voucher, date=date, account=acc, amount=Dm(amt), party=party[0] if party else None))
    return lines

STEPS = []  # (langkah, voucher, tanggal, baris)
def step(label, voucher, date, lines): STEPS.append((label, voucher, date, post(voucher, date, lines)))

jv1 = docs[("JournalEntry", "JV-2026-000001")]
step("Jurnal saldo awal", "JV-2026-000001", "2026-01-01",
     [(r["account"], Dm(r["debit"]) - Dm(r["credit"]), r.get("party")) for r in jv1["accounts"]])
step("Saldo awal stok", "STE-2026-000001", "2026-01-02", [("1131", Dm(125000000)), ("3200", -Dm(125000000))])

# Stok rata-rata bergerak per (item, gudang)
BIN = defaultdict(lambda: [Dm(0), Dm(0)])
def s_in(item, wh, q, v): b = BIN[(item, wh)]; b[0] += q; b[1] += v
def s_out(item, wh, q):
    b = BIN[(item, wh)]; rate = b[1] / b[0]; v = r2(q * rate); b[0] -= q; b[1] -= v; return v
UT, TR = "Gudang Utama Cikarang", "Gudang Transit Jakarta"
for r in docs[("StockEntry", "STE-2026-000001")]["items"]: s_in(r["item"], UT, Dm(r["qty"]), Dm(r["amount"]))

def sales(name):
    d = docs[("SalesInvoice", name)]
    total = sum(Dm(r["amount"]) for r in d["items"]); vat = r2(total * Dm(12) / 100)
    lines = [("1121", total + vat, d["customer"])] + [(r["income_account"], -Dm(r["amount"])) for r in d["items"]] + [("2151", -vat)]
    cogs = Dm(0)
    if d.get("update_stock"):
        for r in d["items"]:
            v = s_out(r["item"], d["warehouse"], Dm(r["qty"])); r["valuation_rate"] = v / Dm(r["qty"]); cogs += v
        lines += [("5100", cogs), ("1131", -cogs)]
    return total, vat, cogs, lines

t1, v1, c1, l1 = sales("SINV-2026-000001")
step("Faktur penjualan Telkom + HPP", "SINV-2026-000001", "2026-01-16", l1)
step("Uang muka karyawan disetujui", "ADV-2026-00001", "2026-01-08", [("1123", Dm(3000000), "EMP-0002"), ("2110", -Dm(3000000), "EMP-0002")])
step("Pencairan uang muka (Payment Entry)", "PE uang muka", "2026-01-09", [("2110", Dm(3000000), "EMP-0002"), ("1112", -Dm(3000000))])
step("Pelunasan Telkom", "PAY-2026-000001", "2026-01-22", [("1112", t1 + v1), ("1121", -(t1 + v1), "CUST-000001")])
s_in("ITM-SRV-001", UT, Dm(1), Dm(35000000))
step("Penerimaan barang (GR)", "GRN-2026-000001", "2026-01-24", [("1131", Dm(35000000)), ("2170", -Dm(35000000))])
step("Faktur pemasok Metrodata (PO)", "PINV-2026-000001", "2026-01-24", [("2170", Dm(35000000)), ("1151", Dm(4200000)), ("2110", -Dm(39200000), "SUPP-000001")])
step("Pelunasan Metrodata", "PAY-2026-000002", "2026-01-28", [("2110", Dm(39200000), "SUPP-000001"), ("1112", -Dm(39200000))])
exp1 = sum(Dm(r["amount"]) for r in docs[("ExpenseClaim", "EXP-2026-000001")]["items"])
step("Klaim biaya + penyelesaian uang muka", "EXP-2026-000001", "2026-01-25",
     [("6400", exp1), ("2110", -(exp1 - Dm(3000000)), "EMP-0002"), ("1123", -Dm(3000000), "EMP-0002")])
t2, v2, c2, l2 = sales("SINV-2026-000002")
step("Faktur penjualan BCA + HPP", "SINV-2026-000002", "2026-01-26", l2)
pinv2 = docs[("PurchaseInvoice", "PINV-2026-000002")]
p2_total = sum(Dm(r["amount"]) for r in pinv2["items"]); p2_vat = r2(p2_total * Dm(12) / 100); p2_wht = r2(p2_total * Dm(2) / 100)
step("Faktur jasa JNE (PPN + PPh 23)", "PINV-2026-000002", "2026-01-26",
     [("6420", p2_total), ("1151", p2_vat), ("2110", -(p2_total + p2_vat - p2_wht), "SUPP-000003"), ("2152", -p2_wht)])
s_out("ITM-SRV-001", UT, Dm(1)); s_in("ITM-SRV-001", TR, Dm(1), Dm(35000000))  # STE-2026-000002: akun persediaan sama → tanpa jurnal
exp2 = docs[("ExpenseClaim", "EXP-2026-000002")]["items"]
step("Klaim biaya IT (peringatan anggaran)", "EXP-2026-000002", "2026-01-27",
     [(r["account"], Dm(r["amount"])) for r in exp2] + [("2110", -sum(Dm(r["amount"]) for r in exp2), "EMP-0001")])
pr_lines = []
for s in slips:
    for c, v in s["earn"]: pr_lines.append((comps[c]["account"], v))
    pr_lines.append(("6110", s["er"]))
    for c, v in s["ded"]: pr_lines.append((comps[c]["account"], -v, s["emp"]))
    pr_lines.append(("2130", -s["net"], s["emp"]))
pr_lines += [("2150", -T("pph")), ("2140", -T("tk")), ("2141", -T("kes"))]
step("Payroll Januari", "PR-2026-00001", "2026-01-28", pr_lines)
# Retur: HPP mengikuti valuation_rate faktur asal
scn = docs[("SalesCreditNote", "SCN-2026-00001")]
scn_total = sum(Dm(r["amount"]) for r in scn["items"]); scn_vat = r2(scn_total * Dm(12) / 100)
orig_rate = {r["item"]: r["valuation_rate"] for r in docs[("SalesInvoice", "SINV-2026-000002")]["items"]}
scn_cost = sum(r2(Dm(r["qty"]) * orig_rate[r["item"]]) for r in scn["items"])
for r in scn["items"]: s_in(r["item"], UT, Dm(r["qty"]), r2(Dm(r["qty"]) * orig_rate[r["item"]]))
step("Retur penjualan BCA", "SCN-2026-00001", "2026-01-29",
     [("4100", scn_total), ("2151", scn_vat), ("1121", -(scn_total + scn_vat), "CUST-000002"), ("1131", scn_cost), ("5100", -scn_cost)])
t3, v3, c3, l3 = sales("SINV-2026-000003")
step("Faktur jasa Astra (batas kredit)", "SINV-2026-000003", "2026-01-30", l3)
prun = {}
for s in slips: prun[s["emp"]] = s["net"]
prun["EMP-0002"] += exp1 - Dm(3000000)
prun["EMP-0001"] += sum(Dm(r["amount"]) for r in exp2)
prun["SUPP-000003"] = p2_total + p2_vat - p2_wht
pr_pay = [("2130", s["net"], s["emp"]) for s in slips] + [("2110", exp1 - Dm(3000000), "EMP-0002"), ("2110", sum(Dm(r["amount"]) for r in exp2), "EMP-0001"),
                                                         ("2110", prun["SUPP-000003"], "SUPP-000003"), ("1112", -sum(prun.values()))]
step("Payment Run (gaji, klaim, pemasok)", "PRUN-2026-00001", "2026-01-30", pr_pay)
ast = docs[("Asset", "AST-2026-00001")]
dep = r2((Dm(ast["gross_purchase_amount"]) - Dm(ast["salvage_value"]) - Dm(ast["opening_accumulated_depreciation"])) / 36)
step("Penyusutan Januari", "DEP-2026-00001", "2026-01-31", [("6300", dep), ("1211", -dep)])
cab = BIN[("ITM-CAB-001", UT)]; cab_rate = cab[1] / cab[0]
sop_diff = r2(Dm(5) * cab_rate) - cab[1]; cab_before = (cab[0], cab[1]); cab[0], cab[1] = Dm(5), r2(Dm(5) * cab_rate)
step("Stock opname (kabel kurang 1 box)", "SOP-2026-00001", "2026-01-31", [("1131", sop_diff), ("5200", -sop_diff)])
cms_base = t1 + t2 - scn_total
cms = sum(rup(b * 2 / 100) for b in (t1, t2, -scn_total))
step("Komisi Citra Januari", "CMS-2026-00001", "2026-01-31", [("6500", cms), ("2180", -cms, "EMP-0002")])
step("Biaya admin bank", "JV-2026-000002", "2026-01-31", [("8400", Dm(25000)), ("1112", -Dm(25000))])
JAN_END = len(LEDGER)
tax_pay = [("2140", T("tk")), ("2141", T("kes")), ("2150", T("pph")), ("2152", p2_wht)]
step("Setor BPJS, PPh 21 & PPh 23", "JV-2026-000003", "2026-02-10", tax_pay + [("1112", -sum(v for _, v in tax_pay))])
pay3 = t2 + v2 - scn_total - scn_vat
step("Pelunasan BCA (setelah retur)", "PAY-2026-000003", "2026-02-10", [("1112", pay3), ("1121", -pay3, "CUST-000002")])
step("Bayar komisi (Payment Entry)", "PE komisi", "2026-02-13", [("2180", cms, "EMP-0002"), ("1112", -cms)])

accounts = of("Account")
def tb(rows):
    bal = defaultdict(Dm)
    for l in rows: bal[l["account"]] += l["amount"]
    return {a: v for a, v in sorted(bal.items()) if v != 0}
def party_bal(rows, acc):
    bal = defaultdict(Dm)
    for l in rows:
        if l["account"] == acc and l["party"]: bal[l["party"]] += l["amount"]
    return {p: v for p, v in sorted(bal.items()) if v != 0}

# ------------------------------------------------------------------------------------------------
# Cocokkan dengan angka tetap di seed fase 8
# ------------------------------------------------------------------------------------------------
errors = []
jv3 = {r["account"]: Dm(r["debit"]) - Dm(r["credit"]) for r in docs[("JournalEntry", "JV-2026-000003")]["accounts"]}
for acc, v in tax_pay + [("1112", -sum(v for _, v in tax_pay))]:
    if jv3.get(acc) != v: errors.append(f"JV-2026-000003 {acc}: seed {jv3.get(acc)} ≠ hitung {v}")
if Dm(docs[("PaymentEntry", "PAY-2026-000003")]["paid_amount"]) != pay3: errors.append(f"PAY-2026-000003: seed ≠ hitung {pay3}")
bst = docs[("BankStatement", "BST-2026-00001")]
bank_jan = sum(l["amount"] for l in LEDGER[:JAN_END] if l["account"] == "1112")
if Dm(bst["closing_balance"]) != bank_jan: errors.append(f"BST-2026-00001 saldo akhir {bst['closing_balance']} ≠ buku besar 1112 per 31-01 {bank_jan}")
wd = sorted(Dm(l["withdrawal"]) for l in bst["lines"] if Dm(l["withdrawal"]) > 0)
expected_wd = sorted([Dm(3000000), Dm(39200000), Dm(25000)] + list(prun.values()))
if wd != expected_wd: errors.append(f"BST-2026-00001 baris debet {wd} ≠ {expected_wd}")

if __name__ == "__main__":
    md = "--markdown" in sys.argv
    print("Payroll Januari 2026 (PR-2026-00001)")
    print(f"  Lembur EMP-0010: {OT_WEIGHTED} jam dibayar × upah sejam {fmt(r2(ot_hourly))} = {fmt(ot_amount)}")
    for s in slips:
        print(f"  {s['emp']} {s['name']:<15} {s['ptkp']:<4} TER {s['cat']} {s['ter']}%  bruto {fmt(s['gross'])}  dasar BPJS {fmt(s['base'])}  bruto pajak {fmt(s['bruto'])}"
              f"  PPh21 {fmt(s['pph'])}  BPJS pegawai {fmt(s['ee'])}  pemberi kerja {fmt(s['er'])}  potongan lain {fmt(s['other'])}  NETO {fmt(s['net'])}")
    print(f"  TOTAL bruto {fmt(T('gross'))}  pemberi kerja {fmt(T('er'))}  PPh21 {fmt(T('pph'))}  BPJS TK {fmt(T('tk'))}  BPJS Kes {fmt(T('kes'))}  neto {fmt(T('net'))}")
    print()
    for label, v, date, lines in sorted(STEPS, key=lambda x: x[2]):
        print(f"{date} {v} — {label}")
        agg = defaultdict(Dm)
        for acc, amt, *p in lines: agg[(acc, p[0] if p else None)] += Dm(amt)
        for (acc, party), amt in agg.items():
            if amt == 0: continue
            name = accounts.get(acc, {}).get("account_name", "")
            print(f"    {'Dr' if amt > 0 else 'Cr'} {acc} {name[:38]:<38} {fmt(abs(amt)):>15}{'  (' + party + ')' if party else ''}")
    for title, rows in (("Neraca saldo per 31-01-2026", LEDGER[:JAN_END]), ("Neraca saldo setelah langkah Februari", LEDGER)):
        print(f"\n{title}")
        b = tb(rows); assert sum(b.values()) == 0
        for acc, v in b.items(): print(f"  {acc} {accounts[acc]['account_name'][:42]:<42} {'Dr' if v > 0 else 'Cr'} {fmt(abs(v)):>15}")
        print(f"  {'Jumlah debit = kredit':<47} {fmt(sum(v for v in b.values() if v > 0)):>18}")
        print(f"  Piutang per pelanggan: { {p: fmt(v) for p, v in party_bal(rows, '1121').items()} }")
        print(f"  Utang usaha per pihak: { {p: fmt(v) for p, v in party_bal(rows, '2110').items()} }")
    print("\nStok per 31-01-2026")
    for (item, wh), (q, v) in sorted(BIN.items()):
        if q: print(f"  {item} @ {wh}: qty {q:g}, nilai {fmt(v)}")
    print(f"  Opname kabel: sistem {cab_before[0]:g} box ({fmt(cab_before[1])}) → hitung 5 → selisih {fmt(sop_diff)}")
    print(f"\nKomisi Citra: dasar {fmt(cms_base)} × 2% = {fmt(cms)};  Penyusutan/bulan {fmt(dep)};  Payment Run total {fmt(sum(prun.values()))}")
    print(f"Laba (rugi) Januari: {fmt(-sum(v for a, v in tb(LEDGER[:JAN_END]).items() if accounts[a]['report_type'] == 'Profit and Loss'))}")
    print("\nCek angka tetap seed fase 8:", "OK" if not errors else "")
    for e in errors: print("  ✗", e)
    sys.exit(1 if errors else 0)
