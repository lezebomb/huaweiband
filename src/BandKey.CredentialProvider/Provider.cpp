#include <windows.h>
#include <initguid.h>
#include <credentialprovider.h>
#include <identitycommon.h>
#include <propkey.h>
#include <ntsecapi.h>
#define SECURITY_WIN32
#include <security.h>
#include <wincred.h>
#include <shlwapi.h>
#include <shlguid.h>
#include <wrl/client.h>
#include <array>
#include <vector>
#include <new>
#include <memory>
#include <optional>
#include "GrantRuntime.h"
#include "LabDiagnostics.h"
#include "OnlineCredentialPacking.h"

using Microsoft::WRL::ComPtr;
static LONG objectCount = 0;
enum Field : DWORD { Logo, Label, Title, Explanation, Password, Submit, FieldCount };
static const CREDENTIAL_PROVIDER_FIELD_TYPE fieldTypes[] = { CPFT_TILE_IMAGE, CPFT_SMALL_TEXT, CPFT_LARGE_TEXT, CPFT_SMALL_TEXT, CPFT_PASSWORD_TEXT, CPFT_SUBMIT_BUTTON };
static const wchar_t* const fieldLabels[] = { L"BandKey", L"BandKey VM credential test", L"BandKey VM credential test", L"Manual local-account password test. Band confirmation is not connected.", L"VM account password", L"Sign in" };
static void GrantDiagnostic(LabStage stage, DWORD code = 0) noexcept {
    try { if (GrantIsSystem()) AppendLabDiagnostic(GrantDirectory() / L"Diagnostics" / L"provider.log", stage, code); } catch (...) {}
}

static HRESULT NegotiatePackage(ULONG* package) {
    HANDLE lsa = nullptr;
    NTSTATUS status = LsaConnectUntrusted(&lsa);
    if (status < 0) return HRESULT_FROM_WIN32(LsaNtStatusToWinError(status));
    char name[] = "Negotiate";
    LSA_STRING text = { sizeof(name) - 1, sizeof(name), name };
    status = LsaLookupAuthenticationPackage(lsa, &text, package);
    LsaDeregisterLogonProcess(lsa);
    return status < 0 ? HRESULT_FROM_WIN32(LsaNtStatusToWinError(status)) : S_OK;
}

// Pack the documented KERB structure, including the unlock message type. This
// does not call LsaLogonUser; LogonUI remains responsible for submitting credentials.
static HRESULT PackLocalCredential(CREDENTIAL_PROVIDER_USAGE_SCENARIO scenario, const std::wstring& qualifiedName,
    wchar_t* password, CREDENTIAL_PROVIDER_CREDENTIAL_SERIALIZATION* result) {
    auto slash = qualifiedName.find(L'\\');
    if (slash == std::wstring::npos || slash == 0 || slash + 1 == qualifiedName.size()) return E_INVALIDARG;
    auto domain = qualifiedName.substr(0, slash), user = qualifiedName.substr(slash + 1);
    if (domain.size() > 256 || user.size() > 256) return E_INVALIDARG;
    SecretBuffer<2048> protectedPassword;
    DWORD chars = 0;
    if (CredProtectW(FALSE, password, static_cast<DWORD>(wcslen(password) + 1), nullptr, &chars, nullptr) ||
        GetLastError() != ERROR_INSUFFICIENT_BUFFER || !chars || chars > protectedPassword.data.size()) return E_FAIL;
    if (!CredProtectW(FALSE, password, static_cast<DWORD>(wcslen(password) + 1), protectedPassword.data.data(), &chars, nullptr)) return HRESULT_FROM_WIN32(GetLastError());
    ULONG package = 0; HRESULT hr = NegotiatePackage(&package);
    if (FAILED(hr)) return hr;
    const wchar_t* strings[] = { domain.c_str(), user.c_str(), protectedPassword.data.data() };
    USHORT lengths[3] = {}; DWORD bytes = sizeof(KERB_INTERACTIVE_UNLOCK_LOGON);
    for (size_t i = 0; i < 3; i++) { lengths[i] = static_cast<USHORT>(wcslen(strings[i]) * sizeof(wchar_t)); bytes += lengths[i]; }
    auto buffer = static_cast<BYTE*>(CoTaskMemAlloc(bytes));
    if (!buffer) return E_OUTOFMEMORY;
    ZeroMemory(buffer, bytes);
    auto packed = reinterpret_cast<KERB_INTERACTIVE_UNLOCK_LOGON*>(buffer);
    packed->Logon.MessageType = scenario == CPUS_UNLOCK_WORKSTATION ? KerbWorkstationUnlockLogon : KerbInteractiveLogon;
    UNICODE_STRING* fields[] = { &packed->Logon.LogonDomainName, &packed->Logon.UserName, &packed->Logon.Password };
    size_t offset = sizeof(*packed);
    for (size_t i = 0; i < 3; i++) {
        fields[i]->Length = fields[i]->MaximumLength = lengths[i];
        fields[i]->Buffer = reinterpret_cast<PWSTR>(offset);
        CopyMemory(buffer + offset, strings[i], lengths[i]); offset += lengths[i];
    }
    result->rgbSerialization = buffer; result->cbSerialization = bytes;
    result->clsidCredentialProvider = BandKeyLabClsid; result->ulAuthenticationPackage = package;
    return S_OK;
}

