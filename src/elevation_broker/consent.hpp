#pragma once
#include "core/broker_trust.hpp"

namespace nks::broker {
class Consent {
public:
    Consent(const Peer& gui, const Peer& bridge);
    bool prepare(actions::Command command, std::span<actions::Result> results);
    /// Binds a Task Manager replacement request to the verified GUI image; returns 0 or the refusal code.
    DWORD prepareReplacement(const actions::Result& target, bool enabled, std::wstring& guiPath);
    bool confirm(actions::Command command, std::uint64_t argument, std::uint32_t group, std::span<const std::uint32_t> selected = {},
        const actions::HandleSpec* handle = nullptr);
    actions::Context& context() noexcept { return context_; }
    HANDLE target(std::size_t index) const noexcept { return targets_[index].get(); }
private:
    const Peer& gui_;
    const Peer& bridge_;
    actions::Context context_;
    std::vector<Handle> targets_;
    std::wstring description_;
};
}
