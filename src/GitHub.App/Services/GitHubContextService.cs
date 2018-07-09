﻿using System;
using System.IO;
using System.Text;
using System.Linq;
using System.Windows;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Text.RegularExpressions;
using System.Runtime.InteropServices;
using GitHub.Services;
using GitHub.Extensions;
using GitHub.Primitives;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.TextManager.Interop;
using LibGit2Sharp;
using Task = System.Threading.Tasks.Task;

namespace GitHub.App.Services
{
    [Export(typeof(IGitHubContextService))]
    public class GitHubContextService : IGitHubContextService
    {
        readonly IGitHubServiceProvider serviceProvider;
        readonly IGitService gitService;
        readonly Lazy<IVsTextManager2> textManager;

        // USERID_REGEX = /[a-z0-9][a-z0-9\-\_]*/i
        const string owner = "(?<owner>[a-zA-Z0-9][a-zA-Z0-9-_]*)";

        // REPO_REGEX = /(?:\w|\.|\-)+/i
        // This supports "_" for legacy superfans with logins that still contain "_".
        const string repo = @"(?<repo>(?:\w|\.|\-)+)";

        //BRANCH_REGEX = /[^\/]+(\/[^\/]+)?/
        const string branch = @"(?<branch>[^./ ~^:?*\[\\][^/ ~^:?*\[\\]*(/[^./ ~^:?*\[\\][^/ ~^:?*\[\\]*)*)";

        const string pull = "(?<pull>[0-9]+)";

        const string issue = "(?<issue>[0-9]+)";

        static readonly string tree = $"^{repo}/(?<tree>[^ ]+)";
        static readonly string blobName = $"^{repo}/(?<blobName>[^ /]+)";

        static readonly Regex windowTitleRepositoryRegex = new Regex($"^(GitHub - )?{owner}/{repo}(: .*)? - ", RegexOptions.Compiled);
        static readonly Regex windowTitleBranchRegex = new Regex($"^(GitHub - )?{owner}/{repo} at {branch} ", RegexOptions.Compiled);
        static readonly Regex windowTitlePullRequestRegex = new Regex($" · Pull Request #{pull} · {owner}/{repo}( · GitHub)? - ", RegexOptions.Compiled);
        static readonly Regex windowTitleIssueRegex = new Regex($" · Issue #{issue} · {owner}/{repo}( · GitHub)? - ", RegexOptions.Compiled);
        static readonly Regex windowTitleBlobRegex = new Regex($"{blobName} at {branch} · {owner}/{repo}( · GitHub)? - ", RegexOptions.Compiled);
        static readonly Regex windowTitleTreeRegex = new Regex($"{tree} at {branch} · {owner}/{repo}( · GitHub)? - ", RegexOptions.Compiled);
        static readonly Regex windowTitleBranchesRegex = new Regex($"Branches · {owner}/{repo}( · GitHub)? - ", RegexOptions.Compiled);

        static readonly Regex urlLineRegex = new Regex($"#L(?<line>[0-9]+)(-L(?<lineEnd>[0-9]+))?$", RegexOptions.Compiled);
        static readonly Regex urlBlobRegex = new Regex($"blob/(?<treeish>[^/]+(/[^/]+)*)/(?<blobName>[^/#]+)", RegexOptions.Compiled);

        static readonly Regex treeishCommitRegex = new Regex($"(?<commit>[a-z0-9]{{40}})(/(?<tree>.+))?", RegexOptions.Compiled);
        static readonly Regex treeishBranchRegex = new Regex($"(?<branch>master)(/(?<tree>.+))?", RegexOptions.Compiled);

        [ImportingConstructor]
        public GitHubContextService(IGitHubServiceProvider serviceProvider, IGitService gitService)
        {
            this.serviceProvider = serviceProvider;
            this.gitService = gitService;
            textManager = new Lazy<IVsTextManager2>(() => serviceProvider.GetService<SVsTextManager, IVsTextManager2>());
        }