class BandKeyCredential final : public ICredentialProviderCredential2 {
    LONG refs = 1;
    CREDENTIAL_PROVIDER_USAGE_SCENARIO scenario;
    std::wstring sid, name;
    const LabPolicy policy;
    SecretBuffer<257> password;
    ComPtr<ICredentialProviderCredentialEvents> events;
    std::unique_ptr<OneTimeWindowsGrant> grant;
    std::optional<WindowsApproval> approval;
    std::filesystem::path requestPath;
    DWORD session = 0;
    bool attempted = false, requestWritten = false, announced = false;
    int observedApproval = -1;
    ULONGLONG deadline = 0;
    void ClearExchange() {
        if (requestWritten) { DeleteFileW(requestPath.c_str()); DeleteFileW((requestPath.wstring() + L".approval").c_str()); }
        requestWritten = false; approval.reset(); grant.reset();
    }
    bool CurrentPolicy() const {
        auto current = GrantPolicy();
        return current.enabled && current.mode == 2 && current.signerKind == policy.signerKind && current.sid == sid && current.publicKey == policy.publicKey &&
            current.accountKind == policy.accountKind && current.qualifiedAccount == policy.qualifiedAccount &&
            MatchesBoundAccount(policy.accountKind, policy.qualifiedAccount, name, policy.accountKind == VmAccountKind::Local);
    }
    void EnsureChallenge() {
        if (policy.mode != 2 || attempted || grant || !GrantIsSystem() || !CurrentPolicy()) return;
        session = GrantSession(); if (!session || !GrantSessionLocked(session)) return;
        auto root = GrantDirectory(); auto exchange = root / L"Exchange";
        CheckNoReparseDirectory(root); CheckNoReparseDirectory(exchange);
        auto challenge = NewWindowsChallenge(sid, session, GrantWallTime());
        deadline = challenge.expiresMs;
        auto pending = std::make_unique<OneTimeWindowsGrant>(challenge, GrantMonotonicTime());
        requestPath = exchange / ChallengeFileName(challenge);
        WriteNewFile(requestPath, reinterpret_cast<const BYTE*>(&challenge), sizeof(challenge)); requestWritten = true;
        grant = std::move(pending);
        GrantDiagnostic(LabStage::ChallengeWritten);
    }
    void ClearPassword() {
        password.Clear();
        if (events) events->SetFieldString(this, Password, L"");
    }
public:
    BandKeyCredential(CREDENTIAL_PROVIDER_USAGE_SCENARIO usage, std::wstring userSid, std::wstring qualifiedName, LabPolicy configuration = {})
        : scenario(usage), sid(std::move(userSid)), name(std::move(qualifiedName)), policy(std::move(configuration)) { InterlockedIncrement(&objectCount); }
    ~BandKeyCredential() { ClearExchange(); InterlockedDecrement(&objectCount); }
    void Cancel() { attempted = true; ClearPassword(); ClearExchange(); }
    bool Finished() const { return attempted; }
    bool Ready() {
        if (policy.mode != 2 || attempted) return false;
        try {
            EnsureChallenge();
            if (grant && GrantWallTime() >= deadline) { GrantDiagnostic(LabStage::ChallengeExpired); Cancel(); return false; }
            if (grant && (!CurrentPolicy() || !GrantSessionLocked(session))) { GrantDiagnostic(LabStage::ContextCancelled); Cancel(); return false; }
            if (!grant) return false;
            if (!approval) {
                auto response = ReadBoundedFile(requestPath.wstring() + L".approval", sizeof(WindowsApproval));
                if (response.size() != sizeof(WindowsApproval)) return false;
                WindowsApproval candidate; CopyMemory(&candidate, response.data(), sizeof(candidate));
                if (grant->Verify(candidate, policy.publicKey, GrantWallTime(), GrantMonotonicTime())) { approval = candidate; if (observedApproval != 1) GrantDiagnostic(LabStage::ApprovalVerified); observedApproval = 1; }
                else { if (observedApproval != 2) GrantDiagnostic(LabStage::ApprovalRejected); observedApproval = 2; }
            }
            return approval && grant->Verify(*approval, policy.publicKey, GrantWallTime(), GrantMonotonicTime());
        } catch (...) { if (observedApproval < 0) { GrantDiagnostic(LabStage::WaitingApproval); observedApproval = 0; } return false; }
    }
    bool NotifyReady() { if (Ready() && !announced) { announced = true; return true; } return false; }
    void NewSubscription() { announced = false; }
    HRESULT STDMETHODCALLTYPE QueryInterface(REFIID iid, void** value) override {
        if (!value) return E_POINTER; *value = nullptr;
        if (iid != IID_IUnknown && iid != __uuidof(ICredentialProviderCredential) && iid != __uuidof(ICredentialProviderCredential2)) return E_NOINTERFACE;
        *value = static_cast<ICredentialProviderCredential2*>(this); AddRef(); return S_OK;
    }
    ULONG STDMETHODCALLTYPE AddRef() override { return InterlockedIncrement(&refs); }
    ULONG STDMETHODCALLTYPE Release() override { auto n = InterlockedDecrement(&refs); if (!n) delete this; return n; }
    HRESULT STDMETHODCALLTYPE Advise(ICredentialProviderCredentialEvents* value) override { if (!value) return E_POINTER; events = value; return S_OK; }
    HRESULT STDMETHODCALLTYPE UnAdvise() override { password.Clear(); events.Reset(); if (policy.mode == 2) GrantDiagnostic(LabStage::CredentialUnAdvise); return S_OK; }
    HRESULT STDMETHODCALLTYPE SetSelected(BOOL* automatic) override { if (!automatic) return E_POINTER; *automatic = Ready() ? TRUE : FALSE; if (policy.mode == 2) GrantDiagnostic(*automatic ? LabStage::SelectedReady : LabStage::SelectedWaiting); return S_OK; }
    HRESULT STDMETHODCALLTYPE SetDeselected() override { ClearPassword(); if (policy.mode == 2) GrantDiagnostic(LabStage::Deselected); return S_OK; }
    HRESULT STDMETHODCALLTYPE GetFieldState(DWORD id, CREDENTIAL_PROVIDER_FIELD_STATE* state, CREDENTIAL_PROVIDER_FIELD_INTERACTIVE_STATE* interactive) override {
        if (!state || !interactive) return E_POINTER;
        if (id >= FieldCount) return E_INVALIDARG;
        *state = id == Label ? CPFS_HIDDEN : ((id == Logo || id == Title) ? CPFS_DISPLAY_IN_BOTH : CPFS_DISPLAY_IN_SELECTED_TILE);
        if (policy.mode == 2 && id == Password) *state = CPFS_HIDDEN;
        *interactive = id == Password && policy.mode != 2 ? CPFIS_FOCUSED : CPFIS_NONE; return S_OK;
    }
    HRESULT STDMETHODCALLTYPE GetStringValue(DWORD id, PWSTR* value) override {
        if (!value) return E_POINTER; *value = nullptr;
        if (id >= FieldCount || id == Logo) return E_INVALIDARG;
        // Never hand the stored password back to a caller through a read method.
        if (policy.mode == 2 && id == Explanation) return SHStrDupW(L"VM signed-confirmation lifecycle fix 2. Waiting for one-time authorization; no password input here.", value);
        return SHStrDupW(id == Password ? L"" : fieldLabels[id], value);
    }
    HRESULT STDMETHODCALLTYPE GetBitmapValue(DWORD id, HBITMAP* value) override {
        if (!value) return E_POINTER; *value = nullptr;
        if (id != Logo) return E_INVALIDARG;
        BITMAPINFO info = {}; info.bmiHeader.biSize = sizeof(BITMAPINFOHEADER);
        info.bmiHeader.biWidth = 64; info.bmiHeader.biHeight = -64; info.bmiHeader.biPlanes = 1; info.bmiHeader.biBitCount = 32;
        void* pixels = nullptr; auto bitmap = CreateDIBSection(nullptr, &info, DIB_RGB_COLORS, &pixels, nullptr, 0);
        if (!bitmap || !pixels) { if (bitmap) DeleteObject(bitmap); return E_OUTOFMEMORY; }
        // A simple opaque blue tile with a white key; no external bitmap/resource dependency.
        auto colors = static_cast<DWORD*>(pixels);
        for (int y = 0; y < 64; y++) for (int x = 0; x < 64; x++) {
            int radius = (x - 23) * (x - 23) + (y - 25) * (y - 25);
            bool key = (radius >= 49 && radius <= 169) || (x >= 34 && x <= 54 && y >= 22 && y <= 28) || (x >= 48 && x <= 54 && y >= 28 && y <= 38);
            colors[y * 64 + x] = key ? 0xffffffff : 0xff2468bc;
        }
        *value = bitmap; return S_OK;
    }
    HRESULT STDMETHODCALLTYPE GetCheckboxValue(DWORD, BOOL* checked, PWSTR* label) override { if (checked) *checked = FALSE; if (label) *label = nullptr; return E_NOTIMPL; }
    HRESULT STDMETHODCALLTYPE GetComboBoxValueCount(DWORD, DWORD* count, DWORD* selected) override { if (count) *count = 0; if (selected) *selected = 0; return E_NOTIMPL; }
    HRESULT STDMETHODCALLTYPE GetComboBoxValueAt(DWORD, DWORD, PWSTR* value) override { if (value) *value = nullptr; return E_NOTIMPL; }
    HRESULT STDMETHODCALLTYPE GetSubmitButtonValue(DWORD id, DWORD* adjacent) override { if (!adjacent) return E_POINTER; if (id != Submit) return E_INVALIDARG; *adjacent = policy.mode == 2 ? Explanation : Password; return S_OK; }
    HRESULT STDMETHODCALLTYPE SetStringValue(DWORD id, PCWSTR value) override {
        if (id != Password) return E_INVALIDARG;
        if (policy.mode == 2) return E_ACCESSDENIED;
        password.Clear();
        if (!value) return E_POINTER;
        auto length = wcsnlen_s(value, password.data.size());
        if (length >= password.data.size()) return E_INVALIDARG;
        CopyMemory(password.data.data(), value, length * sizeof(wchar_t)); return S_OK;
    }
    HRESULT STDMETHODCALLTYPE SetCheckboxValue(DWORD, BOOL) override { return E_NOTIMPL; }
    HRESULT STDMETHODCALLTYPE SetComboBoxSelectedValue(DWORD, DWORD) override { return E_NOTIMPL; }
    HRESULT STDMETHODCALLTYPE CommandLinkClicked(DWORD) override { return E_NOTIMPL; }
    HRESULT STDMETHODCALLTYPE GetSerialization(CREDENTIAL_PROVIDER_GET_SERIALIZATION_RESPONSE* response, CREDENTIAL_PROVIDER_CREDENTIAL_SERIALIZATION* result, PWSTR* status, CREDENTIAL_PROVIDER_STATUS_ICON* icon) override {
        if (!response || !result || !status || !icon) return E_POINTER;
        *response = CPGSR_NO_CREDENTIAL_NOT_FINISHED; *result = {}; *status = nullptr; *icon = CPSI_NONE;
        if (policy.mode == 2) {
            GrantDiagnostic(LabStage::SerializationRequested);
            if (!Ready()) return SHStrDupW(L"No current signed authorization. Use a system sign-in option if needed.", status);
            attempted = true;
            auto approvalDeadline = approval->challenge.expiresMs;
            bool valid = grant->Consume(*approval, policy.publicKey, GrantWallTime(), GrantMonotonicTime());
            ClearExchange();
            if (!valid || !CurrentPolicy() || !GrantSessionLocked(session)) return E_ACCESSDENIED;
            GrantDiagnostic(LabStage::GrantConsumed); GrantDiagnostic(LabStage::VaultReadStarted);
            try { GrantReadPassword(sid, password); GrantDiagnostic(LabStage::VaultReadSucceeded); }
            catch (const std::exception& error) {
                auto stage = strcmp(error.what(), "vault-file-permissions-invalid") == 0 ? LabStage::VaultPermissionRejected : (strcmp(error.what(), "dpapi-unprotect-failed") == 0 ? LabStage::VaultDpapiFailed : LabStage::VaultReadFailed);
                GrantDiagnostic(stage); password.Clear(); return E_ACCESSDENIED;
            }
            catch (...) { GrantDiagnostic(LabStage::VaultReadFailed); password.Clear(); return E_ACCESSDENIED; }
            if (GrantWallTime() >= approvalDeadline || !CurrentPolicy() || !GrantSessionLocked(session)) { password.Clear(); return E_ACCESSDENIED; }
        }
        if (!password.data[0]) { *icon = CPSI_WARNING; return SHStrDupW(L"Enter the independent VM account password. Band authorization is not connected.", status); }
        HRESULT hr = E_OUTOFMEMORY;
        try {
            if (policy.accountKind == VmAccountKind::MicrosoftAccount) {
                ULONG package = 0;
                hr = policy.mode == 2 ? NegotiatePackage(&package) : E_ACCESSDENIED;
                if (SUCCEEDED(hr)) hr = PackMicrosoftAccountCredential(scenario, name, password.data.data(), package, BandKeyLabClsid, result);
            } else hr = PackLocalCredential(scenario, name, password.data.data(), result);
        }
        catch (const std::bad_alloc&) { hr = E_OUTOFMEMORY; }
        ClearPassword();
        if (SUCCEEDED(hr)) *response = CPGSR_RETURN_CREDENTIAL_FINISHED;
        if (policy.mode == 2) GrantDiagnostic(SUCCEEDED(hr) ? LabStage::SerializationPrepared : LabStage::SerializationFailed, static_cast<DWORD>(hr));
        return hr;
    }
    HRESULT STDMETHODCALLTYPE ReportResult(NTSTATUS status, NTSTATUS, PWSTR* text, CREDENTIAL_PROVIDER_STATUS_ICON* icon) override {
        if (!text || !icon) return E_POINTER;
        if (policy.mode == 2) GrantDiagnostic(LabStage::WindowsResult, static_cast<DWORD>(status));
        *text = nullptr; *icon = CPSI_NONE; ClearPassword(); if (policy.mode == 2) { attempted = true; ClearExchange(); }
        if (status < 0) { *icon = CPSI_ERROR; return SHStrDupW(L"Windows rejected this credential. Use a system sign-in option to recover.", text); }
        return S_OK;
    }
    HRESULT STDMETHODCALLTYPE GetUserSid(PWSTR* value) override { if (!value) return E_POINTER; return SHStrDupW(sid.c_str(), value); }
};

