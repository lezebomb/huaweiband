#pragma once
#include <windows.h>
#include <bcrypt.h>
#include <string>
#include <algorithm>
#include <cwctype>
#ifdef BANDKEY_HOST_PILOT
#include "HostBinding.h" // Generated locally; never contains an account or credential.
#endif

// Host pilot and VM laboratory own different classes, keys, files and locks.
#ifdef BANDKEY_HOST_PILOT
inline constexpr bool BandKeyHostPilot = true;
inline constexpr GUID BandKeyLabClsid = {0x1df46bc8,0x73b9,0x48b4,{0x8e,0x5a,0x55,0xec,0x21,0x14,0x3c,0x4e}};
inline constexpr wchar_t BandKeyLabGuid[] = L"{1DF46BC8-73B9-48B4-8E5A-55EC21143C4E}";
inline constexpr wchar_t BandKeyLabConfig[] = L"SOFTWARE\\BandKey\\HostPilot";
inline constexpr wchar_t BandKeyLabCom[] = L"SOFTWARE\\Classes\\CLSID\\{1DF46BC8-73B9-48B4-8E5A-55EC21143C4E}";
inline constexpr wchar_t BandKeyLabRegistration[] = L"SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Authentication\\Credential Providers\\{1DF46BC8-73B9-48B4-8E5A-55EC21143C4E}";
inline constexpr wchar_t BandKeyDirectoryName[] = L"BandKeyHostPilot";
inline constexpr wchar_t BandKeyProviderFile[] = L"BandKey.HostProvider.dll";
inline constexpr wchar_t BandKeyProviderLabel[] = L"BandKey host pilot";
inline constexpr wchar_t BandKeySetupMutex[] = L"Global\\BandKeyHostSetup-1DF46BC8-73B9-48B4-8E5A-55EC21143C4E";
inline constexpr wchar_t BandKeyNotificationClass[] = L"BandKey.Host.Grant.Notification.V1";
#else
inline constexpr bool BandKeyHostPilot = false;
inline constexpr GUID BandKeyLabClsid = {0x76b424b6,0xa72d,0x4e98,{0x9d,0xc1,0x48,0x5e,0x37,0xa5,0x42,0x9c}};
inline constexpr wchar_t BandKeyLabGuid[] = L"{76B424B6-A72D-4E98-9DC1-485E37A5429C}";
inline constexpr wchar_t BandKeyLabConfig[] = L"SOFTWARE\\BandKey\\CredentialProviderLab";
inline constexpr wchar_t BandKeyLabCom[] = L"SOFTWARE\\Classes\\CLSID\\{76B424B6-A72D-4E98-9DC1-485E37A5429C}";
inline constexpr wchar_t BandKeyLabRegistration[] = L"SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Authentication\\Credential Providers\\{76B424B6-A72D-4E98-9DC1-485E37A5429C}";
inline constexpr wchar_t BandKeyDirectoryName[] = L"BandKeyVmLab";
inline constexpr wchar_t BandKeyProviderFile[] = L"BandKey.CredentialProvider.dll";
inline constexpr wchar_t BandKeyProviderLabel[] = L"BandKey VM credential test";
inline constexpr wchar_t BandKeySetupMutex[] = L"Global\\BandKeyVmSetup-76B424B6-A72D-4E98-9DC1-485E37A5429C";
inline constexpr wchar_t BandKeyNotificationClass[] = L"BandKey.VM.Grant.Notification.V1";
#endif

inline std::wstring BandKeyMachineFingerprint(std::wstring value) {
    if(value.empty() || value.size()>1024)return {};
    std::transform(value.begin(),value.end(),value.begin(),[](wchar_t c){return static_cast<wchar_t>(std::towlower(c));});
    BYTE digest[32]{};BCRYPT_ALG_HANDLE algorithm=nullptr;
    if(BCryptOpenAlgorithmProvider(&algorithm,BCRYPT_SHA256_ALGORITHM,nullptr,0)<0)return {};
    const auto status=BCryptHash(algorithm,nullptr,0,reinterpret_cast<PUCHAR>(value.data()),static_cast<ULONG>(value.size()*sizeof(wchar_t)),digest,sizeof(digest));
    BCryptCloseAlgorithmProvider(algorithm,0);if(status<0)return {};
    constexpr wchar_t hex[]=L"0123456789abcdef";std::wstring result;result.reserve(64);
    for(BYTE b:digest){result.push_back(hex[b>>4]);result.push_back(hex[b&15]);}return result;
}
inline bool BandKeyProfileAllowsPolicy(DWORD mode,DWORD signer,DWORD account,DWORD protocol){
#ifdef BANDKEY_HOST_PILOT
    return mode==2 && signer==1 && account==1 && protocol==3;
#else
    UNREFERENCED_PARAMETER(mode);UNREFERENCED_PARAMETER(signer);UNREFERENCED_PARAMETER(account);UNREFERENCED_PARAMETER(protocol);return true;
#endif
}
#ifdef BANDKEY_HOST_PILOT
inline bool BandKeyHostRemovalBindingMatches(const std::wstring& manufacturer,const std::wstring& fingerprint){
    return !manufacturer.empty() && manufacturer!=L"VMware, Inc." && fingerprint==BandKeyHostExpectedMachine;
}
inline bool BandKeyHostBindingMatches(const std::wstring& manufacturer,const std::wstring& build,const std::wstring& fingerprint){
    return BandKeyHostRemovalBindingMatches(manufacturer,fingerprint) && build==BandKeyHostExpectedBuild;
}
#endif
