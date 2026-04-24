using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace BranchedTabs
{
    internal sealed class BranchWorkspaceState
    {
        public const int CurrentVersion = 2;

        public int Version { get; set; } = CurrentVersion;

        public Dictionary<string, BranchFeatureState> Branches { get; set; } =
            new Dictionary<string, BranchFeatureState>(StringComparer.OrdinalIgnoreCase);

        public BranchFeatureState GetOrCreateBranch(string branchName)
        {
            if (!Branches.TryGetValue(branchName, out var branchState) || branchState == null)
            {
                branchState = new BranchFeatureState();
                Branches[branchName] = branchState;
            }

            branchState.OpenTabs = branchState.OpenTabs ?? new List<string>();
            return branchState;
        }
    }

    internal sealed class BranchFeatureState
    {
        public List<string> OpenTabs { get; set; } = new List<string>();

        public StartupConfigurationState StartupConfiguration { get; set; }
    }

    internal enum StartupConfigurationKind
    {
        None,
        SingleProject,
        MultiProject
    }

    internal sealed class StartupConfigurationState
    {
        public StartupConfigurationKind Kind { get; set; }

        public List<StartupProjectEntryState> Projects { get; set; } = new List<StartupProjectEntryState>();
    }

    internal sealed class StartupProjectEntryState
    {
        public string ProjectName { get; set; }

        public string ProjectPath { get; set; }

        public string UniqueName { get; set; }

        public string Action { get; set; } = "Start";

        public int Order { get; set; }
    }

    internal sealed class BranchWorkspaceStateStore
    {
        private const string SaveFileName = "BranchTabs.json";

        public BranchWorkspaceState Load(string solutionPath)
        {
            if (string.IsNullOrWhiteSpace(solutionPath))
            {
                return new BranchWorkspaceState();
            }

            try
            {
                var savePath = GetSavePath(solutionPath);
                if (!File.Exists(savePath))
                {
                    return new BranchWorkspaceState();
                }

                var json = File.ReadAllText(savePath);
                if (string.IsNullOrWhiteSpace(json))
                {
                    return new BranchWorkspaceState();
                }

                var token = JToken.Parse(json);
                if (token["Branches"] != null)
                {
                    var state = token.ToObject<BranchWorkspaceState>() ?? new BranchWorkspaceState();
                    state.Branches = NormalizeBranches(state.Branches);
                    return state;
                }

                var legacyTabs = token.ToObject<Dictionary<string, List<string>>>() ??
                                 new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

                var migratedState = new BranchWorkspaceState();
                foreach (var entry in legacyTabs)
                {
                    migratedState.Branches[entry.Key] = new BranchFeatureState
                    {
                        OpenTabs = entry.Value?.Where(File.Exists).Distinct(StringComparer.OrdinalIgnoreCase).ToList() ?? new List<string>()
                    };
                }

                return migratedState;
            }
            catch (Exception ex)
            {
                BranchedTabsPackage.TraceError("Failed to load persisted branch workspace state.", ex);
                return new BranchWorkspaceState();
            }
        }

        public void Save(string solutionPath, BranchWorkspaceState state)
        {
            if (string.IsNullOrWhiteSpace(solutionPath) || state == null)
            {
                return;
            }

            try
            {
                state.Branches = NormalizeBranches(state.Branches);
                var savePath = GetSavePath(solutionPath);
                Directory.CreateDirectory(Path.GetDirectoryName(savePath));

                var json = JsonConvert.SerializeObject(state, Formatting.Indented);
                File.WriteAllText(savePath, json);
            }
            catch (Exception ex)
            {
                BranchedTabsPackage.TraceError("Failed to save persisted branch workspace state.", ex);
            }
        }

        public BranchFeatureState GetBranch(string solutionPath, string branchName)
        {
            if (string.IsNullOrWhiteSpace(branchName))
            {
                return null;
            }

            var state = Load(solutionPath);
            return state.Branches.TryGetValue(branchName, out var branchState) ? branchState : null;
        }

        public void SaveBranch(string solutionPath, string branchName, Action<BranchFeatureState> updateBranch)
        {
            if (string.IsNullOrWhiteSpace(solutionPath) || string.IsNullOrWhiteSpace(branchName) || updateBranch == null)
            {
                return;
            }

            var state = Load(solutionPath);
            var branchState = state.GetOrCreateBranch(branchName);
            updateBranch(branchState);

            if (HasMeaningfulState(branchState))
            {
                state.Branches[branchName] = branchState;
            }
            else
            {
                state.Branches.Remove(branchName);
            }

            Save(solutionPath, state);
        }

        private static string GetSavePath(string solutionPath)
        {
            return Path.Combine(solutionPath, ".vs", SaveFileName);
        }

        private static Dictionary<string, BranchFeatureState> NormalizeBranches(Dictionary<string, BranchFeatureState> branches)
        {
            var normalized = new Dictionary<string, BranchFeatureState>(StringComparer.OrdinalIgnoreCase);
            if (branches == null)
            {
                return normalized;
            }

            foreach (var entry in branches)
            {
                if (string.IsNullOrWhiteSpace(entry.Key))
                {
                    continue;
                }

                var branchState = entry.Value ?? new BranchFeatureState();
                branchState.OpenTabs = branchState.OpenTabs?.Where(File.Exists).Distinct(StringComparer.OrdinalIgnoreCase).ToList() ?? new List<string>();
                branchState.StartupConfiguration = NormalizeStartupConfiguration(branchState.StartupConfiguration);

                if (HasMeaningfulState(branchState))
                {
                    normalized[entry.Key] = branchState;
                }
            }

            return normalized;
        }

        private static StartupConfigurationState NormalizeStartupConfiguration(StartupConfigurationState startupConfiguration)
        {
            if (startupConfiguration == null)
            {
                return null;
            }

            startupConfiguration.Projects = startupConfiguration.Projects?
                .Where(project => project != null &&
                                  (!string.IsNullOrWhiteSpace(project.UniqueName) ||
                                   !string.IsNullOrWhiteSpace(project.ProjectPath) ||
                                   !string.IsNullOrWhiteSpace(project.ProjectName)))
                .OrderBy(project => project.Order)
                .ToList() ?? new List<StartupProjectEntryState>();

            if (startupConfiguration.Projects.Count == 0)
            {
                startupConfiguration.Kind = StartupConfigurationKind.None;
            }

            return startupConfiguration.Kind == StartupConfigurationKind.None && startupConfiguration.Projects.Count == 0
                ? null
                : startupConfiguration;
        }

        private static bool HasMeaningfulState(BranchFeatureState branchState)
        {
            if (branchState == null)
            {
                return false;
            }

            var hasOpenTabs = branchState.OpenTabs != null && branchState.OpenTabs.Count > 0;
            var startupConfiguration = NormalizeStartupConfiguration(branchState.StartupConfiguration);
            var hasStartupConfiguration = startupConfiguration != null;

            branchState.StartupConfiguration = startupConfiguration;
            return hasOpenTabs || hasStartupConfiguration;
        }
    }
}
