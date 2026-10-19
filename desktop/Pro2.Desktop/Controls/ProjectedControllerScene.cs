// Portable rendering of the original Ns2ProScene. The shell is the exact shared N2M1 asset.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Xml.Linq;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Y700Switch2V60Viiper;
namespace Pro2.Desktop.Controls;

internal sealed class ProjectedControllerScene : IDisposable
{
    private sealed record Mesh(Vector3[] Positions,Vector3[] Normals,int[] Indices);
    private sealed record Part(Mesh Mesh,int Key,float Tone,Func<GamepadState,Matrix4x4> Transform);
    private sealed record Mark(string Text,int Key,Vector3 Position,float Width,float Height,Func<GamepadState,Matrix4x4> Transform,bool Reverse=false);
    private readonly List<Part> parts=[];
    private readonly List<Mark> marks=[];
    private readonly Dictionary<int,(Vector3 Point,Func<GamepadState,Matrix4x4> Transform)> anchors=[];
    private readonly bool rear;
    private float yaw,pitch;
    private double cameraWidth=166;
    private WriteableBitmap? bitmap;
    private int width,height;
    private int[] pixels=[],hitKeys=[];
    private float[] depths=[];
    private int[] basePixels=[],baseHitKeys=[];
    private float[] baseDepths=[];
    private GamepadState lastState=GamepadState.Neutral();
    private bool dirty=true;
    private int lastSelection=-2;
    private static readonly Vector3 Look=Vector3.Normalize(new(0,-20,-280));
    private static readonly Vector3 Right=Vector3.Normalize(Vector3.Cross(Look,Vector3.UnitY));
    private static readonly Vector3 Up=Vector3.Normalize(Vector3.Cross(Right,Look));
    private static readonly Vector3 Camera=new(0,17,280);
    private Vector3[] shellVertices=[];
    private int[] shellIndices=[];
    private Geometry? logo;
    public ProjectedControllerScene(bool rearView)
    {
        rear=rearView;LoadShell();Build();Reset();
        using var mark=AssetLoader.Open(new Uri("avares://PRO2ControllerAssistant/Assets/Controller/switch2-official-mark.svg"));
        var document=XDocument.Load(mark);
        var path=document.Descendants().FirstOrDefault(e=>e.Name.LocalName=="path")?.Attribute("d")?.Value;
        if(path!=null)logo=Geometry.Parse(path);
    }
    public void Reset(){yaw=rear?180:0;pitch=0;cameraWidth=166;dirty=true;}
    public void Orbit(double x,double y){yaw+=(float)x;pitch=Math.Clamp(pitch+(float)y,-32,32);dirty=true;}
    public void Zoom(double amount){cameraWidth=Math.Clamp(cameraWidth+amount,145,220);dirty=true;}
    public void Overview(double aspect){double next=Math.Max(175,aspect*120);if(yaw==-9&&pitch==-4&&Math.Abs(next-cameraWidth)<.01)return;yaw=-9;pitch=-4;cameraWidth=next;dirty=true;}
    public int Hit(Point point)=>point.X>=0&&point.Y>=0&&point.X<width&&point.Y<height?hitKeys[(int)point.Y*width+(int)point.X]:-1;
    public Point Project(int key,GamepadState state)
    {
        if(!anchors.TryGetValue(key,out var anchor))return new(width*.5,height*.5);
        var world=Vector3.Transform(anchor.Point,anchor.Transform(state)*Pose());
        return ProjectPoint(world);
    }
    private Matrix4x4 Pose()=>Matrix4x4.CreateRotationY(yaw*MathF.PI/180)*Matrix4x4.CreateRotationX(pitch*MathF.PI/180);
    private Point ProjectPoint(Vector3 world)
    {
        var d=world-Camera;double scale=width/cameraWidth;
        return new(width*.5+Vector3.Dot(d,Right)*scale,height*.5-Vector3.Dot(d,Up)*scale);
    }
    public void Draw(DrawingContext context,Rect rect,GamepadState state,int selected=-1)
    {
        int w=Math.Max(1,(int)Math.Ceiling(rect.Width)),h=Math.Max(1,(int)Math.Ceiling(rect.Height));
        if(bitmap==null||w!=width||h!=height)
        {
            bitmap?.Dispose();width=w;height=h;
            bitmap=new WriteableBitmap(new PixelSize(w,h),new Avalonia.Vector(96,96),PixelFormat.Bgra8888,AlphaFormat.Unpremul);
            pixels=new int[w*h];depths=new float[w*h];hitKeys=new int[w*h];basePixels=new int[w*h];baseDepths=new float[w*h];baseHitKeys=new int[w*h];dirty=true;
        }
        bool changed=lastState.Buttons!=state.Buttons||lastState.Lx!=state.Lx||lastState.Ly!=state.Ly||lastState.Rx!=state.Rx||lastState.Ry!=state.Ry;
        if(dirty||changed||selected!=lastSelection)
        {
            var pose=Pose();
            if(dirty)
            {
                Array.Clear(pixels);Array.Fill(depths,float.PositiveInfinity);Array.Fill(hitKeys,-1);
                foreach(var part in parts)if(part.Key<0)Raster(part,state,pose,-1);
                Array.Copy(pixels,basePixels,pixels.Length);Array.Copy(depths,baseDepths,depths.Length);Array.Copy(hitKeys,baseHitKeys,hitKeys.Length);
            }
            else {Array.Copy(basePixels,pixels,pixels.Length);Array.Copy(baseDepths,depths,depths.Length);Array.Copy(baseHitKeys,hitKeys,hitKeys.Length);}
            foreach(var part in parts)if(part.Key>=0)Raster(part,state,pose,selected);
            using(var locked=bitmap.Lock())for(int row=0;row<h;row++)Marshal.Copy(pixels,row*w,locked.Address+row*locked.RowBytes,w);
            dirty=false;lastState=state.Clone();lastSelection=selected;
        }
        context.DrawImage(bitmap,new Rect(0,0,w,h),rect);
        DrawLogo(context,rect);
        foreach(var mark in marks)
        {
            var transform=mark.Transform(state)*Pose();
            var normal=Vector3.TransformNormal(mark.Reverse?-Vector3.UnitZ:Vector3.UnitZ,transform);
            if(Vector3.Dot(normal,-Look)<.2)continue;
            var at=ProjectPoint(Vector3.Transform(mark.Position,transform));
            float scale=(float)(width/cameraWidth);
            var text=new FormattedText(mark.Text,CultureInfo.InvariantCulture,FlowDirection.LeftToRight,new Typeface(SharedControllerFont.Family),Math.Max(1,mark.Height*scale),new SolidColorBrush(mark.Key<4?Color.Parse("#C2C1BF"):Color.Parse("#1B1B1B")));
            context.DrawText(text,new Point(rect.X+at.X-text.Width/2,rect.Y+at.Y-text.Height/2));
        }
    }
    private void DrawLogo(DrawingContext context,Rect rect)
    {
        if(logo==null||Vector3.Dot(Vector3.TransformNormal(Vector3.UnitZ,Pose()),-Look)<.2)return;
        var b=logo.Bounds;float h=(float)(18.2*b.Height/b.Width),cy=34.4f;
        var tl=ProjectPoint(Vector3.Transform(new(-9.1f,cy+h/2,22.08f),Pose()));
        var tr=ProjectPoint(Vector3.Transform(new(9.1f,cy+h/2,22.08f),Pose()));
        var bl=ProjectPoint(Vector3.Transform(new(-9.1f,cy-h/2,22.08f),Pose()));
        double a=(tr.X-tl.X)/b.Width,c=(bl.X-tl.X)/b.Height,d=(tr.Y-tl.Y)/b.Width,e=(bl.Y-tl.Y)/b.Height;
        using(context.PushTransform(new Matrix(a,d,c,e,rect.X+tl.X-a*b.Left-c*b.Top,rect.Y+tl.Y-d*b.Left-e*b.Top)))
            context.DrawGeometry(new SolidColorBrush(Color.Parse("#1B1B1B")),null,logo);
    }
    private void Raster(Part part,GamepadState state,Matrix4x4 pose,int selected)
    {
        var transform=part.Transform(state)*pose;
        var positions=part.Mesh.Positions;var points=new Vector3[positions.Length];var tones=new float[positions.Length];
        for(int i=0;i<positions.Length;i++)
        {
            Vector3 world=Vector3.Transform(positions[i],transform),delta=world-Camera;
            points[i]=new((float)(width*.5+Vector3.Dot(delta,Right)*width/cameraWidth),(float)(height*.5-Vector3.Dot(delta,Up)*width/cameraWidth),Vector3.Dot(delta,Look));
            var normal=Vector3.Normalize(Vector3.TransformNormal(part.Mesh.Normals[i],transform));
            float light=.36f+.46f*Math.Max(0,Vector3.Dot(normal,Vector3.Normalize(new(-.6f,.8f,1))))+.22f*Math.Max(0,Vector3.Dot(normal,Vector3.Normalize(new(.9f,-.15f,.5f))))+.26f*Math.Max(0,Vector3.Dot(normal,Vector3.Normalize(new(-.5f,.7f,-1))));
            float shine=MathF.Pow(Math.Max(0,Vector3.Dot(normal,Vector3.Normalize(new(-.3f,.5f,1)))),24)*38;
            tones[i]=Math.Clamp(part.Tone*light+shine,0,255);
        }
        var indices=part.Mesh.Indices;
        for(int i=0;i<indices.Length;i+=3)
        {
            int ai=indices[i],bi=indices[i+1],ci=indices[i+2];var a=points[ai];var b=points[bi];var c=points[ci];
            float area=(b.X-a.X)*(c.Y-a.Y)-(b.Y-a.Y)*(c.X-a.X);
            if(area>=-.00001f)continue;
            int minX=Math.Max(0,(int)MathF.Floor(Math.Min(a.X,Math.Min(b.X,c.X)))),maxX=Math.Min(width-1,(int)MathF.Ceiling(Math.Max(a.X,Math.Max(b.X,c.X))));
            int minY=Math.Max(0,(int)MathF.Floor(Math.Min(a.Y,Math.Min(b.Y,c.Y)))),maxY=Math.Min(height-1,(int)MathF.Ceiling(Math.Max(a.Y,Math.Max(b.Y,c.Y))));
            float inv=1/area;
            for(int y=minY;y<=maxY;y++)for(int x=minX;x<=maxX;x++)
            {
                float px=x+.5f,py=y+.5f;
                float wa=((b.X-px)*(c.Y-py)-(b.Y-py)*(c.X-px))*inv;
                float wb=((c.X-px)*(a.Y-py)-(c.Y-py)*(a.X-px))*inv;
                float wc=1-wa-wb;if(wa<0||wb<0||wc<0)continue;
                float z=wa*a.Z+wb*b.Z+wc*c.Z;int at=y*width+x;if(z>=depths[at])continue;
                depths[at]=z;hitKeys[at]=part.Key;
                int tone=(int)(wa*tones[ai]+wb*tones[bi]+wc*tones[ci]);
                int r=tone,g=tone,bv=tone;
                if(part.Key>=0&&(part.Key==selected||state.IsPressed((GamepadButtons)(1UL<<part.Key)))){r=Math.Min(255,r+2);g=Math.Min(255,g+12);bv=Math.Min(255,bv+9);}
                pixels[at]=unchecked((int)0xFF000000)|(r<<16)|(g<<8)|bv;
            }
        }
    }
    private void LoadShell()
    {
        using var stream=AssetLoader.Open(new Uri("avares://PRO2ControllerAssistant/Assets/Controller/ns2pro-shell.mesh"));
        using var reader=new BinaryReader(stream);
        if(new string(reader.ReadChars(4))!="N2M1")throw new InvalidDataException("Invalid original N2M1 shell mesh");
        int vertices=reader.ReadInt32(),triangles=reader.ReadInt32();
        if(vertices<1||vertices>100000||triangles<1||triangles>200000)throw new InvalidDataException("Invalid original shell bounds");
        var p=new Vector3[vertices];var n=new Vector3[vertices];
        for(int i=0;i<vertices;i++){p[i]=new(reader.ReadSingle(),reader.ReadSingle(),reader.ReadSingle());n[i]=new(reader.ReadSingle(),reader.ReadSingle(),reader.ReadSingle());reader.ReadSingle();reader.ReadSingle();}
        var faces=new int[triangles*3];for(int i=0;i<faces.Length;i++){faces[i]=reader.ReadInt32();if(faces[i]<0||faces[i]>=vertices)throw new InvalidDataException("Original mesh index exceeds bounds");}
        var groups=reader.ReadBytes(triangles);if(groups.Length!=triangles)throw new EndOfStreamException();
        shellVertices=p;shellIndices=faces;
        for(int material=0;material<4;material++)
        {
            var indices=new List<int>();for(int f=0;f<triangles;f++)if(groups[f]==material){indices.Add(faces[f*3]);indices.Add(faces[f*3+1]);indices.Add(faces[f*3+2]);}
            parts.Add(new(new(p,n,indices.ToArray()),-1,material==3?13:48,_=>Matrix4x4.Identity));
        }
    }
    private static bool Press(GamepadState s,int key)=>s.IsPressed((GamepadButtons)(1UL<<key));
    private void Add(Mesh mesh,int key,float tone,Func<GamepadState,Matrix4x4> transform)=>parts.Add(new(mesh,key,tone,transform));
    private void Build()
    {
        Face(3,40.4f,29.7f,5.85f,"X");Face(2,27.8f,18.7f,5.85f,"Y");Face(1,53,18.7f,5.85f,"A");Face(0,40.4f,7.8f,5.85f,"B");
        Face(8,-18.4f,31.1f,3.5f,"−");Face(9,18.8f,31.1f,3.5f,"+");Face(16,9.5f,18.7f,3.65f,"⌂");Face(20,0,-14.9f,3.25f,"C",true);Face(17,-9.3f,18.7f,3.25f,"○",true);
        Stick(10,-42.5f,18.9f);Stick(11,19,-3.1f);Dpad();
        foreach(int side in new[]{-1,1}){Shoulder(side<0?4:5,side*40,44.5f,9.5f,35,4,false);Shoulder(side<0?6:7,side*40,47,-8.5f,33,7,true);Paddle(side<0?19:18,side*46,-10);}
        FaceSeam();
        var usb=Matrix4x4.CreateRotationX(-MathF.PI/2)*Matrix4x4.CreateTranslation(0,47.8f,0);
        Add(Loft(Rounded(10,3.8f,1.4f),[(1,0),(.88f,.6f),(.001f,.6f)]),-1,36,_=>usb);
        Add(Loft(Rounded(8.5f,2.6f,1),[(1,.65f),(.001f,.65f)]),-1,13,_=>usb);
        var jack=Matrix4x4.CreateRotationX(MathF.PI/2)*Matrix4x4.CreateTranslation(0,-26,3);
        Add(Lathe([(0,0),(2.5f,0),(2.5f,.65f),(1.8f,.65f),(1.6f,0),(0,0)]),-1,13,_=>jack);
        for(int i=0;i<4;i++){float x=-4.5f+i*3;Add(Loft(Rounded(1.4f,.7f,.25f),[(1,0),(.001f,0)]),-1,13,_=>Matrix4x4.CreateTranslation(x,-24,19.5f));}
        marks.Add(new("Nintendo",-1,new(0,23,-26),22,4,_=>Matrix4x4.Identity,true));
    }
    private float FrontSurface(float x,float y)
    {
        float front=-100;
        for(int i=0;i<shellIndices.Length;i+=3)
        {
            var a=shellVertices[shellIndices[i]];var b=shellVertices[shellIndices[i+1]];var c=shellVertices[shellIndices[i+2]];
            if(x<Math.Min(a.X,Math.Min(b.X,c.X))||x>Math.Max(a.X,Math.Max(b.X,c.X))||y<Math.Min(a.Y,Math.Min(b.Y,c.Y))||y>Math.Max(a.Y,Math.Max(b.Y,c.Y)))continue;
            float den=(b.Y-c.Y)*(a.X-c.X)+(c.X-b.X)*(a.Y-c.Y);if(Math.Abs(den)<1e-9)continue;
            float u=((b.Y-c.Y)*(x-c.X)+(c.X-b.X)*(y-c.Y))/den,v=((c.Y-a.Y)*(x-c.X)+(a.X-c.X)*(y-c.Y))/den;
            if(u>=0&&v>=0&&u+v<=1)front=Math.Max(front,u*a.Z+v*b.Z+(1-u-v)*c.Z);
        }
        return front>-50?front:21.8f;
    }
    private void FaceSeam()
    {
        Vector2[] half=[new(0,44.5f),new(26,44),new(46,41),new(56,34),new(60,21),new(59,6),new(52,-9),new(40,-19),new(27,-21.7f),new(0,-21.7f)];
        var contour=Smooth(half.Concat(half.Reverse().Skip(1).SkipLast(1).Select(p=>new Vector2(-p.X,p.Y))).ToArray(),12);
        var p=new List<Vector3>();var indices=new List<int>();
        for(int j=0;j<contour.Length;j++)
        {
            var point=contour[j];var tangent=Vector2.Normalize(contour[(j+1)%contour.Length]-contour[(j+contour.Length-1)%contour.Length]);float z=FrontSurface(point.X,point.Y)+.1f;
            for(int i=0;i<8;i++){float a=i*MathF.PI/4;p.Add(new(point.X-tangent.Y*MathF.Cos(a)*.14f,point.Y+tangent.X*MathF.Cos(a)*.14f,z+MathF.Sin(a)*.14f));}
        }
        for(int j=0;j<contour.Length;j++)for(int i=0;i<8;i++){int a=j*8+i,b=j*8+(i+1)%8,c=(j+1)%contour.Length*8+i,d=(j+1)%contour.Length*8+(i+1)%8;indices.AddRange([a,b,c,b,d,c]);}
        Add(Finish(p.ToArray(),indices.ToArray()),-1,13,_=>Matrix4x4.Identity);
    }
    private void Face(int key,float x,float y,float radius,string text,bool square=false)
    {
        Func<GamepadState,Matrix4x4> fixedPart=_=>Matrix4x4.CreateTranslation(x,y,22);
        Add(square?Loft(Rounded(radius*2+1.5f,radius*2+1.5f,1.3f),[(1,.05f),(1,.4f),(.92f,.82f),(.82f,.82f),(.82f,.05f),(1,.05f)]):Lathe([(radius+.1f,.05f),(radius+.9f,.05f),(radius+.9f,.35f),(radius+.35f,.82f),(radius+.1f,.82f),(radius+.1f,.05f)]),key,13,fixedPart);
        Func<GamepadState,Matrix4x4> transform=s=>Matrix4x4.CreateRotationX(Press(s,key)?-.028f:0)*Matrix4x4.CreateTranslation(x,y,22.5f-(Press(s,key)?1.8f:0));
        Add(square?Loft(Rounded(radius*2,radius*2,1),[(.001f,0),(.94f,0),(1,.5f),(1,1.7f),(.88f,2.3f),(.001f,2.3f)]):Lathe([(0,0),(radius*.88f,0),(radius,.45f),(radius,1),(radius*.96f,1.8f),(radius*.85f,2.15f),(0,2.3f)]),key,48,transform);
        anchors[key]=(new(0,0,2.9f),transform);marks.Add(new(text,key,new(0,0,square?2.85f:2.4f),radius*.95f,radius*1.05f,transform));
    }
    private void Stick(int key,float x,float y)
    {
        Func<GamepadState,Matrix4x4> well=_=>Matrix4x4.CreateTranslation(x,y,22);
        Add(Lathe([(0,0),(14.2f,0),(14.5f,.55f),(14.1f,1.3f),(13.4f,1.9f),(12.3f,2),(11.3f,1),(0,.6f)],96),key,13,well);
        Add(Lathe([(12.3f,1.9f),(13,1.9f),(13.4f,2.2f),(13,2.5f),(12.3f,1.9f)],96),key,36,well);
        Func<GamepadState,Matrix4x4> part=s=>Matrix4x4.CreateRotationX(-((key==10?s.Ly:s.Ry)-2048f)/2048*MathF.PI/12)*Matrix4x4.CreateRotationY(((key==10?s.Lx:s.Rx)-2048f)/2048*MathF.PI/12)*Matrix4x4.CreateTranslation(x,y,23-(Press(s,key)?.9f:0));
        Add(Lathe([(0,0),(4.5f,0),(4.5f,5.5f),(0,5.5f)]),key,13,part);
        Add(Lathe([(0,4.8f),(8.4f,4.8f),(9.3f,5.3f),(9.8f,6.5f),(9.65f,8),(9.1f,8.6f),(8.2f,8.6f),(7,8.05f),(0,7.8f)],96),key,41,part);
        for(int i=0;i<5;i++){float r=8.15f+i*.28f;Add(Lathe([(r,8.5f),(r+.09f,8.58f),(r+.1f,8.69f),(r+.02f,8.73f),(r,8.5f)],96),key,36,part);}
        anchors[key]=(new(0,0,8.6f),part);
    }
    private void Dpad()
    {
        Vector2[] cross=[new(-3.7f,12.2f),new(3.7f,12.2f),new(3.7f,3.7f),new(12.2f,3.7f),new(12.2f,-3.7f),new(3.7f,-3.7f),new(3.7f,-12.2f),new(-3.7f,-12.2f),new(-3.7f,-3.7f),new(-12.2f,-3.7f),new(-12.2f,3.7f),new(-3.7f,3.7f)];
        var outline=Fillet(cross,.7f);
        Func<GamepadState,Matrix4x4> transform=s=>{float dx=(Press(s,13)?1:0)-(Press(s,14)?1:0),dy=(Press(s,15)?1:0)-(Press(s,12)?1:0);return Matrix4x4.CreateRotationX(dy*MathF.PI/36)*Matrix4x4.CreateRotationY(-dx*MathF.PI/36)*Matrix4x4.CreateTranslation(-22.3f,-2.9f,22.5f-.35f*Math.Max(Math.Abs(dx),Math.Abs(dy)));};
        Add(Loft(outline.Select(x=>x*1.13f).ToArray(),[(1,0),(.001f,0)]),-1,13,_=>Matrix4x4.CreateTranslation(-22.3f,-2.9f,22));
        // Each sector uses its own ID so model hit testing selects the physical direction.
        var mesh=Loft(outline,[(.001f,0),(.97f,0),(1,.6f),(1,1.8f),(.93f,2.6f),(.001f,2.6f)]);
        var sectorIndices=new Dictionary<int,List<int>>{{12,[]},{13,[]},{14,[]},{15,[]}};
        for(int i=0;i<mesh.Indices.Length;i+=3){var p=(mesh.Positions[mesh.Indices[i]]+mesh.Positions[mesh.Indices[i+1]]+mesh.Positions[mesh.Indices[i+2]])/3;int key=Math.Abs(p.X)>Math.Abs(p.Y)?p.X>0?13:14:p.Y>0?15:12;sectorIndices[key].AddRange(mesh.Indices.AsSpan(i,3).ToArray());}
        foreach(var pair in sectorIndices)Add(new(mesh.Positions,mesh.Normals,pair.Value.ToArray()),pair.Key,48,transform);
        foreach(var (key,x,y,text) in new[]{(15,0f,9f,"▴"),(12,0f,-9f,"▾"),(14,-9f,0f,"◂"),(13,9f,0f,"▸")}){anchors[key]=(new(x,y,2.8f),transform);marks.Add(new(text,key,new(x,y,2.8f),2,2,transform));}
    }
    private void Shoulder(int key,float x,float y,float z,float width,float height,bool trigger)
    {
        Func<GamepadState,Matrix4x4> transform=s=>Matrix4x4.CreateTranslation(0,height*.35f,0)*Matrix4x4.CreateRotationX(Press(s,key)?-(trigger?13:4)*MathF.PI/180:0)*Matrix4x4.CreateTranslation(x,y-height*.35f,z);
        var contour=Smooth([new(-width*.5f,-height*.5f),new(-width*.44f,height*.15f),new(-width*.2f,height*.65f),new(width*.1f,height*.6f),new(width*.4f,height*.15f),new(width*.5f,-height*.5f),new(0,-height*.42f)],8);
        Add(Loft(contour,[(.001f,-6.8f),(.85f,-6.8f),(1,-4),(1,4),(.88f,6.3f),(.001f,6.3f)]),key,trigger?119:48,transform);
        anchors[key]=(new(0,0,6.4f),transform);marks.Add(new(Views.ViewChoices.PhysicalNames[key],key,new(0,0,6.4f),7,3.3f,transform));
    }
    private void Paddle(int key,float x,float y)
    {
        float z=shellVertices.Where(p=>p.Z<0&&Math.Abs(p.X-x)<1.6&&Math.Abs(p.Y-y)<1.6).Select(p=>p.Z).DefaultIfEmpty(-29).Min();
        Func<GamepadState,Matrix4x4> transform=s=>Matrix4x4.CreateRotationZ((x<0?-38:38)*MathF.PI/180)*Matrix4x4.CreateRotationY(MathF.PI)*Matrix4x4.CreateTranslation(x,y,z-.2f+(Press(s,key)?1.1f:0));
        var outline=Smooth([new(-7,-8),new(5,-8),new(7,-4),new(6,6),new(2,10),new(-4,9),new(-7,3)],7);
        Add(Loft(outline.Select(p=>new Vector2(p.X*1.07f,p.Y*1.05f)).ToArray(),[(.001f,0),(1,0),(1,.7f),(.001f,.7f)]),key,13,transform);
        Add(Loft(outline,[(.001f,.2f),(.94f,.2f),(1,.5f),(.98f,1.2f),(.88f,1.7f),(.001f,1.85f)]),key,48,transform);
        anchors[key]=(new(0,0,2.8f),transform);marks.Add(new(key==19?"GL":"GR",key,new(0,0,1.95f),4.5f,3.1f,transform));
    }
    private static Mesh Finish(Vector3[] p,int[] indices,Vector3[]? normals=null)
    {
        if(normals==null){normals=new Vector3[p.Length];for(int i=0;i<indices.Length;i+=3){int a=indices[i],b=indices[i+1],c=indices[i+2];var normal=Vector3.Cross(p[b]-p[a],p[c]-p[a]);normals[a]+=normal;normals[b]+=normal;normals[c]+=normal;}for(int i=0;i<normals.Length;i++)normals[i]=normals[i].LengthSquared()>1e-14?Vector3.Normalize(normals[i]):Vector3.UnitZ;}
        return new(p,normals,indices);
    }
    private static Mesh Lathe((float R,float Z)[] profile,int segments=64)
    {
        var points=new List<Vector3>();var indices=new List<int>();var normals=new List<Vector3>();
        for(int row=0;row<profile.Length;row++){var p=profile[row];var prev=profile[Math.Max(0,row-1)];var next=profile[Math.Min(profile.Length-1,row+1)];float dr=next.R-prev.R,dz=next.Z-prev.Z;for(int col=0;col<segments;col++){float a=col*MathF.PI*2/segments;points.Add(new(p.R*MathF.Cos(a),p.R*MathF.Sin(a),p.Z));var n=p.R<.01f?new Vector3(0,0,dr<0?1:-1):new Vector3(dz*MathF.Cos(a),dz*MathF.Sin(a),-dr);normals.Add(n.LengthSquared()>1e-12?Vector3.Normalize(n):Vector3.UnitZ);}}
        for(int row=0;row<profile.Length-1;row++)for(int col=0;col<segments;col++){int a=row*segments+col,b=row*segments+(col+1)%segments,c=a+segments,d=b+segments;indices.AddRange([a,b,c,b,d,c]);}
        return Finish(points.ToArray(),indices.ToArray(),normals.ToArray());
    }
    private static Mesh Loft(Vector2[] original,(float Scale,float Z)[] rings)
    {
        var contour=original.ToArray();float area=0;for(int i=0;i<contour.Length;i++)area+=contour[i].X*contour[(i+1)%contour.Length].Y-contour[(i+1)%contour.Length].X*contour[i].Y;if(area<0)Array.Reverse(contour);
        if(rings.Length>3)
        {
            var refined=new List<(float Scale,float Z)>();
            for(int i=0;i<rings.Length-1;i++)
            {
                var a=rings[Math.Max(0,i-1)];var b=rings[i];var c=rings[i+1];var d=rings[Math.Min(rings.Length-1,i+2)];
                for(int j=0;j<3;j++)
                {
                    float t=j/3f,t2=t*t,t3=t2*t;
                    float S(float p,float q,float r,float s)=>.5f*(2*q+(-p+r)*t+(2*p-5*q+4*r-s)*t2+(-p+3*q-3*r+s)*t3);
                    refined.Add((Math.Clamp(S(a.Scale,b.Scale,c.Scale,d.Scale),Math.Min(b.Scale,c.Scale),Math.Max(b.Scale,c.Scale)),Math.Clamp(S(a.Z,b.Z,c.Z,d.Z),Math.Min(b.Z,c.Z),Math.Max(b.Z,c.Z))));
                }
            }
            refined.Add(rings[^1]);rings=refined.ToArray();
        }
        var points=new List<Vector3>();var indices=new List<int>();foreach(var ring in rings)foreach(var p in contour)points.Add(new(p.X*ring.Scale,p.Y*ring.Scale,ring.Z));
        bool reverse=rings[^1].Z>rings[0].Z||(rings[^1].Z==rings[0].Z&&rings[^1].Scale<rings[0].Scale);int n=contour.Length;
        for(int row=0;row<rings.Length-1;row++)for(int col=0;col<n;col++){int a=row*n+col,b=row*n+(col+1)%n,c=a+n,d=b+n;indices.AddRange(reverse?[a,b,c,b,d,c]:[a,c,b,b,c,d]);}
        return Finish(points.ToArray(),indices.ToArray());
    }
    private static Vector2[] Smooth(Vector2[] points,int steps)
    {
        var result=new List<Vector2>();for(int i=0;i<points.Length;i++){var a=points[(i+points.Length-1)%points.Length];var b=points[i];var c=points[(i+1)%points.Length];var d=points[(i+2)%points.Length];for(int j=0;j<steps;j++){float t=j/(float)steps,t2=t*t,t3=t2*t;result.Add(.5f*(2*b+(-a+c)*t+(2*a-5*b+4*c-d)*t2+(-a+3*b-3*c+d)*t3));}}return result.ToArray();
    }
    private static Vector2[] Rounded(float width,float height,float radius)
    {
        var result=new List<Vector2>();foreach(var (x,y,start) in new[]{(width/2-radius,height/2-radius,0f),(-width/2+radius,height/2-radius,90f),(-width/2+radius,-height/2+radius,180f),(width/2-radius,-height/2+radius,270f)})for(int i=0;i<9;i++){float a=(start+i*90f/8)*MathF.PI/180;result.Add(new(x+radius*MathF.Cos(a),y+radius*MathF.Sin(a)));}return result.ToArray();
    }
    private static Vector2[] Fillet(Vector2[] points,float radius)
    {
        var result=new List<Vector2>();for(int i=0;i<points.Length;i++){var p=points[i];var a=p+Vector2.Normalize(points[(i+points.Length-1)%points.Length]-p)*radius;var b=p+Vector2.Normalize(points[(i+1)%points.Length]-p)*radius;for(int j=0;j<=6;j++){float t=j/6f;result.Add((1-t)*(1-t)*a+2*(1-t)*t*p+t*t*b);}}return result.ToArray();
    }
    public void Dispose()=>bitmap?.Dispose();
}
