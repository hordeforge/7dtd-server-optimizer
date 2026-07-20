using System;
using System.IO;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;
class D {
  static void Main(string[] a) {
    var resolver = new DefaultAssemblyResolver();
    resolver.AddSearchDirectory(Path.GetDirectoryName(a[0]));
    var asm = AssemblyDefinition.ReadAssembly(a[0], new ReaderParameters{AssemblyResolver=resolver});
    string tn=a[1], mn=a[2];
    var t = asm.MainModule.Types.First(x=>x.Name==tn);
    foreach (var m in t.Methods.Where(m=>m.Name==mn && m.HasBody)) {
      Console.WriteLine($"// {t.FullName}::{m.Name} il={m.Body.Instructions.Count} params={m.Parameters.Count}");
      var calls = m.Body.Instructions.Where(i=>i.OpCode.Code==Code.Call||i.OpCode.Code==Code.Callvirt)
        .Select(i=>i.Operand?.ToString()).GroupBy(s=>s).OrderByDescending(g=>g.Count());
      foreach (var g in calls.Take(30)) Console.WriteLine($"  {g.Count()}x {g.Key}");
      foreach (var ins in m.Body.Instructions.Take(100))
        Console.WriteLine($"  IL_{ins.Offset:X4}: {ins.OpCode} {ins.Operand}");
    }
    // also list fields matching keywords
    foreach (var f in t.Fields)
      if (f.Name.IndexOf("ai", StringComparison.OrdinalIgnoreCase)>=0 ||
          f.Name.IndexOf("Active", StringComparison.OrdinalIgnoreCase)>=0 ||
          f.Name.IndexOf("Player", StringComparison.OrdinalIgnoreCase)>=0 ||
          f.Name.IndexOf("tick", StringComparison.OrdinalIgnoreCase)>=0)
        Console.WriteLine($"FIELD {f.FieldType.Name} {f.Name}");
  }
}
