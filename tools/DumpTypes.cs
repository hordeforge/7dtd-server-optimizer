using System;
using System.IO;
using System.Linq;
using Mono.Cecil;

class DumpTypes {
  static void Main(string[] args) {
    string path = args[0];
    string outDir = args.Length > 1 ? args[1] : "out";
    Directory.CreateDirectory(outDir);
    var resolver = new DefaultAssemblyResolver();
    resolver.AddSearchDirectory(Path.GetDirectoryName(path));
    var rp = new ReaderParameters { AssemblyResolver = resolver, ReadSymbols = false };
    var asm = AssemblyDefinition.ReadAssembly(path, rp);
    string[] hot = {
      "GameManager","World","EntityAlive","EntityEnemy","EntityPlayer",
      "EAIManager","EAIBase","AIDirector","ConnectionManager","ThreadManager",
      "Chunk","ChunkCluster","ChunkManager","DynamicMeshManager","DynamicMeshServer",
      "SleeperVolume","GameTimer","NetPackage","AstarPath","PathFinder",
      "DecoManager","EnvironmentAudioManager","LightManager","SpawnManagerBiomes",
      "GamePrefs","DedicatedServerWatchdog","SdtdConsole","WorldStaticData",
      "EntityFactory","ChunkProviderGenerateWorld","ChunkProviderGenerateWorldFromRaw",
      "NetPackageEntityPosAndRot","NetPackageChunk","NetPackageWorldTime"
    };
    using (var index = new StreamWriter(Path.Combine(outDir, "type_index.txt"))) {
      foreach (var t in asm.MainModule.Types.OrderBy(t => t.FullName)) {
        index.WriteLine($"{t.FullName}\t{t.Methods.Count}\t{t.Fields.Count}");
      }
    }
    Console.WriteLine($"types={asm.MainModule.Types.Count}");
    foreach (var t in asm.MainModule.Types) {
      bool match = hot.Any(h => t.Name == h || t.Name.StartsWith(h));
      if (!match) continue;
      var safe = t.FullName.Replace("/", "_").Replace(".", "_");
      using (var w = new StreamWriter(Path.Combine(outDir, safe + ".txt"))) {
        w.WriteLine($"// {t.FullName} base={t.BaseType}");
        foreach (var f in t.Fields)
          w.WriteLine($"FIELD {f.Attributes} {f.FieldType.Name} {f.Name}");
        foreach (var m in t.Methods) {
          var ps = string.Join(", ", m.Parameters.Select(p => p.ParameterType.Name + " " + p.Name));
          w.WriteLine($"METHOD {m.Attributes} {m.ReturnType.Name} {m.Name}({ps}) rva=0x{m.RVA:X} il={(m.HasBody ? m.Body.Instructions.Count : 0)}");
          // dump short IL for Update/tick methods
          if (m.HasBody && (m.Name.Contains("Update") || m.Name == "OnUpdateLive" || m.Name == "Tick" || m.Name == "FixedUpdate" || m.Name == "LateUpdate" || m.Name.Contains("Path") || m.Name.Contains("Think") || m.Name == "updateTasks")) {
            foreach (var ins in m.Body.Instructions.Take(80))
              w.WriteLine($"  IL_{ins.Offset:X4}: {ins.OpCode} {ins.Operand}");
            if (m.Body.Instructions.Count > 80) w.WriteLine($"  ... +{m.Body.Instructions.Count-80} more");
          }
        }
      }
      Console.WriteLine($"dumped {t.FullName}");
    }
  }
}
