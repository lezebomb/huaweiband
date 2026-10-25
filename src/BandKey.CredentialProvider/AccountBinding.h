#pragma once
#include <windows.h>
#include <wincred.h>
#include <string>

enum class VmAccountKind : DWORD { Local = 0, MicrosoftAccount = 1 };

// Names select a credential format, never an identity. Enrollment also pins the SID.
inline bool ValidQualifiedAccount(const std::wstring& name, VmAccountKind kind) {
    if (name.empty() || name.size() > CREDUI_MAX_USERNAME_LENGTH) return false;
    for (auto ch : name) if (ch < 0x20 || ch == 0x7f) return false;
    auto slash = name.find(L'\\');
    if (slash == std::wstring::npos || !slash || slash + 1 == name.size() || name.find(L'\\', slash + 1) != std::wstring::npos) return false;
    auto domain = name.substr(0, slash);
    if (kind == VmAccountKind::MicrosoftAccount) return _wcsicmp(domain.c_str(), L"MicrosoftAccount") == 0;
    return kind == VmAccountKind::Local && _wcsicmp(domain.c_str(), L"MicrosoftAccount") != 0 && _wcsicmp(domain.c_str(), L"AzureAD") != 0;
}
inline bool MatchesBoundAccount(VmAccountKind kind, const std::wstring& enrolled, const std::wstring& actual, bool localIdentityProvider) {
    if (!ValidQualifiedAccount(actual, kind) || localIdentityProvider != (kind == VmAccountKind::Local)) return false;
    // Older local-only lab enrollments have no qualified-name value. Never infer MSA from it.
    if (enrolled.empty()) return kind == VmAccountKind::Local;
    return ValidQualifiedAccount(enrolled, kind) && _wcsicmp(enrolled.c_str(), actual.c_str()) == 0;
}
