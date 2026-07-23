// Regenerable RE dump: surfaces RealEarth Streamed path depends on.
// Chunk storage, height/index math, claims/PPL, Origin, region files, prefab place,
// chunk gen/load pipeline, light/stability hooks, World height APIs.
// Output: 7dtd-research/il/realearth-surfaces-VERSION/
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Mono.Cecil;
using Mono.Cecil.Cil;

class DumpRealEarthSurfaces
{
  static AssemblyDefinition asm;
  static string outDir;
  static StringBuilder book;

  static void Main(string[] args)
  {
    if (args.Length < 2)
    {
      Console.Error.WriteLine("Usage: DumpRealEarthSurfaces.exe Assembly-CSharp.dll outDir");
      Environment.Exit(2);
    }
    var resolver = new DefaultAssemblyResolver();
    resolver.AddSearchDirectory(Path.GetDirectoryName(args[0]));
    asm = AssemblyDefinition.ReadAssembly(args[0], new ReaderParameters { AssemblyResolver = resolver });
    outDir = args[1];
    Directory.CreateDirectory(outDir);
    book = new StringBuilder();
    book.AppendLine("# RealEarth critical surfaces dump (auto)");
    book.AppendLine();
    book.AppendLine("UTC: " + DateTime.UtcNow.ToString("u"));
    book.AppendLine("Assembly: `" + args[0] + "`");
    book.AppendLine();
    book.AppendLine("Regenerate:");
    book.AppendLine("```");
    book.AppendLine("mcs -r:Mono.Cecil.dll -out:DumpRealEarthSurfaces.exe DumpRealEarthSurfaces.cs");
    book.AppendLine("mono DumpRealEarthSurfaces.exe $ASM 7dtd-research/il/realearth-surfaces-VERSION");
    book.AppendLine("```");
    book.AppendLine();
    book.AppendLine("Narrative: `7days-realworld/docs/realearth-surfaces.md`");
    book.AppendLine();

    Section("1. Type inventory (name contains keywords)");
    var keywords = new[] {
      "Chunk", "Terrain", "Height", "Density", "Region", "Persistent", "LandClaim",
      "LandProtection", "Origin", "Prefab", "GenerateTerrain", "ChunkProvider",
      "Stability", "Light", "WorldState", "Save", "NavObject", "MapObject"
    };
    var matched = new HashSet<string>(StringComparer.Ordinal);
    foreach (var t in asm.MainModule.Types.OrderBy(t => t.FullName))
    {
      bool hit = keywords.Any(k => t.Name.IndexOf(k, StringComparison.OrdinalIgnoreCase) >= 0);
      if (!hit) continue;
      matched.Add(t.FullName);
      int methods = t.Methods.Count(m => m.HasBody);
      book.AppendLine("- `" + t.FullName + "` fields=" + t.Fields.Count + " methods_with_body=" + methods
        + " base=" + (t.BaseType != null ? t.BaseType.Name : "?"));
    }
    book.AppendLine();
    book.AppendLine("Matched types: " + matched.Count);

    // Core types: full field+method inventory
    string[] core = {
      "Chunk", "ChunkBlockLayer", "ChunkBlockChannel", "ChunkCluster", "ChunkManager",
      "World", "WorldConstants", "WorldState", "Origin",
      "PersistentPlayerList", "PersistentPlayerData", "GameManager",
      "RegionFile", "RegionFileAccess", "RegionFileManager", "RegionFileSection",
      "RegionFileChunkSnapshot", "RegionFileAccessAbstract",
      "PrefabManager", "Prefab", "PrefabInstance", "DynamicPrefabDecorator",
      "ChunkProviderGenerateWorld", "ChunkProviderGenerateWorldFromRaw",
      "TerrainGeneratorWithBiomeResource", "TerrainFromRaw", "TerrainFromDTM",
      "IChunkProvider", "ITerrainGenerator",
      "MeshGeneratorMC2", "StabilityCalculator", "StabilityInitializer",
      "LightingAround", "LightProcessor", "ILightProcessor",
      "NavObjectManager", "NavObject", "MapObjectManager", "MapObject"
    };

    Section("2. Core type fields and method IL sizes");
    foreach (var name in core)
    {
      var t = FindType(name);
      if (t == null) { book.AppendLine("### `" + name + "` **NOT FOUND**"); book.AppendLine(); continue; }
      DumpTypeSummary(t);
    }

    // Method dumps for critical paths
    Section("3. Critical method dumps (IL + calls)");
    DumpMethods("Chunk", new[] {
      "GetBlock", "SetBlock", "SetBlockRaw", "GetDensity", "SetDensity", "SetDensityRaw",
      "GetTerrainHeight", "SetTerrainHeight", "GetTopMostTerrainHeight",
      "GetHeight", "SetHeight", "OnLoad", "Write", "read", "Read", "GetBlockId",
      "GetLight", "SetLight", "GetStability", "SetStability", "NeedsRegeneration",
      "GetBlockFace", "FillBlockRaw", "IsEmpty"
    });
    DumpMethods("ChunkBlockLayer", new[] {
      "Get", "Set", "GetAt", "SetAt", "GetId", "SetId", "Alloc", "Free", "Read", "Write"
    });
    DumpMethods("ChunkBlockChannel", new[] {
      "Get", "Set", "GetData", "SetData", "Read", "Write", "Fill"
    });
    DumpMethods("ChunkCluster", new[] {
      "GetBlock", "SetBlock", "SetBlockRaw", "GetDensity", "SetDensity", "SetDensityRaw",
      "LightChunk", "CalcStability", "RegenerateChunk", "AddChunkSync", "GetTerrainHeight"
    });
    DumpMethods("World", new[] {
      "GetTerrainHeight", "GetHeightAt", "GetBlock", "SetBlock", "SetBlockRPC",
      "GetChunkFromWorldPos", "GetChunkSync", "m_ChunkManager",
      "GetDensity", "SetBlockAndDensity", "GetLandClaimOwner", "IsLandProtectionValid",
      "GetGameManager", "TickEntities", "OnUpdateTick", "SaveWorldState", "LoadWorld"
    });
    DumpMethods("Origin", new[] {
      "FixedUpdate", "Reposition", "DoReposition", "UpdateLocalPlayer", "Add", "Remove"
    });
    DumpMethods("PersistentPlayerList", new[] {
      "PlaceLandProtectionBlock", "RemoveLandProtectionBlock", "GetLandProtectionBlockOwner",
      "GetPlayerData", "GetPlayerDataFromEntityID", "MapPlayer", "UnmapPlayer",
      "Write", "Read", "Cleanup", "RemoveExtraLandClaims"
    });
    DumpMethods("PersistentPlayerData", new[] {
      "AddLandProtectionBlock", "RemoveLandProtectionBlock", "GetLandProtectionBlocks",
      "SetPosition", "get_Position", "Write", "Read"
    });
    DumpMethods("GameManager", new[] {
      "GetPersistentPlayerList", "get_persistentPlayers", "UpdateTick", "gmUpdate",
      "SaveLocalPlayerData", "SaveWorld", "StartAsServer", "createWorld"
    });
    DumpMethods("RegionFile", new[] {
      "ReadData", "WriteData", "HasChunk", "GetChunkByteCount", "RemoveChunk",
      "SaveHeaderData", "ConstructFullFilePath", "GetTimestampInfo", "OptimizeLayout"
    });
    DumpMethods("RegionFileManager", new[] {
      "GetChunk", "SaveChunk", "LoadChunk", "MakeChunk", "Update", "Cleanup", "Write"
    });
    DumpMethods("ChunkProviderGenerateWorld", new[] {
      "generateTerrain", "GenerateSingleChunk", "GetTerrainHeightAt", "Update",
      "RequestChunk", "ProvideChunk", "Init"
    });
    DumpMethods("TerrainGeneratorWithBiomeResource", new[] {
      "GenerateTerrain", "GetTerrainHeightAt", "GetTerrainHeightByteAt"
    });
    DumpMethods("Prefab", new[] {
      "CopyIntoLocal", "CopyBlocksIntoWorld", "placePrefab", "PlacePrefab",
      "GetTerrainHeight", "setPosition", "get_size", "Load", "RotateY"
    });
    DumpMethods("PrefabInstance", new[] {
      "CopyIntoLocal", "UpdatePosition", "setPosition", "GetBoundingBox"
    });
    DumpMethods("NavObjectManager", new[] {
      "RegisterNavObject", "UnRegisterNavObject", "Update", "Refresh"
    });
    DumpMethods("MapObjectManager", new[] {
      "Add", "Remove", "Update", "Clear"
    });

    // Index / mask usage in Chunk.GetBlock / SetBlock / density
    Section("4. Indexing analysis: masks and YDim in Chunk block/density methods");
    AnalyzeIndexing("Chunk", new[] { "GetBlock", "SetBlock", "SetBlockRaw", "GetDensity", "SetDensity", "SetDensityRaw", "GetTerrainHeight", "SetTerrainHeight" });
    AnalyzeIndexing("ChunkBlockLayer", null); // all small methods
    AnalyzeIndexing("WorldConstants", null);

    Section("5. Literal scan: 255/256/16384/YMask in Chunk and World height/block paths");
    ScanLiterals(new[] { "Chunk", "World", "ChunkCluster", "TerrainFromRaw", "TerrainFromDTM",
      "TerrainGeneratorWithBiomeResource", "MeshGeneratorMC2", "ChunkProviderGenerateWorldFromRaw" },
      new[] { 255, 256, 16383, 16384, 4095, 4096, 15, 16 });

    Section("6. Origin.FixedUpdate / Reposition call graph");
    DumpCallGraph("Origin", "FixedUpdate");
    DumpCallGraph("Origin", "DoReposition");
    DumpCallGraph("Origin", "Reposition");

    Section("7. Land claim call graph");
    DumpCallGraph("PersistentPlayerList", "PlaceLandProtectionBlock");
    DumpCallGraph("PersistentPlayerList", "GetLandProtectionBlockOwner");
    DumpCallGraph("PersistentPlayerList", "RemoveLandProtectionBlock");

    Section("8. RegionFile Read/Write call graph");
    DumpCallGraph("RegionFile", "ReadData");
    DumpCallGraph("RegionFile", "WriteData");
    DumpCallGraph("RegionFile", "SaveHeaderData");

    Section("9. GenerateTerrain entry call graph");
    DumpCallGraph("ChunkProviderGenerateWorld", "generateTerrain");
    DumpCallGraph("TerrainGeneratorWithBiomeResource", "GenerateTerrain");

    Section("10. World.GetTerrainHeight / GetHeightAt");
    DumpCallGraph("World", "GetTerrainHeight");
    DumpCallGraph("World", "GetHeightAt");

    Section("11. Chunk save version constants");
    var chunk = FindType("Chunk");
    if (chunk != null)
    {
      foreach (var f in chunk.Fields.Where(f => f.Name.IndexOf("Save", StringComparison.OrdinalIgnoreCase) >= 0
        || f.Name.IndexOf("Version", StringComparison.OrdinalIgnoreCase) >= 0
        || f.Name.StartsWith("c")))
      {
        string lit = f.HasConstant ? " = " + f.Constant : "";
        book.AppendLine("- `" + f.Name + "` : " + f.FieldType.Name + lit + (f.IsStatic ? " [static]" : "") + (f.IsLiteral ? " [literal]" : ""));
      }
    }

    Section("12. GameManager persistent player accessors");
    var gm = FindType("GameManager");
    if (gm != null)
    {
      foreach (var m in gm.Methods.Where(m =>
        m.Name.IndexOf("Persistent", StringComparison.OrdinalIgnoreCase) >= 0
        || m.Name.IndexOf("LandClaim", StringComparison.OrdinalIgnoreCase) >= 0
        || m.Name.IndexOf("LandProtection", StringComparison.OrdinalIgnoreCase) >= 0
        || m.Name == "GetPersistentPlayerList"))
      {
        book.AppendLine("- `" + m.Name + "(" + SigParams(m) + ")` ret=" + m.ReturnType.Name
          + " IL=" + (m.HasBody ? m.Body.Instructions.Count.ToString() : "abstract"));
        if (m.HasBody) DumpMethodFiles(gm, m);
      }
      foreach (var f in gm.Fields.Where(f =>
        f.Name.IndexOf("persistent", StringComparison.OrdinalIgnoreCase) >= 0
        || f.Name.IndexOf("Persistent", StringComparison.OrdinalIgnoreCase) >= 0))
        book.AppendLine("- field `" + f.Name + "` : " + f.FieldType.FullName);
    }

    Section("13. Chunk heightmap field types (byte vs int)");
    if (chunk != null)
    {
      foreach (var f in chunk.Fields.Where(f =>
        f.Name.IndexOf("Height", StringComparison.OrdinalIgnoreCase) >= 0
        || f.Name.IndexOf("Terrain", StringComparison.OrdinalIgnoreCase) >= 0
        || f.Name.IndexOf("Biome", StringComparison.OrdinalIgnoreCase) >= 0
        || f.Name.IndexOf("Density", StringComparison.OrdinalIgnoreCase) >= 0
        || f.Name.IndexOf("Layer", StringComparison.OrdinalIgnoreCase) >= 0
        || f.Name.IndexOf("Light", StringComparison.OrdinalIgnoreCase) >= 0
        || f.Name.IndexOf("Stability", StringComparison.OrdinalIgnoreCase) >= 0))
        book.AppendLine("- `" + f.Name + "` : " + f.FieldType.FullName);
    }

    Section("14. World.GetHeightAt overloads and callers sample");
    DumpAllNamed("GetHeightAt");
    DumpAllNamed("GetTerrainHeight");
    DumpAllNamed("GetPersistentPlayerList");
    DumpAllNamed("PlaceLandProtectionBlock");

    File.WriteAllText(Path.Combine(outDir, "REALEARTH_SURFACES_auto.md"), book.ToString());
    File.WriteAllText(Path.Combine(outDir, "INDEX.md"),
      "# RealEarth surfaces dump index\n\n"
      + "Auto narrative: `REALEARTH_SURFACES_auto.md`\n\n"
      + "Human synthesis: **[`../../../7days-realworld/docs/realearth-surfaces.md`](../../../7days-realworld/docs/realearth-surfaces.md)**\n\n"
      + "UTC: " + DateTime.UtcNow.ToString("u") + "\n");
    File.WriteAllText(Path.Combine(outDir, "README.md"),
      "# Raw IL dump set: realearth-surfaces\n\n"
      + "Surfaces RealEarth Streamed inject/session/slide depends on.\n\n"
      + "Human: [`../../../7days-realworld/docs/realearth-surfaces.md`](../../../7days-realworld/docs/realearth-surfaces.md)\n\n"
      + "Regenerable Cecil only. Do not redistribute game assemblies.\n");
    Console.WriteLine("OK → " + outDir);
  }

