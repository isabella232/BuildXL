// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Diagnostics.ContractsLight;
using System.IO;
using System.Security.AccessControl;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using BuildXL.Native.IO.Windows;
using BuildXL.Utilities;
using BuildXL.Utilities.Instrumentation.Common;
using BuildXL.Utilities.Tasks;
using BuildXL.Utilities.Tracing;
using JetBrains.Annotations;
using Microsoft.Win32.SafeHandles;
using static BuildXL.Utilities.FormattableStringEx;
using UnixIO = BuildXL.Interop.Unix.IO;

namespace BuildXL.Native.IO
{
    /// <summary>
    /// Due to the clr's various static initialization order of members this one has to be held
    /// in a separate static class so that setting this does not intiialize the members that depend on the
    /// overwritten version of the LoggingContext
    /// </summary>
    public static class FileUtilitiesStaticLoggingContext
    {
        /// <summary>
        /// The loggingcontext to use for file operations.
        /// Anybody using the static filesystem MUST override this static member before calling any methods;
        /// </summary>
        public static LoggingContext LoggingContext = new LoggingContext("FileUtilities");
    }

    /// <summary>
    /// Static facade with utilities for manipulating files and directories. Also offers functions for directly calling filesystem level functionality.
    /// Serves as an entry point for direct I/O throughout BuildXL's code base and proxies its calls to platform specific implementations of IFileSystem and IFileUtilities.
    /// </summary>
    public static class FileUtilities
    {
        private static LoggingContext LoggingContext => FileUtilitiesStaticLoggingContext.LoggingContext;

        /// <summary>
        /// A platform specific concrete implementation of I/O helpers and utilities
        /// </summary>
        /// <remarks>
        /// When running on Windows but inside the CoreCLR, we use the same concrete implementation
        /// as the vanilla BuildXL build for Windows and skip Unix implementations completely
        /// </remarks>
        private static readonly IFileUtilities s_fileUtilities = OperatingSystemHelper.IsUnixOS
            ? (IFileUtilities) new Unix.FileUtilitiesUnix()
            : (IFileUtilities) new Windows.FileUtilitiesWin(LoggingContext);

        /// <summary>
        /// A platform specific concrete implementation of the file system layer functions
        /// </summary>
        /// <remarks>
        /// When running on Windows but inside the CoreCLR, we use the same concrete implementation
        /// as the vanilla BuildXL build for Windows and skip Unix implementations completely
        /// </remarks>
        private static readonly IFileSystem s_fileSystem =  OperatingSystemHelper.IsUnixOS
            ? ((Unix.FileUtilitiesUnix)s_fileUtilities).FileSystem
            : ((Windows.FileUtilitiesWin)s_fileUtilities).FileSystem;

        private static readonly ObjectPool<List<StringSegment>> StringSegmentListPool = Pools.CreateListPool<StringSegment>();

        /// <summary>
        /// Counters
        /// </summary>
        /// <remarks>
        /// As it is static, we reset it to prevent from having cumulative values between server-mode builds
        /// </remarks>
        public static CounterCollection<StorageCounters> Counters { get; private set; }

        /// <summary>
        /// Create a new collection of the storage counters.
        /// </summary>
        public static void CreateCounters()
        {
            Counters = new CounterCollection<StorageCounters>();
        }

        /// <summary>
        /// LocalLow user folder
        /// </summary>
        /// <remarks>
        /// http://msdn.microsoft.com/en-us/library/windows/desktop/dd378457(v=vs.85).aspx
        /// </remarks>
        [SuppressMessage("Microsoft.Usage", "CA2211")]
        public static Guid KnownFolderLocalLow = new Guid("A520A1A4-1780-4FF6-BD18-167343C5AF16");

        #region Directory related functions

        /// <summary>
        /// Creates all directories up to the given path
        /// </summary>
        /// <exception cref="BuildXLException">
        /// Thrown if the directory creation fails in a recoverable manner (e.g. access denied).
        /// </exception>
        public static void CreateDirectory(string path)
        {
            Contract.Requires(!string.IsNullOrEmpty(path));

            s_fileSystem.CreateDirectory(path);
        }

        /// <summary>
        /// Creates all directories up to the given path and retries a few times in case of failure
        /// </summary>
        /// <exception cref="BuildXLException">
        /// Thrown if the directory creation unrecoverably fails after exhaustive retry attempts.
        /// </exception>
        public static void CreateDirectoryWithRetry(string path)
        {
            Contract.Requires(!string.IsNullOrEmpty(path));

            bool success = Helpers.RetryOnFailure(
                finalRound =>
                {
                    CreateDirectory(path);
                    return true;
                });

            if (!success)
            {
                throw new BuildXLException("Create directory failed after exhausting retries. " + path);
            }
        }

        /// <see cref="IFileSystem.RemoveDirectory(string)"/>
        public static void RemoveDirectory(string path)
        {
            s_fileSystem.RemoveDirectory(path);
        }

        /// <see cref="IFileSystem.TryRemoveDirectory(string, out int)"/>
        public static bool TryRemoveDirectory(string path, out int hr)
        {
            return s_fileSystem.TryRemoveDirectory(path, out hr);
        }

        /// <see cref="IFileUtilities.DeleteDirectoryContents(string, bool, Func{string, bool}, ITempCleaner, bool, CancellationToken?)"/>
        public static void DeleteDirectoryContents(
            string path,
            bool deleteRootDirectory = false,
            Func<string, bool> shouldDelete = null,
            ITempCleaner tempDirectoryCleaner = null,
            bool bestEffort = false,
            CancellationToken? cancellationToken = default) =>
            s_fileUtilities.DeleteDirectoryContents(path, deleteRootDirectory, shouldDelete, tempDirectoryCleaner, bestEffort, cancellationToken);

        /// <see cref="IFileSystem.EnumerateDirectoryEntries(string, bool, Action{string, string, FileAttributes}, bool)"/>
        public static EnumerateDirectoryResult EnumerateDirectoryEntries(
            string directoryPath,
            bool recursive,
            Action<string, string, FileAttributes> handleEntry)
        {
            return s_fileSystem.EnumerateDirectoryEntries(directoryPath, recursive, handleEntry);
        }

        /// <see cref="IFileSystem.EnumerateDirectoryEntries(string, bool, string, Action{string, string, FileAttributes}, bool, bool)"/>
        public static EnumerateDirectoryResult EnumerateDirectoryEntries(
            string directoryPath,
            bool recursive,
            string pattern,
            Action<string, string, FileAttributes> handleEntry)
        {
            return s_fileSystem.EnumerateDirectoryEntries(directoryPath, recursive, pattern, handleEntry, followSymlinksToDirectories: true);
        }

        /// <see cref="IFileSystem.EnumerateFiles(string, bool, string, Action{string, string, FileAttributes, long})"/>
        public static EnumerateDirectoryResult EnumerateFiles(
            string directoryPath,
            bool recursive,
            string pattern,
            Action<string /*filePath*/, string /*fileName*/, FileAttributes /*attributes*/, long /*fileSize*/> handleFileEntry)
        {
            return s_fileSystem.EnumerateFiles(directoryPath, recursive, pattern, handleFileEntry);
        }

