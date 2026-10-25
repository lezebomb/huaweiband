#pragma once
#include "SignedGrant.h"
#include <algorithm>

// Renewal is a different signed purpose; it must never satisfy OneTimeWindowsGrant.
inline bool RenewalContextValid(const WindowsChallenge& value, const std::wstring& sid, DWORD session, ULONGLONG now) {
    static const BYTE domain[16] = {'B','A','N','D','K','E','Y','-','N','E','W','-','V','1',0,0};
    size_t length = wcsnlen_s(value.sid, ARRAYSIZE(value.sid));
    if (memcmp(value.domain, domain, sizeof(domain)) || !session || value.session != session ||
        length != sid.size() || length >= ARRAYSIZE(value.sid) || sid != value.sid ||
        value.expiresMs <= value.issuedMs || value.expiresMs - value.issuedMs != 10000 || now < value.issuedMs || now >= value.expiresMs) return false;
    for (size_t i = length; i < ARRAYSIZE(value.sid); i++) if (value.sid[i]) return false;
    return true;
}
class RequestRenewalGate {
    struct Used { std::array<BYTE,32> nonce; ULONGLONG expires; };
    std::vector<Used> used;
    ULONGLONG lastWall = 0, lastMonotonic = 0;
public:
    bool Consume(const WindowsApproval& proof, const std::vector<BYTE>& publicKey, const std::wstring& sid, DWORD session, ULONGLONG now, ULONGLONG monotonic) {
        if(now < lastWall || monotonic < lastMonotonic || !RenewalContextValid(proof.challenge,sid,session,now))return false;
        lastWall=now;lastMonotonic=monotonic;
        used.erase(std::remove_if(used.begin(),used.end(),[&](const Used& item){return item.expires<=now;}),used.end());
        if(used.size()>=64 || std::any_of(used.begin(),used.end(),[&](const Used& item){return memcmp(item.nonce.data(),proof.challenge.nonce,32)==0;}))return false;
        try{
            SigningKey key;key.Import(publicKey.data(),static_cast<DWORD>(publicKey.size()),false);auto hash=ChallengeHash(proof.challenge);
            if(BCryptVerifySignature(key.key,nullptr,hash.data(),static_cast<ULONG>(hash.size()),const_cast<BYTE*>(proof.signature),sizeof(proof.signature),0)<0)return false;
            Used item{};CopyMemory(item.nonce.data(),proof.challenge.nonce,32);item.expires=proof.challenge.expiresMs;used.push_back(item);return true;
        }catch(...){return false;}
    }
};
