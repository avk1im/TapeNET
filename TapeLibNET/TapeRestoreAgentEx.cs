using System.Diagnostics;
using Windows.Win32.Foundation;
using Microsoft.Extensions.Logging;
using TapeLibNET;

namespace TapeLibNET
{
    public enum TapeHowToHandleExisting
    {
        Skip,
        Overwrite,
        KeepBoth
    }

    public class TapeFileNotifiableCollection(params ITapeFileNotifiable[] first) : ITapeFileNotifiable
    {
        protected readonly List<ITapeFileNotifiable> m_notifiables = new(first);

        public void Add(ITapeFileNotifiable notifiable)
        {
            m_notifiables.Add(notifiable);
        }

        public void Remove(ITapeFileNotifiable notifiable)
        {
            m_notifiables.Remove(notifiable);
        }

        public static TapeFileNotifiableCollection operator +(TapeFileNotifiableCollection coll, ITapeFileNotifiable notifiable)
        {
            coll.Add(notifiable);
            return coll;
        }

        public static TapeFileNotifiableCollection operator -(TapeFileNotifiableCollection coll, ITapeFileNotifiable notifiable)
        {
            coll.Remove(notifiable);
            return coll;
        }

        #region *** ITapeFileNotifiable implementation ***
        // ITapeFileNotifiable implementation via delegation to m_notifiables

        public void BatchEndStatistics(int set, int filesProcessed, int filesFailed, long bytesProcessed)
        {
            foreach (var notifiable in m_notifiables)
                notifiable.BatchEndStatistics(set, filesProcessed, filesFailed, bytesProcessed);
        }

        public void BatchStartStatistics(int set, int filesFound)
        {
            foreach (var notifiable in m_notifiables)
                notifiable.BatchStartStatistics(set, filesFound);
        }

        public void OnFileFailed(TapeFileDescriptor fileDescr, Exception ex)
        {
            foreach (var notifiable in m_notifiables)
                notifiable.OnFileFailed(fileDescr, ex);
        }

        public void OnFileSkipped(TapeFileDescriptor fileDescr)
        {
            foreach (var notifiable in m_notifiables)
                notifiable.OnFileSkipped(fileDescr);
        }

        public bool PostProcessFile(ref TapeFileDescriptor fileDescr)
        {
            // if any of the notifiables returns false, return false
            foreach (var notifiable in m_notifiables)
            {
                if (!notifiable.PostProcessFile(ref fileDescr))
                    return false;
            }
            return true;
        }

        public bool PreProcessFile(ref TapeFileDescriptor fileDescr)
        {
            // if any of the notifiables returns false, call OnFileSkipped() for remaining ones and return false
            bool result = true;
            foreach (var notifiable in m_notifiables)
            {
                if (result)
                {
                    if (!notifiable.PreProcessFile(ref fileDescr))
                        result = false;
                }
                else
                {
                    notifiable.OnFileSkipped(fileDescr);
                }
            }
            return result;
        }

        #endregion // ITapeFileNotifiable implementation
    } // class TapeFileNotifiableCollection


    public class TapeFileRestoreAgentEx(TapeDrive drive,
        string? targetDir, bool recurseSubdirs, TapeHowToHandleExisting handleExisting,
        TapeTOC? legacyTOC = null) : TapeFileRestoreAgent(drive, legacyTOC)
    {
        public string? TargetDirectory { get; set; } = targetDir;
        public bool RecurseSubdirectories { get; set; } = recurseSubdirs;
        public TapeHowToHandleExisting HandleExisting { get; set; } = handleExisting;

        protected override bool PreProcessFileInternal(ref TapeFileDescriptor fileDescr)
        {
            if (!base.PreProcessFileInternal(ref fileDescr)) // first of all call base
                return false;

            var orgName = fileDescr.FullName;

            if (!string.IsNullOrEmpty(TargetDirectory))
            {
                if (RecurseSubdirectories)
                {
                    // replace the root of the fileDescr.FullName with TargetDirectory
                    if (Path.IsPathRooted(fileDescr.FullName))
                    {
                        // fileDescr.FullName is an absolute path, replace the root with TargetDirectory
                        fileDescr.FullName = Path.Combine(TargetDirectory, Path.GetRelativePath(Path.GetPathRoot(fileDescr.FullName)!, fileDescr.FullName));
                    }
                    else
                    {
                        // fileDescr.FullName is a relative path, just combine it with TargetDirectory
                        fileDescr.FullName = Path.Combine(TargetDirectory, fileDescr.FullName);
                    }

                }
                else
                {
                    // replace the directory part of the fileDescr.FullName with TargetDirectory
                    fileDescr.FullName = Path.Combine(TargetDirectory, Path.GetFileName(fileDescr.FullName));
                }
            }

            // ensure that fileDescr.FullName is a fully qualified path name
            fileDescr.FullName = Path.GetFullPath(fileDescr.FullName);

            // create the directory if it doesn't exist
            //  Since we have the full path name in fileDescr.FullName, we can assume Path.GetDirectoryName() is not null
            string directoryName = Path.GetDirectoryName(fileDescr.FullName)!;
            Debug.Assert(!string.IsNullOrEmpty(directoryName));

            if (!Directory.Exists(directoryName))
            {
                try
                {
                    Directory.CreateDirectory(directoryName);
                }
                catch (Exception ex)
                {
                    SetError(ex, $"Couldn't create directory >{directoryName}< for file >{orgName}<");

                    m_logger.LogWarning(ex, "Couldn't create directory >{Directory}< for file >{File}<", directoryName, orgName);
                    
                    return false;
                }
            }

            if (HandleExisting != TapeHowToHandleExisting.Overwrite && File.Exists(fileDescr.FullName))
            {
                switch (HandleExisting)
                {
                    case TapeHowToHandleExisting.Skip:
                        m_logger.LogTrace("File >{File}< already exists -> SKIPPED", fileDescr.FullName);
                        
                        return false;

                    case TapeHowToHandleExisting.KeepBoth:
                        string newFileName;
                        uint counter = 1;
                        do
                        {
                            newFileName = Path.Combine(directoryName,
                                Path.GetFileNameWithoutExtension(fileDescr.FullName) + $"({counter})" + Path.GetExtension(fileDescr.FullName));
                            counter++;
                        } while (File.Exists(newFileName));

                        fileDescr.FullName = newFileName;
                        break;
                }
            }

            m_logger.LogTrace("Restoring file >{Org}< as >{File}<", orgName, fileDescr.FullName);

            return true;
        }
    } // class TapeFileRestoreAgentEx

} // namespace TapeNET
