using System;
using System.Diagnostics;
using System.IO;
using System.Windows.Forms;

internal sealed class TabletEdgeGesture {
 bool tracking,rejected,active;int finger,serial;double x0,y0,anchor;long began;
 internal event Action<int,PadEdge,double> Motion;internal PadEdge Edge {get;private set;}
 internal void Reset(){tracking=rejected=active=false;Edge=PadEdge.None;}
 internal void Cancel(int id){if(tracking&&active&&serial==id)rejected=true;}
 internal void Update(bool pressed,int count,int id,double x,double y,double width,double height,double scale,long time){
  if(!pressed||count==0){Reset();return;}
  if(!tracking){tracking=true;finger=id;x0=x;y0=y;began=time;Edge=x<=26*scale?PadEdge.Screen:x>=width-26*scale?PadEdge.Volume:PadEdge.None;rejected=Edge==PadEdge.None;}
  if(count!=1||id!=finger){rejected=true;return;}if(rejected)return;
  double along=(y0-y)/scale,cross=Math.Abs(x-x0)/scale;bool inside=Edge==PadEdge.Screen?x<=38*scale:x>=width-38*scale;
  double elapsed=(time-began)*1000.0/Stopwatch.Frequency;if(!active&&(!inside||cross>16||elapsed>1600)){rejected=true;return;}
  if(!active){if(Math.Abs(along)<18||Math.Abs(along)<cross*1.8||elapsed<35)return;active=true;anchor=along;serial++;}
  if(Motion!=null)Motion(serial,Edge,(along-anchor)*(Edge==PadEdge.Volume?.32:.20));
 }
}
internal sealed class TabletEdgeControls : IDisposable {
 readonly Control dispatcher;readonly EdgeToast toast=new EdgeToast();readonly TabletEdgeGesture gesture=new TabletEdgeGesture();EdgeActuator actuator;bool disposed;
 internal string Error {get;private set;}internal string LastControl {get;private set;}internal int LastValue {get;private set;}
 internal event Action Changed;
 internal TabletEdgeControls(string folder,Control owner,Screen display){dispatcher=owner;Error="";LastControl="";LastValue=-1;actuator=new EdgeActuator(folder,display);gesture.Motion+=(id,edge,delta)=>actuator.Schedule(id,edge,delta);actuator.Updated+=(edge,value)=>Post(()=>{Error="";LastControl=edge.ToString();LastValue=value;toast.Display(edge,value,display);if(Changed!=null)Changed();});actuator.Failed+=(id,message)=>Post(()=>{gesture.Cancel(id);Error=message;toast.Hide();if(Changed!=null)Changed();});}
 void Post(Action action){if(!disposed&&!dispatcher.IsDisposed)try{dispatcher.BeginInvoke(new Action(()=>{if(!disposed)action();}));}catch(InvalidOperationException){}}
 internal void Update(bool pressed,int count,int id,double x,double y,double width,double height,double scale){if(!disposed)gesture.Update(pressed,count,id,x,y,width,height,scale,Stopwatch.GetTimestamp());}
 internal void Reset(){gesture.Reset();toast.Hide();}
 public void Dispose(){if(disposed)return;disposed=true;gesture.Reset();actuator.Dispose();toast.Dispose();}
}
