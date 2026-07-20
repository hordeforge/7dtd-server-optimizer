using System;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;
class F {
  static void Main(string[] a) {
    var r = new DefaultAssemblyResolver();
    r.AddSearchDirectory(System.IO.Path.GetDirectoryName(a[0]));
    var asm = AssemblyDefinition.ReadAssembly(a[0], new ReaderParameters{AssemblyResolver=r});
    string field = a[1];
    foreach (var t in asm.MainModule.Types)
      foreach (var m in t.Methods.Where(m=>m.HasBody))
        foreach (var i in m.Body.Instructions)
          if ((i.OpCode.Code==Code.Stfld || i.OpCode.Code==Code.Ldfld || i.OpCode.Code==Code.Ldsfld) && i.Operand is FieldReference fr && fr.Name==field)
            Console.WriteLine($"{i.OpCode.Name}\t{t.FullName}::{m.Name}");
  }
}
