#include <windows.h>
#include <initguid.h>
#include <credentialprovider.h>
#include <identitycommon.h>
#include <propkey.h>
#include <shlwapi.h>
#include <shlobj.h>
#include <knownfolders.h>
#include <shellapi.h>
#include <wrl/client.h>
#include <bcrypt.h>
#include <filesystem>
#include <fstream>
#include <string>
#include <vector>
#include <stdexcept>

using Microsoft::WRL::ComPtr;
static const GUID SampleProvider = {0x5fd3d285,0x0dd9,0x4362,{0x88,0x55,0xe0,0xab,0xaa,0xcd,0x4a,0xf6}};
static const wchar_t* FakeSid = L"S-1-5-21-100-200-300-1001";

static std::wstring ReadSystemVersion(const wchar_t* name) {
    wchar_t value[256] = {}; DWORD bytes = sizeof(value);
    return RegGetValueW(HKEY_LOCAL_MACHINE, L"SOFTWARE\\Microsoft\\Windows NT\\CurrentVersion", name,
        RRF_RT_REG_SZ, nullptr, value, &bytes) == ERROR_SUCCESS ? value : L"unknown";
}
static std::string JsonText(const std::wstring& value) {
    int size = WideCharToMultiByte(CP_UTF8, 0, value.data(), static_cast<int>(value.size()), nullptr, 0, nullptr, nullptr);
    std::string utf8(size, '\0'), escaped;
    WideCharToMultiByte(CP_UTF8, 0, value.data(), static_cast<int>(value.size()), utf8.data(), size, nullptr, nullptr);
    constexpr char hex[] = "0123456789abcdef";
    for (unsigned char c : utf8) {
        if (c == '"' || c == '\\') { escaped += '\\'; escaped += static_cast<char>(c); }
        else if (c < 32) { escaped += "\\u00"; escaped += hex[c >> 4]; escaped += hex[c & 15]; }
        else escaped += static_cast<char>(c);
    }
    return escaped;
}

class LabUser final : public ICredentialProviderUser {
    LONG refs = 1;
public:
    HRESULT STDMETHODCALLTYPE QueryInterface(REFIID iid, void** value) override {
        *value = nullptr;
        if (iid == IID_IUnknown || iid == __uuidof(ICredentialProviderUser)) { *value = static_cast<ICredentialProviderUser*>(this); AddRef(); return S_OK; }
        return E_NOINTERFACE;
    }
    ULONG STDMETHODCALLTYPE AddRef() override { return InterlockedIncrement(&refs); }
    ULONG STDMETHODCALLTYPE Release() override { auto n = InterlockedDecrement(&refs); if (!n) delete this; return n; }
    HRESULT STDMETHODCALLTYPE GetSid(PWSTR* value) override { return SHStrDupW(FakeSid, value); }
    HRESULT STDMETHODCALLTYPE GetProviderID(GUID* value) override { *value = Identity_LocalUserProvider; return S_OK; }
    HRESULT STDMETHODCALLTYPE GetStringValue(REFPROPERTYKEY key, PWSTR* value) override {
        const wchar_t* text = nullptr;
        if (IsEqualPropertyKey(key, PKEY_Identity_QualifiedUserName)) text = L"BANDKEYLAB\\TestUser";
        else if (IsEqualPropertyKey(key, PKEY_Identity_UserName)) text = L"TestUser";
        else if (IsEqualPropertyKey(key, PKEY_Identity_DisplayName)) text = L"Synthetic BandKey User";
        else if (IsEqualPropertyKey(key, PKEY_Identity_LogonStatusString)) text = L"Lab only";
        *value = nullptr; return text ? SHStrDupW(text, value) : E_NOTIMPL;
    }
    HRESULT STDMETHODCALLTYPE GetValue(REFPROPERTYKEY, PROPVARIANT* value) override { ZeroMemory(value, sizeof(*value)); return E_NOTIMPL; }
};
class LabUsers final : public ICredentialProviderUserArray {
    LONG refs = 1;
    ComPtr<ICredentialProviderUser> user;
public:
    LabUsers() { user.Attach(new LabUser()); }
    HRESULT STDMETHODCALLTYPE QueryInterface(REFIID iid, void** value) override {
        *value = nullptr;
        if (iid == IID_IUnknown || iid == __uuidof(ICredentialProviderUserArray)) { *value = static_cast<ICredentialProviderUserArray*>(this); AddRef(); return S_OK; }
        return E_NOINTERFACE;
    }
    ULONG STDMETHODCALLTYPE AddRef() override { return InterlockedIncrement(&refs); }
    ULONG STDMETHODCALLTYPE Release() override { auto n = InterlockedDecrement(&refs); if (!n) delete this; return n; }
    HRESULT STDMETHODCALLTYPE SetProviderFilter(REFGUID) override { return S_OK; }
    HRESULT STDMETHODCALLTYPE GetAccountOptions(CREDENTIAL_PROVIDER_ACCOUNT_OPTIONS* value) override { *value = CPAO_NONE; return S_OK; }
    HRESULT STDMETHODCALLTYPE GetCount(DWORD* value) override { *value = 1; return S_OK; }
    HRESULT STDMETHODCALLTYPE GetAt(DWORD index, ICredentialProviderUser** value) override { *value = nullptr; return index == 0 ? user.CopyTo(value) : E_INVALIDARG; }
};

