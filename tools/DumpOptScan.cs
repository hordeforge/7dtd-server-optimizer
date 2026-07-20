using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Mono.Cecil;
using Mono.Cecil.Cil;

class DumpOptScan {
  static AssemblyDefinition asm;
  static string outDir;
  static StringBuilder report;

  static void Main(string[] args) {
    var resolver = new DefaultAssemblyResolver();
    resolver.AddSearchDirectory(Path.GetDirectoryName(args[0]));
    asm = AssemblyDefinition.ReadAssembly(args[0], new ReaderParameters { AssemblyResolver = resolver });
    outDir = args[1];
    Directory.CreateDirectory(outDir);
    report = new StringBuilder();
    report.AppendLine("# Optimization scan RE (V3.0.1)");
    report.AppendLine();
    report.AppendLine("Generated: " + DateTime.UtcNow.ToString("u"));
    report.AppendLine("Assembly: `" + args[0] + "`");
    report.AppendLine();

    // --- large methods on hot types ---
    Section("Largest methods (IL count) — scan selected type name prefixes");
    string[] prefixes = {
      "World","GameManager","Entity","EAI","UAI","Path","Astar","ASP","Chunk","Spawn","Sleeper",
      "NetEntity","Connection","DynamicMesh","Deco","Power","Vehicle","Drone","AIDirector",
      "Block","Falling","Water","MultiBlock","GameTimer","Biome","Prefab","Mesh"
    };
    var big = new List<Tuple<int,string>>();
    foreach (var t in asm.MainModule.Types) {
      bool hit = false;
      foreach (var p in prefixes)
        if (t.Name.IndexOf(p, StringComparison.OrdinalIgnoreCase) >= 0) { hit = true; break; }
      if (!hit) continue;
      foreach (var m in t.Methods) {
        if (!m.HasBody) continue;
        int n = m.Body.Instructions.Count;
        if (n < 80) continue;
        big.Add(Tuple.Create(n, t.Name + "::" + m.Name + "(" + string.Join(",", m.Parameters.Select(p => p.ParameterType.Name)) + ")"));
      }
      // nested
      foreach (var nt in t.NestedTypes) {
        foreach (var m in nt.Methods) {
          if (!m.HasBody) continue;
          int n = m.Body.Instructions.Count;
          if (n < 100) continue;
          big.Add(Tuple.Create(n, t.Name + "/" + nt.Name + "::" + m.Name + " IL=" + n));
        }
      }
    }
    foreach (var x in big.OrderByDescending(t => t.Item1).Take(80))
      report.AppendLine("- **" + x.Item1 + "** `" + x.Item2 + "`");

    // --- dump specific optim-interesting methods ---
    string[][] targets = {
      new[]{"World","AddFallingBlock","AddFallingBlocks","GroupFallingBlocks","CreateFallingBlockGroup","LetBlocksFall","GetClosestPlayer","GetEntitiesInBounds","TickSleeperVolumes","ClearCaches","SaveWorldState"},
      new[]{"WorldBlockTicker","Tick","tickScheduled","tickRandom"},
      new[]{"SpawnManagerBiomes","Update","SpawnUpdate","Update"},
      new[]{"SpawnManagerAbstract","Update"},
      new[]{"AIDirector","ComponentsTick","Tick","DebugTick"},
      new[]{"SleeperVolume","Tick","Respawn","UpdatePlayerTouched","CheckTouching"},
      new[]{"DecoManager","UpdateTick","Update"},
      new[]{"ChunkManager","SendChunksToClients","DetermineChunksToLoad","CopyChunksToUnity","GroundAlignFrameUpdate","ReloadAllChunks"},
      new[]{"NetEntityDistribution","OnUpdateEntities","updateTrackedEntities"},
      new[]{"NetEntityDistributionEntry","update","updatePlayerList","updatePlayerEntity","sendToPlayers","SendPackage"},
      new[]{"EntityAlive","FindPath","updateTasks","OnUpdateLive","GetSpeedModifier"},
      new[]{"EntityEnemy","OnUpdateLive","OnUpdateEntity","updateTasks"},
      new[]{"EntityPlayer","OnUpdateLive","OnUpdateEntity"},
      new[]{"PathNavigate","UpdateNavigation","SetPath","GetPathTo"},
      new[]{"ASPPathNavigate","UpdateNavigation","GetPathTo","CreatePath"},
      new[]{"AstarManager","UpdateGraphs","Init","OriginChanged"},
      new[]{"DynamicMeshServer","Update","SendToClients"},
      new[]{"DynamicMeshManager","Update","ProcessItemMeshGeneration","ProcessChunkRegionRequests"},
      new[]{"ConnectionManager","ProcessPackages","FlushClientSendQueues","UpdatePings","SendPackage"},
      new[]{"GameTimer","updateTimer","Reset"},
      new[]{"EntityActivity"," "},
      new[]{"WaterSplashCubes","Update"},
      new[]{"MultiBlockManager","MainThreadUpdate","Update"},
      new[]{"PowerManager","Update","UpdatePowerManager"},
      new[]{"VehicleManager","Update"},
      new[]{"DroneManager","Update"},
      new[]{"FactionManager","Update"},
      new[]{"QuestEventManager","Update"},
      new[]{"GameEventManager","Update"},
      new[]{"ThreadManager","UpdateMainThreadTasks","LateUpdate"},
      new[]{"MemoryPools","Cleanup"},
      new[]{"Physics","SyncTransforms"},
    };

    Section("Dumped method inventory");
    var dumpedFiles = new List<string>();
    foreach (var row in targets) {
      var t = FindType(row[0]);
      if (t == null) { report.AppendLine("- MISSING `" + row[0] + "`"); continue; }
      for (int i = 1; i < row.Length; i++) {
        if (string.IsNullOrWhiteSpace(row[i])) continue;
        foreach (var m in t.Methods.Where(m => m.HasBody && (m.Name == row[i] || m.Name.IndexOf(row[i], StringComparison.OrdinalIgnoreCase) >= 0))) {
          string f = DumpMethod(m);
          dumpedFiles.Add(f);
          report.AppendLine("- `" + t.Name + "::" + m.Name + "` IL=" + m.Body.Instructions.Count + " → " + f);
        }
      }
    }

    // Find nested FindPaths MoveNext (state machine)
    Section("ASPPathFinderThread nested state machine methods");
    var asp = FindType("ASPPathFinderThread");
    if (asp != null) {
      foreach (var nt in asp.NestedTypes) {
        report.AppendLine("- nested `" + nt.FullName + "`");
        foreach (var m in nt.Methods.Where(m => m.HasBody)) {
          string f = DumpMethod(m);
          report.AppendLine("  - `" + m.Name + "` IL=" + m.Body.Instructions.Count + " → " + f);
        }
      }
    }

    // AStar thread_Pathfinder
    Section("AStarPathFinderThread.thread_Pathfinder (exists, not default Init)");
    var ast = FindType("AStarPathFinderThread");
    if (ast != null) {
      foreach (var m in ast.Methods.Where(m => m.HasBody && m.Name.IndexOf("Path", StringComparison.OrdinalIgnoreCase) >= 0)) {
        report.AppendLine("- `" + m.Name + "` IL=" + m.Body.Instructions.Count);
        DumpMethod(m);
      }
    }

    // Xrefs: GetClosestPlayer, AddFallingBlock, SendPackage, SyncTransforms, GC.Collect
    Section("Cross-refs (callers)");
    XrefCallers("World", "GetClosestPlayer", 30);
    XrefCallers("World", "AddFallingBlock", 25);
    XrefCallers("World", "AddFallingBlocks", 15);
    XrefCallers("World", "GetEntitiesInBounds", 30);
    XrefCallers("EntityAlive", "FindPath", 40);
    XrefCallers("GameManager", "get_IsDedicatedServer", 5);
    XrefFieldWriters("aiActiveScale");
    XrefFieldWriters("fallingBlocks");
    XrefAnyCall("GC::Collect", 15);
    XrefAnyCall("Physics::SyncTransforms", 15);
    XrefAnyCall("GetClosestPlayer", 35);
    XrefAnyCall("SendChunksToClients", 10);

    // Allocation-ish: newobj count in hot methods
    Section("newobj density in selected hot methods (alloc pressure hint)");
    string[] hot = {
      "EntityAlive::updateTasks","EntityAlive::OnUpdateLive","EntityAlive::OnUpdateEntity",
      "World::TickEntities","World::TickEntity","World::EntityActivityUpdate","World::LetBlocksFall",
      "World::OnUpdateTick","EAITaskList::OnUpdateTasks","NetEntityDistribution::OnUpdateEntities",
      "DecoManager::UpdateTick","ChunkManager::SendChunksToClients","GameManager::gmUpdate",
      "GameManager::UpdateTick","ConnectionManager::Update","DynamicMeshManager::Update",
      "ASPPathFinderThread::FindPath","AStarPathFinderThread::thread_Pathfinder"
    };
    foreach (var h in hot) {
      var parts = h.Split(new[]{':'}, StringSplitOptions.RemoveEmptyEntries);
      var t = FindType(parts[0]);
      if (t == null) continue;
      foreach (var m in t.Methods.Where(m => m.HasBody && m.Name == parts[1])) {
        int news = m.Body.Instructions.Count(i => i.OpCode.Code == Code.Newobj);
        int calls = m.Body.Instructions.Count(i => i.OpCode.Code == Code.Call || i.OpCode.Code == Code.Callvirt);
        int boxes = m.Body.Instructions.Count(i => i.OpCode.Code == Code.Box);
        report.AppendLine("- `" + h + "` IL=" + m.Body.Instructions.Count + " newobj=" + news + " box=" + boxes + " calls=" + calls);
      }
    }

    // Enumerate GamePrefs related to AI if present as strings in methods - skip

    // Find MaxSpawned / ai constants in fields
    Section("Interesting static/instance fields (name heuristics)");
    string[] fname = { "MaxSpawned", "aiActive", "falling", "sleeper", "tickEntity", "path", "Spawn", "ViewDistance", "MaxZombie", "bloodMoon", "interest", "slice" };
    int fc = 0;
    foreach (var t in asm.MainModule.Types) {
      foreach (var f in t.Fields) {
        foreach (var n in fname) {
          if (f.Name.IndexOf(n, StringComparison.OrdinalIgnoreCase) >= 0) {
            report.AppendLine("- `" + t.Name + "::" + f.Name + "` : " + f.FieldType.Name);
            if (++fc > 100) goto donefields;
            break;
          }
        }
      }
    }
    donefields:

    File.WriteAllText(Path.Combine(outDir, "OPT_SCAN.md"), report.ToString());
    Console.WriteLine("Wrote " + Path.Combine(outDir, "OPT_SCAN.md") + " dumps=" + Directory.GetFiles(outDir).Length);
  }