class BandKeyProvider final : public ICredentialProvider, public ICredentialProviderSetUserArray {
    LONG refs = 1;
    const LabPolicy policy;
    CREDENTIAL_PROVIDER_USAGE_SCENARIO scenario = CPUS_INVALID;
    ComPtr<ICredentialProviderUserArray> users;
    ComPtr<ICredentialProviderCredential> credential;
    ComPtr<ICredentialProviderEvents> providerEvents;
    UINT_PTR adviseContext = 0;
    HWND notificationWindow = nullptr;
    HINSTANCE windowModule = nullptr;
    bool timerObserved = false;
    static constexpr wchar_t WindowClass[] = L"BandKey.VM.Grant.Notification.V1";
    static LRESULT CALLBACK WindowProcedure(HWND window, UINT message, WPARAM wParam, LPARAM lParam) {
        if (message == WM_NCCREATE) {
            auto create = reinterpret_cast<CREATESTRUCTW*>(lParam);
            SetWindowLongPtrW(window, GWLP_USERDATA, reinterpret_cast<LONG_PTR>(create->lpCreateParams));
        }
        auto self = reinterpret_cast<BandKeyProvider*>(GetWindowLongPtrW(window, GWLP_USERDATA));
        if (message == WM_WTSSESSION_CHANGE && self && (wParam == WTS_SESSION_UNLOCK || wParam == WTS_SESSION_LOGOFF || wParam == WTS_SESSION_LOCK)) {
            self->AddRef();
            try {
                GrantDiagnostic(LabStage::SessionBoundary, static_cast<DWORD>(wParam));
                if (self->credential) static_cast<BandKeyCredential*>(self->credential.Get())->Cancel();
                self->credential.Reset();
                if (wParam == WTS_SESSION_LOCK && self->providerEvents) { auto callback = self->providerEvents; callback->CredentialsChanged(self->adviseContext); }
            } catch (...) { /* Never propagate a notification exception into LogonUI. */ }
            self->Release(); return 0;
        }
        if (message == WM_TIMER && self) {
            self->AddRef();
            try {
                if (!self->timerObserved) { self->timerObserved = true; GrantDiagnostic(LabStage::TimerActive); }
                auto value = static_cast<BandKeyCredential*>(self->credential.Get());
                if (value && value->NotifyReady() && self->providerEvents) {
                    auto callback = self->providerEvents;
                    GrantDiagnostic(LabStage::RefreshRequested);
                    auto result = callback->CredentialsChanged(self->adviseContext);
                    if (FAILED(result)) GrantDiagnostic(LabStage::RefreshFailed, static_cast<DWORD>(result));
                }
            } catch (...) { /* Fail closed; no UI-thread exception may cross Winlogon. */ }
            self->Release(); return 0;
        }
        if (message == WM_NCDESTROY) SetWindowLongPtrW(window, GWLP_USERDATA, 0);
        return DefWindowProcW(window, message, wParam, lParam);
    }
    void StopNotifications() {
        if (notificationWindow) { WTSUnRegisterSessionNotification(notificationWindow); KillTimer(notificationWindow, 1); DestroyWindow(notificationWindow); notificationWindow = nullptr; }
        if (windowModule) UnregisterClassW(WindowClass, windowModule);
        providerEvents.Reset();
    }
    HRESULT Enumerate() {
        if (credential || !policy.enabled || !users || (scenario != CPUS_LOGON && scenario != CPUS_UNLOCK_WORKSTATION)) return S_OK;
        if (policy.mode == 2 && !GrantIsSystem()) return S_OK;
        DWORD count = 0; HRESULT hr = users->GetCount(&count);
        if (FAILED(hr)) return hr;
        if (count > 128) return E_INVALIDARG;
        for (DWORD i = 0; i < count; i++) {
            ComPtr<ICredentialProviderUser> user;
            hr = users->GetAt(i, &user); if (FAILED(hr) || !user) continue;
            GUID provider = {}; if (FAILED(user->GetProviderID(&provider)) || provider == GUID_NULL) continue;
            PWSTR sid = nullptr, name = nullptr;
            hr = user->GetSid(&sid);
            bool matches = SUCCEEDED(hr) && sid && policy.sid == sid;
            if (matches) hr = user->GetStringValue(PKEY_Identity_QualifiedUserName, &name);
            if (matches && SUCCEEDED(hr) && name && MatchesBoundAccount(policy.accountKind, policy.qualifiedAccount, name, provider == Identity_LocalUserProvider)) {
                try { credential.Attach(new(std::nothrow) BandKeyCredential(scenario, sid, name, policy)); }
                catch (const std::bad_alloc&) { hr = E_OUTOFMEMORY; }
                if (!credential) hr = E_OUTOFMEMORY;
                else if (policy.mode == 2) GrantDiagnostic(LabStage::UserBound);
            }
            CoTaskMemFree(sid); CoTaskMemFree(name);
            if (credential || FAILED(hr)) return hr;
        }
        return S_OK;
    }
public:
    explicit BandKeyProvider(LabPolicy configuration) : policy(std::move(configuration)) { InterlockedIncrement(&objectCount); if (policy.mode == 2) GrantDiagnostic(LabStage::ProviderLoaded); }
    ~BandKeyProvider() { StopNotifications(); InterlockedDecrement(&objectCount); }
    HRESULT STDMETHODCALLTYPE QueryInterface(REFIID iid, void** value) override {
        if (!value) return E_POINTER; *value = nullptr;
        if (iid == IID_IUnknown || iid == __uuidof(ICredentialProvider)) *value = static_cast<ICredentialProvider*>(this);
        else if (iid == __uuidof(ICredentialProviderSetUserArray)) *value = static_cast<ICredentialProviderSetUserArray*>(this);
        else return E_NOINTERFACE;
        AddRef(); return S_OK;
    }
    ULONG STDMETHODCALLTYPE AddRef() override { return InterlockedIncrement(&refs); }
    ULONG STDMETHODCALLTYPE Release() override { auto n = InterlockedDecrement(&refs); if (!n) delete this; return n; }
    HRESULT STDMETHODCALLTYPE SetUsageScenario(CREDENTIAL_PROVIDER_USAGE_SCENARIO value, DWORD) override {
        if (policy.mode == 2 && value == scenario && credential && !static_cast<BandKeyCredential*>(credential.Get())->Finished()) return S_OK;
        credential.Reset(); scenario = CPUS_INVALID;
        if (value != CPUS_LOGON && value != CPUS_UNLOCK_WORKSTATION) return E_NOTIMPL;
        scenario = value; return S_OK;
    }
    HRESULT STDMETHODCALLTYPE SetSerialization(const CREDENTIAL_PROVIDER_CREDENTIAL_SERIALIZATION*) override { return E_NOTIMPL; }
    HRESULT STDMETHODCALLTYPE Advise(ICredentialProviderEvents* events, UINT_PTR context) override {
        StopNotifications();
        if (!events) return E_POINTER;
        providerEvents = events; adviseContext = context;
        if (policy.mode != 2) return S_OK;
        if (credential) static_cast<BandKeyCredential*>(credential.Get())->NewSubscription();
        timerObserved = false; GrantDiagnostic(LabStage::Advise);
        APTTYPE apartment; APTTYPEQUALIFIER qualifier;
        if (SUCCEEDED(CoGetApartmentType(&apartment, &qualifier))) GrantDiagnostic(LabStage::Apartment, static_cast<DWORD>(apartment));
        if (!GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT, reinterpret_cast<LPCWSTR>(&WindowProcedure), &windowModule)) return E_FAIL;
        WNDCLASSW type{}; type.lpfnWndProc = WindowProcedure; type.hInstance = windowModule; type.lpszClassName = WindowClass;
        if (!RegisterClassW(&type) && GetLastError() != ERROR_CLASS_ALREADY_EXISTS) return E_FAIL;
        notificationWindow = CreateWindowExW(0, WindowClass, L"", 0, 0, 0, 0, 0, HWND_MESSAGE, nullptr, windowModule, this);
        if (!notificationWindow || !WTSRegisterSessionNotification(notificationWindow, NOTIFY_FOR_THIS_SESSION) || !SetTimer(notificationWindow, 1, 250, nullptr)) { GrantDiagnostic(LabStage::NotificationsFailed, GetLastError()); StopNotifications(); return E_FAIL; }
        GrantDiagnostic(LabStage::NotificationsArmed); return S_OK;
    }
    HRESULT STDMETHODCALLTYPE UnAdvise() override { StopNotifications(); if (policy.mode == 2) GrantDiagnostic(LabStage::ProviderUnAdvise); else credential.Reset(); return S_OK; }
    HRESULT STDMETHODCALLTYPE GetFieldDescriptorCount(DWORD* count) override { if (!count) return E_POINTER; *count = FieldCount; return S_OK; }
    HRESULT STDMETHODCALLTYPE GetFieldDescriptorAt(DWORD id, CREDENTIAL_PROVIDER_FIELD_DESCRIPTOR** value) override {
        if (!value) return E_POINTER; *value = nullptr;
        if (id >= FieldCount) return E_INVALIDARG;
        auto descriptor = static_cast<CREDENTIAL_PROVIDER_FIELD_DESCRIPTOR*>(CoTaskMemAlloc(sizeof(**value)));
        if (!descriptor) return E_OUTOFMEMORY;
        *descriptor = {}; descriptor->dwFieldID = id; descriptor->cpft = fieldTypes[id];
        if (id == Logo) descriptor->guidFieldType = CPFG_CREDENTIAL_PROVIDER_LOGO;
        if (id == Label) descriptor->guidFieldType = CPFG_CREDENTIAL_PROVIDER_LABEL;
        HRESULT hr = SHStrDupW(fieldLabels[id], &descriptor->pszLabel);
        if (FAILED(hr)) CoTaskMemFree(descriptor); else *value = descriptor;
        return hr;
    }
    HRESULT STDMETHODCALLTYPE GetCredentialCount(DWORD* count, DWORD* defaultIndex, BOOL* automatic) override {
        if (!count || !defaultIndex || !automatic) return E_POINTER;
        *count = 0; *defaultIndex = CREDENTIAL_PROVIDER_NO_DEFAULT; *automatic = FALSE;
        HRESULT hr = Enumerate();
        if (SUCCEEDED(hr) && credential) { *count = 1; if (static_cast<BandKeyCredential*>(credential.Get())->Ready()) { *defaultIndex = 0; *automatic = TRUE; if (policy.mode == 2) GrantDiagnostic(LabStage::AutoCredential); } }
        return hr;
    }
    HRESULT STDMETHODCALLTYPE GetCredentialAt(DWORD index, ICredentialProviderCredential** value) override {
        if (!value) return E_POINTER; *value = nullptr;
        if (index != 0 || !credential) return E_INVALIDARG;
        return credential.CopyTo(value);
    }
    HRESULT STDMETHODCALLTYPE SetUserArray(ICredentialProviderUserArray* value) override {
        bool keep = false;
        if (value && policy.mode == 2 && credential && !static_cast<BandKeyCredential*>(credential.Get())->Finished()) {
            DWORD count = 0;
            if (SUCCEEDED(value->GetCount(&count)) && count <= 128) for (DWORD i = 0; i < count; i++) {
                ComPtr<ICredentialProviderUser> user; GUID identity{}; PWSTR id = nullptr, name = nullptr;
                if (SUCCEEDED(value->GetAt(i, &user)) && user && SUCCEEDED(user->GetProviderID(&identity)) && identity != GUID_NULL && SUCCEEDED(user->GetSid(&id)) && id && policy.sid == id &&
                    SUCCEEDED(user->GetStringValue(PKEY_Identity_QualifiedUserName, &name)) && name && MatchesBoundAccount(policy.accountKind, policy.qualifiedAccount, name, identity == Identity_LocalUserProvider)) keep = true;
                CoTaskMemFree(id); CoTaskMemFree(name);
            }
        }
        if (!keep) { if (policy.mode == 2 && credential) static_cast<BandKeyCredential*>(credential.Get())->Cancel(); credential.Reset(); } users = value; return value ? S_OK : E_POINTER;
    }
};

