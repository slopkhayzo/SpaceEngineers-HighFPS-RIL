#include "bootstrap.h"

#include "clr_host.h"
#include "config.h"
#include "logger.h"
#include "native_api.h"
#include "plugin_version.h"

#include <Psapi.h>

#include <algorithm>
#include <array>
#include <cstdint>
#include <string>

namespace sehff {
namespace {

constexpr wchar_t kManagedAssemblyName[] = SE_HIGH_FPS_FIX_NAME L".Managed.dll";
constexpr wchar_t kManagedEntryType[] = L"SpaceEngineers.HighFPSFix.Plugin";
constexpr wchar_t kManagedEntryMethod[] = L"Start";

std::wstring ModulePath(const HMODULE module) {
    std::wstring path(MAX_PATH, L'\0');
    for (;;) {
        const DWORD length =
            GetModuleFileNameW(module, path.data(), static_cast<DWORD>(path.size()));
        if (length == 0) {
            return {};
        }
        if (length < path.size()) {
            path.resize(length);
            return path;
        }
        path.resize(path.size() * 2);
    }
}

std::wstring DirectoryOf(const std::wstring& path) {
    const std::size_t slash = path.find_last_of(L"\\/");
    return slash == std::wstring::npos ? std::wstring() : path.substr(0, slash);
}

std::wstring FileNameOf(const std::wstring& path) {
    const std::size_t slash = path.find_last_of(L"\\/");
    return slash == std::wstring::npos ? path : path.substr(slash + 1);
}

// Logs every DLL loaded from the game folder under a name the Ultimate ASI
// Loader can take, so that a report shows which loader copy is in use. A
// proxy and the system DLL it forwards to share the base name, so modules are
// enumerated rather than looked up by name.
void LogLoaderModules(const std::wstring& gameDirectory) {
    constexpr const wchar_t* kCandidates[] = {L"dinput8.dll", L"winhttp.dll", L"d3d11.dll",
                                              L"dxgi.dll",    L"version.dll", L"winmm.dll"};
    std::array<HMODULE, 1024> modules{};
    DWORD needed = 0;
    if (!K32EnumProcessModules(GetCurrentProcess(), modules.data(),
                               static_cast<DWORD>(sizeof(modules)), &needed)) {
        return;
    }
    std::wstring text = L"ASI loader copies in the game folder:";
    bool any = false;
    const std::size_t count = std::min<std::size_t>(needed / sizeof(HMODULE), modules.size());
    for (std::size_t i = 0; i < count; ++i) {
        wchar_t path[MAX_PATH]{};
        if (GetModuleFileNameW(modules[i], path, MAX_PATH) == 0 ||
            _wcsnicmp(path, gameDirectory.c_str(), gameDirectory.size()) != 0) {
            continue;
        }
        const std::wstring name = FileNameOf(path);
        for (const wchar_t* candidate : kCandidates) {
            if (_wcsicmp(name.c_str(), candidate) == 0) {
                text += L" " + name;
                any = true;
            }
        }
    }
    log::Write(log::Level::info, any ? text : text + L" none");
}

std::wstring PointerText(const void* pointer) {
    std::array<wchar_t, 32> text{};
    _snwprintf_s(text.data(), text.size(), _TRUNCATE, L"0x%llX",
                 static_cast<unsigned long long>(reinterpret_cast<std::uintptr_t>(pointer)));
    return text.data();
}

} // namespace

DWORD WINAPI BootstrapThread(void* moduleParameter) {
    const auto module = static_cast<HMODULE>(moduleParameter);
    const std::wstring pluginPath = ModulePath(module);
    const std::wstring directory = DirectoryOf(pluginPath);
    const std::wstring iniPath = directory + L"\\" SE_HIGH_FPS_FIX_NAME L".ini";

    const config::NativeSettings settings = config::Load(iniPath);
    const std::wstring logPath = log::Initialize(directory, SE_HIGH_FPS_FIX_NAME);
    log::SetDiagnosticsEnabled(settings.diagnostics);
    log::Write(log::Level::info,
               std::wstring(SE_HIGH_FPS_FIX_NAME L" " SE_HIGH_FPS_FIX_VERSION L" loaded from ") +
                   pluginPath);

    LogLoaderModules(directory);

    const std::wstring executable = FileNameOf(ModulePath(nullptr));
    if (_wcsicmp(executable.c_str(), L"SpaceEngineers.exe") != 0) {
        log::Write(log::Level::warning,
                   L"Not SpaceEngineers.exe (" + executable + L"); the plugin stays idle.");
        return 0;
    }
    if (!settings.enabled) {
        log::Write(log::Level::info, L"[General] Enabled = false; the plugin stays idle.");
        return 0;
    }
    if (!InitializeHooking()) {
        log::Write(log::Level::error, L"MinHook could not be initialized; the plugin stays idle.");
        return 0;
    }

    const std::wstring assemblyPath = directory + L"\\" + kManagedAssemblyName;
    if (GetFileAttributesW(assemblyPath.c_str()) == INVALID_FILE_ATTRIBUTES) {
        log::Write(log::Level::error, L"Missing " + assemblyPath + L"; the plugin stays idle.");
        return 0;
    }

    const std::wstring argument = L"api=" + PointerText(GetNativeApi()) + L";dir=" + directory +
                                  L";ini=" + iniPath + L";log=" + logPath;
    unsigned long managedResult = 0;
    if (!clr::RunManagedEntryPoint(assemblyPath, kManagedEntryType, kManagedEntryMethod, argument,
                                   managedResult)) {
        log::Write(log::Level::error, L"The managed part did not start; the game runs unmodified.");
        return 0;
    }
    LogLoaderModules(directory);
    if (managedResult != 0) {
        log::Write(log::Level::error, L"The managed part reported a start failure (" +
                                          std::to_wstring(managedResult) + L").");
    }
    return 0;
}

} // namespace sehff
