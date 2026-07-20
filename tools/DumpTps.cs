
using System; using System.IO; using System.Linq; using Mono.Cecil; using Mono.Cecil.Cil;
class P {
 static void Main(string[] a) {
  var r=new DefaultAssemblyResolver(); r.AddSearchDirectory(Path.GetDirectoryName(a[0]));
  var asm=AssemblyDefinition.ReadAssembly(a[0], new ReaderParameters{AssemblyResolver=r});
  var t=asm.MainModule.Types.First(x=>x.Name=="GameTimer");
  foreach (var m in t.Methods.Where(m=>m.HasBody && (m.IsConstructor || m.Name.Contains("Reset")))) {
    Console.WriteLine("=== "+m.Name+" IL="+m.Body.Instructions.Count);
    foreach (var i in m.Body.Instructions)
      Console.WriteLine("IL_"+i.Offset.ToString("X4")+": "+i.OpCode.Name+" "+(i.Operand!=null?i.Operand:""));
  }
  // ASPPathFinder Calculate summary - AstarPath.StartPath
  var pf=asm.MainModule.Types.First(x=>x.Name=="ASPPathFinder");
  var calc=pf.Methods.First(x=>x.Name=="Calculate");
  Console.WriteLine("=== ASPPathFinder.Calculate calls ===");
  foreach (var i in calc.Body.Instructions) {
    if (i.OpCode.Code==Code.Call || i.OpCode.Code==Code.Callvirt || i.OpCode.Code==Code.Newobj)
      Console.WriteLine("  "+i.Operand);
  }
 }
}
