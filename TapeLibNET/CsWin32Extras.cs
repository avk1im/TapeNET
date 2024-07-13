using System.Formats.Asn1;
using System.Runtime.InteropServices;
using System.Text;
using Windows.Win32.Foundation;

namespace Windows.Win32
{
    namespace System.SystemServices
    {
        public static class Helpers
        {
            public static double ToPrecision(double value, int precision)
            {
                if (precision < 0 || precision > 15)
                    return value;

                double factor = Math.Pow(10.0, precision);

                return Math.Floor(value * factor) / factor;
            }

            public static double BytesToKBytes(long bytes, int precision = 2) => ToPrecision(bytes / 1024, precision);
            public static double BytesToMBytes(long bytes, int precision = 2) => ToPrecision(bytes / (double)(1024 * 1024), precision);
            public static double BytesToGBytes(long bytes, int precision = 2) => ToPrecision(bytes / (double)(1024 * 1024 * 1024), precision);
            public static double BytesToTBytes(long bytes, int precision = 2) => ToPrecision(bytes / (double)(1024L * 1024 * 1024 * 1024), precision);
            public static double BytesToPBytes(long bytes, int precision = 2) => ToPrecision(bytes / (double)(1024L * 1024 * 1024 * 1024 * 1024), precision);
            public static string BytesToString(long bytes, int precision = 2)
            {
                if (bytes < 1024)
                    return $"{bytes} B";
                else if (bytes < 1024 * 1024)
                    return $"{BytesToKBytes(bytes, precision)} KB";
                else if (bytes < 1024 * 1024 * 1024)
                    return $"{BytesToMBytes(bytes, precision)} MB";
                else if (bytes < 1024L * 1024 * 1024 * 1024)
                    return $"{BytesToGBytes(bytes, precision)} GB";
                else if (bytes < 1024L * 1024 * 1024 * 1024 * 1024)
                    return $"{BytesToTBytes(bytes, precision)} TB";
                else
                    return $"{BytesToPBytes(bytes, precision)} PB";
            }
            public static string BytesToStringLong(long bytes, int precision = 2)
            {
                return $"{bytes:N0} bytes" + $"{((bytes > 1024) ? " ~ " + BytesToString(bytes, precision) : string.Empty)}";
            }

            public static uint LoDWORD(int n)
            {
                return (uint)n;
            }
            public static uint HiDWORD(int n)
            {
                return (uint)(((long)n) >> 32);
            }

            public static uint LoDWORD(long n)
            {
                return (uint)(n & 0xFFFFFFFF);
            }
            public static uint HiDWORD(long n)
            {
                return (uint)(n >> 32);
            }

            public static long MakeLong(uint low, uint high)
            {
                return ((long)high << 32) | low;
            }
        } // class Helpers


        public class Stopwatch
        {
            private readonly long m_frequency; // Frequency of the performance counter
            private long m_startTicks = 0L; // Start ticks
            private long m_stopTicks = 0L; // Stop ticks
            public bool IsRunning { get; private set; } = false; // Indicates if the stopwatch is running

            // Constructor initializes the frequency
            public Stopwatch()
            {
                if (!PInvoke.QueryPerformanceFrequency(out m_frequency))
                {
                    throw new NotSupportedException("High-precision timer not available on this system.");
                }
            }

            // Starts the stopwatch
            public void Start()
            {
                if (IsRunning)
                    return;

                IsRunning = true;

                if (m_startTicks == 0) // haven't run yet
                    PInvoke.QueryPerformanceCounter(out m_startTicks);
            }

            public void Restart()
            {
                Reset();
                Start();
            }

            // Stops the stopwatch
            public void Stop()
            {
                if (!IsRunning)
                    return;

                PInvoke.QueryPerformanceCounter(out m_stopTicks);
                IsRunning = false;
            }

            // Resets the stopwatch
            public void Reset()
            {
                m_startTicks = 0;
                m_stopTicks = 0;
                IsRunning = false;
            }

            // Elapsed time in microseconds
            public long ElapsedMicroseconds
            {
                get
                {
                    if (IsRunning)
                    {
                        PInvoke.QueryPerformanceCounter(out var currentTicks);
                        return (currentTicks - m_startTicks) * 1_000_000 / m_frequency;
                    }
                    else
                    {
                        return (m_stopTicks - m_startTicks) * 1_000_000 / m_frequency;
                    }
                }
            }

