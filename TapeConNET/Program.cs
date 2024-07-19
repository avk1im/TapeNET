// See https://aka.ms/new-console-template for more information

using System.Runtime.InteropServices;
using System.Diagnostics;

using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.System.SystemServices;
using Windows.Win32.Security;
using Windows.Win32.Storage.FileSystem;

using System.Text;
using System.ComponentModel;
using System.IO;
using System.Text.RegularExpressions;
using System.Linq.Expressions;
using System.Diagnostics.Metrics;
using System.Collections.Generic;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Debug; // for DebugLoggerProvider

using System.Collections.ObjectModel;
using System.Reflection;
using System.Diagnostics.CodeAnalysis;

using TapeNET;



//[assembly: SupportedOSPlatform("windows10.0.19041")] // Windows 8.0 and later -- set via project properties

#region *** Global program variables ***

ILoggerFactory factory = LoggerFactory.Create(builder =>
{
    builder
        .AddDebug()
        .SetMinimumLevel(LogLevel.Trace);
});
using TapeDrive tapeDrive = new(factory);
TapeTOC? legacyTOC = null;
Windows.Win32.System.SystemServices.Stopwatch stopwatch = new();
const string projectName = "TapeConNET";
const string helpTextResourceName = $"{projectName}.TapeCon.Help.txt";

// Mode seetings -- controlled by mode flags
bool quiteMode = false;
string? setDescription = null;
bool filemarksMode = false;
const uint defaultBlockSizeKB = 16U;
uint blockSizeKB = defaultBlockSizeKB;
long contentCapacityLimit = -1L; // no limit
string targetDir = string.Empty;
bool subdirectoriesMode = false;
bool appendMode = true;
int? appendAfterSet = null; // if null, the set is not set
bool? incrementalMode = null; // if null, the mode is not set
//bool incrementalModeSetExplicitly = false; // need this since incremental by default is off for backup but on for restoring incremental sets
TapeHowToHandleExisting handleExisting = TapeHowToHandleExisting.KeepBoth;
TapeHashAlgorithm hashAlgorithm = TapeHashAlgorithm.None;

var blockSizesKB = new ReadOnlyCollection<uint>([0U, 1U, 2U, 4U, 8U, 16U, 32U, 64U, defaultBlockSizeKB]); // 0 = use the drive's default

Dictionary<string, TapeHashAlgorithm> hashAlgorithmMap = new(StringComparer.OrdinalIgnoreCase)
{
    { "none", TapeHashAlgorithm.None },
        { "off", TapeHashAlgorithm.None },
    { "crc32", TapeHashAlgorithm.Crc32 },
        { "on", TapeHashAlgorithm.Crc32 },
        { "32", TapeHashAlgorithm.Crc32 },
    { "crc64", TapeHashAlgorithm.Crc64 },
        { "64", TapeHashAlgorithm.Crc64 },
    { "xxhash32", TapeHashAlgorithm.XxHash32 },
        { "xx32", TapeHashAlgorithm.XxHash32 },
    { "xxhash3", TapeHashAlgorithm.XxHash3 },
        { "xx3", TapeHashAlgorithm.XxHash3 },
    { "xxhash64", TapeHashAlgorithm.XxHash64 },
        { "xx64", TapeHashAlgorithm.XxHash64 },
    { "xxhash128", TapeHashAlgorithm.XxHash128 },
        { "xx128", TapeHashAlgorithm.XxHash128 },
        { "128", TapeHashAlgorithm.XxHash128 },
};
bool ParseHashAlgorithm(string input, out TapeHashAlgorithm algorithm)
{
    return hashAlgorithmMap.TryGetValue(input.ToLower(), out algorithm);
}

Dictionary<string, TapeHowToHandleExisting> handleExistingMap = new(StringComparer.OrdinalIgnoreCase)
{
    { "skip", TapeHowToHandleExisting.Skip },
        { "s", TapeHowToHandleExisting.Skip },
    { "overwrite", TapeHowToHandleExisting.Overwrite },
        { "o", TapeHowToHandleExisting.Overwrite },
    { "keepboth", TapeHowToHandleExisting.KeepBoth },
        { "k", TapeHowToHandleExisting.KeepBoth },
        { "keep", TapeHowToHandleExisting.KeepBoth },
        { "both", TapeHowToHandleExisting.KeepBoth },
        { "rename", TapeHowToHandleExisting.KeepBoth },
};
bool ParseHandleExisting(string input, out TapeHowToHandleExisting howToHandle)
{
    return handleExistingMap.TryGetValue(input.ToLower(), out howToHandle);
}

Dictionary<string, FlagHandler> flagHandlers = new()
{
    {"-help", HandleHelp},
        {"-h", HandleHelp},
        {"-?", HandleHelp},
    {"-drive", HandleDrive},
        {"-d", HandleDrive},
    {"-format", HandleFormat},
    // do NOT offer any short form for the destructive flag "format", e.g. {"-f", HandleFormat},
    {"-eject", HandleEject},
        {"-j", HandleEject},
    {"-quietmode", HandleQuiet},
        {"-quiet", HandleQuiet},
        {"-q", HandleQuiet},
    {"-description", HandleDescription},
        {"-desc", HandleDescription},
        {"-name", HandleDescription },
        {"-n", HandleDescription},
    {"-filemarks", HandleFilemarks},
        {"-fm", HandleFilemarks},
    {"-blocksize", HandleBlocksize},
        {"-block", HandleBlocksize},
        {"-z", HandleBlocksize},
    {"-capacity", HandleCapacity},
        {"-cap", HandleCapacity},
    {"-subdirectories", HandleSubdirectories},
        {"-subfolders", HandleSubdirectories},
        {"-subdir", HandleSubdirectories},
        {"-s", HandleSubdirectories},
    {"-crc", HandleHash},
        {"-c", HandleHash},
    {"-append", HandleAppend},
        {"-a", HandleAppend},
    {"-incremental", HandleIncremental},
        {"-inc", HandleIncremental},
        {"-i", HandleIncremental},
    {"-backup", HandleBackup},
        {"-b", HandleBackup},
    {"-target", HandleTarget},
        {"-t", HandleTarget},
    {"-existing", HandleExisting},
        {"-e", HandleExisting},
    {"-restore", HandleRestore},
        {"-r", HandleRestore},
    {"-list", HandleList},
        { "-l", HandleList},
    {"-validate", HandleValidate},
        { "-v", HandleValidate},
    {"-verify", HandleVerify},
        { "-y", HandleVerify},
    {"-filemarks", HandleFilemarks},
        {"-fm", HandleFilemarks},
    {"-blocksize", HandleBlocksize},
        {"-block", HandleBlocksize},
        {"-z", HandleBlocksize},
    {"-capacity", HandleCapacity},
        {"-cap", HandleCapacity},
    {"-subdirectories", HandleSubdirectories},
        {"-subfolders", HandleSubdirectories},
        {"-subdir", HandleSubdirectories},
        {"-s", HandleSubdirectories},
    {"-crc", HandleHash},
        {"-c", HandleHash},
    {"-append", HandleAppend},
        {"-a", HandleAppend},
    {"-incremental", HandleIncremental},
        {"-inc", HandleIncremental},
        {"-i", HandleIncremental},
    {"-backup", HandleBackup},
        {"-b", HandleBackup},
    {"-target", HandleTarget},
        {"-t", HandleTarget},
    {"-existing", HandleExisting},
        {"-e", HandleExisting},
    {"-restore", HandleRestore},
        {"-r", HandleRestore},
    {"-list", HandleList},
        { "-l", HandleList},
    {"-validate", HandleValidate},
        { "-v", HandleValidate},
    {"-verify", HandleVerify},
        { "-y", HandleVerify},
};

#endregion // Global program variables


#region *** Static functions ***

