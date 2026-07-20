using System;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;
class D {
  static void Main(string[] a) {
    var r = new DefaultAssemblyResolver();
    r.AddSearchDirectory(System.IO.Path.GetDirectoryName(a[0]));
    var asm = AssemblyDefinition.ReadAssembly(a[0], new ReaderParameters{AssemblyResolver=r});
    foreach (var t in asm.MainModule.Types) {
      var all = new[]{t}.Concat(t.NestedTypes);
      foreach (var nt in all) {
        if (!nt.FullName.Contains("AuthWrapper") && !nt.Name.Contains("AuthWrapper")) continue;
        Console.WriteLine("==== "+nt.FullName+" ====");
        foreach (var f in nt.Fields) Console.WriteLine("FIELD "+f.FieldType.Name+" "+f.Name);
        foreach (var m in nt.Methods.Where(m=>m.HasBody)) {
          Console.WriteLine("METHOD "+m.Name+" il="+m.Body.Instructions.Count);
          if (m.Name.Contains("Connection") || m.Name.Contains("Connect") || m.Name.Contains("Accept") || m.Name.Contains("Check") || m.Body.Instructions.Count < 100) {
            foreach (var i in m.Body.Instructions.Take(100))
              Console.WriteLine("  "+i.OpCode+" "+i.Operand);
          }
        }
      }
    }
  }
}