  static TypeDefinition FindType(string name)
  {
    return asm.MainModule.Types.FirstOrDefault(t => t.Name == name)
      ?? asm.MainModule.Types.SelectMany(t => t.NestedTypes).FirstOrDefault(t => t.Name == name);
  }

  static void Section(string title)
  {
    book.AppendLine();
    book.AppendLine("## " + title);
    book.AppendLine();
  }

  static string SigParams(MethodDefinition m)
  {
    return string.Join(",", m.Parameters.Select(p => p.ParameterType.Name));
  }

  static void DumpTypeSummary(TypeDefinition t)
  {
    book.AppendLine("### `" + t.FullName + "` base=" + (t.BaseType != null ? t.BaseType.FullName : "?"));
    book.AppendLine();
    book.AppendLine("Fields (" + t.Fields.Count + "):");
    foreach (var f in t.Fields.Take(80))
    {
      string lit = f.HasConstant ? " = " + f.Constant : "";
      book.AppendLine("- `" + f.Name + "` : " + f.FieldType.Name + lit
        + (f.IsStatic ? " [static]" : "") + (f.IsLiteral ? " [literal]" : ""));
    }
    if (t.Fields.Count > 80) book.AppendLine("- … +" + (t.Fields.Count - 80) + " more");
    book.AppendLine();
    book.AppendLine("Methods (body, by IL desc, top 40):");
    foreach (var m in t.Methods.Where(m => m.HasBody)
      .OrderByDescending(m => m.Body.Instructions.Count).Take(40))
      book.AppendLine("- `" + m.Name + "(" + SigParams(m) + ")` IL=" + m.Body.Instructions.Count
        + " ret=" + m.ReturnType.Name + (m.IsVirtual ? " virtual" : ""));
    book.AppendLine();
  }

