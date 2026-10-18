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
#include "LabPolicy.h"

using Microsoft::WRL::ComPtr;
static LONG objectCount = 0;
enum Field : DWORD { Logo, Label, Title, Explanation, Password, Submit, FieldCount };
static const CREDENTIAL_PROVIDER_FIELD_TYPE fieldTypes[] = { CPFT_TILE_IMAGE, CPFT_SMALL_TEXT, CPFT_LARGE_TEXT, CPFT_SMALL_TEXT, CPFT_PASSWORD_TEXT, CPFT_SUBMIT_BUTTON };
static const wchar_t* const fieldLabels[] = { L"BandKey", L"BandKey VM credential test", L"BandKey VM credential test", L"Manual local-account password test. Band confirmation is not connected.", L"VM account password", L"Sign in" };

template<size_t N> struct SecretBuffer {
    std::array<wchar_t, N> data{};
    ~SecretBuffer() { Clear(); }
    void Clear() { SecureZeroMemory(data.data(), sizeof(data)); }
    SecretBuffer() = default;
    SecretBuffer(const SecretBuffer&) = delete;
    SecretBuffer& operator=(const SecretBuffer&) = delete;
};
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
    SecretBuffer<257> password;
    ComPtr<ICredentialProviderCredentialEvents> events;
    void ClearPassword() {
        password.Clear();
        if (events) events->SetFieldString(this, Password, L"");
    }
