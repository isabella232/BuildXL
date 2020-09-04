// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.ContractsLight;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using BuildXL.Native.IO;
using BuildXL.Processes;
using BuildXL.Utilities;

namespace Test.BuildXL.Executables.TestProcess
{
    /// <summary>
    /// Defines allowable process operation and their behaviors
    /// </summary>
    public sealed class Operation
    {
        private const int NumArgsExpected = 6;
        private const int ERROR_ALREADY_EXISTS = 183;
        private const string StdErrMoniker = "stderr";
        private const string WaitToFinishMoniker = "wait";
        private const string AllUppercasePath = "allUpper";
        private const string UseLongPathPrefix = "useLongPathPrefix";
        private const string SpawnExePrefix = "[SpawnExe]";
        private const string InvalidPathString = "{Invalid}";

        /// <summary>
        /// Returns an <code>IEnumerable</code> containing <paramref name="operation"/>
        /// <paramref name="count"/> number of times.
        /// </summary>
        public static IEnumerable<Operation> operator *(Operation operation, int count)
        {
            return Enumerable.Range(1, count).Select(_ => operation);
        }

        /*** PUBLIC DEFINITIONS ***/

        /// <summary>
        /// Input args delimiter between different fields of <see cref="Operation"/>
        /// </summary>
        public const char OperationArgsDelimiter = '?';

        /// <summary>
        /// Allowable filesystem operations test processes can execute
        /// </summary>
        public enum Type
        {
            /// <summary>
            /// Default no-op value
            /// Enum.TryParse will return default value on failure
            /// </summary>
            None,

            /// <summary>
            /// Type for creating a directory
            /// </summary>
            CreateDir,

            /// <summary>
            /// Type for deleting a file
            /// </summary>
            DeleteFile,

            /// <summary>
            /// Type for deleting a file with retry support
            /// </summary>
            DeleteFileWithRetries,

            /// <summary>
            /// Type for deleting a directory
            /// </summary>
            DeleteDir,

            /// <summary>
            /// Type for creating/writing a file
            /// </summary>
            WriteFile,

            /// <summary>
            /// Type for moving a file
            /// </summary>
            MoveFile,

            /// <summary>
            /// Write a file conditionally based on an input file
            /// </summary>
            WriteFileIfInputEqual,

            /// <summary>
            /// Type for creating/writing a file with retry support
            /// </summary>
            WriteFileWithRetries,

            /// <summary>
            /// Type for reading the content of a file and creating a file with the same content
            /// </summary>
            ReadAndWriteFile,

            /// <summary>
            /// Type for reading a file
            /// </summary>
            ReadFile,

            /// <summary>
            /// Type for reading a file (fails if a file does not exist)
            /// </summary>
            ReadRequiredFile,

            /// <summary>
            /// Type for reading a file specified as the content of another file
            /// </summary>
            ReadFileFromOtherFile,

            /// <summary>
            /// Type for copying a file
            /// </summary>
            CopyFile,

            /// <summary>
            /// Type for copying a symbolic link
            /// </summary>
            CopySymlink,

            /// <summary>
            /// Type for probing a file
            /// </summary>
            Probe,

            /// <summary>
            /// Type for enumerating a directory
            /// </summary>
            EnumerateDir,

            /// <summary>
            /// Type for creating a symlink to a file or directory
            /// </summary>
            CreateSymlink,

            /// <summary>
            /// Type for creating a junction to a directory
            /// </summary>
            CreateJunction,

            /// <summary>
            /// Type for creating a hardlink
            /// </summary>
            CreateHardlink,

            /// <summary>
            /// Type for failing/crashing the test process
            /// </summary>
            Fail,

            /// <summary>
            /// Type for echoing a message
            /// </summary>
            Echo,

            /// <summary>
            /// Block the process indefinitely
            /// </summary>
            Block,

            /// <summary>
            /// Reads all lines from standard input and prints them out to standard output
            /// </summary>
            ReadStdIn,

            /// <summary>
            /// Echoes the current directory of the processes
            /// </summary>
            EchoCurrentDirectory,

            /// <summary>
            /// Spawns a child process that supports a list of <see cref="Operation"></see>
            /// </summary>
            Spawn,

            /// <summary>
            /// Spawns a given exe as a child process
            /// </summary>
            SpawnExe,

            /// <summary>
            /// Like WriteFile with 'content' being Environment.NewLine
            /// </summary>
            AppendNewLine,

            /// <summary>
            /// Moves directory.
            /// </summary>
            MoveDir,

            /// <summary>
            /// Launches the debugger
            /// </summary>
            LaunchDebugger,

            /// <summary>
            /// Process that fails on first invocation and then succeeds on the second invocation
            /// </summary>
            SucceedOnRetry,

            /// <summary>
            /// Waits until a given file is found on disk
            /// </summary>
            WaitUntilFileExists,

            /// <summary>
            /// A read file access informed to detours without doing any real IO
            /// </summary>
            AugmentedReadFile,

            /// <summary>
            /// A write file access informed to detours without doing any real IO
            /// </summary>
            AugmentedWriteFile,

            /// <summary>
            /// Invokes some native code that crashes hard (by segfaulting or something)
            /// </summary>
            CrashHardNative,
        }

        /// <summary>
        /// Whether a symbolic link leads to a file or a directory,
        /// as defined by WINAPI
        /// </summary>
        public enum SymbolicLinkFlag : uint
        {
            /// <summary>
            /// Arbitrary default that is meaningless to WINAPI call
            /// </summary>
            None = uint.MaxValue,

            /// <summary>
            /// Flag for creating a symbolic link to a file
            /// </summary>
            FILE = 0,

            /// <summary>
            /// Flag for creating a symbolic link to a directory
            /// </summary>
            DIRECTORY = 1,
        }

        // pinvoke functions (TODO: use PAL)
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CreateSymbolicLink")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool ExternWinCreateSymbolicLink(string lpSymlinkFileName, string lpTargetFileName, SymbolicLinkFlag dwFlags);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CreateHardLink")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool ExternWinCreateHardLink(string lpFileName, string lpExistingFileName, IntPtr lpSecurityAttributes);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CreateDirectory")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool ExternWinCreateDirectory(string lpPathName, IntPtr lpSecurityAttributes);

