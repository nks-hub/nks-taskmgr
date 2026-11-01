#pragma once
#include "broker_trust.hpp"
#include <span>

namespace nks::broker {
Handle createPipe(const std::wstring& name, const TokenIdentity& owner);
bool connectPipe(HANDLE pipe, HANDLE peer, ULONGLONG deadline);
bool pipeIo(HANDLE pipe, std::span<std::byte> bytes, bool writing, HANDLE peer, ULONGLONG deadline);
bool pipeWrite(HANDLE pipe, std::span<const std::byte> bytes, HANDLE peer, ULONGLONG deadline);
std::wstring newPipeName();
}
