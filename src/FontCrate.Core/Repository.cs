using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace FontCrate.Core;

public sealed class Repository
{
    private readonly string connectionString;
    private readonly SemaphoreSlim gate = new(1);
    public event Action? Changed;
    public Repository(string databasePath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(databasePath))!);
        connectionString = new SqliteConnectionStringBuilder { DataSource = databasePath, ForeignKeys = true }.ToString();
    }
    private async Task<T> Use<T>(Func<SqliteConnection, Task<T>> action)
    {
        await gate.WaitAsync();
        try
        {
            // Microsoft.Data.Sqlite async methods can execute synchronously; keep disk work off the UI thread.
            return await Task.Run(async () => { await using var c = new SqliteConnection(connectionString); await c.OpenAsync(); return await action(c); });
        }
        finally { gate.Release(); }
    }
    private static SqliteCommand Cmd(SqliteConnection c, string sql, params (string, object?)[] args)
    {
        var cmd = c.CreateCommand(); cmd.CommandText = sql;
        foreach (var (key, value) in args) cmd.Parameters.AddWithValue(key, value ?? DBNull.Value);
        return cmd;
    }
    private static async Task<int> Exec(SqliteConnection c, string sql, params (string, object?)[] args)
    { using var cmd = Cmd(c, sql, args); return await cmd.ExecuteNonQueryAsync(); }

    public Task InitializeAsync() => Use(async c =>
    {
        using var versionCmd = Cmd(c, "PRAGMA user_version");
        var version = Convert.ToInt32(await versionCmd.ExecuteScalarAsync());
        if (version > 2) throw new InvalidDataException("数据库来自较新版本，禁止降级打开");
        await Exec(c, "PRAGMA journal_mode=WAL; PRAGMA busy_timeout=5000;");
        if (version == 2) return 0;
        PerformanceLog.Write("database-upgrade-start", 0, $"from={version}; to=2");
        using var tx = c.BeginTransaction();
        if (version == 0) await Exec(c, """
            CREATE TABLE sources(id INTEGER PRIMARY KEY,path TEXT NOT NULL UNIQUE COLLATE NOCASE,available INTEGER NOT NULL DEFAULT 1);
            CREATE TABLE files(hash TEXT PRIMARY KEY,size INTEGER NOT NULL,installed INTEGER NOT NULL DEFAULT 0,active INTEGER NOT NULL DEFAULT 0,owned INTEGER NOT NULL DEFAULT 0);
            CREATE TABLE locations(source_id INTEGER NOT NULL REFERENCES sources(id) ON DELETE CASCADE,path TEXT NOT NULL COLLATE NOCASE,hash TEXT REFERENCES files(hash),size INTEGER NOT NULL,modified INTEGER NOT NULL,available INTEGER NOT NULL,seen TEXT NOT NULL,PRIMARY KEY(source_id,path));
            CREATE INDEX locations_hash ON locations(hash,available);
            CREATE TABLE faces(id TEXT PRIMARY KEY,hash TEXT NOT NULL REFERENCES files(hash),face_index INTEGER NOT NULL,family TEXT NOT NULL,style TEXT NOT NULL,full_name TEXT NOT NULL,postscript TEXT NOT NULL,version TEXT NOT NULL,weight INTEGER NOT NULL,width INTEGER NOT NULL,italic INTEGER NOT NULL,variable INTEGER NOT NULL,coverage TEXT NOT NULL,favorite INTEGER NOT NULL DEFAULT 0,UNIQUE(hash,face_index));
            CREATE INDEX faces_family ON faces(family COLLATE NOCASE,style COLLATE NOCASE,id);
            CREATE TABLE tags(id INTEGER PRIMARY KEY,name TEXT NOT NULL UNIQUE COLLATE NOCASE);
            CREATE TABLE face_tags(face_id TEXT REFERENCES faces(id) ON DELETE CASCADE,tag_id INTEGER REFERENCES tags(id) ON DELETE CASCADE,PRIMARY KEY(face_id,tag_id));
            CREATE TABLE scan_errors(source_id INTEGER REFERENCES sources(id) ON DELETE CASCADE,path TEXT NOT NULL,message TEXT NOT NULL,PRIMARY KEY(source_id,path));
            CREATE TABLE operations(id INTEGER PRIMARY KEY,hash TEXT NOT NULL REFERENCES files(hash),kind TEXT NOT NULL,stage TEXT NOT NULL,path TEXT NOT NULL,registry_name TEXT NOT NULL,message TEXT NOT NULL DEFAULT '',loaded INTEGER NOT NULL DEFAULT 0);
            CREATE INDEX operations_hash ON operations(hash,kind,stage);
            PRAGMA user_version=2;
            """);
        // Version 1 stored two equivalent classification systems. Merge by SQLite's
        // existing NOCASE rule, preserving tag IDs and the union of memberships.
        if (version == 1) await Exec(c, """
            INSERT INTO tags(name) SELECT name FROM collections WHERE 1 ON CONFLICT(name) DO NOTHING;
            INSERT INTO face_tags(face_id,tag_id)
              SELECT cf.face_id,t.id FROM collection_faces cf
              JOIN collections c ON c.id=cf.collection_id JOIN tags t ON t.name=c.name COLLATE NOCASE
              WHERE 1 ON CONFLICT(face_id,tag_id) DO NOTHING;
            DROP TABLE collection_faces;
            DROP TABLE collections;
            PRAGMA user_version=2;
            """);
        tx.Commit(); PerformanceLog.Write("database-upgrade-complete", 0, "version=2"); return 0;
    });

    public Task<Source> AddSourceAsync(string path) => Use(async c =>
    {
        path = Path.GetFullPath(path);
        await Exec(c, "INSERT INTO sources(path) VALUES($p) ON CONFLICT(path) DO NOTHING", ("$p", path));
        using var cmd = Cmd(c, "SELECT id,path,available FROM sources WHERE path=$p", ("$p", path));
        using var r = await cmd.ExecuteReaderAsync(); await r.ReadAsync();
        return new Source(r.GetInt64(0), r.GetString(1), r.GetBoolean(2));
    });
    public Task<IReadOnlyList<Source>> SourcesAsync() => Use<IReadOnlyList<Source>>(async c =>
    {
        using var cmd = Cmd(c, "SELECT id,path,available FROM sources ORDER BY path"); using var r = await cmd.ExecuteReaderAsync();
        var list = new List<Source>(); while (await r.ReadAsync()) list.Add(new(r.GetInt64(0), r.GetString(1), r.GetBoolean(2))); return list;
    });
    public Task<IReadOnlyList<SourceSummary>> SourceSummariesAsync() => Use<IReadOnlyList<SourceSummary>>(async c =>
    {
        using var cmd = Cmd(c, """
            SELECT s.id,s.path,s.available,COUNT(DISTINCT l.hash),COUNT(DISTINCT f.id)
            FROM sources s LEFT JOIN locations l ON l.source_id=s.id LEFT JOIN faces f ON f.hash=l.hash
            GROUP BY s.id ORDER BY s.path COLLATE NOCASE,s.id
            """);
        using var r = await cmd.ExecuteReaderAsync();
        var result = new List<SourceSummary>();
        while (await r.ReadAsync()) result.Add(new(r.GetInt64(0), r.GetString(1), r.GetBoolean(2), r.GetInt32(3), r.GetInt32(4)));
        return result;
    });

    public async Task RemoveSourceAsync(long id)
    { await Use(c => Exec(c, "DELETE FROM sources WHERE id=$id", ("$id", id))); Changed?.Invoke(); }
    public Task SetSourceAvailabilityAsync(long id, bool available) => Use(async c =>
    {
        await Exec(c, "UPDATE sources SET available=$a WHERE id=$id", ("$a", available), ("$id", id));
        if (!available) await Exec(c, "UPDATE locations SET available=0 WHERE source_id=$id", ("$id", id));
        return 0;
    });
    public Task<IndexedFile?> FindLocationAsync(long source, string path) => Use<IndexedFile?>(async c =>
    {
        using var cmd = Cmd(c, "SELECT size,modified,hash FROM locations WHERE source_id=$s AND path=$p", ("$s", source), ("$p", path));
        using var r = await cmd.ExecuteReaderAsync(); if (!await r.ReadAsync()) return null;
        return new(path, r.GetInt64(0), r.GetInt64(1), r.IsDBNull(2) ? null : r.GetString(2), null);
    });
    public Task<bool> HasFileAsync(string hash) => Use(async c =>
    {
        using var cmd = Cmd(c, "SELECT EXISTS(SELECT 1 FROM faces WHERE hash=$h)", ("$h", hash)); return Convert.ToInt32(await cmd.ExecuteScalarAsync()) != 0;
    });

    public async Task SaveBatchAsync(long source, string scan, IReadOnlyList<IndexedFile> batch)
    {
        await Use(async c =>
        {
            using var tx = c.BeginTransaction();
            foreach (var f in batch)
            {
                if (f.Hash is not null && f.Error is null)
                {
                    await Exec(c, "INSERT INTO files(hash,size) VALUES($h,$s) ON CONFLICT(hash) DO NOTHING", ("$h", f.Hash), ("$s", f.Size));
                    if (f.Faces is not null)
                        foreach (var face in f.Faces)
                            await Exec(c, """
                                INSERT INTO faces(id,hash,face_index,family,style,full_name,postscript,version,weight,width,italic,variable,coverage)
                                VALUES($id,$h,$i,$f,$s,$n,$p,$v,$w,$wd,$it,$var,$c) ON CONFLICT(id) DO NOTHING
                                """, ("$id", FontIdentity.FaceId(f.Hash, face.Index)), ("$h", f.Hash), ("$i", face.Index),
                                ("$f", face.Family), ("$s", face.Style), ("$n", face.FullName), ("$p", face.PostScriptName),
                                ("$v", face.Version), ("$w", face.Weight), ("$wd", face.Width), ("$it", face.Italic),
                                ("$var", face.Variable), ("$c", JsonSerializer.Serialize(face.Coverage)));
                    await Exec(c, "DELETE FROM scan_errors WHERE source_id=$s AND path=$p", ("$s", source), ("$p", f.Path));
                }
                else
                    await Exec(c, "INSERT INTO scan_errors VALUES($s,$p,$e) ON CONFLICT(source_id,path) DO UPDATE SET message=excluded.message",
                        ("$s", source), ("$p", f.Path), ("$e", f.Error ?? "字体解析失败"));
                await Exec(c, """
                    INSERT INTO locations(source_id,path,hash,size,modified,available,seen) VALUES($s,$p,$h,$z,$m,$a,$v)
                    ON CONFLICT(source_id,path) DO UPDATE SET hash=excluded.hash,size=excluded.size,modified=excluded.modified,available=excluded.available,seen=excluded.seen
                    """, ("$s", source), ("$p", f.Path), ("$h", f.Error is null ? f.Hash : null), ("$z", f.Size),
                    ("$m", f.ModifiedTicks), ("$a", f.Error is null), ("$v", scan));
            }
            tx.Commit(); return 0;
        });
        Changed?.Invoke();
    }
    public async Task FinishScanAsync(long source, string scan, bool complete)
    {
        if (complete) await Use(c => Exec(c, "UPDATE locations SET available=0 WHERE source_id=$s AND seen<>$v", ("$s", source), ("$v", scan)));
        Changed?.Invoke();
    }
    public Task RecordErrorAsync(long source, string path, string message) => Use(c => Exec(c,
        "INSERT INTO scan_errors VALUES($s,$p,$e) ON CONFLICT(source_id,path) DO UPDATE SET message=excluded.message",
        ("$s", source), ("$p", path), ("$e", message)));
    public Task<IReadOnlyList<ScanError>> ErrorsAsync() => Use<IReadOnlyList<ScanError>>(async c =>
    {
        using var cmd = Cmd(c, "SELECT path,message FROM scan_errors ORDER BY path LIMIT 1000"); using var r = await cmd.ExecuteReaderAsync();
        var list = new List<ScanError>(); while (await r.ReadAsync()) list.Add(new(r.GetString(0), r.GetString(1))); return list;
    });

    public Task<FacePage> QueryAsync(FaceQuery q) => Use(async c =>
    {
        var where = """
            WHERE ($text='' OR f.family LIKE $text ESCAPE '\' OR f.style LIKE $text ESCAPE '\' OR f.full_name LIKE $text ESCAPE '\' OR f.postscript LIKE $text ESCAPE '\')
            AND ($source IS NULL OR EXISTS(SELECT 1 FROM locations l WHERE l.hash=f.hash AND l.source_id=$source))
            AND ($tag IS NULL OR EXISTS(SELECT 1 FROM face_tags t WHERE t.face_id=f.id AND t.tag_id=$tag))
            AND ($favorite=0 OR f.favorite=1)
            AND ($state='全部' OR ($state='已安装' AND x.installed=1) OR ($state='临时启用' AND x.active=1)
                OR ($state='来源不可用' AND NOT EXISTS(SELECT 1 FROM locations l WHERE l.hash=f.hash AND l.available=1))
                OR ($state='完全重复' AND (SELECT COUNT(DISTINCT path) FROM locations l WHERE l.hash=f.hash AND l.available=1)>1))
            """;
        string text = q.Text.Trim();
        text = text.Length == 0 ? "" : "%" + text.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%";
        (string, object?)[] args = [("$text", text), ("$source", q.SourceId), ("$tag", q.TagId),
            ("$favorite", q.FavoritesOnly), ("$state", q.State)];
        using var count = Cmd(c, "SELECT COUNT(*) FROM faces f JOIN files x ON x.hash=f.hash " + where, args);
        int total = Convert.ToInt32(await count.ExecuteScalarAsync());
        using var cmd = Cmd(c, """
            SELECT f.id,f.hash,f.face_index,f.family,f.style,f.full_name,f.postscript,f.version,f.weight,f.width,f.italic,f.variable,f.favorite,
              (SELECT path FROM locations l WHERE l.hash=f.hash AND l.available=1 ORDER BY path LIMIT 1),
              (SELECT COUNT(DISTINCT path) FROM locations l WHERE l.hash=f.hash AND l.available=1),x.installed,x.active,x.owned,
              (SELECT path FROM locations l WHERE l.hash=f.hash ORDER BY l.available DESC,l.path LIMIT 1)
            FROM faces f JOIN files x ON x.hash=f.hash
            """ + " " + where + " ORDER BY f.family COLLATE NOCASE,f.style COLLATE NOCASE,f.id", args);
        using var r = await cmd.ExecuteReaderAsync(); var rows = new List<FaceRow>();
        while (await r.ReadAsync()) rows.Add(new(r.GetString(0), r.GetString(1), r.GetInt32(2), r.GetString(3), r.GetString(4),
            r.GetString(5), r.GetString(6), r.GetString(7), r.GetInt32(8), r.GetInt32(9), r.GetBoolean(10), r.GetBoolean(11),
            r.GetBoolean(12), r.IsDBNull(13) ? null : r.GetString(13), r.GetInt32(14), r.GetBoolean(15), r.GetBoolean(16), r.GetBoolean(17), r.IsDBNull(18) ? null : r.GetString(18)));
        return new FacePage(rows, total);
    });
    public Task<IReadOnlyList<ParsedFace>> FacesForFileAsync(string hash) => Use<IReadOnlyList<ParsedFace>>(async c =>
    {
        using var cmd = Cmd(c, "SELECT face_index,family,style,full_name,postscript,version,weight,width,italic,variable,coverage FROM faces WHERE hash=$h ORDER BY face_index", ("$h", hash));
        using var r = await cmd.ExecuteReaderAsync(); var list = new List<ParsedFace>();
        while (await r.ReadAsync()) list.Add(new(r.GetInt32(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4), r.GetString(5),
            r.GetInt32(6), r.GetInt32(7), r.GetBoolean(8), r.GetBoolean(9), JsonSerializer.Deserialize<List<UnicodeRange>>(r.GetString(10))!));
        return list;
    });
    public Task<IReadOnlyList<Location>> LocationsAsync(string hash) => Use<IReadOnlyList<Location>>(async c =>
    {
        using var cmd = Cmd(c, "SELECT path,MAX(available) FROM locations WHERE hash=$h GROUP BY path ORDER BY path", ("$h", hash));
        using var r = await cmd.ExecuteReaderAsync(); var list = new List<Location>(); while (await r.ReadAsync()) list.Add(new(r.GetString(0), r.GetBoolean(1))); return list;
    });
    public Task<IReadOnlyList<NamedItem>> NamedItemsAsync() => Use<IReadOnlyList<NamedItem>>(async c =>
    {
        using var cmd = Cmd(c, "SELECT id,name FROM tags ORDER BY name");
        using var r = await cmd.ExecuteReaderAsync(); var list = new List<NamedItem>(); while (await r.ReadAsync()) list.Add(new(r.GetInt64(0), r.GetString(1))); return list;
    });
    public Task<IReadOnlyList<NamedItem>> FaceItemsAsync(string faceId) => Use<IReadOnlyList<NamedItem>>(async c =>
    {
        const string table = "tags", relation = "face_tags", key = "tag_id";
        using var cmd = Cmd(c, $"SELECT n.id,n.name FROM {table} n JOIN {relation} r ON r.{key}=n.id WHERE r.face_id=$f ORDER BY n.name", ("$f", faceId));
        using var r = await cmd.ExecuteReaderAsync(); var list = new List<NamedItem>(); while (await r.ReadAsync()) list.Add(new(r.GetInt64(0), r.GetString(1))); return list;
    });
    private static string AnnotationName(string name)
    {
        name = name.Trim();
        if (name.Length is < 1 or > 100) throw new ArgumentException("名称应为 1–100 个字符");
        return name;
    }
    public Task<IReadOnlyList<NamedItem>> NamedItemCountsAsync() => Use<IReadOnlyList<NamedItem>>(async c =>
    {
        const string table = "tags", relation = "face_tags", key = "tag_id";
        using var cmd = Cmd(c, $"SELECT n.id,n.name,COUNT(r.face_id) FROM {table} n LEFT JOIN {relation} r ON r.{key}=n.id GROUP BY n.id ORDER BY n.name COLLATE NOCASE,n.id");
        using var r = await cmd.ExecuteReaderAsync(); var list = new List<NamedItem>();
        while (await r.ReadAsync()) list.Add(new(r.GetInt64(0), r.GetString(1), r.GetInt32(2)));
        return list;
    });
    public async Task<NamedItem> CreateNamedItemAsync(string name, IEnumerable<string>? faceIds = null)
    {
        name = AnnotationName(name); const string table = "tags";
        var ids = faceIds?.Distinct().ToArray() ?? [];
        var elapsed = System.Diagnostics.Stopwatch.StartNew();
        PerformanceLog.Write("tag-create-start", 0, $"faces={ids.Length}");
        var item = await Use(async c =>
        {
            using var tx = c.BeginTransaction();
            try { await Exec(c, $"INSERT INTO {table}(name) VALUES($n)", ("$n", name)); }
            catch (SqliteException ex) when (ex.SqliteExtendedErrorCode == 2067)
            { throw new ArgumentException("该名称已存在，请使用其他名称", ex); }
            using var cmd = Cmd(c, "SELECT last_insert_rowid()");
            long id = Convert.ToInt64(await cmd.ExecuteScalarAsync());
            foreach (string faceId in ids)
                await Exec(c, "INSERT INTO face_tags(face_id,tag_id) VALUES($face,$tag)", ("$face", faceId), ("$tag", id));
            tx.Commit();
            return new NamedItem(id, name, ids.Length);
        });
        PerformanceLog.Write("tag-create-committed", elapsed.Elapsed.TotalMilliseconds, $"tag={item.Id}; faces={ids.Length}");
        Changed?.Invoke(); return item;
    }
    public async Task RenameNamedItemAsync(long id, string name)
    {
        name = AnnotationName(name); const string table = "tags";
        await Use(async c =>
        {
            try
            {
                if (await Exec(c, $"UPDATE {table} SET name=$n WHERE id=$id", ("$n", name), ("$id", id)) == 0)
                    throw new InvalidOperationException("该项目已不存在，请刷新后重试");
            }
            catch (SqliteException ex) when (ex.SqliteExtendedErrorCode == 2067)
            { throw new ArgumentException("该名称已存在，请使用其他名称", ex); }
            return 0;
        }); Changed?.Invoke();
    }
    public async Task DeleteNamedItemAsync(long id)
    {
        const string table = "tags";
        await Use(c => Exec(c, $"DELETE FROM {table} WHERE id=$id", ("$id", id))); Changed?.Invoke();
    }
    public async Task AnnotateByIdAsync(IEnumerable<string> ids, long itemId, bool remove = false)
    {
        var faceIds = ids.Distinct().ToArray();
        var elapsed = System.Diagnostics.Stopwatch.StartNew();
        PerformanceLog.Write("tag-membership-start", 0, $"tag={itemId}; faces={faceIds.Length}; remove={remove}");
        const string table = "tags", relation = "face_tags", key = "tag_id";
        await Use(async c =>
        {
            using var tx = c.BeginTransaction();
            using var exists = Cmd(c, $"SELECT EXISTS(SELECT 1 FROM {table} WHERE id=$id)", ("$id", itemId));
            if (Convert.ToInt32(await exists.ExecuteScalarAsync()) == 0)
                throw new InvalidOperationException("该项目已不存在，请重新选择");
            foreach (var id in faceIds)
                await Exec(c, remove ? $"DELETE FROM {relation} WHERE face_id=$f AND {key}=$id" :
                    $"INSERT INTO {relation}(face_id,{key}) VALUES($f,$id) ON CONFLICT(face_id,{key}) DO NOTHING",
                    ("$f", id), ("$id", itemId));
            tx.Commit(); return 0;
        });
        PerformanceLog.Write("tag-membership-committed", elapsed.Elapsed.TotalMilliseconds, $"tag={itemId}; faces={faceIds.Length}; remove={remove}");
        Changed?.Invoke();
    }
    public async Task SetFavoriteAsync(IEnumerable<string> ids, bool favorite)
    {
        await Use(async c => { using var tx = c.BeginTransaction(); foreach (var id in ids) await Exec(c, "UPDATE faces SET favorite=$v WHERE id=$id", ("$v", favorite), ("$id", id)); tx.Commit(); return 0; }); Changed?.Invoke();
    }
    public async Task AnnotateAsync(IEnumerable<string> ids, string name, bool remove = false)
    {
        name = AnnotationName(name);
        const string table = "tags", relation = "face_tags", key = "tag_id";
        await Use(async c =>
        {
            using var tx = c.BeginTransaction();
            if (!remove) await Exec(c, $"INSERT INTO {table}(name) VALUES($n) ON CONFLICT(name) DO NOTHING", ("$n", name));
            foreach (var id in ids)
                await Exec(c, remove ? $"DELETE FROM {relation} WHERE face_id=$f AND {key}=(SELECT id FROM {table} WHERE name=$n)" :
                    $"INSERT OR IGNORE INTO {relation}(face_id,{key}) SELECT $f,id FROM {table} WHERE name=$n", ("$f", id), ("$n", name));
            tx.Commit(); return 0;
        }); Changed?.Invoke();
    }

    public async Task SetFileStateAsync(string hash, bool installed, bool active, bool owned)
    { await Use(c => Exec(c, "UPDATE files SET installed=$i,active=$a,owned=$o WHERE hash=$h", ("$i", installed), ("$a", active), ("$o", owned), ("$h", hash))); Changed?.Invoke(); }
    public async Task RefreshInstalledAsync(IEnumerable<string> hashes)
    {
        await Use(async c => { using var tx = c.BeginTransaction(); await Exec(c, "UPDATE files SET installed=0"); foreach (string h in hashes) await Exec(c, "UPDATE files SET installed=1 WHERE hash=$h", ("$h", h)); tx.Commit(); return 0; }); Changed?.Invoke();
    }
    public Task<long> BeginOperationAsync(string hash, string kind, string path, string registryName) => Use(async c =>
    {
        await Exec(c, "INSERT INTO operations(hash,kind,stage,path,registry_name) VALUES($h,$k,'prepared',$p,$r)", ("$h", hash), ("$k", kind), ("$p", path), ("$r", registryName));
        using var cmd = Cmd(c, "SELECT last_insert_rowid()"); return Convert.ToInt64(await cmd.ExecuteScalarAsync());
    });
    public Task UpdateOperationAsync(long id, string stage, string message = "", bool loaded = false) => Use(c => Exec(c,
        "UPDATE operations SET stage=$s,message=$m,loaded=$l WHERE id=$id", ("$s", stage), ("$m", message), ("$l", loaded), ("$id", id)));
    public Task<IReadOnlyList<OperationRecord>> OperationsAsync() => Use<IReadOnlyList<OperationRecord>>(async c =>
    {
        using var cmd = Cmd(c, "SELECT id,hash,kind,stage,path,registry_name,message,loaded FROM operations WHERE stage NOT IN ('removed','rolled_back') ORDER BY id");
        using var r = await cmd.ExecuteReaderAsync(); var list = new List<OperationRecord>();
        while (await r.ReadAsync()) list.Add(new(r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4), r.GetString(5), r.GetString(6), r.GetBoolean(7))); return list;
    });
}
