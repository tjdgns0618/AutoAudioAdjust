using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;

namespace AutoAudioAdjust.App.Services;

/// <summary>
/// Writes a .lnk shortcut tagged with System.AppUserModel.ID. Windows uses such a Start menu
/// shortcut to resolve an unpackaged app's display name and icon (media flyout, notifications).
/// </summary>
internal static class StartMenuShortcut
{
    // PKEY_AppUserModel_ID
    private static readonly PropertyKey AppUserModelIdKey = new(new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"), 5);
    private const ushort VtLpwstr = 31;

    public static void CreateOrUpdate(string shortcutPath, string targetPath, string appUserModelId, string description)
    {
        var link = (IShellLinkW)new ShellLink();
        try
        {
            link.SetPath(targetPath);
            link.SetWorkingDirectory(Path.GetDirectoryName(targetPath) ?? "");
            link.SetDescription(description);
            link.SetIconLocation(targetPath, 0);

            var store = (IPropertyStore)link;
            var variant = Marshal.AllocCoTaskMem(24);
            try
            {
                for (var i = 0; i < 24; i++)
                {
                    Marshal.WriteByte(variant, i, 0);
                }
                Marshal.WriteInt16(variant, 0, (short)VtLpwstr);
                Marshal.WriteIntPtr(variant, 8, Marshal.StringToCoTaskMemUni(appUserModelId));
                var key = AppUserModelIdKey;
                store.SetValue(ref key, variant);
                store.Commit();
            }
            finally
            {
                _ = PropVariantClear(variant);
                Marshal.FreeCoTaskMem(variant);
            }

            Directory.CreateDirectory(Path.GetDirectoryName(shortcutPath)!);
            ((IPersistFile)link).Save(shortcutPath, true);
        }
        finally
        {
            Marshal.ReleaseComObject(link);
        }
    }

    [DllImport("ole32.dll")]
    private static extern int PropVariantClear(IntPtr variant);

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private readonly struct PropertyKey(Guid formatId, uint propertyId)
    {
        public readonly Guid FormatId = formatId;
        public readonly uint PropertyId = propertyId;
    }

    [ComImport]
    [Guid("00021401-0000-0000-C000-000000000046")]
    private class ShellLink
    {
    }

    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("000214F9-0000-0000-C000-000000000046")]
    private interface IShellLinkW
    {
        void GetPath(IntPtr file, int maxChars, IntPtr findData, uint flags);
        void GetIDList(out IntPtr idList);
        void SetIDList(IntPtr idList);
        void GetDescription(IntPtr name, int maxChars);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string name);
        void GetWorkingDirectory(IntPtr dir, int maxChars);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string dir);
        void GetArguments(IntPtr args, int maxChars);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string args);
        void GetHotkey(out short hotkey);
        void SetHotkey(short hotkey);
        void GetShowCmd(out int showCmd);
        void SetShowCmd(int showCmd);
        void GetIconLocation(IntPtr iconPath, int maxChars, out int iconIndex);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string iconPath, int iconIndex);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string relativePath, uint reserved);
        void Resolve(IntPtr hwnd, uint flags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string file);
    }

    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99")]
    private interface IPropertyStore
    {
        void GetCount(out uint count);
        void GetAt(uint index, out PropertyKey key);
        void GetValue(ref PropertyKey key, IntPtr value);
        void SetValue(ref PropertyKey key, IntPtr value);
        void Commit();
    }
}
