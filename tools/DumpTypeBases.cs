using System;
using System.IO;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;
class P {
  static void Main(string[] a) {
    var resolver = new DefaultAssemblyResolver();
    resolver.AddSearchDirectory(Path.GetDirectoryName(a[0]));
    var asm = AssemblyDefinition.ReadAssembly(a[0], new ReaderParameters { AssemblyResolver = resolver });
    foreach (var name in new[]{"GameManager","ConnectionManager","DynamicMeshManager","MeshDataManager","World","GameTimer","EntityAsyncManager","NetPackageManager"}) {
      var t = asm.MainModule.Types.FirstOrDefault(x => x.Name == name);
      if (t==null) { Console.WriteLine(name+" MISSING"); continue; }
      Console.WriteLine(name+" : base="+t.BaseType?.FullName);
      foreach (var m in t.Methods.Where(m=>m.Name=="Update"||m.Name=="LateUpdate"||m.Name=="FixedUpdate"||m.Name=="gmUpdate"))
        Console.WriteLine("  method "+m.Name+" il="+(m.HasBody?m.Body.Instructions.Count:0)+" params="+m.Parameters.Count);
    }
    Console.WriteLine("\nCallers (selected):");
    string[] watch = {
      "ConnectionManager::Update","DynamicMeshManager::Update","MeshDataManager::LateUpdate",
      "World::TickEntities","GameManager::UpdateTick","GameManager::gmUpdate","EntityAsyncManager::Update"
    };
    int n=0;
    foreach (var t in asm.MainModule.Types) {
      foreach (var m in t.Methods) {
        if (!m.HasBody) continue;
        foreach (var i in m.Body.Instructions) {
          if (i.Operand is MethodReference mr) {
            string s = mr.DeclaringType.Name+"::"+mr.Name;
            if (watch.Contains(s) && !(t.Name==mr.DeclaringType.Name && m.Name==mr.Name)) {
              Console.WriteLine("  "+t.Name+"::"+m.Name+" -> "+s);
              if (++n>100) goto done;
            }
          }
        }
      }
    }
    done:
    // aiActiveScale writers
    Console.WriteLine("\nMethods referencing field aiActiveScale:");
    foreach (var t in asm.MainModule.Types) {
      foreach (var m in t.Methods) {
        if (!m.HasBody) continue;
        foreach (var i in m.Body.Instructions) {
          if (i.Operand is FieldReference fr && fr.Name=="aiActiveScale") {
            Console.WriteLine("  "+t.Name+"::"+m.Name+" "+i.OpCode.Name);
            break;
          }
        }
      }
    }
  }
}