        /*** STATE FOR BUILDXL TESTING ***/

        /// <summary>
        /// PathTable from surrounding Context to translate <see cref="FileOrDirectoryArtifact"/>s to strings
        /// </summary>
        public PathTable PathTable { get; set; }

        /// <summary>
        /// Whether or not the dependencies or outputs of this operation should be inferred, defaults to false.
        /// </summary>
        public bool DoNotInfer { get; set; }

        /*** STATE FOR FILESYSTEM OPERATIONS ***/

        /// <summary>
        /// Type of process (filesystem) operation
        /// </summary>
        public Type OpType { get; private set; }

#if TestProcess
        /// <summary>
        /// Should only be used in the context of TestProcess.exe
        /// </summary>
        private string PathAsString { get; set; }
#endif

        /// <summary>
        /// Path to file. NOTE: This will not be accessible within TestProcess.exe
        /// </summary>
        public FileOrDirectoryArtifact Path { get; private set; }

        /// <summary>
        /// Content to write to file
        /// </summary>
        public string Content { get; private set; }

#if TestProcess
        /// <summary>
        /// Should only be used in the context of TestProcess.exe
        /// </summary>
        private string LinkPathAsString { get; set; }
#endif

        /// <summary>
        /// Path of new symlink or hardlink to create. NOTE: This will not be accessible within TestProcess.exe
        /// </summary>
        public FileOrDirectoryArtifact LinkPath { get; private set; }

        /// <summary>
        /// Flag for correcting file or directory symlink
        /// </summary>
        public SymbolicLinkFlag SymLinkFlag { get; private set; }

        /// <summary>
        /// Arbitrary additional arguments that individual operations can interpret whichever way they want.
        ///
        /// Currently used to specify:
        ///   - search pattern for directory enumerations in <see cref="Type.EnumerateDir"/>
        ///   - whether to wait for the spawned process to finish in <see cref="Type.Spawn"/>
        ///   - whether to use stdout or stderr in <see cref="Type.Echo"/>
        ///   - whether to use an all uppercase path in <see cref="Type.WriteFile"/> (for testing casing awareness)
        /// </summary>
        public string AdditionalArgs { get; private set; }

        /// <summary>
        /// Number of retries when writing to a file
        /// </summary>
        public int RetriesOnWrite { get; }

        private Operation(
            Type type,
            FileOrDirectoryArtifact? path = null,
            string content = null,
            FileOrDirectoryArtifact? linkPath = null,
            SymbolicLinkFlag? symLinkFlag = null,
            bool? doNotInfer = null,
            string additionalArgs = null, 
            int retriesOnWrite = 5)
        {
            Contract.Requires(content == null || !content.Contains(Environment.NewLine));

            RetriesOnWrite = retriesOnWrite;
            OpType = type;
            Path = path ?? FileOrDirectoryArtifact.Invalid;
            Content = content;
            LinkPath = linkPath ?? FileOrDirectoryArtifact.Invalid;
            SymLinkFlag = symLinkFlag ?? SymbolicLinkFlag.None;
            DoNotInfer = doNotInfer ?? true;
            AdditionalArgs = additionalArgs;
        }

#if TestProcess
        private static Operation FromCommandLine(Type type, string path = null, string content = null, string linkPath = null, SymbolicLinkFlag? symLinkFlag = null, string additionalArgs = null)
        {
            Contract.Requires(content == null || !content.Contains(Environment.NewLine));

            var result = new Operation(type: type,
                content: content,
                symLinkFlag: symLinkFlag,
                doNotInfer: false,
                additionalArgs: additionalArgs);
            result.PathAsString = path;
            result.LinkPathAsString = linkPath;

            return result;
        }
#endif

        /// <summary>
        /// Executes the associated filesystem operation
        /// </summary>
        public void Run()
        {
            // Make sure the string version of the paths are set in case the operation is run outside of the context of
            // TestProcess.exe
            if (LinkPath.IsValid)
            {
                LinkPathAsString = LinkPath.Path.ToString(PathTable);
            }
            if (Path.IsValid)
            {
                PathAsString = Path.Path.ToString(PathTable);
            }

            try
            {
                switch (OpType)
                {
                    case Type.None:
                        return;
                    case Type.CreateDir:
                        DoCreateDir();
                        return;
                    case Type.DeleteFile:
                        DoDeleteFile();
                        return;
                    case Type.DeleteDir:
                        DoDeleteDir();
                        return;
                    case Type.WriteFile:
                        DoWriteFile();
                        return;
                    case Type.ReadAndWriteFile:
                        DoReadAndWriteFile();
                        return;
                    case Type.ReadFile:
                        DoReadFile();
                        return;
                    case Type.ReadRequiredFile:
                        DoReadRequiredFile();
                        return;
                    case Type.ReadFileFromOtherFile:
                        DoReadFileFromOtherFile();
                        return;
                    case Type.CopyFile:
                        DoCopyFile();
                        return;
                    case Type.CopySymlink:
                        DoCopySymlink();
                        return;
                    case Type.MoveDir:
                        DoMoveDir();
                        return;
                    case Type.Probe:
                        DoProbe();
                        return;
                    case Type.EnumerateDir:
                        DoEnumerateDir();
                        return;
                    case Type.CreateSymlink:
                        DoCreateSymlink();
                        return;
                    case Type.CreateJunction:
                        DoCreateJunction();
                        return;
                    case Type.CreateHardlink:
                        DoCreateHardlink();
                        return;
                    case Type.WriteFileWithRetries:
                        DoOperationWithRetries(DoWriteFile);
                        return;
                    case Type.DeleteFileWithRetries:
                        DoOperationWithRetries(DoDeleteFile);
                        return;
                    case Type.Echo:
                        DoEcho();
                        return;
                    case Type.Block:
                        DoBlock();
                        return;
                    case Type.ReadStdIn:
                        DoReadStdIn();
                        return;
                    case Type.EchoCurrentDirectory:
                        DoEchoCurrentDirectory();
                        return;
                    case Type.Spawn:
                        DoSpawn();
                        return;
                    case Type.SpawnExe:
                        DoSpawnExe();
                        return;
                    case Type.AppendNewLine:
                        DoWriteFile(Environment.NewLine);
                        return;
                    case Type.Fail:
                        DoFail();
                        return;
                    case Type.CrashHardNative:
                        DoCrashHardNative();
                        return;
                    case Type.WriteFileIfInputEqual:
                        DoWriteFileIfInputEqual();
                        return;
                    case Type.LaunchDebugger:
                        Debugger.Launch();
                        return;
                    case Type.MoveFile:
                        DoMoveFile();
                        return;
                    case Type.SucceedOnRetry:
                        DoSucceedOnRetry();
                        return;
                    case Type.WaitUntilFileExists:
                        DoWaitUntilFileExists();
                        return;
                    case Type.AugmentedReadFile:
                        DoAugmentedReadFile();
                        return;
                    case Type.AugmentedWriteFile:
                        DoAugmentedWriteFile();
                        return;
                }
            }
            catch (Exception e)
            {
                // Print error and exit with failure if any operation fails
                Console.Error.WriteLine("The test process failed to execute an operation: " + OpType.ToString());
                Console.Error.WriteLine(e);
                Environment.Exit(1);
            }
        }

