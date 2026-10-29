#include "interop/monitor_api.h"
#include "interop/services_api.h"

_Static_assert(sizeof(NksFrameView) == 288, "C frame layout changed");
_Static_assert(sizeof(NksProcess) == 152, "C process layout changed");
_Static_assert(offsetof(NksProcess, name) == 136, "C UTF-16 name offset changed");
_Static_assert(sizeof(NksServiceConfig) == 80 && sizeof(NksService) == 128, "C service layout changed");
_Static_assert(sizeof(NksServiceDetail) == 168 && sizeof(NksServiceFrameView) == 248, "C service detail layout changed");

NksResult nks_test_c_version(NksMonitor** result)
{
    return nks_monitor_create(NKS_MONITOR_ABI_VERSION + 1, 250, 1, result);
}

NksResult nks_test_c_services_version(NksServices** result)
{
    return nks_services_create(NKS_SERVICES_ABI_VERSION + 1, result);
}
