#pragma once
#include "broker_trust.hpp"
#include "update_worker/protocol.hpp"
#include <stdexcept>

namespace nks::update {
struct Error : std::runtime_error {
    Status status; DWORD windows;
    Error(Status value, const char* message, DWORD code = 0) : std::runtime_error(message), status(value), windows(code) {}
};
enum class Role { application, worker, installer };
struct Image {
    broker::Peer lease;
    Hash hash{};
    std::uint64_t size{};
    std::wstring version, revision;
    std::array<std::uint32_t, 3> numbers{};
};
Image openImage(const std::wstring& path, Role role);
Image inspectImage(broker::Handle file, Role role);
Hash hashFile(HANDLE file, std::uint64_t limit);
void verifyVersion(Image& image, Role role);
bool equalId(const FILE_ID_INFO& left, const FILE_ID_INFO& right) noexcept;
bool sameVersion(const Image& left, const Image& right) noexcept;
std::wstring parentPath(const std::wstring& path);
std::uint64_t unixMs();
}
