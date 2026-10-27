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
#include "AccountBinding.h"
#include "NativeProfile.h"

inline std::wstring ReadMachineString(const wchar_t* key, const wchar_t* name) {
    wchar_t value[1024] = {}; DWORD bytes = sizeof(value);
    if (RegGetValueW(HKEY_LOCAL_MACHINE, key, name, RRF_RT_REG_SZ | RRF_SUBKEY_WOW6464KEY, nullptr, value, &bytes) != ERROR_SUCCESS) return {};
    return value;
}
inline bool IsSupportedVmwareMachine(const std::wstring& manufacturer, const std::wstring& model, const std::wstring& build) {
    return manufacturer == L"VMware, Inc." && model.find(L"VMware") != std::wstring::npos && build == L"26300";
}
inline bool IsSupportedVmwareGuest() {
#ifdef BANDKEY_HOST_PILOT
    const auto manufacturer=ReadMachineString(L"HARDWARE\\DESCRIPTION\\System\\BIOS",L"SystemManufacturer");
    const auto machine=ReadMachineString(L"SOFTWARE\\Microsoft\\Cryptography",L"MachineGuid");
    return BandKeyHostBindingMatches(manufacturer,ReadMachineString(L"SOFTWARE\\Microsoft\\Windows NT\\CurrentVersion",L"CurrentBuildNumber"),BandKeyMachineFingerprint(machine));
#else
    return IsSupportedVmwareMachine(
        ReadMachineString(L"HARDWARE\\DESCRIPTION\\System\\BIOS", L"SystemManufacturer"),
        ReadMachineString(L"HARDWARE\\DESCRIPTION\\System\\BIOS", L"SystemProductName"),
        ReadMachineString(L"SOFTWARE\\Microsoft\\Windows NT\\CurrentVersion", L"CurrentBuildNumber"));
#endif
}
inline std::filesystem::path LabInstallDirectory() {
    PWSTR folder = nullptr;
    if (FAILED(SHGetKnownFolderPath(FOLDERID_ProgramFilesX64, 0, nullptr, &folder))) throw std::runtime_error("program-files-unavailable");
    std::filesystem::path path(folder); CoTaskMemFree(folder); return path / BandKeyDirectoryName;
}
inline bool BandKeyRemovalEnvironment(){
#ifdef BANDKEY_HOST_PILOT
    return BandKeyHostRemovalBindingMatches(ReadMachineString(L"HARDWARE\\DESCRIPTION\\System\\BIOS",L"SystemManufacturer"),BandKeyMachineFingerprint(ReadMachineString(L"SOFTWARE\\Microsoft\\Cryptography",L"MachineGuid")));
#else
    return IsSupportedVmwareGuest();
#endif
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
    DWORD signerKind = 0; // 0 = local simulation key; 1 = separately enrolled phone-lab key.
    std::vector<BYTE> publicKey;
    VmAccountKind accountKind = VmAccountKind::Local;
    std::wstring qualifiedAccount;
    DWORD protocolVersion = 1;
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
        DWORD kind = 0; bytes = sizeof(kind);
        auto kindResult = RegGetValueW(HKEY_LOCAL_MACHINE, BandKeyLabConfig, L"SignerKind", RRF_RT_REG_DWORD | RRF_SUBKEY_WOW6464KEY, nullptr, &kind, &bytes);
        if ((kindResult != ERROR_SUCCESS && kindResult != ERROR_FILE_NOT_FOUND) || kind > 1) return LabPolicy{};
        policy.signerKind = kind;
        policy.publicKey.resize(72); bytes = static_cast<DWORD>(policy.publicKey.size());
        if (RegGetValueW(HKEY_LOCAL_MACHINE, BandKeyLabConfig, L"LabPublicKey", RRF_RT_REG_BINARY | RRF_SUBKEY_WOW6464KEY, nullptr, policy.publicKey.data(), &bytes) != ERROR_SUCCESS || bytes != 72) return LabPolicy{};
    }
    DWORD accountKind = 0; bytes = sizeof(accountKind);
    auto accountResult = RegGetValueW(HKEY_LOCAL_MACHINE, BandKeyLabConfig, L"AccountKind", RRF_RT_REG_DWORD | RRF_SUBKEY_WOW6464KEY, nullptr, &accountKind, &bytes);
    if ((accountResult != ERROR_SUCCESS && accountResult != ERROR_FILE_NOT_FOUND) || accountKind > 1) return LabPolicy{};
    policy.accountKind = static_cast<VmAccountKind>(accountKind);
    policy.qualifiedAccount = ReadMachineString(BandKeyLabConfig, L"QualifiedAccount");
    if ((accountResult == ERROR_SUCCESS && policy.qualifiedAccount.empty()) || (!policy.qualifiedAccount.empty() && !ValidQualifiedAccount(policy.qualifiedAccount, policy.accountKind)) ||
        (policy.accountKind == VmAccountKind::MicrosoftAccount && (mode != 2 || policy.qualifiedAccount.empty()))) return LabPolicy{};
    DWORD protocol = 1; bytes = sizeof(protocol);
    auto protocolResult = RegGetValueW(HKEY_LOCAL_MACHINE, BandKeyLabConfig, L"ProtocolVersion", RRF_RT_REG_DWORD | RRF_SUBKEY_WOW6464KEY, nullptr, &protocol, &bytes);
    if ((protocolResult != ERROR_SUCCESS && protocolResult != ERROR_FILE_NOT_FOUND) || (protocol != 1 && protocol != 3)) return LabPolicy{};
    policy.protocolVersion = protocol;
    if(!BandKeyProfileAllowsPolicy(mode,policy.signerKind,static_cast<DWORD>(policy.accountKind),protocol))return LabPolicy{};
#ifdef BANDKEY_HOST_PILOT
    if(ReadMachineString(BandKeyLabConfig,L"DeploymentProfile")!=L"host-pilot-v1" || ReadMachineString(BandKeyLabConfig,L"HostBinding")!=BandKeyHostExpectedMachine || ReadMachineString(BandKeyLabConfig,L"TargetBuild")!=BandKeyHostExpectedBuild)return LabPolicy{};
#endif
    if (valid) { policy.enabled = true; policy.sid = sid; policy.mode = mode; }
    return policy;
}
