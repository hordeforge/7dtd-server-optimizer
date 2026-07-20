using System; using System.IO; using System.Linq; using Mono.Cecil;
class P { static void Main(string[] a) {
  var r=new DefaultAssemblyResolver(); r.AddSearchDirectory(Path.GetDirectoryName(a[0]));
  var asm=AssemblyDefinition.ReadAssembly(a[0], new ReaderParameters{AssemblyResolver=r});
  var t=asm.MainModule.Types.First(x=>x.Name==a[1]);
  foreach (var m in t.Methods.Where(m=>m.Name.IndexOf(a[2], StringComparison.OrdinalIgnoreCase)>=0))
    Console.WriteLine(m.Name+" il="+(m.HasBody?m.Body.Instructions.Count:-1)+" p="+m.Parameters.Count);
}}
