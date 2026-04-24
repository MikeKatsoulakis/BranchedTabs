using EnvDTE;
using EnvDTE80;
using Microsoft;
using Microsoft.VisualStudio.Shell;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace BranchedTabs
{

    internal sealed class TabManager
    {
        private readonly AsyncPackage _package;
        private readonly GitBranchContext _gitBranchContext;
        private readonly BranchWorkspaceStateStore _stateStore = new BranchWorkspaceStateStore();
        private DTE2 _dte;
        private SolutionEvents _solutionEvents;
        private WindowEvents _windowEvents;
        private string _solutionPath;

        private readonly HashSet<string> _currentlyOpenFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private DocumentEvents _documentEvents;
        private bool _isRestoring;
        private int _pendingTabRefresh;

        private TabManagerOptions Options =>
            (TabManagerOptions)_package.GetDialogPage(typeof(TabManagerOptions));

        public TabManager(AsyncPackage package, GitBranchContext gitBranchContext)
        {
            _package = package;
            _gitBranchContext = gitBranchContext;
        }

        public async Task InitializeAsync()
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

            _dte = await _package.GetServiceAsync(typeof(DTE)) as DTE2;
            Assumes.Present(_dte);

            _documentEvents = _dte.Events.get_DocumentEvents();
            _documentEvents.DocumentOpened += OnDocumentOpened;
            _documentEvents.DocumentClosing += OnDocumentClosing;

            _windowEvents = _dte.Events.WindowEvents;
            _windowEvents.WindowCreated += OnWindowCreated;
            _windowEvents.WindowClosing += OnWindowClosing;

            _solutionEvents = _dte.Events.SolutionEvents;
            _solutionEvents.Opened += OnSolutionOpened;
            _solutionEvents.BeforeClosing += OnSolutionClosing;
            _solutionEvents.AfterClosing += OnSolutionClosed;

            _gitBranchContext.BranchChanged += OnBranchChanged;

            BranchedTabsPackage.Trace("Tab manager hooked solution and document events.");

            if (_dte.Solution.IsOpen)
            {
                OnSolutionOpened();
            }
        }

        private void OnDocumentOpened(Document document)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            try
            {
                if (!string.IsNullOrEmpty(document?.FullName) && File.Exists(document.FullName))
                {
                    _currentlyOpenFiles.Add(document.FullName);
                    PersistCurrentTabs();
                }
            }
            catch (Exception ex)
            {
                BranchedTabsPackage.TraceError("Tab tracking failed while opening a document.", ex);
            }
        }

        private void OnDocumentClosing(Document document)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            try
            {
                if (!string.IsNullOrEmpty(document?.FullName))
                {
                    _currentlyOpenFiles.Remove(document.FullName);
                }

                QueueRefreshAndPersist();
            }
            catch (Exception ex)
            {
                BranchedTabsPackage.TraceError("Tab tracking failed while closing a document.", ex);
            }
        }

        private void OnWindowCreated(Window window)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            try
            {
                if (TryGetWindowDocumentPath(window, out var documentPath))
                {
                    _currentlyOpenFiles.Add(documentPath);
                    PersistCurrentTabs();
                }
            }
            catch (Exception ex)
            {
                BranchedTabsPackage.TraceError("Tab tracking failed while creating a window.", ex);
            }
        }

        private void OnWindowClosing(Window window)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            try
            {
                if (TryGetWindowDocumentPath(window, out var documentPath))
                {
                    _currentlyOpenFiles.Remove(documentPath);
                }

                QueueRefreshAndPersist();
            }
            catch (Exception ex)
            {
                BranchedTabsPackage.TraceError("Tab tracking failed while closing a window.", ex);
            }
        }

        private void OnSolutionOpened()
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            try
            {
                _solutionPath = Path.GetDirectoryName(_dte.Solution.FullName);
                _gitBranchContext.InitializeForSolution(_solutionPath);

                BranchedTabsPackage.Trace($"Tab manager solution opened. Path='{_solutionPath}', Branch='{_gitBranchContext.CurrentBranch}', HasGit={_gitBranchContext.HasGitRepository}, Enabled={IsFeatureEnabled()}");

                RefreshOpenFiles();

                if (!IsFeatureEnabled() || string.IsNullOrEmpty(_solutionPath) || !_gitBranchContext.HasGitRepository)
                    return;

                RestoreTabsForCurrentBranch();
                PersistCurrentTabs();
            }
            catch (Exception ex)
            {
                BranchedTabsPackage.TraceError("Tab restore failed while opening the solution.", ex);
            }
        }

        private void OnSolutionClosing()
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            try
            {
                PersistCurrentTabs();
            }
            catch (Exception ex)
            {
                BranchedTabsPackage.TraceError("Tab save failed while closing the solution.", ex);
            }
        }

        private void OnSolutionClosed()
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            try
            {
                _currentlyOpenFiles.Clear();
                _solutionPath = null;
            }
            catch (Exception ex)
            {
                BranchedTabsPackage.TraceError("Tab manager cleanup failed after closing the solution.", ex);
            }
        }

        private void OnBranchChanged(object sender, BranchChangedEventArgs e)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            try
            {
                if (!IsFeatureEnabled())
                {
                    return;
                }

                if (string.IsNullOrWhiteSpace(_solutionPath) || !_dte.Solution.IsOpen)
                {
                    return;
                }

                BranchedTabsPackage.Trace($"Tab manager branch changed from '{e?.PreviousBranch}' to '{e?.CurrentBranch}'.");

                SaveTabsForBranch(e.PreviousBranch);
                RestoreTabsForCurrentBranch();
                PersistCurrentTabs();
            }
            catch (Exception ex)
            {
                BranchedTabsPackage.TraceError($"Tab restore failed while switching branches from '{e?.PreviousBranch}' to '{e?.CurrentBranch}'.", ex);
            }
        }

        private bool IsFeatureEnabled()
        {
            return Options.EnableBranchTabs;
        }

        private void PersistCurrentTabs()
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            if (_isRestoring || !IsFeatureEnabled() || string.IsNullOrWhiteSpace(_solutionPath) || !_gitBranchContext.HasGitRepository)
            {
                return;
            }

            SaveTabsForCurrentBranch();
        }

        private void QueueRefreshAndPersist()
        {
            if (Interlocked.Exchange(ref _pendingTabRefresh, 1) == 1)
            {
                return;
            }

            ThreadHelper.JoinableTaskFactory.RunAsync(async delegate
            {
                try
                {
                    await Task.Yield();
                    await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

                    RefreshOpenFiles();
                    PersistCurrentTabs();
                }
                catch (Exception ex)
                {
                    BranchedTabsPackage.TraceError("Tab tracking failed while refreshing open files.", ex);
                }
                finally
                {
                    Interlocked.Exchange(ref _pendingTabRefresh, 0);
                }
            });
        }

        private void SaveTabsForCurrentBranch()
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            SaveTabsForBranch(_gitBranchContext.CurrentBranch);
        }

        private void SaveTabsForBranch(string branch)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            if (string.IsNullOrWhiteSpace(branch) || string.IsNullOrWhiteSpace(_solutionPath))
            {
                return;
            }

            var openTabs = _currentlyOpenFiles
                .Where(File.Exists)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            BranchedTabsPackage.Trace($"Saving {openTabs.Count} tabs for branch '{branch}'.");
            _stateStore.SaveBranch(_solutionPath, branch, branchState =>
            {
                branchState.OpenTabs = openTabs;
            });
        }

        private void RefreshOpenFiles()
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            _currentlyOpenFiles.Clear();

            foreach (Window window in _dte.Windows)
            {
                if (TryGetWindowDocumentPath(window, out var documentPath))
                {
                    _currentlyOpenFiles.Add(documentPath);
                }
            }

            if (_currentlyOpenFiles.Count > 0)
            {
                return;
            }

            foreach (Document doc in _dte.Documents)
            {
                if (!string.IsNullOrWhiteSpace(doc.FullName) && File.Exists(doc.FullName))
                {
                    _currentlyOpenFiles.Add(doc.FullName);
                }
            }
        }

        private static bool TryGetWindowDocumentPath(Window window, out string documentPath)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            documentPath = null;
            if (window == null)
            {
                return false;
            }

            try
            {
                var document = window.Document;
                if (document == null || string.IsNullOrWhiteSpace(document.FullName) || !File.Exists(document.FullName))
                {
                    return false;
                }

                documentPath = document.FullName;
                return true;
            }
            catch
            {
                return false;
            }
        }

        private void RestoreTabsForCurrentBranch()
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            var branch = _gitBranchContext.CurrentBranch;
            var branchState = _stateStore.GetBranch(_solutionPath, branch);
            if (string.IsNullOrWhiteSpace(branch) || branchState == null)
            {
                BranchedTabsPackage.Trace($"No saved tabs found for branch '{branch}'.");
                return;
            }

            var filesToOpen = branchState.OpenTabs ?? new List<string>();
            BranchedTabsPackage.Trace($"Restoring {filesToOpen.Count} tabs for branch '{branch}'.");

            var restoreMode = Options.RestoreMode;
            _isRestoring = true;
            try
            {
                if (restoreMode != TabRestoreMode.RestoreOnly)
                {
                    var documentsToClose = _dte.Documents.Cast<Document>().ToList();
                    foreach (Document doc in documentsToClose)
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
            finally
            {
                _isRestoring = false;
            }
        }

    }

}
