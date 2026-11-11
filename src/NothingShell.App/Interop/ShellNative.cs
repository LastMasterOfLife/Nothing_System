using System.Runtime.InteropServices;
using System.Text;
using NothingShell.Core.Bar;

namespace NothingShell.App.Interop;

/// <summary>
/// COM della shell per la barra: elementi e loro proprietà, AppUserModelID delle finestre,
/// liste dei recenti (docs/05-bar.md). Va usata da un thread STA (il thread UI).
/// </summary>
internal static class ShellNative
{
    public const uint SigdnNormalDisplay = 0;
    public const uint SigdnFileSysPath = 0x80058000;

    public static readonly PropertyKey AppUserModelId = new(new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"), 5);
    public static readonly PropertyKey LinkTargetParsingPath = new(new Guid("B9B4B3FC-2B51-4A42-B5D8-324146AFCF25"), 2);
    public static readonly PropertyKey LinkArguments = new(new Guid("436F2667-14E2-4FEB-B30A-146C53B5B674"), 100);
    private static readonly PropertyKey LinkTitle = new(new Guid("F29F85E0-4FF9-1068-AB91-08002B27B3D9"), 2);

    private const ushort VtLpwstr = 31;
    private const int AdltRecent = 0;
    private const int AdltFrequent = 1;
    private static readonly Guid ApplicationDocumentListsClsid = new("86bec222-30f2-47e0-9f25-60d11cd75c28");
    private static readonly Guid UnknownIid = new("00000000-0000-0000-C000-000000000046");

    /// <summary>Cartelle note che Windows sostituisce col GUID negli AppUserModelID predefiniti.</summary>
    private static readonly string[] KnownFolderIds =
    [
        "{6D809377-6AF0-444B-8957-A3773F02200E}", // Programmi (x64)
        "{7C5A40EF-A0FB-4BFC-874A-C0F2E0B9FA8E}", // Programmi (x86)
        "{6365D5A7-0F0D-45E5-87F6-0DA56B6A4F7D}", // Programmi\File comuni (x64)
        "{DE974D24-D9C6-4D3E-BF91-F4455120B917}", // Programmi\File comuni (x86)
        "{1AC14E77-02E7-4E5D-B744-2EB1AE5198B7}", // System32
        "{D65231B0-B2F1-4857-A4CE-A8E7C6EA7D27}", // SysWOW64
        "{F38BF404-1D43-42F2-9305-67DE0B28FC23}", // Windows
        "{F1B32785-6FBA-4FCF-9D55-7B8E7F157091}", // AppData\Local
        "{5CD7AEE2-2219-4A67-B85D-6C9CE15660CB}", // AppData\Local\Programs
        "{3EB685DB-65F9-4CF6-A03A-E3EF65729F3D}", // AppData\Roaming
    ];

    private static IReadOnlyList<KnownFolder>? _knownFolders;

    public static IReadOnlyList<KnownFolder> KnownFolders => _knownFolders ??= KnownFolderIds
        .Select(id => GetKnownFolderPath(id) is { } path ? new KnownFolder(path, id) : null)
        .OfType<KnownFolder>()
        .ToArray();

    /// <summary>Elemento della shell da un ITEMIDLIST serializzato; null se non è più valido (app disinstallata).</summary>
    public static IShellItem2? ItemFromIdList(byte[] idList)
    {
        var handle = GCHandle.Alloc(idList, GCHandleType.Pinned);
        try
        {
            var iid = typeof(IShellItem2).GUID;
            return SHCreateItemFromIDList(handle.AddrOfPinnedObject(), ref iid, out var item) == 0 ? item : null;
        }
        finally
        {
            handle.Free();
        }
    }

    public static IShellItem2? ItemFromPath(string path)
    {
        var iid = typeof(IShellItem2).GUID;
        return SHCreateItemFromParsingName(path, 0, ref iid, out var item) == 0 ? item : null;
    }

    public static string? DisplayName(IShellItem2 item, uint sigdn) =>
        item.GetDisplayName(sigdn, out var name) == 0 && !string.IsNullOrEmpty(name) ? name : null;

    public static string? GetString(IShellItem2 item, PropertyKey key) =>
        item.GetString(key, out var value) == 0 && !string.IsNullOrEmpty(value) ? value : null;

    /// <summary>AppUserModelID impostato sulla finestra (Chrome, PWA, app UWP); null se l'app lo imposta solo sul processo.</summary>
    public static string? GetWindowAppId(nint hwnd)
    {
        var iid = typeof(IPropertyStore).GUID;
        if (SHGetPropertyStoreForWindow(hwnd, ref iid, out var store) != 0 || store is null)
            return null;
        try
        {
            return ReadString(store, AppUserModelId);
        }
        finally
        {
            Marshal.ReleaseComObject(store);
        }
    }

    /// <summary>
    /// Jump list dell'app come la mostra Windows: le categorie dell'app (i progetti recenti di VS Code),
    /// poi i file recenti (o i frequenti, se l'app non tiene i recenti).
    /// </summary>
    public static IReadOnlyList<JumpSection> ReadJumpList(string appId)
    {
        var recent = ReadDocumentList(appId, AdltRecent);
        var frequent = recent.Count == 0;
        return JumpLists.Compose(ReadCustomCategories(appId), frequent ? ReadDocumentList(appId, AdltFrequent) : recent, frequent);
    }

    /// <summary>Categorie personalizzate dal file <c>CustomDestinations</c> dell'app; nessuna se manca o non si legge.</summary>
    private static IReadOnlyList<JumpSection> ReadCustomCategories(string appId)
    {
        if (GetKnownFolderPath("{AE50C081-EBD2-438A-8655-8A092E34987A}") is not { } recentFolder)
            return [];
        var file = System.IO.Path.Combine(recentFolder, "CustomDestinations", CustomDestinations.FileName(appId));
        try
        {
            return System.IO.File.Exists(file) ? CustomDestinations.Parse(System.IO.File.ReadAllBytes(file), ReadSerializedLink) : [];
        }
        catch (System.IO.IOException)
        {
            return [];
        }
        catch (UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary>Carica un collegamento serializzato (formato .lnk) e ne misura la lunghezza dalla posizione del flusso.</summary>
    private static (JumpItem? Item, int Length)? ReadSerializedLink(byte[] data, int offset)
    {
        var type = Type.GetTypeFromCLSID(CustomDestinations.ShellLinkClsid);
        if (type is null || Activator.CreateInstance(type) is not IShellLinkW link)
            return null;
        System.Runtime.InteropServices.ComTypes.IStream? stream = null;
        var position = Marshal.AllocCoTaskMem(sizeof(long));
        try
        {
            stream = SHCreateMemStream(data[offset..], (uint)(data.Length - offset));
            if (stream is null || ((IPersistStream)link).Load(stream) != 0)
                return null;
            stream.Seek(0, 1 /* STREAM_SEEK_CUR */, position);
            var length = (int)Marshal.ReadInt64(position);
            // Voci verso app UWP o eseguibili spariti (Teams) non si saprebbero aprire: si saltano.
            var item = DescribeLink(link);
            return (System.IO.File.Exists(item.Path) ? item : null, length);
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            return null;
        }
        finally
        {
            Marshal.FreeCoTaskMem(position);
            if (stream is not null)
                Marshal.ReleaseComObject(stream);
            Marshal.ReleaseComObject(link);
        }
    }

    private static List<JumpItem> ReadDocumentList(string appId, int listType)
    {
        var items = new List<JumpItem>();
        var type = Type.GetTypeFromCLSID(ApplicationDocumentListsClsid);
        if (type is null || Activator.CreateInstance(type) is not IApplicationDocumentLists lists)
            return items;
        try
        {
            lists.SetAppID(appId);
            var iid = typeof(IObjectArray).GUID;
            if (lists.GetList(listType, (uint)JumpLists.MaxItems * 2, ref iid, out var result) != 0 || result is not IObjectArray array)
                return items;
            array.GetCount(out var count);
            for (uint i = 0; i < count; i++)
            {
                var unknown = UnknownIid;
                if (array.GetAt(i, ref unknown, out var element) != 0)
                    continue;
                if (Describe(element) is { } item)
                    items.Add(item);
                Marshal.ReleaseComObject(element);
            }
            Marshal.ReleaseComObject(array);
        }
        catch (Exception ex) when (ex is COMException or ArgumentException or InvalidCastException)
        {
            // AppID senza lista o lista danneggiata: nessun recente
        }
        finally
        {
            Marshal.ReleaseComObject(lists);
        }
        return items;
    }

    /// <summary>Un elemento della lista è un collegamento (eseguibile + argomenti) o un file/cartella.</summary>
    private static JumpItem? Describe(object element)
    {
        if (element is IShellLinkW link)
            return DescribeLink(link);
        if (element is IShellItem2 item)
            return new JumpItem(DisplayName(item, SigdnNormalDisplay) ?? string.Empty,
                DisplayName(item, SigdnFileSysPath) ?? string.Empty, null, IsLink: false);
        return null;
    }

    /// <summary>
    /// Titolo dalla proprietà Title che le app impostano sulle voci della jump list (VS Code: il nome della cartella),
    /// altrimenti dalla descrizione del collegamento.
    /// </summary>
    private static JumpItem DescribeLink(IShellLinkW link)
    {
        var path = new StringBuilder(1024);
        var arguments = new StringBuilder(4096);
        var description = new StringBuilder(1024);
        link.GetPath(path, path.Capacity, 0, 0);
        link.GetArguments(arguments, arguments.Capacity);
        link.GetDescription(description, description.Capacity);
        var title = link is IPropertyStore store ? ReadString(store, LinkTitle) : null;
        return new JumpItem(title ?? description.ToString(), path.ToString(), arguments.ToString(), IsLink: true);
    }

    private static string? ReadString(IPropertyStore store, PropertyKey key)
    {
        if (store.GetValue(ref key, out var value) != 0)
            return null;
        try
        {
            return value.Type == VtLpwstr && value.Pointer != 0 ? Marshal.PtrToStringUni(value.Pointer) : null;
        }
        finally
        {
            _ = PropVariantClear(ref value);
        }
    }

    /// <summary>Cartella Download dell'utente (anche se spostata su un altro disco).</summary>
    public static string? DownloadsFolder => GetKnownFolderPath("{374DE290-123F-4565-9164-39C4925E467B}");

    /// <summary>Cartella Screenshot (Immagini\Screenshot, dove Windows salva le catture).</summary>
    public static string? ScreenshotsFolder => GetKnownFolderPath("{B7BEDE81-DF94-4682-A7D8-57A52620B86F}");

    private static string? GetKnownFolderPath(string id)
    {
        var guid = new Guid(id);
        if (SHGetKnownFolderPath(ref guid, 0, 0, out var pointer) != 0)
            return null;
        try
        {
            return Marshal.PtrToStringUni(pointer);
        }
        finally
        {
            Marshal.FreeCoTaskMem(pointer);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    public readonly struct PropertyKey(Guid formatId, uint propertyId)
    {
        public readonly Guid FormatId = formatId;
        public readonly uint PropertyId = propertyId;
    }

    /// <summary>PROPVARIANT ridotto: qui servono solo le stringhe (VT_LPWSTR).</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct PropVariant
    {
        public ushort Type;
        public ushort Reserved1;
        public ushort Reserved2;
        public ushort Reserved3;
        public nint Pointer;
        public nint Pointer2;
    }

    [ComImport]
    [Guid("7e9fb0d3-919f-4307-ab2e-9b1860310c93")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IShellItem2
    {
        void BindToHandler(nint bindContext, ref Guid handler, ref Guid iid, out nint result);
        void GetParent(out nint parent);
        [PreserveSig]
        int GetDisplayName(uint sigdn, [MarshalAs(UnmanagedType.LPWStr)] out string name);
        void GetAttributes(uint mask, out uint attributes);
        void Compare(nint other, uint hint, out int order);
        void GetPropertyStore(int flags, ref Guid iid, out nint store);
        void GetPropertyStoreWithCreateObject(int flags, nint createObject, ref Guid iid, out nint store);
        void GetPropertyStoreForKeys(nint keys, uint count, int flags, ref Guid iid, out nint store);
        void GetPropertyDescriptionList(in PropertyKey key, ref Guid iid, out nint list);
        void Update(nint bindContext);
        void GetProperty(in PropertyKey key, nint value);
        void GetCLSID(in PropertyKey key, out Guid clsid);
        void GetFileTime(in PropertyKey key, out long time);
        void GetInt32(in PropertyKey key, out int value);
        [PreserveSig]
        int GetString(in PropertyKey key, [MarshalAs(UnmanagedType.LPWStr)] out string value);
    }

    [ComImport]
    [Guid("886d8eeb-8cf2-4446-8d02-cdba1dbdcf99")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        void GetCount(out uint count);
        void GetAt(uint index, out PropertyKey key);
        [PreserveSig]
        int GetValue(ref PropertyKey key, out PropVariant value);
    }

    [ComImport]
    [Guid("3c594f9f-9f30-47a1-979a-c9e83d3d0a06")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IApplicationDocumentLists
    {
        void SetAppID([MarshalAs(UnmanagedType.LPWStr)] string appId);
        [PreserveSig]
        int GetList(int listType, uint itemsDesired, ref Guid iid, [MarshalAs(UnmanagedType.IUnknown)] out object? result);
    }

    [ComImport]
    [Guid("92CA9DCD-5622-4bba-A805-5E9F541BD8C9")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IObjectArray
    {
        void GetCount(out uint count);
        [PreserveSig]
        int GetAt(uint index, ref Guid iid, [MarshalAs(UnmanagedType.IUnknown)] out object element);
    }

    [ComImport]
    [Guid("000214F9-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder file, int capacity, nint findData, uint flags);
        void GetIDList(out nint idList);
        void SetIDList(nint idList);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder name, int capacity);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string name);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder dir, int capacity);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string dir);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder args, int capacity);
    }

    [ComImport]
    [Guid("00000109-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPersistStream
    {
        void GetClassID(out Guid clsid);
        [PreserveSig]
        int IsDirty();
        [PreserveSig]
        int Load(System.Runtime.InteropServices.ComTypes.IStream stream);
    }

    [DllImport("shlwapi.dll")]
    private static extern System.Runtime.InteropServices.ComTypes.IStream? SHCreateMemStream(byte[] init, uint size);

    [DllImport("shell32.dll")]
    private static extern int SHCreateItemFromIDList(nint idList, ref Guid iid, [MarshalAs(UnmanagedType.Interface)] out IShellItem2? item);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHCreateItemFromParsingName(string path, nint bindContext, ref Guid iid, [MarshalAs(UnmanagedType.Interface)] out IShellItem2? item);

    [DllImport("shell32.dll")]
    private static extern int SHGetPropertyStoreForWindow(nint hwnd, ref Guid iid, [MarshalAs(UnmanagedType.Interface)] out IPropertyStore? store);

    [DllImport("shell32.dll")]
    private static extern int SHGetKnownFolderPath(ref Guid id, uint flags, nint token, out nint path);

    [DllImport("ole32.dll")]
    private static extern int PropVariantClear(ref PropVariant value);
}
