"""Simulasi logika posting hook (InvoiceCore.PostAsync, PaymentEntryHook) terhadap penjaga ledger frogframe
(Ledger.ValidateShape & Ledger.OpenItemViolation, port 1:1 dari Framework.Core/Accounting/Ledger.cs)."""
from decimal import Decimal as Dm, ROUND_HALF_UP

def R2(v): return Dm(v).quantize(Dm("0.01"), rounding=ROUND_HALF_UP)

LEDGER = []  # dict: voucher, account, dr, cr, party, against

class Posting:
    def __init__(s): s.lines = []
    def dr(s, acc, amt, party=None, against=None):
        amt = R2(amt)
        if amt == 0: return
        if amt < 0: return s.cr(acc, -amt, party, against)
        s.lines.append(dict(account=acc, dr=amt, cr=Dm(0), party=party, against=against))
    def cr(s, acc, amt, party=None, against=None):
        amt = R2(amt)
        if amt == 0: return
        if amt < 0: return s.dr(acc, -amt, party, against)
        s.lines.append(dict(account=acc, dr=Dm(0), cr=amt, party=party, against=against))
    def diff(s): return sum(l["dr"] for l in s.lines) - sum(l["cr"] for l in s.lines)
    def balance(s, acc, tol):
        d = s.diff()
        if d == 0: return
        assert abs(d) <= tol, f"tidak seimbang {d}"
        (s.cr if d > 0 else s.dr)(acc, abs(d))

def commit(voucher, posting):
    lines = [dict(l, voucher=voucher) for l in posting.lines]
    # ValidateShape
    for l in lines: assert (l["dr"] > 0) != (l["cr"] > 0), "satu sisi"
    assert sum(l["dr"] for l in lines) == sum(l["cr"] for l in lines), f"{voucher} tidak seimbang"
    # OpenItemViolation (dicek setelah baris baru ditambahkan)
    allrows = LEDGER + lines
    touched = {(l["against"], l["account"]) for l in lines if l["against"]}
    for (ag, acc) in touched:
        rows = [r for r in allrows if r["against"] == ag and r["account"] == acc]
        own = sum(r["dr"] - r["cr"] for r in rows if r["voucher"] == ag)
        total = sum(r["dr"] - r["cr"] for r in rows)
        if (own > 0 and total < 0) or (own < 0 and total > 0) or (own == 0 and total != 0):
            raise AssertionError(f"OpenItemViolation: pembayaran melebihi sisa tagihan {ag}")
    LEDGER.extend(lines)

def open_item(voucher, party):
    rows = [r for r in LEDGER if r["against"] == voucher and r["party"] == party]
    own = next(r for r in rows if r["voucher"] == voucher)
    return own["account"], sum(r["dr"] - r["cr"] for r in rows if r["account"] == own["account"])

# --- port InvoiceTotals.From + InvoiceCore.PostAsync ---
def post_invoice(name, kind, items, vat_rate=Dm(0), dpp=Dm(1), wht_rate=Dm(0), rate=Dm(1), party="P", return_against=None):
    is_sales = kind in ("SI", "SCN"); is_return = kind in ("SCN", "PDN")
    total = taxable = wht_base = Dm(0); lines = []
    for it in items:
        amt = R2(Dm(it["qty"]) * Dm(it["rate"]) * (1 - Dm(it.get("disc", 0)) / 100))
        total += amt; lines.append((it["acc"], amt))
        if it.get("vat", True): taxable += amt
        if it.get("wht", False): wht_base += amt
    tax_base = R2(taxable * dpp); vat = R2(tax_base * vat_rate / 100); wht = R2(wht_base * wht_rate / 100)
    grand = total + vat; payable = grand - wht
    B = lambda v: R2(v * rate)
    sign = (1 if is_sales else -1) * (-1 if is_return else 1)
    against = return_against if is_return else name
    party_acc = "1130-Piutang" if is_sales else "2110-Utang"
    p = Posting()
    side = (lambda a, v, **k: p.dr(a, v, **k)) if sign > 0 else (lambda a, v, **k: p.cr(a, v, **k))
    other = (lambda a, v: p.cr(a, v)) if sign > 0 else (lambda a, v: p.dr(a, v))
    same = (lambda a, v: p.dr(a, v)) if sign > 0 else (lambda a, v: p.cr(a, v))
    side(party_acc, B(grand if is_sales else payable), party=party, against=against)
    for acc, amt in lines: other(acc, B(amt))
    if vat: other("2310-PPN Keluaran" if is_sales else "1410-PPN Masukan", B(vat))
    if wht: same("2320-Utang PPh23", B(wht))
    p.balance("8900-Pembulatan", Dm(1))
    commit(name, p)
    return dict(grand=grand, payable=payable, vat=vat, wht=wht, rate=rate)

