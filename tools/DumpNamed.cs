using System; using System.IO; using System.Linq; using Mono.Cecil; using Mono.Cecil.Cil;
class P {
 static void Main(string[] a) {
  var r=new DefaultAssemblyResolver(); r.AddSearchDirectory(Path.GetDirectoryName(a[0]));
  var asm=AssemblyDefinition.ReadAssembly(a[0], new ReaderParameters{AssemblyResolver=r});
  foreach (var tname in new[]{"PathFinderThread","ASPPathFinderThread","AStarPathFinderThread","EntityAlive"}) {
   var t=asm.MainModule.Types.First(x=>x.Name==tname);
   Console.WriteLine("=== "+tname+" base="+t.BaseType?.Name);
   foreach (var m in t.Methods.Where(m=>m.HasBody && (m.Name.IndexOf("Path")>=0||m.Name.IndexOf("Worker")>=0||m.Name=="GetPath"||m.Name=="FindPath"||m.Name=="StartWorkerThreads"))) {
    Console.WriteLine("  "+m.Name+"("+string.Join(",",m.Parameters.Select(p=>p.ParameterType.Name))+") il="+m.Body.Instructions.Count);
    if (m.Body.Instructions.Count<=60) {
      foreach (var i in m.Body.Instructions) {
        if (i.OpCode.Code==Code.Call||i.OpCode.Code==Code.Callvirt||i.OpCode.Code==Code.Newobj||i.OpCode.FlowControl==FlowControl.Cond_Branch||i.OpCode.FlowControl==FlowControl.Branch||i.OpCode.FlowControl==FlowControl.Return)
          Console.WriteLine("    IL_"+i.Offset.ToString("X4")+": "+i.OpCode.Name+" "+i.Operand);
      }
    }
   }
  }
 }
}
