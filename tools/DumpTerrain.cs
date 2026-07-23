// Regenerable RE: WorldConstants vertical dims + terrain height / generate surfaces.
// Output: 7dtd-research/il/terrain-VERSION/ (raw) + feeds 7dtd-research/docs/terrain-height.md
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Mono.Cecil;
using Mono.Cecil.Cil;

class DumpTerrain
{
  static AssemblyDefinition asm;
  static string outDir;
  static StringBuilder book;

  static void Main(string[] args)
  {
    if (args.Length < 2)
    {
      Console.Error.WriteLine("Usage: DumpTerrain.exe Assembly-CSharp.dll outDir");
      Environment.Exit(2);
    }
    var resolver = new DefaultAssemblyResolver();
    resolver.AddSearchDirectory(Path.GetDirectoryName(args[0]));
    asm = AssemblyDefinition.ReadAssembly(args[0], new ReaderParameters { AssemblyResolver = resolver });
    outDir = args[1];
    Directory.CreateDirectory(outDir);
    book = new StringBuilder();
    book.AppendLine("# Terrain / height engine dump (auto)");
    book.AppendLine();
    book.AppendLine("UTC: " + DateTime.UtcNow.ToString("u"));
    book.AppendLine("Assembly: `" + args[0] + "`");
    book.AppendLine();
    book.AppendLine("Regenerate: `mcs -r:Mono.Cecil.dll -out:DumpTerrain.exe DumpTerrain.cs && mono DumpTerrain.exe $ASM 7dtd-research/il/terrain-VERSION`");
    book.AppendLine();

    Section("1. WorldConstants and related literals");
    DumpTypeFields("WorldConstants");
    DumpTypeFields("ChunkProviderGenerateWorldFromRaw");
    // any type with ChunkBlockYDim field
    foreach (var t in asm.MainModule.Types.Where(t =>
      t.Fields.Any(f => f.Name.Contains("ChunkBlockY") || f.Name == "cMaxHeight" || f.Name.Contains("YDim"))))
    {
      if (t.Name == "WorldConstants") continue;
      book.AppendLine("### type `" + t.FullName + "`");
      foreach (var f in t.Fields.Where(f =>
        f.Name.IndexOf("Y", StringComparison.OrdinalIgnoreCase) >= 0
        || f.Name.Contains("Height") || f.Name.Contains("Layer")))
      {
        string lit = "";
        if (f.HasConstant) lit = " = " + f.Constant;
        book.AppendLine("- `" + f.Name + "` : " + f.FieldType.Name + lit + (f.IsStatic ? " [static]" : ""));
      }
      book.AppendLine();
    }

    Section("2. GetTerrainHeight* method inventory");
    int n = 0;
    foreach (var t in asm.MainModule.Types.OrderBy(t => t.FullName))
    {
      foreach (var m in t.Methods.Where(m => m.Name.IndexOf("TerrainHeight", StringComparison.OrdinalIgnoreCase) >= 0
        || m.Name.IndexOf("GetHeightAt", StringComparison.OrdinalIgnoreCase) >= 0
        || m.Name == "GetTerrainHeight"
        || m.Name == "GetTerrainHeightAt"
        || m.Name == "GetTerrainHeightByteAt"))
      {
        int il = m.HasBody ? m.Body.Instructions.Count : -1;
        book.AppendLine("- `" + t.Name + "::" + m.Name + "(" +
          string.Join(",", m.Parameters.Select(p => p.ParameterType.Name)) + ")` ret=" +
          m.ReturnType.Name + " IL=" + il + (m.IsVirtual ? " virtual" : "") +
          (m.IsAbstract ? " abstract" : ""));
        DumpMethodFiles(t, m);
        if (++n > 80) { book.AppendLine("… truncated"); goto done_h; }
      }
    }
    done_h:

    Section("3. GenerateTerrain / terrain provider methods");
    n = 0;
    foreach (var t in asm.MainModule.Types.Where(t =>
      t.Name.IndexOf("Terrain", StringComparison.OrdinalIgnoreCase) >= 0
      || t.Name.Contains("ChunkProvider")
      || t.Name == "World"
      || t.Name.Contains("WorldBuilder")))
    {
      foreach (var m in t.Methods.Where(m => m.HasBody && (
        m.Name.IndexOf("GenerateTerrain", StringComparison.OrdinalIgnoreCase) >= 0
        || m.Name.IndexOf("generateTerrain", StringComparison.OrdinalIgnoreCase) >= 0
        || m.Name == "FillOccupiedMap"
        || m.Name.IndexOf("TerrainHeight", StringComparison.OrdinalIgnoreCase) >= 0)))
      {
        book.AppendLine("- `" + t.Name + "::" + m.Name + "` IL=" + m.Body.Instructions.Count);
        DumpMethodFiles(t, m);
        if (++n > 40) { book.AppendLine("… truncated"); goto done_g; }
      }
    }
    done_g:

    Section("4. Chunk vertical storage types");
    foreach (var name in new[] { "Chunk", "ChunkBlockLayer", "ChunkBlockChannel", "UnsafeChunkData`1" })
    {
      var t = asm.MainModule.Types.FirstOrDefault(x => x.Name == name || x.Name.StartsWith(name.TrimEnd('1').TrimEnd('`')));
      // fuzzy
      t = asm.MainModule.Types.FirstOrDefault(x => x.Name == name);
      if (t == null && name.Contains("`"))
        t = asm.MainModule.Types.FirstOrDefault(x => x.Name.StartsWith("UnsafeChunkData"));
      if (t == null) { book.AppendLine("- missing `" + name + "`"); continue; }
      book.AppendLine("### `" + t.FullName + "` base=" + (t.BaseType != null ? t.BaseType.Name : "?"));
      foreach (var f in t.Fields.Take(40))
        book.AppendLine("- field `" + f.Name + "` : " + f.FieldType.Name);
      foreach (var m in t.Methods.Where(m => m.HasBody && (
        m.Name.Contains("Block") || m.Name.Contains("Density") || m.Name.Contains("Height")
        || m.Name.Contains("Layer") || m.Name == "GetBlock" || m.Name == "SetBlock"))
        .OrderByDescending(m => m.Body.Instructions.Count).Take(15))
        book.AppendLine("- method `" + m.Name + "` IL=" + m.Body.Instructions.Count);
      book.AppendLine();
    }

    Section("5. ldc.i4 256 / 255 near WorldConstants field names (sample)");
    // list methods that load 256 and call something height-related
    int hits = 0;
    foreach (var t in asm.MainModule.Types)
    {
      foreach (var m in t.Methods.Where(m => m.HasBody && m.Body.Instructions.Count < 400))
      {
        bool has256 = false, hasHeight = m.Name.IndexOf("Height", StringComparison.OrdinalIgnoreCase) >= 0
          || m.Name.IndexOf("Terrain", StringComparison.OrdinalIgnoreCase) >= 0
          || m.Name.IndexOf("Density", StringComparison.OrdinalIgnoreCase) >= 0;
        if (!hasHeight && !t.Name.Contains("Chunk") && t.Name != "World") continue;
        foreach (var i in m.Body.Instructions)
        {
          if (i.OpCode.Code == Code.Ldc_I4 && i.Operand is int iv && (iv == 256 || iv == 255))
            has256 = true;
          if (i.OpCode.Code == Code.Ldc_I4_S && i.Operand is sbyte sb && (sb == unchecked((sbyte)255) /* no */))
            { }
        }
        // simpler: any ldc 256
        has256 = m.Body.Instructions.Any(i =>
          (i.OpCode.Code == Code.Ldc_I4 && Equals(i.Operand, 256))
          || (i.OpCode.Code == Code.Ldc_I4 && Equals(i.Operand, 255)));
        if (has256 && hasHeight)
        {
          book.AppendLine("- `" + t.Name + "::" + m.Name + "` IL=" + m.Body.Instructions.Count + " (contains 255/256 literal)");
          if (++hits > 50) goto done_lit;
        }
      }
    }
    done_lit:

    File.WriteAllText(Path.Combine(outDir, "TERRAIN_auto.md"), book.ToString());
    File.WriteAllText(Path.Combine(outDir, "INDEX.md"),
      "# Terrain dump index\n\nAuto: `TERRAIN_auto.md` + per-method `*_il.txt` / `*_calls.md`.\n\nUTC: "
      + DateTime.UtcNow.ToString("u") + "\n");
    File.WriteAllText(Path.Combine(outDir, "README.md"),
      "# Raw IL dump set: `terrain-v3.0.1`\n\n"
      + "Human research notes: **[`../../docs/terrain-height.md`](../../docs/terrain-height.md)** "
      + "and RealEarth product docs under `7days-realworld/docs/`.\n\n"
      + "Regenerable Cecil outputs only. Do not redistribute game assemblies.\n");
    Console.WriteLine("OK → " + outDir + " methods-ish hits height=" + n + " lit=" + hits);
  }

