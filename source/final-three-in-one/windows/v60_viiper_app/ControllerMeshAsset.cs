using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Media3D;

namespace Y700Switch2V60Viiper;

// Geometry-only Wavefront import. Object/material boundaries, UVs and authored
// normals survive so an accurate licensed model can replace procedural parts
// without changing the controller, mapping or animation pipelines.
public sealed record ControllerMeshPart(string Name,string Material,MeshGeometry3D Mesh);
public static class ControllerMeshAsset
{
    public static IReadOnlyList<ControllerMeshPart> ReadObj(TextReader reader)
    {
        var positions=new List<Point3D>();var normals=new List<Vector3D>();var uvs=new List<System.Windows.Point>();
        var parts=new Dictionary<(string Name,string Material),PartBuilder>();
        string name="Body",material="Default"; int lineNumber=0,triangleCount=0;
        string? line;
        while((line=reader.ReadLine())!=null)
        {
            lineNumber++;if(line.Length>65536)throw Error("Line too long");
            int comment=line.IndexOf('#');if(comment>=0)line=line[..comment];
            string[] token=line.Split((char[]?)null,StringSplitOptions.RemoveEmptyEntries);if(token.Length==0)continue;
            switch(token[0])
            {
                case "v": Require(4);positions.Add(new(Number(1),Number(2),Number(3)));break;
                case "vn":
                    Require(4);var n=new Vector3D(Number(1),Number(2),Number(3));
                    if(n.LengthSquared<1e-12)throw Error("Zero normal");n.Normalize();normals.Add(n);break;
                case "vt": Require(3);uvs.Add(new(Number(1),1-Number(2)));break;
                case "o": case "g": name=token.Length>1 ? string.Join(" ",token.Skip(1)) : "Body";break;
                case "usemtl": material=token.Length>1 ? string.Join(" ",token.Skip(1)) : "Default";break;
                case "f":
                    Require(4);if(token.Length>258)throw Error("Face has too many vertices");
                    if(!parts.TryGetValue((name,material),out var part))parts[(name,material)]=part=new();
                    var face=token.Skip(1).Select(ParseVertex).ToArray();
                    for(int i=1;i<face.Length-1;i++)
                    {if(++triangleCount>500000)throw Error("Model exceeds triangle budget");part.Add(face[0],face[i],face[i+1],positions,normals,uvs);}
                    break;
            }
            if(positions.Count>500000 || normals.Count>500000 || uvs.Count>500000 || parts.Count>1024)throw Error("Model exceeds import budget");
            void Require(int count) {if(token.Length<count)throw Error("Missing coordinates");}
            double Number(int index)
            {if(!double.TryParse(token[index],NumberStyles.Float,CultureInfo.InvariantCulture,out double value)||!double.IsFinite(value)||Math.Abs(value)>1e8)throw Error("Invalid coordinate");return value;}
            (int Position,int Normal,int Uv) ParseVertex(string value)
            {
                var fields=value.Split('/');if(fields.Length>3)throw Error("Invalid face index");
                return(Index(fields[0],positions.Count),fields.Length>2 && fields[2].Length>0 ? Index(fields[2],normals.Count) : -1,
                    fields.Length>1 && fields[1].Length>0 ? Index(fields[1],uvs.Count) : -1);
            }
            int Index(string value,int count)
            {
                if(!int.TryParse(value,NumberStyles.Integer,CultureInfo.InvariantCulture,out int raw)||raw==0)throw Error("Invalid OBJ index");
                long index=raw>0 ? raw-1L : count+(long)raw;
                if(index<0||index>=count)throw Error("OBJ index outside its array");return (int)index;
            }
        }
        if(parts.Count==0)throw Error("Model has no faces");
        return parts.Select(pair=>new ControllerMeshPart(pair.Key.Name,pair.Key.Material,pair.Value.Finish())).ToArray();
        InvalidDataException Error(string message)=>new($"OBJ line {lineNumber}: {message}");
    }
    private sealed class PartBuilder
    {
        private readonly MeshGeometry3D mesh=new();
        private readonly Dictionary<(int Position,int Normal,int Uv),int> map=new();
        private readonly List<bool> authoredNormal=new();
        public void Add((int Position,int Normal,int Uv) a,(int Position,int Normal,int Uv) b,(int Position,int Normal,int Uv) c,
            List<Point3D> positions,List<Vector3D> normals,List<System.Windows.Point> uv)
        {
            int Vertex((int Position,int Normal,int Uv) item)
            {
                if(map.TryGetValue(item,out int existing))return existing;
                int index=mesh.Positions.Count;map[item]=index;mesh.Positions.Add(positions[item.Position]);
                mesh.Normals.Add(item.Normal<0 ? new Vector3D() : normals[item.Normal]);authoredNormal.Add(item.Normal>=0);
                mesh.TextureCoordinates.Add(item.Uv<0 ? new System.Windows.Point() : uv[item.Uv]);return index;
            }
            int ia=Vertex(a),ib=Vertex(b),ic=Vertex(c);mesh.TriangleIndices.Add(ia);mesh.TriangleIndices.Add(ib);mesh.TriangleIndices.Add(ic);
            var normal=Vector3D.CrossProduct(positions[b.Position]-positions[a.Position],positions[c.Position]-positions[a.Position]);
            foreach(int index in new[] {ia,ib,ic})if(!authoredNormal[index])mesh.Normals[index]+=normal;
        }
        public MeshGeometry3D Finish()
        {
            for(int i=0;i<mesh.Normals.Count;i++)
            {var normal=mesh.Normals[i];if(normal.LengthSquared>1e-14)normal.Normalize();else normal=new(0,0,1);mesh.Normals[i]=normal;}
            mesh.Freeze();return mesh;
        }
    }
}
