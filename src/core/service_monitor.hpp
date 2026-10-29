#pragma once

#include "monitor.hpp"
#include "service.hpp"

namespace nks {

class ServiceMonitor {
public:
    ~ServiceMonitor();
    void start();
    void stop();
    void setActive(bool value);
    void setPaused(bool value);
    void select(std::wstring name);
    std::shared_ptr<const ServiceSnapshot> take(std::wstring& error) { return snapshots.take(error); }
    std::shared_ptr<const ServiceDetail> takeDetail(std::wstring& error) { return details.take(error); }
private:
    SnapshotSlot<ServiceSnapshot> snapshots;
    SnapshotSlot<ServiceDetail> details;
    std::mutex mutex;
    std::condition_variable_any wake;
    std::wstring selected;
    bool active{}, paused{}, changed{};
    std::jthread worker;
};

}
