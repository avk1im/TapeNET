using System.Text;
using System.Diagnostics;

using Windows.Win32;
using Windows.Win32.Foundation;

using Microsoft.Extensions.Logging;


namespace TapeNET
{
    // Handles the positioning of the tape
    //  Supports the notion of "content" area with "content sets" and a "TOC" area
    //  The base class for the derivates specific to TOC placement
    public abstract class TapeNavigator : TapeDriveHolder<TapeNavigator>
    {
        #region *** Private fields ***

        protected bool m_fmksMode = false; // use filemarks -- valid only for Content, and only with SmksMode. TOC always uses filemarks

        #endregion // Private fields


        #region *** Properties ***

        public static long TOCCapacity => 16 * 1024 * 1024; // 16 MB
        public static bool UseTOCMark { get; set; } = true; // only used if TOC is in set w/o setmarks
        public virtual bool TOCInvalidated { get; protected set; } = false;

        public virtual long GetRemainingContentCapacity() => Drive.GetRemainingCapacity(); // in bytes
            // Valid only from the current position -> navigate to the end of the content first

        // The content set next to read. Can be counted either from the beginning or the end of content.
        //  0 means the first (oldest written) content set; 1 the second oldest, etc.
        //  -1 means "the end of content" -- must be set e.g. for writing a new content set
        //  -2 means the last (most recently written) content set; -3 second last, etc.
        public int TargetContentSet { get; set; }

        // The content set being accessed. Follows the same semantic as TargetContentSet:
        //  0 means the first (oldest written) content set; 1 the second oldest, etc.
        //  -1 means "the end of content" -- set e.g. for writing a new content set
        //  -2 means the last (most recently written) content set; -3 second last, etc.
        // In addition:
        //  UnknownSet means the current content set is unknown / not set yet
        //  InTOCSet means the current position is in the TOC area
        // Notice we never rely on that we know the number of content sets on the tape!
        //  -> we always count either from the beginning or the end of content.
        public int CurrentContentSet { get; protected set; } = UnknownSet;
        public static int UnknownSet => int.MinValue;
        public static int InTOCSet => UnknownSet + 1;
        internal void ResetContentSet() => CurrentContentSet = UnknownSet;

        #endregion // Properties


        #region *** Constructors ***

        public TapeNavigator(TapeDrive drive) : base(drive)
        {
            m_logger.LogTrace("Drive #{Drive}: Created Navigator of type {Type}", DriveNumber, GetType());
        }

        public static TapeNavigator? ProduceNavigator(TapeDrive drive)
        {
            if (!drive.IsMediaLoaded)
                return null;

            if (drive.PartitionCount > 1U)
                return new TapeNavigatorTOCInPartition(drive);

            if (drive.SupportsSetmarks)
                return new TapeNavigatorTOCInSetWithSmks(drive);

            if (drive.SupportsSeqFilemarks && UseTOCMark)
                return new TapeNavigatorTOCInSetWithFmksAndTOCMark(drive);
                    
            return new TapeNavigatorTOCInSetWithFmks(drive);
        }

        #endregion // Constructors


        #region *** Notifications ***

        public virtual void OnBeginWriteTOC() => CurrentContentSet = InTOCSet;
        public virtual void OnBeginWriteContent() { }
            // do NOT set CurrentContentSet to -1 yet since we don't know what set is being overritenn

        public virtual void OnTOCWritten() => CurrentContentSet = InTOCSet;
        public virtual void OnContentWritten() => CurrentContentSet = -1;


        #endregion  // Notifications


        #region *** Operation modes ***

        public bool FmksMode // use filemarks -- valid only for Content, and only with SmksMode. TOC always uses filemarks
        {
            get => m_fmksMode;
            internal set
            {
                if (UseSmks) // can only use filemarks if setmarks are used
                {
                    m_fmksMode = value;
                    m_logger.LogTrace("Drive #{Drive}: FmksMode set to {Value}", DriveNumber, m_fmksMode);
                }
                else
                {
                    m_fmksMode = false;
                    if (value != false)
                        m_logger.LogWarning("Drive #{Drive}: FmksMode not supported since no setmark support", DriveNumber);
                }
            }
        }

