#pragma once
#include "NativeSecurity.h"
#include <atomic>

#ifdef BANDKEY_HOST_PILOT
inline constexpr ULONGLONG WindowsGrantLifetimeMilliseconds = 120000;
#else
inline constexpr ULONGLONG WindowsGrantLifetimeMilliseconds = 30000;
#endif
inline bool WindowsGrantLifetimeValid(ULONGLONG issued, ULONGLONG expires) {
    return expires > issued && (expires - issued == 30000 || expires - issued == 120000);
}

#pragma pack(push, 1)
struct WindowsChallenge {
    BYTE domain[16] = {'B','A','N','D','K','E','Y','-','W','I','N','-','V','1',0,0};
    DWORD session = 0;
    ULONGLONG issuedMs = 0, expiresMs = 0; // FILETIME epoch (1601), milliseconds.
    BYTE nonce[32]{};
    wchar_t sid[184]{};
};
struct WindowsApproval { WindowsChallenge challenge; BYTE signature[64]{}; };
#pragma pack(pop)
static_assert(sizeof(WindowsChallenge) == 436, "Canonical wire format must not change silently");
static_assert(sizeof(WindowsApproval) == 500, "Canonical approval size");
inline ULONGLONG FileTimeMilliseconds() { FILETIME time; GetSystemTimePreciseAsFileTime(&time); ULARGE_INTEGER value; value.LowPart = time.dwLowDateTime; value.HighPart = time.dwHighDateTime; return value.QuadPart / 10000; }
struct SigningKey {
    BCRYPT_ALG_HANDLE algorithm = nullptr; BCRYPT_KEY_HANDLE key = nullptr;
    ~SigningKey() { if (key) BCryptDestroyKey(key); if (algorithm) BCryptCloseAlgorithmProvider(algorithm, 0); }
    SigningKey() = default; SigningKey(const SigningKey&) = delete; SigningKey& operator=(const SigningKey&) = delete;
    void Generate() {
        NativeRequire(BCryptOpenAlgorithmProvider(&algorithm, BCRYPT_ECDSA_P256_ALGORITHM, nullptr, 0) >= 0 && BCryptGenerateKeyPair(algorithm, &key, 256, 0) >= 0 && BCryptFinalizeKeyPair(key, 0) >= 0, "signing-key-generation-failed");
    }
    void Import(const BYTE* blob, DWORD size, bool privateKey) {
        NativeRequire(size == (privateKey ? 104u : 72u), "key-blob-size-invalid");
        const auto* header = reinterpret_cast<const BCRYPT_ECCKEY_BLOB*>(blob);
        NativeRequire(header->cbKey == 32 && header->dwMagic == static_cast<ULONG>(privateKey ? BCRYPT_ECDSA_PRIVATE_P256_MAGIC : BCRYPT_ECDSA_PUBLIC_P256_MAGIC), "key-blob-type-invalid");
        NativeRequire(BCryptOpenAlgorithmProvider(&algorithm, BCRYPT_ECDSA_P256_ALGORITHM, nullptr, 0) >= 0 && BCryptImportKeyPair(algorithm, nullptr, privateKey ? BCRYPT_ECCPRIVATE_BLOB : BCRYPT_ECCPUBLIC_BLOB, &key, const_cast<BYTE*>(blob), size, 0) >= 0, "signing-key-import-failed");
    }
    std::vector<BYTE> Export(bool privateKey) {
        DWORD size = 0;
        NativeRequire(BCryptExportKey(key, nullptr, privateKey ? BCRYPT_ECCPRIVATE_BLOB : BCRYPT_ECCPUBLIC_BLOB, nullptr, 0, &size, 0) >= 0 && size <= 104, "key-export-size-failed");
        std::vector<BYTE> output(size);
        NativeRequire(BCryptExportKey(key, nullptr, privateKey ? BCRYPT_ECCPRIVATE_BLOB : BCRYPT_ECCPUBLIC_BLOB, output.data(), size, &size, 0) >= 0, "key-export-failed"); return output;
    }
};
inline std::array<BYTE, 32> ChallengeHash(const WindowsChallenge& challenge) {
    BCRYPT_ALG_HANDLE algorithm = nullptr; BCRYPT_HASH_HANDLE hash = nullptr; std::array<BYTE, 32> output{};
    NativeRequire(BCryptOpenAlgorithmProvider(&algorithm, BCRYPT_SHA256_ALGORITHM, nullptr, 0) >= 0, "hash-unavailable");
    bool success = BCryptCreateHash(algorithm, &hash, nullptr, 0, nullptr, 0, 0) >= 0;
    if (success) success = BCryptHashData(hash, reinterpret_cast<PUCHAR>(const_cast<WindowsChallenge*>(&challenge)), sizeof(challenge), 0) >= 0 && BCryptFinishHash(hash, output.data(), static_cast<ULONG>(output.size()), 0) >= 0;
    if (hash) BCryptDestroyHash(hash); BCryptCloseAlgorithmProvider(algorithm, 0); NativeRequire(success, "hash-failed"); return output;
}
inline WindowsChallenge NewWindowsChallenge(const std::wstring& sid, DWORD session, ULONGLONG now) {
    WindowsChallenge challenge;
    NativeRequire(session && !sid.empty() && sid.size() < ARRAYSIZE(challenge.sid) && now <= MAXULONGLONG - WindowsGrantLifetimeMilliseconds, "challenge-context-invalid");
    challenge.session = session; challenge.issuedMs = now; challenge.expiresMs = now + WindowsGrantLifetimeMilliseconds;
    CopyMemory(challenge.sid, sid.c_str(), (sid.size() + 1) * sizeof(wchar_t));
    NativeRequire(BCryptGenRandom(nullptr, challenge.nonce, sizeof(challenge.nonce), BCRYPT_USE_SYSTEM_PREFERRED_RNG) >= 0, "nonce-unavailable"); return challenge;
}
inline bool ChallengeContextValid(const WindowsChallenge& challenge, const std::wstring& sid, DWORD session, ULONGLONG now) {
    WindowsChallenge format;
    return memcmp(challenge.domain, format.domain, sizeof(format.domain)) == 0 && challenge.session == session && session &&
        wcsnlen_s(challenge.sid, ARRAYSIZE(challenge.sid)) < ARRAYSIZE(challenge.sid) && wcsnlen_s(challenge.sid, ARRAYSIZE(challenge.sid)) == sid.size() && sid == challenge.sid &&
        WindowsGrantLifetimeValid(challenge.issuedMs, challenge.expiresMs) && now >= challenge.issuedMs && now < challenge.expiresMs;
}
inline WindowsApproval SignWindowsChallenge(const WindowsChallenge& challenge, SigningKey& key) {
    WindowsApproval approval; approval.challenge = challenge; auto hash = ChallengeHash(challenge); DWORD size = 0;
    NativeRequire(BCryptSignHash(key.key, nullptr, hash.data(), static_cast<ULONG>(hash.size()), approval.signature, sizeof(approval.signature), &size, 0) >= 0 && size == sizeof(approval.signature), "signature-failed"); return approval;
}
class OneTimeWindowsGrant {
    WindowsChallenge expected;
    const ULONGLONG started;
    std::atomic_bool consumed{false};
public:
    OneTimeWindowsGrant(const WindowsChallenge& challenge, ULONGLONG monotonic) : expected(challenge), started(monotonic) {}
    bool Verify(const WindowsApproval& approval, const std::vector<BYTE>& publicKey, ULONGLONG now, ULONGLONG monotonic) const {
        if (consumed.load() || wcsnlen_s(expected.sid, ARRAYSIZE(expected.sid)) >= ARRAYSIZE(expected.sid) || monotonic < started ||
            !WindowsGrantLifetimeValid(expected.issuedMs, expected.expiresMs) || monotonic - started >= expected.expiresMs - expected.issuedMs || memcmp(&expected, &approval.challenge, sizeof(expected)) ||
            !ChallengeContextValid(expected, expected.sid, expected.session, now)) return false;
        try { SigningKey key; key.Import(publicKey.data(), static_cast<DWORD>(publicKey.size()), false); auto hash = ChallengeHash(approval.challenge);
            return BCryptVerifySignature(key.key, nullptr, hash.data(), static_cast<ULONG>(hash.size()), const_cast<BYTE*>(approval.signature), sizeof(approval.signature), 0) >= 0;
        } catch (...) { return false; }
    }
    bool Consume(const WindowsApproval& approval, const std::vector<BYTE>& publicKey, ULONGLONG now, ULONGLONG monotonic) {
        if (!Verify(approval, publicKey, now, monotonic)) return false;
        bool expectedState = false; return consumed.compare_exchange_strong(expectedState, true);
    }
};
inline std::wstring ChallengeFileName(const WindowsChallenge& challenge) {
    constexpr wchar_t hex[] = L"0123456789abcdef"; std::wstring name;
    for (BYTE b : challenge.nonce) { name += hex[b >> 4]; name += hex[b & 15]; } return name + L".request";
}
