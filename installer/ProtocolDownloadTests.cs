using Soda.AssetQuickRead.Setup;
using System.Net;
using System.Security.Cryptography;
using System.Text;


namespace Soda.AssetQuickRead.Setup;
internal static class ProtocolDownloadTests {
internal static string Run(string root, byte[] bytes) { Directory.CreateDirectory(root);
var pin = new CatalogFile("runtime/assets/manifest.json", bytes.Length, Convert.ToHexString(SHA256.HashData(bytes)), true);
var checks = new List<string>();
void Check(bool condition,string name) { if(!condition) throw new Exception(name); checks.Add(name); }
var seen = new List<string>(); int requests=0;
using (var stream = ProtocolDownload.Open(pin,CancellationToken.None,(source,code)=>seen.Add(source+":"+code),()=>new HttpClient(new Fake(request=>{requests++; if(requests==1) return new(HttpStatusCode.Forbidden); Check(request.Headers.Accept.ToString()=="application/vnd.github.raw+json","official_contents_accept"); Check(request.RequestUri!.Query.Contains("4853f07"),"fixed_commit"); return new(HttpStatusCode.OK){Content=new ByteArrayContent(bytes)};}))))
{ Check(stream.Length==bytes.Length && requests==2,"bounded_fallback_success"); }
Check(seen.Contains("github_raw:protocol_download_failed_http_403")&&seen.Contains("github_contents:verified"),"source_stage_codes");
requests=0;
try { using var rejected=ProtocolDownload.Open(pin,CancellationToken.None,null,()=>new HttpClient(new Fake(_=>{requests++; return new(HttpStatusCode.OK){Content=new ByteArrayContent(Enumerable.Repeat((byte)0,bytes.Length).ToArray())};}))); throw new Exception("not_rejected"); } catch(IOException e) { Check(e.Message=="protocol_download_hash_mismatch"&&requests==2,"corrupt_both_rejected"); }
using var cancellation=new CancellationTokenSource(); cancellation.Cancel();
try { ProtocolDownload.Open(pin,cancellation.Token,null,()=>throw new Exception("must_not_request")); throw new Exception("not_cancelled"); } catch(OperationCanceledException) { checks.Add("cancel_before_request"); }
try { ProtocolDownload.Url(pin with {Path="runtime/assets/unknown.json"}); throw new Exception("not_rejected"); } catch(IOException e){Check(e.Message=="protocol_source_rejected","allowlist_before_io");}
var previous=Path.Combine(root,"versions/1.0.2/runtime/assets"); Directory.CreateDirectory(previous); File.WriteAllBytes(Path.Combine(previous,"manifest.json"),bytes);
using(var reused=InstallEngine.ReuseProtocol(root,pin)){Check(reused?.Length==bytes.Length,"verified_local_reuse");}
File.WriteAllBytes(Path.Combine(previous,"manifest.json"),new byte[bytes.Length]); Check(InstallEngine.ReuseProtocol(root,pin)==null,"corrupt_local_rejected");
var diagnostic=new InstallerDiagnostics(Path.Combine(root,"diagnostic")); diagnostic.Record("protocol","verified",0,"github_contents"); diagnostic.Record("failed",InstallerDiagnostics.Code(new IOException("private-user-path secret=abc")));
var record=File.ReadAllText(Path.Combine(root,"diagnostic/latest.json")); Check(!record.Contains("secret")&&!record.Contains("private-user")&&record.Contains("installer_local_operation_failed"),"redacted_diagnostic");
Check(InstallerDiagnostics.Message("protocol_registration_conflict").Contains("入口")&&!InstallerDiagnostics.Message("protocol_registration_conflict").Contains("网络"),"local_failure_not_network");
Check(InstallEngine.Version=="1.0.4","current_version");
return System.Text.Json.JsonSerializer.Serialize(new{passed=checks.Count,checks});
}
sealed class Fake(Func<HttpRequestMessage,HttpResponseMessage> response):HttpMessageHandler { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token) {token.ThrowIfCancellationRequested(); return Task.FromResult(response(request));} }
}