# --- port PaymentEntryHook.AfterSaveAsync (Terima/Bayar) ---
def post_payment(name, typ, paid, refs, deductions=(), rate=Dm(1), ref_rates=None, party="P", party_acc=None, base_ccy=True):
    receive = typ == "Receive"; B = lambda v: R2(v * rate); p = Posting()
    (p.dr if receive else p.cr)("1110-Bank", B(paid))
    for acc, amt in deductions: (p.dr if receive else p.cr)(acc, B(amt))
    alloc_total = Dm(0)
    for ref, alloc in refs:
        acc, net = open_item(ref, party)
        assert (net > 0) if receive else (net < 0), f"arah saldo {ref}"
        rr = (ref_rates or {}).get(ref, Dm(1)); open_base = abs(net); outstanding = R2(open_base / rr)
        assert alloc <= outstanding, f"melebihi sisa {ref} ({outstanding})"
        clear = open_base if alloc == outstanding else R2(alloc * rr)
        (p.cr if receive else p.dr)(acc, clear, party=party, against=ref); alloc_total += alloc
    unalloc = paid + sum(a for _, a in deductions) - alloc_total
    assert unalloc >= 0
    if unalloc > 0: (p.cr if receive else p.dr)(party_acc, B(unalloc), party=party, against=name)
    p.balance("7900-Selisih Kurs" if not base_ccy else "8900-Pembulatan", Dm(10**9) if not base_ccy else Dm(1))
    commit(name, p)

def tb():
    out = {}
    for r in LEDGER: out[r["account"]] = out.get(r["account"], Dm(0)) + r["dr"] - r["cr"]
    return {k: v for k, v in sorted(out.items()) if v != 0}

def expect_fail(fn, what):
    try: fn(); raise SystemExit(f"GAGAL: seharusnya ditolak: {what}")
    except AssertionError as e: print(f"  ✓ ditolak ({what}): {e}")

print("1) Penjualan PPN 12% DPP 11/12, bayar sebagian dipotong PPh23, nota kredit, pelunasan")
si = post_invoice("SINV-1", "SI", [dict(qty=3, rate="1000000.33", acc="4100-Jasa", disc="2.5"), dict(qty=1, rate="250000", acc="4200-Lain", vat=False)],
                  vat_rate=Dm(12), dpp=Dm(11) / Dm(12), party="CUST-1")
print("   grand", si["grand"], "vat", si["vat"], "open", open_item("SINV-1", "CUST-1")[1])
post_payment("PAY-1", "Receive", Dm("1000000"), [("SINV-1", Dm("1040000"))], deductions=[("1420-PPh23 Dibayar Dimuka", Dm("40000"))], party="CUST-1")
post_invoice("SCN-1", "SCN", [dict(qty=1, rate="100000", acc="4100-Jasa")], vat_rate=Dm(12), dpp=Dm(11) / Dm(12), party="CUST-1", return_against="SINV-1")
_, rest = open_item("SINV-1", "CUST-1"); print("   sisa setelah bayar+nota", rest)
expect_fail(lambda: post_payment("PAY-X", "Receive", rest + 1, [("SINV-1", rest + 1)], party="CUST-1"), "lebih bayar")
post_payment("PAY-2", "Receive", rest, [("SINV-1", rest)], party="CUST-1")
assert open_item("SINV-1", "CUST-1")[1] == 0; print("   lunas ✓")

print("2) Pembelian jasa PPN + PPh23, bayar penuh, kelebihan jadi uang muka")
pi = post_invoice("PINV-1", "PI", [dict(qty=1, rate="10000000", acc="6100-Jasa Konsultan", wht=True), dict(qty=2, rate="150000", acc="6200-ATK")],
                  vat_rate=Dm(12), dpp=Dm(11) / Dm(12), wht_rate=Dm(2), party="SUPP-1")
print("   grand", pi["grand"], "pph", pi["wht"], "utang", pi["payable"])
post_payment("PAY-3", "Pay", pi["payable"] + 500000, [("PINV-1", pi["payable"])], party="SUPP-1", party_acc="2110-Utang")
assert open_item("PINV-1", "SUPP-1")[1] == 0; print("   lunas ✓, uang muka", open_item("PAY-3", "SUPP-1")[1])

print("3) Nota debit melebihi sisa utang harus ditolak store")
post_invoice("PINV-2", "PI", [dict(qty=1, rate="1000000", acc="6200-ATK")], party="SUPP-2")
post_payment("PAY-4", "Pay", Dm("900000"), [("PINV-2", Dm("900000"))], party="SUPP-2")
expect_fail(lambda: post_invoice("PDN-1", "PDN", [dict(qty=1, rate="200000", acc="6200-ATK")], party="SUPP-2", return_against="PINV-2"), "nota > sisa")

