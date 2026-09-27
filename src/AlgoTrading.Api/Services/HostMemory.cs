// src/AlgoTrading.Api/Services/HostMemory.cs
namespace AlgoTrading.Api.Services;

/// <summary>How much memory a new process could get, read fresh on every call.</summary>
public interface IHostMemory
{
    /// <summary>
    /// Available memory in bytes, or null where it cannot be read (not Linux,
    /// or /proc unreadable). Null means "unknown", never "none".
    /// </summary>
    long? AvailableBytes();
}

/// <summary>
/// MemAvailable from /proc/meminfo: the kernel's own estimate of what a new
/// process could get without swapping. MemFree would count the page cache as
/// used and read "full" on any healthy box.
/// </summary>
public sealed class ProcHostMemory : IHostMemory
{
    private const string MemInfoPath = "/proc/meminfo";

    public long? AvailableBytes()
    {
        if (!OperatingSystem.IsLinux()) return null;

        try
        {
            return ProcText.ParseMemInfo(File.ReadAllText(MemInfoPath))?.AvailableBytes;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }
}
