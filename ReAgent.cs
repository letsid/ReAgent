using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using ExileCore2;
using ExileCore2.PoEMemory.Components;
using ExileCore2.Shared.Helpers;
using ImGuiNET;
using Newtonsoft.Json;
using ReAgent.SideEffects;
using ReAgent.State;
using RectangleF = ExileCore2.Shared.RectangleF;

namespace ReAgent;

public sealed class ReAgent : BaseSettingsPlugin<ReAgentSettings>
{
    private readonly Queue<(DateTime Date, string Description)> _actionInfo = new();
    private readonly Stopwatch _sinceLastKeyPress = Stopwatch.StartNew();
    private readonly RuleInternalState _internalState = new RuleInternalState();
    private readonly ConditionalWeakTable<Profile, string> _pendingNames = new ConditionalWeakTable<Profile, string>();
    private readonly HashSet<string> _loadedTextures = new();
    private RuleState _state;
    private List<SideEffectContainer> _pendingSideEffects = new List<SideEffectContainer>();
    private string _profileToDelete = null;
    public Dictionary<string, List<string>> CustomAilments { get; set; } = new Dictionary<string, List<string>>();
    public static int ProcessID { get; private set; }

    public override bool Initialise()
    {
        ProcessID = GameController.Window.Process.Id;

        var stringData = File.ReadAllText(Path.Join(DirectoryFullName, "CustomAilments.json"));
        CustomAilments = JsonConvert.DeserializeObject<Dictionary<string, List<string>>>(stringData);
        Settings.DumpState.OnPressed = () => { ImGui.SetClipboardText(JsonConvert.SerializeObject(new RuleState(this, _internalState), new JsonSerializerSettings
        {
            Error = (sender, args) =>
            {
                DebugWindow.LogError($"Error during state dump {args.ErrorContext.Error}");
                args.ErrorContext.Handled = true;
            }
        })); };
        Settings.ImageDirectory.OnValueChanged = () =>
        {
            foreach (var loadedTexture in _loadedTextures)
            {
                Graphics.DisposeTexture(loadedTexture);
            }

            _loadedTextures.Clear();
        };
        return base.Initialise();
    }

    private string _profileImportInput = null;
    private Task<(string text, bool edited)> _profileImportObject = null;

    private void DrawProfileImport()
    {
        var windowVisible = _profileImportInput != null;
        if (windowVisible)
        {
            if (ImGui.Begin("Import reagent profile", ref windowVisible))
            {
                if (_profileImportObject is { IsCompleted: false })
                {
                    ImGui.Text("Checking...");
                }

                if (_profileImportObject is { IsFaulted: true })
                {
                    ImGui.Text($"Check failed: {string.Join("\n", _profileImportObject.Exception.InnerExceptions)}");
                }

                if (ImGui.InputText("Exported code", ref _profileImportInput, 20000))
                {
                    _profileImportObject = Task.Run(() =>
                    {
                        var data = DataExporter.ImportDataBase64(_profileImportInput, "reagent_profile_v1");
                        data.ToObject<Profile>();
                        return (data.ToString(), false);
                    });
                }

                if (_profileImportObject is { IsCompletedSuccessfully: true })
                {
                    if (_profileImportObject.Result.edited)
                    {
                        ImGui.TextColored(Color.Green.ToImguiVec4(), "Editing manually");
                    }

                    var text = _profileImportObject.Result.text;
                    if (ImGui.InputTextMultiline("Json", ref text, 20000,
                            new Vector2(ImGui.GetContentRegionAvail().X, Math.Max(ImGui.GetContentRegionAvail().Y - 50, 50)), ImGuiInputTextFlags.ReadOnly))
                    {
                        _profileImportObject = Task.FromResult((text, true));
                    }
                }

                ImGui.BeginDisabled(_profileImportObject is not { IsCompletedSuccessfully: true });
                if (ImGui.Button("Import"))
                {
                    var profileName = GetNewProfileName("Imported profile ");
                    var profile = JsonConvert.DeserializeObject<Profile>(_profileImportObject.Result.text);
                    if (profile == null)
                    {
                        throw new Exception($"Profile deserialized to a null object, was '{_profileImportObject.Result.text}'");
                    }
                    Settings.Profiles.Add(profileName, profile);
                    windowVisible = false;
                }

                ImGui.EndDisabled();
                ImGui.End();
            }

            if (!windowVisible)
            {
                _profileImportInput = null;
                _profileImportObject = null;
            }
        }
    }

