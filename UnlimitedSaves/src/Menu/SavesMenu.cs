using System;
using System.Collections.Generic;
using System.IO;
using RWCustom;
using UnityEngine;
using global::Menu;
using Kittehface.Framework20;

namespace UnlimitedSaves.Menu;

using Menu = global::Menu;
using ProgressionLoadResult = PlayerProgression.ProgressionLoadResult;

public class SavesMenu : Menu.Menu, SelectOneButton.SelectOneButtonOwner, CheckBox.IOwnCheckBox {
    public static readonly ProcessManager.ProcessID id = new($"{nameof(SavesMenu)}+{Plugin.Id}", true);

    private readonly FSprite _darkSprite;
    private readonly SimpleButton _backButton;
    private readonly SimpleButton _backupsButton;
    private bool _lastPauseButton;
    private bool _exiting;

    private readonly MenuLabel _buttonsLoadingLabel;

    private bool progressionBusy => _waitingOnProgressionLoaded || !manager.rainWorld.progression.progressionLoaded;

    private SelectOneButton[] _saveButtons = [];
    private int _leavingSaveSlot;
    private bool _waitingOnProgressionLoaded;
    private bool _reportCorruptedDialogDisplaying;

    private readonly HoldButton _resetButton;
    private readonly MenuLabel _resetWarningText;
    private readonly MenuLabel _deleteWarningText;
    private int _resetWarningTextCounter;
    private float _resetWarningTextAlpha;
    private readonly CheckBox _deleteCheckbox;

    private bool _shouldDeleteSave;

    private MenuLabel _infoLabel;

    public SavesMenu(ProcessManager manager) : base(manager, id) {
        pages.Add(new Page(this, null, "main", 0));
        scene = new InteractiveMenuScene(this, pages[0], manager.rainWorld.options.subBackground);
        pages[0].subObjects.Add(scene);

        _darkSprite = new FSprite("pixel") {
            color = new Color(0f, 0f, 0f),
            anchorX = 0f,
            anchorY = 0f,
            scaleX = 1368f,
            scaleY = 770f,
            x = -1f,
            y = -1f,
            alpha = 0.85f
        };
        pages[0].Container.AddChild(_darkSprite);

        mySoundLoopID = SoundID.MENU_Main_Menu_LOOP;

        _backButton = new SimpleButton(this, pages[0], Translate("BACK"), "BACK", new Vector2(200f, 50f),
            new Vector2(110f, 30f));
        pages[0].subObjects.Add(_backButton);
        backObject = _backButton;

        _backupsButton = new SimpleButton(this, pages[0], Translate("BACKUPS"), "BACKUPS", new Vector2(320f, 50f),
            new Vector2(110f, 30f));
        pages[0].subObjects.Add(_backupsButton);

        _buttonsLoadingLabel = new MenuLabel(this, pages[0], Translate("Loading..."), new Vector2(1366f / 2f, 680f),
            Vector2.zero, false) { label = { alpha = 0f, anchorY = 1f } };
        pages[0].subObjects.Add(_buttonsLoadingLabel);

        float saveSlotButtonWidth = OptionsMenu.GetSaveSlotButtonWidth(CurrLang);
        _resetButton = new HoldButton(this, pages[0],
            Translate("RESET PROGRESS").Replace("<LINE>", "\r\n"), "RESET PROGRESS",
            new Vector2(1366f / 2f + saveSlotButtonWidth / 2f + 55f + 15f + 20f, 680f - 55f), 400f
        );
        pages[0].subObjects.Add(_resetButton);

        _resetWarningText = new MenuLabel(this, pages[0],
            Translate("WARNING!<LINE>This will reset all progress in the selected save slot,<LINE>including map exploration. Unlocked arenas and<LINE>sandbox items are retained.").Replace("<LINE>", "\r\n"),
            new Vector2(_resetButton.pos.x + 55f + 15f + 20f, _resetButton.pos.y),
            new Vector2(0f, 0f),
            false
        ) { label = { alignment = FLabelAlignment.Left } };
        pages[0].subObjects.Add(_resetWarningText);

        _deleteWarningText = new MenuLabel(this, pages[0],
            Translate("WARNING!!!<LINE>This will fully erase the selected save slot<LINE>without a way to recover it.<LINE>Proceed with caution!").Replace("<LINE>", "\r\n"),
            _resetWarningText.pos,
            _resetWarningText.size,
            false
        ) { label = { alignment = _resetWarningText.label.alignment } };
        pages[0].subObjects.Add(_deleteWarningText);

        _deleteCheckbox = new CheckBox(this, pages[0], this,
            new Vector2(_resetButton.pos.x - 55f - 15f, _resetButton.pos.y - 55f - 15f - 30f), 50f,
            Translate("Delete save"), "DELETE SAVE", true
        );
        _deleteCheckbox.label.label.alignment = FLabelAlignment.Left;
        pages[0].subObjects.Add(_deleteCheckbox);

        _infoLabel = new MenuLabel(this, pages[0], Translate("Loading..."), new Vector2(88f - 15f, 680f + 15f),
            Vector2.zero, false);
        _infoLabel.label.SetAnchor(0f, 1f);
        _infoLabel.label.alignment = FLabelAlignment.Left;
        pages[0].subObjects.Add(_infoLabel);

        UpdateButtons();

        string saveDir = Application.persistentDataPath;
        if (manager.rainWorld.progression.saveFileDataInMemory.overrideBaseDir is not null)
            saveDir = manager.rainWorld.progression.saveFileDataInMemory.overrideBaseDir;
        if (!File.Exists(Path.Combine(saveDir, manager.rainWorld.progression.saveFileDataInMemory.filename)))
            SetCurrentlySelectedOfSeries("SaveSlot", 0);
    }

