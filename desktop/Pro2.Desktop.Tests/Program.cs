using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Pro2.Desktop.ViewModels;
using Pro2.Desktop.Views;
using Pro2.Desktop.Tests;

// Source renders, distinct from desktop captures and hardware acceptance.
// initialize:false keeps the native helper and discovery inactive throughout this run.
AppBuilder.Configure<Pro2.Desktop.App>()
    .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing=false })
    .UseSkia().SetupWithoutStarting();
var root=FindRoot();
var output=Path.Combine(root,"artifacts","ui");Directory.CreateDirectory(output);
var configuration=ConfigurationValidation.Run();
var model=new MainViewModel(initialize:false);
var window=new MainWindow { DataContext=model, Width=1040, Height=820 };
window.Show();
var pages=new[]{"controller","rumble","configuration","settings"};
var expected=new[]{new[]{"NS2 PRO  /  连接状态","游戏输出"},new[]{"震动强度","游戏反馈"},new[]{"配置中心","CONTROL LAB"},new[]{"虚拟手柄驱动","校准与连接"}};
var results=new List<object>();
for(int page=0;page<pages.Length;page++){
    model.SelectedPage=page;Dispatcher.UIThread.RunJobs();
    window.Measure(new Size(1040,820));window.Arrange(new Rect(0,0,1040,820));window.UpdateLayout();
    using var image=new RenderTargetBitmap(new PixelSize(1040,820),new Vector(96,96));
    image.Render(window);var file=Path.Combine(output,pages[page]+".png");image.Save(file);
    var text=window.GetVisualDescendants().OfType<TextBlock>().Where(x=>x.IsEffectivelyVisible).Select(x=>x.Text).Where(x=>!string.IsNullOrEmpty(x)).ToArray();
    foreach(var value in expected[page])if(!text.Contains(value))throw new InvalidOperationException($"Page {pages[page]} is missing visible content: {value}");
    results.Add(new{page=pages[page],file,text});
}
model.SelectedPage=2;model.SelectControlCommand.Execute(19);Dispatcher.UIThread.RunJobs();window.UpdateLayout();
using(var image=new RenderTargetBitmap(new PixelSize(1040,820),new Vector(96,96))){image.Render(window);image.Save(Path.Combine(output,"configuration-editor.png"));}
model.AdvancedEditorExpanded=true;Dispatcher.UIThread.RunJobs();window.UpdateLayout();
using(var image=new RenderTargetBitmap(new PixelSize(1040,820),new Vector(96,96))){image.Render(window);image.Save(Path.Combine(output,"configuration-advanced.png"));}
model.CloseKeyEditorCommand.Execute(null);
var properties=typeof(MainViewModel).GetProperties().Select(x=>x.Name).ToHashSet();
var checkedBindings=new SortedSet<string>();var missing=new SortedSet<string>();
foreach(var file in Directory.GetFiles(Path.Combine(root,"desktop","Pro2.Desktop","Views"),"*.axaml")){
    void Inspect(XElement element,bool item){
        if(element.Name.LocalName=="DataTemplate")item=true;
        foreach(var attribute in element.Attributes()){
            var value=attribute.Value;if(!value.Contains("{Binding")||attribute.Name.LocalName is "DisplayMemberBinding" or "SelectedValueBinding")continue;
            var parent=Regex.Match(value,@"DataContext\.([A-Za-z][\w]*)");
            if(parent.Success){var name=parent.Groups[1].Value;checkedBindings.Add(name);if(!properties.Contains(name))missing.Add(name);continue;}
            if(item||value.Contains("AncestorType=Button"))continue;
            var match=Regex.Match(value,@"\{Binding\s+([A-Za-z][\w.]*)");if(!match.Success)continue;
            var rootName=match.Groups[1].Value.Split('.')[0];checkedBindings.Add(rootName);if(!properties.Contains(rootName))missing.Add(rootName);
        }
        foreach(var child in element.Elements())Inspect(child,item);
    }
    Inspect(XDocument.Load(file).Root!,false);
}
if(missing.Count>0)throw new InvalidOperationException("Missing root UI properties: "+string.Join(", ",missing));
var family=new FontFamily("avares://PRO2ControllerAssistant/Assets/Fonts#Noto Sans SC");
var regular=new Typeface(family,FontStyle.Normal,FontWeight.Normal).GlyphTypeface;
var bold=new Typeface(family,FontStyle.Normal,FontWeight.Bold).GlyphTypeface;
if(regular.Weight!=FontWeight.Normal||bold.Weight!=FontWeight.Bold)throw new InvalidOperationException($"Embedded font weights: {regular.Weight}/{bold.Weight}");
var report=new{renderType="Avalonia.Headless + Skia source rendering",hardwareAcceptance=false,width=1040,height=820,rootBindings=checkedBindings.Count,missing=missing.ToArray(),font=new{regular=regular.Weight.ToString(),bold=bold.Weight.ToString()},configuration,pages=results};
File.WriteAllText(Path.Combine(output,"report.json"),JsonSerializer.Serialize(report,new JsonSerializerOptions{WriteIndented=true}));
Console.WriteLine($"Rendered {pages.Length} pages and the key editor; {checkedBindings.Count} root bindings resolved; {configuration.Checks} configuration checks passed. Output: {output}");
window.Close();
model.ShutdownAsync().GetAwaiter().GetResult();
return 0;

static string FindRoot(){var folder=new DirectoryInfo(AppContext.BaseDirectory);while(folder!=null&&!Directory.Exists(Path.Combine(folder.FullName,"desktop","Pro2.Desktop")))folder=folder.Parent;return folder?.FullName??throw new DirectoryNotFoundException("Repository root");}
