#include "config.h"

#include <Windows.h>

#include <array>
#include <cwctype>
#include <string>

namespace sehff::config {
namespace {

bool ReadBool(const std::wstring& iniPath, const wchar_t* section, const wchar_t* key,
              const bool fallback) {
    std::array<wchar_t, 32> value{};
    GetPrivateProfileStringW(section, key, fallback ? L"true" : L"false", value.data(),
                             static_cast<DWORD>(value.size()), iniPath.c_str());
    std::wstring text(value.data());
    for (wchar_t& character : text) {
        character = static_cast<wchar_t>(std::towlower(character));
    }
    if (text == L"true" || text == L"1" || text == L"yes" || text == L"on") {
        return true;
    }
    if (text == L"false" || text == L"0" || text == L"no" || text == L"off") {
        return false;
    }
    return fallback;
}

} // namespace

NativeSettings Load(const std::wstring& iniPath) {
    NativeSettings settings;
    settings.enabled = ReadBool(iniPath, L"General", L"Enabled", settings.enabled);
    settings.diagnostics = ReadBool(iniPath, L"General", L"Diagnostics", settings.diagnostics);
    return settings;
}

} // namespace sehff::config
