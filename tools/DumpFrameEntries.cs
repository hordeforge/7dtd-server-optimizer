using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Mono.Cecil;
using Mono.Cecil.Cil;

class DumpFrameEntries {
  static bool IsMB(TypeDefinition t) {
    TypeReference b = t.BaseType;
    int guard = 0;
    while (b != null && guard++ < 24) {
      if (b.Name == "MonoBehaviour" || b.FullName == "UnityEngine.MonoBehaviour") return true;
      if (b.Name.StartsWith("SingletonMonoBehaviour")) return true;
      try {
        TypeDefinition r = b.Resolve();
        if (r == null) break;
        b = r.BaseType;
      } catch {
        break;
      }
    }
    return false;
  }

  static void Main(string[] args) {
    var resolver = new DefaultAssemblyResolver();
    resolver.AddSearchDirectory(Path.GetDirectoryName(args[0]));
    var asm = AssemblyDefinition.ReadAssembly(args[0], new ReaderParameters { AssemblyResolver = resolver });
    string outDir = args[1];
    Directory.CreateDirectory(outDir);

    var sb = new StringBuilder();
    sb.AppendLine("# All MonoBehaviour-like Update/LateUpdate/FixedUpdate (V3.0.1)");
    sb.AppendLine();
    sb.AppendLine("| Type | Base | Method | IL |");
    sb.AppendLine("|---|---|---|---:|");

    int count = 0;
    foreach (var t in asm.MainModule.Types.OrderBy(x => x.Name)) {
      if (!IsMB(t)) continue;
      foreach (var m in t.Methods) {
        if (!m.HasBody) continue;
        if (m.Name != "Update" && m.Name != "LateUpdate" && m.Name != "FixedUpdate") continue;
        if (m.Parameters.Count != 0) continue;
        string bas = t.BaseType != null ? t.BaseType.Name : "?";
        sb.AppendLine("| `" + t.Name + "` | " + bas + " | `" + m.Name + "` | " + m.Body.Instructions.Count + " |");
        count++;
      }
    }
    File.WriteAllText(Path.Combine(outDir, "inventory-frame-entries.md"), sb.ToString());

    // Who calls whom for known peers
    var sb3 = new StringBuilder();
    sb3.AppendLine("# Callers of key Update methods");
    sb3.AppendLine();
    string[] watch = new string[] {
      "GameManager::Update","GameManager::gmUpdate","GameManager::UpdateTick","GameManager::LateUpdate","GameManager::FixedUpdate",
      "ConnectionManager::Update","DynamicMeshManager::Update","MeshDataManager::LateUpdate",
      "ThreadManager::UpdateMainThreadTasks","ThreadManager::LateUpdate"
    };
    var hit = new HashSet<string>();
    foreach (var t in asm.MainModule.Types) {
      foreach (var m in t.Methods) {
        if (!m.HasBody) continue;
        foreach (var i in m.Body.Instructions) {
          MethodReference mr = i.Operand as MethodReference;
          if (mr == null) continue;
          string s = mr.DeclaringType.Name + "::" + mr.Name;
          foreach (var w in watch) {
            if (s == w.Split(new char[]{' '})[0] || s == w) {
              string line = "- `" + t.Name + "::" + m.Name + "` -> `" + s + "`";
              if (hit.Add(line)) sb3.AppendLine(line);
            }
          }
        }
      }
    }
    // simpler watch
    File.WriteAllText(Path.Combine(outDir, "inventory-update-callers.md"), sb3.ToString());

    // gmUpdate ordered manager calls only (from existing knowledge re-dump)
    var gm = asm.MainModule.Types.First(x => x.Name == "GameManager");
    var gmu = gm.Methods.First(x => x.Name == "gmUpdate" && x.HasBody);
    var calls = new StringBuilder();
    calls.AppendLine("# GameManager.gmUpdate ordered calls (full, V3.0.1)");
    calls.AppendLine("IL=" + gmu.Body.Instructions.Count);
    calls.AppendLine();
    int n = 0;
    foreach (var i in gmu.Body.Instructions) {
      if (i.OpCode.Code != Code.Call && i.OpCode.Code != Code.Callvirt) continue;
      MethodReference mr = i.Operand as MethodReference;
      if (mr == null) continue;
      n++;
      calls.AppendLine(n + ". IL_" + i.Offset.ToString("X4") + " `" + mr.DeclaringType.Name + "::" + mr.Name + "`");
    }
    File.WriteAllText(Path.Combine(outDir, "inventory-gmupdate-calls.md"), calls.ToString());

    // All types with name ending Manager that have Update method
    var sb2 = new StringBuilder();
    sb2.AppendLine("# Manager-like types with Update* methods");
    sb2.AppendLine();
    foreach (var t in asm.MainModule.Types.OrderBy(x => x.Name)) {
      bool nameHit = t.Name.Contains("Manager") || t.Name.Contains("Tracker") || t.Name.Contains("Director")
        || t.Name.Contains("Ticker") || t.Name.Contains("Spawner") || t.Name.EndsWith("Server");
      if (!nameHit) continue;
      foreach (var m in t.Methods.Where(m => m.HasBody && m.Name.StartsWith("Update"))) {
        sb2.AppendLine("- `" + t.Name + "::" + m.Name + "(" + string.Join(",", m.Parameters.Select(p => p.ParameterType.Name)) + ")` IL=" + m.Body.Instructions.Count + " MB=" + IsMB(t));
      }
    }
    File.WriteAllText(Path.Combine(outDir, "inventory-manager-updates.md"), sb2.ToString());

    Console.WriteLine("MB update methods: " + count);
    Console.WriteLine("gmUpdate calls: " + n);
  }
}
