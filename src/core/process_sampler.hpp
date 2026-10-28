#pragma once

#include "process.hpp"
#include <optional>

namespace nks {

class ProcessSampler {
public:
    void apply(ProcessSnapshot& snapshot);
    void reset() noexcept { previous.reset(); }

private:
    std::optional<ProcessSnapshot> previous;
};

}