    private string GetSaveSlotName(int slot) => $"{Translate("SAVE SLOT")} {slot + 1}";

    public int GetCurrentlySelectedOfSeries(string series) => series switch {
        "SaveSlot" => manager.rainWorld.options.saveSlot,
        _ => 0
    };

    public void SetCurrentlySelectedOfSeries(string series, int to) {
        switch (series) {
            case "SaveSlot":
                if (manager.rainWorld.options.saveSlot == to)
                    break;
                _leavingSaveSlot = manager.rainWorld.options.saveSlot;
                manager.rainWorld.options.saveSlot = to;
                manager.rainWorld.progression.Destroy(_leavingSaveSlot);
                manager.rainWorld.progression = new PlayerProgression(manager.rainWorld, true, false);
                _waitingOnProgressionLoaded = true;
                break;
        }
    }

    public bool GetChecked(CheckBox box) {
        if (box == _deleteCheckbox) {
            return _shouldDeleteSave;
        }
        return false;
    }

    public void SetChecked(CheckBox box, bool c) {
        if (box == _deleteCheckbox) {
            _shouldDeleteSave = c;
        }
    }

    public override void Update() {
        base.Update();

        UpdateHandleBackButton();

        if (_waitingOnProgressionLoaded && manager.rainWorld.progression.progressionLoaded) {
            ProgressionLoadResult res = manager.rainWorld.progression.progressionLoadedResult;
            if (res is ProgressionLoadResult.SUCCESS_CREATE_NEW_FILE
                or ProgressionLoadResult.SUCCESS_LOAD_EXISTING_FILE
                or ProgressionLoadResult.ERROR_SAVE_DATA_MISSING)
                HandleSaveSlotChangeSucceeded(res);
            else
                HandleSaveSlotChangeFailed(res);
        }

        if (_resetButton.FillingUp) {
            _resetWarningTextCounter++;
            _resetWarningTextAlpha = Custom.LerpAndTick(_resetWarningTextAlpha, 1f, 0.08f, 0.025f);
        }
        else {
            _resetWarningTextAlpha = Custom.LerpAndTick(_resetWarningTextAlpha, 0f, 0.08f, 0.05f);
        }
        MenuLabel resetWarningText = _shouldDeleteSave ? _deleteWarningText : _resetWarningText;
        resetWarningText.label.alpha =
            (0.7f + 0.3f * Mathf.Sin(_resetWarningTextCounter / 40f * Mathf.PI * 2f)) * _resetWarningTextAlpha;
        resetWarningText.label.color = Color.Lerp(
            new Color(1f, 0f, 0f), new Color(1f, 1f, 1f),
            Mathf.Pow(0.5f + 0.5f * Mathf.Sin(_resetWarningTextCounter / 40f * Mathf.PI * 2f), 2f)
        );
        MenuLabel otherResetWarningText = !_shouldDeleteSave ? _deleteWarningText : _resetWarningText;
        otherResetWarningText.label.alpha = 0f;

        _backButton.buttonBehav.greyedOut = progressionBusy;

        foreach (SelectOneButton button in _saveButtons)
            button.buttonBehav.greyedOut = progressionBusy;
        _resetButton.buttonBehav.greyedOut = progressionBusy;
    }

