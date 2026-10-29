using Boolka;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

try
{
var root = Path.Combine(AppContext.BaseDirectory, "fixtures", Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
var configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;
string Exe(int version) => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, $"../../../FixtureV{version}/bin/{configuration}/net8.0-windows/boolka.exe"));
void Check(bool result, string message) { if (!result) throw new Exception(message); Console.WriteLine("PASS " + message); }
void Reject(Action action, string message) { try { action(); } catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or InvalidOperationException or KeyNotFoundException) { Console.WriteLine("PASS " + message); return; } throw new Exception("Accepted: " + message); }
async Task RejectAsync(Func<Task> action, string message) { try { await action(); } catch (Exception e) when (e is IOException or InvalidDataException or HttpRequestException or OperationCanceledException) { Console.WriteLine("PASS " + message); return; } throw new Exception("Accepted: " + message); }
byte[] Release(string version = "2.0.0", string? url = null, string? digest = null, bool draft = false, bool prerelease = false, bool duplicate = false, string? name = null) {
 var asset = new { name = name ?? $"boolka-{version}-windows-x64-standalone.zip", size = 4, digest = digest ?? "sha256:" + Convert.ToHexString(SHA256.HashData(new byte[]{1,2,3,4})), browser_download_url = url ?? $"https://github.com/Xlebka3003/boolka/releases/download/v{version}/boolka-{version}-windows-x64-standalone.zip" };
 return JsonSerializer.SerializeToUtf8Bytes(new { tag_name = "v" + version, draft, prerelease, assets = duplicate ? new[]{asset,asset} : new[]{asset} });
}
var current = new Version(1,0,0);
Check(ApplicationReleaseClient.Parse(Release(), current)?.Version == new Version(2,0,0), "new stable release accepted");
Check(ApplicationReleaseClient.Parse(Release("0.9.0"), current) is null && ApplicationReleaseClient.Parse(Release("1.0.0"), current) is null, "older and equal versions do not light button");
Check(ApplicationReleaseClient.Parse(Release(draft:true), current) is null && ApplicationReleaseClient.Parse(Release(prerelease:true), current) is null, "draft and prerelease ignored");
Reject(()=>ApplicationReleaseClient.Parse(Release(url:"https://evil.test/payload.exe"), current), "foreign download URL rejected");
Reject(()=>ApplicationReleaseClient.Parse(Release(digest:"sha256:invalid"), current), "invalid digest rejected");
Reject(()=>ApplicationReleaseClient.Parse(Release(duplicate:true), current), "ambiguous duplicate assets rejected");
Reject(()=>ApplicationReleaseClient.Parse(Release(name:"boolka-2.0.0-windows-arm64.zip"), current), "different architecture rejected");
Reject(()=>ApplicationReleaseClient.Parse(Release("2.0.0-beta"), current), "unapproved version syntax rejected");
Reject(()=>ApplicationReleaseClient.Parse(new byte[1024*1024+1], current), "oversized metadata rejected");
using (var missing = new ApplicationReleaseClient(new HttpClient(new Handler(_=>new HttpResponseMessage(HttpStatusCode.NotFound)))))
 Check(await missing.CheckAsync(current, default) is null, "private or missing release returns quiet no-update");
var requested = 0;
using (var redirect = new ApplicationReleaseClient(new HttpClient(new Handler(_=>{ requested++; var r=new HttpResponseMessage(HttpStatusCode.Redirect);r.Headers.Location=new Uri("http://evil.test/payload");return r; }))))
 await RejectAsync(()=>redirect.CheckAsync(current, default), "unsafe redirect blocked before second request");
