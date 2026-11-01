using NksTaskMgr.Native;

namespace NksTaskMgr.WinUI;

public readonly record struct ProcessKey(BootIdentifier Boot, uint Pid, GenerationKind Kind, ulong Generation);
