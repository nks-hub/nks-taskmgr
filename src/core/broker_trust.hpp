#pragma once
#include "collectors/process_actions.hpp"
#include <array>
#include <string>

namespace nks::broker {
using actions::Handle;
constexpr DWORD trustNotConfigured = 0xe04e0001;
constexpr DWORD trustRejected = 0xe04e0002;
constexpr DWORD consentDeclined = 0xe04e0003;
constexpr DWORD replacementLocation = 0xe04e0004;
constexpr DWORD replacementAccess = 0xe04e0005;
struct TokenIdentity {
    std::vector<std::byte> user, logon;
    DWORD session{}, integrity{};
    LUID authentication{};
    bool elevated{};
};
struct Peer {
    Handle process, image;
    std::vector<Handle> directories;
    ProcessIdentity key;
    TokenIdentity token;
    FILE_ID_INFO fileId{};
    std::wstring path;
};
bool trustConfigured() noexcept;
bool verifyImageFile(Peer& peer);
bool readToken(HANDLE process, TokenIdentity& token);
bool sameLogon(const TokenIdentity& left, const TokenIdentity& right) noexcept;
DWORD parentPid(HANDLE process) noexcept;
bool verifyPeer(DWORD pid, const wchar_t* name, Peer& peer);
bool peerAlive(const Peer& peer) noexcept;
bool sameFile(const Peer& left, const Peer& right) noexcept;
class Consent;
}
