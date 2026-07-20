using System.Text;
using System.Diagnostics;

using Windows.Win32;
using Windows.Win32.Foundation;

using Microsoft.Extensions.Logging;
using TapeLibNET;


namespace TapeLibNET
{
    /// <summary>
    /// Handles tape positioning across a content area (zero or more content sets) and a TOC area.
    /// <para>Subclasses implement the physical layout; use <see cref="ProduceNavigator"/> to create
    ///  the appropriate one for the loaded media.</para>
    /// <para><b>Set indexation:</b> positive values (0, 1, …) count from the oldest set forward;
    ///  negative values (−1 = end-of-content / write position, −2 = newest set, −3 = second newest, …).
    ///  Special sentinels: <see cref="UnknownSet"/>, <see cref="InTOCSet"/>.</para>
    /// </summary>
    /// <remarks>
    /// Tape organizations and corresponding subclasses:
    /// <list type="bullet">
    /// <item><see cref="TapeNavigatorTOCInPartition"/> — WithPartitions</item>
    /// <item><see cref="TapeNavigatorTOCInSetWithSmks"/> — WithSetmarks</item>
    /// <item><see cref="TapeNavigatorTOCInSetWithFmks"/> — WithSeqFilemarks (no TOC mark)</item>
    /// <item><see cref="TapeNavigatorTOCInSetWithFmksAndTOCMark"/> — WithSeqFilemarks + TOC mark</item>
    /// </list>
    /// </remarks>
    public abstract class TapeNavigator : TapeDriveHolder<TapeNavigator>
    {
        #region *** Properties ***

        /// <summary>Default TOC capacity used when no override is specified.</summary>
        public static long DefaultTOCCapacity(TapeDrive? drive)
            => drive?.IsLto5PlusDrive == true
                ? 1024 * 1024 * 1024 // 1 GB for LTO-5+
                : drive?.IsLtoDrive == true
                    ? 512 * 1024 * 1024 // 512 MB for LTO-1..4
                    : 32 * 1024 * 1024; // 32 MB for non-LTO drives (default if no drive specified)

        /// <summary>
        /// Maximum space reserved for the TOC area on this tape.
        /// Instance-level so that concurrent tape operations (or tests) don't interfere.
        /// </summary>
        public long TOCCapacity
        {
            get => m_tocCapacityOverride ?? DefaultTOCCapacity(Drive);
            set => m_tocCapacityOverride = value;
        }

        /// <summary>
        /// Adjusts the remaining content capacity accounting for the drive reporting and TOC capacity.
        /// <para>Do <b>not</b> deduct the TOC capacity; the method will do this.</para>
        /// </summary>
        /// <param name="remainingCapacity">
        /// The remaining content capacity to adjust <b>without</b> deducted TOC capacity.
        /// </param>
        /// <returns>The adjusted remaining content capacity.</returns>
        public long AdjustRemainingContentCapacity(long remainingCapacity)
            => AdjustRemainingContentCapacity(Drive, remainingCapacity);

        /// <summary>
        /// Adjusts the remaining content capacity accounting for the drive reporting and TOC capacity.
        /// <para>Do <b>not</b> deduct the TOC capacity; the method will do this.</para>
        /// </summary>
        /// <param name="drive">The tape drive to use for the adjustment.</param>
        /// <param name="remainingCapacity">
        /// The remaining content capacity to adjust <b>without</b> deducted TOC capacity.
        /// </param>
        /// <returns>The adjusted remaining content capacity.</returns>
        public static long AdjustRemainingContentCapacity(TapeDrive drive, long remainingCapacity)
        {
            var remainingFromDrive = drive.GetContentRemainingCapacity();
            // adjust down by 1% of drive capacity to account for drive reporting inaccuracies
            remainingFromDrive -= drive.Capacity / 100;
            
            remainingCapacity = Math.Max(remainingCapacity, remainingFromDrive);

            if (!drive.HasInitiatorPartition)
                remainingCapacity -= DefaultTOCCapacity(drive);

            remainingCapacity = Math.Max(remainingCapacity, 0); // don't return negative capacity
            return remainingCapacity;
        }
        
        private long? m_tocCapacityOverride = null;

        public virtual bool TOCInvalidated { get; protected set; } = false;

        /// <summary>
        /// Content set to navigate to on the next <see cref="MoveToTargetContentSet"/> call.
        /// <para>0 = oldest set, 1 = second oldest, …; −1 = end-of-content (write position),
        ///  −2 = newest set, −3 = second newest, …</para>
        /// </summary>
        public int TargetContentSet { get; set; }

