using System;
using System.IO;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;

class DumpMethods {
  static void Main(string[] args) {
    string path = args[0];
    string outPath = args[1];
    var resolver = new DefaultAssemblyResolver();
    resolver.AddSearchDirectory(Path.GetDirectoryName(path));
    var asm = AssemblyDefinition.ReadAssembly(path, new ReaderParameters { AssemblyResolver = resolver });
    string[] targets = {
      "GameManager::gmUpdate","GameManager::UpdateTick","GameManager::Update","GameManager::FixedUpdate",
      "World::OnUpdateTick","World::TickEntities","World::TickEntity","World::EntityActivityUpdate","World::TickSleeperVolumes",
      "EntityAlive::OnUpdateLive","EntityAlive::Update","EntityAlive::OnUpdateEntity",
      "EAIManager::Update","ConnectionManager::Update","DynamicMeshManager::Update",
      "SpawnManagerBiomes::SpawnUpdate","SleeperVolume::Tick","DecoManager::UpdateTick",
      "ChunkManager::OriginUpdated","AIDirector::Tick"
    };
    using (var w = new StreamWriter(outPath)) {
      foreach (var t in asm.MainModule.Types) {
        foreach (var m in t.Methods) {
          if (!m.HasBody) continue;
          string key = t.Name + "::" + m.Name;
          if (!targets.Any(x => key == x || key.EndsWith(x.Split(new[]{'/'}).Last()))) continue;
          // match full type name for nested careful
          bool ok = targets.Any(tg => {
            var parts = tg.Split(new[]{':'}, StringSplitOptions.RemoveEmptyEntries);
            return t.Name == parts[0] && m.Name == parts[1];
          });
          if (!ok) continue;
          w.WriteLine($"\n======== {t.FullName}::{m.Name}({string.Join(",", m.Parameters.Select(p=>p.ParameterType.Name))}) il={m.Body.Instructions.Count} ========");
          // call targets summary
          var calls = m.Body.Instructions
            .Where(i => i.OpCode.Code == Code.Call || i.OpCode.Code == Code.Callvirt)
            .Select(i => i.Operand?.ToString())
            .Where(s => s != null)
            .GroupBy(s => s)
            .OrderByDescending(g => g.Count())
            .Take(40);
          w.WriteLine("-- CALLs --");
          foreach (var g in calls) w.WriteLine($"  {g.Count(),3}x {g.Key}");
          w.WriteLine("-- IL (first 120) --");
          foreach (var ins in m.Body.Instructions.Take(120))
            w.WriteLine($"  IL_{ins.Offset:X4}: {ins.OpCode} {ins.Operand}");
          if (m.Body.Instructions.Count > 120)
            w.WriteLine($"  ... +{m.Body.Instructions.Count-120}");
        }
      }
      // Also dump GamePrefs enum values if present related to dedicated/server
      foreach (var t in asm.MainModule.Types) {
        if (t.Name == "EnumGamePrefs" || t.Name == "GameManager" || t.Name == "GameInfoBool" || t.Name == "GameInfoInt") {
          w.WriteLine($"\n======== ENUM/TYPE {t.FullName} ========");
          foreach (var f in t.Fields.Take(200))
            w.WriteLine($"  {f.Name} = {f.Constant}");
        }
      }
      // find IsDedicatedServer / dedicated flags
      w.WriteLine("\n======== DEDICATED-RELATED MEMBERS ========");
      foreach (var t in asm.MainModule.Types) {
        foreach (var f in t.Fields)
          if (f.Name.IndexOf("Dedicated", StringComparison.OrdinalIgnoreCase)>=0 || f.Name.IndexOf("Server", StringComparison.OrdinalIgnoreCase)>=0 && f.Name.IndexOf("Max", StringComparison.OrdinalIgnoreCase)>=0)
            w.WriteLine($"FIELD {t.FullName}::{f.Name} : {f.FieldType.Name}");
        foreach (var m in t.Methods)
          if (m.Name.IndexOf("Dedicated", StringComparison.OrdinalIgnoreCase)>=0 || m.Name == "IsServer" || m.Name == "IsClient")
            w.WriteLine($"METHOD {t.FullName}::{m.Name}");
      }
    }
    Console.WriteLine("done");
  }
}
