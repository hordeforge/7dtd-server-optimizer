using System;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;
class D {
  static void Main(string[] a) {
    var r = new DefaultAssemblyResolver();
    r.AddSearchDirectory(System.IO.Path.GetDirectoryName(a[0]));
    var asm = AssemblyDefinition.ReadAssembly(a[0], new ReaderParameters{AssemblyResolver=r});
    string want = a[1];
    foreach (var t in asm.MainModule.Types) {
      foreach (var nt in new[]{t}.Concat(t.NestedTypes)) {
        foreach (var m in nt.Methods.Where(m=>m.HasBody && m.Name==want)) {
          Console.WriteLine(nt.FullName+"::"+m.Name+" il="+m.Body.Instructions.Count);
          foreach (var i in m.Body.Instructions)
            Console.WriteLine("  "+i.OpCode+" "+i.Operand);
        }
      }
    }
  }
}