        /// <summary>
        /// Content set the tape head is currently positioned at. Same indexation as
        ///  <see cref="TargetContentSet"/>, plus sentinels <see cref="UnknownSet"/> and <see cref="InTOCSet"/>.
        /// <para>The navigator never relies on knowing the total number of sets — it always
        ///  counts from the beginning or the end of content.</para>
        /// </summary>
        public int CurrentContentSet { get; protected set; } = UnknownSet;
        /// <summary>Sentinel: current position is unknown or not yet established.</summary>
        public static int UnknownSet => int.MinValue;
        /// <summary>Sentinel: tape head is positioned inside the TOC area.</summary>
        public static int InTOCSet => UnknownSet + 1;
        internal void ResetContentSet() => CurrentContentSet = UnknownSet;

        /// <summary>
        /// Informs the navigator that the media is known to be blank (e.g. just formatted).
        /// Sets <see cref="CurrentContentSet"/> to −1 ("end of empty content"), so that
        /// <see cref="MoveToBeginOfTOC"/> can proceed without searching for an existing TOC.
        /// </summary>
        internal void AssumeBlankMedia() => CurrentContentSet = -1;

        private bool m_useSmks = false;
        /// <summary>
        /// When <c>true</c>, real tape setmarks are written/read at content set boundaries.
        ///  When <c>false</c>, filemarks are used instead — even if the drive supports setmarks.
        /// <para>Can only be set to <c>true</c> when <see cref="TapeDrive.SupportsSetmarks"/> is <c>true</c>;
        ///  attempts to enable it on drives without setmark support are silently ignored.</para>
        /// </summary>
        public bool UseSmks
        {
            get => m_useSmks;
            set => m_useSmks = value && Drive.SupportsSetmarks;
        }

        #endregion // Properties


        #region *** Constructors and factories ***

        public TapeNavigator(TapeDrive drive) : base(drive)
        {
            m_logger.LogTrace("Drive #{Drive}: Created Navigator of type {Type}", DriveNumber, GetType());
        }

        /// <summary>
        /// Creates the appropriate <see cref="TapeNavigator"/> subclass for the loaded media.
        /// </summary>
        /// <param name="drive">The tape drive with loaded media.</param>
        /// <param name="useTOCMark">
        /// When <c>true</c> (default) and the drive uses sequential filemarks (no setmarks,
        ///  no initiator partition), a dedicated TOC marker sequence is written to help locate
        ///  the TOC. Only affects the <see cref="TapeNavigatorTOCInSetWithFmksAndTOCMark"/>
        ///  vs. <see cref="TapeNavigatorTOCInSetWithFmks"/> choice.
        /// </param>
        public static TapeNavigator? ProduceNavigator(TapeDrive drive, bool useTOCMark = true)
        {
            if (!drive.IsMediaLoaded)
                return null;

            if (drive.HasInitiatorPartition)
            {
                var nav = new TapeNavigatorTOCInPartition(drive)
                {
                    UseSmks = drive.SupportsSetmarks // use real setmarks by default if the drive supports them
                };
                return nav;
            }

            if (drive.SupportsSetmarks)
            {
                TapeNavigatorTOCInSetWithSmks nav = new(drive)
                {
                    UseSmks = true // real setmarks available — use them by default
                };
                return nav;
            }

            if (drive.SupportsSeqFilemarks && useTOCMark)
                return new TapeNavigatorTOCInSetWithFmksAndTOCMark(drive);

            return new TapeNavigatorTOCInSetWithFmks(drive);
        }

        #endregion // Constructors and factories


        #region *** Notifications ***

        public virtual void OnBeginWriteTOC() => CurrentContentSet = InTOCSet;
        public virtual void OnBeginWriteContent() { }
            // do NOT set CurrentContentSet to -1 yet since we don't know what set is being overritenn

        public virtual void OnTOCWritten() => CurrentContentSet = InTOCSet;
        public virtual void OnContentWritten() => CurrentContentSet = -1;


        #endregion  // Notifications


        #region *** TOC positioning ***

        /// <summary>Positions the tape at the start of the TOC area. Subclasses implement the physical seek.</summary>
        public virtual bool MoveToBeginOfTOC()
        {
            // Actual implementation by the derived classes — we just finalize here

            if (WentOK)
                CurrentContentSet = InTOCSet; // if we're in TOC area, we aren't in any content set!
            else
                ResetContentSet(); // since we don't know where we ended up

            if (WentOK)
                m_logger.LogTrace("Drive #{Drive}: Moved to the beginning of TOC", DriveNumber);
            else
                LogErrorAsDebug("Failed to move to the beginning of TOC");

            return WentOK;
        } // MoveToBeginOfTOC

