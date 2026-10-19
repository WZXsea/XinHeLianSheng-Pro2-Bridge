#!/usr/bin/env python3
"""Black-box protocol checks. No controller discovery or Bluetooth permission prompt."""
import json
from pathlib import Path
import subprocess
import sys

binary = Path(sys.argv[1]) if len(sys.argv) > 1 else Path(__file__).resolve().parents[2] / '.build/out/Products/Debug/pro2-native-host'
commands = [
    {'command':'ping', 'id':'ping'},
    {'command':'selfTest', 'id':'state-adapter'},
    {'command':'settings', 'settings':{'runtimebattery':False, 'autooptimize':False, 'skip':True}, 'requestId':'settings'},
    {'command':'settings', 'ps5GyroScalePitch':1.234, 'ps5GyroScaleYaw':-5, 'ps5GyroScaleRoll':9, 'id':'gyro-settings'},
    {'command':'selfTest', 'id':'gyro-self-test'},
    {'command':'switchToUsb', 'slot':4, 'deviceId':'missing-target', 'id':'switch-invalid-slot'},
    {'command':'switchToUsb', 'slot':3, 'deviceId':'missing-target', 'id':'switch-no-usb'},
    {'command':'connect', 'slot':3, 'transport':'usb', 'deviceId':'missing-target', 'id':'switch-usb-source'},
    {'command':'switchToUsb', 'slot':3, 'deviceId':'missing-target', 'id':'switch-non-bluetooth'},
    {'command':'disconnect', 'slot':3, 'id':'switch-cleanup'},
    {'command':'connect', 'slot':4, 'deviceId':'invalid', 'id':'invalid-slot'},
    {'command':'connect', 'slot':True, 'deviceId':'invalid', 'id':'boolean-slot'},
    {'command':'createOutput', 'slot':0, 'mode':'pro2', 'backend':'broker', 'id':'broker-pro2'},
    {'command':'createOutput', 'slot':1, 'mode':'pro2', 'backend':'nativeHID', 'id':'native-pro2'},
    {'command':'submitOutput', 'slot':2, 'state':{}, 'id':'uncreated-output'},
    {'command':'readFeedback', 'slot':0, 'id':'feedback'},
    {'command':'backendStatus', 'id':'backend'},
    {'command':'stop', 'slot':0, 'id':'stop-slot'},
    {'command':'stop', 'id':'stop-all'},
]
wire = ''.join(json.dumps(command, ensure_ascii=False) + '\n' for command in commands)
wire += '{broken-json}\n'
process = subprocess.run([str(binary)], input=wire, capture_output=True, text=True, timeout=30)
assert process.returncode == 0, (process.returncode, process.stderr)
events = [json.loads(line) for line in process.stdout.splitlines()]
assert events[0]['event'] == 'ready' and events[0]['slots'] == 4
assert events[-1]['event'] == 'stopped'
responses = {event.get('id', event.get('requestId')):event for event in events if 'id' in event or 'requestId' in event}
assert responses['ping']['ok']
assert responses['state-adapter']['passed'] and responses['state-adapter']['logicalButtonsTested'] == 21
assert responses['settings']['appliesTo'] == 'nextConnection'
assert responses['settings']['skip'] and not responses['settings']['runtimebattery']
assert responses['gyro-settings']['gyroScales'] == {'pitch':1.23, 'yaw':0.1, 'roll':4}
assert responses['gyro-settings']['gyroAppliesTo'] == 'nextSubmit'
assert responses['gyro-self-test']['gyroScales'] == responses['gyro-settings']['gyroScales']
assert responses['gyro-self-test']['groups'] == 9
assert responses['gyro-self-test']['usbTakeoverPolicy'] is True
assert responses['switch-invalid-slot']['code'] == 'invalid_slot'
assert responses['switch-no-usb']['code'] == 'usb_device_unavailable'
assert responses['switch-non-bluetooth']['code'] == 'source_not_bluetooth'
assert responses['switch-cleanup']['ok']
import math
ds5 = responses['gyro-self-test']['dualsenseFixture']
assert [round(ds5[key] * (1145 * math.pi / 180)) for key in ['gyroX', 'gyroY', 'gyroZ']] == [200,900,-800]
assert [round(ds5[key] * (9.80665 * 100)) for key in ['accelX', 'accelY', 'accelZ']] == [2000,6000,-4000]
pro1 = responses['gyro-self-test']['pro1Fixture']
assert [round(value * 14.2842) for value in [-pro1['gyroZ'], -pro1['gyroX'], pro1['gyroY']]] == [100,200,300]
assert responses['invalid-slot']['code'] == 'invalid_slot'
assert responses['boolean-slot']['code'] == 'invalid_slot'
assert responses['broker-pro2']['code'] == 'profile_unsupported'
assert responses['native-pro2']['code'] == 'profile_unsupported'
assert responses['uncreated-output']['code'] == 'slot_inactive'
assert responses['feedback']['events'] == []
assert responses['backend']['pro2']['available'] is False
assert responses['stop-slot']['slot'] == 0 and responses['stop-all']['ok']
assert any(event.get('code') == 'invalid_json' for event in events)
assert process.stderr == '', process.stderr
print('PASS JSONL startup, request correlation, USB takeover error paths, nine state-adapter groups, gyro settings/normalization, honest backend profiles, feedback, stop and EOF release')

# Feed JSON across partial writes; a final complete command may omit its newline.
process = subprocess.Popen([str(binary)], stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE)
first = json.loads(process.stdout.readline())
assert first['event'] == 'ready'
process.stdin.write(b'{"command":"pi')
process.stdin.flush()
process.stdin.write(b'ng","id":"fragmented"}\n{"command":"ping","id":"final"}')
process.stdin.close()
remaining = [json.loads(line) for line in process.stdout]
assert process.wait(timeout=10) == 0
assert {event.get('id') for event in remaining} >= {'fragmented', 'final'}
assert remaining[-1]['event'] == 'stopped'
assert process.stderr.read() == b''
print('PASS fragmented stdin, final line without newline and orderly EOF shutdown')

# Interactive clients keep stdin open for their whole session. Verify a reply
# arrives before EOF and before filling a sized-read buffer.
import select
process = subprocess.Popen([str(binary)], stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE)
try:
    assert json.loads(process.stdout.readline())['event'] == 'ready'
    process.stdin.write(b'{"command":"ping","requestId":701}\n')
    process.stdin.flush()
    assert select.select([process.stdout], [], [], 5)[0], 'Interactive ping must respond while stdin remains open'
    reply = json.loads(process.stdout.readline())
    assert reply['requestId'] == 701 and reply['ok']
    process.stdin.write(b'{"command":"ping","requestId":702}\n')
    process.stdin.flush()
    assert select.select([process.stdout], [], [], 5)[0], 'Second live request must respond without EOF'
    assert json.loads(process.stdout.readline())['requestId'] == 702
finally:
    process.stdin.close()
    for _ in process.stdout:
        pass
    assert process.wait(timeout=10) == 0
    assert process.stderr.read() == b''
print('PASS interactive requests respond while stdin remains open')
