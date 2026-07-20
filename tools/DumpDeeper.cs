using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Mono.Cecil;
using Mono.Cecil.Cil;

/// <summary>Documentation-only deep RE: dump method bodies + structural notes.</summary>
class DumpDeeper {
  static AssemblyDefinition asm;
  static string outDir;
  static StringBuilder book;

  static void Main(string[] args) {
    var resolver = new DefaultAssemblyResolver();
    resolver.AddSearchDirectory(Path.GetDirectoryName(args[0]));
    asm = AssemblyDefinition.ReadAssembly(args[0], new ReaderParameters { AssemblyResolver = resolver });
    outDir = args[1];
    Directory.CreateDirectory(outDir);
    book = new StringBuilder();
    H1("Deeper RE notes (V3.0.1 dedicated)");
    book.AppendLine("Generated UTC: " + DateTime.UtcNow.ToString("u"));
    book.AppendLine("Assembly: `" + args[0] + "`");
    book.AppendLine();
    book.AppendLine("Documentation only. No game IL redistribution as product.");
    book.AppendLine();

    // ---- catalog all EAI* Update sizes ----
    H2("1. All EAI* / UAI* task methods by IL size");
    var eai = new List<Tuple<int,string>>();
    foreach (var t in asm.MainModule.Types) {
      if (!t.Name.StartsWith("EAI") && !t.Name.StartsWith("UAI") && t.Name != "EAITaskList" && t.Name != "EAIManager")
        continue;
      foreach (var m in t.Methods.Where(m => m.HasBody)) {
        int n = m.Body.Instructions.Count;
        if (n < 20) continue;
        eai.Add(Tuple.Create(n, t.Name + "::" + m.Name + "(" + Sig(m) + ")"));
      }
    }
    foreach (var x in eai.OrderByDescending(t => t.Item1).Take(60))
      book.AppendLine("- **" + x.Item1 + "** `" + x.Item2 + "`");

    // ---- Entity* updateTasks / OnUpdateLive sizes ----
    H2("2. Entity hierarchy live/update overrides (IL)");
    foreach (var t in asm.MainModule.Types.Where(t => t.Name.StartsWith("Entity")).OrderBy(t => t.Name)) {
      foreach (var name in new[] { "updateTasks", "OnUpdateLive", "OnUpdateEntity", "OnUpdatePosition", "Update" }) {
        foreach (var m in t.Methods.Where(m => m.HasBody && m.Name == name)) {
          int n = m.Body.Instructions.Count;
          if (n < 15 && name != "updateTasks") continue;
          book.AppendLine("- `" + t.Name + "::" + name + "` IL=" + n + " base=" + (t.BaseType != null ? t.BaseType.Name : "?"));
        }
      }
    }

    // ---- dump deep targets with full IL always ----
    H2("3. Dumped deep targets");
    string[][] targets = {
      new[]{"EntityMoveHelper","UpdateMoveHelper","SetMoveTo","UpdateClimbing","UpdateDigging"},
      new[]{"EAIApproachAndAttackTarget","Update","CanExecute","Start","Continue","Reset"},
      new[]{"EAISetNearestEntityAsTarget","Update","CanExecute","FindTarget"},
      new[]{"EAIWander","Update","Start","CanExecute"},
      new[]{"EAIRunAway","Update","CanExecute"},
      new[]{"EAIBreakBlock","Update","AttackBlock","CanExecute"},
      new[]{"PathNavigate","UpdateNavigation","noPath","getPathToPos"},
      new[]{"ASPPathNavigate","UpdateNavigation","pathFollow","GetPathTo","CreatePath"},
      new[]{"AStarPathFinderThread","thread_Pathfinder","FindPath","GetPath"},
      new[]{"EntityAlive","FindPath","CheckDespawn","updateCurrentBlockPosAndValue","CanSee","GetDistanceSq"},
      new[]{"EntityEnemy","OnUpdateLive","OnUpdateEntity","updateTasks"},
      new[]{"EntityAnimal","OnUpdateLive","updateTasks"},
      new[]{"EntityZombie","OnUpdateLive","updateTasks"},
      new[]{"World","GetClosestPlayer","GetEntitiesInBounds","ClipBoundsMove","AddFallingBlock","GroupFallingBlocks"},
      new[]{"NetEntityDistributionEntry","updatePlayerList","updatePlayerEntity","SendToPlayers","EncodePos","EncodeRot"},
      new[]{"SpawnManagerBiomes","SpawnUpdate","Update"},
      new[]{"AIDirectorBloodMoonComponent","Tick","get_BloodMoonActive","Spawn"},
      new[]{"AIDirector","ComponentsTick"},
      new[]{"AIHordeSpawner","Tick"},
      new[]{"SleeperVolume","Tick","UpdateSpawn","Despawn","CheckTouching"},
      new[]{"DecoManager","UpdateTick"},
      new[]{"WaterSplashCubes","Update"},
      new[]{"ChunkManager","DetermineChunksToLoad","SendChunksToClients","doCopyChunksToUnity"},
      new[]{"DynamicMeshServer","Update"},
      new[]{"GameTimer","updateTimer"},
      new[]{"EntitySeeCache","ClearIfExpired","CanSee","SetCanSee"},
      new[]{"EntityLookHelper","onUpdateLook"},
      new[]{"EAITaskList","isBestTask","areTasksCompatible","OnUpdateTasks"},
      new[]{"UAIBase","Update","addEntityTargetsToConsider"},
      new[]{"WorldBlockTicker","tickScheduled","tickRandom","execute"},
      new[]{"BlockLiquidv2","UpdateTick","UpdateTime"},
      new[]{"GameManager","ExplodeGroupFrameUpdate","updateTimeOfDay","updateBlockParticles","updatePauseState"},
      new[]{"ConnectionManager","ProcessPackages","SendPackage","FlushClientSendQueues"},
      new[]{"PowerManager","Update"},
      new[]{"VehicleManager","Update"},
      new[]{"DroneManager","Update"},
    };

    foreach (var row in targets) {
      var t = Find(row[0]);
      if (t == null) { book.AppendLine("- MISSING type `" + row[0] + "`"); continue; }
      for (int i = 1; i < row.Length; i++) {
        foreach (var m in t.Methods.Where(m => m.HasBody && (m.Name == row[i] || m.Name.IndexOf(row[i], StringComparison.OrdinalIgnoreCase) >= 0))) {
          DumpFull(m);
          book.AppendLine("- dumped `" + t.Name + "::" + m.Name + "(" + Sig(m) + ")` IL=" + m.Body.Instructions.Count);
        }
      }
    }

    // ASP FindPaths MoveNext full
    var asp = Find("ASPPathFinderThread");
    if (asp != null) {
      foreach (var nt in asp.NestedTypes) {
        foreach (var m in nt.Methods.Where(m => m.HasBody && m.Name == "MoveNext")) {
          DumpFull(m);
          book.AppendLine("- dumped `" + nt.Name + "::MoveNext` IL=" + m.Body.Instructions.Count);
        }
      }
    }

    // ---- constants extraction from key methods ----
    H2("4. Float/int constants in key methods (heuristic thresholds)");
    string[] constMethods = {
      "EntityAlive::FindPath","EntityAlive::updateTasks","World::EntityActivityUpdate",
      "World::GetClosestPlayer","NetEntityDistributionEntry::updatePlayerList",
      "EntityMoveHelper::UpdateMoveHelper","EAIApproachAndAttackTarget::Update",
      "EAIManager::Update","EAITaskList::OnUpdateTasks","SpawnManagerBiomes::SpawnUpdate",
      "SleeperVolume::Tick","GameTimer::updateTimer","ASPPathNavigate::pathFollow"
    };
    foreach (var cm in constMethods) {
      var parts = cm.Split(new[]{':'}, StringSplitOptions.RemoveEmptyEntries);
      var t = Find(parts[0]);
      if (t == null) continue;
      foreach (var m in t.Methods.Where(m => m.HasBody && m.Name == parts[1])) {
        var floats = new List<float>();
        var ints = new List<int>();
        foreach (var ins in m.Body.Instructions) {
          if (ins.OpCode.Code == Code.Ldc_R4 && ins.Operand is float f) floats.Add(f);
          if (ins.OpCode.Code == Code.Ldc_I4 && ins.Operand is int ii) ints.Add(ii);
          if (ins.OpCode.Code == Code.Ldc_I4_S && ins.Operand is sbyte sb) ints.Add(sb);
        }
        book.AppendLine("### `" + cm + "` IL=" + m.Body.Instructions.Count);
        book.AppendLine("- floats: " + string.Join(", ", floats.Distinct().OrderBy(x => x).Select(x => x.ToString(System.Globalization.CultureInfo.InvariantCulture)).Take(40)));
        book.AppendLine("- ints: " + string.Join(", ", ints.Distinct().OrderBy(x => x).Take(40)));
        book.AppendLine();
      }
    }

    // ---- GetEntitiesInBounds / GetClosestPlayer caller heat ----
    H2("5. Spatial query callers (full)");
    XrefAll("GetClosestPlayer");
    XrefAll("GetEntitiesInBounds");
    XrefAll("FindPath");
    XrefAll("UpdateMoveHelper");
    XrefAll("AddFallingBlock");
    XrefAll("SpawnUpdate");
    XrefAll("pathFollow");
    XrefAll("SetPath");
    XrefAll("GetPathTo");

    // ---- fields on EntityAlive AI related ----
    H2("6. EntityAlive AI-related fields");
    var ea = Find("EntityAlive");
    if (ea != null) {
      foreach (var f in ea.Fields.OrderBy(f => f.Name)) {
        string n = f.Name;
        if (n.IndexOf("ai", StringComparison.OrdinalIgnoreCase) >= 0
            || n.IndexOf("path", StringComparison.OrdinalIgnoreCase) >= 0
            || n.IndexOf("move", StringComparison.OrdinalIgnoreCase) >= 0
            || n.IndexOf("nav", StringComparison.OrdinalIgnoreCase) >= 0
            || n.IndexOf("look", StringComparison.OrdinalIgnoreCase) >= 0
            || n.IndexOf("alert", StringComparison.OrdinalIgnoreCase) >= 0
            || n.IndexOf("sleep", StringComparison.OrdinalIgnoreCase) >= 0
            || n.IndexOf("target", StringComparison.OrdinalIgnoreCase) >= 0
            || n.IndexOf("investigate", StringComparison.OrdinalIgnoreCase) >= 0
            || n.IndexOf("distraction", StringComparison.OrdinalIgnoreCase) >= 0
            || n.IndexOf("see", StringComparison.OrdinalIgnoreCase) >= 0)
          book.AppendLine("- `" + f.Name + "` : " + f.FieldType.Name);
      }
    }

    H2("7. World tick-related fields");
    var w = Find("World");
    if (w != null) {
      foreach (var f in w.Fields.OrderBy(f => f.Name)) {
        string n = f.Name;
        if (n.IndexOf("tick", StringComparison.OrdinalIgnoreCase) >= 0
            || n.IndexOf("fall", StringComparison.OrdinalIgnoreCase) >= 0
            || n.IndexOf("sleeper", StringComparison.OrdinalIgnoreCase) >= 0
            || n.IndexOf("spawn", StringComparison.OrdinalIgnoreCase) >= 0
            || n.IndexOf("chunk", StringComparison.OrdinalIgnoreCase) >= 0
            || n.IndexOf("entity", StringComparison.OrdinalIgnoreCase) >= 0)
          book.AppendLine("- `" + f.Name + "` : " + f.FieldType.Name);
      }
    }

    H2("8. PathFinderThread / ASP / AStar fields");
    foreach (var tn in new[] { "PathFinderThread", "ASPPathFinderThread", "AStarPathFinderThread" }) {
      var t = Find(tn);
      if (t == null) continue;
      book.AppendLine("### " + tn);
      foreach (var f in t.Fields)
        book.AppendLine("- `" + f.Name + "` : " + f.FieldType.FullName);
    }

    // ---- Net package types used from updatePlayerList ----
    H2("9. NetPackage* constructed in NetEntityDistributionEntry");
    var ne = Find("NetEntityDistributionEntry");
    if (ne != null) {
      var pkgs = new HashSet<string>();
      foreach (var m in ne.Methods.Where(m => m.HasBody)) {
        foreach (var ins in m.Body.Instructions) {
          var mr = ins.Operand as MethodReference;
          if (mr == null) continue;
          if (mr.DeclaringType.Name.StartsWith("NetPackage") || mr.Name == "GetPackage")
            pkgs.Add(mr.DeclaringType.Name + "::" + mr.Name);
          if (mr.Name.StartsWith("Setup") && m.Name.IndexOf("update", StringComparison.OrdinalIgnoreCase) >= 0)
            pkgs.Add("via " + m.Name + " → " + mr.DeclaringType.Name + "::" + mr.Name);
        }
      }
      foreach (var p in pkgs.OrderBy(x => x))
        book.AppendLine("- `" + p + "`");
    }

    // ---- MoveHelper call frequency internal ----
    H2("10. EntityMoveHelper.UpdateMoveHelper call breakdown");
    SummarizeCalls("EntityMoveHelper", "UpdateMoveHelper");

    H2("11. EAIApproachAndAttackTarget.Update call breakdown");
    SummarizeCalls("EAIApproachAndAttackTarget", "Update");

    H2("12. SpawnManagerBiomes.SpawnUpdate call breakdown");
    SummarizeCalls("SpawnManagerBiomes", "SpawnUpdate");

    H2("13. NetEntityDistributionEntry.updatePlayerList call breakdown");
    SummarizeCalls("NetEntityDistributionEntry", "updatePlayerList");

    H2("14. DynamicMeshServer.Update call breakdown");
    SummarizeCalls("DynamicMeshServer", "Update");

    H2("15. ChunkManager.DetermineChunksToLoad call breakdown");
    SummarizeCalls("ChunkManager", "DetermineChunksToLoad");

    H2("16. GameTimer.updateTimer structure");
    SummarizeCalls("GameTimer", "updateTimer");
    DumpConstants("GameTimer", "updateTimer");

    File.WriteAllText(Path.Combine(outDir, "DEEPER.md"), book.ToString());
    // also write a machine index of all dumps
    var idx = new StringBuilder();
    idx.AppendLine("# File index");
    foreach (var f in Directory.GetFiles(outDir).OrderBy(x => x))
      idx.AppendLine("- `" + Path.GetFileName(f) + "`");
    File.WriteAllText(Path.Combine(outDir, "INDEX.md"), idx.ToString());
    Console.WriteLine("Wrote DEEPER.md files=" + Directory.GetFiles(outDir).Length);
  }