  static void Section(string title) {
    report.AppendLine();
    report.AppendLine("## " + title);
    report.AppendLine();
  }

  static TypeDefinition FindType(string name) {
    return asm.MainModule.Types.FirstOrDefault(t => t.Name == name);
  }

  static string DumpMethod(MethodDefinition m) {
    string safe = (m.DeclaringType.Name + "_" + m.Name).Replace("`", "_").Replace("<", "_").Replace(">", "_").Replace("/", "_");
    if (m.Parameters.Count > 0)
      safe += "_" + string.Join("_", m.Parameters.Select(p => p.ParameterType.Name.Replace("`", "_").Replace("<", "_").Replace(">", "_")));
    // avoid huge collisions
    if (safe.Length > 120) safe = safe.Substring(0, 120);

    var calls = new List<string>();
    var ordered = new List<string>();
    int news = 0;
    foreach (var ins in m.Body.Instructions) {
      if (ins.OpCode.Code == Code.Newobj) news++;
      if (ins.OpCode.Code == Code.Call || ins.OpCode.Code == Code.Callvirt) {
        var mr = ins.Operand as MethodReference;
        if (mr != null) {
          string s = mr.DeclaringType.Name + "::" + mr.Name;
          calls.Add(s);
          ordered.Add("IL_" + ins.Offset.ToString("X4") + " " + s + "(" + string.Join(",", mr.Parameters.Select(p => p.ParameterType.Name)) + ")");
        }
      }
    }

    var sb = new StringBuilder();
    sb.AppendLine("# " + m.DeclaringType.FullName + "::" + m.Name);
    sb.AppendLine("IL=" + m.Body.Instructions.Count + " locals=" + m.Body.Variables.Count + " newobj~=" + news + " eh=" + m.Body.ExceptionHandlers.Count);
    sb.AppendLine();
    sb.AppendLine("## Top calls");
    sb.AppendLine();
    foreach (var g in calls.GroupBy(c => c).OrderByDescending(g => g.Count()).Take(40))
      sb.AppendLine("- " + g.Count() + "x `" + g.Key + "`");
    sb.AppendLine();
    sb.AppendLine("## Ordered calls");
    sb.AppendLine();
    int n = 0;
    foreach (var o in ordered) {
      sb.AppendLine((++n) + ". `" + o + "`");
      if (n >= 120) { sb.AppendLine("... truncated"); break; }
    }
    string path = Path.Combine(outDir, safe + "_calls.md");
    File.WriteAllText(path, sb.ToString());

    // IL only if manageable
    if (m.Body.Instructions.Count <= 400) {
      var il = new StringBuilder();
      il.AppendLine("// " + m.DeclaringType.FullName + "::" + m.Name + " IL=" + m.Body.Instructions.Count);
      foreach (var ins in m.Body.Instructions) {
        string op = ins.Operand == null ? "" : " " + OpStr(ins);
        il.AppendLine("IL_" + ins.Offset.ToString("X4") + ": " + ins.OpCode.Name + op);
      }
      File.WriteAllText(Path.Combine(outDir, safe + "_il.txt"), il.ToString());
    }
    return safe + "_calls.md";
  }

