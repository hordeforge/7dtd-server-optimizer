using System; using System.IO; using System.Linq; using Mono.Cecil; using Mono.Cecil.Cil;
class P {
 static void Main(string[] a) {
  var r=new DefaultAssemblyResolver(); r.AddSearchDirectory(Path.GetDirectoryName(a[0]));
  var asm=AssemblyDefinition.ReadAssembly(a[0], new ReaderParameters{AssemblyResolver=r});
  // region subtypes
  foreach (var t in asm.MainModule.Types.Where(t=>t.BaseType!=null && (
    t.BaseType.Name.Contains("Region") || t.Name.Contains("RegionFile"))).OrderBy(t=>t.FullName))
    Console.WriteLine("TYPE "+t.FullName+" base="+t.BaseType.FullName+" methods="+t.Methods.Count(m=>m.HasBody));
  Console.WriteLine("--- TerrainFromRaw methods IL ---");
  var tr=asm.MainModule.Types.First(t=>t.Name=="TerrainFromRaw");
  foreach (var m in tr.Methods.Where(m=>m.HasBody)) {
    Console.WriteLine("### "+m.Name+"("+string.Join(",",m.Parameters.Select(p=>p.ParameterType.Name))+") IL="+m.Body.Instructions.Count+" ret="+m.ReturnType.Name);
    foreach (var i in m.Body.Instructions) {
      if (m.Body.Instructions.Count>80 && i.OpCode.Code!=Code.Call && i.OpCode.Code!=Code.Callvirt && i.OpCode.Code!=Code.Ldfld && i.OpCode.Code!=Code.Ldc_I4 && i.OpCode.Code!=Code.Ldc_I4_S && i.OpCode.Code!=Code.Ret && i.OpCode.FlowControl!=FlowControl.Cond_Branch) continue;
      Console.WriteLine("  IL_"+i.Offset.ToString("X4")+": "+i.OpCode.Name+" "+(i.Operand!=null?i.Operand.ToString():""));
    }
  }
  Console.WriteLine("--- HeightMap methods ---");
  var hm=asm.MainModule.Types.FirstOrDefault(t=>t.Name=="HeightMap");
  if (hm!=null) {
    foreach (var f in hm.Fields) Console.WriteLine("  F "+f.Name+" "+f.FieldType.Name+(f.HasConstant?" ="+f.Constant:""));
    foreach (var m in hm.Methods.Where(m=>m.HasBody).OrderByDescending(m=>m.Body.Instructions.Count).Take(20))
      Console.WriteLine("  M "+m.Name+" IL="+m.Body.Instructions.Count+" ret="+m.ReturnType.Name);
  }
  Console.WriteLine("--- Entity.OriginChanged ---");
  foreach (var t in asm.MainModule.Types.Where(t=>t.Name=="Entity"||t.Name=="ChunkManager"||t.Name=="AstarManager")) {
    foreach (var m in t.Methods.Where(m=>m.Name.Contains("Origin")))
      Console.WriteLine(t.Name+"::"+m.Name+" IL="+(m.HasBody?m.Body.Instructions.Count:-1));
  }
  Console.WriteLine("--- WorldState Save/Load ---");
  var ws=asm.MainModule.Types.FirstOrDefault(t=>t.Name=="WorldState");
  if (ws!=null) foreach (var m in ws.Methods.Where(m=>m.HasBody && (m.Name.Contains("Save")||m.Name.Contains("Load")||m.Name.Contains("Write")||m.Name.Contains("Read"))))
    Console.WriteLine("WorldState::"+m.Name+" IL="+m.Body.Instructions.Count);
  Console.WriteLine("--- Chunk Write/Read top ---");
  var ch=asm.MainModule.Types.First(t=>t.Name=="Chunk");
  foreach (var m in ch.Methods.Where(m=>m.HasBody && (m.Name=="Write"||m.Name=="Read"||m.Name.StartsWith("write")||m.Name.StartsWith("read")||m.Name.Contains("Save"))).OrderByDescending(m=>m.Body.Instructions.Count).Take(15))
    Console.WriteLine("Chunk::"+m.Name+"("+string.Join(",",m.Parameters.Select(p=>p.ParameterType.Name))+") IL="+m.Body.Instructions.Count);
 }
}
