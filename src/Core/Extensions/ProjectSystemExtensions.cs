using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO;
using System.Linq;
using NuGet.Resources;

namespace NuGet
{
    public static class ProjectSystemExtensions
    {
        public static void AddFiles(this IProjectSystem project,
                                    IEnumerable<IPackageFile> files,
                                    IDictionary<string, IPackageFileTransformer> fileTransformers)
        {
            // Convert files to a list
            List<IPackageFile> fileList = files.ToList();

            // See if the project system knows how to sort the files
            var fileComparer = project as IComparer<IPackageFile>;

            if (fileComparer != null)
            {
                fileList.Sort(fileComparer);
            }

            var batchProcessor = project as IBatchProcessor<string>;

            try
            {
                if (batchProcessor != null)
                {
                    var paths = fileList.Select(file => ResolvePath(fileTransformers, file.EffectivePath));
                    batchProcessor.BeginProcessing(paths, PackageAction.Install);
                }

                foreach (IPackageFile file in fileList)
                {
                    if (file.IsEmptyFolder())
                    {
                        continue;
                    }

                    IPackageFileTransformer transformer;

                    // Resolve the target path
                    string path = ResolveTargetPath(project,
                                                    fileTransformers,
                                                    file.EffectivePath,
                                                    out transformer);

                    if (project.IsSupportedFile(path))
                    {
                        // Try to get the package file modifier for the extension                
                        if (transformer != null)
                        {
                            // If the transform was done then continue
                            transformer.TransformFile(file, path, project);
                        }
                        else
                        {
                            TryAddFile(project, file, path);
                        }
                    }
                }
            }
            finally
            {
                if (batchProcessor != null)
                {
                    batchProcessor.EndProcessing();
                }
            }
        }

        /// <summary>
        /// Try to add the specified the project with the target path. If there's an existing file in the project with the same name, 
        /// it will ask the logger for the resolution, which has 4 choices: Overwrite|Ignore|Overwrite All|Ignore All
        /// </summary>
        /// <param name="project"></param>
        /// <param name="file"></param>
        /// <param name="path"></param>
        public static void TryAddFile(IProjectSystem project, IPackageFile file, string path)
        {
            if (project.FileExists(path))
            {
                // file exists, ask user if he wants to overwrite or ignore
                string conflictMessage = String.Format(CultureInfo.CurrentCulture, NuGetResources.FileConflictMessage, path, project.ProjectName);
                FileConflictResolution resolution = project.Logger.ResolveFileConflict(conflictMessage);
                if (resolution == FileConflictResolution.Overwrite || resolution == FileConflictResolution.OverwriteAll)
                {
                    // overwrite
                    project.Logger.Log(MessageLevel.Info, NuGetResources.Info_OverwriteExistingFile, path);
                    project.AddFile(path, file.GetStream());
                }
                else
                {
                    // ignore
                    project.Logger.Log(MessageLevel.Info, NuGetResources.Warning_FileAlreadyExists, path);
                }
            }
            else
            {
                project.AddFile(path, file.GetStream());
            }
        }

