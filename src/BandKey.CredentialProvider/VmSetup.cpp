#include <windows.h>
#include <shellapi.h>
#include <shlobj.h>
#include <knownfolders.h>
#include <commctrl.h>
#include <aclapi.h>
#include <wtsapi32.h>
#include <lm.h>
#include <bcrypt.h>
#include <filesystem>
#include <fstream>
#include <vector>
#include <array>
#include <stdexcept>
#include <exception>
#include <wincred.h>
#include <strsafe.h>
#include <commdlg.h>
#include "SignedGrant.h"
#include "LabCleanup.h"
#include "DeploymentLifecycle.h"
#include "LabPolicy.h"
#include "ProviderHash.h"
#include "PayloadUpdate.h"
#pragma comment(linker, "/manifestdependency:\"type='win32' name='Microsoft.Windows.Common-Controls' version='6.0.0.0' processorArchitecture='amd64' publicKeyToken='6595b64144ccf1df' language='*'\"")

struct SetupFailure { std::wstring text; DWORD code; };
static void Require(bool success, const wchar_t* text, DWORD code = ERROR_INVALID_DATA) { if (!success) throw SetupFailure{text, code}; }
struct Key {
    HKEY value = nullptr;
    ~Key() { if (value) RegCloseKey(value); }
};
static bool KeyExists(const wchar_t* path) {
    Key key; auto result = RegOpenKeyExW(HKEY_LOCAL_MACHINE, path, 0, KEY_READ | KEY_WOW64_64KEY, &key.value);
    Require(result == ERROR_SUCCESS || result == ERROR_FILE_NOT_FOUND, L"Cannot inspect lab registration", result);
    return result == ERROR_SUCCESS;
}
static void NewKey(const wchar_t* path, Key& key) {
    DWORD disposition = 0;
    auto code = RegCreateKeyExW(HKEY_LOCAL_MACHINE, path, 0, nullptr, 0, KEY_WRITE | KEY_WOW64_64KEY, nullptr, &key.value, &disposition);
    Require(code == ERROR_SUCCESS, L"Cannot create lab registry key", code);
    Require(disposition == REG_CREATED_NEW_KEY, L"Lab key already exists; remove the old lab first", ERROR_ALREADY_EXISTS);
}
static void TextValue(HKEY key, const wchar_t* name, const std::wstring& text) {
    auto code = RegSetValueExW(key, name, 0, REG_SZ, reinterpret_cast<const BYTE*>(text.c_str()), static_cast<DWORD>((text.size() + 1) * sizeof(wchar_t)));
    Require(code == ERROR_SUCCESS, L"Cannot write lab registry value", code);
}
static void EnabledValue(HKEY key, DWORD value) {
    auto code = RegSetValueExW(key, L"Enabled", 0, REG_DWORD, reinterpret_cast<const BYTE*>(&value), sizeof(value));
    Require(code == ERROR_SUCCESS, L"Cannot update lab enable flag", code);
}
static DWORD DeleteOwnedKey(const wchar_t* path) {
    auto code = RegDeleteTreeW(HKEY_LOCAL_MACHINE, path);
    return MissingDeploymentKeyIsSuccess(code);
}
static DWORD DisableOwnedPolicy() {
    Key config;
    auto code = RegOpenKeyExW(HKEY_LOCAL_MACHINE, BandKeyLabConfig, 0, KEY_SET_VALUE | KEY_WOW64_64KEY, &config.value);
    if (code != ERROR_SUCCESS) return MissingDeploymentKeyIsSuccess(code);
    const DWORD disabled = 0;
    return MissingDeploymentKeyIsSuccess(RegSetValueExW(config.value, L"Enabled", 0, REG_DWORD,
        reinterpret_cast<const BYTE*>(&disabled), sizeof(disabled)));
}
static DeploymentRevocation RevokeOwnedRegistration(DeploymentOwnedKeys owned) {
    return RevokeDeployment(owned, DisableOwnedPolicy, [](DeploymentKey key) {
        return DeleteOwnedKey(key == DeploymentKey::Provider ? BandKeyLabRegistration : key == DeploymentKey::Policy ? BandKeyLabConfig : BandKeyLabCom);
    });
}
static void RecordRecoveryRequired(const std::filesystem::path& directory, DWORD error) noexcept {
    try { WriteDeploymentProgress(directory, DeploymentPhase::RecoveryRequired, error); } catch (...) {}
}
static std::filesystem::path ExecutablePath() {
    std::vector<wchar_t> path(32768); DWORD n = GetModuleFileNameW(nullptr, path.data(), static_cast<DWORD>(path.size()));
    Require(n && n < path.size(), L"Cannot resolve setup path"); return path.data();
}
static std::string FileHash(const std::filesystem::path& path);
static void PrepareHostRecovery(){
    if(!BandKeyHostPilot)return;
    PWSTR folder=nullptr;Require(SUCCEEDED(SHGetKnownFolderPath(FOLDERID_ProgramData,0,nullptr,&folder)),L"Cannot resolve recovery folder");
    std::filesystem::path recovery(folder);CoTaskMemFree(folder);recovery/=L"BandKeyHostPilotRecovery";
    const BOOL created=CreateDirectoryW(recovery.c_str(),nullptr);
    Require(created || GetLastError()==ERROR_ALREADY_EXISTS,L"Cannot prepare recovery entry");
    CheckNoReparseDirectory(recovery);
    SetAdministratorSecretSecurity(recovery);
    SetDirectoryDacl(recovery,L"D:P(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)(A;OICI;GRGX;;;BU)");
    const auto source=ExecutablePath(),destination=recovery/L"BandKey.HostSetup.exe";
    if(std::filesystem::exists(destination)){
        const auto attrs=GetFileAttributesW(destination.c_str());Require(attrs!=INVALID_FILE_ATTRIBUTES && !(attrs&(FILE_ATTRIBUTE_REPARSE_POINT|FILE_ATTRIBUTE_DIRECTORY)),L"Invalid recovery entry");
        NativeHandle entry;entry.value=CreateFileW(destination.c_str(),GENERIC_READ,FILE_SHARE_READ,nullptr,OPEN_EXISTING,FILE_FLAG_OPEN_REPARSE_POINT,nullptr);
        BY_HANDLE_FILE_INFORMATION details{};Require(entry.value!=INVALID_HANDLE_VALUE && GetFileInformationByHandle(entry.value,&details) && details.nNumberOfLinks==1,L"Recovery entry must be a single ordinary file");
        CloseHandle(entry.value);entry.value=INVALID_HANDLE_VALUE;
        if(FileHash(destination)!=FileHash(source))Require(CopyFileW(source.c_str(),destination.c_str(),FALSE)!=FALSE,L"Cannot update recovery installer");
    }else Require(CopyFileW(source.c_str(),destination.c_str(),TRUE)!=FALSE,L"Cannot save recovery installer");
    SetAdministratorSecretSecurity(destination);
    SetDirectoryDacl(destination,L"D:P(A;;FA;;;SY)(A;;FA;;;BA)(A;;GRGX;;;BU)");
    Require(FileHash(destination)==FileHash(source),L"Recovery installer readback failed");
}
static std::filesystem::path InstallDirectory() {
    PWSTR folder = nullptr;
    Require(SUCCEEDED(SHGetKnownFolderPath(FOLDERID_ProgramFilesX64, 0, nullptr, &folder)), L"Cannot resolve Program Files");
    std::filesystem::path path(folder); CoTaskMemFree(folder); return path / BandKeyDirectoryName;
}
static void CheckOrdinaryDirectory(const std::filesystem::path& path) {
    auto attributes = GetFileAttributesW(path.c_str());
    Require(attributes != INVALID_FILE_ATTRIBUTES && (attributes & FILE_ATTRIBUTE_DIRECTORY) && !(attributes & FILE_ATTRIBUTE_REPARSE_POINT), L"Install directory is missing or is a reparse point");
}
static bool IsAdmin() {
    SID_IDENTIFIER_AUTHORITY authority = SECURITY_NT_AUTHORITY; PSID administrators = nullptr; BOOL member = FALSE;
    if (!AllocateAndInitializeSid(&authority, 2, SECURITY_BUILTIN_DOMAIN_RID, DOMAIN_ALIAS_RID_ADMINS, 0, 0, 0, 0, 0, 0, &administrators)) return false;
    CheckTokenMembership(nullptr, administrators, &member); FreeSid(administrators); return member != FALSE;
}
struct NetBuffer {
    LPBYTE value = nullptr;
    ~NetBuffer() { if (value) NetApiBufferFree(value); }
};
static std::wstring CurrentVmSid(std::wstring* qualifiedName = nullptr, VmAccountKind* accountKind = nullptr) {
    DWORD session = 0; Require(ProcessIdToSessionId(GetCurrentProcessId(), &session) && session, L"Interactive VM session required");
    PWSTR userText = nullptr, domainText = nullptr; DWORD bytes = 0;
    Require(WTSQuerySessionInformationW(WTS_CURRENT_SERVER_HANDLE, session, WTSUserName, &userText, &bytes) != FALSE, L"Cannot inspect interactive user");
    std::wstring user(userText ? userText : L""); WTSFreeMemory(userText);
    Require(WTSQuerySessionInformationW(WTS_CURRENT_SERVER_HANDLE, session, WTSDomainName, &domainText, &bytes) != FALSE, L"Cannot inspect interactive account type");
    std::wstring domain(domainText ? domainText : L""); WTSFreeMemory(domainText);
    wchar_t computer[256] = {}; DWORD chars = ARRAYSIZE(computer);
    Require(GetComputerNameW(computer, &chars) && !user.empty() && (_wcsicmp(domain.c_str(), computer) == 0 || _wcsicmp(domain.c_str(), L"MicrosoftAccount") == 0), L"Use a local or Microsoft-connected VM account; domain accounts are not supported");
    NetBuffer internetBuffer, accountBuffer;
    auto code = NetUserGetInfo(nullptr, user.c_str(), 24, &internetBuffer.value);
    auto internet = reinterpret_cast<USER_INFO_24*>(internetBuffer.value);
    if (code != NERR_Success || !internet) throw SetupFailure{L"Cannot confirm VM account identity", code};
    code = NetUserGetInfo(nullptr, user.c_str(), 23, &accountBuffer.value);
    auto account = reinterpret_cast<USER_INFO_23*>(accountBuffer.value);
    if (code != NERR_Success || !account || !account->usri23_user_sid || !IsValidSid(account->usri23_user_sid)) throw SetupFailure{L"Cannot resolve VM account SID", code};
    VmAccountKind kind = VmAccountKind::Local; std::wstring qualified = domain + L"\\" + user;
    if (internet->usri24_internet_identity) {
        if (!(internet->usri24_flags == 0 && internet->usri24_internet_provider_name && internet->usri24_internet_principal_name &&
            _wcsicmp(internet->usri24_internet_provider_name, L"MicrosoftAccount") == 0 && internet->usri24_user_sid && IsValidSid(internet->usri24_user_sid) &&
            EqualSid(internet->usri24_user_sid, account->usri23_user_sid))) throw SetupFailure{L"Only a SID-matched MicrosoftAccount identity is supported", ERROR_INVALID_DATA};
        kind = VmAccountKind::MicrosoftAccount; qualified = L"MicrosoftAccount\\" + std::wstring(internet->usri24_internet_principal_name);
    } // Other USER_INFO_24 fields are undefined for a non-Internet account.
    Require(ValidQualifiedAccount(qualified, kind), L"The VM account identity is unsupported or malformed");
    NativeHandle token;
    Require(OpenProcessToken(GetCurrentProcess(), TOKEN_QUERY, &token.value) != FALSE, L"Cannot inspect setup identity", GetLastError());
    bytes = 0; GetTokenInformation(token.value, TokenUser, nullptr, 0, &bytes);
    std::vector<BYTE> tokenData(bytes);
    bool read = bytes && GetTokenInformation(token.value, TokenUser, tokenData.data(), bytes, &bytes);
    bool matches = read && EqualSid(reinterpret_cast<TOKEN_USER*>(tokenData.data())->User.Sid, account->usri23_user_sid);
    PWSTR sidText = nullptr;
    bool converted = matches && ConvertSidToStringSidW(account->usri23_user_sid, &sidText);
    if (!converted || !sidText) throw SetupFailure{L"Run setup as the same account used on the VM desktop", ERROR_INVALID_DATA};
    std::wstring sid(sidText); LocalFree(sidText); if (qualifiedName) *qualifiedName = qualified; if (accountKind) *accountKind = kind; return sid;
}
static void EnrollVault(const std::filesystem::path& directory, const std::wstring& sid, const std::wstring& account, std::vector<BYTE>& publicKey, bool phone) {
    wchar_t user[513]{}; NativeRequire(SUCCEEDED(StringCchCopyW(user, ARRAYSIZE(user), account.c_str())), "account-name-too-long");
    SecretBuffer<257> password; BOOL save = FALSE;
    CREDUI_INFOW info{}; info.cbSize = sizeof(info); info.pszCaptionText = BandKeyHostPilot?L"BandKey host encrypted credential enrollment":L"BandKey VM encrypted credential enrollment";
    info.pszMessageText = BandKeyHostPilot?L"Enter this Windows account's actual password, not its Hello PIN. Windows must accept it and return the exact desktop SID. It is stored only as machine-DPAPI ciphertext in a SYSTEM/administrators vault. System password and Hello providers are kept. Never send this password through chat or commands.":L"Enter the password for the displayed VM account, not its Windows Hello PIN. Windows must verify it and return this exact account SID before anything is saved. The VM-only vault is protected for SYSTEM/administrators. Use only the account you explicitly selected for this isolated VM test; do not send its password through chat or commands.";
    DWORD result = CredUIPromptForCredentialsW(&info, L"BandKey VM lab", nullptr, 0, user, ARRAYSIZE(user), password.data.data(), static_cast<ULONG>(password.data.size()), &save,
        CREDUI_FLAGS_GENERIC_CREDENTIALS | CREDUI_FLAGS_ALWAYS_SHOW_UI | CREDUI_FLAGS_DO_NOT_PERSIST | CREDUI_FLAGS_KEEP_USERNAME);
    Require(result == NO_ERROR, L"Enrollment cancelled or unavailable; no credential was saved", result);
    Require(password.data[0] && _wcsicmp(user, account.c_str()) == 0, L"The VM account and a nonempty password are required");
    auto slash = account.find(L'\\'); NativeRequire(slash != std::wstring::npos, "vm-account-invalid");
    NativeHandle token; BOOL valid = LogonUserW(account.substr(slash + 1).c_str(), account.substr(0, slash).c_str(), password.data.data(), LOGON32_LOGON_INTERACTIVE, LOGON32_PROVIDER_DEFAULT, &token.value);
    Require(valid != FALSE, L"Windows rejected enrollment password; no credential was saved", valid ? ERROR_SUCCESS : GetLastError());
    DWORD bytes = 0; GetTokenInformation(token.value, TokenUser, nullptr, 0, &bytes); std::vector<BYTE> tokenData(bytes);
    NativeRequire(bytes && GetTokenInformation(token.value, TokenUser, tokenData.data(), bytes, &bytes), "enrollment-token-unavailable");
    PSID expected = nullptr; NativeRequire(ConvertStringSidToSidW(sid.c_str(), &expected) != FALSE, "enrollment-sid-invalid");
    bool same = EqualSid(expected, reinterpret_cast<TOKEN_USER*>(tokenData.data())->User.Sid) != FALSE; LocalFree(expected); NativeRequire(same, "enrollment-account-mismatch");
    auto vault = directory / L"Vault"; auto exchange = directory / L"Exchange";
    auto diagnostics = directory / L"Diagnostics";
    NativeRequire(CreateDirectoryW(vault.c_str(), nullptr) && CreateDirectoryW(exchange.c_str(), nullptr) && CreateDirectoryW(diagnostics.c_str(), nullptr), "experiment-directories-unavailable");
    SetAdministratorSecretSecurity(vault);
    SetDirectoryDacl(exchange, L"D:P(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)(A;OICI;0x1301bf;;;" + sid + L")");
    SetDirectoryDacl(diagnostics, L"D:P(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)(A;OICI;GR;;;" + sid + L")");
    ProtectedBytes encrypted;
    EncryptSecret(reinterpret_cast<BYTE*>(password.data.data()), static_cast<DWORD>((wcslen(password.data.data()) + 1) * sizeof(wchar_t)), sid, true, encrypted); password.Clear();
    auto credential = vault / L"credential.dpapi"; WriteNewFile(credential, encrypted.blob.pbData, encrypted.blob.cbData); SetAdministratorSecretSecurity(credential);
    if (phone) return; // Public key already validated. No local simulation private key is created.
    SigningKey key; key.Generate(); publicKey = key.Export(false);
    auto privateKey = key.Export(true);
    ProtectedBytes protectedKey;
    try { EncryptSecret(privateKey.data(), static_cast<DWORD>(privateKey.size()), sid, false, protectedKey); }
    catch (...) { SecureZeroMemory(privateKey.data(), privateKey.size()); throw; }
    SecureZeroMemory(privateKey.data(), privateKey.size());
    WriteNewFile(exchange / L"signer.dpapi", protectedKey.blob.pbData, protectedKey.blob.cbData);
}
static void RemoveExperimentFiles(const std::filesystem::path& directory, LabCleanupResult& result) {
    auto vault = directory / L"Vault"; auto exchange = directory / L"Exchange";
    NativeHandle vaultGuard;
    if (CheckCleanupDirectory(vault, result, &vaultGuard)) {
        result.File(vault / L"credential.dpapi");
        CloseHandle(vaultGuard.value); vaultGuard.value = INVALID_HANDLE_VALUE;
        result.Directory(vault);
    }
    NativeHandle exchangeGuard;
    if (CheckCleanupDirectory(exchange, result, &exchangeGuard)) {
        // No recursion: only known experiment message names. Unknown leftovers stay visible.
        WIN32_FIND_DATAW entry{}; HANDLE find = FindFirstFileW((exchange / L"*").c_str(), &entry);
        if (find != INVALID_HANDLE_VALUE) {
            do { if (!(entry.dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY) && IsLabExchangeFile(entry.cFileName)) result.File(exchange / entry.cFileName); } while (FindNextFileW(find, &entry));
            FindClose(find);
        } else {
            auto error = GetLastError(); if (error != ERROR_FILE_NOT_FOUND) result.Record(exchange, error);
        }
        CloseHandle(exchangeGuard.value); exchangeGuard.value = INVALID_HANDLE_VALUE;
        result.Directory(exchange);
    }
    auto diagnostics = directory / L"Diagnostics"; NativeHandle diagnosticsGuard;
    if (CheckCleanupDirectory(diagnostics, result, &diagnosticsGuard)) {
        result.File(diagnostics / L"provider.log");
        CloseHandle(diagnosticsGuard.value); diagnosticsGuard.value = INVALID_HANDLE_VALUE;
        result.Directory(diagnostics);
    }
}
static std::string FileHash(const std::filesystem::path& path) {
    BCRYPT_ALG_HANDLE algorithm = nullptr; BCRYPT_HASH_HANDLE hash = nullptr;
    Require(BCryptOpenAlgorithmProvider(&algorithm, BCRYPT_SHA256_ALGORITHM, nullptr, 0) >= 0, L"SHA256 unavailable");
    if (BCryptCreateHash(algorithm, &hash, nullptr, 0, nullptr, 0, 0) < 0) { BCryptCloseAlgorithmProvider(algorithm, 0); throw SetupFailure{L"SHA256 initialization failed", ERROR_INVALID_DATA}; }
    bool success = true;
    std::ifstream stream(path, std::ios::binary); std::vector<char> buffer(65536);
    if (!stream) success = false;
    while (success && stream) {
        stream.read(buffer.data(), buffer.size()); auto length = stream.gcount();
        if (length && BCryptHashData(hash, reinterpret_cast<PUCHAR>(buffer.data()), static_cast<ULONG>(length), 0) < 0) success = false;
    }
    BYTE digest[32] = {}; success = success && stream.eof() && BCryptFinishHash(hash, digest, sizeof(digest), 0) >= 0;
    BCryptDestroyHash(hash); BCryptCloseAlgorithmProvider(algorithm, 0);
    Require(success, L"Cannot read or hash provider DLL");
    constexpr char hex[] = "0123456789abcdef"; std::string text;
    for (BYTE b : digest) { text += hex[b >> 4]; text += hex[b & 15]; } return text;
}
static void ProtectDirectory(const std::filesystem::path& path) {
    PSECURITY_DESCRIPTOR descriptor = nullptr;
    Require(ConvertStringSecurityDescriptorToSecurityDescriptorW(L"D:P(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)(A;OICI;GRGX;;;BU)", SDDL_REVISION_1, &descriptor, nullptr) != FALSE, L"Cannot build directory ACL");
    PACL acl = nullptr; BOOL present = FALSE, defaulted = FALSE;
    GetSecurityDescriptorDacl(descriptor, &present, &acl, &defaulted);
    auto code = SetNamedSecurityInfoW(const_cast<PWSTR>(path.c_str()), SE_FILE_OBJECT, DACL_SECURITY_INFORMATION | PROTECTED_DACL_SECURITY_INFORMATION, nullptr, nullptr, acl, nullptr);
    LocalFree(descriptor); Require(code == ERROR_SUCCESS, L"Cannot protect lab directory", code);
}
static std::vector<BYTE> ReadPhonePublicKey() {
    std::vector<wchar_t> path(32768); OPENFILENAMEW picker{}; picker.lStructSize = sizeof(picker); picker.lpstrFile = path.data(); picker.nMaxFile = static_cast<DWORD>(path.size());
    const auto initial=ExecutablePath().parent_path().wstring();picker.lpstrInitialDir=initial.c_str();
    picker.lpstrTitle = BandKeyHostPilot?L"Choose the enrolled phone public key (72 bytes)":L"Choose the phone VM-lab public key (72 bytes)";
    picker.lpstrFilter = L"BandKey public key (*.bandkeypub)\0*.bandkeypub\0\0"; picker.Flags = OFN_FILEMUSTEXIST | OFN_PATHMUSTEXIST | OFN_NOCHANGEDIR;
    Require(GetOpenFileNameW(&picker) != FALSE, L"Phone public key selection cancelled");
    auto bytes = ReadBoundedFile(path.data(), 72); SigningKey key; key.Import(bytes.data(), static_cast<DWORD>(bytes.size()), false);
    BYTE digest[32]{}; DWORD digestSize = sizeof(digest);
    NativeRequire(CryptHashCertificate2(BCRYPT_SHA256_ALGORITHM, 0, nullptr, bytes.data(), static_cast<DWORD>(bytes.size()), digest, &digestSize) && digestSize == sizeof(digest), "phone-key-fingerprint-failed");
    constexpr wchar_t hex[] = L"0123456789abcdef"; std::wstring wide;
    for (BYTE value : digest) { wide += hex[value >> 4]; wide += hex[value & 15]; }
    auto message = (BandKeyHostPilot?L"Compare this phone public-key SHA-256 with BandKey on the phone:\n\n":L"Compare this full native-public-key SHA-256 with the phone VM lab screen:\n\n") + wide + (BandKeyHostPilot?L"\n\nThis key will authorize this computer's account after an explicit real-band confirmation. Continue only if the fingerprints match.":L"\n\nThis is a phone TEST key for this isolated VM. The key alone does not prove a band button action. Continue only if the fingerprints match.");
    Require(MessageBoxW(nullptr, message.c_str(), BandKeyProviderLabel, MB_OKCANCEL | MB_ICONWARNING) == IDOK, L"Phone key enrollment cancelled");
    return bytes;
}
static void Install(bool phone, bool microsoftOnly = false) {
    Require(IsSupportedVmwareGuest(),L"Installation requires the exact supported deployment environment");
#ifdef BANDKEY_HOST_PILOT
    Require(phone && microsoftOnly,L"Host pilot accepts only an enrolled phone and the Microsoft-account path");
    Require(KeyExists(L"SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Authentication\\Credential Providers\\{60B78E88-EAD8-445C-9CFD-0B87F74EA6CD}"),L"The original Windows password provider must remain installed");
#endif
    std::wstring account; VmAccountKind accountKind = VmAccountKind::Local; auto sid = CurrentVmSid(&account, &accountKind); auto directory = InstallDirectory(); auto destination = directory / BandKeyProviderFile;
    Require(!microsoftOnly || accountKind == VmAccountKind::MicrosoftAccount, L"Sign into the Microsoft-connected Windows user inside this VM first. This option refuses local accounts; no installation was performed.");
    auto source = ExecutablePath().parent_path() / destination.filename();
    Require(FileHash(source) == BandKeyExpectedProviderHash, L"Provider DLL hash does not match this setup build");
    Require(!KeyExists(BandKeyLabConfig) && !KeyExists(BandKeyLabCom) && !KeyExists(BandKeyLabRegistration), L"Existing lab registration found; remove it first", ERROR_ALREADY_EXISTS);
    std::vector<BYTE> publicKey; if (phone) publicKey = ReadPhonePublicKey();
    BOOL created = CreateDirectoryW(directory.c_str(), nullptr);
    Require(created != FALSE, L"Lab folder already exists or cannot be created; remove the old lab first", created ? ERROR_SUCCESS : GetLastError());
    bool configCreated = false, comCreated = false, registrationCreated = false, fileCopied = false;
    try {
        CheckOrdinaryDirectory(directory); ProtectDirectory(directory);
        WriteDeploymentProgress(directory, DeploymentPhase::DirectoryPrepared);
        BOOL copied = CopyFileW(source.c_str(), destination.c_str(), TRUE);
        Require(copied != FALSE, L"Cannot copy provider DLL", copied ? ERROR_SUCCESS : GetLastError()); fileCopied = true;
        auto attributeError = PrepareCopiedLabFile(destination);
        Require(attributeError == ERROR_SUCCESS, L"Cannot normalize the copied DLL attributes", attributeError);
        Require(FileHash(destination) == BandKeyExpectedProviderHash, L"Copied DLL hash mismatch");
        WriteDeploymentProgress(directory, DeploymentPhase::PayloadVerified);
        EnrollVault(directory, sid, account, publicKey, phone);
        WriteDeploymentProgress(directory, DeploymentPhase::CredentialEnrolled);
        Key config; NewKey(BandKeyLabConfig, config); configCreated = true;
        EnabledValue(config.value, 0); TextValue(config.value, L"TargetSid", sid);
#ifdef BANDKEY_HOST_PILOT
        TextValue(config.value,L"DeploymentProfile",L"host-pilot-v1");
        TextValue(config.value,L"HostBinding",BandKeyHostExpectedMachine);
        TextValue(config.value,L"TargetBuild",BandKeyHostExpectedBuild);
#endif
        TextValue(config.value, L"QualifiedAccount", account);
        DWORD accountType = static_cast<DWORD>(accountKind);
        auto accountSetting = RegSetValueExW(config.value, L"AccountKind", 0, REG_DWORD, reinterpret_cast<const BYTE*>(&accountType), sizeof(accountType));
        Require(accountSetting == ERROR_SUCCESS, L"Cannot bind VM account type", accountSetting);
        DWORD mode = 2;
        DWORD protocol = 3;
        auto protocolSetting = RegSetValueExW(config.value, L"ProtocolVersion", 0, REG_DWORD, reinterpret_cast<const BYTE*>(&protocol), sizeof(protocol));
        Require(protocolSetting == ERROR_SUCCESS, L"Cannot bind request protocol", protocolSetting);
        auto setting = RegSetValueExW(config.value, L"Mode", 0, REG_DWORD, reinterpret_cast<const BYTE*>(&mode), sizeof(mode));
        Require(setting == ERROR_SUCCESS, L"Cannot set experiment mode", setting);
        DWORD kind = phone ? 1u : 0u;
        setting = RegSetValueExW(config.value, L"SignerKind", 0, REG_DWORD, reinterpret_cast<const BYTE*>(&kind), sizeof(kind));
        Require(setting == ERROR_SUCCESS, L"Cannot bind signer kind", setting);
        setting = RegSetValueExW(config.value, L"LabPublicKey", 0, REG_BINARY, publicKey.data(), static_cast<DWORD>(publicKey.size()));
        Require(setting == ERROR_SUCCESS, L"Cannot pin VM simulation key", setting);
        WriteDeploymentProgress(directory, DeploymentPhase::PolicyWritten);
        Key com; NewKey(BandKeyLabCom, com); comCreated = true; TextValue(com.value, nullptr, BandKeyProviderLabel);
        Key server; DWORD disposition = 0;
        auto code = RegCreateKeyExW(com.value, L"InprocServer32", 0, nullptr, 0, KEY_WRITE | KEY_WOW64_64KEY, nullptr, &server.value, &disposition);
        Require(code == ERROR_SUCCESS, L"Cannot register lab COM server", code);
        TextValue(server.value, nullptr, destination.wstring()); TextValue(server.value, L"ThreadingModel", L"Apartment");
        WriteDeploymentProgress(directory, DeploymentPhase::ComRegistered);
        Key registration; NewKey(BandKeyLabRegistration, registration); registrationCreated = true;
        TextValue(registration.value, nullptr, BandKeyProviderLabel);
        WriteDeploymentProgress(directory, DeploymentPhase::ReadyToActivate);
        if(BandKeyHostPilot)PrepareHostRecovery();
        EnabledValue(config.value, 1); // Last operation; the journal is not activation authority.
    } catch (...) {
        const auto original = std::current_exception();
        const auto revoke = RevokeOwnedRegistration({registrationCreated, configCreated, comCreated});
        if (!revoke.Complete()) {
            RecordRecoveryRequired(directory, revoke.firstError);
            throw SetupFailure{L"Installation failed and registration rollback is incomplete. Run this setup's Remove before trying again; system sign-in options remain available.", revoke.firstError};
        }
        LabCleanupResult cleanup; RemoveExperimentFiles(directory, cleanup);
        if (fileCopied) cleanup.File(destination);
        if (cleanup.complete) { cleanup.File(directory / DeploymentTempName); cleanup.File(directory / DeploymentStateName); }
        if (cleanup.complete) cleanup.Directory(directory);
        if (!cleanup.complete) {
            const auto error = cleanup.issues.front().error; RecordRecoveryRequired(directory, error);
            throw SetupFailure{L"Installation failed. Registration was revoked, but file cleanup is incomplete; run this setup's Remove.", error};
        }
        std::rethrow_exception(original);
    }
}
static LabCleanupResult Remove() {
    // Only this lab's exact registry subtrees, fixed files and known message names.
    const auto revoke = RevokeOwnedRegistration({true, true, true});
    if (!revoke.Complete()) { try { RecordRecoveryRequired(InstallDirectory(), revoke.firstError); } catch (...) {} }
    Require(revoke.Complete(), L"Could not fully revoke the lab registration. Retry Remove or restore the VM baseline snapshot.", revoke.firstError);
    auto directory = InstallDirectory();
    LabCleanupResult result;
    NativeHandle directoryGuard;
    if (!CheckCleanupDirectory(directory, result, &directoryGuard)) return result;
    try { WriteDeploymentProgress(directory, DeploymentPhase::Removing); } catch (...) {}
    RemoveExperimentFiles(directory, result);
    result.File(directory / L"provider-update.tmp");
    result.File(directory / L"provider-previous.dll");
    result.File(directory / BandKeyProviderFile);
    if (result.complete) { result.File(directory / DeploymentTempName); result.File(directory / DeploymentStateName); }
    CloseHandle(directoryGuard.value); directoryGuard.value = INVALID_HANDLE_VALUE;
    if (result.complete) result.Directory(directory);
    if (!result.complete) RecordRecoveryRequired(directory, result.issues.front().error);
    return result;
}
// Atomic, account-preserving payload update. Never re-enrolls or reads the vault.
static void UpdateHostProvider(bool rollback) {
    Require(BandKeyHostPilot && IsSupportedVmwareGuest(), L"This update requires the original supported host");
    const auto policy = ReadLabPolicy();
    Require(policy.enabled && policy.mode == 2 && policy.signerKind == 1 && policy.sid == CurrentVmSid(), L"The active host enrollment must belong to this Windows user");
    const auto directory = InstallDirectory(), destination = directory / BandKeyProviderFile;
    CheckNoReparseDirectory(directory); CheckOrdinaryDirectory(directory);
    Require(ReadMachineString((std::wstring(BandKeyLabCom) + L"\\InprocServer32").c_str(), nullptr) == destination.wstring(), L"Unexpected host COM path; no update performed");
    const auto previous = directory / L"provider-previous.dll", staged = directory / L"provider-update.tmp";
    if (rollback) {
        const auto code = ReplaceBandKeyPayload(destination, previous, staged);
        Require(code == ERROR_SUCCESS, L"Cannot restore previous provider; retry after a normal restart", code);
        RemoveOrdinaryLabFile(staged);
        return;
    }
    const auto source = ExecutablePath().parent_path() / BandKeyProviderFile;
    Require(FileHash(source) == BandKeyExpectedProviderHash, L"Update payload hash mismatch");
    if (FileHash(destination) == BandKeyExpectedProviderHash) return;
    Require(GetFileAttributesW(previous.c_str()) == INVALID_FILE_ATTRIBUTES && GetFileAttributesW(staged.c_str()) == INVALID_FILE_ATTRIBUTES, L"A previous update already exists; resolve or roll it back first");
    const BOOL copied = CopyFileW(source.c_str(), staged.c_str(), TRUE);
    Require(copied != FALSE, L"Cannot stage provider update", copied ? ERROR_SUCCESS : GetLastError());
    try {
        Require(PrepareCopiedLabFile(staged) == ERROR_SUCCESS && FileHash(staged) == BandKeyExpectedProviderHash, L"Staged provider verification failed");
        PrepareHostRecovery();
        const auto code = ReplaceBandKeyPayload(destination, staged, previous);
        Require(code == ERROR_SUCCESS, L"Provider is in use or cannot be replaced. No sign-out or reboot was performed; retry after a normal restart.", code);
    } catch (...) { RemoveOrdinaryLabFile(staged); throw; }
    Require(FileHash(destination) == BandKeyExpectedProviderHash, L"Updated provider readback failed; use --rollback-update");
}
static std::wstring RemovalMessage(const LabCleanupResult& result) {
    if (result.complete) return L"Lab registration and files removed. System providers were preserved.";
    std::wstring text = L"Lab registration removed; some files remain. System providers were preserved.\n";
    for (const auto& issue : result.issues) {
        text += L"\n" + issue.path.wstring() + L"\nWindows error: " + std::to_wstring(issue.error);
        if (issue.error == ERROR_SHARING_VIOLATION || issue.error == ERROR_LOCK_VIOLATION) text += L" (file in use: close the confirmation client; restart the VM if necessary)";
        else if (issue.error == ERROR_ACCESS_DENIED) text += L" (access denied; do not change unrelated system permissions)";
        else if (issue.error == ERROR_DIR_NOT_EMPTY) text += L" (directory contains remaining files; no recursive deletion was performed)";
        else if (issue.error == ERROR_TOO_MANY_LINKS) text += L" (hard-linked file refused)";
    }
    text += L"\n\nUse this updated setup to retry. If it still fails, report this message; do not repeatedly restart without a sharing/lock error.";
    return text;
}
static bool WriteDeploymentStatus(const std::filesystem::path& output) {
    const bool guest = IsSupportedVmwareGuest();
    const bool provider = KeyExists(BandKeyLabRegistration);
    bool policy = false, com = false, directoryExists = false, journalReadable = true, payloadMatched = false;
    int enabled = -1; DWORD phase = 0, progressError = 0;
    if (guest) {
        policy = KeyExists(BandKeyLabConfig); com = KeyExists(BandKeyLabCom);
        DWORD value = 0, bytes = sizeof(value);
        const auto code = RegGetValueW(HKEY_LOCAL_MACHINE, BandKeyLabConfig, L"Enabled",
            RRF_RT_REG_DWORD | RRF_SUBKEY_WOW6464KEY, nullptr, &value, &bytes);
        if (code == ERROR_SUCCESS && value <= 1) enabled = static_cast<int>(value);
        else if (!policy && MissingDeploymentKeyIsSuccess(code) == ERROR_SUCCESS) enabled = 0;
        const auto directory = InstallDirectory();
        const auto attributes = GetFileAttributesW(directory.c_str());
        directoryExists = attributes != INVALID_FILE_ATTRIBUTES;
        if (directoryExists) {
            try {
                CheckNoReparseDirectory(directory);
                auto progress = ReadDeploymentProgress(directory);
                if (progress) { phase = progress->phase; progressError = progress->error; }
            } catch (...) { journalReadable = false; }
            try { CheckNoReparseDirectory(directory); payloadMatched = FileHash(directory / BandKeyProviderFile) == BandKeyExpectedProviderHash; }
            catch (...) { payloadMatched = false; }
        }
    }
    const bool healthy = provider && policy && com && enabled == 1 && directoryExists && payloadMatched;
    const bool anything = provider || policy || com || directoryExists;
    const bool recovery = guest && ((!healthy && anything) || !journalReadable ||
        (phase != 0 && phase != static_cast<DWORD>(DeploymentPhase::ReadyToActivate)));
    std::ofstream report(output);
    report << "{\"schema\":\"bandkey.setup-preflight.v2\",\"vmGuardAccepted\":" << (BandKeyHostPilot ? "false" : guest ? "true" : "false")
        << ",\"environmentAccepted\":" << (guest?"true":"false") << ",\"profile\":\"" << (BandKeyHostPilot?"host-pilot":"vm-lab") << "\""
        << ",\"mutationPerformed\":false,\"supportedBuild\":" << (BandKeyHostPilot?std::stoul(ReadMachineString(L"SOFTWARE\\Microsoft\\Windows NT\\CurrentVersion",L"CurrentBuildNumber")):26300) << ",\"providerRegistered\":" << (provider ? "true" : "false")
        << ",\"policyPresent\":" << (policy ? "true" : "false") << ",\"comPresent\":" << (com ? "true" : "false")
        << ",\"policyEnabled\":" << (enabled < 0 ? "null" : enabled ? "true" : "false")
        << ",\"journalInspected\":" << (guest ? "true" : "false") << ",\"journalReadable\":" << (journalReadable ? "true" : "false")
        << ",\"deploymentRecoveryVersion\":1,\"deploymentPhase\":" << phase << ",\"deploymentError\":" << progressError
        << ",\"payloadHashMatched\":" << (payloadMatched ? "true" : "false")
        << ",\"recoveryRequired\":" << (recovery ? "true" : "false") << ",\"windowsAuthenticationSubmitted\":false}";
    report.close(); return report.good();
}
int WINAPI wWinMain(_In_ HINSTANCE, _In_opt_ HINSTANCE, _In_ PWSTR, _In_ int) {
    int argc = 0; PWSTR* args = CommandLineToArgvW(GetCommandLineW(), &argc);
    if (!args) return 2;
    std::vector<std::wstring> arguments; for (int i = 0; i < argc; i++) arguments.emplace_back(args[i]); LocalFree(args);
    if (argc == 3 && arguments[1] == L"--check") {
        try {
            return WriteDeploymentStatus(arguments[2]) ? 0 : 1;
        } catch (...) { return 1; }
    }
    try {
        Require(BandKeyRemovalEnvironment() && !GetSystemMetrics(SM_REMOTESESSION), BandKeyHostPilot?L"This host-pilot build is bound to its original local computer. No changes were made.":L"This laboratory setup only supports the local VMware Windows 11 build 26300 guest. No changes were made.");
        std::wstring operation;
        if (argc == 2 && (arguments[1] == L"--install" || arguments[1] == L"--install-phone" || arguments[1] == L"--install-phone-msa" || arguments[1] == L"--remove" || arguments[1] == L"--update" || arguments[1] == L"--rollback-update")) operation = arguments[1];
        else {
            Require(argc == 1, L"Unsupported setup arguments");
#ifdef BANDKEY_HOST_PILOT
            const TASKDIALOG_BUTTON buttons[]={{104,L"Install BandKey host pilot\nEnroll this Microsoft account and the phone public key; keep Windows sign-in options"},{102,L"Remove BandKey host pilot\nRevoke only BandKey host registration and its protected files"}};
#else
            const TASKDIALOG_BUTTON buttons[] = {{101, L"Install local simulation experiment\nUse the existing Arm confirmation client"}, {103, L"Install phone-confirmation VM experiment\nImport a phone-lab public key; no local signing key will be created"}, {104, L"Install Microsoft-account phone test\nRequire a Microsoft-connected VM user; refuse local accounts"}, {102, L"Remove VM experiment\nRemove the lab vault, simulation key and provider; keep system sign-in options"}};
#endif
            TASKDIALOGCONFIG dialog = {}; dialog.cbSize = sizeof(dialog); dialog.dwFlags = TDF_USE_COMMAND_LINKS | TDF_ALLOW_DIALOG_CANCELLATION;
            dialog.dwCommonButtons = TDCBF_CANCEL_BUTTON; dialog.pszWindowTitle = BandKeyProviderLabel;
            dialog.pszMainInstruction = BandKeyHostPilot?L"Account-bound Windows host pilot":L"VM-only account authentication and phone confirmation";
            dialog.pszContent = BandKeyHostPilot?L"Preserve system password and Hello sign-in. Only this computer/build, its current Microsoft account and an enrolled phone key are accepted. Windows verifies the password locally before storing machine-DPAPI ciphertext. No simulation signer, provider filter, automatic reboot or automatic lock is installed. Use the supplied host Remove entry to revoke BandKey.":L"Keep a baseline snapshot and working system sign-in option. Enrollment verifies the selected local or Microsoft-connected VM account password and encrypts it for SYSTEM/administrators. Choose a local simulation or separately enrolled phone TEST key; neither proves a real band action. Setup does not lock, sign out or reboot. Administrator approval is requested after choosing an action.";
            dialog.cButtons = ARRAYSIZE(buttons); dialog.pButtons = buttons; int choice = IDCANCEL;
            Require(SUCCEEDED(TaskDialogIndirect(&dialog, &choice, nullptr, nullptr)), L"Cannot open setup dialog");
            if (choice != 101 && choice != 102 && choice != 103 && choice != 104) return 0;
            operation = choice == 104 ? L"--install-phone-msa" : choice == 103 ? L"--install-phone" : (choice == 101 ? L"--install" : L"--remove");
            if (!IsAdmin()) {
                auto executable = ExecutablePath(); SHELLEXECUTEINFOW start = {}; start.cbSize = sizeof(start);
                start.lpVerb = L"runas"; start.lpFile = executable.c_str(); start.lpParameters = operation.c_str(); start.nShow = SW_SHOWNORMAL;
                BOOL launched = ShellExecuteExW(&start);
                Require(launched != FALSE, L"Administrator launch was cancelled or failed", launched ? ERROR_SUCCESS : GetLastError()); return 0;
            }
        }
        Require(IsAdmin(), L"Administrator approval is required for this action");
#ifdef BANDKEY_HOST_PILOT
        Require(operation==L"--install-phone-msa" || operation==L"--remove" || operation==L"--update" || operation==L"--rollback-update",L"Unsupported host-pilot operation");
#endif
        SetupOperationLock operationLock(BandKeySetupMutex);
        if (operation == L"--update" || operation == L"--rollback-update") {
            UpdateHostProvider(operation == L"--rollback-update");
            return 0;
        }
        if (operation == L"--install" || operation == L"--install-phone" || operation == L"--install-phone-msa") { bool phone = operation != L"--install"; Install(phone, operation == L"--install-phone-msa"); MessageBoxW(nullptr, BandKeyHostPilot?L"BandKey host pilot installed. Start BandKey.exe --host-bridge, pair this computer with the phone and use real-band confirmation. Original Windows password and Hello providers were preserved.\n\nRecovery installer: C:\\ProgramData\\BandKeyHostPilotRecovery\\BandKey.HostSetup.exe (choose Remove). No automatic lock, sign-out or reboot was performed.":phone ? L"Phone-confirmation VM experiment installed. Start BandKey.exe --vm-bridge from the lab disc and import the same phone public key. No local simulation signer exists; the enrolled phone must sign each new request after explicit confirmation.\n\nAccount compatibility and desktop unlock must be checked in this VM. Keep its working system sign-in option and baseline snapshot." : L"Signed-confirmation VM experiment installed. Start BandKey.VmConfirmation.exe from the lab disc. Arm one confirmation, then manually lock the VM within 10 seconds.\n\nKeep the system password sign-in option. To roll back, run this setup and choose Remove.", BandKeyProviderLabel, MB_OK | MB_ICONINFORMATION); }
        else { auto result = Remove(); auto text = RemovalMessage(result); MessageBoxW(nullptr, text.c_str(), BandKeyProviderLabel, MB_OK | (result.complete ? MB_ICONINFORMATION : MB_ICONWARNING)); if (!result.complete) return 1; }
        return 0;
    } catch (const SetupFailure& error) { auto text = error.text + L"\nWindows error: " + std::to_wstring(error.code); MessageBoxW(nullptr, text.c_str(), BandKeyProviderLabel, MB_OK | MB_ICONERROR); }
    catch (const SetupOperationBusy&) { MessageBoxW(nullptr, L"Another BandKey setup operation is still running. Wait for it to finish or close it, then retry. No competing install or removal was started.", L"BandKey VM lab - setup busy", MB_OK | MB_ICONINFORMATION); }
    catch (const std::exception&) { MessageBoxW(nullptr, L"Setup failed. Use Remove or the VM baseline snapshot for recovery.", L"BandKey VM lab", MB_OK | MB_ICONERROR); }
    return 1;
}
