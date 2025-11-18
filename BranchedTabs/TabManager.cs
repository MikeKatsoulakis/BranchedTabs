using EnvDTE;
using EnvDTE80;
using Microsoft;
using Microsoft.VisualStudio.Shell;
using Newtonsoft.Json;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace BranchedTabs
{

    public class TabManager
    {
        private readonly AsyncPackage _package;
        private DTE2 _dte;
        private string _solutionPath;

        private FileSystemWatcher _headWatcher;
        private string _currentBranch;
        private string _gitPath;

        private readonly HashSet<string> _currentlyOpenFiles = new HashSet<string>();
        private DocumentEvents _documentEvents;

        private Dictionary<string, List<string>> _branchFileMap = new Dictionary<string, List<string>>();

        private const string SaveFileName = "BranchTabs.json";

        private TabManagerOptions Options =>
            (TabManagerOptions)_package.GetDialogPage(typeof(TabManagerOptions));

        public TabManager(AsyncPackage package)
        {
            _package = package;
        }

        public async Task InitializeAsync()
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

            _dte = await _package.GetServiceAsync(typeof(DTE)) as DTE2;
            Assumes.Present(_dte);

            _documentEvents = _dte.Events.get_DocumentEvents();
            _documentEvents.DocumentOpened += OnDocumentOpened;
            _documentEvents.DocumentClosing += OnDocumentClosing;

            _dte.Events.SolutionEvents.Opened += OnSolutionOpened;
            _dte.Events.SolutionEvents.AfterClosing += OnSolutionClosed;

            if (_dte.Solution.IsOpen)
            {
                OnSolutionOpened();
            }
        }


        /// <summary>
        /// File System Watcher for Branch Changes
        /// </summary>

        private void StartWatchingBranchChanges()
        {
            if (string.IsNullOrEmpty(_gitPath)) return;

            _headWatcher = new FileSystemWatcher();

            _headWatcher.Path = _gitPath;
            _headWatcher.Filter = "HEAD";
            _headWatcher.NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName | NotifyFilters.Attributes;

            _headWatcher.Changed += OnHeadFileChanged;
            _headWatcher.Renamed += OnHeadFileChanged;
            _headWatcher.Created += OnHeadFileChanged;

            _headWatcher.EnableRaisingEvents = true;
        }

        private void StopWatchingBranchChanges()
        {
            if (_headWatcher != null)
            {
                _headWatcher.EnableRaisingEvents = false;
                _headWatcher.Dispose();
                _headWatcher = null;
            }
        }


        /// <summary>
        /// Event Handlers
        /// </summary>

        private void OnDocumentOpened(Document document)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (!string.IsNullOrEmpty(document?.FullName) && File.Exists(document.FullName))
            {
                _currentlyOpenFiles.Add(document.FullName);
            }
        }

        private void OnDocumentClosing(Document document)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (!string.IsNullOrEmpty(document?.FullName))
            {
                _currentlyOpenFiles.Remove(document.FullName);
            }
        }

        private void OnSolutionOpened()
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            _solutionPath = Path.GetDirectoryName(_dte.Solution.FullName);
            if (!IsFeatureEnabled() || string.IsNullOrEmpty(_solutionPath))
                return;

            _gitPath = FindGitDir(_solutionPath);
            if (string.IsNullOrEmpty(_gitPath)) return;

            LoadState();

            _currentBranch = GetCurrentBranch();

            _currentlyOpenFiles.Clear();
            foreach (Document doc in _dte.Documents)
            {
                if (!string.IsNullOrWhiteSpace(doc.FullName) && File.Exists(doc.FullName))
                {
                    _currentlyOpenFiles.Add(doc.FullName);
                }
            }

            RestoreTabsForCurrentBranch();
            StartWatchingBranchChanges();
        }

        private void OnSolutionClosed()
        {
            var path = _solutionPath;
            _solutionPath = null;

            if (!IsFeatureEnabled() || string.IsNullOrEmpty(path))
                return;

            StopWatchingBranchChanges();

            _ = Task.Run(() =>
            {
                ThreadHelper.JoinableTaskFactory.Run(async () =>
                {
                    await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                    SaveTabsForCurrentBranch();
                });
            });
        }

        private void OnHeadFileChanged(object sender, FileSystemEventArgs e)
        {
            ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
            {
                await Task.Delay(200);
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

                var newBranch = GetCurrentBranch();
                if (newBranch != null && newBranch != _currentBranch)
                {
                    SaveTabsForCurrentBranch();
                    _currentBranch = newBranch;
                    RestoreTabsForCurrentBranch();
                }
            });
        }


        /// <summary>
        /// Helper Methods
        /// </summary>>

        private bool IsFeatureEnabled()
        {
            var options = (TabManagerOptions)_package.GetDialogPage(typeof(TabManagerOptions));
            return options.EnableBranchTabs;
        }

        private string GetCurrentBranch()
        {
            if (string.IsNullOrEmpty(_gitPath)) return null;

            var headFile = Path.Combine(_gitPath, "HEAD");
            if (!File.Exists(headFile)) return null;

            for (int i = 0; i < 3; i++)
            {
                try
                {
                    var content = File.ReadAllText(headFile);
                    var match = Regex.Match(content, @"ref:\srefs/heads/(.+)");
                    if (match.Success) return match.Groups[1].Value.Trim();
                    
                    // Handle detached HEAD or other states if needed, for now return null or the hash
                    return null; 
                }
                catch (IOException)
                {
                    System.Threading.Thread.Sleep(100);
                }
            }
            return null;
        }

        private string FindGitDir(string startPath)
        {
            var dir = new DirectoryInfo(startPath);
            while (dir != null)
            {
                var gitDir = Path.Combine(dir.FullName, ".git");
                if (Directory.Exists(gitDir)) return gitDir;
                dir = dir.Parent;
            }
            return null;
        }


        /// <summary>
        /// Tab Management Methods
        /// </summary>>

        private void SaveTabsForCurrentBranch()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            
            if (_currentBranch == null) return;

            _branchFileMap[_currentBranch] = _currentlyOpenFiles.ToList();

            SaveState();
        }

        private void RestoreTabsForCurrentBranch()
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            var branch = GetCurrentBranch();
            if (branch == null || !_branchFileMap.TryGetValue(branch, out var filesToOpen))
                return;

            var restoreMode = Options.RestoreMode;

            if (restoreMode != TabRestoreMode.RestoreOnly)
            {
                foreach (Document doc in _dte.Documents)
                {
                    var isUnsaved = !doc.Saved;

                    switch (restoreMode)
                    {
                        case TabRestoreMode.ReplaceAndKeepUnsaved when isUnsaved:
                            continue;
                        case TabRestoreMode.ReplaceAllAndSaveUnsaved when isUnsaved:
                            doc.Save();
                            break;
                    }

                    doc.Close(vsSaveChanges.vsSaveChangesNo);
                }
            }

            foreach (var file in filesToOpen.Where(File.Exists))
            {
                _dte.ItemOperations.OpenFile(file);
            }
        }


        /// <summary>
        /// Saved State Management Methods
        /// </summary>>

        private void LoadState()
        {
            try
            {
                var savePath = Path.Combine(_solutionPath, ".vs", SaveFileName);
                if (File.Exists(savePath))
                {
                    _branchFileMap = JsonConvert.DeserializeObject<Dictionary<string, List<string>>>(File.ReadAllText(savePath))
                                     ?? new Dictionary<string, List<string>>();
                }
            }
            catch
            {
                _branchFileMap = new Dictionary<string, List<string>>();
            }
        }


        private void SaveState()
        {
            try
            {
                var saveDir = Path.Combine(_solutionPath, ".vs");
                Directory.CreateDirectory(saveDir);

                var savePath = Path.Combine(saveDir, SaveFileName);
                var json = JsonConvert.SerializeObject(_branchFileMap, Formatting.Indented);
                File.WriteAllText(savePath, json);
            }
            catch
            {
                // Handle any exceptions silently, as this is a best-effort save operation
            }
        }

    }

}
