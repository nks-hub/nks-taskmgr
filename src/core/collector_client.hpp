#pragma once

#include "collector_worker/snapshot_codec.hpp"
#include "core/service.hpp"
#include <memory>
#include <stop_token>

namespace nks {

class CollectorClient {
public:
    explicit CollectorClient(collector::Kind kind);
    ~CollectorClient();
    CollectorClient(const CollectorClient&) = delete;
    CollectorClient& operator=(const CollectorClient&) = delete;
    ProcessSnapshot processes(std::stop_token stop);
    ResourceSnapshot resources(std::stop_token stop, bool cpuSensors = false);
    ServiceSnapshot services(std::stop_token stop);
    ServiceDetail detail(const std::wstring& name, std::stop_token stop);
private:
    class Connection;
    std::vector<std::byte> request(std::stop_token stop, collector::Kind command, std::span<const std::byte> payload = {});
    collector::Kind kind;
    std::uint64_t requestId{};
    std::unique_ptr<Connection> connection;
};

}