        /// <see cref="IFileSystem.EnumerateDirectoryEntries(string, Action{string, FileAttributes}, bool)"/>
        public static EnumerateDirectoryResult EnumerateDirectoryEntries(string directoryPath, Action<string, FileAttributes> handleEntry)
        {
            return EnumerateDirectoryEntries(directoryPath, false, (currentDirectory, fileName, fileAttributes) => handleEntry(fileName, fileAttributes));
        }

        /// <see cref="IFileSystem.EnumerateDirectoryEntries(string, bool, string, uint, bool, IDirectoryEntriesAccumulator, bool)"/>
        public static EnumerateDirectoryResult EnumerateDirectoryEntries(
            string directoryPath,
            bool enumerateDirectory,
            string pattern,
            uint directoriesToSkipRecursively,
            bool recursive,
            IDirectoryEntriesAccumulator accumulators)
        {
            return s_fileSystem.EnumerateDirectoryEntries(directoryPath, enumerateDirectory, pattern, directoriesToSkipRecursively, recursive, accumulators);
        }

        /// <see cref="IFileUtilities.FindAllOpenHandlesInDirectory(string, HashSet{string})"/>
        public static string FindAllOpenHandlesInDirectory(string directoryPath, HashSet<string> pathsPossiblyPendingDelete = null) =>
            s_fileUtilities.FindAllOpenHandlesInDirectory(directoryPath, pathsPossiblyPendingDelete);

        /// <see cref="IFileSystem.TryOpenDirectory(string, FileDesiredAccess, FileShare, FileFlagsAndAttributes, out SafeFileHandle)"/>
        public static OpenFileResult TryOpenDirectory(
            string directoryPath,
            FileDesiredAccess desiredAccess,
            FileShare shareMode,
            FileFlagsAndAttributes flagsAndAttributes,
            out SafeFileHandle handle)
        {
            return s_fileSystem.TryOpenDirectory(directoryPath, desiredAccess, shareMode, flagsAndAttributes, out handle);
        }

        /// <see cref="IFileSystem.TryOpenDirectory(string, FileShare, out SafeFileHandle)"/>
        public static OpenFileResult TryOpenDirectory(string directoryPath, FileShare shareMode, out SafeFileHandle handle)
        {
            return s_fileSystem.TryOpenDirectory(directoryPath, shareMode, out handle);
        }

        #endregion

        #region File related functions

        /// <see cref="IFileUtilities.CopyFileAsync(string, string, Func{SafeFileHandle, SafeFileHandle, bool}, Action{SafeFileHandle, SafeFileHandle})"/>
        public static Task<bool> CopyFileAsync(
            string source,
            string destination,
            Func<SafeFileHandle, SafeFileHandle, bool> predicate = null,
            Action<SafeFileHandle, SafeFileHandle> onCompletion = null) => s_fileUtilities.CopyFileAsync(source, destination, predicate, onCompletion);

        /// <see cref="IFileUtilities.MoveFileAsync(string, string, bool)"/>
        public static Task MoveFileAsync(
            string source,
            string destination,
            bool replaceExisting = false) => s_fileUtilities.MoveFileAsync(source, destination, replaceExisting);

        /// <summary>
        /// Tries to create copy-on-write by calling <see cref="IFileUtilities.CloneFile(string, string, bool)"/>.
        /// </summary>
        /// <param name="source">Source of copy.</param>
        /// <param name="destination">Destination path.</param>
        /// <param name="followSymlink">Flag indicating whether to follow source symlink or not.</param>
        public static Possible<Unit> TryCreateCopyOnWrite(string source, string destination, bool followSymlink)
        {
            try
            {
                using (Counters?.StartStopwatch(StorageCounters.CopyOnWriteDuration))
                {
                    Counters?.IncrementCounter(StorageCounters.CopyOnWriteCount);
                    s_fileUtilities.CloneFile(source, destination, followSymlink);
                    Counters?.IncrementCounter(StorageCounters.SuccessfulCopyOnWriteCount);
                    return Unit.Void;
                }
            }
            catch (NativeWin32Exception ex)
            {
                return NativeFailure.CreateFromException(ex);
            }
        }

        /// <summary>
        /// Tries to copy using <see cref="IFileUtilities.InKernelFileCopy(string, string, bool)"/>.
        /// </summary>
        public static Possible<Unit> TryInKernelFileCopy(string source, string destination, bool followSymlink)
        {
            try
            {
                using (Counters?.StartStopwatch(StorageCounters.InKernelFileCopyDuration))
                {
                    Counters?.IncrementCounter(StorageCounters.InKernelFileCopyCount);
                    Possible<Unit> result = s_fileUtilities.InKernelFileCopy(source, destination, followSymlink);
                    if (result.Succeeded)
                    {
                        Counters?.IncrementCounter(StorageCounters.SuccessfulInKernelFileCopyCount);
                    }

                    return result;
                }
            }
            catch (NativeWin32Exception ex)
            {
               return NativeFailure.CreateFromException(ex);
            }
            catch (Exception ex)
            {
                return new NativeFailure((int)BuildXL.Interop.Unix.IO.Errno.ENOSYS, ex.ToString());
            }
        }

        /// <see cref="IFileUtilities.CreateReplacementFile(string, FileShare, bool, bool)"/>
        public static FileStream CreateReplacementFile(
            string path,
            FileShare fileShare,
            bool openAsync = true,
            bool allowExcludeFileShareDelete = false) => s_fileUtilities.CreateReplacementFile(path, fileShare, openAsync, allowExcludeFileShareDelete);

        /// <see cref="IFileUtilities.DeleteFile(string, bool, ITempCleaner)"/>
        public static void DeleteFile(string path, bool retryOnFailure = true, ITempCleaner tempDirectoryCleaner = null) =>
            s_fileUtilities.DeleteFile(path, retryOnFailure, tempDirectoryCleaner);

        /// <see cref="IFileUtilities.PosixDeleteMode"/>
        public static PosixDeleteMode PosixDeleteMode
        {
            get { return s_fileUtilities.PosixDeleteMode; }
            set { s_fileUtilities.PosixDeleteMode = value; }
        }

        /// <summary>
        /// If set to true, then <see cref="PosixDeleteMode"/> the value will be <see cref="PosixDeleteMode.NoRun"/>,
        /// otherwise, for the sake of backward compatibility, the value will be <see cref="PosixDeleteMode.RunFirst"/>.
        /// </summary>
        public static bool SkipPosixDelete
        {
            get
            {
                return s_fileUtilities.PosixDeleteMode == PosixDeleteMode.NoRun;
            }

            set
            {
                if (value)
                {
                    s_fileUtilities.PosixDeleteMode = PosixDeleteMode.NoRun;
                }
                else
                {
                    s_fileUtilities.PosixDeleteMode = PosixDeleteMode.RunFirst;
                }
            }
        }

        /// <see cref="IFileUtilities.TryDeleteFile(string, bool, ITempCleaner)"/>
        public static Possible<string, DeletionFailure> TryDeleteFile(string path, bool retryOnFailure = true, ITempCleaner tempDirectoryCleaner = null) =>
            s_fileUtilities.TryDeleteFile(path, retryOnFailure, tempDirectoryCleaner);

