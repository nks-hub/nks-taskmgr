#include "core/resource.hpp"
#include <iostream>
#include <stdexcept>

namespace {
void require(bool value, const char* text) { if (!value) throw std::runtime_error(text); }

nks::ResourceRecord adapter(const wchar_t* id, std::uint64_t received, std::uint64_t sent, bool valid = true)
{
    nks::ResourceRecord record;
    record.id = id; record.kind = nks::ResourceKind::network;
    record.receivedOctets = received; record.sentOctets = sent; record.octetsValid = valid;
    return record;
}

nks::ResourceSnapshot snapshot(int second, std::vector<nks::ResourceRecord> devices)
{
    nks::ResourceSnapshot value;
    value.measuredAt = std::chrono::steady_clock::time_point(std::chrono::seconds(second));
    value.devices = std::move(devices);
    return value;
}

const nks::NetworkTransfer& transfer(const nks::ResourceSnapshot& value, std::size_t index) { return value.devices.at(index).transfer; }
}

int main()
{
    try {
        nks::NetworkTransferTotals totals;
        auto first = snapshot(1, {adapter(L"network:1", 5'000, 7'000), adapter(L"network:2", 100, 200)});
        totals.apply(first, 1'700'000'000'000'000'000);
        require(transfer(first, 0).state == nks::SampleState::valid && transfer(first, 0).receivedBytes == 0 && transfer(first, 0).sentBytes == 0,
            "First sample must be a measured zero, not the raw counter");
        require(transfer(first, 0).startedAtNs == 1'000'000'000 && transfer(first, 0).startedAtUnixNs == 1'700'000'000'000'000'000,
            "Start time must be the first observation");

        auto normal = snapshot(2, {adapter(L"network:1", 6'500, 7'300), adapter(L"network:2", 100, 200)});
        totals.apply(normal, 1'700'000'001'000'000'000);
        require(transfer(normal, 0).receivedBytes == 1'500 && transfer(normal, 0).sentBytes == 300, "Counter growth was not added");
        require(transfer(normal, 0).startedAtNs == 1'000'000'000 && transfer(normal, 0).startedAtUnixNs == 1'700'000'000'000'000'000,
            "Start time moved after the first sample");
        require(transfer(normal, 1).receivedBytes == 0 && transfer(normal, 1).state == nks::SampleState::valid, "Idle adapter lost its measured zero");

        auto reset = snapshot(3, {adapter(L"network:1", 40, 7'400)});
        totals.apply(reset, 0);
        require(transfer(reset, 0).receivedBytes == 1'500 && transfer(reset, 0).sentBytes == 400,
            "Counter decrease must add nothing and must not affect the other direction");
        auto afterReset = snapshot(4, {adapter(L"network:1", 140, 7'400)});
        totals.apply(afterReset, 0);
        require(transfer(afterReset, 0).receivedBytes == 1'600, "Counting did not continue from the reset baseline");

        auto wrapped = snapshot(5, {adapter(L"network:1", 10, 7'400)});
        totals.apply(wrapped, 0);
        require(transfer(wrapped, 0).receivedBytes == 1'600, "Wrapped counter produced a jump");

        auto unavailable = snapshot(6, {adapter(L"network:1", 0, 0, false)});
        totals.apply(unavailable, 0);
        require(transfer(unavailable, 0).state == nks::SampleState::unavailable && transfer(unavailable, 0).receivedBytes == 1'600,
            "Unavailable counters must be reported as unavailable without dropping the total");
        auto recovered = snapshot(7, {adapter(L"network:1", 9'000'000, 9'000'000)});
        totals.apply(recovered, 0);
        require(transfer(recovered, 0).state == nks::SampleState::valid && transfer(recovered, 0).receivedBytes == 1'600
            && transfer(recovered, 0).sentBytes == 400, "Counters after an unavailable sample produced a jump");

        auto returned = snapshot(9, {adapter(L"network:1", 9'000'100, 9'000'000), adapter(L"network:2", 50'000, 60'000)});
        totals.apply(returned, 1'800'000'000'000'000'000);
        require(transfer(returned, 1).receivedBytes == 0 && transfer(returned, 1).sentBytes == 0 && transfer(returned, 1).state == nks::SampleState::valid,
            "Adapter that disappeared and came back produced a jump");
        require(transfer(returned, 1).startedAtNs == 1'000'000'000, "Returning adapter restarted its counting period");
        require(transfer(returned, 0).receivedBytes == 1'700, "Present adapter was affected by another adapter returning");
        auto continued = snapshot(10, {adapter(L"network:2", 50'500, 60'250)});
        totals.apply(continued, 0);
        require(transfer(continued, 0).receivedBytes == 500 && transfer(continued, 0).sentBytes == 250, "Returned adapter did not resume counting");

        nks::ResourceSnapshot others = snapshot(11, {});
        nks::ResourceRecord disk; disk.kind = nks::ResourceKind::disk; disk.id = L"disk:1"; disk.octetsValid = true; disk.receivedOctets = 5;
        others.devices.push_back(disk);
        totals.apply(others, 0);
        require(others.devices[0].transfer.state == nks::SampleState::unavailable, "Non-network device received network totals");

        std::cout << "Network transfer totals passed\n";
        return 0;
    } catch (const std::exception& error) {
        std::cerr << error.what() << '\n';
        return 1;
    }
}
