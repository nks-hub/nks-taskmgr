#pragma once

#include <atomic>
#include <cstddef>
#include <limits>
#include <new>
#include <string>
#include <type_traits>
#include <vector>

namespace nks {

inline constexpr std::size_t eventBudgetLimit = 8 * 1024 * 1024;
inline std::atomic<std::size_t> eventBudgetUsed{};

// All sessions and retained frames share this budget, including allocator overhead.
template<class T> struct EventAllocator {
    using value_type = T;
    using is_always_equal = std::true_type;
    EventAllocator() noexcept = default;
    template<class U> EventAllocator(const EventAllocator<U>&) noexcept {}
    T* allocate(std::size_t count) {
        if (count > (eventBudgetLimit - 64) / sizeof(T)) throw std::bad_alloc{};
        const auto charge = count * sizeof(T) + 64;
        auto used = eventBudgetUsed.load();
        do { if (used > eventBudgetLimit - charge) throw std::bad_alloc{}; }
        while (!eventBudgetUsed.compare_exchange_weak(used, used + charge));
        try { return static_cast<T*>(::operator new(count * sizeof(T))); }
        catch (...) { eventBudgetUsed.fetch_sub(charge); throw; }
    }
    void deallocate(T* pointer, std::size_t count) noexcept {
        ::operator delete(pointer); eventBudgetUsed.fetch_sub(count * sizeof(T) + 64);
    }
    template<class U> bool operator==(const EventAllocator<U>&) const noexcept { return true; }
};
template<class T> using EventVector = std::vector<T, EventAllocator<T>>;
using EventText = std::basic_string<wchar_t, std::char_traits<wchar_t>, EventAllocator<wchar_t>>;

}
