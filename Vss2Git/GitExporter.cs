﻿/* Copyright 2009 HPDI, LLC
 * 
 * Licensed under the Apache License, Version 2.0 (the "License");
 * you may not use this file except in compliance with the License.
 * You may obtain a copy of the License at
 * 
 *     http://www.apache.org/licenses/LICENSE-2.0
 * 
 * Unless required by applicable law or agreed to in writing, software
 * distributed under the License is distributed on an "AS IS" BASIS,
 * WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
 * See the License for the specific language governing permissions and
 * limitations under the License.
 */

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Windows.Forms;
using Hpdi.VssLogicalLib;

namespace Hpdi.Vss2Git
{
    /// <summary>
    /// Replays and commits changesets into a new Git repository.
    /// </summary>
    /// <author>Trevor Robinson</author>
    class GitExporter : Worker
    {
        private const string DefaultComment = "Vss2Git";

        private readonly VssDatabase database;
        private readonly RevisionAnalyzer revisionAnalyzer;
        private readonly ChangesetBuilder changesetBuilder;
        private readonly StreamCopier streamCopier = new StreamCopier();

        private string emailDomain = "localhost";
        public string EmailDomain
        {
            get { return emailDomain; }
            set { emailDomain = value; }
        }

        public GitExporter(WorkQueue workQueue, Logger logger,
            RevisionAnalyzer revisionAnalyzer, ChangesetBuilder changesetBuilder)
            : base(workQueue, logger)
        {
            this.database = revisionAnalyzer.Database;
            this.revisionAnalyzer = revisionAnalyzer;
            this.changesetBuilder = changesetBuilder;
        }

        public void ExportToGit(string repoPath)
        {
            workQueue.AddLast(delegate(object work)
            {
                var stopwatch = Stopwatch.StartNew();

                logger.WriteSectionSeparator();
                LogStatus(work, "Initializing Git repository");

                // create repository directory if it does not exist
                if (!Directory.Exists(repoPath))
                {
                    Directory.CreateDirectory(repoPath);
                }

                var git = new GitWrapper(repoPath, logger);
                git.Init();

                var pathMapper = new VssPathMapper();

                // create mappings for root projects
                foreach (var rootProject in revisionAnalyzer.RootProjects)
                {
                    var rootPath = VssPathMapper.GetWorkingPath(repoPath, rootProject.Path);
                    pathMapper.SetProjectPath(rootProject.PhysicalName, rootPath);
                }

                // replay each changeset
                int changesetId = 1;
                var changesets = changesetBuilder.Changesets;
                var commitCount = 0;
                var tagCount = 0;
                var replayStopwatch = new Stopwatch();
                var labels = new LinkedList<Revision>();
                foreach (var changeset in changesets)
                {
                    var changesetDesc = string.Format(CultureInfo.InvariantCulture,
                        "changeset {0} from {1}", changesetId, changeset.DateTime);

                    // replay each revision in changeset
                    LogStatus(work, "Replaying " + changesetDesc);
                    labels.Clear();
                    replayStopwatch.Start();
                    bool needCommit;
                    try
                    {
                        needCommit = ReplayChangeset(pathMapper, changeset, git, labels);
                    }
                    finally
                    {
                        replayStopwatch.Stop();
                    }

                    if (workQueue.IsAborting)
                    {
                        return;
                    }

                    // commit changes
                    if (needCommit)
                    {
                        LogStatus(work, "Committing " + changesetDesc);
                        if (CommitChangeset(git, changeset))
                        {
                            ++commitCount;
                        }
                    }

                    if (workQueue.IsAborting)
                    {
                        return;
                    }

                    // create tags for any labels in the changeset
                    if (labels.Count > 0)
                    {
                        foreach (Revision label in labels)
                        {
                            var labelName = ((VssLabelAction)label.Action).Label;
                            LogStatus(work, "Creating tag " + labelName);
                            if (AbortRetryIgnore(
                                delegate { git.Tag(labelName, label.Comment); }))
                            {
                                ++tagCount;
                            }
                        }
                    }

                    ++changesetId;
                }

                stopwatch.Stop();

                logger.WriteSectionSeparator();
                logger.WriteLine("Git export complete in {0:HH:mm:ss}", new DateTime(stopwatch.ElapsedTicks));
                logger.WriteLine("Replay time: {0:HH:mm:ss}", new DateTime(replayStopwatch.ElapsedTicks));
                logger.WriteLine("Git time: {0:HH:mm:ss}", new DateTime(git.ElapsedTime.Ticks));
                logger.WriteLine("Git commits: {0}", commitCount);
                logger.WriteLine("Git tags: {0}", tagCount);
            });
        }

