#pragma once

#include <string>

namespace sehff::clr {

// Waits until the process's .NET Framework 4 runtime is started and the game's
// own assemblies are loaded, then runs the managed entry point
// (static int Method(string)) in the default AppDomain. Returns false when the
// runtime never showed up or the call failed; the reason is logged.
[[nodiscard]] bool RunManagedEntryPoint(const std::wstring& assemblyPath,
                                        const std::wstring& typeName,
                                        const std::wstring& methodName,
                                        const std::wstring& argument,
                                        unsigned long& returnValue);

} // namespace sehff::clr
