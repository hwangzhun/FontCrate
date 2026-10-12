using System.Buffers.Binary;
using System.Text;
using FontCrate.Core;

namespace FontCrate.Tests;

internal sealed class Sandbox : IDisposable
{
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "fontlite-tests-" + Guid.NewGuid().ToString("N"));
    public string Fonts => Path.Combine(Root, "sources");
    public Repository Repository { get; }
    public Sandbox() { Directory.CreateDirectory(Fonts); Repository = new(Path.Combine(Root, "library.db")); }
    public async Task<FaceRow> IndexAsync(string family = "Test Font", string filename = "test.ttf")
    {
        await Repository.InitializeAsync();
        File.WriteAllBytes(Path.Combine(Fonts, filename), FontData.Font(family));
        var source = await Repository.AddSourceAsync(Fonts);
        await new Scanner(Repository, new SfntParser()).ScanAsync(source, false, null, CancellationToken.None);
        return (await Repository.QueryAsync(new())).Rows.Single();
    }
    public void Dispose()
    {
        // Each sandbox owns one pool. Clearing all pools can invalidate another parallel test.
        var connectionString = new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder
        { DataSource = Path.Combine(Root, "library.db"), ForeignKeys = true }.ToString();
        using var connection = new Microsoft.Data.Sqlite.SqliteConnection(connectionString);
        Microsoft.Data.Sqlite.SqliteConnection.ClearPool(connection);
        Directory.Delete(Root, true);
    }
}
internal sealed class CountingParser : IFontParser
{
    public int Calls;
    public int Delay;
    public async Task<IReadOnlyList<ParsedFace>> ParseAsync(string path, CancellationToken ct)
    { Interlocked.Increment(ref Calls); if (Delay > 0) await Task.Delay(Delay, ct); return await new SfntParser().ParseAsync(path, ct); }
}
internal sealed class FakePlatform(string root) : IFontPlatform
{
    public string TemporaryDirectory => Path.Combine(root, "temporary");
    public string UserFontsDirectory => Path.Combine(root, "installed");
    public Dictionary<string, string> Registry { get; } = [];
    public Dictionary<string, int> Loads { get; } = [];
    public List<SystemFont> External { get; } = [];
    public bool FailLoad, FailUnload, FailRegistration, FailAfterRegistration;
    public int LoadDelay, Enumerations, Notifications, MaxConcurrentLoads;
    private int concurrentLoads;
    private readonly object sync = new();
    public Action<string>? Loading;
    public string? FailLoadHash;
    public int Load(string path)
    {
        int concurrent = Interlocked.Increment(ref concurrentLoads);
        lock (sync) MaxConcurrentLoads = Math.Max(MaxConcurrentLoads, concurrent);
        try
        {
            Loading?.Invoke(path);
            if (LoadDelay > 0) Thread.Sleep(LoadDelay);
            lock (sync)
            {
                if (FailLoad || Path.GetFileNameWithoutExtension(path) == FailLoadHash) return 0;
                Loads[path] = Loads.GetValueOrDefault(path) + 1; return 1;
            }
        }
        finally { Interlocked.Decrement(ref concurrentLoads); }
    }
    public bool Unload(string path) { lock (sync) { if (FailUnload || Loads.GetValueOrDefault(path) == 0) return false; Loads[path]--; return true; } }
    public bool IsLoaded(string path) { lock (sync) return Loads.GetValueOrDefault(path) > 0; }
    public void NotifyChanged() => Interlocked.Increment(ref Notifications);
    public string? GetRegistration(string name) => Registry.GetValueOrDefault(name);
    public void SetRegistration(string name, string path) { if (FailRegistration) throw new IOException("injected registry failure"); Registry.Add(name, path); if (FailAfterRegistration) throw new IOException("injected flush failure"); }
    public void RemoveRegistration(string name) => Registry.Remove(name);
    public async Task<IReadOnlyList<SystemFont>> EnumerateAsync(CancellationToken ct)
    {
        Interlocked.Increment(ref Enumerations);
        var fonts = new List<SystemFont>(External);
        foreach (string p in Registry.Values)
            if (File.Exists(p))
            {
                try { fonts.Add(new(p, await FontIdentity.HashAsync(p, ct), await new SfntParser().ParseAsync(p, ct))); }
                catch (InvalidDataException) { fonts.Add(new(p, null, [])); }
            }
        return fonts;
    }
}
internal static class FontData
{
    public static byte[] Font(string family, string version = "Version 1", bool variable = false)
    {
        var names = new Dictionary<int, string> { [1] = family, [2] = "Regular", [4] = family + " Regular", [5] = version, [6] = family.Replace(" ", "") + "-Regular" };
        using var ns = new MemoryStream();
        byte[] nameHeader = new byte[6 + names.Count * 12]; Put16(nameHeader, 2, names.Count); Put16(nameHeader, 4, nameHeader.Length);
        int n = 0;
        foreach (var (id, value) in names)
        {
            var text = Encoding.BigEndianUnicode.GetBytes(value); int p = 6 + n++ * 12;
            Put16(nameHeader, p, 3); Put16(nameHeader, p + 2, 1); Put16(nameHeader, p + 4, 0x0409); Put16(nameHeader, p + 6, id);
            Put16(nameHeader, p + 8, text.Length); Put16(nameHeader, p + 10, (int)ns.Length); ns.Write(text);
        }
        var name = nameHeader.Concat(ns.ToArray()).ToArray();
        byte[] os2 = new byte[64]; Put16(os2, 4, 400); Put16(os2, 6, 5);
        byte[] maxp = new byte[6]; Put32(maxp, 0, 0x10000); Put16(maxp, 4, 5);
        byte[] cmap = new byte[52]; Put16(cmap, 2, 1); Put16(cmap, 4, 3); Put16(cmap, 6, 10); Put32(cmap, 8, 12);
        Put16(cmap, 12, 12); Put32(cmap, 16, 40); Put32(cmap, 24, 2);
        Put32(cmap, 28, 65); Put32(cmap, 32, 66); Put32(cmap, 36, 1);
        Put32(cmap, 40, 0x20000); Put32(cmap, 44, 0x20001); Put32(cmap, 48, 3);
        var tables = new Dictionary<string, byte[]> { ["name"] = name, ["OS/2"] = os2, ["maxp"] = maxp, ["cmap"] = cmap };
        if (variable) tables["fvar"] = new byte[16];
        int offset = 12 + tables.Count * 16;
        var b = new byte[offset + tables.Values.Sum(v => v.Length)]; Put32(b, 0, 0x10000); Put16(b, 4, tables.Count);
        n = 0;
        foreach (var (tag, value) in tables)
        {
            int p = 12 + n++ * 16; Encoding.ASCII.GetBytes(tag).CopyTo(b, p); Put32(b, p + 8, offset); Put32(b, p + 12, value.Length);
            value.CopyTo(b, offset); offset += value.Length;
        }
        return b;
    }
    public static byte[] Collection(params string[] families)
    {
        var fonts = families.Select(f => Font(f)).ToArray(); int header = 12 + fonts.Length * 4;
        byte[] b = new byte[header + fonts.Sum(f => f.Length)]; Put32(b, 0, 0x74746366); Put32(b, 4, 0x10000); Put32(b, 8, fonts.Length);
        int offset = header;
        for (int i = 0; i < fonts.Length; i++)
        {
            var f = fonts[i]; Put32(b, 12 + i * 4, offset);
            int tables = BinaryPrimitives.ReadUInt16BigEndian(f.AsSpan(4, 2));
            for (int j = 0; j < tables; j++) { int p = 12 + j * 16 + 8; Put32(f, p, (int)BinaryPrimitives.ReadUInt32BigEndian(f.AsSpan(p, 4)) + offset); }
            f.CopyTo(b, offset); offset += f.Length;
        }
        return b;
    }
    public static void Put16(byte[] b, int p, int v) => BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(p, 2), (ushort)v);
    public static void Put32(byte[] b, int p, int v) => BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(p, 4), (uint)v);
}
