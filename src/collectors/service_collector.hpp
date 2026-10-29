#pragma once

#include "core/service.hpp"
#include <map>
#include <stop_token>

namespace nks {

class ServiceCollector {
public:
    ServiceSnapshot collect(std::stop_token stop = {});
    ServiceDetail detail(const std::wstring& name);
private:
    struct CachedConfig {
        ServiceConfig config;
        std::chrono::steady_clock::time_point expires;
    };
    std::map<std::wstring, CachedConfig> configs;
};

}
