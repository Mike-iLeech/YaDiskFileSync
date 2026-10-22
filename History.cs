using System.Globalization;
using System.Text.RegularExpressions;

namespace YaDiskFileSync;

/// <summary>
/// Дневные копии: «Бухгалтерия 2026-10-22.xlsx» в папке «История». За день копия одна —
/// каждое сохранение перезаписывает копию этого дня, так что 30 копий = 30 последних дней работы.
/// </summary>
internal static class History
{
    public const string Folder = "app:/История";

    public static string SnapshotName(string fileName, DateTime localDate) =>
        $"{Path.GetFileNameWithoutExtension(fileName)} {localDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}{Path.GetExtension(fileName)}";

    /// <summary>Дата копии этого файла по имени; null — имя чужое (другой файл или переименовано руками).</summary>
    public static DateTime? SnapshotDate(string fileName, string candidate)
    {
        string stem = Regex.Escape(Path.GetFileNameWithoutExtension(fileName));
        string ext = Regex.Escape(Path.GetExtension(fileName));
        var m = Regex.Match(candidate, $"^{stem} (\\d{{4}}-\\d{{2}}-\\d{{2}}){ext}$", RegexOptions.IgnoreCase);
        return m.Success && DateTime.TryParseExact(m.Groups[1].Value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)
            ? d : null;
    }

    /// <summary>Какие копии этого файла лишние: все, кроме <paramref name="keep"/> самых свежих.</summary>
    public static IReadOnlyList<string> Excess(string fileName, IEnumerable<string> existing, int keep) =>
        existing
            .Select(n => (Name: n, Date: SnapshotDate(fileName, n)))
            .Where(x => x.Date != null)
            .OrderByDescending(x => x.Date)
            .ThenByDescending(x => x.Name, StringComparer.Ordinal)
            .Skip(Math.Max(1, keep))
            .Select(x => x.Name)
            .ToList();
}
