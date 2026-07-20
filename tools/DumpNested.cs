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
      if (!t.FullName.Contains("LiteNetLibAuth") && t.Name != "NetworkCommonLiteNetLib") continue;
      Console.WriteLine("TYPE "+t.FullName);
      foreach (var m in t.Methods.Where(m=>m.HasBody)) {
        if (m.Name.Contains("Connection") || m.Name.Contains("Connect") || m.Name.Contains("Init") || m.Name.Contains("Password") || m.Name.Contains("Key")) {
          Console.WriteLine("  METHOD "+m.Name+" il="+m.Body.Instructions.Count);
          foreach (var i in m.Body.Instructions.Take(60))
            Console.WriteLine("    "+i.OpCode+" "+i.Operand);
        }
      }
      foreach (var n in t.NestedTypes) {
        Console.WriteLine("NESTED "+n.FullName);
        foreach (var m in n.Methods.Where(m=>m.HasBody && (m.Name.Contains("Connection")||m.Name.Contains("Connect")||m.Name.Contains("Accept")))) {
          Console.WriteLine("  METHOD "+m.Name+" il="+m.Body.Instructions.Count);
          foreach (var i in m.Body.Instructions.Take(80))
            Console.WriteLine("    "+i.OpCode+" "+i.Operand);
        }
      }
    }
  }
}
