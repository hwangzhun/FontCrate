using System.Runtime.InteropServices;
using System.IO;
using System.Text.Json;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using FontCrate.Core;
using FontCrate.Windows;

namespace FontCrate.Verify;

internal static partial class Program
{
    [STAThread]
    private static async Task<int> Main(string[] args)
    {
        if (args.Length == 2 && args[0] == "--recover")
        {
            string directory = Path.GetFullPath(args[1]);
            if (!File.Exists(Path.Combine(directory, "library.db"))) { Console.Error.WriteLine("没有找到验证数据库"); return 2; }
            var repo = new Repository(Path.Combine(directory, "library.db")); await repo.InitializeAsync();
            var ops = new FontOperations(repo, new WindowsFontPlatform(directory, new DirectWriteParser()));
            try
            {
                Require(await ops.RecoverAsync(default));
                var rows = (await repo.QueryAsync(new())).Rows.Where(r => r.Owned).ToArray();
                Require(await ops.RunAsync("uninstall", rows, default)); Console.WriteLine("验证字体已清理"); return 0;
            }
            catch (Exception e) { Console.Error.WriteLine(e); return 1; }
        }
        if (args.Length < 1) { Console.WriteLine("FontCrate.Verify <font-file> [--exercise] [--output <directory>]\n默认仅解析与渲染；--exercise 会临时启用、当前用户安装及卸载输入字体，要求它尚未安装。"); return 2; }
        string input = Path.GetFullPath(args[0]); bool exercise = args.Contains("--exercise");
        int oi = Array.IndexOf(args, "--output");
        string output = oi >= 0 && oi + 1 < args.Length ? Path.GetFullPath(args[oi + 1]) : Path.Combine(Environment.CurrentDirectory, "artifacts", "native-verification");
        Directory.CreateDirectory(output);
        string data = Path.Combine(Path.GetTempPath(), "fontlite-verify-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(data);
        FontOperations? operations = null;
        FaceRow? installed = null;
        bool successful = false;
        try
        {
            var parser = new DirectWriteParser(); var repository = new Repository(Path.Combine(data, "library.db")); await repository.InitializeAsync();
            string sourcePath = Path.Combine(data, "source"); Directory.CreateDirectory(sourcePath); File.Copy(input, Path.Combine(sourcePath, Path.GetFileName(input)));
            var source = await repository.AddSourceAsync(sourcePath);
            await new Scanner(repository, parser).ScanAsync(source, true, null, default);
            var page = await repository.QueryAsync(new());
            if (page.Rows.Count == 0) throw new InvalidDataException(string.Join("\n", (await repository.ErrorsAsync()).Select(e => e.Message)));
            using var previews = new PreviewService();
            var observations = new List<object>();
            foreach (var row in page.Rows)
            {
                foreach (float dpi in new float[] { 96, 144, 192 })
                {
                    var result = await previews.RenderAsync(new(row, "中文字体 ABC 0123 𠀀", 36, dpi, row.Index), default);
                    var image = BitmapSource.Create(result.Width, result.Height, dpi, dpi, PixelFormats.Pbgra32, null, result.Pixels, result.Stride); image.Freeze();
                    var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
                    using var file = File.Create(Path.Combine(output, $"face-{row.Index}-dpi-{dpi}.png")); encoder.Save(file);
                    observations.Add(new { row.Index, row.Family, dpi, result.Width, result.Height, result.Missing });
                }
            }
            if (exercise)
            {
                var platform = new WindowsFontPlatform(data, parser); operations = new(repository, platform);
                string hash = page.Rows[0].Hash;
                if ((await platform.EnumerateAsync(default)).Any(f => f.Hash == hash)) throw new InvalidOperationException("输入字体已经安装，请使用未安装的测试字体");
                var before = GdiFonts();
                Require(await operations.RunAsync("activate", page.Rows, default));
                var temporary = (await repository.OperationsAsync()).Single(r => r.Kind == "temporary");
                if (!platform.IsLoaded(temporary.Path)) throw new InvalidOperationException("DirectWrite 系统集合未发现临时字体");
                var newGdiEntries = GdiFonts().Except(before, StringComparer.OrdinalIgnoreCase).ToArray();
                if (newGdiEntries.Length == 0) throw new InvalidOperationException("GDI 枚举未发现新增字体");
                Console.WriteLine("GDI 新增：" + string.Join(", ", newGdiEntries));
                Require(await operations.RunAsync("install", page.Rows, default));
                installed = (await repository.QueryAsync(new())).Rows.First();
                if (!installed.Installed || installed.Active || !installed.Owned) throw new InvalidOperationException("临时启用转安装状态不正确");
                Require(await operations.ExitAsync());
                var record = (await repository.OperationsAsync()).Single(r => r.Kind == "install");
                if (!platform.IsLoaded(record.Path)) throw new InvalidOperationException("退出清理撤销了持久字体");
                Require(await operations.RunAsync("uninstall", [installed], default)); installed = null;
                if (platform.GetRegistration(record.RegistryName) is not null || File.Exists(record.Path)) throw new InvalidOperationException("卸载清理不完整");
            }
            File.WriteAllText(Path.Combine(output, "verification.json"), JsonSerializer.Serialize(new { input, exercise, faces = page.Total, observations, time = DateTimeOffset.UtcNow }, new JsonSerializerOptions { WriteIndented = true }));
            successful = true; Console.WriteLine($"PASS：{page.Total} 个 face，预览结果：{output}"); return 0;
        }
        catch (Exception e) { Console.Error.WriteLine(e); return 1; }
        finally
        {
            if (operations is not null)
            {
                try
                {
                    Require(await operations.ExitAsync());
                    if (installed is not null) Require(await operations.RunAsync("uninstall", [installed], default));
                }
                catch (Exception e) { successful = false; Console.Error.WriteLine("清理待处理，保留验证数据库：" + data + "\n" + e); }
            }
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (successful) Directory.Delete(data, true);
            else Console.Error.WriteLine("验证记录保留于：" + data);
        }
    }
    private static void Require(IReadOnlyList<OperationResult> results)
    { if (results.Any(r => !r.Success)) throw new InvalidOperationException(string.Join("\n", results.Where(r => !r.Success).Select(r => r.Message))); }

    private static HashSet<string> GdiFonts()
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase); var logfont = new LogFont { CharSet = 1 };
        IntPtr dc = GetDC(IntPtr.Zero);
        if (dc == IntPtr.Zero) throw new InvalidOperationException("无法获取 GDI DC");
        FontEnum callback = (font, _, _, _) => { string? name = Marshal.PtrToStringUni(font + 92, 64)?.TrimEnd('\0'); if (name is not null) names.Add(name); return 1; };
        try { EnumFontFamiliesExW(dc, ref logfont, callback, IntPtr.Zero, 0); GC.KeepAlive(callback); return names; }
        finally { ReleaseDC(IntPtr.Zero, dc); }
    }
    [StructLayout(LayoutKind.Sequential, CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private unsafe struct LogFont
    {
        public int Height, Width, Escapement, Orientation, Weight;
        public byte Italic, Underline, StrikeOut, CharSet, OutPrecision, ClipPrecision, Quality, PitchAndFamily;
        public fixed char FaceName[32];
    }
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate int FontEnum(IntPtr font, IntPtr metrics, uint type, IntPtr param);
    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)] private static extern int EnumFontFamiliesExW(IntPtr dc, ref LogFont lf, FontEnum callback, IntPtr param, uint flags);
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);
}
