using System; using System.IO; using System.Linq; using Mono.Cecil; using Mono.Cecil.Cil;
class P {
 static void Main(string[] a) {
  var r=new DefaultAssemblyResolver(); r.AddSearchDirectory(Path.GetDirectoryName(a[0]));
  var asm=AssemblyDefinition.ReadAssembly(a[0], new ReaderParameters{AssemblyResolver=r});
  // which PathFinderThread Instance assigned
  Console.WriteLine("=== PathFinderThread.Instance stores ===");
  foreach (var t in asm.MainModule.Types) {
    foreach (var m in t.Methods.Where(m=>m.HasBody)) {
      foreach (var i in m.Body.Instructions) {
        var fr = i.Operand as FieldReference;
        if (fr!=null && fr.Name=="Instance" && fr.DeclaringType.Name=="PathFinderThread" && (i.OpCode.Code==Code.Stsfld)) {
          Console.WriteLine(t.Name+"::"+m.Name+" stsfld Instance");
        }
        var mr = i.Operand as MethodReference;
        if (mr!=null && (mr.Name.Contains("AStarPathFinder")||mr.Name.Contains("ASPPathFinder")||(mr.DeclaringType.Name.Contains("PathFinder")&&mr.Name==".ctor"))) {
          if (t.Name.Contains("Astar")||t.Name.Contains("Path")||t.Name=="GameManager"||t.Name=="World")
            Console.WriteLine(t.Name+"::"+m.Name+" -> "+mr.DeclaringType.Name+"::"+mr.Name);
        }
      }
    }
  }
  var am = asm.MainModule.Types.First(x=>x.Name=="AstarManager");
  Console.WriteLine("=== AstarManager methods ===");
  foreach (var m in am.Methods.Where(m=>m.HasBody)) {
    Console.WriteLine(m.Name+" il="+m.Body.Instructions.Count);
    if (m.Name=="Init" || m.Name.Contains("Start") || m.Body.Instructions.Count<80) {
      foreach (var i in m.Body.Instructions) {
        if (i.OpCode.Code==Code.Call||i.OpCode.Code==Code.Callvirt||i.OpCode.Code==Code.Newobj||i.OpCode.Code==Code.Stsfld)
          Console.WriteLine("  IL_"+i.Offset.ToString("X4")+": "+i.OpCode.Name+" "+i.Operand);
      }
    }
  }
 }
}