        /// <summary>
        /// Tries to delete file or directory if exists.
        /// </summary>
        /// <param name="fileOrDirectoryPath">Path to file or directory to be deleted, if exists.</param>
        /// <param name="tempDirectoryCleaner">Temporary directory cleaner.</param>
        public static Possible<string, Failure> TryDeletePathIfExists(string fileOrDirectoryPath, ITempCleaner tempDirectoryCleaner = null)
        {
            var maybeExistence = TryProbePathExistence(fileOrDirectoryPath, followSymlink: false);
            if (!maybeExistence.Succeeded)
            {
                return maybeExistence.Failure;
            }

            var existence = maybeExistence.Result;
            if (existence == PathExistence.ExistsAsFile)
            {
                var possibleDeletion = TryDeleteFile(
                    fileOrDirectoryPath,
                    retryOnFailure: true,
                    tempDirectoryCleaner: tempDirectoryCleaner);

                if (!possibleDeletion.Succeeded)
                {
                    return possibleDeletion.WithGenericFailure();
                }
            }
            else if (existence == PathExistence.ExistsAsDirectory)
            {
                DeleteDirectoryContents(fileOrDirectoryPath, deleteRootDirectory: true, tempDirectoryCleaner: tempDirectoryCleaner);
            }

            return fileOrDirectoryPath;
        }

        /// <summary>
        /// Returns true if given file attributes denote a real directory.
        /// A symlink pointing to a directory (Directory | ReparsePoint) is not considered a directory.
        /// </summary>
        public static bool IsDirectoryNoFollow(FileAttributes attributes)
        {
            return
                ((attributes & FileAttributes.Directory) == FileAttributes.Directory) &&
                ((attributes & FileAttributes.ReparsePoint) == 0);
        }

        /// <summary>
        /// Returns true if given file attributes denote a reparse point that points to a directory.
        /// </summary>
        public static bool IsDirectorySymlinkOrJunction(FileAttributes attributes) 
        {
            return
                    ((attributes & FileAttributes.Directory) == FileAttributes.Directory) &&
                    ((attributes & FileAttributes.ReparsePoint) != 0);
        }

        /// <summary>
        /// Checks if a directory exists.
        /// </summary>
        /// <remarks>
        /// Doesn't follow the symlink if <paramref name="path"/> is a symlink.  If you want the behavior
        /// that follows symlinks, use <see cref="Directory.Exists(string)"/>.
        /// </remarks>
        public static bool DirectoryExistsNoFollow(string path)
        {
            var maybeExistence = TryProbePathExistence(path, followSymlink: false);
            return maybeExistence.Succeeded && maybeExistence.Result == PathExistence.ExistsAsDirectory;
        }

        /// <summary>
        /// Checks if a file exists.
        /// </summary>
        /// <remarks>
        /// Doesn't follow the symlink if <paramref name="path"/> is a symlink.
        /// </remarks>
        public static bool FileExistsNoFollow(string path)
        {
            var maybeExistence = TryProbePathExistence(path, followSymlink: false);
            return maybeExistence.Succeeded && maybeExistence.Result == PathExistence.ExistsAsFile;
        }

        /// <see cref="IFileUtilities.TryMoveDelete(string, string)"/>
        public static bool TryMoveDelete(string path, string deletionTempDirectory) => s_fileUtilities.TryMoveDelete(path, deletionTempDirectory);

        /// <see cref="IFileUtilities.GetFileName(string)"/>
        public static Possible<string> GetFileName(string path) => s_fileUtilities.GetFileName(path);

        /// <see cref="IFileUtilities.GetFileTimestamps"/>
        public static FileTimestamps GetFileTimestamps(string path, bool followSymlink = false)
            => s_fileUtilities.GetFileTimestamps(path, followSymlink);

        /// <see cref="IFileUtilities.SetFileTimestamps"/>
        public static void SetFileTimestamps(string path, FileTimestamps timestamps, bool followSymlink = false)
            => s_fileUtilities.SetFileTimestamps(path, timestamps, followSymlink);

        /// <see cref="IFileUtilities.WriteAllTextAsync(string, string, Encoding)"/>
        public static Task WriteAllTextAsync(
            string filePath,
            string text,
            Encoding encoding) => s_fileUtilities.WriteAllTextAsync(filePath, text, encoding);

        /// <see cref="IFileUtilities.WriteAllBytesAsync(string, byte[], Func{SafeFileHandle, bool}, Action{SafeFileHandle})"/>
        public static Task<bool> WriteAllBytesAsync(
            string filePath,
            byte[] bytes,
            Func<SafeFileHandle, bool> predicate = null,
            Action<SafeFileHandle> onCompletion = null) => s_fileUtilities.WriteAllBytesAsync(filePath, bytes, predicate, onCompletion);

        /// <see cref="IFileUtilities.TryFindOpenHandlesToFile"/>
        public static bool TryFindOpenHandlesToFile(string filePath, out string diagnosticInfo, bool printCurrentFilePath = true)
            => s_fileUtilities.TryFindOpenHandlesToFile(filePath, out diagnosticInfo, printCurrentFilePath);

        /// <see cref="IFileUtilities.GetHardLinkCount(string)"/>
        public static uint GetHardLinkCount(string path) => s_fileUtilities.GetHardLinkCount(path);

        /// <see cref="IFileUtilities.HasWritableAccessControl(string)"/>
        public static bool HasWritableAccessControl(string path) => s_fileUtilities.HasWritableAccessControl(path);

        /// <see cref="IFileUtilities.HasWritableAttributeAccessControl(string)"/>
        public static bool HasWritableAttributeAccessControl(string path) => s_fileUtilities.HasWritableAttributeAccessControl(path);

        /// <see cref="IFileUtilities.CreateFileStream(string, FileMode, FileAccess, FileShare, FileOptions, bool, bool)"/>
        public static FileStream CreateFileStream(
            string path,
            FileMode fileMode,
            FileAccess fileAccess,
            FileShare fileShare,
            FileOptions options = FileOptions.None,
            bool force = false,
            bool allowExcludeFileShareDelete = false)
        {
            return s_fileUtilities.CreateFileStream(path, fileMode, fileAccess, fileShare, options, force, allowExcludeFileShareDelete);
        }

        /// <see cref="IFileUtilities.CreateAsyncFileStream(string, FileMode, FileAccess, FileShare, FileOptions, bool, bool)"/>
        public static FileStream CreateAsyncFileStream(
            string path,
            FileMode fileMode,
            FileAccess fileAccess,
            FileShare fileShare,
            FileOptions options = FileOptions.None,
            bool force = false,
            bool allowExcludeFileShareDelete = false)
        {
            return s_fileUtilities.CreateAsyncFileStream(path, fileMode, fileAccess, fileShare, options, force, allowExcludeFileShareDelete);
        }

        /// <see cref="IFileUtilities.UsingFileHandleAndFileLength"/>
        public static TResult UsingFileHandleAndFileLength<TResult>(
            string path,
            FileDesiredAccess desiredAccess,
            FileShare shareMode,
            FileMode creationDisposition,
            FileFlagsAndAttributes flagsAndAttributes,
            Func<SafeFileHandle, long, TResult> handleStream)
            =>
                s_fileUtilities.UsingFileHandleAndFileLength(
                    path,
                    desiredAccess,
                    shareMode,
                    creationDisposition,
                    flagsAndAttributes,
                    handleStream);

