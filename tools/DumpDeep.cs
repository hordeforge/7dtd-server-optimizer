using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Mono.Cecil;
using Mono.Cecil.Cil;

class DumpDeep {
  static AssemblyDefinition asm;
  static string outDir;

  static void Main(string[] args) {
    var resolver = new DefaultAssemblyResolver();
    resolver.AddSearchDirectory(Path.GetDirectoryName(args[0]));
    asm = AssemblyDefinition.ReadAssembly(args[0], new ReaderParameters { AssemblyResolver = resolver });
    outDir = args[1];
    Directory.CreateDirectory(outDir);

    string[][] targets = {
      new[]{"Entity","OnUpdateEntity","OnUpdatePosition","OnUpdateLive","Update","CanUpdateEntity"},
      new[]{"EntityAlive","OnUpdateLive","OnUpdateEntity","updateTasks","Update","CheckDespawn","GetAlertTicks"},
      new[]{"EAIManager","Update","Init","ClearTasks"},
      new[]{"EAITaskList","OnUpdateTasks","Update"},
      new[]{"PathNavigate","GetPathTo","UpdateNavigation","SetPath","Stop"},
      new[]{"PathFinderThread","FindPath","GetPath","IsCalculatingPath","StartWorkerThreads"},
      new[]{"AStarPathFinderThread","FindPath","GetPath","StartWorkerThreads"},
      new[]{"ASPPathFinderThread","FindPath","GetPath","StartWorkerThreads"},
      new[]{"AstarManager","Init","Update"},
      new[]{"NetEntityDistribution","OnUpdateEntities","Add","Remove","SendPacketToTrackedPlayers"},
      new[]{"World","LetBlocksFall","AddFallingBlock","AddFallingBlocks","TickSleeperVolumes","EntityActivityUpdate","TickEntities","TickEntity","TickEntitiesSlice","TickEntitiesFlush","OnUpdateTick"},
      new[]{"WorldBlockTicker","Tick","Add","ScheduleBlockUpdate"},
      new[]{"AIDirector","Tick","Update","DebugFrameLateUpdate"},
      new[]{"SleeperVolume","Tick","UpdatePlayerTouched","Respawn"},
      new[]{"GameManager","gmUpdate","UpdateTick","Update","ExplodeGroupFrameUpdate","updateTimeOfDay","updateBlockParticles"},
      new[]{"ConnectionManager","Update","ProcessPackages","FlushClientSendQueues","UpdatePings","SendPackage"},
      new[]{"DynamicMeshManager","Update"},
      new[]{"DynamicMeshServer","Update"},
      new[]{"ChunkManager","SendChunksToClients","DetermineChunksToLoad","CopyChunksToUnity","GroundAlignFrameUpdate"},
      new[]{"SpawnManagerBiomes","Update","SpawnUpdate"},
      new[]{"SpawnManagerAbstract","Update"},
      new[]{"EntityPlayer","OnUpdateLive","OnUpdateEntity"},
      new[]{"EntityEnemy","OnUpdateLive","OnUpdateEntity"},
      new[]{"EAIBase","Update","CanExecute","Start","Reset","Continue"},
      new[]{"GameTimer","updateTimer","Reset"},
      new[]{"PowerManager","Update"},
      new[]{"VehicleManager","Update"},
      new[]{"DroneManager","Update"},
      new[]{"TurretTracker","Update"},
      new[]{"DecoManager","UpdateTick"},
      new[]{"MultiBlockManager","MainThreadUpdate"},
      new[]{"EntityAsyncManager","Update"},
    };

    var index = new StringBuilder();
    index.AppendLine("# Deep dump index V3.0.1");
    index.AppendLine();
    index.AppendLine("Assembly: `" + args[0] + "`");
    index.AppendLine("UTC: " + DateTime.UtcNow.ToString("u"));
    index.AppendLine();

    foreach (var row in targets) {
      string typeName = row[0];
      var t = FindType(typeName);
      if (t == null) {
        index.AppendLine("- MISSING type `" + typeName + "`");
        continue;
      }
      index.AppendLine("## " + t.FullName + " : " + (t.BaseType != null ? t.BaseType.Name : "?"));
      var dumped = new HashSet<string>();
      for (int i = 1; i < row.Length; i++) {
        string want = row[i];
        foreach (var m in t.Methods) {
          if (!m.HasBody) continue;
          if (m.Name != want && m.Name.IndexOf(want, StringComparison.OrdinalIgnoreCase) < 0) continue;
          string key = m.FullName;
          if (!dumped.Add(key)) continue;
          Dump(m, index);
        }
      }
      index.AppendLine();
    }

    index.AppendLine("## Cross-refs");
    index.AppendLine();
    index.AppendLine("### aiActiveScale field ops");
    XrefField("aiActiveScale", index);
    index.AppendLine();
    index.AppendLine("### aiActiveDelay field ops");
    XrefField("aiActiveDelay", index);
    index.AppendLine();
    index.AppendLine("### Callers of EAIManager::Update");
    XrefMethod("EAIManager", "Update", index, 40);
    index.AppendLine();
    index.AppendLine("### Callers of EntityAlive::updateTasks");
    XrefMethod("EntityAlive", "updateTasks", index, 40);
    index.AppendLine();
    index.AppendLine("### Callers of Entity::OnUpdateEntity");
    XrefMethod("Entity", "OnUpdateEntity", index, 40);
    index.AppendLine();
    index.AppendLine("### Callers of EntityAlive::OnUpdateLive");
    XrefMethod("EntityAlive", "OnUpdateLive", index, 40);
    index.AppendLine();
    index.AppendLine("### Path FindPath-related callsites (sample)");
    XrefMethodContains("FindPath", index, 50);
    index.AppendLine();
    index.AppendLine("### Callers of World::LetBlocksFall");
    XrefMethod("World", "LetBlocksFall", index, 20);
    index.AppendLine();
    index.AppendLine("### Callers of NetEntityDistribution::OnUpdateEntities");
    XrefMethod("NetEntityDistribution", "OnUpdateEntities", index, 20);

    File.WriteAllText(Path.Combine(outDir, "INDEX.md"), index.ToString());
    Console.WriteLine("done " + outDir);
  }

