using EnvDTE;
using EnvDTE80;
using Microsoft;
using Microsoft.VisualStudio.Shell;
using Newtonsoft.Json;
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace BranchedTabs
{
    internal sealed class StartupProjectManager
    {
        private readonly AsyncPackage _package;
        private readonly GitBranchContext _gitBranchContext;
        private readonly BranchWorkspaceStateStore _stateStore = new BranchWorkspaceStateStore();

        private DTE2 _dte;
        private SolutionEvents _solutionEvents;
        private Timer _startupConfigurationMonitor;
        private string _solutionPath;
        private bool _isRestoring;
        private int _isMonitoringStartupConfiguration;
        private string _lastObservedStartupConfigurationKey;

        private TabManagerOptions Options =>
            (TabManagerOptions)_package.GetDialogPage(typeof(TabManagerOptions));

        public StartupProjectManager(AsyncPackage package, GitBranchContext gitBranchContext)
        {
            _package = package;
            _gitBranchContext = gitBranchContext;
        }

        public async Task InitializeAsync()
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

            _dte = await _package.GetServiceAsync(typeof(DTE)) as DTE2;
            Assumes.Present(_dte);

            _solutionEvents = _dte.Events.SolutionEvents;
            _solutionEvents.Opened += OnSolutionOpened;
            _solutionEvents.BeforeClosing += OnSolutionClosing;
            _solutionEvents.AfterClosing += OnSolutionClosed;

            _startupConfigurationMonitor = new Timer(OnStartupConfigurationMonitorTick, null, Timeout.Infinite, Timeout.Infinite);

            _gitBranchContext.BranchChanged += OnBranchChanged;

            BranchedTabsPackage.Trace("Startup project manager hooked solution events.");

            if (_dte.Solution.IsOpen)
            {
                OnSolutionOpened();
            }
        }

        private void OnSolutionOpened()
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            try
            {
                _solutionPath = Path.GetDirectoryName(_dte.Solution.FullName);
                _gitBranchContext.InitializeForSolution(_solutionPath);

                BranchedTabsPackage.Trace($"Startup project manager solution opened. Path='{_solutionPath}', Branch='{_gitBranchContext.CurrentBranch}', HasGit={_gitBranchContext.HasGitRepository}, Enabled={Options.EnableBranchStartupProjects}");

                if (string.IsNullOrWhiteSpace(_solutionPath) || !_gitBranchContext.HasGitRepository || !Options.EnableBranchStartupProjects)
                {
                    return;
                }

                RestoreStartupConfiguration(_gitBranchContext.CurrentBranch);
                PersistCurrentStartupConfiguration();
                StartStartupConfigurationMonitoring();
            }
            catch (Exception ex)
            {
                BranchedTabsPackage.TraceError("Startup project restore failed while opening the solution.", ex);
            }
        }

        private void OnSolutionClosing()
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            try
            {
                PersistCurrentStartupConfiguration();
                StopStartupConfigurationMonitoring();
            }
            catch (Exception ex)
            {
                BranchedTabsPackage.TraceError("Startup project save failed while closing the solution.", ex);
            }
        }

        private void OnSolutionClosed()
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            try
            {
                StopStartupConfigurationMonitoring();
                _solutionPath = null;
                _lastObservedStartupConfigurationKey = null;
                _gitBranchContext.ClearSolution();
            }
            catch (Exception ex)
            {
                BranchedTabsPackage.TraceError("Startup project manager cleanup failed after closing the solution.", ex);
            }
        }

        private void OnBranchChanged(object sender, BranchChangedEventArgs e)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            try
            {
                if (!Options.EnableBranchStartupProjects || _isRestoring)
                {
                    return;
                }

                if (string.IsNullOrWhiteSpace(_solutionPath) || !_dte.Solution.IsOpen)
                {
                    return;
                }

                BranchedTabsPackage.Trace($"Startup project manager branch changed from '{e?.PreviousBranch}' to '{e?.CurrentBranch}'.");

                SaveStartupConfiguration(e.PreviousBranch);
                RestoreStartupConfiguration(e.CurrentBranch);
                PersistCurrentStartupConfiguration();
            }
            catch (Exception ex)
            {
                BranchedTabsPackage.TraceError($"Startup project restore failed while switching branches from '{e?.PreviousBranch}' to '{e?.CurrentBranch}'.", ex);
            }
        }

        private void OnStartupConfigurationMonitorTick(object state)
        {
            if (Interlocked.Exchange(ref _isMonitoringStartupConfiguration, 1) == 1)
            {
                return;
            }

            ThreadHelper.JoinableTaskFactory.RunAsync(async delegate
            {
                try
                {
                    await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

                    if (_isRestoring || !Options.EnableBranchStartupProjects || string.IsNullOrWhiteSpace(_solutionPath) || !_gitBranchContext.HasGitRepository || !_dte.Solution.IsOpen)
                    {
                        return;
                    }

                    var currentStartupConfigurationKey = GetCurrentStartupConfigurationKey();
                    if (string.Equals(currentStartupConfigurationKey, _lastObservedStartupConfigurationKey, StringComparison.Ordinal))
                    {
                        return;
                    }

                    PersistCurrentStartupConfiguration();
                }
                catch (Exception ex)
                {
                    BranchedTabsPackage.TraceError("Startup project persistence failed while monitoring for changes.", ex);
                }
                finally
                {
                    Interlocked.Exchange(ref _isMonitoringStartupConfiguration, 0);
                }
            });
        }

        private void PersistCurrentStartupConfiguration()
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            if (_isRestoring || !Options.EnableBranchStartupProjects || string.IsNullOrWhiteSpace(_solutionPath) || !_gitBranchContext.HasGitRepository)
            {
                return;
            }

            SaveStartupConfiguration(_gitBranchContext.CurrentBranch);
            _lastObservedStartupConfigurationKey = GetCurrentStartupConfigurationKey();
        }

        private void StartStartupConfigurationMonitoring()
        {
            if (_startupConfigurationMonitor == null)
            {
                return;
            }

            _startupConfigurationMonitor.Change(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
        }

        private void StopStartupConfigurationMonitoring()
        {
            _startupConfigurationMonitor?.Change(Timeout.Infinite, Timeout.Infinite);
        }

        private void SaveStartupConfiguration(string branchName)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            if (string.IsNullOrWhiteSpace(branchName) || string.IsNullOrWhiteSpace(_solutionPath))
            {
                return;
            }

            var startupConfiguration = CaptureStartupConfiguration();
            _stateStore.SaveBranch(_solutionPath, branchName, branchState =>
            {
                branchState.StartupConfiguration = startupConfiguration;
            });
        }

        private void RestoreStartupConfiguration(string branchName)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            if (string.IsNullOrWhiteSpace(branchName) || string.IsNullOrWhiteSpace(_solutionPath))
            {
                return;
            }

            var branchState = _stateStore.GetBranch(_solutionPath, branchName);
            if (branchState?.StartupConfiguration == null)
            {
                BranchedTabsPackage.Trace($"No saved startup configuration found for branch '{branchName}'.");
                return;
            }

            var startupConfiguration = branchState.StartupConfiguration;
            if (startupConfiguration.Kind == StartupConfigurationKind.None || startupConfiguration.Projects.Count == 0)
            {
                BranchedTabsPackage.Trace($"Saved startup configuration for branch '{branchName}' was empty.");
                return;
            }

            var matchedProjects = ResolveProjects(startupConfiguration.Projects);
            if (matchedProjects.Count == 0)
            {
                BranchedTabsPackage.Trace($"Saved startup configuration for branch '{branchName}' could not match any loaded projects.");
                return;
            }

            BranchedTabsPackage.Trace($"Restoring startup configuration for branch '{branchName}' with {matchedProjects.Count} matched projects.");

            _isRestoring = true;
            try
            {
                ApplyStartupConfiguration(startupConfiguration.Kind, matchedProjects);
            }
            finally
            {
                _isRestoring = false;
            }
        }

        private StartupConfigurationState CaptureStartupConfiguration()
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            var solutionBuild = _dte.Solution.SolutionBuild as SolutionBuild2;
            if (solutionBuild == null)
            {
                return new StartupConfigurationState();
            }

            var startupProjects = EnumerateStartupProjects(solutionBuild.StartupProjects).ToList();
            if (startupProjects.Count == 0)
            {
                return new StartupConfigurationState();
            }

            var projects = GetLoadedProjects();
            var entries = new List<StartupProjectEntryState>();
            for (var index = 0; index < startupProjects.Count; index++)
            {
                var rawValue = startupProjects[index];
                if (string.IsNullOrWhiteSpace(rawValue))
                {
                    continue;
                }

                var parsed = ParseStartupProjectEntry(rawValue, index, projects);
                if (parsed != null)
                {
                    entries.Add(parsed);
                }
            }

            return new StartupConfigurationState
            {
                Kind = entries.Count > 1 ? StartupConfigurationKind.MultiProject : StartupConfigurationKind.SingleProject,
                Projects = entries.OrderBy(entry => entry.Order).ToList()
            };
        }

        private string GetCurrentStartupConfigurationKey()
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            return JsonConvert.SerializeObject(CaptureStartupConfiguration());
        }

        private void ApplyStartupConfiguration(StartupConfigurationKind kind, List<ResolvedStartupProject> matchedProjects)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            var solutionBuild = _dte.Solution.SolutionBuild as SolutionBuild2;
            if (solutionBuild == null)
            {
                return;
            }

            if (kind == StartupConfigurationKind.SingleProject)
            {
                solutionBuild.StartupProjects = matchedProjects[0].Project.UniqueName;
                return;
            }

            var values = matchedProjects
                .OrderBy(project => project.Entry.Order)
                .Select(project => ComposeStartupProjectValue(project.Project.UniqueName, project.Entry.Action))
                .ToArray();

            solutionBuild.StartupProjects = values;
        }

        private List<ResolvedStartupProject> ResolveProjects(IEnumerable<StartupProjectEntryState> savedProjects)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            var loadedProjects = GetLoadedProjects();
            var matches = new List<ResolvedStartupProject>();

            foreach (var entry in savedProjects.OrderBy(project => project.Order))
            {
                var matchedProject = loadedProjects.FirstOrDefault(project =>
                    !string.IsNullOrWhiteSpace(entry.ProjectPath) &&
                    string.Equals(project.FileName, entry.ProjectPath, StringComparison.OrdinalIgnoreCase));

                if (matchedProject == null)
                {
                    matchedProject = loadedProjects.FirstOrDefault(project =>
                        !string.IsNullOrWhiteSpace(entry.UniqueName) &&
                        string.Equals(project.UniqueName, entry.UniqueName, StringComparison.OrdinalIgnoreCase));
                }

                if (matchedProject == null)
                {
                    matchedProject = loadedProjects.FirstOrDefault(project =>
                        !string.IsNullOrWhiteSpace(entry.ProjectName) &&
                        string.Equals(project.Name, entry.ProjectName, StringComparison.OrdinalIgnoreCase));
                }

                if (matchedProject != null)
                {
                    matches.Add(new ResolvedStartupProject(entry, matchedProject));
                }
            }

            return matches;
        }

        private List<Project> GetLoadedProjects()
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            var projects = new List<Project>();
            foreach (Project project in _dte.Solution.Projects)
            {
                CollectProjects(project, projects);
            }

            return projects
                .Where(project => project != null && !string.IsNullOrWhiteSpace(project.UniqueName))
                .ToList();
        }

        private static void CollectProjects(Project project, ICollection<Project> projects)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            if (project == null)
            {
                return;
            }

            if (string.Equals(project.Kind, ProjectKinds.vsProjectKindSolutionFolder, StringComparison.OrdinalIgnoreCase))
            {
                foreach (ProjectItem item in project.ProjectItems)
                {
                    var subProject = item.SubProject;
                    if (subProject != null)
                    {
                        CollectProjects(subProject, projects);
                    }
                }

                return;
            }

            projects.Add(project);
        }

        private StartupProjectEntryState ParseStartupProjectEntry(string rawValue, int order, IList<Project> projects)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            var action = "Start";
            var uniqueName = rawValue;
            var separatorIndex = rawValue.LastIndexOf('|');
            if (separatorIndex > 0)
            {
                uniqueName = rawValue.Substring(0, separatorIndex).Trim();
                var parsedAction = rawValue.Substring(separatorIndex + 1).Trim();
                if (!string.IsNullOrWhiteSpace(parsedAction))
                {
                    action = parsedAction;
                }
            }

            var project = projects.FirstOrDefault(candidate =>
                string.Equals(candidate.UniqueName, uniqueName, StringComparison.OrdinalIgnoreCase));

            return new StartupProjectEntryState
            {
                UniqueName = uniqueName,
                ProjectName = project?.Name,
                ProjectPath = GetProjectPath(project),
                Action = action,
                Order = order
            };
        }

        private static string GetProjectPath(Project project)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            if (project == null)
            {
                return null;
            }

            try
            {
                return string.IsNullOrWhiteSpace(project.FileName) ? null : project.FileName;
            }
            catch
            {
                return null;
            }
        }

        private static IEnumerable<string> EnumerateStartupProjects(object startupProjects)
        {
            if (startupProjects == null)
            {
                yield break;
            }

            if (startupProjects is string startupProject)
            {
                yield return startupProject;
                yield break;
            }

            if (startupProjects is IEnumerable enumerable)
            {
                foreach (var item in enumerable)
                {
                    if (item is string value && !string.IsNullOrWhiteSpace(value))
                    {
                        yield return value;
                    }
                }
            }
        }

        private static string ComposeStartupProjectValue(string uniqueName, string action)
        {
            if (string.IsNullOrWhiteSpace(action) || string.Equals(action, "Start", StringComparison.OrdinalIgnoreCase))
            {
                return uniqueName;
            }

            return uniqueName + "|" + action;
        }

        private sealed class ResolvedStartupProject
        {
            public ResolvedStartupProject(StartupProjectEntryState entry, Project project)
            {
                Entry = entry;
                Project = project;
            }

            public StartupProjectEntryState Entry { get; }

            public Project Project { get; }
        }
    }
}