        private bool UseSmks // use actual setmarks. Automatically ON if and only if drive supports setmarks; cannot be changed by user
        {
            get => Drive.SupportsSetmarks;
        }

        #endregion // Operation modes


        #region *** TOC positioning ***

        public virtual bool MoveToBeginOfTOC()
        {
            // Actuial implementation by the derived classes - we just finalize here

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

        public virtual bool MoveToBeginOfContent()
        {
            // Actual implementation by the derived classes - we just finalize here

            if (WentOK)
                CurrentContentSet = 0; // we're at the beginning of content

            if (WentOK)
                m_logger.LogTrace("Drive #{Drive}: Moved to the beginning of content", DriveNumber);
            else
                LogErrorAsDebug("Failed to move to the beginning of content");

            return WentOK;
        }

        public virtual bool MoveToEndOfContent()
        {
            // Actual implementation by the derived classes - we just finalize here

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
                    //if (LastErrorWin32 == WIN32_ERROR.ERROR_BEGINNING_OF_MEDIA) // might hit the very beginning of the first set
                    //    which would mean we're at the beginning of the first set - yet cannot know if that's the right one!
                    if (WentOK)
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
                    if (LastErrorWin32 == WIN32_ERROR.ERROR_BEGINNING_OF_MEDIA && TargetContentSet == 0) // we hit the beginning of the first set
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
            else // we're emulating setmarks with filemarks
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
            else // we're emulating setmarks with filemarks
                Drive.WriteFilemark();

            if (WentOK)
                m_logger.LogTrace("Drive #{Drive}: Wrote content setmark", DriveNumber);
            else
                LogErrorAsDebug("Failed to write content setmark");

            return WentOK;
        }

        internal bool MoveToNextContentFilemark(int count = 1)
        {
            // Filemarks in content are used only in FmksMode
            if (!FmksMode)
            {
                ResetError();
                return true;
            }
            
            Drive.MoveToNextFilemark(count);

            if (WentOK)
                m_logger.LogTrace("Drive #{Drive}: Moved by {Count} content filemark(s)", DriveNumber, count);
            else
                LogErrorAsDebug("Failed to move to next content filemark(s)");

            return WentOK;
        }

        internal bool WriteContentFilemark()
        {
            // Filemarks in content are used only in FmksMode
            if (!FmksMode)
            {
                ResetError();
                return true;
            }

            Drive.WriteFilemark();

            if (WentOK)
                m_logger.LogTrace("Drive #{Drive}: Wrote a content filemark", DriveNumber);
            else
                LogErrorAsDebug("Failed to write a content filemark");

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


    public class TapeNavigatorTOCInPartition : TapeNavigator
    {
        #region *** Constants ***

        // Notice TOC in partition 2 ("initiator partition"), content in partition 1
        private const int TOCPartition = 2;
        private const int ContentPartition = 1;

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

            Drive.MoveToPartition(TOCPartition);

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

            Drive.MoveToPartition(ContentPartition);

            return base.MoveToEndOfContent();
        }

        public override bool MoveToEndOfContent()
        {
            m_logger.LogTrace("Drive #{Drive}: Moving to the end of content partition", DriveNumber);

            if (CurrentContentSet == -1) // already at the end of content
            {
                m_logger.LogTrace("Drive #{Drive}: Already at the end of content", DriveNumber);
                return true;
            }

            Drive.FastforwardToEnd(partition: ContentPartition);

            return base.MoveToEndOfContent();
        }

        #endregion // Content positioning

    } // TapeNavigatorTOCInPartition


    public abstract class TapeNavigatorTOCInSet(TapeDrive drive) : TapeNavigator(drive)
    {
        #region *** Constants ***

        protected const int CommonPartition = 1;

        #endregion // Constants


        #region *** Properties ***

        public override long GetRemainingContentCapacity() => base.GetRemainingContentCapacity() - TOCCapacity; // in bytes
            // Valid only from the current position -> navigate to the end of the content first

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
            Drive.FastforwardToEnd(partition: CommonPartition);

            // The TOC is in the last set of the only partion [content][SM][toc1][FM][toc1][FM] -> next move to before the last setmark (indicating end of content)
            if (WentOK)
                MoveToNextContentSetmark(-1); // this will bring us to right before the setmark
            if (WentOK)
                MoveToNextContentSetmark(1); // Finally go 1 setmark forward to after the setmark -- the beginning of TOC data == of the to-be-written content data
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
                if (WentOK)
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
                MoveToEndOfConternInternal();
            }

            return base.MoveToBeginOfTOC();
        } // MoveToBeginOfTOC