  static TypeDefinition FindType(string name) {
    foreach (var t in asm.MainModule.Types)
      if (t.Name == name) return t;
    return null;
  }

  static void Dump(MethodDefinition m, StringBuilder index) {
    string safe = m.DeclaringType.Name + "_" + m.Name;
    safe = safe.Replace("`", "_").Replace("<", "_").Replace(">", "_");
    if (m.Parameters.Count > 0) {
      safe += "_" + string.Join("_", m.Parameters.Select(p =>
        p.ParameterType.Name.Replace("`", "_").Replace("<", "_").Replace(">", "_")));
    }
    int il = m.Body.Instructions.Count;
    index.AppendLine("- `" + m.Name + "(" + string.Join(", ", m.Parameters.Select(p => p.ParameterType.Name)) + ")` IL=" + il + " → `" + safe + "_*`");

    var calls = new List<KeyValuePair<int, string>>();
    foreach (var ins in m.Body.Instructions) {
      if (ins.OpCode.Code == Code.Call || ins.OpCode.Code == Code.Callvirt) {
        var mr = ins.Operand as MethodReference;
        if (mr != null) {
          string ps = string.Join(",", mr.Parameters.Select(p => p.ParameterType.Name));
          calls.Add(new KeyValuePair<int, string>(ins.Offset, mr.DeclaringType.Name + "::" + mr.Name + "(" + ps + ")"));
        }
      }
    }

    var sb = new StringBuilder();
    sb.AppendLine("# " + m.DeclaringType.FullName + "::" + m.Name);
    sb.AppendLine("IL=" + il + " locals=" + m.Body.Variables.Count + " maxstack=" + m.Body.MaxStackSize + " eh=" + m.Body.ExceptionHandlers.Count);
    sb.AppendLine();
    sb.AppendLine("## Call frequency");
    sb.AppendLine();
    sb.AppendLine("| N | Target |");
    sb.AppendLine("|---:|---|");
    foreach (var g in calls.GroupBy(c => c.Value).OrderByDescending(g => g.Count()).Take(80))
      sb.AppendLine("| " + g.Count() + " | `" + g.Key.Replace("|", "\\|") + "` |");
    sb.AppendLine();
    sb.AppendLine("## Ordered calls");
    sb.AppendLine();
    sb.AppendLine("| # | IL | Target |");
    sb.AppendLine("|---:|---:|---|");
    int n = 0;
    foreach (var c in calls)
      sb.AppendLine("| " + (++n) + " | IL_" + c.Key.ToString("X4") + " | `" + c.Value.Replace("|", "\\|") + "` |");
    File.WriteAllText(Path.Combine(outDir, safe + "_calls.md"), sb.ToString());

    var ilsb = new StringBuilder();
    ilsb.AppendLine("// " + m.DeclaringType.FullName + "::" + m.Name + " IL=" + il);
    foreach (var v in m.Body.Variables)
      ilsb.AppendLine("// V_" + v.Index + " " + v.VariableType);
    ilsb.AppendLine();
    foreach (var ins in m.Body.Instructions) {
      string op = ins.Operand == null ? "" : " " + OpStr(ins);
      ilsb.AppendLine("IL_" + ins.Offset.ToString("X4") + ": " + ins.OpCode.Name + op);
    }
    File.WriteAllText(Path.Combine(outDir, safe + "_il.txt"), ilsb.ToString());
  }

