using System.Text.Json;
using System.Security.Cryptography;

namespace AudioWorkflow;

public sealed record CleanupRoot(string Directory,string Label,bool IsImported=false);

public sealed record CleanupProject(string Directory,string Name,long Bytes,string Fingerprint,string? ProtectedReason,
    string? Warning = null,string SourceLabel="目前版本",string RootDirectory="",bool IsImported=false)
{
    public override string ToString()=>$"[{SourceLabel}]　{Name}　{Bytes/1048576d:N1} MiB　{Path.GetFileName(Directory)}"+
        (ProtectedReason==null?"":"　[保留："+ProtectedReason+"]")+(Warning==null?"":"　[注意："+Warning+"]");
}

/// <summary>Whole-project cleanup only: never remove individual immutable revisions or SRT files.</summary>
public static class WorkCleanupCatalog
{
    public static CleanupRoot[] PortableRoots(PortablePathMap paths)
    {
        var dataRoot=Path.Combine(paths.Root,"Data");
        var roots=new List<CleanupRoot>
        {
            new(Path.Combine(dataRoot,"AudioWork"),"目前版本"),
            new(Path.Combine(dataRoot,"Imported-0.4"),"舊版 0.4",true),
            new(Path.Combine(dataRoot,"Imported-0.6"),"舊版 0.6",true),
        };
        if(Directory.Exists(dataRoot))
            foreach(var directory in Directory.EnumerateDirectories(dataRoot,"Imported-*",SearchOption.TopDirectoryOnly)
                        .OrderBy(path=>path,StringComparer.OrdinalIgnoreCase))
            {
                var full=Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar);
                if(roots.Any(root=>Path.GetFullPath(root.Directory).TrimEnd(Path.DirectorySeparatorChar)
                       .Equals(full,StringComparison.OrdinalIgnoreCase)))continue;
                var suffix=Path.GetFileName(full)["Imported-".Length..];
                roots.Add(new(full,string.IsNullOrWhiteSpace(suffix)?"舊版匯入":"舊版 "+suffix,true));
            }
        return roots.ToArray();
    }

    public static CleanupProject[] Scan(string workRoot,string? activeDirectory,IEnumerable<string>? activeFiles=null)
        =>Scan([new CleanupRoot(workRoot,"目前版本")],activeDirectory,activeFiles);

    public static CleanupProject[] Scan(IEnumerable<CleanupRoot> cleanupRoots,string? activeDirectory,
        IEnumerable<string>? activeFiles=null)
    {
        var roots=NormalizeRoots(cleanupRoots);

        var resolvedActiveDirectory=string.IsNullOrEmpty(activeDirectory)?null:ResolvePortable(activeDirectory);
        var resolvedActiveFiles=(activeFiles??[]).Where(path=>!string.IsNullOrEmpty(path)).Select(ResolvePortable).ToArray();
        var references=new List<(string Owner,string Target)>();
        var entries=new List<CleanupProject>();
        foreach(var cleanupRoot in roots)
        {
            var root=cleanupRoot.Directory;
            if(!Directory.Exists(root))continue;
            AssertNoLinks(root);
            var directories=Directory.GetDirectories(root).Where(path=>
                Guid.TryParseExact(Path.GetFileName(path),"N",out _)).ToArray();
            foreach(var dir in directories)
            {
                AssertNoLinks(dir);
                var files=SafeFiles(dir).OrderBy(path=>path,StringComparer.Ordinal).ToArray();
                string name=Path.GetFileName(dir);string? reason=null;
                string? warning=cleanupRoot.IsImported?
                    "舊版匯入專案；移出後將無法在本可攜版開啟，回收筒清空前可還原":null;
                var damagedRecords=new List<string>();
                if(!File.Exists(Path.Combine(dir,"current.syncaudio.json")))reason="缺少工作階段紀錄";
                if(resolvedActiveDirectory!=null&&Within(dir,resolvedActiveDirectory)||resolvedActiveFiles.Any(path=>Within(dir,path)))
                    reason="目前使用／復原中";
                if(files.Any(file=>Path.GetFileName(file).Equals("handoff.json",StringComparison.OrdinalIgnoreCase)))
                    warning=AppendWarning(warning,"含 AU 交接資料；刪除整個專案時會一併移至資源回收筒");
                using var hash=IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                long bytes=0;
                foreach(var file in files)
                {
                    var info=new FileInfo(file);bytes=checked(bytes+info.Length);
                    hash.AppendData(System.Text.Encoding.UTF8.GetBytes($"{Path.GetRelativePath(dir,file)}|{info.Length}|{info.LastWriteTimeUtc.Ticks}\n"));
                    if(!file.EndsWith(".json",StringComparison.OrdinalIgnoreCase))continue;
                    if(info.Length>32*1024*1024)throw new InvalidDataException("清理已停止：專案紀錄過大，無法安全核對引用。");
                    var text=File.ReadAllText(file);hash.AppendData(System.Text.Encoding.UTF8.GetBytes(text));
                    try
                    {
                        using var json=JsonDocument.Parse(text);
                        Visit(json.RootElement,"");
                    }
                    catch(JsonException)
                    {
                        // A damaged historical record must not make the entire cleanup
                        // catalog unusable. Keep its owning project visible but protected;
                        // valid projects can still be reviewed and cleaned independently.
                        damagedRecords.Add(Path.GetRelativePath(dir,file));
                    }
                    void Visit(JsonElement item,string field)
                    {
                        if(item.ValueKind==JsonValueKind.Object)foreach(var property in item.EnumerateObject())Visit(property.Value,property.Name);
                        else if(item.ValueKind==JsonValueKind.Array)foreach(var child in item.EnumerateArray())Visit(child,field);
                        else if(item.ValueKind==JsonValueKind.String&&PortablePathMap.IsPathField(field))
                        {
                            var value=ResolvePortable(item.GetString()!);
                            if(Path.IsPathFullyQualified(value))references.Add((dir,Path.GetFullPath(value)));
                            if(field.Equals("subtitleFileName",StringComparison.OrdinalIgnoreCase)&&!string.IsNullOrEmpty(value))
                                name=Path.GetFileNameWithoutExtension(value);
                        }
                    }
                }
                if(damagedRecords.Count>0&&reason==null)
                {
                    var first=damagedRecords[0];
                    var more=damagedRecords.Count==1?"":$"等 {damagedRecords.Count} 個檔案";
                    reason=$"專案紀錄損壞，無法安全核對引用（{first}{more}）";
                }
                entries.Add(new(dir,name,bytes,Convert.ToHexString(hash.GetHashAndReset()),reason,warning,
                    cleanupRoot.Label,root,cleanupRoot.IsImported));
            }
        }
        return entries.Select(e=>references.Any(r=>!r.Owner.Equals(e.Directory,StringComparison.OrdinalIgnoreCase)&&Within(e.Directory,r.Target))?e with {ProtectedReason="被其他專案引用"}:e).OrderBy(e=>e.Name,StringComparer.CurrentCulture).ToArray();
    }

    public static void ValidateSelection(CleanupProject chosen,string root,string? active,IEnumerable<string> activeFiles)
        =>ValidateSelections([chosen],[new CleanupRoot(root,chosen.SourceLabel,chosen.IsImported)],active,activeFiles);

    public static void ValidateSelection(CleanupProject chosen,string? active,IEnumerable<string> activeFiles)
        =>ValidateSelections([chosen],[new CleanupRoot(chosen.RootDirectory,chosen.SourceLabel,chosen.IsImported)],active,activeFiles);

    public static void ValidateSelections(IEnumerable<CleanupProject> chosen,IEnumerable<CleanupRoot> cleanupRoots,
        string? active,IEnumerable<string> activeFiles)
    {
        var selected=chosen.ToArray();
        var roots=NormalizeRoots(cleanupRoots);
        if(selected.Select(item=>Path.GetFullPath(item.Directory)).Distinct(StringComparer.OrdinalIgnoreCase).Count()!=selected.Length)
            throw new InvalidDataException("清理清單含有重複項目，請重新掃描。");
        foreach(var item in selected)
        {
            if(string.IsNullOrWhiteSpace(item.RootDirectory))throw new InvalidDataException("清理項目的根目錄無效，請重新掃描。");
            var itemRoot=Path.GetFullPath(item.RootDirectory).TrimEnd(Path.DirectorySeparatorChar);
            var parent=Path.GetFullPath(Path.GetDirectoryName(item.Directory)!).TrimEnd(Path.DirectorySeparatorChar);
            var root=roots.SingleOrDefault(candidate=>candidate.Directory.Equals(itemRoot,StringComparison.OrdinalIgnoreCase)&&
                candidate.Label.Equals(item.SourceLabel,StringComparison.Ordinal)&&candidate.IsImported==item.IsImported);
            if(root==null||!parent.Equals(root.Directory,StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("清理項目的根目錄無效，請重新掃描。");
        }
        var current=Scan(roots,active,activeFiles).ToDictionary(item=>item.Directory,StringComparer.OrdinalIgnoreCase);
        foreach(var item in selected)
            if(!current.TryGetValue(item.Directory,out var latest)||latest.ProtectedReason!=null||latest.Fingerprint!=item.Fingerprint)
                throw new InvalidDataException("清理清單已變更或檔案仍在使用，請重新掃描。");
    }

    private static string AppendWarning(string? current,string next)=>string.IsNullOrEmpty(current)?next:current+"；"+next;
    private static CleanupRoot[] NormalizeRoots(IEnumerable<CleanupRoot> cleanupRoots)
    {
        var roots=cleanupRoots.Select(item=>item with
        {
            Directory=Path.GetFullPath(item.Directory).TrimEnd(Path.DirectorySeparatorChar)
        }).DistinctBy(item=>item.Directory,StringComparer.OrdinalIgnoreCase).ToArray();
        for(var left=0;left<roots.Length;left++)
        for(var right=left+1;right<roots.Length;right++)
            if(Within(roots[left].Directory,roots[right].Directory)||Within(roots[right].Directory,roots[left].Directory))
                throw new InvalidDataException("清理根目錄不得互相重疊。");
        return roots;
    }
    private static string ResolvePortable(string path)=>PortablePaths.Current?.Resolve(path)??path;
    private static bool Within(string root,string path)=>Path.GetFullPath(path).Equals(root,StringComparison.OrdinalIgnoreCase)||Path.GetFullPath(path).StartsWith(root+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase);
    private static IEnumerable<string> SafeFiles(string root)
    {
        foreach(var item in Directory.EnumerateFileSystemEntries(root))
        {
            AssertNoLinks(item);
            if(Directory.Exists(item))foreach(var file in SafeFiles(item))yield return file;
            else yield return item;
        }
    }
    private static void AssertNoLinks(string path)
    {
        for(var p=Path.GetFullPath(path);!string.IsNullOrEmpty(p);p=Path.GetDirectoryName(p))
            if((File.GetAttributes(p)&FileAttributes.ReparsePoint)!=0)throw new InvalidDataException("清理拒絕連結路徑："+p);
    }
}
