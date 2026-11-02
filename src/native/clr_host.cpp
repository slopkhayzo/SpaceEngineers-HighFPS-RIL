#include "clr_host.h"

#include "logger.h"

#include <Windows.h>
#include <metahost.h>

#include <array>
#include <string>

namespace sehff::clr {
namespace {

// CLSIDs from metahost.h/mscoree.h, defined here so that the plugin needs no
// import library for mscoree.dll (the game has it loaded already).
constexpr CLSID kClsidClrMetaHost = {
    0x9280188d, 0x0e8e, 0x4867, {0xb3, 0x0c, 0x7f, 0xa8, 0x38, 0x84, 0xe8, 0xde}};
constexpr CLSID kClsidClrRuntimeHost = {
    0x90f1a06e, 0x7712, 0x4762, {0x86, 0xb5, 0x7a, 0x5e, 0xba, 0x6b, 0xdb, 0x02}};

constexpr DWORD kPollMilliseconds = 5;
constexpr ULONGLONG kRuntimeTimeoutMilliseconds = 180'000;
// The game's assemblies show up as modules once its Main is running; the
// entry point is not called before that, so that the default AppDomain is
// fully set up.
constexpr ULONGLONG kGameAssemblyTimeoutMilliseconds = 180'000;

using ClrCreateInstanceFn = HRESULT(WINAPI*)(REFCLSID, REFIID, LPVOID*);

template <typename T>
class ComPtr final {
public:
    ComPtr() = default;
    ComPtr(const ComPtr&) = delete;
    ComPtr& operator=(const ComPtr&) = delete;
    ~ComPtr() {
        if (pointer_ != nullptr) {
            pointer_->Release();
        }
    }
    T* operator->() const noexcept { return pointer_; }
    T** Put() noexcept { return &pointer_; }
    [[nodiscard]] T* Get() const noexcept { return pointer_; }

private:
    T* pointer_ = nullptr;
};

std::wstring HexResult(const HRESULT result) {
    std::array<wchar_t, 16> text{};
    _snwprintf_s(text.data(), text.size(), _TRUNCATE, L"0x%08lX",
                 static_cast<unsigned long>(result));
    return text.data();
}

bool WaitForModule(const wchar_t* name, const ULONGLONG timeoutMilliseconds,
                   const wchar_t* alternative = nullptr) {
    const ULONGLONG start = GetTickCount64();
    while (GetModuleHandleW(name) == nullptr &&
           (alternative == nullptr || GetModuleHandleW(alternative) == nullptr)) {
        if (GetTickCount64() - start > timeoutMilliseconds) {
            return false;
        }
        Sleep(kPollMilliseconds);
    }
    return true;
}

bool FindStartedRuntime(ICLRMetaHost* metaHost, ComPtr<ICLRRuntimeInfo>& runtime) {
    const ULONGLONG start = GetTickCount64();
    for (;;) {
        ComPtr<IEnumUnknown> runtimes;
        const HRESULT enumerated =
            metaHost->EnumerateLoadedRuntimes(GetCurrentProcess(), runtimes.Put());
        if (SUCCEEDED(enumerated)) {
            for (;;) {
                ComPtr<IUnknown> item;
                ULONG fetched = 0;
                if (runtimes->Next(1, item.Put(), &fetched) != S_OK || fetched == 0) {
                    break;
                }
                ComPtr<ICLRRuntimeInfo> info;
                if (FAILED(item->QueryInterface(__uuidof(ICLRRuntimeInfo),
                                                reinterpret_cast<void**>(info.Put())))) {
                    continue;
                }
                std::array<wchar_t, 64> version{};
                DWORD length = static_cast<DWORD>(version.size());
                if (FAILED(info->GetVersionString(version.data(), &length)) ||
                    wcsncmp(version.data(), L"v4.", 3) != 0) {
                    continue;
                }
                BOOL started = FALSE;
                DWORD flags = 0;
                if (SUCCEEDED(info->IsStarted(&started, &flags)) && started) {
                    log::Write(log::Level::info,
                               std::wstring(L"CLR ") + version.data() + L" is started.");
                    *runtime.Put() = info.Get();
                    info.Get()->AddRef();
                    return true;
                }
            }
        }
        if (GetTickCount64() - start > kRuntimeTimeoutMilliseconds) {
            return false;
        }
        Sleep(kPollMilliseconds);
    }
}

} // namespace

bool RunManagedEntryPoint(const std::wstring& assemblyPath, const std::wstring& typeName,
                          const std::wstring& methodName, const std::wstring& argument,
                          unsigned long& returnValue) {
    if (!WaitForModule(L"clr.dll", kRuntimeTimeoutMilliseconds)) {
        log::Write(log::Level::error, L"The .NET Framework 4 runtime (clr.dll) never loaded.");
        return false;
    }

    const HMODULE mscoree = GetModuleHandleW(L"mscoree.dll");
    const auto createInstance = mscoree == nullptr
        ? nullptr
        : reinterpret_cast<ClrCreateInstanceFn>(GetProcAddress(mscoree, "CLRCreateInstance"));
    if (createInstance == nullptr) {
        log::Write(log::Level::error, L"mscoree.dll!CLRCreateInstance is unavailable.");
        return false;
    }

    ComPtr<ICLRMetaHost> metaHost;
    HRESULT result = createInstance(kClsidClrMetaHost, __uuidof(ICLRMetaHost),
                                    reinterpret_cast<void**>(metaHost.Put()));
    if (FAILED(result)) {
        log::Write(log::Level::error, L"CLRCreateInstance failed: " + HexResult(result));
        return false;
    }

    ComPtr<ICLRRuntimeInfo> runtime;
    if (!FindStartedRuntime(metaHost.Get(), runtime)) {
        log::Write(log::Level::error, L"No started .NET Framework 4 runtime was found.");
        return false;
    }

    // The game's assemblies are usually NGen-compiled; the native image then
    // stands in for the IL assembly in the module list.
    if (!WaitForModule(L"Sandbox.Game.ni.dll", kGameAssemblyTimeoutMilliseconds,
                       L"Sandbox.Game.dll")) {
        log::Write(log::Level::error, L"Sandbox.Game was never loaded.");
        return false;
    }
    log::Write(log::Level::info, L"Sandbox.Game is loaded; starting the managed part.");

    ComPtr<ICLRRuntimeHost> host;
    result = runtime->GetInterface(kClsidClrRuntimeHost, __uuidof(ICLRRuntimeHost),
                                   reinterpret_cast<void**>(host.Put()));
    if (FAILED(result)) {
        log::Write(log::Level::error,
                   L"ICLRRuntimeInfo::GetInterface(CLRRuntimeHost) failed: " + HexResult(result));
        return false;
    }

    DWORD managedResult = 0;
    result = host->ExecuteInDefaultAppDomain(assemblyPath.c_str(), typeName.c_str(),
                                             methodName.c_str(), argument.c_str(),
                                             &managedResult);
    if (FAILED(result)) {
        log::Write(log::Level::error,
                   L"ExecuteInDefaultAppDomain failed: " + HexResult(result) + L" (" +
                       assemblyPath + L")");
        return false;
    }
    returnValue = managedResult;
    return true;
}

} // namespace sehff::clr
