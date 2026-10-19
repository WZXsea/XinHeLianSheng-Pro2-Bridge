// Apache-2.0. Optional C ABI boundary around the MIT libvirtualhid public API.
#pragma once
#include <stddef.h>
#include <stdint.h>
#ifdef __cplusplus
extern "C" {
#endif
#define PRO2_BROKER_ABI_VERSION 1
#define PRO2_BROKER_MAX_RAW_REPORT 1024
// 0 Generic, 1 Xbox 360, 2 DualShock 4 USB, 3 DualSense USB, 4 Switch Pro.
typedef struct Pro2BrokerHandle *Pro2BrokerRef;
typedef struct {
    int32_t code; // lvh::ErrorCode; negative values describe loading/ABI failures.
    int32_t service_available;
    int32_t license_state; // unavailable, unlicensed, licensed, expired, disabled, invalid.
    uint32_t active_devices;
    uint32_t activation_limit;
    uint32_t activation_usage;
    char message[512];
    char purchase_url[256];
} Pro2BrokerStatus;
typedef struct {
    uint32_t buttons; // Native Pro2Core FD2 button flags.
    float lx, ly, rx, ry; // -1...1; +Y is up.
    float left_trigger, right_trigger; // 0...1.
    int32_t motion_valid;
    float accel_x, accel_y, accel_z; // m/s².
    float gyro_x, gyro_y, gyro_z; // degrees/s.
    int32_t battery_percent; // -1 when unknown, otherwise 0...100.
    int32_t charging;
} Pro2BrokerInput;
typedef struct {
    uint32_t kind; // rumble, rgb_led, adaptive_triggers, raw_report, trigger_rumble, player_leds.
    uint16_t low_rumble, high_rumble, left_trigger_rumble, right_trigger_rumble;
    uint8_t red, green, blue, player_leds, flashing_player_leds;
    uint32_t raw_size;
    uint8_t raw_report[PRO2_BROKER_MAX_RAW_REPORT];
} Pro2BrokerFeedback;
// Probes existing service/license. Performs no installation, activation or device creation.
int32_t pro2_broker_client_self_test(const char *shim_path, Pro2BrokerStatus *status);
int32_t pro2_broker_probe(const char *shim_path, Pro2BrokerStatus *status);
Pro2BrokerRef pro2_broker_create(const char *shim_path, int32_t profile, const char *stable_id, Pro2BrokerStatus *status);
int32_t pro2_broker_send(Pro2BrokerRef handle, const Pro2BrokerInput *input, Pro2BrokerStatus *status);
int32_t pro2_broker_read_feedback(Pro2BrokerRef handle, Pro2BrokerFeedback *feedback);
void pro2_broker_destroy(Pro2BrokerRef handle);
#ifdef __cplusplus
}
#endif