        private const string EncodedStringListDelimeter = "%";

        private static string EncodeList(params string[] args)
        {
            return string.Join(EncodedStringListDelimeter, args.Select(arg => arg == null ? "~" : Convert.ToBase64String(Encoding.UTF8.GetBytes(arg))));
        }

        private static string[] DecodeList(string encoded)
        {
            return encoded.Split(EncodedStringListDelimeter[0]).Select(arg => arg == "~" ? null : Encoding.UTF8.GetString(Convert.FromBase64String(arg))).ToArray();
        }

        /*** FACTORY FUNCTIONS ***/

        /// <summary>
        /// When a message passed to the <see cref="Type.Echo"/> operation matches this regular expression,
        /// the echo operation extracts the 'VarName' block from the match, uses that value as an environment
        /// variable name, and prints out the value of that environment variable.
        /// </summary>
        public static readonly Regex EnvVarRegex = new Regex("^ENV{(?<VarName>.*)}$");

        /// <summary>
        /// Creates a create directory operation (uses WinAPI)
        /// </summary>
        public static Operation CreateDir(FileOrDirectoryArtifact path, bool doNotInfer = false, string additionalArgs = null)
        {
            return new Operation(Type.CreateDir, path, doNotInfer: doNotInfer, additionalArgs: additionalArgs);
        }

        /// <summary>
        /// Creates a delete directory operation (uses WinAPI)
        /// </summary>
        public static Operation DeleteDir(FileOrDirectoryArtifact path, bool doNotInfer = false, string additionalArgs = null)
        {
            return new Operation(Type.DeleteDir, path, doNotInfer: doNotInfer, additionalArgs: additionalArgs);
        }

        /// <summary>
        /// Creates a write file operation that appends. The file is created if it does not exist.
        /// Writes random content to file at path if no content is specified.
        /// </summary>
        public static Operation WriteFile(FileArtifact path, string content = null, bool doNotInfer = false, bool changePathToAllUpperCase = false, bool useLongPathPrefix = false)
        {
            Contract.Assert(!changePathToAllUpperCase || !useLongPathPrefix, "Cannot specify changePathToAllUpperCase and useLongPathPrefix simultaneously");

            string additionalArgs = changePathToAllUpperCase ? Operation.AllUppercasePath : (useLongPathPrefix ? Operation.UseLongPathPrefix : null);

            return content == Environment.NewLine
                ? new Operation(Type.AppendNewLine, path, doNotInfer: doNotInfer, additionalArgs: additionalArgs)
                : new Operation(Type.WriteFile, path, content, doNotInfer: doNotInfer, additionalArgs: additionalArgs);
        }

        /// <summary>
        /// Creates a read file operation followed by a write file operation. The content of the file that was read is used to
        /// write the new file.
        /// </summary>
        public static Operation ReadAndWriteFile(FileArtifact pathToRead, FileArtifact pathToWrite, bool doNotInfer = false)
        {
            return new Operation(Type.ReadAndWriteFile, pathToRead, content: null, pathToWrite, doNotInfer: doNotInfer);
        }

        /// <summary>
        /// Moves source to destination
        /// </summary>
        public static Operation MoveFile(FileArtifact source, FileArtifact destination, bool doNotInfer = false)
        {
            return new Operation(Type.MoveFile, source, content: null, linkPath: destination, doNotInfer: doNotInfer);
        }

        /// <summary>
        /// Creates a write file operation that appends. The file is created if it does not exist.
        /// Writes random content to file at path if no content is specified.
        /// </summary>
        public static Operation WriteFileIfInputEqual(FileArtifact path, string input, string value, string content = null)
        {
            return new Operation(Type.WriteFileIfInputEqual, path, EncodeList(input, value, content));
        }

        /// <summary>
        /// Creates a delete file operation
        /// </summary>
        public static Operation DeleteFile(FileArtifact path, bool doNotInfer = false)
        {
            return new Operation(Type.DeleteFile, path, content: null, doNotInfer: doNotInfer);
        }

        /// <summary>
        /// Creates a delete file operation with with the option to retry deleting to the file a specified number of times
        /// </summary>
        public static Operation DeleteFileWithRetries(FileArtifact path, bool doNotInfer = false, int retries = 5)
        {
            return new Operation(Type.DeleteFileWithRetries, path, content: null, doNotInfer: doNotInfer, retriesOnWrite: retries);
        }

        /// <summary>
        /// <see cref="WriteFile"/>, with the option to retry writing to the file a specified number of times
        /// </summary>
        public static Operation WriteFileWithRetries(FileArtifact path, string content = null, bool doNotInfer = false, int retries = 5)
        {
            Contract.Requires(retries >= 0);
            return new Operation(Type.WriteFileWithRetries, path, content, doNotInfer: doNotInfer, retriesOnWrite: retries);
        }