        #endregion // TOC positioning


        #region *** Content positioning ***

        /// <summary>Positions the tape at the start of the content area (set 0).</summary>
        public virtual bool MoveToBeginOfContent()
        {
            // Actual implementation by the derived classes — we just finalize here

            if (WentOK)
                CurrentContentSet = 0; // we're at the beginning of content

            if (WentOK)
                m_logger.LogTrace("Drive #{Drive}: Moved to the beginning of content", DriveNumber);
            else
                LogErrorAsDebug("Failed to move to the beginning of content");

            return WentOK;
        }

        /// <summary>Positions the tape at the end of the content area (write position for new sets).</summary>
        public virtual bool MoveToEndOfContent()
        {
            // Actual implementation by the derived classes — we just finalize here

            if (WentOK)
                CurrentContentSet = -1; // we're at the end of content
            else
                ResetContentSet(); // since we don't know where we ended up

            if (WentOK)
                m_logger.LogTrace("Drive #{Drive}: Moved to the end of content", DriveNumber);
            else
                LogErrorAsDebug("Failed to move to the end of content");

            return WentOK;
        }

        /// <summary>
        /// Navigates to <see cref="TargetContentSet"/> using bidirectional setmark traversal.
        /// <para>Positive targets seek forward from content start; negative targets seek backward
        ///  from content end. Handles <see cref="WIN32_ERROR.ERROR_BEGINNING_OF_MEDIA"/> gracefully
        ///  when the oldest set has no preceding setmark.</para>
        /// </summary>
        public virtual bool MoveToTargetContentSet()
        {
            m_logger.LogTrace("Drive #{Drive}: Moving to target content set {Set}", DriveNumber, TargetContentSet);

            ResetError();

            if (TargetContentSet == CurrentContentSet)
            {
                m_logger.LogTrace("Drive #{Drive}: Already at target content set {Set}", DriveNumber, TargetContentSet);
                return true;
            }

            if (TargetContentSet < 0) // starting from the end of content -> move to the end of content first
            {
                // [set0][SM]..[setN-2][SM][setN-1][SM][setN][SM][toc]
                //             -4          -3          -2        -1
                if (CurrentContentSet >= 0 || CurrentContentSet == UnknownSet || CurrentContentSet == InTOCSet)
                {
                    MoveToEndOfContent();
                    if (WentBad)
                        return false;
                    Debug.Assert(CurrentContentSet == -1);
                }
                Debug.Assert(CurrentContentSet < 0);

                int count = TargetContentSet - CurrentContentSet;

                if (count < 0)
                {
                    if (WentOK)
                        MoveToNextContentSetmark(count - 1); // moves to just before the target SM; account for 1 SM in front of target set
                    if ((WIN32_ERROR)LastError == WIN32_ERROR.ERROR_BEGINNING_OF_MEDIA)
                        ResetError(); // hit the very beginning → oldest set has no preceding SM, we're already at its start
                    else if (WentOK)
                        MoveToNextContentSetmark(); // move to just after the correct setmark -- the beginning of the target set
                }
                else if (count > 0)
                {
                    if (WentOK)
                        MoveToNextContentSetmark(count); // move to after the last setmark -- the beginning of the set
                }
            }
            else // TargetContentSet >= 0 -- starting from the beginning of the content -> move to the beginning of content first
            {
                // [set0][SM][set1][SM][set2][SM]..[SM][toc]
                // 0         1         2         3
                if (CurrentContentSet < 0) // this includes UnknownSet and InTOCSet
                {
                    MoveToBeginOfContent();
                    if (WentBad)
                        return false;
                    Debug.Assert(CurrentContentSet == 0);
                }
                Debug.Assert(CurrentContentSet >= 0);

                int count = TargetContentSet - CurrentContentSet;

                if (count < 0)
                {
                    if (WentOK)
                        MoveToNextContentSetmark(count - 1); // moves to just before the target setmark
                    if ((WIN32_ERROR)LastError == WIN32_ERROR.ERROR_BEGINNING_OF_MEDIA && TargetContentSet == 0) // we hit the beginning of the first set
                        ResetError(); // if that's the target one, all good -> otherwise we let the error stay
                    else
                        if (WentOK)
                        MoveToNextContentSetmark(); // move to just after the correct setmark -- the beginning of the target set
                }
                else if (count > 0)
                {
                    if (WentOK)
                        MoveToNextContentSetmark(count); // move to after the last setmark -- the beginning of the set
                }
                // esle count == 0 -> ww're already at the beginning of the target set
            }

            if (WentOK)
                CurrentContentSet = TargetContentSet;
            else
                ResetContentSet(); // since we don't know where we ended up

            if (WentOK)
                m_logger.LogTrace("Drive #{Drive}: Moved to target content set {Set}", DriveNumber, TargetContentSet);
            else
                LogErrorAsDebug("Failed to move to target content set");

            return WentOK;
        }