public:
    BandKeyCredential(CREDENTIAL_PROVIDER_USAGE_SCENARIO usage, std::wstring userSid, std::wstring qualifiedName)
        : scenario(usage), sid(std::move(userSid)), name(std::move(qualifiedName)) { InterlockedIncrement(&objectCount); }
    ~BandKeyCredential() { InterlockedDecrement(&objectCount); }
    HRESULT STDMETHODCALLTYPE QueryInterface(REFIID iid, void** value) override {
        if (!value) return E_POINTER; *value = nullptr;
        if (iid != IID_IUnknown && iid != __uuidof(ICredentialProviderCredential) && iid != __uuidof(ICredentialProviderCredential2)) return E_NOINTERFACE;
        *value = static_cast<ICredentialProviderCredential2*>(this); AddRef(); return S_OK;
    }
    ULONG STDMETHODCALLTYPE AddRef() override { return InterlockedIncrement(&refs); }
    ULONG STDMETHODCALLTYPE Release() override { auto n = InterlockedDecrement(&refs); if (!n) delete this; return n; }
    HRESULT STDMETHODCALLTYPE Advise(ICredentialProviderCredentialEvents* value) override { if (!value) return E_POINTER; events = value; return S_OK; }
    HRESULT STDMETHODCALLTYPE UnAdvise() override { password.Clear(); events.Reset(); return S_OK; }
    HRESULT STDMETHODCALLTYPE SetSelected(BOOL* automatic) override { if (!automatic) return E_POINTER; *automatic = FALSE; return S_OK; }
    HRESULT STDMETHODCALLTYPE SetDeselected() override { ClearPassword(); return S_OK; }
    HRESULT STDMETHODCALLTYPE GetFieldState(DWORD id, CREDENTIAL_PROVIDER_FIELD_STATE* state, CREDENTIAL_PROVIDER_FIELD_INTERACTIVE_STATE* interactive) override {
        if (!state || !interactive) return E_POINTER;
        if (id >= FieldCount) return E_INVALIDARG;
        *state = id == Label ? CPFS_HIDDEN : ((id == Logo || id == Title) ? CPFS_DISPLAY_IN_BOTH : CPFS_DISPLAY_IN_SELECTED_TILE);
        *interactive = id == Password ? CPFIS_FOCUSED : CPFIS_NONE; return S_OK;
    }
    HRESULT STDMETHODCALLTYPE GetStringValue(DWORD id, PWSTR* value) override {
        if (!value) return E_POINTER; *value = nullptr;
        if (id >= FieldCount || id == Logo) return E_INVALIDARG;
        // Never hand the stored password back to a caller through a read method.
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
    HRESULT STDMETHODCALLTYPE GetSubmitButtonValue(DWORD id, DWORD* adjacent) override { if (!adjacent) return E_POINTER; if (id != Submit) return E_INVALIDARG; *adjacent = Password; return S_OK; }
    HRESULT STDMETHODCALLTYPE SetStringValue(DWORD id, PCWSTR value) override {
        if (id != Password) return E_INVALIDARG;
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
        if (!password.data[0]) { *icon = CPSI_WARNING; return SHStrDupW(L"Enter the independent VM account password. Band authorization is not connected.", status); }
        HRESULT hr = E_OUTOFMEMORY;
        try { hr = PackLocalCredential(scenario, name, password.data.data(), result); }
        catch (const std::bad_alloc&) { hr = E_OUTOFMEMORY; }
        ClearPassword();
        if (SUCCEEDED(hr)) *response = CPGSR_RETURN_CREDENTIAL_FINISHED;
        return hr;
    }
    HRESULT STDMETHODCALLTYPE ReportResult(NTSTATUS status, NTSTATUS, PWSTR* text, CREDENTIAL_PROVIDER_STATUS_ICON* icon) override {
        if (!text || !icon) return E_POINTER;
        *text = nullptr; *icon = CPSI_NONE; ClearPassword();
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
    HRESULT Enumerate() {
        if (credential || !policy.enabled || !users || (scenario != CPUS_LOGON && scenario != CPUS_UNLOCK_WORKSTATION)) return S_OK;
        DWORD count = 0; HRESULT hr = users->GetCount(&count);
        if (FAILED(hr)) return hr;
        if (count > 128) return E_INVALIDARG;
        for (DWORD i = 0; i < count; i++) {
            ComPtr<ICredentialProviderUser> user;
            hr = users->GetAt(i, &user); if (FAILED(hr) || !user) continue;
            GUID provider = {}; if (FAILED(user->GetProviderID(&provider)) || provider != Identity_LocalUserProvider) continue;
            PWSTR sid = nullptr, name = nullptr;
            hr = user->GetSid(&sid);
            bool matches = SUCCEEDED(hr) && sid && policy.sid == sid;
            if (matches) hr = user->GetStringValue(PKEY_Identity_QualifiedUserName, &name);
            if (matches && SUCCEEDED(hr) && name && wcschr(name, L'\\')) {
                try { credential.Attach(new(std::nothrow) BandKeyCredential(scenario, sid, name)); }
                catch (const std::bad_alloc&) { hr = E_OUTOFMEMORY; }
                if (!credential) hr = E_OUTOFMEMORY;
            }
            CoTaskMemFree(sid); CoTaskMemFree(name);
            if (credential || FAILED(hr)) return hr;
        }
        return S_OK;
    }
public:
    explicit BandKeyProvider(LabPolicy configuration) : policy(std::move(configuration)) { InterlockedIncrement(&objectCount); }
    ~BandKeyProvider() { InterlockedDecrement(&objectCount); }
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
        credential.Reset(); scenario = CPUS_INVALID;
        if (value != CPUS_LOGON && value != CPUS_UNLOCK_WORKSTATION) return E_NOTIMPL;
        scenario = value; return S_OK;
    }
    HRESULT STDMETHODCALLTYPE SetSerialization(const CREDENTIAL_PROVIDER_CREDENTIAL_SERIALIZATION*) override { return E_NOTIMPL; }
    HRESULT STDMETHODCALLTYPE Advise(ICredentialProviderEvents*, UINT_PTR) override { return S_OK; }
    HRESULT STDMETHODCALLTYPE UnAdvise() override { credential.Reset(); return S_OK; }
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
        HRESULT hr = Enumerate(); if (SUCCEEDED(hr) && credential) *count = 1; return hr;
    }
    HRESULT STDMETHODCALLTYPE GetCredentialAt(DWORD index, ICredentialProviderCredential** value) override {
        if (!value) return E_POINTER; *value = nullptr;
        if (index != 0 || !credential) return E_INVALIDARG;
        return credential.CopyTo(value);
    }
    HRESULT STDMETHODCALLTYPE SetUserArray(ICredentialProviderUserArray* value) override { credential.Reset(); users = value; return value ? S_OK : E_POINTER; }
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
