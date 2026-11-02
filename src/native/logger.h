#pragma once

#include <string>
#include <string_view>

namespace sehff::log {

enum class Level {
    debug,
    info,
    warning,
    error,
};

// Opens <directory>\<name>.log, keeping the previous session as
// <name>.previous.log. Falls back to %LOCALAPPDATA%\<name> when the directory
// is not writable. Returns the path in use, or an empty string.
[[nodiscard]] std::wstring Initialize(const std::wstring& directory, const std::wstring& name);
void SetDiagnosticsEnabled(bool enabled);
[[nodiscard]] bool DiagnosticsEnabled();
void Write(Level level, std::wstring_view message);

} // namespace sehff::log