Check(requested==1,"untrusted redirect destination never requested");
var parsed=ApplicationReleaseClient.Parse(Release(),current)!;
using (var download=new ApplicationReleaseClient(new HttpClient(new Handler(_=>new HttpResponseMessage(HttpStatusCode.OK){Content=new ByteArrayContent(new byte[]{1,2,3,4})})))) {
 var path=Path.Combine(root,"ok.zip");await download.DownloadAsync(parsed,path,null,default);Check(File.ReadAllBytes(path).Length==4,"download length and SHA256 verified");
}
using (var corrupted=new ApplicationReleaseClient(new HttpClient(new Handler(_=>new HttpResponseMessage(HttpStatusCode.OK){Content=new ByteArrayContent(new byte[]{4,3,2,1})}))))
 await RejectAsync(()=>corrupted.DownloadAsync(parsed,Path.Combine(root,"bad.zip"),null,default),"corrupted download rejected");
using (var incomplete=new ApplicationReleaseClient(new HttpClient(new Handler(_=>new HttpResponseMessage(HttpStatusCode.OK){Content=new ByteArrayContent(new byte[]{1,2,3})}))))
 await RejectAsync(()=>incomplete.DownloadAsync(parsed,Path.Combine(root,"short.zip"),null,default),"truncated download rejected");
using (var canceled=new ApplicationReleaseClient(new HttpClient(new Handler(_=>new HttpResponseMessage(HttpStatusCode.OK){Content=new ByteArrayContent(new byte[]{1,2,3,4})}))))
 await RejectAsync(()=>canceled.DownloadAsync(parsed,Path.Combine(root,"cancel.zip"),null,new CancellationToken(true)),"canceled download cannot prepare update");

