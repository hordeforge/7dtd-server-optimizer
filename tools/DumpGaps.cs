using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Mono.Cecil;
using Mono.Cecil.Cil;

class DumpGaps {
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
    book.AppendLine("# Gap-closing RE notes (V3.0.1)");
    book.AppendLine();
    book.AppendLine("UTC: " + DateTime.UtcNow.ToString("u"));
    book.AppendLine();

    // --- GameTimer fields / cctor / Reset ---
    Section("1. GameTimer fields and initialization");
    DumpTypeFields("GameTimer");
    DumpMethods("GameTimer", new[] { ".ctor", "updateTimer", "Reset", "get_Instance", "Instance" });
    // field initial values via cctor of owning type
    DumpStaticInit("GameTimer");

    // --- AIDirector construction / component add ---
    Section("2. AIDirector construction and component registration");
    DumpMethods("AIDirector", new[] { ".ctor", "Init", "Create", "Add", "Tick", "ComponentsTick", "Cleanup" });
    XrefNewobj("AIDirector");
    XrefNewobj("AIDirectorBloodMoonComponent");
    XrefNewobj("AIDirectorChunkEventComponent");
    XrefNewobj("AIDirectorWanderingHordeComponent");
    XrefNewobj("AIDirectorAirDropComponent");
    XrefNewobj("AIDirectorPlayerManagementComponent");
    XrefCallers("AIDirector", ".ctor");
    XrefCallers("AIDirector", "Tick");
    // list fields of AIDirector
    DumpTypeFields("AIDirector");

    // --- ASPPathNavigate GetPathTo / pathFollow ---
    Section("3. ASPPathNavigate path compute");
    DumpMethods("ASPPathNavigate", new[] { "GetPathTo", "pathFollow", "CreatePath", "UpdateNavigation", "SetPath" });
    DumpMethods("PathNavigate", new[] { "GetPathTo", "UpdateNavigation", "SetPath", "noPath" });
    DumpMethods("ASPPathFinder", new[] { "Calculate", "FindPath", "Search", "GetPath" });
    // any PathFinder type
    foreach (var t in asm.MainModule.Types.Where(t => t.Name.Contains("PathFinder") || t.Name.Contains("Pathfinding") || t.Name == "PathFinder")) {
      book.AppendLine("### type `" + t.FullName + "` base=" + (t.BaseType != null ? t.BaseType.Name : "?"));
      foreach (var m in t.Methods.Where(m => m.HasBody).OrderByDescending(m => m.Body.Instructions.Count).Take(12))
        book.AppendLine("- `" + m.Name + "` IL=" + m.Body.Instructions.Count);
      book.AppendLine();
    }

    // --- Net package band mapping: dump updatePlayerList with branch annotation ---
    Section("4. NetEntityDistributionEntry.updatePlayerList structure");
    AnnotateMethod("NetEntityDistributionEntry", "updatePlayerList");
    AnnotateMethod("NetEntityDistributionEntry", "updatePlayerEntity");
    AnnotateMethod("NetEntityDistributionEntry", "EncodePos");
    AnnotateMethod("NetEntityDistributionEntry", "EncodeRot");

    // --- Entity activity on dedicated: enabled, isEntityRemote, OnAddedToWorld ---
    Section("5. Entity dedicated activity signals");
    DumpMethods("Entity", new[] { "OnAddedToWorld", "OnEntityUpdate", "SetDead", "updateTransform", "Update", "FixedUpdate" });
    DumpTypeFields("Entity");
    // search for enabled = false / set_enabled on entities in spawn
    XrefField("isEntityRemote");
    XrefField("bWillRespawn");
    AnnotateMethod("EntityFactory", "CreateEntity");
    // EntityCreationData
    foreach (var t in asm.MainModule.Types.Where(t => t.Name.Contains("CreateEntity") || t.Name == "EntityFactory" || t.Name == "EntityCreationData")) {
      book.AppendLine("### `" + t.Name + "`");
      foreach (var m in t.Methods.Where(m => m.HasBody && m.Body.Instructions.Count > 20).OrderByDescending(m => m.Body.Instructions.Count).Take(8))
        book.AppendLine("- `" + m.Name + "` IL=" + m.Body.Instructions.Count);
    }