            public double ElapsedSeconds => ElapsedMicroseconds / 1e6;

            // Elapsed time as a TimeSpan
            public TimeSpan ElapsedTimeSpan => TimeSpan.FromTicks(ElapsedMicroseconds * 10);
        } // class Stopwatch


        // FIX because CsWin32 generates these as PInvoke constants
        [Flags]
        internal enum TAPE_GET_DRIVE_PARAMETERS_FEATURES_LOW : uint
        {
            TAPE_DRIVE_FIXED = 0x00000001,
            TAPE_DRIVE_SELECT = 0x00000002,
            TAPE_DRIVE_INITIATOR = 0x00000004,

            TAPE_DRIVE_ERASE_SHORT = 0x00000010,
            TAPE_DRIVE_ERASE_LONG = 0x00000020,
            TAPE_DRIVE_ERASE_BOP_ONLY = 0x00000040,
            TAPE_DRIVE_ERASE_IMMEDIATE = 0x00000080,

            TAPE_DRIVE_TAPE_CAPACITY = 0x00000100,
            TAPE_DRIVE_TAPE_REMAINING = 0x00000200,
            TAPE_DRIVE_FIXED_BLOCK = 0x00000400,
            TAPE_DRIVE_VARIABLE_BLOCK = 0x00000800,

            TAPE_DRIVE_WRITE_PROTECT = 0x00001000,
            TAPE_DRIVE_EOT_WZ_SIZE = 0x00002000,

            TAPE_DRIVE_ECC = 0x00010000,
            TAPE_DRIVE_COMPRESSION = 0x00020000,
            TAPE_DRIVE_PADDING = 0x00040000,
            TAPE_DRIVE_REPORT_SMKS = 0x00080000,

            TAPE_DRIVE_GET_ABSOLUTE_BLK = 0x00100000,
            TAPE_DRIVE_GET_LOGICAL_BLK = 0x00200000,
            TAPE_DRIVE_SET_EOT_WZ_SIZE = 0x00400000,

            TAPE_DRIVE_EJECT_MEDIA = 0x01000000,
            TAPE_DRIVE_CLEAN_REQUESTS = 0x02000000,
            TAPE_DRIVE_SET_CMP_BOP_ONLY = 0x04000000,

            //TAPE_DRIVE_RESERVED_BIT = 0x80000000,  //don't use this bit!
        }

        internal partial struct TAPE_GET_DRIVE_PARAMETERS
        {
            public readonly bool HasFeature(TAPE_GET_DRIVE_PARAMETERS_FEATURES_LOW feature) =>
                ((TAPE_GET_DRIVE_PARAMETERS_FEATURES_LOW)FeaturesLow).HasFlag(feature);
            public readonly bool HasFeature(TAPE_GET_DRIVE_PARAMETERS_FEATURES_HIGH feature)
            {
                TAPE_GET_DRIVE_PARAMETERS_FEATURES_HIGH fixFeaturesHigh = (TAPE_GET_DRIVE_PARAMETERS_FEATURES_HIGH)(((uint)FeaturesHigh) | PInvoke.TAPE_DRIVE_HIGH_FEATURES);
                return fixFeaturesHigh.HasFlag(feature);
            }

            // Shortcuts to useful feature flags
            public readonly bool CreatesFixedPartitions => HasFeature(TAPE_GET_DRIVE_PARAMETERS_FEATURES_LOW.TAPE_DRIVE_FIXED);
            public readonly bool CreatesSelectPartitions => HasFeature(TAPE_GET_DRIVE_PARAMETERS_FEATURES_LOW.TAPE_DRIVE_SELECT);
            public readonly bool CreatesInitiatorPartitions => HasFeature(TAPE_GET_DRIVE_PARAMETERS_FEATURES_LOW.TAPE_DRIVE_INITIATOR);
            public readonly bool SupportsSetmarks => HasFeature(TAPE_GET_DRIVE_PARAMETERS_FEATURES_HIGH.TAPE_DRIVE_SETMARKS);
            public readonly bool SupportsFilemarks => HasFeature(TAPE_GET_DRIVE_PARAMETERS_FEATURES_HIGH.TAPE_DRIVE_FILEMARKS);
            public readonly bool SupportsSeqFilemarks => HasFeature(TAPE_GET_DRIVE_PARAMETERS_FEATURES_HIGH.TAPE_DRIVE_SEQUENTIAL_FMKS);
            public readonly bool SupportsEndOfData => HasFeature(TAPE_GET_DRIVE_PARAMETERS_FEATURES_HIGH.TAPE_DRIVE_END_OF_DATA);