        /// <summary>
        /// Creates a read file operation
        /// </summary>
        public static Operation ReadFile(FileArtifact path, bool doNotInfer = false)
        {
            return new Operation(Type.ReadFile, path, doNotInfer: doNotInfer);
        }

        /// <summary>
        /// Creates a chained read file operation (reads file specified in the given file)
        /// </summary>
        public static Operation ReadFileFromOtherFile(FileArtifact path, bool doNotInfer = false)
        {
            return new Operation(Type.ReadFileFromOtherFile, path, doNotInfer: doNotInfer);
        }

        /// <summary>
        /// Creates a read file operation (fails if a file does not exist)
        /// </summary>
        public static Operation ReadRequiredFile(FileArtifact path, bool doNotInfer = false)
        {
            return new Operation(Type.ReadRequiredFile, path, doNotInfer: doNotInfer);
        }

        /// <summary>
        /// Creates a copy file operation
        /// </summary>
        public static Operation CopyFile(FileArtifact srcPath, FileArtifact destPath, bool doNotInfer = false)
        {
            return new Operation(Type.CopyFile, path: srcPath, linkPath: destPath, doNotInfer: doNotInfer);
        }

        /// <summary>
        /// Creates a copy symlink operation
        /// </summary>
        public static Operation CopySymlink(FileArtifact srcPath, FileArtifact destPath, SymbolicLinkFlag symLinkFlag, bool doNotInfer = false)
        {
            return new Operation(Type.CopySymlink, path: srcPath, linkPath: destPath, symLinkFlag: symLinkFlag, doNotInfer: doNotInfer);
        }

        /// <summary>
        /// Creates a move directory operation
        /// </summary>
        public static Operation MoveDir(DirectoryArtifact srcPath, DirectoryArtifact destPath)
        {
            return new Operation(Type.MoveDir, path: srcPath, linkPath: destPath, doNotInfer: true);
        }

        /// <summary>
        /// Creates a probe operation
        /// </summary>
        public static Operation Probe(FileOrDirectoryArtifact path, bool doNotInfer = false)
        {
            return new Operation(Type.Probe, path, doNotInfer: doNotInfer);
        }

        /// <summary>
        /// Creates a enumerate directory operation
        /// The path is a FileOrDirectoryArtifact, because we can enumerate directories through directory symlinks - which are FileArtifacts.
        /// </summary>
        public static Operation EnumerateDir(FileOrDirectoryArtifact path, bool doNotInfer = false, string enumeratePattern = null)
        {
            return new Operation(Type.EnumerateDir, path, doNotInfer: doNotInfer, additionalArgs: enumeratePattern);
        }

        /// <summary>
        /// Creates a create symbolic link operation
        /// Requires process to run with elevated permissions for success
        /// </summary>
        public static Operation CreateSymlink(FileOrDirectoryArtifact linkPath, FileOrDirectoryArtifact targetPath, SymbolicLinkFlag symLinkFlag, bool doNotInfer = false)
        {
            return new Operation(Type.CreateSymlink, targetPath, linkPath: linkPath, symLinkFlag: symLinkFlag, doNotInfer: doNotInfer);
        }

        /// <summary>
        /// Creates a create symbolic link operation
        /// </summary>
        /// <remarks>
        /// This method is for testing symlinks whose target either
        /// (a) cannot be represented using FileArtifact/AbsolutePath (e.g., path contains an illegal char), or
        /// (b) should not be represented using these structs (e.g., target's path is a relative path)
        /// </remarks>
        public static Operation CreateSymlink(FileOrDirectoryArtifact linkPath, string target, SymbolicLinkFlag symLinkFlag, bool doNotInfer = false)
        {
            return new Operation(Type.CreateSymlink, linkPath: linkPath, additionalArgs: target, symLinkFlag: symLinkFlag, doNotInfer: doNotInfer);
        }

        /// <summary>
        /// Creates a junction operation (windows only), fails on other operating systems
        /// Requires process to run with elevated permissions for success
        /// </summary>
        public static Operation CreateJunction(FileOrDirectoryArtifact junctionPath, FileOrDirectoryArtifact targetPath, bool doNotInfer = false)
        {
            return new Operation(Type.CreateJunction, targetPath, linkPath: junctionPath, doNotInfer: doNotInfer);
        }

        /// <summary>
        /// Creates a create hard link operation.
        /// </summary>
        /// <param name="linkPath">Existing file</param>
        /// <param name="path">New file to create</param>
        /// <param name="doNotInfer">A flag for the PipTestBase class specifying whether to infer input/output dependencies</param>
        public static Operation CreateHardlink(FileArtifact linkPath, FileArtifact path, bool doNotInfer = false)
        {
            return new Operation(Type.CreateHardlink, path, linkPath: linkPath, doNotInfer: doNotInfer);
        }

        /// <summary>
        /// Creates an echo operation
        /// </summary>
        /// <param name="message">
        /// Message to echo.  If the value matches the <see cref="EnvVarRegex"/> regular expression,
        /// the value of the environment variable with that name is printed out instead.
        /// </param>
        /// <param name="useStdErr">If true: echo to standard error; else: echo to standard output.</param>
        public static Operation Echo(string message, bool useStdErr = false)
        {
            return new Operation(Type.Echo, content: message, additionalArgs: useStdErr ? StdErrMoniker : null);
        }

        /// <summary>
        /// Given an environment variable name (<paramref name="envVarName"/>) returns an
        /// <see cref="Type.Echo"/> operation that print out the value of that environment variable.
        /// </summary>
        public static Operation ReadEnvVar(string envVarName)
            => Echo(RenderGetEnvVarValueSyntax(envVarName));

        /// <summary>
        /// Creates a block operation that blocks the process indefinitely (<see cref="Type.Block"/>)
        /// </summary>
        public static Operation Block()
        {
            return new Operation(Type.Block);
        }

