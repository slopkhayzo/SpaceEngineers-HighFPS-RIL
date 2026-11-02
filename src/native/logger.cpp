#include "logger.h"

#include <Windows.h>
#include <ShlObj.h>

#include <array>
#include <string>

namespace sehff::log {
namespace {

HANDLE g_file = INVALID_HANDLE_VALUE;
SRWLOCK g_lock = SRWLOCK_INIT;
bool g_diagnosticsEnabled = false;

const wchar_t* LevelName(const Level level) noexcept {
    switch (level) {
    case Level::debug:
        return L"DEBUG";
    case Level::info:
        return L"INFO";
    case Level::warning:
        return L"WARN";
    case Level::error:
        return L"ERROR";
    }
    return L"UNKNOWN";
}

std::string ToUtf8(const std::wstring_view text) {
    if (text.empty()) {
        return {};
    }
    const int length = WideCharToMultiByte(
        CP_UTF8, 0, text.data(), static_cast<int>(text.size()), nullptr, 0, nullptr, nullptr);
    if (length <= 0) {
        return {};
    }
    std::string utf8(static_cast<std::size_t>(length), '\0');
    WideCharToMultiByte(CP_UTF8, 0, text.data(), static_cast<int>(text.size()),
                        utf8.data(), length, nullptr, nullptr);
    return utf8;
}

HANDLE OpenFresh(const std::wstring& directory, const std::wstring& name, std::wstring& path) {
    path = directory + L"\\" + name + L".log";
    const std::wstring previous = directory + L"\\" + name + L".previous.log";
    MoveFileExW(path.c_str(), previous.c_str(), MOVEFILE_REPLACE_EXISTING);
    return CreateFileW(path.c_str(), GENERIC_WRITE, FILE_SHARE_READ | FILE_SHARE_DELETE,
                       nullptr, CREATE_ALWAYS, FILE_ATTRIBUTE_NORMAL, nullptr);
}

} // namespace

std::wstring Initialize(const std::wstring& directory, const std::wstring& name) {
    AcquireSRWLockExclusive(&g_lock);
    std::wstring path;
    if (g_file == INVALID_HANDLE_VALUE) {
        g_file = OpenFresh(directory, name, path);
        if (g_file == INVALID_HANDLE_VALUE) {
            PWSTR localAppData = nullptr;
            if (SUCCEEDED(SHGetKnownFolderPath(FOLDERID_LocalAppData, 0, nullptr, &localAppData))) {
                const std::wstring fallback = std::wstring(localAppData) + L"\\" + name;
                CoTaskMemFree(localAppData);
                CreateDirectoryW(fallback.c_str(), nullptr);
                g_file = OpenFresh(fallback, name, path);
            }
        }
    }
    if (g_file == INVALID_HANDLE_VALUE) {
        path.clear();
    }
    ReleaseSRWLockExclusive(&g_lock);
    return path;
}

void SetDiagnosticsEnabled(const bool enabled) {
    AcquireSRWLockExclusive(&g_lock);
    g_diagnosticsEnabled = enabled;
    ReleaseSRWLockExclusive(&g_lock);
}

bool DiagnosticsEnabled() {
    AcquireSRWLockShared(&g_lock);
    const bool enabled = g_diagnosticsEnabled;
    ReleaseSRWLockShared(&g_lock);
    return enabled;
}

void Write(const Level level, const std::wstring_view message) {
    AcquireSRWLockExclusive(&g_lock);
    if (level == Level::debug && !g_diagnosticsEnabled) {
        ReleaseSRWLockExclusive(&g_lock);
        return;
    }

    SYSTEMTIME time{};
    GetLocalTime(&time);
    std::array<wchar_t, 64> prefix{};
    _snwprintf_s(prefix.data(), prefix.size(), _TRUNCATE,
                 L"[%04u-%02u-%02u %02u:%02u:%02u.%03u] [%s] ",
                 time.wYear, time.wMonth, time.wDay, time.wHour, time.wMinute,
                 time.wSecond, time.wMilliseconds, LevelName(level));

    std::wstring line(prefix.data());
    line.append(message);
    line.append(L"\r\n");
    OutputDebugStringW(line.c_str());

    if (g_file != INVALID_HANDLE_VALUE) {
        const std::string utf8 = ToUtf8(line);
        if (!utf8.empty()) {
            DWORD written = 0;
            WriteFile(g_file, utf8.data(), static_cast<DWORD>(utf8.size()), &written, nullptr);
        }
    }
    ReleaseSRWLockExclusive(&g_lock);
}

} // namespace sehff::log