    // Who sets Behaviour.enabled on entities?
    Section("5b. set_enabled / SetActive near Entity spawn");
    int n = 0;
    foreach (var t in asm.MainModule.Types) {
      foreach (var m in t.Methods.Where(m => m.HasBody)) {
        bool mentionsEntity = m.Name.IndexOf("Spawn", StringComparison.OrdinalIgnoreCase) >= 0
          || m.Name.IndexOf("CreateEntity", StringComparison.OrdinalIgnoreCase) >= 0
          || t.Name.Contains("EntityFactory") || t.Name.Contains("World");
        if (!mentionsEntity && t.Name != "Entity" && t.Name != "EntityAlive") continue;
        foreach (var i in m.Body.Instructions) {
          var mr = i.Operand as MethodReference;
          if (mr == null) continue;
          if (mr.Name == "set_enabled" || mr.Name == "SetActive" || mr.Name == "set_isEntityRemote") {
            book.AppendLine("- `" + t.Name + "::" + m.Name + "` -> `" + mr.DeclaringType.Name + "::" + mr.Name + "`");
            if (++n > 40) goto done_en;
            break;
          }
        }
      }
    }
    done_en:

    // --- ProtocolManager ---
    Section("6. ProtocolManager / net stack");
    DumpTypeFields("ProtocolManager");
    DumpMethods("ProtocolManager", new[] { "Update", "LateUpdate", "StartServer", "StartClient", "Send", "Process" });
    DumpMethods("ConnectionManager", new[] { "Update", "ProcessPackages", "SendPackage", "FlushClientSendQueues" });
    // LiteNetLib types present?
    book.AppendLine("### Types containing LiteNet or NetManager");
    foreach (var t in asm.MainModule.Types.Where(t => t.FullName.IndexOf("LiteNet", StringComparison.OrdinalIgnoreCase) >= 0
        || t.Name.Contains("NetManager") || t.Name.Contains("NetPeer")).Take(30))
      book.AppendLine("- `" + t.FullName + "`");

    // --- AntiCheat ---
    Section("7. AntiCheat / EAC surface");
    foreach (var t in asm.MainModule.Types.Where(t => t.Name.IndexOf("AntiCheat", StringComparison.OrdinalIgnoreCase) >= 0
        || t.Name.IndexOf("EAC", StringComparison.OrdinalIgnoreCase) >= 0
        || t.Name.IndexOf("EasyAnti", StringComparison.OrdinalIgnoreCase) >= 0).Take(40)) {
      book.AppendLine("### `" + t.FullName + "`");
      foreach (var m in t.Methods.Where(m => m.HasBody).OrderByDescending(m => m.Body.Instructions.Count).Take(10))
        book.AppendLine("- `" + m.Name + "` IL=" + m.Body.Instructions.Count);
    }

    // --- Client-only classification heuristic ---
    Section("8. MonoBehaviour Update classification (heuristic)");
    ClassifyMBs();

    // --- World.Init / GameManager StartAsServer path ---
    Section("9. Server start path (AIDirector / Astar / managers)");
    DumpMethods("GameManager", new[] { "StartAsServer", "StartGame", "createWorld", "loadWorld", "Awake" });
    XrefCallers("AstarManager", "Init");
    XrefCallers("AIDirector", ".ctor");