print("4) Valas USD: faktur kurs 15.500, dibayar kurs 16.000 → selisih kurs")
post_invoice("SINV-USD", "SI", [dict(qty=3, rate="333.33", acc="4100-Jasa")], rate=Dm(15500), party="CUST-USD")
post_payment("PAY-USD1", "Receive", Dm("500"), [("SINV-USD", Dm("500"))], rate=Dm(16000), ref_rates={"SINV-USD": Dm(15500)}, party="CUST-USD", base_ccy=False)
_, net = open_item("SINV-USD", "CUST-USD"); outstanding = R2(net / Dm(15500))
post_payment("PAY-USD2", "Receive", outstanding, [("SINV-USD", outstanding)], rate=Dm(16000), ref_rates={"SINV-USD": Dm(15500)}, party="CUST-USD", base_ccy=False)
assert open_item("SINV-USD", "CUST-USD")[1] == 0; print("   lunas tanpa sisa sen ✓")

print("5) Neraca saldo seimbang:", sum(tb().values()) == 0)
for k, v in tb().items(): print(f"   {k:30} {v:>20,}")

# ---------------------------------------------------------------------------------------------
# Batch 2: uang muka karyawan → klaim biaya; penyusutan; tutup buku
# ---------------------------------------------------------------------------------------------
def open_dir(voucher, party, want_debit):
    """Port OpenItems.GetForDirectionAsync."""
    rows = [r for r in LEDGER if r["against"] == voucher and r["party"] == party]
    for acc in dict.fromkeys(r["account"] for r in rows if r["voucher"] == voucher):
        on = [r for r in rows if r["account"] == acc]
        own = sum(r["dr"] - r["cr"] for r in on if r["voucher"] == voucher)
        if own != 0 and (own > 0) == want_debit: return acc, sum(r["dr"] - r["cr"] for r in on)
    return None

def pay_ref(name, typ, amount, ref, party):
    """Port PaymentEntry untuk satu referensi (mata uang fungsional)."""
    receive = typ == "Receive"
    item = open_dir(ref, party, receive)
    assert item and ((item[1] > 0) if receive else (item[1] < 0)), f"{ref} tidak punya sisa yang bisa {'diterima' if receive else 'dibayar'}"
    assert amount <= abs(item[1]), f"melebihi sisa {ref}"
    p = Posting()
    (p.dr if receive else p.cr)("1110-Bank", amount)
    (p.cr if receive else p.dr)(item[0], amount, party=party, against=ref)
    commit(name, p)

print("6) Uang muka karyawan Rp1.000.000 → klaim Rp900.000 (potong uang muka Rp700.000) → bayar sisa klaim → kembalikan sisa uang muka")
p = Posting(); p.dr("1180-Uang Muka Karyawan", Dm(1000000), party="EMP-1", against="ADV-1"); p.cr("2120-Utang Karyawan", Dm(1000000), party="EMP-1", against="ADV-1"); commit("ADV-1", p)
pay_ref("PAY-ADV", "Pay", Dm(1000000), "ADV-1", "EMP-1")
p = Posting(); p.dr("6300-Perjalanan Dinas", Dm(900000)); p.cr("2120-Utang Karyawan", Dm(200000), party="EMP-1", against="EXP-1")
p.cr("1180-Uang Muka Karyawan", Dm(700000), party="EMP-1", against="ADV-1"); commit("EXP-1", p)
pay_ref("PAY-EXP", "Pay", Dm(200000), "EXP-1", "EMP-1")
expect_fail(lambda: pay_ref("PAY-RET-X", "Receive", Dm(300001), "ADV-1", "EMP-1"), "kembali > sisa uang muka")
pay_ref("PAY-RET", "Receive", Dm(300000), "ADV-1", "EMP-1")
assert open_dir("ADV-1", "EMP-1", True) is None or open_dir("ADV-1", "EMP-1", True)[1] == 0
assert open_dir("EXP-1", "EMP-1", False)[1] == 0
print("   semua open item uang muka & klaim nol ✓")
p = Posting(); p.dr("6300-Perjalanan Dinas", Dm(1)); p.cr("1180-Uang Muka Karyawan", Dm(1), party="EMP-1", against="ADV-1")
expect_fail(lambda: commit("EXP-X", p), "klaim melebihi sisa uang muka (penjaga store)")