  static void DumpMethods(string typeName, string[] names)
  {
    var t = FindType(typeName);
    if (t == null) { book.AppendLine("- type `" + typeName + "` missing"); return; }
    book.AppendLine("### " + typeName);
    var set = new HashSet<string>(names);
    foreach (var m in t.Methods.Where(m => m.HasBody && (
      set.Contains(m.Name)
      || names.Any(n => m.Name.Equals(n, StringComparison.OrdinalIgnoreCase)
        || m.Name.StartsWith(n, StringComparison.Ordinal)))))
    {
      book.AppendLine("- `" + m.Name + "(" + SigParams(m) + ")` IL=" + m.Body.Instructions.Count
        + " ret=" + m.ReturnType.Name);
      DumpMethodFiles(t, m);
    }
    book.AppendLine();
  }

  static void AnalyzeIndexing(string typeName, string[] methodFilter)
  {
    var t = FindType(typeName);
    if (t == null) { book.AppendLine("- `" + typeName + "` missing"); return; }
    IEnumerable<MethodDefinition> methods = t.Methods.Where(m => m.HasBody);
    if (methodFilter != null)
      methods = methods.Where(m => methodFilter.Any(f => m.Name.IndexOf(f, StringComparison.OrdinalIgnoreCase) >= 0));
    foreach (var m in methods.OrderBy(m => m.Name).Take(30))
    {
      var lits = new SortedSet<int>();
      var fieldRefs = new SortedSet<string>();
      var calls = new List<string>();
      foreach (var i in m.Body.Instructions)
      {
        if (i.OpCode.Code == Code.Ldc_I4 && i.Operand is int iv) lits.Add(iv);
        if (i.OpCode.Code == Code.Ldc_I4_S && i.Operand is sbyte sb) lits.Add(sb);
        if (i.Operand is FieldReference fr) fieldRefs.Add(fr.DeclaringType.Name + "." + fr.Name);
        if ((i.OpCode.Code == Code.Call || i.OpCode.Code == Code.Callvirt) && i.Operand is MethodReference mr)
          calls.Add(mr.DeclaringType.Name + "::" + mr.Name);
      }
      book.AppendLine("#### `" + typeName + "::" + m.Name + "(" + SigParams(m) + ")` IL=" + m.Body.Instructions.Count
        + " ret=" + m.ReturnType.Name);
      if (lits.Count > 0)
        book.AppendLine("- literals: " + string.Join(", ", lits.Where(x => Math.Abs(x) < 100000).Take(40)));
      if (fieldRefs.Count > 0)
        book.AppendLine("- fields: " + string.Join(", ", fieldRefs.Take(25)));
      if (calls.Count > 0)
        book.AppendLine("- calls: " + string.Join(", ", calls.Distinct().Take(25)));
      book.AppendLine();
    }
  }