static int RunLab(const wchar_t* dllPath, const wchar_t* reportPath, std::wstring* summary) {
    std::vector<std::pair<std::string, bool>> tests;
    auto check = [&](const char* name, bool success) { tests.emplace_back(name, success); if (!success) throw std::runtime_error(name); };
    HMODULE library = nullptr;
    HRESULT com = CoInitializeEx(nullptr, COINIT_APARTMENTTHREADED);
    try {
        check("COM initialization", SUCCEEDED(com));
        library = LoadLibraryExW(dllPath, nullptr, LOAD_LIBRARY_SEARCH_DLL_LOAD_DIR | LOAD_LIBRARY_SEARCH_SYSTEM32);
        check("reference DLL loaded without registration", library != nullptr);
        auto getClass = reinterpret_cast<HRESULT(WINAPI*)(REFCLSID, REFIID, void**)>(GetProcAddress(library, "DllGetClassObject"));
        check("class factory export", getClass != nullptr);
        ComPtr<IClassFactory> factory;
        check("class factory instantiated directly", SUCCEEDED(getClass(SampleProvider, IID_PPV_ARGS(&factory))));
        ComPtr<ICredentialProvider> provider;
        check("provider created in ordinary test process", SUCCEEDED(factory->CreateInstance(nullptr, IID_PPV_ARGS(&provider))));
        check("CredUI scenario declined", provider->SetUsageScenario(CPUS_CREDUI, 0) == E_NOTIMPL);
        DWORD fields = 0, passwordField = MAXDWORD;
        check("field descriptors available", SUCCEEDED(provider->GetFieldDescriptorCount(&fields)) && fields > 0);
        for (DWORD i = 0; i < fields; i++) {
            CREDENTIAL_PROVIDER_FIELD_DESCRIPTOR* descriptor = nullptr;
            check("field descriptor valid", SUCCEEDED(provider->GetFieldDescriptorAt(i, &descriptor)) && descriptor != nullptr);
            if (descriptor->cpft == CPFT_PASSWORD_TEXT) passwordField = descriptor->dwFieldID;
            CoTaskMemFree(descriptor->pszLabel); CoTaskMemFree(descriptor);
        }
        check("password field identified", passwordField != MAXDWORD);
        ComPtr<ICredentialProviderSetUserArray> userSetter;
        check("V2 user enumeration interface", SUCCEEDED(provider.As(&userSetter)));
        ComPtr<ICredentialProviderUserArray> users; users.Attach(new LabUsers());
        for (auto scenario : { CPUS_LOGON, CPUS_UNLOCK_WORKSTATION }) {
            check("logon or unlock scenario initialized", SUCCEEDED(provider->SetUsageScenario(scenario, 0)));
            check("synthetic user array accepted", SUCCEEDED(userSetter->SetUserArray(users.Get())));
            DWORD credentials = 0, defaultTile = 0; BOOL autoLogon = TRUE;
            check("tile enumeration does not auto-logon", SUCCEEDED(provider->GetCredentialCount(&credentials, &defaultTile, &autoLogon)) && credentials == 1 && !autoLogon && defaultTile == CREDENTIAL_PROVIDER_NO_DEFAULT);
            ComPtr<ICredentialProviderCredential> credential;
            check("synthetic credential enumerated", SUCCEEDED(provider->GetCredentialAt(0, &credential)));
            ComPtr<ICredentialProviderCredential2> v2;
            check("V2 credential interface", SUCCEEDED(credential.As(&v2)));
            PWSTR sid = nullptr; HRESULT sidResult = v2->GetUserSid(&sid);
            check("credential binds synthetic SID", SUCCEEDED(sidResult) && sid && wcscmp(sid, FakeSid) == 0); CoTaskMemFree(sid);
            UCHAR random[16]; wchar_t password[33] = {};
            check("random test credential generated", BCryptGenRandom(nullptr, random, sizeof(random), BCRYPT_USE_SYSTEM_PREFERRED_RNG) == 0);
            constexpr wchar_t hex[] = L"0123456789abcdef";
            for (size_t i = 0; i < sizeof(random); i++) { password[i * 2] = hex[random[i] >> 4]; password[i * 2 + 1] = hex[random[i] & 15]; }
            HRESULT set = credential->SetStringValue(passwordField, password);
            SecureZeroMemory(password, sizeof(password)); SecureZeroMemory(random, sizeof(random));
            check("synthetic password accepted in memory", SUCCEEDED(set));
            CREDENTIAL_PROVIDER_GET_SERIALIZATION_RESPONSE response = CPGSR_NO_CREDENTIAL_NOT_FINISHED;
            CREDENTIAL_PROVIDER_CREDENTIAL_SERIALIZATION serialized = {};
            PWSTR status = nullptr; CREDENTIAL_PROVIDER_STATUS_ICON icon = CPSI_NONE;
            HRESULT packed = credential->GetSerialization(&response, &serialized, &status, &icon);
            bool valid = SUCCEEDED(packed) && response == CPGSR_RETURN_CREDENTIAL_FINISHED && serialized.rgbSerialization && serialized.cbSerialization > 0 && IsEqualGUID(serialized.clsidCredentialProvider, SampleProvider);
            if (serialized.rgbSerialization) { SecureZeroMemory(serialized.rgbSerialization, serialized.cbSerialization); CoTaskMemFree(serialized.rgbSerialization); }
            CoTaskMemFree(status);
            check("Negotiate credential serialization completed without submitting authentication", valid);
            check("deselection clears credential", SUCCEEDED(credential->SetDeselected()));
        }
    } catch (const std::exception&) {
        if (tests.empty() || tests.back().second) tests.emplace_back("unexpected host failure", false);
    }
    if (library) FreeLibrary(library);
    if (SUCCEEDED(com)) CoUninitialize();
    size_t failed = 0; for (const auto& entry : tests) if (!entry.second) failed++;
    const auto build = ReadSystemVersion(L"CurrentBuildNumber"), version = ReadSystemVersion(L"DisplayVersion");
    std::ofstream output{std::filesystem::path(reportPath)};
    output << "{\"referenceProviderOnly\":true,\"registered\":false,\"windowsAuthenticationSubmitted\":false,\"windowsUnlockTested\":false,\"osBuild\":\"" << JsonText(build)
        << "\",\"displayVersion\":\"" << JsonText(version) << "\",\"total\":" << tests.size() << ",\"passed\":" << tests.size() - failed << ",\"failed\":" << failed << ",\"tests\":[";
    bool first = true; for (const auto& entry : tests) { if (!first) output << ','; first = false; output << "{\"name\":\"" << entry.first << "\",\"passed\":" << (entry.second ? "true" : "false") << '}'; }
    output << "]}";
    output.close();
    const bool reportWritten = static_cast<bool>(output);
    if (summary) {
        *summary = L"Reference provider interface checks: " + std::to_wstring(tests.size() - failed) + L"/" + std::to_wstring(tests.size()) + L" passed.\nWindows build: " + build + L" (" + version
            + L")\n\nProvider registered: NO\nWindows authentication submitted: NO\nWindows unlock tested: NO\n\n";
        *summary += reportWritten ? L"JSON report: " + std::wstring(reportPath) : L"ERROR: report could not be written.";
    }
    return failed || tests.empty() || !reportWritten ? 1 : 0;
}