static List<List<string>> ParseCommandLine(string[] args)
{
    List<List<string>> flagValuesList = [];
    List<string>? currentSet = null;

    foreach (var arg in args)
    {
        if (arg.StartsWith('-') && !char.IsDigit(arg[1])) // check that it's not a negative number
        {
            // Check for "flag:value" format
            if (arg.SplitAt(':', out string flag, out string value0))
            {
                // Start a new set with flag and 1st value
                currentSet = [flag, value0];
            }
            // Check for "flag=value" format
            else if (arg.SplitAt('=', out flag, out value0))
            {
                // Start a new set with flag and 1st value
                currentSet = [flag, value0];
            }
            // Check for "flag+" format
            else if (arg[^1] == '+')
            {
                currentSet = [arg.TrimEnd('+'), "on"];
            }
            // Check for "flag-" format
            else if (arg[^1] == '-')
            {
                currentSet = [arg.TrimEnd('-'), "off"];
            }
            else
            {
                // Start a new set with the flag
                currentSet = [arg];
            }
            flagValuesList.Add(currentSet);
        }
        else
        {
            // Add values to the current set
            currentSet?.Add(arg);
        }
    }

    return flagValuesList;
}

[DoesNotReturn]
static void OnFatalError(string msg, int exitCode = -1)
{
    Console.WriteLine(msg);
    Console.WriteLine("!!! Fatal error. Aborting!");
    Environment.Exit(exitCode); // Exit with an error code
}

static ConsoleKey GetConsoleKey(string prompt)
{
    // empty keyboard buffer
    while (Console.KeyAvailable)
        Console.ReadKey(intercept: true);

    Console.Write(prompt);
    var keyInfo = Console.ReadKey(intercept: true);

    // output the pressed key if reprentable
    if (keyInfo.KeyChar >= 32 && keyInfo.KeyChar <= 126)
        Console.Write(keyInfo.KeyChar);

    return keyInfo.Key;
}

static string? LoadResourceString(string resourceName)
{
    /*
    var names = Assembly.GetExecutingAssembly().GetManifestResourceNames();
    foreach (var name in names)
        Debug.WriteLine(name);
    */

    using Stream? stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resourceName);
    if (stream == null)
        return null;

    using StreamReader reader = new(stream);
    return reader.ReadToEnd();
}

static string? LookupSection(string content, string? section, string separator = "##")
{
    int startIndex;

    if (section == null)
    {
        startIndex = 0;
    }
    else
    {
        /*
        // search for section headers of format e.g. ##help##
        string marker = separator + section + separator;
        
        startIndex = content.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (startIndex < 0)
            return null;

        startIndex += marker.Length;
        */

        // search inside section headers with options, of format e.g. ##help|h|?##
        string pattern = $"{Regex.Escape(separator)}(.*?\\|)*{Regex.Escape(section)}(\\|.*?)*{Regex.Escape(separator)}";
        /*
        Pattern Breakdown
        1. ${Regex.Escape(separator)}:
        This part escapes the separator string (e.g. ##) to ensure it is treated as a literal string in the regular expression.
        Regex.Escape is used to escape any special characters in the separator.
        2. (.*?\\|)*:
        .*? is a non-greedy match for any character (except for a newline) zero or more times.
        \\| matches the literal | character.
        The entire group (.*?\\|)* matches any sequence of characters followed by a |, zero or more times. This allows for any number of options before the specified section.
        3. ${Regex.Escape(section)}:
        This part escapes the section string to ensure it is treated as a literal string in the regular expression.
        It matches the specific section name you are looking for.
        4. (\\|.*?)*:
        \\| matches the literal | character.
        .*? is a non-greedy match for any character (except for a newline) zero or more times.
        The entire group (\\|.*?)* matches any sequence of | followed by characters, zero or more times. This allows for any number of options after the specified section.
        5. ${Regex.Escape(separator)}:
        This part escapes the separator string again to ensure it is treated as a literal string in the regular expression.
        It matches e.g. the closing separator, e.g. ##.
        */

        Match match = Regex.Match(content, pattern, RegexOptions.IgnoreCase);

        if (!match.Success)
            return null;

        startIndex = match.Index + match.Length;
    }
    
    int endIndex = content.IndexOf(separator, startIndex, StringComparison.OrdinalIgnoreCase);
    if (endIndex < 0) endIndex = content.Length;
    
    return content[startIndex..endIndex].Trim();
}

static string FormatSetIndex(TapeTOC toc, int setIndex)
{
    setIndex = toc.SetIndexToStd(setIndex);
    return $"{setIndex} | {toc.SetIndexToAlt(setIndex)}";
}

#endregion // Static functions


#region *** Local functions ***

#region *** User interaction helpers ***

void OnNonFatalError(string msg, int exitCode = -1)
{
    if (quiteMode)
    {
        Console.WriteLine(msg + " -> Quite mode -> Continuing...");
        return;
    }

    if (GetConsoleKey(msg + "\n ?? Press 'C' to continue or any other key to abort: ") == ConsoleKey.C)
    {
        Console.WriteLine(" -> Continuing...");
    }
    else
    {
        Console.WriteLine(" -> Aborting!");
        Environment.Exit(exitCode); // Exit with an error code
    }
}

bool MessageYesNoCancel(string msg, int exitCode = -1)
{
    if (quiteMode)
    {
        Console.WriteLine(msg + " -> Quite mode -> Assuming Yes");
        return true;
    }

    switch (GetConsoleKey(msg + "\n ?? Press 'Y' for Yes, 'N' for No, or any other key to abort: "))
    {
        case ConsoleKey.Y:
            Console.WriteLine(" -> Yes");
            return true;
        case ConsoleKey.N:
            Console.WriteLine(" -> No");
            return false;
        default:
            Console.WriteLine(" -> Aborting!");
            Environment.Exit(exitCode);
            return false;
    }
}

#endregion // User interaction helpers


#region *** Parsing and evaluation helpers ***

