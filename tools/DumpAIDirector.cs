using System;
using System.IO;
using System.Linq;
using System.Text;
using Mono.Cecil;
using Mono.Cecil.Cil;

class DumpAIDirector {
  static void Main(string[] args) {
    var r = new DefaultAssemblyResolver();
    r.AddSearchDirectory(Path.GetDirectoryName(args[0]));
    var asm = AssemblyDefinition.ReadAssembly(args[0], new ReaderParameters { AssemblyResolver = r });
    var sb = new StringBuilder();
    sb.AppendLine("# AIDirector component types (V3.0.1)");
    sb.AppendLine();
    foreach (var t in asm.MainModule.Types.OrderBy(t => t.Name)) {
      bool hit = false;
      var b = t.BaseType;
      int g = 0;
      while (b != null && g++ < 10) {
        if (b.Name == "AIDirectorComponent" || b.Name.Contains("AIDirector")) { hit = true; break; }
        try { var rr = b.Resolve(); if (rr == null) break; b = rr.BaseType; } catch { break; }
      }
      if (t.Name.StartsWith("AIDirector") && t.Name.Contains("Component")) hit = true;
      if (!hit && !t.Name.StartsWith("AIDirector")) continue;
      sb.AppendLine("## " + t.FullName + " : " + (t.BaseType != null ? t.BaseType.Name : "?"));
      foreach (var m in t.Methods.Where(m => m.HasBody && (m.Name == "Tick" || m.Name.StartsWith("Update") || m.Name.Contains("Spawn"))))
        sb.AppendLine("- `" + m.Name + "(" + string.Join(",", m.Parameters.Select(p => p.ParameterType.Name)) + ")` IL=" + m.Body.Instructions.Count);
      sb.AppendLine();
    }
    // IsDedicated checks in Entity.Update?
    sb.AppendLine("# IsDedicatedServer references in Entity* Update methods");
    sb.AppendLine();
    foreach (var t in asm.MainModule.Types.Where(t => t.Name.StartsWith("Entity"))) {
      foreach (var m in t.Methods.Where(m => m.HasBody && (m.Name == "Update" || m.Name == "FixedUpdate"))) {
        foreach (var i in m.Body.Instructions) {
          var mr = i.Operand as MethodReference;
          if (mr != null && mr.Name.IndexOf("Dedicated", StringComparison.OrdinalIgnoreCase) >= 0)
            sb.AppendLine("- `" + t.Name + "::" + m.Name + "` calls `" + mr.Name + "`");
        }
      }
    }
    File.WriteAllText(args[1], sb.ToString());
    Console.WriteLine("wrote " + args[1]);
  }
}