        /// <summary>
        /// Tries to duplicate a file.
        /// </summary>
        public static Task<FileDuplicationResult> TryDuplicateOneFileAsync(string sourcePath, string destinationPath)
        {
            Contract.Requires(!string.IsNullOrWhiteSpace(sourcePath));
            Contract.Requires(!string.IsNullOrWhiteSpace(destinationPath));

            if (string.Compare(sourcePath, destinationPath, OperatingSystemHelper.PathComparison) == 0)
            {
                return Task.FromResult(FileDuplicationResult.Existed); // Nothing to do.
            }

            return ExceptionUtilities.HandleRecoverableIOExceptionAsync(
                async () =>
                {
                    var destinationDirectory = Path.GetDirectoryName(destinationPath);
                    if (DirectoryExistsNoFollow(destinationDirectory))
                    {
                        if (FileExistsNoFollow(destinationPath))
                        {
                            DeleteFile(destinationPath);
                        }
                    }
                    else
                    {
                        CreateDirectory(destinationDirectory);
                    }

                    if (!OperatingSystemHelper.IsUnixOS)
                    {
                        var hardlinkResult = s_fileSystem.TryCreateHardLinkViaSetInformationFile(destinationPath, sourcePath);

                        if (hardlinkResult == CreateHardLinkStatus.Success)
                        {
                            return FileDuplicationResult.Hardlinked;
                        }
                    }
                    else
                    {
                        if (IsCopyOnWriteSupportedByEnlistmentVolume)
                        {
                            var possiblyCreateCopyOnWrite = TryCreateCopyOnWrite(sourcePath, destinationPath, followSymlink: false);
                            if (possiblyCreateCopyOnWrite.Succeeded)
                            {
                                return FileDuplicationResult.Copied;
                            }
                        }
                    }

                    await CopyFileAsync(sourcePath, destinationPath);
                    return FileDuplicationResult.Copied;
                },
                ex => { throw new BuildXLException(ex.Message); });
        }

        /// <see cref="IFileSystem.TryCreateOrOpenFile(string, FileDesiredAccess, FileShare, FileMode, FileFlagsAndAttributes, out SafeFileHandle)"/>
        public static OpenFileResult TryCreateOrOpenFile(
            string path,
            FileDesiredAccess desiredAccess,
            FileShare shareMode,
            FileMode creationDisposition,
            FileFlagsAndAttributes flagsAndAttributes,
            out SafeFileHandle handle)
        {
            return s_fileSystem.TryCreateOrOpenFile(path, desiredAccess, shareMode, creationDisposition, flagsAndAttributes, out handle);
        }

        /// <see cref="IFileSystem.TryOpenFileById(SafeFileHandle, FileId, FileDesiredAccess, FileShare, FileFlagsAndAttributes, out SafeFileHandle)"/>
        public static OpenFileResult TryOpenFileById(
            SafeFileHandle existingHandleOnVolume,
            FileId fileId,
            FileDesiredAccess desiredAccess,
            FileShare shareMode,
            FileFlagsAndAttributes flagsAndAttributes,
            out SafeFileHandle handle)
        {
            return s_fileSystem.TryOpenFileById(existingHandleOnVolume, fileId, desiredAccess, shareMode, flagsAndAttributes, out handle);
        }

        /// <see cref="IFileSystem.TryReOpenFile(SafeFileHandle, FileDesiredAccess, FileShare, FileFlagsAndAttributes, out SafeFileHandle)"/>
        public static ReOpenFileStatus TryReOpenFile(
            SafeFileHandle existing,
            FileDesiredAccess desiredAccess,
            FileShare shareMode,
            FileFlagsAndAttributes flagsAndAttributes,
            out SafeFileHandle reopenedHandle)
        {
            return s_fileSystem.TryReOpenFile(existing, desiredAccess, shareMode, flagsAndAttributes, out reopenedHandle);
        }

        /// <see cref="IFileSystem.TryPosixDelete(string, out OpenFileResult)"/>
        public static unsafe bool TryPosixDelete(string pathToDelete, out OpenFileResult openFileResult)
        {
            return s_fileSystem.TryPosixDelete(pathToDelete, out openFileResult);
        }

        /// <see cref="IFileSystem.TrySetDeletionDisposition(SafeFileHandle)"/>
        public static unsafe bool TrySetDeletionDisposition(SafeFileHandle handle)
        {
            return s_fileSystem.TrySetDeletionDisposition(handle);
        }

        /// <see cref="IFileSystem.GetFileFlagsAndAttributesForPossibleReparsePoint"/>
        public static FileFlagsAndAttributes GetFileFlagsAndAttributesForPossibleReparsePoint(string expandedPath)
        {
            using (Counters?.StartStopwatch(StorageCounters.GetFileFlagsAndAttributesForPossibleReparsePointDuration))
            {
                return s_fileSystem.GetFileFlagsAndAttributesForPossibleReparsePoint(expandedPath);
            }
        }

        /// <see cref="IFileSystem.GetFileAttributesByHandle(SafeFileHandle)"/>
        public static unsafe FileAttributes GetFileAttributesByHandle(SafeFileHandle fileHandle)
        {
            using (Counters?.StartStopwatch(StorageCounters.GetFileAttributesByHandleDuration))
            {
                return s_fileSystem.GetFileAttributesByHandle(fileHandle);
            }
        }

        /// <see cref="IFileSystem.GetFileAttributes(string)"/>
        public static FileAttributes GetFileAttributes(string path) => s_fileSystem.GetFileAttributes(path);

        /// <see cref="IFileSystem.SetFileAttributes(string, FileAttributes)"/>
        public static void SetFileAttributes(string path, FileAttributes attributes)
        {
            s_fileSystem.SetFileAttributes(path, attributes);
        }

        /// <see cref="IFileUtilities.SetFileAccessControl(string, FileSystemRights, bool)"/>
        public static void SetFileAccessControl(string path, FileSystemRights fileSystemRights, bool allow)
        {
            s_fileUtilities.SetFileAccessControl(path, fileSystemRights, allow);
        }

        /// <see cref="IFileSystem.TryWriteFileSync(SafeFileHandle, byte[], out int)"/>
        public static bool TryWriteFileSync(SafeFileHandle handle, byte[] content, out int nativeErrorCode)
        {
            return s_fileSystem.TryWriteFileSync(handle, content, out nativeErrorCode);
        }

        #endregion

        #region General file and directory utilities

        /// <see cref="IFileUtilities.Exists(string)"/>
        public static bool Exists(string path) => s_fileUtilities.Exists(path);

        /// <see cref="IFileUtilities.DoesLogicalDriveHaveSeekPenalty(char)"/>
        public static bool? DoesLogicalDriveHaveSeekPenalty(char driveLetter) => s_fileUtilities.DoesLogicalDriveHaveSeekPenalty(driveLetter);

        /// <see cref="IFileUtilities.GetKnownFolderPath(Guid)"/>
        public static string GetKnownFolderPath(Guid knownFolder) => s_fileUtilities.GetKnownFolderPath(knownFolder);

