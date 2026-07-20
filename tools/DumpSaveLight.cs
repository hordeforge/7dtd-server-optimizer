using System; using System.Collections.Generic; using System.IO; using System.Linq; using System.Text; using Mono.Cecil; using Mono.Cecil.Cil;
class P {
 static void Main(string[] a) {
  var r=new DefaultAssemblyResolver(); r.AddSearchDirectory(Path.GetDirectoryName(a[0]));
  var asm=AssemblyDefinition.ReadAssembly(a[0], new ReaderParameters{AssemblyResolver=r});
  string outDir=a[1]; Directory.CreateDirectory(outDir);
  var book=new StringBuilder();
  book.AppendLine("# Chunk save/load + light/255 scan (auto)");
  book.AppendLine("UTC: "+DateTime.UtcNow.ToString("u"));
  book.AppendLine();

  // Chunk methods with Write/Read/Save/Load
  var ch=asm.MainModule.Types.First(t=>t.Name=="Chunk");
  book.AppendLine("## Chunk Write/Read/Save methods");
  foreach (var m in ch.Methods.Where(m=>m.HasBody && (
    m.Name.IndexOf("Write",StringComparison.OrdinalIgnoreCase)>=0
    || m.Name.IndexOf("Read",StringComparison.OrdinalIgnoreCase)>=0
    || m.Name.IndexOf("Save",StringComparison.OrdinalIgnoreCase)>=0
    || m.Name.IndexOf("Load",StringComparison.OrdinalIgnoreCase)>=0
    || m.Name.IndexOf("Serialize",StringComparison.OrdinalIgnoreCase)>=0
    || m.Name.IndexOf("Binary",StringComparison.OrdinalIgnoreCase)>=0
  )).OrderByDescending(m=>m.Body.Instructions.Count)) {
    book.AppendLine("- `"+m.Name+"("+string.Join(",",m.Parameters.Select(p=>p.ParameterType.Name))+")` IL="+m.Body.Instructions.Count+" ret="+m.ReturnType.Name);
    Dump(outDir, ch, m);
  }

  // Analyze largest Write/Read for WorldConstants and layer length
  book.AppendLine();
  book.AppendLine("## Chunk large serializers: field/literal analysis");
  foreach (var m in ch.Methods.Where(m=>m.HasBody && m.Body.Instructions.Count>50 && (
    m.Name.Contains("Write")||m.Name.Contains("Read")||m.Name.Contains("Save")||m.Name.Contains("Load")))) {
    var lits=new SortedSet<int>();
    var fields=new SortedSet<string>();
    var calls=new List<string>();
    foreach (var i in m.Body.Instructions) {
      if (i.OpCode.Code==Code.Ldc_I4 && i.Operand is int iv && Math.Abs(iv)<100000) lits.Add(iv);
      if (i.OpCode.Code==Code.Ldc_I4_S && i.Operand is sbyte sb) lits.Add(sb);
      if (i.Operand is FieldReference fr) fields.Add(fr.DeclaringType.Name+"."+fr.Name);
      if ((i.OpCode.Code==Code.Call||i.OpCode.Code==Code.Callvirt) && i.Operand is MethodReference mr)
        calls.Add(mr.DeclaringType.Name+"::"+mr.Name);
    }
    book.AppendLine("### `"+m.Name+"` IL="+m.Body.Instructions.Count);
    book.AppendLine("- lits: "+string.Join(", ", lits.Take(50)));
    book.AppendLine("- fields: "+string.Join(", ", fields.Take(40)));
    book.AppendLine("- calls: "+string.Join(", ", calls.Distinct().Take(40)));
    book.AppendLine();
  }

  // Light methods with 255/256
  book.AppendLine("## Methods with light/sun + 255/256 literal (Chunk/World/Light*)");
  int hits=0;
  foreach (var t in asm.MainModule.Types.Where(t=>
    t.Name.IndexOf("Light",StringComparison.OrdinalIgnoreCase)>=0
    || t.Name=="Chunk"||t.Name=="ChunkCluster"||t.Name=="World"
    || t.Name.IndexOf("Stability",StringComparison.OrdinalIgnoreCase)>=0
    || t.Name.IndexOf("Mesh",StringComparison.OrdinalIgnoreCase)>=0)) {
    foreach (var m in t.Methods.Where(m=>m.HasBody && m.Body.Instructions.Count<2000)) {
      bool hasLit=false; var found=new SortedSet<int>();
      foreach (var i in m.Body.Instructions) {
        int? v=null;
        if (i.OpCode.Code==Code.Ldc_I4 && i.Operand is int iv) v=iv;
        else if (i.OpCode.Code==Code.Ldc_I4_S && i.Operand is sbyte sb) v=sb;
        if (v==255||v==256||v==16383||v==16384) { hasLit=true; found.Add(v.Value); }
      }
      if (!hasLit) continue;
      bool nameHit=m.Name.IndexOf("Light",StringComparison.OrdinalIgnoreCase)>=0
        || m.Name.IndexOf("Sun",StringComparison.OrdinalIgnoreCase)>=0
        || m.Name.IndexOf("Height",StringComparison.OrdinalIgnoreCase)>=0
        || m.Name.IndexOf("Y",StringComparison.Ordinal)>=0
        || m.Name.IndexOf("Layer",StringComparison.OrdinalIgnoreCase)>=0
        || m.Name.IndexOf("Stab",StringComparison.OrdinalIgnoreCase)>=0
        || m.Name.IndexOf("Mesh",StringComparison.OrdinalIgnoreCase)>=0
        || m.Name.IndexOf("Regen",StringComparison.OrdinalIgnoreCase)>=0;
      if (!nameHit && !(t.Name.Contains("Light"))) continue;
      book.AppendLine("- `"+t.Name+"::"+m.Name+"` IL="+m.Body.Instructions.Count+" lits=["+string.Join(",",found)+"]");
      if (++hits>120) { book.AppendLine("… truncated"); goto done; }
    }
  }
  done:

  // RegionFileRaw methods inventory
  book.AppendLine();
  book.AppendLine("## RegionFileRaw methods");
  var rfr=asm.MainModule.Types.FirstOrDefault(t=>t.Name=="RegionFileRaw");
  if (rfr!=null) {
    foreach (var f in rfr.Fields) book.AppendLine("- F `"+f.Name+"` "+f.FieldType.Name+(f.HasConstant?" ="+f.Constant:""));
    foreach (var m in rfr.Methods.Where(m=>m.HasBody).OrderByDescending(m=>m.Body.Instructions.Count)) {
      book.AppendLine("- M `"+m.Name+"("+string.Join(",",m.Parameters.Select(p=>p.ParameterType.Name))+")` IL="+m.Body.Instructions.Count);
      Dump(outDir, rfr, m);
    }
  }

  // Entity.OriginChanged
  book.AppendLine();
  book.AppendLine("## Entity.OriginChanged");
  var ent=asm.MainModule.Types.First(t=>t.Name=="Entity");
  foreach (var m in ent.Methods.Where(m=>m.Name.Contains("Origin")||m.Name.Contains("position")||m.Name=="SetPosition")) {
    book.AppendLine("- `"+m.Name+"` IL="+(m.HasBody?m.Body.Instructions.Count.ToString():"?")+" ret="+m.ReturnType.Name);
    if (m.HasBody && m.Body.Instructions.Count<200) Dump(outDir, ent, m);
  }
  var cm=asm.MainModule.Types.FirstOrDefault(t=>t.Name=="ChunkManager");
  if (cm!=null) foreach (var m in cm.Methods.Where(m=>m.Name.Contains("Origin"))) {
    book.AppendLine("- ChunkManager::`"+m.Name+"` IL="+(m.HasBody?m.Body.Instructions.Count.ToString():"?"));
    if (m.HasBody) Dump(outDir, cm, m);
  }

  File.WriteAllText(Path.Combine(outDir, "SAVE_LIGHT_auto.md"), book.ToString());
  Console.WriteLine("OK hits="+hits+" -> "+outDir);
 }
 static void Dump(string outDir, TypeDefinition t, MethodDefinition m) {
  string safe=Sanitize(t.Name+"_"+m.Name+"_"+string.Join("_",m.Parameters.Select(p=>p.ParameterType.Name)));
  if (safe.Length>140) safe=safe.Substring(0,140);
  var il=new StringBuilder();
  il.AppendLine("// "+t.Name+"::"+m.Name+" IL="+m.Body.Instructions.Count);
  foreach (var i in m.Body.Instructions)
    il.AppendLine("IL_"+i.Offset.ToString("X4")+": "+i.OpCode.Name+" "+(i.Operand!=null?i.Operand.ToString():""));
  File.WriteAllText(Path.Combine(outDir, safe+"_il.txt"), il.ToString());
  var calls=new StringBuilder();
  calls.AppendLine("# "+t.Name+"::"+m.Name);
  foreach (var i in m.Body.Instructions) {
    if (i.OpCode.Code!=Code.Call&&i.OpCode.Code!=Code.Callvirt&&i.OpCode.Code!=Code.Newobj) continue;
    calls.AppendLine("- "+i.Operand);
  }
  File.WriteAllText(Path.Combine(outDir, safe+"_calls.md"), calls.ToString());
 }
 static string Sanitize(string s){var sb=new StringBuilder();foreach(char c in s){if(char.IsLetterOrDigit(c)||c=='_')sb.Append(c);else sb.Append('_');}return sb.ToString();}
}
