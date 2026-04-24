using Microsoft.VisualStudio.Shell;
using System.ComponentModel;

namespace BranchedTabs
{
    public class TabManagerOptions : DialogPage
    {
        [Category("General")]
        [DisplayName("Enable Branch-Based Tabs")]
        [Description("When enabled, open document tabs are saved and restored per Git branch for Git-backed solutions. Detached HEAD states are tracked separately, and Git worktrees are supported.")]
        public bool EnableBranchTabs { get; set; } = true;

        [Category("General")]
        [DisplayName("Enable Branch-Based Startup Projects")]
        [Description("When enabled, startup project selections are saved and restored per Git branch for Git-backed solutions. Single-project and multi-project startup configurations are supported, including multi-startup order and action values. Missing or renamed projects restore best-effort by path first, then by project name.")]
        public bool EnableBranchStartupProjects { get; set; } = true;

        [Category("Behavior")]
        [DisplayName("Restore Mode")]
        [Description("Controls how tabs are restored when switching branches. Unsaved documents are handled according to the selected mode.")]
        public TabRestoreMode RestoreMode { get; set; } = TabRestoreMode.RestoreOnly;
    }
}
