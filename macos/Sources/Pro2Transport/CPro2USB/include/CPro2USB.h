#ifndef CPRO2USB_H
#define CPRO2USB_H
#include <stdint.h>
#include <stddef.h>
// Opens only interface 1 of VID 057e/PID 2069 at the selected HID location.
// Sends the Windows project's five startup packets through its bulk OUT pipe.
typedef struct Pro2USBCancellation Pro2USBCancellation;
Pro2USBCancellation *pro2_usb_cancellation_create(void);
void pro2_usb_cancellation_cancel(Pro2USBCancellation *token);
void pro2_usb_cancellation_destroy(Pro2USBCancellation *token);
int32_t pro2_usb_initialize(uint32_t location_id, Pro2USBCancellation *token);
int32_t pro2_usb_select_input_report(uint32_t location_id, uint8_t report_id, Pro2USBCancellation *token);
#endif
