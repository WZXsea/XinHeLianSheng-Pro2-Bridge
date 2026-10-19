#include "CPro2USB.h"
#include <CoreFoundation/CoreFoundation.h>
#include <IOKit/IOCFPlugIn.h>
#include <IOKit/usb/IOUSBLib.h>
#include <unistd.h>
#include <stdlib.h>
#include <stdatomic.h>
#include <stdbool.h>

struct Pro2USBCancellation { atomic_bool cancelled; };
Pro2USBCancellation *pro2_usb_cancellation_create(void) {
    Pro2USBCancellation *token = calloc(1, sizeof(*token));
    if (token) atomic_init(&token->cancelled, false);
    return token;
}
void pro2_usb_cancellation_cancel(Pro2USBCancellation *token) {
    if (token) atomic_store(&token->cancelled, true);
}
void pro2_usb_cancellation_destroy(Pro2USBCancellation *token) { free(token); }
static bool cancelled(Pro2USBCancellation *token) { return token && atomic_load(&token->cancelled); }

static uint32_t number(io_registry_entry_t service, CFStringRef key) {
    CFTypeRef value = IORegistryEntrySearchCFProperty(service, kIOServicePlane, key, kCFAllocatorDefault,
        kIORegistryIterateRecursively | kIORegistryIterateParents);
    uint32_t result = 0;
    if (value) {
        if (CFGetTypeID(value) == CFNumberGetTypeID()) CFNumberGetValue(value, kCFNumberSInt32Type, &result);
        CFRelease(value);
    }
    return result;
}

static int32_t command_channel(uint32_t location_id, Pro2USBCancellation *token, uint8_t report_id) {
    if (cancelled(token)) return kIOReturnAborted;
    if (!location_id) return kIOReturnBadArgument;
    io_iterator_t iterator = 0;
    IOReturn status = IOServiceGetMatchingServices(kIOMainPortDefault, IOServiceMatching(kIOUSBInterfaceClassName), &iterator);
    if (status != kIOReturnSuccess) return status;
    io_service_t service;
    IOUSBInterfaceInterface300 **interface = NULL;
    uint8_t pipe = 0;
    status = kIOReturnNotFound;
    while ((service = IOIteratorNext(iterator))) {
        if (cancelled(token)) { IOObjectRelease(service); status = kIOReturnAborted; break; }
        if (number(service, CFSTR("idVendor")) != 0x057e || number(service, CFSTR("idProduct")) != 0x2069 ||
            number(service, CFSTR("locationID")) != location_id || number(service, CFSTR("bInterfaceNumber")) != 1) {
            IOObjectRelease(service); continue;
        }
        IOCFPlugInInterface **plugin = NULL;
        SInt32 score = 0;
        status = IOCreatePlugInInterfaceForService(service, kIOUSBInterfaceUserClientTypeID,
            kIOCFPlugInInterfaceID, &plugin, &score);
        IOObjectRelease(service);
        if (status != kIOReturnSuccess || !plugin) break;
        HRESULT query = (*plugin)->QueryInterface(plugin, CFUUIDGetUUIDBytes(kIOUSBInterfaceInterfaceID300), (LPVOID *)&interface);
        IODestroyPlugInInterface(plugin);
        if (query || !interface) { status = kIOReturnUnsupported; break; }
        status = (*interface)->USBInterfaceOpen(interface);
        if (status != kIOReturnSuccess) { (*interface)->Release(interface); interface = NULL; break; }
        UInt8 count = 0;
        status = (*interface)->GetNumEndpoints(interface, &count);
        if (status != kIOReturnSuccess) break;
        for (UInt8 index = 1; index <= count; index++) {
            UInt8 direction, endpoint, type, interval; UInt16 size;
            if ((*interface)->GetPipeProperties(interface, index, &direction, &endpoint, &type, &size, &interval) == kIOReturnSuccess &&
                direction == kUSBOut && type == kUSBBulk) { pipe = index; break; }
        }
        status = pipe ? kIOReturnSuccess : kIOReturnNotFound;
        break;
    }
    IOObjectRelease(iterator);
    if (interface && status == kIOReturnSuccess) {
        static uint8_t packets[][28] = {
            {0x03,0x91,0,0x0d,0,8,0,0,1,0,0xff,0xff,0xff,0xff,0xff,0xff},
            {0x0c,0x91,0,2,0,4,0,0,0x27,0,0,0},
            {0x0c,0x91,0,4,0,4,0,0,0x27,0,0,0},
            {0x0a,0x91,0,8,0,0x14,0,0,1,0xff,0xff,0xff,0xff,0xff,0xff,0xff,0xff,0x35,0,0x46,0,0,0,0,0,0,0,0},
            {0x03,0x91,0,0x0a,0,4,0,0,5,0,0,0}
        };
        static const uint32_t lengths[] = {16,12,12,28,12};
        const int first = report_id ? 4 : 0;
        uint8_t select_report[] = {0x03,0x91,0,0x0a,0,4,0,0,report_id,0,0,0};
        for (int index = first; index < 5; index++) {
            if (cancelled(token)) { status = kIOReturnAborted; break; }
            status = (*interface)->WritePipeTO(interface, pipe, report_id ? select_report : packets[index], lengths[index], 1500, 1500);
            if (status != kIOReturnSuccess) break;
            usleep(20000);
        }
    }
    if (interface) { (*interface)->USBInterfaceClose(interface); (*interface)->Release(interface); }
    return status;
}
int32_t pro2_usb_initialize(uint32_t location_id, Pro2USBCancellation *token) {
    return command_channel(location_id, token, 0);
}
int32_t pro2_usb_select_input_report(uint32_t location_id, uint8_t report_id, Pro2USBCancellation *token) {
    if (report_id != 5 && report_id != 9) return kIOReturnBadArgument;
    return command_channel(location_id, token, report_id);
}
