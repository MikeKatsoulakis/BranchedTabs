using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using System;
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
        private TabManager _tabManager;

        protected override async Task InitializeAsync(CancellationToken cancellationToken, IProgress<ServiceProgressData> progress)
        {
            await this.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);

            _tabManager = new TabManager(this);
            await _tabManager.InitializeAsync();
        }

    }
}
