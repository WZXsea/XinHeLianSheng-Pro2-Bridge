#!/bin/bash
# Run the host's real policy self-test on any .NET 8 host, without Windows I/O.
set -euo pipefail
export PYTHONUTF8=1
NATIVE_POLICY_ROOT="$(cd "$(dirname "$0")/../../.." && pwd)"
NATIVE_POLICY_TMP="$(mktemp -d "${TMPDIR:-/tmp}/pro2-windows-policy.XXXXXX")"
trap 'rm -rf "$NATIVE_POLICY_TMP"' EXIT
NATIVE_POLICY_DOTNET="${1:-dotnet}"
python3 - "$NATIVE_POLICY_ROOT" "$NATIVE_POLICY_TMP" <<'PY'
from pathlib import Path
import sys
from xml.sax.saxutils import escape
root,tmp=map(Path,sys.argv[1:])
original=(root/'source/final-three-in-one/windows/v60_viiper_app/MainViewModel.TransportSwitch.cs').read_text(encoding='utf-8-sig')
start=original.index('public static class Pro2TransportSwitchPolicy')
end=original.index('public sealed partial class MainViewModel',start)
(tmp/'OriginalPolicy.cs').write_text('namespace Y700Switch2V60Viiper;\n'+original[start:end])
host=(root/'desktop/Pro2.Platform.Windows/Program.cs').read_text(encoding='utf-8-sig')
failure=next(line for line in host.splitlines() if line.startswith('internal sealed class HostFailure('))
(tmp/'Program.cs').write_text('using System;\nnamespace Pro2.Platform.Windows;\n'+failure+'\ninternal static class Program { static void Main(){WindowsTransportHandover.SelfTest();Console.WriteLine("PASS 216 original USB handover eligibility combinations, actual-state outcomes and protocol preference round trips");} }\n')
policy=escape(str(root/'desktop/Pro2.Platform.Windows/WindowsTransportHandover.cs'))
transport=escape(str(root/'source/final-three-in-one/windows/v60_viiper_app/Pro2InputTransport.cs'))
(tmp/'Policy.csproj').write_text(f'<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net8.0</TargetFramework><Nullable>enable</Nullable><ImplicitUsings>enable</ImplicitUsings><NuGetAudit>false</NuGetAudit></PropertyGroup><ItemGroup><Compile Include="{policy}" Link="WindowsTransportHandover.cs"/><Compile Include="{transport}" Link="Pro2InputTransport.cs"/></ItemGroup></Project>')
PY
"$NATIVE_POLICY_DOTNET" run --project "$NATIVE_POLICY_TMP/Policy.csproj" -c Release