        /// <see cref="IFileUtilities.GetUserSettingsFolder(string)"/>
        public static string GetUserSettingsFolder(string appName) => s_fileUtilities.GetUserSettingsFolder(appName);

        /// <see cref="IFileUtilities.TryTakeOwnershipAndSetWriteable(string)"/>
        public static bool TryTakeOwnershipAndSetWriteable(string path) => s_fileUtilities.TryTakeOwnershipAndSetWriteable(path);

        #endregion

        #region Soft- (Junction) and Hardlink functions

        /// <see cref="IFileSystem.CreateJunction(string, string, bool, bool)"/>
        public static void CreateJunction(string junctionPoint, string targetDir, bool createDirectoryForJunction = true, bool allowNonExistentTarget = false)
        {
            s_fileSystem.CreateJunction(junctionPoint, targetDir, createDirectoryForJunction, allowNonExistentTarget);
        }

        /// <see cref="IFileSystem.TryCreateSymbolicLink(string, string, bool)"/>
        public static Possible<Unit> TryCreateSymbolicLink(string symLinkFileName, string targetFileName, bool isTargetFile)
        {
            return s_fileSystem.TryCreateSymbolicLink(symLinkFileName, targetFileName, isTargetFile);
        }



        /// <summary>
        /// Tries to create a reparse point if targets do not match.
        /// </summary>
        /// <remarks>
        /// The first parameter should be a path to an existing reparse point 
        /// </remarks>
        public static Possible<Unit> TryCreateReparsePointIfTargetsDoNotMatch(string reparsePoint, string reparsePointTarget, ReparsePointType type)
        {
            bool shouldCreate = true;
            if (IsReparsePointActionable(type))
            {
                var openResult = TryCreateOrOpenFile(
                    reparsePoint,
                    FileDesiredAccess.GenericRead,
                    FileShare.Read | FileShare.Delete,
                    FileMode.Open,
                    FileFlagsAndAttributes.FileFlagOverlapped | FileFlagsAndAttributes.FileFlagOpenReparsePoint | FileFlagsAndAttributes.FileFlagBackupSemantics,
                    out SafeFileHandle handle);

                if (openResult.Succeeded)
                {
                    using (handle)
                    {
                        // Do not attempt to convert the target path to absolute path - always compare raw targets
                        var possibleExistingSymlinkTarget = TryGetReparsePointTarget(handle, reparsePoint);
                        var possibleExistingSymlinkType = TryGetReparsePointType(reparsePoint);

                        if (possibleExistingSymlinkTarget.Succeeded && possibleExistingSymlinkType.Succeeded)
                        {
                            shouldCreate = !string.Equals(reparsePointTarget, possibleExistingSymlinkTarget.Result, OperatingSystemHelper.PathComparison) || type != possibleExistingSymlinkType.Result;
                        }
                    }
                }
            }

            if (shouldCreate)
            {
                s_fileUtilities.DeleteFile(reparsePoint, retryOnFailure: true);
                return TryCreateReparsePoint(reparsePoint, reparsePointTarget, type);
            }

            return Unit.Void;
        }

        /// <summary>
        /// Tries to create a reparse point in the indicated path
        /// </summary>
        /// <remarks>
        /// The path should be absent before calling this method
        /// </remarks>
        public static Possible<Unit> TryCreateReparsePoint(string path, string reparsePointTarget, ReparsePointType type)
        {
            if (type == ReparsePointType.Junction)
            {
                try
                {
                    s_fileSystem.CreateJunction(path, reparsePointTarget, allowNonExistentTarget: true);
                }
                catch (Exception e)
                {
                    return new Failure<Exception>(e);
                }
            }
            else
            {
                CreateDirectory(Path.GetDirectoryName(path));

                var maybeSymbolicLink = s_fileSystem.TryCreateSymbolicLink(path, reparsePointTarget, isTargetFile: type != ReparsePointType.DirectorySymlink);
                if (!maybeSymbolicLink.Succeeded)
                {
                    return maybeSymbolicLink.Failure;
                }
            }

            return Unit.Void;
        }

        /// <see cref="IFileSystem.TryCreateHardLink(string, string)"/>
        public static CreateHardLinkStatus TryCreateHardLink(string link, string linkTarget)
        {
            return s_fileSystem.TryCreateHardLink(link, linkTarget);
        }

        /// <see cref="IFileSystem.TryCreateHardLinkViaSetInformationFile(string, string, bool)"/>
        public static CreateHardLinkStatus TryCreateHardLinkViaSetInformationFile(string link, string linkTarget, bool replaceExisting = true)
        {
            return s_fileSystem.TryCreateHardLinkViaSetInformationFile(link, linkTarget, replaceExisting);
        }

        /// <see cref="IFileSystem.IsReparsePointActionable(ReparsePointType)"/>
        public static bool IsReparsePointActionable(ReparsePointType reparsePointType)
        {
            return s_fileSystem.IsReparsePointActionable(reparsePointType);
        }

        /// <see cref="IFileSystem.TryGetReparsePointType(string)"/>
        public static Possible<ReparsePointType> TryGetReparsePointType(string path)
        {
            using (Counters?.StartStopwatch(StorageCounters.GetReparsePointTypeDuration))
            {
                return s_fileSystem.TryGetReparsePointType(path);
            }
        }

        /// <see cref="IFileSystem.IsWciReparseArtifact(string)"/>
        public static bool IsWciReparseArtifact(string path)
        {
            return s_fileSystem.IsWciReparseArtifact(path);
        }

        /// <see cref="IFileSystem.IsWciReparsePoint(string)"/>
        public static bool IsWciReparsePoint(string path)
        {
            return s_fileSystem.IsWciReparsePoint(path);
        }

        /// <see cref="IFileSystem.IsWciTombstoneFile(string)"/>
        public static bool IsWciTombstoneFile(string path)
        {
            return s_fileSystem.IsWciTombstoneFile(path);
        }

        /// <see cref="IFileSystem.GetChainOfReparsePoints(SafeFileHandle, string, IList{string})"/>
        public static void GetChainOfReparsePoints(SafeFileHandle handle, string sourcePath, IList<string> chainOfReparsePoints)
        {
            s_fileSystem.GetChainOfReparsePoints(handle, sourcePath, chainOfReparsePoints);
        }

        /// <see cref="IFileSystem.TryGetReparsePointTarget(SafeFileHandle, string)"/>
        public static Possible<string> TryGetReparsePointTarget(SafeFileHandle handle, string sourcePath)
        {
            return s_fileSystem.TryGetReparsePointTarget(handle, sourcePath);
        }