    File.WriteAllText(Path.Combine(outDir, "GAPS_CLOSED.md"), book.ToString());
    Console.WriteLine("Wrote GAPS_CLOSED.md size=" + new FileInfo(Path.Combine(outDir, "GAPS_CLOSED.md")).Length);
  }

  static void Section(string t) { book.AppendLine(); book.AppendLine("## " + t); book.AppendLine(); }

  static TypeDefinition Find(string name) {
    return asm.MainModule.Types.FirstOrDefault(t => t.Name == name);
  }

  static void DumpTypeFields(string name) {
    var t = Find(name);
    if (t == null) { book.AppendLine("MISSING " + name); return; }
    book.AppendLine("### Fields of `" + name + "`");
    foreach (var f in t.Fields.OrderBy(f => f.Name))
      book.AppendLine("- `" + f.Name + "` : " + f.FieldType.FullName + (f.HasConstant ? " = " + f.Constant : "") + (f.IsStatic ? " [static]" : ""));
    book.AppendLine();
  }

  static void DumpStaticInit(string name) {
    var t = Find(name);
    if (t == null) return;
    foreach (var m in t.Methods.Where(m => m.Name == ".cctor" || m.Name == ".ctor")) {
      if (!m.HasBody) continue;
      book.AppendLine("### `" + name + "::" + m.Name + "` IL=" + m.Body.Instructions.Count);
      foreach (var i in m.Body.Instructions) {
        if (i.OpCode.Code == Code.Ldc_R4 || i.OpCode.Code == Code.Ldc_I4 || i.OpCode.Code == Code.Ldc_R8
            || i.OpCode.Code == Code.Stsfld || i.OpCode.Code == Code.Stfld || i.OpCode.Code == Code.Call || i.OpCode.Code == Code.Callvirt) {
          book.AppendLine("- IL_" + i.Offset.ToString("X4") + " " + i.OpCode.Name + " " + (i.Operand != null ? i.Operand.ToString() : ""));
        }
      }
      DumpFull(m);
    }
  }

  static void DumpMethods(string type, string[] names) {
    var t = Find(type);
    if (t == null) {
      // partial match
      t = asm.MainModule.Types.FirstOrDefault(x => x.Name == type || x.Name.EndsWith(type));
    }
    if (t == null) { book.AppendLine("MISSING type " + type); return; }
    foreach (var want in names) {
      foreach (var m in t.Methods.Where(m => m.HasBody && (m.Name == want || m.Name.IndexOf(want.TrimStart('.'), StringComparison.OrdinalIgnoreCase) >= 0 || (want == ".ctor" && m.IsConstructor)))) {
        book.AppendLine("- dump `" + t.Name + "::" + m.Name + "` IL=" + m.Body.Instructions.Count);
        DumpFull(m);
        // call summary
        var calls = new List<string>();
        foreach (var i in m.Body.Instructions) {
          if (i.OpCode.Code == Code.Call || i.OpCode.Code == Code.Callvirt || i.OpCode.Code == Code.Newobj) {
            var mr = i.Operand as MethodReference;
            if (mr != null) calls.Add((i.OpCode.Code == Code.Newobj ? "new " : "") + mr.DeclaringType.Name + "::" + mr.Name);
          }
        }
        foreach (var g in calls.GroupBy(c => c).OrderByDescending(g => g.Count()).Take(20))
          book.AppendLine("  - " + g.Count() + "x `" + g.Key + "`");
      }
    }
  }

  static void AnnotateMethod(string type, string method) {
    var t = Find(type);
    if (t == null) { book.AppendLine("MISSING " + type); return; }
    foreach (var m in t.Methods.Where(m => m.HasBody && m.Name == method)) {
      book.AppendLine("### Annotated `" + type + "::" + method + "` IL=" + m.Body.Instructions.Count);
      book.AppendLine();
      book.AppendLine("```");
      foreach (var i in m.Body.Instructions) {
        bool interesting =
          i.OpCode.Code == Code.Call || i.OpCode.Code == Code.Callvirt || i.OpCode.Code == Code.Newobj
          || i.OpCode.FlowControl == FlowControl.Cond_Branch || i.OpCode.FlowControl == FlowControl.Branch
          || i.OpCode.FlowControl == FlowControl.Return
          || i.OpCode.Code == Code.Ldc_R4 || i.OpCode.Code == Code.Ldc_I4 || i.OpCode.Code == Code.Ldc_I4_S
          || i.OpCode.Code == Code.Ldfld || i.OpCode.Code == Code.Ldsfld;
        if (!interesting) continue;
        string op = i.Operand == null ? "" : " " + OpStr(i);
        book.AppendLine("IL_" + i.Offset.ToString("X4") + ": " + i.OpCode.Name + op);
      }
      book.AppendLine("```");
      book.AppendLine();
      DumpFull(m);
    }
  }

  static string OpStr(Instruction i) {
    var t = i.Operand as Instruction;
    if (t != null) return "IL_" + t.Offset.ToString("X4");
    var ts = i.Operand as Instruction[];
    if (ts != null) return string.Join(",", Array.ConvertAll(ts, x => "IL_" + x.Offset.ToString("X4")));
    return i.Operand.ToString().Replace("\n", " ");
  }

  static void DumpFull(MethodDefinition m) {
    string safe = (m.DeclaringType.Name + "_" + m.Name).Replace("`", "_").Replace("<", "_").Replace(">", "_").Replace("/", "_");
    if (m.Parameters.Count > 0)
      safe += "_" + string.Join("_", m.Parameters.Select(p => p.ParameterType.Name.Replace("`", "_").Replace("<", "_").Replace(">", "_")));
    if (safe.Length > 120) safe = safe.Substring(0, 120);
    if (m.Body.Instructions.Count > 800) return; // skip huge
    var il = new StringBuilder();
    il.AppendLine("// " + m.DeclaringType.FullName + "::" + m.Name + " IL=" + m.Body.Instructions.Count);
    foreach (var i in m.Body.Instructions) {
      string op = i.Operand == null ? "" : " " + OpStr(i);
      il.AppendLine("IL_" + i.Offset.ToString("X4") + ": " + i.OpCode.Name + op);
    }
    File.WriteAllText(Path.Combine(outDir, safe + "_il.txt"), il.ToString());
  }

  static void XrefNewobj(string typeName) {
    book.AppendLine("#### newobj `" + typeName + "`");
    int n = 0;
    foreach (var t in asm.MainModule.Types) {
      foreach (var m in t.Methods.Where(m => m.HasBody)) {
        foreach (var i in m.Body.Instructions) {
          var mr = i.Operand as MethodReference;
          if (mr != null && i.OpCode.Code == Code.Newobj && mr.DeclaringType.Name == typeName) {
            book.AppendLine("- `" + t.Name + "::" + m.Name + "`");
            if (++n > 25) return;
            break;
          }
        }
      }
    }
  }

  static void XrefCallers(string type, string method) {
    book.AppendLine("#### callers of `" + type + "::" + method + "`");
    int n = 0;
    foreach (var t in asm.MainModule.Types) {
      foreach (var m in t.Methods.Where(m => m.HasBody)) {
        foreach (var i in m.Body.Instructions) {
          var mr = i.Operand as MethodReference;
          if (mr == null) continue;
          bool match = mr.DeclaringType.Name == type && (mr.Name == method || (method == ".ctor" && mr.Name == ".ctor"));
          if (!match) continue;
          book.AppendLine("- `" + t.Name + "::" + m.Name + "`");
          if (++n > 20) return;
          break;
        }
      }
    }
  }

  static void XrefField(string field) {
    book.AppendLine("#### field `" + field + "`");
    foreach (var t in asm.MainModule.Types) {
      foreach (var m in t.Methods.Where(m => m.HasBody)) {
        foreach (var i in m.Body.Instructions) {
          var fr = i.Operand as FieldReference;
          if (fr != null && fr.Name == field) {
            book.AppendLine("- `" + t.Name + "::" + m.Name + "` " + i.OpCode.Name);
            break;
          }
        }
      }
    }
  }

  static bool ClassifyIsMB(TypeDefinition t) {
    TypeReference b = t.BaseType; int g = 0;
    while (b != null && g++ < 20) {
      if (b.Name == "MonoBehaviour" || b.Name.StartsWith("SingletonMonoBehaviour")) return true;
      try { var r = b.Resolve(); if (r == null) break; b = r.BaseType; } catch { break; }
    }
    return false;
  }

  static void ClassifyMBs() {
    string[] dediHints = new string[] {
      "GameManager","ConnectionManager","DynamicMeshManager","SdtdConsole","Origin","WorldEnvironment",
      "AstarManager","Entity","SkyManager","EnvironmentAudio","WaterEvaporation","AutoTurret","MiniTurret",
      "MotionSensor","SpinningBlade","WireNode","ElectricWire","Spotlight","HazardDamage","SelectionBox"
    };
    string[] clientHints = new string[] {
      "Player","Local","XUi","NGui","GUI","UI","vp_","Camera","Avatar","Cursor","Menu","FPS","Demo",
      "Screen","Render","Shader","LightLOD","Reflection","Muzzle","Crosshair","HUD","NGSS","SoftCursor",
      "FlexibleCursor","MainMenu","CharacterGaze","EyeLid","Feather","LagPosition","Billboard"
    };
    var dedi = new List<string>();
    var client = new List<string>();
    var unk = new List<string>();
    foreach (var t in asm.MainModule.Types) {
      if (!ClassifyIsMB(t)) continue;
      bool has = t.Methods.Any(m => m.HasBody && (m.Name == "Update" || m.Name == "LateUpdate" || m.Name == "FixedUpdate") && m.Parameters.Count == 0);
      if (!has) continue;
      string n = t.Name;
      bool d = dediHints.Any(h => n.IndexOf(h, StringComparison.OrdinalIgnoreCase) >= 0);
      bool c = clientHints.Any(h => n.IndexOf(h, StringComparison.OrdinalIgnoreCase) >= 0);
      string line = "`" + n + "`";
      if (d && !c) dedi.Add(line);
      else if (c && !d) client.Add(line);
      else if (d && c) unk.Add(line + " (both hints)");
      else unk.Add(line);
    }
    book.AppendLine("### Likely dedicated-relevant (" + dedi.Count + ")");
    foreach (var x in dedi.OrderBy(x => x)) book.AppendLine("- " + x);
    book.AppendLine();
    book.AppendLine("### Likely client/editor (" + client.Count + ")");
    foreach (var x in client.OrderBy(x => x).Take(80)) book.AppendLine("- " + x);
    if (client.Count > 80) book.AppendLine("- ... +" + (client.Count - 80));
    book.AppendLine();
    book.AppendLine("### Unclassified / mixed (" + unk.Count + ")");
    foreach (var x in unk.OrderBy(x => x)) book.AppendLine("- " + x);
  }
}