class BandKeyFactory final : public IClassFactory {
    LONG refs = 1;
public:
    BandKeyFactory() { InterlockedIncrement(&objectCount); }
    ~BandKeyFactory() { InterlockedDecrement(&objectCount); }
    HRESULT STDMETHODCALLTYPE QueryInterface(REFIID iid, void** value) override {
        if (!value) return E_POINTER; *value = nullptr;
        if (iid != IID_IUnknown && iid != IID_IClassFactory) return E_NOINTERFACE;
        *value = static_cast<IClassFactory*>(this); AddRef(); return S_OK;
    }
    ULONG STDMETHODCALLTYPE AddRef() override { return InterlockedIncrement(&refs); }
    ULONG STDMETHODCALLTYPE Release() override { auto n = InterlockedDecrement(&refs); if (!n) delete this; return n; }
    HRESULT STDMETHODCALLTYPE CreateInstance(IUnknown* outer, REFIID iid, void** value) override {
        if (!value) return E_POINTER; *value = nullptr; if (outer) return CLASS_E_NOAGGREGATION;
        try {
            ComPtr<ICredentialProvider> provider; provider.Attach(new(std::nothrow) BandKeyProvider(ReadLabPolicy()));
            return provider ? provider->QueryInterface(iid, value) : E_OUTOFMEMORY;
        } catch (const std::bad_alloc&) { return E_OUTOFMEMORY; }
    }
    HRESULT STDMETHODCALLTYPE LockServer(BOOL lock) override { if (lock) InterlockedIncrement(&objectCount); else InterlockedDecrement(&objectCount); return S_OK; }
};
_Check_return_ STDAPI DllGetClassObject(_In_ REFCLSID clsid, _In_ REFIID iid, _Outptr_ LPVOID* value) {
    if (!value) return E_POINTER; *value = nullptr;
    if (clsid != BandKeyLabClsid) return CLASS_E_CLASSNOTAVAILABLE;
    ComPtr<IClassFactory> factory; factory.Attach(new(std::nothrow) BandKeyFactory());
    return factory ? factory->QueryInterface(iid, value) : E_OUTOFMEMORY;
}
__control_entrypoint(DllExport) STDAPI DllCanUnloadNow(void) { return objectCount == 0 ? S_OK : S_FALSE; }
#ifndef BANDKEY_UNIT_TEST
BOOL WINAPI DllMain(HINSTANCE module, DWORD reason, LPVOID) { if (reason == DLL_PROCESS_ATTACH) DisableThreadLibraryCalls(module); return TRUE; }
#endif