        [SuppressMessage("Microsoft.Maintainability", "CA1502:AvoidExcessiveComplexity")]
        [SuppressMessage("Microsoft.Design", "CA1031:DoNotCatchGeneralExceptionTypes", Justification = "We want delete to be robust, when exceptions occur we log then and move on")]
        public static void DeleteFiles(this IProjectSystem project,
                                       IEnumerable<IPackageFile> files,
                                       IEnumerable<IPackage> otherPackages,
                                       IDictionary<string, IPackageFileTransformer> fileTransformers)
        {
            IPackageFileTransformer transformer;
            // First get all directories that contain files
            var directoryLookup = files.ToLookup(p => Path.GetDirectoryName(ResolveTargetPath(project, fileTransformers, p.EffectivePath, out transformer)));

            // Get all directories that this package may have added
            var directories = from grouping in directoryLookup
                              from directory in FileSystemExtensions.GetDirectories(grouping.Key)
                              orderby directory.Length descending
                              select directory;

            // Remove files from every directory
            foreach (var directory in directories)
            {
                var directoryFiles = directoryLookup.Contains(directory) ? directoryLookup[directory] : Enumerable.Empty<IPackageFile>();

                if (!project.DirectoryExists(directory))
                {
                    continue;
                }
                var batchProcessor = project as IBatchProcessor<string>;

                try
                {
                    if (batchProcessor != null)
                    {
                        var paths = directoryFiles.Select(file => ResolvePath(fileTransformers, file.EffectivePath));
                        batchProcessor.BeginProcessing(paths, PackageAction.Uninstall);
                    }

                    foreach (var file in directoryFiles)
                    {
                        if (file.IsEmptyFolder())
                        {
                            continue;
                        }

                        // Resolve the path
                        string path = ResolveTargetPath(project,
                                                        fileTransformers,
                                                        file.EffectivePath,
                                                        out transformer);

                        if (project.IsSupportedFile(path))
                        {
                            if (transformer != null)
                            {
                                var matchingFiles = from p in otherPackages
                                                    from otherFile in project.GetCompatibleItemsCore(p.GetContentFiles())
                                                    where otherFile.EffectivePath.Equals(file.EffectivePath, StringComparison.OrdinalIgnoreCase)
                                                    select otherFile;

                                try
                                {
                                    transformer.RevertFile(file, path, matchingFiles, project);
                                }
                                catch (Exception e)
                                {
                                    // Report a warning and move on
                                    project.Logger.Log(MessageLevel.Warning, e.Message);
                                }
                            }
                            else
                            {
                                project.DeleteFileSafe(path, file.GetStream);
                            }
                        }
                    }

                    // If the directory is empty then delete it
                    if (!project.GetFilesSafe(directory).Any() &&
                        !project.GetDirectoriesSafe(directory).Any())
                    {
                        project.DeleteDirectorySafe(directory, recursive: false);
                    }
                }
                finally
                {
                    if (batchProcessor != null)
                    {
                        batchProcessor.EndProcessing();
                    }
                }
            }
        }

        public static bool TryGetCompatibleItems<T>(this IProjectSystem projectSystem, IEnumerable<T> items, out IEnumerable<T> compatibleItems) where T : IFrameworkTargetable
        {
            if (projectSystem == null)
            {
                throw new ArgumentNullException("projectSystem");
            }

            if (items == null)
            {
                throw new ArgumentNullException("items");
            }

            return VersionUtility.TryGetCompatibleItems<T>(projectSystem.TargetFramework, items, out compatibleItems);
        }

        internal static IEnumerable<T> GetCompatibleItemsCore<T>(this IProjectSystem projectSystem, IEnumerable<T> items) where T : IFrameworkTargetable
        {
            IEnumerable<T> compatibleItems;
            if (VersionUtility.TryGetCompatibleItems(projectSystem.TargetFramework, items, out compatibleItems))
            {
                return compatibleItems;
            }
            return Enumerable.Empty<T>();
        }

        private static string ResolvePath(IDictionary<string, IPackageFileTransformer> fileTransformers, string effectivePath)
        {
            // Try to get the package file modifier for the extension
            string extension = Path.GetExtension(effectivePath);

            IPackageFileTransformer transformer;
            if (fileTransformers.TryGetValue(extension, out transformer))
            {
                // Remove the transformer extension (e.g. .pp, .transform)
                string truncatedPath = RemoveExtension(effectivePath);

                // Bug 1686: Don't allow transforming packages.config.transform
                string fileName = Path.GetFileName(truncatedPath);
                if (!Constants.PackageReferenceFile.Equals(fileName, StringComparison.OrdinalIgnoreCase))
                {
                    effectivePath = truncatedPath;
                }
            }

            return effectivePath;
        }

        private static string ResolveTargetPath(IProjectSystem projectSystem,
                                                IDictionary<string, IPackageFileTransformer> fileTransformers,
                                                string effectivePath,
                                                out IPackageFileTransformer transformer)
        {
            // Try to get the package file modifier for the extension
            string extension = Path.GetExtension(effectivePath);
            if (fileTransformers.TryGetValue(extension, out transformer))
            {
                // Remove the transformer extension (e.g. .pp, .transform)
                string truncatedPath = RemoveExtension(effectivePath);

                // Bug 1686: Don't allow transforming packages.config.transform,
                // but we still want to copy packages.config.transform as-is into the project.
                string fileName = Path.GetFileName(truncatedPath);
                if (Constants.PackageReferenceFile.Equals(fileName, StringComparison.OrdinalIgnoreCase))
                {
                    // setting to null means no pre-processing of this file
                    transformer = null;
                }
                else
                {
                    effectivePath = truncatedPath;
                }
            }

            return projectSystem.ResolvePath(effectivePath);
        }

        private static string RemoveExtension(string path)
        {
            // Remove the extension from the file name, preserving the directory
            return Path.Combine(Path.GetDirectoryName(path), Path.GetFileNameWithoutExtension(path));
        }
    }
}