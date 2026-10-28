package dev.bandkey.bridge;

/** A system cleanup may recover; a user stop requires a newer explicit opt-in. */
final class ResidentResumePolicy {
    static boolean allowed(boolean enabled,long enabledAt,boolean userStopped,long exitAt){
        if(!enabled || enabledAt<0 || exitAt<0)return false;
        return !userStopped || enabledAt>exitAt;
    }
}
