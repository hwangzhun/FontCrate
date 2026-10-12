using FontCrate.Core;
using Vortice.DirectWrite;

namespace FontCrate.Windows;

public sealed class DirectWriteParser : IFontParser
{
    private readonly SfntParser metadata = new();
    public async Task<IReadOnlyList<ParsedFace>> ParseAsync(string path, CancellationToken ct)
    {
        var faces = await metadata.ParseAsync(path, ct);
        using var factory = DWrite.DWriteCreateFactory<IDWriteFactory3>();
        using var file = factory.CreateFontFileReference(path);
        file.Analyze(out var supported, out _, out var type, out uint count).CheckError();
        if (!supported || count != faces.Count) throw new InvalidDataException("DirectWrite 不支持此字体或 face 数量不一致");
        foreach (var f in faces)
        {
            ct.ThrowIfCancellationRequested();
            using var face = factory.CreateFontFace(type, [file], (uint)f.Index, FontSimulations.None);
        }
        return faces;
    }
}
