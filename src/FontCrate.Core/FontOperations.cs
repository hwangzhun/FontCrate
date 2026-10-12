using System.Collections.Concurrent;

namespace FontCrate.Core;

/// <summary>Serializes batches, bounds concurrent temporary loads, and journals every durable side effect.</summary>
public sealed class FontOperations(Repository repository, IFontPlatform platform)
{
    private readonly SemaphoreSlim gate = new(1);
    private readonly ConcurrentDictionary<string, OperationRecord> active = new(StringComparer.Ordinal);
    public async Task RefreshAsync(CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try { await RefreshCore(ct); } finally { gate.Release(); }
    }
    private async Task RefreshCore(CancellationToken ct)
    {
        var fonts = await platform.EnumerateAsync(ct);
        await repository.RefreshInstalledAsync(fonts.Where(f => f.Hash is not null).Select(f => f.Hash!));
        foreach (var record in await repository.OperationsAsync())
        {
            if (record.Kind == "install" && record.Stage == "installed" && await OwnedCopyMatches(record, ct) &&
                platform.GetRegistration(record.RegistryName) == record.Path)
                await repository.SetFileStateAsync(record.Hash, true, active.ContainsKey(record.Hash), true);
        }
    }
    public async Task<IReadOnlyList<OperationResult>> RunAsync(string action, IReadOnlyList<FaceRow> selected, CancellationToken ct,
        IProgress<OperationProgress>? progress = null)
    {
        await gate.WaitAsync(ct);
        try
        {
            var files = selected.DistinctBy(f => f.Hash).ToArray();
            if (action == "activate") return await ActivateBatch(files, ct, progress);
            var results = new List<OperationResult>();
            progress?.Report(new(0, files.Length));
            foreach (var face in files)
            {
                if (ct.IsCancellationRequested) break;
                try
                {
                    string message = action switch
                    {
                        "deactivate" => await Deactivate(face.Hash, ct),
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
            var system = await platform.EnumerateAsync(ct);
            var pending = await repository.OperationsAsync();
            var reserved = new List<(string Hash, IReadOnlyList<ParsedFace> Faces)>();
            foreach (string hash in active.Keys)
                reserved.Add((hash, await repository.FacesForFileAsync(hash)));
            // Reserve names in selection order before workers start, including all TTC faces.
            for (int i = 0; i < files.Length; i++)
            {
                if (ct.IsCancellationRequested) break;
                var face = files[i];
                try
                {
                    if (active.ContainsKey(face.Hash)) { Finish(i, true, "已经临时启用"); continue; }
                    if (pending.Any(r => r.Hash == face.Hash &&
                        (r.Stage is not ("active" or "installed") || r.Kind == "temporary" && !active.ContainsKey(face.Hash))))
                        throw new InvalidOperationException("此字体存在未完成操作，请先执行状态恢复");
                    var faces = await repository.FacesForFileAsync(face.Hash);
                    foreach (var font in system.Where(f => f.Hash != face.Hash))
                        if (faces.Any(a => font.Faces.Any(b => FontIdentity.NameConflicts(a, b))))
                            throw new InvalidOperationException("与已安装字体名称冲突，未覆盖：" + font.Path);
                    if (reserved.Any(r => r.Hash != face.Hash && faces.Any(a => r.Faces.Any(b => FontIdentity.NameConflicts(a, b)))))
                        throw new InvalidOperationException("与其他临时启用字体或本批字体名称冲突");
                    if (system.Any(f => f.Hash == face.Hash)) { Finish(i, true, "内容已安装，无需临时启用"); continue; }
                    reserved.Add((face.Hash, faces)); ready.Add(i);
                }
                catch (Exception e) when (e is not OperationCanceledException) { Finish(i, false, e.Message); }
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
        finally { if (ready.Count > 0) platform.NotifyChanged(); }
        return results.OfType<OperationResult>().ToArray();
    }
    private async Task<string> CheckedSource(FaceRow face, CancellationToken ct)
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
        var system = await platform.EnumerateAsync(ct);
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
    private async Task<string> Activate(FaceRow face, CancellationToken ct)
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
            if (platform.Load(path) == 0) throw new IOException("Windows 拒绝加载字体");
            loaded = true;
            // Register in memory immediately, so a subsequent journal failure remains cleanable on exit.
            active[face.Hash] = record with { Stage = "active", Loaded = true };
            await repository.UpdateOperationAsync(id, "active", loaded: true);
            await repository.SetFileStateAsync(face.Hash, false, true, false);
            return "临时启用成功";
        }
        catch (Exception failure)
        {
            if (!loaded || platform.Unload(path) || !platform.IsLoaded(path))
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
    private async Task<string> Deactivate(string hash, CancellationToken ct, bool notify = true)
    {
        if (!active.TryGetValue(hash, out var record)) return "本工具未临时启用此字体";
        await repository.UpdateOperationAsync(record.Id, "removing", loaded: record.Loaded);
        if (record.Loaded && !platform.Unload(record.Path) && platform.IsLoaded(record.Path))
        { await repository.UpdateOperationAsync(record.Id, "pending", "Windows 未确认停用", true); throw new IOException("停用失败，保留副本和操作记录"); }
        active[hash] = record with { Loaded = false };
        await repository.UpdateOperationAsync(record.Id, "removing", loaded: false);
        try { await CleanupCopy(record, ct); }
        catch (IOException e) { await repository.UpdateOperationAsync(record.Id, "pending", e.Message); throw; }
        active.TryRemove(hash, out _);
        await repository.UpdateOperationAsync(record.Id, "removed");
        var install = (await repository.OperationsAsync()).FirstOrDefault(r => r.Hash == hash && r.Kind == "install" && r.Stage == "installed");
        bool installed = install is not null && platform.GetRegistration(install.RegistryName) == install.Path;
        await repository.SetFileStateAsync(hash, installed, false, installed);
        if (notify) platform.NotifyChanged();
        return "临时字体已停用";
    }
    private async Task<string> Install(FaceRow face, CancellationToken ct)
    {
        await EnsureNoPending(face.Hash);
        foreach (var prior in (await repository.OperationsAsync()).Where(r => r.Hash == face.Hash && r.Kind == "install" && r.Stage == "installed"))
            if (platform.GetRegistration(prior.RegistryName) != prior.Path || !await OwnedCopyMatches(prior, ct))
                throw new InvalidOperationException("已有安装记录被外部修改，请先核对与恢复状态");
        if (await CheckConflict(face.Hash, ct)) return "相同内容已经安装";
        string source = await CheckedSource(face, ct);
        string path = Path.Combine(platform.UserFontsDirectory, "FontCrate-" + face.Hash + Path.GetExtension(source).ToLowerInvariant());
        string name = "FontCrate " + face.Hash + " (TrueType)";
        if (platform.GetRegistration(name) is not null) throw new IOException("目标注册项已存在，未覆盖");
        // Persistent resources must use a separate path from temporary resources.
        long id = await repository.BeginOperationAsync(face.Hash, "install", path, name);
        var record = new OperationRecord(id, face.Hash, "install", "prepared", path, name, "", false);
        bool loaded = false;
        try
        {
            await CopyVerified(source, path, face.Hash, ct);
            await repository.UpdateOperationAsync(id, "registering");
            platform.SetRegistration(name, path);
            await repository.UpdateOperationAsync(id, "loading");
            if (platform.Load(path) == 0) throw new IOException("Windows 拒绝加载安装字体");
            loaded = true;
            await repository.UpdateOperationAsync(id, "installed", loaded: true);
            await repository.SetFileStateAsync(face.Hash, true, active.ContainsKey(face.Hash), true);
        }
        catch (Exception e)
        {
            bool safe = !loaded || platform.Unload(path) || !platform.IsLoaded(path);
            if (platform.GetRegistration(name) == path) platform.RemoveRegistration(name);
            if (safe)
            {
                await repository.UpdateOperationAsync(id, "removing", e.Message);
                try { await CleanupCopy(record, CancellationToken.None); await repository.UpdateOperationAsync(id, "rolled_back", e.Message); }
                catch { await repository.UpdateOperationAsync(id, "pending", e.Message); }
            }
            else await repository.UpdateOperationAsync(id, "pending", e.Message, true);
            throw;
        }
        platform.NotifyChanged();
        if (active.ContainsKey(face.Hash))
        {
            try { await Deactivate(face.Hash, CancellationToken.None); }
            catch (Exception e) { return "已持久安装；临时副本清理待重试：" + e.Message; }
        }
        return "已安装到当前用户";
    }
    private async Task<string> Uninstall(string hash, CancellationToken ct)
    {
        var record = (await repository.OperationsAsync()).LastOrDefault(r => r.Hash == hash && r.Kind == "install" && r.Stage is "installed" or "uninstalling");
        if (record is null) throw new InvalidOperationException("仅允许卸载本工具安装的字体");
        if (!await OwnedCopyMatches(record, ct) || platform.GetRegistration(record.RegistryName) != record.Path)
            throw new InvalidOperationException("安装内容或注册项已被外部修改，未执行卸载");
        await repository.UpdateOperationAsync(record.Id, "uninstalling", loaded: true);
        if (platform.IsLoaded(record.Path) && !platform.Unload(record.Path)) throw new IOException("Windows 未确认卸载，保留注册和文件");
        await repository.UpdateOperationAsync(record.Id, "uninstalling", loaded: false);
        platform.RemoveRegistration(record.RegistryName);
        await repository.UpdateOperationAsync(record.Id, "deleting");
        await repository.SetFileStateAsync(hash, false, active.ContainsKey(hash), false);
        platform.NotifyChanged();
        try { await CleanupCopy(record, ct); await repository.UpdateOperationAsync(record.Id, "removed"); }
        catch (IOException e) { await repository.UpdateOperationAsync(record.Id, "deleting", e.Message); throw new IOException("注册已撤销，副本被占用，稍后重试清理", e); }
        return "已卸载本工具安装的字体";
    }
    public async Task<IReadOnlyList<OperationResult>> RecoverAsync(CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            var results = new List<OperationResult>();
            foreach (var record in await repository.OperationsAsync())
            {
                ct.ThrowIfCancellationRequested();
                if (record.Kind == "temporary" && active.ContainsKey(record.Hash)) continue;
                bool loadOutstanding = record.Loaded || record.Stage == "loading";
                try
                {
                    if (record.Kind == "install" && record.Stage == "installed")
                    {
                        if (!await OwnedCopyMatches(record, ct) || platform.GetRegistration(record.RegistryName) != record.Path)
                        {
                            await repository.UpdateOperationAsync(record.Id, "conflict", "外部修改，撤销所有权");
                            await repository.SetFileStateAsync(record.Hash, false, active.ContainsKey(record.Hash), false);
                            results.Add(new(record.Hash, false, "安装记录与系统不匹配，保留外部内容")); continue;
                        }
                        // After a process restart the OS owns the persistent load; do not claim an Add reference.
                        await repository.UpdateOperationAsync(record.Id, "installed", loaded: false);
                        await repository.SetFileStateAsync(record.Hash, true, false, true); continue;
                    }
                    if (record.Stage == "conflict") { results.Add(new(record.Hash, false, record.Message)); continue; }
                    if (File.Exists(record.Path) && !await OwnedCopyMatches(record, ct)) throw new IOException("副本被外部修改，禁止清理");
                    if (record.RegistryName.Length > 0)
                    {
                        string? registered = platform.GetRegistration(record.RegistryName);
                        if (registered is not null && registered != record.Path) throw new IOException("注册项被外部修改，禁止清理");
                        if (registered == record.Path) platform.RemoveRegistration(record.RegistryName);
                    }
                    // Unique content-addressed paths prevent removing another application's registration.
                    if (loadOutstanding && !platform.Unload(record.Path) && platform.IsLoaded(record.Path))
                        throw new IOException("字体仍在系统中加载，保留副本等待重试");
                    loadOutstanding = false;
                    await repository.UpdateOperationAsync(record.Id, "removing", loaded: false);
                    await CleanupCopy(record, ct);
                    await repository.UpdateOperationAsync(record.Id, "removed");
                    await repository.SetFileStateAsync(record.Hash, false, false, false);
                    results.Add(new(record.Hash, true, "已清理未完成操作"));
                }
                catch (Exception e) when (e is not OperationCanceledException)
                { await repository.UpdateOperationAsync(record.Id, record.Stage == "deleting" ? "deleting" : "pending", e.Message, loadOutstanding); results.Add(new(record.Hash, false, e.Message)); }
            }
            platform.NotifyChanged(); await RefreshCore(ct); return results;
        }
        finally { gate.Release(); }
    }
    public async Task<IReadOnlyList<OperationResult>> ExitAsync()
    {
        await gate.WaitAsync();
        try
        {
            var results = new List<OperationResult>();
            var hashes = active.Keys.ToArray();
            foreach (string hash in hashes)
            {
                try { results.Add(new(hash, true, await Deactivate(hash, CancellationToken.None, notify: false))); }
                catch (Exception e) { results.Add(new(hash, false, e.Message)); }
            }
            // Notify other applications even if unloading succeeded but subsequent file cleanup failed.
            if (hashes.Length > 0) platform.NotifyChanged();
            return results;
        }
        finally { gate.Release(); }
    }
    private async Task<bool> OwnedCopyMatches(OperationRecord r, CancellationToken ct)
    {
        string directory = r.Kind == "install" ? platform.UserFontsDirectory : platform.TemporaryDirectory;
        if (!Path.GetDirectoryName(Path.GetFullPath(r.Path))!.Equals(Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase)) return false;
        return File.Exists(r.Path) && await FontIdentity.HashAsync(r.Path, ct) == r.Hash;
    }
    private async Task CleanupCopy(OperationRecord record, CancellationToken ct)
    {
        if (!File.Exists(record.Path)) return;
        if (!await OwnedCopyMatches(record, ct)) throw new IOException("拒绝删除非本工具副本");
        File.Delete(record.Path);
    }
    private static async Task CopyVerified(string source, string target, string hash, CancellationToken ct)
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
