using System; using System.IO; using System.Linq; using Mono.Cecil; using Mono.Cecil.Cil;
class P {
 static void Main(string[] a) {
  var r=new DefaultAssemblyResolver(); r.AddSearchDirectory(Path.GetDirectoryName(a[0]));
  var asm=AssemblyDefinition.ReadAssembly(a[0], new ReaderParameters{AssemblyResolver=r});
  var t=asm.MainModule.Types.First(x=>x.Name=="NavObjectManager");
  foreach (var m in t.Methods.Where(m=>m.Name=="RegisterNavObject" && m.Parameters.Count==6)) {
    Console.WriteLine(m.FullName+" IL="+m.Body.Instructions.Count);
    foreach (var i in m.Body.Instructions.Take(80))
      Console.WriteLine("  "+i.OpCode.Name+" "+i.Operand);
  }
  // NavObject map settings show name?
  var ms=asm.MainModule.Types.FirstOrDefault(x=>x.Name=="NavObjectMapSettings");
  if (ms!=null) foreach (var f in ms.Fields) Console.WriteLine("mapset "+f.Name+" : "+f.FieldType.Name);
 }
}