    public override void DrawSettings()
    {
        base.DrawSettings();
        DrawProfileImport();

        try
        {
            _state = new RuleState(this, _internalState);
        }
        catch (Exception ex)
        {
            LogError(ex.ToString());
        }

        if (!ShouldExecute(out var state))
        {
            ImGui.TextColored(Color.Red.ToImguiVec4(), $"Actions paused: {state}");
        }
        else
        {
            ImGui.Text("");
        }

        if (ImGui.BeginTabBar("Profiles", ImGuiTabBarFlags.AutoSelectNewTabs | ImGuiTabBarFlags.FittingPolicyScroll | ImGuiTabBarFlags.Reorderable))
        {
            if (ImGui.TabItemButton("+##addProfile", ImGuiTabItemFlags.Trailing))
            {
                var profileName = GetNewProfileName("New profile ");
                Settings.Profiles.Add(profileName, Profile.CreateWithDefaultGroup());
            }

            if (ImGui.TabItemButton("Import profile##import", ImGuiTabItemFlags.Trailing))
            {
                _profileImportInput = "";
                _profileImportObject = null;
            }

            foreach (var (profileName, profile) in Settings.Profiles.OrderByDescending(x => x.Key == Settings.CurrentProfile).ThenBy(x => x.Key).ToList())
            {
                if (profile == null)
                {
                    DebugWindow.LogError($"Profile {profileName} is null, creating default");
                    Settings.Profiles[profileName] = Profile.CreateWithDefaultGroup();
                    continue;
                }

                var preserveItem = true;
                var isCurrentProfile = profileName == Settings.CurrentProfile;
                if (isCurrentProfile)
                {
                    ImGui.PushStyleColor(ImGuiCol.Text, Color.LightGreen.ToImgui());
                }

                var tabSelected = ImGui.BeginTabItem($"{profileName}###{profile.TemporaryId}", ref preserveItem, ImGuiTabItemFlags.UnsavedDocument);
                if (isCurrentProfile)
                {
                    ImGui.PopStyleColor();
                }

                if (tabSelected)
                {
                    _pendingNames.TryGetValue(profile, out var newProfileName);
                    newProfileName ??= profileName;
                    ImGui.InputText("Name", ref newProfileName, 40);
                    if (!isCurrentProfile)
                    {
                        using (ImGuiHelpers.UseStyleColor(ImGuiCol.Button, Color.Green.ToImgui()))
                            if (ImGui.Button("Activate"))
                            {
                                Settings.CurrentProfile = profileName;
                            }

                        ImGui.SameLine();
                    }

                    if (ImGui.Button("Export profile"))
                    {
                        ImGui.SetClipboardText(DataExporter.ExportDataBase64(profile, "reagent_profile_v1", new JsonSerializerSettings()));
                    }

                    if (profileName != newProfileName)
                    {
                        if (Settings.Profiles.ContainsKey(newProfileName))
                        {
                            ImGui.SameLine();
                            ImGui.TextColored(Color.Red.ToImguiVec4(), "This profile name is already used");
                            _pendingNames.AddOrUpdate(profile, newProfileName);
                        }
                        else
                        {
                            Settings.Profiles.Remove(profileName);
                            Settings.Profiles.Add(newProfileName, profile);
                            if (isCurrentProfile)
                            {
                                Settings.CurrentProfile = newProfileName;
                            }

                            _pendingNames.Clear();
                        }
                    }

                    profile.DrawSettings(_state, Settings);
                    ImGui.EndTabItem();
                }
                else
                {
                    profile.FocusLost();
                }

                if (!preserveItem)
                {
                    if (ImGui.IsKeyDown(ImGuiKey.ModShift))
                    {
                        Settings.Profiles.Remove(profileName);
                    }
                    else
                    {
                        _profileToDelete = profileName;
                        ImGui.OpenPopup("ProfileDeleteConfirmation");
                    }
                }
            }

            var deleteResult = ImguiExt.DrawDeleteConfirmationPopup("ProfileDeleteConfirmation", $"profile {_profileToDelete}");
            if (deleteResult == true)
            {
                Settings.Profiles.Remove(_profileToDelete);
            }

            if (deleteResult != null)
            {
                _profileToDelete = null;
            }

            ImGui.EndTabBar();
        }
    }