        public GitHubContext FindContextFromClipboard()
        {
            var text = Clipboard.GetText(TextDataFormat.Text);
            return FindContextFromUrl(text);
        }

        public GitHubContext FindContextFromUrl(string url)
        {
            var uri = new UriString(url);
            if (!uri.IsValidUri)
            {
                return null;
            }

            if (!uri.IsHypertextTransferProtocol)
            {
                return null;
            }

            var context = new GitHubContext
            {
                Host = uri.Host,
                Owner = uri.Owner,
                RepositoryName = uri.RepositoryName,
            };

            var repositoryPrefix = uri.ToRepositoryUrl().ToString() + "/";
            if (!url.StartsWith(repositoryPrefix, StringComparison.OrdinalIgnoreCase))
            {
                return context;
            }

            var subpath = url.Substring(repositoryPrefix.Length);

            (context.Line, context.LineEnd) = FindLine(subpath);

            context.PullRequest = FindPullRequest(url);

            var match = urlBlobRegex.Match(subpath);
            if (match.Success)
            {
                context.TreeishPath = match.Groups["treeish"].Value;
                context.BlobName = match.Groups["blobName"].Value;
                return context;
            }

            return context;
        }

        public GitHubContext FindContextFromBrowser()
        {
            return
                FindWindowTitlesForClass("Chrome_WidgetWin_1")              // Chrome
                .Concat(FindWindowTitlesForClass("MozillaWindowClass"))     // Firefox
                .Select(FindContextFromWindowTitle).Where(x => x != null)
                .FirstOrDefault();
        }

        public IEnumerable<string> FindWindowTitlesForClass(string className = "MozillaWindowClass")
        {
            IntPtr handleWin = IntPtr.Zero;
            while (IntPtr.Zero != (handleWin = User32.FindWindowEx(IntPtr.Zero, handleWin, className, IntPtr.Zero)))
            {
                // Allocate correct string length first
                int length = User32.GetWindowTextLength(handleWin);
                if (length == 0)
                {
                    continue;
                }

                var titleBuilder = new StringBuilder(length + 1);
                User32.GetWindowText(handleWin, titleBuilder, titleBuilder.Capacity);
                yield return titleBuilder.ToString();
            }
        }

        public Uri ToRepositoryUrl(GitHubContext context)
        {
            var builder = new UriBuilder("https", context.Host ?? "github.com");
            builder.Path = $"{context.Owner}/{context.RepositoryName}";
            return builder.Uri;
        }

        public GitHubContext FindContextFromWindowTitle(string windowTitle)
        {
            var match = windowTitleBlobRegex.Match(windowTitle);
            if (match.Success)
            {
                return new GitHubContext
                {
                    Owner = match.Groups["owner"].Value,
                    RepositoryName = match.Groups["repo"].Value,
                    BranchName = match.Groups["branch"].Value,
                    BlobName = match.Groups["blobName"].Value
                };
            }

            match = windowTitleTreeRegex.Match(windowTitle);
            if (match.Success)
            {
                return new GitHubContext
                {
                    Owner = match.Groups["owner"].Value,
                    RepositoryName = match.Groups["repo"].Value,
                    BranchName = match.Groups["branch"].Value,
                    TreeishPath = $"{match.Groups["branch"].Value}/{match.Groups["tree"].Value}"
                };
            }

            match = windowTitleRepositoryRegex.Match(windowTitle);
            if (match.Success)
            {
                return new GitHubContext
                {
                    Owner = match.Groups["owner"].Value,
                    RepositoryName = match.Groups["repo"].Value,
                };
            }

            match = windowTitleBranchRegex.Match(windowTitle);
            if (match.Success)
            {
                return new GitHubContext
                {
                    Owner = match.Groups["owner"].Value,
                    RepositoryName = match.Groups["repo"].Value,
                    BranchName = match.Groups["branch"].Value,
                };
            }

            match = windowTitleBranchesRegex.Match(windowTitle);
            if (match.Success)
            {
                return new GitHubContext
                {
                    Owner = match.Groups["owner"].Value,
                    RepositoryName = match.Groups["repo"].Value
                };
            }

            match = windowTitlePullRequestRegex.Match(windowTitle);
            if (match.Success)
            {
                int.TryParse(match.Groups["pull"].Value, out int pullRequest);

                return new GitHubContext
                {
                    Owner = match.Groups["owner"].Value,
                    RepositoryName = match.Groups["repo"].Value,
                    PullRequest = pullRequest
                };
            }

            match = windowTitleIssueRegex.Match(windowTitle);
            if (match.Success)
            {
                int.TryParse(match.Groups["issue"].Value, out int issue);

                return new GitHubContext
                {
                    Owner = match.Groups["owner"].Value,
                    RepositoryName = match.Groups["repo"].Value,
                    Issue = issue
                };
            }

            return null;
        }

