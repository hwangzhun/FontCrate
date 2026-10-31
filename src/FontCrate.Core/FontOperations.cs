using System.Collections.Concurrent;

namespace FontCrate.Core;

/// <summary>Serializes batches, bounds concurrent temporary loads and unloads, and journals every durable side effect.</summary>
public sealed class FontOperations(Repository repository, IFontPlatform platform)
{
    private readonly SemaphoreSlim gate = new(1);
    private readonly ConcurrentDictionary<string, OperationRecord> active = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<long, int> cleanupAttempts = new();
    public async Task RefreshAsync(CancellationToken ct)
    {
        await PerformanceLog.StepAsync("batch-lock", () => gate.WaitAsync(ct));
        try { await RefreshCore(ct); } finally { gate.Release(); }
    }
    private async Task RefreshCore(CancellationToken ct)
    {
        var fonts = await EnumerateAsync(ct);
        await repository.RefreshInstalledAsync(fonts.Where(f => f.Hash is not null).Select(f => f.Hash!));
        foreach (var record in await repository.OperationsAsync())
        {
            if (record.Kind == "install" && record.Stage == "installed" && await OwnedCopyMatches(record, ct) &&
                GetRegistration(record.RegistryName) == record.Path)
                await repository.SetFileStateAsync(record.Hash, true, active.ContainsKey(record.Hash), true);
        }
    }
    public Task<IReadOnlyList<OperationResult>> RunAsync(string action, IReadOnlyList<FaceRow> selected, CancellationToken ct,
        IProgress<OperationProgress>? progress = null) => PerformanceLog.BatchAsync(action,
            () => RunCoreAsync(action, selected, ct, progress), new { total = selected.DistinctBy(f => f.Hash).Count() },
            results => Summary(results, selected.DistinctBy(f => f.Hash).Count()), ct);
    private static object Summary(IReadOnlyList<OperationResult> results, int? total = null) => new
    { total = total ?? results.Count, processed = results.Count, succeeded = results.Count(r => r.Success),
        failed = results.Count(r => !r.Success), remaining = Math.Max(0, (total ?? results.Count) - results.Count) };
    private async Task<IReadOnlyList<OperationResult>> RunCoreAsync(string action, IReadOnlyList<FaceRow> selected, CancellationToken ct,
        IProgress<OperationProgress>? progress)
    {
        await PerformanceLog.StepAsync("batch-lock", () => gate.WaitAsync(ct));
        try
        {
            var files = selected.DistinctBy(f => f.Hash).ToArray();
            if (action == "activate") return await ActivateBatch(files, ct, progress);
            if (action == "deactivate")
                return await DeactivateBatch(files.Select(f => (f.Hash, (string?)f.FullName)).ToArray(), ct, progress);
            var results = new List<OperationResult>();
            progress?.Report(new(0, files.Length));
            foreach (var face in files)
            {
                if (ct.IsCancellationRequested) break;
                try
                {
                    string message = action switch
                    {
                        "install" => await Install(face, ct), "uninstall" => await Uninstall(face.Hash, ct),
                        _ => throw new ArgumentException("未知字体操作")
                    };
                    results.Add(new(face.Hash, true, message));
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    results.Add(new(face.Hash, false, "已取消；未继续处理剩余字体"));
                    progress?.Report(new(results.Count, files.Length, face.FullName)); break;
                }
                catch (Exception e) { results.Add(new(face.Hash, false, e.Message)); }
                progress?.Report(new(results.Count, files.Length, face.FullName));
            }
            return results;
        }
        finally { gate.Release(); }
    }
    private async Task<IReadOnlyList<OperationResult>> ActivateBatch(FaceRow[] files, CancellationToken ct,
        IProgress<OperationProgress>? progress)
    {
        var results = new OperationResult?[files.Length];
        var ready = new List<int>();
        int completed = 0, next = 0;
        var sync = new object();
        void Finish(int index, bool success, string message)
        {
            // Serialize reports as well as counts; asynchronous UI callbacks must never regress.
            lock (sync)
            {
                results[index] = new(files[index].Hash, success, message);
                progress?.Report(new(++completed, files.Length, files[index].FullName));
            }
        }
        progress?.Report(new(0, files.Length));
        if (files.Length == 0) return [];
        try
        {
            var system = await EnumerateAsync(ct);
            var pending = await repository.OperationsAsync();
            var reserved = new List<(string Hash, IReadOnlyList<ParsedFace> Faces)>();
            foreach (string hash in active.Keys)
                reserved.Add((hash, await repository.FacesForFileAsync(hash)));
            // Reserve names in selection order before workers start, including all TTC faces.
            for (int i = 0; i < files.Length; i++)
            {
                if (ct.IsCancellationRequested) break;
                var face = files[i];
                using var fontScope = PerformanceLog.FontScope(face.Hash, face.Path);
                PerformanceLog.TryWrite("font-preflight-start", outcome: "started");
                try
                {
                    if (active.ContainsKey(face.Hash)) { PerformanceLog.TryWrite("font-skipped", data: new { fontHash = face.Hash }, outcome: "skipped", detail: "已经临时启用"); Finish(i, true, "已经临时启用"); continue; }
                    if (pending.Any(r => r.Hash == face.Hash &&
                        (r.Stage is not ("active" or "installed") || r.Kind == "temporary" && !active.ContainsKey(face.Hash))))
                        throw new InvalidOperationException("此字体存在未完成操作，请先执行状态恢复");
                    var faces = await repository.FacesForFileAsync(face.Hash);
                    foreach (var font in system.Where(f => f.Hash != face.Hash))
                        if (faces.Any(a => font.Faces.Any(b => FontIdentity.NameConflicts(a, b))))
                            throw new InvalidOperationException("与已安装字体名称冲突，未覆盖：" + font.Path);
                    if (reserved.Any(r => r.Hash != face.Hash && faces.Any(a => r.Faces.Any(b => FontIdentity.NameConflicts(a, b)))))
                        throw new InvalidOperationException("与其他临时启用字体或本批字体名称冲突");
                    if (system.Any(f => f.Hash == face.Hash)) { PerformanceLog.TryWrite("font-skipped", data: new { fontHash = face.Hash }, outcome: "skipped", detail: "内容已安装"); Finish(i, true, "内容已安装，无需临时启用"); continue; }
                    reserved.Add((face.Hash, faces)); ready.Add(i);
                    PerformanceLog.TryWrite("font-preflight-complete", outcome: "completed");
                }
                catch (Exception e) when (e is not OperationCanceledException) { PerformanceLog.Error("font-preflight-error", e, new { fontHash = face.Hash, face.Path }); Finish(i, false, e.Message); }
            }
            async Task Worker()
            {
                while (true)
                {
                    int index;
                    lock (sync)
                    {
                        if (ct.IsCancellationRequested || next == ready.Count) return;
                        index = ready[next++];
                    }
                    try { Finish(index, true, await Activate(files[index], ct)); }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested)
                    { Finish(index, false, "已取消；未继续处理剩余字体"); }
                    catch (Exception e) { Finish(index, false, e.Message); }
                }
            }
            await Task.WhenAll(Enumerable.Range(0, Math.Min(ready.Count, Math.Clamp(Environment.ProcessorCount, 2, 8)))
                .Select(_ => Task.Run(Worker)));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        finally { if (ready.Count > 0) NotifyChanged(); }
        return results.OfType<OperationResult>().ToArray();
    }
    private async Task<IReadOnlyList<OperationResult>> DeactivateBatch((string Hash, string? FullName)[] files,
        CancellationToken ct, IProgress<OperationProgress>? progress = null)
    {
        var results = new OperationResult?[files.Length];
        int completed = 0, next = 0;
        bool notify = false;
        var sync = new object();
        progress?.Report(new(0, files.Length));
        async Task Worker()
        {
            while (true)
            {
                int index;
                lock (sync)
                {
                    if (ct.IsCancellationRequested || next == files.Length) return;
                    index = next++;
                    notify |= active.ContainsKey(files[index].Hash);
                }
                OperationResult result;
                // Cancellation stops dispatch; an already-started file must finish its journal and cleanup.
                try { result = new(files[index].Hash, true, await Deactivate(files[index].Hash, CancellationToken.None, notify: false)); }
                catch (Exception e) { result = new(files[index].Hash, false, e.Message); }
                lock (sync)
                {
                    results[index] = result;
                    progress?.Report(new(++completed, files.Length, files[index].FullName));
                }
            }
        }
        try
        {
            await Task.WhenAll(Enumerable.Range(0, Math.Min(files.Length, Math.Clamp(Environment.ProcessorCount, 2, 8)))
                .Select(_ => Task.Run(Worker)));
        }
        finally
        {
            // Unloading may succeed even when later copy cleanup fails; other apps still need the broadcast.
            if (notify) NotifyChanged();
        }
        return results.OfType<OperationResult>().ToArray();
    }
    private Task<string> CheckedSource(FaceRow face, CancellationToken ct) =>
        PerformanceLog.StepAsync("source-validation", () => CheckedSourceCore(face, ct), new { face.Hash, face.Path });
    private async Task<string> CheckedSourceCore(FaceRow face, CancellationToken ct)
    {
        foreach (var location in await repository.LocationsAsync(face.Hash))
        {
            if (!location.Available || !File.Exists(location.Path)) continue;
            if (await FontIdentity.HashAsync(location.Path, ct) == face.Hash) return location.Path;
        }
        throw new IOException("来源不可用或内容已变化，请重新扫描");
    }
    private async Task<bool> CheckConflict(string hash, CancellationToken ct)
    {
        var faces = await repository.FacesForFileAsync(hash);
        var system = await EnumerateAsync(ct);
        foreach (var font in system)
        {
            if (font.Hash == hash) continue;
            if (faces.Any(a => font.Faces.Any(b => FontIdentity.NameConflicts(a, b))))
                throw new InvalidOperationException("与已安装字体名称冲突，未覆盖：" + font.Path);
        }
        foreach (var otherHash in active.Keys.Where(h => h != hash))
        {
            var other = await repository.FacesForFileAsync(otherHash);
            if (faces.Any(a => other.Any(b => FontIdentity.NameConflicts(a, b))))
                throw new InvalidOperationException("与其他临时启用字体名称冲突");
        }
        return system.Any(f => f.Hash == hash);
    }
    private async Task EnsureNoPending(string hash)
    {
        if ((await repository.OperationsAsync()).Any(r => r.Hash == hash &&
            (r.Stage is not ("active" or "installed") || r.Kind == "temporary" && !active.ContainsKey(hash))))
            throw new InvalidOperationException("此字体存在未完成操作，请先执行状态恢复");
    }
    private Task<string> Activate(FaceRow face, CancellationToken ct) =>
        PerformanceLog.FontAsync("activate", face.Hash, face.Path, () => ActivateCore(face, ct));
    private async Task<string> ActivateCore(FaceRow face, CancellationToken ct)
    {
        if (active.ContainsKey(face.Hash)) return "已经临时启用";
        // Batch preflight has already checked pending records and reserved all names.
        string source = await CheckedSource(face, ct);
        string path = Path.Combine(platform.TemporaryDirectory, face.Hash + Path.GetExtension(source).ToLowerInvariant());
        long id = await repository.BeginOperationAsync(face.Hash, "temporary", path, "");
        var record = new OperationRecord(id, face.Hash, "temporary", "prepared", path, "", "", false);
        bool loaded = false;
        try
        {
            await CopyVerified(source, path, face.Hash, ct);
            await repository.UpdateOperationAsync(id, "loading");
            if (Load(path) == 0) throw new IOException("Windows 拒绝加载字体");
            loaded = true;
            // Register in memory immediately, so a subsequent journal failure remains cleanable on exit.
            active[face.Hash] = record with { Stage = "active", Loaded = true };
            await repository.UpdateOperationAsync(id, "active", loaded: true);
            await repository.SetFileStateAsync(face.Hash, false, true, false);
            return "临时启用成功";
        }
        catch (Exception failure)
        {
            PerformanceLog.Error("activation-rollback", failure, new { hash = face.Hash, path, loaded });
            if (!loaded || Unload(path) || !IsLoaded(path))
            {
                active.TryRemove(face.Hash, out _);
                await repository.UpdateOperationAsync(id, "removing", failure.Message);
                try { await CleanupCopy(record, CancellationToken.None); await repository.UpdateOperationAsync(id, "rolled_back"); }
                catch (IOException e) { await repository.UpdateOperationAsync(id, "pending", e.Message); }
            }
            else await repository.UpdateOperationAsync(id, "pending", "加载后回滚失败", true);
            throw;
        }
    }
    private Task<string> Deactivate(string hash, CancellationToken ct, bool notify = true) =>
        PerformanceLog.FontAsync("deactivate", hash, active.TryGetValue(hash, out var record) ? record.Path : null, () => DeactivateCore(hash, ct, notify));
    private async Task<string> DeactivateCore(string hash, CancellationToken ct, bool notify = true)
    {
        if (!active.TryGetValue(hash, out var record)) { PerformanceLog.TryWrite("font-skipped", outcome: "skipped", detail: "本工具未临时启用此字体"); return "本工具未临时启用此字体"; }
        int attempt = cleanupAttempts.AddOrUpdate(record.Id, 1, (_, previous) => previous + 1);
        if (attempt > 1 || !record.Loaded)
            PerformanceLog.TryWrite("font-cleanup-retry", data: new { record.Id, record.Loaded, record.Stage, attempt });
        await repository.UpdateOperationAsync(record.Id, "removing", loaded: record.Loaded);
        if (record.Loaded && !Unload(record.Path) && IsLoaded(record.Path))
        { await repository.UpdateOperationAsync(record.Id, "pending", "Windows 未确认停用", true); throw new IOException("停用失败，保留副本和操作记录"); }
        active[hash] = record with { Loaded = false };
        await repository.UpdateOperationAsync(record.Id, "removing", loaded: false);
        try { await CleanupCopy(record, ct); }
        catch (IOException e) { await repository.UpdateOperationAsync(record.Id, "pending", e.Message); throw; }
        active.TryRemove(hash, out _);
        cleanupAttempts.TryRemove(record.Id, out _);
        await repository.UpdateOperationAsync(record.Id, "removed");
        var install = (await repository.OperationsAsync()).FirstOrDefault(r => r.Hash == hash && r.Kind == "install" && r.Stage == "installed");
        bool installed = install is not null && GetRegistration(install.RegistryName) == install.Path;
        await repository.SetFileStateAsync(hash, installed, false, installed);
        if (notify) NotifyChanged();
        return "临时字体已停用";
    }
    private Task<string> Install(FaceRow face, CancellationToken ct) =>
        PerformanceLog.FontAsync("install", face.Hash, face.Path, () => InstallCore(face, ct));
    private async Task<string> InstallCore(FaceRow face, CancellationToken ct)
    {
        await EnsureNoPending(face.Hash);
        foreach (var prior in (await repository.OperationsAsync()).Where(r => r.Hash == face.Hash && r.Kind == "install" && r.Stage == "installed"))
            if (GetRegistration(prior.RegistryName) != prior.Path || !await OwnedCopyMatches(prior, ct))
                throw new InvalidOperationException("已有安装记录被外部修改，请先核对与恢复状态");
        if (await CheckConflict(face.Hash, ct)) return "相同内容已经安装";
        string source = await CheckedSource(face, ct);
        string path = Path.Combine(platform.UserFontsDirectory, "FontCrate-" + face.Hash + Path.GetExtension(source).ToLowerInvariant());
        string name = "FontCrate " + face.Hash + " (TrueType)";
        if (GetRegistration(name) is not null) throw new IOException("目标注册项已存在，未覆盖");
        // Persistent resources must use a separate path from temporary resources.
        long id = await repository.BeginOperationAsync(face.Hash, "install", path, name);
        var record = new OperationRecord(id, face.Hash, "install", "prepared", path, name, "", false);
        bool loaded = false;
        try
        {
            await CopyVerified(source, path, face.Hash, ct);
            await repository.UpdateOperationAsync(id, "registering");
            SetRegistration(name, path);
            await repository.UpdateOperationAsync(id, "loading");
            if (Load(path) == 0) throw new IOException("Windows 拒绝加载安装字体");
            loaded = true;
            await repository.UpdateOperationAsync(id, "installed", loaded: true);
            await repository.SetFileStateAsync(face.Hash, true, active.ContainsKey(face.Hash), true);
        }
        catch (Exception e)
        {
            PerformanceLog.Error("installation-rollback", e, new { hash = face.Hash, path, loaded });
            bool safe = !loaded || Unload(path) || !IsLoaded(path);
            if (GetRegistration(name) == path) RemoveRegistration(name);
            if (safe)
            {
                await repository.UpdateOperationAsync(id, "removing", e.Message);
                try { await CleanupCopy(record, CancellationToken.None); await repository.UpdateOperationAsync(id, "rolled_back", e.Message); }
                catch { await repository.UpdateOperationAsync(id, "pending", e.Message); }
            }
            else await repository.UpdateOperationAsync(id, "pending", e.Message, true);
            throw;
        }
        NotifyChanged();
        if (active.ContainsKey(face.Hash))
        {
            try { await Deactivate(face.Hash, CancellationToken.None); }
            catch (Exception e) { return "已持久安装；临时副本清理待重试：" + e.Message; }
        }
        return "已安装到当前用户";
    }
    private Task<string> Uninstall(string hash, CancellationToken ct) =>
        PerformanceLog.FontAsync("uninstall", hash, null, () => UninstallCore(hash, ct));
    private async Task<string> UninstallCore(string hash, CancellationToken ct)
    {
        var record = (await repository.OperationsAsync()).LastOrDefault(r => r.Hash == hash && r.Kind == "install" && r.Stage is "installed" or "uninstalling");
        if (record is null) throw new InvalidOperationException("仅允许卸载本工具安装的字体");
        using var fontScope = PerformanceLog.FontScope(hash, record.Path);
        if (!await OwnedCopyMatches(record, ct) || GetRegistration(record.RegistryName) != record.Path)
            throw new InvalidOperationException("安装内容或注册项已被外部修改，未执行卸载");
        await repository.UpdateOperationAsync(record.Id, "uninstalling", loaded: true);
        if (IsLoaded(record.Path) && !Unload(record.Path)) throw new IOException("Windows 未确认卸载，保留注册和文件");
        await repository.UpdateOperationAsync(record.Id, "uninstalling", loaded: false);
        RemoveRegistration(record.RegistryName);
        await repository.UpdateOperationAsync(record.Id, "deleting");
        await repository.SetFileStateAsync(hash, false, active.ContainsKey(hash), false);
        NotifyChanged();
        try { await CleanupCopy(record, ct); await repository.UpdateOperationAsync(record.Id, "removed"); }
        catch (IOException e) { await repository.UpdateOperationAsync(record.Id, "deleting", e.Message); throw new IOException("注册已撤销，副本被占用，稍后重试清理", e); }
        return "已卸载本工具安装的字体";
    }
    public Task<IReadOnlyList<OperationResult>> RecoverAsync(CancellationToken ct) =>
        PerformanceLog.BatchAsync("recover", () => RecoverCoreAsync(ct), summarize: results => Summary(results), ct: ct);
    private async Task<IReadOnlyList<OperationResult>> RecoverCoreAsync(CancellationToken ct)
    {
        await PerformanceLog.StepAsync("batch-lock", () => gate.WaitAsync(ct));
        try
        {
            var results = new List<OperationResult>();
            foreach (var record in await repository.OperationsAsync())
            {
                ct.ThrowIfCancellationRequested();
                using var fontScope = PerformanceLog.FontScope(record.Hash, record.Path);
                if (record.Kind == "temporary" && active.ContainsKey(record.Hash))
                { PerformanceLog.TryWrite("recovery-record-skipped", outcome: "skipped", detail: "字体仍由当前会话管理"); continue; }
                bool loadOutstanding = record.Loaded || record.Stage == "loading";
                PerformanceLog.TryWrite("recovery-record-start", data: record, outcome: "started");
                try
                {
                    if (record.Kind == "install" && record.Stage == "installed")
                    {
                        if (!await OwnedCopyMatches(record, ct) || GetRegistration(record.RegistryName) != record.Path)
                        {
                            await repository.UpdateOperationAsync(record.Id, "conflict", "外部修改，撤销所有权");
                            await repository.SetFileStateAsync(record.Hash, false, active.ContainsKey(record.Hash), false);
                            results.Add(new(record.Hash, false, "安装记录与系统不匹配，保留外部内容"));
                            PerformanceLog.TryWrite("recovery-record-conflict", level: "Warning", outcome: "failed", detail: "安装记录与系统不匹配，保留外部内容"); continue;
                        }
                        // After a process restart the OS owns the persistent load; do not claim an Add reference.
                        await repository.UpdateOperationAsync(record.Id, "installed", loaded: false);
                        await repository.SetFileStateAsync(record.Hash, true, false, true);
                        PerformanceLog.TryWrite("recovery-record-complete", outcome: "completed", detail: "持久安装校验通过"); continue;
                    }
                    if (record.Stage == "conflict") { results.Add(new(record.Hash, false, record.Message)); PerformanceLog.TryWrite("recovery-record-conflict", level: "Warning", outcome: "failed", detail: record.Message); continue; }
                    if (File.Exists(record.Path) && !await OwnedCopyMatches(record, ct)) throw new IOException("副本被外部修改，禁止清理");
                    if (record.RegistryName.Length > 0)
                    {
                        string? registered = GetRegistration(record.RegistryName);
                        if (registered is not null && registered != record.Path) throw new IOException("注册项被外部修改，禁止清理");
                        if (registered == record.Path) RemoveRegistration(record.RegistryName);
                    }
                    // Unique content-addressed paths prevent removing another application's registration.
                    if (loadOutstanding && !Unload(record.Path) && IsLoaded(record.Path))
                        throw new IOException("字体仍在系统中加载，保留副本等待重试");
                    loadOutstanding = false;
                    await repository.UpdateOperationAsync(record.Id, "removing", loaded: false);
                    await CleanupCopy(record, ct);
                    await repository.UpdateOperationAsync(record.Id, "removed");
                    await repository.SetFileStateAsync(record.Hash, false, false, false);
                    results.Add(new(record.Hash, true, "已清理未完成操作"));
                    PerformanceLog.TryWrite("recovery-record-complete", outcome: "completed");
                }
                catch (Exception e) when (e is not OperationCanceledException)
                { PerformanceLog.Error("recovery-record-error", e, new { record.Id, record.Hash, record.Path }); await repository.UpdateOperationAsync(record.Id, record.Stage == "deleting" ? "deleting" : "pending", e.Message, loadOutstanding); results.Add(new(record.Hash, false, e.Message)); }
            }
            NotifyChanged(); await RefreshCore(ct); return results;
        }
        finally { gate.Release(); }
    }
    public Task<IReadOnlyList<OperationResult>> ExitAsync() =>
        PerformanceLog.BatchAsync("exit-cleanup", ExitCoreAsync, summarize: results => Summary(results));
    private async Task<IReadOnlyList<OperationResult>> ExitCoreAsync()
    {
        await PerformanceLog.StepAsync("batch-lock", () => gate.WaitAsync());
        try
        {
            var files = active.Keys.Select(hash => (hash, (string?)null)).ToArray();
            return await DeactivateBatch(files, CancellationToken.None);
        }
        finally { gate.Release(); }
    }
    private Task<IReadOnlyList<SystemFont>> EnumerateAsync(CancellationToken ct) =>
        PerformanceLog.StepAsync("windows-enumerate", () => platform.EnumerateAsync(ct), summarize: fonts => new { count = fonts.Count });
    private int Load(string path) => PerformanceLog.Step("windows-load", () => platform.Load(path), new { path });
    private bool Unload(string path) => PerformanceLog.Step("windows-unload", () => platform.Unload(path), new { path });
    private bool IsLoaded(string path) => PerformanceLog.Step("windows-is-loaded", () => platform.IsLoaded(path), new { path });
    private void NotifyChanged() => PerformanceLog.Step("font-change-notify", platform.NotifyChanged);
    private string? GetRegistration(string name) => PerformanceLog.Step("registration-read", () => platform.GetRegistration(name), new { name });
    private void SetRegistration(string name, string path) => PerformanceLog.Step("registration-write", () => platform.SetRegistration(name, path), new { name, path });
    private void RemoveRegistration(string name) => PerformanceLog.Step("registration-remove", () => platform.RemoveRegistration(name), new { name });
    private Task<bool> OwnedCopyMatches(OperationRecord r, CancellationToken ct) =>
        PerformanceLog.StepAsync("copy-validation", () => OwnedCopyMatchesCore(r, ct), new { r.Hash, r.Path });
    private async Task<bool> OwnedCopyMatchesCore(OperationRecord r, CancellationToken ct)
    {
        string directory = r.Kind == "install" ? platform.UserFontsDirectory : platform.TemporaryDirectory;
        if (!Path.GetDirectoryName(Path.GetFullPath(r.Path))!.Equals(Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase)) return false;
        return File.Exists(r.Path) && await FontIdentity.HashAsync(r.Path, ct) == r.Hash;
    }
    private Task CleanupCopy(OperationRecord record, CancellationToken ct) =>
        PerformanceLog.StepAsync("copy-cleanup", () => CleanupCopyCore(record, ct), new { record.Hash, record.Path });
    private async Task CleanupCopyCore(OperationRecord record, CancellationToken ct)
    {
        if (!File.Exists(record.Path)) return;
        if (!await OwnedCopyMatches(record, ct)) throw new IOException("拒绝删除非本工具副本");
        PerformanceLog.Step("copy-delete", () => File.Delete(record.Path), new { record.Path, record.Hash });
    }
    private static Task CopyVerified(string source, string target, string hash, CancellationToken ct) =>
        PerformanceLog.StepAsync("copy-verified", () => CopyVerifiedCore(source, target, hash, ct), new { source, target, hash });
    private static async Task CopyVerifiedCore(string source, string target, string hash, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        if (File.Exists(target))
        {
            throw new IOException("目标副本已存在，请先核对与恢复状态，未覆盖");
        }
        string temp = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, true))
            await using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, true))
            { await input.CopyToAsync(output, ct); await output.FlushAsync(ct); output.Flush(true); }
            if (await FontIdentity.HashAsync(temp, ct) != hash) throw new IOException("字体副本校验失败");
            File.Move(temp, target);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
}