    private void UpdateHandleBackButton() {
        bool pause = RWInput.CheckPauseButton(0);
        if (pause && !_lastPauseButton && manager.dialog == null && !_backButton.buttonBehav.greyedOut)
            Singal(_backButton, _backButton.signalText);
        _lastPauseButton = pause;
    }

    private void UpdateButtons() {
        if (UserData.Busy)
            return;

        foreach (SelectOneButton button in _saveButtons) {
            pages[0].RemoveSubObject(button);
            button.RemoveSprites();
        }
        _saveButtons = [];

        _buttonsLoadingLabel.label.alpha = 1f;

        UserData.Search(Profiles.ActiveProfiles[0], "sav*");
        UserData.OnSearchCompleted += UserDataOnSearchCompleted;
    }

    private void UserDataOnSearchCompleted(Profiles.Profile a, string b, List<UserData.SearchResult> results,
        UserData.Result c) {
        UserData.OnSearchCompleted -= UserDataOnSearchCompleted;

        _buttonsLoadingLabel.label.alpha = 0f;

        HashSet<int> existing = [ 0 ];
        int maxSlot = manager.rainWorld.options.saveSlot;
        foreach (UserData.SearchResult result in results) {
            if (!int.TryParse(result.fileDefinition.fileName.Substring(3), out int slot))
                continue;
            --slot;
            if (slot > maxSlot)
                maxSlot = slot;
            existing.Add(slot);
        }

        _saveButtons = new SelectOneButton[maxSlot + 2];

        Vector2 size = new(OptionsMenu.GetSaveSlotButtonWidth(CurrLang), 30f);
        for (int i = 0; i < _saveButtons.Length; i++) {
            Vector2 pos = new(1366f / 2f - size.x / 2f, 680f - i * 40f - size.y);
            string name = GetSaveSlotName(i);

            _saveButtons[i] = new SelectOneButton(this, pages[0], name, "SaveSlot", pos, size, _saveButtons, i);
            pages[0].subObjects.Add(_saveButtons[i]);

            if (!existing.Contains(i))
                _saveButtons[i].labelColor = new HSLColor(120f / 360f, 0.65f, 0.5f);
        }
    }

    private void HandleSaveSlotChangeSucceeded(ProgressionLoadResult result) {
        Plugin.logger.LogDebug($"succeeded changing save slot: {result}");
        _waitingOnProgressionLoaded = false;
        if (_saveButtons[_saveButtons.Length - 1].AmISelected)
            UpdateButtons();
    }

    private void HandleSaveSlotChangeFailed(ProgressionLoadResult result) {
        Plugin.logger.LogError(
            $"failed changing save slot: {result}, {manager.rainWorld.progression.SaveDataReadFailureError}");

        if (_reportCorruptedDialogDisplaying)
            return;
        _reportCorruptedDialogDisplaying = true;

        string errorText = result.ToString();
        if (result == ProgressionLoadResult.ERROR_READ_FAILED &&
            manager.rainWorld.progression.SaveDataReadFailureError != null)
            errorText = $"{errorText}{Environment.NewLine}{manager.rainWorld.progression.SaveDataReadFailureError}";
        string text = manager.rainWorld.inGameTranslator.Translate("ps4_load_save_slot_load_failed")
            .Replace("{ERROR}", errorText);

        DialogNotify dialog = new(
            Custom.ReplaceLineDelimeters(text),
            DialogBoxNotify.CalculateDialogBoxSize(text, false),
            manager.rainWorld.processManager,
            () => {
                _reportCorruptedDialogDisplaying = false;
                if (_leavingSaveSlot < 0) {
                    _waitingOnProgressionLoaded = false;
                    return;
                }
                manager.rainWorld.options.saveSlot = _leavingSaveSlot;
                manager.rainWorld.progression.Destroy();
                manager.rainWorld.progression = new PlayerProgression(manager.rainWorld, true, false);
                _waitingOnProgressionLoaded = true;
                _leavingSaveSlot = -1;
            }
        );
        manager.rainWorld.processManager.ShowDialog(dialog);
    }