        public bool TryOpenFile(string repositoryDir, GitHubContext context)
        {
            var (commitish, path, isSha) = ResolveBlob(repositoryDir, context);
            if (path == null)
            {
                return false;
            }

            var fullPath = Path.Combine(repositoryDir, path.Replace('/', '\\'));
            var textView = OpenDocument(fullPath);
            SetSelection(textView, context);
            return true;
        }

        public (string commitish, string path, bool isSha) ResolveBlob(string repositoryDir, GitHubContext context, string remoteName = "origin")
        {
            Guard.ArgumentNotNull(repositoryDir, nameof(repositoryDir));
            Guard.ArgumentNotNull(context, nameof(context));

            using (var repository = gitService.GetRepository(repositoryDir))
            {
                if (context.TreeishPath == null)
                {
                    // Blobs without a TreeishPath aren't currently supported
                    return (null, null, false);
                }

                if (context.BlobName == null)
                {
                    // Not a blob
                    return (null, null, false);
                }

                var objectishPath = $"{context.TreeishPath}/{context.BlobName}";
                var objectish = ToObjectish(objectishPath);
                var (commitSha, pathSha) = objectish.First();
                if (ObjectId.TryParse(commitSha, out ObjectId objectId) && repository.Lookup(objectId) != null)
                {
                    if (repository.Lookup($"{commitSha}:{pathSha}") != null)
                    {
                        return (commitSha, pathSha, true);
                    }
                }

                foreach (var (commitish, path) in objectish)
                {
                    var remoteRef = $"refs/remotes/{remoteName}/{commitish}";
                    if (repository.Lookup(remoteRef) == null)
                    {
                        continue;
                    }

                    if (repository.Lookup($"{remoteRef}:{path}") == null)
                    {
                        // Resolved commitish but not path
                        return (remoteRef, null, false);
                    }

                    return (remoteRef, path, false);
                }

                return (null, null, false);
            }

            IEnumerable<(string commitish, string path)> ToObjectish(string treeishPath)
            {
                var index = 0;
                while ((index = treeishPath.IndexOf('/', index + 1)) != -1)
                {
                    var commitish = treeishPath.Substring(0, index);
                    var path = treeishPath.Substring(index + 1);
                    yield return (commitish, path);
                }
            }
        }

        public bool HasChangesInWorkingDirectory(string repositoryDir, string commitish, string path)
        {
            using (var repo = gitService.GetRepository(repositoryDir))
            {
                var commit = repo.Lookup<Commit>(commitish);
                var paths = new[] { path };

                return repo.Diff.Compare<Patch>(commit.Tree, DiffTargets.WorkingDirectory, paths).Count() > 0;
            }
        }

        public async Task<bool> TryAnnotateFile(string repositoryDir, string currentBranch, GitHubContext context)
        {
            var (commitish, path, isSha) = ResolveBlob(repositoryDir, context);
            if (path == null)
            {
                return false;
            }

            if (!AnnotateFile(repositoryDir, currentBranch, path, commitish))
            {
                return false;
            }

            if (context.Line != null)
            {
                await Task.Delay(1000);
                var activeView = FindActiveView();
                SetSelection(activeView, context);
            }

            return true;
        }

