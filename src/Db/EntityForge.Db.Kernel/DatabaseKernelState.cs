namespace EntityForge.Db.Kernel;

/// <summary>Describes the kernel's recovery, serving and disposal lifecycle.</summary>
public enum DatabaseKernelState
{
    /// <summary>The kernel has been created but recovery has not completed.</summary>
    Created,
    /// <summary>The kernel is restoring durable state.</summary>
    Recovering,
    /// <summary>The kernel is recovered and may admit operations.</summary>
    Ready,
    /// <summary>The kernel requires recovery before further ordinary operations.</summary>
    RecoveryRequired,
    /// <summary>The kernel has entered a faulted state.</summary>
    Faulted,
    /// <summary>The kernel has released its owned resources and cannot admit operations.</summary>
    Disposed
}
