using System;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Shell;

namespace BranchedTabs
{
    internal sealed class GitBranchContext : IDisposable
    {
        private readonly AsyncPackage _package;
        private FileSystemWatcher _headWatcher;
        private string _headDirectory;

        public GitBranchContext(AsyncPackage package)
        {
            _package = package;
        }

        public string SolutionPath { get; private set; }

        public string GitDirectory { get; private set; }

        public string CurrentBranch { get; private set; }

        public bool HasGitRepository => !string.IsNullOrWhiteSpace(GitDirectory);

        public event EventHandler<BranchChangedEventArgs> BranchChanged;

        public void InitializeForSolution(string solutionPath)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            StopWatching();

            SolutionPath = solutionPath;
            GitDirectory = ResolveGitDirectory(solutionPath);
            CurrentBranch = GetCurrentBranchName();

            BranchedTabsPackage.Trace($"Git context initialized. Solution='{SolutionPath}', GitDirectory='{GitDirectory}', Branch='{CurrentBranch}'.");

            if (!string.IsNullOrWhiteSpace(GitDirectory))
            {
                StartWatching();
            }
        }

        public void ClearSolution()
        {
            StopWatching();
            SolutionPath = null;
            GitDirectory = null;
            CurrentBranch = null;
        }

        public string RefreshCurrentBranch()
        {
            CurrentBranch = GetCurrentBranchName();
            return CurrentBranch;
        }

        public void Dispose()
        {
            StopWatching();
        }

        private void StartWatching()
        {
            var headPath = Path.Combine(GitDirectory, "HEAD");
            if (!File.Exists(headPath))
            {
                BranchedTabsPackage.Trace($"Git HEAD file not found at '{headPath}'.");
                return;
            }

            _headDirectory = Path.GetDirectoryName(headPath);
            if (string.IsNullOrWhiteSpace(_headDirectory))
            {
                return;
            }

            _headWatcher = new FileSystemWatcher(_headDirectory, "HEAD")
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName | NotifyFilters.Attributes,
                EnableRaisingEvents = true
            };

            _headWatcher.Changed += OnHeadFileChanged;
            _headWatcher.Created += OnHeadFileChanged;
            _headWatcher.Renamed += OnHeadFileChanged;

            BranchedTabsPackage.Trace($"Watching Git HEAD file in '{_headDirectory}'.");
        }

        private void StopWatching()
        {
            if (_headWatcher == null)
            {
                return;
            }

            _headWatcher.EnableRaisingEvents = false;
            _headWatcher.Changed -= OnHeadFileChanged;
            _headWatcher.Created -= OnHeadFileChanged;
            _headWatcher.Renamed -= OnHeadFileChanged;
            _headWatcher.Dispose();
            _headWatcher = null;
            _headDirectory = null;
        }

        private void OnHeadFileChanged(object sender, FileSystemEventArgs e)
        {
            ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
            {
                try
                {
                    await Task.Delay(200);
                    await _package.JoinableTaskFactory.SwitchToMainThreadAsync();

                    var previousBranch = CurrentBranch;
                    var newBranch = GetCurrentBranchName();
                    if (string.Equals(previousBranch, newBranch, StringComparison.OrdinalIgnoreCase))
                    {
                        return;
                    }

                    CurrentBranch = newBranch;
                    BranchedTabsPackage.Trace($"Git HEAD changed from '{previousBranch}' to '{newBranch}'.");
                    BranchChanged?.Invoke(this, new BranchChangedEventArgs(previousBranch, newBranch));
                }
                catch (Exception ex)
                {
                    BranchedTabsPackage.TraceError("Git branch watcher failed while handling a HEAD change.", ex);
                }
            });
        }

        private string GetCurrentBranchName()
        {
            if (string.IsNullOrWhiteSpace(GitDirectory))
            {
                return null;
            }

            var headFile = Path.Combine(GitDirectory, "HEAD");
            if (!File.Exists(headFile))
            {
                return null;
            }

            for (var attempt = 0; attempt < 3; attempt++)
            {
                try
                {
                    var content = File.ReadAllText(headFile).Trim();
                    var match = Regex.Match(content, @"ref:\s+refs/heads/(.+)");
                    if (match.Success)
                    {
                        return match.Groups[1].Value.Trim();
                    }

                    if (!string.IsNullOrWhiteSpace(content))
                    {
                        return $"detached:{content}";
                    }
                }
                catch (IOException)
                {
                    System.Threading.Thread.Sleep(100);
                }
            }

            return null;
        }

        private static string ResolveGitDirectory(string startPath)
        {
            if (string.IsNullOrWhiteSpace(startPath))
            {
                return null;
            }

            var directory = new DirectoryInfo(startPath);
            while (directory != null)
            {
                var gitPath = Path.Combine(directory.FullName, ".git");
                if (Directory.Exists(gitPath))
                {
                    return gitPath;
                }

                if (File.Exists(gitPath))
                {
                    var resolvedPath = TryResolveGitDirectoryFromFile(gitPath, directory.FullName);
                    if (!string.IsNullOrWhiteSpace(resolvedPath))
                    {
                        return resolvedPath;
                    }
                }

                directory = directory.Parent;
            }

            return null;
        }

        private static string TryResolveGitDirectoryFromFile(string gitFilePath, string workingDirectory)
        {
            try
            {
                var content = File.ReadAllText(gitFilePath).Trim();
                const string gitDirPrefix = "gitdir:";
                if (!content.StartsWith(gitDirPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    return null;
                }

                var gitDirectory = content.Substring(gitDirPrefix.Length).Trim();
                if (Path.IsPathRooted(gitDirectory))
                {
                    return Directory.Exists(gitDirectory) ? gitDirectory : null;
                }

                var combinedPath = Path.GetFullPath(Path.Combine(workingDirectory, gitDirectory));
                return Directory.Exists(combinedPath) ? combinedPath : null;
            }
            catch
            {
                return null;
            }
        }
    }

    internal sealed class BranchChangedEventArgs : EventArgs
    {
        public BranchChangedEventArgs(string previousBranch, string currentBranch)
        {
            PreviousBranch = previousBranch;
            CurrentBranch = currentBranch;
        }

        public string PreviousBranch { get; }

        public string CurrentBranch { get; }
    }
}