        /// <summary>
        /// Creates a read operation that reads all lines from standard input and prints them out to standard output.
        /// </summary>
        public static Operation ReadStdIn()
        {
            return new Operation(Type.ReadStdIn);
        }

        /// <summary>
        /// Creates an operation that echoes the current directory.
        /// </summary>
        public static Operation EchoCurrentDirectory()
        {
            return new Operation(Type.EchoCurrentDirectory);
        }

        /// <summary>
        /// Creates an operation that spawns a child process executing given <paramref name="childOperations"/>.
        /// </summary>
        /// <param name="pathTable">Needed for rendering child process operations</param>
        /// <param name="waitToFinish">Whether to wait for the child process to finish before continuing</param>
        /// <param name="pidFile">If valid, file to which to write spawned process id</param>
        /// <param name="doNotInfer">Whether or not to infer dependencies</param>
        /// <param name="childOperations">Definition of the child process</param>
        public static Operation SpawnAndWritePidFile(PathTable pathTable, bool waitToFinish, FileOrDirectoryArtifact? pidFile, bool doNotInfer = false, params Operation[] childOperations)
        {
            var args = childOperations.Select(o => (o.ToCommandLine(pathTable, escapeResult: true))).ToArray();
            return new Operation(Type.Spawn, path: pidFile, content: EncodeList(args), additionalArgs: waitToFinish ? WaitToFinishMoniker : null, doNotInfer: doNotInfer);
        }

        /// <summary>
        /// Like <see cref="SpawnAndWritePidFile"/> except it doesn't write out spawned process pid to file.
        /// </summary>
        public static Operation Spawn(PathTable pathTable, bool waitToFinish, params Operation[] childOperations)
        {
            return SpawnAndWritePidFile(pathTable, waitToFinish, pidFile: null, doNotInfer: false, childOperations);
        }

        /// <summary>
        /// Creates an operation that spawns a child process using a given executable name.
        /// </summary>
        /// <remarks>
        /// The child is launched in a fire-and-forget manner, the parent process doesn't wait for it to finish
        /// </remarks>
        /// <param name="pathTable">Needed for rendering child process operations</param>
        /// <param name="exeLocation">Path to the executable to launch</param>
        /// <param name="arguments">Arguments to pass to the spawned process</param>
        public static Operation SpawnExe(PathTable pathTable, FileOrDirectoryArtifact exeLocation, string arguments = null)
        {
            return new Operation(Type.SpawnExe, path: exeLocation, additionalArgs: arguments);
        }

        /// <summary>
        /// Creates an operation that reports an augmented read file access to detours without actually performing any IO
        /// </summary>
        /// <remarks>
        /// Processes running this operation have to run in a pip that has a non-empty set of processes allowed to breakaway
        /// </remarks>
        public static Operation AugmentedRead(FileOrDirectoryArtifact path, bool doNotInfer = false)
        {
            return new Operation(Type.AugmentedReadFile, path, doNotInfer: doNotInfer);
        }

        /// <summary>
        /// Similar to <see cref="AugmentedRead(FileOrDirectoryArtifact, bool)"/>, but allows to pass paths as plain strings to validate
        /// non-canonicalized path behavior.
        /// </summary>
        public static Operation AugmentedRead(string path, bool doNotInfer = false)
        {
            Contract.Requires(!string.IsNullOrEmpty(path));
            return new Operation(Type.AugmentedReadFile, FileOrDirectoryArtifact.Invalid, doNotInfer: doNotInfer, additionalArgs: path);
        }

        /// <summary>
        /// Creates an operation that reports an augmented write file access to detours without actually performing any IO
        /// </summary>
        /// <remarks>
        /// Processes running this operation have to run in a pip that has a non-empty set of processes allowed to breakaway
        /// </remarks>
        public static Operation AugmentedWrite(FileOrDirectoryArtifact path, bool doNotInfer = false)
        {
            return new Operation(Type.AugmentedWriteFile, path, doNotInfer: doNotInfer);
        }

        /// <summary>
        /// Similar to <see cref="AugmentedWrite(FileOrDirectoryArtifact, bool)"/>, but allows to pass paths as plain strings to validate
        /// non-canonicalized path behavior.
        /// </summary>
        public static Operation AugmentedWrite(string path, bool doNotInfer = false)
        {
            return new Operation(Type.AugmentedWriteFile, FileOrDirectoryArtifact.Invalid, doNotInfer: doNotInfer, additionalArgs: path);
        }

        /// <summary>
        /// Fails the process
        /// </summary>
        public static Operation Fail(int exitCode = -1)
        {
            return new Operation(Type.Fail, content: exitCode.ToString());
        }

        /// <summary>
        /// Invokes some native code that crashes hard (by segfaulting or something)
        /// </summary>
        public static Operation CrashHardNative()
        {
            return new Operation(Type.CrashHardNative);
        }

        /// <summary>
        /// Process that fails on first invocation and succeeds on second invocation.
        /// </summary>
        /// <param name="untrackedStateFilePath">File used to track state. This path should be untracked when scheduling the pip</param>
        /// <param name="firstFailExitCode">Exit code for first failed invocation</param>
        /// <returns></returns>
        public static Operation SucceedOnRetry(FileArtifact untrackedStateFilePath, int firstFailExitCode = -1)
        {
            return new Operation(Type.SucceedOnRetry, path: untrackedStateFilePath, content: firstFailExitCode.ToString());
        }

        /// <summary>
        /// Launches the debugger
        /// </summary>
        public static Operation LaunchDebugger()
        {
            return new Operation(Type.LaunchDebugger);
        }

        /// <summary>
        /// Waits until <paramref name="path"/> exists on disk.
        /// </summary>
        public static Operation WaitUntilFileExists(FileArtifact path, bool doNotInfer = false)
        {
            return new Operation(Type.WaitUntilFileExists, path, doNotInfer: doNotInfer);
        }

        /*** FILESYSTEM OPERATION FUNCTIONS ***/

