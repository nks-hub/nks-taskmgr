#include "interop/monitor_api.h"

_Static_assert(sizeof(NksFrameView) == 288, "C frame layout changed");
_Static_assert(sizeof(NksProcess) == 152, "C process layout changed");
_Static_assert(offsetof(NksProcess, name) == 136, "C UTF-16 name offset changed");

NksResult nks_test_c_version(NksMonitor** result)
{
    return nks_monitor_create(NKS_MONITOR_ABI_VERSION + 1, 250, 1, result);
}
