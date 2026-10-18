#define BANDKEY_UNIT_TEST
#include "../../src/BandKey.CredentialProvider/Provider.cpp"
#include "../../src/BandKey.CredentialProvider/LabCleanup.h"
#include <bcrypt.h>
#include <fstream>
#include <filesystem>
#include <iostream>
#include <stdexcept>
#include <thread>
#include <functional>

static const wchar_t* TestSid = L"S-1-5-21-100-200-300-1001";
class SyntheticProviderEvents final : public ICredentialProviderEvents {
    LONG refs = 1;
public:
    unsigned notifications = 0; UINT_PTR lastContext = 0;
    std::function<void()> changed;
    HRESULT STDMETHODCALLTYPE QueryInterface(REFIID iid, void** value) override {
        if (!value) return E_POINTER; *value = nullptr;
        if (iid != IID_IUnknown && iid != __uuidof(ICredentialProviderEvents)) return E_NOINTERFACE;
        *value = static_cast<ICredentialProviderEvents*>(this); AddRef(); return S_OK;
    }
    ULONG STDMETHODCALLTYPE AddRef() override { return InterlockedIncrement(&refs); }
    ULONG STDMETHODCALLTYPE Release() override { auto n = InterlockedDecrement(&refs); if (!n) delete this; return n; }
    HRESULT STDMETHODCALLTYPE CredentialsChanged(UINT_PTR context) override { ++notifications; lastContext = context; if (changed) changed(); return S_OK; }
};
struct ScopedGrantRuntime {
    SyntheticGrantRuntime* previous;
    explicit ScopedGrantRuntime(SyntheticGrantRuntime& value) : previous(syntheticGrantRuntime) { syntheticGrantRuntime = &value; }
    ~ScopedGrantRuntime() { syntheticGrantRuntime = previous; }
};
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
    if (argc == 3 && (wcscmp(argv[1], L"--verify-interop") == 0 || wcscmp(argv[1], L"--verify-java") == 0)) {
        auto directory = std::filesystem::path(argv[2]); bool passed = false;
        bool java = wcscmp(argv[1], L"--verify-java") == 0;
        try {
            auto body = ReadBoundedFile(directory / L"native-challenge.bin", sizeof(WindowsChallenge));
            auto response = ReadBoundedFile(directory / (java ? L"java-approval.bin" : L"csharp-approval.bin"), sizeof(WindowsApproval));
            auto key = ReadBoundedFile(directory / (java ? L"java-public.bin" : L"csharp-public.bin"), 72);
            NativeRequire(body.size() == sizeof(WindowsChallenge) && response.size() == sizeof(WindowsApproval), "interop-wire-size");
            WindowsChallenge challenge; WindowsApproval approval;
            CopyMemory(&challenge, body.data(), sizeof(challenge)); CopyMemory(&approval, response.data(), sizeof(approval));
            OneTimeWindowsGrant grant(challenge, 1000); passed = grant.Verify(approval, key, 100001, 1001);
            approval.signature[0] ^= 1; passed = passed && !grant.Verify(approval, key, 100001, 1001);
        } catch (...) { passed = false; }
        std::ofstream report(directory / (java ? L"native-java-result.json" : L"native-interop-result.json")); report << "{\"passed\":" << (passed ? 2 : 0) << ",\"failed\":" << (passed ? 0 : 1) << ",\"windowsAuthenticationSubmitted\":false}"; report.close();
        std::cout << "CNG verifies " << (java ? "Java" : "CSharp") << " signature and rejects tampering: " << (passed ? "passed" : "failed") << '\n'; return passed && report ? 0 : 1;
    }
    if (argc != 3 && argc != 4) return 2;
    std::vector<std::pair<const char*, bool>> tests;
    auto check = [&](const char* name, bool success) { tests.emplace_back(name, success); if (!success) throw std::runtime_error(name); };
    HRESULT com = CoInitializeEx(nullptr, COINIT_APARTMENTTHREADED);
    try {
        check("COM initialized", SUCCEEDED(com));
        {
            SyntheticGrantRuntime runtime;
            runtime.directory = std::filesystem::absolute(std::filesystem::path(argv[2]).parent_path()) / (L"grant-flow-" + std::to_wstring(GetCurrentProcessId()) + L"-" + std::to_wstring(GetTickCount64()));
            NativeRequire(CreateDirectoryW(runtime.directory.c_str(), nullptr) && CreateDirectoryW((runtime.directory / L"Exchange").c_str(), nullptr) && CreateDirectoryW((runtime.directory / L"Diagnostics").c_str(), nullptr), "grant-fixture-unavailable");
            BYTE random[16]{}; NativeRequire(BCryptGenRandom(nullptr, random, sizeof(random), BCRYPT_USE_SYSTEM_PREFERRED_RNG) >= 0, "test-random-unavailable");
            constexpr wchar_t hex[] = L"0123456789abcdef";
            for (size_t i = 0; i < sizeof(random); ++i) { runtime.password.data[2 * i] = hex[random[i] >> 4]; runtime.password.data[2 * i + 1] = hex[random[i] & 15]; } SecureZeroMemory(random, sizeof(random));
            SigningKey key; key.Generate(); runtime.policy = LabPolicy(true, TestSid); runtime.policy.mode = 2; runtime.policy.publicKey = key.Export(false);
            ScopedGrantRuntime scope(runtime);
            auto readChallenge = [&]() {
                WIN32_FIND_DATAW entry{}; HANDLE find = FindFirstFileW((runtime.directory / L"Exchange" / L"*.request").c_str(), &entry);
                NativeRequire(find != INVALID_HANDLE_VALUE, "pending-test-challenge-missing"); FindClose(find);
                auto path = runtime.directory / L"Exchange" / entry.cFileName; auto bytes = ReadBoundedFile(path, sizeof(WindowsChallenge));
                NativeRequire(bytes.size() == sizeof(WindowsChallenge), "test-challenge-size"); WindowsChallenge challenge; CopyMemory(&challenge, bytes.data(), sizeof(challenge)); return std::make_pair(path, challenge);
            };
            auto writeApproval = [&](bool invalid) {
                auto request = readChallenge(); auto response = SignWindowsChallenge(request.second, key); if (invalid) response.signature[0] ^= 1;
                WriteNewFile(request.first.wstring() + L".approval", reinterpret_cast<const BYTE*>(&response), sizeof(response));
            };
            {
                ComPtr<BandKeyProvider> provider; provider.Attach(new BandKeyProvider(runtime.policy));
                ComPtr<SyntheticUsers> users; users.Attach(new SyntheticUsers()); users->Add();
                provider->SetUsageScenario(CPUS_UNLOCK_WORKSTATION, 0); provider->SetUserArray(users.Get());
                ComPtr<SyntheticProviderEvents> callback; callback.Attach(new SyntheticProviderEvents());
                check("signed runtime notification setup succeeds", SUCCEEDED(provider->Advise(callback.Get(), 42)));
                DWORD count = 0, selected = 0; BOOL automatic = TRUE;
                check("locked synthetic session issues request without automatic credential", SUCCEEDED(provider->GetCredentialCount(&count, &selected, &automatic)) && count == 1 && !automatic && runtime.vaultReads == 0);
                auto original = readChallenge(); ComPtr<ICredentialProviderCredential> credential; provider->GetCredentialAt(0, &credential);
                credential->SetDeselected(); credential->UnAdvise();
                provider->UnAdvise(); provider->SetUsageScenario(CPUS_UNLOCK_WORKSTATION, 0); provider->SetUserArray(users.Get());
                check("UI unadvise and deselection preserve the original unexpired request", readChallenge().first == original.first && readChallenge().second.expiresMs == original.second.expiresMs);
                check("provider can subscribe again after refresh", SUCCEEDED(provider->Advise(callback.Get(), 42)));
                callback->changed = [&]() { credential->SetDeselected(); credential->UnAdvise(); provider->SetUsageScenario(CPUS_UNLOCK_WORKSTATION, 0); provider->SetUserArray(users.Get()); };
                writeApproval(false);
                auto until = GetTickCount64() + 2000;
                while (!callback->notifications && GetTickCount64() < until) {
                    MSG message{}; while (PeekMessageW(&message, nullptr, 0, 0, PM_REMOVE)) { TranslateMessage(&message); DispatchMessageW(&message); }
                    MsgWaitForMultipleObjectsEx(0, nullptr, 50, QS_ALLINPUT, MWMO_INPUTAVAILABLE);
                }
                check("real message pump delivers one signed-ready provider callback", callback->notifications == 1 && callback->lastContext == 42);
                check("signed authorization survives callback re-enumeration", SUCCEEDED(provider->GetCredentialCount(&count, &selected, &automatic)) && count == 1 && selected == 0 && automatic && runtime.vaultReads == 0);
                provider->UnAdvise(); NativeRequire(SUCCEEDED(provider->Advise(callback.Get(), 42)), "test-resubscription-failed");
                until = GetTickCount64() + 2000;
                while (callback->notifications < 2 && GetTickCount64() < until) {
                    MSG message{}; while (PeekMessageW(&message, nullptr, 0, 0, PM_REMOVE)) { TranslateMessage(&message); DispatchMessageW(&message); }
                    MsgWaitForMultipleObjectsEx(0, nullptr, 50, QS_ALLINPUT, MWMO_INPUTAVAILABLE);
                }
                check("new subscription is notified again without replacing approved request", callback->notifications == 2 && readChallenge().first == original.first && runtime.vaultReads == 0);
                BOOL selectedAutomatic = FALSE; credential->SetSelected(&selectedAutomatic);
                check("selecting valid signed tile requests automatic submission", selectedAutomatic != FALSE);
                CREDENTIAL_PROVIDER_GET_SERIALIZATION_RESPONSE response; CREDENTIAL_PROVIDER_CREDENTIAL_SERIALIZATION serialization{}; PWSTR text = nullptr; CREDENTIAL_PROVIDER_STATUS_ICON icon;
                auto result = credential->GetSerialization(&response, &serialization, &text, &icon); CoTaskMemFree(text);
                check("signed path serializes synthetic credential only after proof", SUCCEEDED(result) && response == CPGSR_RETURN_CREDENTIAL_FINISHED && serialization.rgbSerialization && runtime.vaultReads == 1);
                FreeSerialization(serialization);
                credential->GetSerialization(&response, &serialization, &text, &icon); CoTaskMemFree(text);
                check("refresh and repeated submission never reread the synthetic vault", !serialization.rgbSerialization && runtime.vaultReads == 1);
                provider->UnAdvise(); callback->changed = {};
            }
            auto checkRefusal = [&](int action) {
                runtime.locked = true; runtime.policy.enabled = true; runtime.policy.signerKind = 0; runtime.wall = 100000; runtime.monotonic = 1000;
                unsigned reads = runtime.vaultReads;
                ComPtr<BandKeyCredential> value; value.Attach(new BandKeyCredential(CPUS_UNLOCK_WORKSTATION, TestSid, L"BANDKEYLAB\\TestUser", runtime.policy));
                value->Ready();
                if (action != 0) writeApproval(action == 1);
                value->SetDeselected(); value->UnAdvise();
                if (action == 2) runtime.wall = 130000;
                if (action == 3) runtime.monotonic = 31000;
                if (action == 4) runtime.policy.enabled = false;
                if (action == 5) runtime.locked = false;
                if (action == 6) runtime.policy.signerKind = 1;
                if (action == 7) runtime.policy.publicKey[8] ^= 1;
                CREDENTIAL_PROVIDER_GET_SERIALIZATION_RESPONSE response; CREDENTIAL_PROVIDER_CREDENTIAL_SERIALIZATION serialization{}; PWSTR text = nullptr; CREDENTIAL_PROVIDER_STATUS_ICON icon;
                auto result = value->GetSerialization(&response, &serialization, &text, &icon); CoTaskMemFree(text);
                bool refused = SUCCEEDED(result) && !serialization.rgbSerialization && runtime.vaultReads == reads;
                FreeSerialization(serialization); return refused;
            };
            check("no approval through signed provider cannot read vault", checkRefusal(0));
            check("invalid signature through signed provider cannot read vault", checkRefusal(1));
            check("UI refresh cannot extend original wall-clock expiry", checkRefusal(2));
            check("UI refresh cannot extend original monotonic expiry", checkRefusal(3));
            check("revocation after UI refresh prevents vault read", checkRefusal(4));
            check("unlock after UI refresh prevents vault read", checkRefusal(5));
            check("changing signer kind cancels pending proof before vault read", checkRefusal(6));
            check("changing enrolled public key cancels pending proof before vault read", checkRefusal(7));
            runtime.policy.publicKey = key.Export(false); runtime.policy.signerKind = 1; runtime.policy.enabled = true; runtime.locked = true;
            runtime.wall = 100000; runtime.monotonic = 1000;
            {
                unsigned reads = runtime.vaultReads;
                ComPtr<BandKeyCredential> phone; phone.Attach(new BandKeyCredential(CPUS_UNLOCK_WORKSTATION, TestSid, L"BANDKEYLAB\\TestUser", runtime.policy));
                phone->Ready(); writeApproval(false);
                check("separately enrolled phone-kind key verifies native signed challenge", phone->Ready() && runtime.vaultReads == reads);
                CREDENTIAL_PROVIDER_GET_SERIALIZATION_RESPONSE response; CREDENTIAL_PROVIDER_CREDENTIAL_SERIALIZATION serialization{}; PWSTR text = nullptr; CREDENTIAL_PROVIDER_STATUS_ICON icon;
                auto result = phone->GetSerialization(&response, &serialization, &text, &icon); CoTaskMemFree(text);
                check("phone-kind proof uses same guarded native serialization", SUCCEEDED(result) && serialization.rgbSerialization && runtime.vaultReads == reads + 1);
                FreeSerialization(serialization);
            }
            auto log = runtime.directory / L"Diagnostics" / L"provider.log";
            auto bytes = ReadBoundedFile(log, 262304); std::string messages(bytes.begin(), bytes.end());
            check("diagnostics distinguish accepted signature and serialized credential", messages.find("approval-verified") != std::string::npos && messages.find("serialization-prepared") != std::string::npos);
            check("diagnostics distinguish callback and invalid signature", messages.find("refresh-requested") != std::string::npos && messages.find("approval-rejected") != std::string::npos);
            std::wstring wide(messages.begin(), messages.end());
            check("diagnostics contain no account SID or synthetic password", messages.find("S-1-5-21") == std::string::npos && wide.find(runtime.password.data.data()) == std::wstring::npos);
            LabCleanupResult cleanup; cleanup.File(log); cleanup.Directory(runtime.directory / L"Diagnostics"); cleanup.Directory(runtime.directory / L"Exchange"); cleanup.Directory(runtime.directory);
            check("signed flow fixture cleanup completes", cleanup.complete);
        }
        {
            auto root = std::filesystem::absolute(std::filesystem::path(argv[2]).parent_path()) / (L"cleanup-" + std::to_wstring(GetCurrentProcessId()) + L"-" + std::to_wstring(GetTickCount64()));
            NativeRequire(CreateDirectoryW(root.c_str(), nullptr) != FALSE, "cleanup-fixture-unavailable");
            const BYTE sample[] = {1, 2, 3}; auto source = root / L"source.bin", copied = root / L"copied.bin";
            WriteNewFile(source, sample, sizeof(sample)); NativeRequire(SetFileAttributesW(source.c_str(), FILE_ATTRIBUTE_READONLY) != FALSE, "fixture-readonly-failed");
            NativeRequire(CopyFileW(source.c_str(), copied.c_str(), TRUE) != FALSE, "fixture-copy-failed");
            check("copy reproduces optical-disc read-only inheritance", (GetFileAttributesW(copied.c_str()) & FILE_ATTRIBUTE_READONLY) != 0);
            BOOL oldDeleted = DeleteFileW(copied.c_str()); DWORD oldError = oldDeleted ? ERROR_SUCCESS : GetLastError();
            check("old deletion fails on read-only copy with access denied", !oldDeleted && oldError == ERROR_ACCESS_DENIED);
            check("fixed removal deletes read-only copy", RemoveOrdinaryLabFile(copied) == ERROR_SUCCESS && GetFileAttributesW(copied.c_str()) == INVALID_FILE_ATTRIBUTES);
            NativeRequire(CopyFileW(source.c_str(), copied.c_str(), TRUE) != FALSE, "fixture-copy-failed");
            check("installation clears copied file read-only attribute", PrepareCopiedLabFile(copied) == ERROR_SUCCESS && !(GetFileAttributesW(copied.c_str()) & FILE_ATTRIBUTE_READONLY));
            check("installation leaves source attributes unchanged", (GetFileAttributesW(source.c_str()) & FILE_ATTRIBUTE_READONLY) != 0);
            NativeHandle held; held.value = CreateFileW(copied.c_str(), GENERIC_READ, FILE_SHARE_READ, nullptr, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, nullptr);
            NativeRequire(held.value != INVALID_HANDLE_VALUE, "fixture-hold-failed");
            check("real sharing violation remains distinguishable", RemoveOrdinaryLabFile(copied) == ERROR_SHARING_VIOLATION);
            CloseHandle(held.value); held.value = INVALID_HANDLE_VALUE;
            check("removal succeeds after actual handle closes", RemoveOrdinaryLabFile(copied) == ERROR_SUCCESS);
            check("repeated removal is idempotent", RemoveOrdinaryLabFile(copied) == ERROR_SUCCESS);
            auto link = root / L"linked.bin"; NativeRequire(CreateHardLinkW(link.c_str(), source.c_str(), nullptr) != FALSE, "fixture-hardlink-failed");
            check("hard-linked files refused before attribute mutation", RemoveOrdinaryLabFile(link) == ERROR_TOO_MANY_LINKS && PrepareCopiedLabFile(link) == ERROR_TOO_MANY_LINKS && (GetFileAttributesW(source.c_str()) & FILE_ATTRIBUTE_READONLY));
            NativeRequire(SetFileAttributesW(source.c_str(), FILE_ATTRIBUTE_NORMAL) && DeleteFileW(link.c_str()), "fixture-hardlink-cleanup-failed");
            check("single-link source can be removed", RemoveOrdinaryLabFile(source) == ERROR_SUCCESS);
            auto unknown = root / L"notes.txt"; WriteNewFile(unknown, sample, sizeof(sample));
            NativeHandle parentGuard; LabCleanupResult guarded;
            NativeRequire(CheckCleanupDirectory(root, guarded, &parentGuard), "fixture-directory-guard-failed");
            NativeHandle writeParent; writeParent.value = CreateFileW(root.c_str(), GENERIC_WRITE, FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE, nullptr, OPEN_EXISTING, FILE_FLAG_BACKUP_SEMANTICS, nullptr);
            DWORD guardError = writeParent.value == INVALID_HANDLE_VALUE ? GetLastError() : ERROR_SUCCESS;
            check("parent guard prevents directory substitution while permitting owned child removal", writeParent.value == INVALID_HANDLE_VALUE && guardError == ERROR_SHARING_VIOLATION && RemoveOrdinaryLabFile(unknown) == ERROR_SUCCESS);
            CloseHandle(parentGuard.value); parentGuard.value = INVALID_HANDLE_VALUE;
            WriteNewFile(unknown, sample, sizeof(sample));
            LabCleanupResult result; result.Directory(root);
            check("nonempty directory reports exact error without recursive deletion", !result.complete && result.issues.size() == 1 && result.issues[0].error == ERROR_DIR_NOT_EMPTY && GetFileAttributesW(unknown.c_str()) != INVALID_FILE_ATTRIBUTES);
            check("exchange cleanup accepts only owned message names", IsLabExchangeFile(L"signer.dpapi") && IsLabExchangeFile(std::wstring(64, L'a') + L".request.approval") && !IsLabExchangeFile(L"notes.txt") && !IsLabExchangeFile(L"..\\other.request") && !IsLabExchangeFile(std::wstring(64, L'g') + L".request"));
            LabCleanupResult invalidDirectory;
            check("cleanup refuses wrong path types", RemoveOrdinaryLabFile(root) != ERROR_SUCCESS && (GetFileAttributesW(root.c_str()) & FILE_ATTRIBUTE_DIRECTORY) && !CheckCleanupDirectory(unknown, invalidDirectory) && !invalidDirectory.complete);
            NativeRequire(RemoveOrdinaryLabFile(unknown) == ERROR_SUCCESS, "fixture-file-cleanup-failed");
            LabCleanupResult empty; empty.Directory(root);
            check("empty fixture directory removed", empty.complete && GetFileAttributesW(root.c_str()) == INVALID_FILE_ATTRIBUTES);
        }
        {
            SigningKey key; key.Generate(); auto publicKey = key.Export(false);
            auto challenge = NewWindowsChallenge(TestSid, 5, 100000);
            auto signedRequest = SignWindowsChallenge(challenge, key);
            check("canonical Windows challenge size", sizeof(challenge) == 436 && sizeof(signedRequest) == 500);
            check("P256 public key uses expected native blob", publicKey.size() == 72);
            check("Windows challenge context accepted", ChallengeContextValid(challenge, TestSid, 5, 100001));
            check("wrong Windows session rejected", !ChallengeContextValid(challenge, TestSid, 6, 100001));
            check("wrong Windows SID rejected", !ChallengeContextValid(challenge, L"S-1-5-21-100-200-300-1002", 5, 100001));
            check("challenge expiry boundary rejected", !ChallengeContextValid(challenge, TestSid, 5, 130000));
            check("wall clock before issuance rejected", !ChallengeContextValid(challenge, TestSid, 5, 99999));
            auto otherChallenge = NewWindowsChallenge(TestSid, 5, 100000);
            check("Windows nonces are unpredictable across requests", memcmp(challenge.nonce, otherChallenge.nonce, sizeof(challenge.nonce)) != 0);
            OneTimeWindowsGrant grant(challenge, 1000);
            check("valid signed Windows grant accepted", grant.Verify(signedRequest, publicKey, 100001, 1001));
            auto changed = signedRequest; changed.signature[0] ^= 1;
            check("signature tampering rejected", !grant.Verify(changed, publicKey, 100001, 1001));
            changed = signedRequest; changed.challenge.nonce[0] ^= 1;
            check("nonce substitution rejected", !grant.Verify(changed, publicKey, 100001, 1001));
            changed = signedRequest; changed.challenge.session++;
            check("signed grant cannot change sessions", !grant.Verify(changed, publicKey, 100001, 1001));
            changed = signedRequest; changed.challenge.sid[0] ^= 1;
            check("signed grant cannot change target account", !grant.Verify(changed, publicKey, 100001, 1001));
            changed = signedRequest; changed.challenge.expiresMs++;
            check("signed grant cannot extend lifetime", !grant.Verify(changed, publicKey, 100001, 1001));
            SigningKey rotated; rotated.Generate(); auto rotatedPublic = rotated.Export(false);
            check("wrong or rotated key rejected", !grant.Verify(signedRequest, rotatedPublic, 100001, 1001));
            check("monotonic expiry rejects frozen wall clock", !grant.Verify(signedRequest, publicKey, 100001, 31000));
            check("monotonic rollback rejected", !grant.Verify(signedRequest, publicKey, 100001, 999));
            check("grant consumed once", grant.Consume(signedRequest, publicKey, 100001, 1001));
            check("consumed grant rejects replay", !grant.Consume(signedRequest, publicKey, 100001, 1001));
            OneTimeWindowsGrant concurrent(challenge, 1000); std::atomic_uint successes{0}; std::vector<std::thread> workers;
            for (int i = 0; i < 16; i++) workers.emplace_back([&] { if (concurrent.Consume(signedRequest, publicKey, 100001, 1001)) successes++; });
            for (auto& worker : workers) worker.join();
            check("concurrent approvals consume exactly once", successes == 1);
            auto unterminated = challenge; for (auto& c : unterminated.sid) c = L'x';
            check("unterminated SID rejected without reading beyond packet", !ChallengeContextValid(unterminated, TestSid, 5, 100001));
            OneTimeWindowsGrant malformed(unterminated, 1000);
            check("malformed verifier context fails closed", !malformed.Verify(signedRequest, publicKey, 100001, 1001));
            if (argc == 4) {
                auto directory = std::filesystem::path(argv[3]); std::filesystem::create_directories(directory);
                WriteNewFile(directory / L"native-public.bin", publicKey.data(), static_cast<DWORD>(publicKey.size()));
                WriteNewFile(directory / L"native-challenge.bin", reinterpret_cast<BYTE*>(&challenge), sizeof(challenge));
                WriteNewFile(directory / L"native-approval.bin", reinterpret_cast<BYTE*>(&signedRequest), sizeof(signedRequest));
            }
        }
        {
            SecretBuffer<33> secret; BYTE random[16]{}; NativeRequire(BCryptGenRandom(nullptr, random, sizeof(random), BCRYPT_USE_SYSTEM_PREFERRED_RNG) >= 0, "test-random-unavailable");
            constexpr wchar_t hex[] = L"0123456789abcdef";
            for (size_t i = 0; i < sizeof(random); i++) { secret.data[i * 2] = hex[random[i] >> 4]; secret.data[i * 2 + 1] = hex[random[i] & 15]; } SecureZeroMemory(random, sizeof(random));
            ProtectedBytes encrypted; EncryptSecret(reinterpret_cast<BYTE*>(secret.data.data()), sizeof(secret.data), TestSid, true, encrypted);
            std::vector<BYTE> cipher(encrypted.blob.pbData, encrypted.blob.pbData + encrypted.blob.cbData);
            check("machine DPAPI ciphertext differs from synthetic secret", cipher.size() != sizeof(secret.data) || memcmp(cipher.data(), secret.data.data(), sizeof(secret.data)) != 0);
            ProtectedBytes restored; DecryptSecret(cipher, TestSid, restored);
            check("vault DPAPI round trip preserves synthetic secret", restored.blob.cbData == sizeof(secret.data) && memcmp(restored.blob.pbData, secret.data.data(), sizeof(secret.data)) == 0);
            bool refused = false;
            try { ProtectedBytes wrong; DecryptSecret(cipher, L"S-1-5-21-100-200-300-1002", wrong); } catch (...) { refused = true; }
            check("vault blob binds the target SID", refused);
            cipher.back() ^= 1; refused = false;
            try { ProtectedBytes wrong; DecryptSecret(cipher, TestSid, wrong); } catch (...) { refused = true; }
            check("vault ciphertext tampering rejected", refused);
            auto permissions = [&](const wchar_t* text) {
                PSECURITY_DESCRIPTOR descriptor = nullptr;
                bool valid = ConvertStringSecurityDescriptorToSecurityDescriptorW(text, SDDL_REVISION_1, &descriptor, nullptr) && RestrictedToSystemAndAdministrators(descriptor);
                if (descriptor) LocalFree(descriptor); return valid;
            };
            check("SYSTEM and admin only vault ACL accepted", permissions(L"O:BAG:BAD:P(A;;FA;;;SY)(A;;FA;;;BA)"));
            check("world-readable machine DPAPI vault rejected", !permissions(L"O:BAG:BAD:P(A;;FA;;;SY)(A;;FA;;;BA)(A;;GR;;;WD)"));
            check("target-user-readable machine DPAPI vault rejected", !permissions(L"O:BAG:BAD:P(A;;FA;;;SY)(A;;FA;;;BA)(A;;GR;;;S-1-5-21-100-200-300-1001)"));
            check("untrusted vault owner rejected", !permissions(L"O:S-1-5-21-100-200-300-1001G:BAD:P(A;;FA;;;SY)(A;;FA;;;BA)"));
            check("null vault DACL rejected", !permissions(L"O:BAG:BAD:NO_ACCESS_CONTROL"));
        }
        {
            LabPolicy signedPolicy(true, TestSid); signedPolicy.mode = 2;
            ComPtr<ICredentialProviderCredential> credential; credential.Attach(new BandKeyCredential(CPUS_LOGON, TestSid, L"BANDKEYLAB\\TestUser", signedPolicy));
            CREDENTIAL_PROVIDER_FIELD_STATE state; CREDENTIAL_PROVIDER_FIELD_INTERACTIVE_STATE interactive;
            credential->GetFieldState(Password, &state, &interactive);
            DWORD adjacent = MAXDWORD;
            check("signed mode hides password and uses a visible submit field", state == CPFS_HIDDEN && interactive == CPFIS_NONE && SUCCEEDED(credential->GetSubmitButtonValue(Submit, &adjacent)) && adjacent == Explanation);
            check("signed mode refuses manual password injection", credential->SetStringValue(Password, L"synthetic") == E_ACCESSDENIED);
            CREDENTIAL_PROVIDER_GET_SERIALIZATION_RESPONSE response; CREDENTIAL_PROVIDER_CREDENTIAL_SERIALIZATION serialization{}; PWSTR status = nullptr; CREDENTIAL_PROVIDER_STATUS_ICON icon;
            check("signed mode never emits a credential without authorization", SUCCEEDED(credential->GetSerialization(&response, &serialization, &status, &icon)) && response == CPGSR_NO_CREDENTIAL_NOT_FINISHED && !serialization.rgbSerialization); CoTaskMemFree(status);
        }
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
            NativeRequire(BCryptGenRandom(nullptr, random, sizeof(random), BCRYPT_USE_SYSTEM_PREFERRED_RNG) >= 0, "test-random-unavailable");
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
        if (!factoryFunction) throw std::runtime_error("release-factory-unavailable");
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
