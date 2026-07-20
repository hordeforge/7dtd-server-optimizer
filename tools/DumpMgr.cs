using System; using System.IO; using System.Linq; using Mono.Cecil; using Mono.Cecil.Cil;
class P { static void Main(string[] a){
 var r=new DefaultAssemblyResolver(); r.AddSearchDirectory(Path.GetDirectoryName(a[0]));
 var asm=AssemblyDefinition.ReadAssembly(a[0], new ReaderParameters{AssemblyResolver=r});
 var t=asm.MainModule.Types.First(x=>x.Name=="AstarManager");
 foreach(var mn in new[]{"UpdateGraphPos","UpdateMoveGraph","UpdateGraphs","FindMoveIndex"}){
   var m=t.Methods.FirstOrDefault(x=>x.Name==mn && x.HasBody);
   if(m==null){Console.WriteLine("no "+mn);continue;}
   Console.WriteLine("=== "+mn+" il="+m.Body.Instructions.Count+" ===");
   foreach(var i in m.Body.Instructions){ var c=i.OpCode.Code;
     if(c==Code.Ldc_R4||c==Code.Ldc_I4||c==Code.Call||c==Code.Callvirt||c==Code.Ldfld||c==Code.Stfld||c==Code.Ble||c==Code.Ble_Un||c==Code.Bge||c==Code.Blt||c==Code.Bgt||c==Code.Bge_Un||c==Code.Bgt_Un||c==Code.Beq||c==Code.Bne_Un)
       Console.WriteLine("  IL_"+i.Offset.ToString("X4")+": "+i.OpCode.Name+"  "+i.Operand);
   }
 }
}}
