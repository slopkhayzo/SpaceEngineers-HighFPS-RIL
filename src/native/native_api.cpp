#include "native_api.h"

#include "logger.h"
#include "plugin_version.h"

#include <MinHook.h>

namespace sehff {
namespace {

int __cdecl CreateHook(void* target, void* detour, void** original) {
    if (target == nullptr || detour == nullptr || original == nullptr) {
        return MH_ERROR_NOT_EXECUTABLE;
    }
    MH_STATUS status = MH_CreateHook(target, detour, original);
    if (status != MH_OK) {
        return status;
    }
    status = MH_QueueEnableHook(target);
    if (status != MH_OK) {
        MH_RemoveHook(target);
    }
    return status;
}

int __cdecl ApplyQueued() {
    return MH_ApplyQueued();
}

int __cdecl RemoveHook(void* target) {
    const MH_STATUS disabled = MH_DisableHook(target);
    const MH_STATUS removed = MH_RemoveHook(target);
    return disabled != MH_OK ? disabled : removed;
}

void __cdecl Log(const int level, const wchar_t* message) {
    if (message == nullptr) {
        return;
    }
    log::Level mapped = log::Level::info;
    switch (level) {
    case 0:
        mapped = log::Level::debug;
        break;
    case 2:
        mapped = log::Level::warning;
        break;
    case 3:
        mapped = log::Level::error;
        break;
    default:
        break;
    }
    log::Write(mapped, message);
}

int __cdecl DiagnosticsEnabled() {
    return log::DiagnosticsEnabled() ? 1 : 0;
}

const NativeApi g_api{
    sizeof(NativeApi),
    SE_HIGH_FPS_FIX_API_VERSION,
    &CreateHook,
    &ApplyQueued,
    &RemoveHook,
    &Log,
    &DiagnosticsEnabled,
};

} // namespace

bool InitializeHooking() {
    const MH_STATUS status = MH_Initialize();
    return status == MH_OK || status == MH_ERROR_ALREADY_INITIALIZED;
}

const NativeApi* GetNativeApi() {
    return &g_api;
}

} // namespace sehff