bool EvaluateOnOffFlag(List<string> values, string flagName, bool defaultValue, out bool flagSet)
{
    flagSet = true;

    if (values.Count == 1)
    {
        if (values[0].Equals("on", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }
        else if (values[0].Equals("off", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
    }
    else if (values.Count == 0)
    {
        // no specifier value means "on"
        return true;
    }

    if (!MessageYesNoCancel($"!!! {flagName}: Invalid or missing value (ON/OFF). Proceed with {(defaultValue ? "ON" : "OFF")} ?"))
        return defaultValue;

    flagSet = false;
    return false;
} // EvaluateOnOffFlag()


bool ParseSetIndex(TapeFileAgent agent, string value, out int setIndex)
{
    // Notice backup set index assignment:
    //  positive number means counting from the oldest up: 1 is the oldest set, 2 the second oldest, etc.
    //  0 or negative number means counting from the latest down: 0 is the latest, -1 is the second latest, -2 is the 3rd latest, etc.
    //  0 means the latest (last backed up) set -- this is the default value
    //  Illustration for 3 sets (Count = 3):
    //  index:      1      2      3    <- this is what we return via CurrentSetIndex
    //  oldest -> [set0] [set1] [set2] <- latest
    //  index:     -2     -1      0    <- this is what we also understand

    var toc = agent.TOC;
    setIndex = 0; // by default, use the latest set
    if (int.TryParse(value, out setIndex))
    {
        // validate the set number
        if (setIndex > 0)
        {
            if (setIndex > toc.MaxSetIndex)
            {
                if (MessageYesNoCancel($"!!! Set index >{setIndex}< is out of range [1..{toc.MaxSetIndex}]. " +
                    $"Proceed with the last backup set({toc.MaxSetIndex}) ?"))
                {
                    setIndex = toc.MaxSetIndex;
                }
                else
                    return false;
            }
        }
        else if (setIndex <= 0)
        {
            if (setIndex < toc.MinSetIndex)
            {
                if (MessageYesNoCancel($"!!! Set index >{setIndex}< is out of range [{toc.MinSetIndex}..0]. " +
                    $"Proceed with the oldest backup set ({toc.MinSetIndex}) ?"))
                {
                    setIndex = toc.MinSetIndex;
                }
                else
                    return false;
            }
            // return only the positive variety of the index
            setIndex = toc.SetIndexToAlt(setIndex);
        }
        Debug.Assert(setIndex >= 1 && setIndex <= toc.MaxSetIndex);
    }
    else
        return false;

    return true;
}

bool SetCurrentSetFromArgument(TapeFileAgent agent, List<string> values, string operation)
{
    // Notice backup set index assignment:
    //  positive number means counting from the oldest up: 1 is the oldest set, 2 the second oldest, etc.
    //  0 or negative number means counting from the latest down: 0 is the latest, -1 is the second latest, -2 is the 3rd latest, etc.
    //  0 means the latest (last backed up) set -- this is the default value
    //  Illustration for 3 sets (Count = 3):
    //  index:      1      2      3    <- this is what we return via CurrentSetIndex
    //  oldest -> [set0] [set1] [set2] <- latest
    //  index:     -2     -1      0    <- this is what we also understand
    var toc = agent.TOC;

    int setIndex = 0; // by default, use the latest set
    if (values.Count > 0)
    {
        if (!ParseSetIndex(agent, values[0], out setIndex))
        {
            // Console.WriteLine($"!!! Invalid backup set index >{values[0]}<"); // do not report any error
            return false;
        }
    }

    toc.CurrentSetIndex = setIndex;

    if (setIndex == 0)
        Console.WriteLine($"iii {operation} the last backup set (#{FormatSetIndex(toc, 0)})...");
    else
        Console.WriteLine($"iii {operation} the backup set #{FormatSetIndex(toc, toc.CurrentSetIndex)}...");

    return true;
} // SetCurrentSetIndexFromArgument()

#endregion // Parsing and evaluation helpers


#region *** Output and format helpers ***

void WriteDriveInformation()
{
    Console.WriteLine($" ii Device name: >{tapeDrive.DriveDeviceName}<");

    if (!tapeDrive.IsDriveOpen)
    {
        Console.WriteLine("!!! Drive is not open");
        return;
    }

    Console.WriteLine(" ii Supports multiple partitions: " + (tapeDrive.SupportsMultiplePartitions ? "Yes" : "No"));
    Console.WriteLine(" ii Supports setmarks: " + (tapeDrive.SupportsSetmarks ? "Yes" : "No"));
    Console.WriteLine(" ii Supports sequential filemarks: " + (tapeDrive.SupportsSeqFilemarks ? "Yes" : "No"));
    Console.WriteLine(" ii Block size [min..default..max]: " +
        $"[{Helpers.BytesToString(tapeDrive.MinimumBlockSize)}..{Helpers.BytesToString(tapeDrive.DefaultBlockSize)}..{Helpers.BytesToString(tapeDrive.MaximumBlockSize)}]");

    if (tapeDrive.IsMediaLoaded)
    {
        Console.WriteLine(" ii Media loaded: Yes");
        Console.WriteLine($" ii Partition count: {tapeDrive.PartitionCount}");
        Console.WriteLine($" ii Capacity: {Helpers.BytesToStringLong(tapeDrive.Capacity)}");
        Console.WriteLine($" ii Remaining capacity: {Helpers.BytesToStringLong(tapeDrive.GetRemainingCapacity())}");
    }
    else
    {
        Console.WriteLine(" ii Media loaded: No");
    }
} // WriteDriveInformation()

void WriteMediaInformation(TapeTOC toc)
{
    Console.WriteLine($" ii Name: >{toc.Description}<");
    Console.WriteLine($" ii Created on: {toc.CreationTime}");
    Console.WriteLine($" ii Last saved: {toc.LastSaveTime}");
    Console.WriteLine($" ii Backup sets: {toc.Count}");
    Console.WriteLine($" ii Capacity: {Helpers.BytesToStringLong(tapeDrive.Capacity)}");
    var remaining = tapeDrive.GetRemainingCapacity();
    Console.WriteLine($" ii Used: {Helpers.BytesToStringLong(tapeDrive.Capacity - remaining)}");
    Console.WriteLine($" ii Remaining: {Helpers.BytesToStringLong(remaining)}");
    Console.WriteLine($" ii TOC placement: {((tapeDrive.PartitionCount > 1) ? "partition" : "set")}");
    Console.WriteLine($" ii Volume #{toc.Volume}");
    Console.WriteLine($" ii Continued on next volume? {(toc.ContinuedOnNextVolume ? "Yes" : "No")}");
}

static void WriteCurrentSetInformation(TapeTOC toc)
{
    var setTOC = toc.CurrentSetTOC;
    Console.WriteLine($" ii Name: >{setTOC.Description}<");
    Console.WriteLine($" ii Files: {setTOC.Count}");
    Console.WriteLine($" ii Created on: {setTOC.CreationTime}");
    Console.WriteLine($" ii Last saved: {setTOC.LastSaveTime}");
    Console.WriteLine($" ii Block size: {Helpers.BytesToStringLong(setTOC.BlockSize)}");
    Console.WriteLine($" ii Filemarks: {(setTOC.FmksMode ? "ON" : "OFF")}"); // Filemarks = !BlobMode
    Console.WriteLine($" ii Hash algorithm: {setTOC.HashAlgorithm}");
    Console.WriteLine($" ii Incremental: {(setTOC.Incremental ? "Yes" : "No")}");
    Console.WriteLine($" ii Volume: #{setTOC.Volume}");
    Console.WriteLine($" ii Continued from previous volume: {(toc.IsCurrentSetContFromPrevVolume ? "Yes, directly" :
        toc.IsCurrentSetContFromPrevVolumeInc ? "Yes, incrementally" : "No")}");
    Console.WriteLine($" ii Continued on next volume: {(toc.IsCurrentSetContOnNextVolume ? "Yes" : "No")}");
}

string FormatFileInfo(TapeFileInfo tfi)
{
    var fileDescr = tfi.FileDescr;
    var name = subdirectoriesMode ? fileDescr.FullName : Path.GetFileName(fileDescr.FullName);
    return $"{tfi.Block,10:N0}: {fileDescr.LastWriteTime,24:G} {fileDescr.Length,16:N0}\t{name}";
}

string FormatFileInfoIndex(TapeFileInfo tfi, int index)
{
    var fileDescr = tfi.FileDescr;
    var name = subdirectoriesMode ? fileDescr.FullName : Path.GetFileName(fileDescr.FullName);
    return $"{index,10:N0}# {fileDescr.LastWriteTime,24:G} {fileDescr.Length,16:N0}\t{name}";
}

#endregion // Output and format helpers


#region *** Common core functionality ***

void CheckDrive()
{
    if (!tapeDrive.IsDriveOpen)
    {
        if (!MessageYesNoCancel("!!! No drive open. Proceed with opening drive 0 ?"))
            return;

        HandleDrive(["0"]);
    }
}

bool RestoreTOC(TapeFileAgent agent)
{
    Console.WriteLine(">>> Restoring TOC...");
    if (!agent.RestoreTOC())
    {
        OnNonFatalError("!!! Couldn't restore TOC. Error: " + agent.LastErrorMessage);
        return false;
    }
    Console.WriteLine($"vvv TOC restored with {agent.TOC.Count} set(s); {agent.TOC.CurrentSetTOC.Count} file(s) in the last set");

    return true;
}

#endregion // Common core functionality

#endregion // Local functions


#region *** Main program loop ***

List<List<string>> flagValuesList = ParseCommandLine(args);

if (flagValuesList.Count == 0)
{
    HandleHelp([]);
    return 0;
}

foreach (var flagValues in flagValuesList)
{
    Debug.Assert(flagValues.Count >= 1);

    var flag = flagValues[0].ToLower();

    if (flagHandlers.TryGetValue(flag, out var handler))
    {
        var values = flagValues[1 ..];
        handler(values);
    }
    else
    {
        if (!MessageYesNoCancel($"!!! Unknown flag >{flag}<. Proceed with next flag?"))
            break;
    }
}

return 0;

#endregion // Main program loop


#region *** Flag handlers ***

void HandleHelp(List<string> values)
{
    string? content = LoadResourceString("TapeConNET.TapeCon.Help.txt");
    if (content == null)
        OnFatalError("!!! Help resource not found");

    string? display;

    if (values.Count == 0)
    {
        display = LookupSection(content, null);

        if (display == null)
        {
            Console.WriteLine("!!! Help message not found");
            return;
        }
    }
    else
    {
        foreach (var value in values)
        {
            display = LookupSection(content, value);

            if (display != null)
                Console.WriteLine(display);
            else
                if (MessageYesNoCancel($"!!! Help message not found for flag >{value}<. Display general help?"))
                    HandleHelp([]);

            if (!value.Equals(values.Last()))
                if (!MessageYesNoCancel("??? Continue displaying help?"))
                    break;
        }
    }
}

void HandleDrive(List<string> values)
{
    int driveNumber = -1;

    if (values.Count == 1)
        driveNumber = values[0].TryParseInt(-1);

    if (driveNumber < 0)
    {
        if (!MessageYesNoCancel("!!! Invalid or missing drive number. Proceed with number 0 ?"))
            return;
        driveNumber = 0;
    }

    Console.WriteLine($"\n>>> Openning drive {driveNumber} ...");

    if (!tapeDrive.ReopenDrive((uint)driveNumber))
        OnFatalError("!!! Couldn't open drive. Error: " + tapeDrive.LastErrorMessage);

    Console.WriteLine("vvv Drive opened ok");

    legacyTOC = null; // no TOC yet after (re)opening drive

    Console.WriteLine("\n>>> Loading media...");

    if (!tapeDrive.ReloadMedia())
    {
        Console.WriteLine(tapeDrive.ToString());
        OnFatalError("!!! Couldn't load media. Error: " + tapeDrive.LastErrorMessage);
    }

    Console.WriteLine("vvv Media loaded ok");

    filemarksMode = false; // fill the default filemarks mode
    blockSizeKB = tapeDrive.BlockSize / 1024U; // fill the drive's default block size

    Console.WriteLine("iii Drive information:");
    WriteDriveInformation();
}


void HandleFormat(List<string> values)
{
    if (!MessageYesNoCancel("!!! WARNING: Formatting media will erase ALL data. Proceed?"))
        return;
    Console.WriteLine("\n>>> Formatting media...");

    CheckDrive();

    bool enforceSinglePartition = false;

    if (values.Count == 1 && values[0].Equals("single", StringComparison.OrdinalIgnoreCase))
    {
        enforceSinglePartition = true;
        Console.WriteLine("iii Enforcing single partition format");
    }

    if (!tapeDrive.FormatMedia(enforceSinglePartition ? -1 : TapeNavigator.TOCCapacity))
    {
        OnNonFatalError("!!! Couldn't format media. Error: " + tapeDrive.LastErrorMessage);
        return;
    }
    else
        Console.WriteLine($"vvv Media formatted ok with TOC in {((tapeDrive.PartitionCount > 1)? "partition" : "set")}");

    legacyTOC = null; // no TOC after formatting

    Console.WriteLine("\n>>> Loading media...");

    if (!tapeDrive.ReloadMedia())
        OnFatalError("!!! Couldn't load media. Error: " + tapeDrive.LastErrorMessage);

    Console.WriteLine("vvv Media loaded ok");

    Console.WriteLine("iii Drive & media information:");
    Console.WriteLine(tapeDrive.ToString());
}


void HandleEject(List<string> values)
{
    if (!MessageYesNoCancel("!!! WARNING: Ejecting media will stop the current operation. Proceed?"))
        return;

    Console.WriteLine("\n>>> Ejecting media...");

    if (!tapeDrive.UnloadMedia())
        OnNonFatalError("!!! Couldn't eject media. Error: " + tapeDrive.LastErrorMessage);
    else
        Console.WriteLine("vvv Media ejected ok");
}


void HandleQuiet(List<string> values)
{
    bool quite = EvaluateOnOffFlag(values, "Quiet mode", false, out bool flagSet);
    if (!flagSet)
        return;

    quiteMode = quite;

    Console.WriteLine($"vvv Quiet mode is {(quiteMode ? "ON" : "OFF")}");
}

void HandleDescription(List<string> values)
{
    if (values.Count == 0)
    {
        setDescription = null;
        Console.WriteLine("vvv Backup set description set to standard");
        return;
    }

    // concatenate all values into a single string delimated by spaces
    StringBuilder sb = new(values[0]);
    for (int i = 1; i < values.Count; i++)
        sb.Append(' ').Append(values[i]);
    setDescription = sb.ToString();
    
    Console.WriteLine($"vvv Backup set description set to >{setDescription}<");
}

void HandleFilemarks(List<string> values)
{
    bool filemarks = EvaluateOnOffFlag(values, "Filemarks", false, out bool flagSet);
    if (!flagSet)
        return;

    CheckDrive();

    // filemarks mode only if drive supports setmarks
    filemarksMode = tapeDrive.SupportsSetmarks && filemarks;

    if (filemarksMode != filemarks)
    {
        Console.WriteLine($"!!! Drive does not support filemarks {(filemarks ? "ON" : "OFF")}");
        MessageYesNoCancel($"!!! Proceed with the drive's default filemarks mode? {(filemarksMode ? "ON" : "OFF")}");
    }
    Console.WriteLine($"vvv Filemarks set to {(filemarksMode ? "ON" : "OFF")}");
}

void HandleBlocksize(List<string> values)
{
    // we allow both KB and byte values
    if (values.Count == 0)
    {
        Console.WriteLine("iii Preferred block size value not specified -> setting default");
        blockSizeKB = defaultBlockSizeKB;
    }
    else if (!uint.TryParse(values[0], out uint size)/* || !blockSizesKB.Contains((size < 1024U) ? size : size / 1024U)*/)
    {
        Console.WriteLine("!!! Invalid preferred block size value");
        return;
    }
    else
        blockSizeKB = (size < 1024U) ? size : size / 1024U;

    // Find in blockSizesKB the value closest to blockSizeKB
    blockSizeKB = blockSizesKB.Aggregate((x, y) => Math.Abs(x - blockSizeKB) < Math.Abs(y - blockSizeKB) ? x : y);

    if (blockSizeKB != 0)
        Console.WriteLine($"vvv Preferred block size set to {blockSizeKB} KB");
    else
        Console.WriteLine("vvv Preferred block size set to the drive's default");
}

void HandleCapacity(List<string> values)
{
    CheckDrive();

    if (values.Count == 0)
    {
        contentCapacityLimit = -1L;
        Console.WriteLine("vvv Content capacity limit not specified -> limit OFF");
        return;
    }
    else if (values[0].Equals("off", StringComparison.OrdinalIgnoreCase))
    {
        contentCapacityLimit = -1L;
        Console.WriteLine("vvv Content capacity limit OFF");
        return;
    }
    else if (!int.TryParse(values[0], out int capacity))
    {
        Console.WriteLine("!!! Invalid content capacity limit value");
        return;
    }
    else
    {
        if (capacity < 0)
        {
            Console.WriteLine("vvv Content capacity limit OFF");
            contentCapacityLimit = -1L;
        }
        else
        {
            contentCapacityLimit = (capacity > 1024 * 1024) ? capacity : capacity * (1024L * 1024L); // convert to MB
            Console.WriteLine($"vvv Content capacity limit set to ~{Helpers.BytesToString(contentCapacityLimit)}");
        }
    }
}

void HandleSubdirectories(List<string> values)
{
    bool subdirs = EvaluateOnOffFlag(values, "Subdirectory recursion", true, out bool flagSet);
    if (!flagSet)
        return;

    subdirectoriesMode = subdirs;

    Console.WriteLine($"vvv Subdirectory recursion is {(subdirectoriesMode ? "ON" : "OFF")}");
}

void HandleAppend(List<string> values)
{
    bool append = true; // default is to append

    // Check if a set index (set to back up after) is specified:
    if (values.Count == 1 && int.TryParse(values[0], out int setIndex) || // -append <set_index> format
        (values.Count == 2) && int.TryParse(values[1], out setIndex) && // -append [on|off] <set_index> format
            EvaluateOnOffFlag([values[0]], "Append backup set", true, out bool flagSet))
    {
        appendMode = true;
        appendAfterSet = setIndex; // we'll validate the set index in the backup operation
        Console.WriteLine($"vvv Append: new backup set will be appended after set #{setIndex}");
        return;
    }
    else
        appendAfterSet = null; // not defined

    // Handle -append [on|off] format
    append = EvaluateOnOffFlag(values, "Append backup set", true, out flagSet);
    if (!flagSet)
        return;

    appendMode = append;

    Console.WriteLine($"vvv Append: new backup set will {(appendMode ? "be appended to" : "replace")} tape content");
}

void HandleIncremental(List<string> values)
{
    bool incremental = EvaluateOnOffFlag(values, "Incremental backup mode", false, out bool flagSet);
    if (!flagSet)
        return;

    incrementalMode = incremental;

    Console.WriteLine($"vvv Incremental backup mode is {(incrementalMode.Value ? "ON" : "OFF")}");
}

void HandleHash(List<string> values)
{
    TapeHashAlgorithm algo = TapeHashAlgorithm.Crc32;
    bool flagSet = false;

    if (values.Count == 1 && ParseHashAlgorithm(values[0], out algo))
    {
        flagSet = true;
    }
    else if (values.Count == 0)
    {
        // no specifier value means "crc32"
        algo = TapeHashAlgorithm.Crc32;
        flagSet = true;
    }

    if (!flagSet)
    {
        if (!MessageYesNoCancel("!!! Data integrity hash: Invalid value. Proceed with Crc32 ?"))
            return;
    }

    hashAlgorithm = algo;

    Console.WriteLine($"vvv Data integrity hash set to: {hashAlgorithm}");
}

void HandleBackup(List<string> values)
{
    Console.WriteLine("\n>>> Backing up files...");

    CheckDrive();

    if (!tapeDrive.PrepareMedia())
        OnFatalError("!!! Couldn't prepare media. Error: " + tapeDrive.LastErrorMessage);

    try
    {
        bool append = appendMode || (incrementalMode ?? false); // if incremental, must append
        bool incremental = (incrementalMode ?? false);

        using var agent = new TapeFileBackupAgent(tapeDrive, append ? legacyTOC : null);
        var toc = agent.TOC;
        agent.Manager.ContentCapacityLimit = contentCapacityLimit;
        TapeTOC? backupTOC = null;
        OnFileBackupProcessor proc = new(toc);

        if (values.Count == 0)
        {
            Console.WriteLine("!!! No files specified to backup");
            return;
        }

        if (append)
        {
            if (legacyTOC == null && !RestoreTOC(agent))
            {
                if (!MessageYesNoCancel("??? Proceed to backup replacing media content?"))
                    return;
                append = false;
                incremental = false;
            }
            else // TOC loaded ok or legacy TOC is used
            {
                // Check if this volume is a part of multi-volume
                if (toc.ContinuedOnNextVolume)
                {
                    Console.WriteLine($"!!! The loaded media volume #{toc.Volume} might be continued on the next volume!");
                    if (!MessageYesNoCancel("??? Proceed to backup destroying the multi-volume sequence?"))
                        return;
                }

                // If append replacing set index has been specified, check if it's valid
                if (appendAfterSet.HasValue)
                {
                    appendAfterSet = toc.SetIndexToStd(appendAfterSet.Value);

                    if (appendAfterSet.Value < toc.FirstSetOnVolume || appendAfterSet.Value > toc.LastSetOnVolume)
                    {
                        Console.WriteLine($"!!! Append after set #{appendAfterSet.Value} " +
                            $"is out of range [#{FormatSetIndex(toc, toc.FirstSetOnVolume)}..#{FormatSetIndex(toc, toc.LastSetOnVolume)}].");
                        if (!MessageYesNoCancel("Proceed appending after all sets?"))
                            return;
                        appendAfterSet = null; // not defined; proceed with straight append
                    }
                    else // appendAfterSet is within the range
                    {
                        if (appendAfterSet.Value == toc.LastSetOnVolume)  // the last set -- the straight append case
                            appendAfterSet = null; // not defined; proceed with straight append
                        else
                        {
                            if (!MessageYesNoCancel($"??? Proceed to backup replacing ALL sets after #{FormatSetIndex(toc, appendAfterSet.Value)}?"))
                                return;
                            backupTOC = new(toc); // furnish a backup copy
                            Debug.Assert(appendAfterSet.Value >= toc.FirstSetOnVolume && appendAfterSet.Value < toc.LastSetOnVolume);
                            Debug.Assert(backupTOC != null);
                            // so NOT yet clip the TOC after the specified set -> to retain it's correct index on the volume!
                            toc.CurrentSetIndex = appendAfterSet.Value + 1; // this current set will get replaced by the new backup set
                            toc.EmptyCurrentSet(); // prepare for the new backed up filed -> this will trigger newSet = false
                        }
                    }
                }
                // else straight backup appending new set
            }
        }

        if (!append)
        {
            if (!MessageYesNoCancel("!!! WARNING: Proceed to backup replacing ALL media content?"))
                return;
            if (!toc.IsEmpty)
                backupTOC = new(toc); // firnish a backup copy
            legacyTOC = null; // TOC will be replaced along with the whole media content
            toc.RemoveAllSets();
        }

        if (string.IsNullOrEmpty(toc.Description))
            toc.Description = $"Media created {DateTime.Now}";

        Console.WriteLine("iii Media information:");
        WriteMediaInformation(toc);

        if (toc.IsEmpty)
            incremental = false; // the new set cannot be incremental without any previous sets / files

        bool newSet; // have we newly added the current set?
        if (append)
        {
            if (toc.CurrentSetTOC.Count > 0)
            {
                toc.AddNewSetTOC(0, incremental);
                newSet = true;
            }
            else // even if appending, reuse the last set if it's empty (or has been emptied for appendAfterSet case)
            {
                toc.MarkCurrentSetIncremental(incremental); // just mark it incremental if necessary
                newSet = false;
            }
        }
        else
        {
            newSet = true; // the whole TOC is created anew
        }
        Debug.Assert(toc.CurrentSetTOC.Count == 0); // the current set TOC has no file entries 

        toc.CurrentSetTOC.Description = setDescription ?? $"Backup set created {DateTime.Now}";
        toc.CurrentSetTOC.HashAlgorithm = hashAlgorithm;
        toc.CurrentSetTOC.BlockSize = blockSizeKB * 1024;
        toc.CurrentSetTOC.FmksMode = filemarksMode;

        Console.WriteLine("iii New backup set information:");
        WriteCurrentSetInformation(toc);

        stopwatch.Reset();

        do
        {
            stopwatch.Start();

            bool result = agent.CanResumeToNextVolume ?
                agent.ResumeBackupToNextVolume() :
                agent.BackupFilesToCurrentSet(newSet, values, subdirectoriesMode, ignoreFailures: true, proc);

            bool noFilesBackedup = toc.CurrentSetTOC.Count == 0; // no files backed up

            if (result)
            {
                if (appendAfterSet.HasValue)
                    toc.RemoveSetsAfterCurrent();

                if (noFilesBackedup)
                {
                    Console.WriteLine("iii No files backed up"); // might be ok -- e.g. if incremental backup or no matching files found
                    toc.RemoveLastEmptySet();
                    stopwatch.Stop();
                    legacyTOC = toc; // save the TOC for the next operation
                    break; // needn't carry on to back up the TOC
                }
            }
            else // !result -> there were some failures
            {
                // Check first if it was a volume overrun and we can continue
                if (agent.CanResumeToNextVolume)
                {
                    Console.WriteLine($"\niii Volume #{toc.Volume} is full -> Backup can continue to next volume after TOC has been backed up");

                    if (appendAfterSet.HasValue)
                        toc.RemoveSetsAfterCurrent();

                    if (noFilesBackedup)
                        toc.RemoveLastEmptySet(); // no need to back up the last empty set; volume continuation is marked in the TOC
                }
                else // no continuation to the next volume -> check if we can remove the last empty set
                {
                    if (noFilesBackedup)
                    {
                        if (appendAfterSet.HasValue && backupTOC != null) // if we removed sets, try to restore them
                        {
                            toc.CopyFrom(backupTOC); // restore the TOC from the backup copy
                            Console.WriteLine("!!! No files backed up -> no sets removed");
                        }
                        else
                        {
                            toc.RemoveLastEmptySet();
                            Console.WriteLine($"\n!!! No files backed up of {proc.ProcessedCount} file(s) processed");
                        }

                        if (!agent.Navigator.TOCInvalidated)
                        {
                            stopwatch.Stop();
                            legacyTOC = toc; // save the TOC for the next operation
                            break; // TOC hasn't changed -> only proceed to back up if TOC is in set,
                                   // since it might've been affected, e.g. by a partial content file write
                        }
                    }
                    else // some files were backed up
                    {
                        Console.WriteLine($"\n!!! {proc.FailedCount} file(s) of {proc.ProcessedCount} failed to back up");
                    }
                }
            }

            if (!noFilesBackedup)
                Console.Write($">>> Backing up TOC with {toc.CurrentSetTOC.Count} new file entr(y|ies)...");
            else
                Console.Write($">>> Backing up TOC...");

            if (!agent.BackupTOC())
            {
                OnNonFatalError("\n!!! Couldn't backup TOC. Error: " + agent.LastErrorMessage);
                if (!toc.IsEmpty)
                {
                    Console.WriteLine(">>> Attempting to enforce TOC backup...");
                    if (!agent.BackupTOC(enforce: true))
                        OnFatalError("!!! Couldn't enforce TOC backup. Error: " + agent.LastErrorMessage);
                    else
                        Console.WriteLine("vvv Enforced TOC backup succeeded");
                }
            }
            else
                Console.WriteLine("OK");

            stopwatch.Stop();

            legacyTOC = toc; // save the TOC for the next operation

            Console.WriteLine($"vvv Backed up {proc.ProcessedCount} file(s) incl. TOC ~ {Helpers.BytesToString(agent.BytesBackedup)} in " + stopwatch.ElapsedTimeSpan +
                $" --> throughput {Helpers.BytesToString((long)(agent.BytesBackedup / stopwatch.ElapsedSeconds))}/s");
            Console.WriteLine($"iii Remaining media capacity: {Helpers.BytesToStringLong(tapeDrive.GetRemainingCapacity())}");

            // Check if we can proceed with the next volume
            if (!agent.CanResumeToNextVolume)
                break; // we're done

            // Guiide the user thru the exchange of media
            if (!MessageYesNoCancel("??? Proceed with backup to the next volume?"))
                break;
            Console.WriteLine(">>> Unloading media...");
            if (!tapeDrive.UnloadMedia())
                OnNonFatalError("!!! Couldn't unload media. Error: " + agent.LastErrorMessage);
            Console.WriteLine($">>> Please remove media with Volume #{toc.Volume} and insert a formatted media for Volume #{toc.Volume + 1}");
            if (!MessageYesNoCancel($"??? Is the media for Volume #{toc.Volume + 1} in the drive?"))
                break;
            if (!MessageYesNoCancel($"!!! WARNING: ALL data on the media for Volume #{toc.Volume + 1} will be erased. Proceed with backup?"))
                break;
            if (!tapeDrive.ReloadMedia())
                OnFatalError("!!! Couldn't load media. Error: " + agent.LastErrorMessage);
            if (!tapeDrive.PrepareMedia())
                OnFatalError("!!! Couldn't prepare media. Error: " + agent.LastErrorMessage);
            Console.WriteLine("vvv Media loaded ok");
            // carry on to resume backup
        } while (true);
    }
    finally
    {
        appendMode = true; // reset the append mode
        appendAfterSet = null; // We only use the value for one backup
    }

} // HandleBackup()


void HandleTarget(List<string> values)
{
    if (values.Count == 0)
    {
        Console.WriteLine($"vvv Target directory REMOVED");
        targetDir = string.Empty;
    }
    else
    {
        try
        {
            targetDir = Path.GetFullPath(values[0]);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"!!! Couldn't set target directory >{values[0]}<. Exception: {ex}");
            if (MessageYesNoCancel($"Set target directory to current? (>{Directory.GetCurrentDirectory()}<)"))
                targetDir = Directory.GetCurrentDirectory();
            else
                return;
        }

        Console.WriteLine($"vvv Target directory set to >{targetDir}<");
        if (!Directory.Exists(targetDir))
            Console.WriteLine($"iii Target directory >{targetDir}< doesn't exist. Will attempt to create");
    }
}

void HandleExisting(List<string> values)
{
    if (values.Count == 0)
    {
        Console.WriteLine($"iii Existing file handling value not specified -> assuming KeepBoth");
        handleExisting = TapeHowToHandleExisting.KeepBoth;
    }
    else if (values.Count > 1 || !ParseHandleExisting(values[0], out handleExisting))
    {
        if (!MessageYesNoCancel("!!! Invalid existing file handling value. Proceed with KeepBoth ?"))
            return;
        handleExisting = TapeHowToHandleExisting.KeepBoth;
    }

    Console.WriteLine($"vvv Existing file handling set to: {handleExisting}");
}


#region *** Restore handlers ***

void UniversalRestore(List<string> values, TapeFileRestoreBaseAgent agent, OnFileEventProcessor fileProcessor, string restoringName)
{
    Console.WriteLine($"\n>>> {restoringName} files...");

    CheckDrive();

    if (!tapeDrive.PrepareMedia())
        OnFatalError("!!! Couldn't prepare media. Error: " + tapeDrive.LastErrorMessage);

    var toc = agent.TOC;

    try
    {
        stopwatch.Restart();

        if (legacyTOC == null && !RestoreTOC(agent))
            return;
        legacyTOC = toc; // save the TOC for the next operation

        Console.WriteLine("iii Media information:");
        WriteMediaInformation(toc);

        SetCurrentSetFromArgument(agent, values, restoringName);

        Console.WriteLine("iii Backup set information:");
        WriteCurrentSetInformation(toc);

        // incremental is on by default for restoring incremental sets, unless switched off explicitly
        bool incremental = toc.CurrentSetTOC.Incremental;
        if (incremental)
        {
            if (incrementalMode.HasValue && !incrementalMode.Value) // incremental mode explicitly set off
            {
                incremental = false;
                Console.WriteLine($"iii {restoringName} incremental backup set ONLY since incremental mode has been switched OFF");
            }
            else
                Console.WriteLine($"iii {restoringName} incremental backup set including earlier dependent sets");
        }

        // Check if any file matching patterns have been specified
        //  Notice that if values[0] is a number, it's interpreted as a set index, not a file pattern
        List<string> patterns = (values.Count > 0 && int.TryParse(values[0], out _)) ? values[1..] : values;

        bool result = incremental ?
        (
            (patterns.Count > 0) ?
                agent.RestoreFilesFromCurrentSetInc(patterns, ignoreFailures: true, fileProcessor) :
                agent.RestoreAllFilesFromCurrentSetInc(ignoreFailures: true, fileProcessor)
        ) : (
            (patterns.Count > 0) ?
                agent.RestoreFilesFromCurrentSet(patterns, ignoreFailures: true, fileProcessor) :
                agent.RestoreAllFilesFromCurrentSet(ignoreFailures: true, fileProcessor)
        );

        stopwatch.Stop();

        if (!result)
        {
            // Check if we can proceed with the next volume
            while (agent.CanResumeFromAnotherVolume)
            {
                // Guiide the user thru the exchange of media
                if (!MessageYesNoCancel($"??? {restoringName}: Proceed with Volume #{agent.VolumeToResumeFrom}?"))
                    break;
                Console.WriteLine(">>> Unloading media...");
                if (!tapeDrive.UnloadMedia())
                    OnNonFatalError("!!! Couldn't unload media. Error: " + tapeDrive.LastErrorMessage);
                Console.WriteLine($">>> Please remove media with Volume #{toc.Volume} and insert media with Volume #{agent.VolumeToResumeFrom}");
                if (!MessageYesNoCancel($"??? Is the media for Volume #{agent.VolumeToResumeFrom} in the drive?"))
                    break;
                Console.WriteLine(">>> Loading media...");
                if (!tapeDrive.ReloadMedia())
                    OnFatalError("!!! Couldn't load media. Error: " + agent.LastErrorMessage);
                if (!tapeDrive.PrepareMedia())
                    OnFatalError("!!! Couldn't prepare media. Error: " + agent.LastErrorMessage);
                Console.WriteLine("vvv Media loaded ok");

                stopwatch.Start(); // keep measuring the same time span
                
                result = agent.ResumeRestoreFromAnotherVolume();
                
                stopwatch.Stop();
            }

            if (!result)
            {
                Console.WriteLine();
                OnNonFatalError($"!!! {restoringName}: {fileProcessor.FailedCount} file(s) of {fileProcessor.ProcessedCount} failed. Error: " + agent.LastErrorMessage);
            }
        }
    }
    catch (Exception ex)
    {
        OnNonFatalError($"!!! {restoringName}: Couldn't process set of files. Exception: " + ex + "\n Error: " + tapeDrive.LastErrorMessage);
        return;
    }

    Console.Write($"iii {restoringName}: {fileProcessor.SucceededCount} file(s) incl. TOC ~ {Helpers.BytesToString(agent.BytesRestored)} processed in " + stopwatch.ElapsedTimeSpan +
        $" --> throughput {Helpers.BytesToString((long)(agent.BytesRestored / stopwatch.ElapsedSeconds))}/s");

} // UniversalRestore()

void HandleRestore(List<string> values)
{
    var agent = new TapeFileRestoreAgentEx(tapeDrive, targetDir, subdirectoriesMode, handleExisting, legacyTOC);
    var toc = agent.TOC;
    var proc = new OnFileRestoreProcessor(toc);
    UniversalRestore(values, agent, proc, "Restoring");
}

void HandleValidate(List<string> values)
{
    var agent = new TapeFileValidateAgent(tapeDrive, legacyTOC);
    var toc = agent.TOC;
    var proc = new OnFileValidateProcessor(toc);
    UniversalRestore(values, agent, proc, "Validating");
}

void HandleVerify(List<string> values)
{
    var agent = new TapeFileVerifyAgent(tapeDrive, legacyTOC);
    var toc = agent.TOC;
    var proc = new OnFileVerifyProcessor(toc);
    UniversalRestore(values, agent, proc, "Verifying");
}

#endregion // Restore handlers


void HandleList(List<string> values)
{
    Console.WriteLine("\n>>> Listing media content...");

    CheckDrive();

    if (!tapeDrive.PrepareMedia())
        OnFatalError("!!! Couldn't prepare media. Error: " + tapeDrive.LastErrorMessage);

    using var agent = new TapeFileAgent(tapeDrive, legacyTOC); // since we only work with TOC, the base agent is enough
    var toc = agent.TOC;

    try
    {
        long tocSize = agent.BytesRestored;
        if (legacyTOC == null && !RestoreTOC(agent))
            return;
        tocSize = agent.BytesRestored - tocSize;

        Console.WriteLine("iii Media information:");
        WriteMediaInformation(toc);
        if (legacyTOC == null) // then we've restored the TOC from the tape so have its size
            Console.WriteLine($"TOC size: {Helpers.BytesToStringLong(tocSize)}");
        Console.WriteLine();

        legacyTOC = toc; // save the TOC for the next operation

        // List backup sets from latest to oldest: either a specified range or all sets on tape
        int startIndex = 0;
        int endIndex = 0;
        if (values.Count > 1)
        {
            // try to interpret first two values as set indexes specifying a range of sets
            if (ParseSetIndex(agent, values[0], out startIndex))
            {
                if (ParseSetIndex(agent, values[1], out endIndex))
                {
                    // two set indexes specified
                    values = values[2..]; // remove the set indexes
                }
                else
                {
                    // one set index specified
                    endIndex = startIndex;
                    values = values[1..]; // remove the set index
                }
            }
        }
        else if (values.Count == 1)
        {
            // try to interpret as a set index
            if (ParseSetIndex(agent, values[0], out startIndex))
            {
                // one set index specified
                endIndex = startIndex;
                values = values[1..]; // remove the set index
            }
        }
        else // no set index specified
        {
            // list content for all sets, from the latest to the oldest (1)
            startIndex = toc.MaxSetIndex;
            endIndex = 1;
        }

        // ensure that indexes are in the range and in standard form
        startIndex = toc.SetIndexToStd(toc.CapSetIndex(startIndex));
        endIndex = toc.SetIndexToStd(toc.CapSetIndex(endIndex));
        if (startIndex < endIndex) // make sure startIndex >= endIndex
            (endIndex, startIndex) = (startIndex, endIndex);

        Debug.Assert(startIndex >= endIndex);

        // Interpret remaining values as file patterns
        var patterns = values;

        int totalFiles = 0;
        long totalSize = tocSize;
        for (int setIndex = startIndex; setIndex >= endIndex; )
        {
            toc.CurrentSetIndex = setIndex;
            Console.WriteLine($"iii Backup set #{FormatSetIndex(toc, setIndex)}:");
            WriteCurrentSetInformation(toc);

            // incremental is on by default for restoring incremental sets, unless switched off explicitly
            bool incremental = toc.CurrentSetTOC.Incremental;
            int lastNonIncIndex = toc.LastNonIncSet; // used only for incremental sets
            if (incremental)
            {
                if (incrementalMode.HasValue && !incrementalMode.Value) // incremental mode explicitly set off
                {
                    incremental = false;
                    Console.WriteLine("iii Listing incremental backup set ONLY since incremental mode has been switched OFF");
                }
                else
                    Console.WriteLine($"iii Listing incremental backup sets down to set #{FormatSetIndex(toc, lastNonIncIndex)}");
            }

            // List files in the set
            int setFiles = 0;
            long setSize = 0;

            var tfisBySets = toc.SelectFiles(incremental, (patterns.Count > 0) ? patterns : null); // null means all files in the set
            // notice even non-incremental sets can be continued from the previous set on the previous volume

            for (int i = 0; i < tfisBySets.Length; i++)
            {
                IEnumerable<TapeFileInfo> tfis = tfisBySets[i] ?? (IEnumerable<TapeFileInfo>)toc[setIndex - i]; // null means all files in the set
                if (incremental || tfisBySets.Length > 1)
                    Console.WriteLine($" ii from set #{FormatSetIndex(toc, setIndex - i)} " +
                        $"on Volume #{toc[setIndex - i].Volume}: " +
                        $"{((!tfis.Any()) ? "none" : $"{tfis.Count()} file(s):")}");
                if (toc.CurrentSetTOC.FmksMode)
                {
                    var indexes = toc[setIndex - i].RefsToIndexes(tfis);
                    for (int j = 0; j < indexes.Count; j++)
                    {
                        var tfi = toc.CurrentSetTOC[indexes[j]];
                        Console.WriteLine(FormatFileInfoIndex(tfi, indexes[j]));
                        setFiles++;
                        setSize += tfi.FileDescr.Length;
                    }
                }
                else // !FmksMode
                {
                    foreach (var tfi in tfis)
                    {
                        Console.WriteLine(FormatFileInfo(tfi));
                        setFiles++;
                        setSize += tfi.FileDescr.Length;
                    }
                }
            } // for i

            Console.WriteLine($" Set: \t{setFiles} file(s) {Helpers.BytesToStringLong(setSize)}");
            totalFiles += setFiles;
            totalSize += setSize;
            Console.WriteLine();

            if (incremental && tfisBySets.Length > 0)
                setIndex -= tfisBySets.Length;
            else
                setIndex--;
        } // for setIndex
        Console.WriteLine($"Total: \t{totalFiles} file(s) {Helpers.BytesToStringLong(totalSize)}");
    }
    catch (Exception ex)
    {
        OnNonFatalError("!!! Error listing backup sets. Exception: " + ex + "\n Error: " + tapeDrive.LastErrorMessage);
    }
} // HandleList()

#endregion // Flag handlers


#region *** Helper types and classes ***

delegate void FlagHandler(List<string> values);

// The base class for our implementations of ITapeFileNotifiable
//  Does not need to declare methods as override, since we always use explicit classes
abstract class OnFileEventProcessor(TapeTOC toc) : ITapeFileNotifiable
{
    public int SetIndex { get; set; } = 0; // set index for the current backup set
    public int SucceededCount { get; private set; } = 0; // accumulated by PostProcessFile()
    public int ProcessedCount { get; private set; } = 0; // reported by BatchEndStatistics()
    public int FailedCount { get; private set; } = 0; // reported by BatchEndStatistics()
    public int CompletedCount => ProcessedCount - FailedCount;
    public int SkippedCount => CompletedCount - SucceededCount;
    public long BytesProcessed { get; private set; } = 0; // reported by BatchEndStatistics()
    public double ElapsedSeconds => stopwatch.ElapsedSeconds;

    protected Windows.Win32.System.SystemServices.Stopwatch stopwatch = new();

    protected string FormatSetIndex(int setIndex)
    {
        setIndex = toc.SetIndexToStd(setIndex);
        return $"{setIndex} | {toc.SetIndexToAlt(setIndex)}";
    }

    public virtual void BatchStartStatistics(int set, int filesFound)
    {
        Console.WriteLine($"iii {filesFound} files found to process for/from backup set #{FormatSetIndex(set)}");
        
        SetIndex = set;
        stopwatch.Restart();
    }
    public void BatchEndStatistics(int set, int filesProcessed, int filesFailed, long bytesProcessed)
    {
        stopwatch.Stop();
        ProcessedCount = filesProcessed;
        FailedCount = filesFailed;
        BytesProcessed = bytesProcessed;
        SetIndex = set;

        WriteStatistics();
    }

    public void WriteStatistics()
    {
        if (ProcessedCount != 0)
        {
            Console.WriteLine($"iii Of {ProcessedCount} file(s) processed for/from backup set #{SetIndex}:");
            Console.WriteLine($" ii Completed:\t{CompletedCount} ~ {100.0 * CompletedCount / ProcessedCount:F2}%");
            Console.WriteLine($" ii Succeeded:\t{SucceededCount} ~ {100.0 * SucceededCount / ProcessedCount:F2}%");
            Console.WriteLine($" ii Skipped:\t{SkippedCount} ~ {100.0 * SkippedCount / ProcessedCount:F2}%");
            Console.WriteLine($" ii Failed:\t{FailedCount} ~ {100.0 * FailedCount / ProcessedCount:F2}%");

            Console.Write($"iii Processed {Helpers.BytesToString(BytesProcessed)} in {ElapsedSeconds} sec");
            Console.WriteLine($" --> throughput {Helpers.BytesToString((long)(BytesProcessed / ElapsedSeconds))}/s");
        }
        else
            Console.WriteLine($"iii No files processed for/from backup set #{SetIndex}");
    }

    public virtual bool PreProcessFile(ref TapeFileDescriptor fileDescr)
    {
        Console.Write($" ii Processing file >{fileDescr.FullName}< : {Helpers.BytesToString(fileDescr.Length)} ");
        return true;
        
    }

    // called for a chance to modify the fileDescr after restoring the file. If returns false, skip applying fileDescr
    public virtual bool PostProcessFile(ref TapeFileDescriptor fileDescr)
    {
        // write just the file name
        Console.WriteLine($"OK >{Path.GetFileName(fileDescr.FullName)}<");
        SucceededCount++;
        return true;
    }

    // called when a file error occurs during restoring the file
    public virtual void OnFileFailed(TapeFileDescriptor fileDescr, Exception ex)
    {
        Console.WriteLine($"Failed >{Path.GetFileName(fileDescr.FullName)}<. Exception: {ex}");
        // FailedCount++; // <-- needn't do it here since failed count is reported by BatchEndStatistics()
    }
    public virtual void OnFileSkipped(TapeFileDescriptor fileDescr)
    {
        // write just the file name
        Console.WriteLine($"Skipped >{Path.GetFileName(fileDescr.FullName)}<");
        // SkippedCount++;
    }

} // class OnFileEventProcessor

class OnFileBackupProcessor(TapeTOC toc) : OnFileEventProcessor(toc)
{
    public override bool PreProcessFile(ref TapeFileDescriptor fileDescr)
    {
        FileInfo fileInfo = new(fileDescr.FullName);
        if (!fileInfo.Exists) // just for illustration -- the agent will check if file exists anyways
        {
            Console.WriteLine($" !! >{fileDescr.FullName}< file not found -> skipping");
            return false;
        }

        Console.Write($" ii Backing up file >{fileDescr.FullName}< : {Helpers.BytesToString(fileInfo.Length)} ");

        return true;
    }
}

class OnFileRestoreProcessor(TapeTOC toc) : OnFileEventProcessor(toc)
{
    // called for a chance to modify the fileDescr before restoring the file. If returns false, skip the file
    public override bool PreProcessFile(ref TapeFileDescriptor fileDescr)
    {
        Console.Write($" ii Restoring file >{fileDescr.FullName}< : {Helpers.BytesToString(fileDescr.Length)} ");
        return true;
    } // PreProcessFile()
} // class OnFileRestoreProcessor

class OnFileValidateProcessor(TapeTOC toc) : OnFileEventProcessor(toc)
{
    public override bool PreProcessFile(ref TapeFileDescriptor fileDescr)
    {
        Console.Write($" ii Validating file >{fileDescr.FullName}< : {Helpers.BytesToString(fileDescr.Length)} ");
        return true;
    }
}

class OnFileVerifyProcessor(TapeTOC toc) : OnFileEventProcessor(toc)
{
    public override bool PreProcessFile(ref TapeFileDescriptor fileDescr)
    {
        Console.Write($" ii Verifying file >{fileDescr.FullName}< : {Helpers.BytesToString(fileDescr.Length)} ");
        return true;
    }
}

static class StringHelpers
{
    public static string ToShortOrdinal(this int count)
    {
        string prefix = (count < 0)? "-" : string.Empty;
        count = Math.Abs(count);

        return (count % 100) switch
        {
            11 or 12 or 13 => $"{prefix}{count}th",
            _ => (count % 10) switch
            {
                1 => $"{prefix}{count}st",
                2 => $"{prefix}{count}nd",
                3 => $"{prefix}{count}rd",
                _ => $"{prefix}{count}th",
            },
        };
    }

    public static string SubstringAfter(this string str, char separator)
    {
        int separatorIndex = str.IndexOf(separator);
        return (separatorIndex >= 0)? str[(separatorIndex + 1)..] : string.Empty;
    }

    public static bool SplitAt(this string str, char separator, out string substr0, out string substr1)
    {
        int separatorIndex = str.IndexOf(separator);
        if (separatorIndex >= 0)
        {
            substr0 = str[..separatorIndex]; // up to but not including separator
            substr1 = str[(separatorIndex + 1)..];
            return true;
        }
        else
        {
            substr0 = substr1 = string.Empty;
            return false;
        }
    }

    public static int TryParseInt(this string str, int defaultValue = -1)
    {
        return int.TryParse(str, out int result) ? result : defaultValue;
    }

    public static bool StartsWith(this string str, params string[] prefixes)
    {
        return prefixes.Any(str.StartsWith);
    }

    public static int ParseNumberFromArg(this string str, int defaultValue = -1)
    {
        // Use regex to find the digits after the "=" sign
        Match match = Regex.Match(str, @"(\d+)");
        if (match.Success)
        {
            // Extract the matched digits and convert them to an integer
            return int.Parse(match.Groups[1].Value);
        }
        else
        {
            // No valid number found in the str string
            return defaultValue;
        }
    }

    public static string ParseNameFromArg(this string str, string defaultValue = "")
    {
        // Use regex to find the substring after the ":" sign
        Match match = Regex.Match(str, @":(.+)$");
        if (match.Success)
        {
            return match.Groups[1].Value.Trim();
        }
        else
        {
            // No valid number found in the str string
            return defaultValue;
        }
    }

    public static string GenerateRandomString(int maxLength)
    {
        const string chars = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789!\"#$%&'()*+,-./:;<=>?@[\\]^_`{|}~";
        var random = new Random();

        // Generate a random length (between 1 and maxLength)
        int length = random.Next(1, maxLength + 1);

        // Create the random string
        return new string(Enumerable.Repeat(chars, length)
            .Select(s => s[random.Next(s.Length)]).ToArray());
    }
} // class StringHelpers

#endregion // Helper types and classes

// end of program file
