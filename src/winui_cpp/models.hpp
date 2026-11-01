#pragma once
#include "../interop/monitor_api.h"
#include <algorithm>
#include <array>
#include <cmath>
#include <cstdint>
#include <deque>
#include <map>
#include <set>
#include <string>
#include <vector>

namespace nks::candidate {
struct ProcessKey {
    uint64_t bootLow{}, bootHigh{}, generation{};
    uint32_t pid{}, kind{};
    auto operator<=>(const ProcessKey&) const = default;
    std::wstring text() const;
};
struct Process {
    ProcessKey key;
    std::wstring name;
    uint32_t parent{}, threads{}, handles{};
    uint64_t created{}, workingSet{}, privateBytes{};
    NksMetric cpu{}, read{}, write{};
    bool ended{}, stale{};
};
struct Point { int64_t time{}; NksMetric metric{}; bool breakBefore{}; };
struct History {
    std::deque<Point> points;
    bool interrupted{};
    void append(int64_t time, NksMetric metric);
    double maximum(int64_t start, double floor) const;
};
struct Series {
    uint32_t channel{}, group{}, index{};
    std::wstring resource, detail, name;
    History history;
};
struct Resource {
    uint32_t kind{};
    std::wstring id, title, description, status;
    NksMetric primary{}, secondary{}, tertiary{};
    double capacity{};
    int64_t measured{};
};
struct Service {
    std::wstring name, title, account, command, group;
    std::vector<std::wstring> dependencies;
    uint32_t state{}, pid{}, startType{}, configError{};
};
struct ServiceDetail {
    std::wstring name, description, error;
    std::vector<std::wstring> dependents;
    ProcessKey host;
    uint64_t created{};
    uint32_t hostState{NKS_SAMPLE_UNAVAILABLE};
};
struct Snapshot {
    std::vector<Process> processes;
    std::vector<Resource> resources;
    std::vector<Service> services;
    ServiceDetail detail;
    std::map<uint32_t, Series> series;
    std::wstring processError, resourceError, serviceError, historyError;
    int64_t processTime{}, resourceTime{}, serviceTime{}, now{};
    uint64_t historySequence{}, lost{}, captureErrors{};
    bool processChanged{}, resourceChanged{}, serviceChanged{}, detailChanged{};
};
std::wstring copy(NksString16 text);
std::wstring number(double value, const wchar_t* unit = L"", int digits = 1);
std::wstring metric(NksMetric value, const wchar_t* unit = L"", double divisor = 1);
std::wstring sampleState(uint32_t state);
std::wstring lower(std::wstring text);
std::wstring resourceValue(const Resource& resource, bool stale);
std::wstring serviceState(uint32_t state);
bool valid(NksMetric metric);
}
