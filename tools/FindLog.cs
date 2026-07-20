using System;
using System.Linq;
using Mono.Cecil;
class F {
  static void Main(string[] a) {
    var r = new DefaultAssemblyResolver();
    r.AddSearchDirectory(System.IO.Path.GetDirectoryName(a[0]));
    var asm = AssemblyDefinition.ReadAssembly(a[0], new ReaderParameters{AssemblyResolver=r});
    foreach (var t in asm.MainModule.Types) {
      foreach (var m in t.Methods) {
        if (m.Name == "Out" && t.Name.Contains("Log"))
          Console.WriteLine("TYPE " + t.FullName + " attrs=" + t.Attributes + " method " + m);
      }
      if (t.Name == "Log")
        Console.WriteLine("FOUND " + t.FullName + " ns=" + t.Namespace);
    }
    // nested
    foreach (var t in asm.MainModule.Types)
      foreach (var n in t.NestedTypes)
        if (n.Name == "Log") Console.WriteLine("NESTED " + n.FullName);
  }
}
