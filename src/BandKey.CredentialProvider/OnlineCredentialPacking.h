#pragma once
#include <windows.h>
#include <credentialprovider.h>
#include <wincred.h>
#include <string>

// Shared protected-identity serializer. Packing is not Windows authentication.
// The VM provider accepts MSA only after explicit SID/name/type-bound enrollment.
inline HRESULT PackMicrosoftAccountCredential(CREDENTIAL_PROVIDER_USAGE_SCENARIO scenario, const std::wstring& qualifiedName,
    wchar_t* password, ULONG authenticationPackage, REFGUID provider, CREDENTIAL_PROVIDER_CREDENTIAL_SERIALIZATION* result) {
    if (!result) return E_POINTER;
    if (result->rgbSerialization || result->cbSerialization) return E_INVALIDARG;
    *result = {};
    if ((scenario != CPUS_LOGON && scenario != CPUS_UNLOCK_WORKSTATION) || provider == GUID_NULL || !password) return E_INVALIDARG;
    constexpr wchar_t prefix[] = L"MicrosoftAccount\\";
    constexpr size_t prefixLength = ARRAYSIZE(prefix) - 1;
    if (qualifiedName.size() <= prefixLength || qualifiedName.size() > CREDUI_MAX_USERNAME_LENGTH ||
        _wcsnicmp(qualifiedName.c_str(), prefix, prefixLength) != 0 || qualifiedName.find(L'\\', prefixLength) != std::wstring::npos) return E_INVALIDARG;
    for (auto value : qualifiedName) if (value < 0x20 || value == 0x7f) return E_INVALIDARG;
    size_t passwordChars = wcsnlen_s(password, CREDUI_MAX_PASSWORD_LENGTH + 1);
    if (!passwordChars || passwordChars > CREDUI_MAX_PASSWORD_LENGTH) return E_INVALIDARG;
    constexpr DWORD flags = CRED_PACK_PROTECTED_CREDENTIALS | CRED_PACK_ID_PROVIDER_CREDENTIALS;
    DWORD bytes = 0;
    if (CredPackAuthenticationBufferW(flags, const_cast<PWSTR>(qualifiedName.c_str()), password, nullptr, &bytes) ||
        GetLastError() != ERROR_INSUFFICIENT_BUFFER || !bytes || bytes > 65536) return E_FAIL;
    auto buffer = static_cast<BYTE*>(CoTaskMemAlloc(bytes));
    if (!buffer) return E_OUTOFMEMORY;
    DWORD capacity = bytes; ZeroMemory(buffer, capacity);
    if (!CredPackAuthenticationBufferW(flags, const_cast<PWSTR>(qualifiedName.c_str()), password, buffer, &bytes) || !bytes || bytes > capacity) {
        DWORD error = GetLastError(); SecureZeroMemory(buffer, capacity); CoTaskMemFree(buffer);
        return error ? HRESULT_FROM_WIN32(error) : E_FAIL;
    }
    result->rgbSerialization = buffer; result->cbSerialization = bytes;
    result->ulAuthenticationPackage = authenticationPackage; result->clsidCredentialProvider = provider;
    return S_OK; // The caller must zero and free the returned CoTaskMem allocation.
}