def schedule(method, remaining, life, gross, salvage, opening):
    """Port AssetBook.Schedule."""
    rows, nbv, rate = [], gross - opening, Dm(2) / Dm(life)
    dep = gross - salvage - opening
    for k in range(remaining):
        left = remaining - k
        if k == remaining - 1: amt = nbv - salvage
        elif method == "Declining Balance": amt = min(max(R2(nbv * rate), R2((nbv - salvage) / left)), nbv - salvage)
        else: amt = R2(dep / remaining)
        nbv -= amt; rows.append(amt)
    return rows, nbv

print("7) Jadwal penyusutan")
for method, life, gross, salvage, opening, remaining in [("Straight Line", 48, Dm("17999999.99"), Dm("1000000"), Dm(0), 48),
                                                          ("Declining Balance", 96, Dm("250000000"), Dm("5000000"), Dm(0), 96),
                                                          ("Straight Line", 60, Dm("60000000"), Dm(0), Dm("24000000"), 36)]:
    rows, final = schedule(method, remaining, life, gross, salvage, opening)
    assert all(r >= 0 for r in rows) and final == salvage and sum(rows) == gross - salvage - opening
    print(f"   {method:13} {life} bln: {len(rows)} baris, total {sum(rows):,}, nilai akhir = nilai sisa {final:,} ✓ (bulan 1 {rows[0]:,}, terakhir {rows[-1]:,})")

print("8) Tutup buku: saldo laba rugi dinolkan ke laba ditahan")
pl = lambda a: a[0] in "4567"
bal = tb(); p = Posting(); profit = Dm(0)
for acc, net in bal.items():
    if not pl(acc): continue
    (p.cr if net > 0 else p.dr)(acc, abs(net)); profit -= net
(p.cr if profit > 0 else p.dr)("3200-Laba Ditahan", abs(profit)); commit("PCV-2026", p)
assert all(v == 0 for k, v in tb().items() if pl(k)) and sum(tb().values()) == 0
print(f"   laba bersih {profit:,} pindah ke laba ditahan; akun laba rugi nol; neraca saldo seimbang ✓")

# ---------------------------------------------------------------------------------------------
# Batch 3: Payment Reconciliation (alokasi uang muka pihak ke faktur)
# ---------------------------------------------------------------------------------------------
def reconcile(name, party, is_customer, allocations, check=True):
    """Port PaymentReconciliationHook: validasi saldo lalu Dr/Cr akun kontrol yang sama (vs pembayaran & vs faktur)."""
    sign = Dm(-1) if is_customer else Dm(1)
    p = Posting(); used_pay, used_inv = {}, {}
    for pay, inv, amt in allocations:
        try: acc_p, net_p = open_item(pay, party); acc_i, net_i = open_item(inv, party)
        except StopIteration: raise AssertionError(f"{pay}/{inv} bukan milik {party}")
        advance, outstanding = sign * net_p, -sign * net_i
        if check:
            assert acc_p == acc_i, "akun kontrol berbeda"
            used_pay[pay] = used_pay.get(pay, Dm(0)) + amt; used_inv[inv] = used_inv.get(inv, Dm(0)) + amt
            assert used_pay[pay] <= advance, f"melebihi uang muka {pay}"
            assert used_inv[inv] <= outstanding, f"melebihi sisa tagihan {inv}"
        if is_customer: p.dr(acc_p, amt, party, pay); p.cr(acc_i, amt, party, inv)
        else: p.cr(acc_p, amt, party, pay); p.dr(acc_i, amt, party, inv)
    commit(name, p)

print("9) Payment Reconciliation: uang muka pihak dialokasikan ke faktur berikutnya")
post_invoice("SINV-9", "SI", [dict(qty=1, rate="1000000", acc="4100-Jasa")], party="CUST-9")
post_payment("PAY-9", "Receive", Dm("1500000"), [], party="CUST-9", party_acc="1130-Piutang")
assert open_item("PAY-9", "CUST-9")[1] == Dm("-1500000.00")
expect_fail(lambda: reconcile("PREC-0", "CUST-9", True, [("PAY-9", "SINV-9", Dm("1200000"))]), "alokasi > sisa tagihan")
expect_fail(lambda: reconcile("PREC-X", "CUST-9", True, [("PAY-9", "SINV-9", Dm("1200000"))], check=False), "penjaga ledger (tanpa cek hook)")
reconcile("PREC-1", "CUST-9", True, [("PAY-9", "SINV-9", Dm("1000000"))])
assert open_item("SINV-9", "CUST-9")[1] == 0 and open_item("PAY-9", "CUST-9")[1] == Dm("-500000.00")
print("   pelanggan: faktur lunas, sisa uang muka 500,000 ✓")
post_invoice("PINV-9", "PI", [dict(qty=1, rate="300000", acc="6200-ATK")], party="SUPP-1")
reconcile("PREC-2", "SUPP-1", False, [("PAY-3", "PINV-9", Dm("300000"))])
assert open_item("PINV-9", "SUPP-1")[1] == 0 and open_item("PAY-3", "SUPP-1")[1] == Dm("200000.00")
expect_fail(lambda: reconcile("PREC-3", "SUPP-1", False, [("PAY-3", "PINV-2", Dm("100"))]), "pemasok lain / akun lain")
assert sum(tb().values()) == 0
print("   pemasok: uang muka PAY-3 500,000 → 300,000 dipakai PINV-9, sisa 200,000 ✓; neraca saldo seimbang ✓")

