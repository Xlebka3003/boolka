using Boolka;
using System.IO.Compression;
using System.Security.Cryptography;
var root=Path.Combine(AppContext.BaseDirectory,"fixtures",Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
void Check(bool value,string text){if(!value)throw new Exception(text);Console.WriteLine("PASS "+text);}
Addon Package(long id,string name,bool library=false)=>new(name,"1","Test","","",""){CatalogId=id,BuildVersion="1",PackageModules=[new(name,"1",[],library)],CatalogFolders=[new(name,"1")]};
string Zip(string name,string dependencies){var path=Path.Combine(root,name+".zip");using var zip=ZipFile.Open(path,ZipArchiveMode.Create);using var w=new StreamWriter(zip.CreateEntry(name+"/"+name+".addon").Open());w.Write("## Title: "+name+"\n## AddOnVersion: 1\n## PCDependsOn: "+dependencies);return path;}
var a=Package(1,"Root");var b=Package(2,"LibB",true);var c=Package(3,"LibC",true);
var paths=new Dictionary<long,string>{[1]=Zip("Root","LibB>=1"),[2]=Zip("LibB","LibC>=1"),[3]=Zip("LibC","")};
var catalog=new[]{a,b,c}.Select(x=>x with{CatalogChecksum=Convert.ToHexString(MD5.HashData(File.ReadAllBytes(paths[x.CatalogId!.Value])))}).ToArray();
var pack=new AddonPack(1,"ESO","Test","","",[new(1,"Root")]);var downloads=0;Task<string> Download(Addon x){downloads++;return Task.FromResult(paths[x.CatalogId!.Value]);}
var archives=new Dictionary<long,string>();var preparation=await PackArchivePlanner.PrepareAsync(pack,catalog,[],new HashSet<long>(),archives,Download,default);
Check(preparation.Plan.Plan is {IsReady:true}&&preparation.Plan.Plan.Packages.Count==3,"review contains root plus ZIP-only transitive PC dependencies");
Check(!Directory.Exists(Path.Combine(root,"AddOns")),"preview does not modify AddOns");
var second=await PackArchivePlanner.PrepareAsync(pack,catalog,[],new HashSet<long>(),archives,Download,default);
Check(second.Plan.Fingerprint==preparation.Plan.Fingerprint&&downloads==3,"fresh revalidation reuses checksum-matching ZIPs");
var target=Path.Combine(root,"AddOns");Directory.CreateDirectory(target);Directory.CreateDirectory(Path.Combine(root,"SavedVariables"));File.WriteAllText(Path.Combine(root,"SavedVariables/test.lua"),"keep");
await new SafeInstaller().InstallAsync(second.Plan.Plan!,archives,target,default,allowReplacement:false);
Check(Directory.Exists(Path.Combine(target,"Root"))&&Directory.Exists(Path.Combine(target,"LibB"))&&Directory.Exists(Path.Combine(target,"LibC")),"reviewed import installs every required library");
Check(File.ReadAllText(Path.Combine(root,"SavedVariables/test.lua"))=="keep","SavedVariables preserved");
try{await new SafeInstaller().InstallAsync(second.Plan.Plan!,archives,target,default,allowReplacement:false);throw new Exception("replacement accepted");}catch(InvalidDataException){Console.WriteLine("PASS import still refuses replacement");}
var ignored=await PackArchivePlanner.PrepareAsync(pack,catalog,[],new HashSet<long>{2},new Dictionary<long,string>(),Download,default);
Check(ignored.Plan.Plan is null,"ignored library blocks dependent root before approval");
var canceled=new CancellationToken(true);try{await PackArchivePlanner.PrepareAsync(pack,catalog,[],new HashSet<long>(),archives,Download,canceled);throw new Exception("cancel ignored");}catch(OperationCanceledException){Console.WriteLine("PASS canceled preview");}
var independent=Package(4,"Independent");var independentZip=Zip("Independent","");independent=independent with{CatalogChecksum=Convert.ToHexString(MD5.HashData(File.ReadAllBytes(independentZip)))};
var partialPack=pack with{Addons=[new(1,"Root"),new(4,"Independent")]};
Task<string> PartialDownload(Addon x)=>x.CatalogId==2?Task.FromException<string>(new IOException("fixture library failure")):Task.FromResult(x.CatalogId==4?independentZip:paths[x.CatalogId!.Value]);
var partial=await PackArchivePlanner.PrepareAsync(partialPack,catalog.Append(independent).ToArray(),[],new HashSet<long>(),new Dictionary<long,string>(),PartialDownload,default);
Check(partial.Plan.Plan is {IsReady:true}&&partial.Plan.Plan.Packages.Count==1&&partial.Plan.Plan.Packages[0].CatalogId==4,"failed library skips dependent root but preserves independent import");
var originalFingerprint=PackPlanner.Build(pack,catalog,[],new HashSet<long>()).Fingerprint;
var changedFingerprint=PackPlanner.Build(pack,catalog.Select(x=>x.CatalogId==1?x with{CatalogChecksum=new string('0',32)}:x).ToArray(),[],new HashSet<long>()).Fingerprint;
Check(originalFingerprint!=changedFingerprint,"approval fingerprint detects checksum change even with identical release version");
var tampered=Path.Combine(root,"tampered.zip");File.WriteAllText(tampered,"not the reviewed ZIP");archives[1]=tampered;var beforeDownloads=downloads;
var checkedAgain=await PackArchivePlanner.PrepareAsync(pack,catalog,[],new HashSet<long>(),archives,Download,default);
Check(downloads==beforeDownloads+1&&checkedAgain.Plan.Fingerprint==preparation.Plan.Fingerprint,"tampered cached ZIP is downloaded and checked again");
// Устаревшие требования каталога не должны даже запускать скачивание лишней библиотеки.
var staleRoot = catalog[0] with { PackageModules = [new("Root", "1", ["RemovedLibrary", "LibB>=1"])] };
var staleLibrary = catalog[1] with { PackageModules = [new("LibB", "1", ["RemovedTransitiveLibrary"], true)] };
var staleCatalog = new[] { staleRoot, staleLibrary, catalog[2] };
var fetched = new List<long>();
Task<string> FetchActual(Addon item) { fetched.Add(item.CatalogId!.Value); return Task.FromResult(paths[item.CatalogId.Value]); }
var currentPlan = await ArchiveDependencyPlanner.ExpandAsync(
    ArchiveDependencyPlanner.Build(staleRoot, staleCatalog, []), staleCatalog, [], new Dictionary<long, string>(), FetchActual, _ => true, default);
Check(currentPlan.IsReady && fetched.SequenceEqual(new long[] { 1, 2, 3 }), "root ZIP inspected first; obsolete catalog requirements never downloaded or blocking");
Check(currentPlan.Packages.All(item => item.PackageModules.All(module => !module.RequiredDependencies.Any(x => x.StartsWith("Removed")))), "actual manifests replace root and transitive requirements");
var stalePack = await PackArchivePlanner.PrepareAsync(pack, staleCatalog, [], new HashSet<long>(), new Dictionary<long,string>(), FetchActual, default);
Check(stalePack.Plan.Plan is { IsReady: true, Packages.Count: 3 }, "import review ignores removed catalog dependencies too");
var emptyRoot = independent with { PackageModules = [new("Independent", "1", ["RemovedLibrary"])] };
var emptyCatalog = new[] { emptyRoot };
var emptyPlan = await ArchiveDependencyPlanner.ExpandAsync(ArchiveDependencyPlanner.Build(emptyRoot, emptyCatalog, []), emptyCatalog, [],
    new Dictionary<long,string>(), _ => Task.FromResult(independentZip), _ => true, default);
Check(emptyPlan.IsReady && emptyPlan.Packages.Count == 1 && emptyPlan.Root.PackageModules[0].RequiredDependencies.Count == 0,
    "empty DependsOn removes all obsolete requirements");
var repairRoot = staleRoot with { PackageModules = [new("Root", "1", ["LibB>=1"])] };
Check(ArchiveDependencyPlanner.Build(repairRoot, staleCatalog, [], includeRoot:false).Packages.Single().CatalogId == 2,
    "library-only repair retains actual installed root requirements");
var unknownRoot = ArchiveDependencyPlanner.Inspect(staleRoot, paths[1], default);
Check(!ArchiveDependencyPlanner.Build(unknownRoot, [unknownRoot], []).IsReady, "missing actual library still blocks installation");
var cachePath = Path.Combine(root, "dependencies.json");
File.WriteAllText(cachePath, System.Text.Json.JsonSerializer.Serialize(new[] { new { Id=1, Version="1", Checksum=catalog[0].CatalogChecksum, Modules=staleRoot.PackageModules } }));
var dependencyCache = new ArchiveDependencyCache(cachePath);
Check(dependencyCache.Find(catalog[0]) is null, "old cache with catalog additions is invalidated");
dependencyCache.Remember(unknownRoot, paths[1]);
Check(dependencyCache.Find(catalog[0])![0].RequiredDependencies.SequenceEqual(new[] { "LibB>=1" }), "cache stores only checked manifest requirements");
var zipDestination = Path.Combine(root, "SavedZips"); Directory.CreateDirectory(zipDestination);
using var zipHttp = new System.Net.Http.HttpClient(new FixtureHandler(paths));
var saved = await new DependencyBatchDownloader(new ArchiveDownloader(zipHttp)).DownloadAsync(
    ZipDownloadPlan.Build(staleRoot, staleCatalog, [])!, zipDestination, Path.Combine(zipDestination, "root.zip"), null, default, staleCatalog, []);
Check(saved.Files.Count == 3 && saved.Files.All(File.Exists), "ZIP export discovers actual libraries and publishes every archive");
Check(!Directory.EnumerateDirectories(zipDestination, ".boolka-dependencies-*").Any(), "ZIP export removes its temporary staging folder");
if(args.Contains("--live")){
 var provider=new EsoProvider();var remote=await provider.GetCatalogAsync(default);
 var names=new[]{"Dolgubon's Lazy Writ", "LibMediaProvider", "pChat (", "Votan's Fisherman"};
 var chosen=names.Select(name=>remote.First(x=>x.Name.StartsWith(name,StringComparison.OrdinalIgnoreCase))).ToArray();
 var livePack=new AddonPack(1,"ESO","Regression","","",chosen.Select(x=>new PackAddon(x.CatalogId!.Value,x.Name)).ToArray());
 var cache=new Dictionary<long,string>();var downloader=new ArchiveDownloader();
 async Task<string> Fetch(Addon x){var path=Path.Combine(root,"live-"+x.CatalogId+".zip");await downloader.DownloadAsync(x.CatalogId!.Value,path,null,default);Console.WriteLine("Downloaded "+x.Name);return path;}
 var live=await PackArchivePlanner.PrepareAsync(livePack,remote,[],new HashSet<long>(),cache,Fetch,default);
 Check(live.Plan.Plan is {IsReady:true}&&chosen.All(x=>live.Plan.Plan.Packages.Any(p=>p.CatalogId==x.CatalogId)),"four reported projects have reviewed plans from live archives");
 var liveTarget=Path.Combine(root,"Live/AddOns");Directory.CreateDirectory(liveTarget);
 await new SafeInstaller().InstallAsync(live.Plan.Plan!,cache,liveTarget,default,allowReplacement:false);
 Check((await provider.ScanAsync(liveTarget)).Addons.Count>=4,"actual four archives and dependencies install in isolated folder");
}

// Похожие проекты с одной папкой различаем только по подтверждаемому требованию сборки.
var sharedRoot=Package(99001,"SharedRoot") with { PackageModules=[new("SharedRoot","1",["SharedLib>=24"])] };
var matchingLibrary=Package(99002,"SharedLib",true) with { PackageModules=[new("SharedLib","24",[],true)] };
var obsoleteLibrary=Package(99003,"SharedLib",true) with { PackageModules=[new("SharedLib","2",[],true)] };
var sharedPlan=DependencyResolver.Build(sharedRoot,[sharedRoot,matchingLibrary,obsoleteLibrary],[]);
Check(sharedPlan.IsReady && sharedPlan.Packages.Any(p=>p.CatalogId==99002) && sharedPlan.Packages.All(p=>p.CatalogId!=99003),"minimum build removes obsolete same-name library candidate");
Check(!DependencyResolver.Build(sharedRoot,[sharedRoot,obsoleteLibrary],[]).IsReady,"no candidate meeting minimum remains blocked");
Check(!DependencyResolver.Build(sharedRoot,[sharedRoot,matchingLibrary,obsoleteLibrary with {PackageModules=[new("SharedLib","25",[],true)]}],[]).IsReady,"multiple suitable candidates remain ambiguous");
Check(!DependencyResolver.Build(sharedRoot with {PackageModules=[new("SharedRoot","1",["SharedLib"])]},[sharedRoot,matchingLibrary,obsoleteLibrary],[]).IsReady,"no-minimum requirement does not guess a project");

sealed class FixtureHandler(IReadOnlyDictionary<long, string> paths) : System.Net.Http.HttpMessageHandler
{
    protected override Task<System.Net.Http.HttpResponseMessage> SendAsync(System.Net.Http.HttpRequestMessage request, CancellationToken token)
    {
        var uri = request.RequestUri!;
        var id = uri.Host == "api.mmoui.com" ? long.Parse(Path.GetFileNameWithoutExtension(uri.AbsolutePath)) : long.Parse(uri.Query.Split('=')[1]);
        System.Net.Http.HttpContent content = uri.Host == "api.mmoui.com"
            ? new System.Net.Http.StringContent(System.Text.Json.JsonSerializer.Serialize(new { id, title="Fixture", version="1", downloadUri="https://cdn.esoui.com/downloads/getfile.php?id="+id }))
            : new System.Net.Http.ByteArrayContent(File.ReadAllBytes(paths[id]));
        return Task.FromResult(new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = content });
    }
}
