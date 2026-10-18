#define BANDKEY_UNIT_TEST
#include "../../src/BandKey.CredentialProvider/Provider.cpp"
#include <bcrypt.h>
#include <fstream>
#include <filesystem>
#include <iostream>
#include <stdexcept>

static const wchar_t* TestSid = L"S-1-5-21-100-200-300-1001";
class SyntheticUser final : public ICredentialProviderUser {
    LONG refs = 1;
public:
    std::wstring sid, name; GUID provider;
    SyntheticUser(std::wstring id, std::wstring account, GUID identity) : sid(std::move(id)), name(std::move(account)), provider(identity) {}
    HRESULT STDMETHODCALLTYPE QueryInterface(REFIID iid, void** value) override {
        if (!value) return E_POINTER; *value = nullptr;
        if (iid != IID_IUnknown && iid != __uuidof(ICredentialProviderUser)) return E_NOINTERFACE;
        *value = static_cast<ICredentialProviderUser*>(this); AddRef(); return S_OK;
    }
    ULONG STDMETHODCALLTYPE AddRef() override { return InterlockedIncrement(&refs); }
    ULONG STDMETHODCALLTYPE Release() override { auto n = InterlockedDecrement(&refs); if (!n) delete this; return n; }
    HRESULT STDMETHODCALLTYPE GetSid(PWSTR* value) override { return SHStrDupW(sid.c_str(), value); }
    HRESULT STDMETHODCALLTYPE GetProviderID(GUID* value) override { *value = provider; return S_OK; }
    HRESULT STDMETHODCALLTYPE GetStringValue(REFPROPERTYKEY key, PWSTR* value) override { *value = nullptr; return IsEqualPropertyKey(key, PKEY_Identity_QualifiedUserName) ? SHStrDupW(name.c_str(), value) : E_NOTIMPL; }
    HRESULT STDMETHODCALLTYPE GetValue(REFPROPERTYKEY, PROPVARIANT* value) override { *value = {}; return E_NOTIMPL; }
};
class SyntheticUsers final : public ICredentialProviderUserArray {
    LONG refs = 1;
public:
    std::vector<ComPtr<ICredentialProviderUser>> values;
    DWORD reportedCount = MAXDWORD;
    void Add(const wchar_t* sid = TestSid, const wchar_t* name = L"BANDKEYLAB\\TestUser", GUID identity = Identity_LocalUserProvider) {
        ComPtr<ICredentialProviderUser> user; user.Attach(new SyntheticUser(sid, name, identity)); values.push_back(user);
    }
    HRESULT STDMETHODCALLTYPE QueryInterface(REFIID iid, void** value) override {
        if (!value) return E_POINTER; *value = nullptr;
        if (iid != IID_IUnknown && iid != __uuidof(ICredentialProviderUserArray)) return E_NOINTERFACE;
        *value = static_cast<ICredentialProviderUserArray*>(this); AddRef(); return S_OK;
    }
    ULONG STDMETHODCALLTYPE AddRef() override { return InterlockedIncrement(&refs); }
    ULONG STDMETHODCALLTYPE Release() override { auto n = InterlockedDecrement(&refs); if (!n) delete this; return n; }
    HRESULT STDMETHODCALLTYPE SetProviderFilter(REFGUID) override { return S_OK; }
    HRESULT STDMETHODCALLTYPE GetAccountOptions(CREDENTIAL_PROVIDER_ACCOUNT_OPTIONS* value) override { *value = CPAO_NONE; return S_OK; }
    HRESULT STDMETHODCALLTYPE GetCount(DWORD* value) override { *value = reportedCount == MAXDWORD ? static_cast<DWORD>(values.size()) : reportedCount; return S_OK; }
    HRESULT STDMETHODCALLTYPE GetAt(DWORD index, ICredentialProviderUser** value) override { *value = nullptr; return index < values.size() ? values[index].CopyTo(value) : E_INVALIDARG; }
};
static DWORD Count(ICredentialProvider* provider) {
    DWORD count = MAXDWORD, selected = 0; BOOL automatic = TRUE;
    if (FAILED(provider->GetCredentialCount(&count, &selected, &automatic)) || automatic || selected != CREDENTIAL_PROVIDER_NO_DEFAULT) throw std::runtime_error("unsafe enumeration");
    return count;
}
static void FreeSerialization(CREDENTIAL_PROVIDER_CREDENTIAL_SERIALIZATION& result) {
    if (result.rgbSerialization) { SecureZeroMemory(result.rgbSerialization, result.cbSerialization); CoTaskMemFree(result.rgbSerialization); result = {}; }
}
int wmain(int argc, wchar_t** argv) {
    if (argc != 3) return 2;
    std::vector<std::pair<const char*, bool>> tests;
    auto check = [&](const char* name, bool success) { tests.emplace_back(name, success); if (!success) throw std::runtime_error(name); };
    HRESULT com = CoInitializeEx(nullptr, COINIT_APARTMENTTHREADED);
    try {
        check("COM initialized", SUCCEEDED(com));
        check("VMware build 26300 accepted by safety guard", IsSupportedVmwareMachine(L"VMware, Inc.", L"VMware20,1", L"26300"));
        check("physical PC refused by safety guard", !IsSupportedVmwareMachine(L"ASUSTeK COMPUTER INC.", L"Desktop", L"26300"));
        check("different guest build refused", !IsSupportedVmwareMachine(L"VMware, Inc.", L"VMware20,1", L"26200"));
        check("incomplete VM identity refused", !IsSupportedVmwareMachine(L"VMware, Inc.", L"Desktop", L"26300"));
        {
            ComPtr<IClassFactory> factory;
            check("production class factory available without registration", SUCCEEDED(DllGetClassObject(BandKeyLabClsid, IID_PPV_ARGS(&factory))));
            void* unknown = reinterpret_cast<void*>(1);
            check("unknown class refused", DllGetClassObject(GUID_NULL, IID_IClassFactory, &unknown) == CLASS_E_CLASSNOTAVAILABLE && !unknown);
            check("aggregation refused", factory->CreateInstance(factory.Get(), IID_IUnknown, &unknown) == CLASS_E_NOAGGREGATION && !unknown);
            ComPtr<ICredentialProvider> disabled;
            disabled.Attach(new BandKeyProvider({false, TestSid}));
            ComPtr<SyntheticUsers> users; users.Attach(new SyntheticUsers()); users->Add();
            disabled->SetUsageScenario(CPUS_LOGON, 0); ComPtr<ICredentialProviderSetUserArray> setter; disabled.As(&setter); setter->SetUserArray(users.Get());
            check("disabled lab exposes no tile", Count(disabled.Get()) == 0);
        }
        for (auto scenario : {CPUS_LOGON, CPUS_UNLOCK_WORKSTATION}) {
            ComPtr<ICredentialProvider> provider; provider.Attach(new BandKeyProvider({true, TestSid}));
            check("local usage scenario accepted", SUCCEEDED(provider->SetUsageScenario(scenario, 0)));
            check("missing users expose no tile", Count(provider.Get()) == 0);
            ComPtr<ICredentialProviderSetUserArray> setter; provider.As(&setter);
            ComPtr<SyntheticUsers> users; users.Attach(new SyntheticUsers());
            setter->SetUserArray(users.Get()); check("empty users expose no tile", Count(provider.Get()) == 0);
            users->Add(L"S-1-5-21-100-200-300-1002"); setter->SetUserArray(users.Get());
            check("wrong SID exposes no tile", Count(provider.Get()) == 0);
            users->Add(TestSid, L"MicrosoftAccount\\fake@example.invalid", GUID_NULL); setter->SetUserArray(users.Get());
            check("non-local identity exposes no tile", Count(provider.Get()) == 0);
            users->Add(); setter->SetUserArray(users.Get());
            check("configured local user enumerated beyond first user", Count(provider.Get()) == 1);
            DWORD fields = 0; provider->GetFieldDescriptorCount(&fields);
            check("lab has six fields including option logo and label", fields == FieldCount);
            CREDENTIAL_PROVIDER_FIELD_DESCRIPTOR* descriptor = nullptr;
            check("invalid field safely refused", provider->GetFieldDescriptorAt(MAXDWORD, &descriptor) == E_INVALIDARG && !descriptor);
            ComPtr<ICredentialProviderCredential> credential;
            check("configured tile available", SUCCEEDED(provider->GetCredentialAt(0, &credential)));
            provider->GetFieldDescriptorAt(Logo, &descriptor);
            check("sign-in option exposes logo metadata", descriptor && descriptor->cpft == CPFT_TILE_IMAGE && descriptor->guidFieldType == CPFG_CREDENTIAL_PROVIDER_LOGO);
            CoTaskMemFree(descriptor->pszLabel); CoTaskMemFree(descriptor);
            provider->GetFieldDescriptorAt(Label, &descriptor);
            check("sign-in option exposes label metadata", descriptor && descriptor->cpft == CPFT_SMALL_TEXT && descriptor->guidFieldType == CPFG_CREDENTIAL_PROVIDER_LABEL);
            CoTaskMemFree(descriptor->pszLabel); CoTaskMemFree(descriptor);
            HBITMAP bitmap = nullptr; BITMAP bitmapInfo = {};
            check("sign-in option logo can be rendered", SUCCEEDED(credential->GetBitmapValue(Logo, &bitmap)) && bitmap && GetObjectW(bitmap, sizeof(bitmapInfo), &bitmapInfo) && bitmapInfo.bmWidth == 64 && bitmapInfo.bmHeight == 64); DeleteObject(bitmap);
            PWSTR label = nullptr; credential->GetStringValue(Label, &label);
            check("sign-in option label identifies the VM lab", label && wcscmp(label, L"BandKey VM credential test") == 0); CoTaskMemFree(label);
            BOOL automatic = TRUE; credential->SetSelected(&automatic); check("selecting tile never auto-signs in", !automatic);
            ComPtr<ICredentialProviderCredential2> v2; credential.As(&v2); PWSTR sid = nullptr; v2->GetUserSid(&sid);
            check("tile bound to configured SID", sid && wcscmp(sid, TestSid) == 0); CoTaskMemFree(sid);
            CREDENTIAL_PROVIDER_GET_SERIALIZATION_RESPONSE response;
            CREDENTIAL_PROVIDER_CREDENTIAL_SERIALIZATION packed = {}; PWSTR status = nullptr; CREDENTIAL_PROVIDER_STATUS_ICON icon;
            check("no password never emits credential", SUCCEEDED(credential->GetSerialization(&response, &packed, &status, &icon)) && response == CPGSR_NO_CREDENTIAL_NOT_FINISHED && !packed.rgbSerialization); CoTaskMemFree(status);
            std::wstring tooLong(257, L'x');
            check("oversized input refused", credential->SetStringValue(Password, tooLong.c_str()) == E_INVALIDARG);
            SecretBuffer<33> password; BYTE random[16] = {};
            BCryptGenRandom(nullptr, random, sizeof(random), BCRYPT_USE_SYSTEM_PREFERRED_RNG);
            constexpr wchar_t hex[] = L"0123456789abcdef";
            for (size_t i = 0; i < sizeof(random); i++) { password.data[i * 2] = hex[random[i] >> 4]; password.data[i * 2 + 1] = hex[random[i] & 15]; }
            SecureZeroMemory(random, sizeof(random));
            check("synthetic input accepted", SUCCEEDED(credential->SetStringValue(Password, password.data.data())));
            PWSTR readback = nullptr; credential->GetStringValue(Password, &readback);
            check("password cannot be read back", readback && !readback[0]); CoTaskMemFree(readback);
            check("local credential serializes", SUCCEEDED(credential->GetSerialization(&response, &packed, &status, &icon)) && response == CPGSR_RETURN_CREDENTIAL_FINISHED && packed.rgbSerialization); CoTaskMemFree(status);
            auto kerb = reinterpret_cast<KERB_INTERACTIVE_UNLOCK_LOGON*>(packed.rgbSerialization);
            check("correct logon or unlock message type", kerb->Logon.MessageType == (scenario == CPUS_LOGON ? KerbInteractiveLogon : KerbWorkstationUnlockLogon));
            check("serialization uses BandKey lab CLSID", packed.clsidCredentialProvider == BandKeyLabClsid);
            auto offset = reinterpret_cast<size_t>(kerb->Logon.Password.Buffer);
            check("protected password stays inside serialization", offset >= sizeof(*kerb) && offset + kerb->Logon.Password.Length <= packed.cbSerialization);
            auto protectedText = reinterpret_cast<const wchar_t*>(packed.rgbSerialization + offset);
            check("serialized password differs from plaintext", kerb->Logon.Password.Length != 64 || memcmp(protectedText, password.data.data(), 64) != 0);
            SecretBuffer<2048> protectedCopy;
            check("protected credential length is bounded", kerb->Logon.Password.Length < sizeof(protectedCopy.data));
            CopyMemory(protectedCopy.data.data(), protectedText, kerb->Logon.Password.Length);
            CRED_PROTECTION_TYPE protection = CredUnprotected;
            check("Windows recognizes protected credential", CredIsProtectedW(protectedCopy.data.data(), &protection) && protection != CredUnprotected);
            SecretBuffer<257> restored; DWORD restoredChars = static_cast<DWORD>(restored.data.size());
            check("Windows protection round trip preserves synthetic credential", CredUnprotectW(FALSE, protectedCopy.data.data(), static_cast<DWORD>(wcslen(protectedCopy.data.data()) + 1), restored.data.data(), &restoredChars) && wcscmp(restored.data.data(), password.data.data()) == 0);
            FreeSerialization(packed);
            check("successful packing consumes password", SUCCEEDED(credential->GetSerialization(&response, &packed, &status, &icon)) && response == CPGSR_NO_CREDENTIAL_NOT_FINISHED && !packed.rgbSerialization); CoTaskMemFree(status);
            credential->SetStringValue(Password, password.data.data()); credential->SetDeselected();
            check("deselection erases password", SUCCEEDED(credential->GetSerialization(&response, &packed, &status, &icon)) && !packed.rgbSerialization); CoTaskMemFree(status);
            credential->SetStringValue(Password, password.data.data()); credential->UnAdvise();
            check("unadvise erases password", SUCCEEDED(credential->GetSerialization(&response, &packed, &status, &icon)) && !packed.rgbSerialization); CoTaskMemFree(status);
            credential->SetStringValue(Password, password.data.data());
            credential->ReportResult(static_cast<NTSTATUS>(0xc000006d), 0, &status, &icon);
            check("Windows failure is reported as error", icon == CPSI_ERROR && status); CoTaskMemFree(status);
            check("Windows failure erases password", SUCCEEDED(credential->GetSerialization(&response, &packed, &status, &icon)) && !packed.rgbSerialization); CoTaskMemFree(status);
            users->reportedCount = 129; setter->SetUserArray(users.Get());
            DWORD count, selected; check("unbounded enumeration refused", provider->GetCredentialCount(&count, &selected, &automatic) == E_INVALIDARG && count == 0);
            users->reportedCount = MAXDWORD;
            check("CredUI scenario refused", provider->SetUsageScenario(CPUS_CREDUI, 0) == E_NOTIMPL);
            check("unsupported scenario clears old tile", Count(provider.Get()) == 0);
        }
        check("all COM objects released", DllCanUnloadNow() == S_OK);
        // Also load the actual packaged DLL. No registry writes or system authentication.
        HMODULE module = LoadLibraryExW(argv[1], nullptr, LOAD_LIBRARY_SEARCH_DLL_LOAD_DIR | LOAD_LIBRARY_SEARCH_SYSTEM32);
        check("release DLL loads", module != nullptr);
        auto factoryFunction = reinterpret_cast<HRESULT(WINAPI*)(REFCLSID, REFIID, void**)>(GetProcAddress(module, "DllGetClassObject"));
        check("release DLL factory exported", factoryFunction != nullptr);
        {
            ComPtr<IClassFactory> factory; check("release DLL creates factory", SUCCEEDED(factoryFunction(BandKeyLabClsid, IID_PPV_ARGS(&factory))));
            ComPtr<ICredentialProvider> provider; check("release DLL creates provider", SUCCEEDED(factory->CreateInstance(nullptr, IID_PPV_ARGS(&provider))));
            provider->SetUsageScenario(CPUS_LOGON, 0); check("unconfigured release DLL exposes no tile", Count(provider.Get()) == 0);
        }
        FreeLibrary(module);
    } catch (const std::exception& error) { std::cerr << error.what() << '\n'; if (tests.empty() || tests.back().second) tests.emplace_back("unexpected test failure", false); }
    if (SUCCEEDED(com)) CoUninitialize();
    size_t failed = 0; for (const auto& entry : tests) if (!entry.second) failed++;
    std::ofstream report{std::filesystem::path(argv[2])};
    report << "{\"registered\":false,\"windowsAuthenticationSubmitted\":false,\"windowsUnlockTested\":false,\"syntheticIdentity\":true,\"total\":" << tests.size() << ",\"passed\":" << tests.size() - failed << ",\"failed\":" << failed << ",\"tests\":[";
    bool first = true; for (const auto& entry : tests) { if (!first) report << ','; first = false; report << "{\"name\":\"" << entry.first << "\",\"passed\":" << (entry.second ? "true" : "false") << '}'; } report << "]}"; report.close();
    std::cout << "BandKey VM provider: " << tests.size() - failed << '/' << tests.size() << "; no Windows authentication submitted.\n";
    return failed || !report ? 1 : 0;
}
