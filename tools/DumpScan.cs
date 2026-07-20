using System; using System.IO; using System.Linq; using System.Collections.Generic; using Mono.Cecil; using Mono.Cecil.Cil;
class P {
 static IEnumerable<TypeDefinition> All(ModuleDefinition m){ foreach(var t in m.Types){ yield return t; foreach(var n in t.NestedTypes) yield return n; } }
 static void Main(string[] a){
 var r=new DefaultAssemblyResolver(); r.AddSearchDirectory(Path.GetDirectoryName(a[0]));
 var asm=AssemblyDefinition.ReadAssembly(a[0], new ReaderParameters{AssemblyResolver=r});
 Action<MethodDefinition> dump = m => {
   Console.WriteLine("=== "+m.DeclaringType.Name+"::"+m.Name+" (il="+m.Body.Instructions.Count+") ===");
   foreach(var i in m.Body.Instructions){ var c=i.OpCode.Code;
     if(c==Code.Newarr||c==Code.Newobj||c==Code.Call||c==Code.Callvirt||c==Code.Stfld)
       Console.WriteLine("  IL_"+i.Offset.ToString("X4")+": "+i.OpCode.Name+"  "+i.Operand);
   }
 };
 var types=All(asm.MainModule).ToList();
 var it=types.FirstOrDefault(x=>x.Name=="<ScanInternal>d__45");
 if(it!=null){ var mn=it.Methods.First(x=>x.Name=="MoveNext"); dump(mn); }
 else Console.WriteLine("no iterator in this asm");
 foreach(var tn in new[]{"LayerGridGraph","GridGraph","NavGraph"}){
   var t=types.FirstOrDefault(x=>x.Name==tn);
   if(t==null){Console.WriteLine("no "+tn+" in "+Path.GetFileName(a[0]));continue;}
   Console.WriteLine("### TYPE "+t.FullName);
   foreach(var m in t.Methods.Where(m=>m.HasBody)){
     if(m.Body.Instructions.Any(i=>i.OpCode.Code==Code.Newarr)) dump(m);
   }
 }
}}
