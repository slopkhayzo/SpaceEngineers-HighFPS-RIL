# Bundled loader provenance

- Project: Ultimate ASI Loader
- Repository: https://github.com/ThirteenAG/Ultimate-ASI-Loader
- Release: `v9.7.4` / official `x64-latest` `version-x64.zip`
- Product version: `9.7.4`
- Archive SHA-256: `76115DDD48AD8E614AFDBC4BCD9B0E82D65746D6151A5E4DC646FFE85630E73E`
- `version.dll` SHA-512: `840C97B7B3ED40DE3D4E6372D9BD43B022796F4EA57C71969B5DED978B344ED5A70579318337877B9AAD8C6F0E74CE19B32F1AC5D4C5E5679D5F4682819937FA`

The binary itself is not stored in Git. Pass the verified x64 binary to CMake
through `SEHFF_ULTIMATE_ASI_LOADER_DLL` when producing the loader-bundled
package; it is shipped under the name in `SEHFF_LOADER_NAME`.

## Why `dinput8.dll` and not `version.dll`

`SpaceEngineers.exe` is a .NET Framework program whose only import is
`mscoree.dll`. Windows resolves the imports of that runtime shim (which
include `VERSION.dll`) from System32 only, so a `version.dll` in `Bin64` is
never loaded (checked on 2026-11-02: `VERSION.dll` came from
`C:\WINDOWS\SYSTEM32` with the loader in `Bin64`).

Later DLLs are found through the normal search order, application folder
first. The loader is shipped as `dinput8.dll`: the game always loads
DirectInput (`MyVRagePlatform.CreateInput2` reads the mouse and controllers
through SharpDX.DirectInput), the name does not collide
with graphics injectors such as ReShade or Special K (which use `dxgi.dll` or
`d3d11.dll`), and in tests the loader loaded from `Bin64` under that name
alone. `d3d11.dll` also works (it loads earlier, during device creation).
