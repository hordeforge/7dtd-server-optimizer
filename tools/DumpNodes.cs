using System; using System.IO; using System.Linq; using System.Collections.Generic; using Mono.Cecil; using Mono.Cecil.Cil;
class P {
 static IEnumerable<TypeDefinition> All(ModuleDefinition m){ foreach(var t in m.Types){ yield return t; foreach(var n in t.NestedTypes){ yield return n; foreach(var n2 in n.NestedTypes) yield return n2; } } }
 static void Main(string[] a){
 var r=new DefaultAssemblyResolver(); r.AddSearchDirectory(Path.GetDirectoryName(a[0]));
 var asm=AssemblyDefinition.ReadAssembly(a[0], new ReaderParameters{AssemblyResolver=r});
 foreach(var t in All(asm.MainModule)){
   foreach(var m in t.Methods.Where(x=>x.HasBody)){
     foreach(var i in m.Body.Instructions){
       var fr=i.Operand as FieldReference;
       if((i.OpCode.Code==Code.Stfld) && fr!=null && fr.Name=="nodes" && fr.DeclaringType.Name=="LayerGridGraph")
         Console.WriteLine("STFLD nodes  in "+t.Name+"::"+m.Name+" il="+m.Body.Instructions.Count);
       var mr=i.Operand as MethodReference;
       if(i.OpCode.Code==Code.Newobj && mr!=null && mr.DeclaringType.Name=="LevelGridNode")
         Console.WriteLine("NEWOBJ LevelGridNode in "+t.Name+"::"+m.Name+" il="+m.Body.Instructions.Count);
     }
   }
 }
}}
