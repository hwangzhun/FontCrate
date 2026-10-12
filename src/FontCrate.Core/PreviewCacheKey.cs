namespace FontCrate.Core;

// Rendering generation affects delivery, not pixels. Colors do affect pixels.
public readonly record struct PreviewCacheKey(string Hash, int Index, string Text, float Size,
    float Dpi, int Width, string Grid, uint ForegroundArgb, uint BackgroundArgb)
{
    public static PreviewCacheKey From(PreviewRequest request) => new(request.Face.Hash, request.Face.Index,
        request.Text, request.Size, request.Dpi, request.Width,
        request.GridCodes is null ? "" : string.Join(",", request.GridCodes),
        request.ForegroundArgb, request.BackgroundArgb);
}
