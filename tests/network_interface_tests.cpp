#include "collectors/network_interface.hpp"
#include <iostream>
#include <stdexcept>

int main()
{
    using Class = nks::NetworkInterfaceClass;
    const struct {
        const char* name;
        nks::NetworkInterfaceTraits traits;
        Class expected;
    } cases[] = {
        {"connected Ethernet", {IF_TYPE_ETHERNET_CSMACD, TUNNEL_TYPE_NONE, true, true, false, L"Ethernet", L"Ethernet adapter"}, Class::physical},
        {"disconnected Wi-Fi", {IF_TYPE_IEEE80211, TUNNEL_TYPE_NONE, true, false, false, L"Wi-Fi", L"Wireless adapter"}, Class::physical},
        {"software switch", {IF_TYPE_ETHERNET_CSMACD, TUNNEL_TYPE_NONE, false, true, false, L"vEthernet", L"Hyper-V Virtual Ethernet Adapter"}, Class::auxiliary},
        {"loopback", {IF_TYPE_SOFTWARE_LOOPBACK, TUNNEL_TYPE_NONE, false, true, false, L"Loopback", L"Loopback"}, Class::auxiliary},
        {"Npcap loopback with hardware Ethernet flags", {IF_TYPE_ETHERNET_CSMACD, TUNNEL_TYPE_NONE, true, true, false, L"Npcap Loopback Adapter", L"Npcap Loopback Adapter"}, Class::auxiliary},
        {"renamed Npcap loopback driver", {IF_TYPE_ETHERNET_CSMACD, TUNNEL_TYPE_NONE, true, true, false, L"Capture", L"Npcap Loopback Adapter"}, Class::auxiliary},
        {"physical Ethernet with Npcap loopback alias", {IF_TYPE_ETHERNET_CSMACD, TUNNEL_TYPE_NONE, true, true, false, L"Npcap Loopback Adapter", L"Ethernet adapter"}, Class::physical},
        {"explicit RAS VPN before capture loopback hint", {IF_TYPE_ETHERNET_CSMACD, TUNNEL_TYPE_NONE, true, true, true, L"VPN", L"Npcap Loopback Adapter"}, Class::vpn},
        {"system Teredo before name hint", {IF_TYPE_TUNNEL, TUNNEL_TYPE_TEREDO, false, true, false, L"OpenVPN", L"Teredo Tunneling"}, Class::auxiliary},
        {"system ISATAP", {IF_TYPE_TUNNEL, TUNNEL_TYPE_ISATAP, false, true, false, L"ISATAP", L"ISATAP"}, Class::auxiliary},
        {"system 6to4", {IF_TYPE_TUNNEL, TUNNEL_TYPE_6TO4, false, true, false, L"6to4", L"6to4"}, Class::auxiliary},
        {"system IP-HTTPS", {IF_TYPE_TUNNEL, TUNNEL_TYPE_IPHTTPS, false, true, false, L"IP-HTTPS", L"IP-HTTPS"}, Class::auxiliary},
        {"connected RAS WAN VPN", {IF_TYPE_PPP, TUNNEL_TYPE_NONE, false, true, true, L"Work VPN", L"WAN Miniport (IKEv2)"}, Class::vpn},
        {"connected RAS unknown tunnel", {IF_TYPE_TUNNEL, TUNNEL_TYPE_OTHER, false, true, true, L"Work VPN", L"WAN Miniport"}, Class::vpn},
        {"connected PPP without RAS metadata", {IF_TYPE_PPP, TUNNEL_TYPE_NONE, false, true, false, L"PPP", L"WAN Miniport (IKEv2)"}, Class::pointToPoint},
        {"inactive WAN miniport", {IF_TYPE_PPP, TUNNEL_TYPE_NONE, false, false, false, L"WAN", L"WAN Miniport (IKEv2)"}, Class::auxiliary},
        {"WAN text is not a blacklist", {IF_TYPE_ETHERNET_CSMACD, TUNNEL_TYPE_NONE, true, true, false, L"WAN", L"WAN Ethernet"}, Class::physical},
        {"Wintun", {IF_TYPE_TUNNEL, TUNNEL_TYPE_OTHER, false, true, false, L"VPN", L"Wintun Userspace Tunnel"}, Class::vpn},
        {"TAP VPN", {IF_TYPE_ETHERNET_CSMACD, TUNNEL_TYPE_NONE, false, true, false, L"VPN", L"TAP-Windows Adapter V9"}, Class::vpn},
        {"disconnected WireGuard", {IF_TYPE_TUNNEL, TUNNEL_TYPE_OTHER, false, false, false, L"VPN", L"WireGuard Tunnel"}, Class::vpn},
        {"VPN alias case insensitive", {IF_TYPE_TUNNEL, TUNNEL_TYPE_OTHER, false, true, false, L"tailscale", L"Tunnel adapter"}, Class::vpn},
        {"Cisco AnyConnect software Ethernet", {IF_TYPE_ETHERNET_CSMACD, TUNNEL_TYPE_NONE, false, true, false, L"Ethernet", L"Cisco AnyConnect Secure Mobility Client Virtual Miniport Adapter"}, Class::vpn},
        {"Cisco Secure Client software Ethernet", {IF_TYPE_ETHERNET_CSMACD, TUNNEL_TYPE_NONE, false, true, false, L"Ethernet", L"Cisco Secure Client Virtual Miniport Adapter"}, Class::vpn},
        {"Fortinet SSL VPN software Ethernet", {IF_TYPE_ETHERNET_CSMACD, TUNNEL_TYPE_NONE, false, true, false, L"Ethernet", L"Fortinet SSL VPN Virtual Ethernet Adapter"}, Class::vpn},
        {"FortiClient software Ethernet", {IF_TYPE_ETHERNET_CSMACD, TUNNEL_TYPE_NONE, false, true, false, L"Ethernet", L"FortiClient Virtual Ethernet Adapter"}, Class::vpn},
        {"PANGP software Ethernet", {IF_TYPE_ETHERNET_CSMACD, TUNNEL_TYPE_NONE, false, true, false, L"Ethernet", L"PANGP Virtual Ethernet Adapter"}, Class::vpn},
        {"GlobalProtect software Ethernet", {IF_TYPE_ETHERNET_CSMACD, TUNNEL_TYPE_NONE, false, true, false, L"Ethernet", L"GlobalProtect Virtual Ethernet Adapter"}, Class::vpn},
        {"Docker software Ethernet", {IF_TYPE_ETHERNET_CSMACD, TUNNEL_TYPE_NONE, false, true, false, L"vEthernet (DockerNAT)", L"Hyper-V Virtual Ethernet Adapter"}, Class::auxiliary},
        {"Hyper-V default switch", {IF_TYPE_ETHERNET_CSMACD, TUNNEL_TYPE_NONE, false, true, false, L"vEthernet (Default Switch)", L"Hyper-V Virtual Ethernet Adapter #2"}, Class::auxiliary},
        {"unidentified software tunnel", {IF_TYPE_TUNNEL, TUNNEL_TYPE_OTHER, false, true, false, L"Tunnel", L"Tunnel adapter"}, Class::auxiliary},
    };
    try {
        for (const auto& test : cases) {
            if (nks::classifyNetworkInterface(test.traits) != test.expected) throw std::runtime_error(test.name);
            std::cout << "PASS network classification: " << test.name << '\n';
        }
        return 0;
    } catch (const std::exception& error) {
        std::cerr << "FAIL network classification: " << error.what() << '\n';
        return 1;
    }
}