  static void H1(string s) { book.AppendLine("# " + s); book.AppendLine(); }
  static void H2(string s) { book.AppendLine(); book.AppendLine("## " + s); book.AppendLine(); }

  static TypeDefinition Find(string name) {
    return asm.MainModule.Types.FirstOrDefault(t => t.Name == name);
  }

  static string Sig(MethodDefinition m) {
    return string.Join(",", m.Parameters.Select(p => p.ParameterType.Name));
  }

  static string SafeName(MethodDefinition m) {
    string s = m.DeclaringType.Name + "_" + m.Name;
    if (m.DeclaringType.IsNested)
      s = m.DeclaringType.DeclaringType.Name + "_" + m.DeclaringType.Name.Replace("<", "_").Replace(">", "_").Replace("/", "_") + "_" + m.Name;
    s = s.Replace("`", "_").Replace("<", "_").Replace(">", "_").Replace("|", "_");
    if (m.Parameters.Count > 0)
      s += "_" + string.Join("_", m.Parameters.Select(p => p.ParameterType.Name.Replace("`", "_").Replace("<", "_").Replace(">", "_")));
    if (s.Length > 140) s = s.Substring(0, 140);
    return s;
  }

  static void DumpFull(MethodDefinition m) {
    string safe = SafeName(m);
    var il = new StringBuilder();
    il.AppendLine("// " + m.DeclaringType.FullName + "::" + m.Name + " IL=" + m.Body.Instructions.Count);
    foreach (var v in m.Body.Variables)
      il.AppendLine("// V_" + v.Index + " " + v.VariableType);
    il.AppendLine();
    foreach (var ins in m.Body.Instructions) {
      string op = ins.Operand == null ? "" : " " + OpStr(ins);
      il.AppendLine("IL_" + ins.Offset.ToString("X4") + ": " + ins.OpCode.Name + op);
    }
    File.WriteAllText(Path.Combine(outDir, safe + "_il.txt"), il.ToString());

    var calls = new StringBuilder();
    calls.AppendLine("# " + m.DeclaringType.FullName + "::" + m.Name);
    calls.AppendLine("IL=" + m.Body.Instructions.Count);
    calls.AppendLine();
    var list = new List<string>();
    int news = 0, boxes = 0;
    foreach (var ins in m.Body.Instructions) {
      if (ins.OpCode.Code == Code.Newobj) news++;
      if (ins.OpCode.Code == Code.Box) boxes++;
      if (ins.OpCode.Code == Code.Call || ins.OpCode.Code == Code.Callvirt) {
        var mr = ins.Operand as MethodReference;
        if (mr != null)
          list.Add(mr.DeclaringType.Name + "::" + mr.Name + "(" + string.Join(",", mr.Parameters.Select(p => p.ParameterType.Name)) + ")");
      }
    }
    calls.AppendLine("newobj~=" + news + " box=" + boxes);
    calls.AppendLine();
    calls.AppendLine("## Frequency");
    calls.AppendLine();
    foreach (var g in list.GroupBy(x => x).OrderByDescending(g => g.Count()).Take(50))
      calls.AppendLine("- " + g.Count() + "x `" + g.Key + "`");
    calls.AppendLine();
    calls.AppendLine("## Ordered");
    calls.AppendLine();
    int n = 0;
    foreach (var c in list) {
      calls.AppendLine((++n) + ". `" + c + "`");
      if (n >= 200) { calls.AppendLine("..."); break; }
    }
    File.WriteAllText(Path.Combine(outDir, safe + "_calls.md"), calls.ToString());
  }

