#include "bootstrap.h"

#include <Windows.h>

BOOL WINAPI DllMain(const HINSTANCE instance, const DWORD reason, LPVOID) {
    if (reason != DLL_PROCESS_ATTACH) {
        return TRUE;
    }

    DisableThreadLibraryCalls(instance);
    const HANDLE bootstrap =
        CreateThread(nullptr, 0, sehff::BootstrapThread, instance, 0, nullptr);
    if (bootstrap == nullptr) {
        OutputDebugStringW(
            L"[SpaceEngineers.HighFPSFix] Bootstrap thread creation failed; "
            L"the game runs unmodified.\n");
        return TRUE;
    }
    CloseHandle(bootstrap);
    return TRUE;
}