  static void Section(string title)
  {
    book.AppendLine();
    book.AppendLine("## " + title);
    book.AppendLine();
  }

  static void DumpTypeFields(string typeName)
  {
    var t = asm.MainModule.Types.FirstOrDefault(x => x.Name == typeName);
    if (t == null) { book.AppendLine("- type `" + typeName + "` not found"); return; }
    book.AppendLine("### `" + t.FullName + "`");
    foreach (var f in t.Fields)
    {
      string lit = f.HasConstant ? " = " + f.Constant : "";
      book.AppendLine("- `" + f.Name + "` : " + f.FieldType.FullName + lit
        + (f.IsStatic ? " [static]" : "") + (f.IsLiteral ? " [literal]" : ""));
    }
    book.AppendLine();
  }

  static void DumpMethodFiles(TypeDefinition t, MethodDefinition m)
  {
    if (!m.HasBody) return;
    string safe = Sanitize(t.Name + "_" + m.Name + "_" + string.Join("_", m.Parameters.Select(p => p.ParameterType.Name)));
    if (safe.Length > 120) safe = safe.Substring(0, 120);
    var il = new StringBuilder();
    il.AppendLine("// " + t.FullName + "::" + m.FullName);
    il.AppendLine("// IL count " + m.Body.Instructions.Count);
    foreach (var i in m.Body.Instructions)
      il.AppendLine("IL_" + i.Offset.ToString("X4") + ": " + i.OpCode.Name + " " + (i.Operand != null ? i.Operand.ToString() : ""));
    File.WriteAllText(Path.Combine(outDir, safe + "_il.txt"), il.ToString());
    var calls = new StringBuilder();
    calls.AppendLine("# " + t.Name + "::" + m.Name);
    calls.AppendLine();
    calls.AppendLine("IL=" + m.Body.Instructions.Count);
    calls.AppendLine();
    int c = 0;
    foreach (var i in m.Body.Instructions)
    {
      if (i.OpCode.Code != Code.Call && i.OpCode.Code != Code.Callvirt && i.OpCode.Code != Code.Newobj) continue;
      calls.AppendLine("- " + i.OpCode.Name + " `" + i.Operand + "`");
      if (++c > 80) break;
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
