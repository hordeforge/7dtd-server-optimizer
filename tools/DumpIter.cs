using System; using System.IO; using System.Linq; using System.Collections.Generic; using Mono.Cecil; using Mono.Cecil.Cil;
class P {
 static IEnumerable<TypeDefinition> All(ModuleDefinition m){ foreach(var t in m.Types){ yield return t; foreach(var n in t.NestedTypes){ yield return n; foreach(var n2 in n.NestedTypes) yield return n2; } } }
 static void Main(string[] a){
 var r=new DefaultAssemblyResolver(); r.AddSearchDirectory(Path.GetDirectoryName(a[0]));
 var asm=AssemblyDefinition.ReadAssembly(a[0], new ReaderParameters{AssemblyResolver=r});
 var t=All(asm.MainModule).First(x=>x.Name=="<ScanInternal>d__21" && x.DeclaringType.Name=="LayerGridGraph");
 var m=t.Methods.First(x=>x.Name=="MoveNext");
 foreach(var i in m.Body.Instructions){ var c=i.OpCode.Code;
   if(c==Code.Newarr||c==Code.Newobj||c==Code.Call||c==Code.Callvirt||c==Code.Stfld||c==Code.Ldfld||c==Code.Ldlen)
     Console.WriteLine("  IL_"+i.Offset.ToString("X4")+": "+i.OpCode.Name+"  "+i.Operand);
 }
}}
