using System.Text;

namespace Framework.Printing;

/// <summary>Menuliskan angka dalam kata-kata Bahasa Indonesia, mis. 2650000 → "Dua juta enam ratus lima puluh ribu rupiah".</summary>
public static class Terbilang
{
    private static readonly string[] Satuan =
        ["", "satu", "dua", "tiga", "empat", "lima", "enam", "tujuh", "delapan", "sembilan", "sepuluh", "sebelas"];

    public static string Rupiah(decimal amount)
    {
        var whole = (long)Math.Floor(Math.Abs(amount));
        var cents = (int)Math.Round((Math.Abs(amount) - whole) * 100);
        var sb = new StringBuilder(whole == 0 ? "nol" : Words(whole));
        sb.Append(" rupiah");
        if (cents > 0) sb.Append(' ').Append(Words(cents)).Append(" sen");
        var text = (amount < 0 ? "minus " : "") + sb.ToString();
        return char.ToUpperInvariant(text[0]) + text[1..];
    }

    public static string Words(long n) => Build(n).Trim().Replace("  ", " ");

    private static string Build(long n) => n switch
    {
        < 12 => Satuan[n],
        < 20 => Build(n - 10) + " belas",
        < 100 => Build(n / 10) + " puluh " + Build(n % 10),
        < 200 => "seratus " + Build(n - 100),
        < 1_000 => Build(n / 100) + " ratus " + Build(n % 100),
        < 2_000 => "seribu " + Build(n - 1_000),
        < 1_000_000 => Build(n / 1_000) + " ribu " + Build(n % 1_000),
        < 1_000_000_000 => Build(n / 1_000_000) + " juta " + Build(n % 1_000_000),
        < 1_000_000_000_000 => Build(n / 1_000_000_000) + " miliar " + Build(n % 1_000_000_000),
        _ => Build(n / 1_000_000_000_000) + " triliun " + Build(n % 1_000_000_000_000)
    };
}