    private string GetNewProfileName(string prefix)
    {
        return Enumerable.Range(1, 10000).Select(i => $"{prefix}{i}").Except(Settings.Profiles.Keys).First();
    }

    public override void Render()
    {
        if (Settings.Profiles.Count == 0)
        {
            Settings.Profiles.Add(GetNewProfileName("New profile "), Profile.CreateWithDefaultGroup());
            Settings.CurrentProfile = Settings.Profiles.Keys.Single();
        }

        if (string.IsNullOrEmpty(Settings.CurrentProfile) || !Settings.Profiles.TryGetValue(Settings.CurrentProfile, out var profile))
        {
            Settings.CurrentProfile = Settings.Profiles.Keys.First();
            profile = Settings.Profiles[Settings.CurrentProfile];
        }

        var shouldExecute = ShouldExecute(out var state);
        while (_actionInfo.TryPeek(out var entry) && (DateTime.Now - entry.Date).TotalSeconds > Settings.HistorySecondsToKeep)
        {
            _actionInfo.Dequeue();
        }

        if (Settings.ShowDebugWindow)
        {
            var show = Settings.ShowDebugWindow.Value;
            ImGui.Begin("Debug Mode Window", ref show);
            Settings.ShowDebugWindow.Value = show;
            var activePlugins = GameController.PluginBridge.GetMethod<Func<List<string>>>("GetActivePlugins")?.Invoke();
            ImGui.TextWrapped($"State: {state}");
            if (ImGui.Button("Clear History"))
            {
                _actionInfo.Clear();
            }

            ImGui.BeginChild("KeyPressesInfo");
            foreach (var (dateTime, @event) in _actionInfo.Reverse())
            {
                ImGui.TextUnformatted($"{dateTime:HH:mm:ss.fff}: {@event}");
            }
            if (activePlugins != null && activePlugins.Any())
            {
                foreach (var plugin in activePlugins)
                {
                    ImGui.TextColored(Color.Green.ToImguiVec4(), $" - {plugin}");
                }
            }
            else
            {
                ImGui.TextColored(Color.Red.ToImguiVec4(), "No active plugins.");
            }
            ImGui.EndChild();
            ImGui.End();
        }

        if (!shouldExecute && !Settings.InspectState)
        {
            return;
        }

        _internalState.KeyToPress = null;
        _internalState.KeysToHoldDown.Clear();
        _internalState.KeysToRelease.Clear();
        _internalState.TextToDisplay.Clear();
        _internalState.GraphicToDisplay.Clear();
        _internalState.ProgressBarsToDisplay.Clear();
        _internalState.ChatTitlePanelVisible = GameController.IngameState.IngameUi.ChatTitlePanel.IsVisible;
        _internalState.CanPressKey = _sinceLastKeyPress.ElapsedMilliseconds >= Settings.GlobalKeyPressCooldown && !_internalState.ChatTitlePanelVisible;
        _internalState.LeftPanelVisible = GameController.IngameState.IngameUi.OpenLeftPanel.IsVisible;
        _internalState.RightPanelVisible = GameController.IngameState.IngameUi.OpenRightPanel.IsVisible;
        _internalState.LargePanelVisible = GameController.IngameState.IngameUi.LargePanels.Any(p => p.IsVisible);
        _internalState.FullscreenPanelVisible = GameController.IngameState.IngameUi.FullscreenPanels.Any(p => p.IsVisible);
        _state = new RuleState(this, _internalState);

        if (Settings.InspectState)
        {
            GameController.InspectObject(_state, "ReAgent state");
        }

        if (!shouldExecute && !Settings.InspectState)
        {
            return;
        }

        ApplyPendingSideEffects();

        foreach (var group in profile.Groups)
        {
            var newSideEffects = group.Evaluate(_state).ToList();
            foreach (var sideEffect in newSideEffects)
            {
                sideEffect.SetPending();
                _pendingSideEffects.Add(sideEffect);
            }
        }

        ApplyPendingSideEffects();

        if (_internalState.KeyToPress is { } key)
        {
            _internalState.KeyToPress = null;
            InputHelper.SendInputPress(key);
            _sinceLastKeyPress.Restart();
        }

        foreach (var heldKey in _internalState.KeysToHoldDown)
        {
            InputHelper.SendInputDown(heldKey);
        }


        foreach (var heldKey in _internalState.KeysToRelease)
        {
            InputHelper.SendInputUp(heldKey);
        }

        foreach (var (text, position, size, fraction, color, backgroundColor, textColor) in _internalState.ProgressBarsToDisplay)
        {
            var textSize = Graphics.MeasureText(text);
            Graphics.DrawBox(position, position + size, ColorFromName(backgroundColor));
            Graphics.DrawBox(position, position + size with { X = size.X * fraction }, ColorFromName(color));
            Graphics.DrawText(text, position + size / 2 - textSize / 2, ColorFromName(textColor));
        }

        foreach (var (graphicFilePath, position, size, tintColor) in _internalState.GraphicToDisplay)
        {
            if (!_loadedTextures.Contains(graphicFilePath))
            {
                var graphicFileFullPath = Path.Combine(Path.GetDirectoryName(typeof(Core).Assembly.Location)!, Settings.ImageDirectory, graphicFilePath);
                if (File.Exists(graphicFileFullPath))
                {
                    if (Graphics.InitImage(graphicFilePath, graphicFileFullPath))
                    {
                        _loadedTextures.Add(graphicFilePath);
                    }
                }
            }

            if (_loadedTextures.Contains(graphicFilePath))
            {
                Graphics.DrawImage(graphicFilePath, new RectangleF(position.X, position.Y, size.X, size.Y), ColorFromName(tintColor));
            }
        }

        foreach (var (text, position, color) in _internalState.TextToDisplay)
        {
            var textSize = Graphics.MeasureText(text);
            Graphics.DrawBox(position, position + textSize, Color.Black);
            Graphics.DrawText(text, position, ColorFromName(color));
        }
    }

