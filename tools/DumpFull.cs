using System;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;
class D {
  static void Main(string[] a) {
    var r = new DefaultAssemblyResolver();
    r.AddSearchDirectory(System.IO.Path.GetDirectoryName(a[0]));
    var asm = AssemblyDefinition.ReadAssembly(a[0], new ReaderParameters{AssemblyResolver=r});
    var t = asm.MainModule.Types.First(x=>x.Name==a[1]);
    var m = t.Methods.First(x=>x.Name==a[2] && x.HasBody);
    Console.WriteLine($"// {t}::{m.Name} il={m.Body.Instructions.Count}");
    foreach (var ins in m.Body.Instructions)
      Console.WriteLine($"IL_{ins.Offset:X4}: {ins.OpCode} {ins.Operand}");
  }
}
