using System.Runtime.InteropServices;

namespace EntityForge.Db.Kernel;

/// <summary>Reads Linux's disciplined UTC clock and reported maximum error without changing the clock.</summary>
/// <remarks>
/// Requires a qualified NTP/PTP service that maintains the kernel synchronisation status and
/// maximum-error estimate. A running daemon, a PHC alone, or zero offset is not sufficient.
/// The operator must ensure that the reported bound includes the trusted source's error.
/// Unsynchronised clocks, leap transitions and excessive uncertainty fail closed.
/// This provider currently supports the Linux x64 timex ABI. Other deployments can implement
/// <see cref="IDatabaseBoundedClock"/> for another qualified clock service.
/// </remarks>
public sealed class LinuxDisciplinedDatabaseClock : IDatabaseBoundedClock
{
    private const int Synchronized = 0;
    private const int UnsynchronizedStatus = 0x0040;
    private const int ClockErrorStatus = 0x1000;
    private const int NanosecondStatus = 0x2000;
    private const long MicrosecondsPerSecond = 1_000_000;
    private const long NanosecondsPerSecond = 1_000_000_000;
    private static readonly TimeSpan MaximumObservationDuration = TimeSpan.FromMilliseconds(100);
    private readonly TimeProvider _time;
    private readonly Func<Reading> _read;
    private readonly MonitoredDatabaseClock _clock;
    private readonly long _driftPartsPerMillion;

    /// <inheritdoc />
    public Guid Domain => _clock.Domain;

    /// <summary>Creates a Linux x64 disciplined-clock reader for the supplied clock domain, uncertainty bound and maximum drift in parts per million.</summary>
    public LinuxDisciplinedDatabaseClock(Guid domain, TimeSpan maximumUncertainty, long maximumDriftPartsPerMillion)
        : this(domain, maximumUncertainty, maximumDriftPartsPerMillion, TimeProvider.System, PlatformReader())
    {
    }

    internal LinuxDisciplinedDatabaseClock(Guid domain, TimeSpan maximumUncertainty, long maximumDriftPartsPerMillion,
        TimeProvider time, Func<Reading> read)
    {
        _time = time;
        _read = read;
        _driftPartsPerMillion = maximumDriftPartsPerMillion;
        _clock = new(domain, time, Observe, MaximumObservationDuration, maximumUncertainty, maximumDriftPartsPerMillion);
    }

    /// <inheritdoc />
    public DatabaseClockInterval Read() => _clock.Read();

    private DatabaseClockObservation Observe()
    {
        long start = _time.GetTimestamp();
        Reading value = _read();
        long end = _time.GetTimestamp();
        TimeSpan duration = _time.GetElapsedTime(start, end);
        long units = (value.Status & NanosecondStatus) != 0 ? NanosecondsPerSecond : MicrosecondsPerSecond;
        if (value.State != Synchronized || (value.Status & (UnsynchronizedStatus | ClockErrorStatus)) != 0 ||
            value.MaximumErrorMicroseconds < 0 || value.PrecisionMicroseconds < 0 || value.Seconds < 0 || value.Fraction < 0 || value.Fraction >= units ||
            duration < TimeSpan.Zero || duration > MaximumObservationDuration)
        {
            throw new InvalidOperationException("Linux clock does not provide an admissible synchronised UTC bound.");
        }

        decimal fraction = (decimal)value.Fraction * TimeSpan.TicksPerSecond / units;
        long whole = checked(DateTime.UnixEpoch.Ticks + value.Seconds * TimeSpan.TicksPerSecond);
        long error = checked((value.MaximumErrorMicroseconds + value.PrecisionMicroseconds) * (TimeSpan.TicksPerSecond / MicrosecondsPerSecond));
        long drift = checked((long)decimal.Ceiling((decimal)duration.Ticks * _driftPartsPerMillion / MicrosecondsPerSecond));
        long resolution = checked((long)decimal.Ceiling((decimal)TimeSpan.TicksPerSecond / _time.TimestampFrequency));
        // The syscall's observation occurs somewhere in [start,end]. Include its
        // whole duration and quantisation; never equate a returned offset with accuracy.
        long earliest = checked(whole + (long)decimal.Floor(fraction) - error - resolution - drift);
        long latest = checked(whole + (long)decimal.Ceiling(fraction) + error + duration.Ticks + resolution + drift);
        return new(earliest, latest, end, true);
    }

    internal readonly record struct Reading(int State, int Status, long Seconds, long Fraction, long MaximumErrorMicroseconds, long PrecisionMicroseconds = 0);

    private static Func<Reading> PlatformReader()
    {
        if (!OperatingSystem.IsLinux() || RuntimeInformation.ProcessArchitecture != Architecture.X64)
        {
            throw new PlatformNotSupportedException("Linux disciplined clocks require the qualified Linux x64 timex ABI.");
        }

        return () =>
        {
            var value = new Timex(); // modes=0: observation only; no clock adjustment.
            int state = ReadTimex(ref value);
            if (state < 0)
            {
                throw new IOException($"adjtimex observation failed (errno {Marshal.GetLastPInvokeError()}).");
            }

            return new(state, value.Status, value.Seconds, value.Fraction, value.MaximumError, value.Precision);
        };
    }

    [StructLayout(LayoutKind.Explicit, Size = 208)]
    private struct Timex
    {
        [FieldOffset(0)] internal uint Modes;
        [FieldOffset(24)] internal long MaximumError;
        [FieldOffset(40)] internal int Status;
        [FieldOffset(56)] internal long Precision;
        [FieldOffset(72)] internal long Seconds;
        [FieldOffset(80)] internal long Fraction;
    }

    [DllImport("libc", EntryPoint = "adjtimex", SetLastError = true)]
    private static extern int ReadTimex(ref Timex value);
}
