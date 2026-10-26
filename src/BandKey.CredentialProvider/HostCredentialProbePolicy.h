#pragma once
#include "AccountBinding.h"
struct HostProbeAuthentication { bool attempted = false, accepted = false, sameSid = false; DWORD error = 0; };
struct HostProbeResult {
    bool attempted = false, accepted = false, sameSid = false, packed = false;
    DWORD error = 0;
};
// No identity/secret is returned. Packing never means install or desktop-unlock success.
template<class Authenticate, class Pack>
HostProbeResult VerifyHostPassword(const std::wstring& expected, const std::wstring& entered, wchar_t* password,
    Authenticate authenticate, Pack pack) {
    HostProbeResult result;
    if (!ValidQualifiedAccount(expected, VmAccountKind::MicrosoftAccount) || _wcsicmp(expected.c_str(), entered.c_str()) != 0) {
        result.error = ERROR_INVALID_ACCOUNT_NAME; return result;
    }
    if (!password || !password[0] || wcsnlen_s(password, CREDUI_MAX_PASSWORD_LENGTH + 1) > CREDUI_MAX_PASSWORD_LENGTH) {
        result.error = ERROR_INVALID_PASSWORD; return result;
    }
    const auto auth = authenticate(password);
    result.attempted = auth.attempted; result.accepted = auth.attempted && auth.accepted; result.sameSid = result.accepted && auth.sameSid; result.error = auth.error;
    if (!result.accepted) { if (!result.error) result.error = result.attempted ? ERROR_LOGON_FAILURE : ERROR_INVALID_DATA; return result; }
    if (!result.sameSid) { result.error = ERROR_ACCESS_DENIED; return result; }
    const HRESULT status = pack(password);
    result.packed = SUCCEEDED(status); result.error = result.packed ? ERROR_SUCCESS : static_cast<DWORD>(status);
    return result;
}