        private void DoCreateDir()
        {
            bool failIfExists = false;

            if (!string.IsNullOrEmpty(AdditionalArgs))
            {
                string[] args = AdditionalArgs.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                foreach (var arg in args)
                {
                    if (string.Equals(arg, "--failIfExists", StringComparison.OrdinalIgnoreCase))
                    {
                        failIfExists = true;
                    }
                }
            }

            if (FileUtilities.DirectoryExistsNoFollow(PathAsString) || FileUtilities.FileExistsNoFollow(PathAsString))
            {
                if (failIfExists)
                {
                    throw new InvalidOperationException($"Directory creation failed because '{PathAsString}' exists");
                }
            }

            if (OperatingSystemHelper.IsUnixOS)
            {
                Directory.CreateDirectory(PathAsString);
            }
            else
            {
                // .NET's Directory.CreateDirectory first checks for directory existence, so when a directory
                // exists it does nothing; to test a specific Detours access policy, we want to issue a "create directory"
                // operation regardless of whether the directory exists, hence p-invoking Windows native CreateDirectory.
                if (!ExternWinCreateDirectory(PathAsString, IntPtr.Zero))
                {
                    int errorCode = Marshal.GetLastWin32Error();

                    // If we got ERROR_ALREADY_EXISTS (183), keep it quiet --- yes we did not 'write' but the directory
                    // is on the disk after this method returns --- and do not fail the operation.
                    if (errorCode == ERROR_ALREADY_EXISTS)
                    {
                        return;
                    }

                    throw new InvalidOperationException($"Directory creation (native) for path '{PathAsString}' failed with error {errorCode}.");
                }
            }
        }

        private void DoDeleteFile()
        {
            FileUtilities.DeleteFile(PathAsString);
        }

        private void DoDeleteDir()
        {
            Directory.Delete(PathAsString);
        }

        // Writes random Content if Content not specified
        private void DoWriteFile()
        {
            DoWriteFile(Content ?? Guid.NewGuid().ToString());
        }

        private void DoReadFileFromOtherFile()
        {
            // Read the file path from the first file
            var path = DoReadRequiredFile();

            // Now read the file
            DoReadFile(path);
        }

        private void DoReadAndWriteFile()
        {
            string content = DoReadFile();
            try
            {
                File.WriteAllText(LinkPathAsString, content == string.Empty ? Guid.NewGuid().ToString() : content);
            }
            catch (UnauthorizedAccessException)
            {
                // Ignore tests for denied file access policies
            }
        }
        
        private void DoWriteFileIfInputEqual()
        {
            string[] argument = DecodeList(Content);
            string input = argument[0];
            string value = argument[1];
            string content = argument[2];

            if (File.ReadAllText(input) == value)
            {
                DoWriteFile(content ?? Guid.NewGuid().ToString());
            }
        }

        private void DoWriteFile(string content)
        {
            DoWriteFile(PathAsString, content);
        }

        private void DoWriteFile(string file, string content)
        {
            try
            {
                if (AdditionalArgs == AllUppercasePath)
                {
                    file = file.ToUpperInvariant();
                }

                if (AdditionalArgs == UseLongPathPrefix)
                {
                    file = @"\\?\" + file;
                }

                // Ensure directory exists.
                string directory = System.IO.Path.GetDirectoryName(file);
                Directory.CreateDirectory(directory);

                File.AppendAllText(file, content);
            }
            catch (UnauthorizedAccessException)
            {
                // Ignore tests for denied file access policies
            }
        }

        private void DoOperationWithRetries(Action operation)
        {
            Content = Content ?? Guid.NewGuid().ToString();

            var retries = RetriesOnWrite;
            var sucess = false;
            while (!sucess || retries > 0)
            {
                try
                {
                    operation();
                    sucess = true;
                }
                catch (IOException)
                {
                    // This exception is thrown if two processes try to write to the same file
                    // So we put the thread to sleep for a small amount of (random) time
                    // so competing threads have their chance
                    Thread.Sleep(TimeSpan.FromMilliseconds(new Random().Next(1, 50)));
                }
                retries--;
            }
        }

        private string DoReadFile(string path = null)
        {
            try
            {
                path = path ?? PathAsString;
                var content = File.ReadAllText(path);
                return content;
            }
            catch (FileNotFoundException)
            {
                // Ignore reading absent files
            }
            catch (DirectoryNotFoundException)
            {
                // Ignore reading absent paths
            }
            catch (UnauthorizedAccessException)
            {
                // Ignore tests for denied file access policies
            }

            return string.Empty;
        }

        private string DoAugmentedReadFile(string path = null)
        {
            path = path ?? PathAsString;

            // A plain string could have been passed to exercise non-canonicalized paths
            if (path == InvalidPathString)
            {
                path = AdditionalArgs;
            }

            var success = AugmentedManifestReporter.Instance.TryReportFileReads(new[] { path });

            if (!success)
            {
                throw new InvalidOperationException($"Cannot report augmented read for {path}");
            }

            return string.Empty;
        }

        private string DoAugmentedWriteFile(string path = null)
        {
            path = path ?? PathAsString;

            // A plain string could have been passed to exercise non-canonicalized paths
            if (path == InvalidPathString)
            {
                path = AdditionalArgs;
            }

            var success = AugmentedManifestReporter.Instance.TryReportFileCreations(new[] { path });

            if (!success)
            {
                throw new InvalidOperationException($"Cannot report augmented write for {path}");
            }

            return string.Empty;
        }

        private string DoReadRequiredFile()
        {
            return File.ReadAllText(PathAsString);
        }

        private void DoCopyFile()
        {
            File.Copy(PathAsString, LinkPathAsString, overwrite: true);
        }

        private void DoCopySymlink()
        {
            var possibleTarget = FileUtilities.TryGetReparsePointTarget(null, PathAsString);
            if (!possibleTarget.Succeeded)
            {
                possibleTarget.Failure.Throw();
            }

            var reparsepointTarget = possibleTarget.Result;
            FileUtilities.DeleteFile(LinkPathAsString);

            var maybeSymlink = FileUtilities.TryCreateSymbolicLink(LinkPathAsString, reparsepointTarget, isTargetFile: SymLinkFlag == SymbolicLinkFlag.FILE);
            if (!maybeSymlink.Succeeded)
            {
                throw maybeSymlink.Failure.CreateException();
            }
        }

