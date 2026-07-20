using System;
using System.Linq;
using Mono.Cecil;
class F {
  static void Main(string[] a) {
    var r = new DefaultAssemblyResolver();
    r.AddSearchDirectory(System.IO.Path.GetDirectoryName(a[0]));
    var asm = AssemblyDefinition.ReadAssembly(a[0], new ReaderParameters{AssemblyResolver=r});
    foreach (var t in asm.MainModule.Types)
      if (t.Name == "Log" || t.Name.EndsWith(".Log") || t.Name == "Logger")
        Console.WriteLine(t.FullName);
    // methods named Out on types with Log in name
    foreach (var t in asm.MainModule.Types)
      if (t.Name.IndexOf("Log", StringComparison.OrdinalIgnoreCase)>=0)
        foreach (var m in t.Methods)
          if (m.Name == "Out" || m.Name == "Warning" || m.Name == "Error")
            Console.WriteLine(t.FullName + "::" + m.Name);
  }
}
