#pragma once

#include <string>

namespace sehff::config {

// The native side reads only what it needs before the managed part runs; the
// managed part reads the rest of the same INI.
struct NativeSettings {
    bool enabled = true;
    bool diagnostics = false;
};

[[nodiscard]] NativeSettings Load(const std::wstring& iniPath);

} // namespace sehff::config
