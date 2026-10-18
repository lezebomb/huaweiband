#pragma once
#include <windows.h>
#include <sddl.h>
#include <string>

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
struct LabPolicy { bool enabled = false; std::wstring sid; };
inline LabPolicy ReadLabPolicy() {
    LabPolicy policy;
    if (!IsSupportedVmwareGuest() || GetSystemMetrics(SM_REMOTESESSION)) return policy;
    DWORD enabled = 0, bytes = sizeof(enabled);
    if (RegGetValueW(HKEY_LOCAL_MACHINE, BandKeyLabConfig, L"Enabled", RRF_RT_REG_DWORD | RRF_SUBKEY_WOW6464KEY, nullptr, &enabled, &bytes) != ERROR_SUCCESS || enabled != 1) return policy;
    auto sid = ReadMachineString(BandKeyLabConfig, L"TargetSid"); PSID parsed = nullptr;
    if (!ConvertStringSidToSidW(sid.c_str(), &parsed)) return policy;
    bool valid = IsValidSid(parsed) != FALSE; LocalFree(parsed);
    if (valid) { policy.enabled = true; policy.sid = sid; }
    return policy;
}