    private static Color ColorFromName(string color)
    {
        return Color.FromName(color);
    }

    private void ApplyPendingSideEffects()
    {
        var applicationResults = _pendingSideEffects.Select(x => (x, ApplicationResult: x.Apply(_state))).ToList();
        foreach (var successfulApplication in applicationResults.Where(x =>
                     x.ApplicationResult is SideEffectApplicationResult.AppliedUnique or SideEffectApplicationResult.AppliedDuplicate))
        {
            successfulApplication.x.SetExecuted(_state);
            if (successfulApplication.ApplicationResult == SideEffectApplicationResult.AppliedUnique)
            {
                _actionInfo.Enqueue((DateTime.Now, successfulApplication.x.SideEffect.ToString()));
            }
        }

        _pendingSideEffects = applicationResults.Where(x => x.ApplicationResult == SideEffectApplicationResult.UnableToApply).Select(x => x.x).ToList();
    }

    private bool IsPluginActive(string pluginName)
    {
        var method = GameController.PluginBridge.GetMethod<Func<bool>>($"{pluginName}.IsActive");
        return method?.Invoke() ?? false;
    }


    private bool ShouldExecute(out string state)
    {
        if (!GameController.Window.IsForeground())
        {
            state = "Game window is not focused";
            return false;
        }

        if (IsPluginActive("AutoBlink"))
        {
            state = "Paused by AutoBlink";
            return false;
        }

        if (IsPluginActive("SoulOffering"))
        {
            state = "Paused by SoulOffering";
            return false;
        }

        if (!Settings.PluginSettings.EnableInEscapeState &&
            GameController.Game.IsEscapeState)
        {
            state = "Escape state is active";
            return false;
        }

        if (GameController.Player.TryGetComponent<Life>(out var lifeComp))
        {
            if (lifeComp.CurHP <= 0)
            {
                state = "Player is dead";
                return false;
            }
        }
        else
        {
            state = "Cannot find player Life component";
            return false;
        }

        if (GameController.Player.TryGetComponent<Buffs>(out var buffComp))
        {
            if (buffComp.HasBuff("grace_period"))
            {
                state = "Grace period is active";
                return false;
            }
        }
        else
        {
            state = "Cannot find player Buffs component";
            return false;
        }

        if (!GameController.Player.HasComponent<Actor>())
        {
            state = "Cannot find player Actor component";
            return false;
        }

        state = "Ready";
        return true;
    }

}
