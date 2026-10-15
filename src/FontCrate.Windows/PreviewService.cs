using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;
using FontCrate.Core;
using Vortice.Direct2D1;
using Vortice.DirectWrite;
using Vortice.Mathematics;
using Vortice.WIC;

namespace FontCrate.Windows;

public sealed partial class PreviewService : IPreviewService
{
    private readonly SemaphoreSlim gate = new(1);
    private readonly Dictionary<PreviewCacheKey, (PreviewResult Result, LinkedListNode<PreviewCacheKey> Node)> cache = [];
    private readonly LinkedList<PreviewCacheKey> order = [];
    private const long Budget = 64 * 1024 * 1024;
    private long bytes;
    private bool disposed;
    public async Task<PreviewResult> RenderAsync(PreviewRequest request, CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            var key = PreviewCacheKey.From(request);
            lock (cache)
            {
                if (cache.TryGetValue(key, out var hit))
                { order.Remove(hit.Node); order.AddFirst(hit.Node); return hit.Result with { Generation = request.Generation }; }
            }
            if (request.Face.Path is null) throw new IOException("字体来源不可用");
            if (await FontIdentity.HashAsync(request.Face.Path, ct) != request.Face.Hash) throw new IOException("字体文件已变化，请重新扫描");
            var result = await Task.Run(() =>
            {
                var elapsed = System.Diagnostics.Stopwatch.StartNew();
                PerformanceLog.Write("preview-native-start", 0, $"face={request.Face.Id}; width={request.Width}");
                try
                {
                    var rendered = Render(request, ct);
                    PerformanceLog.Write("preview-native-complete", elapsed.Elapsed.TotalMilliseconds, $"face={request.Face.Id}");
                    return rendered;
                }
                catch (OperationCanceledException) { PerformanceLog.Write("preview-native-canceled", elapsed.Elapsed.TotalMilliseconds, $"face={request.Face.Id}"); throw; }
                catch (Exception ex) { PerformanceLog.Write("preview-native-error", elapsed.Elapsed.TotalMilliseconds, ex.ToString()); throw; }
            }, ct);
            ct.ThrowIfCancellationRequested();
            lock (cache)
            {
                if (result.Pixels.LongLength <= Budget)
                {
                    while (bytes + result.Pixels.LongLength > Budget && order.Last is not null)
                    { var old = order.Last; bytes -= cache[old.Value].Result.Pixels.LongLength; cache.Remove(old.Value); order.RemoveLast(); }
                    cache[key] = (result, order.AddFirst(key)); bytes += result.Pixels.LongLength;
                }
            }
            return result;
        }
        finally { gate.Release(); }
    }
    private static PreviewResult Render(PreviewRequest r, CancellationToken ct)
    {
        int hr = CoInitializeEx(IntPtr.Zero, 0);
        bool initialized = hr >= 0;
        if (hr < 0 && hr != unchecked((int)0x80010106)) Marshal.ThrowExceptionForHR(hr);
        try
        {
            ct.ThrowIfCancellationRequested();
            using var write = DWrite.DWriteCreateFactory<IDWriteFactory3>();
            using var reference = write.CreateFontFaceReference(r.Face.Path!, null, (uint)r.Face.Index, FontSimulations.None);
            using var fontFace = reference.CreateFontFace();
            using var builder = write.CreateFontSetBuilder();
            builder.AddFontFaceReference(reference);
            using var set = builder.CreateFontSet();
            using var collection = write.CreateFontCollectionFromFontSet(set);
            using var family = collection.GetFontFamily(0);
            using var names = family.FamilyNames;
            string familyName = names.GetString(0);
            using var format = write.CreateTextFormat(familyName, collection, fontFace.Weight, fontFace.Style,
                fontFace.Stretch, Math.Clamp(r.Size, 8, 192), "zh-CN");
            using var fallbackBuilder = write.CreateFontFallbackBuilder();
            using var fallback = fallbackBuilder.CreateFontFallback();
            using var format1 = format.QueryInterface<IDWriteTextFormat1>();
            format1.FontFallback = fallback;
            string text = string.IsNullOrEmpty(r.Text) ? " " : r.Text;
            if (text.Length > 4096) throw new ArgumentException("预览文字最多 4096 个 UTF-16 单元");
            uint[] scalars = text.EnumerateRunes().Select(x => (uint)x.Value).ToArray();
            var indices = fontFace.GetGlyphIndices(scalars);
            int[] missing = scalars.Where((c, i) => indices[i] == 0 && !Rune.IsWhiteSpace(new Rune((int)c)) &&
                !Rune.IsControl(new Rune((int)c))).Select(c => (int)c).Distinct().ToArray();
            float dpi = Math.Clamp(r.Dpi, 96, 288);
            float logicalWidth = Math.Clamp(r.Width, 120, 1200);
            using var layout = write.CreateTextLayout(text, format, logicalWidth - 24, 1200);
            using var layout2 = layout.QueryInterface<IDWriteTextLayout2>();
            layout2.FontFallback = fallback;
            int width = (int)Math.Ceiling(logicalWidth * dpi / 96);
            float logicalHeight = r.GridCodes is null ? Math.Clamp(layout.Metrics.Height + 24, 72, 1200) : Math.Max(1, (r.GridCodes.Count + 7) / 8) * 72;
            int height = (int)Math.Ceiling(logicalHeight * dpi / 96);
            using var wic = new IWICImagingFactory();
            using var bitmap = wic.CreateBitmap((uint)width, (uint)height, Vortice.WIC.PixelFormat.Format32bppPBGRA, BitmapCreateCacheOption.CacheOnLoad);
            using var d2d = D2D1.D2D1CreateFactory<ID2D1Factory>(Vortice.Direct2D1.FactoryType.SingleThreaded);
            var properties = new RenderTargetProperties(RenderTargetType.Software,
                new Vortice.DCommon.PixelFormat(Vortice.DXGI.Format.B8G8R8A8_UNorm, Vortice.DCommon.AlphaMode.Premultiplied), dpi, dpi, RenderTargetUsage.None, FeatureLevel.Default);
            using var target = d2d.CreateWicBitmapRenderTarget(bitmap, properties);
            using var brush = target.CreateSolidColorBrush(Color(r.ForegroundArgb));
            target.BeginDraw(); target.Clear(Color(r.BackgroundArgb));
            if (r.GridCodes is null) target.DrawTextLayout(new Vector2(12, 12), layout, brush, DrawTextOptions.Clip);
            else
            {
                using var labelFormat = write.CreateTextFormat("Segoe UI", 9);
                for (int i = 0; i < r.GridCodes.Count; i++)
                {
                    ct.ThrowIfCancellationRequested();
                    float x = i % 8 * logicalWidth / 8 + 8, y = i / 8 * 72 + 4;
                    using var cell = write.CreateTextLayout(char.ConvertFromUtf32(r.GridCodes[i]), format, logicalWidth / 8 - 12, 44);
                    using var cell2 = cell.QueryInterface<IDWriteTextLayout2>(); cell2.FontFallback = fallback;
                    target.DrawTextLayout(new Vector2(x, y), cell, brush, DrawTextOptions.Clip);
                    using var label = write.CreateTextLayout($"U+{r.GridCodes[i]:X4}", labelFormat, logicalWidth / 8 - 8, 18);
                    target.DrawTextLayout(new Vector2(x, y + 45), label, brush, DrawTextOptions.Clip);
                }
            }
            target.EndDraw().CheckError();
            ct.ThrowIfCancellationRequested();
            int stride = checked(width * 4);
            byte[] pixels = new byte[checked(stride * height)]; bitmap.CopyPixels((uint)stride, pixels);
            return new(pixels, width, height, stride, missing, r.Generation);
        }
        finally { if (initialized) CoUninitialize(); }
    }
    private static Color4 Color(uint argb) => new(
        ((argb >> 16) & 255) / 255f, ((argb >> 8) & 255) / 255f,
        (argb & 255) / 255f, ((argb >> 24) & 255) / 255f);
    public void ClearCache() { lock (cache) { cache.Clear(); order.Clear(); bytes = 0; } }
    public void Dispose() { disposed = true; ClearCache(); }
    [LibraryImport("ole32.dll")] private static partial int CoInitializeEx(IntPtr reserved, uint mode);
    [LibraryImport("ole32.dll")] private static partial void CoUninitialize();
}