        private void DoMoveDir()
        {
            if (Directory.Exists(LinkPathAsString))
            {
                Directory.Delete(LinkPathAsString, true);
            }

            Directory.Move(PathAsString, LinkPathAsString);
        }

        private void DoMoveFile()
        {
            File.Move(PathAsString, LinkPathAsString);
        }

        private void DoProbe()
        {
            File.Exists(PathAsString);
        }

        private void DoEnumerateDir()
        {
            try
            {
                string enumeratePattern = AdditionalArgs;
                if (enumeratePattern == null)
                {
                    Directory.EnumerateFileSystemEntries(PathAsString);
                }
                else
                {
                    Directory.EnumerateFileSystemEntries(PathAsString, enumeratePattern);
                }
            }
            catch (DirectoryNotFoundException)
            {
                // Ignore enumerating absent directories
            }
        }

        private void DoCreateSymlink()
        {
            string target = AdditionalArgs ?? PathAsString;

            // delete whatever might exist at the link location
            var linkPath = LinkPathAsString;
            FileUtilities.DeleteFile(linkPath);
            var maybeSymlink = FileUtilities.TryCreateSymbolicLink(linkPath, target, SymLinkFlag == SymbolicLinkFlag.FILE);
            if (!maybeSymlink.Succeeded)
            {
                throw maybeSymlink.Failure.CreateException();
            }
        }
        
        private void DoCreateJunction()
        {
            Contract.Assert(!OperatingSystemHelper.IsUnixOS);

            var maybeSymlink = FileUtilities.TryCreateReparsePointIfNotExistsOrTargetsDoNotMatch(LinkPathAsString, PathAsString, ReparsePointType.Junction, out var created);
            if (!maybeSymlink.Succeeded)
            {
                throw maybeSymlink.Failure.CreateException();
            }
        }

        private void DoCreateHardlink()
        {
            var status = FileUtilities.TryCreateHardLink(PathAsString, LinkPathAsString);
            if (status != CreateHardLinkStatus.Success)
            {
                int error = Marshal.GetLastWin32Error();
                throw new InvalidOperationException("Failed to create hard link at '" + PathAsString + "' pointing to '" + LinkPathAsString + "'. Error: " + error + ".");
            }
        }

        private static string ExtractVarNameFromEnvVarRegexMatch(Match m)
            => m.Groups["VarName"].Value;

        /// <summary>
        /// Given an environment variable name (<paramref name="envVarName"/>) returns a string which
        /// when passed to the <see cref="Type.Echo"/> operation makes the operation print out the value
        /// of that environment variable.
        /// </summary>
        private static string RenderGetEnvVarValueSyntax(string envVarName)
        {
            var result = $"ENV{{{envVarName}}}";
#if DEBUG
            // make sure that whatever we are about to return matches the regex used for parsing it
            var m = EnvVarRegex.Match(result);
            Contract.Assert(m.Success);
            Contract.Assert(envVarName == ExtractVarNameFromEnvVarRegexMatch(m));
#endif
            return result;
        }

        private void DoEcho()
        {
            var dest = (AdditionalArgs == StdErrMoniker)
                ? Console.Error
                : Console.Out;

            var match = EnvVarRegex.Match(Content);
            var message = match.Success
                ? Environment.GetEnvironmentVariable(ExtractVarNameFromEnvVarRegexMatch(match))
                : Content;

            dest.WriteLine(message);
        }

        private void DoBlock()
        {
            Console.WriteLine("Blocked forever");
            new ManualResetEventSlim().Wait();
        }

        private void DoReadStdIn()
        {
            string line;
            while ((line = Console.In.ReadLine()) != null)
            {
                Console.WriteLine(line);
            }
        }

        private void DoEchoCurrentDirectory()
        {
            Console.WriteLine(Environment.CurrentDirectory);
        }