            public override readonly string ToString()
            {
                StringBuilder sb = new();

                sb.Append("ECC: " + (bool)ECC);
                sb.Append("\nCompression: " + (bool)Compression);
                sb.Append("\nDefault block size: " + Helpers.BytesToString(DefaultBlockSize));
                sb.Append("\nMaximum block size: " + Helpers.BytesToString(MaximumBlockSize));
                sb.Append("\nMinimum block size: " + Helpers.BytesToString(MinimumBlockSize));
                sb.Append("\nMaximum partition count: " + MaximumPartitionCount);
                sb.Append("\nWarning zone size: " + Helpers.BytesToString(EOTWarningZoneSize));
                
                sb.Append($"\n{nameof(TAPE_GET_DRIVE_PARAMETERS_FEATURES_LOW.TAPE_DRIVE_FIXED)}: " + HasFeature(TAPE_GET_DRIVE_PARAMETERS_FEATURES_LOW.TAPE_DRIVE_FIXED));
                sb.Append($"\n{nameof(TAPE_GET_DRIVE_PARAMETERS_FEATURES_LOW.TAPE_DRIVE_SELECT)}: " + HasFeature(TAPE_GET_DRIVE_PARAMETERS_FEATURES_LOW.TAPE_DRIVE_SELECT));
                sb.Append($"\n{nameof(TAPE_GET_DRIVE_PARAMETERS_FEATURES_LOW.TAPE_DRIVE_INITIATOR)}: " + HasFeature(TAPE_GET_DRIVE_PARAMETERS_FEATURES_LOW.TAPE_DRIVE_INITIATOR));
                sb.Append($"\n{nameof(TAPE_GET_DRIVE_PARAMETERS_FEATURES_LOW.TAPE_DRIVE_ERASE_SHORT)}: " + HasFeature(TAPE_GET_DRIVE_PARAMETERS_FEATURES_LOW.TAPE_DRIVE_FIXED));
                sb.Append($"\n{nameof(TAPE_GET_DRIVE_PARAMETERS_FEATURES_LOW.TAPE_DRIVE_ERASE_LONG)}: " + HasFeature(TAPE_GET_DRIVE_PARAMETERS_FEATURES_LOW.TAPE_DRIVE_ERASE_LONG));
                sb.Append($"\n{nameof(TAPE_GET_DRIVE_PARAMETERS_FEATURES_LOW.TAPE_DRIVE_ERASE_BOP_ONLY)}: " + HasFeature(TAPE_GET_DRIVE_PARAMETERS_FEATURES_LOW.TAPE_DRIVE_ERASE_BOP_ONLY));
                sb.Append($"\n{nameof(TAPE_GET_DRIVE_PARAMETERS_FEATURES_LOW.TAPE_DRIVE_ERASE_IMMEDIATE)}: " + HasFeature(TAPE_GET_DRIVE_PARAMETERS_FEATURES_LOW.TAPE_DRIVE_ERASE_IMMEDIATE));
                sb.Append($"\n{nameof(TAPE_GET_DRIVE_PARAMETERS_FEATURES_LOW.TAPE_DRIVE_TAPE_CAPACITY)}: " + HasFeature(TAPE_GET_DRIVE_PARAMETERS_FEATURES_LOW.TAPE_DRIVE_TAPE_CAPACITY));
                sb.Append($"\n{nameof(TAPE_GET_DRIVE_PARAMETERS_FEATURES_LOW.TAPE_DRIVE_TAPE_REMAINING)}: " + HasFeature(TAPE_GET_DRIVE_PARAMETERS_FEATURES_LOW.TAPE_DRIVE_TAPE_REMAINING));
                sb.Append($"\n{nameof(TAPE_GET_DRIVE_PARAMETERS_FEATURES_LOW.TAPE_DRIVE_FIXED_BLOCK)}: " + HasFeature(TAPE_GET_DRIVE_PARAMETERS_FEATURES_LOW.TAPE_DRIVE_FIXED_BLOCK));
                sb.Append($"\n{nameof(TAPE_GET_DRIVE_PARAMETERS_FEATURES_LOW.TAPE_DRIVE_VARIABLE_BLOCK)}: " + HasFeature(TAPE_GET_DRIVE_PARAMETERS_FEATURES_LOW.TAPE_DRIVE_VARIABLE_BLOCK));
                sb.Append($"\n{nameof(TAPE_GET_DRIVE_PARAMETERS_FEATURES_LOW.TAPE_DRIVE_WRITE_PROTECT)}: " + HasFeature(TAPE_GET_DRIVE_PARAMETERS_FEATURES_LOW.TAPE_DRIVE_WRITE_PROTECT));
                sb.Append($"\n{nameof(TAPE_GET_DRIVE_PARAMETERS_FEATURES_LOW.TAPE_DRIVE_EOT_WZ_SIZE)}: " + HasFeature(TAPE_GET_DRIVE_PARAMETERS_FEATURES_LOW.TAPE_DRIVE_EOT_WZ_SIZE));
                sb.Append($"\n{nameof(TAPE_GET_DRIVE_PARAMETERS_FEATURES_LOW.TAPE_DRIVE_ECC)}: " + HasFeature(TAPE_GET_DRIVE_PARAMETERS_FEATURES_LOW.TAPE_DRIVE_ECC));
                sb.Append($"\n{nameof(TAPE_GET_DRIVE_PARAMETERS_FEATURES_LOW.TAPE_DRIVE_COMPRESSION)}: " + HasFeature(TAPE_GET_DRIVE_PARAMETERS_FEATURES_LOW.TAPE_DRIVE_COMPRESSION));
                sb.Append($"\n{nameof(TAPE_GET_DRIVE_PARAMETERS_FEATURES_LOW.TAPE_DRIVE_PADDING)}: " + HasFeature(TAPE_GET_DRIVE_PARAMETERS_FEATURES_LOW.TAPE_DRIVE_PADDING));
                sb.Append($"\n{nameof(TAPE_GET_DRIVE_PARAMETERS_FEATURES_LOW.TAPE_DRIVE_REPORT_SMKS)}: " + HasFeature(TAPE_GET_DRIVE_PARAMETERS_FEATURES_LOW.TAPE_DRIVE_REPORT_SMKS));
                sb.Append($"\n{nameof(TAPE_GET_DRIVE_PARAMETERS_FEATURES_LOW.TAPE_DRIVE_GET_ABSOLUTE_BLK)}: " + HasFeature(TAPE_GET_DRIVE_PARAMETERS_FEATURES_LOW.TAPE_DRIVE_GET_ABSOLUTE_BLK));
                sb.Append($"\n{nameof(TAPE_GET_DRIVE_PARAMETERS_FEATURES_LOW.TAPE_DRIVE_GET_LOGICAL_BLK)}: " + HasFeature(TAPE_GET_DRIVE_PARAMETERS_FEATURES_LOW.TAPE_DRIVE_GET_LOGICAL_BLK));
                sb.Append($"\n{nameof(TAPE_GET_DRIVE_PARAMETERS_FEATURES_LOW.TAPE_DRIVE_SET_EOT_WZ_SIZE)}: " + HasFeature(TAPE_GET_DRIVE_PARAMETERS_FEATURES_LOW.TAPE_DRIVE_SET_EOT_WZ_SIZE));
                sb.Append($"\n{nameof(TAPE_GET_DRIVE_PARAMETERS_FEATURES_LOW.TAPE_DRIVE_EJECT_MEDIA)}: " + HasFeature(TAPE_GET_DRIVE_PARAMETERS_FEATURES_LOW.TAPE_DRIVE_EJECT_MEDIA));
                sb.Append($"\n{nameof(TAPE_GET_DRIVE_PARAMETERS_FEATURES_LOW.TAPE_DRIVE_CLEAN_REQUESTS)}: " + HasFeature(TAPE_GET_DRIVE_PARAMETERS_FEATURES_LOW.TAPE_DRIVE_CLEAN_REQUESTS));
                sb.Append($"\n{nameof(TAPE_GET_DRIVE_PARAMETERS_FEATURES_LOW.TAPE_DRIVE_SET_CMP_BOP_ONLY)}: " + HasFeature(TAPE_GET_DRIVE_PARAMETERS_FEATURES_LOW.TAPE_DRIVE_SET_CMP_BOP_ONLY));

                sb.Append($"\n{nameof(TAPE_GET_DRIVE_PARAMETERS_FEATURES_HIGH.TAPE_DRIVE_ABS_BLK_IMMED)}: " + HasFeature(TAPE_GET_DRIVE_PARAMETERS_FEATURES_HIGH.TAPE_DRIVE_ABS_BLK_IMMED));
                sb.Append($"\n{nameof(TAPE_GET_DRIVE_PARAMETERS_FEATURES_HIGH.TAPE_DRIVE_ABSOLUTE_BLK)}: " + HasFeature(TAPE_GET_DRIVE_PARAMETERS_FEATURES_HIGH.TAPE_DRIVE_ABSOLUTE_BLK));
                sb.Append($"\n{nameof(TAPE_GET_DRIVE_PARAMETERS_FEATURES_HIGH.TAPE_DRIVE_END_OF_DATA)}: " + HasFeature(TAPE_GET_DRIVE_PARAMETERS_FEATURES_HIGH.TAPE_DRIVE_END_OF_DATA));
                sb.Append($"\n{nameof(TAPE_GET_DRIVE_PARAMETERS_FEATURES_HIGH.TAPE_DRIVE_FILEMARKS)}: " + HasFeature(TAPE_GET_DRIVE_PARAMETERS_FEATURES_HIGH.TAPE_DRIVE_FILEMARKS));
                sb.Append($"\n{nameof(TAPE_GET_DRIVE_PARAMETERS_FEATURES_HIGH.TAPE_DRIVE_LOAD_UNLOAD)}: " + HasFeature(TAPE_GET_DRIVE_PARAMETERS_FEATURES_HIGH.TAPE_DRIVE_LOAD_UNLOAD));
                sb.Append($"\n{nameof(TAPE_GET_DRIVE_PARAMETERS_FEATURES_HIGH.TAPE_DRIVE_LOAD_UNLD_IMMED)}: " + HasFeature(TAPE_GET_DRIVE_PARAMETERS_FEATURES_HIGH.TAPE_DRIVE_LOAD_UNLD_IMMED));
                sb.Append($"\n{nameof(TAPE_GET_DRIVE_PARAMETERS_FEATURES_HIGH.TAPE_DRIVE_LOCK_UNLOCK)}: " + HasFeature(TAPE_GET_DRIVE_PARAMETERS_FEATURES_HIGH.TAPE_DRIVE_LOCK_UNLOCK));
                sb.Append($"\n{nameof(TAPE_GET_DRIVE_PARAMETERS_FEATURES_HIGH.TAPE_DRIVE_LOCK_UNLK_IMMED)}: " + HasFeature(TAPE_GET_DRIVE_PARAMETERS_FEATURES_HIGH.TAPE_DRIVE_LOCK_UNLK_IMMED));
                sb.Append($"\n{nameof(TAPE_GET_DRIVE_PARAMETERS_FEATURES_HIGH.TAPE_DRIVE_LOG_BLK_IMMED)}: " + HasFeature(TAPE_GET_DRIVE_PARAMETERS_FEATURES_HIGH.TAPE_DRIVE_LOG_BLK_IMMED));
                sb.Append($"\n{nameof(TAPE_GET_DRIVE_PARAMETERS_FEATURES_HIGH.TAPE_DRIVE_LOGICAL_BLK)}: " + HasFeature(TAPE_GET_DRIVE_PARAMETERS_FEATURES_HIGH.TAPE_DRIVE_LOGICAL_BLK));
                sb.Append($"\n{nameof(TAPE_GET_DRIVE_PARAMETERS_FEATURES_HIGH.TAPE_DRIVE_RELATIVE_BLKS)}: " + HasFeature(TAPE_GET_DRIVE_PARAMETERS_FEATURES_HIGH.TAPE_DRIVE_RELATIVE_BLKS));
                sb.Append($"\n{nameof(TAPE_GET_DRIVE_PARAMETERS_FEATURES_HIGH.TAPE_DRIVE_REVERSE_POSITION)}: " + HasFeature(TAPE_GET_DRIVE_PARAMETERS_FEATURES_HIGH.TAPE_DRIVE_REVERSE_POSITION));
                sb.Append($"\n{nameof(TAPE_GET_DRIVE_PARAMETERS_FEATURES_HIGH.TAPE_DRIVE_REWIND_IMMEDIATE)}: " + HasFeature(TAPE_GET_DRIVE_PARAMETERS_FEATURES_HIGH.TAPE_DRIVE_REWIND_IMMEDIATE));
                sb.Append($"\n{nameof(TAPE_GET_DRIVE_PARAMETERS_FEATURES_HIGH.TAPE_DRIVE_SEQUENTIAL_FMKS)}: " + HasFeature(TAPE_GET_DRIVE_PARAMETERS_FEATURES_HIGH.TAPE_DRIVE_SEQUENTIAL_FMKS));
                sb.Append($"\n{nameof(TAPE_GET_DRIVE_PARAMETERS_FEATURES_HIGH.TAPE_DRIVE_SEQUENTIAL_SMKS)}: " + HasFeature(TAPE_GET_DRIVE_PARAMETERS_FEATURES_HIGH.TAPE_DRIVE_SEQUENTIAL_SMKS));
                sb.Append($"\n{nameof(TAPE_GET_DRIVE_PARAMETERS_FEATURES_HIGH.TAPE_DRIVE_SET_BLOCK_SIZE)}: " + HasFeature(TAPE_GET_DRIVE_PARAMETERS_FEATURES_HIGH.TAPE_DRIVE_SET_BLOCK_SIZE));
                sb.Append($"\n{nameof(TAPE_GET_DRIVE_PARAMETERS_FEATURES_HIGH.TAPE_DRIVE_SET_COMPRESSION)}: " + HasFeature(TAPE_GET_DRIVE_PARAMETERS_FEATURES_HIGH.TAPE_DRIVE_SET_COMPRESSION));
                sb.Append($"\n{nameof(TAPE_GET_DRIVE_PARAMETERS_FEATURES_HIGH.TAPE_DRIVE_SET_ECC)}: " + HasFeature(TAPE_GET_DRIVE_PARAMETERS_FEATURES_HIGH.TAPE_DRIVE_SET_ECC));
                sb.Append($"\n{nameof(TAPE_GET_DRIVE_PARAMETERS_FEATURES_HIGH.TAPE_DRIVE_SET_PADDING)}: " + HasFeature(TAPE_GET_DRIVE_PARAMETERS_FEATURES_HIGH.TAPE_DRIVE_SET_PADDING));
                sb.Append($"\n{nameof(TAPE_GET_DRIVE_PARAMETERS_FEATURES_HIGH.TAPE_DRIVE_SET_REPORT_SMKS)}: " + HasFeature(TAPE_GET_DRIVE_PARAMETERS_FEATURES_HIGH.TAPE_DRIVE_SET_REPORT_SMKS));
                sb.Append($"\n{nameof(TAPE_GET_DRIVE_PARAMETERS_FEATURES_HIGH.TAPE_DRIVE_SETMARKS)}: " + HasFeature(TAPE_GET_DRIVE_PARAMETERS_FEATURES_HIGH.TAPE_DRIVE_SETMARKS));
                sb.Append($"\n{nameof(TAPE_GET_DRIVE_PARAMETERS_FEATURES_HIGH.TAPE_DRIVE_SPACE_IMMEDIATE)}: " + HasFeature(TAPE_GET_DRIVE_PARAMETERS_FEATURES_HIGH.TAPE_DRIVE_SPACE_IMMEDIATE));
                sb.Append($"\n{nameof(TAPE_GET_DRIVE_PARAMETERS_FEATURES_HIGH.TAPE_DRIVE_TENSION)}: " + HasFeature(TAPE_GET_DRIVE_PARAMETERS_FEATURES_HIGH.TAPE_DRIVE_TENSION));
                sb.Append($"\n{nameof(TAPE_GET_DRIVE_PARAMETERS_FEATURES_HIGH.TAPE_DRIVE_TENSION_IMMED)}: " + HasFeature(TAPE_GET_DRIVE_PARAMETERS_FEATURES_HIGH.TAPE_DRIVE_TENSION_IMMED));
                sb.Append($"\n{nameof(TAPE_GET_DRIVE_PARAMETERS_FEATURES_HIGH.TAPE_DRIVE_WRITE_FILEMARKS)}: " + HasFeature(TAPE_GET_DRIVE_PARAMETERS_FEATURES_HIGH.TAPE_DRIVE_WRITE_FILEMARKS));
                sb.Append($"\n{nameof(TAPE_GET_DRIVE_PARAMETERS_FEATURES_HIGH.TAPE_DRIVE_WRITE_LONG_FMKS)}: " + HasFeature(TAPE_GET_DRIVE_PARAMETERS_FEATURES_HIGH.TAPE_DRIVE_WRITE_LONG_FMKS));
                sb.Append($"\n{nameof(TAPE_GET_DRIVE_PARAMETERS_FEATURES_HIGH.TAPE_DRIVE_WRITE_MARK_IMMED)}: " + HasFeature(TAPE_GET_DRIVE_PARAMETERS_FEATURES_HIGH.TAPE_DRIVE_WRITE_MARK_IMMED));
                sb.Append($"\n{nameof(TAPE_GET_DRIVE_PARAMETERS_FEATURES_HIGH.TAPE_DRIVE_WRITE_SETMARKS)}: " + HasFeature(TAPE_GET_DRIVE_PARAMETERS_FEATURES_HIGH.TAPE_DRIVE_WRITE_SETMARKS));
                sb.Append($"\n{nameof(TAPE_GET_DRIVE_PARAMETERS_FEATURES_HIGH.TAPE_DRIVE_WRITE_SHORT_FMKS)}: " + HasFeature(TAPE_GET_DRIVE_PARAMETERS_FEATURES_HIGH.TAPE_DRIVE_WRITE_SHORT_FMKS));

                sb.Append('\n');

                return sb.ToString();
            }
            /*
                    internal enum TAPE_GET_DRIVE_PARAMETERS_FEATURES_HIGH : uint
                    {
                        TAPE_DRIVE_ABS_BLK_IMMED = 0x80002000,
                        TAPE_DRIVE_ABSOLUTE_BLK = 0x80001000,
                        TAPE_DRIVE_END_OF_DATA = 0x80010000,
                        TAPE_DRIVE_FILEMARKS = 0x80040000,
                        TAPE_DRIVE_LOAD_UNLOAD = 0x80000001,
                        TAPE_DRIVE_LOAD_UNLD_IMMED = 0x80000020,
                        TAPE_DRIVE_LOCK_UNLOCK = 0x80000004,
                        TAPE_DRIVE_LOCK_UNLK_IMMED = 0x80000080,
                        TAPE_DRIVE_LOG_BLK_IMMED = 0x80008000,
                        TAPE_DRIVE_LOGICAL_BLK = 0x80004000,
                        TAPE_DRIVE_RELATIVE_BLKS = 0x80020000,
                        TAPE_DRIVE_REVERSE_POSITION = 0x80400000,
                        TAPE_DRIVE_REWIND_IMMEDIATE = 0x80000008,
                        TAPE_DRIVE_SEQUENTIAL_FMKS = 0x80080000,
                        TAPE_DRIVE_SEQUENTIAL_SMKS = 0x80200000,
                        TAPE_DRIVE_SET_BLOCK_SIZE = 0x80000010,
                        TAPE_DRIVE_SET_COMPRESSION = 0x80000200,
                        TAPE_DRIVE_SET_ECC = 0x80000100,
                        TAPE_DRIVE_SET_PADDING = 0x80000400,
                        TAPE_DRIVE_SET_REPORT_SMKS = 0x80000800,
                        TAPE_DRIVE_SETMARKS = 0x80100000,
                        TAPE_DRIVE_SPACE_IMMEDIATE = 0x80800000,
                        TAPE_DRIVE_TENSION = 0x80000002,
                        TAPE_DRIVE_TENSION_IMMED = 0x80000040,
                        TAPE_DRIVE_WRITE_FILEMARKS = 0x82000000,
                        TAPE_DRIVE_WRITE_LONG_FMKS = 0x88000000,
                        TAPE_DRIVE_WRITE_MARK_IMMED = 0x90000000,
                        TAPE_DRIVE_WRITE_SETMARKS = 0x81000000,
                        TAPE_DRIVE_WRITE_SHORT_FMKS = 0x84000000,
                    }


                    internal partial struct TAPE_GET_DRIVE_PARAMETERS
                    {
                        /// <summary>If this member is <b>TRUE</b>, the device supports hardware error correction. Otherwise, it does not.</summary>
                        internal winmdroot.Foundation.BOOLEAN ECC;

                        /// <summary>If this member is <b>TRUE</b>, hardware data compression is enabled. Otherwise, it is disabled.</summary>
                        internal winmdroot.Foundation.BOOLEAN Compression;

                        /// <summary>If this member is <b>TRUE</b>, data padding is enabled. Otherwise, it is disabled. Data padding keeps the tape streaming at a constant speed.</summary>
                        internal winmdroot.Foundation.BOOLEAN DataPadding;

                        /// <summary>If this member is <b>TRUE</b>, setmark reporting is enabled. Otherwise, it is disabled.</summary>
                        internal winmdroot.Foundation.BOOLEAN ReportSetmarks;

                        /// <summary>Device's default fixed block size, in bytes.</summary>
                        internal uint DefaultBlockSize;

                        /// <summary>Device's maximum block size, in bytes.</summary>
                        internal uint MaximumBlockSize;

                        /// <summary>Device's minimum block size, in bytes.</summary>
                        internal uint MinimumBlockSize;

                        /// <summary>Maximum number of partitions that can be created on the device.</summary>
                        internal uint MaximumPartitionCount;

                        /// <summary>
                        /// <para>Low-order bits of the device features flag. This member can be one or more of following values.</para>
                        /// <para></para>
                        /// <para>This doc was truncated.</para>
                        /// <para><see href="https://learn.microsoft.com/windows/win32/api/winnt/ns-winnt-tape_get_drive_parameters#members">Read more on docs.microsoft.com</see>.</para>
                        /// </summary>
                        internal uint FeaturesLow;

                        /// <summary></summary>
                        internal winmdroot.System.SystemServices.TAPE_GET_DRIVE_PARAMETERS_FEATURES_HIGH FeaturesHigh;

                        /// <summary>Indicates the number of bytes between the end-of-tape warning and the physical end of the tape.</summary>
                        internal uint EOTWarningZoneSize;
                    }

            */

        } // TAPE_GET_DRIVE_PARAMETERS

