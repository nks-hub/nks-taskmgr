#pragma once
#include "journal.hpp"
#include "recovery.hpp"

namespace nks::update {
struct Context {
    broker::Peer gui, self;
    Image installed, worker;
    std::wstring installDir;
    Scope scope;
};
Context authenticate(const Header& header, bool staged, bool compatible = false);
bool validHeader(const Header& header, Command command, std::size_t payload);
Plan createPlan(Cache& cache, Context& context, const Hash& requested);
Plan loadPlan(Cache& cache, Context& context, const Header& request, std::span<const std::byte> payload);
void install(Cache& cache, Context& context, Plan& plan, Image& package);
Image cacheSource(Context& context, std::span<const std::byte> payload);
std::vector<std::byte> result(Status status, DWORD error, State state, const std::wstring& detail = {},
    const std::vector<Image>* packages = nullptr, const Plan* plan = nullptr, std::uint64_t commitDeadline = 0, const std::wstring& current = {}, const std::wstring& previous = {},
    const Context* context = nullptr, const std::vector<Inspection>* candidates = nullptr);
}
