using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using Task = System.Threading.Tasks.Task;

namespace BranchedTabs
{
    [PackageRegistration(UseManagedResourcesOnly = true, AllowsBackgroundLoading = true)]
    [ProvideOptionPage(typeof(TabManagerOptions), "Branched Tabs", "General", 0, 0, true)]
    [InstalledProductRegistration("Branched Tabs", "Restores tabs per Git branch", "1.1")]
    [Guid("15c59b45-c3fa-499e-a2b8-b4f37014b49a")]
    [ProvideAutoLoad(UIContextGuids80.SolutionExists, PackageAutoLoadFlags.BackgroundLoad)]
    public sealed class BranchedTabsPackage : AsyncPackage
    {
        private GitBranchContext _gitBranchContext;
        private TabManager _tabManager;
        private StartupProjectManager _startupProjectManager;

        internal static void Trace(string message)
        {
            Debug.WriteLine($"[BranchedTabs] {message}");
        }

        internal static void TraceError(string message, Exception ex)
        {
            Debug.WriteLine($"[BranchedTabs] ERROR: {message} {ex}");
        }

        protected override async Task InitializeAsync(CancellationToken cancellationToken, IProgress<ServiceProgressData> progress)
        {
            await this.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);

            Trace("Package initialization started.");

            try
            {
                _gitBranchContext = new GitBranchContext(this);

                _tabManager = new TabManager(this, _gitBranchContext);
                await _tabManager.InitializeAsync();
                Trace("Tab manager initialized.");
            }
            catch (Exception ex)
            {
                TraceError("Tab manager initialization failed.", ex);
            }

            try
            {
                if (_gitBranchContext == null)
                {
                    _gitBranchContext = new GitBranchContext(this);
                }

                _startupProjectManager = new StartupProjectManager(this, _gitBranchContext);
                await _startupProjectManager.InitializeAsync();
                Trace("Startup project manager initialized.");
            }
            catch (Exception ex)
            {
                TraceError("Startup project manager initialization failed.", ex);
            }
        }

    }
}