        /// <summary>
        /// Returns the last element of a reparse point chain. If the source path is not a reparse point
        /// it returns the same path.
        /// </summary>
        /// <param name="handle">Handle to the source path. Can be null, in which case is not used</param>
        /// <param name="sourcePath">Path to the artifact</param>
        public static Possible<string> TryGetLastReparsePointTargetInChain([CanBeNull]SafeFileHandle handle, string sourcePath)
        {
            Contract.RequiresNotNullOrEmpty(sourcePath);

            if (handle == null)
            {
                var openResult = FileUtilities.TryOpenDirectory(
                                                sourcePath,
                                                FileDesiredAccess.GenericRead,
                                                FileShare.Read | FileShare.Delete,
                                                FileFlagsAndAttributes.FileFlagOverlapped | FileFlagsAndAttributes.FileFlagOpenReparsePoint,
                                                out handle);
                if (!openResult.Succeeded)
                {
                    return openResult.CreateFailureForError();
                } 
            }

            using (handle)
            {
                var symlinkChainElements = new List<string>();
                FileUtilities.GetChainOfReparsePoints(handle, sourcePath, symlinkChainElements);
                return symlinkChainElements[symlinkChainElements.Count - 1];
            }
        }

        /// <see cref="IFileSystem.IsDirectorySymlinkOrJunction(string)"/>
        public static bool IsDirectorySymlinkOrJunction(string path) => s_fileSystem.IsDirectorySymlinkOrJunction(path);

        /// <see cref="IFileSystem.GetFullPath(string)"/>
        public static string GetFullPath(string path) => s_fileSystem.GetFullPath(path);

        /// <summary>
        /// Returns a unique temporary file name, and creates a 0-byte file by that name on disk.
        /// </summary>
        /// <remarks>
        /// This method functions like <see cref="Path.GetTempFileName()"/>, i.e., it creates a unqiue temp file and returns its name with full path.
        /// <see cref="Path.GetTempFileName()"/> uses the combination a hardcoded prefix and a 4-letter random number as the file name. 
        /// If the file already exist, it will loop to create a new random number until it finds a name of a file doesn't exist. 
        /// This API is shared. If any of the managed process doesn't clean up their temp files, it will affect our performance and we might possibly get access denial. 
        /// So we implement this API to replace <see cref="Path.GetTempFileName()"/>. 
        /// We use Guid.NewGuid().ToString() as part of the file name to make sure the uniqueness.
        /// </remarks>
        public static string GetTempFileName()
        {
            var path = GetTempPath();
            using var fileStream = File.Create(path);
            fileStream.Close();
            return path;
        }

        /// <summary>
        /// Returns a unique temporary file path without creating a file at that location.
        /// <seealso cref="GetTempFileName" />
        /// </summary>
        public static string GetTempPath()
        {
            return Path.Combine(Path.GetTempPath(), "bxl_" + Guid.NewGuid().ToString() + ".tmp");
        }

        #endregion

        #region Journaling functions

        /// <see cref="IFileSystem.ReadFileUsnByHandle(SafeFileHandle, bool)"/>
        public static unsafe MiniUsnRecord? ReadFileUsnByHandle(SafeFileHandle fileHandle, bool forceJournalVersion2 = false)
        {
            using (Counters?.StartStopwatch(StorageCounters.ReadFileUsnByHandleDuration))
            {
                return s_fileSystem.ReadFileUsnByHandle(fileHandle, forceJournalVersion2);
            }
        }

        /// <see cref="IFileSystem.TryReadUsnJournal(SafeFileHandle, byte[], ulong, Usn, bool, bool)"/>
        public static unsafe ReadUsnJournalResult TryReadUsnJournal(
            SafeFileHandle volumeHandle,
            byte[] buffer,
            ulong journalId,
            Usn startUsn = default(Usn),
            bool forceJournalVersion2 = false,
            bool isJournalUnprivileged = false)
        {
            using (Counters?.StartStopwatch(StorageCounters.ReadUsnJournalDuration))
            {
                return s_fileSystem.TryReadUsnJournal(volumeHandle, buffer, journalId, startUsn, forceJournalVersion2, isJournalUnprivileged);
            }
        }

        /// <see cref="IFileSystem.TryQueryUsnJournal(SafeFileHandle)"/>
        public static QueryUsnJournalResult TryQueryUsnJournal(SafeFileHandle volumeHandle)
        {
            return s_fileSystem.TryQueryUsnJournal(volumeHandle);
        }

        /// <see cref="IFileSystem.TryWriteUsnCloseRecordByHandle(SafeFileHandle)"/>
        public static unsafe Usn? TryWriteUsnCloseRecordByHandle(SafeFileHandle fileHandle)
        {
            using (Counters?.StartStopwatch(StorageCounters.WriteUsnCloseRecordByHandleDuration))
            {
                return s_fileSystem.TryWriteUsnCloseRecordByHandle(fileHandle);
            }
        }

        #endregion

        #region Volume handling functions

        /// <see cref="IFileSystem.ListVolumeGuidPathsAndSerials"/>
        public static List<Tuple<VolumeGuidPath, ulong>> ListVolumeGuidPathsAndSerials()
        {
            return s_fileSystem.ListVolumeGuidPathsAndSerials();
        }

        /// <see cref="IFileSystem.GetVolumeFileSystemByHandle(SafeFileHandle)"/>
        public static FileSystemType GetVolumeFileSystemByHandle(SafeFileHandle fileHandle)
        {
            return s_fileSystem.GetVolumeFileSystemByHandle(fileHandle);
        }

        /// <see cref="IFileSystem.GetShortVolumeSerialNumberByHandle(SafeFileHandle)"/>
        public static unsafe uint GetShortVolumeSerialNumberByHandle(SafeFileHandle fileHandle)
        {
            return s_fileSystem.GetShortVolumeSerialNumberByHandle(fileHandle);
        }

        /// <see cref="IFileSystem.GetVolumeFileSystemByHandle(SafeFileHandle)"/>
        public static ulong GetVolumeSerialNumberByHandle(SafeFileHandle fileHandle)
        {
            return s_fileSystem.GetVolumeSerialNumberByHandle(fileHandle);
        }

        /// <see cref="IFileSystem.TryGetFileIdAndVolumeIdByHandle(SafeFileHandle)"/>
        public static unsafe FileIdAndVolumeId? TryGetFileIdAndVolumeIdByHandle(SafeFileHandle fileHandle)
        {
            return s_fileSystem.TryGetFileIdAndVolumeIdByHandle(fileHandle);
        }

        /// <see cref="IFileSystem.IsVolumeMapped(string)"/>
        public static bool IsVolumeMapped(string volume) => s_fileSystem.IsVolumeMapped(volume);

        #endregion

        #region File identity and version

        /// <see cref="IFileSystem.TryGetFileIdentityByHandle(SafeFileHandle)"/>
        public static unsafe FileIdAndVolumeId? TryGetFileIdentityByHandle(SafeFileHandle fileHandle) => s_fileSystem.TryGetFileIdentityByHandle(fileHandle);

        /// <see cref="IFileSystem.TryGetVersionedFileIdentityByHandle(SafeFileHandle)"/>
        public static unsafe (FileIdAndVolumeId, Usn)? TryGetVersionedFileIdentityByHandle(SafeFileHandle fileHandle) => s_fileSystem.TryGetVersionedFileIdentityByHandle(fileHandle);

        /// <see cref="IFileSystem.TryEstablishVersionedFileIdentityByHandle(SafeFileHandle,bool)"/>
        public static unsafe (FileIdAndVolumeId, Usn)? TryEstablishVersionedFileIdentityByHandle(SafeFileHandle fileHandle, bool flushPageCache)
            => s_fileSystem.TryEstablishVersionedFileIdentityByHandle(fileHandle, flushPageCache);

