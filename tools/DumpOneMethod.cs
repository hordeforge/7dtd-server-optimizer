using System; using System.IO; using System.Linq; using Mono.Cecil; using Mono.Cecil.Cil;
class P { static void Main(string[] a) {
  var r=new DefaultAssemblyResolver(); r.AddSearchDirectory(Path.GetDirectoryName(a[0]));
  var asm=AssemblyDefinition.ReadAssembly(a[0], new ReaderParameters{AssemblyResolver=r});
  var t=asm.MainModule.Types.First(x=>x.Name==a[1]);
  var m=t.Methods.First(x=>x.Name==a[2] && x.HasBody);
  Console.WriteLine(t.Name+"::"+m.Name+" il="+m.Body.Instructions.Count);
  foreach (var i in m.Body.Instructions) {
    if (i.OpCode.Code==Code.Call||i.OpCode.Code==Code.Callvirt||i.OpCode.FlowControl==FlowControl.Cond_Branch||i.OpCode.FlowControl==FlowControl.Branch||i.OpCode.FlowControl==FlowControl.Return)
      Console.WriteLine($"IL_{i.Offset:X4}: {i.OpCode.Name} {i.Operand}");
  }
}}
