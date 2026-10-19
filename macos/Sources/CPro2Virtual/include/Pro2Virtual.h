#pragma once
#include <stdint.h>
#include <stddef.h>
typedef struct Pro2Virtual *Pro2VirtualRef;
Pro2VirtualRef pro2_virtual_create(void);
int32_t pro2_virtual_send(Pro2VirtualRef device, const uint8_t *bytes, size_t length);
void pro2_virtual_destroy(Pro2VirtualRef device);