        private void DoSpawn()
        {
            var cmdLine = string.Join(" ", DecodeList(Content));
            var waitToFinish = AdditionalArgs == WaitToFinishMoniker;

            var feedStdoutDone = new ManualResetEventSlim();
            var feedStderrDone = new ManualResetEventSlim();

            var process = new Process();
            process.EnableRaisingEvents = true;
            process.OutputDataReceived += (sender, e) => FeedToOut(Console.Out, e.Data, feedStdoutDone);
            process.ErrorDataReceived += (sender, e) => FeedToOut(Console.Error, e.Data, feedStderrDone);
            process.StartInfo = new ProcessStartInfo
            {
                FileName = AssemblyHelper.GetAssemblyLocation(System.Reflection.Assembly.GetExecutingAssembly(), computeAssemblyLocation: true),
                Arguments = cmdLine,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            if (PathAsString != null && PathAsString != InvalidPathString)
            {
                // write PID to file
                File.WriteAllText(PathAsString, contents: process.Id.ToString());
            }

            if (waitToFinish)
            {
                process.WaitForExit();
                feedStdoutDone.Wait();
                feedStderrDone.Wait();
            }

            void FeedToOut(TextWriter tw, string str, ManualResetEventSlim mre)
            {
                if (str == null)
                {
                    mre.Set();
                }
                else
                {
                    tw.WriteLine(str);
                }
            }
        }

        private void DoSpawnExe()
        {
            var process = new Process();

            process.StartInfo = new ProcessStartInfo
            {
                FileName = PathAsString,
                Arguments = AdditionalArgs,
                // Important to redirect stdout/stderr because we don't want stdout/stderr
                // of this child process to go to this process's stdout/stderr; without
                // this the child process cannot fully break away
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            process.Start();

            // Log the the process that was launched with its id so it can be retrieved by bxl tests later
            // This is used when the child process is launched to breakaway from the job object, so we actually
            // don't get its information back as part of a reported process
            string processName;
            try
            {
                // process.ProcessName throws if process has already exited
                processName = process.ProcessName;
            }
#pragma warning disable ERP022 // Unobserved exception in generic exception handler
            catch
            {
                processName = PathAsString;
            }
#pragma warning restore ERP022 // Unobserved exception in generic exception handler

            LogSpawnExeChildProcess(processName, process.Id);
        }

        private void DoFail()
        {
            int exitCode = int.TryParse(Content, out var result) ? result : -1;
            Environment.Exit(exitCode);
        }

        private void DoCrashHardNative()
        {
            global::BuildXL.Interop.Dispatch.ForceQuit();
        }

        private void DoSucceedOnRetry()
        {
            // Use this state file to differentiate between the first run and the second run. The file will contain the exit code for the second run
            if (File.Exists(PathAsString))
            {
                int thisRunExitCode = int.Parse(File.ReadAllText(PathAsString));
                Environment.Exit(thisRunExitCode);
            }
            else
            {
                File.WriteAllText(PathAsString, "0");
                Environment.Exit(int.Parse(Content));
            }
        }

        private void DoWaitUntilFileExists()
        {
            while (true)
            {
                var maybeExistence = FileUtilities.TryProbePathExistence(PathAsString, followSymlink: false);
                if (!maybeExistence.Succeeded || maybeExistence.Result == PathExistence.ExistsAsFile)
                {
                    return;
                }

                Thread.Sleep(TimeSpan.FromMilliseconds(500));
            }
        }

        /*** COMMAND LINE PARSING FUNCTION ***/

        /// <summary>
        /// Converts command line arg input to process ops
        /// Input format: "OpType?Path?Content?LinkPath?SymLinkFlag"
        /// </summary>
        /// <remarks>
        /// This and the execution of Operations within the context of TestProcess.exe should not utilize the PathTable
        /// since this adds an appreciable amount of JIT overhead to our test execution
        /// </remarks>
        public static Operation CreateFromCommandLine(string commandLineArg)
        {
            var opArgs = commandLineArg.Split(OperationArgsDelimiter);
            if (opArgs.Length != NumArgsExpected)
            {
                throw new ArgumentException(
                    string.Format(System.Globalization.CultureInfo.CurrentCulture,
                    "An input argument (or delimiter) is missing. Expected {0} arguments, but received {1} arguments. Valid format is: OpType?Path?Content?LinkPath?SymLinkFlag. Raw command line is {2}", NumArgsExpected, opArgs.Length, commandLineArg));
            }

            Type opType;
            string pathAsString = InvalidPathString;
            SymbolicLinkFlag symLinkFlag;
            string linkPathAsString = InvalidPathString;

            if (!Enum.TryParse<Type>(opArgs[0], out opType))
            {
                throw new InvalidCastException("A malformed or invalid input argument was passed for the Operation type enum.");
            }

            if (opArgs[1].Length != 0)
            {
                pathAsString = opArgs[1];
            }

            string content = opArgs[2].Length == 0 ? null : opArgs[2];

            if (opArgs[3].Length != 0)
            {
                linkPathAsString = opArgs[3];
            }

            if (!Enum.TryParse<SymbolicLinkFlag>(opArgs[4], out symLinkFlag))
            {
                throw new InvalidCastException("A malformed or invalid input argument was passed for the Operation symbolic link flag enum.");
            }

            string additionalArgs = opArgs[5].Length == 0 ? null : opArgs[5];

            return Operation.FromCommandLine(
                type: opType, 
                path: pathAsString, 
                content: content, 
                linkPath: linkPathAsString, 
                symLinkFlag: symLinkFlag, 
                additionalArgs: additionalArgs);
        }

        /// <summary>
        /// Converts <see cref="Operation"/> to a well-formed string that can be used as command line input
        /// </summary>
        public string ToCommandLine(PathTable pathTable, bool escapeResult = false)
        {
            PathTable = pathTable;

            StringBuilder sb = new StringBuilder();

            sb.Append(((int)OpType).ToString(System.Globalization.CultureInfo.CurrentCulture));

            var orderOfArgs = new string[]
            {
                Path.IsValid ? FileOrDirectoryToString(Path) : null,
                Content,
                LinkPath.IsValid ? FileOrDirectoryToString(LinkPath) : null,
                SymLinkFlag.ToString(),
                AdditionalArgs
                // The process as a test executable does not use the DoNotInfer field
            };

            foreach (var arg in orderOfArgs)
            {
                sb.Append(OperationArgsDelimiter);
                if (arg != null)
                {
                    sb.Append(arg.ToString());
                }
            }

            return escapeResult
                ? CommandLineEscaping.EscapeAsCommandLineWord(sb.ToString())
                : sb.ToString();
        }

        /// <summary>
        /// Retrieves all process (process name and pid) spawn by calling SpawnExe from the parent process standard output
        /// </summary>
        /// <remarks>
        /// Useful when the spawn process is configured to breakaway, and therefore we have no detours to track them
        /// </remarks>
        public static IEnumerable<(string processName, int pid)> RetrieveChildProcessesCreatedBySpawnExe(string processStandardOutput)
        {
            var result = new List<(string processName, int pid)>();
            var prefixLength = SpawnExePrefix.Length;
            foreach (string line in processStandardOutput.Split(new[] { Environment.NewLine }, StringSplitOptions.RemoveEmptyEntries))
            {
                if (line.StartsWith(SpawnExePrefix))
                {
                    int separatorIndex = line.IndexOf(':');
                    result.Add((line.Substring(prefixLength, separatorIndex - prefixLength), int.Parse(line.Substring(separatorIndex + 1))));
                }
            }

            return result;
        }

        /// <summary>
        /// Keep in sync with <see cref="RetrieveChildProcessesCreatedBySpawnExe"/>
        /// </summary>
        private void LogSpawnExeChildProcess(string processName, long processId)
        {
            Console.WriteLine($"{SpawnExePrefix}{processName}:{processId}");
        }

        /*** TO STRING HELPER FUNCTIONS ***/

        /// <summary>
        /// Converts FileOrDirectoryArtifact to string
        /// </summary>
        public string FileOrDirectoryToString(FileOrDirectoryArtifact fileArtifact)
        {
            return fileArtifact.Path.ToString(PathTable);
        }
    }
}
