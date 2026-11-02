#pragma once

#include <cstddef>
#include <cstdint>

namespace sehff {

// Function table handed to the managed side; its address travels in the
// argument of the managed entry point. The layout is mirrored in
// src/managed/NativeApi.cs: bump SE_HIGH_FPS_FIX_API_VERSION when it changes.
struct NativeApi {
    std::uint32_t size;
    std::uint32_t version;
    // Creates a MinHook detour and queues it for enabling; *original receives
    // the trampoline. Returns the MH_STATUS value (0 = MH_OK).
    int(__cdecl* createHook)(void* target, void* detour, void** original);
    // Enables every queued hook with one thread freeze.
    int(__cdecl* applyQueued)();
    int(__cdecl* removeHook)(void* target);
    void(__cdecl* log)(int level, const wchar_t* message);
    int(__cdecl* diagnosticsEnabled)();
};

// The managed side reads the table by these offsets (NativeApi.cs).
static_assert(sizeof(NativeApi) == 48);
static_assert(offsetof(NativeApi, createHook) == 8);
static_assert(offsetof(NativeApi, applyQueued) == 16);
static_assert(offsetof(NativeApi, removeHook) == 24);
static_assert(offsetof(NativeApi, log) == 32);
static_assert(offsetof(NativeApi, diagnosticsEnabled) == 40);

[[nodiscard]] bool InitializeHooking();
[[nodiscard]] const NativeApi* GetNativeApi();

} // namespace sehff
