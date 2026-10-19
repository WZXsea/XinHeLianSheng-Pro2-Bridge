// Apache-2.0. Generic HID gamepad, using Apple's IOHIDUserDevice API.
#include "Pro2Virtual.h"
#include <IOKit/hidsystem/IOHIDUserDevice.h>
#include <IOKit/hid/IOHIDKeys.h>
#include <mach/mach_time.h>
#include <stdlib.h>

struct Pro2Virtual { IOHIDUserDeviceRef device; };
// 24 buttons (3 bytes), four signed 16-bit axes (8 bytes). No report ID.
static const uint8_t descriptor[] = {
    0x05,0x01,0x09,0x05,0xA1,0x01,
    0x05,0x09,0x19,0x01,0x29,0x18,0x15,0x00,0x25,0x01,
    0x75,0x01,0x95,0x18,0x81,0x02,
    0x05,0x01,0x09,0x30,0x09,0x31,0x09,0x33,0x09,0x34,
    0x16,0x01,0x80,0x26,0xFF,0x7F,0x75,0x10,0x95,0x04,0x81,0x02,0xC0
};
Pro2VirtualRef pro2_virtual_create(void) {
    CFMutableDictionaryRef props = CFDictionaryCreateMutable(NULL,0,&kCFTypeDictionaryKeyCallBacks,&kCFTypeDictionaryValueCallBacks);
    CFDataRef desc = CFDataCreate(NULL,descriptor,sizeof(descriptor));
    CFDictionarySetValue(props,CFSTR(kIOHIDReportDescriptorKey),desc);
    CFDictionarySetValue(props,CFSTR(kIOHIDProductKey),CFSTR("PRO2 Bridge Gamepad"));
    CFDictionarySetValue(props,CFSTR(kIOHIDManufacturerKey),CFSTR("PRO2 Bridge"));
    CFDictionarySetValue(props,CFSTR(kIOHIDTransportKey),CFSTR("Virtual"));
    IOHIDUserDeviceRef device = IOHIDUserDeviceCreateWithProperties(NULL,props,0);
    CFRelease(desc); CFRelease(props);
    if (!device) return NULL;
    Pro2VirtualRef result = calloc(1,sizeof(*result));
    if (!result) { CFRelease(device); return NULL; }
    result->device = device;
    return result;
}
int32_t pro2_virtual_send(Pro2VirtualRef device, const uint8_t *bytes, size_t length) {
    if (!device || length != 11) return kIOReturnBadArgument;
    return IOHIDUserDeviceHandleReportWithTimeStamp(device->device,mach_absolute_time(),bytes,(CFIndex)length);
}
void pro2_virtual_destroy(Pro2VirtualRef device) {
    if (device) { CFRelease(device->device); free(device); }
}