  static string OpStr(Instruction ins) {
    var t = ins.Operand as Instruction;
    if (t != null) return "IL_" + t.Offset.ToString("X4");
    var ts = ins.Operand as Instruction[];
    if (ts != null) return string.Join(",", Array.ConvertAll(ts, x => "IL_" + x.Offset.ToString("X4")));
    return ins.Operand.ToString().Replace("\n", " ");
  }

  static void XrefField(string field, StringBuilder index) {
    foreach (var t in asm.MainModule.Types) {
      foreach (var m in t.Methods) {
        if (!m.HasBody) continue;
        foreach (var i in m.Body.Instructions) {
          var fr = i.Operand as FieldReference;
          if (fr != null && fr.Name == field) {
            index.AppendLine("- `" + t.Name + "::" + m.Name + "` " + i.OpCode.Name);
            break;
          }
        }
      }
    }
  }

  static void XrefMethod(string type, string method, StringBuilder index, int max) {
    int n = 0;
    foreach (var t in asm.MainModule.Types) {
      foreach (var m in t.Methods) {
        if (!m.HasBody) continue;
        foreach (var i in m.Body.Instructions) {
          var mr = i.Operand as MethodReference;
          if (mr != null && mr.DeclaringType.Name == type && mr.Name == method) {
            index.AppendLine("- `" + t.Name + "::" + m.Name + "` → `" + type + "::" + method + "`");
            if (++n >= max) return;
            break;
          }
        }
      }
    }
  }

  static void XrefMethodContains(string name, StringBuilder index, int max) {
    int n = 0;
    foreach (var t in asm.MainModule.Types) {
      foreach (var m in t.Methods) {
        if (!m.HasBody) continue;
        foreach (var i in m.Body.Instructions) {
          var mr = i.Operand as MethodReference;
          if (mr == null) continue;
          if (mr.Name.IndexOf(name, StringComparison.OrdinalIgnoreCase) < 0) continue;
          if (mr.DeclaringType.Name.IndexOf("Path", StringComparison.OrdinalIgnoreCase) < 0 && mr.Name != "FindPath")
            continue;
          index.AppendLine("- `" + t.Name + "::" + m.Name + "` → `" + mr.DeclaringType.Name + "::" + mr.Name + "`");
          if (++n >= max) return;
          break;
        }
      }
    }
  }
}
