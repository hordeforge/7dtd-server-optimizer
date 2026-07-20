using System;
using System.Linq;
using Mono.Cecil;
class F {
  static void Main(string[] a) {
    var r = new DefaultAssemblyResolver();
    r.AddSearchDirectory(System.IO.Path.GetDirectoryName(a[0]));
    var asm = AssemblyDefinition.ReadAssembly(a[0], new ReaderParameters{AssemblyResolver=r});
    string baseName = a[1];
    foreach (var t in asm.MainModule.Types) {
      var b = t.BaseType;
      while (b != null) {
        if (b.Name == baseName || b.FullName.Contains(baseName)) {
          Console.WriteLine(t.FullName + " : " + b.FullName);
          break;
        }
        // resolve
        try { b = b.Resolve()?.BaseType; } catch { break; }
      }
    }
  }
}
