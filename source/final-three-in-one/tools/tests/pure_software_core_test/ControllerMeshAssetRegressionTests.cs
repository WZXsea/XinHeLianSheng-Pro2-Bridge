using System.IO;
using System.Windows.Media.Media3D;
using Y700Switch2V60Viiper;

internal static class ControllerMeshAssetRegressionTests
{
    public static void Run(Action<bool,string> check)
    {
        const string input="""
            v 0 0 0
            v 1 0 0
            v 1 1 0
            v 0 1 0
            vn 0 0 1
            vt 0 0
            vt 1 0
            vt 1 1
            vt 0 1
            o Shell
            usemtl Graphite
            f 1/1/1 2/2/1 3/3/1 4/4/1
            o Button_A
            usemtl Rubber
            f -4/1/1 -3/2/1 -2/3/1
            """;
        var parts=ControllerMeshAsset.ReadObj(new StringReader(input));
        check(parts.Count==2 && parts[0].Name=="Shell" && parts[1].Name=="Button_A" && parts[1].Material=="Rubber",
            "OBJ preserves named independent mechanical parts and material boundaries");
        check(parts[0].Mesh.IsFrozen && parts[0].Mesh.TriangleIndices.Count==6 && parts[1].Mesh.TriangleIndices.Count==3,
            "OBJ quads triangulate and geometry freezes for retained 3D rendering");
        check(parts[1].Mesh.Positions[0]==new Point3D(0,0,0) && parts[0].Mesh.TextureCoordinates[0].Y==1 && parts[0].Mesh.Normals.All(x=>x.Z==1),
            "OBJ preserves authored normals/UV orientation and handles relative indices");
        foreach(string bad in new[] {"v NaN 0 0","v 0 0 0\nf 1 2 3","v 0 0 0\nf 0 0 0","o Empty","vn 0 0 0"})
        {
            bool rejected=false;try {ControllerMeshAsset.ReadObj(new StringReader(bad));}catch(InvalidDataException){rejected=true;}
            check(rejected,"invalid OBJ fails visibly without manufacturing a substitute controller");
        }
    }
}
