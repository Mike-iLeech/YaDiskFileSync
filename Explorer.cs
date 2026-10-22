using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace YaDiskFileSync;

/// <summary>
/// Папка синхронизации в панели навигации Проводника — так же, как у Яндекс.Диска и OneDrive
/// («Integrate a Cloud Storage Provider»): своя запись CLSID в HKCU, которая показывает обычную папку.
/// Прав администратора не нужно. Удаляется программой (--unregister) при удалении через установщик.
/// </summary>
internal static class Explorer
{
    public const string Clsid = "{6B1E4A27-3C9D-4F8E-9A51-2D7C0E8B4F13}";
    public const string Title = "YaDiskSync";
    private const string DelegateFolder = "{0E5AAE11-A475-4c5b-AB00-C66DE400274E}";
    private static readonly string[] ClassRoots = { @"Software\Classes\CLSID", @"Software\Classes\Wow6432Node\CLSID" };
    private const string NameSpace = @"Software\Microsoft\Windows\CurrentVersion\Explorer\Desktop\NameSpace";
    private const string HideDesktop = @"Software\Microsoft\Windows\CurrentVersion\Explorer\HideDesktopIcons\NewStartPanel";

    /// <summary>Добавить (или перенаправить на новую папку) пункт панели навигации.</summary>
    public static void Register(string folder, string iconExe)
    {
        foreach (string root in ClassRoots)
        {
            using var k = Registry.CurrentUser.CreateSubKey($@"{root}\{Clsid}");
            k.SetValue("", Title);
            k.SetValue("System.IsPinnedToNameSpaceTree", 1, RegistryValueKind.DWord);
            k.SetValue("SortOrderIndex", 0x42, RegistryValueKind.DWord);
            using (var icon = k.CreateSubKey("DefaultIcon")) icon.SetValue("", $"\"{iconExe}\",0");
            using (var srv = k.CreateSubKey("InProcServer32"))
                srv.SetValue("", @"%SystemRoot%\system32\shell32.dll", RegistryValueKind.ExpandString);
            using (var inst = k.CreateSubKey("Instance"))
            {
                inst.SetValue("CLSID", DelegateFolder);
                using var bag = inst.CreateSubKey("InitPropertyBag");
                bag.SetValue("Attributes", 0x11, RegistryValueKind.DWord);   // FILE_ATTRIBUTE_DIRECTORY | READONLY
                bag.SetValue("TargetFolderPath", folder, RegistryValueKind.ExpandString);
            }
            using (var sf = k.CreateSubKey("ShellFolder"))
            {
                sf.SetValue("FolderValueFlags", 0x28, RegistryValueKind.DWord);
                sf.SetValue("Attributes", unchecked((int)0xF080004D), RegistryValueKind.DWord);
            }
        }
        using (var ns = Registry.CurrentUser.CreateSubKey($@"{NameSpace}\{Clsid}")) ns.SetValue("", Title);
        // без этого на рабочем столе появится лишний значок
        using (var hide = Registry.CurrentUser.CreateSubKey(HideDesktop)) hide.SetValue(Clsid, 1, RegistryValueKind.DWord);
        Refresh();
    }

    public static void Unregister()
    {
        foreach (string root in ClassRoots)
            Registry.CurrentUser.DeleteSubKeyTree($@"{root}\{Clsid}", throwOnMissingSubKey: false);
        Registry.CurrentUser.DeleteSubKeyTree($@"{NameSpace}\{Clsid}", throwOnMissingSubKey: false);
        using (var hide = Registry.CurrentUser.OpenSubKey(HideDesktop, writable: true)) hide?.DeleteValue(Clsid, false);
        Refresh();
    }

    /// <summary>На какую папку сейчас указывает пункт навигации (null — не зарегистрирован).</summary>
    public static string? RegisteredFolder()
    {
        using var k = Registry.CurrentUser.OpenSubKey($@"{ClassRoots[0]}\{Clsid}\Instance\InitPropertyBag");
        return k?.GetValue("TargetFolderPath") as string;
    }

    /// <summary>Значок программы на самой папке (desktop.ini). Чужой desktop.ini не трогаем.</summary>
    public static void SetFolderIcon(string folder, string iconExe)
    {
        string ini = Path.Combine(folder, "desktop.ini");
        string text = $"[.ShellClassInfo]\r\nIconResource={iconExe},0\r\nInfoTip=Синхронизируется с Яндекс.Диском\r\n";
        if (File.Exists(ini))
        {
            string old = File.ReadAllText(ini);
            if (old == text || !old.Contains(Path.GetFileName(iconExe), StringComparison.OrdinalIgnoreCase)) return;
            File.SetAttributes(ini, FileAttributes.Normal);
        }
        File.WriteAllText(ini, text, System.Text.Encoding.Unicode);
        File.SetAttributes(ini, FileAttributes.Hidden | FileAttributes.System);
        // Проводник читает desktop.ini только у папок с атрибутом «только чтение» или «системный»
        var di = new DirectoryInfo(folder);
        di.Attributes |= FileAttributes.ReadOnly;
    }

    /// <summary>Служебная папка .yadisksync (временные файлы скачивания) — скрытая.</summary>
    public static void HideServiceDir(string root)
    {
        try
        {
            var d = new DirectoryInfo(Path.Combine(root, Ignore.ServiceDir));
            if (!d.Exists) d.Create();
            if ((d.Attributes & FileAttributes.Hidden) == 0) d.Attributes |= FileAttributes.Hidden;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    private static void Refresh()
    {
        try { SHChangeNotify(0x08000000 /* SHCNE_ASSOCCHANGED */, 0, IntPtr.Zero, IntPtr.Zero); } catch { }
    }

    [DllImport("shell32.dll")]
    private static extern void SHChangeNotify(int eventId, uint flags, IntPtr item1, IntPtr item2);
}