# ---------------------------------------------------------------------------------------------
# Batch 4: laporan (port aritmetika ErpReports: neraca, laba rugi tanpa jurnal tutup buku, aging)
# ---------------------------------------------------------------------------------------------
print("10) Laporan: neraca seimbang, laba rugi mengeluarkan jurnal tutup buku, aging = sisa faktur")
ROOT = {"1": "Asset", "2": "Liability", "3": "Equity", "4": "Income", "5": "Expense", "6": "Expense", "7": "Income"}
def balances(exclude_pcv):
    out = {}
    for r in LEDGER:
        if exclude_pcv and r["voucher"].startswith("PCV"): continue
        out[r["account"]] = out.get(r["account"], Dm(0)) + r["dr"] - r["cr"]
    return out
b = balances(False)
assets = sum(v for k, v in b.items() if ROOT[k[0]] == "Asset")
liab = -sum(v for k, v in b.items() if ROOT[k[0]] == "Liability")
equity = -sum(v for k, v in b.items() if ROOT[k[0]] == "Equity")
unclosed = -sum(v for k, v in b.items() if ROOT[k[0]] in ("Income", "Expense"))
assert assets == liab + equity + unclosed, (assets, liab, equity, unclosed)
print(f"   neraca: aset {assets:,} = kewajiban {liab:,} + ekuitas {equity:,} + laba belum ditutup {unclosed:,} ✓")
pl = balances(True)
income = -sum(v for k, v in pl.items() if ROOT[k[0]] == "Income"); expense = sum(v for k, v in pl.items() if ROOT[k[0]] == "Expense")
after = unclosed  # laba sesudah tutup buku (skenario 9: SINV-9 1,000,000 − PINV-9 300,000)
assert after == Dm("700000.00") and income - expense == profit + after, (income - expense, profit, after)
print(f"   laba rugi tanpa PCV: {income - expense:,} = laba yang ditutup {profit:,} + laba sesudahnya {after:,} ✓")
assert open_item("PINV-2", "SUPP-2")[1] == Dm("-100000.00")
print("   aging utang: PINV-2 sisa 100,000 (faktur 1,000,000 − bayar 900,000) ✓")

# ---------------------------------------------------------------------------------------------
# Batch 5: Payroll (port PayrollCalc + PayslipHook + PayrollRunHook)
# ---------------------------------------------------------------------------------------------
import math
TER_A = [(5400000,0),(5650000,0.25),(5950000,0.5),(6300000,0.75),(6750000,1),(7500000,1.25),(8550000,1.5),(9650000,1.75),(10050000,2),
         (10350000,2.25),(10700000,2.5),(11050000,3),(11600000,3.5),(12500000,4),(13750000,5),(15100000,6),(16950000,7),(19750000,8),
         (24150000,9),(26450000,10),(28000000,11),(30050000,12),(32400000,13),(35400000,14),(39100000,15),(43850000,16),(47800000,17),
         (51400000,18),(56300000,19),(62200000,20),(68600000,21),(77500000,22),(89000000,23),(103000000,24),(125000000,25),(157000000,26),
         (206000000,27),(337000000,28),(454000000,29),(550000000,30),(695000000,31),(910000000,32),(1400000000,33),(0,34)]
ART17 = [(60000000,5),(250000000,15),(500000000,25),(5000000000,30),(0,35)]
def ter_rate(table, gross):
    for up, rate in sorted(table, key=lambda t: float("inf") if t[0] == 0 else t[0]):
        if up == 0 or gross <= up: return Dm(str(rate))
def art17(pkp):
    tax, lower = Dm(0), Dm(0)
    for up, rate in sorted(ART17, key=lambda t: float("inf") if t[0] == 0 else t[0]):
        if pkp <= lower: break
        top = pkp if up == 0 else min(pkp, Dm(up)); tax += (top - lower) * Dm(rate) / 100; lower = pkp if up == 0 else Dm(up)
    return Dm(math.floor(tax))
rup = lambda v: Dm(v).quantize(Dm(1), rounding=ROUND_HALF_UP)