        #endregion // Content positioning


        #region *** Content mark handling ***

        internal bool MoveToNextContentSetmark(int count = 1) // count may be negative meaning move back. Used e.g. when TOC is in the last set
        {
            if (count == 0) // nothing to do
            {
                ResetError();
                return true;
            }

            if (UseSmks)
                // move forward by 'count' setmarks
                Drive.MoveToNextSetmark(count);
            else // use filemarks to emulate setmarks
                Drive.MoveToNextFilemark(count);

            if (WentOK)
                CurrentContentSet += count;

            if (WentOK)
                m_logger.LogTrace("Drive #{Drive}: Moved by {Count} content setmarks -> CurrentContentSet = {Set}",
                    DriveNumber, count, CurrentContentSet);
            else
                LogErrorAsDebug("Failed to move to next content setmark(s)");

            return WentOK;
        }

        internal bool MoveToBlock(long block) => Drive.MoveToBlock(block);

        // Fetches the block number directly from the device -- the most reliable way
        internal long GetCurrentBlock() => Drive.GetCurrentBlock();

        internal bool WriteContentSetmark()
        {
            if (UseSmks)
                Drive.WriteSetmark();
            else // use filemarks to emulate setmarks
                Drive.WriteFilemark();

            if (WentOK)
                m_logger.LogTrace("Drive #{Drive}: Wrote content setmark", DriveNumber);
            else
                LogErrorAsDebug("Failed to write content setmark");

            return WentOK;
        }

        internal bool MoveToNextTOCFilemark()
        {
            // TOC always uses filemarks
            Drive.MoveToNextFilemark();

            if (WentOK)
                m_logger.LogTrace("Drive #{Drive}: Moved to next TOC filemark", DriveNumber);
            else
                LogErrorAsDebug("Failed to move to next TOC filemark");

            return WentOK;
        }

        internal bool WriteTOCFilemark()
        {
            // TOC always uses filemarks
            Drive.WriteFilemark();

            if (WentOK)
                m_logger.LogTrace("Drive #{Drive}: Wrote a TOC filemark", DriveNumber);
            else
                LogErrorAsDebug("Failed to write a TOC filemark");

            return WentOK;
        }

        #endregion // Content mark handling

    } // TapeNavigator


    /// <summary>
    /// Navigator for drives with an initiator partition (WithPartitions organization).
    /// <para>TOC resides in the initiator partition; content in the content partition.
    ///  Partition switches trigger media-parameter refresh (e.g. capacity).</para>
    /// <code>
    /// Partition 1 (Content): [set0][SM][set1][SM]…[setN][SM]
    /// Partition 2 (Initiator): [toc1][FM][toc2][FM]
    /// </code>
    /// </summary>
    public class TapeNavigatorTOCInPartition : TapeNavigator
    {
        #region *** Constants ***

        // Notice TOC in partition 2 ("initiator partition"), content in partition 1
        //private const int TOCPartition = 2;
        //private const int ContentPartition = 1;

        #endregion // Constants


        #region *** Constructors ***

        internal TapeNavigatorTOCInPartition(TapeDrive drive) : base(drive) { }

        #endregion // Constructors


        #region *** Notifications ***

        // use base class versions

        #endregion  // Notifications


        #region *** TOC positioning ***

        public override bool MoveToBeginOfTOC()
        {
            m_logger.LogTrace("Drive #{Drive}: Moving to the beginning of TOC partition", DriveNumber);

            ResetError();

            Drive.MoveToPartition(MediaPartition.Initiator); // TOCPartition

            m_logger.LogTrace("Drive #{Drive}: Current partition after moving to Initiator is >{Partition}<",
                DriveNumber, Drive.GetCurrentPartition());

            return base.MoveToBeginOfTOC();
        } // MoveToBeginOfTOC

        #endregion // TOC positioning


