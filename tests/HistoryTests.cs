using Xunit;

namespace YaDiskFileSync.Tests;

public class HistoryTests
{
    [Fact]
    public void SnapshotName_KeepsExtension() =>
        Assert.Equal("Бухгалтерия 2026-10-22.xlsx", History.SnapshotName("Бухгалтерия.xlsx", new DateTime(2026, 10, 22, 23, 59, 0)));

    [Fact]
    public void SnapshotName_NameWithDots() =>
        Assert.Equal("Учёт v2.1 2026-01-05.xlsm", History.SnapshotName("Учёт v2.1.xlsm", new DateTime(2026, 1, 5)));

    [Theory]
    [InlineData("Бухгалтерия 2026-10-22.xlsx", true)]
    [InlineData("бухгалтерия 2026-10-22.XLSX", true)]      // Диск не различает регистр — и мы тоже
    [InlineData("Бухгалтерия 2026-13-40.xlsx", false)]     // не дата
    [InlineData("Бухгалтерия (2) 2026-10-22.xlsx", false)] // чужой файл
    [InlineData("Бухгалтерия 2026-10-22.xls", false)]
    [InlineData("Бухгалтерия.xlsx", false)]
    public void SnapshotDate_OnlyOurNames(string candidate, bool ours) =>
        Assert.Equal(ours, History.SnapshotDate("Бухгалтерия.xlsx", candidate) != null);

    [Fact]
    public void SnapshotDate_RegexCharsInName() =>
        Assert.Equal(new DateTime(2026, 10, 1), History.SnapshotDate("a+b (1).xlsx", "a+b (1) 2026-10-01.xlsx"));

    [Fact]
    public void Excess_KeepsNewest_IgnoresForeign()
    {
        var names = Enumerable.Range(1, 35).Select(d => History.SnapshotName("Б.xlsx", new DateTime(2026, 1, 1).AddDays(d))).ToList();
        names.Add("Чужой 2020-01-01.xlsx");
        names.Add("Б заметки.txt");
        var excess = History.Excess("Б.xlsx", names, 30);
        Assert.Equal(5, excess.Count);
        Assert.All(excess, n => Assert.True(History.SnapshotDate("Б.xlsx", n) <= new DateTime(2026, 1, 6)));
        Assert.DoesNotContain("Чужой 2020-01-01.xlsx", excess);
    }

    [Fact]
    public void Excess_NothingWhenUnderLimit() =>
        Assert.Empty(History.Excess("Б.xlsx", new[] { "Б 2026-01-01.xlsx", "Б 2026-01-02.xlsx" }, 30));

    [Fact]
    public void Excess_KeepAtLeastOne() =>
        Assert.Equal(new[] { "Б 2026-01-01.xlsx" }, History.Excess("Б.xlsx", new[] { "Б 2026-01-01.xlsx", "Б 2026-01-02.xlsx" }, 0));
}
