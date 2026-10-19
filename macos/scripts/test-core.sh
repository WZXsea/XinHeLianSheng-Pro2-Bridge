#!/bin/bash
# Run the same XCTest assertion bodies on full Xcode or Command Line Tools.
# The fallback builds the real Core/Output/C bridge; tests call pure encoders only.
set -euo pipefail
MACOS_ROOT="$(cd "$(dirname "$0")/.." && pwd)"
TEST_TMP="$(mktemp -d "${TMPDIR:-/tmp}/pro2-core-tests.XXXXXX")"
trap 'rm -rf "$TEST_TMP"' EXIT
printf 'import XCTest\n' > "$TEST_TMP/Probe.swift"
if swiftc -typecheck "$TEST_TMP/Probe.swift" >/dev/null 2>&1; then
    swift test --package-path "$MACOS_ROOT" --filter Pro2CoreTests
    exit $?
fi
printf '%s\n' 'XCTest unavailable in this toolchain; running identical assertions with the standalone Swift runner.'

# Generate a small C module map without changing the production headers.
mkdir -p "$TEST_TMP/CPro2Virtual"
cp "$MACOS_ROOT/Sources/CPro2Virtual/include/Pro2Virtual.h" "$TEST_TMP/CPro2Virtual/"
printf 'module CPro2Virtual { header "Pro2Virtual.h" export * }\n' > "$TEST_TMP/CPro2Virtual/module.modulemap"
clang -c "$MACOS_ROOT/Sources/CPro2Virtual/Pro2Virtual.c" \
    -I "$MACOS_ROOT/Sources/CPro2Virtual/include" -o "$TEST_TMP/Pro2Virtual.o"
swiftc -emit-library -emit-module -enable-testing -module-name Pro2Core \
    "$MACOS_ROOT"/Sources/Pro2Core/*.swift \
    -emit-module-path "$TEST_TMP/Pro2Core.swiftmodule" -o "$TEST_TMP/libPro2Core.dylib"
swiftc -emit-library -emit-module -module-name Pro2Output \
    "$MACOS_ROOT"/Sources/Pro2Output/*.swift "$TEST_TMP/Pro2Virtual.o" \
    -I "$TEST_TMP" -I "$TEST_TMP/CPro2Virtual" -L "$TEST_TMP" -lPro2Core \
    -framework IOKit -framework CoreFoundation -framework ApplicationServices \
    -emit-module-path "$TEST_TMP/Pro2Output.swiftmodule" -o "$TEST_TMP/libPro2Output.dylib"
python3 - "$MACOS_ROOT/Tests/Pro2CoreTests" "$TEST_TMP/Runner.swift" <<'PY'
from pathlib import Path
import re, sys
# Test bodies remain the authoritative source. Only import/base class change.
sources = '\n'.join(p.read_text() for p in sorted(Path(sys.argv[1]).glob('*.swift')))
sources = sources.replace('import XCTest', 'import Foundation').replace(': XCTestCase', '')
classes = re.findall(r'final class (\w+)\s*\{(.*?)(?=\nfinal class |\Z)', sources, re.S)
shim = r'''
func XCTAssertEqual<T: Equatable>(_ actual: T, _ expected: T, file: StaticString = #filePath, line: UInt = #line) {
    precondition(actual == expected, "Expected \(expected), got \(actual) at \(file):\(line)")
}
func XCTAssertNil<T>(_ value: T?, file: StaticString = #filePath, line: UInt = #line) {
    precondition(value == nil, "Expected nil at \(file):\(line)")
}
func XCTAssertTrue(_ value: Bool, file: StaticString = #filePath, line: UInt = #line) {
    precondition(value, "Expected true at \(file):\(line)")
}
func XCTAssertFalse(_ value: Bool, file: StaticString = #filePath, line: UInt = #line) {
    precondition(!value, "Expected false at \(file):\(line)")
}
func XCTAssertLessThanOrEqual<T: Comparable>(_ actual: T, _ limit: T) { precondition(actual <= limit) }
func XCTAssertThrowsError<T>(_ expression: @autoclosure () throws -> T, file: StaticString = #filePath, line: UInt = #line) {
    do { _ = try expression(); preconditionFailure("Expected error at \(file):\(line)") } catch {}
}
'''
main = ['@main struct Main { static func main() throws {']
count = 0
for classname, body in classes:
    for name, throwing in re.findall(r'    func (test\w+)\(\)\s*(throws)?\s*\{', body):
        main.append(f'{"try " if throwing else ""}{classname}().{name}(); print("PASS {name}")')
        count += 1
assert count, 'No test methods discovered'
main.append(f'print("Passed {count} protocol/output test groups")')
main.append('} }')
Path(sys.argv[2]).write_text(shim + sources + '\n' + '\n'.join(main) + '\n')
PY
swiftc "$TEST_TMP/Runner.swift" -parse-as-library \
    -I "$TEST_TMP" -I "$TEST_TMP/CPro2Virtual" -L "$TEST_TMP" -lPro2Core -lPro2Output \
    -Xlinker -rpath -Xlinker "$TEST_TMP" -o "$TEST_TMP/RunTests"
"$TEST_TMP/RunTests"
