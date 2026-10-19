using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;

namespace Pro2.Application;

/// Downloads the manifest-pinned upstream disk image. Installation and opening
/// the original signed image remain explicit actions of the desktop application.
public sealed class MacBackendInstaller {
 public const long MaximumDownloadBytes=16L*1024*1024;
 static readonly TimeSpan DownloadTimeout=TimeSpan.FromSeconds(90);
 static readonly SemaphoreSlim DownloadGate=new(1,1);
 static readonly HttpClient DefaultClient=CreateDefaultClient();
 readonly HttpClient client;
 readonly Uri source;
 readonly byte[] expectedHash;
 readonly long expectedSize;
 readonly string cacheDirectory;
 public string CachedFilePath {get;}

 public MacBackendInstaller(HttpClient? httpClient=null,string? cacheDirectory=null){
  using var stream=typeof(MacBackendInstaller).Assembly.GetManifestResourceStream("Pro2.BackendManifest.json")
   ??throw new InvalidDataException("安装程序清单资源 Pro2.BackendManifest.json 需要随应用提供。");
  using var document=JsonDocument.Parse(stream);
  var manifest=document.RootElement;
  try{
   source=new Uri(manifest.GetProperty("source_url").GetString()??"",UriKind.Absolute);
   var digest=manifest.GetProperty("release_sha256").GetString()??"";
   if(digest.Length!=64)throw new FormatException("SHA256 length");
   expectedHash=Convert.FromHexString(digest);
   expectedSize=manifest.GetProperty("release_size").GetInt64();
  }catch(Exception error)when(error is KeyNotFoundException or FormatException or InvalidOperationException or UriFormatException){
   throw new InvalidDataException("官方驱动下载清单的地址、大小和 SHA256 信息需要完整提供。",error);
  }
  if(source.Scheme!=Uri.UriSchemeHttps||!source.Host.Equals("github.com",StringComparison.OrdinalIgnoreCase)||
    !source.AbsolutePath.StartsWith("/LizardByte/libvirtualhid/releases/download/",StringComparison.Ordinal)||
    !source.AbsolutePath.EndsWith("/libvirtualhid-macOS-universal.dmg",StringComparison.Ordinal)||
    !string.IsNullOrEmpty(source.UserInfo)||!string.IsNullOrEmpty(source.Query)||!string.IsNullOrEmpty(source.Fragment))
   throw new InvalidDataException("驱动清单应指向 LizardByte/libvirtualhid 的官方 HTTPS 发布文件。");
  if(expectedSize<=0||expectedSize>MaximumDownloadBytes)
   throw new InvalidDataException("官方驱动下载清单的文件大小应在 1 字节至 16 MiB 范围内。");
  client=httpClient??DefaultClient;
  this.cacheDirectory=Path.GetFullPath(cacheDirectory??Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),"Library","Caches","PRO2Bridge","driver"));
  CachedFilePath=Path.Combine(this.cacheDirectory,Convert.ToHexString(expectedHash).ToLowerInvariant(),"libvirtualhid-macOS-universal.dmg");
 }

 /// Progress is a fraction in 0..1; 1 is reported only after verification and
 /// atomic cache publication. A valid existing image is reused without a GET.
 public async Task<string> DownloadAsync(IProgress<double>? progress=null,CancellationToken cancellationToken=default){
  if(!OperatingSystem.IsMacOS())throw new PlatformNotSupportedException("此下载接口用于 macOS 官方驱动安装程序。");
  using var timeout=CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
  timeout.CancelAfter(DownloadTimeout);
  var token=timeout.Token;
  bool entered=false;
  string? temporary=null;
  try{
   await DownloadGate.WaitAsync(token).ConfigureAwait(false);entered=true;
   Directory.CreateDirectory(cacheDirectory);
   using var cacheLock=await AcquireCacheLockAsync(Path.Combine(cacheDirectory,".download.lock"),token).ConfigureAwait(false);
   progress?.Report(0);
   if(await VerifyFileAsync(CachedFilePath,expectedSize,expectedHash,token).ConfigureAwait(false)){
    progress?.Report(1);return CachedFilePath;
   }
   Directory.CreateDirectory(Path.GetDirectoryName(CachedFilePath)!);
   temporary=Path.Combine(Path.GetDirectoryName(CachedFilePath)!,".download-"+Guid.NewGuid().ToString("N")+".tmp");
   using var request=new HttpRequestMessage(HttpMethod.Get,source);
   request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/octet-stream"));
   request.Headers.UserAgent.ParseAdd("PRO2Bridge-DriverInstaller/1.0");
   using var response=await client.SendAsync(request,HttpCompletionOption.ResponseHeadersRead,token).ConfigureAwait(false);
   response.EnsureSuccessStatusCode();
   var final=response.RequestMessage?.RequestUri;
   if(final is not null&&!IsOfficialReleaseLocation(final))throw new InvalidDataException("官方发布文件需要通过 GitHub 的 HTTPS 资源地址提供。");
   if(response.Content.Headers.ContentLength is{}length&&(length!=expectedSize||length>MaximumDownloadBytes))
    throw new InvalidDataException("下载文件大小与官方清单不一致，请重试。");
   await using(var input=await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false))
   await using(var output=new FileStream(temporary,FileMode.CreateNew,FileAccess.Write,FileShare.None,65536,FileOptions.Asynchronous|FileOptions.SequentialScan)){
    using var hash=IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    var buffer=new byte[65536];long total=0;
    while(true){
     var read=await input.ReadAsync(buffer.AsMemory(),token).ConfigureAwait(false);if(read==0)break;
     total+=read;
     if(total>expectedSize||total>MaximumDownloadBytes)throw new InvalidDataException("下载文件超过官方清单大小或 16 MiB 上限。");
     hash.AppendData(buffer,0,read);
     await output.WriteAsync(buffer.AsMemory(0,read),token).ConfigureAwait(false);
     progress?.Report(0.99*total/expectedSize);
    }
    if(total!=expectedSize)throw new InvalidDataException("下载文件长度与官方清单不一致，请重试。");
    if(!CryptographicOperations.FixedTimeEquals(hash.GetHashAndReset(),expectedHash))throw new InvalidDataException("下载文件 SHA256 校验结果与官方清单不一致，请重试。");
    await output.FlushAsync(token).ConfigureAwait(false);
    output.Flush(flushToDisk:true);
   }
   token.ThrowIfCancellationRequested();
   File.Move(temporary,CachedFilePath,overwrite:true);temporary=null;
   progress?.Report(1);return CachedFilePath;
  }catch(OperationCanceledException error)when(!cancellationToken.IsCancellationRequested){
   throw new TimeoutException("驱动下载等待超过 90 秒，请重试。",error);
  }finally{
   if(temporary is not null)try{File.Delete(temporary);}catch(IOException){}catch(UnauthorizedAccessException){}
   if(entered)DownloadGate.Release();
  }
 }

 /// Pure file validation is also used by acceptance tests and cache inspection.
 public static Task<bool> VerifyFileAsync(string path,long size,string sha256,CancellationToken cancellationToken=default){
  if(size<=0||size>MaximumDownloadBytes)throw new ArgumentOutOfRangeException(nameof(size));
  if(sha256.Length!=64)throw new ArgumentException("Expected a complete SHA256 digest.",nameof(sha256));
  var digest=Convert.FromHexString(sha256);
  return VerifyFileAsync(path,size,digest,cancellationToken);
 }
 static async Task<bool> VerifyFileAsync(string path,long size,byte[] digest,CancellationToken token){
  token.ThrowIfCancellationRequested();
  try{
   var info=new FileInfo(path);if(!info.Exists||info.Length!=size)return false;
   await using var file=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read,65536,FileOptions.Asynchronous|FileOptions.SequentialScan);
   var actual=await SHA256.HashDataAsync(file,token).ConfigureAwait(false);
   return CryptographicOperations.FixedTimeEquals(actual,digest);
  }catch(IOException){return false;}catch(UnauthorizedAccessException){return false;}
 }
 static async Task<FileStream> AcquireCacheLockAsync(string path,CancellationToken token){
  while(true){
   token.ThrowIfCancellationRequested();
   try{return new FileStream(path,FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None,1,FileOptions.None);}
   catch(IOException error)when((error.HResult&0xffff)is 11 or 32 or 33 or 35){await Task.Delay(80,token).ConfigureAwait(false);}
  }
 }
 static bool IsOfficialReleaseLocation(Uri address)=>address.Scheme==Uri.UriSchemeHttps&&
  (address.Host.Equals("github.com",StringComparison.OrdinalIgnoreCase)||
   address.Host.Equals("release-assets.githubusercontent.com",StringComparison.OrdinalIgnoreCase)||
   address.Host.Equals("objects.githubusercontent.com",StringComparison.OrdinalIgnoreCase)||
   address.Host.Equals("github-releases.githubusercontent.com",StringComparison.OrdinalIgnoreCase));
 static HttpClient CreateDefaultClient()=>new(new HttpClientHandler{AllowAutoRedirect=true,MaxAutomaticRedirections=5}){Timeout=Timeout.InfiniteTimeSpan};
}
