using System; using RocketWorld.Sim;
class T{ static int f=0; static void A(bool c,string n){ if(!c){f++;Console.WriteLine("FAIL "+n);} }
static void Main(){
 var id=PriorityLogic.Compute(Priority.None,false); A(id.sse==1&&id.scale==1&&id.turboSse==48f,"identity");
 var fl=PriorityLogic.Compute(Priority.FastLoad,false); A(fl.turboSse==64f&&fl.loads>1&&PriorityLogic.CulledSse(fl)>128f,"fast");
 var q=PriorityLogic.Compute(Priority.TileQuality,false); A(PriorityLogic.Sse(20,q,false)<20,"quality sse");
 var fp=PriorityLogic.Compute(Priority.Fps,false); A(PriorityLogic.Sse(20,fp,false)>20&&fp.scale<1&&PriorityLogic.Far(20000,fp)<20000,"fps");
 var both=PriorityLogic.Compute(Priority.TileQuality|Priority.Fps,false); float s=PriorityLogic.Sse(20,both,false); A(s>19&&s<21,"quality+fps cancel ~ "+s);
 var r=PriorityLogic.Compute(Priority.Realism,false); A(PriorityLogic.Fx(0f,r,false)==1f && PriorityLogic.Fx(0f,r,true)==0f,"realism floor / anticrash");
 A(PriorityLogic.Radius(600,PriorityLogic.Compute(Priority.Realism|Priority.TileQuality,true))<=600f,"mobile radius cap");
 A(PriorityLogic.Sse(20,PriorityLogic.Compute(Priority.All,true),true)>=12f,"mobile sse floor");
 A(PriorityLogic.Sse(1000,fp,false)==64f && PriorityLogic.Sse(1,q,false)==6f,"clamps");
 A(PriorityLogic.Parse("fps,realism")==(Priority.Fps|Priority.Realism)&&PriorityLogic.Parse("5")==(Priority.FastLoad|Priority.Fps)&&PriorityLogic.Parse("x")==Priority.None&&PriorityLogic.Parse("99")==(Priority)(99&15),"parse");
 Console.WriteLine(f==0?"OK 10/10":"FAILED "+f); } }
