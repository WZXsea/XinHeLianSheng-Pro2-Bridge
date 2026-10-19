// Apache-2.0. Uses only libvirtualhid's MIT-licensed public C++ API.
#include "Pro2Broker.h"
#include <libvirtualhid/runtime.hpp>
#include <libvirtualhid/profiles.hpp>
#include <libvirtualhid/license.hpp>
#include <libvirtualhid/report.hpp>
#include <algorithm>
#include <cstdio>
#include <cstring>
#include <deque>
#include <memory>
#include <numbers>
#include <mutex>
#include <exception>

namespace {
struct FeedbackQueue { std::mutex mutex; std::deque<Pro2BrokerFeedback> reports; };
struct GamepadHandle {
    std::unique_ptr<lvh::Runtime> runtime;
    std::unique_ptr<lvh::Gamepad> gamepad;
    std::shared_ptr<FeedbackQueue> feedback = std::make_shared<FeedbackQueue>();
};
void status_result(Pro2BrokerStatus *out,const lvh::OperationStatus &status) {
    if (!out) return;
    std::memset(out,0,sizeof(*out)); out->code = static_cast<int32_t>(status.code());
    std::snprintf(out->message,sizeof(out->message),"%s",status.message().c_str());
}
void exception_result(Pro2BrokerStatus *out,const char *message) {
    if (!out) return;
    std::memset(out,0,sizeof(*out)); out->code = 9;
    std::snprintf(out->message,sizeof(out->message),"%s",message);
}
lvh::DeviceProfile profile_for(int32_t profile) {
    switch (profile) {
    case 1: return lvh::profiles::xbox_360();
    case 2: return lvh::profiles::dualshock4_usb();
    case 3: return lvh::profiles::dualsense_usb();
    case 4: return lvh::profiles::switch_pro();
    default: return lvh::profiles::generic_gamepad();
    }
}
}
extern "C" uint32_t pro2_lvh_abi_version(void) { return PRO2_BROKER_ABI_VERSION; }
extern "C" int32_t pro2_lvh_probe(Pro2BrokerStatus *out) {
    try {
        auto result = lvh::get_license_status(); status_result(out,result.status);
        if (out) {
            out->service_available = result.license.service_available;
            out->license_state = static_cast<int32_t>(result.license.state);
            out->active_devices = result.license.active_devices;
            out->activation_limit = result.license.activation_limit;
            out->activation_usage = result.license.activation_usage;
            std::snprintf(out->purchase_url,sizeof(out->purchase_url),"%s",result.license.purchase_url.c_str());
            if (result.status.ok()) std::snprintf(out->message,sizeof(out->message),"%s",result.license.message.c_str());
        }
        return static_cast<int32_t>(result.status.code());
    } catch (const std::exception &e) { exception_result(out,e.what()); return 9; }
    catch (...) { exception_result(out,"libvirtualhid probe failed"); return 9; }
}
extern "C" void *pro2_lvh_create(int32_t profile,const char *stable_id,Pro2BrokerStatus *out) {
    try {
        if (profile < 0 || profile > 4) { exception_result(out,"Unsupported virtual gamepad profile"); return nullptr; }
        auto handle = std::make_unique<GamepadHandle>();
        lvh::RuntimeOptions runtime_options; runtime_options.backend = lvh::BackendKind::platform_default;
        handle->runtime = lvh::Runtime::create(runtime_options);
        lvh::CreateGamepadOptions options; options.profile = profile_for(profile);
        options.metadata.client_type = lvh::ClientControllerType::nintendo;
        options.metadata.has_motion_sensors = true; options.metadata.has_battery = true;
        options.metadata.stable_id = stable_id && *stable_id ? stable_id : "PRO2-BRIDGE-S1";
        auto result = handle->runtime->create_gamepad(options); status_result(out,result.status);
        if (!result) return nullptr;
        handle->gamepad = std::move(result.gamepad);
        auto queue = handle->feedback;
        handle->gamepad->set_output_callback([queue](const lvh::GamepadOutput &output) {
            Pro2BrokerFeedback report {};
            report.kind = static_cast<uint32_t>(output.kind);
            report.low_rumble = output.low_frequency_rumble; report.high_rumble = output.high_frequency_rumble;
            report.left_trigger_rumble = output.left_trigger_rumble; report.right_trigger_rumble = output.right_trigger_rumble;
            report.red = output.red; report.green = output.green; report.blue = output.blue;
            for (size_t i=0;i<4;i++) { if (output.player_leds[i]) report.player_leds |= 1U<<i; if (output.flashing_player_leds[i]) report.flashing_player_leds |= 1U<<i; }
            report.raw_size = static_cast<uint32_t>(std::min(output.raw_report.size(),size_t(PRO2_BROKER_MAX_RAW_REPORT)));
            std::copy_n(output.raw_report.begin(),report.raw_size,report.raw_report);
            std::lock_guard lock(queue->mutex);
            if (queue->reports.size() >= 64) queue->reports.pop_front();
            queue->reports.push_back(report);
        });
        return handle.release();
    } catch (const std::exception &e) { exception_result(out,e.what()); return nullptr; }
    catch (...) { exception_result(out,"libvirtualhid gamepad creation failed"); return nullptr; }
}
extern "C" int32_t pro2_lvh_send(void *opaque,const Pro2BrokerInput *input,Pro2BrokerStatus *out) {
    try {
        if (!opaque || !input) { exception_result(out,"Virtual gamepad is closed"); return 3; }
        auto handle = static_cast<GamepadHandle *>(opaque);
        lvh::GamepadState state;
        struct Button { uint32_t flag; lvh::GamepadButton button; };
        const Button buttons[] = {
            {1U<<2,lvh::GamepadButton::a},{1U<<3,lvh::GamepadButton::b},{1U<<0,lvh::GamepadButton::x},{1U<<1,lvh::GamepadButton::y},
            {1U<<8,lvh::GamepadButton::back},{1U<<9,lvh::GamepadButton::start},{1U<<12,lvh::GamepadButton::guide},
            {1U<<11,lvh::GamepadButton::left_stick},{1U<<10,lvh::GamepadButton::right_stick},
            {1U<<22,lvh::GamepadButton::left_shoulder},{1U<<6,lvh::GamepadButton::right_shoulder},
            {1U<<17,lvh::GamepadButton::dpad_up},{1U<<16,lvh::GamepadButton::dpad_down},{1U<<19,lvh::GamepadButton::dpad_left},{1U<<18,lvh::GamepadButton::dpad_right},
            {1U<<13,lvh::GamepadButton::misc1},{1U<<14,lvh::GamepadButton::touchpad},
            {1U<<24,lvh::GamepadButton::paddle1},{1U<<25,lvh::GamepadButton::paddle2}
        };
        for (const auto &button:buttons) state.buttons.set(button.button,(input->buttons & button.flag)!=0);
        state.left_stick = {std::clamp(input->lx,-1.0F,1.0F),std::clamp(input->ly,-1.0F,1.0F)};
        state.right_stick = {std::clamp(input->rx,-1.0F,1.0F),std::clamp(input->ry,-1.0F,1.0F)};
        state.left_trigger = std::clamp(input->left_trigger,0.0F,1.0F); state.right_trigger = std::clamp(input->right_trigger,0.0F,1.0F);
        if (input->motion_valid) {
            state.acceleration = lvh::Vector3 {input->accel_x,input->accel_y,input->accel_z};
            state.gyroscope = lvh::Vector3 {input->gyro_x,input->gyro_y,input->gyro_z};
        }
        if (input->battery_percent >= 0) {
            lvh::GamepadBattery battery; battery.percentage = static_cast<uint8_t>(std::clamp(input->battery_percent,0,100));
            battery.state = input->charging ? lvh::GamepadBatteryState::charging : lvh::GamepadBatteryState::discharging;
            state.battery = battery;
        }
        auto status = handle->gamepad->submit(state); status_result(out,status); return static_cast<int32_t>(status.code());
    } catch (const std::exception &e) { exception_result(out,e.what()); return 9; }
    catch (...) { exception_result(out,"libvirtualhid submit failed"); return 9; }
}
extern "C" int32_t pro2_lvh_read_feedback(void *opaque,Pro2BrokerFeedback *out) {
    if (!opaque || !out) return 0;
    auto handle = static_cast<GamepadHandle *>(opaque); std::lock_guard lock(handle->feedback->mutex);
    if (handle->feedback->reports.empty()) return 0;
    *out = handle->feedback->reports.front(); handle->feedback->reports.pop_front(); return 1;
}
extern "C" void pro2_lvh_destroy(void *opaque) {
    if (!opaque) return;
    std::unique_ptr<GamepadHandle> handle(static_cast<GamepadHandle *>(opaque));
    try { handle->gamepad->set_output_callback({}); static_cast<void>(handle->gamepad->submit(lvh::GamepadState {})); static_cast<void>(handle->gamepad->close()); }
    catch (...) {}
}

