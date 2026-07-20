using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Mono.Cecil;
using Mono.Cecil.Cil;

/// <summary>
/// Completeness pass: dump remaining dedicated-loop subsystems + open-gap signals.
/// </summary>
class DumpLoopComplete {
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
    book.AppendLine("# Loop-complete dump notes (V3.0.1)");
    book.AppendLine();
    book.AppendLine("UTC: " + DateTime.UtcNow.ToString("u"));
    book.AppendLine();

    // Dump key remaining methods
    string[][] targets = {
      new[]{"AIDirector","ComponentsTick","Tick","DebugTick","CanSpawn"},
      new[]{"AIDirectorBloodMoonComponent","Tick","get_BloodMoonActive","Spawn"},
      new[]{"AIDirectorChunkEventComponent","Tick","SpawnScouts"},
      new[]{"AIDirectorWanderingHordeComponent","Tick"},
      new[]{"AIDirectorPlayerManagementComponent","Tick"},
      new[]{"AIHordeSpawner","Tick","Update"},
      new[]{"AIScoutHordeSpawner","Update","UpdateHorde"},
      new[]{"AIWanderingHordeSpawner","Update","UpdateSpawn","UpdateHorde"},
      new[]{"GameManager","SaveLocalPlayerData","SaveWorld","ExplodeGroupFrameUpdate","updateTimeOfDay","updateBlockParticles","ReportUnusedAssets","Cleanup"},
      new[]{"World","SaveWorldState","SaveDecorations","ClearCaches","ClipBoundsMove"},
      new[]{"PersistentPlayerList","Save","Write"},
      new[]{"WorldState","Save","Load","SaveLoad"},
      new[]{"IChunkProvider","Update","SaveRandomChunks"},
      new[]{"ChunkProviderGenerateWorld","Update","SaveRandomChunks","MainThreadCacheProtectedPositions"},
      new[]{"ChunkCluster","SetBlock","chunkPosNeedsRegeneration"},
      new[]{"NetEntityDistribution","OnUpdateEntities","Add","Remove"},
      new[]{"ProtocolManager","Update"},
      new[]{"SdtdConsole","Update"},
      new[]{"LoadManager","Update"},
      new[]{"PlatformManager","Update","LateUpdate"},
      new[]{"Origin","FixedUpdate"},
      new[]{"WorldEnvironment","Update"},
      new[]{"SkyManager","Update"},
      new[]{"GameLightManager","UpdateLightFrameUpdate","UpdateLightInit"},
      new[]{"WaterSimulationNative","Update"},
      new[]{"WaterEvaporationManager","Update","UpdateEvaporation"},
      new[]{"SignTextureManager","MainThreadUpdate"},
      new[]{"MultiBlockManager","MainThreadUpdate","UpdateOversizedStability","UpdateAlignment"},
      new[]{"DynamicMusic","Conductor"},
      new[]{"AstarManager","UpdateGraphs","UpdateMoveGraph","Init"},
      new[]{"FPS","Update"},
      new[]{"MemoryPools","Cleanup"},
      new[]{"GameObjectPool","FrameUpdate"},
      new[]{"MeshDataManager","LateUpdate","Update"},
      new[]{"ThreadManager","UpdateMainThreadTasks","LateUpdate","StartThread"},
      new[]{"EntityAsyncManager","Update"},
      new[]{"PowerManager","Update"},
      new[]{"QuestEventManager","Update"},
      new[]{"GameEventManager","Update","HandleSpawnUpdates","HandleActionUpdates"},
      new[]{"VehicleManager","Update"},
      new[]{"DroneManager","Update"},
      new[]{"TurretTracker","Update"},
      new[]{"FactionManager","Update"},
      new[]{"PartyManager","Update"},
      new[]{"TwitchManager","Update"},
      new[]{"TwitchVoteScheduler","Update"},
      new[]{"TriggerManager","Update"},
      new[]{"NavObjectManager","Update"},
      new[]{"TokenManager","Update"},
      new[]{"InviteManager","Update"},
      new[]{"LockManager","Update"},
      new[]{"RaycastPathManager","Update"},
      new[]{"DismembermentManager","Update"},
      new[]{"TrajectorySimulation","UpdateSimulationQueue"},
      new[]{"BlockedPlayerList","Update"},
      new[]{"SpeedTreeWindHistoryBufferManager","Update"},
      new[]{"PrefabEditModeManager","Update"},
      new[]{"TriggerEffectManager","Update"},
      new[]{"StabilityViewer","Update"},
      new[]{"GameSenseManager","Update"},
      new[]{"PrefabLODManager","FrameUpdate"},
      new[]{"BlockLiquidv2","UpdateTime","UpdateTick"},
      new[]{"EnvironmentAudioManager","Update","FixedUpdate","LateUpdate"},
      new[]{"Audio","Manager"},
    };

