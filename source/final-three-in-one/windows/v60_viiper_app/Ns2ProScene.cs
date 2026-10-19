using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using Point = System.Windows.Point;
using Color = System.Windows.Media.Color;
using ColorConverter = System.Windows.Media.ColorConverter;
using Brush = System.Windows.Media.Brush;

namespace Y700Switch2V60Viiper;

// Original, self-contained geometric reconstruction. Millimetre-scale dimensions,
// no photographed shell or external model/runtime. Immutable meshes/materials are
// frozen for retained rendering. Mechanical transforms and each face cap's
// contact shading change while a physical key travels into its recess.
public sealed class Ns2ProScene
{
    public Viewport3D Viewport { get; } = new() { ClipToBounds = false };
    private readonly Model3DGroup assembly = new();
    private Point3D[] shellVertices = [];
    private int[] shellTriangles = [];
    private readonly Dictionary<GeometryModel3D, GamepadButtons> hitKeys = new();
    private readonly Dictionary<GamepadButtons, (Point3D Point, Transform3D Part)> anchors = new();
    private readonly Dictionary<GamepadButtons, TranslateTransform3D> plungers = new();
    private sealed record FaceActuator(TranslateTransform3D Press,AxisAngleRotation3D Tilt,
        SolidColorBrush Diffuse,SolidColorBrush Specular);
    private readonly Dictionary<GamepadButtons, FaceActuator> faceActuators = new();
    private readonly Dictionary<GamepadButtons, AxisAngleRotation3D> hinges = new();
    private readonly Dictionary<GamepadButtons, (AxisAngleRotation3D X, AxisAngleRotation3D Y, TranslateTransform3D Press)> sticks = new();
    private readonly AxisAngleRotation3D dpadX = new(new(1,0,0),0), dpadY = new(new(0,1,0),0);
    private readonly TranslateTransform3D dpadPress = new();
    private readonly AxisAngleRotation3D yaw = new(new(0,1,0),0), pitch = new(new(1,0,0),0);
    private readonly Transform3DGroup pose = new();
    private readonly bool rear;
    private readonly OrthographicCamera camera = new(new(0,17,280),new(0,-20,-280),new(0,1,0),166);
    private static readonly Material Face = Plastic("#303030", "#606060", 24);
    private static readonly Material Dark = Plastic("#0D0E0E", "#292929", 28);
    private static readonly Material Cap = Plastic("#303030", "#6A6A6A", 32);
    private static readonly Material Rim = Plastic("#242424", "#515151", 38);
    private static readonly Material Rubber = Plastic("#292929", "#444444", 14);
    private static readonly Material Grip = Plastic("#303030", "#606060", 24);
    private static readonly Material Back = Plastic("#303030", "#606060", 24);
    private static readonly Material ShoulderCap = Plastic("#77797B", "#757575", 22);
    public int TriangleCount { get; private set; }
    public int MeshCount => hitKeys.Count;
    public bool HasKey(GamepadButtons key) => anchors.ContainsKey(key);