        /// <see cref="IFileSystem.CheckIfVolumeSupportsPreciseFileVersionByHandle(SafeFileHandle)"/>
        public static bool CheckIfVolumeSupportsPreciseFileVersionByHandle(SafeFileHandle fileHandle) => s_fileSystem.CheckIfVolumeSupportsPreciseFileVersionByHandle(fileHandle);

        /// <see cref="IFileSystem.IsPreciseFileVersionSupportedByEnlistmentVolume"/>
        public static bool IsPreciseFileVersionSupportedByEnlistmentVolume
        {
            get => s_fileSystem.IsPreciseFileVersionSupportedByEnlistmentVolume;

            set
            {
                s_fileSystem.IsPreciseFileVersionSupportedByEnlistmentVolume = value;
            }
        }

        #endregion

        #region Generic file system helpers
        /// <see cref="IFileSystem.MaxDirectoryPathLength"/>
        public static int MaxDirectoryPathLength()
        {
            return s_fileSystem.MaxDirectoryPathLength();
        }

        /// <see cref="IFileSystem.TryProbePathExistence(string, bool, out bool)"/>
        public static Possible<PathExistence, NativeFailure> TryProbePathExistence(string path, bool followSymlink)
        {
            return s_fileSystem.TryProbePathExistence(path, followSymlink, out _);
        }

        /// <see cref="IFileSystem.TryProbePathExistence(string, bool, out bool)"/>
        public static Possible<PathExistence, NativeFailure> TryProbePathExistence(string path, bool followSymlink, out bool isReparsePoint)
        {
            return s_fileSystem.TryProbePathExistence(path, followSymlink, out isReparsePoint);
        }

        /// <see cref="IFileSystem.PathMatchPattern"/>
        public static bool PathMatchPattern(string path, string pattern)
        {
            return s_fileSystem.PathMatchPattern(path, pattern);
        }

        /// <see cref="IFileSystem.IsPendingDelete(SafeFileHandle)"/>
        public static unsafe bool IsPendingDelete(SafeFileHandle fileHandle)
        {
            return s_fileSystem.IsPendingDelete(fileHandle);
        }

        /// <see cref="IFileSystem.GetFinalPathNameByHandle(SafeFileHandle, bool)"/>
        public static string GetFinalPathNameByHandle(SafeFileHandle handle, bool volumeGuidPath = false)
        {
            return s_fileSystem.GetFinalPathNameByHandle(handle, volumeGuidPath);
        }
        
        /// <see cref="IFileSystem.TryGetFinalPathNameByPath(string, out string, out int, bool)"/>
        public static bool TryGetFinalPathNameByPath(string path, out string finalPath, out int nativeErrorCode, bool volumeGuidPath = false)
        {
            return s_fileSystem.TryGetFinalPathNameByPath(path, out finalPath, out nativeErrorCode, volumeGuidPath);
        }

        /// <see cref="IFileSystem.FlushPageCacheToFilesystem(SafeFileHandle)"/>
        public static unsafe NtStatus FlushPageCacheToFilesystem(SafeFileHandle handle)
        {
            return s_fileSystem.FlushPageCacheToFilesystem(handle);
        }

        /// <see cref="IFileSystem.CheckIfVolumeSupportsCopyOnWriteByHandle(SafeFileHandle)"/>
        public static bool CheckIfVolumeSupportsCopyOnWriteByHandle(SafeFileHandle fileHandle) => s_fileSystem.CheckIfVolumeSupportsCopyOnWriteByHandle(fileHandle);

        /// <see cref="IFileSystem.IsCopyOnWriteSupportedByEnlistmentVolume"/>
        public static bool IsCopyOnWriteSupportedByEnlistmentVolume
        {
            get => s_fileSystem.IsCopyOnWriteSupportedByEnlistmentVolume;

            set
            {
                s_fileSystem.IsCopyOnWriteSupportedByEnlistmentVolume = value;
            }
        }

        /// <see cref="IFileSystem.IsCopyOnWriteSupportedByEnlistmentVolume"/>
        public static bool IsInKernelCopyingSupportedByHostSystem
        {
            get => s_fileSystem.IsInKernelCopyingSupportedByHostSystem;
        }

        /// <summary>
        /// Determines whether the file system is case sensitive.
        /// </summary>
        /// <remarks>
        /// Ideally we'd use something like pathconf with _PC_CASE_SENSITIVE, but that is non-portable,
        /// not supported on Windows or Linux, etc. For now, this function creates a tmp file with capital letters
        /// and then tests for its existence with lower-case letters.  This could return invalid results in corner
        /// cases where, for example, different file systems are mounted with differing sensitivities.
        /// See: https://github.com/dotnet/corefx/blob/1bff7880bfa949e8c5e46039808ec412640bbb5e/src/Common/src/System/IO/PathInternal.CaseSensitivity.cs#L41
        /// </remarks>
        public static bool IsFileSystemCaseSensitive()
        {
            try
            {
                string pathWithUpperCase = Path.Combine(Path.GetTempPath(), "BUILDXL_CASESENSITIVE_TEST" + Guid.NewGuid().ToString("N"));
                using (new FileStream(pathWithUpperCase, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 0x1000, FileOptions.DeleteOnClose))
                {
                    string lowerCased = pathWithUpperCase.ToLowerInvariant();
                    return !FileExistsNoFollow(lowerCased);
                }
            }
#pragma warning disable ERP022 // Unobserved exception in generic exception handler
            catch
            {
                // In case something goes terribly wrong, we don't want to fail just because
                // of a casing test, so we assume case-insensitive.
                return false;
            }
#pragma warning restore ERP022 // Unobserved exception in generic exception handler
        }

        /// <summary>
        /// Resolves symlink target.
        /// </summary>
        /// <remarks>
        /// If <paramref name="targetPath"/> is an absolute path, then this method simply returns <paramref name="targetPath"/>.
        /// If <paramref name="targetPath"/> is a relative path, then it first resolves the prefixes of <paramref name="symlinkPath"/>
        /// using <see cref="IFileSystem.TryResolveReparsePointRelativeTarget(string, string)"/> (see its documentation for details) before combining the resolved
        /// path with <paramref name="targetPath"/>.
        /// </remarks>
        public static Possible<string> ResolveSymlinkTarget(string symlinkPath, string targetPath = null)
        {
            Contract.Requires(s_fileSystem.IsPathRooted(symlinkPath));

            if (targetPath == null)
            {
                var maybeTarget = s_fileSystem.TryGetReparsePointTarget(null, symlinkPath);
                if (!maybeTarget.Succeeded)
                {
                    return maybeTarget.Failure;
                }

                targetPath = maybeTarget.Result;
            }

            if (s_fileSystem.IsPathRooted(targetPath))
            {
                // If symlink target is an absolute path, then simply returns that path.
                return targetPath;
            }

            // Symlink target is a relative path.
            var maybeResolvedRelative = s_fileSystem.TryResolveReparsePointRelativeTarget(symlinkPath, targetPath);

            if (!maybeResolvedRelative.Succeeded)
            {
                return maybeResolvedRelative.Failure;
            }

            return maybeResolvedRelative;
        }