  static void ScanLiterals(string[] typeNames, int[] interesting)
  {
    var want = new HashSet<int>(interesting);
    foreach (var tn in typeNames)
    {
      var t = FindType(tn);
      if (t == null) continue;
      foreach (var m in t.Methods.Where(m => m.HasBody && m.Body.Instructions.Count < 800))
      {
        var found = new SortedSet<int>();
        foreach (var i in m.Body.Instructions)
        {
          int? v = null;
          if (i.OpCode.Code == Code.Ldc_I4 && i.Operand is int iv) v = iv;
          else if (i.OpCode.Code == Code.Ldc_I4_S && i.Operand is sbyte sb) v = sb;
          if (v.HasValue && want.Contains(v.Value)) found.Add(v.Value);
        }
        if (found.Count == 0) continue;
        bool nameHit = m.Name.IndexOf("Height", StringComparison.OrdinalIgnoreCase) >= 0
          || m.Name.IndexOf("Block", StringComparison.OrdinalIgnoreCase) >= 0
          || m.Name.IndexOf("Density", StringComparison.OrdinalIgnoreCase) >= 0
          || m.Name.IndexOf("Terrain", StringComparison.OrdinalIgnoreCase) >= 0
          || m.Name.IndexOf("Layer", StringComparison.OrdinalIgnoreCase) >= 0
          || m.Name.IndexOf("Light", StringComparison.OrdinalIgnoreCase) >= 0
          || m.Name.IndexOf("Write", StringComparison.OrdinalIgnoreCase) >= 0
          || m.Name.IndexOf("Read", StringComparison.OrdinalIgnoreCase) >= 0;
        if (!nameHit && found.All(x => x == 15 || x == 16)) continue;
        book.AppendLine("- `" + tn + "::" + m.Name + "` IL=" + m.Body.Instructions.Count
          + " lits=[" + string.Join(",", found) + "]");
      }
    }
  }