        #region *** Content positioning ***

        public override bool MoveToBeginOfContent()
        {
            m_logger.LogTrace("Drive #{Drive}: Moving to the beginning of content partition", DriveNumber);

            if (CurrentContentSet == 0) // already at the beginning of content
            {
                m_logger.LogTrace("Drive #{Drive}: Already at the beginning of content", DriveNumber);
                return true;
            }

            Drive.MoveToPartition(MediaPartition.Content); // ContentPartition
                                                           // Drive will refresh MediaParams for the new partition, esp. Capacity

            m_logger.LogTrace("Drive #{Drive}: Current partition after moving to Content is >{Partition}<",
                DriveNumber, Drive.GetCurrentPartition());

            return base.MoveToBeginOfContent();
        }

        public override bool MoveToEndOfContent()
        {
            m_logger.LogTrace("Drive #{Drive}: Moving to the end of content partition", DriveNumber);

            if (CurrentContentSet == -1) // already at the end of content
            {
                m_logger.LogTrace("Drive #{Drive}: Already at the end of content", DriveNumber);
                return true;
            }

            Drive.MoveToPartition(MediaPartition.Content); // ContentPartition

            m_logger.LogTrace("Drive #{Drive}: Current partition after moving to Content is >{Partition}<",
                DriveNumber, Drive.GetCurrentPartition());

            Drive.FastforwardToEnd(partition: MediaPartition.Current); // ContentPartition

            m_logger.LogTrace("Drive #{Drive}: Current partition after ffwd'ing to end is >{Partition}<",
                DriveNumber, Drive.GetCurrentPartition());

            // [content][SM][EOM] <-- we're here
            if (WentOK)
                MoveToNextContentSetmark(-1); // this will bring us to right before the last setmark
            
            if (WentOK)
                MoveToNextContentSetmark(1); // Finally go 1 setmark forward to after the setmark -- the to-be-written content data
            else
                // assume there's no data written yet -- we must be at the begining of the content partion, as we wish
                ResetError();

            return base.MoveToEndOfContent();
        }

        #endregion // Content positioning

    } // TapeNavigatorTOCInPartition


    /// <summary>
    /// Base class for single-partition layouts where the TOC follows the content on the same tape.
    /// <para><see cref="TOCInvalidated"/> starts <see langword="true"/> and is cleared after each
    ///  successful TOC write; any content write re-invalidates it.</para>
    /// </summary>
    public abstract class TapeNavigatorTOCInSet(TapeDrive drive) : TapeNavigator(drive)
    {
        #region *** Constants ***

        //protected const int CommonPartition = 1;

        #endregion // Constants


        #region *** Properties ***

        public override bool TOCInvalidated { get; protected set; } = true;

        #endregion // Properties


        #region *** Notifications ***

        public override void OnTOCWritten()
        {
            TOCInvalidated = false;
            base.OnTOCWritten();
        }
        public override void OnContentWritten()
        {
            TOCInvalidated = true;
            base.OnContentWritten();
        }

        #endregion  // Notifications


        #region *** Content positioning ***

        public override bool MoveToBeginOfContent()
        {
            m_logger.LogTrace("Drive #{Drive}: Moving to the beginning of content in set", DriveNumber);

            if (CurrentContentSet == 0) // already at the beginning of content
            {
                m_logger.LogTrace("Drive #{Drive}: Already at the beginning of content", DriveNumber);
                return true;
            }

            Drive.Rewind();

            return base.MoveToBeginOfContent();
        }

        #endregion // Content positioning
    }


    /// <summary>
    /// Navigator for drives supporting real setmarks (WithSetmarks organization).
    /// <para>Content sets are delimited by setmarks; the TOC follows the last setmark.</para>
    /// <code>
    /// [set0][SM][set1][SM]…[setN][SM][toc1][FM][toc2][FM]
    /// </code>
    /// </summary>
    public class TapeNavigatorTOCInSetWithSmks : TapeNavigatorTOCInSet
    {
        #region *** Constructors ***

        internal TapeNavigatorTOCInSetWithSmks(TapeDrive drive) : base(drive) { }

        #endregion // Constructors


        #region *** TOC positioning ***

        // The TOC is in the last set of the only partion [content][SM][toc1][FM][toc1][FM]
        public override bool MoveToBeginOfTOC()
        {
            m_logger.LogTrace("Drive #{Drive}: Moving to the beginning of TOC in set with setmarks", DriveNumber);

            ResetError();

            if (CurrentContentSet == -1)  // else if we're at the end of content, we're already at the beginning of TOC
            {
                m_logger.LogTrace("Drive #{Drive}: Already at the beginning of TOC", DriveNumber);
            }
            else          
            {
                MoveToEndOfContentInternal();
            }

            return base.MoveToBeginOfTOC();
        }