  static string OpStr(Instruction ins) {
    var t = ins.Operand as Instruction;
    if (t != null) return "IL_" + t.Offset.ToString("X4");
    var ts = ins.Operand as Instruction[];
    if (ts != null) return string.Join(",", Array.ConvertAll(ts, x => "IL_" + x.Offset.ToString("X4")));
    return ins.Operand.ToString().Replace("\n", " ");
  }

  static void XrefCallers(string type, string method, int max) {
    report.AppendLine("### Callers of `" + type + "::" + method + "`");
    report.AppendLine();
    int n = 0;
    foreach (var t in asm.MainModule.Types) {
      foreach (var m in t.Methods) {
        if (!m.HasBody) continue;
        foreach (var i in m.Body.Instructions) {
          var mr = i.Operand as MethodReference;
          if (mr != null && mr.DeclaringType.Name == type && mr.Name == method) {
            report.AppendLine("- `" + t.Name + "::" + m.Name + "`");
            if (++n >= max) { report.AppendLine(); return; }
            break;
          }
        }
      }
    }
    report.AppendLine();
  }

  static void XrefFieldWriters(string field) {
    report.AppendLine("### Field `" + field + "` ops");
    report.AppendLine();
    foreach (var t in asm.MainModule.Types) {
      foreach (var m in t.Methods) {
        if (!m.HasBody) continue;
        foreach (var i in m.Body.Instructions) {
          var fr = i.Operand as FieldReference;
          if (fr != null && fr.Name == field) {
            report.AppendLine("- `" + t.Name + "::" + m.Name + "` " + i.OpCode.Name);
            break;
          }
        }
      }
    }
    report.AppendLine();
  }