def payslip(month, basic, allowance, bonus=0, earlier=(), ptkp=Dm(54000000), jp_cap=Dm(11086300), kes_cap=Dm(12000000)):
    gross = Dm(basic + allowance + bonus); base = Dm(basic + allowance)
    jht_ee, jht_er = rup(base * Dm("0.02")), rup(base * Dm("0.037")); jkk, jkm = rup(base * Dm("0.0024")), rup(base * Dm("0.003"))
    jpb = min(base, jp_cap); jp_ee, jp_er = rup(jpb * Dm("0.01")), rup(jpb * Dm("0.02"))
    kb = min(base, kes_cap); kes_ee, kes_er = rup(kb * Dm("0.01")), rup(kb * Dm("0.04"))
    bruto = gross + jkk + jkm + kes_er
    if month < 12:
        pph = Dm(math.floor(bruto * ter_rate(TER_A, bruto) / 100))
    else:
        yg = sum(e["bruto"] for e in earlier) + bruto; months = len(earlier) + 1
        bj = min(yg * Dm("0.05"), Dm(500000) * months)
        pension = sum(e["pension"] for e in earlier) + jht_ee + jp_ee
        pkp = max(Dm(0), Dm(math.floor((yg - bj - pension - ptkp) / 1000)) * 1000)
        pph = art17(pkp) - sum(e["pph"] for e in earlier)
    ee = jht_ee + jp_ee + kes_ee; er = jht_er + jp_er + jkk + jkm + kes_er
    return dict(bruto=bruto, pph=pph, pension=jht_ee + jp_ee, gross=gross, ee=ee, er=er, net=gross - ee - pph,
                tk=jht_ee + jht_er + jp_ee + jp_er + jkk + jkm, kes=kes_ee + kes_er)

print("11) Payroll: TER Jan–Nov + Pasal 17 Desember, BPJS, jurnal payroll seimbang")
# Sanity resmi: bruto 10 jt/bulan tanpa BPJS, TK/0 (kategori A, tarif 2%) → setahun PKP 60 jt → PPh 3 jt.
assert ter_rate(TER_A, Dm(10000000)) == 2 and art17(Dm(60000000)) == Dm(3000000)
assert ter_rate(TER_A, Dm(5400000)) == 0 and ter_rate(TER_A, Dm(5400001)) == Dm("0.25") and ter_rate(TER_A, Dm(2000000000)) == 34
slips = []
for mth in range(1, 13):
    slips.append(payslip(mth, 12000000, 3000000, bonus=15000000 if mth == 3 else 0, earlier=list(slips)))
annual_check = slips[-1]
year_pph = sum(s["pph"] for s in slips)
print(f"   gaji 15 jt (pokok 12 jt + tunjangan 3 jt) + THR 15 jt Maret, TK/0: PPh Jan {slips[0]['pph']:,}, Mar {slips[2]['pph']:,}, Des {slips[11]['pph']:,}, setahun {year_pph:,}")
# PPh setahun = Pasal 17 atas PKP tahunan (independen dari pemotongan bulanan)
yg = sum(s["bruto"] for s in slips); bj = min(yg * Dm("0.05"), Dm(6000000)); pen = sum(s["pension"] for s in slips)
assert year_pph == art17(max(Dm(0), Dm(math.floor((yg - bj - pen - Dm(54000000)) / 1000)) * 1000)); print("   total PPh setahun = Pasal 17 atas PKP tahunan ✓")
# Jurnal payroll satu bulan (2 karyawan)
p = Posting()
for emp, s in [("EMP-1", slips[0]), ("EMP-2", payslip(1, 6000000, 0))]:
    p.dr("6110-Beban Gaji", s["gross"]); p.dr("6120-Beban BPJS", s["er"])
    p.cr("2150-Utang Gaji", s["net"], emp, "PR-1")
    p.cr("2310-Utang PPh21", s["pph"]); p.cr("2410-Utang BPJS TK", s["tk"]); p.cr("2420-Utang BPJS Kes", s["kes"])
commit("PR-1", p)
assert sum(tb().values()) == 0
print("   jurnal payroll seimbang; utang gaji per karyawan menjadi open item PR-1 ✓")

# ---------------------------------------------------------------------------------------------
# Batch 6: lembur PP 35/2021 (port Overtime.Weigh / PayAsync)
# ---------------------------------------------------------------------------------------------
print("12) Lembur PP 35/2021: pengali per jenis hari, upah sejam 1/173, aturan 75%")
def tiers(h, t2): return min(h, t2) * 2 + max(0, min(h - t2, 1)) * 3 + max(0, min(h - t2 - 1, 3)) * 4
def weigh(day, week, h):
    if day == "work": return min(h, 1) * Dm("1.5") + max(h - 1, 0) * 2
    if day == "shortest": return tiers(h, 5)
    return tiers(h, 8) if week == 5 else tiers(h, 7)