        #endregion // TOC positioning


        #region *** Content positioning ***

        private void MoveToEndOfContentInternal()
        {
            // First move to the end of the data in the partition. Notice this will fail if TOC hasn't been written yet
            Drive.FastforwardToEnd(partition: MediaPartition.Content); // CommonPartition

            // The TOC is in the last set of the only partion [content][SM][toc1][FM][toc1][FM] -> next move to before the last setmark (indicating end of content)
            if (WentOK)
                MoveToNextContentSetmark(-1); // this will bring us to right before the last setmark

            if (WentOK)
                MoveToNextContentSetmark(1); // Finally go 1 setmark forward to after the setmark -- the beginning of TOC data == of the to-be-written content data
            else
            {
                // no setmarks found -> assume no content yet on media, just rewind to the begin of media
                ResetError();
                Drive.Rewind();
            }
        }

        public override bool MoveToEndOfContent()
        {
            m_logger.LogTrace("Drive #{Drive}: Moving to the end of content in set with setmarks", DriveNumber);

            if (CurrentContentSet == -1) // already at the end of content
            {
                m_logger.LogTrace("Drive #{Drive}: Already at the end of content", DriveNumber);
                return true;
            }

            MoveToEndOfContentInternal();

            return base.MoveToEndOfContent();
        }

        // optimized version for the case when we're inside TOC
        public override bool MoveToTargetContentSet()
        {
            ResetError();

            if (TargetContentSet < 0 && CurrentContentSet == InTOCSet)
            {
                m_logger.LogTrace("Drive #{Drive}: Moving to target content set {Set}; optimized case 'TOC to from-content-end'",
                    DriveNumber, TargetContentSet);

                // [set0][SM]..[setN-2][SM][setN-1][SM][setN][SM][toc]
                //             -4          -3          -2        -1
                
                // Since we're inside TOC, we only need to move back by TargetContentSet setmarks, then forward by 1
                MoveToNextContentSetmark(TargetContentSet); // moves to just before the target SM
                if ((WIN32_ERROR)LastError == WIN32_ERROR.ERROR_BEGINNING_OF_MEDIA)
                    ResetError(); // hit BOM → oldest set has no preceding SM, we're already at its start
                else if (WentOK)
                    MoveToNextContentSetmark(); // move to just after the correct setmark -- the beginning of the target set

                if (WentOK)
                    CurrentContentSet = TargetContentSet;
                else
                    ResetContentSet(); // since we don't know where we ended up

                if (WentOK)
                    m_logger.LogTrace("Drive #{Drive}: Moved to target content set {Set}", DriveNumber, TargetContentSet);
                else
                    LogErrorAsDebug("Failed to move to target content set");

                return WentOK;
            }

            // for all other cases, use the base class implementation
            return base.MoveToTargetContentSet();
        }

        #endregion // Content positioning

    } // TapeNavigatorTOCInSetWithSmks


    /// <summary>
    /// Navigator for drives with filemarks only, without setmarks or a TOC marker (LTO-style).
    /// <para>Content sets and TOC copies are all separated by filemarks. Locating the TOC
    ///  requires seeking backward from end-of-data by a known filemark count.</para>
    /// <code>
    /// [set0][FM][set1][FM]…[setN][FM][toc1][FM][toc2][FM]
    /// </code>
    /// </summary>
    public class TapeNavigatorTOCInSetWithFmks : TapeNavigatorTOCInSet
    {
        #region *** Constructors ***

        internal TapeNavigatorTOCInSetWithFmks(TapeDrive drive) : base(drive) { }

        #endregion // Constructors


        #region *** TOC positioning ***
        // The TOC is in the last two files: [content][FM][toc1][FM][toc2][FM]

        public override bool MoveToBeginOfTOC()
        {
            m_logger.LogTrace("Drive #{Drive}: Moving to the beginning of TOC in set with filemarks", DriveNumber);

            if (CurrentContentSet == -1) // if we're at the end of content, we're already at the beginning of TOC
            {
                m_logger.LogTrace("Drive #{Drive}: Already at the beginning of TOC", DriveNumber);
            }
            else
            {
                MoveToEndOfContentInternal();
            }

            return base.MoveToBeginOfTOC();
        } // MoveToBeginOfTOC