        private bool ReplayChangeset(VssPathMapper pathMapper, Changeset changeset,
            GitWrapper git, LinkedList<Revision> labels)
        {
            var needCommit = false;
            foreach (Revision revision in changeset.Revisions)
            {
                if (workQueue.IsAborting)
                {
                    break;
                }

                AbortRetryIgnore(delegate
                {
                    needCommit |= ReplayRevision(pathMapper, revision, git, labels);
                });
            }
            return needCommit;
        }

        private bool ReplayRevision(VssPathMapper pathMapper, Revision revision,
            GitWrapper git, LinkedList<Revision> labels)
        {
            var needCommit = false;
            var actionType = revision.Action.Type;
            if (revision.Item.IsProject)
            {
                // note that project path (and therefore target path) can be
                // null if a project was moved and its original location was
                // subsequently destroyed
                var project = revision.Item;
                var projectName = project.LogicalName;
                var projectPath = pathMapper.GetProjectPath(project.PhysicalName);
                var projectDesc = projectPath;
                if (projectPath == null)
                {
                    projectDesc = revision.Item.ToString();
                    logger.WriteLine("NOTE: {0} is currently unmapped", project);
                }

                VssItemName target = null;
                string targetPath = null;
                var namedAction = revision.Action as VssNamedAction;
                if (namedAction != null)
                {
                    target = namedAction.Name;
                    if (projectPath != null)
                    {
                        targetPath = Path.Combine(projectPath, target.LogicalName);
                    }
                }

                bool writeProject = false;
                bool writeFile = false;
                switch (actionType)
                {
                    case VssActionType.Label:
                        // defer tagging until after commit
                        labels.AddLast(revision);
                        break;

                    case VssActionType.Create:
                        // ignored; should be filtered out by RevisionAnalyzer
                        break;

                    case VssActionType.Add:
                    case VssActionType.Share:
                    case VssActionType.Recover:
                        logger.WriteLine("{0}: {1} {2}", projectDesc, actionType, target.LogicalName);
                        if (actionType == VssActionType.Recover)
                        {
                            pathMapper.RecoverItem(project, target);
                        }
                        else
                        {
                            pathMapper.AddItem(project, target);
                        }
                        if (targetPath != null)
                        {
                            if (target.IsProject)
                            {
                                Directory.CreateDirectory(targetPath);
                                writeProject = true;
                            }
                            else
                            {
                                writeFile = true;
                            }
                        }
                        break;

                    case VssActionType.Delete:
                    case VssActionType.Destroy:
                        {
                            logger.WriteLine("{0}: {1} {2}", projectDesc, actionType, target.LogicalName);
                            var itemInfo = pathMapper.DeleteItem(project, target);
                            if (targetPath != null)
                            {
                                if (target.IsProject)
                                {
                                    if (Directory.Exists(targetPath))
                                    {
                                        if (((VssProjectInfo)itemInfo).ContainsFiles())
                                        {
                                            git.Remove(targetPath, true);
                                            needCommit = true;
                                        }
                                        else
                                        {
                                            // git doesn't care about directories with no files
                                            Directory.Delete(targetPath, true);
                                        }
                                    }
                                }
                                else
                                {
                                    if (File.Exists(targetPath))
                                    {
                                        File.Delete(targetPath);
                                        needCommit = true;
                                    }
                                }
                            }
                        }
                        break;

                    case VssActionType.Rename:
                        {
                            var renameAction = (VssRenameAction)revision.Action;
                            logger.WriteLine("{0}: {1} {2} to {3}",
                                projectDesc, actionType, renameAction.OriginalName, target.LogicalName);
                            var itemInfo = pathMapper.RenameItem(target);
                            if (targetPath != null)
                            {
                                var sourcePath = Path.Combine(projectPath, renameAction.OriginalName);
                                var projectInfo = itemInfo as VssProjectInfo;
                                if (projectInfo == null || projectInfo.ContainsFiles())
                                {
                                    git.Move(sourcePath, targetPath);
                                    needCommit = true;
                                }
                                else
                                {
                                    // git doesn't care about directories with no files
                                    Directory.Move(sourcePath, targetPath);
                                }
                            }
                        }
                        break;

                    case VssActionType.MoveFrom:
                        // if both MoveFrom & MoveTo are present (e.g.
                        // one of them has not been destroyed), only one
                        // can succeed, so check that the source exists
                        {
                            var moveFromAction = (VssMoveFromAction)revision.Action;
                            logger.WriteLine("{0}: Move {1} to {2}",
                                projectDesc, moveFromAction.OriginalProject, target.LogicalName);
                            var sourcePath = pathMapper.GetProjectPath(target.PhysicalName);
                            var projectInfo = pathMapper.MoveProjectFrom(
                                project, target, moveFromAction.OriginalProject);
                            if (sourcePath != null)
                            {
                                if (targetPath != null && Directory.Exists(sourcePath))
                                {
                                    if (projectInfo.ContainsFiles())
                                    {
                                        git.Move(sourcePath, targetPath);
                                        needCommit = true;
                                    }
                                    else
                                    {
                                        // git doesn't care about directories with no files
                                        Directory.Move(sourcePath, targetPath);
                                    }
                                }
                            }
                            else
                            {
                                // project was moved from a now-destroyed project
                                writeProject = true;
                            }
                        }
                        break;

                    case VssActionType.MoveTo:
                        // currently ignored; rely on MoveFrom
                        {
                            var moveToAction = (VssMoveToAction)revision.Action;
                            logger.WriteLine("{0}: Move {1} to {2} (ignored)",
                                projectDesc, target.LogicalName, moveToAction.NewProject);
                        }
                        break;

                    case VssActionType.Pin:
                        {
                            var pinAction = (VssPinAction)revision.Action;
                            if (pinAction.Pinned)
                            {
                                logger.WriteLine("{0}: Pin {1}", projectDesc, target.LogicalName);
                                pathMapper.PinItem(project, target);
                            }
                            else
                            {
                                logger.WriteLine("{0}: Unpin {1}", projectDesc, target.LogicalName);
                                pathMapper.UnpinItem(project, target);
                                writeFile = true;
                            }
                        }
                        break;

                    case VssActionType.Branch:
                        {
                            var branchAction = (VssBranchAction)revision.Action;
                            logger.WriteLine("{0}: {1} {2}", projectDesc, actionType, target.LogicalName);
                            pathMapper.BranchFile(project, target, branchAction.Source);
                        }
                        break;

                    case VssActionType.Archive:
                        // currently ignored
                        {
                            var archiveAction = (VssArchiveAction)revision.Action;
                            logger.WriteLine("{0}: Archive {1} to {2} (ignored)",
                                projectDesc, target.LogicalName, archiveAction.ArchivePath);
                        }
                        break;

                    case VssActionType.Restore:
                        // currently ignored
                        {
                            var restoreAction = (VssRestoreAction)revision.Action;
                            logger.WriteLine("{0}: Restore {1} from archive {2} (ignored)",
                                projectDesc, target.LogicalName, restoreAction.ArchivePath);
                        }
                        break;
                }

                if (targetPath != null)
                {
                    if (writeProject && pathMapper.IsProjectRooted(target.PhysicalName))
                    {
                        // write current rev of all contained files
                        foreach (var fileInfo in pathMapper.GetAllFiles(target.PhysicalName))
                        {
                            WriteRevision(pathMapper, actionType, fileInfo.PhysicalName,
                                fileInfo.Version, target.PhysicalName, git);
                            needCommit = true;
                        }
                    }
                    else if (writeFile)
                    {
                        // write current rev to working path
                        int version = pathMapper.GetFileVersion(target.PhysicalName);
                        WriteRevisionTo(target.PhysicalName, version, targetPath);
                        needCommit = true;
                    }
                }
            }
            else if (actionType == VssActionType.Edit)
            {
                var target = revision.Item;

                // update current rev
                pathMapper.SetFileVersion(target, revision.Version);

                // write current rev to all sharing projects
                WriteRevision(pathMapper, actionType, target.PhysicalName,
                    revision.Version, null, git);
                needCommit = true;
            }
            return needCommit;
        }

