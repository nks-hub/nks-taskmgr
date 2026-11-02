#include "cpu_sensors.hpp"
#include <windows.h>
#include <winhttp.h>
#include <algorithm>
#include <array>
#include <charconv>
#include <cmath>
#include <cwchar>
#include <iomanip>
#include <locale>
#include <map>
#include <set>
#include <sstream>
#include <stdexcept>
#include <string_view>
#include <system_error>

namespace nks {
namespace {

constexpr std::size_t maximumBody = 512 * 1024, maximumLine = 16384, maximumLines = 4096, maximumSensors = 128;
constexpr std::string_view cpuMetric = "lhm_cpu_temperature_celsius";
constexpr wchar_t endpoint[] = L"http://127.0.0.1:8085/metrics?archivelength=0&lastvalue=1&timestamps=1";

struct HttpHandle {
    HINTERNET value{};
    explicit HttpHandle(HINTERNET handle) : value(handle) {}
    ~HttpHandle() { if (value) WinHttpCloseHandle(value); }
    HttpHandle(const HttpHandle&) = delete;
    HttpHandle& operator=(const HttpHandle&) = delete;
};

struct SensorFailure { SampleState state; DWORD error; const wchar_t* detail; };
struct SensorCancelled {};

void checkCancelled(std::stop_token stop)
{
    if (stop.stop_requested()) throw SensorCancelled{};
}

void requireHttp(bool success)
{
    if (success) return;
    const auto error = GetLastError();
    throw SensorFailure{error == ERROR_ACCESS_DENIED ? SampleState::accessDenied : SampleState::unavailable,
        error, L"Místní čtecí server není dostupný nebo neodpověděl včas"};
}

std::string response(std::stop_token stop)
{
    checkCancelled(stop);
    HttpHandle session(WinHttpOpen(L"NksTaskMgr CPU sensors/1", WINHTTP_ACCESS_TYPE_NO_PROXY,
        WINHTTP_NO_PROXY_NAME, WINHTTP_NO_PROXY_BYPASS, 0));
    requireHttp(session.value != nullptr);
    requireHttp(WinHttpSetTimeouts(session.value, 250, 250, 500, 500) != FALSE);
    HttpHandle connection(WinHttpConnect(session.value, L"127.0.0.1", 8085, 0));
    requireHttp(connection.value != nullptr);
    HttpHandle request(WinHttpOpenRequest(connection.value, L"GET",
        L"/metrics?archivelength=0&lastvalue=1&timestamps=1", nullptr, WINHTTP_NO_REFERER, WINHTTP_DEFAULT_ACCEPT_TYPES, 0));
    requireHttp(request.value != nullptr);
    DWORD redirect = WINHTTP_OPTION_REDIRECT_POLICY_NEVER;
    requireHttp(WinHttpSetOption(request.value, WINHTTP_OPTION_REDIRECT_POLICY, &redirect, sizeof(redirect)) != FALSE);
    DWORD disabled = WINHTTP_DISABLE_COOKIES | WINHTTP_DISABLE_AUTHENTICATION;
    requireHttp(WinHttpSetOption(request.value, WINHTTP_OPTION_DISABLE_FEATURE, &disabled, sizeof(disabled)) != FALSE);
    checkCancelled(stop);
    requireHttp(WinHttpSendRequest(request.value, WINHTTP_NO_ADDITIONAL_HEADERS, 0, WINHTTP_NO_REQUEST_DATA, 0, 0, 0) != FALSE);
    checkCancelled(stop);
    requireHttp(WinHttpReceiveResponse(request.value, nullptr) != FALSE);
    checkCancelled(stop);
    DWORD status{}, size = sizeof(status);
    requireHttp(WinHttpQueryHeaders(request.value, WINHTTP_QUERY_STATUS_CODE | WINHTTP_QUERY_FLAG_NUMBER,
        WINHTTP_HEADER_NAME_BY_INDEX, &status, &size, WINHTTP_NO_HEADER_INDEX) != FALSE);
    if (status != 200) throw SensorFailure{status == 401 || status == 403 ? SampleState::accessDenied
        : status == 404 ? SampleState::unsupported : SampleState::unavailable, status,
        L"Místní server odmítl čtecí API (HTTP)"};
    std::string body;
    std::array<char, 8192> buffer{};
    const auto deadline = GetTickCount64() + 1500;
    for (;;) {
        checkCancelled(stop);
        if (GetTickCount64() >= deadline) throw SensorFailure{SampleState::unavailable, ERROR_TIMEOUT, L"Čtení čidel překročilo časový limit"};
        DWORD received{};
        requireHttp(WinHttpReadData(request.value, buffer.data(), static_cast<DWORD>(buffer.size()), &received) != FALSE);
        checkCancelled(stop);
        if (!received) break;
        if (received > maximumBody - body.size()) throw std::length_error("CPU sensor response exceeds byte limit");
        body.append(buffer.data(), received);
    }
    return body;
}

bool space(char ch) { return ch == ' ' || ch == '\t'; }

void trim(std::string_view& value)
{
    while (!value.empty() && space(value.front())) value.remove_prefix(1);
    while (!value.empty() && (space(value.back()) || value.back() == '\r')) value.remove_suffix(1);
}

std::wstring text(std::string_view value)
{
    if (value.empty() || value.size() > 1024) throw std::runtime_error("Invalid CPU sensor label length");
    const auto length = MultiByteToWideChar(CP_UTF8, MB_ERR_INVALID_CHARS, value.data(), static_cast<int>(value.size()), nullptr, 0);
    if (length <= 0 || length > 256) throw std::runtime_error("Invalid CPU sensor label encoding");
    std::wstring result(static_cast<std::size_t>(length), L'\0');
    if (MultiByteToWideChar(CP_UTF8, MB_ERR_INVALID_CHARS, value.data(), static_cast<int>(value.size()), result.data(), length) != length)
        throw std::runtime_error("Invalid CPU sensor label encoding");
    if (std::any_of(result.begin(), result.end(), [](wchar_t ch) { return ch < 32 || ch == 127; }))
        throw std::runtime_error("Invalid CPU sensor label control character");
    return result;
}

std::map<std::string, std::string> labels(std::string_view& line)
{
    if (line.empty() || line.front() != '{') throw std::runtime_error("Missing CPU sensor labels");
    line.remove_prefix(1);
    std::map<std::string, std::string> result;
    for (unsigned count = 0; count < 16; ++count) {
        trim(line);
        const auto equal = line.find('=');
        if (equal == std::string_view::npos || equal == 0 || equal > 32) throw std::runtime_error("Invalid CPU sensor label");
        auto key = line.substr(0, equal);
        if (key.size() >= 2 && key.front() == '"' && key.back() == '"') key = key.substr(1, key.size() - 2);
        std::string name(key);
        if (!std::all_of(name.begin(), name.end(), [](char ch) {
            return (ch >= 'a' && ch <= 'z') || (ch >= 'A' && ch <= 'Z') || ch == '_';
        })) throw std::runtime_error("Invalid CPU sensor label name");
        line.remove_prefix(equal + 1);
        if (line.empty() || line.front() != '"') throw std::runtime_error("Invalid CPU sensor label quoting");
        line.remove_prefix(1);
        std::string value;
        bool closed{};
        while (!line.empty()) {
            char ch = line.front(); line.remove_prefix(1);
            if (ch == '"') { closed = true; break; }
            if (ch == '\\') {
                if (line.empty() || (line.front() != '\\' && line.front() != '"'))
                    throw std::runtime_error("Unsupported CPU sensor label escape");
                ch = line.front(); line.remove_prefix(1);
            }
            if (value.size() >= 1024) throw std::length_error("CPU sensor label exceeds limit");
            value.push_back(ch);
        }
        if (!closed || !result.emplace(std::move(name), std::move(value)).second)
            throw std::runtime_error("Invalid or duplicate CPU sensor label");
        trim(line);
        if (!line.empty() && line.front() == '}') { line.remove_prefix(1); return result; }
        if (line.empty() || line.front() != ',') throw std::runtime_error("Invalid CPU sensor label separator");
        line.remove_prefix(1);
    }
    throw std::length_error("CPU sensor label count exceeds limit");
}

std::vector<CpuSensorSample> parse(std::string_view body)
{
    std::vector<CpuSensorSample> result;
    std::set<std::wstring> identifiers;
    bool providerFormat{};
    unsigned lineCount{};
    while (!body.empty()) {
        const auto end = body.find('\n');
        auto line = body.substr(0, end);
        body.remove_prefix(end == std::string_view::npos ? body.size() : end + 1);
        if (++lineCount > maximumLines || line.size() > maximumLine) throw std::length_error("CPU sensor line limit exceeded");
        trim(line);
        if (line.starts_with("# TYPE lhm_")) providerFormat = true;
        if (!line.starts_with(cpuMetric)) continue;
        line.remove_prefix(cpuMetric.size());
        if (!line.empty() && line.front() != '{' && !space(line.front())) continue;
        trim(line);
        const auto fields = labels(line);
        trim(line);
        double celsius{};
        const auto number = std::from_chars(line.data(), line.data() + line.size(), celsius);
        if (number.ec != std::errc{} || !std::isfinite(celsius) || celsius < -273.15
            || number.ptr == line.data() + line.size() || !space(*number.ptr))
            throw std::runtime_error("Invalid CPU sensor temperature");
        line.remove_prefix(static_cast<std::size_t>(number.ptr - line.data())); trim(line);
        std::int64_t timestamp{};
        const auto time = std::from_chars(line.data(), line.data() + line.size(), timestamp);
        if (time.ec != std::errc{} || time.ptr != line.data() + line.size() || timestamp <= 0)
            throw std::runtime_error("CPU sensor timestamp missing or invalid");
        const auto field = [&](const char* key) {
            const auto found = fields.find(key);
            if (found == fields.end()) throw std::runtime_error("Missing CPU sensor identity");
            return text(found->second);
        };
        const auto hardware = field("hardwareId"), sensor = field("sensorId");
        if (!hardware.starts_with(L"/") || !sensor.starts_with(L"/")) throw std::runtime_error("Invalid CPU sensor identity");
        const auto id = hardware + sensor;
        if (!identifiers.insert(id).second) throw std::runtime_error("Duplicate CPU sensor identity");
        if (result.size() >= maximumSensors) throw std::length_error("CPU sensor count exceeds limit");
        result.push_back({id, field("hardwareName") + L" · " + field("sensorName"), celsius, timestamp});
    }
    if (!providerFormat) throw SensorFailure{SampleState::unsupported, 0, L"Server neposkytuje podporovaný formát /metrics"};
    return result;
}

std::wstring utc(std::int64_t milliseconds)
{
    constexpr std::uint64_t epochMs = 11644473600000ull;
    const auto ms = static_cast<std::uint64_t>(milliseconds);
    if (ms > UINT64_MAX / 10000 - epochMs) return L"Neplatný čas";
    ULARGE_INTEGER ticks{}; ticks.QuadPart = (ms + epochMs) * 10000;
    const FILETIME file{ticks.LowPart, ticks.HighPart};
    SYSTEMTIME time{};
    if (!FileTimeToSystemTime(&file, &time)) return L"Neplatný čas";
    wchar_t value[32]{};
    swprintf_s(value, L"%04u-%02u-%02u %02u:%02u:%02u UTC", time.wYear, time.wMonth, time.wDay,
        time.wHour, time.wMinute, time.wSecond);
    return value;
}

}

CpuSensors::Reading CpuSensors::read(std::stop_token stop)
{
    Reading result;
    try {
        result.samples = parse(response(stop));
        result.state = result.samples.empty() ? SampleState::unsupported : SampleState::valid;
        if (result.samples.empty()) result.detail = L"API neposkytlo teplotní čidlo typu CPU";
    } catch (const SensorFailure& failure) {
        result.state = failure.state; result.detail = std::wstring(failure.detail) + L" (" + std::to_wstring(failure.error) + L")";
    } catch (const std::runtime_error&) {
        result.state = SampleState::error; result.detail = L"Neplatné nebo nepodporované senzorové údaje; vyžaduje se číselná teplota a čas vzorku";
    } catch (const std::length_error&) {
        result.state = SampleState::error; result.detail = L"Čtecí API překročilo bezpečný limit odpovědi";
    }
    checkCancelled(stop);
    result.readAtUnixMs = std::chrono::duration_cast<std::chrono::milliseconds>(std::chrono::system_clock::now().time_since_epoch()).count();
    return result;
}

CpuSensors::~CpuSensors()
{
    {
        std::lock_guard lock(mutex);
        enabled = false; queued = false; ++generation;
        requestStop.request_stop();
    }
    worker.request_stop(); ready.notify_all();
    if (worker.joinable()) worker.join();
}

void CpuSensors::run(std::stop_token stop)
{
    SetThreadDescription(GetCurrentThread(), L"NKS CPU sensor reader");
    while (!stop.stop_requested()) {
        std::unique_lock lock(mutex);
        if (!ready.wait(lock, stop, [this] { return queued; })) break;
        const auto version = requestGeneration;
        const auto token = requestStop.get_token();
        queued = false; reading = true;
        lock.unlock();
        std::shared_ptr<const Reading> result;
        bool failure{};
        try { result = std::make_shared<Reading>(read(token)); }
        catch (const SensorCancelled&) {}
        catch (const std::exception&) { failure = true; }
        lock.lock();
        reading = false;
        if (enabled && generation == version && !token.stop_requested()) {
            // Only collect() schedules another request; pause leaves this thread idle.
            latest.swap(result); failed = failure; lastFinished = std::chrono::steady_clock::now();
        }
        lock.unlock();
    }
}

std::vector<HardwareFact> CpuSensors::collect(bool requested)
{
    std::shared_ptr<const Reading> snapshot, retired;
    bool failure{}, pending{};
    {
        std::lock_guard lock(mutex);
        if (!requested) {
            if (enabled) { enabled = false; queued = false; ++generation; requestStop.request_stop(); }
            latest.swap(retired); failed = false; lastFinished = {};
        } else {
            if (!enabled) { enabled = true; ++generation; }
            const auto now = std::chrono::steady_clock::now();
            if (!worker.joinable() && (lastFinished == std::chrono::steady_clock::time_point{} || now - lastFinished >= std::chrono::seconds(5))) {
                try { worker = std::jthread([this](std::stop_token stop) { run(stop); }); }
                catch (const std::system_error&) { failed = true; lastFinished = now; }
            }
            if (worker.joinable() && !reading && !queued && (lastFinished == std::chrono::steady_clock::time_point{} || now - lastFinished >= std::chrono::seconds(5))) {
                requestStop = std::stop_source{}; requestGeneration = generation; queued = true; ready.notify_one();
            }
            snapshot = latest; failure = failed; pending = queued || reading;
        }
    }
    if (!requested)
        return {{L"Stav", L"Vypnuto; žádné dotazy na čidla"}};
    const auto unixMs = std::chrono::duration_cast<std::chrono::milliseconds>(std::chrono::system_clock::now().time_since_epoch()).count();
    std::size_t current{};
    std::vector<HardwareFact> facts;
    if (snapshot) for (const auto& sample : snapshot->samples) {
        const bool fresh = sample.publishedAtUnixMs <= unixMs && unixMs - sample.publishedAtUnixMs <= 15000;
        if (fresh) ++current;
        std::wostringstream value; value.imbue(std::locale::classic());
        value << std::fixed << std::setprecision(1) << sample.celsius << L" °C · " << utc(sample.publishedAtUnixMs)
            << (fresh ? L" · publikovaný vzorek" : L" · zastaralé nebo nejistý čas");
        if (sample.publishedAtUnixMs <= unixMs) value << L" · stáří " << static_cast<double>(unixMs - sample.publishedAtUnixMs) / 1000.0 << L" s";
        else value << L" · publikovaný čas je v budoucnu";
        facts.push_back({sample.name + L" [" + sample.id + L"]", value.str()});
    }
    const auto state = snapshot ? snapshot->state : failure ? SampleState::error : SampleState::first;
    const auto status = state == SampleState::first ? L"Načítání čidel"
        : state == SampleState::valid ? (current == snapshot->samples.size() ? L"Publikované CPU vzorky" : L"Zastaralé nebo částečně zastaralé vzorky")
        : state == SampleState::accessDenied ? L"Přístup zamítnut" : state == SampleState::unsupported ? L"Nepodporováno"
        : state == SampleState::error ? L"Chyba čtecího API" : L"Poskytovatel nedostupný";
    facts.insert(facts.begin(), {{L"Stav", status}, {L"Zdroj", L"Místní HTTP API kompatibilní s LibreHardwareMonitor; identita poskytovatele neověřena"},
        {L"Adresa čtení", endpoint}, {L"Poslední čtení API", snapshot ? utc(snapshot->readAtUnixMs) : L"Dosud neproběhlo"},
        {L"Význam měření", L"Teplotní čidla typu CPU; °C, poslední publikovaný vzorek, nikoli ACPI thermal zone"},
        {L"Platnost čtení", L"Čtení nejvýše jednou za 5 s; stáří publikovaného UTC vzorku nejvýše 15 s. Změna hodin může vzorek zneplatnit."}});
    facts.push_back({L"Aktualizace", pending ? L"Čtení probíhá" : L"Čeká na další čtení"});
    if (snapshot && !snapshot->detail.empty()) facts.push_back({L"Omezení", snapshot->detail});
    if (failure) facts.push_back({L"Omezení", L"Pracovní vlákno nemohlo dokončit čtení"});
    return facts;
}

}