cases = [("work", 6, Dm(1), Dm("1.5")), ("work", 6, Dm(3), Dm("5.5")), ("work", 5, Dm(4), Dm("7.5")),
         ("rest", 6, Dm(7), Dm(14)), ("rest", 6, Dm(8), Dm(17)), ("rest", 6, Dm(11), Dm(29)),
         ("rest", 5, Dm(8), Dm(16)), ("rest", 5, Dm(9), Dm(19)), ("rest", 5, Dm(12), Dm(31)),
         ("shortest", 6, Dm(5), Dm(10)), ("shortest", 6, Dm(6), Dm(13)), ("shortest", 6, Dm(9), Dm(25))]
for day, week, h, want in cases: assert weigh(day, week, h) == want, (day, week, h, weigh(day, week, h))
print(f"   {len(cases)} kasus pengali cocok (mis. hari istirahat 6 hari kerja 11 jam = 7×2 + 1×3 + 3×4 = 29 jam dibayar) ✓")
fixed, total = Dm(4000000), Dm(6000000)  # pokok+tunjangan tetap 4 jt, total upah 6 jt → 66,7% < 75%
monthly = total * Dm("0.75") if fixed < total * Dm("0.75") else fixed
pay = rup(Dm("5.5") * monthly / 173)
assert monthly == Dm(4500000) and pay == Dm(143064)
print(f"   upah tetap 4 jt dari total 6 jt → dasar 75% = 4,5 jt; lembur 3 jam hari kerja (5,5 jam dibayar) = {pay:,} ✓")

# ---------------------------------------------------------------------------------------------
# Batch 7: kontrol anggaran & komisi (port BudgetControl / CommissionStatementHook)
# ---------------------------------------------------------------------------------------------
print("13) Anggaran year-to-date dan komisi penjualan")
def budget_check(annual, actual, amount, month, ytd, months=12):
    limit = (annual * month / months).quantize(Dm("0.01")) if ytd else annual
    return actual + amount <= limit, limit
ok, lim = budget_check(Dm(12000000), Dm(2500000), Dm(400000), 3, True)
assert ok and lim == Dm(3000000)
ok, _ = budget_check(Dm(12000000), Dm(2800000), Dm(400000), 3, True); assert not ok
ok, _ = budget_check(Dm(12000000), Dm(2800000), Dm(400000), 3, False); assert ok
print("   anggaran 12 jt: Maret YTD batas 3 jt → 2,5 jt + 0,4 jt lolos, 2,8 jt + 0,4 jt ditolak; basis tahunan lolos ✓")
inv = [Dm("10000000"), Dm("5000000")]; cn = [Dm("2000000")]; rate = Dm("2.5")
comm = sum(rup(x * rate / 100) for x in inv) - sum(rup(x * rate / 100) for x in cn)
assert comm == Dm(325000)
p = Posting(); p.dr("6300-Beban Komisi", comm); p.cr("2160-Utang Komisi", comm, "EMP-1", "CMS-1"); commit("CMS-1", p)
assert open_item("CMS-1", "EMP-1")[1] == Dm(-325000) and sum(tb().values()) == 0
print(f"   komisi 2,5% × (10 jt + 5 jt − nota kredit 2 jt) = {comm:,}; utang komisi per sales menjadi open item ✓")

# ---------------------------------------------------------------------------------------------
# Batch 8: proporsi gaji, 3-way match, skor kinerja
# ---------------------------------------------------------------------------------------------
print("14) Proporsi gaji, 3-way match, rating kinerja")
working, employed, unpaid = Dm(22), Dm(15), Dm(2)   # masuk tgl 10: 15 hari kerja, cuti tanpa upah 2 hari
payable = employed - unpaid; basic = rup(Dm(9000000) * payable / working)
assert payable == 13 and basic == Dm(5318182)
print(f"   gaji 9 jt, 22 hari kerja, masuk pertengahan (15 hari) − cuti tanpa upah 2 = 13 hari → {basic:,} ✓")
po_lines = [(Dm(10), Dm(150000), Dm(0)), (Dm(1), Dm(2000000), Dm(10))]   # qty, rate, diskon %
received = {1: Dm(6), 2: Dm(1)}
recv_value = sum((received[i + 1] * r * (1 - dsc / 100)).quantize(Dm("0.01")) for i, (q, r, dsc) in enumerate(po_lines))
assert recv_value == Dm(2700000)
assert Dm(3300000) > recv_value + 1 and not (Dm(2700000) > recv_value + 1)
print(f"   PO 3,3 jt, diterima 6/10 + 1/1 → nilai diterima {recv_value:,}; tagihan 3,3 jt ditandai, 2,7 jt lolos ✓")
def rating(sc): return "Outstanding" if sc >= Dm("4.5") else "Exceeds" if sc >= Dm("3.5") else "Meets" if sc >= Dm("2.5") else "Needs improvement" if sc >= Dm("1.5") else "Unsatisfactory"
goals = [(Dm(50), 4), (Dm(30), 3), (Dm(20), 5)]
score = sum(w * s / 100 for w, s in goals)
assert sum(w for w, _ in goals) == 100 and score == Dm("3.9") and rating(score) == "Exceeds"
print(f"   bobot 50/30/20, nilai 4/3/5 → skor {score} → Exceeds expectations ✓")