    book.AppendLine("## Dumped methods");
    book.AppendLine();
    foreach (var row in targets) {
      var t = FindType(row[0]);
      if (t == null) {
        // try nested / namespace-less match by ends with
        t = asm.MainModule.Types.FirstOrDefault(x => x.Name == row[0] || x.Name.EndsWith("." + row[0]));
      }
      if (t == null) {
        // DynamicMusic.Conductor
        if (row[0] == "DynamicMusic" || row[0] == "Audio") {
          foreach (var tt in asm.MainModule.Types.Where(x => x.FullName.Contains(row[0]))) {
            foreach (var m in tt.Methods.Where(m => m.HasBody && m.Name.Contains("Update"))) {
              Dump(m);
              book.AppendLine("- `" + tt.FullName + "::" + m.Name + "` IL=" + m.Body.Instructions.Count);
            }
          }
          continue;
        }
        book.AppendLine("- MISSING `" + row[0] + "`");
        continue;
      }
      for (int i = 1; i < row.Length; i++) {
        string want = row[i];
        bool any = false;
        foreach (var m in t.Methods.Where(m => m.HasBody && (m.Name == want || m.Name.IndexOf(want, StringComparison.OrdinalIgnoreCase) >= 0))) {
          Dump(m);
          book.AppendLine("- `" + t.Name + "::" + m.Name + "(" + string.Join(",", m.Parameters.Select(p => p.ParameterType.Name)) + ")` IL=" + m.Body.Instructions.Count);
          any = true;
        }
        if (!any && want != "get_BloodMoonActive")
          book.AppendLine("- no method `" + t.Name + "::*" + want + "*`");
      }
    }

    // AIDirector component list
    book.AppendLine();
    book.AppendLine("## AIDirector ComponentsTick callees");
    book.AppendLine();
    Summarize("AIDirector", "ComponentsTick");

    // Who implements IChunkProvider Update
    book.AppendLine();
    book.AppendLine("## Types with Save* methods (sample hot)");
    book.AppendLine();
    int sc = 0;
    foreach (var t in asm.MainModule.Types) {
      foreach (var m in t.Methods.Where(m => m.HasBody && m.Name.StartsWith("Save") && m.Body.Instructions.Count > 10)) {
        if (t.Name.Contains("World") || t.Name.Contains("Player") || t.Name.Contains("Chunk") || t.Name.Contains("GameManager") || t.Name.Contains("Persistent") || t.Name.Contains("Region")) {
          book.AppendLine("- `" + t.Name + "::" + m.Name + "` IL=" + m.Body.Instructions.Count);
          if (++sc > 40) goto donesave;
        }
      }
    }
    donesave:

    // FixedUpdate on Entity - what does it do
    book.AppendLine();
    book.AppendLine("## Entity FixedUpdate / Update (Unity path vs TickEntity)");
    book.AppendLine();
    foreach (var name in new[] { "Entity", "EntityAlive", "EntityPlayer" }) {
      var t = FindType(name);
      if (t == null) continue;
      foreach (var m in t.Methods.Where(m => m.HasBody && (m.Name == "Update" || m.Name == "FixedUpdate" || m.Name == "LateUpdate"))) {
        book.AppendLine("### " + name + "::" + m.Name + " IL=" + m.Body.Instructions.Count);
        var calls = new List<string>();
        foreach (var i in m.Body.Instructions) {
          if (i.OpCode.Code == Code.Call || i.OpCode.Code == Code.Callvirt) {
            var mr = i.Operand as MethodReference;
            if (mr != null) calls.Add(mr.DeclaringType.Name + "::" + mr.Name);
          }
        }
        foreach (var g in calls.GroupBy(c => c).OrderByDescending(g => g.Count()).Take(15))
          book.AppendLine("- " + g.Count() + "x `" + g.Key + "`");
        Dump(m);
        book.AppendLine();
      }
    }

