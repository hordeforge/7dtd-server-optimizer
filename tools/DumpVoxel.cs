using System; using System.IO; using System.Linq; using Mono.Cecil; using Mono.Cecil.Cil;
class P { static void Main(string[] a){
 var r=new DefaultAssemblyResolver(); r.AddSearchDirectory(Path.GetDirectoryName(a[0]));
 var asm=AssemblyDefinition.ReadAssembly(a[0], new ReaderParameters{AssemblyResolver=r});
 var t=asm.MainModule.Types.First(x=>x.Name=="AstarVoxelGrid");
 Console.WriteLine("BASE: "+t.BaseType.FullName);
 Console.WriteLine("=== FIELDS ===");
 foreach(var f in t.Fields) Console.WriteLine("  "+f.FieldType.FullName+"  "+f.Name);
 // base fields
 var bt=t.BaseType.Resolve();
 Console.WriteLine("=== BASE FIELDS ("+bt.FullName+") ===");
 foreach(var f in bt.Fields) Console.WriteLine("  "+f.FieldType.FullName+"  "+f.Name);
 Console.WriteLine("=== METHODS (name/il) ===");
 foreach(var m in t.Methods.Where(m=>m.HasBody)) Console.WriteLine("  "+m.Name+" il="+m.Body.Instructions.Count);
 foreach(var mn in new[]{"InitScan","ScanInternal","MoveGraph","Scan"}){
   var m=t.Methods.FirstOrDefault(x=>x.Name==mn && x.HasBody);
   if(m==null){Console.WriteLine("--- "+mn+" NOT FOUND on type ---");continue;}
   Console.WriteLine("=== "+mn+" (il="+m.Body.Instructions.Count+") ===");
   foreach(var i in m.Body.Instructions){
     var c=i.OpCode.Code;
     if(c==Code.Newarr||c==Code.Newobj||c==Code.Call||c==Code.Callvirt||c==Code.Stfld||c==Code.Ldfld||c==Code.Initobj)
       Console.WriteLine("  IL_"+i.Offset.ToString("X4")+": "+i.OpCode.Name+"  "+i.Operand);
   }
 }
}}