  static void XrefAnyCall(string contains, int max) {
    report.AppendLine("### Calls matching `" + contains + "`");
    report.AppendLine();
    int n = 0;
    foreach (var t in asm.MainModule.Types) {
      foreach (var m in t.Methods) {
        if (!m.HasBody) continue;
        foreach (var i in m.Body.Instructions) {
          var mr = i.Operand as MethodReference;
          if (mr == null) continue;
          string s = mr.DeclaringType.Name + "::" + mr.Name;
          if (s.IndexOf(contains.Replace("::", ".").Split('.')[0], StringComparison.OrdinalIgnoreCase) < 0
              && s.IndexOf(contains, StringComparison.OrdinalIgnoreCase) < 0) {
            // also match Collect
            if (contains.IndexOf("Collect", StringComparison.Ordinal) >= 0 && mr.Name != "Collect") continue;
            if (contains.IndexOf("SyncTransforms", StringComparison.Ordinal) >= 0 && mr.Name != "SyncTransforms") continue;
            if (contains.IndexOf("GetClosestPlayer", StringComparison.Ordinal) >= 0 && mr.Name != "GetClosestPlayer") continue;
            if (contains.IndexOf("SendChunksToClients", StringComparison.Ordinal) >= 0 && mr.Name != "SendChunksToClients") continue;
            if (!(mr.Name == "Collect" || mr.Name == "SyncTransforms" || mr.Name == "GetClosestPlayer" || mr.Name == "SendChunksToClients"))
              continue;
          }
          if (contains.Contains("Collect") && mr.Name != "Collect") continue;
          if (contains.Contains("SyncTransforms") && mr.Name != "SyncTransforms") continue;
          if (contains.Contains("GetClosestPlayer") && mr.Name != "GetClosestPlayer") continue;
          if (contains.Contains("SendChunksToClients") && mr.Name != "SendChunksToClients") continue;

          report.AppendLine("- `" + t.Name + "::" + m.Name + "` → `" + s + "`");
          if (++n >= max) { report.AppendLine(); return; }
          break;
        }
      }
    }
    report.AppendLine();
  }
}
