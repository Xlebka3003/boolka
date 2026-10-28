using Boolka;
var fixtures=Path.Combine(AppContext.BaseDirectory,"fixtures",Guid.NewGuid().ToString("N"));Directory.CreateDirectory(fixtures);
void Check(bool condition,string name){if(!condition)throw new Exception(name);Console.WriteLine("PASS "+name);}
(string Root,string Container,string Other) Fixture(string label)
{
 var root=Path.Combine(fixtures,label,"AddOns");var container=Path.Combine(root,"HarvestMapData");
 foreach(var name in new[]{"HarvestMapAD","HarvestMapDC","HarvestMapDLC","HarvestMapEP","HarvestMapNF"}){
  var module=Path.Combine(container,"Modules",name);Directory.CreateDirectory(module);File.WriteAllText(Path.Combine(module,name+".txt"),"## Title: "+name+"\n## APIVersion: 101050\n## AddOnVersion: 1\n");File.WriteAllText(Path.Combine(module,"data.lua"),"preserved module data");
 }
 var other=Path.Combine(root,"Other");Directory.CreateDirectory(other);File.WriteAllText(Path.Combine(other,"Other.addon"),"## Title: Other\n");
 var saved=Path.Combine(root,"SavedVariables");Directory.CreateDirectory(saved);File.WriteAllText(Path.Combine(saved,"settings.lua"),"keep settings");return(root,container,other);
}
var group=Fixture("group");var scanned=await new EsoProvider().ScanAsync(group.Root);
Check(scanned.Addons.Any(x=>x.IsModuleContainer&&x.Name=="HarvestMapData"),"scanner recognizes root without own manifest");
var result=await new SafeAddonRemoval().RemoveAsync(group.Root,scanned.Addons.Select(x=>x.Path).ToArray(),default);
Check(result.InstalledFolders.Count==2&&!Directory.Exists(group.Container)&&!Directory.Exists(group.Other),"whole selected collection including container removed");
Check(Directory.GetFiles(Path.Combine(result.BackupDirectory,"HarvestMapData"),"data.lua",SearchOption.AllDirectories).Length==5,"all five nested modules retained in backup");
Check(File.ReadAllText(Path.Combine(group.Root,"SavedVariables/settings.lua"))=="keep settings","SavedVariables preserved");
var rollback=Fixture("rollback");var moved=0;
try{await new SafeAddonRemoval(stage=>{if(stage=="after-move"&&++moved==2)throw new IOException("fixture failure");}).RemoveAsync(rollback.Root,new[]{rollback.Container,rollback.Other},default);throw new Exception("failure missing");}catch(IOException){}
Check(Directory.GetFiles(rollback.Container,"data.lua",SearchOption.AllDirectories).Length==5&&Directory.Exists(rollback.Other),"failed group removal restores container and ordinary addon");
var bad=Fixture("invalid-module");File.WriteAllText(Path.Combine(bad.Container,"Modules/HarvestMapAD/HarvestMapAD.txt"),"invalid manifest");
try{await new SafeAddonRemoval().RemoveAsync(bad.Root,new[]{bad.Other,bad.Container},default);throw new Exception("invalid module accepted");}catch(InvalidDataException){}
Check(Directory.Exists(bad.Other)&&Directory.Exists(bad.Container),"invalid nested manifest blocks group before first move");
var incomplete=Fixture("incomplete");var nested=incomplete.Container;for(var i=0;i<10;i++){nested=Path.Combine(nested,"Deep");Directory.CreateDirectory(nested);}
try{await new SafeAddonRemoval().RemoveAsync(incomplete.Root,incomplete.Container,default);throw new Exception("incomplete scan accepted");}catch(InvalidDataException){}
Check(Directory.Exists(incomplete.Container),"incomplete nested discovery remains blocked");
if(args.Length>0){
 var source=Path.GetFullPath(args[0]);var real=Fixture("real-manifests");
 foreach(var file in Directory.EnumerateFiles(source,"*.txt",SearchOption.AllDirectories)){
  if(Path.GetFileNameWithoutExtension(file)!=Path.GetFileName(Path.GetDirectoryName(file)))continue;
  var dest=Path.Combine(real.Container,Path.GetRelativePath(source,file));Directory.CreateDirectory(Path.GetDirectoryName(dest)!);File.Copy(file,dest,true);
 }
 var actual=await new SafeAddonRemoval().RemoveAsync(real.Root,new[]{real.Container,real.Other},default);
 Check(actual.InstalledFolders.Count==2&&!Directory.Exists(real.Container),"actual HarvestMapData manifest layout removable with group");
}
