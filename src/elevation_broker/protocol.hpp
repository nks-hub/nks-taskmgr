#pragma once
#include "collectors/process_actions_wire.hpp"
#include "core/broker_trust.hpp"
#include <algorithm>

namespace nks::broker {
constexpr std::uint32_t envelopeMagic = 0x31424b4e; // NKB1
constexpr std::size_t envelopeBytes = 144, challengeBytes = 64;
constexpr ULONGLONG startupMs = 90000, promptMs = 60000, operationMs = 5000;
using Envelope = std::array<std::byte, envelopeBytes>;
using Challenge = std::array<std::byte, challengeBytes>;
inline void keyTo(std::span<std::byte> bytes, std::size_t at, const ProcessIdentity& key)
{
    using actions::write;
    write(bytes, at, key.boot.low, 8); write(bytes, at + 8, key.boot.high, 8); write(bytes, at + 16, key.pid, 4);
    write(bytes, at + 20, static_cast<unsigned>(key.generationKind), 4); write(bytes, at + 24, key.generation, 8);
}
inline bool keyMatches(std::span<const std::byte> bytes, std::size_t at, const ProcessIdentity& key)
{
    using namespace actions;
    return read64(bytes, at) == key.boot.low && read64(bytes, at + 8) == key.boot.high && read32(bytes, at + 16) == key.pid
        && read32(bytes, at + 20) == static_cast<unsigned>(key.generationKind) && read64(bytes, at + 24) == key.generation;
}
inline Envelope envelope(const actions::wire::Header& request, const Peer& client, const Peer& gui, ULONGLONG expires, const Challenge& challenge)
{
    using actions::write; Envelope bytes{};
    write(bytes, 0, envelopeMagic, 4); write(bytes, 4, 1, 2); write(bytes, 6, envelopeBytes, 2);
    write(bytes, 8, actions::headerBytes + actions::read32(request, 8), 4); write(bytes, 16, expires, 8);
    std::copy(request.begin() + 16, request.begin() + 40, bytes.begin() + 24);
    keyTo(bytes, 48, client.key); keyTo(bytes, 80, gui.key);
    std::copy(challenge.begin() + 40, challenge.begin() + 56, bytes.begin() + 112); return bytes;
}
inline bool validEnvelope(const Envelope& bytes, const actions::wire::Header& request, const Peer& client, const Peer& gui, const Challenge& challenge)
{
    using namespace actions;
    const auto now = GetTickCount64(), expires = read64(bytes, 16);
    return read32(bytes, 0) == envelopeMagic && read(bytes, 4, 2) == 1 && read(bytes, 6, 2) == envelopeBytes
        && read32(bytes, 8) == headerBytes + read32(request, 8) && !read32(bytes, 12)
        && expires > now && expires - now <= startupMs && !read64(bytes, 128) && !read64(bytes, 136)
        && std::equal(challenge.begin() + 40, challenge.begin() + 56, bytes.begin() + 112)
        && std::equal(request.begin() + 16, request.begin() + 40, bytes.begin() + 24)
        && keyMatches(bytes, 48, client.key) && keyMatches(bytes, 80, gui.key);
}
}
