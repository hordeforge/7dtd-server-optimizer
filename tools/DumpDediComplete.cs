// Complete dedicated managed surface inventory + residual-closing dumps
using System; using System.Collections.Generic; using System.IO; using System.Linq; using System.Text;
using Mono.Cecil; using Mono.Cecil.Cil;
class DumpDediComplete {
  static AssemblyDefinition asm; static string outDir; static StringBuilder book;
  static void Main(string[] a) {
    var r=new DefaultAssemblyResolver(); r.AddSearchDirectory(Path.GetDirectoryName(a[0]));
    asm=AssemblyDefinition.ReadAssembly(a[0], new ReaderParameters{AssemblyResolver=r});
    outDir=a[1]; Directory.CreateDirectory(outDir);
    book=new StringBuilder();
    book.AppendLine("# Dedicated complete surfaces dump (auto)");
    book.AppendLine("UTC: "+DateTime.UtcNow.ToString("u"));
    book.AppendLine("Assembly: `"+a[0]+"`");
    book.AppendLine();

    // 1. WorldConstants stock pin
    Section("1. WorldConstants vertical pin (live)");
    var wc=Find("WorldConstants");
    if (wc!=null) foreach (var f in wc.Fields.Where(f=>f.HasConstant && (f.Name.Contains("Y")||f.Name.Contains("Layer")||f.Name.Contains("Dim")||f.Name.Contains("Mask"))))
      book.AppendLine("- `"+f.Name+"` = "+f.Constant);

    // 2. ModEvents fields/methods
    Section("2. ModEvents surface");
    foreach (var t in asm.MainModule.Types.Where(t=>t.Name.IndexOf("ModEvent",StringComparison.OrdinalIgnoreCase)>=0).OrderBy(t=>t.Name)) {
      book.AppendLine("### `"+t.FullName+"`");
      foreach (var f in t.Fields) book.AppendLine("- F `"+f.Name+"` : "+f.FieldType.Name);
      foreach (var m in t.Methods.Where(m=>m.HasBody).OrderBy(m=>m.Name))
        book.AppendLine("- M `"+m.Name+"("+Sig(m)+")` IL="+m.Body.Instructions.Count);
      book.AppendLine();
    }

    // 3. NetPackage type inventory
    Section("3. NetPackage* types (name + largest method IL)");
    int n=0;
    foreach (var t in asm.MainModule.Types.Where(t=>t.Name.StartsWith("NetPackage")||t.Name.Contains("NetPackage")).OrderBy(t=>t.Name)) {
      int maxIl=t.Methods.Where(m=>m.HasBody).Select(m=>m.Body.Instructions.Count).DefaultIfEmpty(0).Max();
      book.AppendLine("- `"+t.Name+"` base="+t.BaseType?.Name+" methods="+t.Methods.Count(m=>m.HasBody)+" maxIL="+maxIl);
      if (++n>250) { book.AppendLine("… truncated"); break; }
    }

    // 4. ConnectionManager / Protocol / Network
    Section("4. Connection / Protocol / Network type map");
    foreach (var name in new[]{"ConnectionManager","ProtocolManager","NetPackageManager","NetEntityDistribution","NetEntityDistributionEntry","ClientInfo","INetConnection","NetworkConnectionLiteNetLib","NetworkServerLiteNetLib","NetworkClientLiteNetLib","LiteNetLibConnectionManager"}) {
      var t=Find(name);
      if (t==null) { book.AppendLine("- `"+name+"` NOT FOUND"); continue; }
      book.AppendLine("### `"+t.FullName+"` base="+t.BaseType?.Name);
      foreach (var m in t.Methods.Where(m=>m.HasBody).OrderByDescending(m=>m.Body.Instructions.Count).Take(20))
        book.AppendLine("- `"+m.Name+"("+Sig(m)+")` IL="+m.Body.Instructions.Count);
      DumpMethods(t, new[]{"Update","LateUpdate","ProcessPackages","SendPackage","FlushClientSendQueues","UpdatePings","OnUpdateEntities","updatePlayerList"});
      book.AppendLine();
    }

    // 5. WorldState full field list + SaveLoad analysis
    Section("5. WorldState fields + SaveLoad call analysis");
    var ws=Find("WorldState");
    if (ws!=null) {
      foreach (var f in ws.Fields) book.AppendLine("- F `"+f.Name+"` : "+f.FieldType.Name+(f.HasConstant?" ="+f.Constant:"")+(f.IsStatic?" [static]":""));
      foreach (var m in ws.Methods.Where(m=>m.HasBody && (m.Name.Contains("Save")||m.Name.Contains("Load")||m.Name.Contains("SetFrom")||m.Name.Contains("ReadWrite")))) {
        book.AppendLine("#### `"+m.Name+"("+Sig(m)+")` IL="+m.Body.Instructions.Count);
        Analyze(m);
        DumpMethod(ws, m);
      }
    }

    // 6. ChunkManager pipeline
    Section("6. ChunkManager / DetermineChunks / SendChunks");
    foreach (var name in new[]{"ChunkManager","ChunkCluster","ChunkProviderGenerateWorld","ChunkProviderAbstract"}) {
      var t=Find(name);
      if (t==null) continue;
      book.AppendLine("### `"+name+"`");
      foreach (var m in t.Methods.Where(m=>m.HasBody && (
        m.Name.IndexOf("Chunk",StringComparison.OrdinalIgnoreCase)>=0
        || m.Name.IndexOf("Load",StringComparison.OrdinalIgnoreCase)>=0
        || m.Name.IndexOf("Unload",StringComparison.OrdinalIgnoreCase)>=0
        || m.Name.IndexOf("Send",StringComparison.OrdinalIgnoreCase)>=0
        || m.Name.IndexOf("Determine",StringComparison.OrdinalIgnoreCase)>=0
        || m.Name.IndexOf("Origin",StringComparison.OrdinalIgnoreCase)>=0
        || m.Name.IndexOf("Update",StringComparison.OrdinalIgnoreCase)>=0
        || m.Name.IndexOf("Save",StringComparison.OrdinalIgnoreCase)>=0
        || m.Name.IndexOf("Generate",StringComparison.OrdinalIgnoreCase)>=0
      )).OrderByDescending(m=>m.Body.Instructions.Count).Take(25)) {
        book.AppendLine("- `"+m.Name+"("+Sig(m)+")` IL="+m.Body.Instructions.Count);
        if (m.Body.Instructions.Count<400) DumpMethod(t, m);
      }
      book.AppendLine();
    }

    // 7. Water / light / stability / mesh
    Section("7. Water / Light / Stability / Mesh");
    foreach (var name in new[]{"WaterSimulationNative","WaterEvaporationManager","WaterSplashCubes","LightProcessor","LightingAround","GameLightManager","StabilityCalculator","StabilityInitializer","MultiBlockManager","DynamicMeshManager","DynamicMeshServer","MeshDataManager","MeshGeneratorMC2","DecoManager","WorldBlockTicker"}) {
      var t=Find(name);
      if (t==null) { book.AppendLine("- `"+name+"` NOT FOUND"); continue; }
      book.AppendLine("### `"+name+"` base="+t.BaseType?.Name);
      foreach (var m in t.Methods.Where(m=>m.HasBody).OrderByDescending(m=>m.Body.Instructions.Count).Take(12))
        book.AppendLine("- `"+m.Name+"("+Sig(m)+")` IL="+m.Body.Instructions.Count+(m.IsVirtual?" virtual":""));
      DumpMethods(t, new[]{"Update","UpdateTick","LateUpdate","Tick","LightChunk","CalcStability","RegenerateChunk","RefreshSunlight","MainThreadUpdate"});
      book.AppendLine();
    }

    // 8. Entity tick chain sizes
    Section("8. Entity authority tick chain IL sizes");
    foreach (var pair in new[]{
      new[]{"World","TickEntities"},new[]{"World","TickEntitiesSlice"},new[]{"World","TickEntity"},
      new[]{"World","OnUpdateTick"},new[]{"GameManager","UpdateTick"},new[]{"GameManager","gmUpdate"},
      new[]{"Entity","OnUpdateEntity"},new[]{"EntityAlive","OnUpdateEntity"},new[]{"EntityAlive","OnUpdateLive"},
      new[]{"EntityAlive","updateTasks"},new[]{"Entity","Update"},new[]{"EntityAlive","Update"},
      new[]{"EntityMoveHelper","UpdateMoveHelper"},new[]{"EAIManager","Update"},new[]{"EAITaskBase","Update"},
    }) {
      var t=Find(pair[0]); if (t==null) continue;
      foreach (var m in t.Methods.Where(m=>m.HasBody && m.Name==pair[1]))
        book.AppendLine("- `"+pair[0]+"::"+m.Name+"("+Sig(m)+")` IL="+m.Body.Instructions.Count);
    }

    // 9. EAC / Platform types
    Section("9. AntiCheat / Platform type map");
    foreach (var t in asm.MainModule.Types.Where(t=>
      t.FullName.IndexOf("AntiCheat",StringComparison.OrdinalIgnoreCase)>=0
      || t.Name.IndexOf("EAC",StringComparison.OrdinalIgnoreCase)>=0
      || (t.Name.Contains("Platform") && t.Name.Contains("Server"))
    ).OrderBy(t=>t.FullName).Take(40))
      book.AppendLine("- `"+t.FullName+"` methods="+t.Methods.Count(m=>m.HasBody));

    // 10. Origin FixedUpdate first instructions (dedi gate)
    Section("10. Origin.FixedUpdate dedicated gate");
    var orig=Find("Origin");
    if (orig!=null) {
      var m=orig.Methods.First(x=>x.Name=="FixedUpdate" && x.HasBody);
      book.AppendLine("IL="+m.Body.Instructions.Count);
      for (int i=0;i<Math.Min(15,m.Body.Instructions.Count);i++) {
        var ins=m.Body.Instructions[i];
        book.AppendLine("- IL_"+ins.Offset.ToString("X4")+": "+ins.OpCode.Name+" "+(ins.Operand!=null?ins.Operand.ToString():""));
      }
      book.AppendLine("Interpretation: first call IsDedicatedServer; brtrue → ret ⇒ **no-op on dedicated**.");
      DumpMethod(orig, m);
    }

    // 11. Managers from gmUpdate inventory
    Section("11. gmUpdate manager Update IL sizes");
    foreach (var name in new[]{"PowerManager","VehicleManager","DroneManager","QuestEventManager","GameEventManager","PartyManager","DismembermentManager","TurretTracker","FactionManager","TriggerManager","NavObjectManager","TokenManager","ThreadManager","EntityAsyncManager","TwitchManager","BlockedPlayerList","PrefabLODManager","TrajectoryManager","RaycastPathManager","SpeedTreeWindManager","TriggerEffectManager"}) {
      var t=Find(name);
      if (t==null) { book.AppendLine("- `"+name+"` missing"); continue; }
      var upd=t.Methods.Where(m=>m.HasBody && m.Name=="Update").OrderByDescending(m=>m.Body.Instructions.Count).FirstOrDefault();
      book.AppendLine("- `"+name+"::Update` IL="+(upd!=null?upd.Body.Instructions.Count.ToString():"none"));
    }

    // 12. Chunk density channel write
    Section("12. ChunkBlockChannel Read/Write analysis");
    var cbc=Find("ChunkBlockChannel");
    if (cbc!=null) foreach (var m in cbc.Methods.Where(m=>m.HasBody && (m.Name=="Write"||m.Name=="Read"))) {
      book.AppendLine("#### `"+m.Name+"` IL="+m.Body.Instructions.Count);
      Analyze(m);
      DumpMethod(cbc, m);
    }

    // 13. Type count summary
    Section("13. Assembly type census");
    int total=asm.MainModule.Types.Count();
    int withBody=asm.MainModule.Types.Sum(t=>t.Methods.Count(m=>m.HasBody));
    book.AppendLine("- top-level types: "+total);
    book.AppendLine("- methods with body: "+withBody);
    book.AppendLine("- NetPackage* types: "+asm.MainModule.Types.Count(t=>t.Name.StartsWith("NetPackage")));

    File.WriteAllText(Path.Combine(outDir,"DEDI_COMPLETE_auto.md"), book.ToString());
    File.WriteAllText(Path.Combine(outDir,"INDEX.md"), "# dedi-complete dump\n\nAuto: DEDI_COMPLETE_auto.md\nNarrative: 7dtd-research/docs/coverage.md\n");
    File.WriteAllText(Path.Combine(outDir,"README.md"), "# dedi-complete-v3.0.1\n\nRegenerable. DumpDediComplete.cs\n");
    Console.WriteLine("OK types="+total+" methods="+withBody+" -> "+outDir);
  }
  static TypeDefinition Find(string n)=>asm.MainModule.Types.FirstOrDefault(t=>t.Name==n);
  static string Sig(MethodDefinition m)=>string.Join(",",m.Parameters.Select(p=>p.ParameterType.Name));
  static void Section(string t){book.AppendLine();book.AppendLine("## "+t);book.AppendLine();}
  static void Analyze(MethodDefinition m){
    var lits=new SortedSet<int>(); var fields=new SortedSet<string>(); var calls=new List<string>();
    foreach (var i in m.Body.Instructions) {
      if (i.OpCode.Code==Code.Ldc_I4 && i.Operand is int iv && Math.Abs(iv)<1000000) lits.Add(iv);
      if (i.Operand is FieldReference fr) fields.Add(fr.DeclaringType.Name+"."+fr.Name);
      if ((i.OpCode.Code==Code.Call||i.OpCode.Code==Code.Callvirt||i.OpCode.Code==Code.Newobj) && i.Operand is MethodReference mr)
        calls.Add(mr.DeclaringType.Name+"::"+mr.Name);
    }
    if (lits.Count>0) book.AppendLine("- lits: "+string.Join(", ",lits.Take(40)));
    if (fields.Count>0) book.AppendLine("- fields: "+string.Join(", ",fields.Take(40)));
    if (calls.Count>0) book.AppendLine("- calls: "+string.Join(", ",calls.Distinct().Take(40)));
  }
  static void DumpMethods(TypeDefinition t, string[] names){
    foreach (var m in t.Methods.Where(m=>m.HasBody && names.Any(n=>m.Name==n || m.Name.StartsWith(n))))
      DumpMethod(t,m);
  }
  static void DumpMethod(TypeDefinition t, MethodDefinition m){
    string safe=San(t.Name+"_"+m.Name+"_"+string.Join("_",m.Parameters.Select(p=>p.ParameterType.Name)));
    if (safe.Length>140) safe=safe.Substring(0,140);
    var il=new StringBuilder(); il.AppendLine("// "+t.Name+"::"+m.Name+" IL="+m.Body.Instructions.Count);
    foreach (var i in m.Body.Instructions) il.AppendLine("IL_"+i.Offset.ToString("X4")+": "+i.OpCode.Name+" "+(i.Operand!=null?i.Operand.ToString():""));
    File.WriteAllText(Path.Combine(outDir,safe+"_il.txt"), il.ToString());
    var c=new StringBuilder(); c.AppendLine("# "+t.Name+"::"+m.Name+" IL="+m.Body.Instructions.Count);
    foreach (var i in m.Body.Instructions)
      if (i.OpCode.Code==Code.Call||i.OpCode.Code==Code.Callvirt||i.OpCode.Code==Code.Newobj)
        c.AppendLine("- "+i.Operand);
    File.WriteAllText(Path.Combine(outDir,safe+"_calls.md"), c.ToString());
  }
  static string San(string s){var sb=new StringBuilder();foreach(char ch in s){if(char.IsLetterOrDigit(ch)||ch=='_')sb.Append(ch);else sb.Append('_');}return sb.ToString();}
}