        #endregion // TOC positioning


        #region *** Content positioning ***

        private void MoveToEndOfContentInternal()
        {
            // QUIRK in Quantum SDLT: it seems necessary to rewind before going to the end of the data
            if (!Drive.IsLtoDrive)
                Drive.Rewind();

            // First move to the end of the data in the partition. Notice the following will produce an error if TOC hasn't been written yet
            Drive.FastforwardToEnd(partition: MediaPartition.Content); // CommonPartition

            // Next move to before the filemark before first TOC file
            if (WentOK)
                Drive.MoveToNextFilemark(-3); // this will bring us to right before the filemark before first TOC file
            // Finally advance 1 filemark forward to after the filemark -- the beginning of TOC data == of the to-be-written content data
            if (WentOK)
                Drive.MoveToNextFilemark(1);
            else
            {
                // No filemarks found — assume no content/TOC on media yet, rewind to beginning
                ResetError();
                Drive.Rewind();
            }
        }

        public override bool MoveToEndOfContent()
        {
            m_logger.LogTrace("Drive #{Drive}: Moving to the end of content in set with filemarks", DriveNumber);

            if (CurrentContentSet == -1) // already at the end of content
            {
                m_logger.LogTrace("Drive #{Drive}: Already at the end of content", DriveNumber);
                return true;
            }

            MoveToEndOfContentInternal();

            return base.MoveToEndOfContent();
        } // MoveToEndOfContent()

        #endregion // Content positioning

    } // TapeNavigatorTOCInSetWithFmks


    /// <summary>
    /// Navigator for drives with sequential filemarks, using a dedicated TOC marker sequence
    ///  (WithSeqFilemarks + TOCMark organization).
    /// <para>A gap file followed by <c>FmksAsTOCMark</c> (3) consecutive filemarks marks the
    ///  boundary between content and TOC. The marker enables reliable forward scanning via
    ///  <see cref="TapeDrive.MovePastSeqFilemarks"/>.</para>
    /// <code>
    /// [set0][FM][set1][FM]…[setN][FM][gap][FM][FM][FM][toc1][FM][toc2][FM]
    ///                                    └── TOC mark ──┘
    /// </code>
    /// </summary>
    public class TapeNavigatorTOCInSetWithFmksAndTOCMark : TapeNavigatorTOCInSet
    {
        #region *** Constants ***

        private const int FmksAsTOCMark = 3; // number of filemarks used as TOC mark

        #endregion // Constants


        #region *** Private fields ***

        // Tracks whether the physical TOC mark sequence (gap + 3 filemarks) needs to be
        //  (re)written. Separate from base-class TOCInvalidated which tracks TOC *data*
        //  staleness. Set true when content overwrites the mark area, false when a seek
        //  confirms the mark is still present on tape or after WriteTOCMark() succeeds.
        private bool m_tocMarkInvalidated = true;

        #endregion // Private fields


        #region *** Constructors ***

        internal TapeNavigatorTOCInSetWithFmksAndTOCMark(TapeDrive drive) : base(drive) { }

        #endregion // Constructors


        #region *** Notifications ***
        
        public override void OnBeginWriteTOC()
        {
            // Only write a new TOC mark if the previous one was invalidated
            //  (overwritten by content). When the navigator already found and
            //  positioned past an existing mark in MoveToBeginOfTOC(), reuse it.
            if (m_tocMarkInvalidated)
                WriteTOCMark();
            base.OnBeginWriteTOC();
        }

        public override void OnContentWritten()
        {
            m_tocMarkInvalidated = true;
            base.OnContentWritten();
        }

        #endregion // Notifications


        #region *** TOC positioning ***

        // The TOC is in the last two files, separated by additional "TOC marker" (c_fmksAsTOCMark filemarks):
        //  [content][FM][gap][FM][FM][toc1][FM][toc2][FM]