        internal partial struct TAPE_GET_MEDIA_PARAMETERS
        {
            public override readonly string ToString()
            {
                StringBuilder sb = new();

                sb.Append("Tape capacity: " + Helpers.BytesToString(Capacity));
                sb.Append("\nRemaining to end of tape: " + Helpers.BytesToString(Remaining));
                sb.Append("\nBlock size: " + Helpers.BytesToString(BlockSize));
                sb.Append("\nPartition count: " + PartitionCount);
                sb.Append("\nWrite protected?: " + (bool)WriteProtected);

                return sb.ToString();
            }
            /*
                        internal partial struct TAPE_GET_MEDIA_PARAMETERS
                        {
                            /// <summary>Total number of bytes on the current tape partition.</summary>
                            internal long Capacity;

                            /// <summary>Number of bytes between the current position and the end of the current tape partition.</summary>
                            internal long Remaining;

                            /// <summary>Number of bytes per block.</summary>
                            internal uint BlockSize;

                            /// <summary>Number of partitions on the tape.</summary>
                            internal uint PartitionCount;

                            /// <summary>If this member is <b>TRUE</b>, the tape is write-protected. Otherwise, it is not.</summary>
                            internal winmdroot.Foundation.BOOLEAN WriteProtected;
                        }

                */

            } // TAPE_GET_DRIVE_PARAMETERS
        }
}