        /// <summary>
        /// Resolve a reparse point path with respect to its relative target.
        /// </summary>
        /// <remarks>
        /// Given a reparse point path A\B\C and its relative target D\E\F, where D and E can be '.' or '..',
        /// this method simply combines A\B with D\E\F and normalizes the result, i.e., removes '.' and '..'.
        /// </remarks>
        public static Possible<string> TryResolveRelativeTarget(string path, string relativeTarget)
        {
            string parent = Path.GetDirectoryName(path);
            if (parent == null)
            {
                return new Failure<string>($"Failed to resolve relative target for path {path} with target {relativeTarget}");
            }

            return Path.GetFullPath(Path.Combine(parent, relativeTarget));
        }
        
        /// <summary>
        /// Splits path into atoms and push it into the stack such that the top stack contains the last atom.
        /// </summary>
        public static void SplitPathsReverse(string path, Stack<string> atoms)
        {
            string nextPath = path;

            do
            {
                path = nextPath;
                string name = Path.GetFileName(path);
                AddAtom(name);
                nextPath = Path.GetDirectoryName(path);
            }
            while (!string.IsNullOrEmpty(nextPath));

            if (!string.IsNullOrEmpty(path))
            {
                AddAtom(path);
            }

            void AddAtom(string atom)
            {
                if (!string.IsNullOrEmpty(atom))
                {
                    atoms.Push(atom);
                }
            }
        }

        /// <summary>
        /// Makes an exclusive link for a file.
        /// </summary>
        /// <param name="originalPath">File path.</param>
        /// <param name="optionalTemporaryFileName">Temporary file name that users can supply.</param>
        /// <param name="preserveOriginalTimestamp">Whether or not the original timestamp should be preserved.</param>
        public static async Task<Possible<Unit>> TryMakeExclusiveLinkAsync(string originalPath, string optionalTemporaryFileName = null, bool preserveOriginalTimestamp = true)
        {
            if (!FileExistsNoFollow(originalPath))
            {
                return new Failure<string>(I($"Failed to make exclusive link for '{originalPath}' because the file does not exist"));
            }

            // Construct temporary path.
            string directoryName = Path.GetDirectoryName(originalPath);
            string temporaryPath = Path.Combine(directoryName, optionalTemporaryFileName ?? Guid.NewGuid().ToString());

            if (!await CopyFileAsync(originalPath, temporaryPath))
            {
                return new Failure<string>(I($"Failed to make exclusive link for '{originalPath}' because copying it to '{temporaryPath}' failed"));
            }

            if (preserveOriginalTimestamp)
            {
                // Preserve original timestamp if requested.
                var timestamps = GetFileTimestamps(originalPath);
                SetFileTimestamps(temporaryPath, timestamps);
            }

            await MoveFileAsync(temporaryPath, originalPath, replaceExisting: true);

            return Unit.Void;
        }

        /// <summary>
        /// Infers subst source and subst target from a given reference path.
        /// </summary>
        /// <param name="referenceFullPath">Rooted reference path.</param>
        /// <param name="substSource">Output subst source.</param>
        /// <param name="substTarget">Output subst target.</param>
        /// <param name="errorMessage">Error message when this method failed to get the subst source/target.</param>
        /// <returns>
        /// Returns true if the function was able to successfully determine whether subst is used on the referenced path and subst is used. If not, then this function will return false
        /// along with an error message set if an error occured.
        /// On a Unix OS, this will return false because subst is not supported, and the errorMessage will be null.
        /// </returns>
        /// <remarks>
        /// This method calls <code>GetFinalPathByHandle</code> which is only applicable on Windows.
        /// </remarks>
        public static bool TryGetSubstSourceAndTarget(string referenceFullPath, out string substSource, out string substTarget, out string errorMessage)
        {
            Contract.Requires(Path.IsPathRooted(referenceFullPath));

            substSource = null;
            substTarget = null;
            errorMessage = null;

            if (OperatingSystemHelper.IsUnixOS)
            {
                // There is currently no subst in non-Windows OS.
                return false;
            }

            OpenFileResult directoryOpenResult = TryOpenDirectory(
                referenceFullPath,
                FileShare.Read | FileShare.Write | FileShare.Delete,
                out SafeFileHandle directoryHandle);

            if (!directoryOpenResult.Succeeded)
            {
                errorMessage = directoryOpenResult.CreateExceptionForError().Message;
                return false;
            }

            string directoryHandlePath = GetFinalPathNameByHandle(directoryHandle, volumeGuidPath: false);

            if (!string.Equals(referenceFullPath, directoryHandlePath, OperatingSystemHelper.PathComparison))
            {
                string commonPath = referenceFullPath.Substring(2); // Include '\' of '<Drive>:\'  for searching.
                substTarget = referenceFullPath.Substring(0, 3);    // Include '\' of '<Drive>:\' in the substTarget.
                int commonIndex = directoryHandlePath.IndexOf(commonPath, 0, OperatingSystemHelper.PathComparison);

                if (commonIndex == -1)
                {
                    substTarget = null;
                }
                else
                {
                    substSource = directoryHandlePath.Substring(0, commonIndex + 1);
                }
            }

            return !string.IsNullOrWhiteSpace(substSource) && !string.IsNullOrWhiteSpace(substTarget);
        }

        /// <summary>
        /// Unix only (no-op on windows): sets u+x on <paramref name="fileName"/>.  Throws if file doesn't exists and <paramref name="throwIfNotFound"/> is true.
        /// </summary>
        /// <returns>
        /// true if file exists and already has execute permissions or execute permissions were set. Otherwise, false.
        /// </returns>
        public static bool TrySetExecutePermissionIfNeeded(string fileName, bool throwIfNotFound = true)
        {
            if (OperatingSystemHelper.IsUnixOS)
            {
                var mode = UnixIO.GetFilePermissionsForFilePath(fileName, followSymlink: false);
                if (mode < 0)
                {
                    if (throwIfNotFound)
                    {
                        throw new BuildXLException($"Process creation failed: File '{fileName}' not found", new Win32Exception(0x2));
                    }
                    else
                    {
                        return false;
                    }
                }

                var filePermissions = checked((UnixIO.FilePermissions)mode);
                UnixIO.FilePermissions exePermission = UnixIO.FilePermissions.S_IXUSR;
                if (!filePermissions.HasFlag(exePermission))
                {
                    var result = UnixIO.SetFilePermissionsForFilePath(fileName, (filePermissions | exePermission));
                    return result >= 0;
                }

                return true;
            }

            return true;
        }

        /// <summary>
        /// Gets subst drive and path from subst source and target.
        /// </summary>
        public static (string drive, string path) GetSubstDriveAndPath(string substSource, string substTarget)
        {
            Contract.Requires(Path.IsPathRooted(substSource));
            Contract.Requires(Path.IsPathRooted(substTarget));

            string substDrive = Path.GetPathRoot(substTarget).TrimEnd(Path.DirectorySeparatorChar);
            string substPath = substSource.TrimEnd(Path.DirectorySeparatorChar);

            return (substDrive, substPath);
        }

        #endregion
    }
}