        public override bool MoveToBeginOfTOC()
        {
            m_logger.LogTrace("Drive #{Drive}: Moving to the beginning of TOC in set with filemarks and TOC mark", DriveNumber);

            if (CurrentContentSet == -1)
            {
                // We're already at the beginning of the TOC marker
                if (m_tocMarkInvalidated) //  ...but if we were writing content, we've overwritten the marker
                {
                    ; // ...but we'll write it again in BeginWriteTOC() -> stay at the end of content
                }
                else
                {
                    SeekForwardPastTOCMark();
                }
            }
            else if (CurrentContentSet == InTOCSet)
            {
                SeekBackwardBeforeTOCMark();
                if (WentOK)
                    SeekForwardPastTOCMark();
            }
            else if (CurrentContentSet == UnknownSet)
            {
                // First move to the very beginning
                Drive.Rewind();
                if (WentOK)
                    SeekForwardPastTOCMark();

                /*
                // The following doesn't work on Quantum SDLT
                // First go to the end. Notice this will fail on an empty tape
                FastforwardToEnd(partition: 1);
                if (WentOK)
                    SeekBackwardBeforeTOCMark();
                if (WentOK)
                    SeekForwardPastTOCMark();
                */
        }
            else // we're somewhere in the content
            {
                SeekForwardPastTOCMark();
            }

            return base.MoveToBeginOfTOC();
        } // MoveToBeginOfTOC

        #endregion // TOC positioning


        #region *** Content positioning ***

        public override bool MoveToBeginOfContent()
        {
            m_logger.LogTrace("Drive #{Drive}: Moving to the beginning of content in set with filemarks and TOC mark", DriveNumber);

            if (CurrentContentSet == 0) // already at the beginning of content
            {
                m_logger.LogTrace("Drive #{Drive}: Already at the beginning of content", DriveNumber);
                return true;
            }

            Drive.Rewind();

            return base.MoveToBeginOfContent();
        }

        public override bool MoveToEndOfContent()
        {
            m_logger.LogTrace("Drive #{Drive}: Moving to the end of content in set with filemarks and TOC mark", DriveNumber);

            if (CurrentContentSet == -1) // already at the end of content
            {
                m_logger.LogTrace("Drive #{Drive}: Already at the end of content", DriveNumber);
                return true;
            }

            // The TOC is in the last two files, separated by additional c_fmksAsTOCMark filemarks ("TOC marker"):
            //  [content][FM][gap][FM][FM][toc1][FM][toc2][FM]

            if (CurrentContentSet == UnknownSet)
                Drive.FastforwardToEnd(partition: MediaPartition.Content); // CommonPartition
            else if (CurrentContentSet != InTOCSet)
                SeekForwardPastTOCMark();
            // else we're in TOC area, that is already past the TOC marker

            if (WentOK)
                SeekBackwardBeforeTOCMark();
            if (WentOK)
            {
                Drive.MoveToNextFilemark(-1); // move to before the last content FM
                if (WentOK)
                    Drive.MoveToNextFilemark(1); // move to after the last FM
            }
            else
            {
                // No TOC mark found -> assume no content/TOC on media, just rewind to the beginning
                ResetError();
                Drive.Rewind();
            }

            return base.MoveToEndOfContent();
        }

        #endregion // Content positioning


        #region *** TOC mark handling ***

        internal bool WriteTOCMark()
        {
            Drive.WriteGapFile(); // write a short file to space out from the content's concluding filemark

            if (WentOK)
                Drive.WriteFilemark(FmksAsTOCMark);

            if (WentOK)
            {
                m_tocMarkInvalidated = false;
                m_logger.LogTrace("Drive #{Drive}: Wrote TOC mark", DriveNumber);
            }
            else
                LogErrorAsDebug("Failed to write TOC mark");

            return WentOK;
        }

        private bool SeekForwardPastTOCMark()
        {
            m_logger.LogTrace("Drive #{Drive}: Seeking forward past TOC mark", DriveNumber);

            Drive.MovePastSeqFilemarks(FmksAsTOCMark); // need + 1 to move past the last filemark

            if (WentOK)
            {
                m_tocMarkInvalidated = false; // mark confirmed present on tape
                m_logger.LogTrace("Drive #{Drive}: Moved forward past TOC mark", DriveNumber);
            }
            else
                LogErrorAsDebug("Failed to seek forward past TOC mark");

            return WentOK;
        }

        private bool SeekBackwardBeforeTOCMark()
        {
            m_logger.LogTrace("Drive #{Drive}: Seeking backward before TOC mark", DriveNumber);

            Drive.MovePastSeqFilemarks(-FmksAsTOCMark);

            if (WentOK)
            {
                m_tocMarkInvalidated = false; // mark confirmed present on tape
                m_logger.LogTrace("Drive #{Drive}: Moved backward before TOC mark", DriveNumber);
            }
            else
                LogErrorAsDebug("Failed to seek backward before TOC mark");

            return WentOK;
        }

        #endregion // TOC mark handling

    } // TapeNavigatorTOCInSetWithFmksAndTOCMark

} // namespace TapeNET
