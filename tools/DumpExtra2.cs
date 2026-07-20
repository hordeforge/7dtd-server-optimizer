using System; using System.IO; using System.Linq; using Mono.Cecil; using Mono.Cecil.Cil;
class P {
 static void Main(string[] a) {
  var r=new DefaultAssemblyResolver(); r.AddSearchDirectory(Path.GetDirectoryName(a[0]));
  var asm=AssemblyDefinition.ReadAssembly(a[0], new ReaderParameters{AssemblyResolver=r});
  void Dump(string tn, string mn) {
    var t=asm.MainModule.Types.FirstOrDefault(x=>x.Name==tn);
    if (t==null) { Console.WriteLine("missing "+tn); return; }
    foreach (var m in t.Methods.Where(m=>m.HasBody && (mn==null || m.Name==mn || m.Name.Contains(mn)))) {
      Console.WriteLine("### "+tn+"::"+m.Name+" IL="+m.Body.Instructions.Count+" ret="+m.ReturnType.Name);
      int c=0;
      foreach (var i in m.Body.Instructions) {
        Console.WriteLine("  IL_"+i.Offset.ToString("X4")+": "+i.OpCode.Name+" "+(i.Operand!=null?i.Operand.ToString():""));
        if (++c>80) { Console.WriteLine("  ..."); break; }
      }
    }
  }
  Dump("HeightMap", "GetAt");
  Dump("Entity", "OriginChanged");
  Dump("ChunkManager", "OriginChanged");
  Console.WriteLine("--- RegionFileRaw methods ---");
  var t=asm.MainModule.Types.First(x=>x.Name=="RegionFileRaw");
  foreach (var m in t.Methods.Where(m=>m.HasBody).OrderByDescending(m=>m.Body.Instructions.Count).Take(25))
    Console.WriteLine(m.Name+" IL="+m.Body.Instructions.Count+" ret="+m.ReturnType.Name+" ("+string.Join(",",m.Parameters.Select(p=>p.ParameterType.Name))+")");
  Console.WriteLine("--- Chunk write/read ---");
  var ch=asm.MainModule.Types.First(x=>x.Name=="Chunk");
  foreach (var m in ch.Methods.Where(m=>m.HasBody && (m.Name.Contains("Write")||m.Name.Contains("Read")||m.Name.Contains("Save")||m.Name.Contains("Load"))).OrderByDescending(m=>m.Body.Instructions.Count).Take(20))
    Console.WriteLine("Chunk::"+m.Name+" IL="+m.Body.Instructions.Count);
  Console.WriteLine("--- WorldState ---");
  var ws=asm.MainModule.Types.First(x=>x.Name=="WorldState");
  foreach (var m in ws.Methods.Where(m=>m.HasBody).OrderByDescending(m=>m.Body.Instructions.Count).Take(15))
    Console.WriteLine("WorldState::"+m.Name+" IL="+m.Body.Instructions.Count);
  // EXT constant on snapshot
  var snap=asm.MainModule.Types.FirstOrDefault(x=>x.Name=="RegionFileChunkSnapshot");
  if (snap!=null) foreach (var f in snap.Fields) Console.WriteLine("Snap F "+f.Name+" "+(f.HasConstant?f.Constant:"")+" "+f.FieldType.Name);
  var rfr=asm.MainModule.Types.FirstOrDefault(x=>x.Name=="RegionFileRaw");
  if (rfr!=null) foreach (var f in rfr.Fields.Take(20)) Console.WriteLine("RFR F "+f.Name+" "+f.FieldType.Name+(f.HasConstant?" ="+f.Constant:""));
 }
}