// Exercises official descriptor packers in memory; system-wide availability is probed separately.
extern "C" int32_t pro2_lvh_client_self_test(Pro2BrokerStatus *out) {
    try {
        lvh::RuntimeOptions options; options.backend = lvh::BackendKind::fake;
        auto runtime = lvh::Runtime::create(options);
        for (int profile=0;profile<5;profile++) {
            auto created = runtime->create_gamepad(profile_for(profile));
            if (!created) { status_result(out,created.status); return static_cast<int32_t>(created.status.code()); }
            lvh::GamepadState input; input.buttons.set(lvh::GamepadButton::a); input.left_stick = {1.0F,-1.0F}; input.right_trigger = 1.0F;
            input.gyroscope = lvh::Vector3 {1.0F,2.0F,3.0F}; input.acceleration = lvh::Vector3 {0.0F,0.0F,9.80665F};
            auto status = created.gamepad->submit(input);
            if (!status.ok()) { status_result(out,status); return static_cast<int32_t>(status.code()); }
            if (created.gamepad->last_input_report().size() != created.gamepad->profile().input_report_size) { exception_result(out,"Profile packer report size mismatch"); return 9; }
            static_cast<void>(created.gamepad->close());
        }
        // Golden HID values match the original Windows VirtualPadPackets fixture.
        // The pinned upstream packers use profile-specific units, so adapters invert
        // those exact factors rather than assuming uniform degrees/second or m/s².
        constexpr float ps5GyroScale = 1145.0F * std::numbers::pi_v<float> / 180.0F;
        constexpr float ps5AccelScale = 9.80665F * 100.0F;
        lvh::GamepadState ps5Motion;
        ps5Motion.gyroscope = lvh::Vector3 {200.0F/ps5GyroScale,900.0F/ps5GyroScale,-800.0F/ps5GyroScale};
        ps5Motion.acceleration = lvh::Vector3 {2000.0F/ps5AccelScale,6000.0F/ps5AccelScale,-4000.0F/ps5AccelScale};
        auto ps5Report = lvh::reports::pack_input_report(lvh::profiles::dualsense_usb(),ps5Motion);
        auto readI16 = [](const std::vector<uint8_t> &report,size_t offset) {return static_cast<int16_t>(static_cast<uint16_t>(report.at(offset)) | (static_cast<uint16_t>(report.at(offset+1)) << 8));};
        if (readI16(ps5Report,16)!=200 || readI16(ps5Report,18)!=900 || readI16(ps5Report,20)!=-800 ||
            readI16(ps5Report,22)!=2000 || readI16(ps5Report,24)!=6000 || readI16(ps5Report,26)!=-4000) {
            exception_result(out,"DualSense HID motion scale/axis compatibility failed"); return 9;
        }
        lvh::GamepadState pro1Motion;
        pro1Motion.gyroscope = lvh::Vector3 {-200.0F/14.2842F,300.0F/14.2842F,-100.0F/14.2842F};
        pro1Motion.acceleration = lvh::Vector3 {-2000.0F*9.80665F/4096.0F,3000.0F*9.80665F/4096.0F,-1000.0F*9.80665F/4096.0F};
        auto pro1Report = lvh::reports::pack_input_report(lvh::profiles::switch_pro(),pro1Motion);
        for (size_t offset : {13U,25U,37U}) {
            if (readI16(pro1Report,offset)!=1000 || readI16(pro1Report,offset+2)!=2000 || readI16(pro1Report,offset+4)!=3000 ||
                readI16(pro1Report,offset+6)!=100 || readI16(pro1Report,offset+8)!=200 || readI16(pro1Report,offset+10)!=300) {
                exception_result(out,"Switch Pro HID motion scale/axis compatibility failed"); return 9;
            }
        }
        status_result(out,lvh::OperationStatus::success());
        if (out) std::snprintf(out->message,sizeof(out->message),"Five official profile packers and Windows-compatible DualSense/Switch motion fixtures passed");
        return 0;
    } catch (const std::exception &e) { exception_result(out,e.what()); return 9; }
    catch (...) { exception_result(out,"libvirtualhid client self-test failed"); return 9; }
}
