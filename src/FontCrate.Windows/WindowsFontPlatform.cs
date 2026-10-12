using System.Runtime.InteropServices;
using FontCrate.Core;
using Microsoft.Win32;
using Vortice.DirectWrite;
using SharpGen.Runtime;

namespace FontCrate.Windows;

public sealed partial class WindowsFontPlatform(string dataDirectory, IFontParser parser) : IFontPlatform
{
    private const string RegistryPath = @"Software\Microsoft\Windows NT\CurrentVersion\Fonts";
    private readonly Dictionary<string, (long Size, long Ticks, SystemFont Font)> cache = new(StringComparer.OrdinalIgnoreCase);
    public string TemporaryDirectory { get; } = Path.Combine(dataDirectory, "temporary-fonts");
    public string UserFontsDirectory { get; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "Windows", "Fonts");
    public int Load(string path) => AddFontResourceExW(path, 0, IntPtr.Zero);
    public bool Unload(string path) => RemoveFontResourceExW(path, 0, IntPtr.Zero);
    public unsafe bool IsLoaded(string path)
    {
        using var factory = DWrite.DWriteCreateFactory<IDWriteFactory3>(FactoryType.Isolated);
        using var collection = factory.GetSystemFontCollection(false, true);
        using var fonts = collection.FontSet;
        for (uint i = 0; i < fonts.FontCount; i++)
        {
            using var reference = fonts.GetFontFaceReference(i);
            using var file = reference.FontFile;
            using var loader = file.Loader;
            using var local = ComObject.QueryInterfaceOrNull<IDWriteLocalFontFileLoader>(MarshallingHelpers.ToCallbackPtr<IDWriteFontFileLoader>(loader));
            if (local is null) continue;
            var key = file.GetReferenceKey();
            fixed (byte* kp = key)
            {
                uint length = local.GetFilePathLengthFromKey((IntPtr)kp, (uint)key.Length);
                var text = new char[length + 1];
                fixed (char* tp = text) local.GetFilePathFromKey((IntPtr)kp, (uint)key.Length, (IntPtr)tp, length + 1);
                if (new string(text, 0, (int)length).Equals(path, StringComparison.OrdinalIgnoreCase)) return true;
            }
        }
        return false;
    }
    public void NotifyChanged() => SendMessageTimeoutW(new IntPtr(0xFFFF), 0x001D, IntPtr.Zero, IntPtr.Zero, 0x0002, 1000, out _);
    public string? GetRegistration(string name)
    { using var key = Registry.CurrentUser.OpenSubKey(RegistryPath); return key?.GetValue(name) as string; }
    public void SetRegistration(string name, string path)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RegistryPath);
        if (key.GetValue(name) is not null) throw new IOException("字体注册项已存在，拒绝覆盖");
        key.SetValue(name, path, RegistryValueKind.String); key.Flush();
    }
    public void RemoveRegistration(string name)
    { using var key = Registry.CurrentUser.OpenSubKey(RegistryPath, true); key?.DeleteValue(name, false); key?.Flush(); }
    public async Task<IReadOnlyList<SystemFont>> EnumerateAsync(CancellationToken ct)
    {
        var paths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in new[] { Registry.LocalMachine, Registry.CurrentUser })
        {
            using var key = root.OpenSubKey(RegistryPath);
            if (key is null) continue;
            foreach (var name in key.GetValueNames())
            {
                if (key.GetValue(name) is not string value) continue;
                value = Environment.ExpandEnvironmentVariables(value);
                string path = Path.IsPathRooted(value) ? value : Path.Combine(root == Registry.CurrentUser ? UserFontsDirectory :
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Fonts"), value);
                paths[path] = name.Replace(" (TrueType)", "").Replace(" (OpenType)", "");
            }
        }
        var result = new List<SystemFont>();
        foreach (var (path, displayName) in paths)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var info = new FileInfo(path);
                if (cache.TryGetValue(path, out var known) && known.Size == info.Length && known.Ticks == info.LastWriteTimeUtc.Ticks)
                { result.Add(known.Font); continue; }
                string hash = await FontIdentity.HashAsync(path, ct);
                var faces = await parser.ParseAsync(path, ct);
                var font = new SystemFont(path, hash, faces);
                cache[path] = (info.Length, info.LastWriteTimeUtc.Ticks, font); result.Add(font);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                // Retain registry names for conservative conflict checks when an installed font is unreadable.
                result.Add(new(path, null, [new(0, displayName, "", displayName, "", "", 400, 5, false, false, [])]));
            }
        }
        foreach (string old in cache.Keys.Except(paths.Keys, StringComparer.OrdinalIgnoreCase).ToArray()) cache.Remove(old);
        return result;
    }
    [LibraryImport("gdi32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int AddFontResourceExW(string path, uint flags, IntPtr reserved);
    [LibraryImport("gdi32.dll", StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool RemoveFontResourceExW(string path, uint flags, IntPtr reserved);
    [LibraryImport("user32.dll")]
    private static partial IntPtr SendMessageTimeoutW(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam, uint flags, uint timeout, out IntPtr result);
}
