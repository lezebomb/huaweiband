#pragma once
#include "SignedGrant.h"
#include "LabPolicy.h"

// The override is compiled only into the ordinary-process test EXE, never the DLL.
#ifdef BANDKEY_UNIT_TEST
struct SyntheticGrantRuntime {
    LabPolicy policy;
    std::filesystem::path directory;
    DWORD session = 5;
    ULONGLONG wall = 100000, monotonic = 1000;
    bool locked = true, system = true;
    unsigned vaultReads = 0;
    SecretBuffer<257> password;
};
inline SyntheticGrantRuntime* syntheticGrantRuntime = nullptr;
#endif
inline bool GrantIsSystem() {
#ifdef BANDKEY_UNIT_TEST
    if (syntheticGrantRuntime) return syntheticGrantRuntime->system;
#endif
    return ProcessIsLocalSystem();
}
inline LabPolicy GrantPolicy() {
#ifdef BANDKEY_UNIT_TEST
    if (syntheticGrantRuntime) return syntheticGrantRuntime->policy;
#endif
    return ReadLabPolicy();
}
inline std::filesystem::path GrantDirectory() {
#ifdef BANDKEY_UNIT_TEST
    if (syntheticGrantRuntime) return syntheticGrantRuntime->directory;
#endif
    return LabInstallDirectory();
}
inline DWORD GrantSession() {
#ifdef BANDKEY_UNIT_TEST
    if (syntheticGrantRuntime) return syntheticGrantRuntime->session;
#endif
    DWORD session = 0; return ProcessIdToSessionId(GetCurrentProcessId(), &session) ? session : 0;
}
inline bool GrantSessionLocked(DWORD session) {
#ifdef BANDKEY_UNIT_TEST
    if (syntheticGrantRuntime) return syntheticGrantRuntime->locked && syntheticGrantRuntime->session == session;
#endif
    return SessionIsLocked(session);
}
inline ULONGLONG GrantWallTime() {
#ifdef BANDKEY_UNIT_TEST
    if (syntheticGrantRuntime) return syntheticGrantRuntime->wall;
#endif
    return FileTimeMilliseconds();
}
inline ULONGLONG GrantMonotonicTime() {
#ifdef BANDKEY_UNIT_TEST
    if (syntheticGrantRuntime) return syntheticGrantRuntime->monotonic;
#endif
    return GetTickCount64();
}
inline void GrantReadPassword(const std::wstring& sid, SecretBuffer<257>& password) {
#ifdef BANDKEY_UNIT_TEST
    if (syntheticGrantRuntime) { ++syntheticGrantRuntime->vaultReads; CopyMemory(password.data.data(), syntheticGrantRuntime->password.data.data(), sizeof(password.data)); return; }
#endif
    ReadVaultPassword(GrantDirectory() / L"Vault", sid, password);
}