# ---------------------------------------------------------------------------------------------
# Batch 9: persediaan rata-rata bergerak (port StockPlan / StockEntryHook / SalesStockHook / opname)
# ---------------------------------------------------------------------------------------------
print("15) Persediaan: rata-rata bergerak, HPP, transfer, opname, larangan posting mundur, stok = buku besar")
BINS, MOVES = {}, []
class StockErr(AssertionError): pass
def bin_(i, w): return BINS.setdefault((i, w), {"q": Dm(0), "v": Dm(0), "last": 0})
def s_in(i, w, q, v, day):
    b = bin_(i, w)
    if b["last"] > day: raise StockErr(f"posting mundur {i}/{w}")
    b["q"] += q; b["v"] += v; b["last"] = day; MOVES.append((day, i, w, q, v))
def s_out(i, w, q, day):
    b = bin_(i, w)
    if b["last"] > day: raise StockErr(f"posting mundur {i}/{w}")
    if q > b["q"]: raise StockErr(f"stok {i} di {w} hanya {b['q']}")
    v = b["v"] if q == b["q"] else (q * b["v"] / b["q"]).quantize(Dm("0.01"), rounding=ROUND_HALF_UP)
    b["q"] -= q; b["v"] -= v; b["last"] = day; MOVES.append((day, i, w, -q, -v)); return v
INV, GRNI, COGS, ADJ = "1150-Persediaan", "2170-Barang Diterima Belum Ditagih", "5100-HPP", "5900-Selisih Persediaan"
# GR 10 @ 100.000 (PO), GR 10 @ 130.000
for n, (q, r, day) in enumerate([(Dm(10), Dm(100000), 1), (Dm(10), Dm(130000), 2)], 1):
    v = q * r; s_in("SKU-1", "WH-A", q, v, day); p = Posting(); p.dr(INV, v); p.cr(GRNI, v); commit(f"GRN-{n}", p)
assert bin_("SKU-1", "WH-A")["v"] / bin_("SKU-1", "WH-A")["q"] == Dm(115000)
# faktur pemasok untuk GR pertama (harga sama) membersihkan GRNI
p = Posting(); p.dr(GRNI, Dm(1000000)); p.cr("2110-Utang", Dm(1000000), "SUPP-9", "PINV-S1"); commit("PINV-S1", p)
# jual 5 (update stock): HPP rata-rata 115.000
cogs = s_out("SKU-1", "WH-A", Dm(5), 3); p = Posting(); p.dr(COGS, cogs); p.cr(INV, cogs); commit("SINV-S1", p)
assert cogs == Dm(575000)
# transfer 5 ke WH-B (akun persediaan sama → jurnal bersih nol)
tv = s_out("SKU-1", "WH-A", Dm(5), 4); s_in("SKU-1", "WH-B", Dm(5), tv, 4)
# opname WH-A: sistem 10, hitung 9 → selisih −115.000
b = bin_("SKU-1", "WH-A"); diff = (Dm(9) * b["v"] / b["q"]).quantize(Dm("0.01")) - b["v"]; s_in("SKU-1", "WH-A", Dm(-1), diff, 5)
p = Posting(); p.dr(INV, diff); p.cr(ADJ, diff); commit("SOP-1", p)
assert diff == Dm(-115000)
expect_fail(lambda: s_out("SKU-1", "WH-A", Dm(1), 4), "posting mundur sebelum opname")
expect_fail(lambda: s_out("SKU-1", "WH-B", Dm(6), 6), "keluar melebihi stok")
stock_value = sum(b["v"] for b in BINS.values())
gl_inv = tb().get(INV, Dm(0))
assert stock_value == gl_inv == Dm(1610000), (stock_value, gl_inv)
assert tb().get(GRNI) == Dm(-1300000)  # GR kedua belum ditagih
print(f"   rata-rata 115.000 → HPP 5 unit {cogs:,}; transfer; opname −1 unit {diff:,}; nilai stok {stock_value:,} = saldo akun persediaan; GRNI sisa 1,3 jt (GR kedua belum ditagih) ✓")
assert sum(tb().values()) == 0