    public Ns2ProScene(bool rearView)
    {
        rear = rearView;
        Viewport.Camera = camera;
        RenderOptions.SetEdgeMode(Viewport, EdgeMode.Unspecified);
        var lights = new Model3DGroup();
        lights.Children.Add(new AmbientLight(C("#565656")));
        lights.Children.Add(new DirectionalLight(C("#E1DFDB"),new(-.6,-.8,-1)));
        lights.Children.Add(new DirectionalLight(C("#777A7E"),new(.9,.15,-.5)));
        lights.Children.Add(new DirectionalLight(C("#CFD0D1"),new(-.5,-.7,1)));
        lights.Freeze(); Viewport.Children.Add(new ModelVisual3D { Content = lights });
        pose.Children.Add(new RotateTransform3D(yaw)); pose.Children.Add(new RotateTransform3D(pitch));
        assembly.Transform = pose;
        Build(); ResetView();
        Viewport.Children.Add(new ModelVisual3D { Content = assembly });
    }
    private static Color C(string value) => (Color)ColorConverter.ConvertFromString(value);
    private static Material Plastic(string color, string shine, double power, bool grain = false)
    {
        Brush diffuse = new SolidColorBrush(C(color));
        if (grain)
        {
            var drawing = new DrawingGroup();
            using (var dc = drawing.Open())
            {
                dc.DrawRectangle(diffuse,null,new Rect(0,0,32,32));
                var random = new Random(218);
                for (int i=0;i<95;i++)
                {
                    double x=random.NextDouble()*32,y=random.NextDouble()*32;
                    dc.DrawEllipse(new SolidColorBrush(Color.FromArgb(32,6,9,12)),null,new(x,y),.55,.55);
                    dc.DrawEllipse(new SolidColorBrush(Color.FromArgb(20,191,200,208)),null,new(x-.12,y-.2),.2,.2);
                }
            }
            drawing.Freeze();
            diffuse = new DrawingBrush(drawing) { TileMode=TileMode.Tile, ViewportUnits=BrushMappingMode.RelativeToBoundingBox,
                Viewport = new Rect(0,0,.11,.11), Stretch=Stretch.Fill };
        }
        diffuse.Freeze();
        var material = new MaterialGroup(); material.Children.Add(new DiffuseMaterial(diffuse));
        material.Children.Add(new SpecularMaterial(new SolidColorBrush(C(shine)),power));
        material.Freeze(); return material;
    }
    private GeometryModel3D Add(MeshGeometry3D mesh, Material material, Model3DGroup? parent = null, GamepadButtons key = GamepadButtons.None)
    {
        TriangleCount += mesh.TriangleIndices.Count/3;
        var model = new GeometryModel3D(mesh,material);
        (parent ?? assembly).Children.Add(model); hitKeys[model]=key; return model;
    }
    private void BuildShell()
    {
        var resource=System.Windows.Application.GetResourceStream(new Uri("pack://application:,,,/PRO2ControllerAssistant;component/Assets/Controller/ns2pro-shell.mesh"))!;
        using var reader=new BinaryReader(resource.Stream);
        if(new string(reader.ReadChars(4))!="N2M1") throw new InvalidDataException("Invalid embedded shell mesh");
        int vertexCount=reader.ReadInt32(),triangleCount=reader.ReadInt32();
        if(vertexCount is <1 or >100000 || triangleCount is <1 or >200000) throw new InvalidDataException("Invalid shell mesh bounds");
        var positions=new Point3D[vertexCount];var normals=new Vector3D[vertexCount];var uv=new Point[vertexCount];
        for(int i=0;i<vertexCount;i++)
        {
            positions[i]=new(reader.ReadSingle(),reader.ReadSingle(),reader.ReadSingle());
            normals[i]=new(reader.ReadSingle(),reader.ReadSingle(),reader.ReadSingle());uv[i]=new(reader.ReadSingle(),reader.ReadSingle());
        }
        var faces=new int[triangleCount*3];for(int i=0;i<faces.Length;i++)faces[i]=reader.ReadInt32();
        shellVertices=positions;
        shellTriangles=faces;
        var groups=reader.ReadBytes(triangleCount);
        var materials=new[] {Face,Grip,Back,Dark};
        for(int material=0;material<materials.Length;material++)
        {
            var mesh=new MeshGeometry3D();var map=new Dictionary<int,int>();
            for(int face=0;face<triangleCount;face++) if(groups[face]==material)
                for(int corner=0;corner<3;corner++)
                {
                    int index=faces[face*3+corner];
                    if(!map.TryGetValue(index,out int compact))
                    {compact=map.Count;map[index]=compact;mesh.Positions.Add(positions[index]);mesh.Normals.Add(normals[index]);mesh.TextureCoordinates.Add(uv[index]);}
                    mesh.TriangleIndices.Add(compact);
                }
            mesh.Freeze();Add(mesh,materials[material]);
        }
    }
    private void Build()
    {
        BuildShell();
        // Centres and radii measured from the supplied straight-on reference.
        FaceButton(GamepadButtons.North,40.4,29.7,5.85,"X"); FaceButton(GamepadButtons.West,27.8,18.7,5.85,"Y");
        FaceButton(GamepadButtons.East,53,18.7,5.85,"A"); FaceButton(GamepadButtons.South,40.4,7.8,5.85,"B");
        FaceButton(GamepadButtons.Back,-18.4,31.1,3.5,"−"); FaceButton(GamepadButtons.Start,18.8,31.1,3.5,"+");
        FaceButton(GamepadButtons.Home,9.5,18.7,3.65,"⌂"); FaceButton(GamepadButtons.Aux,0,-14.9,3.25,"C",true);
        FaceButton(GamepadButtons.Capture,-9.3,18.7,3.25,"○",true);
        Stick(GamepadButtons.LeftStick,-42.5,18.9); Stick(GamepadButtons.RightStick,19,-3.1);
        Dpad();
        foreach (int side in new[] {-1,1})
        {
            Shoulder(side<0 ? GamepadButtons.L1 : GamepadButtons.R1,side*40,44.5,9.5,35,4,false);
            Shoulder(side<0 ? GamepadButtons.L2 : GamepadButtons.R2,side*40,47,-8.5,33,7,true);
            RearPaddle(side<0 ? GamepadButtons.PaddleLeft : GamepadButtons.PaddleRight,side*46,-10);
        }
        FaceSeam();
        SwitchMark();
        Decal(assembly,"Nintendo",0,23,-26,22,6,true,"#181818");
        // Physical connector details are modelled too: metal rim and dark recess.
        var usb = new Model3DGroup(); usb.Transform = new Transform3DGroup { Children =
            {new RotateTransform3D(new AxisAngleRotation3D(new(1,0,0),-90)),new TranslateTransform3D(0,47.8,0)} };
        assembly.Children.Add(usb);
        Add(Loft(Rounded(10,3.8,1.4),new(),[(1,0),(.88,.6),(.001,.6)]),Rim,usb);
        Add(Loft(Rounded(8.5,2.6,1),new(),[(1,.65),(.001,.65)]),Dark,usb);
        var jack = new Model3DGroup { Transform = new Transform3DGroup { Children =
            {new RotateTransform3D(new AxisAngleRotation3D(new(1,0,0),90)),new TranslateTransform3D(0,-26,3)} } };
        assembly.Children.Add(jack); Add(Lathe([(0,0),(2.5,0),(2.5,.65),(1.8,.65),(1.6,0),(0,0)]),Dark,jack);
        for(int i=0;i<4;i++)
        {
            var led = new Model3DGroup { Transform = new TranslateTransform3D(-4.5+i*3,-24,19.5) };
            assembly.Children.Add(led); Add(Loft(Rounded(1.4,.7,.25),new(),[(1,0),(.001,0)]),Dark,led);
        }
    }
    private double FrontSurface(double x,double y)
    {
        double front=-100;
        for(int i=0;i<shellTriangles.Length;i+=3)
        {
            var a=shellVertices[shellTriangles[i]];var b=shellVertices[shellTriangles[i+1]];var c=shellVertices[shellTriangles[i+2]];
            if(x<Math.Min(a.X,Math.Min(b.X,c.X)) || x>Math.Max(a.X,Math.Max(b.X,c.X)) ||
               y<Math.Min(a.Y,Math.Min(b.Y,c.Y)) || y>Math.Max(a.Y,Math.Max(b.Y,c.Y)))continue;
            double den=(b.Y-c.Y)*(a.X-c.X)+(c.X-b.X)*(a.Y-c.Y);if(Math.Abs(den)<1e-9)continue;
            double u=((b.Y-c.Y)*(x-c.X)+(c.X-b.X)*(y-c.Y))/den;
            double v=((c.Y-a.Y)*(x-c.X)+(a.X-c.X)*(y-c.Y))/den;
            if(u>=0 && v>=0 && u+v<=1)front=Math.Max(front,u*a.Z+v*b.Z+(1-u-v)*c.Z);
        }
        return front>-50 ? front : 21.8;
    }
    private void FaceSeam()
    {
        Point[] half=[new(0,44.5),new(26,44),new(46,41),new(56,34),new(60,21),new(59,6),new(52,-9),new(40,-19),new(27,-21.7),new(0,-21.7)];
        var points=half.Concat(half.Reverse().Skip(1).SkipLast(1).Select(p=>new Point(-p.X,p.Y))).ToArray();
        var path=Smooth(points,12);
        var mesh=new MeshGeometry3D();
        for(int j=0;j<path.Length;j++)
        {
            var p=path[j];var tangent=path[(j+1)%path.Length]-path[(j+path.Length-1)%path.Length];tangent.Normalize();
            double z=FrontSurface(p.X,p.Y)+.1;
            for(int i=0;i<8;i++)
            {
                double a=i*Math.PI/4;
                mesh.Positions.Add(new(p.X-tangent.Y*Math.Cos(a)*.14,p.Y+tangent.X*Math.Cos(a)*.14,z+Math.Sin(a)*.14));
            }
        }
        for(int j=0;j<path.Length;j++)for(int i=0;i<8;i++)
        {
            int a=j*8+i,b=j*8+(i+1)%8,c=(j+1)%path.Length*8+i,d=(j+1)%path.Length*8+(i+1)%8;
            foreach(int index in new[]{a,b,c,b,d,c})mesh.TriangleIndices.Add(index);
        }
        Add(Finish(mesh),Dark);
    }
    private void SwitchMark()
    {
        // Original Nintendo vector paths preserve the icon's negative spaces,
        // numeral and lettering. The mark is printed flat on the moulded face.
        var resource=System.Windows.Application.GetResourceStream(new Uri("pack://application:,,,/PRO2ControllerAssistant;component/Assets/Controller/switch2-official-mark.svg"))!;
        using var stream=resource.Stream;
        var document=System.Xml.Linq.XDocument.Load(stream);
        var data=document.Descendants().Single(e=>e.Name.LocalName=="path").Attribute("d")!.Value;
        var outline=PathGeometry.CreateFromGeometry(Geometry.Parse(data));
        outline.FillRule=FillRule.EvenOdd;outline.Freeze();
        var drawing=new GeometryDrawing(new SolidColorBrush(C("#1B1B1B")),null,outline);drawing.Freeze();
        var brush=new DrawingBrush(drawing) {Viewbox=outline.Bounds,ViewboxUnits=BrushMappingMode.Absolute,Stretch=Stretch.Fill};brush.Freeze();
        var material=new DiffuseMaterial(brush);material.Freeze();
        double width=18.2,height=width*outline.Bounds.Height/outline.Bounds.Width,cy=34.4;
        var mesh=new MeshGeometry3D {
            Positions=new Point3DCollection {new(-width/2,cy-height/2,22.08),new(width/2,cy-height/2,22.08),
                new(width/2,cy+height/2,22.08),new(-width/2,cy+height/2,22.08)},
            TextureCoordinates=new PointCollection {new(0,1),new(1,1),new(1,0),new(0,0)},
            TriangleIndices=new Int32Collection {0,1,2,0,2,3}};
        mesh.Freeze();Add(mesh,material);
    }
    private void FaceButton(GamepadButtons key, double x,double y,double radius,string text,bool square=false)
    {
        var hole = new Model3DGroup { Transform = new TranslateTransform3D(x,y,22) }; assembly.Children.Add(hole);
        // A stationary raised lip makes the sidewall disappear as the key sinks.
        // A flat black disc underneath a translating cap had no visible travel
        // when viewed nearly head-on with an orthographic camera.
        Add(square ? Loft(Rounded(radius*2+1.5,radius*2+1.5,1.3),new(),
            [(1,.05),(1,.4),(.92,.82),(.82,.82),(.82,.05),(1,.05)]) :
            Lathe([(radius+.1,.05),(radius+.9,.05),(radius+.9,.35),
                (radius+.35,.82),(radius+.1,.82),(radius+.1,.05)]),Dark,hole,key);
        var motion = new TranslateTransform3D();
        var tilt = new AxisAngleRotation3D(new(1,0,0),0);
        var diffuse=new SolidColorBrush(C("#303030"));var specular=new SolidColorBrush(C("#6A6A6A"));
        var capMaterial=new MaterialGroup();capMaterial.Children.Add(new DiffuseMaterial(diffuse));
        capMaterial.Children.Add(new SpecularMaterial(specular,32));
        faceActuators[key]=new(motion,tilt,diffuse,specular);
        var part = new Model3DGroup { Transform = new Transform3DGroup { Children={
            new RotateTransform3D(tilt,new Point3D(0,-radius*.35,1.4)),motion,new TranslateTransform3D(x,y,22.5)} } };
        assembly.Children.Add(part);
        Add(square ? Loft(Rounded(radius*2,radius*2,1),new(),[(.001,0),(.94,0),(1,.5),(1,1.7),(.88,2.3),(.001,2.3)]) :
            Lathe([(0,0),(radius*.88,0),(radius,.45),(radius,1),(radius*.96,1.8),(radius*.85,2.15),(0,2.3)]),capMaterial,part,key);
        Decal(part,text,0,0,square ? 2.85 : 2.4,radius*.95,radius*1.05,false,
            key is GamepadButtons.Back or GamepadButtons.Start or GamepadButtons.Home or GamepadButtons.Capture or GamepadButtons.Aux ? "#101010" : "#C2C1BF",key);
        anchors[key]=(new(0,0,2.9),part.Transform);
    }
    private void Stick(GamepadButtons key,double x,double y)
    {
        var well = new Model3DGroup { Transform=new TranslateTransform3D(x,y,22) }; assembly.Children.Add(well);
        Add(Lathe([(0,0),(14.2,0),(14.5,.55),(14.1,1.3),(13.4,1.9),(12.3,2),(11.3,1),(0,.6)],96),Dark,well,key);
        Add(Lathe([(12.3,1.9),(13,1.9),(13.4,2.2),(13,2.5),(12.3,1.9)],96),Rim,well,key);
        var rx=new AxisAngleRotation3D(new(1,0,0),0); var ry=new AxisAngleRotation3D(new(0,1,0),0);
        var press=new TranslateTransform3D(); sticks[key]=(rx,ry,press);
        var transforms = new Transform3DGroup { Children={new RotateTransform3D(rx),new RotateTransform3D(ry),press,new TranslateTransform3D(x,y,23)} };
        var part=new Model3DGroup { Transform=transforms }; assembly.Children.Add(part);
        Add(Lathe([(0,0),(4.5,0),(4.5,5.5),(0,5.5)]),Dark,part,key);
        Add(Lathe([(0,4.8),(8.4,4.8),(9.3,5.3),(9.8,6.5),(9.65,8),(9.1,8.6),(8.2,8.6),(7,8.05),(0,7.8)],96),Rubber,part,key);
        for(int i=0;i<5;i++)
        {
            double r=8.15+i*.28,z=8.58;
            Add(Lathe([(r,z-.08),(r+.09,z),(r+.1,z+.11),(r+.02,z+.15),(r,z-.08)],96),Rim,part,key);
        }
        anchors[key]=(new(0,0,8.6),transforms);
    }
    private void Dpad()
    {
        Point[] cross = [new(-3.7,12.2),new(3.7,12.2),new(3.7,3.7),new(12.2,3.7),new(12.2,-3.7),new(3.7,-3.7),
            new(3.7,-12.2),new(-3.7,-12.2),new(-3.7,-3.7),new(-12.2,-3.7),new(-12.2,3.7),new(-3.7,3.7)];
        cross=Fillet(cross,.7);
        var baseGroup = new Model3DGroup { Transform=new TranslateTransform3D(-22.3,-2.9,22) }; assembly.Children.Add(baseGroup);
        Add(Loft(cross.Select(p=>new Point(p.X*1.13,p.Y*1.13)).ToArray(),new(),[(1,0),(.001,0)]),Dark,baseGroup);
        var transform = new Transform3DGroup { Children={new RotateTransform3D(dpadX),new RotateTransform3D(dpadY),dpadPress,new TranslateTransform3D(-22.3,-2.9,22.5)} };
        var part = new Model3DGroup { Transform=transform }; assembly.Children.Add(part);
        Add(Loft(cross,new(),[(.001,0),(.97,0),(1,.6),(1,1.8),(.93,2.6),(.001,2.6)]),Cap,part,GamepadButtons.DPadUp|GamepadButtons.DPadDown|GamepadButtons.DPadLeft|GamepadButtons.DPadRight);
        foreach(var (key,x,y,label) in new[] {(GamepadButtons.DPadUp,0d,9d,"˄"),(GamepadButtons.DPadDown,0d,-9d,"˅"),
            (GamepadButtons.DPadLeft,-9d,0d,"‹"),(GamepadButtons.DPadRight,9d,0d,"›")})
        {
            var mark=new Model3DGroup {Transform=new TranslateTransform3D(x,y,2.74)};part.Children.Add(mark);
            double angle=key switch {GamepadButtons.DPadRight=>-90,GamepadButtons.DPadDown=>180,GamepadButtons.DPadLeft=>90,_=>0};
            mark.Transform=new Transform3DGroup { Children={new RotateTransform3D(new AxisAngleRotation3D(new(0,0,1),angle)),new TranslateTransform3D(x,y,2.74)} };
            Add(Loft([new(-.7,-.4),new(.7,-.4),new(0,.8)],new(),[(1,0),(.001,0)]),Plastic("#A3A3A3","#777777",20),mark,key);
            anchors[key]=(new(x,y,2.8),transform);
        }
    }
    private void Shoulder(GamepadButtons key,double x,double y,double z,double width,double height,bool trigger)
    {
        var hinge = new AxisAngleRotation3D(new(1,0,0),0); hinges[key]=hinge;
        var transforms = new Transform3DGroup { Children={new RotateTransform3D(hinge,new Point3D(0,-height*.35,0)),new TranslateTransform3D(x,y,z)} };
        var part = new Model3DGroup { Transform=transforms }; assembly.Children.Add(part);
        var contour=Smooth([new(-width*.5,-height*.5),new(-width*.44,height*.15),new(-width*.2,height*.65),
            new(width*.1,height*.6),new(width*.4,height*.15),new(width*.5,-height*.5),new(0,-height*.42)],8);
        Add(Loft(contour,new(),[(.001,-6.8),(.85,-6.8),(1,-4),(1,4),(.88,6.3),(.001,6.3)]),trigger ? ShoulderCap : Cap,part,key);
        string label=key switch {GamepadButtons.L1=>"L",GamepadButtons.R1=>"R",GamepadButtons.L2=>"ZL",_=>"ZR"};
        Decal(part,label,0,0,6.4,7,3.3,false,"#575859",key);
        anchors[key]=(new(0,0,6.4),transforms);
    }
    private double RearSurface(double x,double y) => shellVertices.Where(p=>p.Z<0 && Math.Abs(p.X-x)<1.6 && Math.Abs(p.Y-y)<1.6).Select(p=>p.Z).DefaultIfEmpty(-29).Min();
    private void RearPaddle(GamepadButtons key,double x,double y)
    {
        var motion=new TranslateTransform3D(); plungers[key]=motion;
        var transforms=new Transform3DGroup { Children={new RotateTransform3D(new AxisAngleRotation3D(new(0,0,1),x<0 ? -38 : 38)),
            new RotateTransform3D(new AxisAngleRotation3D(new(0,1,0),180)),motion,new TranslateTransform3D(x,y,RearSurface(x,y)-.2)} };
        var part=new Model3DGroup { Transform=transforms }; assembly.Children.Add(part);
        var outline=Smooth([new(-7,-8),new(5,-8),new(7,-4),new(6,6),new(2,10),new(-4,9),new(-7,3)],7);
        Add(Loft(outline.Select(p=>new Point(p.X*1.07,p.Y*1.05)).ToArray(),new(),[(.001,0),(1,0),(1,.7),(.001,.7)]),Dark,part,key);
        Add(Loft(outline,new(),[(.001,.2),(.94,.2),(1,.5),(.98,1.2),(.88,1.7),(.001,1.85)]),Cap,part,key);
        Decal(part,key==GamepadButtons.PaddleLeft ? "GL" : "GR",0,0,1.95,4.5,3.1,false,"#191919",key);
        anchors[key]=(new(0,0,2.8),transforms);
    }
    private void Decal(Model3DGroup parent,string text,double x,double y,double z,double width,double height,bool reverse,string color,GamepadButtons key=0)
    {
        var ft=new FormattedText(text,CultureInfo.InvariantCulture,System.Windows.FlowDirection.LeftToRight,new Typeface("Segoe UI"),64,new SolidColorBrush(C(color)),1);
        var drawing=new DrawingGroup(); using(var dc=drawing.Open()) dc.DrawText(ft,new Point(0,0)); drawing.Freeze();
        var brush=new DrawingBrush(drawing) { Stretch=Stretch.Uniform }; brush.Freeze();
        var mat=new MaterialGroup(); mat.Children.Add(new DiffuseMaterial(brush));
        mat.Children.Add(new EmissiveMaterial(brush) {Color=Color.FromRgb(85,85,85)}); mat.Freeze();
        var mesh=new MeshGeometry3D { Positions=new Point3DCollection {new(x-width/2,y-height/2,z),new(x+width/2,y-height/2,z),new(x+width/2,y+height/2,z),new(x-width/2,y+height/2,z)},
            TextureCoordinates=new PointCollection {new(0,1),new(1,1),new(1,0),new(0,0)},TriangleIndices=new Int32Collection {0,1,2,0,2,3} };
        if(reverse) { mesh.TriangleIndices=new Int32Collection {2,1,0,3,2,0}; mesh.TextureCoordinates=new PointCollection {new(1,1),new(0,1),new(0,0),new(1,0)}; }
        mesh.Freeze(); Add(mesh,mat,parent,key);
    }
    public void Update(GamepadState state, Func<GamepadButtons,double> travel)
    {
        foreach(var (key,actuator) in faceActuators)
        {
            double t=Math.Clamp(travel(key),0,1);
            actuator.Press.OffsetZ=-1.8*t;
            actuator.Tilt.Angle=-1.6*t;
            // Approximate the loss of incident light in the shallow button well.
            // This stays attached to the actual moving cap, without an overlay halo.
            byte diffuse=(byte)Math.Round(48*(1-.34*t)),specular=(byte)Math.Round(106*(1-.42*t));
            actuator.Diffuse.Color=Color.FromRgb(diffuse,diffuse,diffuse);
            actuator.Specular.Color=Color.FromRgb(specular,specular,specular);
        }
        foreach(var (key,move) in plungers) move.OffsetZ = (key is GamepadButtons.PaddleLeft or GamepadButtons.PaddleRight ? 1.1 : -1.05)*travel(key);
        foreach(var (key,hinge) in hinges) hinge.Angle=-(key is GamepadButtons.L2 or GamepadButtons.R2 ? 13 : 4)*travel(key);
        foreach(var (key,s) in sticks)
        {
            bool left=key==GamepadButtons.LeftStick;
            s.X.Angle=-ControllerTestSnapshot.Axis(left ? state.Ly : state.Ry)*15;
            s.Y.Angle=ControllerTestSnapshot.Axis(left ? state.Lx : state.Rx)*15;
            s.Press.OffsetZ=-.9*travel(key);
        }
        double dx=travel(GamepadButtons.DPadRight)-travel(GamepadButtons.DPadLeft),dy=travel(GamepadButtons.DPadUp)-travel(GamepadButtons.DPadDown);
        dpadX.Angle=dy*5; dpadY.Angle=-dx*5; dpadPress.OffsetZ=-.35*Math.Max(Math.Abs(dx),Math.Abs(dy));
    }
    public void ResetView() { yaw.Angle=rear ? 180 : 0; pitch.Angle=0; camera.Width=166; }
    public void Orbit(double horizontal,double vertical) { yaw.Angle+=horizontal; pitch.Angle=Math.Clamp(pitch.Angle+vertical,-32,32); }
    public void Zoom(double amount) => camera.Width=Math.Clamp(camera.Width+amount,145,220);
    public Point Project(GamepadButtons key)
    {
        var anchor=anchors[key]; var p=pose.Transform(anchor.Part.Transform(anchor.Point));
        var look=camera.LookDirection; look.Normalize(); var right=Vector3D.CrossProduct(look,camera.UpDirection); right.Normalize();
        var up=Vector3D.CrossProduct(right,look); up.Normalize(); var delta=p-camera.Position;
        double scale=Viewport.Width/camera.Width;
        return new(Viewport.Width*.5+Vector3D.DotProduct(delta,right)*scale,Viewport.Height*.5-Vector3D.DotProduct(delta,up)*scale);
    }
    public GamepadButtons Hit(Point point)
    {
        GamepadButtons found=0;
        VisualTreeHelper.HitTest(Viewport,null,result=>
        {
            if(result is not RayMeshGeometry3DHitTestResult ray) return HitTestResultBehavior.Continue;
            if(ray.ModelHit is not GeometryModel3D model || !hitKeys.TryGetValue(model,out var key) || key==0) return HitTestResultBehavior.Stop;
            if(key==(GamepadButtons.DPadUp|GamepadButtons.DPadDown|GamepadButtons.DPadLeft|GamepadButtons.DPadRight))
            {
                // PointHit is in the mesh coordinate system before its mechanical transform.
                found=Math.Abs(ray.PointHit.X)>Math.Abs(ray.PointHit.Y) ? ray.PointHit.X>0 ? GamepadButtons.DPadRight : GamepadButtons.DPadLeft :
                    ray.PointHit.Y>0 ? GamepadButtons.DPadUp : GamepadButtons.DPadDown;
            }
            else found=key;
            return HitTestResultBehavior.Stop;
        },new PointHitTestParameters(point));
        return found;
    }
    private static Point[] Smooth(Point[] points,int steps)
    {
        var result=new List<Point>();
        for(int i=0;i<points.Length;i++)
        {
            var a=points[(i+points.Length-1)%points.Length]; var b=points[i]; var c=points[(i+1)%points.Length]; var d=points[(i+2)%points.Length];
            for(int j=0;j<steps;j++)
            {
                double t=j/(double)steps,t2=t*t,t3=t2*t;
                result.Add(new(.5*((2*b.X)+(-a.X+c.X)*t+(2*a.X-5*b.X+4*c.X-d.X)*t2+(-a.X+3*b.X-3*c.X+d.X)*t3),
                    .5*((2*b.Y)+(-a.Y+c.Y)*t+(2*a.Y-5*b.Y+4*c.Y-d.Y)*t2+(-a.Y+3*b.Y-3*c.Y+d.Y)*t3)));
            }
        }
        return result.ToArray();
    }
    private static Point[] Rounded(double width,double height,double radius)
    {
        var result=new List<Point>();
        foreach(var (x,y,start) in new[] {(width/2-radius,height/2-radius,0d),(-width/2+radius,height/2-radius,90d),
            (-width/2+radius,-height/2+radius,180d),(width/2-radius,-height/2+radius,270d)})
            for(int i=0;i<9;i++) {double angle=(start+i*90.0/8)*Math.PI/180; result.Add(new(x+radius*Math.Cos(angle),y+radius*Math.Sin(angle)));}
        return result.ToArray();
    }
    private static Point[] Fillet(Point[] points,double radius)
    {
        var result=new List<Point>();
        for(int i=0;i<points.Length;i++)
        {
            var p=points[i];var incoming=points[(i+points.Length-1)%points.Length]-p;
            var outgoing=points[(i+1)%points.Length]-p;incoming.Normalize();outgoing.Normalize();
            var a=p+incoming*radius;var b=p+outgoing*radius;
            for(int j=0;j<=6;j++)
            {
                double t=j/6d;result.Add(new((1-t)*(1-t)*a.X+2*(1-t)*t*p.X+t*t*b.X,
                    (1-t)*(1-t)*a.Y+2*(1-t)*t*p.Y+t*t*b.Y));
            }
        }
        return result.ToArray();
    }
    private static MeshGeometry3D Loft(Point[] contour,Point center,(double Scale,double Z)[] rings)
    {
        // Make every contour counter-clockwise so winding/normals remain stable after mirroring.
        double area=0; for(int i=0;i<contour.Length;i++) area+=contour[i].X*contour[(i+1)%contour.Length].Y-contour[(i+1)%contour.Length].X*contour[i].Y;
        if(area<0) contour=contour.Reverse().ToArray();
        if(rings.Length>3)
        {
            var refined=new List<(double Scale,double Z)>();
            for(int i=0;i<rings.Length-1;i++)
            {
                var a=rings[Math.Max(0,i-1)];var b=rings[i];var c=rings[i+1];var d=rings[Math.Min(rings.Length-1,i+2)];
                for(int j=0;j<3;j++)
                {
                    double t=j/3.0,t2=t*t,t3=t2*t;
                    double S(double p,double q,double r,double s)=>.5*(2*q+(-p+r)*t+(2*p-5*q+4*r-s)*t2+(-p+3*q-3*r+s)*t3);
                    refined.Add((Math.Clamp(S(a.Scale,b.Scale,c.Scale,d.Scale),Math.Min(b.Scale,c.Scale),Math.Max(b.Scale,c.Scale)),
                        Math.Clamp(S(a.Z,b.Z,c.Z,d.Z),Math.Min(b.Z,c.Z),Math.Max(b.Z,c.Z))));
                }
            }
            refined.Add(rings[^1]);rings=refined.ToArray();
        }
        var mesh=new MeshGeometry3D(); int n=contour.Length;
        foreach(var ring in rings) foreach(var p in contour)
        {
            mesh.Positions.Add(new(center.X+(p.X-center.X)*ring.Scale,center.Y+(p.Y-center.Y)*ring.Scale,ring.Z));
            mesh.TextureCoordinates.Add(new((p.X+80)/160,(p.Y+60)/120));
        }
        for(int row=0;row<rings.Length-1;row++) for(int col=0;col<n;col++)
        {
            int a=row*n+col,b=row*n+(col+1)%n,c=(row+1)*n+col,d=(row+1)*n+(col+1)%n;
            mesh.TriangleIndices.Add(a);mesh.TriangleIndices.Add(c);mesh.TriangleIndices.Add(b);
            mesh.TriangleIndices.Add(b);mesh.TriangleIndices.Add(c);mesh.TriangleIndices.Add(d);
        }
        bool reverse = rings[^1].Z > rings[0].Z || (rings[^1].Z == rings[0].Z && rings[^1].Scale < rings[0].Scale);
        if(reverse) for(int i=0;i<mesh.TriangleIndices.Count;i+=3)
            (mesh.TriangleIndices[i+1],mesh.TriangleIndices[i+2])=(mesh.TriangleIndices[i+2],mesh.TriangleIndices[i+1]);
        return Finish(mesh);
    }
    private static MeshGeometry3D Lathe((double R,double Z)[] profile,int segments=64)
    {
        var mesh=new MeshGeometry3D();
        double radius=Math.Max(.001,profile.Max(p=>p.R));
        foreach(var p in profile) for(int i=0;i<segments;i++)
        {
            double a=i*Math.PI*2/segments; mesh.Positions.Add(new(p.R*Math.Cos(a),p.R*Math.Sin(a),p.Z));
            mesh.TextureCoordinates.Add(new(.5+p.R/radius*.5*Math.Cos(a),.5+p.R/radius*.5*Math.Sin(a)));
        }
        for(int row=0;row<profile.Length-1;row++) for(int col=0;col<segments;col++)
        {
            int a=row*segments+col,b=row*segments+(col+1)%segments,c=(row+1)*segments+col,d=(row+1)*segments+(col+1)%segments;
            mesh.TriangleIndices.Add(a);mesh.TriangleIndices.Add(b);mesh.TriangleIndices.Add(c);
            mesh.TriangleIndices.Add(b);mesh.TriangleIndices.Add(d);mesh.TriangleIndices.Add(c);
        }
        // Analytic revolution normals keep the centre of a dished stick cap
        // uniform. Triangle averaging gave coincident pole vertices spokes.
        for(int row=0;row<profile.Length;row++)
        {
            var prev=profile[Math.Max(0,row-1)];var next=profile[Math.Min(profile.Length-1,row+1)];
            double dr=next.R-prev.R,dz=next.Z-prev.Z;
            for(int col=0;col<segments;col++)
            {
                double a=col*Math.PI*2/segments;
                var n=profile[row].R<.01 ? new Vector3D(0,0,dr<0 ? 1 : -1) : new Vector3D(dz*Math.Cos(a),dz*Math.Sin(a),-dr);
                if(n.LengthSquared>1e-12)n.Normalize();else n=new(0,0,1);mesh.Normals.Add(n);
            }
        }
        mesh.Freeze();return mesh;
    }
    private static MeshGeometry3D Finish(MeshGeometry3D mesh)
    {
        var normals=new Vector3D[mesh.Positions.Count];
        for(int i=0;i<mesh.TriangleIndices.Count;i+=3)
        {
            int a=mesh.TriangleIndices[i],b=mesh.TriangleIndices[i+1],c=mesh.TriangleIndices[i+2];
            var normal=Vector3D.CrossProduct(mesh.Positions[b]-mesh.Positions[a],mesh.Positions[c]-mesh.Positions[a]);
            normals[a]+=normal;normals[b]+=normal;normals[c]+=normal;
        }
        foreach(var v in normals) {var normal=v; if(normal.LengthSquared>1e-14)normal.Normalize();else normal=new(0,0,1);mesh.Normals.Add(normal);}
        mesh.Freeze();return mesh;
    }
}