#ifdef BANDKEY_GUI
int WINAPI wWinMain(HINSTANCE, HINSTANCE, PWSTR, int) {
    int argc = 0; auto args = CommandLineToArgvW(GetCommandLineW(), &argc);
    if (!args) return 2;
    const bool quiet = argc == 4 && wcscmp(args[3], L"--quiet") == 0;
    std::wstring summary; int result = 2;
    try {
        std::filesystem::path dll, report;
        if (argc == 1) {
            wchar_t executable[32768]; DWORD n = GetModuleFileNameW(nullptr, executable, ARRAYSIZE(executable));
            if (!n || n >= ARRAYSIZE(executable)) throw std::runtime_error("module path");
            dll = std::filesystem::path(executable).parent_path() / L"SampleV2CredentialProvider.dll";
            PWSTR local = nullptr;
            if (FAILED(SHGetKnownFolderPath(FOLDERID_LocalAppData, 0, nullptr, &local))) throw std::runtime_error("local app data");
            report = std::filesystem::path(local) / L"BandKeyLab"; CoTaskMemFree(local);
            std::filesystem::create_directories(report);
            report /= L"interface-" + std::to_wstring(GetTickCount64()) + L".json";
        } else if (argc == 3 || quiet) {
            dll = std::filesystem::absolute(args[1]); report = std::filesystem::absolute(args[2]);
        } else throw std::runtime_error("arguments");
        result = RunLab(dll.c_str(), report.c_str(), &summary);
    } catch (const std::exception&) { summary = L"Lab setup failed. No Windows authentication was submitted."; }
    LocalFree(args);
    if (!quiet) MessageBoxW(nullptr, summary.c_str(), L"BandKey interface lab - no Windows logon", MB_OK | (result ? MB_ICONERROR : MB_ICONINFORMATION));
    return result;
}
#else
int wmain(int argc, wchar_t** argv) { return argc == 3 ? RunLab(argv[1], argv[2], nullptr) : 2; }
#endif
