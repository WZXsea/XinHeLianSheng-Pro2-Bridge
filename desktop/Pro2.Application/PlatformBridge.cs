using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
namespace Pro2.Application;

public sealed class PlatformBridge : IAsyncDisposable {
 sealed class Session(Process process) {
  public Process Process {get;}=process;
  public Task Reader {get;set;}=Task.CompletedTask;
  public Task Errors {get;set;}=Task.CompletedTask;
  public TaskCompletionSource ReadersStarted {get;}=new(TaskCreationOptions.RunContinuationsAsynchronously);
  int disposed;
  public void DisposeProcess(){if(Interlocked.Exchange(ref disposed,1)==0)Process.Dispose();}
 }
 sealed record PendingRequest(Session Owner,TaskCompletionSource<JsonElement> Completion);
 readonly SemaphoreSlim writes=new(1,1);
 readonly ConcurrentDictionary<long,PendingRequest> pending=new();
 readonly List<Session> sessions=[];
 readonly CancellationTokenSource lifetime=new();
 Session? current;
 long sequence;
 int disposed;
 public event Action<JsonElement>? EventReceived;
 public event Action<string>? Log;
 public bool IsRunning=>Volatile.Read(ref disposed)==0&&Running(Volatile.Read(ref current));
 static bool Running(Session? session){try{return session is not null&&!session.Process.HasExited;}catch(InvalidOperationException){return false;}}
 public static string ResolveHostPath() {
  var overridePath=Environment.GetEnvironmentVariable("PRO2_NATIVE_HOST");
  if(!string.IsNullOrWhiteSpace(overridePath))return Path.GetFullPath(overridePath);
  var filename=OperatingSystem.IsWindows()?"Pro2.Platform.Windows.exe":"pro2-native-host";
  var packaged=Path.Combine(AppContext.BaseDirectory,"native",filename);
  if(File.Exists(packaged))return packaged;
  if(OperatingSystem.IsMacOS()){
   var adjacent=Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,"..","Helpers",filename));
   if(File.Exists(adjacent))return adjacent;
  }
  for(var cursor=new DirectoryInfo(AppContext.BaseDirectory);cursor!=null;cursor=cursor.Parent){
   if(OperatingSystem.IsMacOS())foreach(var relative in new[]{"macos/.build/out/Products/Release/pro2-native-host","macos/.build/out/Products/Debug/pro2-native-host","macos/.build/arm64-apple-macosx/release/pro2-native-host","macos/.build/arm64-apple-macosx/debug/pro2-native-host"}){
    var candidate=Path.Combine(cursor.FullName,relative);if(File.Exists(candidate))return candidate;
   }
   if(OperatingSystem.IsWindows()){
    var candidate=Path.Combine(cursor.FullName,"desktop/Pro2.Platform.Windows/bin/Release/net8.0-windows10.0.26100.0/win-x64/Pro2.Platform.Windows.exe");if(File.Exists(candidate))return candidate;
   }
  }
  return packaged;
 }
 // Startup shares the write gate with requests, so all callers select the same
 // process and stdin stream even when the first requests arrive concurrently.
 public async Task StartAsync(){
  ThrowIfDisposed();
  await writes.WaitAsync(lifetime.Token);
  try{ThrowIfDisposed();StartCore();}finally{writes.Release();}
 }
 Session StartCore(){
  if(Running(current))return current!;
  var path=ResolveHostPath();
  if(!File.Exists(path))throw new FileNotFoundException("平台通信组件需要构建或随安装包提供。",path);
  var start=new ProcessStartInfo(path){UseShellExecute=false,RedirectStandardInput=true,RedirectStandardOutput=true,RedirectStandardError=true,CreateNoWindow=true,StandardInputEncoding=new UTF8Encoding(false),StandardOutputEncoding=Encoding.UTF8,StandardErrorEncoding=Encoding.UTF8};
  var process=new Process{StartInfo=start,EnableRaisingEvents=true};
  var session=new Session(process);
  process.Exited+=(_,_)=>_=HandleExitAsync(session);
  bool started;
  try{started=process.Start();}catch{process.Dispose();throw;}
  if(!started){process.Dispose();throw new IOException("平台通信组件启动失败。");}
  current=session;sessions.Add(session);
  session.Reader=ReadAsync(session,process.StandardOutput,lifetime.Token);
  session.Errors=ReadErrorsAsync(session,process.StandardError,lifetime.Token);
  session.ReadersStarted.TrySetResult();
  return session;
 }
 async Task HandleExitAsync(Session session){
  // A process may exit immediately after writing its final response. Drain the
  // pipes before failing requests that truly have no matching response.
  await session.ReadersStarted.Task;
  try{await Task.WhenAll(session.Reader,session.Errors);}catch{}
  foreach(var item in pending){
   if(ReferenceEquals(item.Value.Owner,session)&&pending.TryRemove(item.Key,out var request))
    request.Completion.TrySetException(new IOException("平台通信组件已退出。"));
  }
  await writes.WaitAsync();
  try{
   sessions.Remove(session);
   if(ReferenceEquals(current,session)){
    current=null;
    if(Volatile.Read(ref disposed)==0){
     int? code=null;try{code=session.Process.ExitCode;}catch{}
     using var document=JsonDocument.Parse(JsonSerializer.Serialize(new{ @event="hostExited",reason="processExit",exitCode=code }));
     NotifyEvent(document.RootElement.Clone());
     NotifyLog("平台通信组件已退出，当前输出已释放。");
    }
   }
  }finally{writes.Release();}
  session.DisposeProcess();
 }
 async Task ReadAsync(Session session,StreamReader reader,CancellationToken token){
  try{while(await reader.ReadLineAsync(token)is{}line){
   JsonElement value;
   try{using var document=JsonDocument.Parse(line);value=document.RootElement.Clone();}
   catch(JsonException){NotifyLog("通信组件输出格式检查："+line[..Math.Min(120,line.Length)]);continue;}
   if(value.ValueKind!=JsonValueKind.Object){NotifyLog("通信组件事件需要 JSON 对象。");continue;}
   if((value.TryGetProperty("requestId",out var id)||value.TryGetProperty("id",out id))&&id.ValueKind==JsonValueKind.Number&&id.TryGetInt64(out var key)&&pending.TryGetValue(key,out var candidate)&&ReferenceEquals(candidate.Owner,session)&&pending.TryRemove(key,out var request))
    request.Completion.TrySetResult(value);
   if(!ReferenceEquals(Volatile.Read(ref current),session))continue;
   NotifyEvent(value);
  }}catch(OperationCanceledException){}catch(Exception error){NotifyLog("平台事件读取："+error.Message);}
 }
 async Task ReadErrorsAsync(Session session,StreamReader reader,CancellationToken token){
  try{while(await reader.ReadLineAsync(token)is{}line)if(ReferenceEquals(Volatile.Read(ref current),session))NotifyLog(line);}
  catch(OperationCanceledException){}catch(Exception error){if(ReferenceEquals(Volatile.Read(ref current),session))NotifyLog("平台诊断读取："+error.Message);}
 }
 public async Task SendAsync(object command,CancellationToken token=default){
  ThrowIfDisposed();
  await writes.WaitAsync(token);
  try{ThrowIfDisposed();await WriteAsync(StartCore(),command,token);}finally{writes.Release();}
 }
 static async Task WriteAsync(Session session,object command,CancellationToken token){
  await session.Process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(command).AsMemory(),token);
  await session.Process.StandardInput.FlushAsync(token);
 }
 public async Task<JsonElement> RequestAsync(Dictionary<string,object?> command,TimeSpan? timeout=null){
  ThrowIfDisposed();
  var id=Interlocked.Increment(ref sequence);
  var payload=new Dictionary<string,object?>(command){["requestId"]=id};
  var completion=new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
  await writes.WaitAsync(lifetime.Token);
  try{
   ThrowIfDisposed();var session=StartCore();pending[id]=new(session,completion);
   try{await WriteAsync(session,payload,lifetime.Token);}catch{pending.TryRemove(id,out _);throw;}
  }finally{writes.Release();}
  try{return await completion.Task.WaitAsync(timeout??TimeSpan.FromSeconds(10),lifetime.Token);}
  finally{pending.TryRemove(id,out _);}
 }
 void ThrowIfDisposed(){ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed)!=0,this);}
 void NotifyEvent(JsonElement value){if(EventReceived is{}handlers)foreach(Action<JsonElement> handler in handlers.GetInvocationList())try{handler(value);}catch(Exception error){NotifyLog("平台事件订阅者："+error.Message);}}
 void NotifyLog(string message){if(Log is{}handlers)foreach(Action<string> handler in handlers.GetInvocationList())try{handler(message);}catch{}}
 public async ValueTask DisposeAsync(){
  if(Interlocked.Exchange(ref disposed,1)!=0)return;
  await writes.WaitAsync();
  Session[] stopping;
  try{
   stopping=sessions.ToArray();
   if(Running(current)){
    try{await WriteAsync(current!,new{command="stop"},CancellationToken.None);current!.Process.StandardInput.Close();}
    catch{}
   }
  }finally{writes.Release();}
  foreach(var session in stopping){
   try{await session.Process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(OperatingSystem.IsWindows()?60:5));}
   catch{if(Running(session)){try{session.Process.Kill(true);await session.Process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(2));}catch{}}}
   try{await Task.WhenAll(session.Reader,session.Errors).WaitAsync(TimeSpan.FromSeconds(1));}catch{}
  }
  lifetime.Cancel();
  foreach(var request in pending.Values)request.Completion.TrySetException(new ObjectDisposedException(nameof(PlatformBridge)));
  pending.Clear();
  foreach(var session in stopping)session.DisposeProcess();
  await writes.WaitAsync();
  try{current=null;sessions.Clear();}finally{writes.Release();}
  // Keep the lightweight semaphore/token alive so an already-queued caller can
  // acquire the gate and receive ObjectDisposedException deterministically.
 }
}
