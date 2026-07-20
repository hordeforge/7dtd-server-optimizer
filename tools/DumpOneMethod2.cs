using System; using System.IO; using System.Linq; using Mono.Cecil; using Mono.Cecil.Cil;
class P {
 static void Main(string[] a) {
  var r=new DefaultAssemblyResolver(); r.AddSearchDirectory(Path.GetDirectoryName(a[0]));
  var asm=AssemblyDefinition.ReadAssembly(a[0], new ReaderParameters{AssemblyResolver=r});
  foreach (var pair in new[]{("MapObjectWaypoint","IsShowName"),("MapObjectWaypoint","GetName"),("NavObject","get_name"),("Waypoint",".ctor")}) {
    var t=asm.MainModule.Types.First(x=>x.Name==pair.Item1);
    var m=t.Methods.FirstOrDefault(x=>x.Name==pair.Item2);
    if (m==null){Console.WriteLine("no "+pair);continue;}
    Console.WriteLine("=== "+pair.Item1+"::"+pair.Item2+" IL="+m.Body.Instructions.Count);
    foreach (var i in m.Body.Instructions) Console.WriteLine("  "+i.OpCode.Name+" "+i.Operand);
  }
 }
}
