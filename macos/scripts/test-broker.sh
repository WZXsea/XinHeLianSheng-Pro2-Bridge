#!/bin/bash
# Uses the production optional loader and MIT client; no service installation or activation.
set -euo pipefail
BROKER_MACOS_ROOT="$(cd "$(dirname "$0")/.." && pwd)"
BROKER_TEST_TMP="$(mktemp -d "${TMPDIR:-/tmp}/pro2-broker-tests.XXXXXX")"
trap 'rm -rf "$BROKER_TEST_TMP"' EXIT
"$BROKER_MACOS_ROOT/scripts/build-broker-shim.sh"
mkdir -p "$BROKER_TEST_TMP/CPro2Broker"
cp "$BROKER_MACOS_ROOT/Sources/CPro2Broker/include/Pro2Broker.h" "$BROKER_TEST_TMP/CPro2Broker/"
printf 'module CPro2Broker { header "Pro2Broker.h" export * }\n' > "$BROKER_TEST_TMP/CPro2Broker/module.modulemap"
clang -c "$BROKER_MACOS_ROOT/Sources/CPro2Broker/Pro2Broker.c" -I "$BROKER_MACOS_ROOT/Sources/CPro2Broker/include" -o "$BROKER_TEST_TMP/Pro2Broker.o"
swiftc -emit-library -emit-module -module-name Pro2Core \
    "$BROKER_MACOS_ROOT"/Sources/Pro2Core/*.swift \
    -emit-module-path "$BROKER_TEST_TMP/Pro2Core.swiftmodule" -o "$BROKER_TEST_TMP/libPro2Core.dylib"
swiftc -emit-library -emit-module -module-name Pro2Broker \
    "$BROKER_MACOS_ROOT"/Sources/Pro2Broker/*.swift "$BROKER_TEST_TMP/Pro2Broker.o" \
    -I "$BROKER_TEST_TMP" -I "$BROKER_TEST_TMP/CPro2Broker" -L "$BROKER_TEST_TMP" -lPro2Core \
    -emit-module-path "$BROKER_TEST_TMP/Pro2Broker.swiftmodule" -o "$BROKER_TEST_TMP/libPro2Broker.dylib"
python3 - "$BROKER_MACOS_ROOT/Tests/Pro2BrokerTests" "$BROKER_TEST_TMP/Runner.swift" <<'PY'
from pathlib import Path
import re,sys
sources='\n'.join(p.read_text() for p in sorted(Path(sys.argv[1]).glob('*.swift')))
sources=sources.replace('import XCTest','import Foundation').replace(': XCTestCase','')
classes=re.findall(r'final class (\w+)\s*\{(.*?)(?=\nfinal class |\Z)',sources,re.S)
shim=r'''
func XCTAssertEqual<T: Equatable>(_ actual: T, _ expected: T, file: StaticString = #filePath, line: UInt = #line) { precondition(actual == expected, "Expected \(expected), got \(actual) at \(file):\(line)") }
func XCTAssertTrue(_ value: Bool, file: StaticString = #filePath, line: UInt = #line) { precondition(value, "Expected true at \(file):\(line)") }
func XCTAssertFalse(_ value: Bool, file: StaticString = #filePath, line: UInt = #line) { precondition(!value, "Expected false at \(file):\(line)") }
'''
main=['@main struct Main { static func main() throws {']
count=0
for classname,body in classes:
 for name,throwing in re.findall(r'    func (test\w+)\(\)\s*(throws)?\s*\{',body):
  main.append(f'{"try " if throwing else ""}{classname}().{name}(); print("PASS {name}")');count+=1
assert count,'No test methods discovered'
main.extend([f'print("Passed {count} optional-broker client test groups")','} }'])
Path(sys.argv[2]).write_text(shim+sources+'\n'+'\n'.join(main)+'\n')
PY
swiftc "$BROKER_TEST_TMP/Runner.swift" "$BROKER_TEST_TMP/Pro2Broker.o" -parse-as-library \
    -I "$BROKER_TEST_TMP" -I "$BROKER_TEST_TMP/CPro2Broker" -L "$BROKER_TEST_TMP" -lPro2Core -lPro2Broker \
    -Xlinker -rpath -Xlinker "$BROKER_TEST_TMP" -o "$BROKER_TEST_TMP/RunTests"
"$BROKER_TEST_TMP/RunTests"