        IVsTextView FindActiveView()
        {
            if (!ErrorHandler.Succeeded(textManager.Value.GetActiveView2(1, null, (uint)_VIEWFRAMETYPE.vftToolWindow, out IVsTextView textView)))
            {
                return null;
            }

            return textView;
        }

        bool AnnotateFile(string repositoryPath, string branchName, string relativePath, string versionSha)
        {
            var gitExt2Type = Type.GetType("Microsoft.VisualStudio.TeamFoundation.Git.Extensibility.IGitExt2, Microsoft.TeamFoundation.Git.Provider", false);
            if (gitExt2Type == null)
            {
                return false;
            }

            InvokeService(gitExt2Type, "AnnotateFile", repositoryPath, branchName, relativePath, versionSha);
            return true;
        }

        void InvokeService<T1, T2, T3, T4>(Type serviceType, string method, T1 arg1, T2 arg2, T3 arg3, T4 arg4)
        {
            var service = serviceProvider.GetService(serviceType);
            var action = (Action<T1, T2, T3, T4>)Delegate.CreateDelegate(typeof(Action<T1, T2, T3, T4>), service, method);
            action.Invoke(arg1, arg2, arg3, arg4);
        }

        static void SetSelection(IVsTextView textView, GitHubContext context)
        {
            var line = context.Line;
            var lineEnd = context.LineEnd ?? line;

            if (line != null)
            {
                ErrorHandler.ThrowOnFailure(textView.GetBuffer(out IVsTextLines buffer));
                buffer.GetLengthOfLine(lineEnd.Value - 1, out int lineEndLength);
                ErrorHandler.ThrowOnFailure(textView.SetSelection(line.Value - 1, 0, lineEnd.Value - 1, lineEndLength));
                ErrorHandler.ThrowOnFailure(textView.CenterLines(line.Value - 1, lineEnd.Value - line.Value + 1));
            }
        }

        IVsTextView OpenDocument(string fullPath)
        {
            var logicalView = VSConstants.LOGVIEWID.TextView_guid;
            IVsUIHierarchy hierarchy;
            uint itemID;
            IVsWindowFrame windowFrame;
            IVsTextView view;
            VsShellUtilities.OpenDocument(serviceProvider, fullPath, logicalView, out hierarchy, out itemID, out windowFrame, out view);
            return view;
        }

        static (int? lineStart, int? lineEnd) FindLine(UriString gitHubUrl)
        {
            var url = gitHubUrl.ToString();

            var match = urlLineRegex.Match(url);
            if (match.Success)
            {
                int.TryParse(match.Groups["line"].Value, out int line);

                var lineEndGroup = match.Groups["lineEnd"];
                if (string.IsNullOrEmpty(lineEndGroup.Value))
                {
                    return (line, null);
                }

                int.TryParse(lineEndGroup.Value, out int lineEnd);
                return (line, lineEnd);
            }

            return (null, null);
        }

        static int? FindPullRequest(UriString gitHubUrl)
        {
            var pullRequest = FindSubPath(gitHubUrl, "/pull/")?.Split('/').First();
            if (pullRequest == null)
            {
                return null;
            }

            if (!int.TryParse(pullRequest, out int number))
            {
                return null;
            }

            return number;
        }

        static string FindSubPath(UriString gitHubUrl, string matchPath)
        {
            var url = gitHubUrl.ToString();
            var prefix = gitHubUrl.ToRepositoryUrl() + matchPath;
            if (!url.StartsWith(prefix))
            {
                return null;
            }

            var endIndex = url.IndexOf('#');
            if (endIndex == -1)
            {
                endIndex = gitHubUrl.Length;
            }

            var path = url.Substring(prefix.Length, endIndex - prefix.Length);
            return path;
        }

        static class User32
        {
            [DllImport("user32.dll", SetLastError = true)]
            internal static extern IntPtr FindWindowEx(IntPtr parentHandle, IntPtr childAfter, string className, IntPtr windowTitle);

            [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
            internal static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

            [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
            internal static extern int GetWindowTextLength(IntPtr hWnd);
        }
    }
}