    public override void Singal(MenuObject sender, string message) {
        if (_exiting || progressionBusy)
            return;
        switch (message) {
            case "RESET PROGRESS" when !_shouldDeleteSave:
                ResetSave();
                break;
            case "RESET PROGRESS" when _shouldDeleteSave:
                DeleteSave();
                break;
            case "BACKUPS":
                _exiting = true;
                manager.RequestMainProcessSwitch(ProcessManager.ProcessID.BackupManager);
                PlaySound(SoundID.MENU_Switch_Page_In);
                manager.rainWorld.options.Save();
                On.ProcessManager.RequestMainProcessSwitch_ProcessID_float += BackupsMenuBackOverride;
                break;
            case "BACK":
                _exiting = true;
                manager.RequestMainProcessSwitch(ProcessManager.ProcessID.MainMenu);
                PlaySound(SoundID.MENU_Switch_Page_Out);
                manager.rainWorld.options.Save();
                break;
        }
    }

    private static void BackupsMenuBackOverride(On.ProcessManager.orig_RequestMainProcessSwitch_ProcessID_float orig,
        ProcessManager self, ProcessManager.ProcessID id, float fadeOutSeconds) {
        if (id != ProcessManager.ProcessID.OptionsMenu) {
            orig(self, id, fadeOutSeconds);
            return;
        }
        Plugin.logger.LogInfo("redirecting options to saves because backups was entered from saves");
        On.ProcessManager.RequestMainProcessSwitch_ProcessID_float -= BackupsMenuBackOverride;
        orig(self, SavesMenu.id, fadeOutSeconds);
    }

    private void ResetSave() {
        Plugin.logger.LogInfo("resetting save");
        manager.rainWorld.progression.WipeAll();
        manager.RequestMainProcessSwitch(id);
        PlaySound(SoundID.MENU_Switch_Page_In);
    }

    private void DeleteSave() {
        Plugin.logger.LogInfo("deleting save");
        Plugin.logger.LogDebug("deleting expCore (expedition)");
        string path = Path.Combine(Application.persistentDataPath, $"expCore{manager.rainWorld.options.saveSlot + 1}");
        if (File.Exists(path))
            File.Delete(path);
        Plugin.logger.LogDebug("deleting SJ (saint journey)");
        path = Path.Combine(Application.persistentDataPath, $"SJ_{manager.rainWorld.options.saveSlot}");
        if (Directory.Exists(path))
            Directory.Delete(path, true);
        Plugin.logger.LogDebug("deleting sav (regular save)");
        manager.rainWorld.progression.saveFileDataInMemory.OnDeleteCompleted += OnDeleteSaveCompleted;
        manager.rainWorld.progression.saveFileDataInMemory.Delete();
    }

    private void OnDeleteSaveCompleted(UserData.File file, UserData.Result result) {
        Plugin.logger.LogDebug("deletion complete");
        manager.rainWorld.progression.saveFileDataInMemory.OnDeleteCompleted -= OnDeleteSaveCompleted;
        _exiting = true;
        manager.RequestMainProcessSwitch(id);
        PlaySound(SoundID.MENU_Switch_Page_In);
    }

    public override string UpdateInfoText() {
        if (selectedObject == _backButton)
            return Translate("Back to main menu");
        if (selectedObject == _backupsButton)
            return Translate("backups_description");
        if (selectedObject == _resetButton)
            return Translate("Hold down to wipe your save slot and start over");
        if (selectedObject == _deleteCheckbox)
            return Translate("Enable to fully delete the selected save slot instead of wiping it");
        return base.UpdateInfoText();
    }

    public override void ShutDownProcess() {
        base.ShutDownProcess();
        _darkSprite.RemoveFromContainer();
    }
}
