#pragma once
#include <windows.h>
#include <wincrypt.h>
#include <bcrypt.h>
#include <aclapi.h>
#include <sddl.h>
#include <array>
#include <vector>
#include <string>
#include <filesystem>
#include <stdexcept>

template<size_t N> struct SecretBuffer {
    std::array<wchar_t, N> data{};
    ~SecretBuffer() { Clear(); }
    void Clear() { SecureZeroMemory(data.data(), sizeof(data)); }
    SecretBuffer() = default;
    SecretBuffer(const SecretBuffer&) = delete;
    SecretBuffer& operator=(const SecretBuffer&) = delete;
};
struct ProtectedBytes {
    DATA_BLOB blob{};
    ~ProtectedBytes() { if (blob.pbData) { SecureZeroMemory(blob.pbData, blob.cbData); LocalFree(blob.pbData); } }
    ProtectedBytes() = default;
    ProtectedBytes(const ProtectedBytes&) = delete;
    ProtectedBytes& operator=(const ProtectedBytes&) = delete;
};
inline void NativeRequire(bool condition, const char* operation) { if (!condition) throw std::runtime_error(operation); }
struct NativeHandle {
    HANDLE value = INVALID_HANDLE_VALUE;
    ~NativeHandle() { if (value != INVALID_HANDLE_VALUE && value) CloseHandle(value); }
};
inline void CheckNoReparseDirectory(const std::filesystem::path& path) {
    auto attributes = GetFileAttributesW(path.c_str());
    NativeRequire(attributes != INVALID_FILE_ATTRIBUTES && (attributes & FILE_ATTRIBUTE_DIRECTORY) && !(attributes & FILE_ATTRIBUTE_REPARSE_POINT), "directory-unavailable-or-reparse");
}
inline std::vector<BYTE> ReadBoundedFile(const std::filesystem::path& path, DWORD maximum) {
    NativeHandle file;
    file.value = CreateFileW(path.c_str(), GENERIC_READ, FILE_SHARE_READ, nullptr, OPEN_EXISTING, FILE_FLAG_OPEN_REPARSE_POINT, nullptr);
    NativeRequire(file.value != INVALID_HANDLE_VALUE, "file-unavailable");
    BY_HANDLE_FILE_INFORMATION info{}; LARGE_INTEGER length{};
    NativeRequire(GetFileInformationByHandle(file.value, &info) && !(info.dwFileAttributes & (FILE_ATTRIBUTE_REPARSE_POINT | FILE_ATTRIBUTE_DIRECTORY)) &&
        GetFileSizeEx(file.value, &length) && length.QuadPart > 0 && length.QuadPart <= maximum, "file-shape-invalid");
    std::vector<BYTE> bytes(static_cast<size_t>(length.QuadPart)); DWORD read = 0;
    NativeRequire(ReadFile(file.value, bytes.data(), static_cast<DWORD>(bytes.size()), &read, nullptr) && read == bytes.size(), "file-read-failed"); return bytes;
}
inline void WriteNewFile(const std::filesystem::path& path, const BYTE* bytes, DWORD size) {
    NativeHandle file; file.value = CreateFileW(path.c_str(), GENERIC_WRITE, 0, nullptr, CREATE_NEW, FILE_ATTRIBUTE_NORMAL, nullptr);
    NativeRequire(file.value != INVALID_HANDLE_VALUE, "new-file-unavailable"); DWORD written = 0;
    NativeRequire(WriteFile(file.value, bytes, size, &written, nullptr) && written == size && FlushFileBuffers(file.value), "file-write-failed");
}
inline void SetDirectoryDacl(const std::filesystem::path& directory, const std::wstring& sddl) {
    PSECURITY_DESCRIPTOR descriptor = nullptr;
    NativeRequire(ConvertStringSecurityDescriptorToSecurityDescriptorW(sddl.c_str(), SDDL_REVISION_1, &descriptor, nullptr) != FALSE, "invalid-directory-dacl");
    PACL acl = nullptr; BOOL present = FALSE, defaulted = FALSE;
    BOOL found = GetSecurityDescriptorDacl(descriptor, &present, &acl, &defaulted);
    DWORD error = found && present && acl ? SetNamedSecurityInfoW(const_cast<PWSTR>(directory.c_str()), SE_FILE_OBJECT, DACL_SECURITY_INFORMATION | PROTECTED_DACL_SECURITY_INFORMATION, nullptr, nullptr, acl, nullptr) : ERROR_INVALID_ACL;
    LocalFree(descriptor); NativeRequire(error == ERROR_SUCCESS, "directory-dacl-failed");
}
inline void SetAdministratorSecretSecurity(const std::filesystem::path& path) {
    PSECURITY_DESCRIPTOR descriptor = nullptr;
    NativeRequire(ConvertStringSecurityDescriptorToSecurityDescriptorW(L"O:BAG:BAD:P(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)", SDDL_REVISION_1, &descriptor, nullptr) != FALSE, "secret-descriptor-invalid");
    PSID owner = nullptr; PACL acl = nullptr; BOOL present = FALSE, defaulted = FALSE;
    BOOL valid = GetSecurityDescriptorOwner(descriptor, &owner, &defaulted) && GetSecurityDescriptorDacl(descriptor, &present, &acl, &defaulted);
    DWORD error = valid && owner && present && acl ? SetNamedSecurityInfoW(const_cast<PWSTR>(path.c_str()), SE_FILE_OBJECT, OWNER_SECURITY_INFORMATION | DACL_SECURITY_INFORMATION | PROTECTED_DACL_SECURITY_INFORMATION, owner, nullptr, acl, nullptr) : ERROR_INVALID_ACL;
    LocalFree(descriptor); NativeRequire(error == ERROR_SUCCESS, "secret-permissions-failed");
}
inline bool ProcessIsLocalSystem() {
    NativeHandle token; if (!OpenProcessToken(GetCurrentProcess(), TOKEN_QUERY, &token.value)) return false;
    DWORD size = 0; GetTokenInformation(token.value, TokenUser, nullptr, 0, &size);
    std::vector<BYTE> bytes(size);
    return size && GetTokenInformation(token.value, TokenUser, bytes.data(), size, &size) && IsWellKnownSid(reinterpret_cast<TOKEN_USER*>(bytes.data())->User.Sid, WinLocalSystemSid);
}
inline bool RestrictedToSystemAndAdministrators(PSECURITY_DESCRIPTOR descriptor) {
    PSID owner = nullptr; BOOL defaulted = FALSE, present = FALSE; PACL acl = nullptr;
    BYTE system[SECURITY_MAX_SID_SIZE]{}, administrators[SECURITY_MAX_SID_SIZE]{}; DWORD a = sizeof(system), b = sizeof(administrators);
    if (!CreateWellKnownSid(WinLocalSystemSid, nullptr, system, &a) || !CreateWellKnownSid(WinBuiltinAdministratorsSid, nullptr, administrators, &b) ||
        !GetSecurityDescriptorOwner(descriptor, &owner, &defaulted) || !owner || (!EqualSid(owner, system) && !EqualSid(owner, administrators)) ||
        !GetSecurityDescriptorDacl(descriptor, &present, &acl, &defaulted) || !present || !acl || !acl->AceCount) return false;
    bool allowsSystem = false;
    for (DWORD i = 0; i < acl->AceCount; i++) {
        void* entry = nullptr; if (!GetAce(acl, i, &entry)) return false;
        auto header = static_cast<ACE_HEADER*>(entry);
        if (header->AceType != ACCESS_ALLOWED_ACE_TYPE || header->AceFlags & INHERIT_ONLY_ACE) return false;
        auto allowed = static_cast<ACCESS_ALLOWED_ACE*>(entry); PSID sid = &allowed->SidStart;
        if (!IsValidSid(sid) || (!EqualSid(sid, system) && !EqualSid(sid, administrators))) return false;
        if (EqualSid(sid, system) && (allowed->Mask & (GENERIC_ALL | FILE_ALL_ACCESS)) == FILE_ALL_ACCESS) allowsSystem = true;
    }
    return allowsSystem;
}
inline void CheckSecretFileSecurity(HANDLE file) {
    PSECURITY_DESCRIPTOR descriptor = nullptr;
    auto error = GetSecurityInfo(file, SE_FILE_OBJECT, OWNER_SECURITY_INFORMATION | DACL_SECURITY_INFORMATION, nullptr, nullptr, nullptr, nullptr, &descriptor);
    bool safe = error == ERROR_SUCCESS && descriptor && RestrictedToSystemAndAdministrators(descriptor);
    if (descriptor) LocalFree(descriptor);
    NativeRequire(safe, "vault-file-permissions-invalid");
}
inline void EncryptSecret(const BYTE* data, DWORD size, const std::wstring& sid, bool machine, ProtectedBytes& output) {
    DATA_BLOB input{size, const_cast<BYTE*>(data)};
    DATA_BLOB entropy{static_cast<DWORD>(sid.size() * sizeof(wchar_t)), reinterpret_cast<BYTE*>(const_cast<wchar_t*>(sid.data()))};
    NativeRequire(size && size <= 8192 && CryptProtectData(&input, L"BandKey VM lab", &entropy, nullptr, nullptr,
        CRYPTPROTECT_UI_FORBIDDEN | (machine ? CRYPTPROTECT_LOCAL_MACHINE : 0), &output.blob), "dpapi-protect-failed");
}
inline void DecryptSecret(const std::vector<BYTE>& data, const std::wstring& sid, ProtectedBytes& output) {
    DATA_BLOB input{static_cast<DWORD>(data.size()), const_cast<BYTE*>(data.data())};
    DATA_BLOB entropy{static_cast<DWORD>(sid.size() * sizeof(wchar_t)), reinterpret_cast<BYTE*>(const_cast<wchar_t*>(sid.data()))};
    NativeRequire(!data.empty() && data.size() <= 16384 && CryptUnprotectData(&input, nullptr, &entropy, nullptr, nullptr, CRYPTPROTECT_UI_FORBIDDEN, &output.blob), "dpapi-unprotect-failed");
}
inline void ReadVaultPassword(const std::filesystem::path& directory, const std::wstring& sid, SecretBuffer<257>& password) {
    CheckNoReparseDirectory(directory.parent_path()); CheckNoReparseDirectory(directory);
    NativeHandle file; file.value = CreateFileW((directory / L"credential.dpapi").c_str(), GENERIC_READ | READ_CONTROL, 0, nullptr, OPEN_EXISTING, FILE_FLAG_OPEN_REPARSE_POINT, nullptr);
    NativeRequire(file.value != INVALID_HANDLE_VALUE, "vault-unavailable"); CheckSecretFileSecurity(file.value);
    BY_HANDLE_FILE_INFORMATION info{}; LARGE_INTEGER length{};
    NativeRequire(GetFileInformationByHandle(file.value, &info) && !(info.dwFileAttributes & (FILE_ATTRIBUTE_REPARSE_POINT | FILE_ATTRIBUTE_DIRECTORY)) && GetFileSizeEx(file.value, &length) && length.QuadPart > 0 && length.QuadPart <= 16384, "vault-shape-invalid");
    std::vector<BYTE> encrypted(static_cast<size_t>(length.QuadPart)); DWORD read = 0;
    NativeRequire(ReadFile(file.value, encrypted.data(), static_cast<DWORD>(encrypted.size()), &read, nullptr) && read == encrypted.size(), "vault-read-failed");
    ProtectedBytes secret; DecryptSecret(encrypted, sid, secret);
    NativeRequire(secret.blob.cbData >= 2 * sizeof(wchar_t) && secret.blob.cbData <= sizeof(password.data) && secret.blob.cbData % sizeof(wchar_t) == 0, "vault-content-invalid");
    auto text = reinterpret_cast<wchar_t*>(secret.blob.pbData); size_t chars = secret.blob.cbData / sizeof(wchar_t);
    NativeRequire(text[chars - 1] == 0 && wcsnlen_s(text, chars) == chars - 1, "vault-password-invalid");
    CopyMemory(password.data.data(), text, secret.blob.cbData);
}