  static string OpStr(Instruction ins) {
    var t = ins.Operand as Instruction;
    if (t != null) return "IL_" + t.Offset.ToString("X4");
    var ts = ins.Operand as Instruction[];
    if (ts != null) return string.Join(",", Array.ConvertAll(ts, x => "IL_" + x.Offset.ToString("X4")));
    return ins.Operand.ToString().Replace("\n", " ");
  }

  static void XrefAll(string methodName) {
    book.AppendLine("### `" + methodName + "`");
    book.AppendLine();
    int n = 0;
    foreach (var t in asm.MainModule.Types) {
      foreach (var m in t.Methods) {
        if (!m.HasBody) continue;
        foreach (var i in m.Body.Instructions) {
          var mr = i.Operand as MethodReference;
          if (mr != null && mr.Name == methodName) {
            book.AppendLine("- `" + t.Name + "::" + m.Name + "` → `" + mr.DeclaringType.Name + "::" + mr.Name + "`");
            n++;
            break;
          }
        }
      }
    }
    book.AppendLine();
    book.AppendLine("_(" + n + " caller types)_");
    book.AppendLine();
  }

  static void SummarizeCalls(string type, string method) {
    var t = Find(type);
    if (t == null) { book.AppendLine("MISSING " + type); return; }
    foreach (var m in t.Methods.Where(m => m.HasBody && m.Name == method)) {
      book.AppendLine("### `" + type + "::" + method + "` IL=" + m.Body.Instructions.Count);
      var list = new List<string>();
      foreach (var ins in m.Body.Instructions) {
        if (ins.OpCode.Code == Code.Call || ins.OpCode.Code == Code.Callvirt) {
          var mr = ins.Operand as MethodReference;
          if (mr != null) list.Add(mr.DeclaringType.Name + "::" + mr.Name);
        }
      }
      foreach (var g in list.GroupBy(x => x).OrderByDescending(g => g.Count()).Take(35))
        book.AppendLine("- " + g.Count() + "x `" + g.Key + "`");
      book.AppendLine();
      DumpFull(m);
    }
  }

  static void DumpConstants(string type, string method) {
    var t = Find(type);
    if (t == null) return;
    foreach (var m in t.Methods.Where(m => m.HasBody && m.Name == method)) {
      var floats = new List<float>();
      foreach (var ins in m.Body.Instructions)
        if (ins.OpCode.Code == Code.Ldc_R4 && ins.Operand is float f) floats.Add(f);
      book.AppendLine("constants floats: " + string.Join(", ", floats.Distinct()));
    }
  }
}
