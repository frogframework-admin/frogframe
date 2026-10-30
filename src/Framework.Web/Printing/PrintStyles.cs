namespace Framework.Web.Printing;

/// <summary>
/// CSS cetak A4. Disimpan sebagai string agar halaman cetak dan PDF (HTML mandiri, tanpa akses jaringan)
/// memakai gaya yang persis sama. Semua selector di bawah .pf agar tidak bentrok dengan UI aplikasi.
/// </summary>
public static class PrintStyles
{
    public const string Css = """
@page { size: A4; margin: 16mm 15mm 18mm 15mm;
  @bottom-right { content: "Halaman " counter(page) " dari " counter(pages); font: 8pt "Public Sans", "Segoe UI", Arial, "Liberation Sans", "DejaVu Sans", sans-serif; color: #6b7280; } }
.pf { --ink: #111827; --muted: #6b7280; --line: #d1d5db; --soft: #f3f4f6; --accent: #3f3d9a;
  --pending: #a16207; --positive: #15803d; --negative: #b91c1c;
  font-family: "Public Sans", "Segoe UI", "Helvetica Neue", Arial, "Liberation Sans", "DejaVu Sans", sans-serif;
  color: var(--ink); font-size: 9.5pt; line-height: 1.45; -webkit-print-color-adjust: exact; print-color-adjust: exact; }
.pf * { box-sizing: border-box; }
.pf .sheet { position: relative; background: #fff; }
.pf .lh { display: flex; align-items: center; gap: 12px; padding-bottom: 10px; border-bottom: 2px solid var(--ink); }
.pf .lh-logo { width: 44px; height: 44px; flex-shrink: 0; }
.pf .lh-logo svg { width: 100%; height: 100%; }
.pf .lh-name { font-size: 13pt; font-weight: 800; letter-spacing: .01em; }
.pf .lh-meta { font-size: 8pt; color: var(--muted); }
.pf .head { display: flex; justify-content: space-between; align-items: flex-start; gap: 16px; margin: 16px 0 14px; }
.pf .title { font-size: 15pt; font-weight: 800; letter-spacing: .06em; text-transform: uppercase; margin: 0; }
.pf .docno { font-size: 10.5pt; font-weight: 600; margin-top: 2px; }
.pf .docmeta { font-size: 8.5pt; color: var(--muted); margin-top: 2px; }
.pf .stamp { display: inline-block; font-weight: 800; letter-spacing: .08em; text-transform: uppercase; font-size: 9.5pt;
  color: var(--accent); border: 2px solid currentColor; border-radius: 4px; padding: 4px 10px;
  box-shadow: inset 0 0 0 2px #fff, inset 0 0 0 3px currentColor; transform: rotate(-4deg); white-space: nowrap; margin-top: 4px; }
.pf .stamp.tone-pending { color: var(--pending); } .pf .stamp.tone-positive { color: var(--positive); } .pf .stamp.tone-negative { color: var(--negative); }
.pf .sec { margin-bottom: 12px; break-inside: avoid; }
.pf .sec-title { font-size: 8pt; font-weight: 700; letter-spacing: .08em; text-transform: uppercase; color: var(--muted);
  border-bottom: 1px solid var(--line); padding-bottom: 3px; margin: 0 0 6px; }
.pf .kv { display: grid; grid-template-columns: 1fr 1fr; column-gap: 24px; row-gap: 4px; }
.pf .kv .row { display: grid; grid-template-columns: 38% 62%; gap: 8px; }
.pf .kv .row.wide { grid-column: 1 / -1; grid-template-columns: 19% 81%; }
.pf .kv .k { color: var(--muted); }
.pf .kv .v { font-weight: 500; white-space: pre-wrap; }
.pf table.items { width: 100%; border-collapse: collapse; font-variant-numeric: tabular-nums; margin-top: 2px; }
.pf table.items th { text-align: left; font-size: 8pt; font-weight: 700; color: var(--muted); background: var(--soft);
  border-top: 1px solid var(--line); border-bottom: 1px solid var(--line); padding: 5px 6px; }
.pf table.items td { padding: 5px 6px; border-bottom: 1px solid var(--line); vertical-align: top; }
.pf table.items .num { text-align: right; white-space: nowrap; }
.pf table.items .no { width: 26px; color: var(--muted); }
.pf table.items tr { break-inside: avoid; }
.pf table.items thead { display: table-header-group; }
.pf .totals { display: flex; justify-content: flex-end; margin-top: 6px; }
.pf .totals table { border-collapse: collapse; min-width: 45%; }
.pf .totals td { padding: 4px 6px; }
.pf .totals td:last-child { text-align: right; font-weight: 800; font-size: 11pt; font-variant-numeric: tabular-nums; white-space: nowrap; }
.pf .words { margin-top: 6px; padding: 6px 8px; background: var(--soft); border-left: 3px solid var(--accent); font-style: italic; }
.pf .signs { display: grid; grid-template-columns: repeat(3, 1fr); gap: 14px; margin-top: 22px; break-inside: avoid; }
.pf .sign { border: 1px solid var(--line); border-radius: 4px; padding: 8px 10px; min-height: 92px; display: flex; flex-direction: column; }
.pf .sign-label { font-size: 8pt; color: var(--muted); }
.pf .sign-mark { margin: 6px 0 4px; font-size: 7.5pt; font-weight: 700; letter-spacing: .06em; color: var(--positive); text-transform: uppercase; }
.pf .sign-name { font-weight: 700; margin-top: auto; border-top: 1px solid var(--line); padding-top: 4px; }
.pf .sign-meta { font-size: 7.5pt; color: var(--muted); }
.pf .sign.empty .sign-name { color: var(--muted); font-weight: 400; }
.pf .note { margin-top: 16px; font-size: 8pt; color: var(--muted); }
.pf .running { display: none; font-size: 7.5pt; color: var(--muted); }
.pf .watermark { position: fixed; inset: 0; display: flex; align-items: center; justify-content: center; pointer-events: none; z-index: 0; }
.pf .watermark span { font-size: 82pt; font-weight: 800; letter-spacing: .08em; color: rgba(17, 24, 39, .06); transform: rotate(-30deg); white-space: nowrap; }
.pf .content { position: relative; z-index: 1; }
@media screen {
  .pf .sheet { width: 210mm; min-height: 297mm; margin: 0 auto 24px; padding: 16mm 15mm 18mm; box-shadow: 0 2px 6px rgba(0,0,0,.08), 0 12px 32px rgba(0,0,0,.10); }
  .pf .watermark { position: absolute; }
  .pf .running { display: block; margin-top: 18px; padding-top: 6px; border-top: 1px dashed var(--line); }
}
""";
}
