using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Diagnostics;
using Pro2.Application;

internal static class MacBackendInstallerTests {
 sealed class CaptureProgress:IProgress<double>{public List<double> Values {get;}=[];public void Report(double value)=>Values.Add(value);}
 sealed class LocalHandler(Func<HttpContent> content):HttpMessageHandler {
  public int Requests;public List<Uri> Addresses {get;}=[];
  protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token){
   Interlocked.Increment(ref Requests);lock(Addresses)Addresses.Add(request.RequestUri!);
   return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK){RequestMessage=request,Content=content()});
  }
 }
 sealed class UnknownLengthContent(byte[] bytes):HttpContent {
  protected override bool TryComputeLength(out long length){length=0;return false;}
  protected override Task SerializeToStreamAsync(Stream stream,TransportContext? context)=>stream.WriteAsync(bytes).AsTask();
  protected override Task<Stream> CreateContentReadStreamAsync()=>Task.FromResult<Stream>(new MemoryStream(bytes,writable:false));
 }
 sealed class BlockingContent:HttpContent {
  protected override bool TryComputeLength(out long length){length=0;return false;}
  protected override Task SerializeToStreamAsync(Stream stream,TransportContext? context)=>Task.Delay(Timeout.Infinite);
  protected override Task<Stream> CreateContentReadStreamAsync()=>Task.FromResult<Stream>(new BlockingStream());
 }
 sealed class BlockingStream:Stream {
  public override bool CanRead=>true;public override bool CanSeek=>false;public override bool CanWrite=>false;
  public override long Length=>throw new NotSupportedException();public override long Position{get=>throw new NotSupportedException();set=>throw new NotSupportedException();}
  public override void Flush(){}public override int Read(byte[] buffer,int offset,int count)=>throw new NotSupportedException();
  public override async ValueTask<int> ReadAsync(Memory<byte> buffer,CancellationToken token=default){await Task.Delay(Timeout.Infinite,token);return 0;}
  public override long Seek(long offset,SeekOrigin origin)=>throw new NotSupportedException();public override void SetLength(long value)=>throw new NotSupportedException();
  public override void Write(byte[] buffer,int offset,int count)=>throw new NotSupportedException();
 }
 public static async Task<int> RunStandalone(string fixture){
  int assertions=0;
  try{await RunAsync(fixture,(success,message)=>{assertions++;if(!success)throw new InvalidOperationException(message);});Console.WriteLine($"Installer file/download acceptance: {assertions} assertions passed.");return 0;}
  catch(Exception error){Console.Error.WriteLine(error);return 1;}
 }
 public static async Task<int> RunPeer(string fixture,string cache,string requestLog){
  var bytes=await File.ReadAllBytesAsync(fixture);
  using var handler=new LocalHandler(()=>{File.AppendAllText(requestLog,"GET\n");return new UnknownLengthContent(bytes);});
  using var client=new HttpClient(handler);
  await new MacBackendInstaller(client,cache).DownloadAsync();return 0;
 }
 public static async Task RunAsync(string fixture,Action<bool,string> check){
  if(!OperatingSystem.IsMacOS())throw new PlatformNotSupportedException("These acceptance tests target the macOS installer cache.");
  using var resource=typeof(MacBackendInstaller).Assembly.GetManifestResourceStream("Pro2.BackendManifest.json")!;
  using var document=JsonDocument.Parse(resource);var manifest=document.RootElement;
  var expectedSize=manifest.GetProperty("release_size").GetInt64();var expectedHash=manifest.GetProperty("release_sha256").GetString()!;
  var official=new Uri(manifest.GetProperty("source_url").GetString()!);
  check(await MacBackendInstaller.VerifyFileAsync(fixture,expectedSize,expectedHash),"official disk-image fixture matches the embedded manifest");
  var bytes=await File.ReadAllBytesAsync(fixture);
  var root=Path.Combine(Path.GetTempPath(),"pro2-driver-cache-tests-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
  try{
   check(!await MacBackendInstaller.VerifyFileAsync(fixture,expectedSize,new string('0',64)),"wrong expected hash rejects otherwise valid bytes");
   check(!await MacBackendInstaller.VerifyFileAsync(fixture,expectedSize+1,expectedHash),"wrong expected size rejects otherwise valid bytes");
   check(!await MacBackendInstaller.VerifyFileAsync(Path.Combine(root,"missing.dmg"),expectedSize,expectedHash),"missing cache is invalid");
   Console.WriteLine("PASS installer pure file validation: official fixture, wrong SHA256, wrong size and absent cache");

   var cacheHandler=new LocalHandler(()=>throw new InvalidOperationException("Valid cache must bypass HTTP."));using var cacheClient=new HttpClient(cacheHandler);
   var cached=new MacBackendInstaller(cacheClient,Path.Combine(root,"valid"));Directory.CreateDirectory(Path.GetDirectoryName(cached.CachedFilePath)!);File.Copy(fixture,cached.CachedFilePath);
   var progress=new CaptureProgress();var result=await cached.DownloadAsync(progress);
   check(result==cached.CachedFilePath&&cacheHandler.Requests==0,"valid cached disk image is reused without a request");
   check(progress.Values.Last()==1,"valid cache completes progress");
   check(await MacBackendInstaller.VerifyFileAsync(result,expectedSize,expectedHash),"cache preserves the complete official disk-image bytes");
   Console.WriteLine("PASS installer valid cache reuses the original official disk image without network access");

   var mutated=bytes.ToArray();mutated[0]^=1;
   var hashHandler=new LocalHandler(()=>new ByteArrayContent(mutated));using var hashClient=new HttpClient(hashHandler);
   var wrongHash=new MacBackendInstaller(hashClient,Path.Combine(root,"wrong-hash"));
   await RejectAsync(wrongHash,check,"wrong body SHA256");check(hashHandler.Requests==1,"wrong hash uses one official source request");
   check(hashHandler.Addresses.Single()==official,"download requests exactly the embedded official source URL");
   Console.WriteLine("PASS installer rejects a same-length modified body and removes the partial image");

   var sizeHandler=new LocalHandler(()=>new ByteArrayContent(bytes[..^1]));using var sizeClient=new HttpClient(sizeHandler);
   await RejectAsync(new MacBackendInstaller(sizeClient,Path.Combine(root,"wrong-size")),check,"wrong content length");
   var oversizedHandler=new LocalHandler(()=>{var content=new ByteArrayContent([]);content.Headers.ContentLength=MacBackendInstaller.MaximumDownloadBytes+1;return content;});using var oversizedClient=new HttpClient(oversizedHandler);
   await RejectAsync(new MacBackendInstaller(oversizedClient,Path.Combine(root,"over-cap")),check,"16 MiB cap");
   var overflow=bytes.Concat(new byte[]{0}).ToArray();var streamHandler=new LocalHandler(()=>new UnknownLengthContent(overflow));using var streamClient=new HttpClient(streamHandler);
   await RejectAsync(new MacBackendInstaller(streamClient,Path.Combine(root,"stream-overflow")),check,"unknown-length stream overflow");
   Console.WriteLine("PASS installer checks header size, the hard cap and unknown-length stream overflow");

   var concurrentHandler=new LocalHandler(()=>new UnknownLengthContent(bytes));using var concurrentClient=new HttpClient(concurrentHandler);
   var concurrentPath=Path.Combine(root,"concurrent");
   var installers=Enumerable.Range(0,12).Select(_=>new MacBackendInstaller(concurrentClient,concurrentPath)).ToArray();
   var paths=await Task.WhenAll(installers.Select(installer=>installer.DownloadAsync()));
   check(paths.Distinct().Count()==1&&concurrentHandler.Requests==1,"twelve concurrent instances publish one verified cache using one GET");
   check(await MacBackendInstaller.VerifyFileAsync(paths[0],expectedSize,expectedHash),"concurrent publication leaves a complete verified image");
   check(!Directory.GetFiles(concurrentPath,"*.tmp",SearchOption.AllDirectories).Any(),"concurrent publication leaves no partial files");
   Console.WriteLine("PASS installer concurrent calls are idempotent with one atomic cache publication");

   var processCache=Path.Combine(root,"process-concurrent");var requestLog=Path.Combine(root,"process-requests.log");
   var workers=Enumerable.Range(0,3).Select(_=>{
    var start=new ProcessStartInfo(Environment.ProcessPath!){UseShellExecute=false,RedirectStandardOutput=true,RedirectStandardError=true};
    foreach(var arg in new[]{"--installer-peer",fixture,processCache,requestLog})start.ArgumentList.Add(arg);
    return Process.Start(start)!;
   }).ToArray();
   try{
    await Task.WhenAll(workers.Select(async worker=>{var output=worker.StandardOutput.ReadToEndAsync();var error=worker.StandardError.ReadToEndAsync();await worker.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));check(worker.ExitCode==0,"independent cache writer completes: "+await error);await output;}));
   }finally{foreach(var worker in workers){if(!worker.HasExited)worker.Kill(true);worker.Dispose();}}
   check(File.ReadAllLines(requestLog).Length==1,"independent processes share one cache lock and one GET");
   var processImage=new MacBackendInstaller(concurrentClient,processCache).CachedFilePath;
   check(await MacBackendInstaller.VerifyFileAsync(processImage,expectedSize,expectedHash),"cross-process publication remains a complete original image");
   Console.WriteLine("PASS installer independent processes share the cache lock and one verified download");

   var cancelHandler=new LocalHandler(()=>new BlockingContent());using var cancelClient=new HttpClient(cancelHandler);
   var cancelPath=Path.Combine(root,"cancelled");var cancelled=new MacBackendInstaller(cancelClient,cancelPath);using var cancellation=new CancellationTokenSource(TimeSpan.FromMilliseconds(60));
   var wasCancelled=false;try{await cancelled.DownloadAsync(null,cancellation.Token);}catch(OperationCanceledException){wasCancelled=true;}
   check(wasCancelled,"caller cancellation is preserved instead of reported as timeout");
   check(!File.Exists(cancelled.CachedFilePath)&&!Directory.GetFiles(cancelPath,"*.tmp",SearchOption.AllDirectories).Any(),"cancelled download leaves no published or partial image");
   var retryHandler=new LocalHandler(()=>new ByteArrayContent(bytes));using var retryClient=new HttpClient(retryHandler);
   var retry=new MacBackendInstaller(retryClient,cancelPath);check(File.Exists(await retry.DownloadAsync()),"cancelled operation releases locks for a later verified retry");
   check(manifest.GetProperty("broker_package_vendored").GetBoolean()==false,"manifest records upstream broker package as downloaded separately");
   check(manifest.GetProperty("files").EnumerateArray().All(file=>file.GetProperty("license").GetString()=="MIT"),"vendored manifest entries remain MIT client materials");
   Console.WriteLine("PASS installer cancellation cleans temporary data and releases concurrency locks");
  }finally{Directory.Delete(root,recursive:true);}
 }
 static async Task RejectAsync(MacBackendInstaller installer,Action<bool,string> check,string label){
  var rejected=false;var progress=new CaptureProgress();
  try{await installer.DownloadAsync(progress);}catch(InvalidDataException){rejected=true;}
  check(rejected,label+" must fail verification");
  check(!File.Exists(installer.CachedFilePath),label+" never publishes a cache image");
  check(!Directory.GetFiles(Path.GetDirectoryName(installer.CachedFilePath)!,"*.tmp",SearchOption.AllDirectories).Any(),label+" cleans its partial file");
  check(!progress.Values.Contains(1),label+" never reports verified completion");
 }
}