        private bool CommitChangeset(GitWrapper git, Changeset changeset)
        {
            var result = false;
            AbortRetryIgnore(delegate
            {
                result = git.AddAll() &&
                    git.Commit(changeset.User, GetEmail(changeset.User),
                    changeset.Comment ?? DefaultComment, changeset.DateTime);
            });
            return result;
        }

        private bool AbortRetryIgnore(ThreadStart work)
        {
            int tries = 0;
            bool retry;
            do
            {
                ++tries;
                try
                {
                    work();
                    return true;
                }
                catch (Exception e)
                {
                    var message = ExceptionFormatter.Format(e);
                    logger.WriteLine("ERROR: {0}", message);

                    var button = MessageBox.Show(message, "Error",
                        MessageBoxButtons.AbortRetryIgnore, MessageBoxIcon.Error);
                    switch (button)
                    {
                        case DialogResult.Retry:
                            retry = true;
                            break;
                        case DialogResult.Ignore:
                            retry = false;
                            break;
                        default:
                            retry = false;
                            workQueue.Abort();
                            break;
                    }
                }
            } while (retry);
            return false;
        }

        private string GetEmail(string user)
        {
            // TODO: user-defined mapping of user names to email addresses
            return user.ToLower().Replace(' ', '.') + "@" + emailDomain;
        }

