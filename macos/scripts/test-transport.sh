#!/bin/bash
set -euo pipefail
MACOS_ROOT="$(cd "$(dirname "$0")/.." && pwd)"
TEST_TMP="$(mktemp -d "${TMPDIR:-/tmp}/pro2-transport-tests.XXXXXX")"
trap 'rm -rf "$TEST_TMP"' EXIT
printf 'import XCTest\n' > "$TEST_TMP/Probe.swift"
if swiftc -typecheck "$TEST_TMP/Probe.swift" >/dev/null 2>&1; then
    swift test --package-path "$MACOS_ROOT" --filter Pro2TransportTests
    exit $?
fi
printf '%s\n' 'XCTest unavailable; compiling the production BLE/USB transport and identical assertions without creating a central manager.'
mkdir -p "$TEST_TMP/CPro2USB"
cp "$MACOS_ROOT/Sources/Pro2Transport/CPro2USB/include/CPro2USB.h" "$TEST_TMP/CPro2USB/"
printf 'module CPro2USB { header "CPro2USB.h" export * }\n' > "$TEST_TMP/CPro2USB/module.modulemap"
clang -c "$MACOS_ROOT/Sources/Pro2Transport/CPro2USB/USBCommand.c" \
    -I "$MACOS_ROOT/Sources/Pro2Transport/CPro2USB/include" -o "$TEST_TMP/USBCommand.o"
swiftc -emit-library -emit-module -enable-testing -module-name Pro2Transport \
    "$MACOS_ROOT"/Sources/Pro2Transport/*.swift "$TEST_TMP/USBCommand.o" \
    -I "$TEST_TMP/CPro2USB" -framework CoreBluetooth -framework IOKit -framework CoreFoundation \
    -emit-module-path "$TEST_TMP/Pro2Transport.swiftmodule" -o "$TEST_TMP/libPro2Transport.dylib"
python3 - "$MACOS_ROOT/Tests/Pro2TransportTests/Pro2TransportTests.swift" "$TEST_TMP/Runner.swift" <<'PY'
from pathlib import Path
import re, sys
source = Path(sys.argv[1]).read_text().replace('import XCTest', 'import Foundation').replace(': XCTestCase', '')
shim = r'''
func XCTAssertEqual<T: Equatable>(_ actual: T, _ expected: T, file: StaticString = #filePath, line: UInt = #line) {
    precondition(actual == expected, "Expected \(expected), got \(actual) at \(file):\(line)")
}
func XCTAssertTrue(_ value: Bool, file: StaticString = #filePath, line: UInt = #line) {
    precondition(value, "Expected true at \(file):\(line)")
}
func XCTAssertFalse(_ value: Bool, file: StaticString = #filePath, line: UInt = #line) {
    precondition(!value, "Expected false at \(file):\(line)")
}
func XCTAssertNil<T>(_ value: T?, file: StaticString = #filePath, line: UInt = #line) {
    precondition(value == nil, "Expected nil at \(file):\(line)")
}
'''
names = re.findall(r'    func (test\w+)\(\)\s*\{', source)
assert names, 'No transport tests found'
main = ['@main struct Main { static func main() {']
main += [f'Pro2TransportTests().{name}(); print("PASS {name}")' for name in names]
main += [f'print("Passed {len(names)} transport policy groups")', '} }']
Path(sys.argv[2]).write_text(shim + source + '\n' + '\n'.join(main) + '\n')
PY
swiftc "$TEST_TMP/Runner.swift" -parse-as-library -I "$TEST_TMP" -I "$TEST_TMP/CPro2USB" \
    -L "$TEST_TMP" -lPro2Transport -Xlinker -rpath -Xlinker "$TEST_TMP" -o "$TEST_TMP/RunTests"
"$TEST_TMP/RunTests"