    // ConnectionManager LateUpdate
    Summarize("ConnectionManager", "LateUpdate");
    Summarize("SdtdConsole", "Update");
    Summarize("LoadManager", "Update");
    Summarize("Origin", "FixedUpdate");
    Summarize("WorldEnvironment", "Update");
    Summarize("SkyManager", "Update");
    Summarize("AstarManager", "UpdateGraphs");
    Summarize("AIDirector", "ComponentsTick");
    Summarize("AIDirectorBloodMoonComponent", "Tick");
    Summarize("ProtocolManager", "Update");
    Summarize("MeshDataManager", "LateUpdate");
    Summarize("GameLightManager", "UpdateLightFrameUpdate");

    File.WriteAllText(Path.Combine(outDir, "inventory-loop-complete.md"), book.ToString());
    Console.WriteLine("files=" + Directory.GetFiles(outDir).Length);
  }

  static TypeDefinition FindType(string name) {
    return asm.MainModule.Types.FirstOrDefault(t => t.Name == name);
  }

  static void Dump(MethodDefinition m) {
    string safe = m.DeclaringType.Name.Replace("`", "_") + "_" + m.Name;
    if (m.Parameters.Count > 0)
      safe += "_" + string.Join("_", m.Parameters.Select(p => p.ParameterType.Name.Replace("`", "_").Replace("<", "_").Replace(">", "_")));
    if (safe.Length > 120) safe = safe.Substring(0, 120);
    var sb = new StringBuilder();
    sb.AppendLine("# " + m.DeclaringType.FullName + "::" + m.Name);
    sb.AppendLine("IL=" + m.Body.Instructions.Count);
    sb.AppendLine();
    var list = new List<string>();
    foreach (var i in m.Body.Instructions) {
      if (i.OpCode.Code == Code.Call || i.OpCode.Code == Code.Callvirt) {
        var mr = i.Operand as MethodReference;
        if (mr != null) list.Add(mr.DeclaringType.Name + "::" + mr.Name);
      }
    }
    sb.AppendLine("## Frequency");
    foreach (var g in list.GroupBy(x => x).OrderByDescending(g => g.Count()).Take(40))
      sb.AppendLine("- " + g.Count() + "x `" + g.Key + "`");
    sb.AppendLine();
    sb.AppendLine("## Ordered");
    int n = 0;
    foreach (var c in list) {
      sb.AppendLine((++n) + ". `" + c + "`");
      if (n >= 100) { sb.AppendLine("..."); break; }
    }
    File.WriteAllText(Path.Combine(outDir, safe + "_calls.md"), sb.ToString());
    if (m.Body.Instructions.Count <= 500) {
      var il = new StringBuilder();
      il.AppendLine("// " + m.DeclaringType.FullName + "::" + m.Name + " IL=" + m.Body.Instructions.Count);
      foreach (var i in m.Body.Instructions) {
        string op = i.Operand == null ? "" : " " + (i.Operand is Instruction t ? "IL_" + t.Offset.ToString("X4") : i.Operand.ToString().Replace("\n", " "));
        il.AppendLine("IL_" + i.Offset.ToString("X4") + ": " + i.OpCode.Name + op);
      }
      File.WriteAllText(Path.Combine(outDir, safe + "_il.txt"), il.ToString());
    }
  }

  static void Summarize(string type, string method) {
    var t = FindType(type);
    if (t == null) { book.AppendLine("MISSING " + type + "::" + method); return; }
    foreach (var m in t.Methods.Where(m => m.HasBody && m.Name == method)) {
      book.AppendLine("### " + type + "::" + method + " IL=" + m.Body.Instructions.Count);
      var list = new List<string>();
      foreach (var i in m.Body.Instructions) {
        if (i.OpCode.Code == Code.Call || i.OpCode.Code == Code.Callvirt) {
          var mr = i.Operand as MethodReference;
          if (mr != null) list.Add(mr.DeclaringType.Name + "::" + mr.Name);
        }
      }
      foreach (var g in list.GroupBy(x => x).OrderByDescending(g => g.Count()).Take(25))
        book.AppendLine("- " + g.Count() + "x `" + g.Key + "`");
      Dump(m);
      book.AppendLine();
    }
  }
}
