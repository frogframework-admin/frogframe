using BlazorBlueprint.Components;

namespace Framework.Web;

/// <summary>Teks bawaan komponen BlazorBlueprint dalam Bahasa Indonesia.</summary>
public static class BbIndonesian
{
    public static void Apply(DefaultBbLocalizer l)
    {
        l.Set("Alert.Dismiss", "Tutup");
        l.Set("Calendar.GoToPreviousMonth", "Bulan sebelumnya");
        l.Set("Calendar.GoToNextMonth", "Bulan berikutnya");
        l.Set("Combobox.EmptyMessage", "Tidak ada hasil.");
        l.Set("Combobox.Placeholder", "Pilih…");
        l.Set("Combobox.SearchPlaceholder", "Cari…");
        l.Set("DatePicker.Placeholder", "Pilih tanggal");
        l.Set("DatePicker.OpenCalendar", "Buka kalender");
        l.Set("Dialog.Close", "Tutup");
        l.Set("Pagination.Pagination", "Halaman");
        l.Set("Pagination.Previous", "Sebelumnya");
        l.Set("Pagination.Next", "Berikutnya");
        l.Set("Pagination.MorePages", "Halaman lain");
        l.Set("Pagination.GoToFirstPage", "Ke halaman pertama");
        l.Set("Pagination.GoToLastPage", "Ke halaman terakhir");
        l.Set("Pagination.ShowingFormat", "{0}–{1} dari {2}");
        l.Set("Pagination.PageFormat", "Halaman {0} dari {1}");
        l.Set("Pagination.NoItems", "Tidak ada data");
        l.Set("Select.ChooseOption", "Pilih salah satu");
        l.Set("Sidebar.PillNavigation", "Navigasi utama");
        l.Set("Sidebar.ExpandNavigation", "Buka navigasi");
        l.Set("Sidebar.ToggleSidebar", "Buka/tutup sidebar");
    }
}