  static void DumpCallGraph(string typeName, string methodName)
  {
    var t = FindType(typeName);
    if (t == null) { book.AppendLine("- `" + typeName + "` missing"); return; }
    var methods = t.Methods.Where(m => m.HasBody && m.Name == methodName).ToList();
    if (methods.Count == 0)
    {
      // try startswith
      methods = t.Methods.Where(m => m.HasBody && m.Name.StartsWith(methodName, StringComparison.Ordinal)).ToList();
    }
    if (methods.Count == 0) { book.AppendLine("- `" + typeName + "::" + methodName + "` not found"); return; }
    foreach (var m in methods)
    {
      book.AppendLine("### `" + typeName + "::" + m.Name + "(" + SigParams(m) + ")` IL=" + m.Body.Instructions.Count
        + " ret=" + m.ReturnType.Name);
      int c = 0;
      foreach (var i in m.Body.Instructions)
      {
        if (i.OpCode.Code != Code.Call && i.OpCode.Code != Code.Callvirt && i.OpCode.Code != Code.Newobj
          && i.OpCode.FlowControl != FlowControl.Cond_Branch && i.OpCode.FlowControl != FlowControl.Branch)
          continue;
        book.AppendLine("- IL_" + i.Offset.ToString("X4") + ": " + i.OpCode.Name + " `"
          + (i.Operand != null ? i.Operand.ToString() : "") + "`");
        if (++c > 100) { book.AppendLine("- … truncated"); break; }
      }
      book.AppendLine();
      DumpMethodFiles(t, m);
    }
  }