        private void WriteRevision(VssPathMapper pathMapper, VssActionType actionType,
            string physicalName, int version, string underProject, GitWrapper git)
        {
            var paths = pathMapper.GetFilePaths(physicalName, underProject);
            foreach (string path in paths)
            {
                logger.WriteLine("{0}: {1} revision {2}", path, actionType, version);
                WriteRevisionTo(physicalName, version, path);
            }
        }

        private void WriteRevisionTo(string physical, int version, string destPath)
        {
            // check for destroyed files
            if (revisionAnalyzer.IsDestroyed(physical) && !database.ItemExists(physical))
            {
                logger.WriteLine("NOTE: Skipping destroyed file: {0}", destPath);
                return;
            }

            VssFile item;
            VssFileRevision revision;
            Stream contents;
            try
            {
                item = (VssFile)database.GetItemPhysical(physical);
                revision = item.GetRevision(version);
                contents = revision.GetContents();
            }
            catch (Exception e)
            {
                // log an error for missing data files or versions, but keep processing
                logger.WriteLine("ERROR: {0}", e.Message);
                return;
            }

            // propagate exceptions here (e.g. disk full) to abort/retry/ignore
            using (contents)
            {
                WriteStream(contents, destPath);
            }

            // try to use the first revision (for this branch) as the create time,
            // since the item creation time doesn't seem to be meaningful
            var createDateTime = item.Created;
            using (var revEnum = item.Revisions.GetEnumerator())
            {
                if (revEnum.MoveNext())
                {
                    createDateTime = revEnum.Current.DateTime;
                }
            }

            // set file creation and update timestamps
            File.SetCreationTimeUtc(destPath, TimeZoneInfo.ConvertTimeToUtc(createDateTime));
            File.SetLastWriteTimeUtc(destPath, TimeZoneInfo.ConvertTimeToUtc(revision.DateTime));
        }

        private void WriteStream(Stream inputStream, string path)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));

            using (var outputStream = new FileStream(
                path, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                streamCopier.Copy(inputStream, outputStream);
            }
        }
    }
}
