#pragma once
#include <windows.h>
#include <sddl.h>
#include <string>
#include <stdexcept>
#include <vector>
#include <filesystem>
#include <shlobj.h>
#include <knownfolders.h>
#include <wtsapi32.h>

// This CLSID belongs only to the VM laboratory provider. No system provider is replaced.
inline constexpr GUID BandKeyLabClsid = {0x76b424b6,0xa72d,0x4e98,{0x9d,0xc1,0x48,0x5e,0x37,0xa5,0x42,0x9c}};
inline constexpr wchar_t BandKeyLabGuid[] = L"{76B424B6-A72D-4E98-9DC1-485E37A5429C}";
inline constexpr wchar_t BandKeyLabConfig[] = L"SOFTWARE\\BandKey\\CredentialProviderLab";
inline constexpr wchar_t BandKeyLabCom[] = L"SOFTWARE\\Classes\\CLSID\\{76B424B6-A72D-4E98-9DC1-485E37A5429C}";
inline constexpr wchar_t BandKeyLabRegistration[] = L"SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Authentication\\Credential Providers\\{76B424B6-A72D-4E98-9DC1-485E37A5429C}";

inline std::wstring ReadMachineString(const wchar_t* key, const wchar_t* name) {
    wchar_t value[1024] = {}; DWORD bytes = sizeof(value);
    if (RegGetValueW(HKEY_LOCAL_MACHINE, key, name, RRF_RT_REG_SZ | RRF_SUBKEY_WOW6464KEY, nullptr, value, &bytes) != ERROR_SUCCESS) return {};
    return value;
}
inline bool IsSupportedVmwareMachine(const std::wstring& manufacturer, const std::wstring& model, const std::wstring& build) {
    return manufacturer == L"VMware, Inc." && model.find(L"VMware") != std::wstring::npos && build == L"26300";
}
inline bool IsSupportedVmwareGuest() {
    return IsSupportedVmwareMachine(
        ReadMachineString(L"HARDWARE\\DESCRIPTION\\System\\BIOS", L"SystemManufacturer"),
        ReadMachineString(L"HARDWARE\\DESCRIPTION\\System\\BIOS", L"SystemProductName"),
        ReadMachineString(L"SOFTWARE\\Microsoft\\Windows NT\\CurrentVersion", L"CurrentBuildNumber"));
}
inline std::filesystem::path LabInstallDirectory() {
    PWSTR folder = nullptr;
    if (FAILED(SHGetKnownFolderPath(FOLDERID_ProgramFilesX64, 0, nullptr, &folder))) throw std::runtime_error("program-files-unavailable");
    std::filesystem::path path(folder); CoTaskMemFree(folder); return path / L"BandKeyVmLab";
}
inline bool SessionIsLocked(DWORD session) {
    PWSTR buffer = nullptr; DWORD bytes = 0;
    if (!WTSQuerySessionInformationW(WTS_CURRENT_SERVER_HANDLE, session, WTSSessionInfoEx, &buffer, &bytes)) return false;
    bool locked = false;
    if (buffer && bytes >= sizeof(WTSINFOEXW)) {
        auto info = reinterpret_cast<WTSINFOEXW*>(buffer);
        locked = info->Level == 1 && info->Data.WTSInfoExLevel1.SessionId == session && info->Data.WTSInfoExLevel1.SessionFlags == WTS_SESSIONSTATE_LOCK;
    }
    WTSFreeMemory(buffer); return locked;
}
struct LabPolicy {
    bool enabled = false;
    std::wstring sid;
    DWORD mode = 0; // 0 = manual password; 2 = isolated VM simulated-signature + vault experiment.
    std::vector<BYTE> publicKey;
    LabPolicy(bool active = false, std::wstring target = {}) : enabled(active), sid(std::move(target)) {}
};
inline LabPolicy ReadLabPolicy() {
    LabPolicy policy;
    if (!IsSupportedVmwareGuest() || GetSystemMetrics(SM_REMOTESESSION)) return policy;
    DWORD enabled = 0, bytes = sizeof(enabled);
    if (RegGetValueW(HKEY_LOCAL_MACHINE, BandKeyLabConfig, L"Enabled", RRF_RT_REG_DWORD | RRF_SUBKEY_WOW6464KEY, nullptr, &enabled, &bytes) != ERROR_SUCCESS || enabled != 1) return policy;
    auto sid = ReadMachineString(BandKeyLabConfig, L"TargetSid"); PSID parsed = nullptr;
    if (!ConvertStringSidToSidW(sid.c_str(), &parsed)) return policy;
    bool valid = IsValidSid(parsed) != FALSE; LocalFree(parsed);
    DWORD mode = 0; bytes = sizeof(mode);
    auto result = RegGetValueW(HKEY_LOCAL_MACHINE, BandKeyLabConfig, L"Mode", RRF_RT_REG_DWORD | RRF_SUBKEY_WOW6464KEY, nullptr, &mode, &bytes);
    if (result != ERROR_SUCCESS && result != ERROR_FILE_NOT_FOUND) return policy;
    if (mode != 0 && mode != 2) return policy;
    if (mode == 2) {
        policy.publicKey.resize(72); bytes = static_cast<DWORD>(policy.publicKey.size());
        if (RegGetValueW(HKEY_LOCAL_MACHINE, BandKeyLabConfig, L"LabPublicKey", RRF_RT_REG_BINARY | RRF_SUBKEY_WOW6464KEY, nullptr, policy.publicKey.data(), &bytes) != ERROR_SUCCESS || bytes != 72) return LabPolicy{};
    }
    if (valid) { policy.enabled = true; policy.sid = sid; policy.mode = mode; }
    return policy;
}