  static void DumpAllNamed(string methodName)
  {
    book.AppendLine("### All `" + methodName + "`");
    int n = 0;
    foreach (var t in asm.MainModule.Types.OrderBy(t => t.Name))
    {
      foreach (var m in t.Methods.Where(m => m.Name == methodName))
      {
        book.AppendLine("- `" + t.Name + "::" + m.Name + "(" + SigParams(m) + ")` ret="
          + m.ReturnType.Name + " IL=" + (m.HasBody ? m.Body.Instructions.Count.ToString() : "?")
          + (m.IsVirtual ? " virtual" : "") + (m.IsAbstract ? " abstract" : ""));
        if (m.HasBody) DumpMethodFiles(t, m);
        if (++n > 60) { book.AppendLine("… truncated"); return; }
      }
    }
    book.AppendLine();
  }

  static void DumpMethodFiles(TypeDefinition t, MethodDefinition m)
  {
    if (!m.HasBody) return;
    string safe = Sanitize(t.Name + "_" + m.Name + "_" + string.Join("_", m.Parameters.Select(p => p.ParameterType.Name)));
    if (safe.Length > 140) safe = safe.Substring(0, 140);
    var il = new StringBuilder();
    il.AppendLine("// " + t.FullName + "::" + m.Name);
    il.AppendLine("// ret=" + m.ReturnType.FullName + " IL=" + m.Body.Instructions.Count);
    foreach (var i in m.Body.Instructions)
      il.AppendLine("IL_" + i.Offset.ToString("X4") + ": " + i.OpCode.Name + " "
        + (i.Operand != null ? i.Operand.ToString() : ""));
    File.WriteAllText(Path.Combine(outDir, safe + "_il.txt"), il.ToString());
    var calls = new StringBuilder();
    calls.AppendLine("# " + t.Name + "::" + m.Name + "(" + SigParams(m) + ")");
    calls.AppendLine();
    calls.AppendLine("IL=" + m.Body.Instructions.Count + " ret=" + m.ReturnType.Name);
    calls.AppendLine();
    int c = 0;
    foreach (var i in m.Body.Instructions)
    {
      if (i.OpCode.Code != Code.Call && i.OpCode.Code != Code.Callvirt && i.OpCode.Code != Code.Newobj) continue;
      calls.AppendLine("- " + i.OpCode.Name + " `" + i.Operand + "`");
      if (++c > 120) break;
    }
    File.WriteAllText(Path.Combine(outDir, safe + "_calls.md"), calls.ToString());
  }

  static string Sanitize(string s)
  {
    var sb = new StringBuilder();
    foreach (char ch in s)
    {
      if (char.IsLetterOrDigit(ch) || ch == '_' || ch == '.') sb.Append(ch);
      else sb.Append('_');
    }
    return sb.ToString();
  }
}
