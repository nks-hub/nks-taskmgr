#pragma once
#include <winsock2.h>
#include <ifdef.h>
#include <ipifcons.h>
#include <algorithm>
#include <cwctype>
#include <initializer_list>
#include <string_view>

namespace nks {

enum class NetworkInterfaceClass { physical, vpn, pointToPoint, auxiliary };

struct NetworkInterfaceTraits {
    unsigned type{};
    TUNNEL_TYPE tunnel{TUNNEL_TYPE_NONE};
    bool hardware{}, connected{}, rasVpn{};
    std::wstring_view alias, description;
};

inline bool networkNameContains(std::wstring_view value, std::wstring_view hint)
{
    return std::search(value.begin(), value.end(), hint.begin(), hint.end(), [](wchar_t a, wchar_t b) {
        return std::towupper(a) == std::towupper(b);
    }) != value.end();
}

inline NetworkInterfaceClass classifyNetworkInterface(const NetworkInterfaceTraits& value)
{
    if (value.type == IF_TYPE_SOFTWARE_LOOPBACK) return NetworkInterfaceClass::auxiliary;
    if (value.rasVpn) return NetworkInterfaceClass::vpn;
    if (value.tunnel == TUNNEL_TYPE_6TO4 || value.tunnel == TUNNEL_TYPE_ISATAP
        || value.tunnel == TUNNEL_TYPE_TEREDO || value.tunnel == TUNNEL_TYPE_IPHTTPS) return NetworkInterfaceClass::auxiliary;
    for (const auto hint : {L"Wintun", L"TAP-Windows", L"WireGuard", L"OpenVPN", L"Tailscale", L"ZeroTier", L"SoftEther VPN", L"Cloudflare WARP",
        L"Cisco AnyConnect", L"Cisco Secure Client", L"Fortinet SSL VPN", L"Fortinet SSLVPN", L"FortiClient", L"PANGP Virtual Ethernet", L"GlobalProtect"})
        if (networkNameContains(value.description, hint) || networkNameContains(value.alias, hint)) return NetworkInterfaceClass::vpn;
    // Connected PPP includes native RAS VPNs even when RAS metadata is unavailable.
    if (value.type == IF_TYPE_PPP) return value.connected ? NetworkInterfaceClass::pointToPoint : NetworkInterfaceClass::auxiliary;
    if (value.type == IF_TYPE_TUNNEL || !value.hardware) return NetworkInterfaceClass::auxiliary;
    return NetworkInterfaceClass::physical;
}

inline const wchar_t* networkInterfaceClassName(NetworkInterfaceClass value)
{
    switch (value) {
    case NetworkInterfaceClass::physical: return L"Fyzické";
    case NetworkInterfaceClass::vpn: return L"VPN";
    case NetworkInterfaceClass::pointToPoint: return L"PPP";
    default: return L"Pomocné / virtuální";
    }
}

}