        #endregion // TOC positioning


        #region *** Content positioning ***

        private void MoveToEndOfConternInternal()
        {
            // QUIRK in Quantum SDLT: it seems necessary to rewind before going to the end of the data
            Drive.Rewind();

            // First move to the end of the data in the partition. Notice the following will produce an error if TOC hasn't been written yet
            Drive.FastforwardToEnd(partition: CommonPartition);
            
            // Next move to before the filemark before first TOC file
            if (WentOK)
                Drive.MoveToNextFilemark(-3); // this will bring us to right before the filemark before first TOC file
            // Finally advance 1 filemark forward to after the filemark -- the beginning of TOC data == of the to-be-written content data
            if (WentOK)
                Drive.MoveToNextFilemark(1);
        }

        public override bool MoveToEndOfContent()
        {
            m_logger.LogTrace("Drive #{Drive}: Moving to the end of content in set with filemarks", DriveNumber);

            if (CurrentContentSet == -1) // already at the end of content
            {
                m_logger.LogTrace("Drive #{Drive}: Already at the end of content", DriveNumber);
                return true;
            }

            MoveToEndOfConternInternal();

            return base.MoveToEndOfContent();
        } // MoveToEndOfContent()

        #endregion // Content positioning

    } // TapeNavigatorTOCInSetWithFmks


    public class TapeNavigatorTOCInSetWithFmksAndTOCMark : TapeNavigatorTOCInSet
    {
        #region *** Constants ***

        private const int FmksAsTOCMark = 3; // number of filemarks used as TOC mark

        #endregion // Constants

        #region *** Constructors ***

        internal TapeNavigatorTOCInSetWithFmksAndTOCMark(TapeDrive drive) : base(drive) { }

        #endregion // Constructors


        #region *** Notifications ***
        
        public override void OnBeginWriteTOC()
        {
            WriteTOCMark();
            base.OnBeginWriteTOC();
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
                if (TOCInvalidated) //  ...but if we were writing content, we've overwritten the marker
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
                Drive.FastforwardToEnd(partition: CommonPartition);
            else if (CurrentContentSet != InTOCSet)
                SeekForwardPastTOCMark();
            // else we're in TOC area, that is already past the TOC marker

            if (WentOK)
                SeekBackwardBeforeTOCMark();
            if (WentOK)
                Drive.MoveToNextFilemark(-1); // move to before the last content FM
            if (WentOK)
                Drive.MoveToNextFilemark(1); // move to after the last FM

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
                m_logger.LogTrace("Drive #{Drive}: Wrote TOC mark", DriveNumber);
            else
                LogErrorAsDebug("Failed to write TOC mark");

            return WentOK;
        }

        private bool SeekForwardPastTOCMark()
        {
            m_logger.LogTrace("Drive #{Drive}: Seeking forward past TOC mark", DriveNumber);

            Drive.MovePastSeqFilemarks(FmksAsTOCMark); // need + 1 to move past the last filemark

            if (WentOK)
                m_logger.LogTrace("Drive #{Drive}: Moved forward past TOC mark", DriveNumber);
            else
                LogErrorAsDebug("Failed to seek forward past TOC mark");

            return WentOK;
        }

        private bool SeekBackwardBeforeTOCMark()
        {
            m_logger.LogTrace("Drive #{Drive}: Seeking backward before TOC mark", DriveNumber);

            Drive.MovePastSeqFilemarks(-FmksAsTOCMark);

            if (WentOK)
                m_logger.LogTrace("Drive #{Drive}: Moved backward before TOC mark", DriveNumber);
            else
                LogErrorAsDebug("Failed to seek backward before TOC mark");

            return WentOK;
        }

        #endregion // TOC mark handling

    } // TapeNavigatorTOCInSetWithFmksAndTOCMark

} // namespace TapeNET
