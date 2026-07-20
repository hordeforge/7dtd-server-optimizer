using System;
using System.Linq;
using Mono.Cecil;
class D {
  static void Main(string[] a) {
    var r = new DefaultAssemblyResolver();
    r.AddSearchDirectory(System.IO.Path.GetDirectoryName(a[0]));
    var asm = AssemblyDefinition.ReadAssembly(a[0], new ReaderParameters{AssemblyResolver=r});
    var t = asm.MainModule.Types.FirstOrDefault(x=>x.Name==a[1]);
    if (t==null) { Console.WriteLine("not found"); return; }
    foreach (var f in t.Fields) Console.WriteLine($"FIELD {f.Attributes} {f.FieldType.Name} {f.Name}");
    foreach (var m in t.Methods) Console.WriteLine($"METHOD {m.Name}({string.Join(",",m.Parameters.Select(p=>p.ParameterType.Name))})");
  }
}