string Target(string name, int version=1) {
 var target=Path.Combine(root,name);Directory.CreateDirectory(target);
 foreach(var file in ApplicationUpdatePackage.FileNames){var path=Path.Combine(target,file);Directory.CreateDirectory(Path.GetDirectoryName(path)!);if(file=="boolka.exe")File.Copy(Exe(version),path);else File.WriteAllText(path,"old "+file);}
 File.WriteAllText(Path.Combine(target,"unins000.exe"),"uninstaller");Directory.CreateDirectory(Path.Combine(target,"SavedVariables"));File.WriteAllText(Path.Combine(target,"SavedVariables/test.lua"),"keep");return target;
}
string Archive(string name, int version=2, string? extra=null, bool missing=false, bool link=false) {
 var path=Path.Combine(root,name+".zip");using var z=ZipFile.Open(path,ZipArchiveMode.Create);
 foreach(var file in ApplicationUpdatePackage.FileNames.Where(f=>!missing||f!="LICENSE")) {
  var entry=z.CreateEntry(file);using var stream=entry.Open();if(file=="boolka.exe"){using var input=File.OpenRead(Exe(version));input.CopyTo(stream);}else{var bytes=Encoding.UTF8.GetBytes("new "+file);stream.Write(bytes);}
 }
 if(extra is not null){var e=z.CreateEntry(extra);if(link)e.ExternalAttributes=unchecked((int)0xA1FF0000);using var w=new StreamWriter(e.Open());w.Write("bad");}
 return path;
}
var zip=Archive("valid");
ApplicationUpdatePlan Prepare(string archive,string target,string workspace,Version? version=null)=>ApplicationUpdatePackage.Prepare(archive,workspace,target,version??new Version(2,0,0),Environment.ProcessId,1,ApplicationUpdatePackage.Hash(archive));
var successTarget=Target("success");var successWorkspace=ApplicationUpdatePackage.CreateWorkspace();var successPlan=Prepare(zip,successTarget,successWorkspace);
Check(File.ReadAllText(Path.Combine(successTarget,"LICENSE")).StartsWith("old"),"preparation never changes installed files");
var loadedPlan=ApplicationUpdatePackage.ReadPlan(Path.Combine(successWorkspace,"plan.json"));Check(loadedPlan.Version=="2.0.0","saved plan can be validated");
var backup=ApplicationUpdateTransaction.Apply(successPlan,successWorkspace);
Check(ApplicationUpdatePackage.Hash(Path.Combine(successTarget,"boolka.exe"))==ApplicationUpdatePackage.Hash(Exe(2)),"new executable installed");
Check(File.ReadAllText(Path.Combine(successTarget,"LICENSE")).StartsWith("new")&&File.ReadAllText(Path.Combine(successTarget,"unins000.exe"))=="uninstaller"&&File.ReadAllText(Path.Combine(successTarget,"SavedVariables/test.lua"))=="keep","license update preserves uninstaller and unrelated user files");
ApplicationUpdateTransaction.RestoreAfterLaunchFailure(backup,successTarget);
Check(ApplicationUpdatePackage.Hash(Path.Combine(successTarget,"boolka.exe"))==ApplicationUpdatePackage.Hash(Exe(1))&&File.ReadAllText(Path.Combine(successTarget,"LICENSE")).StartsWith("old"),"launch failure can restore all originals");
var rollbackTarget=Target("rollback");var rollbackWorkspace=ApplicationUpdatePackage.CreateWorkspace();var rollbackPlan=Prepare(zip,rollbackTarget,rollbackWorkspace);
Reject(()=>ApplicationUpdateTransaction.Apply(rollbackPlan,rollbackWorkspace,index=>{if(index==2)throw new IOException("fixture failure");}),"partial replacement rolls back");
Check(ApplicationUpdatePackage.FileNames.Where(n=>n!="boolka.exe").All(n=>File.ReadAllText(Path.Combine(rollbackTarget,n)).StartsWith("old")),"every original license restored after partial failure");
var recoveryTarget=Target("recovery");var recoveryWorkspace=ApplicationUpdatePackage.CreateWorkspace();var recoveryPlan=Prepare(zip,recoveryTarget,recoveryWorkspace);var recoveryBackup=ApplicationUpdateTransaction.Apply(recoveryPlan,recoveryWorkspace);
var journal=Path.Combine(recoveryBackup,"journal.json");File.WriteAllText(journal,File.ReadAllText(journal).Replace("completed","prepared"));
File.Copy(Path.Combine(recoveryBackup,"backup","LICENSE"),Path.Combine(recoveryTarget,"LICENSE"),true);
Directory.CreateDirectory(Path.Combine(recoveryBackup,"restore"));
File.WriteAllText(Path.Combine(recoveryBackup,"restore/LICENSE"),"interrupted restore");
ApplicationUpdateTransaction.Recover(recoveryTarget);
Check(ApplicationUpdatePackage.Hash(Path.Combine(recoveryTarget,"boolka.exe"))==ApplicationUpdatePackage.Hash(Exe(1))&&File.ReadAllText(Path.Combine(recoveryTarget,"THIRD_PARTY_NOTICES.txt")).StartsWith("old"),"interrupted mixed replacement recovers before startup");
Check(!ApplicationUpdateTransaction.NeedsRecovery(recoveryTarget), "recovery journal marked finished");
var concurrentTarget=Target("concurrent");var concurrentWorkspace=ApplicationUpdatePackage.CreateWorkspace();var concurrentPlan=Prepare(zip,concurrentTarget,concurrentWorkspace);
using(var held = new FileStream(Path.Combine(concurrentTarget,".boolka-update.lock"),FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None)) {
 Reject(()=>ApplicationUpdateTransaction.Apply(concurrentPlan,concurrentWorkspace),"concurrent update blocked by target-wide lock");
 Reject(()=>ApplicationUpdateTransaction.Recover(concurrentTarget),"recovery cannot race an active update");
}
var malformedTarget=Target("malformed");var malformedJournal=Path.Combine(malformedTarget,".boolka-update-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(malformedJournal);File.WriteAllText(Path.Combine(malformedJournal,"journal.json"),"{\"State\":\"prepared\",\"Files\":null,\"Originals\":null}");
Reject(()=>ApplicationUpdateTransaction.NeedsRecovery(malformedTarget),"null journal fields rejected with controlled failure");
var changedTarget=Target("changed");var changedWorkspace=ApplicationUpdatePackage.CreateWorkspace();var changedPlan=Prepare(zip,changedTarget,changedWorkspace);File.WriteAllText(Path.Combine(changedWorkspace,"payload/LICENSE"),"tampered");
Reject(()=>ApplicationUpdateTransaction.Apply(changedPlan,changedWorkspace),"payload tampering rejected before installation");
Check(ApplicationUpdatePackage.Hash(Path.Combine(changedTarget,"boolka.exe"))==ApplicationUpdatePackage.Hash(Exe(1)),"tampering leaves original executable intact");
var lockedTarget=Target("locked");var lockedWorkspace=ApplicationUpdatePackage.CreateWorkspace();var lockedPlan=Prepare(zip,lockedTarget,lockedWorkspace);
using(var locked=File.Open(Path.Combine(lockedTarget,"boolka.exe"),FileMode.Open,FileAccess.Read,FileShare.Read))Reject(()=>ApplicationUpdateTransaction.Apply(lockedPlan,lockedWorkspace),"running second instance or locked EXE blocks replacement");
var oldZip=Archive("old",1);var newerTarget=Target("newer",2);
Reject(()=>{var w=ApplicationUpdatePackage.CreateWorkspace();var p=Prepare(oldZip,newerTarget,w,new Version(1,0,0));ApplicationUpdateTransaction.Apply(p,w);},"downgrade rejected");
foreach(var pair in new[]{("traversal","../outside.txt"),("absolute","C:/outside.txt"),("stream","LICENSE:stream"),("duplicate","boolka.exe"),("case","Boolka.exe"),("user","SavedVariables/test.lua")}) {
 var badZip=Archive(pair.Item1,extra:pair.Item2);Reject(()=>Prepare(badZip,Target(pair.Item1),ApplicationUpdatePackage.CreateWorkspace()),"archive rejects "+pair.Item1);
}
Reject(()=>Prepare(Archive("missing",missing:true),Target("missing"),ApplicationUpdatePackage.CreateWorkspace()),"missing package file rejected");
Reject(()=>Prepare(Archive("link",extra:"link",link:true),Target("link"),ApplicationUpdatePackage.CreateWorkspace()),"ZIP links rejected");
Reject(()=>Prepare(zip,Target("wrongversion"),ApplicationUpdatePackage.CreateWorkspace(),new Version(3,0,0)),"EXE version must match release");
Reject(()=>Prepare(zip,Target("badworkspace"),root),"workspace outside isolated cache rejected");
var linkTarget=Target("linked");var external=Path.Combine(root,"external");Directory.CreateDirectory(external);Directory.Delete(Path.Combine(linkTarget,"Assets/Flags"),true);
var junctionPath=Path.GetFullPath(Path.Combine(linkTarget,"Assets/Flags"));
if(root.IndexOfAny(['&','|','<','>','^','%','!','"','\r','\n'])>=0)throw new Exception("Unsafe fixture path for junction command");
var junctionStart=new System.Diagnostics.ProcessStartInfo("cmd.exe"){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true};
foreach(var argument in new[]{"/d","/c","mklink","/J",junctionPath,external})junctionStart.ArgumentList.Add(argument);
using(var junction=System.Diagnostics.Process.Start(junctionStart)!) {
 var output=junction.StandardOutput.ReadToEnd();var error=junction.StandardError.ReadToEnd();junction.WaitForExit();
 if(junction.ExitCode!=0)throw new Exception("Fixture junction failed: "+output+error);
}
Reject(()=>Prepare(zip,linkTarget,ApplicationUpdatePackage.CreateWorkspace()),"linked target ancestor rejected before writes");
Check(!File.Exists(Path.Combine(external,"LICENSE.txt")),"junction destination untouched");
Console.WriteLine("All application update checks passed; no fixture EXE or real app was launched.");

}
catch (Exception error) { Console.Error.WriteLine("FAIL " + error); Environment.ExitCode = 1; }

sealed class Handler(Func<HttpRequestMessage,HttpResponseMessage> response) : HttpMessageHandler {
 protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken cancellationToken){cancellationToken.ThrowIfCancellationRequested();return Task.FromResult(response(request));}
}
